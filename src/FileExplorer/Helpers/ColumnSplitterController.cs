using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FileExplorer.Helpers;

/// Wires pointer drag on a thin splitter element to resize an adjacent Grid ColumnDefinition.
public sealed class ColumnSplitterController
{
    private readonly SplitterHandle _splitter;
    private readonly ColumnDefinition _column;
    private readonly bool _invert;
    private readonly double _min;
    private readonly double _max;
    private bool _dragging;
    private double _startWidth;
    private Windows.Foundation.Point _startPoint;

    public ColumnSplitterController(SplitterHandle splitter, ColumnDefinition column, bool invert, double min, double max)
    {
        _splitter = splitter;
        _column = column;
        _invert = invert;
        _min = min;
        _max = max;

        splitter.PointerEntered += (_, _) => splitter.SetResizeCursor(true);
        splitter.PointerExited += (_, _) => splitter.SetResizeCursor(false);
        splitter.PointerPressed += OnPressed;
        splitter.PointerMoved += OnMoved;
        splitter.PointerReleased += OnReleased;
        splitter.PointerCaptureLost += (_, _) => _dragging = false;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _startWidth = _column.ActualWidth;
        _startPoint = e.GetCurrentPoint(null).Position;
        _splitter.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var pos = e.GetCurrentPoint(null).Position;
        var delta = pos.X - _startPoint.X;
        if (_invert)
        {
            delta = -delta;
        }

        var newWidth = Math.Clamp(_startWidth + delta, _min, _max);
        _column.Width = new GridLength(newWidth);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _splitter.ReleasePointerCapture(e.Pointer);
    }
}
