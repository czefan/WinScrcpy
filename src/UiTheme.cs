using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class UiTheme
{
    public static readonly Font BodyFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Regular);
    public static readonly Font BodyBoldFont = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
    public static readonly Font TitleFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
    public static readonly Font SubFont = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Regular);
    public static readonly Font BadgeFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);

    public static readonly Color PageBg = Color.FromArgb(245, 245, 247);
    public static readonly Color SurfaceBg = Color.White;
    public static readonly Color StatusBg = Color.FromArgb(245, 245, 247);
    public static readonly Color BorderColor = Color.FromArgb(229, 229, 234);
    public static readonly Color InputBorder = Color.FromArgb(209, 209, 214);
    public static readonly Color TextPrimary = Color.FromArgb(29, 29, 31);
    public static readonly Color TextSecondary = Color.FromArgb(110, 110, 115);
    public static readonly Color AccentBlue = Color.FromArgb(0, 122, 255);
    public static readonly Color StatusGreen = Color.FromArgb(52, 199, 89);
    public static readonly Color StatusRed = Color.FromArgb(255, 59, 48);
    public static readonly Color StatusOrange = Color.FromArgb(255, 159, 10);
    public static readonly Color TextDanger = Color.FromArgb(196, 30, 40);
    public static readonly Color TextDisabled = Color.FromArgb(174, 174, 178);

    private static Icon appIcon;
    private static bool appIconLoaded;

    const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    const int DWMWA_CLOAK = 13;
    const int EM_SETCUEBANNER = 0x1501;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public static void PrepareDialogHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int val = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref val, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref val, sizeof(int));
        }
        catch (Exception) { }
    }

    public static void RevealDialog(Form dialog)
    {
        if (dialog == null || dialog.IsDisposed) return;
        try
        {
            dialog.Refresh();
        }
        finally
        {
            if (!dialog.IsDisposed && dialog.IsHandleCreated)
            {
                try
                {
                    int val = 0;
                    DwmSetWindowAttribute(dialog.Handle, DWMWA_CLOAK, ref val, sizeof(int));
                }
                catch (Exception) { }
            }
        }
    }

    public static void SetCueBanner(TextBox box, string cue)
    {
        if (box == null || string.IsNullOrEmpty(cue) || !box.IsHandleCreated) return;
        try
        {
            SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, cue);
        }
        catch (Exception) { }
    }

    public static void ApplyWindowIcon(Form form)
    {
        if (form == null) return;
        if (!appIconLoaded)
        {
            appIconLoaded = true;
            try
            {
                using (Stream iconStream = typeof(UiTheme).Assembly.GetManifestResourceStream("WinScrcpy.app.ico"))
                {
                    if (iconStream != null)
                    {
                        appIcon = new Icon(iconStream);
                    }
                }
            }
            catch
            {
                appIcon = null;
            }
            if (appIcon == null)
            {
                try
                {
                    appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch
                {
                    appIcon = null;
                }
            }
        }
        if (appIcon != null)
        {
            form.Icon = appIcon;
        }
    }

    public static float GetDpiScale(Control control)
    {
        if (control == null || control.DeviceDpi <= 0) return 1f;
        float ratio = control.DeviceDpi / 96f;
        return ratio < 1f ? 1f : ratio;
    }

    public static int Scale(Control control, int dp)
    {
        return control != null ? control.LogicalToDeviceUnits(dp) : dp;
    }

    public static Size Size(Control control, int w, int h)
    {
        return control != null ? control.LogicalToDeviceUnits(new Size(w, h)) : new Size(w, h);
    }

    public static Padding Pad(Control control, int left, int top, int right, int bottom)
    {
        return new Padding(Scale(control, left), Scale(control, top), Scale(control, right), Scale(control, bottom));
    }

    public static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = radius * 2f;
        if (d <= 0.5f || bounds.Width < 2f || bounds.Height < 2f)
        {
            path.AddRectangle(bounds);
            return path;
        }
        if (d > bounds.Width) d = bounds.Width;
        if (d > bounds.Height) d = bounds.Height;

        path.AddArc(bounds.X, bounds.Y, d, d, 180f, 90f);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270f, 90f);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public static Button MakeToolButton(string text)
    {
        return new ModernButton(text, false);
    }

    public static Button MakePrimaryButton(string text)
    {
        return new ModernButton(text, true);
    }

    public static Label MakeBadgeLabel(string text, ChannelKind channel)
    {
        return new PillBadge(text, channel);
    }

    public static Color ResolveParentBg(Control parent, Color fallback)
    {
        if (parent == null || parent.BackColor == Color.Transparent) return fallback;
        return parent.BackColor;
    }

    public static DialogResult ShowDialogCentered(Form owner, Form dialog)
    {
        if (dialog == null) return DialogResult.Cancel;
        if (owner != null)
        {
            dialog.StartPosition = FormStartPosition.CenterParent;
        }
        return dialog.ShowDialog(owner);
    }

    public static void CenterOnOwner(Form dialog)
    {
        if (dialog == null || dialog.Owner == null || dialog.StartPosition != FormStartPosition.CenterParent) return;
        CenterOnForm(dialog.Owner, dialog);
    }

    static void CenterOnForm(Form owner, Form dialog)
    {
        if (owner == null || dialog == null) return;
        int x = owner.Left + (owner.Width - dialog.Width) / 2;
        int y = owner.Top + (owner.Height - dialog.Height) / 2;
        Screen screen = Screen.FromControl(owner);
        Rectangle area = screen.WorkingArea;
        if (area.IntersectsWith(owner.Bounds))
        {
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + dialog.Width > area.Right) x = Math.Max(area.Left, area.Right - dialog.Width);
            if (y + dialog.Height > area.Bottom) y = Math.Max(area.Top, area.Bottom - dialog.Height);
        }
        dialog.Location = new Point(x, y);
    }
}

internal sealed class ModernButton : Button
{
    readonly bool isPrimary;
    bool hovered;
    bool pressed;

    public ModernButton(string text, bool isPrimary)
    {
        this.isPrimary = isPrimary;
        Text = text;
        Font = isPrimary ? UiTheme.BodyBoldFont : UiTheme.BodyFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size textSize = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        int w = textSize.Width + LogicalToDeviceUnits(24);
        int h = Math.Max(LogicalToDeviceUnits(32), textSize.Height + LogicalToDeviceUnits(12));
        return new Size(w, h);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            pressed = true;
            Invalidate();
        }
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.ResolveParentBg(Parent, UiTheme.SurfaceBg));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Color fill;
        Color border;
        Color fg;

        if (!Enabled)
        {
            fill = isPrimary ? UiTheme.TextDisabled : Color.FromArgb(250, 250, 252);
            border = isPrimary ? fill : Color.FromArgb(229, 229, 234);
            fg = isPrimary ? Color.White : UiTheme.TextDisabled;
        }
        else if (isPrimary)
        {
            fill = pressed ? Color.FromArgb(0, 88, 189)
                 : (hovered ? Color.FromArgb(0, 108, 230) : UiTheme.AccentBlue);
            border = fill;
            fg = Color.White;
        }
        else
        {
            fill = pressed ? Color.FromArgb(238, 238, 242)
                 : (hovered ? Color.FromArgb(249, 249, 251) : Color.White);
            border = hovered ? Color.FromArgb(180, 180, 188) : UiTheme.InputBorder;
            fg = UiTheme.TextPrimary;
        }

        RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = LogicalToDeviceUnits(7);
        using (GraphicsPath path = UiTheme.CreateRoundedRect(rect, radius))
        using (SolidBrush brush = new SolidBrush(fill))
        using (Pen pen = new Pen(border, 1f))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues && Enabled)
        {
            RectangleF focusRect = new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f);
            using (GraphicsPath focusPath = UiTheme.CreateRoundedRect(focusRect, Math.Max(2f, radius - 2f)))
            using (Pen focusPen = new Pen(isPrimary ? Color.FromArgb(191, 219, 254) : UiTheme.AccentBlue, 1f))
            {
                focusPen.DashStyle = DashStyle.Dot;
                e.Graphics.DrawPath(focusPen, focusPath);
            }
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class PillBadge : Label
{
    readonly Color pillBg;
    readonly Color pillFg;

    public PillBadge(string text, ChannelKind channel)
    {
        Text = text;
        Font = UiTheme.BadgeFont;
        AutoSize = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        switch (channel)
        {
            case ChannelKind.Usb:
                pillBg = Color.FromArgb(232, 248, 238);
                pillFg = Color.FromArgb(29, 122, 56);
                break;
            case ChannelKind.WirelessConnected:
                pillBg = Color.FromArgb(232, 242, 255);
                pillFg = Color.FromArgb(0, 102, 204);
                break;
            case ChannelKind.MdnsPending:
                pillBg = Color.FromArgb(255, 245, 227);
                pillFg = Color.FromArgb(168, 88, 0);
                break;
            case ChannelKind.SavedPending:
                pillBg = Color.FromArgb(240, 238, 255);
                pillFg = Color.FromArgb(88, 66, 178);
                break;
            default:
                pillBg = Color.FromArgb(255, 235, 234);
                pillFg = Color.FromArgb(196, 30, 40);
                break;
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        Size textSize = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        return new Size(textSize.Width + LogicalToDeviceUnits(18), textSize.Height + LogicalToDeviceUnits(8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.ResolveParentBg(Parent, UiTheme.SurfaceBg));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath path = UiTheme.CreateRoundedRect(rect, LogicalToDeviceUnits(6)))
        using (SolidBrush brush = new SolidBrush(pillBg))
        {
            e.Graphics.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            pillFg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

internal class RoundedPanel : TableLayoutPanel
{
    public Color CardBg;
    public Color BorderLineColor;
    public int CornerRadiusDp;

    public RoundedPanel()
    {
        CardBg = UiTheme.SurfaceBg;
        BorderLineColor = UiTheme.BorderColor;
        CornerRadiusDp = 8;
        BackColor = UiTheme.SurfaceBg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.ResolveParentBg(Parent, UiTheme.PageBg));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        RectangleF rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath path = UiTheme.CreateRoundedRect(rect, LogicalToDeviceUnits(CornerRadiusDp)))
        using (SolidBrush brush = new SolidBrush(CardBg))
        using (Pen pen = new Pen(BorderLineColor, 1f))
        {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
    }
}

internal sealed class ModernTextBoxHost : RoundedPanel
{
    public readonly TextBox InnerBox;
    readonly string placeholder;

    public ModernTextBoxHost(string placeholder)
    {
        this.placeholder = placeholder ?? "";
        CornerRadiusDp = 6;
        BorderLineColor = UiTheme.InputBorder;
        CardBg = Color.White;
        BackColor = Color.White;
        ColumnCount = 1;
        RowCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Padding = new Padding(10, 7, 10, 7);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        InnerBox = new TextBox();
        InnerBox.BorderStyle = BorderStyle.None;
        InnerBox.AutoSize = false;
        InnerBox.Font = UiTheme.BodyFont;
        InnerBox.ForeColor = UiTheme.TextPrimary;
        InnerBox.BackColor = Color.White;
        InnerBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        InnerBox.Margin = new Padding(0);
        UpdateInputHeight();

        if (this.placeholder.Length > 0)
        {
            InnerBox.HandleCreated += delegate
            {
                UiTheme.SetCueBanner(InnerBox, this.placeholder);
            };
        }
        InnerBox.GotFocus += delegate
        {
            if (BorderLineColor != UiTheme.AccentBlue)
            {
                BorderLineColor = UiTheme.AccentBlue;
                Invalidate();
            }
        };
        InnerBox.LostFocus += delegate
        {
            if (BorderLineColor != UiTheme.InputBorder)
            {
                BorderLineColor = UiTheme.InputBorder;
                Invalidate();
            }
        };

        Controls.Add(InnerBox, 0, 0);
        Click += delegate { InnerBox.Focus(); };
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (InnerBox != null)
        {
            InnerBox.Font = Font;
            UpdateInputHeight();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateInputHeight();
    }

    void UpdateInputHeight()
    {
        if (InnerBox == null) return;
        Font activeFont = InnerBox.Font ?? UiTheme.BodyFont;
        int boxHeight = Math.Max(InnerBox.PreferredSize.Height, TextRenderer.MeasureText("测gy", activeFont).Height + 2);
        InnerBox.Height = boxHeight;
        InnerBox.MinimumSize = new Size(0, boxHeight);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string TextValue
    {
        get { return (InnerBox.Text ?? "").Trim(); }
        set { InnerBox.Text = value ?? ""; }
    }
}

internal sealed class ModernCheckBox : CheckBox
{
    bool hovered;

    public ModernCheckBox()
    {
        AutoSize = false;
        Text = "";
        Size = new Size(22, 22);
        Cursor = Cursors.Hand;
        BackColor = UiTheme.SurfaceBg;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        return LogicalToDeviceUnits(new Size(22, 22));
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null)
        {
            Size = LogicalToDeviceUnits(new Size(22, 22));
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Size = LogicalToDeviceUnits(new Size(22, 22));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
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

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(UiTheme.ResolveParentBg(Parent, UiTheme.SurfaceBg));
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        float boxSize = LogicalToDeviceUnits(18);
        float x = (Width - boxSize) / 2f;
        float y = (Height - boxSize) / 2f;
        RectangleF rect = new RectangleF(x, y, boxSize, boxSize);
        float radius = LogicalToDeviceUnits(5);
        float dpiScale = UiTheme.GetDpiScale(this);

        Color fill;
        Color border;
        if (!Enabled)
        {
            fill = Checked ? UiTheme.TextDisabled : Color.FromArgb(245, 245, 247);
            border = Color.FromArgb(215, 215, 220);
        }
        else if (Checked)
        {
            fill = hovered ? Color.FromArgb(0, 108, 230) : UiTheme.AccentBlue;
            border = fill;
        }
        else
        {
            fill = Color.White;
            border = hovered ? UiTheme.AccentBlue : Color.FromArgb(185, 185, 192);
        }

        using (GraphicsPath path = UiTheme.CreateRoundedRect(rect, radius))
        using (SolidBrush brush = new SolidBrush(fill))
        using (Pen pen = new Pen(border, Math.Max(1.4f, dpiScale * 1.1f)))
        {
            pevent.Graphics.FillPath(brush, path);
            pevent.Graphics.DrawPath(pen, path);
        }

        if (Checked)
        {
            using (Pen checkPen = new Pen(Color.White, Math.Max(2f, dpiScale * 1.5f)))
            {
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                checkPen.LineJoin = LineJoin.Round;
                PointF p1 = new PointF(x + boxSize * 0.25f, y + boxSize * 0.53f);
                PointF p2 = new PointF(x + boxSize * 0.43f, y + boxSize * 0.72f);
                PointF p3 = new PointF(x + boxSize * 0.77f, y + boxSize * 0.30f);
                pevent.Graphics.DrawLines(checkPen, new PointF[] { p1, p2, p3 });
            }
        }

        if (Focused && ShowFocusCues && Enabled)
        {
            RectangleF focusRect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath focusPath = UiTheme.CreateRoundedRect(focusRect, LogicalToDeviceUnits(5)))
            using (Pen focusPen = new Pen(UiTheme.AccentBlue, 1f))
            {
                focusPen.DashStyle = DashStyle.Dot;
                pevent.Graphics.DrawPath(focusPen, focusPath);
            }
        }
    }
}

internal sealed class SelectableTextLabel : TextBox
{
    int maxWrapWidth;

    public SelectableTextLabel(Font font, Color foreColor, Color backColor, bool multiline)
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        TabStop = false;
        Multiline = multiline;
        WordWrap = multiline;
        ScrollBars = ScrollBars.None;
        ShortcutsEnabled = true;
        Font = font;
        ForeColor = foreColor;
        BackColor = backColor;
        Cursor = Cursors.IBeam;
        Margin = new Padding(0);
    }

    public void ConstrainWidth(int maxWidth)
    {
        int newLimit = Math.Max(LogicalToDeviceUnits(80), maxWidth);
        if (newLimit == maxWrapWidth && Width > 0 && Height > 0) return;
        maxWrapWidth = newLimit;
        UpdateMeasuredBounds();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateMeasuredBounds();
    }

    public void UpdateMeasuredBounds()
    {
        string content = string.IsNullOrEmpty(Text) ? " " : Text;
        int limit = maxWrapWidth > 0 ? maxWrapWidth : LogicalToDeviceUnits(480);
        if (!Multiline)
        {
            Size single = TextRenderer.MeasureText(
                content,
                Font,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int w = Math.Min(limit, single.Width + LogicalToDeviceUnits(8));
            Size = new Size(Math.Max(LogicalToDeviceUnits(20), w), single.Height + LogicalToDeviceUnits(2));
            return;
        }

        // 先按略窄于容器的宽度测高，再留出 EDIT 控件内边距宽度，避免最后一行在 TextBox 内意外多折出半行。
        int measureLimit = Math.Max(LogicalToDeviceUnits(60), limit - LogicalToDeviceUnits(10));
        Size wrapped = TextRenderer.MeasureText(
            content,
            Font,
            new Size(measureLimit, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        int finalWidth = Math.Min(limit, wrapped.Width + LogicalToDeviceUnits(12));
        int finalHeight = wrapped.Height + LogicalToDeviceUnits(4);
        Size = new Size(Math.Max(LogicalToDeviceUnits(20), finalWidth), Math.Max(LogicalToDeviceUnits(20), finalHeight));
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        return Size;
    }
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    }
}
