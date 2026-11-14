using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class SelfTests
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + message);
    }

    public static int Run()
    {
        try
        {
            TestMdnsDedupeAndOrder();
            TestDualChannelUsbAndWireless();
            TestDuplicateWirelessMerged();
            TestDuplicateWirelessWithoutMdns();
            TestMdnsStaleCacheFilter();
            TestTryParseEndpoint();
            TestBuildDisconnectTargets();
            TestConfigResolution();
            TestQuoteArg();
            TestTerminalCommands();
            TestQrEncoderAndPairingMatch();
            TestToolPathResolution();
            TestProcessRunner();
            TestProcessRunnerPipeHeldByChild();
            TestProcessRunnerReadErrors();
            TestScrcpyEnvironmentAndInvalidConfig();
            TestUiLayoutNoClipping();
            TestMainFormBusyRecovery();
            Console.WriteLine("SELFTEST PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELFTEST FAIL: " + ex.Message);
            return 1;
        }
    }

    public static int RenderPreview(string outputDir)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(outputDir);

        AppConfig cfg = AppConfig.CreateDefault();
        ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_test_noop.json"));

        using (MainForm emptyForm = new MainForm(mgr, cfg, null, false))
        {
            ShowOffscreen(emptyForm);
            emptyForm.LoadScanResultForTest(new ScanResult());
            Application.DoEvents();
            SaveBitmap(emptyForm, Path.Combine(outputDir, "preview_empty.png"));
            emptyForm.Close();
        }

        using (MainForm devicesForm = new MainForm(mgr, cfg, null, false))
        {
            ShowOffscreen(devicesForm);
            devicesForm.LoadScanResultForTest(BuildMockScanResult());
            Application.DoEvents();
            SaveBitmap(devicesForm, Path.Combine(outputDir, "preview_devices.png"));

            devicesForm.SelectAllCheckBox.Checked = true;
            Application.DoEvents();
            SaveBitmap(devicesForm, Path.Combine(outputDir, "preview_selected.png"));

            ScanResult mixedSaved = BuildMockScanResult();
            AdbScanner.MergeReachableSavedEndpoints(mixedSaved, new string[] { "192.168.3.12:5555" });
            devicesForm.LoadScanResultForTest(mixedSaved);
            Application.DoEvents();
            SaveBitmap(devicesForm, Path.Combine(outputDir, "preview_saved.png"));

            ScanResult offlineScan = AdbScanner.ParseAndMerge("List of devices attached\n192.168.3.12:5555      offline product:dev_a model:Model_A transport_id:7\n", "");
            devicesForm.LoadScanResultForTest(offlineScan);
            Application.DoEvents();
            SaveBitmap(devicesForm, Path.Combine(outputDir, "preview_offline.png"));
            devicesForm.Close();
        }

        using (PairDialog dlg = new PairDialog(new DeviceRunner(cfg), null))
        {
            dlg.SetPollingEnabledForTest(false);
            ShowOffscreen(dlg);
            Application.DoEvents();
            SaveBitmap(dlg, Path.Combine(outputDir, "preview_pair_qr.png"));

            dlg.SelectTabForTest(false);
            Application.DoEvents();
            SaveBitmap(dlg, Path.Combine(outputDir, "preview_pair_code.png"));
            dlg.Close();
        }

        cfg.savedEndpoints.Add("192.168.3.12:5555");
        cfg.savedEndpoints.Add("192.168.3.15:5555");
        ConfigManager.SetDeviceAlias(cfg, "192.168.3.12:5555", "客厅测试机");
        using (ManualConnectDialog connectDlg = new ManualConnectDialog(new DeviceRunner(cfg)))
        {
            ShowOffscreen(connectDlg);
            Application.DoEvents();
            SaveBitmap(connectDlg, Path.Combine(outputDir, "preview_connect.png"));
            connectDlg.Close();
        }

        using (SavedEndpointsDialog savedDlg = new SavedEndpointsDialog(mgr, cfg))
        {
            ShowOffscreen(savedDlg);
            Application.DoEvents();
            SaveBitmap(savedDlg, Path.Combine(outputDir, "preview_manage_saved.png"));
            savedDlg.Close();
        }
        return 0;
    }

    static void ShowOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
    }

    static void SaveBitmap(Form form, string path)
    {
        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    static ScanResult BuildMockScanResult()
    {
        string mdns = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081"
        });
        string devices = "List of devices attached\nUSBDEV001\tdevice model:Pixel_9\n";
        return AdbScanner.ParseAndMerge(devices, mdns);
    }

    static void TestMdnsDedupeAndOrder()
    {
        string mdns = string.Join("\n", new string[]
        {
            "List of discovered mdns services",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841",
            "adb-9A8B7C6D5E4F3G2H-pair\t_adb-tls-pairing._tcp\t192.168.3.8:41234"
        });
        ScanResult res = AdbScanner.ParseAndMerge("List of devices attached\n", mdns);

        Assert(res.PairingServices.Count == 1 && res.PairingServices[0].Endpoint == "192.168.3.8:41234",
            "pairing service extracted and excluded from device list");
        Assert(res.Devices.Count == 1, "duplicate mDNS entries with (2)/(3) merged into 1 device");
        DeviceItem dev = res.Devices[0];
        Assert(dev.Channel == ChannelKind.MdnsPending, "unconnected mDNS marked as MdnsPending");
        Assert(dev.HardwareSerial == "9A8B7C6D5E4F3G2H", "hardware serial extracted from mDNS instance");
        Assert(dev.CandidateEndpoints.Count == 2
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
        Assert(trailRes.PairingServices.Count == 1 && trailRes.PairingServices[0].Endpoint == "192.168.3.20:41200",
            "ParseMdns handles trailing dot in pairing service type");
        Assert(trailRes.Devices.Count == 1
            && trailRes.Devices[0].Channel == ChannelKind.MdnsPending
            && trailRes.Devices[0].CandidateEndpoints[0] == "192.168.3.20:39001",
            "ParseMdns handles trailing dot in connect service type");
    }

    static void TestDualChannelUsbAndWireless()
    {
        string devices = string.Join("\n", new string[]
        {
            "List of devices attached",
            "9A8B7C6D5E4F3G2H       device product:pad_x model:Pad_X device:pad_x transport_id:1",
            "192.168.3.8:35841      device product:pad_x model:Pad_X device:pad_x transport_id:2"
        });
        string mdns = "adb-9A8B7C6D5E4F3G2H-YzPHtO (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841\n";

        ScanResult res = AdbScanner.ParseAndMerge(devices, mdns);
        Assert(res.Devices.Count == 2, "USB and Wireless channels preserved separately");
        Assert(res.Devices[0].Channel == ChannelKind.Usb && res.Devices[0].AdbSerial == "9A8B7C6D5E4F3G2H", "USB channel ranked first");
        Assert(res.Devices[1].Channel == ChannelKind.WirelessConnected
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
        Assert(offlineUsbRes.Devices.Count == 2, "offline USB device stays isolated from wireless channel");
        Assert(offlineUsbRes.Devices[1].Channel == ChannelKind.Offline && offlineUsbRes.Devices[1].AdbSerial == "USBDEV001",
            "offline USB serial is preserved instead of promoted to MdnsPending");
    }

    static void TestConfigResolution()
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

        DeviceItem tablet = new DeviceItem();
        tablet.HardwareSerial = "9A8B7C6D5E4F3G2H";
        tablet.AdbSerial = "192.168.3.8:35841";
        ResolvedDeviceConfig r1 = ConfigManager.Resolve(cfg, tablet);
        Assert(r1.Alias == "平板" && r1.HasCustomArgs && r1.Args.Count == 1 && r1.Args[0] == "--max-size=1600",
            "case-insensitive match by hardware serial resolves alias and custom args");

        DeviceItem backup = new DeviceItem();
        backup.HardwareSerial = "OTHER";
        ResolvedDeviceConfig r2 = ConfigManager.Resolve(cfg, backup);
        Assert(r2.Alias == "备用机" && !r2.HasCustomArgs && r2.Args.Count == 1 && r2.Args[0] == "--stay-awake",
            "empty device args falls back to globalArgs");

        DeviceItem mixed = new DeviceItem();
        mixed.HardwareSerial = "MIXED01";
        mixed.AdbSerial = "192.168.3.50:5555";
        ResolvedDeviceConfig r3 = ConfigManager.Resolve(cfg, mixed);
        Assert(r3.Alias == "混合设备" && r3.HasCustomArgs && r3.Args.Count == 1 && r3.Args[0] == "--max-fps=60",
            "Resolve merges args from hardware serial and alias from IP endpoint");
    }

    static void TestQuoteArg()
    {
        Assert(DeviceRunner.QuoteArg("--stay-awake") == "--stay-awake", "simple arg stays unquoted");
        Assert(DeviceRunner.QuoteArg("--window-title=My Phone") == "\"--window-title=My Phone\"", "arg with space quoted");
        Assert(DeviceRunner.QuoteArg("a\"b") == "\"a\\\"b\"", "embedded quote escaped");
    }

    static void TestTerminalCommands()
    {
        string wtShell = DeviceRunner.BuildWtTerminalArguments("adb", "pwsh.exe", "192.168.3.12:5555", "客厅;机", @"C:\Tools\WinScrcpy\", TerminalMode.AdbShell);
        Assert(wtShell.Contains("-w 0 new-tab --title \"客厅 机 - ADB Shell\" -d C:\\Tools\\WinScrcpy -- adb -s 192.168.3.12:5555 shell"),
            "wt AdbShell command targets active window tab and trims dir trailing backslash: " + wtShell);

        string wtLocal = DeviceRunner.BuildWtTerminalArguments(@"D:\SDK Tools\adb.exe", @"C:\Program Files\PowerShell\7\pwsh.exe", "USBDEV001", "Pixel 9", @"C:\Tools\WinScrcpy\", TerminalMode.LocalAdbEnv);
        string afterSep = wtLocal.Split(new string[] { " -- " }, StringSplitOptions.None)[1];
        Assert(afterSep.StartsWith("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo -NoExit -EncodedCommand ")
            && !afterSep.Contains(";"),
            "wt LocalAdbEnv launches pwsh7 with EncodedCommand and no unescaped semicolons: " + wtLocal);

        string psScript = DeviceRunner.BuildLocalPowerShellInitScript(@"D:\SDK Tools\adb.exe", "USBDEV001", "Pixel 9 - ADB 终端");
        Assert(psScript.Contains("$env:ANDROID_SERIAL = 'USBDEV001'")
            && psScript.Contains("$env:PATH = 'D:\\SDK Tools;' + $env:PATH")
            && psScript.Contains("function global:adb { & 'D:\\SDK Tools\\adb.exe' @args }"),
            "pwsh7 init script sets ANDROID_SERIAL, PATH, and custom adb function: " + psScript);

        string standaloneLocal = DeviceRunner.BuildStandalonePsTerminalArguments("adb", "192.168.3.12:5555", "Test & Box", TerminalMode.LocalAdbEnv);
        Assert(standaloneLocal.StartsWith("-NoLogo -NoExit -EncodedCommand "),
            "standalone LocalAdbEnv uses -NoLogo -NoExit -EncodedCommand");
        int b64Idx = standaloneLocal.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length;
        string decodedLocal = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(standaloneLocal.Substring(b64Idx)));
        Assert(decodedLocal.Contains("$env:ANDROID_SERIAL = '192.168.3.12:5555'")
            && !decodedLocal.Contains("function global:adb"),
            "standalone decoded script binds ANDROID_SERIAL without overriding bare adb: " + decodedLocal);

        string standaloneShell = DeviceRunner.BuildStandalonePsTerminalArguments("adb", "192.168.3.12:5555", "Box", TerminalMode.AdbShell);
        Assert(standaloneShell.StartsWith("-NoLogo -EncodedCommand ") && !standaloneShell.Contains("-NoExit"),
            "standalone AdbShell exits cleanly on exit 0");
    }

    static void TestMdnsStaleCacheFilter()
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

        AdbScanner.FilterReachableMdns(res, delegate(string ep)
        {
            return ep == "192.168.3.8:38081";
        });

        Assert(res.FilteredStaleMdnsCount == 1, "fully unreachable mDNS device counted and filtered");
        Assert(res.Devices.Count == 2, "USB and partially reachable mDNS devices remain");
        Assert(res.Devices[0].Channel == ChannelKind.Usb, "USB device unaffected by mDNS probe");
        Assert(res.Devices[1].Channel == ChannelKind.MdnsPending
            && res.Devices[1].CandidateEndpoints.Count == 1
            && res.Devices[1].CandidateEndpoints[0] == "192.168.3.8:38081",
            "unreachable primary port pruned while reachable fallback port kept");
    }

    static void TestTryParseEndpoint()
    {
        string host;
        int port;
        Assert(DeviceRunner.TryParseEndpoint("192.168.3.8:35841", out host, out port)
            && host == "192.168.3.8" && port == 35841, "IPv4 endpoint parsed");
        Assert(DeviceRunner.TryParseEndpoint("[fe80::1]:5555", out host, out port)
            && host == "fe80::1" && port == 5555, "bracketed IPv6 endpoint parsed");
        Assert(!DeviceRunner.TryParseEndpoint("invalid-port:99999", out host, out port), "out-of-range port rejected");
    }

    static void TestBuildDisconnectTargets()
    {
        DeviceItem mDnsConnected = new DeviceItem();
        mDnsConnected.Channel = ChannelKind.WirelessConnected;
        mDnsConnected.AdbSerial = "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp";
        mDnsConnected.CandidateEndpoints.Add("192.168.3.8:46787");

        List<string> targets = DeviceRunner.BuildDisconnectTargets(mDnsConnected);
        Assert(targets.Count == 2
            && targets[0] == "192.168.3.8:46787"
            && targets[1] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp",
            "disconnect prioritizes IP:Port before mDNS TLS serial");
    }

    static void TestDuplicateWirelessMerged()
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
        Assert(res.Devices.Count == 1, "duplicate ip:port and mDNS TLS connections for same device merged into 1 entry");
        DeviceItem item = res.Devices[0];
        Assert(item.Channel == ChannelKind.WirelessConnected, "merged item stays WirelessConnected");
        Assert(item.AdbSerial == "192.168.3.8:46787", "ip:port chosen as primary serial over long mDNS serial");
        Assert(item.AlternateSerials.Count == 1
            && item.AlternateSerials[0] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp",
            "mDNS TLS serial kept in AlternateSerials for complete disconnect");
        Assert(DeviceRunner.BuildDisconnectTargets(item).Count == 4 || DeviceRunner.BuildDisconnectTargets(item).Count >= 2,
            "disconnect targets include both handles");
    }

    static void TestDuplicateWirelessWithoutMdns()
    {
        string devices = string.Join("\n", new string[]
        {
            "List of devices attached",
            "192.168.3.8:46787      device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:3",
            "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp device product:phone_z model:Phone_Z device:Phone_Z_01 transport_id:4"
        });
        ScanResult res = AdbScanner.ParseAndMerge(devices, "List of discovered mdns services\n");
        Assert(res.Devices.Count == 2, "before enrichment ip:port lacks hardware serial");

        AdbScanner.EnrichAndDeduplicate(res, delegate(DeviceItem dev)
        {
            return dev.AdbSerial == "192.168.3.8:46787" ? "9A8B7C6D5E4F3G2H" : "";
        });

        Assert(res.Devices.Count == 1, "after hardware serial enrichment duplicate wireless entries merge into 1");
        Assert(res.Devices[0].AdbSerial == "192.168.3.8:46787", "ip:port kept as primary");
        Assert(res.Devices[0].HardwareSerial == "9A8B7C6D5E4F3G2H", "hardware serial retained");
        Assert(res.Devices[0].AlternateSerials.Count == 1
            && res.Devices[0].AlternateSerials[0] == "adb-9A8B7C6D5E4F3G2H-YzPHtO._adb-tls-connect._tcp",
            "mDNS TLS handle stored in AlternateSerials");
    }

    static void TestUiLayoutNoClipping()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppConfig cfg = AppConfig.CreateDefault();
        ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_test_noop.json"));

        using (MainForm form = new MainForm(mgr, cfg, null, false))
        {
            ShowOffscreen(form);

            // Round 1: Empty state layout (including statusBar / statusLabel).
            form.LoadScanResultForTest(new ScanResult());
            Application.DoEvents();
            Assert(form.ActiveTableCard == null, "no table card when empty");
            Assert(!form.ConnectSelectedButton.Visible, "batch mirror hidden when nothing selected");
            AssertAllControlsInTreeUnclipped(form);

            // Round 2: Populated devices layout (single grouped inset table card).
            form.LoadScanResultForTest(BuildMockScanResult());
            Application.DoEvents();
            Assert(form.ActiveTableCard != null, "grouped inset table card created");
            Assert(form.DeviceListHost.Controls.Count == 1 && form.DeviceListHost.Controls[0] == form.ActiveTableCard,
                "device list hosts one unified grouped table card");
            Assert(!form.ConnectSelectedButton.Visible, "batch mirror stays hidden until >= 1 device selected");
            Assert(form.MoreMenu.Items.Count == 4
                && form.MoreMenu.Items[0].Text == "管理常用地址…"
                && form.MoreMenu.Items[1] is ToolStripSeparator
                && form.MoreMenu.Items[2].Text == "断开全部无线连接"
                && form.MoreMenu.Items[3].Text == "重启 ADB 服务",
                "overflow menu contains manage saved, separator, wireless disconnect and restart adb");
            AssertAllControlsInTreeUnclipped(form);
            Assert(!form.DeviceListHost.HorizontalScroll.Visible, "no horizontal scrollbar in device list");

            // Toggle select-all checkbox checks enabled rows and dynamically shows contextual batch mirror button.
            form.SelectAllCheckBox.Checked = true;
            Application.DoEvents();
            Assert(form.ConnectSelectedButton.Visible && form.ConnectSelectedButton.Text == "投屏所选 (2)",
                "selecting all shows contextual batch mirror button with count");
            AssertAllControlsInTreeUnclipped(form);

            List<CheckBox> rowChecks = new List<CheckBox>();
            for (int i = 0; i < form.ActiveTableCard.Controls.Count; i++)
            {
                if (form.ActiveTableCard.Controls[i].Tag is DeviceItem)
                {
                    CollectControls(form.ActiveTableCard.Controls[i], rowChecks);
                }
            }
            Assert(rowChecks.Count == 2 && rowChecks[0].Checked && rowChecks[1].Checked, "select-all checks all enabled rows");
            rowChecks[0].Checked = false;
            Application.DoEvents();
            Assert(!form.SelectAllCheckBox.Checked, "unchecking one row clears select-all");
            Assert(form.ConnectSelectedButton.Visible && form.ConnectSelectedButton.Text == "投屏所选 (1)",
                "unchecking one row updates batch count to 1");
            rowChecks[1].Checked = false;
            Application.DoEvents();
            Assert(!form.ConnectSelectedButton.Visible, "unchecking last row hides batch button again");

            bool checkedNoDuplicateSn = false;
            bool checkedUsbHasTwoButtons = false;
            bool checkedWirelessHasThreeButtons = false;
            for (int i = 0; i < form.ActiveTableCard.Controls.Count; i++)
            {
                Control row = form.ActiveTableCard.Controls[i];
                DeviceItem item = row.Tag as DeviceItem;
                if (item == null) continue;
                List<Button> buttons = new List<Button>();
                CollectControls(row, buttons);
                if (item.Channel == ChannelKind.Usb)
                {
                    Assert(buttons.Count == 2 && buttons[0].Text == "截屏" && buttons[1].Text == "投屏",
                        "USB row shows 截屏 and 投屏 buttons");
                    Assert(row.ContextMenuStrip != null
                        && row.ContextMenuStrip.Items.Count == 2
                        && row.ContextMenuStrip.Items[0].Text == "进入 ADB Shell"
                        && row.ContextMenuStrip.Items[1].Text == "打开本地 ADB 终端",
                        "connectable row attaches terminal context menu");
                    checkedUsbHasTwoButtons = true;
                }
                if (item.HardwareSerial == "9A8B7C6D5E4F3G2H")
                {
                    Assert(buttons.Count == 3 && buttons[0].Text == "连接" && buttons[1].Text == "截屏" && buttons[2].Text == "投屏",
                        "mDNS pending row shows 连接, 截屏 and 投屏 buttons");
                    checkedWirelessHasThreeButtons = true;
                    List<SelectableTextLabel> selectableTexts = new List<SelectableTextLabel>();
                    CollectControls(row, selectableTexts);
                    Assert(selectableTexts.Count == 2 && selectableTexts[0].ReadOnly && selectableTexts[1].ReadOnly,
                        "device row title and subtitle are selectable read-only text boxes");
                    for (int k = 0; k < selectableTexts.Count; k++)
                    {
                        Assert(!selectableTexts[k].Text.Contains("SN: 9A8B7C6D5E4F3G2H"),
                            "subtitle must not repeat SN when title already shows HardwareSerial");
                    }
                    checkedNoDuplicateSn = true;
                }
            }
            Assert(checkedNoDuplicateSn && checkedUsbHasTwoButtons && checkedWirelessHasThreeButtons,
                "verified device row button variants and duplicate SN suppression");

            // Maximize / expand and restore / shrink must not leave horizontal or spurious scrollbars.
            Size originalSize = form.Size;
            form.Size = new Size(Math.Max(originalSize.Width + 600, 1800), Math.Max(originalSize.Height + 400, 1000));
            Application.DoEvents();
            form.Size = originalSize;
            Application.DoEvents();
            Assert(!form.DeviceListHost.HorizontalScroll.Visible,
                "no horizontal scrollbar after expand and shrink (card.Bounds=" + form.ActiveTableCard.Bounds
                + ", clientSize=" + form.DeviceListHost.ClientSize
                + ", displayRect=" + form.DeviceListHost.DisplayRectangle
                + ", scrollPos=" + form.DeviceListHost.AutoScrollPosition
                + ", hMax=" + form.DeviceListHost.HorizontalScroll.Maximum + ")");
            Assert(!form.DeviceListHost.VerticalScroll.Visible,
                "no vertical scrollbar after expand and shrink (card.Bottom=" + form.ActiveTableCard.Bottom + ", clientHeight=" + form.DeviceListHost.ClientSize.Height + ")");

            // Also verify centering of child dialogs against MainForm owner.
            using (PairDialog centeredPair = new PairDialog(new DeviceRunner(cfg), null))
            {
                centeredPair.SetPollingEnabledForTest(false);
                centeredPair.Show(form);
                Application.DoEvents();
                int expectedX = form.Left + (form.Width - centeredPair.Width) / 2;
                int expectedY = form.Top + (form.Height - centeredPair.Height) / 2;
                Assert(Math.Abs(centeredPair.Left - expectedX) <= 2 && Math.Abs(centeredPair.Top - expectedY) <= 2,
                    "PairDialog centers on owner (actual=" + centeredPair.Location + ", expected=" + expectedX + "," + expectedY + ")");
                centeredPair.Close();
            }

            using (ManualConnectDialog centeredConnect = new ManualConnectDialog(new DeviceRunner(cfg)))
            {
                centeredConnect.Show(form);
                Application.DoEvents();
                int expectedX = form.Left + (form.Width - centeredConnect.Width) / 2;
                int expectedY = form.Top + (form.Height - centeredConnect.Height) / 2;
                Assert(Math.Abs(centeredConnect.Left - expectedX) <= 2 && Math.Abs(centeredConnect.Top - expectedY) <= 2,
                    "ManualConnectDialog centers on owner (actual=" + centeredConnect.Location + ", expected=" + expectedX + "," + expectedY + ")");
                centeredConnect.Close();
            }

            using (SavedEndpointsDialog centeredSaved = new SavedEndpointsDialog(null, cfg))
            {
                centeredSaved.Show(form);
                Application.DoEvents();
                int expectedX = form.Left + (form.Width - centeredSaved.Width) / 2;
                int expectedY = form.Top + (form.Height - centeredSaved.Height) / 2;
                Assert(Math.Abs(centeredSaved.Left - expectedX) <= 2 && Math.Abs(centeredSaved.Top - expectedY) <= 2,
                    "SavedEndpointsDialog centers on owner (actual=" + centeredSaved.Location + ", expected=" + expectedX + "," + expectedY + ")");
                centeredSaved.Close();
            }
            // Verify SavedPending row renders non-empty endpoint title and action buttons
            ScanResult savedScan = new ScanResult();
            AdbScanner.MergeReachableSavedEndpoints(savedScan, new string[] { "192.168.3.12:5555" });
            form.LoadScanResultForTest(savedScan);
            Application.DoEvents();
            List<SelectableTextLabel> savedTexts = new List<SelectableTextLabel>();
            CollectControls(form.ActiveTableCard, savedTexts);
            Control savedRow = null;
            for (int r = 0; r < form.ActiveTableCard.Controls.Count; r++)
            {
                if (form.ActiveTableCard.Controls[r].Tag is DeviceItem) { savedRow = form.ActiveTableCard.Controls[r]; break; }
            }
            Control infoTbl = savedRow.Controls[1];
            TableLayoutPanel infoTlp = infoTbl as TableLayoutPanel;
            Assert(infoTlp != null && infoTlp.Height <= infoTlp.Controls[0].Height + infoTlp.Controls[1].Height + UiTheme.Scale(8),
                "SavedPending row must not leave extra vertical gap below subtitle (infoHeight=" + (infoTlp != null ? infoTlp.Height : 0) + ")");
            Assert(savedTexts.Count == 2 && savedTexts[0].Text == "192.168.3.12:5555"
                && !savedTexts[1].Text.Contains("192.168.3.12:5555"),
                "SavedPending row shows endpoint as primary title without duplicating in subtitle");
            AssertAllControlsInTreeUnclipped(form);

            // Verify wireless Offline row exposes 连接 and 断开 buttons while keeping 截屏 and 投屏 disabled
            ScanResult offlineScan = AdbScanner.ParseAndMerge("List of devices attached\n192.168.3.12:5555      offline product:dev_a model:Model_A transport_id:7\n", "");
            form.LoadScanResultForTest(offlineScan);
            Application.DoEvents();
            Control offlineRow = null;
            for (int r = 0; r < form.ActiveTableCard.Controls.Count; r++)
            {
                if (form.ActiveTableCard.Controls[r].Tag is DeviceItem) { offlineRow = form.ActiveTableCard.Controls[r]; break; }
            }
            List<Button> offlineBtns = new List<Button>();
            CollectControls(offlineRow, offlineBtns);
            Assert(offlineBtns.Count == 4
                && offlineBtns[0].Text == "连接" && offlineBtns[0].Enabled
                && offlineBtns[1].Text == "断开" && offlineBtns[1].Enabled
                && offlineBtns[2].Text == "截屏" && !offlineBtns[2].Enabled
                && offlineBtns[3].Text == "投屏" && !offlineBtns[3].Enabled,
                "wireless Offline row allows 连接 and 断开 while keeping 截屏 and 投屏 disabled");
            AssertAllControlsInTreeUnclipped(form);
            form.Close();
        }

        // Round 3: Wireless pair dialog (both QR scan and pairing-code tabs) + manual connect dialog.
        using (PairDialog dlg = new PairDialog(new DeviceRunner(cfg), null))
        {
            dlg.SetPollingEnabledForTest(false);
            ShowOffscreen(dlg);
            Application.DoEvents();
            Size qrTabSize = dlg.Size;
            AssertAllControlsInTreeUnclipped(dlg);

            dlg.SelectTabForTest(false);
            Application.DoEvents();
            Assert(dlg.Size == qrTabSize, "PairDialog maintains constant size across tab switch (qr=" + qrTabSize + ", code=" + dlg.Size + ")");
            int gapTabToTip;
            int gapTipToInput;
            dlg.GetCodeSpacingForTest(out gapTabToTip, out gapTipToInput);
            Assert(Math.Abs(gapTabToTip - gapTipToInput) <= UiTheme.Scale(2),
                "PairDialog code tab equalizes Tab->Tip (" + gapTabToTip + ") and Tip->Input (" + gapTipToInput + ") spacing");
            AssertAllControlsInTreeUnclipped(dlg);
            dlg.Close();
        }

        using (ManualConnectDialog connectDlg = new ManualConnectDialog(new DeviceRunner(cfg)))
        {
            ShowOffscreen(connectDlg);
            Application.DoEvents();
            AssertAllControlsInTreeUnclipped(connectDlg);

            // Mutual exclusion between full IP:Port input and 192.168 quick boxes
            connectDlg.fullEndpointHost.TextValue = "10.0.0.5:5555";
            connectDlg.subnetHost.TextValue = "3";
            Assert(connectDlg.fullEndpointHost.TextValue == "", "editing quick segment clears full endpoint box");
            connectDlg.hostOctetHost.TextValue = "8";
            connectDlg.fullEndpointHost.TextValue = "192.168.1.9:37000";
            Assert(connectDlg.subnetHost.TextValue == "" && connectDlg.hostOctetHost.TextValue == ""
                && connectDlg.portHost.TextValue == "5555",
                "editing full endpoint clears quick IP octets while keeping port default");
            connectDlg.Close();
        }

        // Round 4: Saved endpoints manager table dialog (add, edit, deduplicate, remove, persist).
        string tmpSavedPath = Path.Combine(Path.GetTempPath(), "winscrcpy_saved_dlg_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AppConfig savedDlgCfg = AppConfig.CreateDefault();
            savedDlgCfg.savedEndpoints.Add("192.168.3.12:5555");
            ConfigManager.SetDeviceAlias(savedDlgCfg, "192.168.3.12:5555", "旧名字");
            DeviceConfig customArgsEntry = new DeviceConfig();
            customArgsEntry.alias = "自定义机";
            customArgsEntry.args.Add("--max-size=1280");
            savedDlgCfg.savedEndpoints.Add("192.168.3.20:5555");
            savedDlgCfg.devices["192.168.3.20:5555"] = customArgsEntry;

            ConfigManager savedDlgMgr = new ConfigManager(tmpSavedPath);
            using (SavedEndpointsDialog savedDlg = new SavedEndpointsDialog(savedDlgMgr, savedDlgCfg))
            {
                ShowOffscreen(savedDlg);
                Application.DoEvents();
                AssertAllControlsInTreeUnclipped(savedDlg);
                Assert(savedDlg.grid.Rows.Count == 2
                    && Convert.ToString(savedDlg.grid.Rows[0].Cells[0].Value) == "192.168.3.12:5555"
                    && Convert.ToString(savedDlg.grid.Rows[0].Cells[1].Value) == "旧名字",
                    "SavedEndpointsDialog loads savedEndpoints and aliases");

                int minCellH = TextRenderer.MeasureText("192.168.3.12:5555", UiTheme.BodyFont).Height;
                Assert(savedDlg.grid.RowTemplate.Height > minCellH
                    && savedDlg.grid.ColumnHeadersHeight > TextRenderer.MeasureText("地址", UiTheme.BodyBoldFont).Height,
                    "DataGridView header and row heights exceed rendered font height");

                // Reject duplicate endpoints
                savedDlg.grid.Rows.Add("192.168.3.12", "重复行");
                Assert(!savedDlg.TrySaveFromGrid() && !savedDlg.Saved, "duplicate normalized endpoint rejected");

                // Remove duplicate row + remove second row (192.168.3.20:5555) + add valid bare IP
                savedDlg.grid.Rows.RemoveAt(2);
                savedDlg.grid.ClearSelection();
                savedDlg.grid.Rows[1].Cells[0].Selected = true;
                savedDlg.RemoveSelectedRows();
                savedDlg.grid.Rows[0].Cells[1].Value = "新客厅";
                savedDlg.grid.Rows.Add("192.168.3.88", "卧室");

                Assert(savedDlg.TrySaveFromGrid() && savedDlg.Saved, "valid table rows save cleanly");
                savedDlg.Close();
            }

            string reloadWarn;
            AppConfig reloadedSaved = savedDlgMgr.LoadOrCreate(out reloadWarn);
            Assert(string.IsNullOrEmpty(reloadWarn)
                && reloadedSaved.savedEndpoints.Count == 2
                && reloadedSaved.savedEndpoints[0] == "192.168.3.12:5555"
                && reloadedSaved.savedEndpoints[1] == "192.168.3.88:5555"
                && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.12:5555") == "新客厅"
                && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.88:5555") == "卧室"
                && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.20:5555") == ""
                && reloadedSaved.devices["192.168.3.20:5555"].args.Count == 1,
                "SavedEndpointsDialog persists endpoints/aliases and preserves args on removed row");
        }
        finally
        {
            try { if (File.Exists(tmpSavedPath)) File.Delete(tmpSavedPath); }
            catch { }
        }
    }

    static void TestQrEncoderAndPairingMatch()
    {
        bool[,] qr = QrEncoder.Encode("WIFI:T:ADB;S:winscrcpy-abc123;P:secret99;;");
        int n = qr.GetLength(0);
        Assert(n == qr.GetLength(1) && n >= 21 && (n - 17) % 4 == 0, "QR matrix has valid symbol size");
        // Verify 3x3 solid center + outer dark border of top-left finder
        Assert(qr[3, 3] && qr[2, 2] && qr[4, 4] && !qr[1, 1] && qr[0, 0], "top-left finder pattern valid");
        Assert(qr[3, n - 4] && !qr[1, n - 2] && qr[0, n - 1], "top-right finder pattern valid");
        Assert(qr[n - 4, 3] && !qr[n - 2, 1] && qr[n - 1, 0], "bottom-left finder pattern valid");
        Assert(qr[n - 8, 8], "dark module set at (size - 8, 8)");
        Assert(qr[6, 8] && !qr[6, 9], "horizontal timing pattern alternates");

        using (Bitmap bmp = QrEncoder.RenderBitmap(qr, 4, 2))
        {
            Assert(bmp.Width == (n + 4) * 4 && bmp.Height == bmp.Width, "QR bitmap dimensions match quiet zone + modules");
        }

        string mdns = "List of discovered mdns services\nwinscrcpy-abc123 (2)\t_adb-tls-pairing._tcp\t192.168.3.8:37123\n";
        List<PairingServiceInfo> list = AdbScanner.ParsePairingServices(mdns);
        Assert(list.Count == 1 && list[0].Endpoint == "192.168.3.8:37123", "ParsePairingServices extracts pairing endpoint");
        PairingServiceInfo matched = PairDialog.FindMatchingPairingService(list, "winscrcpy-abc123");
        Assert(matched != null && matched.Endpoint == "192.168.3.8:37123", "QR instance matcher handles suffixed mDNS names");
        Assert(ManualConnectDialog.BuildEndpointFromInputs("10.0.0.5:5555", "", "", "5555") == "10.0.0.5:5555",
            "full endpoint field preserved when filled");
        Assert(ManualConnectDialog.BuildEndpointFromInputs("", "3", "8", "5555") == "192.168.3.8:5555",
            "quick 192.168 row builds full IP:Port");
        Assert(ManualConnectDialog.BuildEndpointFromInputs("", "3", "", "5555") == "",
            "incomplete quick IP segment rejected");

        // Saved endpoints persistence, normalization, MRU dedupe, and scan merge
        Assert(ConfigManager.NormalizeEndpoint("192.168.3.12") == "192.168.3.12:5555",
            "bare IP defaults to :5555");
        Assert(ConfigManager.NormalizeEndpoint("fe80::1") == "[fe80::1]:5555"
            && ConfigManager.NormalizeEndpoint("[fe80::1]") == "[fe80::1]:5555",
            "bare IPv6 defaults to :5555");
        AppConfig roundCfg = AppConfig.CreateDefault();
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.12");
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.15:5556");
        ConfigManager.AddSavedEndpoint(roundCfg, "192.168.3.12:5555");
        ConfigManager.SetDeviceAlias(roundCfg, "CTRL01", "带\t制表\n换行\"测试\\别名");
        Assert(roundCfg.savedEndpoints.Count == 2
            && roundCfg.savedEndpoints[0] == "192.168.3.12:5555"
            && roundCfg.savedEndpoints[1] == "192.168.3.15:5556",
            "savedEndpoints deduplicated and sorted by most recent use");
        string tmpCfgPath = Path.Combine(Path.GetTempPath(), "winscrcpy_test_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ConfigManager tmpMgr = new ConfigManager(tmpCfgPath);
            tmpMgr.Save(roundCfg);
            string warn;
            AppConfig reloaded = tmpMgr.LoadOrCreate(out warn);
            Assert(string.IsNullOrEmpty(warn)
                && reloaded.savedEndpoints.Count == 2
                && reloaded.savedEndpoints[0] == "192.168.3.12:5555"
                && ConfigManager.GetDeviceAlias(reloaded, "CTRL01") == "带\t制表\n换行\"测试\\别名",
                "savedEndpoints and escaped control-char alias persist cleanly across JSON save/reload");
        }
        finally
        {
            try { if (File.Exists(tmpCfgPath)) File.Delete(tmpCfgPath); }
            catch { }
        }

        ScanResult mergeRes = AdbScanner.ParseAndMerge("List of devices attached\n", "List of discovered mdns services\nadb-ABC-1\t_adb-tls-connect._tcp\t192.168.3.8:35841\n");
        AdbScanner.MergeReachableSavedEndpoints(mergeRes, new string[] { "192.168.3.8:35841", "192.168.3.12:5555" });
        Assert(mergeRes.Devices.Count == 2
            && mergeRes.Devices[0].Channel == ChannelKind.MdnsPending
            && mergeRes.Devices[1].Channel == ChannelKind.SavedPending
            && mergeRes.Devices[1].CandidateEndpoints[0] == "192.168.3.12:5555",
            "reachable saved 5555 endpoint merges without duplicating mDNS entries");

        AppConfig aliasCfg = AppConfig.CreateDefault();
        DeviceConfig dc = new DeviceConfig();
        dc.alias = "客厅盒子";
        aliasCfg.devices["192.168.3.12:5555"] = dc;
        ResolvedDeviceConfig savedResolved = ConfigManager.Resolve(aliasCfg, mergeRes.Devices[1]);
        Assert(savedResolved.Alias == "客厅盒子", "SavedPending resolves custom alias by endpoint key");

        // Stale offline wireless entry in adb devices should still be probed and promoted when endpoint is reachable
        ScanResult staleOffline = AdbScanner.ParseAndMerge("List of devices attached\n192.168.3.12:5555      offline product:dev_a model:Model_A transport_id:7\n", "");
        Assert(staleOffline.Devices.Count == 1 && staleOffline.Devices[0].Channel == ChannelKind.Offline,
            "raw adb offline line starts as Offline");
        Assert(!AdbScanner.HasOnlineOrPendingDeviceForEndpoint(staleOffline.Devices, "192.168.3.12:5555"),
            "Offline saved endpoint must still be probed for reachability");
        AdbScanner.MergeReachableSavedEndpoints(staleOffline, new string[] { "192.168.3.12:5555" });
        Assert(staleOffline.Devices.Count == 1
            && staleOffline.Devices[0].Channel == ChannelKind.SavedPending
            && staleOffline.Devices[0].Model == "Model A"
            && staleOffline.Devices[0].AdbSerial == ""
            && staleOffline.Devices[0].CandidateEndpoints[0] == "192.168.3.12:5555",
            "reachable saved Offline entry promotes to SavedPending while preserving model name");
    }

    static void AssertAllControlsInTreeUnclipped(Control root)
    {
        for (int i = 0; i < root.Controls.Count; i++)
        {
            Control child = root.Controls[i];
            if (!child.Visible) continue;
            if (child is Button || child is Label || child is TextBox || child is CheckBox || child is RoundedPanel)
            {
                AssertControlInsideAndUnclipped(child, string.IsNullOrEmpty(child.Text) ? child.GetType().Name : child.Text);
            }
            if (child.Controls.Count > 0)
            {
                AssertAllControlsInTreeUnclipped(child);
            }
        }
    }

    static void AssertControlInsideAndUnclipped(Control ctrl, string label)
    {
        if (ctrl is Label || ctrl is Button || ctrl is TextBox || ctrl is CheckBox)
        {
            Size pref = ctrl.PreferredSize;
            Assert(ctrl.Width >= pref.Width && ctrl.Height >= pref.Height,
                string.Format("control [{0}] clipped: actual={1}x{2}, preferred={3}x{4}",
                    label, ctrl.Width, ctrl.Height, pref.Width, pref.Height));
        }

        if (ctrl.Parent != null)
        {
            Size pSize = ctrl.Parent.ClientSize;
            Assert(ctrl.Left >= 0 && ctrl.Top >= 0 && ctrl.Right <= pSize.Width && ctrl.Bottom <= pSize.Height,
                string.Format("control [{0}] out of parent bounds: rect=({1},{2},{3},{4}), parent={5}x{6}",
                    label, ctrl.Left, ctrl.Top, ctrl.Right, ctrl.Bottom, pSize.Width, pSize.Height));
        }
    }

    static void CollectControls<T>(Control root, List<T> list) where T : Control
    {
        for (int i = 0; i < root.Controls.Count; i++)
        {
            Control c = root.Controls[i];
            T typed = c as T;
            if (typed != null) list.Add(typed);
            if (c.Controls.Count > 0) CollectControls(c, list);
        }
    }

    static void TestToolPathResolution()
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

            // 1. 显式配置有效路径 -> 返回完整绝对路径
            ToolResolutionResult r1 = ToolPathResolver.ResolveTool(explicitScrcpy, "scrcpy", appDir, fakePath);
            Assert(r1.Success && string.Equals(r1.ResolvedPath, Path.GetFullPath(explicitScrcpy), StringComparison.OrdinalIgnoreCase),
                "explicit valid path with spaces & Chinese should resolve");

            // 2. 显式配置无效路径 -> 必须报错，不能静默回退到 appDir 或 PATH
            string missingExplicit = Path.Combine(explicitDir, "missing-adb.exe");
            ToolResolutionResult r2 = ToolPathResolver.ResolveTool(missingExplicit, "adb", appDir, fakePath);
            Assert(!r2.Success && string.IsNullOrEmpty(r2.ResolvedPath) && r2.Error.Contains("missing-adb.exe"),
                "explicit missing path must fail without falling back");

            // 3. 默认名称 -> appDir 优先于 PATH
            ToolResolutionResult r3 = ToolPathResolver.ResolveTool("adb", "adb", appDir, fakePath);
            Assert(r3.Success && string.Equals(r3.ResolvedPath, Path.GetFullPath(appAdb), StringComparison.OrdinalIgnoreCase),
                "appDir should take precedence over PATH");

            // 4. appDir 不存在目标时 -> 从 PATH（含带引号和中文空格路径）解析
            ToolResolutionResult r4 = ToolPathResolver.ResolveTool("", "scrcpy", appDir, fakePath);
            Assert(r4.Success && string.Equals(r4.ResolvedPath, Path.GetFullPath(pathScrcpy), StringComparison.OrdinalIgnoreCase),
                "PATH lookup should handle quoted dir with spaces and Chinese");

            // 5. 全部路径缺失 -> 返回失败
            ToolResolutionResult r5 = ToolPathResolver.ResolveTool("", "nonexistent-tool-xyz", appDir, fakePath);
            Assert(!r5.Success && !string.IsNullOrEmpty(r5.Error), "missing default tool should return error");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    static void TestProcessRunner()
    {
        string psExe = ToolPathResolver.FindExecutableInPath("powershell");
        Assert(!string.IsNullOrEmpty(psExe), "powershell.exe should exist in PATH for self tests");

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 进程测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "参数 与 状态.ps1");
        File.WriteAllText(scriptPath,
            "param([string]$mode)\r\n" +
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
            new UTF8Encoding(false));

        try
        {
            // 1. 空格、中文、脚本路径均通过 ArgumentList 精确传递
            CommandOutput echoOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "echo", "hello world", "中文 参数" },
                10000);
            Assert(echoOut.Success && echoOut.ExitCode == 0, "echo process should succeed: " + echoOut.GetFailureSummary("failed"));
            Assert(echoOut.StdOut.Contains("COUNT=2") && echoOut.StdOut.Contains("ARG=hello world") && echoOut.StdOut.Contains("ARG=中文 参数"),
                "ArgumentList should preserve spaces and Chinese: " + echoOut.StdOut);
            Assert(echoOut.StdErr.Contains("STDERR_中文"), "StdErr should preserve UTF-8 Chinese text");

            // 2. 非零退出码
            CommandOutput failOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "fail" },
                10000);
            Assert(!failOut.Success && !failOut.TimedOut && !failOut.Cancelled && failOut.ExitCode == 42,
                "non-zero exit code should be preserved");
            Assert(failOut.StdErr.Contains("boom_error"), "stderr should be captured on non-zero exit");

            // 3. 二进制 stdout
            CommandOutput binOut = ProcessRunner.RunBinary(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "binary" },
                10000);
            Assert(binOut.Success && binOut.StdOutBytes != null && binOut.StdOutBytes.Length == 6 &&
                   binOut.StdOutBytes[0] == 0x89 && binOut.StdOutBytes[4] == 0x00 && binOut.StdOutBytes[5] == 0xFF,
                "binary stdout should capture exact bytes including NUL and 0xFF");

            // 4. 超时终止
            Stopwatch swTimeout = Stopwatch.StartNew();
            CommandOutput timeoutOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "sleep" },
                250);
            swTimeout.Stop();
            Assert(!timeoutOut.Success && timeoutOut.TimedOut && !timeoutOut.Cancelled && swTimeout.ElapsedMilliseconds < 5000,
                "timeout should terminate child promptly");

            // 5. 外部取消
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
                Assert(!cancelOut.Success && cancelOut.Cancelled && !cancelOut.TimedOut && swCancel.ElapsedMilliseconds < 5000,
                    "cancellation should terminate child promptly");
            }

            // 6. 缺失可执行文件
            string missingExe = Path.Combine(root, "不存在的工具.exe");
            CommandOutput missingOut = ProcessRunner.RunText(missingExe, new string[] { "--version" }, 2000);
            Assert(!missingOut.Success && !string.IsNullOrEmpty(missingOut.LaunchError),
                "missing executable should populate LaunchError");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

    static void TestScrcpyEnvironmentAndInvalidConfig()
    {
        ProcessStartInfo psi = DeviceRunner.BuildScrcpyStartInfo(
            "C:\\工具 空格\\scrcpy.exe",
            "D:\\安卓 工具\\adb.exe",
            "192.168.1.5:5555",
            "我的手机 [无线]",
            new List<string> { "--max-size", "1280" });
        Assert(psi.FileName == "C:\\工具 空格\\scrcpy.exe", "scrcpy FileName mismatch");
        Assert(!psi.UseShellExecute, "UseShellExecute must be false to inject ADB env var");
        Assert(psi.Environment["ADB"] == "D:\\安卓 工具\\adb.exe", "ADB env var must point to resolved adb.exe");
        Assert(psi.Environment["ADB_MDNS_AUTO_CONNECT"] == "0", "ADB_MDNS_AUTO_CONNECT must be 0");
        Assert(psi.ArgumentList.Count == 5 &&
               psi.ArgumentList[0] == "-s" &&
               psi.ArgumentList[1] == "192.168.1.5:5555" &&
               psi.ArgumentList[2] == "--window-title=我的手机 [无线]" &&
               psi.ArgumentList[3] == "--max-size" &&
               psi.ArgumentList[4] == "1280",
            "scrcpy ArgumentList mismatch");

        AppConfig invalidCfg = new AppConfig();
        invalidCfg.adbPath = "C:\\不存在_WinScrcpy\\adb.exe";
        invalidCfg.scrcpyPath = "C:\\不存在_WinScrcpy\\scrcpy.exe";
        DeviceRunner runner = new DeviceRunner(invalidCfg);

        CommandOutput adbOut = runner.RunAdb(new string[] { "version" }, 2000);
        Assert(!adbOut.Success && adbOut.LaunchError.Contains("adb") && adbOut.LaunchError.Contains("不存在"),
            "invalid explicit adbPath must fail clearly: " + adbOut.LaunchError);

        string err;
        Assert(!runner.LaunchScrcpy("serial1", "测试设备", new List<string>(), out err) && err.Contains("scrcpy") && err.Contains("不存在"),
            "invalid explicit scrcpyPath must fail clearly: " + err);

        string launchedWith;
        Assert(!runner.OpenDeviceTerminal("serial1", "测试设备", TerminalMode.LocalAdbEnv, out launchedWith, out err) && err.Contains("adb") && err.Contains("不存在"),
            "terminal launcher must reject invalid explicit adbPath: " + err);
    }

    static void TestMainFormBusyRecovery()
    {
        string tempCfg = Path.Combine(Path.GetTempPath(), "WinScrcpy-ui-test-" + Guid.NewGuid().ToString("N") + ".json");
        ConfigManager manager = new ConfigManager(tempCfg);
        using (MainForm form = new MainForm(manager, new AppConfig(), null, false))
        {
            IntPtr handle = form.Handle;
            Assert(handle != IntPtr.Zero, "MainForm handle should be created");

            form.RunBusyActionForTest("模拟异常任务", delegate
            {
                throw new InvalidOperationException("模拟后台异常");
            }, "测试操作失败");
            Assert(form.IsBusyState, "MainForm should enter busy state immediately");

            Stopwatch sw = Stopwatch.StartNew();
            while (form.IsBusyState && sw.ElapsedMilliseconds < 3000)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }

            Assert(!form.IsBusyState, "MainForm must recover from busy state when async work throws");
        }
    }

    static void KillProcessByPidFile(string pidFile)
    {
        if (!File.Exists(pidFile)) return;
        try
        {
            string raw = File.ReadAllText(pidFile).Trim();
            int pid;
            if (int.TryParse(raw, out pid) && pid > 0)
            {
                using (Process child = Process.GetProcessById(pid))
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                        child.WaitForExit(1000);
                    }
                }
            }
        }
        catch (Exception) { }
    }

    static void TestProcessRunnerPipeHeldByChild()
    {
        string psExe = ToolPathResolver.FindExecutableInPath("powershell.exe");
        Assert(!string.IsNullOrEmpty(psExe), "powershell.exe should exist in PATH");

        string root = Path.Combine(Path.GetTempPath(), "WinScrcpy 管道测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "hold_pipe.ps1");
        string pidTextFile = Path.Combine(root, "pid_text.txt");
        string pidBinFile = Path.Combine(root, "pid_bin.txt");

        File.WriteAllText(scriptPath,
            "param([string]$mode, [string]$pidFile)\r\n" +
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
            new UTF8Encoding(false));

        try
        {
            // 1. 文本模式：父进程写出无换行文本并退出，子进程继续持有管道
            Stopwatch swText = Stopwatch.StartNew();
            CommandOutput textOut = ProcessRunner.RunText(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "text", pidTextFile },
                10000,
                null,
                null,
                CancellationToken.None,
                300);
            swText.Stop();
            KillProcessByPidFile(pidTextFile);

            Assert(swText.ElapsedMilliseconds < 5000, "text drain timeout should prevent hanging on child pipe");
            Assert(textOut.ExitCode == 0 && !textOut.TimedOut && !textOut.Cancelled,
                "parent exit should preserve ExitCode=0 without process timeout");
            Assert(textOut.OutputTimedOut && !textOut.Success && textOut.HasExecutionFailure,
                "incomplete pipe drain must set OutputTimedOut and fail Success");
            Assert(textOut.GetFailureSummary("fail").Contains("超时"),
                "failure summary should explain incomplete output drain");
            Assert(textOut.StdOut.Contains("NO_NEWLINE_中文输出"),
                "unterminated text chunk written before exit must be preserved: " + textOut.StdOut);

            // 2. 二进制模式：父进程写出 PNG 前缀字节并退出，子进程继续持有管道
            Stopwatch swBin = Stopwatch.StartNew();
            CommandOutput binOut = ProcessRunner.RunBinary(
                psExe,
                new string[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "binary", pidBinFile },
                10000,
                null,
                null,
                CancellationToken.None,
                300);
            swBin.Stop();
            KillProcessByPidFile(pidBinFile);

            Assert(swBin.ElapsedMilliseconds < 5000, "binary drain timeout should prevent hanging on child pipe");
            Assert(binOut.ExitCode == 0 && !binOut.TimedOut && !binOut.Cancelled,
                "parent exit in binary mode should preserve ExitCode=0");
            Assert(binOut.OutputTimedOut && !binOut.Success && binOut.HasExecutionFailure,
                "incomplete binary drain must set OutputTimedOut and fail Success");
            Assert(binOut.StdOutBytes != null && binOut.StdOutBytes.Length == 6 &&
                   binOut.StdOutBytes[0] == 0x89 && binOut.StdOutBytes[4] == 0x00 && binOut.StdOutBytes[5] == 0xFF,
                "partial binary bytes written before child held pipe must be preserved instead of null");
        }
        finally
        {
            KillProcessByPidFile(pidTextFile);
            KillProcessByPidFile(pidBinFile);
            try { Directory.Delete(root, true); }
            catch (Exception) { }
        }
    }

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

    static void TestProcessRunnerReadErrors()
    {
        CommandOutput textErrResult = new CommandOutput { ExitCode = 0 };
        using (PartialThrowingStream stdout = new PartialThrowingStream(Encoding.UTF8.GetBytes("部分文本")))
        using (MemoryStream stderr = new MemoryStream())
        {
            ProcessRunner.CaptureStreamsForTest(stdout, stderr, false, 1000, true, textErrResult);
        }
        Assert(!textErrResult.Success && textErrResult.HasExecutionFailure &&
               !string.IsNullOrWhiteSpace(textErrResult.OutputError) &&
               textErrResult.OutputError.Contains("simulated stream failure"),
            "text stream read exception must populate OutputError and fail Success");
        Assert(textErrResult.StdOut == "部分文本",
            "text prefix before stream error should be preserved: " + textErrResult.StdOut);

        CommandOutput binErrResult = new CommandOutput { ExitCode = 0 };
        using (PartialThrowingStream stdout = new PartialThrowingStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        using (MemoryStream stderr = new MemoryStream())
        {
            ProcessRunner.CaptureStreamsForTest(stdout, stderr, true, 1000, true, binErrResult);
        }
        Assert(!binErrResult.Success && binErrResult.HasExecutionFailure &&
               !string.IsNullOrWhiteSpace(binErrResult.OutputError) &&
               binErrResult.OutputError.Contains("simulated stream failure"),
            "binary stream read exception must populate OutputError and fail Success");
        Assert(binErrResult.StdOutBytes != null && binErrResult.StdOutBytes.Length == 4 &&
               binErrResult.StdOutBytes[0] == 0x89 && binErrResult.StdOutBytes[3] == 0x47,
            "binary prefix before stream error should be preserved");
    }
}
