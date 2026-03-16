using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DNVRR.Controls;

/// <summary>
/// HwndHost that creates a native child window for HCNetSDK to render into.
/// The SDK draws video frames directly to this HWND via DirectDraw/D3D.
/// </summary>
public class VideoPanel : HwndHost
{
    private IntPtr _hwnd;
    private static bool _classRegistered;
    private static readonly object _classLock = new();
    private const string ClassName = "DNVRRVideoPanel";

    public IntPtr VideoHandle => _hwnd;

    public event EventHandler? NativeDoubleClick;
    public event EventHandler? NativeMouseDown;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();

        _hwnd = CreateWindowEx(
            0,
            ClassName,
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0,
            (int)ActualWidth, (int)ActualHeight,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        return new HandleRef(this, _hwnd);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_LBUTTONDBLCLK = 0x0203;
        const int WM_LBUTTONDOWN = 0x0201;

        if (msg == WM_LBUTTONDBLCLK)
        {
            NativeDoubleClick?.Invoke(this, EventArgs.Empty);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_LBUTTONDOWN)
        {
            NativeMouseDown?.Invoke(this, EventArgs.Empty);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (_classLock)
        {
            if (_classRegistered) return;

            var wc = new WNDCLASS
            {
                style = CS_DBLCLKS,
                lpfnWin32WndProc = DefWindowProcW,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
                hbrBackground = GetStockObject(BLACK_BRUSH),
            };

            RegisterClassW(ref wc);
            _classRegistered = true;
        }
    }

    // Win32 interop
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int BLACK_BRUSH = 4;
    private const uint CS_DBLCLKS = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public Win32WndProc lpfnWin32WndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate IntPtr Win32WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
