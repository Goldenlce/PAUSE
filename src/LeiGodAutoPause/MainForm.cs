using Microsoft.Win32;

namespace LeiGodAutoPause;

public sealed class MainForm : Form
{
    private const string AppTitle = "雷神加速器自动暂停助手";

    private readonly object _configLock = new();
    private readonly SemaphoreSlim _actionLock = new(1, 1);
    private readonly LeigodApiClient _apiClient = new();
    private readonly GameProcessMonitor _monitor;

    private readonly ListBox _gameList = new();
    private readonly CheckBox _pauseOnExitCheck = new();
    private readonly CheckBox _resumeOnStartCheck = new();
    private readonly CheckBox _pauseOnShutdownCheck = new();
    private readonly CheckBox _startWithWindowsCheck = new();
    private readonly CheckBox _closeToTrayCheck = new();
    private readonly NumericUpDown _delaySeconds = new();
    private readonly NumericUpDown _pollSeconds = new();
    private readonly Label _sessionStatusLabel = new();
    private readonly Label _monitorStatusLabel = new();
    private readonly TextBox _logBox = new();
    private readonly Button _startStopButton = new();

    private AppConfig _config;
    private LeigodSession? _session;
    private DateTime _lastSessionReadUtc = DateTime.MinValue;
    private DateTime _lastPauseRequestUtc = DateTime.MinValue;
    private bool _updatingUi;
    private bool _allowExit;
    private bool _trayHintShown;
    private bool _resourcesDisposed;
    private readonly bool _startMinimized;
    private int _shutdownPauseAttempted;
    private NotifyIcon? _trayIcon;

    public MainForm(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
        _config = AppConfig.Load();
        NormalizeConfig(_config);
        _monitor = new GameProcessMonitor(GetMonitorOptions);

        BuildUi();
        LoadConfigIntoUi();
        CreateTrayIcon();
        SystemEvents.SessionEnding += OnSystemSessionEnding;
        ApplyStartupSetting(_config.StartWithWindows, logAlways: false);

        _monitor.RunningStateChanged += MonitorOnRunningStateChanged;
        _monitor.Faulted += MonitorOnFaulted;

        Shown += async (_, _) =>
        {
            if (_startMinimized)
            {
                ShowInTaskbar = false;
                Hide();
            }

            AppendLog("程序已启动。添加游戏 exe 后，点击“开始监控”即可。");

            if (GetMonitorOptions().GamePaths.Count > 0)
            {
                StartMonitoring();
            }

            await RefreshSessionAsync(force: true, showMessage: false);
        };

        FormClosing += OnFormClosing;
        FormClosed += (_, _) => DisposeResources();
    }

    private void BuildUi()
    {
        Text = AppTitle;
        Width = 980;
        Height = 760;
        MinimumSize = new Size(860, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(247, 248, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 318));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMainContent(), 0, 1);
        root.Controls.Add(BuildLogArea(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 10) };

        var title = new Label
        {
            Text = AppTitle,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 32, 40),
            Location = new Point(0, 0)
        };

        var description = new Label
        {
            Text = "检测配置的游戏进程：游戏运行时可自动恢复时长，全部退出后自动暂停计时。",
            AutoSize = true,
            ForeColor = Color.FromArgb(90, 96, 108),
            Location = new Point(2, 42)
        };

        _sessionStatusLabel.Text = "雷神登录信息：未读取";
        _sessionStatusLabel.AutoSize = true;
        _sessionStatusLabel.ForeColor = Color.FromArgb(52, 120, 246);
        _sessionStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _sessionStatusLabel.Location = new Point(690, 20);

        panel.Controls.Add(title);
        panel.Controls.Add(description);
        panel.Controls.Add(_sessionStatusLabel);
        panel.Resize += (_, _) => _sessionStatusLabel.Left = Math.Max(0, panel.ClientSize.Width - _sessionStatusLabel.Width);
        return panel;
    }

    private Control BuildMainContent()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        layout.Controls.Add(BuildGameGroup(), 0, 0);
        layout.Controls.Add(BuildOptionsGroup(), 1, 0);
        return layout;
    }

    private Control BuildGameGroup()
    {
        var group = new GroupBox
        {
            Text = "1. 要监控的游戏",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(37, 42, 52)
        };

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 8, 0) };
        _gameList.Dock = DockStyle.Fill;
        _gameList.IntegralHeight = false;
        _gameList.SelectionMode = SelectionMode.One;
        _gameList.HorizontalScrollbar = true;

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };

        var addButton = CreateButton("添加游戏 exe", 118);
        addButton.Click += AddGameButton_Click;

        var removeButton = CreateButton("移除选中", 94);
        removeButton.Click += RemoveGameButton_Click;

        buttonPanel.Controls.Add(addButton);
        buttonPanel.Controls.Add(removeButton);

        listPanel.Controls.Add(_gameList);
        listPanel.Controls.Add(buttonPanel);
        group.Controls.Add(listPanel);
        return group;
    }

    private Control BuildOptionsGroup()
    {
        var group = new GroupBox
        {
            Text = "2. 自动化选项",
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 10, 8),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(37, 42, 52)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(0, 4, 0, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _pauseOnExitCheck.Text = "游戏全部退出后自动暂停时长";
        _pauseOnExitCheck.Checked = true;
        _pauseOnExitCheck.AutoSize = true;
        _pauseOnExitCheck.Anchor = AnchorStyles.Left;
        _pauseOnExitCheck.CheckedChanged += SettingsChanged;

        _resumeOnStartCheck.Text = "游戏启动时自动恢复时长";
        _resumeOnStartCheck.AutoSize = true;
        _resumeOnStartCheck.Anchor = AnchorStyles.Left;
        _resumeOnStartCheck.CheckedChanged += SettingsChanged;

        _pauseOnShutdownCheck.Text = "关机或注销时自动暂停时长";
        _pauseOnShutdownCheck.AutoSize = true;
        _pauseOnShutdownCheck.Anchor = AnchorStyles.Left;
        _pauseOnShutdownCheck.CheckedChanged += SettingsChanged;

        _startWithWindowsCheck.Text = "开机自动启动（缩到托盘）";
        _startWithWindowsCheck.AutoSize = true;
        _startWithWindowsCheck.Anchor = AnchorStyles.Left;
        _startWithWindowsCheck.CheckedChanged += SettingsChanged;

        _closeToTrayCheck.Text = "关闭窗口时最小化到托盘";
        _closeToTrayCheck.AutoSize = true;
        _closeToTrayCheck.Anchor = AnchorStyles.Left;
        _closeToTrayCheck.CheckedChanged += SettingsChanged;

        _delaySeconds.Minimum = 0;
        _delaySeconds.Maximum = 300;
        _delaySeconds.Value = 15;
        _delaySeconds.Width = 72;
        _delaySeconds.Anchor = AnchorStyles.Left;
        _delaySeconds.ValueChanged += SettingsChanged;

        _pollSeconds.Minimum = 1;
        _pollSeconds.Maximum = 60;
        _pollSeconds.Value = 3;
        _pollSeconds.Width = 72;
        _pollSeconds.Anchor = AnchorStyles.Left;
        _pollSeconds.ValueChanged += SettingsChanged;

        layout.Controls.Add(_pauseOnExitCheck, 0, 0);
        layout.SetColumnSpan(_pauseOnExitCheck, 2);
        layout.Controls.Add(_resumeOnStartCheck, 0, 1);
        layout.SetColumnSpan(_resumeOnStartCheck, 2);
        layout.Controls.Add(new Label { Text = "退出后延迟（秒）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_delaySeconds, 1, 2);
        layout.Controls.Add(new Label { Text = "检测间隔（秒）", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        layout.Controls.Add(_pollSeconds, 1, 3);
        layout.Controls.Add(_pauseOnShutdownCheck, 0, 4);
        layout.SetColumnSpan(_pauseOnShutdownCheck, 2);
        layout.Controls.Add(_startWithWindowsCheck, 0, 5);
        layout.SetColumnSpan(_startWithWindowsCheck, 2);
        layout.Controls.Add(_closeToTrayCheck, 0, 6);
        layout.SetColumnSpan(_closeToTrayCheck, 2);

        var reloadButton = CreateButton("重新读取雷神登录信息", 188);
        reloadButton.Click += async (_, _) => await RefreshSessionAsync(force: true, showMessage: true);
        layout.Controls.Add(reloadButton, 0, 7);
        layout.SetColumnSpan(reloadButton, 2);

        var testButton = CreateButton("测试雷神连接", 150);
        testButton.Click += async (_, _) => await TestConnectionAsync();
        layout.Controls.Add(testButton, 1, 7);

        var hint = new Label
        {
            Text = "提示：本程序从雷神本机日志读取当前会话，不保存账号密码或登录令牌。",
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            ForeColor = Color.FromArgb(110, 116, 128),
            Anchor = AnchorStyles.Left | AnchorStyles.Top
        };
        layout.Controls.Add(hint, 0, 8);
        layout.SetColumnSpan(hint, 2);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildLogArea()
    {
        var group = new GroupBox
        {
            Text = "运行日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(37, 42, 52)
        };

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.BackColor = Color.FromArgb(250, 251, 253);
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 9F);

        group.Controls.Add(_logBox);
        return group;
    }

    private Control BuildFooter()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };

        _monitorStatusLabel.Text = "监控状态：未启动";
        _monitorStatusLabel.AutoSize = true;
        _monitorStatusLabel.ForeColor = Color.FromArgb(90, 96, 108);
        _monitorStatusLabel.Location = new Point(2, 24);

        var pauseButton = CreateButton("立即暂停", 96);
        pauseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        pauseButton.Click += async (_, _) => await PauseNowAsync(automatic: false);

        var resumeButton = CreateButton("立即恢复", 96);
        resumeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        resumeButton.Click += async (_, _) => await ResumeNowAsync(automatic: false);

        _startStopButton.Text = "开始监控";
        _startStopButton.Width = 110;
        _startStopButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _startStopButton.Click += (_, _) =>
        {
            if (_monitor.IsMonitoring)
            {
                StopMonitoring();
            }
            else
            {
                StartMonitoring();
            }
        };

        panel.Controls.Add(_monitorStatusLabel);
        panel.Controls.Add(_startStopButton);
        panel.Controls.Add(pauseButton);
        panel.Controls.Add(resumeButton);

        panel.Resize += (_, _) =>
        {
            _startStopButton.Location = new Point(panel.ClientSize.Width - _startStopButton.Width, 12);
            pauseButton.Location = new Point(_startStopButton.Left - pauseButton.Width - 8, 12);
            resumeButton.Location = new Point(pauseButton.Left - resumeButton.Width - 8, 12);
        };

        return panel;
    }

    private static Button CreateButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 0, 8, 0)
        };
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("立即暂停", null, async (_, _) => await PauseNowAsync(automatic: false));
        menu.Items.Add("立即恢复", null, async (_, _) => await ResumeNowAsync(automatic: false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Text = AppTitle,
            Icon = SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void LoadConfigIntoUi()
    {
        _updatingUi = true;
        try
        {
            _pauseOnExitCheck.Checked = _config.PauseWhenAllGamesExit;
            _resumeOnStartCheck.Checked = _config.ResumeWhenGameStarts;
            _pauseOnShutdownCheck.Checked = _config.PauseOnShutdown;
            _startWithWindowsCheck.Checked = _config.StartWithWindows;
            _closeToTrayCheck.Checked = _config.CloseToTray;
            _delaySeconds.Value = Math.Clamp(_config.ExitDelaySeconds, (int)_delaySeconds.Minimum, (int)_delaySeconds.Maximum);
            _pollSeconds.Value = Math.Clamp(_config.PollSeconds, (int)_pollSeconds.Minimum, (int)_pollSeconds.Maximum);
            RefreshGameList();
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void RefreshGameList()
    {
        _gameList.BeginUpdate();
        try
        {
            _gameList.Items.Clear();
            foreach (var path in _config.GamePaths.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                _gameList.Items.Add(path);
            }
        }
        finally
        {
            _gameList.EndUpdate();
        }
    }

    private void AddGameButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要监控的游戏程序",
            Filter = "游戏程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        lock (_configLock)
        {
            foreach (var file in dialog.FileNames)
            {
                if (!_config.GamePaths.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    _config.GamePaths.Add(file);
                }
            }
        }

        SaveConfig();
        RefreshGameList();
        AppendLog($"已添加 {dialog.FileNames.Length} 个游戏程序。");
    }

    private void RemoveGameButton_Click(object? sender, EventArgs e)
    {
        if (_gameList.SelectedItem is not string selectedPath)
        {
            return;
        }

        lock (_configLock)
        {
            _config.GamePaths.RemoveAll(path => string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        SaveConfig();
        RefreshGameList();
        AppendLog("已移除选中的游戏程序。");
    }

    private void SettingsChanged(object? sender, EventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        lock (_configLock)
        {
            _config.PauseWhenAllGamesExit = _pauseOnExitCheck.Checked;
            _config.ResumeWhenGameStarts = _resumeOnStartCheck.Checked;
            _config.PauseOnShutdown = _pauseOnShutdownCheck.Checked;
            _config.StartWithWindows = _startWithWindowsCheck.Checked;
            _config.CloseToTray = _closeToTrayCheck.Checked;
            _config.ExitDelaySeconds = (int)_delaySeconds.Value;
            _config.PollSeconds = (int)_pollSeconds.Value;
        }

        SaveConfig();
        ApplyStartupSetting(_startWithWindowsCheck.Checked, logAlways: true);
    }

    private void StartMonitoring()
    {
        var options = GetMonitorOptions();
        if (options.GamePaths.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一个游戏程序。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _monitor.Start();
        _startStopButton.Text = "停止监控";
        _monitorStatusLabel.Text = "监控状态：监控中";
        _monitorStatusLabel.ForeColor = Color.FromArgb(22, 138, 82);
        AppendLog("已开始监控游戏进程。");
    }

    private void StopMonitoring()
    {
        _monitor.Stop();
        _startStopButton.Text = "开始监控";
        _monitorStatusLabel.Text = "监控状态：已停止";
        _monitorStatusLabel.ForeColor = Color.FromArgb(90, 96, 108);
        AppendLog("已停止监控。");
    }

    private void MonitorOnRunningStateChanged(object? sender, bool running)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => MonitorOnRunningStateChanged(sender, running)));
            return;
        }

        _ = HandleRunningStateChangedAsync(running);
    }

    private async Task HandleRunningStateChangedAsync(bool running)
    {
        if (running)
        {
            _monitorStatusLabel.Text = "监控状态：游戏运行中";
            _monitorStatusLabel.ForeColor = Color.FromArgb(22, 138, 82);
            AppendLog("检测到游戏已启动。");

            if (!GetConfigSnapshot().ResumeWhenGameStarts)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            if (_monitor.IsAnyGameRunning())
            {
                await ResumeNowAsync(automatic: true);
            }

            return;
        }

        _monitorStatusLabel.Text = "监控状态：监控中";
        _monitorStatusLabel.ForeColor = Color.FromArgb(52, 120, 246);
        AppendLog("检测到游戏已全部退出。");

        if (!GetConfigSnapshot().PauseWhenAllGamesExit)
        {
            return;
        }

        var delaySeconds = GetConfigSnapshot().ExitDelaySeconds;
        if (delaySeconds > 0)
        {
            AppendLog($"将在 {delaySeconds} 秒后确认并自动暂停。");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        if (!GetConfigSnapshot().PauseWhenAllGamesExit)
        {
            AppendLog("自动暂停已在等待期间被关闭，本次不执行。");
            return;
        }

        if (_monitor.IsAnyGameRunning())
        {
            AppendLog("检测到游戏重新启动，已取消本次自动暂停。");
            return;
        }

        await PauseNowAsync(automatic: true);
    }

    private void MonitorOnFaulted(object? sender, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => MonitorOnFaulted(sender, message)));
            return;
        }

        AppendLog("监控异常：" + message);
    }

    private async Task RefreshSessionAsync(bool force, bool showMessage)
    {
        if (!force && _session is not null && DateTime.UtcNow - _lastSessionReadUtc < TimeSpan.FromMinutes(3))
        {
            return;
        }

        var result = await Task.Run(() =>
        {
            var parsed = LeigodSessionReader.TryReadLatest(out var message);
            return (Session: parsed, Message: message);
        });

        _session = result.Session;
        _lastSessionReadUtc = DateTime.UtcNow;
        UpdateSessionLabel();

        AppendLog(result.Message);
        if (showMessage)
        {
            MessageBox.Show(
                this,
                result.Message,
                AppTitle,
                MessageBoxButtons.OK,
                result.Session is null ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    private async Task<bool> EnsureSessionAsync(bool showMessage)
    {
        if (_session is null || DateTime.UtcNow - _lastSessionReadUtc >= TimeSpan.FromMinutes(3))
        {
            await RefreshSessionAsync(force: true, showMessage: false);
        }

        if (_session is not null)
        {
            return true;
        }

        if (showMessage)
        {
            MessageBox.Show(
                this,
                "未读取到雷神登录信息。请先启动并登录雷神加速器，然后点击“重新读取雷神登录信息”。",
                AppTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        return false;
    }

    private void UpdateSessionLabel()
    {
        if (_session is null)
        {
            _sessionStatusLabel.Text = "雷神登录信息：未读取";
            _sessionStatusLabel.ForeColor = Color.FromArgb(180, 84, 44);
            return;
        }

        _sessionStatusLabel.Text = $"雷神登录信息：已读取（账号 {_session.MaskedAccountId}）";
        _sessionStatusLabel.ForeColor = Color.FromArgb(22, 138, 82);
        _sessionStatusLabel.Left = Math.Max(0, _sessionStatusLabel.Parent?.ClientSize.Width - _sessionStatusLabel.Width ?? 0);
    }

    private async Task TestConnectionAsync()
    {
        if (!await EnsureSessionAsync(showMessage: true) || _session is null)
        {
            return;
        }

        var result = await _apiClient.TestAsync(_session);
        AppendLog(result.Message);
        MessageBox.Show(this, result.Message, AppTitle, MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task PauseNowAsync(bool automatic)
    {
        await _actionLock.WaitAsync();
        try
        {
            if (!await EnsureSessionAsync(showMessage: !automatic) || _session is null)
            {
                return;
            }

            if (DateTime.UtcNow - _lastPauseRequestUtc < TimeSpan.FromSeconds(8))
            {
                AppendLog("刚刚已经发送过暂停请求，本次跳过重复操作。");
                return;
            }

            var result = await _apiClient.PauseAsync(_session);
            AppendLog(automatic ? "自动暂停：" + result.Message : result.Message);
            if (result.Success)
            {
                _lastPauseRequestUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private async Task ResumeNowAsync(bool automatic)
    {
        await _actionLock.WaitAsync();
        try
        {
            if (!await EnsureSessionAsync(showMessage: !automatic) || _session is null)
            {
                return;
            }

            var result = await _apiClient.ResumeAsync(_session);
            AppendLog(automatic ? "自动恢复：" + result.Message : result.Message);
        }
        finally
        {
            _actionLock.Release();
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            PauseBeforeShutdown();
        }

        if (_allowExit ||
            e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing ||
            !GetConfigSnapshot().CloseToTray)
        {
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(2500, AppTitle, "程序仍在托盘运行，会继续监控游戏进程。", ToolTipIcon.Info);
        }
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    private void ApplyStartupSetting(bool enabled, bool logAlways)
    {
        var executablePath = Application.ExecutablePath;
        var success = StartupManager.Apply(enabled, executablePath, out var message);
        if (logAlways || !success)
        {
            AppendLog(message);
        }
    }

    private void OnSystemSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (e.Reason is SessionEndReasons.SystemShutdown or SessionEndReasons.Logoff)
        {
            PauseBeforeShutdown();
        }
    }

    private void PauseBeforeShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownPauseAttempted, 1) != 0)
        {
            return;
        }

        if (!GetConfigSnapshot().PauseOnShutdown)
        {
            return;
        }

        try
        {
            var session = LeigodSessionReader.TryReadLatest(out _) ?? _session;
            if (session is null)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _apiClient.PauseAsync(session, timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Windows shutdown is time-limited. The request is best-effort only.
        }
    }

    private void SaveConfig()
    {
        try
        {
            lock (_configLock)
            {
                _config.Save();
            }
        }
        catch (Exception ex)
        {
            AppendLog("保存设置失败：" + ex.Message);
        }
    }

    private AppConfig GetConfigSnapshot()
    {
        lock (_configLock)
        {
            return new AppConfig
            {
                PauseWhenAllGamesExit = _config.PauseWhenAllGamesExit,
                ResumeWhenGameStarts = _config.ResumeWhenGameStarts,
                PauseOnShutdown = _config.PauseOnShutdown,
                StartWithWindows = _config.StartWithWindows,
                ExitDelaySeconds = _config.ExitDelaySeconds,
                PollSeconds = _config.PollSeconds,
                CloseToTray = _config.CloseToTray,
                GamePaths = _config.GamePaths.ToList()
            };
        }
    }

    private GameMonitorOptions GetMonitorOptions()
    {
        lock (_configLock)
        {
            return new GameMonitorOptions(_config.GamePaths.ToArray(), _config.PollSeconds);
        }
    }

    private static void NormalizeConfig(AppConfig config)
    {
        config.GamePaths ??= new List<string>();
        config.GamePaths = config.GamePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _logBox.AppendText(line);

        if (_logBox.TextLength > 120_000)
        {
            _logBox.Text = _logBox.Text[^80_000..];
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        SystemEvents.SessionEnding -= OnSystemSessionEnding;
        _monitor.Dispose();
        _apiClient.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeResources();
        }

        base.Dispose(disposing);
    }
}











