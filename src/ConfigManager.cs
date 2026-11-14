using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class DeviceConfig
{
    public string alias { get; set; }
    public List<string> args { get; set; }

    public DeviceConfig()
    {
        alias = "";
        args = new List<string>();
    }
}

internal sealed class AppConfig
{
    public string adbPath { get; set; }
    public string scrcpyPath { get; set; }
    public List<string> globalArgs { get; set; }
    public List<string> savedEndpoints { get; set; }
    public Dictionary<string, DeviceConfig> devices { get; set; }

    public AppConfig()
    {
        adbPath = "adb";
        scrcpyPath = "scrcpy";
        globalArgs = new List<string>();
        savedEndpoints = new List<string>();
        devices = new Dictionary<string, DeviceConfig>();
    }

    public static AppConfig CreateDefault()
    {
        AppConfig cfg = new AppConfig();
        cfg.adbPath = "adb";
        cfg.scrcpyPath = "scrcpy";
        cfg.globalArgs.Add("--stay-awake");
        return cfg;
    }
}

internal sealed class ResolvedDeviceConfig
{
    public string Alias;
    public List<string> Args;
    public bool HasCustomArgs;

    public ResolvedDeviceConfig()
    {
        Alias = "";
        Args = new List<string>();
        HasCustomArgs = false;
    }
}

internal sealed class ConfigManager
{
    public readonly string ConfigPath;

    public static string DefaultConfigPath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "winscrcpy.json"); }
    }

    public ConfigManager(string configPath)
    {
        ConfigPath = configPath;
    }

    public AppConfig LoadOrCreate(out string warning)
    {
        warning = null;
        if (!File.Exists(ConfigPath))
        {
            AppConfig def = AppConfig.CreateDefault();
            try
            {
                Save(def);
            }
            catch (Exception ex)
            {
                warning = "无法写入默认配置文件: " + ex.Message;
            }
            return def;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            return ParseJson(json);
        }
        catch (Exception ex)
        {
            warning = "配置文件解析失败，已回退默认配置: " + ex.Message;
            return AppConfig.CreateDefault();
        }
    }

    public static AppConfig ParseJson(string json)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        AppConfig cfg = serializer.Deserialize<AppConfig>(json);
        if (cfg == null) cfg = AppConfig.CreateDefault();
        if (string.IsNullOrWhiteSpace(cfg.adbPath)) cfg.adbPath = "adb";
        if (string.IsNullOrWhiteSpace(cfg.scrcpyPath)) cfg.scrcpyPath = "scrcpy";
        if (cfg.globalArgs == null) cfg.globalArgs = new List<string>();
        cfg.savedEndpoints = NormalizeSavedList(cfg.savedEndpoints);
        if (cfg.devices == null) cfg.devices = new Dictionary<string, DeviceConfig>();
        return cfg;
    }

    public static string NormalizeEndpoint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            trimmed = trimmed + ":5555";
        }
        else if (trimmed.IndexOf(':') < 0)
        {
            trimmed = trimmed + ":5555";
        }
        else if (!trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.IndexOf(':') != trimmed.LastIndexOf(':'))
        {
            IPAddress bareIpv6;
            if (IPAddress.TryParse(trimmed, out bareIpv6))
            {
                trimmed = "[" + trimmed + "]:5555";
            }
        }
        string host;
        int port;
        if (!DeviceRunner.TryParseEndpoint(trimmed, out host, out port)) return "";
        return (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + port;
    }

    public static bool TryValidateEndpoint(string raw, out string normalized)
    {
        normalized = NormalizeEndpoint(raw);
        if (string.IsNullOrEmpty(normalized)) return false;
        string host;
        int port;
        if (!DeviceRunner.TryParseEndpoint(normalized, out host, out port)) return false;
        IPAddress parsedIp;
        if (char.IsDigit(host[0]) && !IPAddress.TryParse(host, out parsedIp)) return false;
        return true;
    }

    static List<string> NormalizeSavedList(List<string> rawList)
    {
        List<string> list = new List<string>();
        if (rawList == null) return list;
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rawList.Count; i++)
        {
            string norm = NormalizeEndpoint(rawList[i]);
            if (!string.IsNullOrEmpty(norm) && seen.Add(norm))
            {
                list.Add(norm);
            }
        }
        return list;
    }

    public static bool AddSavedEndpoint(AppConfig cfg, string endpoint)
    {
        if (cfg == null) return false;
        string norm = NormalizeEndpoint(endpoint);
        if (string.IsNullOrEmpty(norm)) return false;
        if (cfg.savedEndpoints == null) cfg.savedEndpoints = new List<string>();

        for (int i = cfg.savedEndpoints.Count - 1; i >= 0; i--)
        {
            if (string.Equals(cfg.savedEndpoints[i], norm, StringComparison.OrdinalIgnoreCase))
            {
                cfg.savedEndpoints.RemoveAt(i);
            }
        }
        cfg.savedEndpoints.Insert(0, norm);
        while (cfg.savedEndpoints.Count > 12)
        {
            cfg.savedEndpoints.RemoveAt(cfg.savedEndpoints.Count - 1);
        }
        return true;
    }

    public static string GetDeviceAlias(AppConfig cfg, string key)
    {
        if (cfg == null || cfg.devices == null || string.IsNullOrWhiteSpace(key)) return "";
        DeviceConfig existing = FindCaseInsensitive(cfg.devices, key.Trim());
        return existing != null && !string.IsNullOrWhiteSpace(existing.alias) ? existing.alias.Trim() : "";
    }

    public static bool SetDeviceAlias(AppConfig cfg, string key, string alias)
    {
        if (cfg == null || string.IsNullOrWhiteSpace(key)) return false;
        string trimmedKey = key.Trim();
        string trimmedAlias = (alias ?? "").Trim();
        if (cfg.devices == null) cfg.devices = new Dictionary<string, DeviceConfig>();

        DeviceConfig existing = FindCaseInsensitive(cfg.devices, trimmedKey);
        if (trimmedAlias.Length == 0)
        {
            if (existing == null) return false;
            existing.alias = "";
            if (existing.args == null || existing.args.Count == 0)
            {
                RemoveCaseInsensitive(cfg.devices, trimmedKey);
            }
            return true;
        }

        if (existing != null)
        {
            existing.alias = trimmedAlias;
        }
        else
        {
            DeviceConfig dc = new DeviceConfig();
            dc.alias = trimmedAlias;
            cfg.devices[trimmedKey] = dc;
        }
        return true;
    }

    public static void ApplySavedEndpoints(AppConfig cfg, IList<string> endpoints, IList<string> aliases)
    {
        if (cfg == null) return;
        List<string> previous = cfg.savedEndpoints != null ? new List<string>(cfg.savedEndpoints) : new List<string>();
        List<string> nextList = new List<string>();
        HashSet<string> nextSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (endpoints != null)
        {
            for (int i = 0; i < endpoints.Count; i++)
            {
                string norm = NormalizeEndpoint(endpoints[i]);
                if (string.IsNullOrEmpty(norm) || !nextSet.Add(norm)) continue;
                nextList.Add(norm);
                string alias = (aliases != null && i < aliases.Count) ? aliases[i] : "";
                SetDeviceAlias(cfg, norm, alias);
            }
        }

        for (int i = 0; i < previous.Count; i++)
        {
            string oldEp = previous[i];
            if (!string.IsNullOrWhiteSpace(oldEp) && !nextSet.Contains(oldEp))
            {
                SetDeviceAlias(cfg, oldEp, "");
            }
        }

        cfg.savedEndpoints = nextList;
    }

    public void Save(AppConfig cfg)
    {
        if (cfg == null) cfg = AppConfig.CreateDefault();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"adbPath\": \"").Append(EscapeJson(string.IsNullOrWhiteSpace(cfg.adbPath) ? "adb" : cfg.adbPath)).AppendLine("\",");
        sb.Append("  \"scrcpyPath\": \"").Append(EscapeJson(string.IsNullOrWhiteSpace(cfg.scrcpyPath) ? "scrcpy" : cfg.scrcpyPath)).AppendLine("\",");
        AppendStringArrayProperty(sb, 2, "globalArgs", cfg.globalArgs, true);
        AppendStringArrayProperty(sb, 2, "savedEndpoints", cfg.savedEndpoints, true);
        sb.Append("  \"devices\": {");

        Dictionary<string, DeviceConfig> devs = cfg.devices ?? new Dictionary<string, DeviceConfig>();
        if (devs.Count == 0)
        {
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine();
            int idx = 0;
            foreach (KeyValuePair<string, DeviceConfig> pair in devs)
            {
                DeviceConfig dc = pair.Value ?? new DeviceConfig();
                sb.Append("    \"").Append(EscapeJson(pair.Key)).AppendLine("\": {");
                sb.Append("      \"alias\": \"").Append(EscapeJson(dc.alias)).AppendLine("\",");
                AppendStringArrayProperty(sb, 6, "args", dc.args, false);
                sb.Append("    }");
                if (++idx < devs.Count) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  }");
        }

        sb.AppendLine("}");
        File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
    }

    static void AppendStringArrayProperty(StringBuilder sb, int indentSpaces, string key, List<string> items, bool trailingComma)
    {
        string indent = new string(' ', indentSpaces);
        sb.Append(indent).Append("\"").Append(key).Append("\": [");
        if (items != null && items.Count > 0)
        {
            sb.AppendLine();
            string itemIndent = new string(' ', indentSpaces + 2);
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(itemIndent).Append("\"").Append(EscapeJson(items[i])).Append("\"");
                if (i + 1 < items.Count) sb.Append(",");
                sb.AppendLine();
            }
            sb.Append(indent);
        }
        sb.Append("]");
        if (trailingComma) sb.Append(",");
        sb.AppendLine();
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public static ResolvedDeviceConfig Resolve(AppConfig config, DeviceItem item)
    {
        ResolvedDeviceConfig result = new ResolvedDeviceConfig();
        List<string> globalArgs = (config != null && config.globalArgs != null)
            ? new List<string>(config.globalArgs)
            : new List<string>();
        result.Args = globalArgs;

        if (config == null || config.devices == null || config.devices.Count == 0)
        {
            return result;
        }

        List<string> keys = new List<string>();
        keys.Add(item.HardwareSerial);
        keys.Add(item.AdbSerial);
        keys.Add(item.BaseMdnsName);
        if (item.CandidateEndpoints != null)
        {
            for (int c = 0; c < item.CandidateEndpoints.Count; c++)
            {
                keys.Add(item.CandidateEndpoints[c]);
            }
        }
        bool foundAlias = false;
        bool foundArgs = false;
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (string.IsNullOrWhiteSpace(key)) continue;

            DeviceConfig match = FindCaseInsensitive(config.devices, key);
            if (match != null)
            {
                if (!foundAlias && !string.IsNullOrWhiteSpace(match.alias))
                {
                    result.Alias = match.alias.Trim();
                    foundAlias = true;
                }
                if (!foundArgs && match.args != null && match.args.Count > 0)
                {
                    result.Args = new List<string>(match.args);
                    result.HasCustomArgs = true;
                    foundArgs = true;
                }
                if (foundAlias && foundArgs) break;
            }
        }
        return result;
    }

    static DeviceConfig FindCaseInsensitive(Dictionary<string, DeviceConfig> dict, string key)
    {
        DeviceConfig exact;
        if (dict.TryGetValue(key, out exact)) return exact;
        foreach (KeyValuePair<string, DeviceConfig> pair in dict)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }
        return null;
    }

    static void RemoveCaseInsensitive(Dictionary<string, DeviceConfig> dict, string key)
    {
        if (dict.Remove(key)) return;
        string matchKey = null;
        foreach (KeyValuePair<string, DeviceConfig> pair in dict)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                matchKey = pair.Key;
                break;
            }
        }
        if (matchKey != null) dict.Remove(matchKey);
    }
}
