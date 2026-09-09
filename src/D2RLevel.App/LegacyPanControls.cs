using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace D2RLevel.App;

public sealed partial class LegacyFloorWindow
{
    private void HookPanning(ScrollViewer scroll)
    {
        bool panning = false;
        Point anchor = default;
        double startX = 0, startY = 0;
        Cursor? previousCursor = null;
        bool previousForceCursor = false;
        scroll.Background = Brushes.Transparent;
        scroll.ToolTip = "Middle-drag to pan the collision map";
        void EndPan()
        {
            if (!panning) return;
            panning = false;
            scroll.Cursor = previousCursor; scroll.ForceCursor = previousForceCursor;
            if (scroll.IsMouseCaptured) scroll.ReleaseMouseCapture();
        }
        scroll.PreviewMouseDown += (_, e) =>
        {
            if (panning) { e.Handled = true; return; }
            if (e.ChangedButton != MouseButton.Middle) return;
            if (painting) CancelStroke();
            anchor = e.GetPosition(scroll); startX = scroll.HorizontalOffset; startY = scroll.VerticalOffset;
            previousCursor = scroll.Cursor; previousForceCursor = scroll.ForceCursor;
            panning = scroll.CaptureMouse();
            if (panning) { scroll.Cursor = Cursors.Hand; scroll.ForceCursor = true; }
            e.Handled = true;
        };
        scroll.PreviewMouseMove += (_, e) =>
        {
            if (!panning) return;
            if (e.MiddleButton != MouseButtonState.Pressed) { EndPan(); return; }
            var delta = e.GetPosition(scroll) - anchor;
            scroll.ScrollToHorizontalOffset(startX - delta.X);
            scroll.ScrollToVerticalOffset(startY - delta.Y);
            e.Handled = true;
        };
        scroll.PreviewMouseUp += (_, e) =>
        {
            if (!panning) return;
            if (e.ChangedButton == MouseButton.Middle) EndPan();
            e.Handled = true;
        };
        scroll.LostMouseCapture += (_, e) => { if (e.OriginalSource == scroll) EndPan(); };
        Deactivated += (_, _) => EndPan();
        Closed += (_, _) => EndPan();
        PreviewKeyDown += (_, e) => { if (panning && e.Key == Key.Escape) { EndPan(); e.Handled = true; } };
    }
}
