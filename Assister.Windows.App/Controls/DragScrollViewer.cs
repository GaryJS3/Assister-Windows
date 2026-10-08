using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Assister.Windows.App.Controls;

public sealed class DragScrollViewer : ScrollViewer
{
    private Point? _origin;
    private double _offset;
    private bool _dragging;
    internal bool IsUserScrolling => _dragging || TouchesOver.Any();

    public DragScrollViewer()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PanningMode = PanningMode.VerticalOnly;
        PanningDeceleration = 0.002;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.StylusDevice is not null || IsInteractive(e.OriginalSource as DependencyObject)) return;
        _origin = e.GetPosition(this);
        _offset = VerticalOffset;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed) { _origin = null; return; }
        if (_origin is not { } origin || e.LeftButton != MouseButtonState.Pressed || e.StylusDevice is not null) return;
        var delta = e.GetPosition(this).Y - origin.Y;
        if (!_dragging && Math.Abs(delta) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_dragging)
        {
            _dragging = CaptureMouse();
            if (!_dragging) { _origin = null; return; }
        }
        ScrollToVerticalOffset(_offset - delta);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_dragging) { e.Handled = true; ReleaseMouseCapture(); }
        _origin = null;
        _dragging = false;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _origin = null;
        _dragging = false;
        base.OnLostMouseCapture(e);
    }

    private bool IsInteractive(DependencyObject? source)
    {
        while (source is not null && source != this)
        {
            if (source is ButtonBase or TextBoxBase or PasswordBox) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
