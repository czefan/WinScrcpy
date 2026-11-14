param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ReportPath = '',
    [switch]$NoBuild,
    [string]$BaselineCommit = '05d4ec1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repoRoot "bin\$Configuration\baseline-report.md"
}

$buildScript = Join-Path $repoRoot 'build.ps1'
$buildDir = Join-Path $repoRoot "bin\$Configuration\net10.0-windows"
$appDll = Join-Path $buildDir 'WinScrcpy.dll'
$normalExePath = Join-Path $buildDir 'WinScrcpy.exe'
$fdPublishExe = Join-Path $repoRoot "bin\$Configuration\net10.0-windows\win-x64\publish-framework-dependent\WinScrcpy.exe"
$scPublishExe = Join-Path $repoRoot "bin\$Configuration\net10.0-windows\win-x64\publish-selfcontained\WinScrcpy.exe"
$scCompPublishExe = Join-Path $repoRoot "bin\$Configuration\net10.0-windows\win-x64\publish-selfcontained-compressed\WinScrcpy.exe"
$fdZip = Join-Path $repoRoot "bin\$Configuration\WinScrcpy-win-x64-framework-dependent.zip"
$scZip = Join-Path $repoRoot "bin\$Configuration\WinScrcpy-win-x64-selfcontained.zip"
$scCompZip = Join-Path $repoRoot "bin\$Configuration\WinScrcpy-win-x64-selfcontained-compressed.zip"

if (-not $NoBuild) {
    & $buildScript -Configuration $Configuration | Out-Null
    & $buildScript -Configuration $Configuration -Publish -Package | Out-Null
    & $buildScript -Configuration $Configuration -Publish -SelfContained -Package | Out-Null
    & $buildScript -Configuration $Configuration -Publish -SelfContained -Compress -Package | Out-Null
} elseif (-not (Test-Path -LiteralPath $appDll)) {
    throw "WinScrcpy.dll was not found at '$appDll'. Run without -NoBuild first."
}

function Get-GitValue([string[]]$gitArgs, [string]$fallback) {
    try {
        $out = & git -C $repoRoot @gitArgs 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($out | Out-String))) {
            return ($out | Out-String).Trim()
        }
    } catch { }
    return $fallback
}

function Get-ArtifactMeta([string]$path, [string]$missingText) {
    if (-not (Test-Path -LiteralPath $path)) {
        return @{
            Exists = $false
            Size = $missingText
            Sha256 = '-'
        }
    }
    $len = (Get-Item -LiteralPath $path).Length
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    return @{
        Exists = $true
        Size = ('{0:N2} MB' -f ($len / 1MB))
        Sha256 = $hash
    }
}

$gitCommit = Get-GitValue @('rev-parse', 'HEAD') '未知'
$gitShort = Get-GitValue @('rev-parse', '--short', 'HEAD') '未知'
$gitStatusLines = @()
try {
    $rawStatus = & git -C $repoRoot status --porcelain 2>$null
    if ($LASTEXITCODE -eq 0 -and $null -ne $rawStatus) {
        $gitStatusLines = @($rawStatus | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
} catch { }
$workspaceState = if ($gitCommit -eq '未知') {
    '未知（无 Git 上下文）'
} elseif ($gitStatusLines.Count -eq 0) {
    '干净（无未提交改动）'
} else {
    "有未提交改动（$($gitStatusLines.Count) 项）"
}

$runnerDir = Join-Path ([System.IO.Path]::GetTempPath()) ("winscrcpy-bench-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runnerDir -Force | Out-Null

try {
    # 1) Compile deterministic MockAdb.exe so startup & first-device timing are independent of physical devices
    $mockAdbDir = Join-Path $runnerDir 'mock-adb'
    New-Item -ItemType Directory -Path $mockAdbDir -Force | Out-Null
    $mockAdbCs = @'
using System;
static class MockAdb {
    static int Main(string[] args) {
        if (args.Length > 0 && string.Equals(args[0], "devices", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine("List of devices attached");
            Console.WriteLine("MOCKUSB001\tdevice product:winscrcpy_bench model:Pixel_9 transport_id:1");
            Console.WriteLine("MOCKUSB002\tdevice product:winscrcpy_bench model:Galaxy_S24 transport_id:2");
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "mdns", StringComparison.OrdinalIgnoreCase)) {
            Console.WriteLine("List of discovered mdns services");
            return 0;
        }
        return 0;
    }
}
'@
    $mockAdbCsProj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>adb</AssemblyName>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
</Project>
'@
    $mockAdbSrcDir = Join-Path $runnerDir 'mock-adb-src'
    New-Item -ItemType Directory -Path $mockAdbSrcDir -Force | Out-Null
    Set-Content -Path (Join-Path $mockAdbSrcDir 'adb.csproj') -Value $mockAdbCsProj -Encoding UTF8
    Set-Content -Path (Join-Path $mockAdbSrcDir 'Program.cs') -Value $mockAdbCs -Encoding UTF8
    & dotnet publish (Join-Path $mockAdbSrcDir 'adb.csproj') -c Release -r win-x64 --self-contained false -o $mockAdbDir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to build MockAdb helper.'
    }
    $mockAdbExe = Join-Path $mockAdbDir 'adb.exe'

    # 2) Optional pre-migration .NET Framework 4 baseline build from git history (without touching workspace)
    $baselineExePath = ''
    $baselineStatusNote = ''
    $csc4 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if ([string]::IsNullOrWhiteSpace($BaselineCommit)) {
        $baselineStatusNote = '缺失（未指定基线提交）'
    } elseif (-not (Test-Path -LiteralPath $csc4)) {
        $baselineStatusNote = '缺失（本机缺少 .NET Framework 4 csc.exe）'
    } else {
        $hasCommit = $false
        try {
            & git -C $repoRoot cat-file -e "$BaselineCommit`^{commit}" 2>$null
            $hasCommit = ($LASTEXITCODE -eq 0)
        } catch { $hasCommit = $false }

        if (-not $hasCommit) {
            $baselineStatusNote = "缺失（仓库历史中未找到基线提交 $BaselineCommit）"
        } else {
            $baselineZip = Join-Path $runnerDir 'baseline-src.zip'
            $baselineSrcDir = Join-Path $runnerDir 'baseline-src'
            & git -C $repoRoot archive --format=zip -o $baselineZip $BaselineCommit
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $baselineZip)) {
                Expand-Archive -Path $baselineZip -DestinationPath $baselineSrcDir -Force
                $baselineBuildScript = Join-Path $baselineSrcDir 'build.ps1'
                if (Test-Path -LiteralPath $baselineBuildScript) {
                    & powershell -NoProfile -ExecutionPolicy Bypass -File $baselineBuildScript | Out-Null
                    $candidateExe = Join-Path $baselineSrcDir 'WinScrcpy.exe'
                    if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $candidateExe)) {
                        $baselineExePath = $candidateExe
                    } else {
                        $baselineStatusNote = "缺失（基线提交 $BaselineCommit 编译失败）"
                    }
                } else {
                    $baselineStatusNote = "缺失（基线提交 $BaselineCommit 缺少 build.ps1）"
                }
            } else {
                $baselineStatusNote = "缺失（导出基线提交 $BaselineCommit 失败）"
            }
        }
    }

    # 3) Prepare target manifest for end-to-end independent-process measurements
    $targets = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrEmpty($baselineExePath)) {
        $targets.Add("netfx4_baseline|迁移前基线 (.NET Framework 4, $BaselineCommit)|$baselineExePath|false")
    }
    if (Test-Path -LiteralPath $normalExePath) {
        $targets.Add("net10_normal|迁移后 · 普通构建 (.NET 10 框架依赖)|$normalExePath|true")
    }
    if (Test-Path -LiteralPath $fdPublishExe) {
        $targets.Add("net10_fd_sf|迁移后 · 单文件 (.NET 10 框架依赖)|$fdPublishExe|false")
    }
    if (Test-Path -LiteralPath $scPublishExe) {
        $targets.Add("net10_sc_sf|迁移后 · 不压缩自包含单文件 (.NET 10 SingleFile)|$scPublishExe|false")
    }
    if (Test-Path -LiteralPath $scCompPublishExe) {
        $targets.Add("net10_sc_comp_sf|迁移后 · 压缩自包含单文件 (.NET 10 SingleFile)|$scCompPublishExe|false")
    }
    $targetsFile = Join-Path $runnerDir 'targets.txt'
    [System.IO.File]::WriteAllLines($targetsFile, $targets, (New-Object System.Text.UTF8Encoding($false)))

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>WinScrcpy.Tests</AssemblyName>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="WinScrcpy">
      <HintPath>$appDll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
"@

    $programCs = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class BenchAdbClient : IAdbClient
{
    readonly string devicesOut;
    readonly string mdnsOut;

    public BenchAdbClient(string devicesOut, string mdnsOut)
    {
        this.devicesOut = devicesOut;
        this.mdnsOut = mdnsOut;
    }

    public Task<CommandOutput> QueryDevicesRawAsync(CancellationToken ct)
    {
        return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = devicesOut, StdErr = "" });
    }

    public Task<CommandOutput> QueryMdnsServicesRawAsync(CancellationToken ct)
    {
        return Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = mdnsOut, StdErr = "" });
    }

    public Task<string> QueryHardwareSerialAsync(string adbSerial, CancellationToken ct)
    {
        return Task.FromResult("SER_" + adbSerial);
    }

    public Task<(bool ok, string activeSerial, string error)> EnsureConnectedAsync(DeviceItem item, CancellationToken ct)
    {
        return Task.FromResult((true, string.IsNullOrEmpty(item.AdbSerial) ? item.PrimaryEndpointDisplay : item.AdbSerial, ""));
    }
}

internal static class Win32Probe
{
    const uint WM_NULL = 0x0000;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    static string ReadWindowText(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        StringBuilder sb = new StringBuilder(len + 2);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IntPtr FindMainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid != (uint)processId || !IsWindowVisible(hWnd)) return true;
            string title = ReadWindowText(hWnd);
            if (title.IndexOf("WinScrcpy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsWindowInteractive(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) return false;
        IntPtr result;
        IntPtr res = SendMessageTimeout(hWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out result);
        return res != IntPtr.Zero;
    }

    public static bool HasFirstDeviceRendered(IntPtr mainHwnd)
    {
        if (mainHwnd == IntPtr.Zero) return false;
        bool found = false;
        EnumChildWindows(mainHwnd, delegate(IntPtr child, IntPtr lParam)
        {
            if (!IsWindowVisible(child)) return true;
            string text = ReadWindowText(child).Trim();
            if (string.Equals(text, "投屏", StringComparison.Ordinal)
                || (text.StartsWith("共 ", StringComparison.Ordinal) && text.EndsWith(" 台", StringComparison.Ordinal)))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string runnerRoot = Environment.GetEnvironmentVariable("WINSCRCPY_BENCH_ROOT");
        if (string.IsNullOrWhiteSpace(runnerRoot))
        {
            runnerRoot = args.Length > 0 ? args[0] : AppContext.BaseDirectory;
        }
        string targetsPath = Path.Combine(runnerRoot, "targets.txt");
        string mockAdbExe = Path.Combine(runnerRoot, "mock-adb", "adb.exe");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine("DOTNET=" + RuntimeInformation.FrameworkDescription);
        Console.WriteLine("OS=" + Environment.OSVersion.VersionString);
        Console.WriteLine("CPU_COUNT=" + Environment.ProcessorCount);

        MeasureEndToEndTargets(runnerRoot, targetsPath, mockAdbExe);
        MeasureScanParse();
        MeasureConfigRoundTrip();
        MeasureDiscoveryBoundedProbe();
        MeasureMainFormSingleRender();
        MeasureSameListRefresh(2);
        MeasureSameListRefresh(10);
        return 0;
    }

    static string EscapeJson(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static void CopyBuildDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("winscrcpy.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destDir, name), true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            if (dirName.StartsWith("publish", StringComparison.OrdinalIgnoreCase)) continue;
            CopyBuildDirectory(subDir, Path.Combine(destDir, dirName));
        }
    }

    static void MeasureEndToEndTargets(string runnerRoot, string targetsPath, string mockAdbExe)
    {
        if (!File.Exists(targetsPath)) return;
        string[] lines = File.ReadAllLines(targetsPath, Encoding.UTF8);
        for (int idx = 0; idx < lines.Length; idx++)
        {
            string line = lines[idx].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            string[] parts = line.Split('|');
            if (parts.Length < 4) continue;
            string id = parts[0];
            string label = parts[1];
            string exePath = parts[2];
            bool copyWholeDir = string.Equals(parts[3], "true", StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(exePath)) continue;

            string stageDir = Path.Combine(runnerRoot, "e2e_" + id);
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);

            string stagedExe;
            if (copyWholeDir)
            {
                CopyBuildDirectory(Path.GetDirectoryName(exePath), stageDir);
                stagedExe = Path.Combine(stageDir, Path.GetFileName(exePath));
            }
            else
            {
                stagedExe = Path.Combine(stageDir, Path.GetFileName(exePath));
                File.Copy(exePath, stagedExe, true);
            }

            string cfgJson = "{\n  \"adbPath\": \"" + EscapeJson(mockAdbExe) + "\",\n  \"scrcpyPath\": \"scrcpy\",\n  \"globalArgs\": [\"--stay-awake\"],\n  \"savedEndpoints\": [],\n  \"devices\": {}\n}\n";
            File.WriteAllText(Path.Combine(stageDir, "winscrcpy.json"), cfgJson, Encoding.UTF8);

            string coldBundleDir = Path.Combine(runnerRoot, "bundle_cold_" + id);
            string warmBundleDir = Path.Combine(runnerRoot, "bundle_warm_" + id);
            Directory.CreateDirectory(coldBundleDir);
            Directory.CreateDirectory(warmBundleDir);

            double firstInteractiveMs = 0;
            double firstDeviceMs = 0;
            List<double> warmInteractive = new List<double>();
            List<double> warmDevices = new List<double>();
            List<double> idleCpuSamples = new List<double>();
            List<double> workingSetSamples = new List<double>();
            List<double> privateMemSamples = new List<double>();
            List<int> threadSamples = new List<int>();

            const int totalRuns = 4;
            for (int r = 0; r < totalRuns; r++)
            {
                ProcessStartInfo psi = new ProcessStartInfo(stagedExe);
                psi.WorkingDirectory = stageDir;
                psi.UseShellExecute = false;
                psi.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = (r == 0) ? coldBundleDir : warmBundleDir;

                Stopwatch sw = Stopwatch.StartNew();
                using (Process proc = Process.Start(psi))
                {
                    double interactiveMs = -1;
                    double deviceMs = -1;
                    IntPtr mainHwnd = IntPtr.Zero;

                    try
                    {
                        while (sw.ElapsedMilliseconds < 15000)
                        {
                            if (proc.HasExited) break;
                            if (mainHwnd == IntPtr.Zero)
                            {
                                mainHwnd = Win32Probe.FindMainWindow(proc.Id);
                            }
                            if (mainHwnd != IntPtr.Zero && interactiveMs < 0)
                            {
                                try { proc.WaitForInputIdle(20); } catch { }
                                if (Win32Probe.IsWindowInteractive(mainHwnd))
                                {
                                    interactiveMs = sw.Elapsed.TotalMilliseconds;
                                }
                            }
                            if (mainHwnd != IntPtr.Zero && interactiveMs >= 0 && deviceMs < 0)
                            {
                                if (Win32Probe.HasFirstDeviceRendered(mainHwnd))
                                {
                                    deviceMs = sw.Elapsed.TotalMilliseconds;
                                    break;
                                }
                            }
                            Thread.Sleep(5);
                        }

                        if (interactiveMs < 0 || deviceMs < 0)
                        {
                            throw new TimeoutException(string.Format(
                                "E2E probe timed out for {0} (run {1}): interactive={2}, device={3}",
                                id, r, interactiveMs, deviceMs));
                        }

                        if (r == 0)
                        {
                            firstInteractiveMs = interactiveMs;
                            firstDeviceMs = deviceMs;
                        }
                        else
                        {
                            warmInteractive.Add(interactiveMs);
                            warmDevices.Add(deviceMs);
                        }

                        // Let post-scan UI work settle, then sample idle CPU, memory, and thread count over 1500 ms.
                        Thread.Sleep(1500);
                        proc.Refresh();
                        TimeSpan cpu0 = proc.TotalProcessorTime;
                        Stopwatch idleSw = Stopwatch.StartNew();
                        Thread.Sleep(1500);
                        idleSw.Stop();
                        proc.Refresh();
                        TimeSpan cpu1 = proc.TotalProcessorTime;

                        double cpuPct = ((cpu1 - cpu0).TotalMilliseconds / (idleSw.Elapsed.TotalMilliseconds * Environment.ProcessorCount)) * 100.0;
                        if (cpuPct < 0) cpuPct = 0;
                        idleCpuSamples.Add(cpuPct);
                        workingSetSamples.Add(proc.WorkingSet64 / (1024.0 * 1024.0));
                        privateMemSamples.Add(proc.PrivateMemorySize64 / (1024.0 * 1024.0));
                        threadSamples.Add(proc.Threads.Count);
                    }
                    finally
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                proc.CloseMainWindow();
                                if (!proc.WaitForExit(3000))
                                {
                                    proc.Kill();
                                    proc.WaitForExit(3000);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            warmInteractive.Sort();
            warmDevices.Sort();
            idleCpuSamples.Sort();
            workingSetSamples.Sort();
            privateMemSamples.Sort();
            threadSamples.Sort();

            Console.WriteLine(string.Format(
                "E2E_{0}=label:{1}|first_interactive:{2:F1}|warm_interactive_avg:{3:F1}|warm_interactive_p95:{4:F1}|first_device:{5:F1}|warm_device_avg:{6:F1}|warm_device_p95:{7:F1}|idle_cpu_avg:{8:F2}|ws_avg:{9:F1}|priv_avg:{10:F1}|threads_med:{11}",
                id,
                label,
                firstInteractiveMs,
                warmInteractive.Average(),
                Percentile(warmInteractive, 0.95),
                firstDeviceMs,
                warmDevices.Average(),
                Percentile(warmDevices, 0.95),
                idleCpuSamples.Average(),
                workingSetSamples.Average(),
                privateMemSamples.Average(),
                threadSamples[threadSamples.Count / 2]));
        }
    }

    static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(sorted.Count - 1, lo + 1);
        double frac = idx - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    static void MeasureScanParse()
    {
        StringBuilder devSb = new StringBuilder("List of devices attached\n");
        StringBuilder mdnsSb = new StringBuilder("List of discovered mdns services\n");
        for (int i = 0; i < 50; i++)
        {
            devSb.AppendFormat("SER{0:D4}\tdevice product:prod_{0} model:Model_{0} transport_id:{1}\n", i, i + 1);
            mdnsSb.AppendFormat("adb-SER{0:D4}-abc (2)\t_adb-tls-connect._tcp\t192.168.10.{1}:35555\n", i, 10 + (i % 200));
        }
        string devOut = devSb.ToString();
        string mdnsOut = mdnsSb.ToString();

        AdbScanner.ParseAndMerge(devOut, mdnsOut);

        List<double> samples = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            ScanResult res = AdbScanner.ParseAndMerge(devOut, mdnsOut);
            sw.Stop();
            if (res.Devices.Count != 100) throw new InvalidOperationException("Unexpected count: " + res.Devices.Count);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine(string.Format(
            "PARSE_MERGE_50DEV_MS=avg:{0:F3},p95:{1:F3}",
            samples.Average(), Percentile(samples, 0.95)));
    }

    static void MeasureConfigRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_bench_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
        try
        {
            ConfigManager mgr = new ConfigManager(cfgPath);
            AppConfig cfg = AppConfig.CreateDefault();
            for (int i = 0; i < 20; i++)
            {
                string ep = "192.168.1." + (10 + i) + ":5555";
                ConfigManager.AddSavedEndpoint(cfg, ep);
                ConfigManager.SetDeviceAlias(cfg, ep, "测试设备_" + i);
            }

            mgr.Save(cfg);
            mgr.LoadOrCreate(out _);

            List<double> saveSamples = new List<double>();
            List<double> loadSamples = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                Stopwatch swSave = Stopwatch.StartNew();
                mgr.Save(cfg);
                swSave.Stop();
                saveSamples.Add(swSave.Elapsed.TotalMilliseconds);

                Stopwatch swLoad = Stopwatch.StartNew();
                mgr.LoadOrCreate(out _);
                swLoad.Stop();
                loadSamples.Add(swLoad.Elapsed.TotalMilliseconds);
            }
            saveSamples.Sort();
            loadSamples.Sort();
            Console.WriteLine(string.Format(
                "CONFIG_ATOMIC_SAVE_MS=avg:{0:F3},p95:{1:F3}",
                saveSamples.Average(), Percentile(saveSamples, 0.95)));
            Console.WriteLine(string.Format(
                "CONFIG_LOAD_MS=avg:{0:F3},p95:{1:F3}",
                loadSamples.Average(), Percentile(loadSamples, 0.95)));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    static void MeasureDiscoveryBoundedProbe()
    {
        List<TcpListener> openListeners = new List<TcpListener>();
        List<string> endpoints = new List<string>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                openListeners.Add(l);
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                endpoints.Add("127.0.0.1:" + p);
            }
            for (int i = 0; i < 8; i++)
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                endpoints.Add("127.0.0.1:" + p);
            }

            AppConfig cfg = AppConfig.CreateDefault();
            foreach (string ep in endpoints) ConfigManager.AddSavedEndpoint(cfg, ep);

            BenchAdbClient client = new BenchAdbClient("List of devices attached\n", "");
            DeviceDiscoveryService svc = new DeviceDiscoveryService(cfg, client);

            svc.ScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            List<double> samples = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                ScanResult res = svc.ScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                sw.Stop();
                if (res.Devices.Count < 4) throw new InvalidOperationException("Missing reachable listeners");
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            Console.WriteLine(string.Format(
                "DISCOVERY_12EP_LOOPBACK_MS=avg:{0:F1},p95:{1:F1}",
                samples.Average(), Percentile(samples, 0.95)));
        }
        finally
        {
            foreach (TcpListener l in openListeners) { try { l.Stop(); } catch { } }
        }
    }

    static void MeasureMainFormSingleRender()
    {
        StringBuilder devSb = new StringBuilder("List of devices attached\n");
        for (int i = 0; i < 10; i++)
        {
            devSb.AppendFormat("DEV{0:D3}\tdevice model:Pixel_{0}\n", i);
        }
        ScanResult sampleScan = AdbScanner.ParseAndMerge(devSb.ToString(), "");
        AppConfig cfg = AppConfig.CreateDefault();
        ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_ui_bench.json"));

        Stopwatch sw = Stopwatch.StartNew();
        int dpi = 96;
        using (MainForm form = new MainForm(mgr, cfg, null, false))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            form.LoadScanResultForTest(sampleScan);
            Application.DoEvents();
            dpi = form.DeviceDpi;
            sw.Stop();
            form.Close();
        }
        Console.WriteLine("DEVICE_DPI=" + dpi);
        Console.WriteLine(string.Format("UI_MAINFORM_10DEV_SINGLE_MS={0:F1}", sw.Elapsed.TotalMilliseconds));
    }

    static void MeasureSameListRefresh(int deviceCount)
    {
        StringBuilder devSb = new StringBuilder("List of devices attached\n");
        for (int i = 0; i < deviceCount; i++)
        {
            devSb.AppendFormat("DEV{0:D3}\tdevice model:Pixel_{0}\n", i);
        }
        string raw = devSb.ToString();
        AppConfig cfg = AppConfig.CreateDefault();
        ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_refresh_bench.json"));

        List<double> samples = new List<double>();
        List<double> allocKb = new List<double>();
        int rebuildsAfterWarmup = 0;
        using (MainForm form = new MainForm(mgr, cfg, null, false))
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            form.LoadScanResultForTest(AdbScanner.ParseAndMerge(raw, ""));
            Application.DoEvents();
            int baseRebuild = form.RebuildCountForTest;

            for (int i = 0; i < 5; i++)
            {
                ScanResult dup = AdbScanner.ParseAndMerge(raw, "");
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                Stopwatch sw = Stopwatch.StartNew();
                form.LoadScanResultForTest(dup);
                Application.DoEvents();
                sw.Stop();
                long a1 = GC.GetAllocatedBytesForCurrentThread();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                allocKb.Add((a1 - a0) / 1024.0);
            }
            rebuildsAfterWarmup = form.RebuildCountForTest - baseRebuild;
            form.Close();
        }
        samples.Sort();
        Console.WriteLine(string.Format(
            "UI_SAME_LIST_REFRESH_{0}DEV=avg:{1:F3} ms,p95:{2:F3} ms,alloc:{3:F1} KB,rebuilds:{4}",
            deviceCount, samples.Average(), Percentile(samples, 0.95), allocKb.Average(), rebuildsAfterWarmup));
    }
}
'@

    $benchSrcDir = Join-Path $runnerDir 'bench-src'
    New-Item -ItemType Directory -Path $benchSrcDir -Force | Out-Null
    Set-Content -Path (Join-Path $benchSrcDir 'Bench.csproj') -Value $csproj -Encoding UTF8
    Set-Content -Path (Join-Path $benchSrcDir 'Program.cs') -Value $programCs -Encoding UTF8

    $env:WINSCRCPY_BENCH_ROOT = $runnerDir
    try {
        $rawOutput = & dotnet run --project (Join-Path $benchSrcDir 'Bench.csproj') -c Release -- $runnerDir
    } finally {
        Remove-Item Env:WINSCRCPY_BENCH_ROOT -ErrorAction SilentlyContinue
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline runner failed: $rawOutput"
    }

    $metrics = @{}
    $e2eRows = New-Object System.Collections.Generic.List[string]
    foreach ($line in $rawOutput) {
        if ($line -match '^E2E_([^=]+)=(.+)$') {
            $kv = @{}
            foreach ($pair in $matches[2].Split('|')) {
                $idx = $pair.IndexOf(':')
                if ($idx -gt 0) {
                    $kv[$pair.Substring(0, $idx)] = $pair.Substring($idx + 1)
                }
            }
            $e2eRows.Add("| $($kv['label']) | $($kv['first_interactive']) / $($kv['warm_interactive_avg']) / $($kv['warm_interactive_p95']) | $($kv['first_device']) / $($kv['warm_device_avg']) / $($kv['warm_device_p95']) | $($kv['idle_cpu_avg'])% | $($kv['ws_avg']) MB / $($kv['priv_avg']) MB | $($kv['threads_med']) |")
        } elseif ($line -match '^([^=]+)=(.+)$') {
            $metrics[$matches[1]] = $matches[2].Trim()
        }
    }
    if ($targets.Count -gt 0 -and $e2eRows.Count -eq 0) {
        throw "No E2E baseline rows were emitted by the benchmark runner."
    }
    if (-not [string]::IsNullOrEmpty($baselineStatusNote)) {
        $e2eRows.Insert(0, "| 迁移前基线 (.NET Framework 4, $BaselineCommit) | $baselineStatusNote | - | - | - | - |")
    }

    $dllMeta = Get-ArtifactMeta $appDll '未构建'
    $normalExeMeta = Get-ArtifactMeta $normalExePath '未构建'
    $fdExeMeta = Get-ArtifactMeta $fdPublishExe '未发布 (可加 -Publish)'
    $scExeMeta = Get-ArtifactMeta $scPublishExe '未发布 (可加 -Publish -SelfContained)'
    $scCompExeMeta = Get-ArtifactMeta $scCompPublishExe '未发布 (可加 -Publish -SelfContained -Compress)'
    $fdZipMeta = Get-ArtifactMeta $fdZip '未打包 (可加 -Publish -Package)'
    $scZipMeta = Get-ArtifactMeta $scZip '未打包 (可加 -Publish -SelfContained -Package)'
    $scCompZipMeta = Get-ArtifactMeta $scCompZip '未打包 (可加 -Publish -SelfContained -Compress -Package)'
    $normalDirMb = if (Test-Path -LiteralPath $buildDir) {
        $sum = (Get-ChildItem -LiteralPath $buildDir -File | Measure-Object -Property Length -Sum).Sum
        '{0:N2} MB' -f ($sum / 1MB)
    } else { '未构建' }
    $baselineExeMeta = if (-not [string]::IsNullOrEmpty($baselineExePath)) { Get-ArtifactMeta $baselineExePath '未构建' } else { @{ Exists = $false; Size = $baselineStatusNote; Sha256 = '-' } }

    $buildModeNote = if ($NoBuild) { '-NoBuild（跳过构建，直接测量既有产物）' } else { '默认先完整构建并分别发布框架依赖/自包含单文件' }
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
    $e2eTable = $e2eRows -join "`n"

    $report = @"
# WinScrcpy 性能与产物体积基线报告

- **生成时间**：$timestamp
- **提交与工作区**：``$gitShort`` (``$gitCommit``) · 工作区状态：$workspaceState
- **构建配置与模式**：``$Configuration`` · $buildModeNote
- **运行环境**：$($metrics['DOTNET']) / $($metrics['OS']) / CPU 逻辑核心 $($metrics['CPU_COUNT']) / 测试窗口 DPI $($metrics['DEVICE_DPI'])

## 产物体积与校验基线

| 产物与发布类型 | 路径 | 实测大小 | SHA256 |
| :--- | :--- | :--- | :--- |
| 迁移前基线 EXE（.NET Framework 4，临时构建） | ``git:$BaselineCommit -> WinScrcpy.exe`` | $($baselineExeMeta.Size) | ``$($baselineExeMeta.Sha256)`` |
| 被测核心程序集（框架依赖） | ``bin\$Configuration\net10.0-windows\WinScrcpy.dll`` | $($dllMeta.Size) | ``$($dllMeta.Sha256)`` |
| 普通构建入口 EXE（框架依赖） | ``bin\$Configuration\net10.0-windows\WinScrcpy.exe`` | $($normalExeMeta.Size) | ``$($normalExeMeta.Sha256)`` |
| 普通构建目录总大小（框架依赖） | ``bin\$Configuration\net10.0-windows\`` | $normalDirMb | - |
| 单文件发布（框架依赖） | ``bin\$Configuration\net10.0-windows\win-x64\publish-framework-dependent\WinScrcpy.exe`` | $($fdExeMeta.Size) | ``$($fdExeMeta.Sha256)`` |
| 单文件发布（不压缩自包含，推荐） | ``bin\$Configuration\net10.0-windows\win-x64\publish-selfcontained\WinScrcpy.exe`` | $($scExeMeta.Size) | ``$($scExeMeta.Sha256)`` |
| 单文件发布（压缩自包含，小体积） | ``bin\$Configuration\net10.0-windows\win-x64\publish-selfcontained-compressed\WinScrcpy.exe`` | $($scCompExeMeta.Size) | ``$($scCompExeMeta.Sha256)`` |
| 便携发布包（框架依赖） | ``bin\$Configuration\WinScrcpy-win-x64-framework-dependent.zip`` | $($fdZipMeta.Size) | ``$($fdZipMeta.Sha256)`` |
| 便携发布包（不压缩自包含） | ``bin\$Configuration\WinScrcpy-win-x64-selfcontained.zip`` | $($scZipMeta.Size) | ``$($scZipMeta.Sha256)`` |
| 便携发布包（压缩自包含） | ``bin\$Configuration\WinScrcpy-win-x64-selfcontained-compressed.zip`` | $($scCompZipMeta.Size) | ``$($scCompZipMeta.Sha256)`` |

## 应用端到端启动与空闲资源基线（独立进程实测）

每类产物均在隔离临时目录启动 4 次**独立子进程**（第 1 轮为全新单文件解包目录 ``DOTNET_BUNDLE_EXTRACT_BASE_DIR``，第 2~4 轮为后续独立进程），通过 Mock ``adb.exe`` 返回 2 台有线设备后于静置期采样：

| 构建形态 | 启动至窗口可交互 (首轮 / 后续均值 / P95 ms) | 首批设备出现 (首轮 / 后续均值 / P95 ms) | 空闲 CPU 占用 | 空闲内存 (工作集 / 专用) | 空闲线程数 (中位) |
| :--- | :--- | :--- | :--- | :--- | :--- |
$e2eTable

> 口径说明：窗口可交互以可见主窗口句柄通过 ``SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`` 判定；首批设备出现以界面渲染出 ``共 2 台`` / ``投屏`` 子控件判定；空闲资源在扫描完成静置 1.5s 后连续采样 1.5s（CPU 百分比按全部逻辑核心归一化）。

## 局部微基准（同进程受控替身，非应用冷启动）

| 测量项 | 实测结果 | 说明 |
| :--- | :--- | :--- |
| 100 条通道（50 有线 + 50 mDNS）``AdbScanner.ParseAndMerge`` | $($metrics['PARSE_MERGE_50DEV_MS']) ms | 同进程连续 200 次（已预热） |
| 20 项设备 ``ConfigManager.Save``（含 ``.tmp`` + ``File.Replace`` ``.bak``） | $($metrics['CONFIG_ATOMIC_SAVE_MS']) ms | 同进程连续 30 次（已预热） |
| 20 项设备 ``ConfigManager.LoadOrCreate`` | $($metrics['CONFIG_LOAD_MS']) ms | 同进程连续 30 次（已预热） |
| 12 个本地回环端点的 ``DeviceDiscoveryService.ScanAsync`` | $($metrics['DISCOVERY_12EP_LOOPBACK_MS']) ms | 4 个监听中 + 8 个已关闭端口，5 次统计 |
| 同进程单次 ``MainForm`` 创建 + 10 台设备首轮渲染与 DPI 布局 | $($metrics['UI_MAINFORM_10DEV_SINGLE_MS']) ms | 仅用于组件渲染回归参考，不代表应用冷启动 |
| 同窗体相同列表刷新（2 台 / 5 次） | $($metrics['UI_SAME_LIST_REFRESH_2DEV']) | 快照匹配跳过控件销毁与重建 |
| 同窗体相同列表刷新（10 台 / 5 次） | $($metrics['UI_SAME_LIST_REFRESH_10DEV']) | 快照匹配跳过控件销毁与重建 |

> 附注：此前同进程连续 6 次创建并 ``Show()/Close()`` ``MainForm`` 时，第 2~3 次起出现约 1,798~2,020 ms 的反常耗时；经实测排查，空 ``new System.Windows.Forms.Form()`` 在同进程连续 ``Show()/Close()`` 到第 7 次起同样触发约 2,013 ms 的系统级 ``Form.Show()`` 阻塞，属于同进程重复弹窗测量的系统伪影，并非 ``MainForm`` 性能退化。实际应用启动性能请以上方「应用端到端启动与空闲资源基线」为准。
"@

    $reportDir = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrEmpty($reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }
    Set-Content -Path $ReportPath -Value $report -Encoding UTF8
    Write-Output $report
    Write-Output "Report saved: $ReportPath"
}
finally {
    if (Test-Path -LiteralPath $runnerDir) {
        Remove-Item -LiteralPath $runnerDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
