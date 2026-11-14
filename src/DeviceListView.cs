using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

internal sealed class DeviceListView : BufferedPanel
{
    readonly List<RowEntry> rowEntries = new List<RowEntry>();
    readonly List<CheckBox> rowCheckBoxes = new List<CheckBox>();

    Panel headerDivider;
    CheckBox selectAllCheck;
    Label tableCountLabel;
    RoundedPanel activeTableCard;
    AppConfig currentConfig;
    ScanResult lastScanResult;
    DisplaySnapshot renderedSnapshot;
    bool isActionBusy;
    bool syncingCheckBoxes;
    bool inRelayout;
    int lastLayoutWidth = -1;

    public Action RequestScan;
    public Action<DeviceItem> ConnectSingle;
    public Action<DeviceItem, string> CaptureScreenshot;
    public Action<DeviceItem, string, TerminalMode> OpenTerminal;
    public Action<DeviceItem, string, bool> WirelessAction;
    public Func<bool> CanExecuteActions;
    public Action<int> SelectionChanged;

    public DeviceListView()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(18, 16, 18, 16);
        BackColor = UiTheme.PageBg;
        Resize += delegate { RelayoutCards(); };
    }

    public Control ActiveTableCard { get { return activeTableCard; } }
    public CheckBox SelectAllCheck { get { return selectAllCheck; } }
    public ScanResult LastScanResult { get { return lastScanResult; } }
    public int RowCount { get { return rowCheckBoxes.Count; } }

    internal int ApplyCountForTest { get; private set; }
    internal int CardRebuildCountForTest { get; private set; }
    internal int RowCreateCountForTest { get; private set; }
    internal int RowUpdateCountForTest { get; private set; }

    public void SetActionBusy(bool actionBusy)
    {
        isActionBusy = actionBusy;
        if (selectAllCheck != null)
        {
            selectAllCheck.Enabled = !actionBusy && HasAnyConnectableRow();
        }
    }

    public List<DeviceItem> GetSelectedDevices()
    {
        List<DeviceItem> selected = new List<DeviceItem>();
        for (int i = 0; i < rowEntries.Count; i++)
        {
            RowEntry entry = rowEntries[i];
            if (entry.CheckBox.Checked && entry.Item != null)
            {
                selected.Add(entry.Item);
            }
        }
        return selected;
    }

    public void SetDevices(ScanResult result, AppConfig config, bool forceRebuild = false)
    {
        lastScanResult = result ?? new ScanResult();
        currentConfig = config;
        List<DeviceItem> devices = lastScanResult.Devices ?? new List<DeviceItem>();

        DisplaySnapshot nextSnapshot = BuildDisplaySnapshot(lastScanResult, currentConfig);
        if (!forceRebuild && renderedSnapshot != null && renderedSnapshot.Equals(nextSnapshot) && Controls.Count > 0)
        {
            SyncItemsOnReusedRows(devices, nextSnapshot);
            return;
        }

        renderedSnapshot = nextSnapshot;
        ApplyCountForTest++;

        if (devices.Count == 0)
        {
            ApplyEmptyState(forceRebuild);
            SyncSelectionState();
            ResetStaleHorizontalScroll();
            return;
        }

        if (activeTableCard == null || forceRebuild)
        {
            RebuildCardFromScratch(devices, nextSnapshot);
        }
        else
        {
            ApplyIncrementalRows(devices, nextSnapshot);
        }

        SyncSelectionState();
        if (HorizontalScroll.Visible)
        {
            HorizontalScroll.Enabled = false;
            HorizontalScroll.Visible = false;
        }
    }

    public void HandleDpiChanged()
    {
        if (lastScanResult != null)
        {
            int savedScrollY = Math.Max(0, -AutoScrollPosition.Y);
            List<DeviceRowIdentity> checkedIds = CollectCheckedIdentities();
            SetDevices(lastScanResult, currentConfig, forceRebuild: true);
            RestoreCheckedIdentities(checkedIds);
            if (savedScrollY > 0 && VerticalScroll.Visible)
            {
                AutoScrollPosition = new Point(0, savedScrollY);
            }
        }
        else if (Controls.Count > 0)
        {
            SetDevices(new ScanResult(), currentConfig, forceRebuild: true);
        }
    }

    void SyncItemsOnReusedRows(IList<DeviceItem> devices, DisplaySnapshot snapshot)
    {
        for (int i = 0; i < rowEntries.Count && i < devices.Count; i++)
        {
            RowEntry entry = rowEntries[i];
            string title = i < snapshot.Rows.Count ? snapshot.Rows[i].Title : entry.DisplayTitle;
            DeviceRowIdentity nextId = DeviceRowIdentity.FromItem(devices[i]);
            entry.BindItem(devices[i], title, snapshot.Rows[i], nextId);
        }
    }

    void ApplyEmptyState(bool forceRebuild)
    {
        bool alreadyEmpty = Controls.Count == 1 && object.Equals(Controls[0].Tag, "EmptyState");
        if (alreadyEmpty && !forceRebuild) return;

        ClearAllListControls();
        Controls.Add(BuildEmptyStateView());
    }

    void RebuildCardFromScratch(IList<DeviceItem> devices, DisplaySnapshot snapshot)
    {
        int savedScrollY = Math.Max(0, -AutoScrollPosition.Y);
        List<DeviceRowIdentity> checkedIds = CollectCheckedIdentities();

        ClearAllListControls();
        CardRebuildCountForTest++;

        RoundedPanel tableCard = CreateTableCardShell(devices.Count);
        activeTableCard = tableCard;

        int cellRow = 2;
        for (int i = 0; i < devices.Count; i++)
        {
            Panel divider = null;
            if (i > 0)
            {
                divider = CreateRowDivider();
                tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                tableCard.Controls.Add(divider, 0, cellRow++);
            }

            DisplayRowSnapshot rowSnap = snapshot.Rows[i];
            DeviceRowIdentity identity = DeviceRowIdentity.FromItem(devices[i]);
            RowEntry entry = CreateRowEntry(devices[i], rowSnap, identity);
            entry.Divider = divider;
            if (entry.CheckBox.Enabled && MatchesAnyIdentity(identity, checkedIds))
            {
                entry.CheckBox.Checked = true;
            }
            RowCreateCountForTest++;

            rowEntries.Add(entry);
            rowCheckBoxes.Add(entry.CheckBox);
            tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tableCard.Controls.Add(entry.RowPanel, 0, cellRow++);
        }

        tableCard.RowCount = cellRow;
        tableCard.ResumeLayout(false);

        SuspendLayout();
        Controls.Add(tableCard);
        ResumeLayout(true);

        lastLayoutWidth = -1;
        RelayoutCards(forceRelayout: true, explicitScrollY: savedScrollY);
    }

    void ApplyIncrementalRows(IList<DeviceItem> devices, DisplaySnapshot snapshot)
    {
        int savedScrollY = Math.Max(0, -AutoScrollPosition.Y);
        int oldCount = rowEntries.Count;
        bool[] claimed = new bool[oldCount];
        List<RowEntry> nextEntries = new List<RowEntry>(devices.Count);
        bool anyCreatedOrUpdated = false;

        for (int i = 0; i < devices.Count; i++)
        {
            DeviceItem item = devices[i];
            DisplayRowSnapshot rowSnap = snapshot.Rows[i];
            DeviceRowIdentity identity = DeviceRowIdentity.FromItem(item);

            int matchedIdx = FindClaimableEntryIndex(identity, i, claimed);
            if (matchedIdx >= 0)
            {
                claimed[matchedIdx] = true;
                RowEntry entry = rowEntries[matchedIdx];
                if (!entry.Snapshot.Equals(rowSnap))
                {
                    UpdateRowEntry(entry, item, rowSnap, identity);
                    RowUpdateCountForTest++;
                    anyCreatedOrUpdated = true;
                }
                else
                {
                    entry.BindItem(item, rowSnap.Title, rowSnap, identity);
                }
                nextEntries.Add(entry);
            }
            else
            {
                RowEntry created = CreateRowEntry(item, rowSnap, identity);
                RowCreateCountForTest++;
                anyCreatedOrUpdated = true;
                nextEntries.Add(created);
            }
        }

        bool hasRemovals = false;
        for (int i = 0; i < oldCount; i++)
        {
            if (!claimed[i])
            {
                hasRemovals = true;
                break;
            }
        }

        bool orderOrCountChanged = hasRemovals || nextEntries.Count != oldCount;
        if (!orderOrCountChanged)
        {
            for (int i = 0; i < nextEntries.Count; i++)
            {
                if (!ReferenceEquals(nextEntries[i], rowEntries[i]))
                {
                    orderOrCountChanged = true;
                    break;
                }
            }
        }

        if (orderOrCountChanged)
        {
            activeTableCard.SuspendLayout();
            for (int i = 0; i < oldCount; i++)
            {
                if (!claimed[i])
                {
                    RemoveAndDisposeRowEntry(rowEntries[i]);
                }
            }

            // 先整理每行前置分隔线状态：首行不保留 Divider，后续行确保自带一条 Divider。
            for (int i = 0; i < nextEntries.Count; i++)
            {
                RowEntry entry = nextEntries[i];
                if (i == 0)
                {
                    if (entry.Divider != null)
                    {
                        activeTableCard.Controls.Remove(entry.Divider);
                        entry.Divider.Dispose();
                        entry.Divider = null;
                    }
                }
                else if (entry.Divider == null)
                {
                    entry.Divider = CreateRowDivider();
                    activeTableCard.Controls.Add(entry.Divider);
                }

                if (entry.RowPanel.Parent != activeTableCard)
                {
                    activeTableCard.Controls.Add(entry.RowPanel);
                }
            }

            int totalRows = 1 + nextEntries.Count * 2;
            while (activeTableCard.RowStyles.Count < totalRows)
            {
                activeTableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            while (activeTableCard.RowStyles.Count > totalRows)
            {
                activeTableCard.RowStyles.RemoveAt(activeTableCard.RowStyles.Count - 1);
            }
            activeTableCard.RowCount = totalRows;

            // 同步 Controls 子控件顺序与 TableLayoutPanel 行索引。
            int childIndex = 2; // 0: headerRow, 1: headerDivider
            int cellRow = 2;
            for (int i = 0; i < nextEntries.Count; i++)
            {
                RowEntry entry = nextEntries[i];
                if (i > 0 && entry.Divider != null)
                {
                    if (activeTableCard.GetRow(entry.Divider) != cellRow)
                    {
                        activeTableCard.SetRow(entry.Divider, cellRow);
                    }
                    activeTableCard.SetColumn(entry.Divider, 0);
                    if (activeTableCard.Controls.GetChildIndex(entry.Divider) != childIndex)
                    {
                        activeTableCard.Controls.SetChildIndex(entry.Divider, childIndex);
                    }
                    childIndex++;
                    cellRow++;
                }

                if (activeTableCard.GetRow(entry.RowPanel) != cellRow)
                {
                    activeTableCard.SetRow(entry.RowPanel, cellRow);
                }
                activeTableCard.SetColumn(entry.RowPanel, 0);
                if (activeTableCard.Controls.GetChildIndex(entry.RowPanel) != childIndex)
                {
                    activeTableCard.Controls.SetChildIndex(entry.RowPanel, childIndex);
                }
                childIndex++;
                cellRow++;
            }

            rowEntries.Clear();
            rowEntries.AddRange(nextEntries);
            rowCheckBoxes.Clear();
            for (int i = 0; i < rowEntries.Count; i++)
            {
                rowCheckBoxes.Add(rowEntries[i].CheckBox);
            }

            activeTableCard.ResumeLayout(false);
        }

        if (orderOrCountChanged || anyCreatedOrUpdated)
        {
            RelayoutCards(forceRelayout: true, explicitScrollY: savedScrollY);
        }
    }

    int FindClaimableEntryIndex(DeviceRowIdentity identity, int preferredIndex, bool[] claimed)
    {
        if (preferredIndex >= 0 && preferredIndex < rowEntries.Count
            && !claimed[preferredIndex]
            && rowEntries[preferredIndex].Identity.MatchesExactPrimary(identity))
        {
            return preferredIndex;
        }
        for (int i = 0; i < rowEntries.Count; i++)
        {
            if (!claimed[i] && rowEntries[i].Identity.MatchesExactPrimary(identity))
            {
                return i;
            }
        }
        if (preferredIndex >= 0 && preferredIndex < rowEntries.Count
            && !claimed[preferredIndex]
            && rowEntries[preferredIndex].Identity.SharesTokenWith(identity))
        {
            return preferredIndex;
        }
        for (int i = 0; i < rowEntries.Count; i++)
        {
            if (!claimed[i] && rowEntries[i].Identity.SharesTokenWith(identity))
            {
                return i;
            }
        }
        return -1;
    }

    void RemoveAndDisposeRowEntry(RowEntry entry)
    {
        if (entry == null) return;
        if (entry.Divider != null)
        {
            activeTableCard.Controls.Remove(entry.Divider);
            entry.Divider.Dispose();
            entry.Divider = null;
        }
        if (entry.RowPanel != null)
        {
            activeTableCard.Controls.Remove(entry.RowPanel);
            entry.RowPanel.Dispose();
        }
    }

    void ClearAllListControls()
    {
        SuspendLayout();
        while (Controls.Count > 0)
        {
            int last = Controls.Count - 1;
            Control old = Controls[last];
            Controls.RemoveAt(last);
            old.Dispose();
        }
        rowEntries.Clear();
        rowCheckBoxes.Clear();
        headerDivider = null;
        selectAllCheck = null;
        tableCountLabel = null;
        activeTableCard = null;
        lastLayoutWidth = -1;
        ResumeLayout(false);
    }

    RoundedPanel CreateTableCardShell(int initialDeviceCount)
    {
        RoundedPanel tableCard = new RoundedPanel();
        tableCard.SuspendLayout();
        tableCard.AutoSize = true;
        tableCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        tableCard.CornerRadiusDp = 12;
        tableCard.CardBg = UiTheme.SurfaceBg;
        tableCard.BorderLineColor = UiTheme.BorderColor;
        tableCard.Padding = UiTheme.Pad(this, 1, 6, 1, 6);
        tableCard.Margin = new Padding(0);
        tableCard.ColumnCount = 1;
        tableCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        TableLayoutPanel headerRow = new TableLayoutPanel();
        headerRow.SuspendLayout();
        headerRow.AutoSize = true;
        headerRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        headerRow.BackColor = UiTheme.SurfaceBg;
        headerRow.ColumnCount = 3;
        headerRow.RowCount = 1;
        headerRow.Padding = UiTheme.Pad(this, 16, 7, 16, 8);
        headerRow.Margin = new Padding(0);
        headerRow.Tag = "TableHeaderRow";
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        selectAllCheck = new ModernCheckBox();
        selectAllCheck.Anchor = AnchorStyles.Left;
        selectAllCheck.Margin = UiTheme.Pad(this, 0, 0, 12, 0);
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
        tableCountLabel.Text = string.Format("共 {0} 台", initialDeviceCount);

        headerRow.Controls.Add(selectAllCheck, 0, 0);
        headerRow.Controls.Add(headerDeviceLbl, 1, 0);
        headerRow.Controls.Add(tableCountLabel, 2, 0);
        headerRow.ResumeLayout(false);

        tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableCard.Controls.Add(headerRow, 0, 0);

        headerDivider = new Panel();
        headerDivider.Height = 1;
        headerDivider.Dock = DockStyle.Fill;
        headerDivider.BackColor = UiTheme.BorderColor;
        headerDivider.Margin = new Padding(0);
        tableCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tableCard.Controls.Add(headerDivider, 0, 1);

        return tableCard;
    }

    Panel CreateRowDivider()
    {
        Panel insetDivider = new Panel();
        insetDivider.Height = 1;
        insetDivider.Dock = DockStyle.Fill;
        insetDivider.BackColor = UiTheme.BorderColor;
        insetDivider.Margin = UiTheme.Pad(this, 16, 0, 16, 0);
        insetDivider.Tag = "RowDivider";
        return insetDivider;
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
        card.CornerRadiusDp = 12;
        card.ColumnCount = 1;
        card.RowCount = 3;
        card.Padding = UiTheme.Pad(this, 30, 24, 30, 24);
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Label heading = new Label();
        heading.AutoSize = true;
        heading.Font = UiTheme.TitleFont;
        heading.ForeColor = UiTheme.TextPrimary;
        heading.Anchor = AnchorStyles.None;
        heading.Margin = UiTheme.Pad(this, 0, 0, 0, 10);
        heading.Text = "暂未检测到可用设备";

        Label tips = new Label();
        tips.AutoSize = true;
        tips.Font = UiTheme.SubFont;
        tips.ForeColor = UiTheme.TextSecondary;
        tips.Margin = UiTheme.Pad(this, 0, 0, 0, 18);
        tips.Text = "• 有线：插入 USB 并在手机确认「允许调试」\n• 无线：开启手机「无线调试」并确保与电脑在同一局域网\n• 首次配对或手动直连：点击左上角「无线配对…」或「手动连接…」";

        Button emptyScanBtn = UiTheme.MakePrimaryButton("立即扫描设备");
        emptyScanBtn.Anchor = AnchorStyles.None;
        emptyScanBtn.Margin = new Padding(0);
        emptyScanBtn.Click += delegate { if (RequestScan != null) RequestScan(); };

        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(tips, 0, 1);
        card.Controls.Add(emptyScanBtn, 0, 2);

        host.Controls.Add(card, 1, 1);
        return host;
    }

    RowEntry CreateRowEntry(DeviceItem item, DisplayRowSnapshot rowSnap, DeviceRowIdentity identity)
    {
        RowEntry entry = new RowEntry(identity);
        entry.BindItem(item, rowSnap.Title, rowSnap, identity);

        TableLayoutPanel row = new TableLayoutPanel();
        row.SuspendLayout();
        row.AutoSize = true;
        row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row.BackColor = UiTheme.SurfaceBg;
        row.ColumnCount = 3;
        row.RowCount = 1;
        row.Padding = UiTheme.Pad(this, 16, 13, 16, 13);
        row.Margin = new Padding(0);
        row.Tag = item;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ModernCheckBox cb = new ModernCheckBox();
        cb.Anchor = AnchorStyles.Left;
        cb.Margin = UiTheme.Pad(this, 0, 0, 12, 0);
        cb.Enabled = item.CanConnect;
        cb.Tag = item;
        cb.CheckedChanged += delegate { if (!syncingCheckBoxes) SyncSelectionState(); };

        TableLayoutPanel infoTable = new TableLayoutPanel();
        infoTable.SuspendLayout();
        infoTable.AutoSize = true;
        infoTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        infoTable.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        infoTable.BackColor = UiTheme.SurfaceBg;
        infoTable.ColumnCount = 1;
        infoTable.RowCount = 2;
        infoTable.Margin = UiTheme.Pad(this, 0, 0, 14, 0);
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
        titleLbl.Margin = UiTheme.Pad(this, 0, 2, 10, 0);
        titleLbl.Anchor = AnchorStyles.Left;
        titleLbl.Text = rowSnap.Title;
        titleLbl.UpdateMeasuredBounds();

        PillBadge badgeLbl = new PillBadge(item.BadgeText, item.Channel);
        badgeLbl.Anchor = AnchorStyles.Left;
        badgeLbl.Margin = new Padding(0);

        headerFlow.Controls.Add(titleLbl);
        headerFlow.Controls.Add(badgeLbl);
        headerFlow.ResumeLayout(false);

        SelectableTextLabel subLbl = new SelectableTextLabel(UiTheme.SubFont, UiTheme.TextSecondary, UiTheme.SurfaceBg, true);
        subLbl.Margin = UiTheme.Pad(this, 0, 5, 0, 0);
        subLbl.Text = rowSnap.SubText;
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

        Button shotBtn = UiTheme.MakeToolButton("截屏");
        shotBtn.Margin = UiTheme.Pad(this, 0, 0, 8, 0);
        shotBtn.Enabled = item.CanConnect;
        shotBtn.Click += delegate { if (CaptureScreenshot != null && entry.Item != null) CaptureScreenshot(entry.Item, entry.DisplayTitle); };

        Button mirrorBtn = UiTheme.MakePrimaryButton("投屏");
        mirrorBtn.Margin = new Padding(0);
        mirrorBtn.Enabled = item.CanConnect;
        mirrorBtn.Click += delegate { if (ConnectSingle != null && entry.Item != null) ConnectSingle(entry.Item); };

        actionFlow.Controls.Add(shotBtn);
        actionFlow.Controls.Add(mirrorBtn);

        ContextMenuStrip termMenu = new ContextMenuStrip();
        termMenu.ShowImageMargin = false;
        termMenu.Font = UiTheme.BodyFont;
        termMenu.BackColor = Color.White;
        termMenu.Opening += delegate(object s, CancelEventArgs ce)
        {
            if (CanExecuteActions != null && !CanExecuteActions()) ce.Cancel = true;
        };

        ToolStripMenuItem adbShellItem = new ToolStripMenuItem("进入 ADB Shell");
        adbShellItem.Click += delegate { if (OpenTerminal != null && entry.Item != null) OpenTerminal(entry.Item, entry.DisplayTitle, TerminalMode.AdbShell); };
        ToolStripMenuItem localTermItem = new ToolStripMenuItem("打开本地 ADB 终端");
        localTermItem.Click += delegate { if (OpenTerminal != null && entry.Item != null) OpenTerminal(entry.Item, entry.DisplayTitle, TerminalMode.LocalAdbEnv); };
        termMenu.Items.Add(adbShellItem);
        termMenu.Items.Add(localTermItem);

        entry.RowPanel = row;
        entry.CheckBox = cb;
        entry.InfoTable = infoTable;
        entry.HeaderFlow = headerFlow;
        entry.TitleLabel = titleLbl;
        entry.BadgeLabel = badgeLbl;
        entry.SubLabel = subLbl;
        entry.ActionFlow = actionFlow;
        entry.ScreenshotBtn = shotBtn;
        entry.MirrorBtn = mirrorBtn;
        entry.TermMenu = termMenu;

        SyncRowWirelessButtons(entry, item);
        actionFlow.ResumeLayout(false);

        ApplyRowContextMenu(entry, item.CanConnect);
        row.Disposed += delegate { termMenu.Dispose(); };

        row.Controls.Add(cb, 0, 0);
        row.Controls.Add(infoTable, 1, 0);
        row.Controls.Add(actionFlow, 2, 0);
        row.ResumeLayout(false);
        return entry;
    }

    void UpdateRowEntry(RowEntry entry, DeviceItem item, DisplayRowSnapshot rowSnap, DeviceRowIdentity identity)
    {
        bool titleChanged = !string.Equals(entry.TitleLabel.Text, rowSnap.Title, StringComparison.Ordinal);
        bool subChanged = !string.Equals(entry.SubLabel.Text, rowSnap.SubText, StringComparison.Ordinal);
        bool canConnectChanged = entry.CheckBox.Enabled != item.CanConnect;

        entry.BindItem(item, rowSnap.Title, rowSnap, identity);
        entry.RowPanel.SuspendLayout();

        if (!item.CanConnect && entry.CheckBox.Checked)
        {
            syncingCheckBoxes = true;
            entry.CheckBox.Checked = false;
            syncingCheckBoxes = false;
        }
        entry.CheckBox.Enabled = item.CanConnect;

        if (titleChanged)
        {
            entry.TitleLabel.Text = rowSnap.Title;
            entry.TitleLabel.UpdateMeasuredBounds();
        }
        entry.BadgeLabel.UpdateBadge(item.BadgeText, item.Channel);
        if (subChanged)
        {
            entry.SubLabel.Text = rowSnap.SubText;
            entry.SubLabel.UpdateMeasuredBounds();
        }

        if (canConnectChanged)
        {
            entry.ScreenshotBtn.Enabled = item.CanConnect;
            entry.MirrorBtn.Enabled = item.CanConnect;
            ApplyRowContextMenu(entry, item.CanConnect);
        }

        SyncRowWirelessButtons(entry, item);

        entry.AppliedLayoutWidth = -1;
        entry.RowPanel.ResumeLayout(true);
    }

    void SyncRowWirelessButtons(RowEntry entry, DeviceItem item)
    {
        WirelessActionSpec nextSpec = ResolveWirelessActionSpec(item);
        if (entry.WirelessSpec.Equals(nextSpec)) return;

        bool sameStructure = entry.WirelessSpec.HasConnect == nextSpec.HasConnect
            && entry.WirelessSpec.HasDisconnect == nextSpec.HasDisconnect;

        entry.WirelessSpec = nextSpec;
        if (sameStructure)
        {
            if (entry.WirelessBtn1 != null)
            {
                entry.WirelessBtn1.Enabled = nextSpec.HasConnect ? nextSpec.ConnectEnabled : nextSpec.DisconnectEnabled;
            }
            if (entry.WirelessBtn2 != null)
            {
                entry.WirelessBtn2.Enabled = nextSpec.DisconnectEnabled;
            }
            return;
        }

        entry.ActionFlow.SuspendLayout();
        if (entry.WirelessBtn1 != null)
        {
            entry.ActionFlow.Controls.Remove(entry.WirelessBtn1);
            entry.WirelessBtn1.Dispose();
            entry.WirelessBtn1 = null;
        }
        if (entry.WirelessBtn2 != null)
        {
            entry.ActionFlow.Controls.Remove(entry.WirelessBtn2);
            entry.WirelessBtn2.Dispose();
            entry.WirelessBtn2 = null;
        }

        int insertIdx = 0;
        if (nextSpec.HasConnect)
        {
            Button connBtn = CreateWirelessActionButton("连接", entry, false, nextSpec.ConnectEnabled);
            entry.ActionFlow.Controls.Add(connBtn);
            entry.ActionFlow.Controls.SetChildIndex(connBtn, insertIdx++);
            entry.WirelessBtn1 = connBtn;
        }
        if (nextSpec.HasDisconnect)
        {
            Button discBtn = CreateWirelessActionButton("断开", entry, true, nextSpec.DisconnectEnabled);
            entry.ActionFlow.Controls.Add(discBtn);
            entry.ActionFlow.Controls.SetChildIndex(discBtn, insertIdx++);
            if (entry.WirelessBtn1 == null) entry.WirelessBtn1 = discBtn;
            else entry.WirelessBtn2 = discBtn;
        }

        entry.ActionFlow.ResumeLayout(true);
    }

    Button CreateWirelessActionButton(string text, RowEntry entry, bool isDisconnecting, bool enabled)
    {
        Button btn = UiTheme.MakeToolButton(text);
        btn.Margin = UiTheme.Pad(this, 0, 0, 8, 0);
        btn.Enabled = enabled;
        btn.Click += delegate { if (WirelessAction != null && entry.Item != null) WirelessAction(entry.Item, entry.DisplayTitle, isDisconnecting); };
        return btn;
    }

    static WirelessActionSpec ResolveWirelessActionSpec(DeviceItem item)
    {
        if (IsPendingWireless(item.Channel))
        {
            return new WirelessActionSpec(true, item.CanConnect, false, false);
        }
        if (item.Channel == ChannelKind.WirelessConnected)
        {
            return new WirelessActionSpec(false, false, true, item.CanConnect);
        }
        if (item.Channel == ChannelKind.Offline && AdbScanner.IsWirelessDevice(item))
        {
            bool hasConnect = item.CandidateEndpoints.Count > 0;
            return new WirelessActionSpec(hasConnect, hasConnect, true, true);
        }
        return default(WirelessActionSpec);
    }

    static void ApplyRowContextMenu(RowEntry entry, bool enabled)
    {
        ContextMenuStrip menu = enabled ? entry.TermMenu : null;
        entry.RowPanel.ContextMenuStrip = menu;
        entry.InfoTable.ContextMenuStrip = menu;
        entry.HeaderFlow.ContextMenuStrip = menu;
        entry.BadgeLabel.ContextMenuStrip = menu;
        entry.ActionFlow.ContextMenuStrip = menu;
    }

    void RelayoutCards(bool forceRelayout = false, int? explicitScrollY = null)
    {
        if (Controls.Count == 0) return;
        if (Controls.Count == 1 && object.Equals(Controls[0].Tag, "EmptyState"))
        {
            ResetStaleHorizontalScroll();
            return;
        }

        int availWidth = Math.Max(UiTheme.Scale(this, 420), ClientSize.Width - Padding.Horizontal);
        Control card = Controls[0];
        if (!forceRelayout && availWidth == lastLayoutWidth && card.Width == availWidth && !HorizontalScroll.Visible) return;
        lastLayoutWidth = availWidth;

        int savedScrollY = explicitScrollY.HasValue ? explicitScrollY.Value : Math.Max(0, -AutoScrollPosition.Y);
        SuspendLayout();
        card.SuspendLayout();
        int innerRowWidth = Math.Max(UiTheme.Scale(this, 380), availWidth - card.Padding.Horizontal);

        for (int i = 0; i < rowEntries.Count; i++)
        {
            RowEntry entry = rowEntries[i];
            if (entry.AppliedLayoutWidth == innerRowWidth && entry.RowPanel.Width == innerRowWidth) continue;

            TableLayoutPanel row = entry.RowPanel;
            row.SuspendLayout();
            ConstrainRowLabels(entry, innerRowWidth);
            row.MinimumSize = new Size(innerRowWidth, 0);
            row.MaximumSize = new Size(innerRowWidth, 0);
            row.Width = innerRowWidth;
            entry.AppliedLayoutWidth = innerRowWidth;
            row.ResumeLayout(true);
        }

        card.MinimumSize = new Size(availWidth, 0);
        card.MaximumSize = new Size(availWidth, 0);
        card.Left = Padding.Left;
        card.Top = Padding.Top - savedScrollY;
        card.Width = availWidth;
        card.ResumeLayout(true);
        ResumeLayout(true);

        if (HorizontalScroll.Visible || DisplayRectangle.Width > ClientSize.Width)
        {
            AutoScroll = false;
            AutoScroll = true;
        }
        if (savedScrollY > 0 && VerticalScroll.Visible)
        {
            AutoScrollPosition = new Point(0, savedScrollY);
        }

        int afterScrollWidth = Math.Max(UiTheme.Scale(this, 420), ClientSize.Width - Padding.Horizontal);
        if (afterScrollWidth != availWidth && !inRelayout)
        {
            inRelayout = true;
            try
            {
                lastLayoutWidth = -1;
                RelayoutCards(forceRelayout: true, explicitScrollY: savedScrollY);
            }
            finally
            {
                inRelayout = false;
            }
        }
    }

    void ResetStaleHorizontalScroll()
    {
        if (HorizontalScroll.Visible || DisplayRectangle.Width > ClientSize.Width)
        {
            AutoScroll = false;
            AutoScroll = true;
        }
    }

    static void ConstrainRowLabels(RowEntry entry, int rowWidth)
    {
        TableLayoutPanel tlp = entry.RowPanel;
        if (tlp == null || tlp.Controls.Count < 3) return;

        int leftWidth = tlp.Controls[0].PreferredSize.Width + tlp.Controls[0].Margin.Horizontal;
        int rightWidth = tlp.Controls[2].PreferredSize.Width + tlp.Controls[2].Margin.Horizontal;
        int maxTextWidth = Math.Max(UiTheme.Scale(tlp, 160), rowWidth - tlp.Padding.Horizontal - leftWidth - rightWidth - UiTheme.Scale(tlp, 20));

        int badgeSpace = entry.BadgeLabel.PreferredSize.Width + entry.BadgeLabel.Margin.Horizontal + entry.TitleLabel.Margin.Horizontal;
        entry.TitleLabel.ConstrainWidth(Math.Max(UiTheme.Scale(tlp, 100), maxTextWidth - badgeSpace));
        entry.SubLabel.ConstrainWidth(maxTextWidth);
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

        if (selectAllCheck != null)
        {
            syncingCheckBoxes = true;
            selectAllCheck.Enabled = !isActionBusy && anyConnectable;
            selectAllCheck.Checked = allChecked;
            syncingCheckBoxes = false;
        }

        if (tableCountLabel != null)
        {
            tableCountLabel.Text = checkedCount > 0
                ? string.Format("已选 {0} / {1} 台", checkedCount, rowCheckBoxes.Count)
                : string.Format("共 {0} 台", rowCheckBoxes.Count);
        }

        if (SelectionChanged != null)
        {
            SelectionChanged(checkedCount);
        }
    }

    List<DeviceRowIdentity> CollectCheckedIdentities()
    {
        List<DeviceRowIdentity> ids = new List<DeviceRowIdentity>();
        for (int i = 0; i < rowEntries.Count; i++)
        {
            RowEntry entry = rowEntries[i];
            if (entry.CheckBox.Checked && entry.Item != null)
            {
                ids.Add(entry.Identity);
            }
        }
        return ids;
    }

    void RestoreCheckedIdentities(List<DeviceRowIdentity> checkedIds)
    {
        if (checkedIds == null || checkedIds.Count == 0) return;
        syncingCheckBoxes = true;
        try
        {
            for (int i = 0; i < rowEntries.Count; i++)
            {
                RowEntry entry = rowEntries[i];
                if (entry.CheckBox.Enabled && MatchesAnyIdentity(entry.Identity, checkedIds))
                {
                    entry.CheckBox.Checked = true;
                }
            }
        }
        finally
        {
            syncingCheckBoxes = false;
        }
        SyncSelectionState();
    }

    static bool MatchesAnyIdentity(DeviceRowIdentity candidate, IList<DeviceRowIdentity> targets)
    {
        if (targets == null || targets.Count == 0) return false;
        for (int i = 0; i < targets.Count; i++)
        {
            if (candidate.Matches(targets[i]))
            {
                return true;
            }
        }
        return false;
    }

    DisplaySnapshot BuildDisplaySnapshot(ScanResult result, AppConfig cfg)
    {
        DisplaySnapshot snap = new DisplaySnapshot(DeviceDpi);
        if (result == null || result.Devices == null) return snap;

        for (int i = 0; i < result.Devices.Count; i++)
        {
            DeviceItem item = result.Devices[i];
            ResolvedDeviceConfig resolved = ConfigManager.Resolve(cfg, item);
            string title = GetDisplayTitle(item, resolved);
            string subText = BuildSubText(item, resolved, title);
            snap.Rows.Add(new DisplayRowSnapshot(item, resolved.HasCustomArgs, title, subText));
        }
        return snap;
    }

    internal static bool IsPendingWireless(ChannelKind kind)
    {
        return kind == ChannelKind.MdnsPending || kind == ChannelKind.SavedPending;
    }

    internal static string GetDisplayTitle(DeviceItem item, ResolvedDeviceConfig resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.Alias)) return resolved.Alias;
        if (!string.IsNullOrWhiteSpace(item.Model)) return item.Model;
        if (!string.IsNullOrWhiteSpace(item.HardwareSerial)) return item.HardwareSerial;
        if (!string.IsNullOrWhiteSpace(item.AdbSerial)) return item.AdbSerial;
        if (!string.IsNullOrWhiteSpace(item.BaseMdnsName)) return item.BaseMdnsName;
        return item.PrimaryEndpointDisplay;
    }

    internal static string BuildSubText(DeviceItem item, ResolvedDeviceConfig resolved, string displayTitle)
    {
        List<string> parts = new List<string>();
        if (item.IsChecking)
        {
            parts.Add("正在检查…");
        }
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

    sealed class RowEntry
    {
        public DeviceRowIdentity Identity;
        public DeviceItem Item;
        public string DisplayTitle;
        public DisplayRowSnapshot Snapshot;
        public Panel Divider;
        public TableLayoutPanel RowPanel;
        public ModernCheckBox CheckBox;
        public TableLayoutPanel InfoTable;
        public FlowLayoutPanel HeaderFlow;
        public SelectableTextLabel TitleLabel;
        public PillBadge BadgeLabel;
        public SelectableTextLabel SubLabel;
        public FlowLayoutPanel ActionFlow;
        public Button WirelessBtn1;
        public Button WirelessBtn2;
        public Button ScreenshotBtn;
        public Button MirrorBtn;
        public ContextMenuStrip TermMenu;
        public WirelessActionSpec WirelessSpec;
        public int AppliedLayoutWidth = -1;

        public RowEntry(DeviceRowIdentity identity)
        {
            Identity = identity;
        }

        public void BindItem(DeviceItem item, string displayTitle, DisplayRowSnapshot snapshot, DeviceRowIdentity identity)
        {
            Item = item;
            DisplayTitle = displayTitle ?? "";
            Snapshot = snapshot;
            Identity = identity;
            if (RowPanel != null) RowPanel.Tag = item;
            if (CheckBox != null) CheckBox.Tag = item;
        }
    }

    readonly struct WirelessActionSpec : IEquatable<WirelessActionSpec>
    {
        public readonly bool HasConnect;
        public readonly bool ConnectEnabled;
        public readonly bool HasDisconnect;
        public readonly bool DisconnectEnabled;

        public WirelessActionSpec(bool hasConnect, bool connectEnabled, bool hasDisconnect, bool disconnectEnabled)
        {
            HasConnect = hasConnect;
            ConnectEnabled = connectEnabled;
            HasDisconnect = hasDisconnect;
            DisconnectEnabled = disconnectEnabled;
        }

        public bool Equals(WirelessActionSpec other)
        {
            return HasConnect == other.HasConnect
                && ConnectEnabled == other.ConnectEnabled
                && HasDisconnect == other.HasDisconnect
                && DisconnectEnabled == other.DisconnectEnabled;
        }
    }

    sealed class DisplaySnapshot
    {
        public readonly int Dpi;
        public readonly List<DisplayRowSnapshot> Rows = new List<DisplayRowSnapshot>();

        public DisplaySnapshot(int dpi)
        {
            Dpi = dpi;
        }

        public bool Equals(DisplaySnapshot other)
        {
            if (other == null || Dpi != other.Dpi || Rows.Count != other.Rows.Count) return false;
            for (int i = 0; i < Rows.Count; i++)
            {
                if (!Rows[i].Equals(other.Rows[i])) return false;
            }
            return true;
        }
    }

    sealed class DisplayRowSnapshot
    {
        readonly ChannelKind channel;
        readonly string adbSerial;
        readonly string hardwareSerial;
        readonly string baseMdnsName;
        readonly string model;
        readonly bool canConnect;
        readonly bool isChecking;
        readonly bool hasCustomArgs;
        public readonly string Title;
        public readonly string SubText;
        readonly string[] candidates;
        readonly string[] alternates;

        public DisplayRowSnapshot(DeviceItem item, bool hasCustomArgs, string title, string subText)
        {
            channel = item.Channel;
            adbSerial = item.AdbSerial ?? "";
            hardwareSerial = item.HardwareSerial ?? "";
            baseMdnsName = item.BaseMdnsName ?? "";
            model = item.Model ?? "";
            canConnect = item.CanConnect;
            isChecking = item.IsChecking;
            this.hasCustomArgs = hasCustomArgs;
            Title = title ?? "";
            SubText = subText ?? "";
            candidates = item.CandidateEndpoints != null ? item.CandidateEndpoints.ToArray() : Array.Empty<string>();
            alternates = item.AlternateSerials != null ? item.AlternateSerials.ToArray() : Array.Empty<string>();
        }

        public bool Equals(DisplayRowSnapshot other)
        {
            if (other == null) return false;
            return channel == other.channel
                && canConnect == other.canConnect
                && isChecking == other.isChecking
                && hasCustomArgs == other.hasCustomArgs
                && string.Equals(adbSerial, other.adbSerial, StringComparison.Ordinal)
                && string.Equals(hardwareSerial, other.hardwareSerial, StringComparison.Ordinal)
                && string.Equals(baseMdnsName, other.baseMdnsName, StringComparison.Ordinal)
                && string.Equals(model, other.model, StringComparison.Ordinal)
                && string.Equals(Title, other.Title, StringComparison.Ordinal)
                && string.Equals(SubText, other.SubText, StringComparison.Ordinal)
                && StringArrayEquals(candidates, other.candidates)
                && StringArrayEquals(alternates, other.alternates);
        }

        static bool StringArrayEquals(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    internal enum TransportKind
    {
        Usb,
        Wireless
    }

    internal sealed class DeviceRowIdentity
    {
        public readonly TransportKind Transport;
        public readonly string PrimaryKey;
        readonly string[] tokens;

        DeviceRowIdentity(TransportKind transport, string primaryKey, string[] tokens)
        {
            Transport = transport;
            PrimaryKey = primaryKey ?? "";
            this.tokens = tokens ?? Array.Empty<string>();
        }

        public static DeviceRowIdentity FromItem(DeviceItem item)
        {
            if (item == null)
            {
                return new DeviceRowIdentity(TransportKind.Usb, "", Array.Empty<string>());
            }

            TransportKind transport = ResolveTransport(item);
            if (transport == TransportKind.Usb)
            {
                string primary = FirstNonEmpty(
                    TrimToken(item.AdbSerial),
                    TrimToken(item.PrimaryEndpointDisplay));
                string[] usbTokens = !string.IsNullOrEmpty(primary)
                    ? new[] { primary }
                    : Array.Empty<string>();
                return new DeviceRowIdentity(TransportKind.Usb, primary, usbTokens);
            }

            string firstCandidate = item.CandidateEndpoints != null && item.CandidateEndpoints.Count > 0
                ? NormalizeToken(item.CandidateEndpoints[0])
                : "";
            string wirelessPrimary = FirstNonEmpty(
                NormalizeToken(item.AdbSerial),
                firstCandidate,
                TrimToken(item.BaseMdnsName),
                TrimToken(item.HardwareSerial));

            List<string> tokenList = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddToken(tokenList, seen, NormalizeToken(item.AdbSerial));
            AddToken(tokenList, seen, TrimToken(item.BaseMdnsName));
            AddToken(tokenList, seen, TrimToken(item.HardwareSerial));
            if (item.CandidateEndpoints != null)
            {
                for (int i = 0; i < item.CandidateEndpoints.Count; i++)
                {
                    AddToken(tokenList, seen, NormalizeToken(item.CandidateEndpoints[i]));
                }
            }
            if (item.AlternateSerials != null)
            {
                for (int i = 0; i < item.AlternateSerials.Count; i++)
                {
                    AddToken(tokenList, seen, NormalizeToken(item.AlternateSerials[i]));
                }
            }

            return new DeviceRowIdentity(TransportKind.Wireless, wirelessPrimary, tokenList.ToArray());
        }

        public bool MatchesExactPrimary(DeviceRowIdentity other)
        {
            return other != null
                && Transport == other.Transport
                && !string.IsNullOrEmpty(PrimaryKey)
                && string.Equals(PrimaryKey, other.PrimaryKey, StringComparison.OrdinalIgnoreCase);
        }

        public bool SharesTokenWith(DeviceRowIdentity other)
        {
            if (other == null || Transport != other.Transport) return false;
            if (tokens.Length == 0 || other.tokens.Length == 0) return false;

            for (int i = 0; i < tokens.Length; i++)
            {
                string left = tokens[i];
                for (int j = 0; j < other.tokens.Length; j++)
                {
                    if (string.Equals(left, other.tokens[j], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool Matches(DeviceRowIdentity other)
        {
            return MatchesExactPrimary(other) || SharesTokenWith(other);
        }

        static TransportKind ResolveTransport(DeviceItem item)
        {
            if (item.Channel == ChannelKind.Usb) return TransportKind.Usb;
            if (IsPendingWireless(item.Channel) || item.Channel == ChannelKind.WirelessConnected)
            {
                return TransportKind.Wireless;
            }
            return AdbScanner.IsWirelessDevice(item) ? TransportKind.Wireless : TransportKind.Usb;
        }

        static string TrimToken(string raw)
        {
            return string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim();
        }

        static string NormalizeToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string trimmed = raw.Trim();
            string normalizedEp = ConfigManager.NormalizeEndpoint(trimmed);
            return !string.IsNullOrEmpty(normalizedEp) ? normalizedEp : trimmed;
        }

        static string FirstNonEmpty(params string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!string.IsNullOrEmpty(candidates[i])) return candidates[i];
            }
            return "";
        }

        static void AddToken(List<string> list, HashSet<string> seen, string token)
        {
            if (!string.IsNullOrEmpty(token) && seen.Add(token))
            {
                list.Add(token);
            }
        }
    }
}
