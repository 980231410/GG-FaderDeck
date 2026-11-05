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
    private TrayVolumeFlyout? _flyout;
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

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 SteelSeries GG", null, (_, _) => OpenSteelSeriesGg());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _applicationIcon = TrayIconFactory.Create();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
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

        _flyout = new TrayVolumeFlyout();
        _trayIcon.MouseMove += (_, _) => OnTrayHover();

        _wheelWatcher = new TrayWheelWatcher(
            () => DateTime.UtcNow < _hoverUntil,
            delta => Dispatcher.BeginInvoke(() => OnTrayWheel(delta)));

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow >= _hoverUntil)
            {
                HideFlyout();
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

        if (_flyout is not null)
        {
            _flyout.AllowClose();
            _flyout.Close();
        }

        _applicationIcon?.Dispose();

        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private void OnTrayHover()
    {
        if (_mainWindow is null || _flyout is null)
        {
            return;
        }

        _hoverUntil = DateTime.UtcNow.AddMilliseconds(1200);
        var (name, percent) = _mainWindow.GetPersonalOutputState();
        _flyout.Update(name, percent);
        _flyout.ShowNearCursor();
    }

    private void OnTrayWheel(int wheelDelta)
    {
        if (_mainWindow is null || _flyout is null || DateTime.UtcNow >= _hoverUntil)
        {
            return;
        }

        var step = wheelDelta > 0 ? 2 : -2;
        var percent = _mainWindow.NudgePersonalOutput(step);
        var (name, _) = _mainWindow.GetPersonalOutputState();
        _flyout.Update(name, percent);
        _hoverUntil = DateTime.UtcNow.AddMilliseconds(1200);
        _flyout.ShowNearCursor();
    }

    private void HideFlyout()
    {
        _hoverUntil = DateTime.MinValue;
        _flyout?.Hide();
    }

    private static void OpenSteelSeriesGg()
    {
        var candidates = new[]
        {
            @"C:\Program Files\SteelSeries\GG\SteelSeriesGG.exe",
            @"C:\Program Files (x86)\SteelSeries\GG\SteelSeriesGG.exe",
        };

        foreach (var path in candidates)
        {
            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path)!,
                });
                return;
            }
            catch
            {
                // 打不开就静默放弃，菜单项不应该把程序带崩。
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
