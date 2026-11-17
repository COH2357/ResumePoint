using System.Drawing;
using ResumePoint.Core;

namespace ResumePoint.UI;

/// <summary>托盘宿主：常驻后台、承载菜单、驱动记录端循环。</summary>
public sealed class TrayHost : IDisposable
{
    private readonly Settings _settings;
    private readonly ResumeDb _db;
    private readonly Recorder _recorder;
    private readonly PlayerLauncher _launcher;
    private readonly Smtc _smtc;

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem = new("状态：空闲");
    private readonly ToolStripMenuItem _currentItem = new("当前：—") { Enabled = false };
    private readonly ToolStripMenuItem _recentItem = new("最近记录");
    private readonly ToolStripMenuItem _recordToggle = new("记录端运行中");
    private readonly Control _dispatch = new();

    private CancellationTokenSource _cts = new();
    private Task? _loop;
    private MainForm? _main;
    private bool _recordingEnabled = true;
    private Recorder.RecorderSnapshot? _lastSnap;
    private string _lastStatus = "";

    public ApplicationContext Context { get; }

    public TrayHost(Settings settings, ResumeDb db, Recorder recorder, PlayerLauncher launcher)
    {
        _settings = settings;
        _db = db;
        _recorder = recorder;
        _launcher = launcher;
        _smtc = recorder.Smtc;

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Get(),
            Text = "ResumePoint 断点续播",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(_currentItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_recentItem);
        _menu.Items.Add(new ToolStripMenuItem("打开记录面板…", null, (_, _) => ShowMain()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("用媒体播放器续播打开…", null, (_, _) => PickAndOpen(OpenMode.Resume)));
        _menu.Items.Add(new ToolStripMenuItem("从头播放…", null, (_, _) => PickAndOpen(OpenMode.FromStart)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_recordToggle);
        _recordToggle.Click += (_, _) => ToggleRecording();
        _menu.Items.Add(new ToolStripMenuItem("打开数据目录", null, (_, _) => OpenDataDirectory()));
        _menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray.ContextMenuStrip = _menu;

        _recorder.Changed += OnRecorderChanged;

        Context = new ApplicationContext();

        // 强制创建窗口句柄：Control.BeginInvoke 在没有句柄时会直接抛
        // InvalidOperationException（"Invoke or BeginInvoke cannot be called on a control
        // until the window handle has been created"），状态更新会被静默吞掉。
        _ = _dispatch.Handle;

        // 关键：SMTC manager 必须在有消息泵的 UI 线程上创建，
        // 否则它看不到"后来才出现"的媒体会话（播放器是之后才启动的）。
        // AttachDispatcher 让后台线程的请求也能把创建动作调度回 UI 线程。
        _smtc.AttachDispatcher(_dispatch);
        _ = _smtc.WarmUpAsync();

        RefreshRecentMenu();
        UpdateStatus();

        _loop = Task.Run(RecordLoopAsync);
        AppLog.Info($"ResumePoint 启动，数据库 {settings.DatabasePath}");
    }

    // ------------------------------------------------------------ 记录端循环

    private async Task RecordLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (_recordingEnabled)
            {
                try
                {
                    await _recorder.TickAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error("记录端轮询异常", ex);
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ToggleRecording()
    {
        _recordingEnabled = !_recordingEnabled;
        _recordToggle.Text = _recordingEnabled ? "记录端运行中" : "记录端已暂停";
        _recordToggle.Checked = _recordingEnabled;
        AppLog.Info($"记录端已{(_recordingEnabled ? "启用" : "暂停")}");
        UpdateStatus();
    }

    // ------------------------------------------------------------ 状态刷新

    private void OnRecorderChanged(Recorder.RecorderSnapshot snap)
    {
        _lastSnap = snap;
        try
        {
            _dispatch.BeginInvoke(new Action(UpdateStatus));
        }
        catch { }
    }

    private void UpdateStatus()
    {
        var snap = _lastSnap;
        string status;
        string current;

        if (snap is null || snap.Session is null)
        {
            current = "当前：—";
            status = "● 空闲（播放器未运行）";
        }
        else
        {
            var pos = PlayerLauncher.FormatTime(snap.PositionSeconds);
            if (snap.Path is not null)
            {
                var name = Path.GetFileName(snap.Path);
                current = $"当前：{name}  @ {pos}";
                status = snap.Confidence >= PathGuess.High ? "● 记录中" : "● 等待识别文件";
            }
            else
            {
                current = $"当前：（{snap.Title ?? "未知"}） @ {pos}";
                status = "● 等待识别文件";
            }
        }

        _currentItem.Text = current;
        SetStatus(status, status.Contains("空闲") ? Color.Gray
                    : status.Contains("等待") ? Color.DarkOrange : Color.ForestGreen);

        if (status != _lastStatus)
        {
            _lastStatus = status;
            AppLog.Info($"托盘状态: {status}  {current}");
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusItem.Text = text;
        _statusItem.ForeColor = color;
        var plain = text.Replace("●", "").Trim();
        _tray.Text = Truncate($"ResumePoint — {plain}", 63);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void RefreshRecentMenu()
    {
        _recentItem.DropDownItems.Clear();
        try
        {
            var records = _db.List(limit: 8);
            if (records.Count == 0)
            {
                _recentItem.DropDownItems.Add(new ToolStripMenuItem("（暂无记录）") { Enabled = false });
                return;
            }
            foreach (var r in records)
            {
                var name = Path.GetFileName(r.DisplayPath);
                var label = $"{name}  @ {PlayerLauncher.FormatTime(r.Position)}";
                var item = new ToolStripMenuItem(label, null, (_, _) => _ = OpenPathAsync(r.DisplayPath, OpenMode.Resume));
                item.Tag = r.DisplayPath;
                _recentItem.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("刷新最近记录失败", ex);
        }
    }

    // ------------------------------------------------------------ 操作

    private void PickAndOpen(OpenMode mode)
    {
        using var dlg = new OpenFileDialog
        {
            Title = mode == OpenMode.Resume ? "用媒体播放器续播打开" : "从头播放",
            Filter = "媒体文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.mp3;*.m4a;*.flac;*.wav;*.aac;*.wma|" +
                     "所有文件|*.*",
            Multiselect = false,
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _ = OpenPathAsync(dlg.FileName, mode);
    }

    private async Task OpenPathAsync(string path, OpenMode mode)
    {
        try
        {
            var outcome = await _launcher.OpenAsync(path, mode, CancellationToken.None).ConfigureAwait(false);
            if (outcome.Message is not null)
            {
                _dispatch.BeginInvoke(new Action(() =>
                {
                    _tray.BalloonTipTitle = "ResumePoint";
                    _tray.BalloonTipText = outcome.Message!;
                    _tray.BalloonTipIcon = outcome.Seeked ? ToolTipIcon.Info : ToolTipIcon.Warning;
                    _tray.ShowBalloonTip(4000);
                }));
            }
            RefreshRecentMenu();
        }
        catch (Exception ex)
        {
            AppLog.Error("打开失败", ex);
        }
    }

    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(_settings.DataDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settings.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开数据目录失败", ex);
        }
    }

    public void ShowMain()
    {
        try
        {
            if (_main is null || _main.IsDisposed)
            {
                _main = new MainForm(_settings, _db, _launcher, RefreshRecentMenu);
                _main.FormClosed += (_, _) => _main = null;
            }
            _main.Show();
            _main.WindowState = FormWindowState.Normal;
            _main.Activate();
        }
        catch (Exception ex)
        {
            AppLog.Error("显示主面板失败", ex);
        }
    }

    private void ShowSettings()
    {
        ShowMain();
        _main?.ShowSettingsTab();
    }

    private void ExitApp()
    {
        Context?.ExitThread();
    }

    private void Shutdown()
    {
        try
        {
            _cts.Cancel();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _dispatch.Dispose();
            AppLog.Info("ResumePoint 退出");
        }
        catch { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        Shutdown();
        try { _cts?.Dispose(); } catch { }
    }
}
