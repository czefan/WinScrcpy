using System;
using System.Collections.Generic;

internal enum ChannelKind
{
    Usb,
    WirelessConnected,
    MdnsPending,
    SavedPending,
    Offline,
    Unauthorized
}

internal enum TerminalMode
{
    AdbShell,
    LocalAdbEnv
}

internal sealed class PairingServiceInfo
{
    public string InstanceName;
    public string Endpoint;
}

internal sealed class DeviceItem
{
    public ChannelKind Channel;
    public string AdbSerial;
    public string HardwareSerial;
    public string BaseMdnsName;
    public string Model;
    public List<string> CandidateEndpoints;
    public List<string> AlternateSerials;

    public DeviceItem()
    {
        AdbSerial = "";
        HardwareSerial = "";
        BaseMdnsName = "";
        Model = "";
        CandidateEndpoints = new List<string>();
        AlternateSerials = new List<string>();
    }

    public bool CanConnect
    {
        get
        {
            return Channel == ChannelKind.Usb
                || Channel == ChannelKind.WirelessConnected
                || ((Channel == ChannelKind.MdnsPending || Channel == ChannelKind.SavedPending) && CandidateEndpoints.Count > 0);
        }
    }

    public string BadgeText
    {
        get
        {
            switch (Channel)
            {
                case ChannelKind.Usb: return "USB";
                case ChannelKind.WirelessConnected: return "无线已连";
                case ChannelKind.MdnsPending: return "mDNS 待连";
                case ChannelKind.SavedPending: return "常用待连";
                case ChannelKind.Unauthorized: return "未授权";
                default: return "离线";
            }
        }
    }

    public string PrimaryEndpointDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AdbSerial)) return AdbSerial;
            if (CandidateEndpoints.Count > 0) return CandidateEndpoints[0];
            return BaseMdnsName;
        }
    }
}

internal sealed class ScanResult
{
    public List<DeviceItem> Devices;
    public List<PairingServiceInfo> PairingServices;
    public int FilteredStaleMdnsCount;
    public string Warning;

    public ScanResult()
    {
        Devices = new List<DeviceItem>();
        PairingServices = new List<PairingServiceInfo>();
        FilteredStaleMdnsCount = 0;
        Warning = "";
    }
}

internal sealed class CommandOutput
{
    public int ExitCode;
    public string StdOut;
    public byte[] StdOutBytes;
    public string StdErr;
    public bool TimedOut;
    public bool Cancelled;
    public bool OutputTimedOut;
    public string LaunchError;
    public string OutputError;

    public bool HasExecutionFailure
    {
        get
        {
            return TimedOut
                || Cancelled
                || OutputTimedOut
                || !string.IsNullOrWhiteSpace(LaunchError)
                || !string.IsNullOrWhiteSpace(OutputError);
        }
    }

    public bool Success
    {
        get { return !HasExecutionFailure && ExitCode == 0; }
    }

    public string CombinedOutput
    {
        get
        {
            string a = (StdOut ?? "").Trim();
            string b = (StdErr ?? "").Trim();
            if (a.Length > 0 && b.Length > 0) return a + Environment.NewLine + b;
            return a.Length > 0 ? a : b;
        }
    }

    public string GetFailureSummary(string fallback, string timeoutMessage = "命令执行超时", string cancelledMessage = "操作已取消")
    {
        if (!string.IsNullOrWhiteSpace(LaunchError)) return LaunchError.Trim();
        if (Cancelled) return cancelledMessage;
        if (TimedOut) return timeoutMessage;
        if (!string.IsNullOrWhiteSpace(OutputError)) return OutputError.Trim();
        if (OutputTimedOut) return "命令输出未读取完整（等待输出流结束超时）。";
        string combined = CombinedOutput;
        if (!string.IsNullOrWhiteSpace(combined)) return combined;
        return string.IsNullOrWhiteSpace(fallback) ? "命令执行失败" : fallback;
    }
}
