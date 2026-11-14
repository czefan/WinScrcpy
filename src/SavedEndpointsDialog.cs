using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed class SavedEndpointsDialog : Form
{
    internal readonly DataGridView grid;
    readonly Label statusLabel;
    readonly Button addButton;
    readonly Button removeButton;
    readonly Button cancelButton;
    readonly Button saveButton;
    readonly ConfigManager configManager;
    readonly AppConfig config;

    public bool Saved { get; private set; }

    public SavedEndpointsDialog(ConfigManager configManager, AppConfig config)
    {
        this.configManager = configManager;
        this.config = config;
        Saved = false;

        Text = "管理常用地址";
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
            if (e.KeyCode == Keys.Escape && (grid == null || !grid.IsCurrentCellInEditMode))
            {
                e.Handled = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };

        SuspendLayout();

        TableLayoutPanel layout = new BufferedTableLayoutPanel();
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.ColumnCount = 1;
        layout.RowCount = 4;
        layout.Padding = UiTheme.Pad(20, 18, 20, 18);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label tipLabel = new Label();
        tipLabel.AutoSize = true;
        tipLabel.MaximumSize = new Size(UiTheme.Scale(540), 0);
        tipLabel.Font = UiTheme.SubFont;
        tipLabel.ForeColor = UiTheme.TextSecondary;
        tipLabel.Margin = UiTheme.Pad(0, 0, 0, 12);
        tipLabel.Text = "维护常用无线地址（不填端口默认 :5555）及显示名称，双击或选中直接输入即可修改：";
        layout.Controls.Add(tipLabel, 0, 0);

        int headerTextH = TextRenderer.MeasureText("地址", UiTheme.BodyBoldFont).Height;
        int cellTextH = TextRenderer.MeasureText("192.168.3.12:5555", UiTheme.BodyFont).Height;

        grid = new BufferedDataGridView();
        grid.Size = UiTheme.Size(540, 250);
        grid.Margin = new Padding(0);
        grid.BackgroundColor = UiTheme.SurfaceBg;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        grid.GridColor = UiTheme.BorderColor;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = Math.Max(UiTheme.Scale(38), headerTextH + UiTheme.Scale(16));
        grid.RowTemplate.Height = Math.Max(UiTheme.Scale(40), cellTextH + UiTheme.Scale(22));

        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.PageBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.Font = UiTheme.BodyBoldFont;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.PageBg;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.TextPrimary;
        grid.ColumnHeadersDefaultCellStyle.Padding = UiTheme.Pad(8, 4, 8, 4);

        grid.DefaultCellStyle.Font = UiTheme.BodyFont;
        grid.DefaultCellStyle.BackColor = UiTheme.SurfaceBg;
        grid.DefaultCellStyle.ForeColor = UiTheme.TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(228, 241, 255);
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.TextPrimary;
        grid.DefaultCellStyle.Padding = UiTheme.Pad(8, 3, 8, 3);

        DataGridViewTextBoxColumn epCol = new DataGridViewTextBoxColumn();
        epCol.Name = "Endpoint";
        epCol.HeaderText = "地址 (IP:Port)";
        epCol.FillWeight = 54f;
        epCol.SortMode = DataGridViewColumnSortMode.NotSortable;

        DataGridViewTextBoxColumn aliasCol = new DataGridViewTextBoxColumn();
        aliasCol.Name = "Alias";
        aliasCol.HeaderText = "设备名称";
        aliasCol.FillWeight = 46f;
        aliasCol.SortMode = DataGridViewColumnSortMode.NotSortable;

        grid.Columns.Add(epCol);
        grid.Columns.Add(aliasCol);
        grid.KeyDown += OnGridKeyDown;

        Panel gridFrame = new BufferedPanel();
        gridFrame.AutoSize = true;
        gridFrame.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        gridFrame.BackColor = UiTheme.InputBorder;
        gridFrame.Padding = new Padding(1);
        gridFrame.Margin = UiTheme.Pad(0, 0, 0, 10);
        gridFrame.Controls.Add(grid);
        layout.Controls.Add(gridFrame, 0, 1);

        statusLabel = new Label();
        statusLabel.AutoSize = true;
        statusLabel.MaximumSize = new Size(UiTheme.Scale(540), 0);
        statusLabel.Font = UiTheme.SubFont;
        statusLabel.ForeColor = UiTheme.TextSecondary;
        statusLabel.Margin = UiTheme.Pad(0, 0, 0, 12);
        statusLabel.Text = " ";
        layout.Controls.Add(statusLabel, 0, 2);

        TableLayoutPanel footer = new BufferedTableLayoutPanel();
        footer.Width = gridFrame.PreferredSize.Width;
        footer.AutoSize = true;
        footer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        footer.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        footer.ColumnCount = 2;
        footer.RowCount = 1;
        footer.Margin = new Padding(0);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        FlowLayoutPanel leftActions = new BufferedFlowLayoutPanel();
        leftActions.AutoSize = true;
        leftActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        leftActions.WrapContents = false;
        leftActions.Anchor = AnchorStyles.Left;
        leftActions.Margin = new Padding(0);

        addButton = UiTheme.MakeToolButton("新增");
        addButton.Margin = UiTheme.Pad(0, 0, 8, 0);
        addButton.Click += OnAddClick;

        removeButton = UiTheme.MakeToolButton("删除选中");
        removeButton.Margin = new Padding(0);
        removeButton.Click += OnRemoveClick;

        leftActions.Controls.Add(addButton);
        leftActions.Controls.Add(removeButton);

        FlowLayoutPanel rightActions = new BufferedFlowLayoutPanel();
        rightActions.AutoSize = true;
        rightActions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        rightActions.WrapContents = false;
        rightActions.Anchor = AnchorStyles.Right;
        rightActions.Margin = new Padding(0);

        cancelButton = UiTheme.MakeToolButton("取消");
        cancelButton.DialogResult = DialogResult.Cancel;
        cancelButton.Margin = UiTheme.Pad(0, 0, 8, 0);
        cancelButton.Click += delegate
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        saveButton = UiTheme.MakePrimaryButton("保存");
        saveButton.Margin = new Padding(0);
        saveButton.Click += OnSaveClick;

        rightActions.Controls.Add(cancelButton);
        rightActions.Controls.Add(saveButton);

        footer.Controls.Add(leftActions, 0, 0);
        footer.Controls.Add(rightActions, 1, 0);
        layout.Controls.Add(footer, 0, 3);

        Controls.Add(layout);
        CancelButton = cancelButton;
        LoadRowsFromConfig();

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

    void LoadRowsFromConfig()
    {
        grid.Rows.Clear();
        if (config == null || config.savedEndpoints == null) return;
        for (int i = 0; i < config.savedEndpoints.Count; i++)
        {
            string ep = config.savedEndpoints[i];
            if (string.IsNullOrWhiteSpace(ep)) continue;
            string alias = ConfigManager.GetDeviceAlias(config, ep);
            grid.Rows.Add(ep, alias);
        }
        UpdateRemoveButtonState();
    }

    void UpdateRemoveButtonState()
    {
        removeButton.Enabled = grid.Rows.Count > 0;
    }

    void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
        {
            e.Handled = true;
            RemoveSelectedRows();
        }
    }

    void OnAddClick(object sender, EventArgs e)
    {
        statusLabel.Text = " ";
        int rowIdx = grid.Rows.Add("", "");
        UpdateRemoveButtonState();
        grid.CurrentCell = grid.Rows[rowIdx].Cells[0];
        grid.BeginEdit(true);
    }

    void OnRemoveClick(object sender, EventArgs e)
    {
        RemoveSelectedRows();
    }

    internal void RemoveSelectedRows()
    {
        if (grid.Rows.Count == 0) return;
        if (grid.IsCurrentCellInEditMode) grid.CancelEdit();

        SortedDictionary<int, bool> toRemove = new SortedDictionary<int, bool>();
        for (int i = 0; i < grid.SelectedCells.Count; i++)
        {
            int idx = grid.SelectedCells[i].RowIndex;
            if (idx >= 0 && idx < grid.Rows.Count) toRemove[idx] = true;
        }
        if (toRemove.Count == 0 && grid.CurrentRow != null && grid.CurrentRow.Index >= 0)
        {
            toRemove[grid.CurrentRow.Index] = true;
        }
        if (toRemove.Count == 0)
        {
            SetError("请先选中要删除的行。");
            return;
        }

        List<int> indices = new List<int>(toRemove.Keys);
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            grid.Rows.RemoveAt(indices[i]);
        }
        statusLabel.Text = " ";
        UpdateRemoveButtonState();
    }

    void OnSaveClick(object sender, EventArgs e)
    {
        if (!TrySaveFromGrid()) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    internal bool TrySaveFromGrid()
    {
        grid.EndEdit();

        List<string> endpoints = new List<string>();
        List<string> aliases = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < grid.Rows.Count; i++)
        {
            DataGridViewRow row = grid.Rows[i];
            string rawEp = (Convert.ToString(row.Cells[0].Value) ?? "").Trim();
            string rawAlias = (Convert.ToString(row.Cells[1].Value) ?? "").Trim();

            if (rawEp.Length == 0 && rawAlias.Length == 0) continue;
            if (rawEp.Length == 0)
            {
                FocusCellWithError(i, 0, string.Format("第 {0} 行已填名称但缺少地址 (IP:Port)。", i + 1));
                return false;
            }

            string normalized;
            if (!ConfigManager.TryValidateEndpoint(rawEp, out normalized))
            {
                FocusCellWithError(i, 0, string.Format("第 {0} 行地址格式无效：{1}", i + 1, rawEp));
                return false;
            }

            if (!seen.Add(normalized))
            {
                FocusCellWithError(i, 0, string.Format("第 {0} 行存在重复地址：{1}", i + 1, normalized));
                return false;
            }

            row.Cells[0].Value = normalized;
            row.Cells[1].Value = rawAlias;
            endpoints.Add(normalized);
            aliases.Add(rawAlias);
        }

        ConfigManager.ApplySavedEndpoints(config, endpoints, aliases);
        if (configManager != null && config != null)
        {
            try
            {
                configManager.Save(config);
            }
            catch (Exception ex)
            {
                SetError("保存配置失败: " + ex.Message);
                return false;
            }
        }

        Saved = true;
        return true;
    }

    void FocusCellWithError(int rowIndex, int colIndex, string message)
    {
        if (rowIndex >= 0 && rowIndex < grid.Rows.Count)
        {
            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[rowIndex].Cells[colIndex];
        }
        SetError(message);
    }

    void SetError(string message)
    {
        statusLabel.ForeColor = UiTheme.TextDanger;
        statusLabel.Text = message;
    }

    sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView()
        {
            DoubleBuffered = true;
        }
    }
}
