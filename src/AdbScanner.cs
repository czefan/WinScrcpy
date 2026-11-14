using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

internal static class AdbScanner
{
    static readonly Regex DevicesLineRegex = new Regex(@"^(\S+)\s+(.+)$", RegexOptions.Compiled);
    static readonly Regex ModelRegex = new Regex(@"(?:^|\s)model:(\S+)", RegexOptions.Compiled);
    static readonly Regex MdnsLineRegex = new Regex(@"^(.+?)\s+(_adb-[^\s]+)\s+(\S+)$", RegexOptions.Compiled);
    static readonly Regex InstanceSuffixRegex = new Regex(@"^(.*?)(?:\s+\((\d+)\))?$", RegexOptions.Compiled);
    static readonly Regex AdbInstanceSerialRegex = new Regex(@"^adb-(.+)-([A-Za-z0-9]+)$", RegexOptions.Compiled);
    static readonly Regex AdbTlsDeviceRegex = new Regex(@"^(.+?)\._adb-tls-connect\._tcp\.?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    sealed class MdnsCandidate
    {
        public int Order;
        public string Endpoint;
        public int Sequence;
    }

    sealed class MdnsGroup
    {
        public string BaseName;
        public string HardwareSerial;
        public List<MdnsCandidate> Candidates;

        public MdnsGroup()
        {
            Candidates = new List<MdnsCandidate>();
        }
    }

    public static ScanResult ParseAndMerge(string devicesOutput, string mdnsOutput)
    {
        ScanResult result = new ScanResult();
        List<DeviceItem> parsedDevices = ParseDevices(devicesOutput);
        Dictionary<string, MdnsGroup> mdnsGroups = ParseMdns(mdnsOutput, result.PairingServices);

        List<DeviceItem> merged = new List<DeviceItem>();
        HashSet<string> matchedMdnsBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < parsedDevices.Count; i++)
        {
            DeviceItem dev = parsedDevices[i];
            if (!IsWirelessDevice(dev))
            {
                merged.Add(dev);
                continue;
            }

            MdnsGroup matchingGroup = FindMatchingGroup(dev, mdnsGroups);
            if (matchingGroup != null)
            {
                matchedMdnsBases.Add(matchingGroup.BaseName);
                dev.BaseMdnsName = matchingGroup.BaseName;
                if (string.IsNullOrWhiteSpace(dev.HardwareSerial) && !string.IsNullOrWhiteSpace(matchingGroup.HardwareSerial))
                {
                    dev.HardwareSerial = matchingGroup.HardwareSerial;
                }

                List<string> orderedEndpoints = GetOrderedEndpoints(matchingGroup);
                for (int k = 0; k < orderedEndpoints.Count; k++)
                {
                    if (!ContainsIgnoreCase(dev.CandidateEndpoints, orderedEndpoints[k]))
                    {
                        dev.CandidateEndpoints.Add(orderedEndpoints[k]);
                    }
                }

                if (dev.Channel == ChannelKind.Offline && dev.CandidateEndpoints.Count > 0)
                {
                    dev.Channel = ChannelKind.MdnsPending;
                    dev.AdbSerial = "";
                }
            }
            merged.Add(dev);
        }

        foreach (KeyValuePair<string, MdnsGroup> pair in mdnsGroups)
        {
            if (matchedMdnsBases.Contains(pair.Key)) continue;

            MdnsGroup group = pair.Value;
            DeviceItem mdnsItem = new DeviceItem();
            mdnsItem.Channel = ChannelKind.MdnsPending;
            mdnsItem.BaseMdnsName = group.BaseName;
            mdnsItem.HardwareSerial = group.HardwareSerial;
            mdnsItem.CandidateEndpoints = GetOrderedEndpoints(group);
            merged.Add(mdnsItem);
        }

        BackfillKnownModels(merged);

        merged = DeduplicateWirelessEntries(merged);
        merged.Sort(CompareDevices);
        result.Devices = merged;
        return result;
    }

    public static void EnrichAndDeduplicate(ScanResult result, Func<DeviceItem, string> hardwareSerialResolver)
    {
        if (result == null || result.Devices == null) return;

        if (hardwareSerialResolver != null)
        {
            for (int i = 0; i < result.Devices.Count; i++)
            {
                DeviceItem item = result.Devices[i];
                if (item.Channel == ChannelKind.WirelessConnected && string.IsNullOrWhiteSpace(item.HardwareSerial))
                {
                    string resolved = hardwareSerialResolver(item);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        item.HardwareSerial = resolved.Trim();
                    }
                }
            }
        }

        BackfillKnownModels(result.Devices);

        result.Devices = DeduplicateWirelessEntries(result.Devices);
        result.Devices.Sort(CompareDevices);
    }

    static void BackfillKnownModels(List<DeviceItem> items)
    {
        Dictionary<string, string> serialToModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            DeviceItem dev = items[i];
            if (!string.IsNullOrWhiteSpace(dev.HardwareSerial) && !string.IsNullOrWhiteSpace(dev.Model))
            {
                if (!serialToModel.ContainsKey(dev.HardwareSerial))
                {
                    serialToModel[dev.HardwareSerial] = dev.Model;
                }
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            DeviceItem dev = items[i];
            if (string.IsNullOrWhiteSpace(dev.Model) && !string.IsNullOrWhiteSpace(dev.HardwareSerial))
            {
                string knownModel;
                if (serialToModel.TryGetValue(dev.HardwareSerial, out knownModel))
                {
                    dev.Model = knownModel;
                }
            }
        }
    }

    public static bool IsWirelessSerial(string serial)
    {
        return !string.IsNullOrWhiteSpace(serial)
            && (serial.IndexOf(':') >= 0 || serial.IndexOf("._adb-", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static bool IsWirelessDevice(DeviceItem item)
    {
        if (item == null || item.Channel == ChannelKind.Usb) return false;
        if (item.Channel == ChannelKind.WirelessConnected
            || item.Channel == ChannelKind.MdnsPending
            || item.Channel == ChannelKind.SavedPending)
        {
            return true;
        }
        if (item.CandidateEndpoints != null && item.CandidateEndpoints.Count > 0) return true;
        return IsWirelessSerial(item.AdbSerial);
    }

    static bool IsDeduplicableWireless(DeviceItem item)
    {
        return item.Channel == ChannelKind.WirelessConnected
            || item.Channel == ChannelKind.MdnsPending
            || (item.Channel == ChannelKind.Offline && IsWirelessDevice(item));
    }

    static List<DeviceItem> DeduplicateWirelessEntries(List<DeviceItem> items)
    {
        List<DeviceItem> result = new List<DeviceItem>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            DeviceItem incoming = items[i];
            if (!IsDeduplicableWireless(incoming))
            {
                result.Add(incoming);
                continue;
            }

            DeviceItem existing = FindExistingWirelessMatch(result, incoming);
            if (existing == null)
            {
                result.Add(incoming);
                continue;
            }

            MergeWirelessPair(existing, incoming);
        }
        return result;
    }

    static DeviceItem FindExistingWirelessMatch(List<DeviceItem> existingList, DeviceItem incoming)
    {
        for (int i = 0; i < existingList.Count; i++)
        {
            DeviceItem cur = existingList[i];
            if (!IsDeduplicableWireless(cur))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(cur.HardwareSerial)
                && !string.IsNullOrWhiteSpace(incoming.HardwareSerial)
                && string.Equals(cur.HardwareSerial, incoming.HardwareSerial, StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }

            if (!string.IsNullOrWhiteSpace(cur.BaseMdnsName)
                && !string.IsNullOrWhiteSpace(incoming.BaseMdnsName)
                && string.Equals(cur.BaseMdnsName, incoming.BaseMdnsName, StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }

            if (!string.IsNullOrWhiteSpace(cur.AdbSerial)
                && !string.IsNullOrWhiteSpace(incoming.AdbSerial)
                && string.Equals(cur.AdbSerial, incoming.AdbSerial, StringComparison.OrdinalIgnoreCase))
            {
                return cur;
            }
        }
        return null;
    }

    static void MergeWirelessPair(DeviceItem target, DeviceItem incoming)
    {
        // Prefer an active WirelessConnected channel over MdnsPending/Offline,
        // and prefer short ip:port serial over long ._adb-tls-connect._tcp serial.
        bool incomingHasHigherState = incoming.Channel == ChannelKind.WirelessConnected && target.Channel != ChannelKind.WirelessConnected;
        bool bothConnectedPreferIpPort = incoming.Channel == ChannelKind.WirelessConnected
            && target.Channel == ChannelKind.WirelessConnected
            && IsDirectIpPort(incoming.AdbSerial)
            && !IsDirectIpPort(target.AdbSerial);

        if (incomingHasHigherState || bothConnectedPreferIpPort)
        {
            if (!string.IsNullOrWhiteSpace(target.AdbSerial) && !ContainsIgnoreCase(target.AlternateSerials, target.AdbSerial))
            {
                target.AlternateSerials.Add(target.AdbSerial);
            }
            target.Channel = incoming.Channel;
            target.AdbSerial = incoming.AdbSerial;
        }
        else if (!string.IsNullOrWhiteSpace(incoming.AdbSerial)
            && !string.Equals(incoming.AdbSerial, target.AdbSerial, StringComparison.OrdinalIgnoreCase)
            && !ContainsIgnoreCase(target.AlternateSerials, incoming.AdbSerial))
        {
            target.AlternateSerials.Add(incoming.AdbSerial);
        }

        if (incoming.AlternateSerials != null)
        {
            for (int i = 0; i < incoming.AlternateSerials.Count; i++)
            {
                string alt = incoming.AlternateSerials[i];
                if (!string.IsNullOrWhiteSpace(alt)
                    && !string.Equals(alt, target.AdbSerial, StringComparison.OrdinalIgnoreCase)
                    && !ContainsIgnoreCase(target.AlternateSerials, alt))
                {
                    target.AlternateSerials.Add(alt);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(target.HardwareSerial)) target.HardwareSerial = incoming.HardwareSerial;
        if (string.IsNullOrWhiteSpace(target.BaseMdnsName)) target.BaseMdnsName = incoming.BaseMdnsName;
        if (string.IsNullOrWhiteSpace(target.Model)) target.Model = incoming.Model;

        if (incoming.CandidateEndpoints != null)
        {
            for (int i = 0; i < incoming.CandidateEndpoints.Count; i++)
            {
                string ep = incoming.CandidateEndpoints[i];
                if (!ContainsIgnoreCase(target.CandidateEndpoints, ep))
                {
                    target.CandidateEndpoints.Add(ep);
                }
            }
        }
    }

    static bool IsDirectIpPort(string serial)
    {
        return !string.IsNullOrWhiteSpace(serial)
            && serial.IndexOf(':') >= 0
            && serial.IndexOf("._adb-", StringComparison.OrdinalIgnoreCase) < 0;
    }

    public static void FilterReachableMdns(ScanResult result, Func<string, bool> probe)
    {
        if (result == null || result.Devices == null || probe == null) return;

        List<DeviceItem> kept = new List<DeviceItem>(result.Devices.Count);
        for (int i = 0; i < result.Devices.Count; i++)
        {
            DeviceItem item = result.Devices[i];
            if (item.Channel != ChannelKind.MdnsPending)
            {
                kept.Add(item);
                continue;
            }

            List<string> reachable = new List<string>();
            for (int k = 0; k < item.CandidateEndpoints.Count; k++)
            {
                string ep = item.CandidateEndpoints[k];
                if (probe(ep))
                {
                    reachable.Add(ep);
                }
            }

            if (reachable.Count > 0)
            {
                item.CandidateEndpoints = reachable;
                kept.Add(item);
            }
            else
            {
                result.FilteredStaleMdnsCount++;
            }
        }
        result.Devices = kept;
    }

    public static List<DeviceItem> ParseDevices(string output)
    {
        List<DeviceItem> items = new List<DeviceItem>();
        if (string.IsNullOrWhiteSpace(output)) return items;

        string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;

            Match m = DevicesLineRegex.Match(line);
            if (!m.Success) continue;

            string serial = m.Groups[1].Value.Trim();
            string rest = m.Groups[2].Value.Trim();
            string state;
            if (rest.StartsWith("no permissions", StringComparison.OrdinalIgnoreCase))
            {
                state = "no permissions";
            }
            else
            {
                int spaceIdx = rest.IndexOfAny(new char[] { ' ', '\t' });
                state = spaceIdx > 0 ? rest.Substring(0, spaceIdx) : rest;
            }

            DeviceItem item = new DeviceItem();
            item.AdbSerial = serial;

            Match modelMatch = ModelRegex.Match(rest);
            if (modelMatch.Success)
            {
                item.Model = modelMatch.Groups[1].Value.Replace('_', ' ').Trim();
            }

            bool isWireless = IsWirelessSerial(serial);
            if (isWireless)
            {
                Match tlsMatch = AdbTlsDeviceRegex.Match(serial);
                if (tlsMatch.Success)
                {
                    string instance = StripInstanceOrder(tlsMatch.Groups[1].Value);
                    item.BaseMdnsName = instance;
                    item.HardwareSerial = ExtractHardwareSerialFromInstance(instance);
                }
                else if (serial.IndexOf(':') >= 0)
                {
                    item.CandidateEndpoints.Add(serial);
                }
            }
            else
            {
                item.HardwareSerial = serial;
            }

            if (string.Equals(state, "device", StringComparison.OrdinalIgnoreCase))
            {
                item.Channel = isWireless ? ChannelKind.WirelessConnected : ChannelKind.Usb;
            }
            else if (string.Equals(state, "unauthorized", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(state, "authorizing", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(state, "no permissions", StringComparison.OrdinalIgnoreCase))
            {
                item.Channel = ChannelKind.Unauthorized;
            }
            else
            {
                item.Channel = ChannelKind.Offline;
            }

            items.Add(item);
        }
        return items;
    }

    static Dictionary<string, MdnsGroup> ParseMdns(string output, List<PairingServiceInfo> pairingList)
    {
        Dictionary<string, MdnsGroup> groups = new Dictionary<string, MdnsGroup>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output)) return groups;

        string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int seq = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;

            Match m = MdnsLineRegex.Match(line);
            if (!m.Success) continue;

            string instanceName = m.Groups[1].Value.Trim();
            string serviceType = m.Groups[2].Value.Trim().TrimEnd('.');
            string endpoint = m.Groups[3].Value.Trim();

            if (string.Equals(serviceType, "_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase))
            {
                PairingServiceInfo pairing = new PairingServiceInfo();
                pairing.InstanceName = instanceName;
                pairing.Endpoint = endpoint;
                pairingList.Add(pairing);
                continue;
            }

            if (!string.Equals(serviceType, "_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int order = 0;
            string baseName = instanceName;
            Match suffix = InstanceSuffixRegex.Match(instanceName);
            if (suffix.Success)
            {
                baseName = suffix.Groups[1].Value.Trim();
                if (suffix.Groups[2].Success)
                {
                    int parsedOrder;
                    if (int.TryParse(suffix.Groups[2].Value, out parsedOrder))
                    {
                        order = parsedOrder;
                    }
                }
            }

            MdnsGroup group;
            if (!groups.TryGetValue(baseName, out group))
            {
                group = new MdnsGroup();
                group.BaseName = baseName;
                group.HardwareSerial = ExtractHardwareSerialFromInstance(baseName);
                groups[baseName] = group;
            }

            MdnsCandidate existing = null;
            for (int c = 0; c < group.Candidates.Count; c++)
            {
                if (string.Equals(group.Candidates[c].Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    existing = group.Candidates[c];
                    break;
                }
            }

            if (existing != null)
            {
                if (order > existing.Order) existing.Order = order;
            }
            else
            {
                MdnsCandidate cand = new MdnsCandidate();
                cand.Order = order;
                cand.Endpoint = endpoint;
                cand.Sequence = seq++;
                group.Candidates.Add(cand);
            }
        }
        return groups;
    }

    public static List<PairingServiceInfo> ParsePairingServices(string output)
    {
        List<PairingServiceInfo> list = new List<PairingServiceInfo>();
        ParseMdns(output, list);
        return list;
    }

    static MdnsGroup FindMatchingGroup(DeviceItem dev, Dictionary<string, MdnsGroup> groups)
    {
        if (!string.IsNullOrWhiteSpace(dev.BaseMdnsName))
        {
            MdnsGroup byName;
            if (groups.TryGetValue(dev.BaseMdnsName, out byName)) return byName;
        }

        foreach (KeyValuePair<string, MdnsGroup> pair in groups)
        {
            MdnsGroup grp = pair.Value;
            for (int i = 0; i < grp.Candidates.Count; i++)
            {
                if (string.Equals(grp.Candidates[i].Endpoint, dev.AdbSerial, StringComparison.OrdinalIgnoreCase))
                {
                    return grp;
                }
            }
        }
        return null;
    }

    static List<string> GetOrderedEndpoints(MdnsGroup group)
    {
        List<MdnsCandidate> copy = new List<MdnsCandidate>(group.Candidates);
        copy.Sort(delegate(MdnsCandidate a, MdnsCandidate b)
        {
            int cmp = b.Order.CompareTo(a.Order);
            return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
        });

        List<string> endpoints = new List<string>(copy.Count);
        for (int i = 0; i < copy.Count; i++)
        {
            endpoints.Add(copy[i].Endpoint);
        }
        return endpoints;
    }

    static string StripInstanceOrder(string name)
    {
        Match m = InstanceSuffixRegex.Match(name ?? "");
        return m.Success ? m.Groups[1].Value.Trim() : (name ?? "").Trim();
    }

    public static string ExtractHardwareSerialFromInstance(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) return "";
        Match m = AdbInstanceSerialRegex.Match(baseName.Trim());
        return m.Success ? m.Groups[1].Value : "";
    }

    internal static bool ContainsIgnoreCase(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static void AddUniqueIgnoreCase(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!ContainsIgnoreCase(list, value)) list.Add(value);
    }

    public static void MergeReachableSavedEndpoints(ScanResult result, IList<string> reachableSavedEndpoints)
    {
        if (result == null || reachableSavedEndpoints == null || reachableSavedEndpoints.Count == 0) return;
        for (int i = 0; i < reachableSavedEndpoints.Count; i++)
        {
            string ep = reachableSavedEndpoints[i];
            if (string.IsNullOrWhiteSpace(ep)) continue;

            DeviceItem existing = FindDeviceForEndpoint(result.Devices, ep);
            if (existing != null)
            {
                if (existing.Channel == ChannelKind.Offline)
                {
                    existing.Channel = ChannelKind.SavedPending;
                    existing.AdbSerial = "";
                    if (!ContainsIgnoreCase(existing.CandidateEndpoints, ep))
                    {
                        existing.CandidateEndpoints.Insert(0, ep);
                    }
                }
                continue;
            }

            DeviceItem item = new DeviceItem();
            item.Channel = ChannelKind.SavedPending;
            item.CandidateEndpoints.Add(ep);
            result.Devices.Add(item);
        }
        SortDevices(result.Devices);
    }

    public static bool HasOnlineOrPendingDeviceForEndpoint(IList<DeviceItem> devices, string endpoint)
    {
        DeviceItem existing = FindDeviceForEndpoint(devices, endpoint);
        return existing != null && existing.Channel != ChannelKind.Offline;
    }

    static DeviceItem FindDeviceForEndpoint(IList<DeviceItem> devices, string endpoint)
    {
        if (devices == null || string.IsNullOrWhiteSpace(endpoint)) return null;
        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem d = devices[i];
            if (string.Equals(d.AdbSerial, endpoint, StringComparison.OrdinalIgnoreCase)) return d;
            if (ContainsIgnoreCase(d.AlternateSerials, endpoint)) return d;
            if (ContainsIgnoreCase(d.CandidateEndpoints, endpoint)) return d;
        }
        return null;
    }

    public static void SortDevices(List<DeviceItem> devices)
    {
        if (devices != null) devices.Sort(CompareDevices);
    }

    static int CompareDevices(DeviceItem a, DeviceItem b)
    {
        int rankA = ChannelRank(a.Channel);
        int rankB = ChannelRank(b.Channel);
        if (rankA != rankB) return rankA.CompareTo(rankB);
        return string.Compare(a.PrimaryEndpointDisplay, b.PrimaryEndpointDisplay, StringComparison.OrdinalIgnoreCase);
    }

    static int ChannelRank(ChannelKind kind)
    {
        switch (kind)
        {
            case ChannelKind.Usb: return 0;
            case ChannelKind.WirelessConnected: return 1;
            case ChannelKind.MdnsPending: return 2;
            case ChannelKind.SavedPending: return 3;
            case ChannelKind.Unauthorized: return 4;
            default: return 5;
        }
    }
}
