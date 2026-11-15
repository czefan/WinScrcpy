using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal sealed class HardwareSerialCache
{
    readonly struct Entry
    {
        public readonly string HardwareSerial;
        public readonly string Model;
        public readonly string TransportId;

        public Entry(string hardwareSerial, string model, string transportId)
        {
            HardwareSerial = hardwareSerial;
            Model = model ?? "";
            TransportId = transportId ?? "";
        }
    }

    readonly object sync = new object();
    readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    public bool TryGet(DeviceItem item, out string hardwareSerial)
    {
        hardwareSerial = "";
        if (item == null || item.Channel != ChannelKind.WirelessConnected || string.IsNullOrWhiteSpace(item.AdbSerial))
        {
            return false;
        }

        string key = item.AdbSerial.Trim();
        string currentTransportId = item.TransportId != null ? item.TransportId.Trim() : "";
        lock (sync)
        {
            Entry cached;
            if (!entries.TryGetValue(key, out cached))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(cached.TransportId)
                || string.IsNullOrWhiteSpace(currentTransportId)
                || !string.Equals(cached.TransportId, currentTransportId, StringComparison.Ordinal))
            {
                entries.Remove(key);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(cached.Model)
                && !string.IsNullOrWhiteSpace(item.Model)
                && !string.Equals(cached.Model, item.Model.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                entries.Remove(key);
                return false;
            }

            string inferred = !string.IsNullOrWhiteSpace(item.BaseMdnsName)
                ? AdbScanner.ExtractHardwareSerialFromInstance(item.BaseMdnsName)
                : "";
            if (!string.IsNullOrWhiteSpace(inferred)
                && !string.Equals(inferred, cached.HardwareSerial, StringComparison.OrdinalIgnoreCase))
            {
                entries.Remove(key);
                return false;
            }

            hardwareSerial = cached.HardwareSerial;
            return true;
        }
    }

    public void Set(string adbSerial, string transportId, string hardwareSerial, string model)
    {
        if (string.IsNullOrWhiteSpace(adbSerial) || string.IsNullOrWhiteSpace(hardwareSerial)) return;
        string key = adbSerial.Trim();
        lock (sync)
        {
            if (string.IsNullOrWhiteSpace(transportId))
            {
                entries.Remove(key);
                return;
            }
            entries[key] = new Entry(hardwareSerial.Trim(), model != null ? model.Trim() : "", transportId.Trim());
        }
    }

    public void ReconcileDevices(IList<DeviceItem> devices)
    {
        HashSet<string> activeWireless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (devices != null)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                DeviceItem item = devices[i];
                if (item != null && item.Channel == ChannelKind.WirelessConnected && !string.IsNullOrWhiteSpace(item.AdbSerial))
                {
                    activeWireless.Add(item.AdbSerial.Trim());
                }
            }
        }

        lock (sync)
        {
            if (entries.Count == 0) return;
            List<string> stale = null;
            foreach (string key in entries.Keys)
            {
                if (!activeWireless.Contains(key))
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(key);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    entries.Remove(stale[i]);
                }
            }
        }
    }

    public void InvalidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        lock (sync)
        {
            entries.Remove(endpoint.Trim());
        }
    }

    public void InvalidateDevice(DeviceItem item)
    {
        if (item == null) return;
        lock (sync)
        {
            if (!string.IsNullOrWhiteSpace(item.AdbSerial)) entries.Remove(item.AdbSerial.Trim());
            if (item.CandidateEndpoints != null)
            {
                for (int i = 0; i < item.CandidateEndpoints.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(item.CandidateEndpoints[i]))
                    {
                        entries.Remove(item.CandidateEndpoints[i].Trim());
                    }
                }
            }
            if (item.AlternateSerials != null)
            {
                for (int i = 0; i < item.AlternateSerials.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(item.AlternateSerials[i]))
                    {
                        entries.Remove(item.AlternateSerials[i].Trim());
                    }
                }
            }
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
        }
    }
}

internal sealed class DeviceDiscoveryService
{
    public const int MaxTcpProbeConcurrency = 16;
    public const int MaxAdbQueryConcurrency = 4;

    readonly AppConfig config;
    readonly IAdbClient adb;
    readonly Func<string, int, CancellationToken, Task<bool>> tcpProbe;
    readonly HardwareSerialCache serialCache;

    public DeviceDiscoveryService(
        AppConfig config,
        IAdbClient adbClient = null,
        Func<string, int, CancellationToken, Task<bool>> tcpProbe = null,
        HardwareSerialCache hardwareSerialCache = null)
    {
        this.config = config;
        this.adb = adbClient ?? new AdbClient(config);
        this.tcpProbe = tcpProbe ?? ProbeTcpEndpointAsync;
        this.serialCache = hardwareSerialCache ?? new HardwareSerialCache();
    }

    public Task<ScanResult> ScanAsync(Action<ScanResult> onStage1 = null, CancellationToken cancellationToken = default)
    {
        return ScanInternalAsync(onStage1, cancellationToken);
    }

    async Task<ScanResult> ScanInternalAsync(Action<ScanResult> onStage1, CancellationToken cancellationToken)
    {
        AdbClient realAdb = adb as AdbClient;
        bool runStage1BeforeMdns = realAdb != null && !realAdb.IsDaemonProcessRunning();

        CommandOutput devOut;
        CommandOutput mdnsOut;

        if (runStage1BeforeMdns)
        {
            // 冷启动时先由单一 adb 命令拉起 daemon，避免 devices 与 mdns 并发抢占 5037 端口导致 failed to start daemon。
            devOut = await adb.QueryDevicesRawAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EmitStage1(onStage1, devOut);

            mdnsOut = await adb.QueryMdnsServicesRawAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            Task<CommandOutput> devTask = adb.QueryDevicesRawAsync(cancellationToken);
            Task<CommandOutput> mdnsTask = adb.QueryMdnsServicesRawAsync(cancellationToken);

            devOut = await devTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (IsDaemonStartupRaceFailure(devOut))
            {
                mdnsOut = await mdnsTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSucceeded(mdnsOut))
                {
                    devOut = await adb.QueryDevicesRawAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                EmitStage1(onStage1, devOut);
            }
            else
            {
                EmitStage1(onStage1, devOut);
                mdnsOut = await mdnsTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDaemonStartupRaceFailure(mdnsOut) && IsSucceeded(devOut))
                {
                    mdnsOut = await adb.QueryMdnsServicesRawAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        ScanResult result = AdbScanner.ParseAndMerge(
            devOut != null ? devOut.StdOut : "",
            mdnsOut != null ? mdnsOut.StdOut : "");

        ApplyDevicesStageWarning(result, devOut);
        ApplyMdnsStageWarning(result, mdnsOut);

        // 无线 TCP 探活与序列号读取仅返回只读映射；全部完成后统一串行修改 result.Devices，防止竞态覆盖。
        Task<(List<string> savedToProbe, Dictionary<string, bool> probeResults)> wirelessTask =
            QueryWirelessReachabilityAsync(result.Devices, cancellationToken);
        Task<Dictionary<string, string>> serialTask =
            QueryConnectedSerialsAsync(result.Devices, cancellationToken);

        await CompleteScanPostProcessAsync(result, wirelessTask, serialTask, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static async Task CompleteScanPostProcessAsync(
        ScanResult result,
        Task<(List<string> savedToProbe, Dictionary<string, bool> probeResults)> wirelessTask,
        Task<Dictionary<string, string>> serialTask,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(wirelessTask, serialTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var wireless = await wirelessTask.ConfigureAwait(false);
        Dictionary<string, string> serialMap = await serialTask.ConfigureAwait(false);
        ApplyParallelScanQueries(result, wireless.savedToProbe, wireless.probeResults, serialMap);
    }

    internal static void ApplyParallelScanQueries(
        ScanResult result,
        IList<string> savedToProbe,
        IDictionary<string, bool> probeResults,
        IDictionary<string, string> serialMap)
    {
        if (result == null) return;

        if (probeResults != null && probeResults.Count > 0)
        {
            AdbScanner.FilterReachableMdns(result, delegate(string ep)
            {
                bool ok;
                return probeResults.TryGetValue(ep, out ok) && ok;
            });

            if (savedToProbe != null && savedToProbe.Count > 0)
            {
                List<string> reachableSaved = new List<string>();
                for (int i = 0; i < savedToProbe.Count; i++)
                {
                    bool ok;
                    if (probeResults.TryGetValue(savedToProbe[i], out ok) && ok)
                    {
                        reachableSaved.Add(savedToProbe[i]);
                    }
                }
                AdbScanner.MergeReachableSavedEndpoints(result, reachableSaved);
            }
        }

        AdbScanner.EnrichAndDeduplicate(result, delegate(DeviceItem item)
        {
            string sn;
            return serialMap != null && !string.IsNullOrWhiteSpace(item.AdbSerial) && serialMap.TryGetValue(item.AdbSerial, out sn)
                ? sn
                : "";
        });
    }

    static void EmitStage1(Action<ScanResult> onStage1, CommandOutput devOut)
    {
        if (onStage1 == null) return;
        ScanResult stage1 = AdbScanner.ParseAndMerge(devOut != null ? devOut.StdOut : "", "");
        ApplyDevicesStageWarning(stage1, devOut);
        onStage1(stage1);
    }

    static bool IsSucceeded(CommandOutput outp)
    {
        return outp != null && !outp.HasExecutionFailure && outp.ExitCode == 0;
    }

    static bool IsDaemonStartupRaceFailure(CommandOutput outp)
    {
        if (outp == null || outp.HasExecutionFailure || outp.ExitCode == 0) return false;
        string err = outp.StdErr ?? "";
        return err.IndexOf("daemon not running", StringComparison.OrdinalIgnoreCase) >= 0
            && (err.IndexOf("failed to start daemon", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("could not read ok from ADB Server", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("ADB server didn't ACK", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("cannot connect to daemon", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static string NormalizeSingleLineDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";
        string[] lines = detail.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> cleaned = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length > 0) cleaned.Add(trimmed);
        }
        return string.Join(" | ", cleaned);
    }

    static void ApplyDevicesStageWarning(ScanResult result, CommandOutput devOut)
    {
        if (devOut == null) return;
        if (!string.IsNullOrWhiteSpace(devOut.LaunchError))
        {
            result.Warning = "ADB 调用失败: " + NormalizeSingleLineDetail(devOut.LaunchError);
        }
        else if (devOut.Cancelled)
        {
            result.Warning = "ADB 扫描已取消。";
        }
        else if (devOut.TimedOut)
        {
            result.Warning = "adb devices 执行超时，可尝试点击「重启 ADB」。";
        }
        else if (devOut.OutputTimedOut || !string.IsNullOrWhiteSpace(devOut.OutputError))
        {
            result.Warning = "adb devices 输出读取不完整: " + NormalizeSingleLineDetail(devOut.GetFailureSummary("输出异常"));
        }
        else if (devOut.ExitCode != 0)
        {
            string detail = !string.IsNullOrWhiteSpace(devOut.StdErr)
                ? devOut.StdErr
                : devOut.GetFailureSummary(string.Format("异常退出 (退出码 {0})", devOut.ExitCode));
            result.Warning = "adb devices 查询失败: " + NormalizeSingleLineDetail(detail);
        }
    }

    static void ApplyMdnsStageWarning(ScanResult result, CommandOutput mdnsOut)
    {
        if (!string.IsNullOrEmpty(result.Warning) || mdnsOut == null) return;
        if (!mdnsOut.HasExecutionFailure && mdnsOut.ExitCode == 0) return;

        string fallback = mdnsOut.ExitCode != 0
            ? string.Format("异常退出 (退出码 {0})", mdnsOut.ExitCode)
            : "查询异常";
        string detail = !mdnsOut.HasExecutionFailure && !string.IsNullOrWhiteSpace(mdnsOut.StdErr)
            ? mdnsOut.StdErr
            : mdnsOut.GetFailureSummary(fallback);
        result.Warning = "mDNS 查询提示: " + NormalizeSingleLineDetail(detail);
    }

    public static ScanResult MergePendingStage1(ScanResult stage1, ScanResult previousScan)
    {
        ScanResult merged = new ScanResult();
        if (stage1 != null)
        {
            merged.Warning = stage1.Warning ?? "";
            merged.FilteredStaleMdnsCount = stage1.FilteredStaleMdnsCount;
            if (stage1.PairingServices != null) merged.PairingServices.AddRange(stage1.PairingServices);
            if (stage1.Devices != null) merged.Devices.AddRange(stage1.Devices);
        }

        if (previousScan == null || previousScan.Devices == null || previousScan.Devices.Count == 0)
        {
            return merged;
        }

        bool addedPending = false;
        for (int i = 0; i < previousScan.Devices.Count; i++)
        {
            DeviceItem prev = previousScan.Devices[i];
            if (prev == null || (prev.Channel != ChannelKind.MdnsPending && prev.Channel != ChannelKind.SavedPending))
            {
                continue;
            }
            if (HasNonUsbMatch(merged.Devices, prev))
            {
                continue;
            }

            DeviceItem clone = new DeviceItem
            {
                Channel = prev.Channel,
                AdbSerial = prev.AdbSerial,
                HardwareSerial = prev.HardwareSerial,
                BaseMdnsName = prev.BaseMdnsName,
                Model = prev.Model,
                TransportId = prev.TransportId,
                CandidateEndpoints = prev.CandidateEndpoints != null ? new List<string>(prev.CandidateEndpoints) : new List<string>(),
                AlternateSerials = prev.AlternateSerials != null ? new List<string>(prev.AlternateSerials) : new List<string>(),
                IsChecking = true
            };
            merged.Devices.Add(clone);
            addedPending = true;
        }

        if (addedPending)
        {
            AdbScanner.SortDevices(merged.Devices);
        }
        return merged;
    }

    static bool HasNonUsbMatch(IList<DeviceItem> devices, DeviceItem pending)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem d = devices[i];
            if (d == null || d.Channel == ChannelKind.Usb) continue;

            if (!string.IsNullOrWhiteSpace(pending.HardwareSerial)
                && string.Equals(pending.HardwareSerial, d.HardwareSerial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pending.BaseMdnsName)
                && string.Equals(pending.BaseMdnsName, d.BaseMdnsName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pending.AdbSerial)
                && string.Equals(pending.AdbSerial, d.AdbSerial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (pending.CandidateEndpoints != null)
            {
                for (int k = 0; k < pending.CandidateEndpoints.Count; k++)
                {
                    string ep = pending.CandidateEndpoints[k];
                    if (string.IsNullOrWhiteSpace(ep)) continue;
                    if (string.Equals(ep, d.AdbSerial, StringComparison.OrdinalIgnoreCase)
                        || (d.CandidateEndpoints != null && ContainsIgnoreCase(d.CandidateEndpoints, ep))
                        || (d.AlternateSerials != null && ContainsIgnoreCase(d.AlternateSerials, ep)))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    static bool ContainsIgnoreCase(IList<string> list, string target)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], target, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    async Task<Dictionary<string, string>> QueryConnectedSerialsAsync(IList<DeviceItem> devices, CancellationToken cancellationToken)
    {
        Dictionary<string, string> serialMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        serialCache.ReconcileDevices(devices);
        if (devices == null || devices.Count == 0) return serialMap;

        List<(string serial, string transportId, string model)> targets = new List<(string, string, string)>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem item = devices[i];
            if (item.Channel == ChannelKind.WirelessConnected
                && string.IsNullOrWhiteSpace(item.HardwareSerial)
                && !string.IsNullOrWhiteSpace(item.AdbSerial)
                && seen.Add(item.AdbSerial))
            {
                string cachedSn;
                if (serialCache.TryGet(item, out cachedSn))
                {
                    serialMap[item.AdbSerial] = cachedSn;
                }
                else
                {
                    targets.Add((item.AdbSerial, item.TransportId ?? "", item.Model ?? ""));
                }
            }
        }
        if (targets.Count == 0) return serialMap;

        object sync = new object();
        using (SemaphoreSlim adbGate = new SemaphoreSlim(MaxAdbQueryConcurrency, MaxAdbQueryConcurrency))
        {
            Task[] tasks = new Task[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                string target = targets[i].serial;
                string transportId = targets[i].transportId;
                string model = targets[i].model;
                tasks[i] = QuerySingleHardwareSerialAsync(target, transportId, model, adbGate, serialMap, sync, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return serialMap;
    }

    async Task QuerySingleHardwareSerialAsync(
        string target,
        string transportId,
        string model,
        SemaphoreSlim adbGate,
        Dictionary<string, string> serialMap,
        object sync,
        CancellationToken cancellationToken)
    {
        await adbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string sn = await adb.QueryHardwareSerialAsync(target, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sn))
            {
                string trimmed = sn.Trim();
                serialCache.Set(target, transportId, trimmed, model);
                lock (sync)
                {
                    serialMap[target] = trimmed;
                }
            }
            else
            {
                serialCache.InvalidateEndpoint(target);
            }
        }
        finally
        {
            adbGate.Release();
        }
    }

    sealed class EndpointProbeRequest
    {
        public readonly string Endpoint;
        public CancellationTokenSource ListenCts;
        public readonly List<CancellationTokenSource> NotifyGroups;
        public int ConsumerCount;

        public EndpointProbeRequest(string endpoint)
        {
            Endpoint = endpoint;
            NotifyGroups = new List<CancellationTokenSource>();
        }

        public void AddConsumer(CancellationTokenSource groupCts)
        {
            ConsumerCount++;
            if (groupCts != null)
            {
                NotifyGroups.Add(groupCts);
            }
            // 仅单一消费组独占的端点可被组内早停取消；共享端点或常用地址只响应扫描级取消。
            ListenCts = ConsumerCount == 1 && NotifyGroups.Count == 1 ? NotifyGroups[0] : null;
        }
    }

    async Task<(List<string> savedToProbe, Dictionary<string, bool> probeResults)> QueryWirelessReachabilityAsync(
        IList<DeviceItem> devices,
        CancellationToken cancellationToken)
    {
        List<string> savedToProbe = new List<string>();
        Dictionary<string, bool> emptyResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (devices == null) return (savedToProbe, emptyResults);

        List<EndpointProbeRequest> requests = new List<EndpointProbeRequest>();
        Dictionary<string, EndpointProbeRequest> byEndpoint = new Dictionary<string, EndpointProbeRequest>(StringComparer.OrdinalIgnoreCase);
        List<CancellationTokenSource> groupCtsList = new List<CancellationTokenSource>();

        try
        {
            for (int i = 0; i < devices.Count; i++)
            {
                DeviceItem item = devices[i];
                if (item.Channel != ChannelKind.MdnsPending || item.CandidateEndpoints.Count == 0) continue;

                // 同一 mDNS 设备只要有一个候选端口连通，立即取消同组其余独占失效端口的等待。
                CancellationTokenSource groupCts = item.CandidateEndpoints.Count > 1
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    : null;
                if (groupCts != null) groupCtsList.Add(groupCts);

                HashSet<string> seenInItem = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < item.CandidateEndpoints.Count; k++)
                {
                    string ep = item.CandidateEndpoints[k];
                    if (!seenInItem.Add(ep)) continue;

                    EndpointProbeRequest req;
                    if (!byEndpoint.TryGetValue(ep, out req))
                    {
                        req = new EndpointProbeRequest(ep);
                        byEndpoint[ep] = req;
                        requests.Add(req);
                    }
                    req.AddConsumer(groupCts);
                }
            }

            if (config != null && config.savedEndpoints != null)
            {
                for (int i = 0; i < config.savedEndpoints.Count; i++)
                {
                    string ep = ConfigManager.NormalizeEndpoint(config.savedEndpoints[i]);
                    if (string.IsNullOrEmpty(ep) || AdbScanner.HasOnlineOrPendingDeviceForEndpoint(devices, ep)) continue;
                    savedToProbe.Add(ep);

                    EndpointProbeRequest req;
                    if (!byEndpoint.TryGetValue(ep, out req))
                    {
                        req = new EndpointProbeRequest(ep);
                        byEndpoint[ep] = req;
                        requests.Add(req);
                    }
                    req.AddConsumer(null);
                }
            }

            if (requests.Count == 0) return (savedToProbe, emptyResults);

            Dictionary<string, bool> probeResults = await ProbeEndpointRequestsAsync(
                requests, 1000, MaxTcpProbeConcurrency, cancellationToken, tcpProbe).ConfigureAwait(false);
            return (savedToProbe, probeResults);
        }
        finally
        {
            for (int i = 0; i < groupCtsList.Count; i++)
            {
                groupCtsList[i].Dispose();
            }
        }
    }

    internal static Task<Dictionary<string, bool>> ProbeEndpointsAsync(
        IList<string> endpoints,
        int timeoutMs,
        int maxConcurrency,
        CancellationToken cancellationToken,
        Func<string, int, CancellationToken, Task<bool>> probeOverride = null)
    {
        if (endpoints == null || endpoints.Count == 0)
        {
            return Task.FromResult(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
        }

        List<EndpointProbeRequest> requests = new List<EndpointProbeRequest>(endpoints.Count);
        for (int i = 0; i < endpoints.Count; i++)
        {
            requests.Add(new EndpointProbeRequest(endpoints[i]));
        }
        return ProbeEndpointRequestsAsync(requests, timeoutMs, maxConcurrency, cancellationToken, probeOverride);
    }

    static async Task<Dictionary<string, bool>> ProbeEndpointRequestsAsync(
        IList<EndpointProbeRequest> requests,
        int timeoutMs,
        int maxConcurrency,
        CancellationToken cancellationToken,
        Func<string, int, CancellationToken, Task<bool>> probeOverride)
    {
        Dictionary<string, bool> results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (requests == null || requests.Count == 0) return results;

        int limit = Math.Max(1, maxConcurrency);
        Func<string, int, CancellationToken, Task<bool>> probeFn = probeOverride ?? ProbeTcpEndpointAsync;
        object sync = new object();

        using (SemaphoreSlim gate = new SemaphoreSlim(limit, limit))
        {
            Task[] tasks = new Task[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                EndpointProbeRequest request = requests[i];
                tasks[i] = ProbeOneEndpointAsync(request, timeoutMs, gate, probeFn, results, sync, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    static async Task ProbeOneEndpointAsync(
        EndpointProbeRequest request,
        int timeoutMs,
        SemaphoreSlim gate,
        Func<string, int, CancellationToken, Task<bool>> probeFn,
        Dictionary<string, bool> results,
        object sync,
        CancellationToken cancellationToken)
    {
        CancellationToken effectiveToken = request.ListenCts != null ? request.ListenCts.Token : cancellationToken;
        bool entered = false;
        bool reachable = false;
        try
        {
            await gate.WaitAsync(effectiveToken).ConfigureAwait(false);
            entered = true;

            if (!effectiveToken.IsCancellationRequested)
            {
                reachable = await probeFn(request.Endpoint, timeoutMs, effectiveToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reachable = false;
        }
        finally
        {
            if (entered) gate.Release();
        }

        lock (sync)
        {
            results[request.Endpoint] = reachable;
        }

        if (reachable)
        {
            for (int i = 0; i < request.NotifyGroups.Count; i++)
            {
                try
                {
                    request.NotifyGroups[i].Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    internal static async Task<bool> ProbeTcpEndpointAsync(string endpoint, int timeoutMs, CancellationToken cancellationToken)
    {
        string host;
        int port;
        if (!AdbClient.TryParseEndpoint(endpoint, out host, out port)) return false;

        IPAddress ip;
        AddressFamily family = (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

        using (TcpClient client = new TcpClient(family))
        using (CancellationTokenSource timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : new CancellationTokenSource())
        using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        {
            try
            {
                await client.ConnectAsync(host, port, linkedCts.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
