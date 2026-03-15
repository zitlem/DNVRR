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

    public IntPtr VideoHandle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            "static",
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

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
    }

    // Win32 interop
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
