using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class PairDialog : Form
{
    readonly SegmentTabButton tabQrBtn;
    readonly SegmentTabButton tabCodeBtn;
    readonly RoundedPanel segmentHost;
    readonly TableLayoutPanel rootLayout;
    readonly Panel contentHost;

    readonly TableLayoutPanel qrPanel;
    readonly PictureBox qrPictureBox;
    readonly Label qrStatusLabel;
    readonly Button refreshQrButton;

    readonly TableLayoutPanel codePanel;
    readonly Label codeTipLabel;
    readonly ModernTextBoxHost endpointHost;
    readonly ModernTextBoxHost codeHost;
    readonly Label codeStatusLabel;
    readonly Button submitButton;

    readonly AdbClient adbClient;
    readonly string discoveredPairingEndpoint;
    readonly System.Windows.Forms.Timer pollTimer;
    readonly CancellationTokenSource dialogCts = new CancellationTokenSource();

    bool isQrMode = true;
    bool pollingEnabled = true;
    bool adjustingCodeLayout;
    bool pollInFlight;
    bool isPairing;
    string qrServiceName = "";
    string qrPassword = "";

    public bool Paired { get; private set; }
    public string ResultMessage { get; private set; }

    public PairDialog(AdbClient adbClient, List<PairingServiceInfo> pairingServices)
    {
        this.adbClient = adbClient;
        this.discoveredPairingEndpoint = (pairingServices != null && pairingServices.Count > 0) ? pairingServices[0].Endpoint : "";
        Paired = false;

        Text = "无线调试配对";
        UiTheme.ApplyWindowIcon(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = UiTheme.PageBg;
        Font = UiTheme.BodyFont;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !isPairing)
            {
                e.Handled = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };

        SuspendLayout();

        rootLayout = new BufferedTableLayoutPanel();
        rootLayout.AutoSize = true;
        rootLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        rootLayout.ColumnCount = 1;
        rootLayout.RowCount = 2;
        rootLayout.Padding = UiTheme.Pad(20, 18, 20, 18);
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Apple 风格分段切换条：扫码配对 | 配对码配对
        segmentHost = new RoundedPanel();
        segmentHost.AutoSize = true;
        segmentHost.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        segmentHost.CardBg = Color.FromArgb(233, 233, 238);
        segmentHost.BorderLineColor = Color.FromArgb(222, 222, 228);
        segmentHost.CornerRadius = UiTheme.Scale(8);
        segmentHost.ColumnCount = 2;
        segmentHost.RowCount = 1;
        segmentHost.Padding = UiTheme.Pad(3, 3, 3, 3);
        segmentHost.Margin = UiTheme.Pad(0, 0, 0, 14);
        segmentHost.MinimumSize = new Size(UiTheme.Scale(460), 0);
        segmentHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        segmentHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        segmentHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        segmentHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        tabQrBtn = new SegmentTabButton("扫码配对");
        tabQrBtn.Margin = UiTheme.Pad(0, 0, 2, 0);
        tabQrBtn.Click += delegate { SwitchMode(true); };

        tabCodeBtn = new SegmentTabButton("配对码配对");
        tabCodeBtn.Margin = UiTheme.Pad(2, 0, 0, 0);
        tabCodeBtn.Click += delegate { SwitchMode(false); };

        segmentHost.Controls.Add(tabQrBtn, 0, 0);
        segmentHost.Controls.Add(tabCodeBtn, 1, 0);
        rootLayout.Controls.Add(segmentHost, 0, 0);

        // Tab 1: 扫码配对面板
        qrPanel = new BufferedTableLayoutPanel();
        qrPanel.AutoSize = true;
        qrPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        qrPanel.ColumnCount = 1;
        qrPanel.RowCount = 3;
        qrPanel.Margin = new Padding(0);
        qrPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label qrTipLabel = new Label();
        qrTipLabel.AutoSize = true;
        qrTipLabel.MaximumSize = new Size(UiTheme.Scale(460), 0);
        qrTipLabel.Font = UiTheme.SubFont;
        qrTipLabel.ForeColor = UiTheme.TextSecondary;
        qrTipLabel.Margin = UiTheme.Pad(0, 0, 0, 14);
        qrTipLabel.Text = "在手机「开发者选项 -> 无线调试 -> 使用二维码配对设备」扫描下方二维码：";
        qrPanel.Controls.Add(qrTipLabel, 0, 0);

        RoundedPanel qrCard = new RoundedPanel();
        qrCard.AutoSize = true;
        qrCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        qrCard.CardBg = Color.White;
        qrCard.BorderLineColor = UiTheme.BorderColor;
        qrCard.CornerRadius = UiTheme.Scale(12);
        qrCard.ColumnCount = 1;
        qrCard.RowCount = 1;
        qrCard.Padding = UiTheme.Pad(10, 10, 10, 10);
        qrCard.Margin = UiTheme.Pad(0, 0, 0, 12);
        qrCard.Anchor = AnchorStyles.None;

        qrPictureBox = new PictureBox();
        qrPictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
        qrPictureBox.BackColor = Color.White;
        qrPictureBox.Margin = new Padding(0);
        qrCard.Controls.Add(qrPictureBox, 0, 0);
        qrPanel.Controls.Add(qrCard, 0, 1);

        TableLayoutPanel qrFooter = new BufferedTableLayoutPanel();
        qrFooter.AutoSize = true;
        qrFooter.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        qrFooter.ColumnCount = 2;
        qrFooter.RowCount = 1;
        qrFooter.Margin = new Padding(0);
        qrFooter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        qrFooter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        qrFooter.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        qrStatusLabel = new Label();
        qrStatusLabel.AutoSize = true;
        qrStatusLabel.MaximumSize = new Size(UiTheme.Scale(330), 0);
        qrStatusLabel.Anchor = AnchorStyles.Left;
        qrStatusLabel.Font = UiTheme.SubFont;
        qrStatusLabel.ForeColor = UiTheme.TextSecondary;
        qrStatusLabel.Margin = UiTheme.Pad(0, 4, 12, 0);
        qrStatusLabel.Text = "等待手机扫码，检测到服务将自动完成配对…";
        qrFooter.Controls.Add(qrStatusLabel, 0, 0);

        refreshQrButton = UiTheme.MakeToolButton("刷新二维码");
        refreshQrButton.Anchor = AnchorStyles.Right;
        refreshQrButton.Margin = new Padding(0);
        refreshQrButton.Click += delegate { RegenerateQrCode(); };
        qrFooter.Controls.Add(refreshQrButton, 1, 0);

        qrPanel.Controls.Add(qrFooter, 0, 2);

        // Tab 2: 配对码配对面板
        codePanel = new BufferedTableLayoutPanel();
        codePanel.AutoSize = true;
        codePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        codePanel.ColumnCount = 2;
        codePanel.RowCount = 5;
        codePanel.Margin = new Padding(0);
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        codePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        codeTipLabel = new Label();
        codeTipLabel.AutoSize = true;
        codeTipLabel.MaximumSize = new Size(UiTheme.Scale(460), 0);
        codeTipLabel.Font = UiTheme.SubFont;
        codeTipLabel.ForeColor = UiTheme.TextSecondary;
        codeTipLabel.Margin = UiTheme.Pad(0, 0, 0, 20);
        codeTipLabel.Text = "在手机「开发者选项 -> 无线调试 -> 使用配对码配对设备」查看地址与配对码：";
        codePanel.Controls.Add(codeTipLabel, 0, 0);
        codePanel.SetColumnSpan(codeTipLabel, 2);

        Label epLabel = new Label();
        epLabel.AutoSize = true;
        epLabel.Anchor = AnchorStyles.Left;
        epLabel.ForeColor = UiTheme.TextPrimary;
        epLabel.Margin = UiTheme.Pad(0, 0, 16, 14);
        epLabel.Text = "IP:Port";
        codePanel.Controls.Add(epLabel, 0, 1);

        endpointHost = new ModernTextBoxHost("例如 192.168.1.5:37891");
        endpointHost.Width = UiTheme.Scale(370);
        endpointHost.MinimumSize = new Size(UiTheme.Scale(370), 0);
        endpointHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        endpointHost.Margin = UiTheme.Pad(0, 0, 0, 14);
        if (!string.IsNullOrEmpty(discoveredPairingEndpoint))
        {
            endpointHost.TextValue = discoveredPairingEndpoint;
        }
        codePanel.Controls.Add(endpointHost, 1, 1);

        Label codeLabel = new Label();
        codeLabel.AutoSize = true;
        codeLabel.Anchor = AnchorStyles.Left;
        codeLabel.ForeColor = UiTheme.TextPrimary;
        codeLabel.Margin = UiTheme.Pad(0, 0, 16, 14);
        codeLabel.Text = "配对码";
        codePanel.Controls.Add(codeLabel, 0, 2);

        codeHost = new ModernTextBoxHost("6 位数字配对码");
        codeHost.Width = UiTheme.Scale(240);
        codeHost.MinimumSize = new Size(UiTheme.Scale(240), 0);
        codeHost.InnerBox.MaxLength = 12;
        codeHost.Margin = UiTheme.Pad(0, 0, 0, 14);
        codePanel.Controls.Add(codeHost, 1, 2);

        codeStatusLabel = new Label();
        codeStatusLabel.AutoSize = true;
        codeStatusLabel.MaximumSize = new Size(UiTheme.Scale(460), 0);
        codeStatusLabel.Font = UiTheme.SubFont;
        codeStatusLabel.ForeColor = UiTheme.TextDanger;
        codeStatusLabel.Margin = UiTheme.Pad(0, 2, 0, 14);
        codePanel.Controls.Add(codeStatusLabel, 0, 3);
        codePanel.SetColumnSpan(codeStatusLabel, 2);

        submitButton = UiTheme.MakePrimaryButton("配对");
        submitButton.Anchor = AnchorStyles.Right;
        submitButton.Margin = new Padding(0);
        submitButton.Click += OnSubmitCodeClick;
        codePanel.Controls.Add(submitButton, 0, 4);
        codePanel.SetColumnSpan(submitButton, 2);

        RegenerateQrCode();

        // 将两个面板放进固定尺寸的共享容器内，切换页签时不再缩放顶层窗口，避免闪烁跳动。
        Size qrPref = qrPanel.GetPreferredSize(Size.Empty);
        Size codePref = codePanel.GetPreferredSize(Size.Empty);
        int hostW = Math.Max(UiTheme.Scale(460), Math.Max(qrPref.Width, codePref.Width));
        int hostH = Math.Max(qrPref.Height, codePref.Height) + UiTheme.Scale(14);

        qrPanel.Dock = DockStyle.Top;
        codePanel.Visible = false;

        contentHost = new BufferedPanel();
        contentHost.Size = new Size(hostW, hostH);
        contentHost.MinimumSize = new Size(hostW, hostH);
        contentHost.BackColor = UiTheme.PageBg;
        contentHost.Margin = new Padding(0);
        contentHost.Controls.Add(qrPanel);
        contentHost.Controls.Add(codePanel);
        codePanel.SizeChanged += delegate { CenterCodePanel(); };
        contentHost.SizeChanged += delegate { CenterCodePanel(); };

        rootLayout.Controls.Add(contentHost, 0, 1);
        Controls.Add(rootLayout);

        pollTimer = new System.Windows.Forms.Timer();
        pollTimer.Interval = 900;
        pollTimer.Tick += OnPollTimerTick;

        SwitchMode(true);
        ResumeLayout(false);
        PerformLayout();
        CenterCodePanel();
        ClientSize = rootLayout.GetPreferredSize(Size.Empty);

        Shown += delegate
        {
            UpdatePollTimerState();
            if (!isQrMode)
            {
                FocusCodeInput();
            }
        };
        FormClosing += delegate { StopBackgroundWork(); };
        FormClosed += delegate { StopBackgroundWork(); };
    }

    internal bool IsBackgroundPollingActiveForTest
    {
        get { return pollingEnabled || (pollTimer != null && pollTimer.Enabled); }
    }

    internal bool IsDialogCancelledForTest
    {
        get { return dialogCts.IsCancellationRequested; }
    }

    void StopBackgroundWork()
    {
        pollingEnabled = false;
        if (pollTimer != null) pollTimer.Stop();
        try { dialogCts.Cancel(); }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopBackgroundWork();
            if (pollTimer != null)
            {
                pollTimer.Dispose();
            }
            dialogCts.Dispose();
            if (qrPictureBox != null)
            {
                DisposeQrImage();
            }
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.PrepareDialogHandle(Handle);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SuspendLayout();
        PerformLayout();
        CenterCodePanel();
        ResumeLayout(false);
        UiTheme.CenterOnOwner(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UiTheme.RevealDialog(this);
    }

    internal void SetPollingEnabledForTest(bool enabled)
    {
        pollingEnabled = enabled;
        UpdatePollTimerState();
    }

    internal void SelectTabForTest(bool qrMode)
    {
        SwitchMode(qrMode);
    }

    internal void GetCodeSpacingForTest(out int gapTabToTip, out int gapTipToInput)
    {
        int segBottom = segmentHost.PointToScreen(new Point(0, segmentHost.Height)).Y;
        int tipTop = codeTipLabel.PointToScreen(Point.Empty).Y;
        int tipBottom = tipTop + codeTipLabel.Height;
        int inputTop = endpointHost.PointToScreen(Point.Empty).Y;
        gapTabToTip = tipTop - segBottom;
        gapTipToInput = inputTop - tipBottom;
    }

    void SwitchMode(bool qrMode)
    {
        if (isPairing) return;
        isQrMode = qrMode;

        contentHost.SuspendLayout();
        tabQrBtn.IsActive = qrMode;
        tabCodeBtn.IsActive = !qrMode;

        qrPanel.Visible = qrMode;
        codePanel.Visible = !qrMode;
        codeStatusLabel.Text = " ";
        if (!qrMode) CenterCodePanel();
        contentHost.ResumeLayout(true);

        AcceptButton = qrMode ? null : submitButton;

        UpdatePollTimerState();
        if (!qrMode && Visible)
        {
            FocusCodeInput();
        }
    }

    void CenterCodePanel()
    {
        if (adjustingCodeLayout || contentHost == null || codePanel == null || codeTipLabel == null) return;
        adjustingCodeLayout = true;
        try
        {
            int hostW = contentHost.ClientSize.Width;
            int hostH = contentHost.ClientSize.Height;
            if (hostW <= 0 || hostH <= 0) return;

            int segGap = (segmentHost != null && segmentHost.Height > 0 && contentHost.Top > 0)
                ? Math.Max(0, contentHost.Top - segmentHost.Bottom)
                : (segmentHost != null ? segmentHost.Margin.Bottom : UiTheme.Scale(14));
            int outerBottom = rootLayout != null ? rootLayout.Padding.Bottom : UiTheme.Scale(18);

            int curTipBottom = codeTipLabel.Margin.Bottom;
            Size curPref = codePanel.GetPreferredSize(Size.Empty);
            int baseCodeH = Math.Max(0, curPref.Height - curTipBottom);
            int freeY = Math.Max(0, hostH - baseCodeH);

            // 使「Tab -> 说明文字」「说明文字 -> 输入框」「按钮 -> 底部边距」三段垂直留白保持均衡。
            int g = (segGap + freeY + outerBottom) / 3;
            int minTip = UiTheme.Scale(14);
            int tipBottom = Math.Max(minTip, g);
            int maxEqualTip = (freeY + segGap) / 2;
            if (tipBottom > maxEqualTip)
            {
                tipBottom = Math.Max(minTip, maxEqualTip);
            }
            int topInHost = Math.Max(0, tipBottom - segGap);
            if (topInHost + tipBottom > freeY)
            {
                tipBottom = Math.Max(UiTheme.Scale(8), Math.Min(tipBottom, freeY));
                topInHost = Math.Max(0, freeY - tipBottom);
            }

            bool marginChanged = codeTipLabel.Margin.Bottom != tipBottom;
            if (marginChanged)
            {
                codeTipLabel.Margin = new Padding(0, 0, 0, tipBottom);
                codePanel.PerformLayout();
            }
            Size finalPref = marginChanged ? codePanel.GetPreferredSize(Size.Empty) : curPref;
            int x = Math.Max(0, (hostW - finalPref.Width) / 2);
            codePanel.Location = new Point(x, topInHost);
        }
        finally
        {
            adjustingCodeLayout = false;
        }
    }

    void FocusCodeInput()
    {
        if (!string.IsNullOrEmpty(discoveredPairingEndpoint))
        {
            codeHost.InnerBox.Focus();
        }
        else
        {
            endpointHost.InnerBox.Focus();
        }
    }

    void UpdatePollTimerState()
    {
        bool shouldPoll = Visible && pollingEnabled && isQrMode && !isPairing && adbClient != null;
        if (shouldPoll && !pollTimer.Enabled)
        {
            pollTimer.Start();
        }
        else if (!shouldPoll && pollTimer.Enabled)
        {
            pollTimer.Stop();
        }
    }

    void RegenerateQrCode()
    {
        if (isPairing) return;
        qrServiceName = "winscrcpy-" + GenerateRandomToken(8);
        qrPassword = GenerateRandomToken(10);
        string payload = "WIFI:T:ADB;S:" + qrServiceName + ";P:" + qrPassword + ";;";

        bool[,] matrix = QrEncoder.Encode(payload);
        int modulePx = Math.Max(4, UiTheme.Scale(4));
        Bitmap bmp = QrEncoder.RenderBitmap(matrix, modulePx, 3);

        DisposeQrImage();
        qrPictureBox.Image = bmp;
        qrStatusLabel.ForeColor = UiTheme.TextSecondary;
        qrStatusLabel.Text = "等待手机扫码，检测到服务将自动完成配对…";
    }

    void DisposeQrImage()
    {
        Image old = qrPictureBox.Image;
        if (old != null)
        {
            qrPictureBox.Image = null;
            old.Dispose();
        }
    }

    async void OnPollTimerTick(object sender, EventArgs e)
    {
        if (!pollingEnabled || !isQrMode || isPairing || pollInFlight || adbClient == null || dialogCts.IsCancellationRequested) return;
        pollInFlight = true;

        string targetService = qrServiceName;
        string targetPassword = qrPassword;

        try
        {
            List<PairingServiceInfo> services = await adbClient.ListPairingServicesAsync(dialogCts.Token);
            if (IsDisposed || Disposing || dialogCts.IsCancellationRequested) return;
            if (!isQrMode || isPairing) return;

            PairingServiceInfo matched = FindMatchingPairingService(services, targetService);
            if (matched != null && string.Equals(targetService, qrServiceName, StringComparison.OrdinalIgnoreCase))
            {
                StartQrPairing(matched.Endpoint, targetPassword);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            pollInFlight = false;
        }
    }

    async void StartQrPairing(string endpoint, string password)
    {
        if (isPairing || dialogCts.IsCancellationRequested) return;
        isPairing = true;
        UpdatePollTimerState();
        tabQrBtn.Enabled = false;
        tabCodeBtn.Enabled = false;
        refreshQrButton.Enabled = false;
        qrStatusLabel.ForeColor = UiTheme.AccentBlue;
        qrStatusLabel.Text = "已识别设备 " + endpoint + "，正在配对…";

        try
        {
            (bool ok, string msg) = await adbClient.PairEndpointAsync(endpoint, password, dialogCts.Token);
            if (IsDisposed || Disposing || dialogCts.IsCancellationRequested) return;
            if (ok)
            {
                StopBackgroundWork();
                Paired = true;
                ResultMessage = "扫码配对成功 (" + endpoint + ")，正在刷新设备列表…";
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                qrStatusLabel.ForeColor = UiTheme.TextDanger;
                qrStatusLabel.Text = "配对失败（" + msg + "），请刷新二维码重试或改用配对码";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                qrStatusLabel.ForeColor = UiTheme.TextDanger;
                qrStatusLabel.Text = "配对失败（" + ex.Message + "），请刷新二维码重试或改用配对码";
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                isPairing = false;
                tabQrBtn.Enabled = true;
                tabCodeBtn.Enabled = true;
                refreshQrButton.Enabled = true;
                UpdatePollTimerState();
            }
        }
    }

    async void OnSubmitCodeClick(object sender, EventArgs e)
    {
        if (isPairing || dialogCts.IsCancellationRequested) return;
        string ep = endpointHost.TextValue;
        string code = codeHost.TextValue;
        if (string.IsNullOrWhiteSpace(ep) || string.IsNullOrWhiteSpace(code))
        {
            codeStatusLabel.ForeColor = UiTheme.TextDanger;
            codeStatusLabel.Text = "请填写 IP:Port 与 6 位配对码。";
            return;
        }

        isPairing = true;
        UpdatePollTimerState();
        submitButton.Enabled = false;
        tabQrBtn.Enabled = false;
        tabCodeBtn.Enabled = false;
        codeStatusLabel.ForeColor = UiTheme.TextSecondary;
        codeStatusLabel.Text = "正在配对 " + ep + " …";

        try
        {
            (bool ok, string msg) = await adbClient.PairEndpointAsync(ep, code, dialogCts.Token);
            if (IsDisposed || Disposing || dialogCts.IsCancellationRequested) return;
            if (ok)
            {
                StopBackgroundWork();
                Paired = true;
                ResultMessage = "无线配对成功 (" + ep + ")，正在刷新设备列表…";
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                codeStatusLabel.ForeColor = UiTheme.TextDanger;
                codeStatusLabel.Text = msg;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                codeStatusLabel.ForeColor = UiTheme.TextDanger;
                codeStatusLabel.Text = "配对过程发生异常: " + ex.Message;
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                isPairing = false;
                submitButton.Enabled = true;
                tabQrBtn.Enabled = true;
                tabCodeBtn.Enabled = true;
            }
        }
    }

    internal static PairingServiceInfo FindMatchingPairingService(List<PairingServiceInfo> services, string expectedInstance)
    {
        if (services == null || string.IsNullOrEmpty(expectedInstance)) return null;
        for (int i = 0; i < services.Count; i++)
        {
            PairingServiceInfo s = services[i];
            if (s == null || string.IsNullOrWhiteSpace(s.InstanceName)) continue;
            if (string.Equals(s.InstanceName, expectedInstance, StringComparison.OrdinalIgnoreCase)
                || s.InstanceName.StartsWith(expectedInstance + " ", StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    static string GenerateRandomToken(int length)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }
        return new string(chars);
    }

    sealed class SegmentTabButton : Button
    {
        bool isActive;

        public SegmentTabButton(string text)
        {
            Text = text;
            AutoSize = false;
            Height = UiTheme.Scale(36);
            Dock = DockStyle.Fill;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsActive
        {
            get { return isActive; }
            set
            {
                isActive = value;
                Invalidate();
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color trackBg = Color.FromArgb(233, 233, 238);
            e.Graphics.Clear(trackBg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float radius = UiTheme.Scale(6);
            if (isActive)
            {
                Color activeBg = Enabled ? Color.White : Color.FromArgb(245, 245, 247);
                Color activeBorder = Enabled ? Color.FromArgb(218, 218, 224) : Color.FromArgb(228, 228, 232);
                RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath path = UiTheme.CreateRoundedRect(rect, radius))
                using (SolidBrush brush = new SolidBrush(activeBg))
                using (Pen border = new Pen(activeBorder, 1f))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(border, path);
                }
            }

            if (Focused && ShowFocusCues && Enabled)
            {
                RectangleF focusRect = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
                using (GraphicsPath focusPath = UiTheme.CreateRoundedRect(focusRect, Math.Max(2f, radius - 2f)))
                using (Pen focusPen = new Pen(UiTheme.AccentBlue, 1f))
                {
                    focusPen.DashStyle = DashStyle.Dot;
                    e.Graphics.DrawPath(focusPen, focusPath);
                }
            }

            Font drawFont = isActive ? UiTheme.BodyBoldFont : UiTheme.SubFont;
            Color drawColor = !Enabled ? UiTheme.TextDisabled : (isActive ? UiTheme.TextPrimary : UiTheme.TextSecondary);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                drawFont,
                ClientRectangle,
                drawColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}
