using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class BatchLaunchResult
{
    public List<string> ReadyLines = new List<string>();
    public List<string> AliveLines = new List<string>();
    public List<string> FailureLines = new List<string>();

    public bool HasFailures { get { return FailureLines.Count > 0; } }
    public bool HasAnyStarted { get { return ReadyLines.Count > 0 || AliveLines.Count > 0; } }

    public string BuildSummary()
    {
        List<string> parts = new List<string>();
        if (ReadyLines.Count > 0) parts.Add(string.Format("投屏就绪 {0} 台", ReadyLines.Count));
        if (AliveLines.Count > 0) parts.Add(string.Format("已启动(未确认就绪) {0} 台", AliveLines.Count));
        if (FailureLines.Count > 0) parts.Add(string.Format("失败 {0} 台", FailureLines.Count));
        return string.Join("，", parts.ToArray());
    }

    public string BuildDetail()
    {
        List<string> sections = new List<string>();
        if (ReadyLines.Count > 0)
        {
            sections.Add("【已确认投屏就绪】\n" + string.Join("\n", ReadyLines.ToArray()));
        }
        if (AliveLines.Count > 0)
        {
            sections.Add("【已启动（未确认画面就绪）】\n" + string.Join("\n", AliveLines.ToArray()));
        }
        if (FailureLines.Count > 0)
        {
            sections.Add("【启动失败】\n" + string.Join("\n\n", FailureLines.ToArray()));
        }
        return string.Join("\n\n", sections.ToArray());
    }
}

/// <summary>
/// Builds scrcpy arguments/environment and evaluates launch state
/// ("stream ready" vs "process alive" vs "failed") via detached log files.
/// </summary>
internal sealed class ScrcpyLauncher
{
    public const int DefaultReadyTimeoutMs = 10000;
    public const int UnverifiableModeObserveMs = 600;

    readonly AppConfig config;

    public ScrcpyLauncher(AppConfig config)
    {
        this.config = config ?? AppConfig.CreateDefault();
    }

    public static string LogDirectory
    {
        get { return Path.Combine(Path.GetTempPath(), "WinScrcpy", "logs"); }
    }

    public static ProcessStartInfo BuildStartInfo(
        string scrcpyExe,
        string adbExe,
        string activeSerial,
        string windowTitle,
        IList<string> extraArgs)
    {
        List<string> args = new List<string>();
        args.Add("-s");
        args.Add(activeSerial);
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            args.Add("--window-title=" + windowTitle);
        }
        if (extraArgs != null)
        {
            for (int i = 0; i < extraArgs.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(extraArgs[i]))
                {
                    args.Add(extraArgs[i]);
                }
            }
        }

        Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(adbExe))
        {
            env["ADB"] = adbExe;
        }
        env["ADB_MDNS_AUTO_CONNECT"] = "0";

        return ProcessRunner.BuildStartInfo(scrcpyExe, args, env, AppContext.BaseDirectory, false, false);
    }

    public ScrcpyLaunchResult Launch(
        string activeSerial,
        string windowTitle,
        List<string> extraArgs,
        int readyTimeoutMs = DefaultReadyTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ScrcpyLaunchResult result = new ScrcpyLaunchResult();
        if (cancellationToken.IsCancellationRequested)
        {
            result.Outcome = ScrcpyLaunchOutcome.Failed;
            result.Cancelled = true;
            result.ErrorMessage = "启动请求在创建进程前已取消。";
            return result;
        }

        ToolResolutionResult scrcpyRes = ToolPathResolver.ResolveTool(config.scrcpyPath, "scrcpy");
        if (!scrcpyRes.Success)
        {
            result.Outcome = ScrcpyLaunchOutcome.Failed;
            result.ErrorMessage = scrcpyRes.Error;
            return result;
        }

        ToolResolutionResult adbRes = ToolPathResolver.ResolveTool(config.adbPath, "adb");
        if (!adbRes.Success)
        {
            result.Outcome = ScrcpyLaunchOutcome.Failed;
            result.ErrorMessage = adbRes.Error;
            return result;
        }

        string unverifiableReason = DetectUnverifiableReadyReason(extraArgs);
        bool canVerifyReadyLog = string.IsNullOrEmpty(unverifiableReason);
        int monitorTimeout = canVerifyReadyLog
            ? readyTimeoutMs
            : Math.Min(Math.Max(0, readyTimeoutMs), UnverifiableModeObserveMs);

        string logPath = CreateLogPath(activeSerial);
        DetachedProcessLauncher.PruneOldLogs(LogDirectory, "scrcpy-*.log", 20);
        ProcessStartInfo psi = BuildStartInfo(scrcpyRes.ResolvedPath, adbRes.ResolvedPath, activeSerial, windowTitle, extraArgs);

        DetachedMonitorResult mon = DetachedProcessLauncher.LaunchWithLogAndMonitor(
            psi,
            logPath,
            monitorTimeout,
            canVerifyReadyLog ? IsReadyLogLine : (Func<string, bool>)null,
            cancellationToken);

        result.ProcessStarted = mon.ProcessStarted;
        result.ProcessId = mon.ProcessId;
        result.ExitCode = mon.ExitCode;
        result.LogFilePath = mon.LogFilePath;

        if (mon.Status == DetachedMonitorStatus.MatchedReady)
        {
            result.Outcome = ScrcpyLaunchOutcome.StreamReady;
            result.DiagnosticSummary = "已检测到投屏就绪日志，GUI 退出后 scrcpy 将继续独立运行。";
            return result;
        }

        if (mon.Status == DetachedMonitorStatus.TimedOutAlive)
        {
            result.Outcome = ScrcpyLaunchOutcome.ProcessAlive;
            result.DiagnosticSummary = ExtractDiagnostic(mon.CapturedLog, 4);
            result.ErrorMessage = !canVerifyReadyLog
                ? string.Format("scrcpy 进程已启动（{0}），未确认投屏画面就绪。", unverifiableReason)
                : string.Format("scrcpy 进程已启动并保持运行，但在 {0:0.#} 秒内未捕获到就绪日志（仅适配 scrcpy 2.x–5.x 默认视频日志），仅确认进程存活。", monitorTimeout / 1000.0);
            return result;
        }

        if (mon.Status == DetachedMonitorStatus.Cancelled)
        {
            result.Cancelled = true;
            if (mon.ProcessStarted)
            {
                result.Outcome = ScrcpyLaunchOutcome.ProcessAlive;
                result.DiagnosticSummary = ExtractDiagnostic(mon.CapturedLog, 4);
                result.ErrorMessage = string.Format("scrcpy 进程已启动 (PID {0})，已停止继续等待就绪信号。", mon.ProcessId);
            }
            else
            {
                result.Outcome = ScrcpyLaunchOutcome.Failed;
                result.ErrorMessage = "启动请求在创建进程前已取消。";
            }
            return result;
        }

        result.Outcome = ScrcpyLaunchOutcome.Failed;
        if (mon.Status == DetachedMonitorStatus.StartFailed)
        {
            result.ErrorMessage = "启动 scrcpy 失败: " + (string.IsNullOrWhiteSpace(mon.Error) ? "未知启动错误" : mon.Error);
            return result;
        }

        string diag = ExtractDiagnostic(mon.CapturedLog, 6);
        result.DiagnosticSummary = diag;
        string header = mon.ExitCode == 0
            ? "scrcpy 在未确认投屏就绪前已提前退出 (退出码 0)。"
            : string.Format("scrcpy 启动失败并退出 (退出码 {0})。", mon.ExitCode);
        if (!string.IsNullOrWhiteSpace(diag))
        {
            header += "\n诊断输出:\n" + diag;
        }
        if (!string.IsNullOrWhiteSpace(result.LogFilePath))
        {
            header += "\n完整日志: " + result.LogFilePath;
        }
        result.ErrorMessage = header;
        return result;
    }

    /// <summary>
    /// Returns a reason string when arguments suppress INFO logs or standard video rendering,
    /// so the caller avoids waiting the full ready timeout for a log line that cannot appear.
    /// </summary>
    public static string DetectUnverifiableReadyReason(IList<string> args)
    {
        if (args == null) return null;

        bool infoSuppressed = false;
        bool nonStandardVideo = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = (args[i] ?? "").Trim();
            if (arg.Length == 0) continue;

            if (string.Equals(arg, "--no-video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--no-playback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--no-video-playback", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--no-window", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--otg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-N", StringComparison.Ordinal))
            {
                nonStandardVideo = true;
            }
            else if (arg.StartsWith("--verbosity=", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("-V=", StringComparison.Ordinal))
            {
                string val = arg.Substring(arg.IndexOf('=') + 1).Trim().ToLowerInvariant();
                if (val == "warn" || val == "error") infoSuppressed = true;
            }
            else if ((string.Equals(arg, "--verbosity", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-V", StringComparison.Ordinal))
                && i + 1 < args.Count)
            {
                string val = (args[i + 1] ?? "").Trim().ToLowerInvariant();
                if (val == "warn" || val == "error") infoSuppressed = true;
            }
        }

        if (infoSuppressed) return "配置抑制了 INFO 日志输出";
        if (nonStandardVideo) return "当前参数未启用标准视频窗口投屏";
        return null;
    }

    /// <summary>
    /// Only matches explicit video frame/texture initialization logs emitted by scrcpy 2.x–5.x.
    /// </summary>
    public static bool IsReadyLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.IndexOf("INFO: Texture:", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("INFO: Initial texture:", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string ExtractDiagnostic(string capturedLog, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(capturedLog)) return "";
        string[] rawLines = capturedLog.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> priority = new List<string>();
        List<string> tail = new List<string>();

        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].Trim();
            if (line.Length == 0) continue;
            tail.Add(line);
            if (line.IndexOf("ERROR:", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("WARN:", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("ambiguous option", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                priority.Add(line);
            }
        }

        List<string> chosen = priority.Count > 0 ? priority : tail;
        int start = Math.Max(0, chosen.Count - maxLines);
        List<string> slice = new List<string>();
        for (int i = start; i < chosen.Count; i++)
        {
            slice.Add(chosen[i]);
        }
        return string.Join("\n", slice.ToArray());
    }

    public async Task<BatchLaunchResult> LaunchBatchAsync(
        IList<DeviceItem> targets,
        IAdbClient adbClient,
        Func<DeviceItem, ResolvedDeviceConfig, string> displayTitleSelector,
        CancellationToken cancellationToken = default,
        Func<string, string, List<string>, CancellationToken, ScrcpyLaunchResult> launchOverride = null)
    {
        BatchLaunchResult outcome = new BatchLaunchResult();
        if (targets == null || targets.Count == 0) return outcome;

        int count = targets.Count;
        string[] readyLines = new string[count];
        string[] aliveLines = new string[count];
        string[] failureLines = new string[count];

        int maxParallel = Math.Min(3, Math.Max(1, count));
        Func<string, string, List<string>, CancellationToken, ScrcpyLaunchResult> launchFn =
            launchOverride ?? ((serial, title, args, ct) => Launch(serial, title, args, DefaultReadyTimeoutMs, ct));

        using (SemaphoreSlim gate = new SemaphoreSlim(maxParallel, maxParallel))
        {
            List<Task> tasks = new List<Task>(count);
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                tasks.Add(LaunchSingleTargetAsync(
                    targets[idx],
                    idx,
                    gate,
                    adbClient,
                    displayTitleSelector,
                    launchFn,
                    readyLines,
                    aliveLines,
                    failureLines,
                    cancellationToken));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        outcome.ReadyLines = CollectNonEmpty(readyLines);
        outcome.AliveLines = CollectNonEmpty(aliveLines);
        outcome.FailureLines = CollectNonEmpty(failureLines);
        return outcome;
    }

    async Task LaunchSingleTargetAsync(
        DeviceItem item,
        int index,
        SemaphoreSlim gate,
        IAdbClient adbClient,
        Func<DeviceItem, ResolvedDeviceConfig, string> displayTitleSelector,
        Func<string, string, List<string>, CancellationToken, ScrcpyLaunchResult> launchFn,
        string[] readyLines,
        string[] aliveLines,
        string[] failureLines,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;

            ResolvedDeviceConfig resolved = ConfigManager.Resolve(config, item);
            string display = displayTitleSelector != null
                ? displayTitleSelector(item, resolved)
                : (!string.IsNullOrWhiteSpace(resolved.Alias) ? resolved.Alias : item.PrimaryEndpointDisplay);

            var conn = await adbClient.EnsureConnectedAsync(item, cancellationToken).ConfigureAwait(false);
            if (!conn.ok)
            {
                failureLines[index] = display + ": " + conn.error;
                return;
            }

            string badge = item.Channel == ChannelKind.Usb ? item.BadgeText : "无线已连";
            string title = display + " [" + badge + "]";
            ScrcpyLaunchResult res = await Task.Run(delegate
            {
                return launchFn(conn.activeSerial, title, resolved.Args, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            if (res.Outcome == ScrcpyLaunchOutcome.StreamReady)
            {
                readyLines[index] = display + " (" + conn.activeSerial + ")";
            }
            else if (res.Outcome == ScrcpyLaunchOutcome.ProcessAlive)
            {
                string aliveDetail = display + " (" + conn.activeSerial + "): " + res.ErrorMessage;
                if (!string.IsNullOrWhiteSpace(res.LogFilePath))
                {
                    aliveDetail += "\n  日志: " + res.LogFilePath;
                }
                aliveLines[index] = aliveDetail;
            }
            else
            {
                failureLines[index] = display + ": " + res.ErrorMessage;
            }
        }
        catch (OperationCanceledException)
        {
            failureLines[index] = "已取消启动";
        }
        catch (Exception ex)
        {
            failureLines[index] = "设备启动异常: " + ex.Message;
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    static List<string> CollectNonEmpty(string[] items)
    {
        List<string> list = new List<string>();
        for (int i = 0; i < items.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(items[i])) list.Add(items[i]);
        }
        return list;
    }

    static string CreateLogPath(string serial)
    {
        StringBuilder safe = new StringBuilder();
        string raw = serial ?? "device";
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        string name = string.Format("scrcpy-{0:yyyyMMdd-HHmmss-fff}-{1}.log", DateTime.Now, safe.ToString());
        return Path.Combine(LogDirectory, name);
    }
}
