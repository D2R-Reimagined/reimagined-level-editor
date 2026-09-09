using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace D2RLevel.App;

public partial class MainWindow
{
    private static void FocusDs1(Window window)
    {
        if (!window.IsVisible) window.Show();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        // Restore the previous normal/maximized state, not just visibility.
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
        // Raise without changing its size/position or making it always-on-top.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        SetForegroundWindow(handle);
        window.Activate(); window.Focus();
    }
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
