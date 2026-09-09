using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

public partial class MainWindow
{
    private readonly string? settingsReadWarning;
    private WindowState lastVisibleState = WindowState.Maximized;
    private EditorWindowPlacement? closingPlacement;

    private void InitializeWindowPlacement()
    {
        // Apply before the first visible frame, including existing settings files with no placement.
        WindowState = WindowState.Maximized;
        void RestorePlacement()
        {
            if (settings.WindowPlacement is not { } saved ||
                !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) ||
                !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height) ||
                saved.Width < MinWidth || saved.Height < MinHeight ||
                Math.Abs(saved.Left) > 100000 || Math.Abs(saved.Top) > 100000 || saved.Width > 100000 || saved.Height > 100000) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            // Require a reachable title bar on a connected monitor. Otherwise use the default maximized window.
            var title = new NativeRect { Left = (int)(saved.Left * dpi.DpiScaleX), Top = (int)(saved.Top * dpi.DpiScaleY),
                Right = (int)((saved.Left + Math.Min(saved.Width, 200)) * dpi.DpiScaleX), Bottom = (int)((saved.Top + 32) * dpi.DpiScaleY) };
            if (MonitorFromRect(ref title, 0) == IntPtr.Zero) return;
            WindowState = WindowState.Normal;
            Left = saved.Left; Top = saved.Top; Width = saved.Width; Height = saved.Height;
            WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
            lastVisibleState = WindowState;
        }
        RestorePlacement();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) lastVisibleState = WindowState; };
        Closing += (_, _) =>
        {
            var bounds = RestoreBounds;
            if (bounds.IsEmpty) return;
            closingPlacement = new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, lastVisibleState == WindowState.Maximized);
        };
        Closed += (_, _) =>
        {
            if (closingPlacement is null) return;
            settings = settings with { WindowPlacement = closingPlacement };
            SaveSettings();
        };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
}
