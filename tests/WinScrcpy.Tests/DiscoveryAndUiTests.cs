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
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    static bool IsAppCloaked(Form form)
    {
        Assert.Equal(0, DwmGetWindowAttribute(form.Handle, 14, out int value, sizeof(int)));
        return (value & 1) != 0;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainForm_FirstFrameIsCloakedUntilLayoutCompletes(bool createHandleEarly)
    {
        StaRunner.Run(() =>
        {
            AppConfig cfg = AppConfig.CreateDefault();
            using (MainForm form = new MainForm(new ConfigManager(Path.Combine(Path.GetTempPath(),
                "winscrcpy_first_frame_" + Guid.NewGuid().ToString("N") + ".json")), cfg, null, false))
            {
                form.ShowInTaskbar = false;
                bool loaded = false;
                bool shown = false;
                form.Load += delegate
                {
                    Assert.True(IsAppCloaked(form));
                    loaded = true;
                };
                form.Shown += delegate
                {
                    Assert.True(loaded);
                    Assert.False(IsAppCloaked(form));
                    shown = true;
                };
                if (createHandleEarly)
                {
                    IntPtr handle = form.Handle;
                    Assert.True(IsAppCloaked(form));
                }
                form.Show();
                Assert.True(UiTestHelpers.PumpUntil(() => shown, 3000));
                Rectangle area = Screen.FromControl(form).WorkingArea;
                Assert.Equal(area.Left + Math.Max(0, (area.Width - form.Width) / 2), form.Left);
                Assert.Equal(area.Top + Math.Max(0, (area.Height - form.Height) / 2), form.Top);

                form.Hide();
                form.Show();
                Application.DoEvents();
                Assert.False(IsAppCloaked(form));
                typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                Application.DoEvents();
                Assert.False(IsAppCloaked(form));
                form.Close();
            }
        });
    }

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
    public async Task DiscoveryService_CancelsSiblingStaleMdnsProbesOnceGroupEndpointSucceeds()
    {
        AppConfig cfg = AppConfig.CreateDefault();
        cfg.savedEndpoints.Add("192.168.10.50:5555");

        FakeAdbClient fakeAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = "List of devices attached\n"
            }),
            QueryMdnsHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = string.Join("\n", new string[]
                {
                    "List of discovered mdns services",
                    "adb-PHONE1-abc (3)\t_adb-tls-connect._tcp\t192.168.3.8:35841",
                    "adb-PHONE1-abc\t_adb-tls-connect._tcp\t192.168.3.8:40741",
                    "adb-PHONE1-abc (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081"
                })
            })
        };

        int siblingCanceledCount = 0;
        bool savedCompleted = false;

        DeviceDiscoveryService service = new DeviceDiscoveryService(
            cfg,
            fakeAdb,
            async (ep, timeoutMs, ct) =>
            {
                if (ep == "192.168.3.8:40741")
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                    return true;
                }
                if (ep == "192.168.10.50:5555")
                {
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    savedCompleted = true;
                    return true;
                }
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref siblingCanceledCount);
                    throw;
                }
            });

        ScanResult result = await service.ScanAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref siblingCanceledCount));
        Assert.True(savedCompleted);
        Assert.Equal(2, result.Devices.Count);
        Assert.Contains(result.Devices, d =>
            d.Channel == ChannelKind.MdnsPending
            && d.CandidateEndpoints.Count == 1
            && d.CandidateEndpoints[0] == "192.168.3.8:40741");
        Assert.Contains(result.Devices, d =>
            d.Channel == ChannelKind.SavedPending
            && d.CandidateEndpoints.Contains("192.168.10.50:5555"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryService_SharedCandidateEndpointIsNotCanceledByOtherGroupEarlyStop(bool reverseGroupOrder)
    {
        string[] lines = reverseGroupOrder
            ? new string[]
            {
                "List of discovered mdns services",
                "adb-DEV_B-2\t_adb-tls-connect._tcp\t192.168.3.20:40002",
                "adb-DEV_B-2 (2)\t_adb-tls-connect._tcp\t192.168.3.20:40003",
                "adb-DEV_A-1\t_adb-tls-connect._tcp\t192.168.3.10:40001",
                "adb-DEV_A-1 (2)\t_adb-tls-connect._tcp\t192.168.3.20:40002"
            }
            : new string[]
            {
                "List of discovered mdns services",
                "adb-DEV_A-1\t_adb-tls-connect._tcp\t192.168.3.10:40001",
                "adb-DEV_A-1 (2)\t_adb-tls-connect._tcp\t192.168.3.20:40002",
                "adb-DEV_B-2\t_adb-tls-connect._tcp\t192.168.3.20:40002",
                "adb-DEV_B-2 (2)\t_adb-tls-connect._tcp\t192.168.3.20:40003"
            };

        FakeAdbClient fakeAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = "List of devices attached\n"
            }),
            QueryMdnsHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = string.Join("\n", lines)
            })
        };

        int exclusiveStaleCanceledCount = 0;
        DeviceDiscoveryService service = new DeviceDiscoveryService(
            AppConfig.CreateDefault(),
            fakeAdb,
            async (ep, timeoutMs, ct) =>
            {
                if (ep == "192.168.3.10:40001")
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    return true;
                }
                if (ep == "192.168.3.20:40002")
                {
                    // Slower than group A's fast endpoint (10 ms), must not be canceled by group A.
                    await Task.Delay(70, ct).ConfigureAwait(false);
                    return true;
                }
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref exclusiveStaleCanceledCount);
                    throw;
                }
            });

        ScanResult result = await service.ScanAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref exclusiveStaleCanceledCount));
        Assert.Equal(2, result.Devices.Count);
        Assert.Contains(result.Devices, d =>
            d.BaseMdnsName == "adb-DEV_A-1"
            && d.CandidateEndpoints.Contains("192.168.3.10:40001")
            && d.CandidateEndpoints.Contains("192.168.3.20:40002"));
        Assert.Contains(result.Devices, d =>
            d.BaseMdnsName == "adb-DEV_B-2"
            && d.CandidateEndpoints.Count == 1
            && d.CandidateEndpoints[0] == "192.168.3.20:40002");
    }

    [Fact]
    public async Task DiscoveryService_SurfacesNonZeroAdbAndMdnsExitWarningsEvenWithoutOutput()
    {
        // 1. adb devices exits non-zero with daemon stderr
        FakeAdbClient daemonErrorAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 1,
                StdOut = "",
                StdErr = "error: cannot connect to daemon"
            }),
            QueryMdnsHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = "List of discovered mdns services\n"
            })
        };

        DeviceDiscoveryService errSvc = new DeviceDiscoveryService(AppConfig.CreateDefault(), daemonErrorAdb);
        ScanResult errStage1 = null;
        ScanResult errFinal = await errSvc.ScanAsync(s1 => errStage1 = s1, CancellationToken.None);

        Assert.NotNull(errStage1);
        Assert.Contains("error: cannot connect to daemon", errStage1.Warning);
        Assert.Contains("error: cannot connect to daemon", errFinal.Warning);
        Assert.Empty(errFinal.Devices);

        // 2. adb devices exits non-zero silently without stdout/stderr
        FakeAdbClient silentDevicesErrAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 2,
                StdOut = "",
                StdErr = ""
            }),
            QueryMdnsHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = ""
            })
        };

        DeviceDiscoveryService silentDevSvc = new DeviceDiscoveryService(AppConfig.CreateDefault(), silentDevicesErrAdb);
        ScanResult silentDevStage1 = null;
        ScanResult silentDevFinal = await silentDevSvc.ScanAsync(s1 => silentDevStage1 = s1, CancellationToken.None);

        Assert.NotNull(silentDevStage1);
        Assert.Contains("adb devices", silentDevStage1.Warning);
        Assert.Contains("2", silentDevStage1.Warning);
        Assert.Contains("adb devices", silentDevFinal.Warning);
        Assert.Contains("2", silentDevFinal.Warning);

        // 3. adb mdns services exits non-zero silently when adb devices succeeded
        FakeAdbClient silentMdnsErrAdb = new FakeAdbClient
        {
            QueryDevicesHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 0,
                StdOut = "List of devices attached\n"
            }),
            QueryMdnsHandler = ct => Task.FromResult(new CommandOutput
            {
                ExitCode = 3,
                StdOut = "",
                StdErr = ""
            })
        };

        DeviceDiscoveryService silentMdnsSvc = new DeviceDiscoveryService(AppConfig.CreateDefault(), silentMdnsErrAdb);
        ScanResult silentMdnsStage1 = null;
        ScanResult silentMdnsFinal = await silentMdnsSvc.ScanAsync(s1 => silentMdnsStage1 = s1, CancellationToken.None);

        Assert.NotNull(silentMdnsStage1);
        Assert.True(string.IsNullOrEmpty(silentMdnsStage1.Warning));
        Assert.Contains("mDNS", silentMdnsFinal.Warning);
        Assert.Contains("3", silentMdnsFinal.Warning);

        // 4. MainForm renders ADB warning instead of "未发现设备"
        StaRunner.Run(delegate
        {
            string tempCfg = Path.Combine(Path.GetTempPath(), "WinScrcpy-adb-warn-" + Guid.NewGuid().ToString("N") + ".json");
            ConfigManager manager = new ConfigManager(tempCfg);
            using (MainForm form = new MainForm(manager, AppConfig.CreateDefault(), null, false))
            {
                UiTestHelpers.ShowOffscreen(form);
                Task scanTask = form.ExecuteScanCoreAsync((onStage1, ct) => errSvc.ScanAsync(onStage1, ct));
                UiTestHelpers.PumpUntil(() => scanTask.IsCompleted, 3000);
                scanTask.GetAwaiter().GetResult();

                Assert.Contains("error: cannot connect to daemon", form.StatusTextForTest);
                Assert.DoesNotContain("未发现设备", form.StatusTextForTest);
            }
            if (File.Exists(tempCfg)) File.Delete(tempCfg);
        });
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

    [Fact]
    public void ManualConnectDialog_ModernTextBoxHostDoesNotDoubleScaleOnDpi()
    {
        StaRunner.Run(() =>
        {
            AppConfig cfg = AppConfig.CreateDefault();
            using (ManualConnectDialog connect = new ManualConnectDialog(new AdbClient(cfg)))
            {
                UiTestHelpers.ShowOffscreen(connect);
                using (Graphics g = connect.fullEndpointHost.InnerBox.CreateGraphics())
                {
                    int expectedSingleLine = Math.Max(
                        connect.fullEndpointHost.InnerBox.PreferredHeight,
                        TextRenderer.MeasureText(g, "测gy", connect.fullEndpointHost.InnerBox.Font).Height + 2);
                    Assert.Equal(expectedSingleLine, connect.fullEndpointHost.InnerBox.Height);
                }
                Assert.True(connect.subnetHost.Width <= connect.LogicalToDeviceUnits(64));
                Assert.True(connect.portHost.Width <= connect.LogicalToDeviceUnits(84));
                connect.Close();
            }
        });
    }

    [Fact]
    public void SavedEndpointsDialog_RefusesOverwriteWhenConfigCorrupted()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_dlg_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            string brokenJson = "{ broken";
            try
            {
                File.WriteAllText(cfgPath, brokenJson);
                ConfigManager mgr = new ConfigManager(cfgPath);
                AppConfig fallback = mgr.LoadOrCreate(out string warn);
                Assert.True(mgr.IsConfigCorrupted && !string.IsNullOrEmpty(warn));

                using (SavedEndpointsDialog dlg = new SavedEndpointsDialog(mgr, fallback))
                {
                    UiTestHelpers.ShowOffscreen(dlg);
                    dlg.grid.Rows.Add("192.168.3.50", "客厅");
                    Assert.False(dlg.TrySaveFromGrid());
                    Assert.False(dlg.Saved);
                    Assert.Equal(brokenJson, File.ReadAllText(cfgPath));
                    dlg.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void MainForm_AllowsOperatingOnlineDeviceDuringStage2ScanAndCancelsScan()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_stage2_act_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            AppConfig cfg = AppConfig.CreateDefault();
            cfg.adbPath = Path.Combine(tempDir, "missing_adb.exe");
            ConfigManager mgr = new ConfigManager(cfgPath);
            mgr.Save(cfg);

            try
            {
                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(form);

                    bool scanCanceled = false;
                    TaskCompletionSource<ScanResult> stage2Gate = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ScanResult stage1Usb = AdbScanner.ParseAndMerge(
                        "List of devices attached\nUSB_EARLY_01 device product:p1 model:EarlyPhone transport_id:1\n",
                        "");

                    Task scanTask = form.ExecuteScanCoreAsync(async (onStage1, ct) =>
                    {
                        ct.Register(() => scanCanceled = true);
                        await Task.Run(() => onStage1(stage1Usb)).ConfigureAwait(false);
                        return await stage2Gate.Task.ConfigureAwait(false);
                    });

                    Assert.True(UiTestHelpers.PumpUntil(
                        () => form.RowCountForTest == 1 && form.StatusTextForTest.Contains("正在探测无线设备"),
                        2500));

                    Button mirrorBtn = FindDescendantButton(form, "投屏");
                    Button shotBtn = FindDescendantButton(form, "截屏");
                    Button pairBtn = FindDescendantButton(form, "无线配对…");
                    Button manualBtn = FindDescendantButton(form, "手动连接…");
                    Button moreBtn = FindDescendantButton(form, "更多 ▾");
                    ModernCheckBox selectAll = FindDescendantCheckBox(form, null);
                    ModernCheckBox rowCheck = FindDescendantCheckBox(form, selectAll);

                    Assert.True(mirrorBtn != null && mirrorBtn.Enabled);
                    Assert.True(shotBtn != null && shotBtn.Enabled);
                    Assert.True(pairBtn != null && pairBtn.Enabled);
                    Assert.True(manualBtn != null && manualBtn.Enabled);
                    Assert.True(moreBtn != null && moreBtn.Enabled);
                    Assert.True(selectAll != null && selectAll.Enabled);
                    Assert.True(rowCheck != null && rowCheck.Enabled);

                    rowCheck.Checked = true;
                    Assert.True(form.ConnectSelectedButton.Visible && form.ConnectSelectedButton.Enabled);

                    shotBtn.PerformClick();

                    Assert.True(UiTestHelpers.PumpUntil(() => scanCanceled, 1000), "操作开始后应立即取消进行中的扫描");
                    Assert.True(UiTestHelpers.PumpUntil(() => !form.IsBusyState && form.StatusTextForTest.Contains("截屏失败"), 2500),
                        "已上线设备操作应立即完成而无需等待慢速无线探测结束");

                    stage2Gate.TrySetCanceled();
                    Assert.True(UiTestHelpers.PumpUntil(() => scanTask.IsCompleted, 1000));
                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void MainForm_SkipsControlRebuildWhenScanSnapshotMatches()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_skip_rebuild_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(form);

                    // 1. Empty -> Empty skips duplicate rebuild.
                    form.LoadScanResultForTest(new ScanResult());
                    Application.DoEvents();
                    int afterFirstEmpty = form.RebuildCountForTest;
                    form.LoadScanResultForTest(new ScanResult());
                    Application.DoEvents();
                    Assert.Equal(afterFirstEmpty, form.RebuildCountForTest);

                    // 2. Identical scan result preserves ActiveTableCard, checkbox state, and skips rebuild.
                    ScanResult firstScan = UiTestHelpers.BuildMockScanResult();
                    form.LoadScanResultForTest(firstScan);
                    Application.DoEvents();
                    int afterFirstDevices = form.RebuildCountForTest;
                    Assert.True(afterFirstDevices > afterFirstEmpty);
                    Control initialCard = form.ActiveTableCard;
                    form.SelectAllCheckBox.Checked = true;
                    Application.DoEvents();
                    Assert.True(form.ConnectSelectedButton.Visible);

                    ScanResult sameDevicesClone = UiTestHelpers.BuildMockScanResult();
                    form.LoadScanResultForTest(sameDevicesClone);
                    Application.DoEvents();
                    Assert.Equal(afterFirstDevices, form.RebuildCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.True(form.ConnectSelectedButton.Visible && form.SelectAllCheckBox.Checked);

                    // 3. Single scan with identical Stage1 and Final devices triggers only 1 rebuild.
                    form.LoadScanResultForTest(new ScanResult());
                    Application.DoEvents();
                    int beforeTwoStage = form.RebuildCountForTest;
                    ScanResult stage1Scan = AdbScanner.ParseAndMerge("List of devices attached\nNEWUSB01\tdevice model:Pixel_10\n", "");
                    ScanResult finalScan = AdbScanner.ParseAndMerge("List of devices attached\nNEWUSB01\tdevice model:Pixel_10\n", "");
                    Task scanTask = form.ExecuteScanCoreAsync((onStage1, ct) =>
                    {
                        onStage1(stage1Scan);
                        return Task.FromResult(finalScan);
                    });
                    Assert.True(UiTestHelpers.PumpUntil(() => scanTask.IsCompleted && !form.IsBusyState, 3000));
                    scanTask.GetAwaiter().GetResult();
                    Assert.Equal(beforeTwoStage + 1, form.RebuildCountForTest);

                    // 4. Config alias change updates snapshot and forces rebuild on next scan.
                    AppConfig updatedCfg = AppConfig.CreateDefault();
                    ConfigManager.SetDeviceAlias(updatedCfg, "NEWUSB01", "主力新机");
                    mgr.Save(updatedCfg);
                    int beforeAliasRebuild = form.RebuildCountForTest;
                    ScanResult postAliasScan = AdbScanner.ParseAndMerge("List of devices attached\nNEWUSB01\tdevice model:Pixel_10\n", "");
                    Task aliasScanTask = form.ExecuteScanCoreAsync((onStage1, ct) => Task.FromResult(postAliasScan));
                    Assert.True(UiTestHelpers.PumpUntil(() => aliasScanTask.IsCompleted && !form.IsBusyState, 3000));
                    aliasScanTask.GetAwaiter().GetResult();
                    Assert.Equal(beforeAliasRebuild + 1, form.RebuildCountForTest);

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void MainForm_PreservesSelectionAcrossEquivalentRefreshAndForcedRebuild()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_keep_sel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                Func<ScanResult> createDualChannelScan = () =>
                {
                    string devices = "List of devices attached\n9A8B7C6D5E4F3G2H\tdevice model:Pixel_9\n";
                    string mdns = string.Join("\n", new string[]
                    {
                        "List of discovered mdns services",
                        "adb-9A8B7C6D5E4F3G2H-YzPHtO (2)\t_adb-tls-connect._tcp\t192.168.3.8:38081"
                    });
                    return AdbScanner.ParseAndMerge(devices, mdns);
                };

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(form);

                    form.LoadScanResultForTest(createDualChannelScan());
                    Application.DoEvents();
                    int afterInitial = form.RebuildCountForTest;
                    Control initialCard = form.ActiveTableCard;

                    List<ModernCheckBox> initialChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(2, initialChecks.Count);
                    Assert.Equal(ChannelKind.Usb, ((DeviceItem)initialChecks[0].Tag).Channel);
                    Assert.Equal(ChannelKind.MdnsPending, ((DeviceItem)initialChecks[1].Tag).Channel);

                    // Select only the USB channel of the device, leaving the wireless channel unchecked.
                    initialChecks[0].Checked = true;
                    Application.DoEvents();
                    Assert.True(form.ConnectSelectedButton.Visible);
                    Assert.False(form.SelectAllCheckBox.Checked);

                    // Equivalent scan replaces lastScanResult with new DeviceItem instances while reusing existing controls.
                    form.LoadScanResultForTest(createDualChannelScan());
                    Application.DoEvents();
                    Assert.Equal(afterInitial, form.RebuildCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.True(initialChecks[0].Checked);
                    Assert.False(initialChecks[1].Checked);

                    // Forced rebuild (e.g., DPI change) must restore selection by stable device/channel identity.
                    form.RebuildDeviceListPreservingSelection();
                    Application.DoEvents();
                    Assert.Equal(afterInitial + 1, form.RebuildCountForTest);
                    Assert.NotSame(initialCard, form.ActiveTableCard);

                    List<ModernCheckBox> rebuiltChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(2, rebuiltChecks.Count);
                    Assert.NotSame(initialChecks[0], rebuiltChecks[0]);
                    Assert.True(rebuiltChecks[0].Checked);
                    Assert.False(rebuiltChecks[1].Checked);
                    Assert.True(form.ConnectSelectedButton.Visible);
                    Assert.False(form.SelectAllCheckBox.Checked);
                    Assert.Contains("已选 1 / 2 台", CollectAllLabelText(form.ActiveTableCard));

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void DeviceListView_IncrementallyUpdatesChangedRowsAndPreservesState()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_incr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                Func<int[], string, ScanResult> buildScan = (ids, changedModel) =>
                {
                    List<string> lines = new List<string> { "List of devices attached" };
                    for (int i = 0; i < ids.Length; i++)
                    {
                        int id = ids[i];
                        string model = id == 3 && !string.IsNullOrEmpty(changedModel) ? changedModel : ("Model_" + id);
                        lines.Add(string.Format("DEV_{0:D2}\tdevice model:{1}", id, model));
                    }
                    return AdbScanner.ParseAndMerge(string.Join("\n", lines.ToArray()), "");
                };

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    form.Height = 420;
                    UiTestHelpers.ShowOffscreen(form);

                    form.LoadScanResultForTest(buildScan(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, null));
                    Application.DoEvents();

                    Control initialCard = form.ActiveTableCard;
                    int baseCardRebuilds = form.CardRebuildCountForTest;
                    int baseRowCreates = form.RowCreateCountForTest;
                    int baseRowUpdates = form.RowUpdateCountForTest;

                    List<Control> initialRows = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> initialChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(8, initialRows.Count);
                    Assert.Equal(8, initialChecks.Count);

                    // Select row 2 (DEV_02) and row 5 (DEV_05), and scroll down.
                    initialChecks[1].Checked = true;
                    initialChecks[4].Checked = true;
                    form.DeviceListHost.AutoScrollPosition = new Point(0, 80);
                    Application.DoEvents();
                    int savedScroll = -form.DeviceListHost.AutoScrollPosition.Y;
                    Assert.True(savedScroll > 0);

                    // Incrementally change: modify DEV_03 model, remove DEV_06, append DEV_09.
                    ScanResult nextScan = buildScan(new[] { 1, 2, 3, 4, 5, 7, 8, 9 }, "Model_3_New");
                    form.LoadScanResultForTest(nextScan);
                    Application.DoEvents();

                    // Whole card was NOT rebuilt; only DEV_03 updated and DEV_09 created.
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(baseCardRebuilds, form.CardRebuildCountForTest);
                    Assert.Equal(baseRowCreates + 1, form.RowCreateCountForTest);
                    Assert.Equal(baseRowUpdates + 1, form.RowUpdateCountForTest);

                    List<Control> updatedRows = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> updatedChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(8, updatedRows.Count);

                    // Unchanged rows & updated DEV_03 keep exact same row/checkbox instances.
                    Assert.Same(initialRows[0], updatedRows[0]);
                    Assert.Same(initialRows[1], updatedRows[1]);
                    Assert.Same(initialRows[2], updatedRows[2]);
                    Assert.Same(initialRows[3], updatedRows[3]);
                    Assert.Same(initialRows[4], updatedRows[4]);
                    Assert.Same(initialRows[6], updatedRows[5]); // DEV_07 shifted left
                    Assert.Same(initialRows[7], updatedRows[6]); // DEV_08 shifted left
                    Assert.NotSame(initialRows[7], updatedRows[7]); // DEV_09 newly created

                    // DEV_03 shows new title, DEV_02 & DEV_05 keep selection, Tag reflects latest DeviceItem instance, scroll preserved.
                    Assert.Contains("Model 3 New", CollectRowText(updatedRows[2]));
                    Assert.True(updatedChecks[1].Checked);
                    Assert.True(updatedChecks[4].Checked);
                    Assert.False(updatedChecks[0].Checked);
                    Assert.Same(nextScan.Devices[1], updatedRows[1].Tag);
                    Assert.Same(nextScan.Devices[1], updatedChecks[1].Tag);
                    Assert.Contains("已选 2 / 8 台", CollectAllLabelText(form.ActiveTableCard));
                    Assert.True(-form.DeviceListHost.AutoScrollPosition.Y > 0);

                    // Removing first row (DEV_01) promotes DEV_02 cleanly with no leading divider before first row.
                    form.LoadScanResultForTest(buildScan(new[] { 2, 3, 4, 5, 7, 8, 9 }, "Model_3_New"));
                    Application.DoEvents();
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(baseCardRebuilds, form.CardRebuildCountForTest);

                    List<Control> afterHeadRemoved = CollectDeviceRowPanels(form.ActiveTableCard);
                    Assert.Equal(7, afterHeadRemoved.Count);
                    Assert.Same(initialRows[1], afterHeadRemoved[0]);
                    // Child index 2 in tableCard (after HeaderRow=0, HeaderDivider=1) must be the first row itself, not a divider.
                    Assert.Same(afterHeadRemoved[0], form.ActiveTableCard.Controls[2]);
                    UiTestHelpers.AssertAllControlsInTreeUnclipped(form.ActiveTableCard.Controls[0]);
                    for (int i = 0; i < afterHeadRemoved.Count; i++)
                    {
                        UiTestHelpers.AssertAllControlsInTreeUnclipped(afterHeadRemoved[i]);
                    }
                    Assert.False(form.DeviceListHost.HorizontalScroll.Visible);

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void MainForm_Stage1RetainsPreviousPendingWirelessDevicesAsChecking()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_stage1_keep_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(form);

                    // Initial list has 1 USB + 1 MdnsPending device; check the MdnsPending row.
                    form.LoadScanResultForTest(UiTestHelpers.BuildMockScanResult());
                    Application.DoEvents();
                    Control initialCard = form.ActiveTableCard;
                    int initialCardRebuilds = form.CardRebuildCountForTest;
                    List<Control> initialRows = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> initialChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(2, initialRows.Count);
                    Assert.Equal(ChannelKind.MdnsPending, ((DeviceItem)initialChecks[1].Tag).Channel);
                    initialChecks[1].Checked = true;
                    Application.DoEvents();

                    TaskCompletionSource<ScanResult> finalTcs = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    ScanResult stage1UsbOnly = AdbScanner.ParseAndMerge("List of devices attached\nUSBDEV001\tdevice model:Pixel_9\n", "");

                    Task scanTask = form.ExecuteScanCoreAsync((onStage1, ct) =>
                    {
                        onStage1(stage1UsbOnly);
                        return finalTcs.Task;
                    });

                    // Stage 1 must NOT shrink to 1 row: previous MdnsPending stays visible, checked, and marked checking.
                    Assert.True(UiTestHelpers.PumpUntil(() => form.StatusTextForTest.Contains("已发现 1 台已连接设备"), 2500));
                    Assert.Equal(2, form.RowCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(initialCardRebuilds, form.CardRebuildCountForTest);
                    Assert.Same(initialRows[0], CollectDeviceRowPanels(form.ActiveTableCard)[0]);
                    Assert.Same(initialRows[1], CollectDeviceRowPanels(form.ActiveTableCard)[1]);
                    Assert.True(initialChecks[1].Checked);
                    Assert.Contains("正在检查…", CollectRowText(initialRows[1]));

                    // Final confirms the wireless device is still reachable -> clears checking label, keeps selection.
                    finalTcs.SetResult(UiTestHelpers.BuildMockScanResult());
                    Assert.True(UiTestHelpers.PumpUntil(() => scanTask.IsCompleted && !form.IsBusyState, 2500));
                    scanTask.GetAwaiter().GetResult();
                    Assert.Equal(2, form.RowCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.True(initialChecks[1].Checked);
                    Assert.DoesNotContain("正在检查…", CollectRowText(initialRows[1]));

                    // Second scan where final drops offline wireless -> shrinks cleanly after confirmation, USB row still reused.
                    int beforeSecondCreates = form.RowCreateCountForTest;
                    ScanResult usbFinalOnly = AdbScanner.ParseAndMerge("List of devices attached\nUSBDEV001\tdevice model:Pixel_9\n", "");
                    Task secondScan = form.ExecuteScanCoreAsync((onStage1, ct) => Task.FromResult(usbFinalOnly));
                    Assert.True(UiTestHelpers.PumpUntil(() => secondScan.IsCompleted && !form.IsBusyState, 2500));
                    secondScan.GetAwaiter().GetResult();

                    Assert.Equal(1, form.RowCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(beforeSecondCreates, form.RowCreateCountForTest);
                    Assert.Same(initialRows[0], CollectDeviceRowPanels(form.ActiveTableCard)[0]);

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void DeviceListView_Stage2EnrichmentAndChannelTransitionPreserveRowAndSelection()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_stage2_id_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(form);

                    // Stage 1: USB + WirelessConnected (no HardwareSerial yet) + MdnsPending
                    ScanResult stage1 = AdbScanner.ParseAndMerge(
                        "List of devices attached\n9A8B7C6D5E4F3G2H\tdevice model:Pixel_9 transport_id:1\n192.168.1.80:5555\tdevice model:Pixel_9 transport_id:2\n",
                        "List of discovered mdns services\nadb-PEND999-AbCdEf\t_adb-tls-connect._tcp\t192.168.1.90:41234\n");

                    form.LoadScanResultForTest(stage1);
                    Application.DoEvents();

                    Control initialCard = form.ActiveTableCard;
                    int initialCardRebuilds = form.CardRebuildCountForTest;
                    int initialRowCreates = form.RowCreateCountForTest;
                    List<Control> rows1 = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> checks1 = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(3, rows1.Count);
                    Assert.Equal(ChannelKind.Usb, ((DeviceItem)checks1[0].Tag).Channel);
                    Assert.Equal(ChannelKind.WirelessConnected, ((DeviceItem)checks1[1].Tag).Channel);
                    Assert.Equal(ChannelKind.MdnsPending, ((DeviceItem)checks1[2].Tag).Channel);

                    // User checks both wireless rows, leaves USB row unchecked.
                    checks1[1].Checked = true;
                    checks1[2].Checked = true;
                    Application.DoEvents();

                    // Stage 2:
                    // - 192.168.1.80:5555 gains HardwareSerial matching USB hardware serial & BaseMdnsName
                    // - MdnsPending transitions to WirelessConnected at 192.168.1.90:41234
                    ScanResult stage2 = AdbScanner.ParseAndMerge(
                        "List of devices attached\n"
                        + "9A8B7C6D5E4F3G2H\tdevice model:Pixel_9 transport_id:1\n"
                        + "192.168.1.80:5555\tdevice model:Pixel_9 transport_id:2\n"
                        + "192.168.1.90:41234\tdevice model:PendingPhone transport_id:3\n",
                        "List of discovered mdns services\n"
                        + "adb-9A8B7C6D5E4F3G2H-YzPHtO\t_adb-tls-connect._tcp\t192.168.1.80:5555\n"
                        + "adb-PEND999-AbCdEf\t_adb-tls-connect._tcp\t192.168.1.90:41234\n");
                    DeviceItem enrichedWireless = stage2.Devices.Find(d => d.Channel == ChannelKind.WirelessConnected && d.AdbSerial == "192.168.1.80:5555");
                    Assert.NotNull(enrichedWireless);
                    enrichedWireless.HardwareSerial = "9A8B7C6D5E4F3G2H";
                    DeviceItem transitionedWireless = stage2.Devices.Find(d => d.Channel == ChannelKind.WirelessConnected && d.AdbSerial == "192.168.1.90:41234");
                    Assert.NotNull(transitionedWireless);
                    transitionedWireless.HardwareSerial = "PEND999";
                    transitionedWireless.BaseMdnsName = "adb-PEND999-AbCdEf";

                    form.LoadScanResultForTest(stage2);
                    Application.DoEvents();

                    List<Control> rows2 = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> checks2 = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(3, rows2.Count);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(initialCardRebuilds, form.CardRebuildCountForTest);
                    Assert.Equal(initialRowCreates, form.RowCreateCountForTest);

                    for (int i = 0; i < 3; i++)
                    {
                        Assert.Same(rows1[i], rows2[i]);
                        Assert.Same(checks1[i], checks2[i]);
                    }
                    Assert.False(checks2[0].Checked);
                    Assert.True(checks2[1].Checked);
                    Assert.True(checks2[2].Checked);

                    // Force rebuild (e.g. DPI change) also keeps only the two wireless selections.
                    ((DeviceListView)form.ActiveTableCard.Parent).HandleDpiChanged();
                    Application.DoEvents();
                    List<ModernCheckBox> checksAfterDpi = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(3, checksAfterDpi.Count);
                    Assert.False(checksAfterDpi[0].Checked);
                    Assert.True(checksAfterDpi[1].Checked);
                    Assert.True(checksAfterDpi[2].Checked);

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public void DeviceListView_SingleRowChangeInLargeListReusesUnchangedRowsAndPreservesScrollAndSelection(int deviceCount)
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_single_row_" + deviceCount + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                Func<int, ScanResult> buildList = changedIndex =>
                {
                    var lines = new List<string>(deviceCount + 1) { "List of devices attached" };
                    for (int i = 0; i < deviceCount; i++)
                    {
                        string model = i == changedIndex ? "Model_" + i + "_New" : "Model_" + i;
                        lines.Add(string.Format("DEV_{0:D2}\tdevice model:{1}", i, model));
                    }
                    return AdbScanner.ParseAndMerge(string.Join("\n", lines), "");
                };

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    form.Height = 320;
                    UiTestHelpers.ShowOffscreen(form);

                    form.LoadScanResultForTest(buildList(-1));
                    Application.DoEvents();

                    Control initialCard = form.ActiveTableCard;
                    ScrollableControl listView = (ScrollableControl)initialCard.Parent;
                    listView.AutoScrollPosition = new Point(0, 140);
                    Application.DoEvents();
                    int scrollBefore = -listView.AutoScrollPosition.Y;
                    Assert.True(scrollBefore > 0, "Expected vertical scroll offset before update.");

                    List<Control> initialRows = CollectDeviceRowPanels(initialCard);
                    List<ModernCheckBox> initialChecks = CollectRowCheckBoxes(initialCard, form.SelectAllCheckBox);
                    Assert.Equal(deviceCount, initialRows.Count);

                    int keepCheckedA = 1;
                    int keepCheckedB = deviceCount - 2;
                    initialChecks[keepCheckedA].Checked = true;
                    initialChecks[keepCheckedB].Checked = true;
                    Application.DoEvents();

                    int beforeCardRebuilds = form.CardRebuildCountForTest;
                    int beforeRowCreates = form.RowCreateCountForTest;
                    int beforeRowUpdates = form.RowUpdateCountForTest;

                    // Change only row index 3.
                    int changedRow = 3;
                    form.LoadScanResultForTest(buildList(changedRow));
                    Application.DoEvents();

                    Assert.Equal(deviceCount, form.RowCountForTest);
                    Assert.Same(initialCard, form.ActiveTableCard);
                    Assert.Equal(beforeCardRebuilds, form.CardRebuildCountForTest);
                    Assert.Equal(beforeRowCreates, form.RowCreateCountForTest);
                    Assert.Equal(beforeRowUpdates + 1, form.RowUpdateCountForTest);

                    List<Control> afterRows = CollectDeviceRowPanels(form.ActiveTableCard);
                    List<ModernCheckBox> afterChecks = CollectRowCheckBoxes(form.ActiveTableCard, form.SelectAllCheckBox);
                    Assert.Equal(deviceCount, afterRows.Count);

                    for (int i = 0; i < deviceCount; i++)
                    {
                        Assert.Same(initialRows[i], afterRows[i]);
                        Assert.Same(initialChecks[i], afterChecks[i]);
                    }

                    Assert.Contains("Model 3 New", CollectRowText(afterRows[changedRow]));
                    Assert.True(afterChecks[keepCheckedA].Checked);
                    Assert.True(afterChecks[keepCheckedB].Checked);

                    int scrollAfter = -listView.AutoScrollPosition.Y;
                    Assert.True(Math.Abs(scrollAfter - scrollBefore) <= 2,
                        string.Format("Expected scrollY near {0}, actual {1}.", scrollBefore, scrollAfter));

                    form.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    [Fact]
    public void MainForm_CenteredStartupPlacesHandleAtCenteredCoordinatesBeforeVisible()
    {
        StaRunner.Run(() =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "winscrcpy_center_start_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string cfgPath = Path.Combine(tempDir, "winscrcpy.json");
            try
            {
                AppConfig cfg = AppConfig.CreateDefault();
                ConfigManager mgr = new ConfigManager(cfgPath);
                mgr.Save(cfg);

                Point? locAtHandleCreated = null;
                Point? locAtLoad = null;
                Point? locAtShown = null;
                Size? sizeAtShown = null;

                using (MainForm form = new MainForm(mgr, cfg, null, false))
                {
                    form.ShowInTaskbar = false;
                    form.HandleCreated += delegate { locAtHandleCreated = form.Location; };
                    form.Load += delegate { locAtLoad = form.Location; };
                    form.Shown += delegate
                    {
                        locAtShown = form.Location;
                        sizeAtShown = form.Size;
                        form.Close();
                    };
                    Application.Run(form);
                }

                Assert.NotNull(locAtHandleCreated);
                Assert.NotNull(locAtLoad);
                Assert.NotNull(locAtShown);
                Assert.NotNull(sizeAtShown);

                Rectangle area = Screen.FromRectangle(new Rectangle(locAtShown.Value, sizeAtShown.Value)).WorkingArea;
                int expectedX = area.Left + Math.Max(0, (area.Width - sizeAtShown.Value.Width) / 2);
                int expectedY = area.Top + Math.Max(0, (area.Height - sizeAtShown.Value.Height) / 2);
                Assert.Equal(expectedX, locAtHandleCreated.Value.X);
                Assert.Equal(expectedY, locAtHandleCreated.Value.Y);
                Assert.Equal(locAtHandleCreated.Value, locAtLoad.Value);
                Assert.Equal(locAtHandleCreated.Value, locAtShown.Value);

                using (MainForm offscreen = new MainForm(mgr, cfg, null, false))
                {
                    UiTestHelpers.ShowOffscreen(offscreen);
                    Assert.Equal(new Point(-32000, -32000), offscreen.Location);
                    offscreen.Close();
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        });
    }

    static List<Control> CollectDeviceRowPanels(Control card)
    {
        List<Control> rows = new List<Control>();
        if (card == null) return rows;
        for (int i = 0; i < card.Controls.Count; i++)
        {
            if (card.Controls[i].Tag is DeviceItem) rows.Add(card.Controls[i]);
        }
        return rows;
    }

    static string CollectRowText(Control row)
    {
        if (row == null) return "";
        List<string> texts = new List<string>();
        CollectRowTextCore(row, texts);
        return string.Join(" | ", texts);
    }

    static void CollectRowTextCore(Control root, List<string> texts)
    {
        foreach (Control c in root.Controls)
        {
            if ((c is Label || c is SelectableTextLabel) && !string.IsNullOrEmpty(c.Text)) texts.Add(c.Text);
            CollectRowTextCore(c, texts);
        }
    }

    static List<ModernCheckBox> CollectRowCheckBoxes(Control root, CheckBox selectAll)
    {
        List<ModernCheckBox> list = new List<ModernCheckBox>();
        CollectRowCheckBoxesCore(root, selectAll, list);
        return list;
    }

    static void CollectRowCheckBoxesCore(Control root, CheckBox selectAll, List<ModernCheckBox> list)
    {
        if (root == null) return;
        foreach (Control c in root.Controls)
        {
            ModernCheckBox cb = c as ModernCheckBox;
            if (cb != null && !ReferenceEquals(cb, selectAll) && cb.Tag is DeviceItem)
            {
                list.Add(cb);
            }
            CollectRowCheckBoxesCore(c, selectAll, list);
        }
    }

    static string CollectAllLabelText(Control root)
    {
        if (root == null) return "";
        List<string> texts = new List<string>();
        CollectAllLabelTextCore(root, texts);
        return string.Join(" | ", texts);
    }

    static void CollectAllLabelTextCore(Control root, List<string> texts)
    {
        foreach (Control c in root.Controls)
        {
            Label lbl = c as Label;
            if (lbl != null && !string.IsNullOrEmpty(lbl.Text)) texts.Add(lbl.Text);
            CollectAllLabelTextCore(c, texts);
        }
    }

    static Button FindDescendantButton(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            Button btn = c as Button;
            if (btn != null && string.Equals(btn.Text, text, StringComparison.Ordinal)) return btn;
            Button nested = FindDescendantButton(c, text);
            if (nested != null) return nested;
        }
        return null;
    }

    static ModernCheckBox FindDescendantCheckBox(Control root, ModernCheckBox exclude)
    {
        foreach (Control c in root.Controls)
        {
            ModernCheckBox cb = c as ModernCheckBox;
            if (cb != null && !ReferenceEquals(cb, exclude)) return cb;
            ModernCheckBox nested = FindDescendantCheckBox(c, exclude);
            if (nested != null) return nested;
        }
        return null;
    }
}
