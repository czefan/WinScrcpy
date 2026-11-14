using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace WinScrcpy.Tests;

internal static class StaRunner
{
    static readonly object WinFormsInitLock = new object();
    static bool winFormsInitialized;

    public static void EnsureWinFormsInitialized()
    {
        lock (WinFormsInitLock)
        {
            if (winFormsInitialized) return;
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch (Exception) { }
            try { Application.EnableVisualStyles(); } catch (Exception) { }
            try { Application.SetCompatibleTextRenderingDefault(false); } catch (InvalidOperationException) { }
            winFormsInitialized = true;
        }
    }

    public static void Run(Action action)
    {
        Exception captured = null;
        Thread thread = new Thread(delegate()
        {
            EnsureWinFormsInitialized();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured != null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}

internal static class UiTestHelpers
{
    public static void ShowOffscreen(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.Show();
        Application.DoEvents();
    }

    public static bool PumpUntil(Func<bool> predicate, int timeoutMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        Application.DoEvents();
        return predicate();
    }

    public static ScanResult BuildMockScanResult()
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

    public static void AssertAllControlsInTreeUnclipped(Control root)
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
            Assert.True(ctrl.Width >= pref.Width && ctrl.Height >= pref.Height,
                string.Format("control [{0}] clipped: actual={1}x{2}, preferred={3}x{4}",
                    label, ctrl.Width, ctrl.Height, pref.Width, pref.Height));
        }

        if (ctrl.Parent != null)
        {
            Size pSize = ctrl.Parent.ClientSize;
            Assert.True(ctrl.Left >= 0 && ctrl.Top >= 0 && ctrl.Right <= pSize.Width && ctrl.Bottom <= pSize.Height,
                string.Format("control [{0}] out of parent bounds: rect=({1},{2},{3},{4}), parent={5}x{6}",
                    label, ctrl.Left, ctrl.Top, ctrl.Right, ctrl.Bottom, pSize.Width, pSize.Height));
        }
    }

    public static void CollectControls<T>(Control root, List<T> list) where T : Control
    {
        for (int i = 0; i < root.Controls.Count; i++)
        {
            Control c = root.Controls[i];
            if (c is T typed) list.Add(typed);
            if (c.Controls.Count > 0) CollectControls(c, list);
        }
    }

    /// <summary>
    /// Generates offscreen dialog/main window preview PNGs for manual visual inspection.
    /// </summary>
    public static void RenderPreview(string outputDir)
    {
        StaRunner.EnsureWinFormsInitialized();
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

        using (PairDialog dlg = new PairDialog(new AdbClient(cfg), null))
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
        using (ManualConnectDialog connectDlg = new ManualConnectDialog(new AdbClient(cfg)))
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
    }

    static void SaveBitmap(Form form, string path)
    {
        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}

internal static class ProcessTestHelpers
{
    public static void KillProcessSafe(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using (Process p = Process.GetProcessById(pid))
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(1000);
                }
            }
        }
        catch (Exception) { }
    }

    public static void KillProcessByPidFile(string pidFile)
    {
        if (!File.Exists(pidFile)) return;
        try
        {
            string raw = File.ReadAllText(pidFile).Trim();
            int pid;
            if (int.TryParse(raw, out pid) && pid > 0)
            {
                KillProcessSafe(pid);
            }
        }
        catch (Exception) { }
    }
}

internal sealed class FakeAdbClient : IAdbClient
{
    public Func<CancellationToken, Task<CommandOutput>> QueryDevicesHandler;
    public Func<CancellationToken, Task<CommandOutput>> QueryMdnsHandler;
    public Func<string, CancellationToken, Task<string>> QuerySerialHandler;
    public Func<DeviceItem, CancellationToken, Task<(bool ok, string activeSerial, string error)>> EnsureConnectedHandler;

    public Task<CommandOutput> QueryDevicesRawAsync(CancellationToken cancellationToken)
    {
        return QueryDevicesHandler != null
            ? QueryDevicesHandler(cancellationToken)
            : Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "List of devices attached\n" });
    }

    public Task<CommandOutput> QueryMdnsServicesRawAsync(CancellationToken cancellationToken)
    {
        return QueryMdnsHandler != null
            ? QueryMdnsHandler(cancellationToken)
            : Task.FromResult(new CommandOutput { ExitCode = 0, StdOut = "List of discovered mdns services\n" });
    }

    public Task<string> QueryHardwareSerialAsync(string adbSerial, CancellationToken cancellationToken)
    {
        return QuerySerialHandler != null
            ? QuerySerialHandler(adbSerial, cancellationToken)
            : Task.FromResult("");
    }

    public Task<(bool ok, string activeSerial, string error)> EnsureConnectedAsync(DeviceItem item, CancellationToken cancellationToken)
    {
        return EnsureConnectedHandler != null
            ? EnsureConnectedHandler(item, cancellationToken)
            : Task.FromResult((true, string.IsNullOrEmpty(item.AdbSerial) ? item.PrimaryEndpointDisplay : item.AdbSerial, ""));
    }
}
