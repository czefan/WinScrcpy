using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal sealed class ManualConnectDialog : Form
{
    internal readonly ModernTextBoxHost fullEndpointHost;
    internal readonly ModernTextBoxHost subnetHost;
    internal readonly ModernTextBoxHost hostOctetHost;
    internal readonly ModernTextBoxHost portHost;

    readonly Label statusLabel;
    readonly Button connectButton;
    readonly AdbClient adbClient;
    readonly CancellationTokenSource dialogCts = new CancellationTokenSource();

    bool suppressChangeTracking;
    bool isConnecting;

    public bool Connected { get; private set; }
    public string ResultMessage { get; private set; }

    public ManualConnectDialog(AdbClient adbClient)
    {
        this.adbClient = adbClient;
        Connected = false;

        Text = "手动连接设备";
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
            if (e.KeyCode == Keys.Escape && !isConnecting)
            {
                e.Handled = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        FormClosing += delegate { CancelPendingWork(); };

        SuspendLayout();

        TableLayoutPanel layout = new BufferedTableLayoutPanel();
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.ColumnCount = 2;
        layout.RowCount = 5;
        layout.Padding = UiTheme.Pad(20, 18, 20, 18);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label tipLabel = new Label();
        tipLabel.AutoSize = true;
        tipLabel.MaximumSize = new Size(UiTheme.Scale(460), 0);
        tipLabel.Font = UiTheme.SubFont;
        tipLabel.ForeColor = UiTheme.TextSecondary;
        tipLabel.Margin = UiTheme.Pad(0, 0, 0, 14);
        tipLabel.Text = "输入完整地址或使用快捷栏，点击「连接」执行单次手动连接：";
        layout.Controls.Add(tipLabel, 0, 0);
        layout.SetColumnSpan(tipLabel, 2);

        Label epLabel = MakeInlineLabel("IP:Port", UiTheme.Pad(0, 0, 12, 12));
        layout.Controls.Add(epLabel, 0, 1);

        fullEndpointHost = new ModernTextBoxHost("例如 192.168.1.5:5555");
        fullEndpointHost.Width = UiTheme.Scale(370);
        fullEndpointHost.MinimumSize = new Size(UiTheme.Scale(370), 0);
        fullEndpointHost.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        fullEndpointHost.Margin = UiTheme.Pad(0, 0, 0, 12);
        fullEndpointHost.InnerBox.TextChanged += delegate
        {
            if (suppressChangeTracking || string.IsNullOrWhiteSpace(fullEndpointHost.TextValue)) return;
            suppressChangeTracking = true;
            subnetHost.TextValue = "";
            hostOctetHost.TextValue = "";
            suppressChangeTracking = false;
        };
        layout.Controls.Add(fullEndpointHost, 1, 1);

        Label quickLabel = MakeInlineLabel("快捷输入", UiTheme.Pad(0, 0, 12, 12));
        layout.Controls.Add(quickLabel, 0, 2);

        FlowLayoutPanel quickRow = new BufferedFlowLayoutPanel();
        quickRow.AutoSize = true;
        quickRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        quickRow.WrapContents = false;
        quickRow.BackColor = UiTheme.PageBg;
        quickRow.Anchor = AnchorStyles.Left;
        quickRow.Margin = UiTheme.Pad(0, 0, 0, 12);

        Label fixedPrefix = MakeInlineLabel("192.168.", UiTheme.Pad(0, 0, 4, 0));
        fixedPrefix.Font = UiTheme.BodyBoldFont;

        subnetHost = MakeCompactInput(58, 3, "");
        Label dotLabel = MakeInlineLabel(".", UiTheme.Pad(5, 0, 5, 0));
        hostOctetHost = MakeCompactInput(58, 3, "");
        Label colonLabel = MakeInlineLabel(":", UiTheme.Pad(5, 0, 5, 0));

        suppressChangeTracking = true;
        portHost = MakeCompactInput(78, 5, "5555");
        suppressChangeTracking = false;

        WireQuickSegment(subnetHost, hostOctetHost, '.');
        WireQuickSegment(hostOctetHost, portHost, ':');
        WireQuickEditTracking(portHost);
        portHost.InnerBox.KeyPress += delegate(object s, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };

        quickRow.Controls.Add(fixedPrefix);
        quickRow.Controls.Add(subnetHost);
        quickRow.Controls.Add(dotLabel);
        quickRow.Controls.Add(hostOctetHost);
        quickRow.Controls.Add(colonLabel);
        quickRow.Controls.Add(portHost);
        layout.Controls.Add(quickRow, 1, 2);

        statusLabel = new Label();
        statusLabel.AutoSize = true;
        statusLabel.MaximumSize = new Size(UiTheme.Scale(460), 0);
        statusLabel.Font = UiTheme.SubFont;
        statusLabel.ForeColor = UiTheme.TextSecondary;
        statusLabel.Margin = UiTheme.Pad(0, 2, 0, 14);
        layout.Controls.Add(statusLabel, 0, 3);
        layout.SetColumnSpan(statusLabel, 2);

        connectButton = UiTheme.MakePrimaryButton("连接");
        connectButton.Anchor = AnchorStyles.Right;
        connectButton.Margin = new Padding(0);
        connectButton.Click += OnConnectClick;
        layout.Controls.Add(connectButton, 0, 4);
        layout.SetColumnSpan(connectButton, 2);

        Controls.Add(layout);
        AcceptButton = connectButton;
        fullEndpointHost.BorderLineColor = UiTheme.AccentBlue;
        ActiveControl = fullEndpointHost.InnerBox;

        ResumeLayout(false);
        PerformLayout();
        ClientSize = layout.GetPreferredSize(Size.Empty);
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
        ResumeLayout(false);
        UiTheme.CenterOnOwner(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UiTheme.RevealDialog(this);
    }

    static Label MakeInlineLabel(string text, Padding margin)
    {
        Label lbl = new Label();
        lbl.AutoSize = true;
        lbl.Anchor = AnchorStyles.Left;
        lbl.ForeColor = UiTheme.TextPrimary;
        lbl.Margin = margin;
        lbl.Text = text;
        return lbl;
    }

    static ModernTextBoxHost MakeCompactInput(int widthDp, int maxLength, string initialValue)
    {
        ModernTextBoxHost box = new ModernTextBoxHost("");
        int scaledW = UiTheme.Scale(widthDp);
        box.Width = scaledW;
        box.MinimumSize = new Size(scaledW, 0);
        box.Padding = UiTheme.Pad(6, 7, 6, 7);
        box.Margin = new Padding(0);
        box.InnerBox.MaxLength = maxLength;
        box.InnerBox.TextAlign = HorizontalAlignment.Center;
        if (!string.IsNullOrEmpty(initialValue))
        {
            box.TextValue = initialValue;
        }
        return box;
    }

    void WireQuickEditTracking(ModernTextBoxHost box)
    {
        box.InnerBox.TextChanged += delegate
        {
            if (suppressChangeTracking || string.IsNullOrEmpty(fullEndpointHost.TextValue)) return;
            suppressChangeTracking = true;
            fullEndpointHost.TextValue = "";
            suppressChangeTracking = false;
        };
    }

    void WireQuickSegment(ModernTextBoxHost box, ModernTextBoxHost nextBox, char jumpChar)
    {
        WireQuickEditTracking(box);
        box.InnerBox.KeyPress += delegate(object s, KeyPressEventArgs e)
        {
            if (e.KeyChar == jumpChar || (jumpChar == ':' && e.KeyChar == '.'))
            {
                e.Handled = true;
                nextBox.InnerBox.Focus();
                nextBox.InnerBox.SelectAll();
                return;
            }
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
        };
        box.InnerBox.TextChanged += delegate
        {
            if (!suppressChangeTracking && box.InnerBox.Focused && box.InnerBox.TextLength >= 3)
            {
                nextBox.InnerBox.Focus();
                nextBox.InnerBox.SelectAll();
            }
        };
    }

    internal static string BuildEndpointFromInputs(string fullText, string octet3, string octet4, string portText)
    {
        string trimmedFull = (fullText ?? "").Trim();
        if (trimmedFull.Length > 0) return trimmedFull;

        string o3 = (octet3 ?? "").Trim();
        string o4 = (octet4 ?? "").Trim();
        string p = (portText ?? "").Trim();
        if (o3.Length == 0 && o4.Length == 0) return "";
        if (o3.Length == 0 || o4.Length == 0 || p.Length == 0) return "";
        return "192.168." + o3 + "." + o4 + ":" + p;
    }

    bool TryGetValidatedEndpoint(out string endpoint)
    {
        string raw = BuildEndpointFromInputs(
            fullEndpointHost.TextValue,
            subnetHost.TextValue,
            hostOctetHost.TextValue,
            portHost.TextValue);
        if (!ConfigManager.TryValidateEndpoint(raw, out endpoint))
        {
            statusLabel.ForeColor = UiTheme.TextDanger;
            statusLabel.Text = "请填写完整 IP:Port，或在下方快捷栏补全后两段 IP 数字。";
            return false;
        }
        return true;
    }

    void CancelPendingWork()
    {
        try { dialogCts.Cancel(); }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPendingWork();
            dialogCts.Dispose();
        }
        base.Dispose(disposing);
    }

    async void OnConnectClick(object sender, EventArgs e)
    {
        if (isConnecting || dialogCts.IsCancellationRequested) return;
        string ep;
        if (!TryGetValidatedEndpoint(out ep)) return;

        isConnecting = true;
        connectButton.Enabled = false;
        statusLabel.ForeColor = UiTheme.TextSecondary;
        statusLabel.Text = "正在连接 " + ep + " …";

        try
        {
            (bool ok, string msg) = await adbClient.ConnectEndpointAsync(ep, dialogCts.Token);
            if (IsDisposed || Disposing || dialogCts.IsCancellationRequested) return;
            if (ok)
            {
                Connected = true;
                ResultMessage = "已连接 " + ep + "，正在刷新设备列表…";
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                statusLabel.ForeColor = UiTheme.TextDanger;
                statusLabel.Text = msg;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
            {
                statusLabel.ForeColor = UiTheme.TextDanger;
                statusLabel.Text = "连接过程发生异常: " + ex.Message;
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                isConnecting = false;
                connectButton.Enabled = true;
            }
        }
    }
}
