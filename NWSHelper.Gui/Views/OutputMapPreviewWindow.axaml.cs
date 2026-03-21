using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using NWSHelper.Gui.ViewModels;

namespace NWSHelper.Gui.Views;

public partial class OutputMapPreviewWindow : Window
{
    private bool isPanning;
    private Point lastPanPoint;

    public OutputMapPreviewWindow()
    {
        InitializeComponent();
    }

    private OutputMapPreviewViewModel? ViewModel => DataContext as OutputMapPreviewViewModel;

    private void OnMapPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var viewportPoint = e.GetPosition(MapViewport);
        ViewModel?.ZoomByTouchpadDelta(e.Delta.Y, viewportPoint);
        e.Handled = true;
    }

    private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(MapViewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is InputElement inputElement)
        {
            e.Pointer.Capture(inputElement);
        }

        isPanning = true;
        lastPanPoint = e.GetPosition(MapViewport);
        e.Handled = true;
    }

    private void OnMapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isPanning || ViewModel is null)
        {
            return;
        }

        var currentPoint = e.GetPosition(MapViewport);
        var deltaX = currentPoint.X - lastPanPoint.X;
        var deltaY = currentPoint.Y - lastPanPoint.Y;

        lastPanPoint = currentPoint;
        ViewModel.PanByPixels(deltaX, deltaY);
        e.Handled = true;
    }

    private void OnMapPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndPanning(e);
    }

    private void OnMapPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isPanning = false;
    }

    private void EndPanning(PointerEventArgs e)
    {
        isPanning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

