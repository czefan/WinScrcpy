using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    readonly ConfigManager configManager;
    readonly HardwareSerialCache serialCache = new HardwareSerialCache();
    AppConfig config;
    AdbClient adbClient;
    DeviceDiscoveryService discoveryService;
    ScrcpyLauncher scrcpyLauncher;
    TerminalLauncher terminalLauncher;
    readonly bool autoScanOnShow;

    readonly TableLayoutPanel topBar;
    readonly DeviceListView deviceList;
    readonly TableLayoutPanel statusBar;
    readonly Panel statusDot;
    readonly Button refreshBtn;
    readonly Button pairBtn;
    readonly Button manualBtn;
    readonly Button connectSelectedBtn;
    readonly Button moreBtn;
    readonly ContextMenuStrip moreMenu;
    readonly ToolStripMenuItem manageSavedItem;
    readonly ToolStripMenuItem disconnectAllItem;
    readonly ToolStripMenuItem restartAdbItem;
    readonly Label statusLabel;

    readonly List<PairingServiceInfo> lastPairingServices = new List<PairingServiceInfo>();
    Color statusDotColor = UiTheme.StatusGreen;
    bool isActionBusy;
    bool isScanning;
    bool isBusy { get { return isActionBusy || isScanning; } }
    readonly CancellationTokenSource formCts = new CancellationTokenSource();
    CancellationTokenSource activeScanCts;
    long activeScanId;

    public MainForm(ConfigManager configManager, AppConfig config, string initialWarning, bool autoScanOnShow)
    {
        this.configManager = configManager;
        this.config = config;
        InitServices();
        this.autoScanOnShow = autoScanOnShow;

        Text = "WinScrcpy";
        UiTheme.ApplyWindowIcon(this);
        SuspendLayout();
        ClientSize = new Size(780, 520);
        MinimumSize = new Size(660, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiTheme.PageBg;
        Font = UiTheme.BodyFont;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += OnFormKeyDown;

        // 顶部统一命令栏：左放高频入口（扫描、添加无线），右放上下文批量操作与维护菜单。
        topBar = new TableLayoutPanel();
        topBar.Dock = DockStyle.Top;
        topBar.AutoSize = true;
        topBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        topBar.ColumnCount = 3;
        topBar.RowCount = 1;
        topBar.Padding = new Padding(18, 10, 18, 10);
        topBar.BackColor = UiTheme.SurfaceBg;
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        FlowLayoutPanel topLeftFlow = new FlowLayoutPanel();
        topLeftFlow.AutoSize = true;
        topLeftFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        topLeftFlow.WrapContents = false;
        topLeftFlow.BackColor = UiTheme.SurfaceBg;
        topLeftFlow.Anchor = AnchorStyles.Left;
        topLeftFlow.Margin = new Padding(0);

        refreshBtn = UiTheme.MakeToolButton("扫描 (F5)");
        refreshBtn.Margin = new Padding(0, 0, 8, 0);
        refreshBtn.Click += delegate { StartScan(); };

        pairBtn = UiTheme.MakeToolButton("无线配对…");
        pairBtn.Margin = new Padding(0, 0, 8, 0);
        pairBtn.Click += OnOpenPairDialog;

        manualBtn = UiTheme.MakeToolButton("手动连接…");
        manualBtn.Margin = new Padding(0);
        manualBtn.Click += OnOpenManualConnectDialog;

        topLeftFlow.Controls.Add(refreshBtn);
        topLeftFlow.Controls.Add(pairBtn);
        topLeftFlow.Controls.Add(manualBtn);

        FlowLayoutPanel topRightFlow = new FlowLayoutPanel();
        topRightFlow.AutoSize = true;
        topRightFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        topRightFlow.WrapContents = false;
        topRightFlow.BackColor = UiTheme.SurfaceBg;
        topRightFlow.Anchor = AnchorStyles.Right;
        topRightFlow.Margin = new Padding(0);

        connectSelectedBtn = UiTheme.MakePrimaryButton("投屏所选 (0)");
        connectSelectedBtn.Margin = new Padding(0, 0, 8, 0);
        connectSelectedBtn.Visible = false;
        connectSelectedBtn.Click += OnConnectSelectedClick;

        moreBtn = UiTheme.MakeToolButton("更多 ▾");
        moreBtn.Margin = new Padding(0);
        moreBtn.Click += OnMoreButtonClick;

        moreMenu = new ContextMenuStrip();
        moreMenu.ShowImageMargin = false;
        moreMenu.Font = UiTheme.BodyFont;
        moreMenu.BackColor = Color.White;
        manageSavedItem = new ToolStripMenuItem("管理常用地址…");
        manageSavedItem.Click += OnOpenManageSavedDialog;
        disconnectAllItem = new ToolStripMenuItem("断开全部无线连接");
        disconnectAllItem.Click += delegate
        {
            RunGlobalAdbAction("正在断开无线连接...", async ct =>
            {
                CommandOutput r = await adbClient.RunAdbAsync(new string[] { "disconnect" }, 4000, ct);
                return r.Success ? null : r.GetFailureSummary("断开无线连接失败");
            });
        };
        restartAdbItem = new ToolStripMenuItem("重启 ADB 服务");
        restartAdbItem.Click += delegate
        {
            RunGlobalAdbAction("正在重启 ADB 服务...", async ct =>
            {
                CommandOutput kill = await adbClient.RunAdbAsync(new string[] { "kill-server" }, 4000, ct);
                if (!kill.Success) return kill.GetFailureSummary("停止 ADB 服务失败");
                CommandOutput start = await adbClient.RunAdbAsync(new string[] { "start-server" }, 6000, ct);
                return start.Success ? null : start.GetFailureSummary("启动 ADB 服务失败");
            });
        };
        moreMenu.Items.Add(manageSavedItem);
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add(disconnectAllItem);
        moreMenu.Items.Add(restartAdbItem);

        topRightFlow.Controls.Add(connectSelectedBtn);
        topRightFlow.Controls.Add(moreBtn);

        topBar.Controls.Add(topLeftFlow, 0, 0);
        topBar.Controls.Add(topRightFlow, 2, 0);

        Panel topBorder = new Panel();
        topBorder.Dock = DockStyle.Top;
        topBorder.Height = 1;
        topBorder.BackColor = UiTheme.BorderColor;

        deviceList = new DeviceListView();
        deviceList.RequestScan = delegate { StartScan(); };
        deviceList.ConnectSingle = ExecuteConnectSingle;
        deviceList.CaptureScreenshot = ExecuteScreenshot;
        deviceList.OpenTerminal = ExecuteOpenTerminal;
        deviceList.WirelessAction = ExecuteWirelessAction;
        deviceList.CanExecuteActions = delegate { return !isActionBusy; };
        deviceList.SelectionChanged = OnListSelectionChanged;

        Panel statusBorder = new Panel();
        statusBorder.Dock = DockStyle.Bottom;
        statusBorder.Height = 1;
        statusBorder.BackColor = UiTheme.BorderColor;

        statusBar = new TableLayoutPanel();
        statusBar.Dock = DockStyle.Bottom;
        statusBar.AutoSize = true;
        statusBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        statusBar.ColumnCount = 2;
        statusBar.RowCount = 1;
        statusBar.BackColor = UiTheme.StatusBg;
        statusBar.Padding = new Padding(18, 8, 18, 8);
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        statusBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        statusDot = new Panel();
        statusDot.Size = new Size(9, 9);
        statusDot.Anchor = AnchorStyles.Left;
        statusDot.Margin = new Padding(0, 0, 8, 0);
        statusDot.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.Clear(UiTheme.StatusBg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(statusDotColor))
            {
                e.Graphics.FillEllipse(brush, 1, 1, statusDot.Width - 2, statusDot.Height - 2);
            }
        };

        statusLabel = new Label();
        statusLabel.AutoSize = false;
        statusLabel.AutoEllipsis = true;
        statusLabel.Font = UiTheme.SubFont;
        statusLabel.ForeColor = UiTheme.TextSecondary;
        statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        statusLabel.Margin = new Padding(0);
        statusLabel.Height = statusLabel.PreferredHeight;
        statusLabel.Text = string.IsNullOrEmpty(initialWarning) ? "准备就绪" : initialWarning;
        if (!string.IsNullOrEmpty(initialWarning))
        {
            statusDotColor = UiTheme.StatusOrange;
        }

        statusBar.Controls.Add(statusDot, 0, 0);
        statusBar.Controls.Add(statusLabel, 1, 0);

        // WinForms lays out docked controls in reverse collection order: add Fill first so edges claim space first.
        Controls.Add(deviceList);
        Controls.Add(topBorder);
        Controls.Add(topBar);
        Controls.Add(statusBorder);
        Controls.Add(statusBar);

        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        FormClosing += delegate { CancelAllBackgroundWork(); };
        if (autoScanOnShow)
        {
            Shown += delegate { StartScan(); };
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (statusLabel != null)
        {
            statusLabel.Height = statusLabel.PreferredHeight;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (statusLabel != null)
        {
            statusLabel.Height = statusLabel.PreferredHeight;
        }
        RebuildDeviceListPreservingSelection();
    }

    internal void RebuildDeviceListPreservingSelection()
    {
        deviceList.HandleDpiChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelAllBackgroundWork();
            formCts.Dispose();
            if (moreMenu != null)
            {
                moreMenu.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    public Panel DeviceListHost { get { return deviceList; } }
    public Control ActiveTableCard { get { return deviceList.ActiveTableCard; } }
    public CheckBox SelectAllCheckBox { get { return deviceList.SelectAllCheck; } }
    public Button ConnectSelectedButton { get { return connectSelectedBtn; } }
    public ContextMenuStrip MoreMenu { get { return moreMenu; } }

    public void LoadScanResultForTest(ScanResult result)
    {
        ApplyScanResult(result);
        statusLabel.Text = BuildScanSummary(result);
        UpdateStatusTone(result);
    }

    void OnFormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            StartScan();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    void OnMoreButtonClick(object sender, EventArgs e)
    {
        if (isActionBusy) return;
        Point pt = new Point(moreBtn.Width - moreMenu.PreferredSize.Width, moreBtn.Height + moreBtn.LogicalToDeviceUnits(4));
        if (pt.X < -moreBtn.Left) pt.X = 0;
        moreMenu.Show(moreBtn, pt);
    }

    void SetUiBusy(bool actionBusy, bool scanning, string status, Color? dotColor)
    {
        isActionBusy = actionBusy;
        isScanning = scanning;
        // 扫描探测无线设备期间允许立即操作已发现的在线设备，仅在执行显式操作期间禁用交互。
        refreshBtn.Enabled = !actionBusy;
        refreshBtn.Text = scanning ? "扫描中…" : "扫描 (F5)";
        pairBtn.Enabled = !actionBusy;
        manualBtn.Enabled = !actionBusy;
        moreBtn.Enabled = !actionBusy;
        manageSavedItem.Enabled = !actionBusy;
        disconnectAllItem.Enabled = !actionBusy;
        restartAdbItem.Enabled = !actionBusy;
        connectSelectedBtn.Enabled = !actionBusy;
        deviceList.SetActionBusy(actionBusy);
        if (status != null) statusLabel.Text = status;
        if (dotColor.HasValue)
        {
            statusDotColor = dotColor.Value;
            statusDot.Invalidate();
        }
    }

    void UpdateStatusTone(ScanResult result)
    {
        if (!string.IsNullOrEmpty(result.Warning))
        {
            statusDotColor = UiTheme.StatusRed;
        }
        else if (result.Devices.Count == 0)
        {
            statusDotColor = UiTheme.TextDisabled;
        }
        else
        {
            statusDotColor = UiTheme.StatusGreen;
        }
        statusDot.Invalidate();
    }

    void InitServices()
    {
        adbClient = new AdbClient(config);
        discoveryService = new DeviceDiscoveryService(config, adbClient, null, serialCache);
        scrcpyLauncher = new ScrcpyLauncher(config);
        terminalLauncher = new TerminalLauncher(adbClient);
    }

    void ReloadConfig()
    {
        string prevAdbPath = config != null ? (config.adbPath ?? "") : "";
        string warn;
        config = configManager.LoadOrCreate(out warn);
        string nextAdbPath = config != null ? (config.adbPath ?? "") : "";
        if (!string.Equals(prevAdbPath, nextAdbPath, StringComparison.OrdinalIgnoreCase))
        {
            serialCache.Clear();
        }
        InitServices();
        if (!string.IsNullOrEmpty(warn))
        {
            statusLabel.Text = warn;
            statusDotColor = UiTheme.StatusOrange;
            statusDot.Invalidate();
        }
    }

    internal bool IsBusyState
    {
        get { return isBusy; }
    }

    internal string StatusTextForTest
    {
        get { return statusLabel.Text; }
    }

    internal int RowCountForTest
    {
        get { return deviceList.RowCount; }
    }

    internal int RebuildCountForTest
    {
        get { return deviceList.ApplyCountForTest; }
    }

    internal int CardRebuildCountForTest
    {
        get { return deviceList.CardRebuildCountForTest; }
    }

    internal int RowCreateCountForTest
    {
        get { return deviceList.RowCreateCountForTest; }
    }

    internal int RowUpdateCountForTest
    {
        get { return deviceList.RowUpdateCountForTest; }
    }

    internal async void RunBusyActionForTest(string statusText, Action work, string failureContext = "操作失败")
    {
        SetUiBusy(true, false, statusText, UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;
        try
        {
            await Task.Run(work, ct);
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "准备就绪", UiTheme.StatusGreen);
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "操作已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, failureContext + ": " + ex.Message, UiTheme.StatusRed);
            }
        }
    }

    void CancelActiveScan()
    {
        CancellationTokenSource old = activeScanCts;
        activeScanCts = null;
        if (old != null)
        {
            try { old.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    void InvalidateActiveScan()
    {
        unchecked { activeScanId++; }
        CancelActiveScan();
    }

    void CancelAllBackgroundWork()
    {
        InvalidateActiveScan();
        try { formCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    void StartScan(string customBusyText = null)
    {
        var _ = ExecuteScanCoreAsync((onStage1, ct) => discoveryService.ScanAsync(onStage1, ct), customBusyText);
    }

    internal async Task ExecuteScanCoreAsync(
        Func<Action<ScanResult>, CancellationToken, Task<ScanResult>> scanFunc,
        string customBusyText = null)
    {
        if (isActionBusy || IsDisposed || Disposing) return;
        CancelActiveScan();
        long myScanId = ++activeScanId;
        CancellationTokenSource scanCts = CancellationTokenSource.CreateLinkedTokenSource(formCts.Token);
        activeScanCts = scanCts;
        CancellationToken ct = scanCts.Token;
        bool scanFinished = false;

        ReloadConfig();
        string busyText = string.IsNullOrEmpty(customBusyText)
            ? "正在扫描已连接设备与 mDNS 无线调试服务…"
            : customBusyText;
        SetUiBusy(false, true, busyText, UiTheme.AccentBlue);

        ScanResult result = null;
        bool canceled = false;
        Exception scanError = null;
        try
        {
            result = await scanFunc(stage1 =>
            {
                if (Volatile.Read(ref scanFinished) || IsDisposed || Disposing || !IsHandleCreated) return;
                Action applyStage1 = delegate
                {
                    if (Volatile.Read(ref scanFinished) || IsDisposed || Disposing || myScanId != activeScanId || ct.IsCancellationRequested) return;
                    int stage1ConnectedCount = stage1 != null && stage1.Devices != null ? stage1.Devices.Count : 0;
                    ScanResult mergedStage1 = DeviceDiscoveryService.MergePendingStage1(stage1, deviceList.LastScanResult);
                    if (mergedStage1.Devices.Count > 0 || !string.IsNullOrEmpty(mergedStage1.Warning))
                    {
                        ApplyScanResult(mergedStage1);
                        string stageText = stage1ConnectedCount > 0
                            ? string.Format("已发现 {0} 台已连接设备，正在探测无线设备…", stage1ConnectedCount)
                            : (!string.IsNullOrEmpty(mergedStage1.Warning)
                                ? mergedStage1.Warning
                                : "已检查有线连接，正在探测无线设备…");
                        SetUiBusy(false, true, stageText, string.IsNullOrEmpty(mergedStage1.Warning) ? UiTheme.AccentBlue : UiTheme.StatusOrange);
                    }
                    else
                    {
                        SetUiBusy(false, true, "已检查有线连接，正在探测无线设备…", UiTheme.AccentBlue);
                    }
                };
                if (InvokeRequired)
                {
                    try { BeginInvoke(applyStage1); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
                else
                {
                    applyStage1();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            scanError = ex;
        }
        finally
        {
            Volatile.Write(ref scanFinished, true);
        }

        await InvokeOnUiAsync(() =>
        {
            try
            {
                if (canceled)
                {
                    if (!IsDisposed && !Disposing && myScanId == activeScanId && !formCts.IsCancellationRequested)
                    {
                        SetUiBusy(false, false, "扫描已取消", UiTheme.StatusOrange);
                    }
                }
                else if (scanError != null)
                {
                    if (!IsDisposed && !Disposing && myScanId == activeScanId)
                    {
                        SetUiBusy(false, false, "扫描设备失败: " + scanError.Message, UiTheme.StatusRed);
                    }
                }
                else if (!IsDisposed && !Disposing && myScanId == activeScanId && !ct.IsCancellationRequested)
                {
                    ApplyScanResult(result);
                    UpdateStatusTone(result);
                    SetUiBusy(false, false, BuildScanSummary(result), null);
                }
            }
            finally
            {
                if (ReferenceEquals(activeScanCts, scanCts))
                {
                    activeScanCts = null;
                }
                if (!IsDisposed && !Disposing && myScanId == activeScanId && isScanning)
                {
                    SetUiBusy(false, false, null, null);
                }
                scanCts.Dispose();
            }
        }).ConfigureAwait(false);
    }

    Task InvokeOnUiAsync(Action action)
    {
        if (!InvokeRequired || IsDisposed || Disposing || !IsHandleCreated)
        {
            action();
            return Task.CompletedTask;
        }
        TaskCompletionSource<object> tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((Action)(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
        }
        catch (ObjectDisposedException)
        {
            tcs.TrySetResult(null);
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    static string BuildScanSummary(ScanResult result)
    {
        if (!string.IsNullOrEmpty(result.Warning)) return result.Warning;
        if (result.Devices.Count == 0)
        {
            if (result.FilteredStaleMdnsCount > 0)
            {
                return string.Format("未发现可用设备（已忽略 {0} 条离线 mDNS 缓存）", result.FilteredStaleMdnsCount);
            }
            return "未发现设备；可插线，或点击左上角「无线配对…」「手动连接…」";
        }
        if (result.FilteredStaleMdnsCount > 0)
        {
            return string.Format("共发现 {0} 个设备通道（已忽略 {1} 条离线 mDNS 缓存）", result.Devices.Count, result.FilteredStaleMdnsCount);
        }
        return string.Format("共发现 {0} 个设备通道", result.Devices.Count);
    }

    async void RunGlobalAdbAction(string statusText, Func<CancellationToken, Task<string>> work)
    {
        if (isActionBusy) return;
        InvalidateActiveScan();
        SetUiBusy(true, false, statusText, UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            string actionErr = await work(ct);
            serialCache.Clear();
            ScanResult scan = await discoveryService.ScanAsync(null, ct);
            if (IsDisposed || Disposing) return;
            ApplyScanResult(scan);
            if (!string.IsNullOrEmpty(actionErr))
            {
                SetUiBusy(false, false, actionErr, UiTheme.StatusRed);
            }
            else
            {
                UpdateStatusTone(scan);
                SetUiBusy(false, false, BuildScanSummary(scan), null);
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "操作已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "执行 ADB 操作失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing && isActionBusy)
            {
                SetUiBusy(false, false, null, null);
            }
        }
    }

    void OnOpenPairDialog(object sender, EventArgs e)
    {
        if (isActionBusy) return;
        using (PairDialog dlg = new PairDialog(adbClient, lastPairingServices))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Paired)
            {
                serialCache.Clear();
                StartScan(string.IsNullOrEmpty(dlg.ResultMessage) ? "无线配对成功，正在刷新设备列表…" : dlg.ResultMessage);
            }
        }
    }

    void OnOpenManualConnectDialog(object sender, EventArgs e)
    {
        if (isActionBusy) return;
        using (ManualConnectDialog dlg = new ManualConnectDialog(adbClient))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Connected)
            {
                serialCache.Clear();
                StartScan(string.IsNullOrEmpty(dlg.ResultMessage) ? "连接成功，正在刷新设备列表…" : dlg.ResultMessage);
            }
        }
    }

    void OnOpenManageSavedDialog(object sender, EventArgs e)
    {
        if (isActionBusy) return;
        using (SavedEndpointsDialog dlg = new SavedEndpointsDialog(configManager, config))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Saved)
            {
                StartScan("已更新常用地址列表，正在刷新…");
            }
        }
    }

    void ApplyScanResult(ScanResult result, bool forceRebuild = false)
    {
        ScanResult effective = result ?? new ScanResult();
        lastPairingServices.Clear();
        if (effective.PairingServices != null)
        {
            lastPairingServices.AddRange(effective.PairingServices);
        }
        deviceList.SetDevices(effective, config, forceRebuild);
    }

    void OnListSelectionChanged(int checkedCount)
    {
        if (checkedCount > 0)
        {
            connectSelectedBtn.Text = string.Format("投屏所选 ({0})", checkedCount);
            connectSelectedBtn.Visible = true;
        }
        else
        {
            connectSelectedBtn.Visible = false;
        }
    }

    void ExecuteConnectSingle(DeviceItem item)
    {
        List<DeviceItem> single = new List<DeviceItem>();
        single.Add(item);
        ExecuteConnectBatch(single);
    }

    void OnConnectSelectedClick(object sender, EventArgs e)
    {
        List<DeviceItem> selected = deviceList.GetSelectedDevices();

        if (selected.Count == 0)
        {
            statusLabel.Text = "请先勾选至少一个设备，或直接点击对应行右侧的「投屏」。";
            return;
        }
        ExecuteConnectBatch(selected);
    }

    async void ExecuteWirelessAction(DeviceItem item, string displayTitle, bool isDisconnecting)
    {
        if (isActionBusy) return;
        InvalidateActiveScan();
        SetUiBusy(true, false, (isDisconnecting ? "正在断开 [" : "正在连接 [") + displayTitle + "] 无线 ADB…", UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            string detail;
            bool ok;
            if (isDisconnecting)
            {
                var res = await adbClient.DisconnectDeviceAsync(item, ct);
                ok = res.ok;
                detail = ok ? ("已断开 [" + displayTitle + "] 无线连接") : ("断开失败: " + res.error);
            }
            else
            {
                var res = await adbClient.EnsureConnectedAsync(item, ct);
                ok = res.ok;
                detail = ok ? string.Format("已连接 [{0}] ({1})", displayTitle, res.activeSerial) : ("连接失败: " + res.error);
            }

            serialCache.InvalidateDevice(item);
            ScanResult refreshed = await discoveryService.ScanAsync(null, ct);
            if (IsDisposed || Disposing) return;
            ApplyScanResult(refreshed);
            SetUiBusy(false, false, detail, ok ? UiTheme.StatusGreen : UiTheme.StatusRed);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "操作已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "无线连接操作失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing && isActionBusy)
            {
                SetUiBusy(false, false, null, null);
            }
        }
    }

    async void ExecuteConnectBatch(List<DeviceItem> targets)
    {
        if (isActionBusy) return;
        InvalidateActiveScan();
        SetUiBusy(true, false, string.Format("正在连接并启动投屏 ({0} 台设备)…", targets.Count), UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            BatchLaunchResult outcome = await scrcpyLauncher.LaunchBatchAsync(targets, adbClient, DeviceListView.GetDisplayTitle, ct);
            if (ct.IsCancellationRequested || IsDisposed || Disposing) return;

            // 普通成功流程保持安静，仅在存在失败时刷新界面并展开诊断详情。
            if (!outcome.HasFailures)
            {
                Close();
                return;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                serialCache.InvalidateDevice(targets[i]);
            }
            ScanResult refreshed = await discoveryService.ScanAsync(null, ct);
            if (IsDisposed || Disposing) return;
            ApplyScanResult(refreshed);

            SetUiBusy(false, false, outcome.BuildSummary(), UiTheme.StatusRed);
            MessageBox.Show(
                this,
                outcome.BuildDetail(),
                outcome.HasAnyStarted ? "部分设备启动失败" : "设备启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "启动投屏已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "启动投屏失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing && isActionBusy)
            {
                SetUiBusy(false, false, null, null);
            }
        }
    }

    async void ExecuteScreenshot(DeviceItem item, string displayTitle)
    {
        if (isActionBusy) return;
        InvalidateActiveScan();
        bool wasPending = DeviceListView.IsPendingWireless(item.Channel);
        SetUiBusy(true, false, "正在截取 [" + displayTitle + "] 屏幕…", UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;
        Bitmap bmp = null;

        try
        {
            var conn = await adbClient.EnsureConnectedAsync(item, ct);
            if (!conn.ok)
            {
                if (!IsDisposed && !Disposing)
                {
                    SetUiBusy(false, false, string.Format("[{0}] 截屏失败: {1}", displayTitle, conn.error), UiTheme.StatusRed);
                }
                return;
            }

            var shot = await adbClient.CaptureScreenshotAsync(conn.activeSerial, ct);
            if (!shot.ok || shot.bitmap == null)
            {
                if (!IsDisposed && !Disposing)
                {
                    SetUiBusy(false, false, string.Format("[{0}] 截屏失败: {1}", displayTitle, shot.error), UiTheme.StatusRed);
                }
                return;
            }
            bmp = shot.bitmap;

            if (wasPending) serialCache.InvalidateDevice(item);
            ScanResult refreshed = wasPending ? await discoveryService.ScanAsync(null, ct) : null;
            if (IsDisposed || Disposing || !IsHandleCreated) return;

            int w = bmp.Width;
            int h = bmp.Height;
            string clipErr;
            bool copied = TrySetClipboardImage(bmp, out clipErr);
            bmp.Dispose();
            bmp = null;

            if (refreshed != null) ApplyScanResult(refreshed);
            if (copied)
            {
                SetUiBusy(false, false, string.Format("已复制 [{0}] 截图到剪贴板 ({1}x{2})", displayTitle, w, h), UiTheme.StatusGreen);
            }
            else
            {
                SetUiBusy(false, false, string.Format("[{0}] 复制截图到剪贴板失败: {1}", displayTitle, clipErr), UiTheme.StatusRed);
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "截屏已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "截屏失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            if (bmp != null) bmp.Dispose();
            if (!IsDisposed && !Disposing && isActionBusy)
            {
                SetUiBusy(false, false, null, null);
            }
        }
    }

    static bool TrySetClipboardImage(Bitmap bmp, out string error)
    {
        error = "";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetImage(bmp);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (attempt < 2) Thread.Sleep(40);
            }
        }
        return false;
    }

    async void ExecuteOpenTerminal(DeviceItem item, string displayTitle, TerminalMode mode)
    {
        if (isActionBusy || !item.CanConnect) return;
        InvalidateActiveScan();
        bool wasPending = DeviceListView.IsPendingWireless(item.Channel);
        string modeName = mode == TerminalMode.AdbShell ? "ADB Shell" : "本地 ADB 终端";
        SetUiBusy(true, false, "正在打开 [" + displayTitle + "] " + modeName + "…", UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            var conn = await adbClient.EnsureConnectedAsync(item, ct);
            if (!conn.ok)
            {
                if (!IsDisposed && !Disposing)
                {
                    SetUiBusy(false, false, string.Format("[{0}] 打开{1}失败: {2}", displayTitle, modeName, conn.error), UiTheme.StatusRed);
                }
                return;
            }

            var termRes = await Task.Run(delegate
            {
                string launchedWith;
                string err;
                bool ok = terminalLauncher.OpenDeviceTerminal(conn.activeSerial, displayTitle, mode, out launchedWith, out err);
                return (ok, launchedWith, err);
            }, ct);

            if (wasPending) serialCache.InvalidateDevice(item);
            ScanResult refreshed = wasPending ? await discoveryService.ScanAsync(null, ct) : null;
            if (IsDisposed || Disposing) return;
            if (refreshed != null) ApplyScanResult(refreshed);

            if (!termRes.ok)
            {
                SetUiBusy(false, false, string.Format("[{0}] 打开{1}失败: {2}", displayTitle, modeName, termRes.err), UiTheme.StatusRed);
                return;
            }
            SetUiBusy(false, false, string.Format("已打开 [{0}] {1} ({2})", displayTitle, modeName, termRes.launchedWith), UiTheme.StatusGreen);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "打开终端已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                SetUiBusy(false, false, "打开终端失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing && isActionBusy)
            {
                SetUiBusy(false, false, null, null);
            }
        }
    }
}
