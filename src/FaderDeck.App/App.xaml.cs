using System.Windows;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FaderDeck.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private TrayWheelWatcher? _wheelWatcher;
    private DispatcherTimer? _hoverTimer;
    private DateTime _hoverUntil = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isSmokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        var mutexName = isSmokeTest
            ? $"Local\\FaderDeck.SmokeTest.{Environment.ProcessId}"
            : "Local\\FaderDeck.SingleInstance";
        _singleInstanceMutex = new Mutex(true, mutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.HideOnDeactivate = !isSmokeTest;
        MainWindow = _mainWindow;

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            BackColor = TrayMenuRenderer.Background,
            ForeColor = TrayMenuRenderer.Text,
            ShowImageMargin = false,
            DropShadowEnabled = false,
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Forms.Padding(4, 4, 4, 4),
            MinimumSize = new System.Drawing.Size(0, 0),
        };
        menu.Items.Add("打开 SteelSeries GG", null, (_, _) => OpenSteelSeriesGg());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.Padding = new Forms.Padding(8, 5, 10, 5);
        }

        _applicationIcon = TrayIconFactory.Create();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            // 悬停时的系统提示框直接显示音量；内容由 UpdateTrayText 实时刷新。
            Text = "GG FaderDeck",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                TogglePanel();
            }
        };

        _trayIcon.MouseMove += (_, _) => OnTrayHover();

        _wheelWatcher = new TrayWheelWatcher(
            () => DateTime.UtcNow < _hoverUntil,
            delta => Dispatcher.BeginInvoke(() => OnTrayWheel(delta)));

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _hoverUntil)
            {
                ResetHoverState();
            }
        };
        _hoverTimer.Start();

        // Start discovery while the program is quietly resident in the tray, so opening the
        // panel later usually reuses an already warm Sonar connection and EQ cache.
        _mainWindow.LoadSonarStateAsync(showConnecting: false);

        if (isSmokeTest)
        {
            ShowPanel();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ExitApplication();
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _wheelWatcher?.Dispose();
        _hoverTimer?.Stop();

        _applicationIcon?.Dispose();

        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private void OnTrayHover()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _hoverUntil = DateTime.UtcNow.AddMilliseconds(1200);
        var (name, percent) = _mainWindow.GetPersonalOutputState();
        UpdateTrayText(name, percent);
    }

    private void OnTrayWheel(int wheelDelta)
    {
        if (_mainWindow is null || DateTime.UtcNow >= _hoverUntil)
        {
            return;
        }

        var step = wheelDelta > 0 ? 2 : -2;
        var percent = _mainWindow.NudgePersonalOutput(step);
        var (name, _) = _mainWindow.GetPersonalOutputState();
        UpdateTrayText(name, percent);
        _hoverUntil = DateTime.UtcNow.AddMilliseconds(1200);
    }

    /// <summary>
    /// 系统托盘提示框最多 63 个字符，超长会被 WinForms 直接拒掉，所以这里先截断再写入。
    /// </summary>
    private void UpdateTrayText(string name, int percent)
    {
        if (_trayIcon is null)
        {
            return;
        }

        var text = $"{name}：{percent}%";
        if (text.Length > 63)
        {
            text = text[..60] + "...";
        }

        try
        {
            _trayIcon.Text = text;
        }
        catch (ArgumentException)
        {
            // 极端情况下（名字里有非法字符）保持上一次的提示文本。
        }
    }

    private void ResetHoverState()
    {
        _hoverUntil = DateTime.MinValue;
    }

    /// <summary>
    /// 把 GG 界面叫出来。GG 常驻在托盘时，直接启动 SteelSeriesGG.exe 只会让新实例秒退（单实例），
    /// 界面上什么都不会发生；真正会把主窗口叫出来的是托盘宿主 SteelSeriesGGEZ.exe。
    /// </summary>
    private static void OpenSteelSeriesGg()
    {
        var directories = new[]
        {
            @"C:\Program Files\SteelSeries\GG",
            @"C:\Program Files (x86)\SteelSeries\GG",
        };

        var launchers = new[] { "SteelSeriesGGEZ.exe", "SteelSeriesGG.exe" };

        foreach (var directory in directories)
        {
            foreach (var launcher in launchers)
            {
                var path = System.IO.Path.Combine(directory, launcher);
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = directory,
                    });
                    return;
                }
                catch
                {
                    // 打不开就换下一个候选；菜单项不应该把程序带崩。
                }
            }
        }
    }

    private void TogglePanel()
    {
        if (_mainWindow?.IsVisible == true)
        {
            _mainWindow.Hide();
        }
        else
        {
            ShowPanel();
        }
    }

    private void ShowPanel()
    {
        if (_mainWindow is null)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _mainWindow.Left = workArea.Right - _mainWindow.Width - 12;
        _mainWindow.Top = workArea.Bottom - _mainWindow.Height - 12;
        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.LoadSonarStateAsync();
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        Shutdown();
    }
}
