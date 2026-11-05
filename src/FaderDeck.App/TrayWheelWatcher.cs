using System.Runtime.InteropServices;

namespace FaderDeck.App;

/// <summary>
/// 全局鼠标滚轮钩子：仅在鼠标停在托盘图标上时截下滚轮事件，用来调个人合成器音量。
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
