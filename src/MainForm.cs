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
    AppConfig config;
    AdbClient adbClient;
    DeviceDiscoveryService discoveryService;
    ScrcpyLauncher scrcpyLauncher;
    TerminalLauncher terminalLauncher;
    readonly bool autoScanOnShow;

    readonly TableLayoutPanel topBar;
    readonly Panel listHost;
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

    CheckBox selectAllCheck;
    Label tableCountLabel;
    Control activeTableCard;
    readonly List<CheckBox> rowCheckBoxes = new List<CheckBox>();
    readonly List<PairingServiceInfo> lastPairingServices = new List<PairingServiceInfo>();
    Color statusDotColor = UiTheme.StatusGreen;
    bool isActionBusy;
    bool isScanning;
    bool isBusy { get { return isActionBusy || isScanning; } }
    readonly CancellationTokenSource formCts = new CancellationTokenSource();
    CancellationTokenSource activeScanCts;
    long activeScanId;
    bool syncingCheckBoxes;
    bool inRelayout;
    int lastLayoutWidth = -1;

    public MainForm(ConfigManager configManager, AppConfig config, string initialWarning, bool autoScanOnShow)
    {
        this.configManager = configManager;
        this.config = config;
        InitServices();
        this.autoScanOnShow = autoScanOnShow;

        Text = "WinScrcpy";
        UiTheme.ApplyWindowIcon(this);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = UiTheme.Size(780, 520);
        MinimumSize = UiTheme.Size(660, 400);
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
        topBar.Padding = UiTheme.Pad(18, 10, 18, 10);
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
        refreshBtn.Margin = UiTheme.Pad(0, 0, 8, 0);
        refreshBtn.Click += delegate { StartScan(); };

        pairBtn = UiTheme.MakeToolButton("无线配对…");
        pairBtn.Margin = UiTheme.Pad(0, 0, 8, 0);
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
        connectSelectedBtn.Margin = UiTheme.Pad(0, 0, 8, 0);
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

        listHost = new BufferedPanel();
        listHost.Dock = DockStyle.Fill;
        listHost.AutoScroll = true;
        listHost.BackColor = UiTheme.PageBg;
        listHost.Padding = UiTheme.Pad(18, 16, 18, 16);
        listHost.Resize += delegate { RelayoutCards(); };

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
        statusBar.Padding = UiTheme.Pad(18, 8, 18, 8);
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        statusBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        statusDot = new Panel();
        statusDot.Size = UiTheme.Size(9, 9);
        statusDot.Anchor = AnchorStyles.Left;
        statusDot.Margin = UiTheme.Pad(0, 0, 8, 0);
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
        Controls.Add(listHost);
        Controls.Add(topBorder);
        Controls.Add(topBar);
        Controls.Add(statusBorder);
        Controls.Add(statusBar);

        FormClosing += delegate { CancelAllBackgroundWork(); };
        if (autoScanOnShow)
        {
            Shown += delegate { StartScan(); };
        }
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

    public Panel DeviceListHost { get { return listHost; } }
    public Control ActiveTableCard { get { return activeTableCard; } }
    public CheckBox SelectAllCheckBox { get { return selectAllCheck; } }
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
        if (isBusy) return;
        Point pt = new Point(moreBtn.Width - moreMenu.PreferredSize.Width, moreBtn.Height + UiTheme.Scale(4));
        if (pt.X < -moreBtn.Left) pt.X = 0;
        moreMenu.Show(moreBtn, pt);
    }

    void SetUiBusy(bool actionBusy, bool scanning, string status, Color? dotColor)
    {
        isActionBusy = actionBusy;
        isScanning = scanning;
        bool blocksActions = actionBusy || scanning;
        refreshBtn.Enabled = !actionBusy;
        refreshBtn.Text = scanning ? "扫描中…" : "扫描 (F5)";
        pairBtn.Enabled = !blocksActions;
        manualBtn.Enabled = !blocksActions;
        moreBtn.Enabled = !blocksActions;
        manageSavedItem.Enabled = !blocksActions;
        disconnectAllItem.Enabled = !blocksActions;
        restartAdbItem.Enabled = !blocksActions;
        connectSelectedBtn.Enabled = !blocksActions;
        if (selectAllCheck != null)
        {
            selectAllCheck.Enabled = !blocksActions && HasAnyConnectableRow();
        }
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
        discoveryService = new DeviceDiscoveryService(config, adbClient);
        scrcpyLauncher = new ScrcpyLauncher(config);
        terminalLauncher = new TerminalLauncher(adbClient);
    }

    void ReloadConfig()
    {
        string warn;
        config = configManager.LoadOrCreate(out warn);
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
        get { return rowCheckBoxes.Count; }
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

        try
        {
            ScanResult result = await scanFunc(stage1 =>
            {
                if (Volatile.Read(ref scanFinished) || IsDisposed || Disposing || !IsHandleCreated) return;
                Action applyStage1 = delegate
                {
                    if (Volatile.Read(ref scanFinished) || IsDisposed || Disposing || myScanId != activeScanId || ct.IsCancellationRequested) return;
                    if (stage1 != null && (stage1.Devices.Count > 0 || !string.IsNullOrEmpty(stage1.Warning)))
                    {
                        ApplyScanResult(stage1);
                        string stageText = stage1.Devices.Count > 0
                            ? string.Format("已发现 {0} 台已连接设备，正在探测无线设备…", stage1.Devices.Count)
                            : stage1.Warning;
                        SetUiBusy(false, true, stageText, string.IsNullOrEmpty(stage1.Warning) ? UiTheme.AccentBlue : UiTheme.StatusOrange);
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
            }, ct);

            Volatile.Write(ref scanFinished, true);
            if (IsDisposed || Disposing || myScanId != activeScanId || ct.IsCancellationRequested) return;
            ApplyScanResult(result);
            UpdateStatusTone(result);
            SetUiBusy(false, false, BuildScanSummary(result), null);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref scanFinished, true);
            if (!IsDisposed && !Disposing && myScanId == activeScanId && !formCts.IsCancellationRequested)
            {
                SetUiBusy(false, false, "扫描已取消", UiTheme.StatusOrange);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref scanFinished, true);
            if (!IsDisposed && !Disposing && myScanId == activeScanId)
            {
                SetUiBusy(false, false, "扫描设备失败: " + ex.Message, UiTheme.StatusRed);
            }
        }
        finally
        {
            Volatile.Write(ref scanFinished, true);
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
        if (isBusy) return;
        InvalidateActiveScan();
        SetUiBusy(true, false, statusText, UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            string actionErr = await work(ct);
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
        if (isBusy) return;
        using (PairDialog dlg = new PairDialog(adbClient, lastPairingServices))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Paired)
            {
                StartScan(string.IsNullOrEmpty(dlg.ResultMessage) ? "无线配对成功，正在刷新设备列表…" : dlg.ResultMessage);
            }
        }
    }

    void OnOpenManualConnectDialog(object sender, EventArgs e)
    {
        if (isBusy) return;
        using (ManualConnectDialog dlg = new ManualConnectDialog(adbClient))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Connected)
            {
                StartScan(string.IsNullOrEmpty(dlg.ResultMessage) ? "连接成功，正在刷新设备列表…" : dlg.ResultMessage);
            }
        }
    }

    void OnOpenManageSavedDialog(object sender, EventArgs e)
    {
        if (isBusy) return;
        using (SavedEndpointsDialog dlg = new SavedEndpointsDialog(configManager, config))
        {
            if (UiTheme.ShowDialogCentered(this, dlg) == DialogResult.OK && dlg.Saved)
            {
                StartScan("已更新常用地址列表，正在刷新…");
            }
        }
    }

    void ApplyScanResult(ScanResult result)
    {
        lastPairingServices.Clear();
        if (result.PairingServices != null)
        {
            lastPairingServices.AddRange(result.PairingServices);
        }

        listHost.SuspendLayout();
        while (listHost.Controls.Count > 0)
        {
            int last = listHost.Controls.Count - 1;
            Control old = listHost.Controls[last];
            listHost.Controls.RemoveAt(last);
            old.Dispose();
        }
        rowCheckBoxes.Clear();
        selectAllCheck = null;
        tableCountLabel = null;
        activeTableCard = null;
        lastLayoutWidth = -1;

        if (result.Devices.Count == 0)
        {
            listHost.Controls.Add(BuildEmptyStateView());
            listHost.ResumeLayout(true);
        }
        else
        {
            activeTableCard = BuildGroupedTableCard(result.Devices);
            listHost.Controls.Add(activeTableCard);
            listHost.ResumeLayout(true);
            lastLayoutWidth = -1;
            RelayoutCards();
        }

        SyncSelectionState();
        if (listHost.HorizontalScroll.Visible)
        {
            listHost.HorizontalScroll.Enabled = false;
            listHost.HorizontalScroll.Visible = false;
        }
    }

    Control BuildEmptyStateView()
    {
        TableLayoutPanel host = new TableLayoutPanel();
        host.Dock = DockStyle.Fill;
        host.Tag = "EmptyState";
        host.BackColor = UiTheme.PageBg;
        host.ColumnCount = 3;
        host.RowCount = 3;
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

        RoundedPanel card = new RoundedPanel();
        card.AutoSize = true;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        card.CornerRadius = UiTheme.Scale(12);
        card.ColumnCount = 1;
        card.RowCount = 3;
        card.Padding = UiTheme.Pad(30, 24, 30, 24);
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label heading = new Label();
        heading.AutoSize = true;
        heading.Font = UiTheme.TitleFont;
        heading.ForeColor = UiTheme.TextPrimary;
        heading.Anchor = AnchorStyles.None;
        heading.Margin = UiTheme.Pad(0, 0, 0, 10);
        heading.Text = "暂未检测到可用设备";

        Label tips = new Label();
        tips.AutoSize = true;
        tips.Font = UiTheme.SubFont;
        tips.ForeColor = UiTheme.TextSecondary;
        tips.Margin = UiTheme.Pad(0, 0, 0, 18);
        tips.Text = "• 有线：插入 USB 并在手机确认「允许调试」\n• 无线：开启手机「无线调试」并确保与电脑在同一局域网\n• 首次配对或手动直连：点击左上角「无线配对…」或「手动连接…」";

        Button emptyScanBtn = UiTheme.MakePrimaryButton("立即扫描设备");
        emptyScanBtn.Anchor = AnchorStyles.None;
        emptyScanBtn.Margin = new Padding(0);
        emptyScanBtn.Click += delegate { StartScan(); };

        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(tips, 0, 1);
        card.Controls.Add(emptyScanBtn, 0, 2);

        host.Controls.Add(card, 1, 1);
        return host;
    }

    Control BuildGroupedTableCard(List<DeviceItem> devices)
    {
        RoundedPanel tableCard = new RoundedPanel();
        tableCard.SuspendLayout();
        tableCard.AutoSize = true;
        tableCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tableCard.CornerRadius = UiTheme.Scale(12);
        tableCard.CardBg = UiTheme.SurfaceBg;
        tableCard.BorderLineColor = UiTheme.BorderColor;
        tableCard.Padding = UiTheme.Pad(1, 6, 1, 6);
        tableCard.Margin = new Padding(0);
        tableCard.ColumnCount = 1;
        tableCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // 表头行：左侧复选框与列对齐，右侧展示精简计数。
        TableLayoutPanel headerRow = new TableLayoutPanel();
        headerRow.SuspendLayout();
        headerRow.AutoSize = true;
        headerRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerRow.BackColor = UiTheme.SurfaceBg;
        headerRow.ColumnCount = 3;
        headerRow.RowCount = 1;
        headerRow.Padding = UiTheme.Pad(16, 7, 16, 8);
        headerRow.Margin = new Padding(0);
        headerRow.Tag = "TableHeaderRow";
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        selectAllCheck = new ModernCheckBox();
        selectAllCheck.Anchor = AnchorStyles.Left;
        selectAllCheck.Margin = UiTheme.Pad(0, 0, 12, 0);
        selectAllCheck.CheckedChanged += OnSelectAllCheckedChanged;

        Label headerDeviceLbl = new Label();
        headerDeviceLbl.AutoSize = true;
        headerDeviceLbl.Font = UiTheme.SubFont;
        headerDeviceLbl.ForeColor = UiTheme.TextSecondary;
        headerDeviceLbl.Anchor = AnchorStyles.Left;
        headerDeviceLbl.Margin = new Padding(0);
        headerDeviceLbl.Text = "设备与通道";

        tableCountLabel = new Label();
        tableCountLabel.AutoSize = true;
        tableCountLabel.Font = UiTheme.SubFont;
        tableCountLabel.ForeColor = UiTheme.TextSecondary;
        tableCountLabel.Anchor = AnchorStyles.Right;
        tableCountLabel.Margin = new Padding(0);
        tableCountLabel.Text = string.Format("共 {0} 台", devices.Count);

        headerRow.Controls.Add(selectAllCheck, 0, 0);
        headerRow.Controls.Add(headerDeviceLbl, 1, 0);
        headerRow.Controls.Add(tableCountLabel, 2, 0);
        headerRow.ResumeLayout(false);

        int rowIndex = 0;
        tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableCard.Controls.Add(headerRow, 0, rowIndex++);

        Panel headerDivider = new Panel();
        headerDivider.Height = 1;
        headerDivider.Dock = DockStyle.Fill;
        headerDivider.BackColor = UiTheme.BorderColor;
        headerDivider.Margin = new Padding(0);
        tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableCard.Controls.Add(headerDivider, 0, rowIndex++);

        for (int i = 0; i < devices.Count; i++)
        {
            if (i > 0)
            {
                // Apple 风格内缩细分隔线（左右留出 16px）
                Panel insetDivider = new Panel();
                insetDivider.Height = 1;
                insetDivider.Dock = DockStyle.Fill;
                insetDivider.BackColor = UiTheme.BorderColor;
                insetDivider.Margin = UiTheme.Pad(16, 0, 16, 0);
                tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tableCard.Controls.Add(insetDivider, 0, rowIndex++);
            }

            Control row = BuildDeviceRow(devices[i]);
            tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tableCard.Controls.Add(row, 0, rowIndex++);
        }

        tableCard.RowCount = rowIndex;
        tableCard.ResumeLayout(false);
        return tableCard;
    }

    Control BuildDeviceRow(DeviceItem item)
    {
        ResolvedDeviceConfig resolved = ConfigManager.Resolve(config, item);
        string title = GetDisplayTitle(item, resolved);

        TableLayoutPanel row = new TableLayoutPanel();
        row.SuspendLayout();
        row.AutoSize = true;
        row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row.BackColor = UiTheme.SurfaceBg;
        row.ColumnCount = 3;
        row.RowCount = 1;
        row.Padding = UiTheme.Pad(16, 13, 16, 13);
        row.Margin = new Padding(0);
        row.Tag = item;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ModernCheckBox cb = new ModernCheckBox();
        cb.Anchor = AnchorStyles.Left;
        cb.Margin = UiTheme.Pad(0, 0, 12, 0);
        cb.Enabled = item.CanConnect;
        cb.Tag = item;
        cb.CheckedChanged += delegate { if (!syncingCheckBoxes) SyncSelectionState(); };
        rowCheckBoxes.Add(cb);

        TableLayoutPanel infoTable = new TableLayoutPanel();
        infoTable.SuspendLayout();
        infoTable.AutoSize = true;
        infoTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        infoTable.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        infoTable.BackColor = UiTheme.SurfaceBg;
        infoTable.ColumnCount = 1;
        infoTable.RowCount = 2;
        infoTable.Margin = UiTheme.Pad(0, 0, 14, 0);
        infoTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        infoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        infoTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        FlowLayoutPanel headerFlow = new FlowLayoutPanel();
        headerFlow.SuspendLayout();
        headerFlow.AutoSize = true;
        headerFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerFlow.WrapContents = false;
        headerFlow.BackColor = UiTheme.SurfaceBg;
        headerFlow.Margin = new Padding(0);

        SelectableTextLabel titleLbl = new SelectableTextLabel(UiTheme.TitleFont, UiTheme.TextPrimary, UiTheme.SurfaceBg, true);
        titleLbl.Margin = UiTheme.Pad(0, 2, 10, 0);
        titleLbl.Anchor = AnchorStyles.Left;
        titleLbl.Text = title;
        titleLbl.UpdateMeasuredBounds();

        Label badgeLbl = UiTheme.MakeBadgeLabel(item.BadgeText, item.Channel);
        badgeLbl.Anchor = AnchorStyles.Left;
        badgeLbl.Margin = new Padding(0);

        headerFlow.Controls.Add(titleLbl);
        headerFlow.Controls.Add(badgeLbl);
        headerFlow.ResumeLayout(false);

        SelectableTextLabel subLbl = new SelectableTextLabel(UiTheme.SubFont, UiTheme.TextSecondary, UiTheme.SurfaceBg, true);
        subLbl.Margin = UiTheme.Pad(0, 5, 0, 0);
        subLbl.Text = BuildSubText(item, resolved, title);
        subLbl.UpdateMeasuredBounds();

        infoTable.Controls.Add(headerFlow, 0, 0);
        infoTable.Controls.Add(subLbl, 0, 1);
        infoTable.ResumeLayout(false);

        FlowLayoutPanel actionFlow = new FlowLayoutPanel();
        actionFlow.SuspendLayout();
        actionFlow.AutoSize = true;
        actionFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        actionFlow.WrapContents = false;
        actionFlow.BackColor = UiTheme.SurfaceBg;
        actionFlow.Anchor = AnchorStyles.Right;
        actionFlow.Margin = new Padding(0);

        if (IsPendingWireless(item.Channel))
        {
            AddWirelessActionButton(actionFlow, "连接", item, title, false, item.CanConnect);
        }
        else if (item.Channel == ChannelKind.WirelessConnected)
        {
            AddWirelessActionButton(actionFlow, "断开", item, title, true, item.CanConnect);
        }
        else if (item.Channel == ChannelKind.Offline && AdbScanner.IsWirelessDevice(item))
        {
            if (item.CandidateEndpoints.Count > 0)
            {
                AddWirelessActionButton(actionFlow, "连接", item, title, false, true);
            }
            AddWirelessActionButton(actionFlow, "断开", item, title, true, true);
        }

        Button shotBtn = UiTheme.MakeToolButton("截屏");
        shotBtn.Margin = UiTheme.Pad(0, 0, 8, 0);
        shotBtn.Enabled = item.CanConnect;
        DeviceItem capturedForShot = item;
        string capturedTitle = title;
        shotBtn.Click += delegate { ExecuteScreenshot(capturedForShot, capturedTitle); };

        ContextMenuStrip termMenu = new ContextMenuStrip();
        termMenu.ShowImageMargin = false;
        termMenu.Font = UiTheme.BodyFont;
        termMenu.BackColor = Color.White;
        termMenu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs ce)
        {
            if (isBusy) ce.Cancel = true;
        };

        DeviceItem capturedForTerm = item;
        string capturedTermTitle = title;

        ToolStripMenuItem adbShellItem = new ToolStripMenuItem("进入 ADB Shell");
        adbShellItem.Click += delegate { ExecuteOpenTerminal(capturedForTerm, capturedTermTitle, TerminalMode.AdbShell); };
        ToolStripMenuItem localTermItem = new ToolStripMenuItem("打开本地 ADB 终端");
        localTermItem.Click += delegate { ExecuteOpenTerminal(capturedForTerm, capturedTermTitle, TerminalMode.LocalAdbEnv); };
        termMenu.Items.Add(adbShellItem);
        termMenu.Items.Add(localTermItem);

        Button mirrorBtn = UiTheme.MakePrimaryButton("投屏");
        mirrorBtn.Margin = new Padding(0);
        mirrorBtn.Enabled = item.CanConnect;
        DeviceItem capturedForMirror = item;
        mirrorBtn.Click += delegate { ExecuteConnectSingle(capturedForMirror); };

        actionFlow.Controls.Add(shotBtn);
        actionFlow.Controls.Add(mirrorBtn);
        actionFlow.ResumeLayout(false);

        if (item.CanConnect)
        {
            row.ContextMenuStrip = termMenu;
            infoTable.ContextMenuStrip = termMenu;
            headerFlow.ContextMenuStrip = termMenu;
            badgeLbl.ContextMenuStrip = termMenu;
            actionFlow.ContextMenuStrip = termMenu;
        }
        row.Disposed += delegate { termMenu.Dispose(); };

        row.Controls.Add(cb, 0, 0);
        row.Controls.Add(infoTable, 1, 0);
        row.Controls.Add(actionFlow, 2, 0);
        row.ResumeLayout(false);
        return row;
    }

    void RelayoutCards()
    {
        if (listHost.Controls.Count == 0) return;
        if (listHost.Controls.Count == 1 && object.Equals(listHost.Controls[0].Tag, "EmptyState"))
        {
            ResetStaleHorizontalScroll();
            return;
        }

        int availWidth = Math.Max(UiTheme.Scale(420), listHost.ClientSize.Width - listHost.Padding.Horizontal);
        Control card = listHost.Controls[0];
        if (availWidth == lastLayoutWidth && card.Width == availWidth && !listHost.HorizontalScroll.Visible) return;
        lastLayoutWidth = availWidth;

        int savedScrollY = Math.Max(0, -listHost.AutoScrollPosition.Y);
        listHost.SuspendLayout();
        card.SuspendLayout();
        int innerRowWidth = Math.Max(UiTheme.Scale(380), availWidth - card.Padding.Horizontal);
        for (int i = 0; i < card.Controls.Count; i++)
        {
            Control child = card.Controls[i];
            if (child is TableLayoutPanel)
            {
                child.SuspendLayout();
                if (child.Tag is DeviceItem)
                {
                    ConstrainRowLabels(child, innerRowWidth);
                }
                child.MinimumSize = new Size(innerRowWidth, 0);
                child.MaximumSize = new Size(innerRowWidth, 0);
                child.Width = innerRowWidth;
                child.ResumeLayout(false);
            }
        }
        card.MinimumSize = new Size(availWidth, 0);
        card.MaximumSize = new Size(availWidth, 0);
        card.Left = listHost.Padding.Left;
        card.Top = listHost.Padding.Top - savedScrollY;
        card.Width = availWidth;
        card.ResumeLayout(true);
        listHost.ResumeLayout(true);

        // 从最大化/宽窗口缩回时，WinForms Panel 会缓存旧的最大内容宽度并显示横向滚动条；
        // 重置 AutoScroll 可强制按当前卡片宽度重算 DisplayRectangle。
        if (listHost.HorizontalScroll.Visible || listHost.DisplayRectangle.Width > listHost.ClientSize.Width)
        {
            listHost.AutoScroll = false;
            listHost.AutoScroll = true;
            if (savedScrollY > 0 && listHost.VerticalScroll.Visible)
            {
                listHost.AutoScrollPosition = new Point(0, savedScrollY);
            }
        }

        int afterScrollWidth = Math.Max(UiTheme.Scale(420), listHost.ClientSize.Width - listHost.Padding.Horizontal);
        if (afterScrollWidth != availWidth && !inRelayout)
        {
            inRelayout = true;
            try
            {
                lastLayoutWidth = -1;
                RelayoutCards();
            }
            finally
            {
                inRelayout = false;
            }
        }
    }

    void ResetStaleHorizontalScroll()
    {
        if (listHost.HorizontalScroll.Visible || listHost.DisplayRectangle.Width > listHost.ClientSize.Width)
        {
            listHost.AutoScroll = false;
            listHost.AutoScroll = true;
        }
    }

    static void ConstrainRowLabels(Control row, int rowWidth)
    {
        TableLayoutPanel tlp = row as TableLayoutPanel;
        if (tlp == null || tlp.Controls.Count < 3) return;

        int leftWidth = tlp.Controls[0].PreferredSize.Width + tlp.Controls[0].Margin.Horizontal;
        int rightWidth = tlp.Controls[2].PreferredSize.Width + tlp.Controls[2].Margin.Horizontal;
        int maxTextWidth = Math.Max(UiTheme.Scale(160), rowWidth - tlp.Padding.Horizontal - leftWidth - rightWidth - UiTheme.Scale(20));

        Control info = tlp.Controls[1];
        if (info.Controls.Count >= 2)
        {
            Control hdr = info.Controls[0];
            SelectableTextLabel titleLbl = hdr.Controls.Count >= 1 ? hdr.Controls[0] as SelectableTextLabel : null;
            Control badgeLbl = hdr.Controls.Count >= 2 ? hdr.Controls[1] : null;
            if (titleLbl != null)
            {
                int badgeSpace = badgeLbl != null ? (badgeLbl.PreferredSize.Width + badgeLbl.Margin.Horizontal + titleLbl.Margin.Horizontal) : 0;
                titleLbl.ConstrainWidth(Math.Max(UiTheme.Scale(100), maxTextWidth - badgeSpace));
            }
            SelectableTextLabel subLbl = info.Controls[1] as SelectableTextLabel;
            if (subLbl != null) subLbl.ConstrainWidth(maxTextWidth);
        }
    }

    void AddWirelessActionButton(FlowLayoutPanel container, string text, DeviceItem item, string displayTitle, bool isDisconnecting, bool enabled)
    {
        Button btn = UiTheme.MakeToolButton(text);
        btn.Margin = UiTheme.Pad(0, 0, 8, 0);
        btn.Enabled = enabled;
        btn.Click += delegate { ExecuteWirelessAction(item, displayTitle, isDisconnecting); };
        container.Controls.Add(btn);
    }

    static bool IsPendingWireless(ChannelKind kind)
    {
        return kind == ChannelKind.MdnsPending || kind == ChannelKind.SavedPending;
    }

    static string GetDisplayTitle(DeviceItem item, ResolvedDeviceConfig resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.Alias)) return resolved.Alias;
        if (!string.IsNullOrWhiteSpace(item.Model)) return item.Model;
        if (!string.IsNullOrWhiteSpace(item.HardwareSerial)) return item.HardwareSerial;
        if (!string.IsNullOrWhiteSpace(item.AdbSerial)) return item.AdbSerial;
        if (!string.IsNullOrWhiteSpace(item.BaseMdnsName)) return item.BaseMdnsName;
        return item.PrimaryEndpointDisplay;
    }

    static string BuildSubText(DeviceItem item, ResolvedDeviceConfig resolved, string displayTitle)
    {
        List<string> parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.HardwareSerial)
            && !string.Equals(displayTitle, item.HardwareSerial, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("SN: " + item.HardwareSerial);
        }

        if (IsPendingWireless(item.Channel) && item.CandidateEndpoints.Count > 0)
        {
            if (item.CandidateEndpoints.Count == 1)
            {
                if (!string.Equals(displayTitle, item.CandidateEndpoints[0], StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(item.CandidateEndpoints[0]);
                }
            }
            else
            {
                List<string> fallbacks = new List<string>();
                for (int i = 1; i < item.CandidateEndpoints.Count; i++)
                {
                    fallbacks.Add(item.CandidateEndpoints[i]);
                }
                parts.Add(string.Format("{0} (备选: {1})", item.CandidateEndpoints[0], string.Join(", ", fallbacks.ToArray())));
            }
        }
        else if (!string.IsNullOrWhiteSpace(item.AdbSerial)
            && !string.Equals(displayTitle, item.AdbSerial, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.HardwareSerial, item.AdbSerial, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(item.AdbSerial);
        }

        if (resolved.HasCustomArgs)
        {
            parts.Add("独立参数: " + string.Join(" ", resolved.Args.ToArray()));
        }
        else if (resolved.Args.Count > 0)
        {
            parts.Add("参数: " + string.Join(" ", resolved.Args.ToArray()));
        }

        return string.Join("  ·  ", parts.ToArray());
    }

    void OnSelectAllCheckedChanged(object sender, EventArgs e)
    {
        if (syncingCheckBoxes || selectAllCheck == null) return;
        syncingCheckBoxes = true;
        bool checkAll = selectAllCheck.Checked;
        for (int i = 0; i < rowCheckBoxes.Count; i++)
        {
            if (rowCheckBoxes[i].Enabled) rowCheckBoxes[i].Checked = checkAll;
        }
        syncingCheckBoxes = false;
        SyncSelectionState();
    }

    bool HasAnyConnectableRow()
    {
        for (int i = 0; i < rowCheckBoxes.Count; i++)
        {
            if (rowCheckBoxes[i].Enabled) return true;
        }
        return false;
    }

    void SyncSelectionState()
    {
        int connectableCount = 0;
        int checkedCount = 0;
        for (int i = 0; i < rowCheckBoxes.Count; i++)
        {
            if (!rowCheckBoxes[i].Enabled) continue;
            connectableCount++;
            if (rowCheckBoxes[i].Checked) checkedCount++;
        }

        bool anyConnectable = connectableCount > 0;
        bool allChecked = anyConnectable && checkedCount == connectableCount;
        UpdateSelectionUi(checkedCount, anyConnectable, allChecked);
    }

    void UpdateSelectionUi(int checkedCount, bool anyConnectable, bool allChecked)
    {
        if (selectAllCheck != null)
        {
            syncingCheckBoxes = true;
            selectAllCheck.Enabled = !isBusy && anyConnectable;
            selectAllCheck.Checked = allChecked;
            syncingCheckBoxes = false;
        }

        if (tableCountLabel != null)
        {
            tableCountLabel.Text = checkedCount > 0
                ? string.Format("已选 {0} / {1} 台", checkedCount, rowCheckBoxes.Count)
                : string.Format("共 {0} 台", rowCheckBoxes.Count);
        }

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
        List<DeviceItem> selected = new List<DeviceItem>();
        for (int i = 0; i < rowCheckBoxes.Count; i++)
        {
            if (rowCheckBoxes[i].Checked && rowCheckBoxes[i].Tag is DeviceItem)
            {
                selected.Add((DeviceItem)rowCheckBoxes[i].Tag);
            }
        }

        if (selected.Count == 0)
        {
            statusLabel.Text = "请先勾选至少一个设备，或直接点击对应行右侧的「投屏」。";
            return;
        }
        ExecuteConnectBatch(selected);
    }

    async void ExecuteWirelessAction(DeviceItem item, string displayTitle, bool isDisconnecting)
    {
        if (isBusy) return;
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
        if (isBusy) return;
        InvalidateActiveScan();
        SetUiBusy(true, false, string.Format("正在连接并启动投屏 ({0} 台设备)…", targets.Count), UiTheme.AccentBlue);
        CancellationToken ct = formCts.Token;

        try
        {
            BatchLaunchResult outcome = await scrcpyLauncher.LaunchBatchAsync(targets, adbClient, GetDisplayTitle, ct);
            if (ct.IsCancellationRequested || IsDisposed || Disposing) return;

            // 普通成功流程保持安静，仅在存在失败时刷新界面并展开诊断详情。
            if (!outcome.HasFailures)
            {
                Close();
                return;
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
        if (isBusy) return;
        InvalidateActiveScan();
        bool wasPending = IsPendingWireless(item.Channel);
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
        if (isBusy || !item.CanConnect) return;
        InvalidateActiveScan();
        bool wasPending = IsPendingWireless(item.Channel);
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
