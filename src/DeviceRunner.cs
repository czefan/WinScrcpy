using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class DeviceRunner
{
    public const int MaxTcpProbeConcurrency = 16;
    public const int MaxAdbQueryConcurrency = 4;

    readonly AppConfig config;
    public readonly ScrcpyLauncher Scrcpy;

    public DeviceRunner(AppConfig config)
    {
        this.config = config;
        this.Scrcpy = new ScrcpyLauncher(config);
    }

    public Task<ScanResult> ScanAsync(Action<ScanResult> onStage1 = null, CancellationToken cancellationToken = default)
    {
        return ScanInternalAsync(onStage1, cancellationToken);
    }

    public ScanResult Scan(CancellationToken cancellationToken = default)
    {
        return ScanInternalAsync(null, cancellationToken).GetAwaiter().GetResult();
    }

    async Task<ScanResult> ScanInternalAsync(Action<ScanResult> onStage1, CancellationToken cancellationToken)
    {
        Task<CommandOutput> devTask = RunAdbAsync(new string[] { "devices", "-l" }, 6000, cancellationToken);
        Task<CommandOutput> mdnsTask = RunAdbAsync(new string[] { "mdns", "services" }, 6000, cancellationToken);

        CommandOutput devOut = await devTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (onStage1 != null)
        {
            ScanResult stage1 = AdbScanner.ParseAndMerge(devOut != null ? devOut.StdOut : "", "");
            ApplyDevicesStageWarning(stage1, devOut);
            onStage1(stage1);
        }

        CommandOutput mdnsOut = await mdnsTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        ScanResult result = AdbScanner.ParseAndMerge(
            devOut != null ? devOut.StdOut : "",
            mdnsOut != null ? mdnsOut.StdOut : "");

        ApplyDevicesStageWarning(result, devOut);
        if (string.IsNullOrEmpty(result.Warning)
            && mdnsOut != null
            && (mdnsOut.HasExecutionFailure || (mdnsOut.ExitCode != 0 && !string.IsNullOrWhiteSpace(mdnsOut.StdErr))))
        {
            result.Warning = "mDNS 查询提示: " + mdnsOut.GetFailureSummary(mdnsOut.StdErr != null ? mdnsOut.StdErr.Trim() : "");
        }

        // 网络查询（TCP 探活、序列号读取）并发执行，仅返回只读映射；最终串行合并修改 result.Devices，避免竞态覆盖去重结果。
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

    static void ApplyDevicesStageWarning(ScanResult result, CommandOutput devOut)
    {
        if (devOut == null) return;
        if (!string.IsNullOrWhiteSpace(devOut.LaunchError))
        {
            result.Warning = "ADB 调用失败: " + devOut.LaunchError;
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
            result.Warning = "adb devices 输出读取不完整: " + devOut.GetFailureSummary("输出异常");
        }
    }

    async Task<Dictionary<string, string>> QueryConnectedSerialsAsync(IList<DeviceItem> devices, CancellationToken cancellationToken)
    {
        Dictionary<string, string> serialMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (devices == null || devices.Count == 0) return serialMap;

        List<string> targets = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem item = devices[i];
            if (item.Channel == ChannelKind.WirelessConnected
                && string.IsNullOrWhiteSpace(item.HardwareSerial)
                && !string.IsNullOrWhiteSpace(item.AdbSerial)
                && seen.Add(item.AdbSerial))
            {
                targets.Add(item.AdbSerial);
            }
        }
        if (targets.Count == 0) return serialMap;

        object sync = new object();
        using (SemaphoreSlim adbGate = new SemaphoreSlim(MaxAdbQueryConcurrency, MaxAdbQueryConcurrency))
        {
            Task[] tasks = new Task[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                string target = targets[i];
                tasks[i] = Task.Run(async delegate
                {
                    await adbGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        CommandOutput outp = await RunAdbAsync(
                            new string[] { "-s", target, "shell", "getprop", "ro.serialno" },
                            1500,
                            cancellationToken).ConfigureAwait(false);
                        if (outp.Success && !string.IsNullOrWhiteSpace(outp.StdOut))
                        {
                            string sn = outp.StdOut.Trim();
                            if (sn.IndexOfAny(new char[] { '\r', '\n', ' ' }) < 0)
                            {
                                lock (sync)
                                {
                                    serialMap[target] = sn;
                                }
                            }
                        }
                    }
                    finally
                    {
                        adbGate.Release();
                    }
                }, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return serialMap;
    }

    async Task<(List<string> savedToProbe, Dictionary<string, bool> probeResults)> QueryWirelessReachabilityAsync(
        IList<DeviceItem> devices,
        CancellationToken cancellationToken)
    {
        List<string> savedToProbe = new List<string>();
        Dictionary<string, bool> emptyResults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (devices == null) return (savedToProbe, emptyResults);

        List<string> uniqueEndpoints = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem item = devices[i];
            if (item.Channel != ChannelKind.MdnsPending) continue;
            for (int k = 0; k < item.CandidateEndpoints.Count; k++)
            {
                string ep = item.CandidateEndpoints[k];
                if (seen.Add(ep)) uniqueEndpoints.Add(ep);
            }
        }

        if (config != null && config.savedEndpoints != null)
        {
            for (int i = 0; i < config.savedEndpoints.Count; i++)
            {
                string ep = ConfigManager.NormalizeEndpoint(config.savedEndpoints[i]);
                if (string.IsNullOrEmpty(ep) || AdbScanner.HasOnlineOrPendingDeviceForEndpoint(devices, ep)) continue;
                savedToProbe.Add(ep);
                if (seen.Add(ep)) uniqueEndpoints.Add(ep);
            }
        }

        if (uniqueEndpoints.Count == 0) return (savedToProbe, emptyResults);

        Dictionary<string, bool> probeResults = await ProbeEndpointsAsync(
            uniqueEndpoints, 1000, MaxTcpProbeConcurrency, cancellationToken).ConfigureAwait(false);
        return (savedToProbe, probeResults);
    }

    internal static async Task<Dictionary<string, bool>> ProbeEndpointsAsync(
        IList<string> endpoints,
        int timeoutMs,
        int maxConcurrency,
        CancellationToken cancellationToken,
        Func<string, int, CancellationToken, Task<bool>> probeOverride = null)
    {
        Dictionary<string, bool> results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (endpoints == null || endpoints.Count == 0) return results;

        int limit = Math.Max(1, maxConcurrency);
        Func<string, int, CancellationToken, Task<bool>> probeFn = probeOverride ?? ProbeTcpEndpointAsync;
        object sync = new object();

        using (SemaphoreSlim gate = new SemaphoreSlim(limit, limit))
        {
            Task[] tasks = new Task[endpoints.Count];
            for (int i = 0; i < endpoints.Count; i++)
            {
                string ep = endpoints[i];
                tasks[i] = ProbeOneEndpointAsync(ep, timeoutMs, gate, probeFn, results, sync, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        return results;
    }

    static async Task ProbeOneEndpointAsync(
        string ep,
        int timeoutMs,
        SemaphoreSlim gate,
        Func<string, int, CancellationToken, Task<bool>> probeFn,
        Dictionary<string, bool> results,
        object sync,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool reachable = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                reachable = await probeFn(ep, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            lock (sync)
            {
                results[ep] = reachable;
            }
        }
        finally
        {
            gate.Release();
        }
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

    internal static async Task<bool> ProbeTcpEndpointAsync(string endpoint, int timeoutMs, CancellationToken cancellationToken)
    {
        string host;
        int port;
        if (!TryParseEndpoint(endpoint, out host, out port)) return false;

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

    public ToolResolutionResult ResolveAdbPath()
    {
        return ToolPathResolver.ResolveTool(config != null ? config.adbPath : "adb", "adb");
    }

    public Task<CommandOutput> RunAdbAsync(IList<string> args, int timeoutMs, CancellationToken cancellationToken = default)
    {
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

        // If adb still held a stale "offline" handle, adb connect replies "already connected to" without reviving it;
        // disconnect and reconnect once so the session comes back cleanly.
        if (msg.IndexOf("already connected to", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            await RunSingleDisconnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            (rawOk, msg, error) = await RunAdbConnectRawAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!rawOk) return (false, error);
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

        // Disconnect all merged active wireless handles (e.g. both ip:port and mDNS TLS serial).
        for (int i = 0; i < activeHandles.Count; i++)
        {
            (bool ok, string err) = await RunSingleDisconnectAsync(activeHandles[i], cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                anySucceeded = true;
            }
            else
            {
                lastError = err;
            }
        }

        if (anySucceeded) return (true, "");

        // If active handles were mDNS names that adb disconnect refused directly, fall back to ip:port candidates.
        for (int i = 0; i < fallbackTargets.Count; i++)
        {
            string target = fallbackTargets[i];
            if (AdbScanner.ContainsIgnoreCase(activeHandles, target)) continue;
            (bool ok, string err) = await RunSingleDisconnectAsync(target, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                return (true, "");
            }
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

    public bool OpenDeviceTerminal(string activeSerial, string displayTitle, TerminalMode mode, out string launchedWith, out string error)
    {
        error = "";
        launchedWith = "";
        if (string.IsNullOrWhiteSpace(activeSerial))
        {
            error = "设备序列号无效。";
            return false;
        }

        ToolResolutionResult adbRes = ResolveAdbPath();
        if (!adbRes.Success)
        {
            error = adbRes.Error;
            return false;
        }

        string adbExe = adbRes.ResolvedPath;
        string workDir = NormalizeDirForCli(AppContext.BaseDirectory);
        string wtExe = ResolveWtExecutable();
        string pwshExe = ResolvePwshExecutable();
        string psExe = string.IsNullOrEmpty(pwshExe) ? "powershell.exe" : pwshExe;
        string psTag = string.IsNullOrEmpty(pwshExe) ? "powershell" : "pwsh7";

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.UseShellExecute = true;
        psi.WorkingDirectory = Directory.Exists(workDir) ? workDir : AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(wtExe))
        {
            psi.FileName = wtExe;
            psi.Arguments = BuildWtTerminalArguments(adbExe, psExe, activeSerial, displayTitle, workDir, mode);
            launchedWith = mode == TerminalMode.AdbShell ? "wt" : ("wt · " + psTag);
        }
        else
        {
            psi.FileName = psExe;
            psi.Arguments = BuildStandalonePsTerminalArguments(adbExe, activeSerial, displayTitle, mode);
            launchedWith = psTag;
        }

        try
        {
            using (Process proc = Process.Start(psi))
            {
                if (proc == null)
                {
                    error = "未能启动终端进程。";
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            error = "启动终端失败: " + ex.Message;
            return false;
        }
    }

    internal static string ResolveWtExecutable()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            string aliasPath = Path.Combine(localApp, "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(aliasPath)) return aliasPath;
        }
        return ToolPathResolver.FindExecutableInPath("wt.exe");
    }

    internal static string ResolvePwshExecutable()
    {
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
        {
            string aliasPath = Path.Combine(localApp, "Microsoft", "WindowsApps", "pwsh.exe");
            if (File.Exists(aliasPath)) return aliasPath;
        }

        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(progFiles))
        {
            string stdPath = Path.Combine(progFiles, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(stdPath)) return stdPath;
        }
        return ToolPathResolver.FindExecutableInPath("pwsh.exe");
    }

    internal static string BuildWtTerminalArguments(string adbExe, string psExe, string activeSerial, string displayTitle, string workDir, TerminalMode mode)
    {
        string tabTitle = BuildTerminalTitle(activeSerial, displayTitle, mode);
        string cleanDir = NormalizeDirForCli(workDir);
        string safeDir = string.IsNullOrEmpty(cleanDir) ? "." : cleanDir.Replace(";", "\\;");

        string prefix = "-w 0 new-tab --title " + QuoteArg(tabTitle) + " -d " + QuoteArg(safeDir) + " -- ";
        if (mode == TerminalMode.AdbShell)
        {
            return prefix + QuoteArg(adbExe.Replace(";", "\\;")) + " -s " + QuoteArg(activeSerial) + " shell";
        }

        string initScript = BuildLocalPowerShellInitScript(adbExe, activeSerial, tabTitle);
        string shellTarget = string.IsNullOrWhiteSpace(psExe) ? "pwsh.exe" : psExe.Replace(";", "\\;");
        return prefix + QuoteArg(shellTarget) + " -NoLogo -NoExit -EncodedCommand " + EncodePowerShellCommand(initScript);
    }

    internal static string BuildStandalonePsTerminalArguments(string adbExe, string activeSerial, string displayTitle, TerminalMode mode)
    {
        string tabTitle = BuildTerminalTitle(activeSerial, displayTitle, mode);
        if (mode == TerminalMode.AdbShell)
        {
            string script = BuildShellPowerShellScript(adbExe, activeSerial, tabTitle);
            return "-NoLogo -EncodedCommand " + EncodePowerShellCommand(script);
        }

        string initScript = BuildLocalPowerShellInitScript(adbExe, activeSerial, tabTitle);
        return "-NoLogo -NoExit -EncodedCommand " + EncodePowerShellCommand(initScript);
    }

    internal static string BuildLocalPowerShellInitScript(string adbExe, string activeSerial, string windowTitle)
    {
        string cleanSerial = (activeSerial ?? "").Replace("\r", "").Replace("\n", "").Trim();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = '" + EscapePsSingleQuoted(windowTitle) + "'");
        sb.AppendLine("$env:ANDROID_SERIAL = '" + EscapePsSingleQuoted(cleanSerial) + "'");
        if (!string.Equals(adbExe, "adb", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(adbExe, "adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(adbExe);
            if (!string.IsNullOrEmpty(dir))
            {
                sb.AppendLine("$env:PATH = '" + EscapePsSingleQuoted(dir) + ";' + $env:PATH");
            }
            sb.AppendLine("function global:adb { & '" + EscapePsSingleQuoted(adbExe) + "' @args }");
        }
        sb.AppendLine("Write-Host '[WinScrcpy] 已绑定设备: " + EscapePsSingleQuoted(cleanSerial) + "' -ForegroundColor Cyan");
        return sb.ToString();
    }

    internal static string BuildShellPowerShellScript(string adbExe, string activeSerial, string windowTitle)
    {
        string cleanSerial = (activeSerial ?? "").Replace("\r", "").Replace("\n", "").Trim();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = '" + EscapePsSingleQuoted(windowTitle) + "'");
        sb.AppendLine("& '" + EscapePsSingleQuoted(adbExe) + "' -s '" + EscapePsSingleQuoted(cleanSerial) + "' shell");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { Read-Host 'ADB Shell 已退出，按回车关闭窗口' }");
        return sb.ToString();
    }

    static string BuildTerminalTitle(string activeSerial, string displayTitle, TerminalMode mode)
    {
        string suffix = mode == TerminalMode.AdbShell ? " - ADB Shell" : " - ADB 终端";
        return SanitizeLabelForShell(string.IsNullOrWhiteSpace(displayTitle) ? activeSerial : displayTitle) + suffix;
    }

    static string EncodePowerShellCommand(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script ?? ""));
    }

    static string EscapePsSingleQuoted(string text)
    {
        return (text ?? "").Replace("'", "''");
    }

    static string NormalizeDirForCli(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string trimmed = dir.Trim();
        if (trimmed.Length > 3 && trimmed.EndsWith("\\", StringComparison.Ordinal))
        {
            trimmed = trimmed.TrimEnd('\\');
            if (trimmed.Length == 2 && trimmed[1] == ':') trimmed = trimmed + "\\";
        }
        return trimmed;
    }

    static string SanitizeLabelForShell(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "ADB";
        char[] invalid = new char[] { ';', '&', '|', '<', '>', '^', '"', '\r', '\n', '%' };
        StringBuilder sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        }
        string cleaned = sb.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : "ADB";
    }

    static bool IsPngHeader(byte[] data)
    {
        if (data == null || data.Length < 8) return false;
        return data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;
    }

    public static string QuoteArg(string arg)
    {
        if (arg == null) return "\"\"";
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny(new char[] { ' ', '\t', '"', '\n', '\r' }) < 0) return arg;

        StringBuilder sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char c = arg[i];
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
