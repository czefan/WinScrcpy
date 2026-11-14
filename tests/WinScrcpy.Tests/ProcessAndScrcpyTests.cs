using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WinScrcpy.Tests;

public sealed class ProcessAndScrcpyTests
{
    static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);
    const string PsUtf8OutputSetup = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\r\n";

    sealed class PartialThrowingStream : Stream
    {
        readonly byte[] prefix;
        int pos;
        bool thrown;

        public PartialThrowingStream(byte[] prefix)
        {
            this.prefix = prefix ?? new byte[0];
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos < prefix.Length)
            {
                int copy = Math.Min(count, prefix.Length - pos);
                Buffer.BlockCopy(prefix, pos, buffer, offset, copy);
                pos += copy;
                return copy;
            }
            if (!thrown)
            {
                thrown = true;
                throw new IOException("simulated stream failure");
            }
            return 0;
        }
    }

    [Fact]
    public void ToolPathResolver_EnforcesExplicitAndPrecedenceRules()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 测试路径 " + Guid.NewGuid().ToString("N"));
        string appDir = Path.Combine(root, "应用 目录");
        string explicitDir = Path.Combine(root, "指定 工具");
        string pathDir = Path.Combine(root, "环境变量 目录");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(explicitDir);
        Directory.CreateDirectory(pathDir);

        try
        {
            string appAdb = Path.Combine(appDir, "adb.exe");
            string explicitScrcpy = Path.Combine(explicitDir, "my scrcpy.exe");
            string pathAdb = Path.Combine(pathDir, "adb.exe");
            string pathScrcpy = Path.Combine(pathDir, "scrcpy.exe");
            File.WriteAllText(appAdb, "");
            File.WriteAllText(explicitScrcpy, "");
            File.WriteAllText(pathAdb, "");
            File.WriteAllText(pathScrcpy, "");

            string fakePath = "\"" + pathDir + "\";C:\\不存在_WinScrcpy_Test";

            ToolResolutionResult r1 = ToolPathResolver.ResolveTool(explicitScrcpy, "scrcpy", appDir, fakePath);
            Assert.True(r1.Success && string.Equals(r1.ResolvedPath, Path.GetFullPath(explicitScrcpy), StringComparison.OrdinalIgnoreCase));

            string missingExplicit = Path.Combine(explicitDir, "missing-adb.exe");
            ToolResolutionResult r2 = ToolPathResolver.ResolveTool(missingExplicit, "adb", appDir, fakePath);
            Assert.True(!r2.Success && string.IsNullOrEmpty(r2.ResolvedPath) && r2.Error.Contains("missing-adb.exe"));

            ToolResolutionResult r3 = ToolPathResolver.ResolveTool("adb", "adb", appDir, fakePath);
            Assert.True(r3.Success && string.Equals(r3.ResolvedPath, Path.GetFullPath(appAdb), StringComparison.OrdinalIgnoreCase));

            ToolResolutionResult r4 = ToolPathResolver.ResolveTool("", "scrcpy", appDir, fakePath);
            Assert.True(r4.Success && string.Equals(r4.ResolvedPath, Path.GetFullPath(pathScrcpy), StringComparison.OrdinalIgnoreCase));

            ToolResolutionResult r5 = ToolPathResolver.ResolveTool("", "nonexistent-tool-xyz", appDir, fakePath);
            Assert.True(!r5.Success && !string.IsNullOrEmpty(r5.Error));
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    [Fact]
    public void ProcessRunner_HandlesArgsExitCodesBinaryTimeoutCancelAndMissing()
    {
        string psExe = ToolPathResolver.FindExecutableInPath("powershell");
        Assert.False(string.IsNullOrEmpty(psExe));

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 进程测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "参数 与 状态.ps1");
        File.WriteAllText(scriptPath,
            "param([string]$mode)\r\n" +
            PsUtf8OutputSetup +
            "$rest = @($args)\r\n" +
            "if ($mode -eq 'echo') {\r\n" +
            "  [Console]::Out.WriteLine('COUNT=' + $rest.Count)\r\n" +
            "  for ($i = 0; $i -lt $rest.Count; $i++) { [Console]::Out.WriteLine('ARG=' + $rest[$i]) }\r\n" +
            "  [Console]::Error.WriteLine('STDERR_中文')\r\n" +
            "  exit 0\r\n" +
            "}\r\n" +
            "if ($mode -eq 'fail') {\r\n" +
            "  [Console]::Error.WriteLine('boom_error')\r\n" +
            "  exit 42\r\n" +
            "}\r\n" +
            "if ($mode -eq 'binary') {\r\n" +
            "  $bytes = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF)\r\n" +
            "  $stream = [Console]::OpenStandardOutput()\r\n" +
            "  $stream.Write($bytes, 0, $bytes.Length)\r\n" +
            "  $stream.Flush()\r\n" +
            "  exit 0\r\n" +
            "}\r\n" +
            "if ($mode -eq 'sleep') {\r\n" +
            "  Start-Sleep -Seconds 30\r\n" +
            "  exit 0\r\n" +
            "}\r\n",
            Utf8Bom);

        try
        {
            CommandOutput echoOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "echo", "hello world", "中文 参数" },
                10000);
            Assert.True(echoOut.Success && echoOut.ExitCode == 0, echoOut.GetFailureSummary("failed"));
            Assert.True(echoOut.StdOut.Contains("COUNT=2") && echoOut.StdOut.Contains("ARG=hello world") && echoOut.StdOut.Contains("ARG=中文 参数"));
            Assert.Contains("STDERR_中文", echoOut.StdErr);

            CommandOutput failOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "fail" },
                10000);
            Assert.True(!failOut.Success && !failOut.TimedOut && !failOut.Cancelled && failOut.ExitCode == 42);
            Assert.Contains("boom_error", failOut.StdErr);

            CommandOutput binOut = ProcessRunner.RunBinary(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "binary" },
                10000);
            Assert.True(binOut.Success && binOut.StdOutBytes != null && binOut.StdOutBytes.Length == 6 &&
                   binOut.StdOutBytes[0] == 0x89 && binOut.StdOutBytes[4] == 0x00 && binOut.StdOutBytes[5] == 0xFF);

            Stopwatch swTimeout = Stopwatch.StartNew();
            CommandOutput timeoutOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "sleep" },
                250);
            swTimeout.Stop();
            Assert.True(!timeoutOut.Success && timeoutOut.TimedOut && !timeoutOut.Cancelled && swTimeout.ElapsedMilliseconds < 5000);

            using (CancellationTokenSource cts = new CancellationTokenSource(200))
            {
                Stopwatch swCancel = Stopwatch.StartNew();
                CommandOutput cancelOut = ProcessRunner.RunText(
                    psExe,
                    new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "sleep" },
                    15000,
                    null,
                    null,
                    cts.Token);
                swCancel.Stop();
                Assert.True(!cancelOut.Success && cancelOut.Cancelled && !cancelOut.TimedOut && swCancel.ElapsedMilliseconds < 5000);
            }

            string missingExe = Path.Combine(root, "不存在的工具.exe");
            CommandOutput missingOut = ProcessRunner.RunText(missingExe, new string[] { "--version" }, 2000);
            Assert.True(!missingOut.Success && !string.IsNullOrEmpty(missingOut.LaunchError));
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    [Fact]
    public void ProcessRunner_ReportsIncompleteDrainWhenChildHoldsPipe()
    {
        string psExe = ToolPathResolver.FindExecutableInPath("powershell.exe");
        Assert.False(string.IsNullOrEmpty(psExe));

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 管道测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "hold_pipe.ps1");
        string pidTextFile = Path.Combine(root, "pid_text.txt");
        string pidBinFile = Path.Combine(root, "pid_bin.txt");

        File.WriteAllText(scriptPath,
            "param([string]$mode, [string]$pidFile)\r\n" +
            PsUtf8OutputSetup +
            "if ($mode -eq 'text') {\r\n" +
            "  [Console]::Out.Write('NO_NEWLINE_中文输出')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "}\r\n" +
            "elseif ($mode -eq 'binary') {\r\n" +
            "  $bytes = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF)\r\n" +
            "  $s = [Console]::OpenStandardOutput()\r\n" +
            "  $s.Write($bytes, 0, $bytes.Length)\r\n" +
            "  $s.Flush()\r\n" +
            "}\r\n" +
            "$psi = [System.Diagnostics.ProcessStartInfo]::new($PSHOME + '\\powershell.exe', '-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"')\r\n" +
            "$psi.UseShellExecute = $false\r\n" +
            "$psi.CreateNoWindow = $true\r\n" +
            "$child = [System.Diagnostics.Process]::Start($psi)\r\n" +
            "[System.IO.File]::WriteAllText($pidFile, $child.Id.ToString())\r\n" +
            "exit 0\r\n",
            Utf8Bom);

        try
        {
            Stopwatch swText = Stopwatch.StartNew();
            CommandOutput textOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "text", pidTextFile },
                10000, null, null, CancellationToken.None, 300);
            swText.Stop();
            ProcessTestHelpers.KillProcessByPidFile(pidTextFile);

            Assert.True(swText.ElapsedMilliseconds < 5000);
            Assert.True(textOut.ExitCode == 0 && !textOut.TimedOut && !textOut.Cancelled);
            Assert.True(textOut.OutputTimedOut && !textOut.Success && textOut.HasExecutionFailure);
            Assert.Contains("超时", textOut.GetFailureSummary("fail"));
            Assert.Contains("NO_NEWLINE_中文输出", textOut.StdOut);

            Stopwatch swBin = Stopwatch.StartNew();
            CommandOutput binOut = ProcessRunner.RunBinary(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "binary", pidBinFile },
                10000, null, null, CancellationToken.None, 300);
            swBin.Stop();
            ProcessTestHelpers.KillProcessByPidFile(pidBinFile);

            Assert.True(swBin.ElapsedMilliseconds < 5000);
            Assert.True(binOut.ExitCode == 0 && !binOut.TimedOut && !binOut.Cancelled);
            Assert.True(binOut.OutputTimedOut && !binOut.Success && binOut.HasExecutionFailure);
            Assert.True(binOut.StdOutBytes != null && binOut.StdOutBytes.Length == 6 &&
                   binOut.StdOutBytes[0] == 0x89 && binOut.StdOutBytes[4] == 0x00 && binOut.StdOutBytes[5] == 0xFF);
        }
        finally
        {
            ProcessTestHelpers.KillProcessByPidFile(pidTextFile);
            ProcessTestHelpers.KillProcessByPidFile(pidBinFile);
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    [Fact]
    public void ProcessRunner_CapturesStreamReadExceptions()
    {
        CommandOutput textErrResult = new CommandOutput { ExitCode = 0 };
        using (PartialThrowingStream stdout = new PartialThrowingStream(Encoding.UTF8.GetBytes("部分文本")))
        using (MemoryStream stderr = new MemoryStream())
        {
            ProcessRunner.CaptureStreamsForTest(stdout, stderr, false, 1000, true, textErrResult);
        }
        Assert.True(!textErrResult.Success && textErrResult.HasExecutionFailure &&
               !string.IsNullOrWhiteSpace(textErrResult.OutputError) &&
               textErrResult.OutputError.Contains("simulated stream failure"));
        Assert.Equal("部分文本", textErrResult.StdOut);

        CommandOutput binErrResult = new CommandOutput { ExitCode = 0 };
        using (PartialThrowingStream stdout = new PartialThrowingStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        using (MemoryStream stderr = new MemoryStream())
        {
            ProcessRunner.CaptureStreamsForTest(stdout, stderr, true, 1000, true, binErrResult);
        }
        Assert.True(!binErrResult.Success && binErrResult.HasExecutionFailure &&
               !string.IsNullOrWhiteSpace(binErrResult.OutputError) &&
               binErrResult.OutputError.Contains("simulated stream failure"));
        Assert.True(binErrResult.StdOutBytes != null && binErrResult.StdOutBytes.Length == 4 &&
               binErrResult.StdOutBytes[0] == 0x89 && binErrResult.StdOutBytes[3] == 0x47);
    }

    [Fact]
    public void ScrcpyAndTerminal_RejectInvalidExplicitToolPaths()
    {
        ProcessStartInfo psi = ScrcpyLauncher.BuildStartInfo(
            "C:\\工具 空格\\scrcpy.exe",
            "D:\\安卓 工具\\adb.exe",
            "192.168.1.5:5555",
            "我的手机 [无线]",
            new List<string> { "--max-size", "1280" });
        Assert.Equal("C:\\工具 空格\\scrcpy.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("D:\\安卓 工具\\adb.exe", psi.Environment["ADB"]);
        Assert.Equal("0", psi.Environment["ADB_MDNS_AUTO_CONNECT"]);
        Assert.True(psi.ArgumentList.Count == 5 &&
               psi.ArgumentList[0] == "-s" &&
               psi.ArgumentList[1] == "192.168.1.5:5555" &&
               psi.ArgumentList[2] == "--window-title=我的手机 [无线]" &&
               psi.ArgumentList[3] == "--max-size" &&
               psi.ArgumentList[4] == "1280");

        AppConfig invalidCfg = new AppConfig
        {
            adbPath = "C:\\不存在_WinScrcpy\\adb.exe",
            scrcpyPath = "C:\\不存在_WinScrcpy\\scrcpy.exe"
        };
        AdbClient adb = new AdbClient(invalidCfg);
        ScrcpyLauncher scrcpy = new ScrcpyLauncher(invalidCfg);
        TerminalLauncher terminal = new TerminalLauncher(adb);

        CommandOutput adbOut = adb.RunAdb(new string[] { "version" }, 2000);
        Assert.True(!adbOut.Success && adbOut.LaunchError.Contains("adb") && adbOut.LaunchError.Contains("不存在"));

        ScrcpyLaunchResult launchRes = scrcpy.Launch("serial1", "测试设备", new List<string>());
        Assert.True(launchRes.IsFailure && !launchRes.ProcessStarted && launchRes.ErrorMessage.Contains("scrcpy") && launchRes.ErrorMessage.Contains("不存在"));

        string launchedWith;
        string err;
        Assert.True(!terminal.OpenDeviceTerminal("serial1", "测试设备", TerminalMode.LocalAdbEnv, out launchedWith, out err)
            && err.Contains("adb") && err.Contains("不存在"));
    }

    [Fact]
    public async Task ScrcpyLauncher_BatchLaunchSeparatesOutcomesAndCapsConcurrency()
    {
        AppConfig cfg = AppConfig.CreateDefault();
        FakeAdbClient fakeAdb = new FakeAdbClient
        {
            EnsureConnectedHandler = (item, ct) =>
            {
                if (item.AdbSerial == "FAIL_CONN") return Task.FromResult((false, "", "连接超时"));
                return Task.FromResult((true, item.AdbSerial, ""));
            }
        };

        List<DeviceItem> targets = new List<DeviceItem>();
        for (int i = 1; i <= 5; i++)
        {
            targets.Add(new DeviceItem
            {
                Channel = ChannelKind.Usb,
                AdbSerial = i == 5 ? "FAIL_CONN" : ("DEV_" + i),
                Model = "Phone_" + i
            });
        }

        int active = 0;
        int maxActive = 0;
        ScrcpyLauncher launcher = new ScrcpyLauncher(cfg);
        BatchLaunchResult batch = await launcher.LaunchBatchAsync(
            targets,
            fakeAdb,
            (item, resolved) => item.Model,
            CancellationToken.None,
            (serial, title, args, ct) =>
            {
                int cur = Interlocked.Increment(ref active);
                while (true)
                {
                    int prev = Volatile.Read(ref maxActive);
                    if (cur <= prev || Interlocked.CompareExchange(ref maxActive, cur, prev) == prev) break;
                }
                try
                {
                    Thread.Sleep(35);
                    if (serial == "DEV_1") return new ScrcpyLaunchResult { Outcome = ScrcpyLaunchOutcome.StreamReady, ProcessStarted = true };
                    if (serial == "DEV_2") return new ScrcpyLaunchResult { Outcome = ScrcpyLaunchOutcome.ProcessAlive, ProcessStarted = true, ErrorMessage = "无法从日志确认就绪" };
                    if (serial == "DEV_3") return new ScrcpyLaunchResult { Outcome = ScrcpyLaunchOutcome.Failed, ErrorMessage = "未知参数" };
                    return new ScrcpyLaunchResult { Outcome = ScrcpyLaunchOutcome.StreamReady, ProcessStarted = true };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        Assert.True(maxActive <= 3 && maxActive >= 2, "batch concurrency must stay capped <= 3, got=" + maxActive);
        Assert.Equal(2, batch.ReadyLines.Count);
        Assert.Single(batch.AliveLines);
        Assert.Equal(2, batch.FailureLines.Count);
        Assert.True(batch.HasFailures && batch.HasAnyStarted);

        string detail = batch.BuildDetail();
        Assert.Contains("【已确认投屏就绪】", detail);
        Assert.Contains("【已启动（未确认画面就绪）】", detail);
        Assert.Contains("【启动失败】", detail);
        Assert.Contains("投屏就绪 2 台，已启动(未确认就绪) 1 台，失败 2 台", batch.BuildSummary());
    }

    [Fact]
    public void ScrcpyLogLifecycleAndLaunchJudgment_VerifiesDetachedProcessSemantics()
    {
        Assert.True(string.IsNullOrEmpty(ScrcpyLauncher.DetectUnverifiableReadyReason(new string[] { "--stay-awake" })));
        Assert.False(string.IsNullOrEmpty(ScrcpyLauncher.DetectUnverifiableReadyReason(new string[] { "--stay-awake", "--verbosity=warn" })));
        Assert.False(string.IsNullOrEmpty(ScrcpyLauncher.DetectUnverifiableReadyReason(new string[] { "--no-video", "-V", "ERROR" })));

        Assert.True(ScrcpyLauncher.IsReadyLogLine("INFO: Texture: 1080x2400"));
        Assert.True(ScrcpyLauncher.IsReadyLogLine("INFO: Initial texture: 1080x2400"));
        Assert.False(ScrcpyLauncher.IsReadyLogLine("INFO: [server] INFO: Device: Pixel"));
        Assert.Contains("Could not find any ADB device",
            ScrcpyLauncher.ExtractDiagnostic("INFO: connecting\nERROR: Could not find any ADB device\nERROR: Server connection failed\n", 4));

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 启动监控 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string psExe = ToolPathResolver.FindExecutableInPath("powershell.exe");
        Assert.False(string.IsNullOrEmpty(psExe));

        string preCancelLog = Path.Combine(root, "precancel.log");
        string preCancelMarker = Path.Combine(root, "precancel_ran.txt");
        string scriptPath = Path.Combine(root, "mock_scrcpy.ps1");

        File.WriteAllText(scriptPath,
            "param([string]$mode, [string]$argA, [string]$argB)\r\n" +
            PsUtf8OutputSetup +
            "if ($mode -eq 'touch_and_sleep') {\r\n" +
            "  [System.IO.File]::WriteAllText($argA, 'RAN')\r\n" +
            "  Start-Sleep -Seconds 20\r\n" +
            "  exit 0\r\n" +
            "}\r\n" +
            "if ($mode -eq 'args') {\r\n" +
            "  [Console]::Out.WriteLine('ARG_A=' + $argA)\r\n" +
            "  [Console]::Out.WriteLine('ARG_B=' + $argB)\r\n" +
            "  [Console]::Out.WriteLine('ENV_ADB=' + $env:ADB)\r\n" +
            "  [Console]::Out.WriteLine('INFO: Texture: 1200x2000')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  Start-Sleep -Seconds 20\r\n" +
            "  exit 0\r\n" +
            "}\r\n" +
            "if ($mode -eq 'immediate_fail') {\r\n" +
            "  [Console]::Error.WriteLine('unknown option -- invalid-flag-test')\r\n" +
            "  [Console]::Error.Flush()\r\n" +
            "  exit 1\r\n" +
            "}\r\n" +
            "if ($mode -eq 'delayed_fail') {\r\n" +
            "  [Console]::Out.WriteLine('INFO: adb reverse ...')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  Start-Sleep -Milliseconds 600\r\n" +
            "  [Console]::Error.WriteLine('ERROR: Device disconnected during startup')\r\n" +
            "  [Console]::Error.Flush()\r\n" +
            "  exit 1\r\n" +
            "}\r\n" +
            "if ($mode -eq 'ready_and_detach') {\r\n" +
            "  [Console]::Out.WriteLine('INFO: connecting')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  Start-Sleep -Milliseconds 250\r\n" +
            "  [Console]::Out.WriteLine('INFO: Texture: 1080x2400')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  Start-Sleep -Milliseconds 400\r\n" +
            "  [Console]::Out.WriteLine('POST_DETACH_WRITE_OK')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  [System.IO.File]::WriteAllText($argA, 'ALIVE_AFTER_DETACH')\r\n" +
            "  Start-Sleep -Seconds 20\r\n" +
            "  exit 0\r\n" +
            "}\r\n" +
            "if ($mode -eq 'alive_no_ready') {\r\n" +
            "  [Console]::Out.WriteLine('INFO: waiting silently...')\r\n" +
            "  [Console]::Out.Flush()\r\n" +
            "  Start-Sleep -Seconds 20\r\n" +
            "  exit 0\r\n" +
            "}\r\n",
            Utf8Bom);

        int readyPid = 0;
        int detachPid = 0;
        int alivePid = 0;
        int postCancelPid = 0;

        try
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            env["ADB"] = "D:\\工具 目录\\adb.exe";

            ProcessStartInfo psiPreCancel = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "touch_and_sleep", preCancelMarker },
                env, root, false, false);
            DetachedMonitorResult rPreCancel = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiPreCancel, preCancelLog, 5000, ScrcpyLauncher.IsReadyLogLine, new CancellationToken(true));
            Assert.True(rPreCancel.Status == DetachedMonitorStatus.Cancelled
                && !rPreCancel.ProcessStarted
                && rPreCancel.ProcessId == 0
                && !File.Exists(preCancelLog)
                && !File.Exists(preCancelMarker));

            ScrcpyLauncher launcher = new ScrcpyLauncher(AppConfig.CreateDefault());
            ScrcpyLaunchResult sPreCancel = launcher.Launch("serial", "title", new List<string>(), 5000, new CancellationToken(true));
            Assert.True(sPreCancel.IsFailure && sPreCancel.Cancelled && !sPreCancel.ProcessStarted && sPreCancel.ProcessId == 0);

            string postCancelLog = Path.Combine(root, "postcancel.log");
            string postCancelMarker = Path.Combine(root, "postcancel_ran.txt");
            ProcessStartInfo psiPostCancel = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "touch_and_sleep", postCancelMarker },
                env, root, false, false);
            using (CancellationTokenSource postCts = new CancellationTokenSource(350))
            {
                DetachedMonitorResult rPostCancel = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                    psiPostCancel, postCancelLog, 10000, ScrcpyLauncher.IsReadyLogLine, postCts.Token);
                postCancelPid = rPostCancel.ProcessId;
                Assert.True(rPostCancel.Status == DetachedMonitorStatus.Cancelled
                    && rPostCancel.ProcessStarted
                    && rPostCancel.ProcessId > 0);
                using (Process p = Process.GetProcessById(postCancelPid))
                {
                    Assert.False(p.HasExited);
                }
            }
            ProcessTestHelpers.KillProcessSafe(postCancelPid);
            postCancelPid = 0;

            string immediateLog = Path.Combine(root, "immediate.log");
            ProcessStartInfo psiImmediate = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "immediate_fail" },
                env, root, false, false);
            DetachedMonitorResult rImmediate = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiImmediate, immediateLog, 5000, ScrcpyLauncher.IsReadyLogLine);
            Assert.True(rImmediate.ProcessStarted && rImmediate.Status == DetachedMonitorStatus.Exited && rImmediate.ExitCode == 1);
            Assert.True(rImmediate.CapturedLog.Contains("unknown option -- invalid-flag-test") && File.Exists(immediateLog));

            string delayedLog = Path.Combine(root, "delayed.log");
            ProcessStartInfo psiDelayed = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "delayed_fail" },
                env, root, false, false);
            DetachedMonitorResult rDelayed = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiDelayed, delayedLog, 5000, ScrcpyLauncher.IsReadyLogLine);
            Assert.True(rDelayed.ProcessStarted && rDelayed.Status == DetachedMonitorStatus.Exited && rDelayed.ExitCode == 1);
            Assert.Contains("ERROR: Device disconnected during startup", rDelayed.CapturedLog);

            string argsLog = Path.Combine(root, "args.log");
            ProcessStartInfo psiArgs = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "args", "中文 空格", "trail\\slash\"quote" },
                env, root, false, false);
            DetachedMonitorResult rArgs = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiArgs, argsLog, 8000, ScrcpyLauncher.IsReadyLogLine);
            readyPid = rArgs.ProcessId;
            Assert.True(rArgs.ProcessStarted && rArgs.Status == DetachedMonitorStatus.MatchedReady);
            Assert.True(rArgs.CapturedLog.Contains("ARG_A=中文 空格")
                && rArgs.CapturedLog.Contains("ARG_B=trail\\slash\"quote")
                && rArgs.CapturedLog.Contains("ENV_ADB=D:\\工具 目录\\adb.exe"));
            ProcessTestHelpers.KillProcessSafe(readyPid);
            readyPid = 0;

            string oldUnlocked1 = Path.Combine(root, "scrcpy-00000001-old1.log");
            string oldUnlocked2 = Path.Combine(root, "scrcpy-00000002-old2.log");
            string oldUnlocked3 = Path.Combine(root, "scrcpy-00000003-old3.log");
            File.WriteAllText(oldUnlocked1, "old1");
            File.WriteAllText(oldUnlocked2, "old2");
            File.WriteAllText(oldUnlocked3, "old3");
            string activeLockedLog = Path.Combine(root, "scrcpy-00000000-active.log");
            string detachMarker = Path.Combine(root, "detached_ok.txt");

            ProcessStartInfo psiDetach = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "ready_and_detach", detachMarker },
                env, root, false, false);
            Stopwatch swReady = Stopwatch.StartNew();
            DetachedMonitorResult rDetach = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiDetach, activeLockedLog, 10000, ScrcpyLauncher.IsReadyLogLine);
            swReady.Stop();
            detachPid = rDetach.ProcessId;
            Assert.True(rDetach.Status == DetachedMonitorStatus.MatchedReady && swReady.ElapsedMilliseconds < 5000);

            DetachedProcessLauncher.PruneOldLogs(root, "scrcpy-*.log", 1);
            Assert.True(File.Exists(activeLockedLog), "PruneOldLogs MUST NOT delete log held by running child process");
            Assert.True(!File.Exists(oldUnlocked1) && !File.Exists(oldUnlocked2) && File.Exists(oldUnlocked3));

            Stopwatch swPost = Stopwatch.StartNew();
            while (!File.Exists(detachMarker) && swPost.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(50);
            }
            Assert.True(File.Exists(detachMarker), "child must keep running after monitor detaches");
            string finalLogText;
            using (FileStream fsRead = new FileStream(activeLockedLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader srRead = new StreamReader(fsRead, Encoding.UTF8))
            {
                finalLogText = srRead.ReadToEnd();
            }
            Assert.Contains("POST_DETACH_WRITE_OK", finalLogText);
            ProcessTestHelpers.KillProcessSafe(detachPid);
            detachPid = 0;

            string aliveLog = Path.Combine(root, "alive.log");
            ProcessStartInfo psiAlive = ProcessRunner.BuildStartInfo(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "alive_no_ready" },
                env, root, false, false);
            DetachedMonitorResult rAlive = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                psiAlive, aliveLog, 450, ScrcpyLauncher.IsReadyLogLine);
            alivePid = rAlive.ProcessId;
            Assert.True(rAlive.ProcessStarted && rAlive.Status == DetachedMonitorStatus.TimedOutAlive);
            ProcessTestHelpers.KillProcessSafe(alivePid);
            alivePid = 0;
        }
        finally
        {
            ProcessTestHelpers.KillProcessSafe(postCancelPid);
            ProcessTestHelpers.KillProcessSafe(readyPid);
            ProcessTestHelpers.KillProcessSafe(detachPid);
            ProcessTestHelpers.KillProcessSafe(alivePid);
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task DetachedProcessLauncher_DoesNotInheritUnrelatedInheritablePipe()
    {
        string psExe = ToolPathResolver.FindExecutableInPath("powershell");
        Assert.False(string.IsNullOrEmpty(psExe));

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 管道隔离 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "child.ps1");
        File.WriteAllText(scriptPath,
            PsUtf8OutputSetup +
            "[Console]::Out.WriteLine('INFO: Texture: 1080x2400')\r\n" +
            "[Console]::Out.Flush()\r\n" +
            "Start-Sleep -Seconds 5\r\n" +
            "exit 0\r\n",
            Utf8Bom);

        int childPid = 0;
        try
        {
            string pipeLog = Path.Combine(root, "pipe_leak_check.log");
            using (AnonymousPipeServerStream pipeRead = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
            {
                ProcessStartInfo psi = ProcessRunner.BuildStartInfo(
                    psExe,
                    new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
                    null, root, false, false);
                DetachedMonitorResult r = DetachedProcessLauncher.LaunchWithLogAndMonitor(
                    psi, pipeLog, 8000, ScrcpyLauncher.IsReadyLogLine);
                childPid = r.ProcessId;
                Assert.True(r.ProcessStarted && r.Status == DetachedMonitorStatus.MatchedReady && childPid > 0);
                using (Process p = Process.GetProcessById(childPid))
                {
                    Assert.False(p.HasExited);
                }

                pipeRead.DisposeLocalCopyOfClientHandle();
                byte[] probeBuf = new byte[1];
                Task<int> readTask = pipeRead.ReadAsync(probeBuf, 0, 1);
                Task completed = await Task.WhenAny(readTask, Task.Delay(1000));
                Assert.Same(readTask, completed);
                Assert.Equal(0, await readTask);
                using (Process pAfter = Process.GetProcessById(childPid))
                {
                    Assert.False(pAfter.HasExited);
                }
            }
        }
        finally
        {
            ProcessTestHelpers.KillProcessSafe(childPid);
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    [Fact]
    public async Task AdbClient_ConnectEndpointAsync_OnlyDisconnectsWhenOffline()
    {
        List<string> onlineCommands = new List<string>();
        AdbClient onlineClient = new AdbClient(AppConfig.CreateDefault());
        onlineClient.RunAdbOverrideForTest = (args, timeoutMs, ct) =>
        {
            string cmd = string.Join(" ", args);
            onlineCommands.Add(cmd);
            if (args.Count >= 1 && args[0] == "connect")
            {
                return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "already connected to 127.0.0.1:5555\n" });
            }
            if (args.Count >= 1 && args[0] == "devices")
            {
                return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "List of devices attached\n127.0.0.1:5555\tdevice product:p1 model:M1 transport_id:1\n" });
            }
            return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "" });
        };

        (bool okOnline, string errOnline) = await onlineClient.ConnectEndpointAsync("127.0.0.1:5555");
        Assert.True(okOnline && string.IsNullOrEmpty(errOnline));
        Assert.Equal(new string[] { "connect 127.0.0.1:5555", "devices -l" }, onlineCommands.ToArray());

        List<string> offlineCommands = new List<string>();
        int connectAttempt = 0;
        AdbClient offlineClient = new AdbClient(AppConfig.CreateDefault());
        offlineClient.RunAdbOverrideForTest = (args, timeoutMs, ct) =>
        {
            string cmd = string.Join(" ", args);
            offlineCommands.Add(cmd);
            if (args.Count >= 1 && args[0] == "connect")
            {
                connectAttempt++;
                return Task.FromResult(new CommandOutput
                {
                    ExitCode = 0,
                    StdOut = connectAttempt == 1 ? "already connected to 127.0.0.1:5555\n" : "connected to 127.0.0.1:5555\n"
                });
            }
            if (args.Count >= 1 && args[0] == "devices")
            {
                return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "List of devices attached\n127.0.0.1:5555\toffline\n" });
            }
            if (args.Count >= 1 && args[0] == "disconnect")
            {
                return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "disconnected 127.0.0.1:5555\n" });
            }
            return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "" });
        };

        (bool okOffline, string errOffline) = await offlineClient.ConnectEndpointAsync("127.0.0.1:5555");
        Assert.True(okOffline && string.IsNullOrEmpty(errOffline));
        Assert.Equal(
            new string[] { "connect 127.0.0.1:5555", "devices -l", "disconnect 127.0.0.1:5555", "connect 127.0.0.1:5555" },
            offlineCommands.ToArray());
    }
}
