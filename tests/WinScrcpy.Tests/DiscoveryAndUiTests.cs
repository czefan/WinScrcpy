using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace WinScrcpy.Tests;

public sealed class DiscoveryAndUiTests
{
    [Fact]
    public async Task ProbeEndpointsAsync_CapsConcurrencyAvoidsThreadExplosionAndHonorsCancel()
    {
        List<string> endpoints = new List<string>(50);
        for (int i = 1; i <= 50; i++)
        {
            endpoints.Add("192.168.99." + i + ":5555");
        }
        int active = 0;
        int maxActive = 0;
        int completedCount = 0;
        int threadsBefore = Process.GetCurrentProcess().Threads.Count;

        Dictionary<string, bool> probeResults = await DeviceDiscoveryService.ProbeEndpointsAsync(
            endpoints,
            1000,
            8,
            CancellationToken.None,
            async (ep, timeoutMs, ct) =>
            {
                int cur = Interlocked.Increment(ref active);
                while (true)
                {
                    int prevMax = Volatile.Read(ref maxActive);
                    if (cur <= prevMax || Interlocked.CompareExchange(ref maxActive, cur, prevMax) == prevMax) break;
                }
                try
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref completedCount);
                    return ep.EndsWith("1:5555");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });

        int threadsAfter = Process.GetCurrentProcess().Threads.Count;
        Assert.True(completedCount == 50 && probeResults.Count == 50);
        Assert.True(maxActive >= 2 && maxActive <= 8, "got maxActive=" + maxActive);
        Assert.True((threadsAfter - threadsBefore) < 35);
        Assert.True(probeResults["192.168.99.1:5555"] && !probeResults["192.168.99.2:5555"]);

        using (CancellationTokenSource cancelProbeCts = new CancellationTokenSource(40))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async delegate
            {
                await DeviceDiscoveryService.ProbeEndpointsAsync(
                    endpoints,
                    2000,
                    8,
                    cancelProbeCts.Token,
                    async (ep, timeoutMs, ct) =>
                    {
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                        return true;
                    });
            });
        }
    }

    [Fact]
    public async Task ProbeTcpEndpointAsync_DistinguishesListeningAndStoppedLoopbackPorts()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(await DeviceDiscoveryService.ProbeTcpEndpointAsync("127.0.0.1:" + port, 800, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
        Assert.False(await DeviceDiscoveryService.ProbeTcpEndpointAsync("127.0.0.1:" + port, 300, CancellationToken.None));
    }

    [Fact]
    public async Task DiscoveryService_StagesUsbBeforeSlowWirelessAndMergesDeterministically()
    {
        AppConfig cfg = AppConfig.CreateDefault();
        cfg.savedEndpoints.Add("192.168.10.50:5555");

        FakeAdbClient fakeAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = string.Join("\n", new string[]
                {
                    "List of devices attached",
                    "USB_PHONE              device product:p0 model:UsbPhone transport_id:1",
                    "192.168.10.20:5555     device product:p1 model:PhoneOne transport_id:2",
                    "192.168.10.20:37123    device product:p1 model:PhoneOne transport_id:3"
                })
            }),
            QueryMdnsHandler = async ct =>
            {
                await Task.Delay(60, ct).ConfigureAwait(false);
                return new CommandOutput
                {
                    ExitCode = 0,
                    StdOut = "List of discovered mdns services\nadb-DEAD-1\t_adb-tls-connect._tcp\t192.168.10.99:40000\n"
                };
            },
            QuerySerialHandler = (serial, ct) =>
            {
                if (serial == "192.168.10.20:5555" || serial == "192.168.10.20:37123")
                {
                    return Task.FromResult("HW_DUPE_1");
                }
                return Task.FromResult("");
            }
        };

        DeviceDiscoveryService service = new DeviceDiscoveryService(
            cfg,
            fakeAdb,
            (ep, timeoutMs, ct) => Task.FromResult(ep == "192.168.10.50:5555"));

        ScanResult stage1Snapshot = null;
        ScanResult finalResult = await service.ScanAsync(s1 => stage1Snapshot = s1, CancellationToken.None);

        Assert.NotNull(stage1Snapshot);
        Assert.Equal(3, stage1Snapshot.Devices.Count);
        Assert.Equal(1, finalResult.FilteredStaleMdnsCount);

        int usbCount = 0;
        int wirelessCount = 0;
        int savedCount = 0;
        for (int i = 0; i < finalResult.Devices.Count; i++)
        {
            ChannelKind kind = finalResult.Devices[i].Channel;
            if (kind == ChannelKind.Usb) usbCount++;
            else if (kind == ChannelKind.WirelessConnected) wirelessCount++;
            else if (kind == ChannelKind.SavedPending) savedCount++;
        }
        Assert.True(usbCount == 1 && wirelessCount == 1 && savedCount == 1 && finalResult.Devices.Count == 3);

        // Also explicitly verify parallel query post-processing race when wireless probe finishes after serial enrichment.
        ScanResult raceResult = AdbScanner.ParseAndMerge(
            "List of devices attached\n"
            + "192.168.10.20:5555   device product:p1 model:PhoneOne transport_id:10\n"
            + "192.168.10.20:37123  device product:p1 model:PhoneOne transport_id:11\n",
            "List of discovered mdns services\n"
            + "adb-DEAD-1\t_adb-tls-connect._tcp\t192.168.10.99:40000\n");

        Task<Dictionary<string, string>> fastSerialTask = Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "192.168.10.20:5555", "HW_DUPE_1" },
            { "192.168.10.20:37123", "HW_DUPE_1" }
        });
        Task<(List<string> savedToProbe, Dictionary<string, bool> probeResults)> slowWirelessTask = Task.Run(async delegate
        {
            await Task.Delay(40).ConfigureAwait(false);
            return (
                new List<string> { "192.168.10.50:5555" },
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    { "192.168.10.99:40000", false },
                    { "192.168.10.50:5555", true }
                });
        });

        await DeviceDiscoveryService.CompleteScanPostProcessAsync(raceResult, slowWirelessTask, fastSerialTask, CancellationToken.None);
        Assert.Equal(1, raceResult.FilteredStaleMdnsCount);
        Assert.Equal(2, raceResult.Devices.Count);
    }

    [Fact]
    public void MainForm_RecoversFromBusyExceptions_AndIsolatesStagedAndSameScanRaces()
    {
        StaRunner.Run(delegate
        {
            string tempCfg = Path.Combine(Path.GetTempPath(), "WinScrcpy-ui-test-" + Guid.NewGuid().ToString("N") + ".json");
            ConfigManager manager = new ConfigManager(tempCfg);

            // 1. Async work exception must always clear busy state.
            using (MainForm busyForm = new MainForm(manager, new AppConfig(), null, false))
            {
                IntPtr handle = busyForm.Handle;
                Assert.NotEqual(IntPtr.Zero, handle);

                busyForm.RunBusyActionForTest("模拟异常任务", delegate
                {
                    throw new InvalidOperationException("模拟后台异常");
                }, "测试操作失败");
                Assert.True(busyForm.IsBusyState);

                Assert.True(UiTestHelpers.PumpUntil(() => !busyForm.IsBusyState, 3000),
                    "MainForm must recover from busy state when async work throws");
            }

            // 2. Staged scan + newer scan cancels & discards older + same-scan BeginInvoke race guard.
            using (MainForm form = new MainForm(manager, new AppConfig(), null, false))
            {
                IntPtr handle = form.Handle;
                Assert.NotEqual(IntPtr.Zero, handle);

                TaskCompletionSource<bool> releaseScan1 = new TaskCompletionSource<bool>();
                bool scan1TokenCanceled = false;
                Action<ScanResult> capturedScan1Stage1 = null;

                ScanResult scan1UsbOnly = AdbScanner.ParseAndMerge(
                    "List of devices attached\nUSB_STAGE1 device product:usb model:UsbPhone transport_id:1\n",
                    "List of discovered mdns services\n");
                ScanResult scan1StaleFinal = AdbScanner.ParseAndMerge(
                    "List of devices attached\nSTALE_A device product:s1 model:StaleOne transport_id:2\nSTALE_B device product:s2 model:StaleTwo transport_id:3\n",
                    "List of discovered mdns services\n");
                ScanResult scan2FreshFinal = AdbScanner.ParseAndMerge(
                    "List of devices attached\n192.168.50.8:5555 device product:w1 model:FreshWifi transport_id:4\n",
                    "List of discovered mdns services\n");

                Task scan1Task = form.ExecuteScanCoreAsync(async (onStage1, ct) =>
                {
                    ct.Register(() => scan1TokenCanceled = true);
                    capturedScan1Stage1 = onStage1;
                    await Task.Run(() => onStage1(scan1UsbOnly)).ConfigureAwait(false);
                    await releaseScan1.Task.ConfigureAwait(false);
                    return scan1StaleFinal;
                });

                Assert.True(UiTestHelpers.PumpUntil(() => form.RowCountForTest == 1 && form.StatusTextForTest.Contains("正在探测无线设备"), 2500));

                Task scan2Task = form.ExecuteScanCoreAsync(async (onStage1, ct) =>
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                    return scan2FreshFinal;
                });

                Assert.True(scan1TokenCanceled);
                Assert.True(UiTestHelpers.PumpUntil(() => scan2Task.IsCompleted && form.RowCountForTest == 1 && form.StatusTextForTest.Contains("1 个设备通道"), 2500));

                if (capturedScan1Stage1 != null)
                {
                    capturedScan1Stage1(scan1StaleFinal);
                }
                releaseScan1.TrySetResult(true);
                Assert.True(UiTestHelpers.PumpUntil(() => scan1Task.IsCompleted, 2500));
                Application.DoEvents();
                Assert.True(form.RowCountForTest == 1 && form.StatusTextForTest.Contains("1 个设备通道") && !form.IsBusyState);

                // Same-scan race: BeginInvoke stage1 queued after final-result continuation.
                TaskCompletionSource<ScanResult> sameScanTcs = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<ScanResult> capturedSameScanStage1 = null;
                Task sameScanTask = form.ExecuteScanCoreAsync((onStage1, ct) =>
                {
                    capturedSameScanStage1 = onStage1;
                    return sameScanTcs.Task;
                });

                Task.Run(delegate
                {
                    sameScanTcs.TrySetResult(scan1StaleFinal);
                    if (capturedSameScanStage1 != null)
                    {
                        capturedSameScanStage1(scan1UsbOnly);
                    }
                }).GetAwaiter().GetResult();

                Assert.True(UiTestHelpers.PumpUntil(() => sameScanTask.IsCompleted, 2500));
                Thread.Sleep(30);
                Application.DoEvents();
                Assert.True(form.RowCountForTest == 2 && !form.IsBusyState && form.StatusTextForTest.Contains("2 个设备通道"));

                Task.Run(delegate { if (capturedSameScanStage1 != null) capturedSameScanStage1(scan1UsbOnly); }).GetAwaiter().GetResult();
                Thread.Sleep(20);
                Application.DoEvents();
                Assert.True(form.RowCountForTest == 2 && !form.IsBusyState);
            }

            // 3. Closing PairDialog must stop polling timer and cancel CTS.
            using (PairDialog dlg = new PairDialog(new AdbClient(new AppConfig()), null))
            {
                UiTestHelpers.ShowOffscreen(dlg);
                Assert.True(dlg.IsBackgroundPollingActiveForTest && !dlg.IsDialogCancelledForTest);
                dlg.Close();
                Application.DoEvents();
                Assert.True(!dlg.IsBackgroundPollingActiveForTest && dlg.IsDialogCancelledForTest);
            }
        });
    }

    [Fact]
    public void UiLayout_AllFormsAndDialogsRenderWithoutClippingOrScrollbars()
    {
        StaRunner.Run(delegate
        {
            AppConfig cfg = AppConfig.CreateDefault();
            ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_test_noop.json"));

            using (MainForm form = new MainForm(mgr, cfg, null, false))
            {
                UiTestHelpers.ShowOffscreen(form);

                form.LoadScanResultForTest(new ScanResult());
                Application.DoEvents();
                Assert.Null(form.ActiveTableCard);
                Assert.False(form.ConnectSelectedButton.Visible);
                UiTestHelpers.AssertAllControlsInTreeUnclipped(form);

                form.LoadScanResultForTest(UiTestHelpers.BuildMockScanResult());
                Application.DoEvents();
                Assert.NotNull(form.ActiveTableCard);
                Assert.True(form.DeviceListHost.Controls.Count == 1 && form.DeviceListHost.Controls[0] == form.ActiveTableCard);
                Assert.False(form.ConnectSelectedButton.Visible);
                Assert.True(form.MoreMenu.Items.Count == 4
                    && form.MoreMenu.Items[0].Text == "管理常用地址…"
                    && form.MoreMenu.Items[1] is ToolStripSeparator
                    && form.MoreMenu.Items[2].Text == "断开全部无线连接"
                    && form.MoreMenu.Items[3].Text == "重启 ADB 服务");
                UiTestHelpers.AssertAllControlsInTreeUnclipped(form);
                Assert.False(form.DeviceListHost.HorizontalScroll.Visible);

                form.SelectAllCheckBox.Checked = true;
                Application.DoEvents();
                Assert.True(form.ConnectSelectedButton.Visible && form.ConnectSelectedButton.Text == "投屏所选 (2)");
                UiTestHelpers.AssertAllControlsInTreeUnclipped(form);

                List<CheckBox> rowChecks = new List<CheckBox>();
                for (int i = 0; i < form.ActiveTableCard.Controls.Count; i++)
                {
                    if (form.ActiveTableCard.Controls[i].Tag is DeviceItem)
                    {
                        UiTestHelpers.CollectControls(form.ActiveTableCard.Controls[i], rowChecks);
                    }
                }
                Assert.True(rowChecks.Count == 2 && rowChecks[0].Checked && rowChecks[1].Checked);
                rowChecks[0].Checked = false;
                Application.DoEvents();
                Assert.False(form.SelectAllCheckBox.Checked);
                Assert.True(form.ConnectSelectedButton.Visible && form.ConnectSelectedButton.Text == "投屏所选 (1)");
                rowChecks[1].Checked = false;
                Application.DoEvents();
                Assert.False(form.ConnectSelectedButton.Visible);

                bool checkedNoDuplicateSn = false;
                bool checkedUsbHasTwoButtons = false;
                bool checkedWirelessHasThreeButtons = false;
                for (int i = 0; i < form.ActiveTableCard.Controls.Count; i++)
                {
                    Control row = form.ActiveTableCard.Controls[i];
                    if (row.Tag is not DeviceItem item) continue;
                    List<Button> buttons = new List<Button>();
                    UiTestHelpers.CollectControls(row, buttons);
                    if (item.Channel == ChannelKind.Usb)
                    {
                        Assert.True(buttons.Count == 2 && buttons[0].Text == "截屏" && buttons[1].Text == "投屏");
                        Assert.True(row.ContextMenuStrip != null
                            && row.ContextMenuStrip.Items.Count == 2
                            && row.ContextMenuStrip.Items[0].Text == "进入 ADB Shell"
                            && row.ContextMenuStrip.Items[1].Text == "打开本地 ADB 终端");
                        checkedUsbHasTwoButtons = true;
                    }
                    if (item.HardwareSerial == "9A8B7C6D5E4F3G2H")
                    {
                        Assert.True(buttons.Count == 3 && buttons[0].Text == "连接" && buttons[1].Text == "截屏" && buttons[2].Text == "投屏");
                        checkedWirelessHasThreeButtons = true;
                        List<SelectableTextLabel> selectableTexts = new List<SelectableTextLabel>();
                        UiTestHelpers.CollectControls(row, selectableTexts);
                        Assert.True(selectableTexts.Count == 2 && selectableTexts[0].ReadOnly && selectableTexts[1].ReadOnly);
                        for (int k = 0; k < selectableTexts.Count; k++)
                        {
                            Assert.DoesNotContain("SN: 9A8B7C6D5E4F3G2H", selectableTexts[k].Text);
                        }
                        checkedNoDuplicateSn = true;
                    }
                }
                Assert.True(checkedNoDuplicateSn && checkedUsbHasTwoButtons && checkedWirelessHasThreeButtons);

                Size originalSize = form.Size;
                form.Size = new Size(Math.Max(originalSize.Width + 600, 1800), Math.Max(originalSize.Height + 400, 1000));
                Application.DoEvents();
                form.Size = originalSize;
                Application.DoEvents();
                Assert.False(form.DeviceListHost.HorizontalScroll.Visible);
                Assert.False(form.DeviceListHost.VerticalScroll.Visible);

                using (PairDialog centeredPair = new PairDialog(new AdbClient(cfg), null))
                {
                    centeredPair.SetPollingEnabledForTest(false);
                    centeredPair.Show(form);
                    Application.DoEvents();
                    int expectedX = form.Left + (form.Width - centeredPair.Width) / 2;
                    int expectedY = form.Top + (form.Height - centeredPair.Height) / 2;
                    Assert.True(Math.Abs(centeredPair.Left - expectedX) <= 2 && Math.Abs(centeredPair.Top - expectedY) <= 2);
                    centeredPair.Close();
                }

                using (ManualConnectDialog centeredConnect = new ManualConnectDialog(new AdbClient(cfg)))
                {
                    centeredConnect.Show(form);
                    Application.DoEvents();
                    int expectedX = form.Left + (form.Width - centeredConnect.Width) / 2;
                    int expectedY = form.Top + (form.Height - centeredConnect.Height) / 2;
                    Assert.True(Math.Abs(centeredConnect.Left - expectedX) <= 2 && Math.Abs(centeredConnect.Top - expectedY) <= 2);
                    centeredConnect.Close();
                }

                using (SavedEndpointsDialog centeredSaved = new SavedEndpointsDialog(null, cfg))
                {
                    centeredSaved.Show(form);
                    Application.DoEvents();
                    int expectedX = form.Left + (form.Width - centeredSaved.Width) / 2;
                    int expectedY = form.Top + (form.Height - centeredSaved.Height) / 2;
                    Assert.True(Math.Abs(centeredSaved.Left - expectedX) <= 2 && Math.Abs(centeredSaved.Top - expectedY) <= 2);
                    centeredSaved.Close();
                }

                ScanResult savedScan = new ScanResult();
                AdbScanner.MergeReachableSavedEndpoints(savedScan, new string[] { "192.168.3.12:5555" });
                form.LoadScanResultForTest(savedScan);
                Application.DoEvents();
                List<SelectableTextLabel> savedTexts = new List<SelectableTextLabel>();
                UiTestHelpers.CollectControls(form.ActiveTableCard, savedTexts);
                Control savedRow = null;
                for (int r = 0; r < form.ActiveTableCard.Controls.Count; r++)
                {
                    if (form.ActiveTableCard.Controls[r].Tag is DeviceItem) { savedRow = form.ActiveTableCard.Controls[r]; break; }
                }
                TableLayoutPanel infoTlp = savedRow.Controls[1] as TableLayoutPanel;
                Assert.True(infoTlp != null && infoTlp.Height <= infoTlp.Controls[0].Height + infoTlp.Controls[1].Height + UiTheme.Scale(form, 8));
                Assert.True(savedTexts.Count == 2 && savedTexts[0].Text == "192.168.3.12:5555" && !savedTexts[1].Text.Contains("192.168.3.12:5555"));
                UiTestHelpers.AssertAllControlsInTreeUnclipped(form);

                ScanResult offlineScan = AdbScanner.ParseAndMerge("List of devices attached\n192.168.3.12:5555      offline product:dev_a model:Model_A transport_id:7\n", "");
                form.LoadScanResultForTest(offlineScan);
                Application.DoEvents();
                Control offlineRow = null;
                for (int r = 0; r < form.ActiveTableCard.Controls.Count; r++)
                {
                    if (form.ActiveTableCard.Controls[r].Tag is DeviceItem) { offlineRow = form.ActiveTableCard.Controls[r]; break; }
                }
                List<Button> offlineBtns = new List<Button>();
                UiTestHelpers.CollectControls(offlineRow, offlineBtns);
                Assert.True(offlineBtns.Count == 4
                    && offlineBtns[0].Text == "连接" && offlineBtns[0].Enabled
                    && offlineBtns[1].Text == "断开" && offlineBtns[1].Enabled
                    && offlineBtns[2].Text == "截屏" && !offlineBtns[2].Enabled
                    && offlineBtns[3].Text == "投屏" && !offlineBtns[3].Enabled);
                UiTestHelpers.AssertAllControlsInTreeUnclipped(form);
                form.Close();
            }

            using (PairDialog dlg = new PairDialog(new AdbClient(cfg), null))
            {
                dlg.SetPollingEnabledForTest(false);
                UiTestHelpers.ShowOffscreen(dlg);
                Size qrTabSize = dlg.Size;
                UiTestHelpers.AssertAllControlsInTreeUnclipped(dlg);

                dlg.SelectTabForTest(false);
                Application.DoEvents();
                Assert.Equal(qrTabSize, dlg.Size);
                dlg.GetCodeSpacingForTest(out int gapTabToTip, out int gapTipToInput);
                Assert.True(Math.Abs(gapTabToTip - gapTipToInput) <= UiTheme.Scale(dlg, 2));
                UiTestHelpers.AssertAllControlsInTreeUnclipped(dlg);
                dlg.Close();
            }

            using (ManualConnectDialog connectDlg = new ManualConnectDialog(new AdbClient(cfg)))
            {
                UiTestHelpers.ShowOffscreen(connectDlg);
                UiTestHelpers.AssertAllControlsInTreeUnclipped(connectDlg);

                connectDlg.fullEndpointHost.TextValue = "10.0.0.5:5555";
                connectDlg.subnetHost.TextValue = "3";
                Assert.Equal("", connectDlg.fullEndpointHost.TextValue);
                connectDlg.hostOctetHost.TextValue = "8";
                connectDlg.fullEndpointHost.TextValue = "192.168.1.9:37000";
                Assert.True(connectDlg.subnetHost.TextValue == "" && connectDlg.hostOctetHost.TextValue == ""
                    && connectDlg.portHost.TextValue == "5555");
                connectDlg.Close();
            }

            string tmpSavedPath = Path.Combine(Path.GetTempPath(), "winscrcpy_saved_dlg_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                AppConfig savedDlgCfg = AppConfig.CreateDefault();
                savedDlgCfg.savedEndpoints.Add("192.168.3.12:5555");
                ConfigManager.SetDeviceAlias(savedDlgCfg, "192.168.3.12:5555", "旧名字");
                DeviceConfig customArgsEntry = new DeviceConfig { alias = "自定义机" };
                customArgsEntry.args.Add("--max-size=1280");
                savedDlgCfg.savedEndpoints.Add("192.168.3.20:5555");
                savedDlgCfg.devices["192.168.3.20:5555"] = customArgsEntry;

                ConfigManager savedDlgMgr = new ConfigManager(tmpSavedPath);
                using (SavedEndpointsDialog savedDlg = new SavedEndpointsDialog(savedDlgMgr, savedDlgCfg))
                {
                    UiTestHelpers.ShowOffscreen(savedDlg);
                    UiTestHelpers.AssertAllControlsInTreeUnclipped(savedDlg);
                    Assert.True(savedDlg.grid.Rows.Count == 2
                        && Convert.ToString(savedDlg.grid.Rows[0].Cells[0].Value) == "192.168.3.12:5555"
                        && Convert.ToString(savedDlg.grid.Rows[0].Cells[1].Value) == "旧名字");

                    int minCellH = TextRenderer.MeasureText("192.168.3.12:5555", UiTheme.BodyFont).Height;
                    Assert.True(savedDlg.grid.RowTemplate.Height > minCellH
                        && savedDlg.grid.ColumnHeadersHeight > TextRenderer.MeasureText("地址", UiTheme.BodyBoldFont).Height);

                    savedDlg.grid.Rows.Add("192.168.3.12", "重复行");
                    Assert.True(!savedDlg.TrySaveFromGrid() && !savedDlg.Saved);

                    savedDlg.grid.Rows.RemoveAt(2);
                    savedDlg.grid.ClearSelection();
                    savedDlg.grid.Rows[1].Cells[0].Selected = true;
                    savedDlg.RemoveSelectedRows();
                    savedDlg.grid.Rows[0].Cells[1].Value = "新客厅";
                    savedDlg.grid.Rows.Add("192.168.3.88", "卧室");

                    Assert.True(savedDlg.TrySaveFromGrid() && savedDlg.Saved);
                    savedDlg.Close();
                }

                AppConfig reloadedSaved = savedDlgMgr.LoadOrCreate(out string reloadWarn);
                Assert.True(string.IsNullOrEmpty(reloadWarn)
                    && reloadedSaved.savedEndpoints.Count == 2
                    && reloadedSaved.savedEndpoints[0] == "192.168.3.12:5555"
                    && reloadedSaved.savedEndpoints[1] == "192.168.3.88:5555"
                    && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.12:5555") == "新客厅"
                    && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.88:5555") == "卧室"
                    && ConfigManager.GetDeviceAlias(reloadedSaved, "192.168.3.20:5555") == ""
                    && reloadedSaved.devices["192.168.3.20:5555"].args.Count == 1);
            }
            finally
            {
                try { if (File.Exists(tmpSavedPath)) File.Delete(tmpSavedPath); }
                catch { }
            }
        });
    }

    [Fact]
    public void RenderPreview_GeneratesAllPreviewImagesToTempDirectory()
    {
        string previewDir = Path.Combine(Path.GetTempPath(), "WinScrcpy-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            StaRunner.Run(() => UiTestHelpers.RenderPreview(previewDir));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_empty.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_devices.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_selected.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_saved.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_offline.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_pair_qr.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_pair_code.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_connect.png")));
            Assert.True(File.Exists(Path.Combine(previewDir, "preview_manage_saved.png")));
        }
        finally
        {
            try { if (Directory.Exists(previewDir)) Directory.Delete(previewDir, true); }
            catch { }
        }
    }

    [Fact]
    public void UiLayout_HighDpiPerMonitorV2ConfiguredAndUsesControlDeviceDpi()
    {
        StaRunner.Run(() =>
        {
            Assert.Equal(HighDpiMode.PerMonitorV2, Application.HighDpiMode);

            AppConfig cfg = AppConfig.CreateDefault();
            ConfigManager mgr = new ConfigManager(Path.Combine(Path.GetTempPath(), "winscrcpy_dpi_" + Guid.NewGuid().ToString("N") + ".json"));
            using (MainForm main = new MainForm(mgr, cfg, null, false))
            using (PairDialog pair = new PairDialog(new AdbClient(cfg), null))
            using (ManualConnectDialog connect = new ManualConnectDialog(new AdbClient(cfg)))
            using (SavedEndpointsDialog saved = new SavedEndpointsDialog(mgr, cfg))
            {
                Assert.Equal(AutoScaleMode.Dpi, main.AutoScaleMode);
                Assert.Equal(AutoScaleMode.Dpi, pair.AutoScaleMode);
                Assert.Equal(AutoScaleMode.Dpi, connect.AutoScaleMode);
                Assert.Equal(AutoScaleMode.Dpi, saved.AutoScaleMode);
                Assert.Equal(main.LogicalToDeviceUnits(100), UiTheme.Scale(main, 100));
                Assert.Equal(Math.Max(1f, main.DeviceDpi / 96f), UiTheme.GetDpiScale(main));
            }
        });
    }
}
