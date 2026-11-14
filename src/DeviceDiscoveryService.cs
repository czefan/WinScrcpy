using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

internal sealed class DeviceDiscoveryService
{
    public const int MaxTcpProbeConcurrency = 16;
    public const int MaxAdbQueryConcurrency = 4;

    readonly AppConfig config;
    readonly IAdbClient adb;
    readonly Func<string, int, CancellationToken, Task<bool>> tcpProbe;

    public DeviceDiscoveryService(
        AppConfig config,
        IAdbClient adbClient = null,
        Func<string, int, CancellationToken, Task<bool>> tcpProbe = null)
    {
        this.config = config;
        this.adb = adbClient ?? new AdbClient(config);
        this.tcpProbe = tcpProbe ?? ProbeTcpEndpointAsync;
    }

    public Task<ScanResult> ScanAsync(Action<ScanResult> onStage1 = null, CancellationToken cancellationToken = default)
    {
        return ScanInternalAsync(onStage1, cancellationToken);
    }

    async Task<ScanResult> ScanInternalAsync(Action<ScanResult> onStage1, CancellationToken cancellationToken)
    {
        Task<CommandOutput> devTask = adb.QueryDevicesRawAsync(cancellationToken);
        Task<CommandOutput> mdnsTask = adb.QueryMdnsServicesRawAsync(cancellationToken);

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
                        string sn = await adb.QueryHardwareSerialAsync(target, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(sn))
                        {
                            lock (sync)
                            {
                                serialMap[target] = sn;
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
            uniqueEndpoints, 1000, MaxTcpProbeConcurrency, cancellationToken, tcpProbe).ConfigureAwait(false);
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
