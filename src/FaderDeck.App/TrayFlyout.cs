using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace FaderDeck.App;

/// <summary>
/// 托盘悬停时的小浮窗：模仿系统音量提示，显示“个人合成器设备名 + 音量百分比”。
/// 浮窗不抢焦点、不接收鼠标事件，鼠标滚轮由 <see cref="TrayWheelWatcher"/> 处理。
/// </summary>
internal sealed class TrayVolumeFlyout : Window
{
    private readonly TextBlock _nameText;
    private readonly TextBlock _percentText;
    private readonly Border _root;

    public TrayVolumeFlyout()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _nameText = new TextBlock
        {
            FontSize = 11,
            MaxWidth = 210,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xC6, 0xD0, 0xDC)),
        };

        _percentText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xF2, 0xF5, 0xFF)),
        };

        var panel = new StackPanel();
        panel.Children.Add(_nameText);
        panel.Children.Add(_percentText);

        _root = new Border
        {
            Padding = new Thickness(14, 9, 14, 10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(WpfColor.FromRgb(0x1E, 0x25, 0x2B)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x2F, 0x3C, 0x48)),
            BorderThickness = new Thickness(1),
            Child = panel,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.45,
                Color = System.Windows.Media.Colors.Black,
            },
        };

        Content = _root;
    }

    public void Update(string name, int percent)
    {
        _nameText.Text = string.IsNullOrWhiteSpace(name) ? "个人合成器" : name;
        _percentText.Text = $"{percent}%";
    }

    public void AllowClose() => _allowClose = true;

    private bool _allowClose;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    public void ShowNearCursor()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : 180;
        var height = ActualHeight > 0 ? ActualHeight : 70;
        var cursor = System.Windows.Forms.Cursor.Position;

        var left = cursor.X - (width / 2);
        left = Math.Clamp(left, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        var top = area.Bottom - height - 14;

        Left = left;
        Top = top;
        if (!IsVisible)
        {
            Show();
        }
    }
}

/// <summary>
/// 托盘悬停期间的全局滚轮监听：只有鼠标正停在托盘图标上时才吞掉滚轮并调整音量，
/// 其余时刻完全放行，不影响系统与其它程序。
/// </summary>
internal sealed class TrayWheelWatcher : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;

    private readonly Func<bool> _isHovering;
    private readonly Action<int> _onWheel;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;

    public TrayWheelWatcher(Func<bool> isHovering, Action<int> onWheel)
    {
        _isHovering = isHovering;
        _onWheel = onWheel;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, IntPtr.Zero, 0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmMouseWheel && _isHovering())
        {
            var data = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var delta = (short)((data.MouseData >> 16) & 0xFFFF);
            _onWheel(delta);
            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public Point Pt;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
