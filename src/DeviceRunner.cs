using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal sealed class DeviceRunner
{
    readonly AppConfig config;
    public readonly ScrcpyLauncher Scrcpy;

    public DeviceRunner(AppConfig config)
    {
        this.config = config;
        this.Scrcpy = new ScrcpyLauncher(config);
    }

    public ScanResult Scan(CancellationToken cancellationToken = default)
    {
        CommandOutput devOut = null;
        CommandOutput mdnsOut = null;

        Thread t1 = new Thread(delegate() { devOut = RunAdb(new string[] { "devices", "-l" }, 6000, cancellationToken); });
        Thread t2 = new Thread(delegate() { mdnsOut = RunAdb(new string[] { "mdns", "services" }, 6000, cancellationToken); });
        t1.IsBackground = true;
        t2.IsBackground = true;
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        ScanResult result = AdbScanner.ParseAndMerge(
            devOut != null ? devOut.StdOut : "",
            mdnsOut != null ? mdnsOut.StdOut : "");

        if (devOut != null && !string.IsNullOrWhiteSpace(devOut.LaunchError))
        {
            result.Warning = "ADB 调用失败: " + devOut.LaunchError;
        }
        else if (devOut != null && devOut.Cancelled)
        {
            result.Warning = "ADB 扫描已取消。";
        }
        else if (devOut != null && devOut.TimedOut)
        {
            result.Warning = "adb devices 执行超时，可尝试点击「重启 ADB」。";
        }
        else if (devOut != null && (devOut.OutputTimedOut || !string.IsNullOrWhiteSpace(devOut.OutputError)))
        {
            result.Warning = "adb devices 输出读取不完整: " + devOut.GetFailureSummary("输出异常");
        }
        else if (mdnsOut != null && (mdnsOut.HasExecutionFailure || (mdnsOut.ExitCode != 0 && !string.IsNullOrWhiteSpace(mdnsOut.StdErr))))
        {
            result.Warning = "mDNS 查询提示: " + mdnsOut.GetFailureSummary(mdnsOut.StdErr != null ? mdnsOut.StdErr.Trim() : "");
        }

        FilterAndAppendWirelessEndpoints(result);
        EnrichConnectedSerials(result, cancellationToken);
        return result;
    }

    void EnrichConnectedSerials(ScanResult result, CancellationToken cancellationToken)
    {
        List<DeviceItem> missing = new List<DeviceItem>();
        for (int i = 0; i < result.Devices.Count; i++)
        {
            DeviceItem item = result.Devices[i];
            if (item.Channel == ChannelKind.WirelessConnected
                && string.IsNullOrWhiteSpace(item.HardwareSerial)
                && !string.IsNullOrWhiteSpace(item.AdbSerial))
            {
                missing.Add(item);
            }
        }
        if (missing.Count == 0) return;

        Dictionary<string, string> serialMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object sync = new object();
        RunParallelAndWait(missing.Count, delegate(int i)
        {
            string target = missing[i].AdbSerial;
            CommandOutput outp = RunAdb(new string[] { "-s", target, "shell", "getprop", "ro.serialno" }, 1500, cancellationToken);
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
        });

        AdbScanner.EnrichAndDeduplicate(result, delegate(DeviceItem item)
        {
            string sn;
            return serialMap.TryGetValue(item.AdbSerial, out sn) ? sn : "";
        });
    }

    void FilterAndAppendWirelessEndpoints(ScanResult result)
    {
        List<string> uniqueEndpoints = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < result.Devices.Count; i++)
        {
            DeviceItem item = result.Devices[i];
            if (item.Channel != ChannelKind.MdnsPending) continue;
            for (int k = 0; k < item.CandidateEndpoints.Count; k++)
            {
                string ep = item.CandidateEndpoints[k];
                if (seen.Add(ep)) uniqueEndpoints.Add(ep);
            }
        }

        List<string> savedToProbe = new List<string>();
        if (config != null && config.savedEndpoints != null)
        {
            for (int i = 0; i < config.savedEndpoints.Count; i++)
            {
                string ep = ConfigManager.NormalizeEndpoint(config.savedEndpoints[i]);
                if (string.IsNullOrEmpty(ep) || AdbScanner.HasOnlineOrPendingDeviceForEndpoint(result.Devices, ep)) continue;
                savedToProbe.Add(ep);
                if (seen.Add(ep)) uniqueEndpoints.Add(ep);
            }
        }

        if (uniqueEndpoints.Count == 0) return;

        Dictionary<string, bool> probeResults = ProbeEndpointsParallel(uniqueEndpoints, 1000);
        AdbScanner.FilterReachableMdns(result, delegate(string ep)
        {
            bool ok;
            return probeResults.TryGetValue(ep, out ok) && ok;
        });

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

    static Dictionary<string, bool> ProbeEndpointsParallel(List<string> endpoints, int timeoutMs)
    {
        Dictionary<string, bool> results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        object sync = new object();
        RunParallelAndWait(endpoints.Count, delegate(int i)
        {
            string ep = endpoints[i];
            bool reachable = ProbeTcpEndpoint(ep, timeoutMs);
            lock (sync)
            {
                results[ep] = reachable;
            }
        });
        return results;
    }

    // 阻塞型并发任务（TCP 探测、adb 调用）使用独立线程，避免占满线程池后新线程按约 500ms 节奏注入导致扫描变慢。
    static void RunParallelAndWait(int count, Action<int> work)
    {
        Thread[] threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            threads[i] = new Thread(delegate() { work(index); });
            threads[i].IsBackground = true;
            threads[i].Start();
        }
        for (int i = 0; i < count; i++)
        {
            threads[i].Join();
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

    static bool ProbeTcpEndpoint(string endpoint, int timeoutMs)
    {
        string host;
        int port;
        if (!TryParseEndpoint(endpoint, out host, out port)) return false;

        IPAddress ip;
        AddressFamily family = (IPAddress.TryParse(host, out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;

        TcpClient client = new TcpClient(family);
        IAsyncResult ar = null;
        try
        {
            ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
            client.EndConnect(ar);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { client.Close(); }
            catch (Exception) { }
            if (ar != null)
            {
                try { ar.AsyncWaitHandle.Close(); }
                catch (Exception) { }
            }
        }
    }

    public ToolResolutionResult ResolveAdbPath()
    {
        return ToolPathResolver.ResolveTool(config != null ? config.adbPath : "adb", "adb");
    }

    public CommandOutput RunAdb(IList<string> args, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ToolResolutionResult adbRes = ResolveAdbPath();
        if (!adbRes.Success)
        {
            return new CommandOutput
            {
                ExitCode = -1,
                LaunchError = adbRes.Error
            };
        }

        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 禁止 adb 后台服务按 mDNS 广播自动连接无线设备，将「扫描」与「手动连接」彻底分离。
        env["ADB_MDNS_AUTO_CONNECT"] = "0";
        return ProcessRunner.RunText(adbRes.ResolvedPath, args, timeoutMs, env, AppContext.BaseDirectory, cancellationToken);
    }

    public List<PairingServiceInfo> ListPairingServices(CancellationToken cancellationToken = default)
    {
        CommandOutput mdnsOut = RunAdb(new string[] { "mdns", "services" }, 3000, cancellationToken);
        return (mdnsOut != null && mdnsOut.Success)
            ? AdbScanner.ParsePairingServices(mdnsOut.StdOut)
            : new List<PairingServiceInfo>();
    }

    public bool EnsureConnected(DeviceItem item, out string activeSerial, out string error)
    {
        activeSerial = "";
        error = "";

        if (item.Channel == ChannelKind.Usb || item.Channel == ChannelKind.WirelessConnected)
        {
            activeSerial = item.AdbSerial;
            return true;
        }

        if (item.Channel == ChannelKind.Unauthorized)
        {
            error = "设备未授权，请在手机弹窗中点击「允许 USB 调试」。";
            return false;
        }

        if (item.CandidateEndpoints == null || item.CandidateEndpoints.Count == 0)
        {
            error = "设备离线且未发现可用无线端点。";
            return false;
        }

        string lastError = "无法连接到设备。";
        for (int i = 0; i < item.CandidateEndpoints.Count; i++)
        {
            string ep = item.CandidateEndpoints[i];
            string err;
            if (ConnectEndpoint(ep, out err))
            {
                activeSerial = ep;
                return true;
            }
            lastError = string.Format("{0}: {1}", ep, err);
        }

        error = lastError;
        return false;
    }

    public bool ConnectEndpoint(string endpoint, out string error)
    {
        string msg;
        if (!RunAdbConnectRaw(endpoint, out msg, out error)) return false;

        // If adb still held a stale "offline" handle, adb connect replies "already connected to" without reviving it;
        // disconnect and reconnect once so the session comes back cleanly.
        if (msg.IndexOf("already connected to", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string ignored;
            RunSingleDisconnect(endpoint, out ignored);
            if (!RunAdbConnectRaw(endpoint, out msg, out error)) return false;
        }

        if (msg.IndexOf("connected to", StringComparison.OrdinalIgnoreCase) >= 0
            && msg.IndexOf("cannot connect", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("unable to connect", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }

        error = string.IsNullOrWhiteSpace(msg) ? "adb connect 返回空响应" : msg;
        return false;
    }

    bool RunAdbConnectRaw(string endpoint, out string msg, out string error)
    {
        msg = "";
        error = "";
        CommandOutput outp = RunAdb(new string[] { "connect", endpoint }, 5000);
        if (outp.HasExecutionFailure)
        {
            error = outp.GetFailureSummary("adb connect 失败", "连接超时", "连接已取消");
            return false;
        }
        msg = outp.CombinedOutput;
        return true;
    }

    public bool DisconnectDevice(DeviceItem item, out string error)
    {
        error = "";
        if (item == null || item.Channel == ChannelKind.Usb)
        {
            error = "USB 有线设备无法通过软件断开，请直接拔出数据线。";
            return false;
        }

        List<string> activeHandles = BuildActiveDisconnectHandles(item);
        List<string> fallbackTargets = BuildDisconnectTargets(item);
        if (activeHandles.Count == 0 && fallbackTargets.Count == 0)
        {
            error = "未找到可断开的无线端点。";
            return false;
        }

        bool anySucceeded = false;
        string lastError = "断开无线连接失败。";

        // Disconnect all merged active wireless handles (e.g. both ip:port and mDNS TLS serial).
        for (int i = 0; i < activeHandles.Count; i++)
        {
            string err;
            if (RunSingleDisconnect(activeHandles[i], out err))
            {
                anySucceeded = true;
            }
            else
            {
                lastError = err;
            }
        }

        if (anySucceeded) return true;

        // If active handles were mDNS names that adb disconnect refused directly, fall back to ip:port candidates.
        for (int i = 0; i < fallbackTargets.Count; i++)
        {
            string target = fallbackTargets[i];
            if (AdbScanner.ContainsIgnoreCase(activeHandles, target)) continue;
            string err;
            if (RunSingleDisconnect(target, out err))
            {
                return true;
            }
            lastError = err;
        }

        error = lastError;
        return false;
    }

    bool RunSingleDisconnect(string target, out string error)
    {
        error = "";
        CommandOutput outp = RunAdb(new string[] { "disconnect", target }, 5000);
        if (outp.HasExecutionFailure)
        {
            error = outp.GetFailureSummary("adb disconnect 失败", "断开超时", "断开已取消");
            return false;
        }

        string msg = outp.CombinedOutput;
        if (outp.ExitCode == 0
            && msg.IndexOf("error:", StringComparison.OrdinalIgnoreCase) < 0
            && msg.IndexOf("no such device", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return true;
        }
        error = string.IsNullOrWhiteSpace(msg) ? "adb disconnect 失败" : msg;
        return false;
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

    public bool PairEndpoint(string endpoint, string code, out string message)
    {
        CommandOutput outp = RunAdb(new string[] { "pair", endpoint, code }, 8000);
        if (outp.HasExecutionFailure)
        {
            message = outp.GetFailureSummary("配对失败", "配对超时，请核对手机屏幕上显示的 IP:Port 与配对码。", "配对已取消。");
            return false;
        }

        string msg = outp.CombinedOutput;
        bool ok = outp.ExitCode == 0 && msg.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) >= 0;
        message = string.IsNullOrWhiteSpace(msg) ? (ok ? "配对成功" : "配对失败") : msg;
        return ok;
    }

    public bool CaptureScreenshot(string activeSerial, out Bitmap bitmap, out string error, CancellationToken cancellationToken = default)
    {
        bitmap = null;
        error = "";
        ToolResolutionResult adbRes = ResolveAdbPath();
        if (!adbRes.Success)
        {
            error = adbRes.Error;
            return false;
        }

        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        env["ADB_MDNS_AUTO_CONNECT"] = "0";

        CommandOutput outp = ProcessRunner.RunBinary(
            adbRes.ResolvedPath,
            new string[] { "-s", activeSerial, "exec-out", "screencap", "-p" },
            12000,
            env,
            AppContext.BaseDirectory,
            cancellationToken);

        if (outp.HasExecutionFailure)
        {
            error = outp.GetFailureSummary(
                "读取截图数据失败。",
                "截屏超时，请确认设备屏幕正常且 ADB 连接未断开。",
                "截屏已取消。");
            return false;
        }

        if (outp.ExitCode != 0 || !IsPngHeader(outp.StdOutBytes))
        {
            string errText = (outp.StdErr ?? "").Trim();
            error = string.IsNullOrWhiteSpace(errText)
                ? "设备未能返回有效的 PNG 截图数据。"
                : errText;
            return false;
        }

        try
        {
            using (MemoryStream ms = new MemoryStream(outp.StdOutBytes))
            using (Bitmap raw = new Bitmap(ms))
            {
                bitmap = new Bitmap(raw);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "解析截图图像失败: " + ex.Message;
            return false;
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
