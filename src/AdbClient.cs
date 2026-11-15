using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal interface IAdbClient
{
    Task<CommandOutput> QueryDevicesRawAsync(CancellationToken cancellationToken);
    Task<CommandOutput> QueryMdnsServicesRawAsync(CancellationToken cancellationToken);
    Task<string> QueryHardwareSerialAsync(string adbSerial, CancellationToken cancellationToken);
    Task<(bool ok, string activeSerial, string error)> EnsureConnectedAsync(DeviceItem item, CancellationToken cancellationToken);
}

internal sealed class AdbClient : IAdbClient
{
    readonly AppConfig config;
    internal Func<IList<string>, int, CancellationToken, Task<CommandOutput>> RunAdbOverrideForTest;

    public AdbClient(AppConfig config)
    {
        this.config = config;
    }

    public ToolResolutionResult ResolveAdbPath()
    {
        return ToolPathResolver.ResolveTool(config != null ? config.adbPath : "adb", "adb");
    }

    internal bool IsDaemonProcessRunning()
    {
        ToolResolutionResult res = ResolveAdbPath();
        if (!res.Success || string.IsNullOrWhiteSpace(res.ResolvedPath)) return true;
        string procName = Path.GetFileNameWithoutExtension(res.ResolvedPath);
        if (string.IsNullOrWhiteSpace(procName)) return true;
        try
        {
            return System.Diagnostics.Process.GetProcessesByName(procName).Length > 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    public Task<CommandOutput> RunAdbAsync(IList<string> args, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (RunAdbOverrideForTest != null)
        {
            return RunAdbOverrideForTest(args, timeoutMs, cancellationToken);
        }

        ToolResolutionResult adbRes = ResolveAdbPath();
        if (!adbRes.Success)
        {
            return Task.FromResult(new CommandOutput
            {
                ExitCode = -1,
                LaunchError = adbRes.Error
            });
        }

        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 禁止 adb 后台服务按 mDNS 广播自动连接无线设备，将「扫描」与「手动连接」彻底分离。
        env["ADB_MDNS_AUTO_CONNECT"] = "0";
        return ProcessRunner.RunTextAsync(adbRes.ResolvedPath, args, timeoutMs, env, AppContext.BaseDirectory, cancellationToken);
    }

    public CommandOutput RunAdb(IList<string> args, int timeoutMs, CancellationToken cancellationToken = default)
    {
        return RunAdbAsync(args, timeoutMs, cancellationToken).GetAwaiter().GetResult();
    }

    public Task<CommandOutput> QueryDevicesRawAsync(CancellationToken cancellationToken = default)
    {
        return RunAdbAsync(new string[] { "devices", "-l" }, 6000, cancellationToken);
    }

    public Task<CommandOutput> QueryMdnsServicesRawAsync(CancellationToken cancellationToken = default)
    {
        return RunAdbAsync(new string[] { "mdns", "services" }, 6000, cancellationToken);
    }

    public async Task<string> QueryHardwareSerialAsync(string adbSerial, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(adbSerial)) return "";
        CommandOutput outp = await RunAdbAsync(
            new string[] { "-s", adbSerial, "shell", "getprop", "ro.serialno" },
            1500,
            cancellationToken).ConfigureAwait(false);
        if (outp != null && outp.Success && !string.IsNullOrWhiteSpace(outp.StdOut))
        {
            string sn = outp.StdOut.Trim();
            if (sn.IndexOfAny(new char[] { '\r', '\n', ' ' }) < 0)
            {
                return sn;
            }
        }
        return "";
    }

    public async Task<List<PairingServiceInfo>> ListPairingServicesAsync(CancellationToken cancellationToken = default)
    {
        CommandOutput mdnsOut = await RunAdbAsync(new string[] { "mdns", "services" }, 3000, cancellationToken).ConfigureAwait(false);
        return (mdnsOut != null && mdnsOut.Success)
            ? AdbScanner.ParsePairingServices(mdnsOut.StdOut)
            : new List<PairingServiceInfo>();
    }

    public async Task<(bool ok, string activeSerial, string error)> EnsureConnectedAsync(DeviceItem item, CancellationToken cancellationToken = default)
    {
        if (item.Channel == ChannelKind.Usb || item.Channel == ChannelKind.WirelessConnected)
        {
            return (true, item.AdbSerial, "");
        }

        if (item.Channel == ChannelKind.Unauthorized)
        {
            return (false, "", "设备未授权，请在手机弹窗中点击「允许 USB 调试」。");
        }

        if (item.CandidateEndpoints == null || item.CandidateEndpoints.Count == 0)
        {
            return (false, "", "设备离线且未发现可用无线端点。");
        }

        string lastError = "无法连接到设备。";
        for (int i = 0; i < item.CandidateEndpoints.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ep = item.CandidateEndpoints[i];
            (bool ok, string err) = await ConnectEndpointAsync(ep, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return (true, ep, "");
            }
            lastError = string.Format("{0}: {1}", ep, err);
        }

        return (false, "", lastError);
    }

    public async Task<(bool ok, string error)> ConnectEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        (bool rawOk, string msg, string error) = await RunAdbConnectRawAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (!rawOk) return (false, error);

        // 仅当 adb devices 确认该端点确实处于 offline 状态时才断开重连，避免打断正常在线会话。
        if (msg.IndexOf("already connected to", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string offlineHandle = await FindOfflineHandleAsync(endpoint, msg, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(offlineHandle))
            {
                await RunSingleDisconnectAsync(offlineHandle, cancellationToken).ConfigureAwait(false);
                (rawOk, msg, error) = await RunAdbConnectRawAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!rawOk) return (false, error);
            }
        }

        if (msg.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
            && msg.IndexOf("cannot connect", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("unable to connect", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return (true, "");
        }

        return (false, string.IsNullOrWhiteSpace(msg) ? "adb connect 返回空响应" : msg);
    }

    async Task<(bool ok, string msg, string error)> RunAdbConnectRawAsync(string endpoint, CancellationToken cancellationToken)
    {
        CommandOutput outp = await RunAdbAsync(new string[] { "connect", endpoint }, 5000, cancellationToken).ConfigureAwait(false);
        if (outp.HasExecutionFailure)
        {
            return (false, "", outp.GetFailureSummary("adb connect 失败", "连接超时", "连接已取消"));
        }
        return (true, outp.CombinedOutput, "");
    }

    async Task<string> FindOfflineHandleAsync(string endpoint, string connectMsg, CancellationToken cancellationToken)
    {
        CommandOutput devicesOut = await QueryDevicesRawAsync(cancellationToken).ConfigureAwait(false);
        if (devicesOut == null || !devicesOut.Success) return "";

        string reportedTarget = ExtractAlreadyConnectedTarget(connectMsg);
        List<DeviceItem> items = AdbScanner.ParseDevices(devicesOut.StdOut);
        for (int i = 0; i < items.Count; i++)
        {
            DeviceItem item = items[i];
            if (item.Channel != ChannelKind.Offline) continue;
            if (string.Equals(item.AdbSerial, endpoint, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(reportedTarget)
                    && string.Equals(item.AdbSerial, reportedTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return item.AdbSerial;
            }
        }
        return "";
    }

    static string ExtractAlreadyConnectedTarget(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "";
        const string marker = "already connected to ";
        int idx = msg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        string rest = msg.Substring(idx + marker.Length).Trim();
        int end = rest.IndexOfAny(new char[] { '\r', '\n', ' ', '\t' });
        return end > 0 ? rest.Substring(0, end) : rest;
    }

    public async Task<(bool ok, string error)> DisconnectDeviceAsync(DeviceItem item, CancellationToken cancellationToken = default)
    {
        if (item == null || item.Channel == ChannelKind.Usb)
        {
            return (false, "USB 有线设备无法通过软件断开，请直接拔出数据线。");
        }

        List<string> activeHandles = BuildActiveDisconnectHandles(item);
        List<string> fallbackTargets = BuildDisconnectTargets(item);
        if (activeHandles.Count == 0 && fallbackTargets.Count == 0)
        {
            return (false, "未找到可断开的无线端点。");
        }

        bool anySucceeded = false;
        string lastError = "断开无线连接失败。";

        for (int i = 0; i < activeHandles.Count; i++)
        {
            (bool ok, string err) = await RunSingleDisconnectAsync(activeHandles[i], cancellationToken).ConfigureAwait(false);
            if (ok) anySucceeded = true;
            else lastError = err;
        }

        if (anySucceeded) return (true, "");

        for (int i = 0; i < fallbackTargets.Count; i++)
        {
            string target = fallbackTargets[i];
            if (AdbScanner.ContainsIgnoreCase(activeHandles, target)) continue;
            (bool ok, string err) = await RunSingleDisconnectAsync(target, cancellationToken).ConfigureAwait(false);
            if (ok) return (true, "");
            lastError = err;
        }

        return (false, lastError);
    }

    async Task<(bool ok, string error)> RunSingleDisconnectAsync(string target, CancellationToken cancellationToken)
    {
        CommandOutput outp = await RunAdbAsync(new string[] { "disconnect", target }, 5000, cancellationToken).ConfigureAwait(false);
        if (outp.HasExecutionFailure)
        {
            return (false, outp.GetFailureSummary("adb disconnect 失败", "断开超时", "断开已取消"));
        }

        string msg = outp.CombinedOutput;
        if (outp.ExitCode == 0
            && msg.IndexOf("error:", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("no such device", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return (true, "");
        }
        return (false, string.IsNullOrWhiteSpace(msg) ? "adb disconnect 失败" : msg);
    }

    public async Task<(bool ok, string message)> PairEndpointAsync(string endpoint, string code, CancellationToken cancellationToken = default)
    {
        CommandOutput outp = await RunAdbAsync(new string[] { "pair", endpoint, code }, 8000, cancellationToken).ConfigureAwait(false);
        if (outp.HasExecutionFailure)
        {
            return (false, outp.GetFailureSummary("配对失败", "配对超时，请核对手机屏幕上显示的 IP:Port 与配对码。", "配对已取消。"));
        }

        string msg = outp.CombinedOutput;
        bool ok = outp.ExitCode == 0 && msg.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0;
        return (ok, string.IsNullOrWhiteSpace(msg) ? (ok ? "配对成功" : "配对失败") : msg);
    }

    public async Task<(bool ok, Bitmap bitmap, string error)> CaptureScreenshotAsync(string activeSerial, CancellationToken cancellationToken = default)
    {
        ToolResolutionResult adbRes = ResolveAdbPath();
        if (!adbRes.Success)
        {
            return (false, null, adbRes.Error);
        }

        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        env["ADB_MDNS_AUTO_CONNECT"] = "0";

        CommandOutput outp = await ProcessRunner.RunBinaryAsync(
            adbRes.ResolvedPath,
            new string[] { "-s", activeSerial, "exec-out", "screencap", "-p" },
            12000,
            env,
            AppContext.BaseDirectory,
            cancellationToken).ConfigureAwait(false);

        if (outp.HasExecutionFailure)
        {
            return (false, null, outp.GetFailureSummary(
                "读取截图数据失败。",
                "截屏超时，请确认设备屏幕正常且 ADB 连接未断开。",
                "截屏已取消。"));
        }

        if (outp.ExitCode != 0 || !IsPngHeader(outp.StdOutBytes))
        {
            string errText = (outp.StdErr ?? "").Trim();
            return (false, null, string.IsNullOrWhiteSpace(errText)
                ? "设备未能返回有效的 PNG 截图数据。"
                : errText);
        }

        try
        {
            using (MemoryStream ms = new MemoryStream(outp.StdOutBytes))
            using (Bitmap raw = new Bitmap(ms))
            {
                return (true, new Bitmap(raw), "");
            }
        }
        catch (Exception ex)
        {
            return (false, null, "解析截图图像失败: " + ex.Message);
        }
    }

    static List<string> BuildActiveDisconnectHandles(DeviceItem item)
    {
        List<string> handles = new List<string>();
        if (item == null) return handles;
        AdbScanner.AddUniqueIgnoreCase(handles, item.AdbSerial);
        if (item.AlternateSerials != null)
        {
            for (int i = 0; i < item.AlternateSerials.Count; i++)
            {
                AdbScanner.AddUniqueIgnoreCase(handles, item.AlternateSerials[i]);
            }
        }
        return handles;
    }

    public static List<string> BuildDisconnectTargets(DeviceItem item)
    {
        List<string> targets = new List<string>();
        if (item == null) return targets;

        if (!string.IsNullOrWhiteSpace(item.AdbSerial)
            && item.AdbSerial.IndexOf("._adb-", StringComparison.OrdinalIgnoreCase) < 0)
        {
            AdbScanner.AddUniqueIgnoreCase(targets, item.AdbSerial);
        }
        if (item.AlternateSerials != null)
        {
            for (int i = 0; i < item.AlternateSerials.Count; i++)
            {
                string alt = item.AlternateSerials[i];
                if (!string.IsNullOrWhiteSpace(alt) && alt.IndexOf("._adb-", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    AdbScanner.AddUniqueIgnoreCase(targets, alt);
                }
            }
        }
        if (item.CandidateEndpoints != null)
        {
            for (int i = 0; i < item.CandidateEndpoints.Count; i++)
            {
                AdbScanner.AddUniqueIgnoreCase(targets, item.CandidateEndpoints[i]);
            }
        }
        if (!string.IsNullOrWhiteSpace(item.AdbSerial))
        {
            AdbScanner.AddUniqueIgnoreCase(targets, item.AdbSerial);
        }
        if (item.AlternateSerials != null)
        {
            for (int i = 0; i < item.AlternateSerials.Count; i++)
            {
                AdbScanner.AddUniqueIgnoreCase(targets, item.AlternateSerials[i]);
            }
        }
        return targets;
    }

    public static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        string trimmed = endpoint.Trim();
        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon >= trimmed.Length - 1) return false;

        string rawHost = trimmed.Substring(0, colon).Trim();
        if (rawHost.StartsWith("[") && rawHost.EndsWith("]") && rawHost.Length > 2)
        {
            rawHost = rawHost.Substring(1, rawHost.Length - 2);
        }

        int parsedPort;
        if (!int.TryParse(trimmed.Substring(colon + 1).Trim(), out parsedPort)) return false;
        if (parsedPort < 1 || parsedPort > 65535 || string.IsNullOrWhiteSpace(rawHost)) return false;

        host = rawHost;
        port = parsedPort;
        return true;
    }

    static bool IsPngHeader(byte[] data)
    {
        if (data == null || data.Length < 8) return false;
        return data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
    }
}
