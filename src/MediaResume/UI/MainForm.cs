using System.Drawing;
using MediaResume.Core;

namespace MediaResume.UI;

/// <summary>主面板：记录列表 + 设置。</summary>
public sealed class MainForm : Form
{
    private readonly Settings _settings;
    private readonly ResumeDb _db;
    private readonly PlayerLauncher _launcher;
    private readonly Action _onRecordsChanged;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new();
    private readonly Label _currentLabel = new() { Text = "当前未在播放", AutoSize = true };
    private readonly Label _currentDetail = new() { Text = "", AutoSize = true, ForeColor = Color.Gray };
    private readonly ProgressBar _currentBar = new() { Height = 6, Dock = DockStyle.Top };

    // 设置控件
    private readonly TextBox _dataDir = new() { Width = 360 };
    private readonly NumericUpDown _pollInterval = new() { Minimum = 1, Maximum = 30, Width = 70 };
    private readonly NumericUpDown _commitInterval = new() { Minimum = 1, Maximum = 120, Width = 70 };
    private readonly NumericUpDown _minResume = new() { Minimum = 0, Maximum = 600, Width = 70 };
    private readonly NumericUpDown _completeWithin = new() { Minimum = 0, Maximum = 300, Width = 70 };
    private readonly CheckBox _autoResume = new() { Text = "打开文件时自动续播", AutoSize = true };
    private readonly CheckBox _autoPlay = new() { Text = "跳转后自动播放（否则跳完先暂停）", AutoSize = true };
    private readonly CheckBox _minimizeOnClose = new() { Text = "关闭主窗口时隐藏到托盘", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "开机自动启动", AutoSize = true };

    private bool _loading;

    public MainForm(Settings settings, ResumeDb db, PlayerLauncher launcher, Action onRecordsChanged)
    {
        _settings = settings;
        _db = db;
        _launcher = launcher;
        _onRecordsChanged = onRecordsChanged;

        Text = "MediaResume —— 断点续播";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 480);
        Size = new Size(940, 600);

        BuildUi();
        LoadSettingsToUi();
        RefreshRecords();

        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // ---- 顶部：当前会话 ----
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(10, 8, 10, 4),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));

        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        left.Controls.Add(_currentLabel);
        left.Controls.Add(_currentDetail);
        statusPanel.Controls.Add(left, 0, 0);

        var topRight = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        topRight.Controls.Add(_currentBar);
        statusPanel.Controls.Add(topRight, 1, 0);

        root.Controls.Add(statusPanel, 0, 0);

        // ---- 页签 ----
        _tabs.TabPages.Add(BuildRecordsTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        root.Controls.Add(_tabs, 0, 1);

        Controls.Add(root);
    }

    private TabPage BuildRecordsTab()
    {
        var page = new TabPage("记录");

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 246, 248);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(OpenMode.Resume); };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "文件", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pos", HeaderText = "位置", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "dur", HeaderText = "时长", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "prog", HeaderText = "进度", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "last", HeaderText = "最后播放", FillWeight = 17 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "watch", HeaderText = "累计观看", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "路径", Visible = false });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6) };
        buttons.Controls.Add(MakeButton("刷新", RefreshRecords));
        buttons.Controls.Add(MakeButton("续播打开", () => OpenSelected(OpenMode.Resume)));
        buttons.Controls.Add(MakeButton("从头播放", () => OpenSelected(OpenMode.FromStart)));
        buttons.Controls.Add(MakeButton("定位文件", LocateSelected));
        buttons.Controls.Add(MakeButton("删除记录", DeleteSelected));
        buttons.Controls.Add(MakeButton("清空全部", ClearAll));

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        split.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
        split.Controls.Add(_grid, 0, 0);
        split.Controls.Add(buttons, 0, 1);

        page.Controls.Add(split);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("设置");
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(14, 12, 14, 12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;
        void AddRow(string label, Control control, string? hint = null)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 0, row);
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(3, 4, 3, 4);
            table.Controls.Add(control, 1, row);
            if (hint is not null)
                table.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.Gray, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 2, row);
            row++;
        }

        var dirPanel = new FlowLayoutPanel { AutoSize = true };
        dirPanel.Controls.Add(_dataDir);
        var browse = MakeButton("浏览…", BrowseDataDir);
        browse.Margin = new Padding(6, 0, 0, 0);
        dirPanel.Controls.Add(browse);
        AddRow("记录写入位置", dirPanel, "数据库与日志存放处，可随时更改");

        AddRow("轮询间隔（秒）", _pollInterval, "多久读一次播放器位置");
        AddRow("落盘间隔（秒）", _commitInterval, "断电最多丢失这么久的位置");
        AddRow("最小续播秒数", _minResume, "断点早于这个值就从头播");
        AddRow("距片尾（秒）", _completeWithin, "距结尾这么近算「已看完」，不再续播");

        _autoResume.CheckedChanged += (_, _) => SaveSettings();
        _autoPlay.CheckedChanged += (_, _) => SaveSettings();
        _minimizeOnClose.CheckedChanged += (_, _) => SaveSettings();
        _startWithWindows.CheckedChanged += (_, _) => SaveSettings();
        _pollInterval.ValueChanged += (_, _) => SaveSettings();
        _commitInterval.ValueChanged += (_, _) => SaveSettings();
        _minResume.ValueChanged += (_, _) => SaveSettings();
        _completeWithin.ValueChanged += (_, _) => SaveSettings();

        AddRow("", _autoResume);
        AddRow("", _autoPlay);
        AddRow("", _minimizeOnClose);
        AddRow("", _startWithWindows);

        var info = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(3, 14, 3, 3),
            Text = "说明：本工具不修改媒体播放器本体，通过 Windows 官方 SMTC 接口读取/跳转播放位置。\r\n" +
                   "SMTC 需要交互式桌面会话，因此本工具不能作为 Windows 服务运行。",
        };
        table.Controls.Add(info, 1, row);

        page.Controls.Add(table);
        return page;
    }

    private static Button MakeButton(string text, Action action)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28 };
        b.Click += (_, _) => action();
        return b;
    }

    // ------------------------------------------------------------ 设置

    private void LoadSettingsToUi()
    {
        _loading = true;
        try
        {
            _dataDir.Text = _settings.DataDirectory;
            _pollInterval.Value = Math.Clamp(_settings.PollIntervalSeconds, 1, 30);
            _commitInterval.Value = Math.Clamp(_settings.CommitIntervalSeconds, 1, 120);
            _minResume.Value = (int)Math.Clamp(_settings.MinResumeSeconds, 0, 600);
            _completeWithin.Value = (int)Math.Clamp(_settings.CompleteWithinSeconds, 0, 300);
            _autoResume.Checked = _settings.AutoResumeEnabled;
            _autoPlay.Checked = _settings.AutoPlayAfterSeek;
            _minimizeOnClose.Checked = _settings.MinimizeOnClose;
            _startWithWindows.Checked = AutoStart.IsEnabled();
        }
        finally { _loading = false; }
    }

    private void SaveSettings()
    {
        if (_loading) return;
        try
        {
            _settings.DataDirectory = _dataDir.Text.Trim();
            _settings.PollIntervalSeconds = (int)_pollInterval.Value;
            _settings.CommitIntervalSeconds = (int)_commitInterval.Value;
            _settings.MinResumeSeconds = (double)_minResume.Value;
            _settings.CompleteWithinSeconds = (double)_completeWithin.Value;
            _settings.AutoResumeEnabled = _autoResume.Checked;
            _settings.AutoPlayAfterSeek = _autoPlay.Checked;
            _settings.MinimizeOnClose = _minimizeOnClose.Checked;

            if (_startWithWindows.Checked)
            {
                AutoStart.Enable();
                _settings.StartWithWindows = AutoStart.IsEnabled();
            }
            else
            {
                AutoStart.Disable();
                _settings.StartWithWindows = false;
            }

            _settings.Normalize();
            _settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Error("保存设置失败", ex);
        }
    }

    private void BrowseDataDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择记录写入位置",
            SelectedPath = _dataDir.Text,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _dataDir.Text = dlg.SelectedPath;
            SaveSettings();
        }
    }

    // ------------------------------------------------------------ 记录

    public void RefreshRecords()
    {
        try
        {
            _grid.Rows.Clear();
            foreach (var r in _db.List())
            {
                var idx = _grid.Rows.Add();
                var row = _grid.Rows[idx];
                row.Cells["name"].Value = Path.GetFileName(r.DisplayPath);
                row.Cells["pos"].Value = PlayerLauncher.FormatTime(r.Position);
                row.Cells["dur"].Value = PlayerLauncher.FormatTime(r.Duration);
                row.Cells["prog"].Value = $"{r.Progress * 100:0}%";
                row.Cells["last"].Value = r.LastPlayedAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(r.LastPlayedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                    : "—";
                row.Cells["watch"].Value = PlayerLauncher.FormatTime(r.TotalWatched);
                row.Cells["state"].Value = r.IsComplete ? "已看完" : "未看完";
                row.Cells["path"].Value = r.DisplayPath;
                if (r.IsComplete) row.DefaultCellStyle.ForeColor = Color.Gray;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("刷新记录失败", ex);
        }
    }

    private MediaRecord? SelectedRecord()
    {
        if (_grid.CurrentRow is null) return null;
        var path = _grid.CurrentRow.Cells["path"].Value as string;
        return path is null ? null : _db.FindByPath(path);
    }

    private async void OpenSelected(OpenMode mode)
    {
        var rec = SelectedRecord();
        if (rec is null)
        {
            MessageBox.Show(this, "请先选中一条记录。", "MediaResume", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var outcome = await _launcher.OpenAsync(rec.DisplayPath, mode, CancellationToken.None);
            MessageBox.Show(this, outcome.Message ?? "已完成", "MediaResume",
                MessageBoxButtons.OK, outcome.Seeked ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("打开失败", ex);
            MessageBox.Show(this, ex.Message, "MediaResume", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LocateSelected()
    {
        var rec = SelectedRecord();
        if (rec is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{rec.DisplayPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("定位文件失败", ex);
        }
    }

    private void DeleteSelected()
    {
        var rec = SelectedRecord();
        if (rec is null) return;
        if (MessageBox.Show(this, $"删除这条记录？\n\n{rec.DisplayPath}", "MediaResume",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _db.Delete(rec.Id);
        RefreshRecords();
        _onRecordsChanged?.Invoke();
    }

    private void ClearAll()
    {
        if (MessageBox.Show(this, "清空全部断点记录？此操作不可撤销。", "MediaResume",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _db.Clear();
        RefreshRecords();
        _onRecordsChanged?.Invoke();
    }

    public void ShowSettingsTab() => _tabs.SelectedIndex = 1;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_settings.MinimizeOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveSettings();
    }
}
