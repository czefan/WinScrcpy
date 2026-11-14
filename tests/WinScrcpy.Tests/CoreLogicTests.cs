using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace WinScrcpy.Tests;

public sealed class CoreLogicTests
{
    [Fact]
    public void MdnsDedupeAndOrder_MergesSuffixedEntriesInDescendingOrder()
    {
        string mdns = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841",
            "adb-9A8B7C6D5E4F3G2H-pair\t_adb-tls-pairing._tcp\t192.168.3.8:41234"
        });
        ScanResult res = AdbScanner.ParseAndMerge("List of devices attached\n", mdns);

        Assert.True(res.PairingServices.Count == 1 && res.PairingServices[0].Endpoint == "192.168.3.8:41234",
            "pairing service extracted and excluded from device list");
        Assert.True(res.Devices.Count == 1, "duplicate mDNS entries with (2)/(3) merged into 1 device");
        DeviceItem dev = res.Devices[0];
        Assert.Equal(ChannelKind.MdnsPending, dev.Channel);
        Assert.Equal("9A8B7C6D5E4F3G2H", dev.HardwareSerial);
        Assert.True(dev.CandidateEndpoints.Count == 2
            && dev.CandidateEndpoints[0] == "192.168.3.8:35841"
            && dev.CandidateEndpoints[1] == "192.168.3.8:38081",
            "endpoints ordered by descending suffix order (3) before (2)");

        string mdnsTrailingDot = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-TRAIL01-xyz (1)\t_adb-tls-connect._tcp.\t192.168.3.20:39001",
            "adb-TRAIL01-pair\t_adb-tls-pairing._tcp.\t192.168.3.20:41200"
        });
        ScanResult trailRes = AdbScanner.ParseAndMerge("List of devices attached\n", mdnsTrailingDot);
        Assert.True(trailRes.PairingServices.Count == 1 && trailRes.PairingServices[0].Endpoint == "192.168.3.20:41200",
            "ParseMdns handles trailing dot in pairing service type");
        Assert.True(trailRes.Devices.Count == 1
            && trailRes.Devices[0].Channel == ChannelKind.MdnsPending
            && trailRes.Devices[0].CandidateEndpoints[0] == "192.168.3.20:39001",
            "ParseMdns handles trailing dot in connect service type");
    }

    [Fact]
    public void DualChannelAndOfflineUsb_PreservedSeparately()
    {
        string devices = string.Join("\n", new string[]
        {
            "List of devices attached",
            "9A8B7C6D5E4F3G2H       device product:pad_x model:Pad_X device:pad_x transport_id:1",
            "192.168.3.8:35841      device product:pad_x model:Pad_X device:pad_x transport_id:2"
        });
        string mdns = "adb-9A8B7C6D5E4F3G2H-YzPHtO (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841\n";

        ScanResult res = AdbScanner.ParseAndMerge(devices, mdns);
        Assert.Equal(2, res.Devices.Count);
        Assert.True(res.Devices[0].Channel == ChannelKind.Usb && res.Devices[0].AdbSerial == "9A8B7C6D5E4F3G2H", "USB channel ranked first");
        Assert.True(res.Devices[1].Channel == ChannelKind.WirelessConnected
            && res.Devices[1].AdbSerial == "192.168.3.8:35841"
            && res.Devices[1].HardwareSerial == "9A8B7C6D5E4F3G2H",
            "connected wireless merged with mDNS and enriched with hardware serial");

        string offlineUsbDevices = string.Join("\n", new string[]
        {
            "List of devices attached",
            "USBDEV001              offline transport_id:1",
            "adb-USBDEV001-YzPHtO._adb-tls-connect._tcp device product:pad_x model:Pad_X transport_id:2"
        });
        string offlineUsbMdns = "adb-USBDEV001-YzPHtO\t_adb-tls-connect._tcp\t192.168.3.8:35841\n";
        ScanResult offlineUsbRes = AdbScanner.ParseAndMerge(offlineUsbDevices, offlineUsbMdns);
        Assert.Equal(2, offlineUsbRes.Devices.Count);
        Assert.True(offlineUsbRes.Devices[1].Channel == ChannelKind.Offline && offlineUsbRes.Devices[1].AdbSerial == "USBDEV001",
            "offline USB serial is preserved instead of promoted to MdnsPending");
    }

    [Fact]
    public void DuplicateWireless_MergesWithAndWithoutMdns()
    {
        string devices = string.Join("\n", new string[]
        {
            "List of devices attached",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:4",
            "192.168.3.8:46787      device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:3"
        });
        string mdns = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO\t_adb-tls-connect._tcp\t192.168.3.8:46787"
        });

        ScanResult res = AdbScanner.ParseAndMerge(devices, mdns);
        Assert.Single(res.Devices);
        DeviceItem item = res.Devices[0];
        Assert.Equal(ChannelKind.WirelessConnected, item.Channel);
        Assert.Equal("192.168.3.8:46787", item.AdbSerial);
        Assert.True(item.AlternateSerials.Count == 1
            && item.AlternateSerials[0] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp");
        Assert.True(AdbClient.BuildDisconnectTargets(item).Count >= 2);

        string devicesNoMdns = string.Join("\n", new string[]
        {
            "List of devices attached",
            "192.168.3.8:46787      device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:3",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:4"
        });
        ScanResult resNoMdns = AdbScanner.ParseAndMerge(devicesNoMdns, "List of discovered mdns services\n");
        Assert.Equal(2, resNoMdns.Devices.Count);

        AdbScanner.EnrichAndDeduplicate(resNoMdns, dev => dev.AdbSerial == "192.168.3.8:46787" ? "9A8B7C6D5E4F3G2H" : "");
        Assert.Single(resNoMdns.Devices);
        Assert.Equal("192.168.3.8:46787", resNoMdns.Devices[0].AdbSerial);
        Assert.Equal("9A8B7C6D5E4F3G2H", resNoMdns.Devices[0].HardwareSerial);
        Assert.True(resNoMdns.Devices[0].AlternateSerials.Count == 1
            && resNoMdns.Devices[0].AlternateSerials[0] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp");
    }

    [Fact]
    public void MdnsStaleCacheFilter_KeepsReachablePortsOnly()
    {
        string mdns = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-STALE01-abc (2)\t_adb-tls-connect._tcp\t192.168.3.99:40001",
            "adb-PARTIAL02-def (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841",
            "adb-PARTIAL02-def (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081"
        });
        string devices = "List of devices attached\nUSBDEV01\tdevice model:Pixel_9\n";
        ScanResult res = AdbScanner.ParseAndMerge(devices, mdns);

        AdbScanner.FilterReachableMdns(res, ep => ep == "192.168.3.8:38081");

        Assert.Equal(1, res.FilteredStaleMdnsCount);
        Assert.Equal(2, res.Devices.Count);
        Assert.Equal(ChannelKind.Usb, res.Devices[0].Channel);
        Assert.True(res.Devices[1].Channel == ChannelKind.MdnsPending
            && res.Devices[1].CandidateEndpoints.Count == 1
            && res.Devices[1].CandidateEndpoints[0] == "192.168.3.8:38081");
    }

    [Fact]
    public void ConfigResolution_CaseInsensitiveMatchAndArgInheritance()
    {
        string json = "{"
            + "\"globalArgs\": [\"--stay-awake\"],"
            + "\"devices\": {"
            + "  \"9a8b7c6d5e4f3g2h\": { \"alias\": \"平板\", \"args\": [\"--max-size=1600\"] },"
            + "  \"OTHER\": { \"alias\": \"备用机\", \"args\": [] },"
            + "  \"MIXED01\": { \"alias\": \"\", \"args\": [\"--max-fps=60\"] },"
            + "  \"192.168.3.50:5555\": { \"alias\": \"混合设备\", \"args\": [] }"
            + "}"
            + "}";
        AppConfig cfg = ConfigManager.ParseJson(json);

        DeviceItem tablet = new DeviceItem { HardwareSerial = "9A8B7C6D5E4F3G2H", AdbSerial = "192.168.3.8:35841" };
        ResolvedDeviceConfig r1 = ConfigManager.Resolve(cfg, tablet);
        Assert.True(r1.Alias == "平板" && r1.HasCustomArgs && r1.Args.Count == 1 && r1.Args[0] == "--max-size=1600");

        DeviceItem backup = new DeviceItem { HardwareSerial = "OTHER" };
        ResolvedDeviceConfig r2 = ConfigManager.Resolve(cfg, backup);
        Assert.True(r2.Alias == "备用机" && !r2.HasCustomArgs && r2.Args.Count == 1 && r2.Args[0] == "--stay-awake");

        DeviceItem mixed = new DeviceItem { HardwareSerial = "MIXED01", AdbSerial = "192.168.3.50:5555" };
        ResolvedDeviceConfig r3 = ConfigManager.Resolve(cfg, mixed);
        Assert.True(r3.Alias == "混合设备" && r3.HasCustomArgs && r3.Args.Count == 1 && r3.Args[0] == "--max-fps=60");
    }

    [Fact]
    public void TerminalLauncher_QuoteAndCommandBuilders()
    {
        Assert.Equal("--stay-awake", TerminalLauncher.QuoteArg("--stay-awake"));
        Assert.Equal("\"--window-title=My Phone\"", TerminalLauncher.QuoteArg("--window-title=My Phone"));
        Assert.Equal("\"a\\\"b\"", TerminalLauncher.QuoteArg("a\"b"));

        string wtShell = TerminalLauncher.BuildWtTerminalArguments("adb", "pwsh.exe", "192.168.3.12:5555", "客厅;机", @"C:\Tools\WinScrcpy\", TerminalMode.AdbShell);
        Assert.Contains("-w 0 new-tab --title \"客厅 机 - ADB Shell\" -d C:\\Tools\\WinScrcpy -- adb -s 192.168.3.12:5555 shell", wtShell);

        string wtLocal = TerminalLauncher.BuildWtTerminalArguments(@"D:\SDK Tools\adb.exe", @"C:\Program Files\PowerShell\7\pwsh.exe", "USBDEV001", "Pixel 9", @"C:\Tools\WinScrcpy\", TerminalMode.LocalAdbEnv);
        string afterSep = wtLocal.Split(new string[] { " -- " }, StringSplitOptions.None)[1];
        Assert.True(afterSep.StartsWith("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoExit -EncodedCommand ")
            && !afterSep.Contains(";"), wtLocal);

        string psScript = TerminalLauncher.BuildLocalPowerShellInitScript(@"D:\SDK Tools\adb.exe", "USBDEV001", "Pixel 9 - ADB 终端");
        Assert.Contains("$env:ANDROID_SERIAL = 'USBDEV001'", psScript);
        Assert.Contains("$env:PATH = 'D:\\SDK Tools;' + $env:PATH", psScript);
        Assert.Contains("function global:adb { & 'D:\\SDK Tools\\adb.exe' @args }", psScript);

        string standaloneLocal = TerminalLauncher.BuildStandalonePsTerminalArguments("adb", "192.168.3.12:5555", "Test & Box", TerminalMode.LocalAdbEnv);
        Assert.StartsWith("-NoLogo -NoExit -EncodedCommand ", standaloneLocal);
        int b64Idx = standaloneLocal.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length;
        string decodedLocal = Encoding.Unicode.GetString(Convert.FromBase64String(standaloneLocal.Substring(b64Idx)));
        Assert.True(decodedLocal.Contains("$env:ANDROID_SERIAL = '192.168.3.12:5555'") && !decodedLocal.Contains("function global:adb"), decodedLocal);

        string standaloneShell = TerminalLauncher.BuildStandalonePsTerminalArguments("adb", "192.168.3.12:5555", "Box", TerminalMode.AdbShell);
        Assert.True(standaloneShell.StartsWith("-NoLogo -EncodedCommand ") && !standaloneShell.Contains("-NoExit"), standaloneShell);
    }

    [Fact]
    public void AdbClient_EndpointParsingAndDisconnectTargets()
    {
        string host;
        int port;
        Assert.True(AdbClient.TryParseEndpoint("192.168.3.8:35841", out host, out port)
            && host == "192.168.3.8" && port == 35841);
        Assert.True(AdbClient.TryParseEndpoint("[fe80::1]:5555", out host, out port)
            && host == "fe80::1" && port == 5555);
        Assert.False(AdbClient.TryParseEndpoint("invalid-port:99999", out host, out port));

        DeviceItem mDnsConnected = new DeviceItem
        {
            Channel = ChannelKind.WirelessConnected,
            AdbSerial = "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp"
        };
        mDnsConnected.CandidateEndpoints.Add("192.168.3.8:46787");

        List<string> targets = AdbClient.BuildDisconnectTargets(mDnsConnected);
        Assert.True(targets.Count == 2
            && targets[0] == "192.168.3.8:46787"
            && targets[1] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp");
    }

    [Fact]
    public void QrEncoderAndSavedConfigPersistence_RoundTripsCleanly()
    {
        bool[,] qr = QrEncoder.Encode("WIFI:T:ADB;S:winscrcpy-abc123;P:secret99;;");
        int n = qr.GetLength(0);
        Assert.True(n == qr.GetLength(1) && n >= 21 && (n - 17) % 4 == 0);
        Assert.True(qr[3, 3] && qr[2, 2] && qr[4, 4] && !qr[1, 1] && qr[0, 0]);
        Assert.True(qr[3, n - 4] && !qr[1, n - 2] && qr[0, n - 1]);
        Assert.True(qr[n - 4, 3] && !qr[n - 2, 1] && qr[n - 1, 0]);
        Assert.True(qr[n - 8, 8]);
        Assert.True(qr[6, 8] && !qr[6, 9]);

        using (Bitmap bmp = QrEncoder.RenderBitmap(qr, 4, 2))
        {
            Assert.True(bmp.Width == (n + 4) * 4 && bmp.Height == bmp.Width);
        }

        string mdns = "List of discovered mdns services\nwinscrcpy-abc123 (2)\t_adb-tls-pairing._tcp\t192.168.3.8:37123\n";
        List<PairingServiceInfo> list = AdbScanner.ParsePairingServices(mdns);
        Assert.True(list.Count == 1 && list[0].Endpoint == "192.168.3.8:37123");
        PairingServiceInfo matched = PairDialog.FindMatchingPairingService(list, "winscrcpy-abc123");
        Assert.True(matched != null && matched.Endpoint == "192.168.3.8:37123");
        Assert.Equal("10.0.0.5:5555", ManualConnectDialog.BuildEndpointFromInputs("10.0.0.5:5555", "", "", "5555"));
        Assert.Equal("192.168.3.8:5555", ManualConnectDialog.BuildEndpointFromInputs("", "3", "8", "5555"));
        Assert.Equal("", ManualConnectDialog.BuildEndpointFromInputs("", "3", "", "5555"));

        Assert.Equal("192.168.3.12:5555", ConfigManager.NormalizeEndpoint("192.168.3.12"));
        Assert.True(ConfigManager.NormalizeEndpoint("fe80::1") == "[fe80::1]:5555"
            && ConfigManager.NormalizeEndpoint("[fe80::1]") == "[fe80::1]:5555");

        AppConfig roundCfg = AppConfig.CreateDefault();
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.12");
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.15:5556");
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.12:5555");
        ConfigManager.SetDeviceAlias(roundCfg, "CTRL01", "带\t制表\n换行\"测试\\别名");
        Assert.True(roundCfg.savedEndpoints.Count == 2
            && roundCfg.savedEndpoints[0] == "192.168.3.12:5555"
            && roundCfg.savedEndpoints[1] == "192.168.3.15:5556");

        string tmpCfgPath = Path.Combine(Path.GetTempPath(), "winscrcpy_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ConfigManager tmpMgr = new ConfigManager(tmpCfgPath);
            tmpMgr.Save(roundCfg);
            string warn;
            AppConfig reloaded = tmpMgr.LoadOrCreate(out warn);
            Assert.True(string.IsNullOrEmpty(warn)
                && reloaded.savedEndpoints.Count == 2
                && reloaded.savedEndpoints[0] == "192.168.3.12:5555"
                && ConfigManager.GetDeviceAlias(reloaded, "CTRL01") == "带\t制表\n换行\"测试\\别名");
        }
        finally
        {
            try { if (File.Exists(tmpCfgPath)) File.Delete(tmpCfgPath); }
            catch { }
        }

        ScanResult mergeRes = AdbScanner.ParseAndMerge("List of devices attached\n", "List of discovered mdns services\nadb-ABC-1\t_adb-tls-connect._tcp\t192.168.3.8:35841\n");
        AdbScanner.MergeReachableSavedEndpoints(mergeRes, new string[] { "192.168.3.8:35841", "192.168.3.12:5555" });
        Assert.True(mergeRes.Devices.Count == 2
            && mergeRes.Devices[0].Channel == ChannelKind.MdnsPending
            && mergeRes.Devices[1].Channel == ChannelKind.SavedPending
            && mergeRes.Devices[1].CandidateEndpoints[0] == "192.168.3.12:5555");

        AppConfig aliasCfg = AppConfig.CreateDefault();
        aliasCfg.devices["192.168.3.12:5555"] = new DeviceConfig { alias = "客厅盒子" };
        ResolvedDeviceConfig savedResolved = ConfigManager.Resolve(aliasCfg, mergeRes.Devices[1]);
        Assert.Equal("客厅盒子", savedResolved.Alias);

        ScanResult staleOffline = AdbScanner.ParseAndMerge("List of devices attached\n192.168.3.12:5555      offline product:dev_a model:Model_A transport_id:7\n", "");
        Assert.True(staleOffline.Devices.Count == 1 && staleOffline.Devices[0].Channel == ChannelKind.Offline);
        Assert.False(AdbScanner.HasOnlineOrPendingDeviceForEndpoint(staleOffline.Devices, "192.168.3.12:5555"));
        AdbScanner.MergeReachableSavedEndpoints(staleOffline, new string[] { "192.168.3.12:5555" });
        Assert.True(staleOffline.Devices.Count == 1
            && staleOffline.Devices[0].Channel == ChannelKind.SavedPending
            && staleOffline.Devices[0].Model == "Model A"
            && staleOffline.Devices[0].AdbSerial == ""
            && staleOffline.Devices[0].CandidateEndpoints[0] == "192.168.3.12:5555");
    }

    [Fact]
    public void ConfigManager_AtomicSaveCreatesBackup_AndPreservesCorruptedConfig()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "winscrcpy.json"), ConfigManager.DefaultConfigPath);

        string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_cfg_rel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
        try
        {
            ConfigManager mgr = new ConfigManager(cfgPath);
            AppConfig first = AppConfig.CreateDefault();
            ConfigManager.AddSavedEndpoint(first, "192.168.3.10:5555");
            mgr.Save(first);

            Assert.True(File.Exists(cfgPath));
            Assert.False(File.Exists(mgr.BackupPath));
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));

            string firstJson = File.ReadAllText(cfgPath, Encoding.UTF8);

            AppConfig second = AppConfig.CreateDefault();
            ConfigManager.AddSavedEndpoint(second, "192.168.3.20:5555");
            mgr.Save(second);

            Assert.True(File.Exists(cfgPath));
            Assert.True(File.Exists(mgr.BackupPath));
            Assert.Equal(firstJson, File.ReadAllText(mgr.BackupPath, Encoding.UTF8));
            Assert.Contains("192.168.3.20:5555", File.ReadAllText(cfgPath, Encoding.UTF8));
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));

            string brokenJson = "{ \"savedEndpoints\": [ broken";
            File.WriteAllText(cfgPath, brokenJson, Encoding.UTF8);

            AppConfig fallback = mgr.LoadOrCreate(out string warn);
            Assert.NotNull(fallback);
            Assert.False(string.IsNullOrEmpty(warn));
            Assert.True(mgr.IsConfigCorrupted);
            Assert.Equal(brokenJson, File.ReadAllText(cfgPath, Encoding.UTF8));

            Assert.Throws<InvalidOperationException>(() => mgr.Save(fallback));
            Assert.Equal(brokenJson, File.ReadAllText(cfgPath, Encoding.UTF8));
            Assert.Equal(firstJson, File.ReadAllText(mgr.BackupPath, Encoding.UTF8));
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));

            // Repairing file and reloading clears the corrupted state so saving resumes cleanly.
            File.WriteAllText(cfgPath, firstJson, Encoding.UTF8);
            AppConfig recovered = mgr.LoadOrCreate(out string recoverWarn);
            Assert.True(string.IsNullOrEmpty(recoverWarn));
            Assert.False(mgr.IsConfigCorrupted);
            ConfigManager.AddSavedEndpoint(recovered, "192.168.3.30:5555");
            mgr.Save(recovered);
            Assert.Contains("192.168.3.30:5555", File.ReadAllText(cfgPath, Encoding.UTF8));
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    [Fact]
    public async Task DeviceDiscoveryService_CachesHardwareSerialAndInvalidatesOnChange()
    {
        int queryCount = 0;
        string currentDevicesOut = "List of devices attached\n192.168.1.50:5555\tdevice model:Pixel_9 transport_id:1\n";
        string currentReturnedSerial = "HWSN_001";

        FakeAdbClient fakeAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = currentDevicesOut }),
            QuerySerialHandler = (adbSerial, ct) =>
            {
                Interlocked.Increment(ref queryCount);
                return Task.FromResult(currentReturnedSerial);
            }
        };

        HardwareSerialCache cache = new HardwareSerialCache();
        AppConfig cfg = AppConfig.CreateDefault();
        DeviceDiscoveryService svc = new DeviceDiscoveryService(cfg, fakeAdb, (ep, ms, ct) => Task.FromResult(false), cache);

        // 1. First scan queries once and populates cache (with transport_id:1).
        ScanResult r1 = await svc.ScanAsync();
        Assert.Equal(1, queryCount);
        Assert.Single(r1.Devices);
        Assert.Equal("1", r1.Devices[0].TransportId);
        Assert.Equal("HWSN_001", r1.Devices[0].HardwareSerial);

        // 2. Second scan with same endpoint, model & transport_id:1 hits cache.
        ScanResult r2 = await svc.ScanAsync();
        Assert.Equal(1, queryCount);
        Assert.Equal("HWSN_001", r2.Devices[0].HardwareSerial);

        // 3. Same IP:port & same model replaced without offline step (transport_id:1 -> 2): invalidates cache & re-queries.
        currentDevicesOut = "List of devices attached\n192.168.1.50:5555\tdevice model:Pixel_9 transport_id:2\n";
        currentReturnedSerial = "HWSN_002";
        ScanResult r3 = await svc.ScanAsync();
        Assert.Equal(2, queryCount);
        Assert.Equal("2", r3.Devices[0].TransportId);
        Assert.Equal("HWSN_002", r3.Devices[0].HardwareSerial);

        // 4. Reconnect/disconnect: endpoint goes offline, invalidating cache entry.
        currentDevicesOut = "List of devices attached\n192.168.1.50:5555\toffline model:Pixel_9 transport_id:2\n";
        await svc.ScanAsync();
        Assert.Equal(2, queryCount);

        currentDevicesOut = "List of devices attached\n192.168.1.50:5555\tdevice model:Pixel_9 transport_id:2\n";
        currentReturnedSerial = "HWSN_003";
        ScanResult r4 = await svc.ScanAsync();
        Assert.Equal(3, queryCount);
        Assert.Equal("HWSN_003", r4.Devices[0].HardwareSerial);

        // 5. Identity uncertainty (Model changed on same IP:port) invalidates cache.
        currentDevicesOut = "List of devices attached\n192.168.1.50:5555\tdevice model:Pixel_10_Pro transport_id:2\n";
        currentReturnedSerial = "HWSN_004";
        ScanResult r5 = await svc.ScanAsync();
        Assert.Equal(4, queryCount);
        Assert.Equal("HWSN_004", r5.Devices[0].HardwareSerial);

        // 6. Explicit device invalidation clears entry, and empty failure output is not cached.
        cache.InvalidateDevice(r5.Devices[0]);
        currentReturnedSerial = "";
        ScanResult r6 = await svc.ScanAsync();
        Assert.Equal(5, queryCount);
        Assert.Equal("", r6.Devices[0].HardwareSerial);

        currentReturnedSerial = "HWSN_005";
        ScanResult r7 = await svc.ScanAsync();
        Assert.Equal(6, queryCount);
        Assert.Equal("HWSN_005", r7.Devices[0].HardwareSerial);

        // 7. Missing transport_id (unverifiable continuity) conservatively clears cache and bypasses caching.
        currentDevicesOut = "List of devices attached\n192.168.1.50:5555\tdevice model:Pixel_10_Pro\n";
        currentReturnedSerial = "HWSN_006";
        ScanResult r8 = await svc.ScanAsync();
        Assert.Equal(7, queryCount);
        Assert.Equal("HWSN_006", r8.Devices[0].HardwareSerial);

        currentReturnedSerial = "HWSN_007";
        ScanResult r9 = await svc.ScanAsync();
        Assert.Equal(8, queryCount);
        Assert.Equal("HWSN_007", r9.Devices[0].HardwareSerial);
    }

    [Fact]
    public void DeviceDiscoveryService_MergePendingStage1PreservesUnmatchedWirelessCandidates()
    {
        ScanResult previous = AdbScanner.ParseAndMerge(
            "List of devices attached\n9A8B7C6D5E4F3G2H\tdevice model:Pixel_9\n",
            string.Join("\n", new[]
            {
                "List of discovered mdns services",
                "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081",
                "adb-OTHER999-AbCdEf\t_adb-tls-connect._tcp\t192.168.3.99:41001"
            }));
        AdbScanner.MergeReachableSavedEndpoints(previous, new[] { "192.168.3.12:5555" });

        // Stage1 only sees USB + one already-connected saved endpoint (192.168.3.12:5555).
        ScanResult stage1 = AdbScanner.ParseAndMerge(
            "List of devices attached\n9A8B7C6D5E4F3G2H\tdevice model:Pixel_9\n192.168.3.12:5555\tdevice model:SavedTab\n",
            "");

        ScanResult merged = DeviceDiscoveryService.MergePendingStage1(stage1, previous);

        // USB matches should NOT suppress wireless MdnsPending of same hardware serial;
        // 192.168.3.12:5555 is already WirelessConnected in stage1 so SavedPending is NOT duplicated.
        Assert.Equal(4, merged.Devices.Count);
        Assert.Contains(merged.Devices, d => d.Channel == ChannelKind.Usb && !d.IsChecking);
        Assert.Contains(merged.Devices, d => d.Channel == ChannelKind.WirelessConnected && d.AdbSerial == "192.168.3.12:5555" && !d.IsChecking);
        Assert.Contains(merged.Devices, d => d.Channel == ChannelKind.MdnsPending && d.HardwareSerial == "9A8B7C6D5E4F3G2H" && d.IsChecking);
        Assert.Contains(merged.Devices, d => d.Channel == ChannelKind.MdnsPending && d.CandidateEndpoints.Contains("192.168.3.99:41001") && d.IsChecking);
        Assert.DoesNotContain(merged.Devices, d => d.Channel == ChannelKind.SavedPending);
    }
}
