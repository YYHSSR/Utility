using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Utility.App.Controls;

/// <summary>
/// Eased, interruptible mouse-wheel scrolling for any ScrollViewer (enabled from an implicit style).
/// Each wheel notch moves a target offset; every frame the real offset closes a fixed fraction of the
/// remaining distance, so consecutive notches accumulate smoothly and a new notch never jumps.
/// The per-frame loop only runs while a scroll is in flight (idle CPU stays at zero).
/// Nested viewers: an outer viewer defers to an inner one that can still scroll in that direction.
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 96;     // one wheel notch (120 delta)
    private const double SettleRate = 18;         // 1/s; ~95% of a notch done in ~170 ms

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollState), typeof(SmoothScroll));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
            return;
        if ((bool)e.NewValue)
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer || Keyboard.Modifiers != ModifierKeys.None)
            return;
        // Item-based scrolling (CanContentScroll with item units) can't take pixel offsets.
        if (viewer.CanContentScroll && !IsPixelBased(viewer))
            return;

        var down = e.Delta < 0;
        if (HasInnerScrollableViewer(e.OriginalSource as DependencyObject, viewer, down))
            return; // the inner viewer's own handler (later in the tunnel) takes it
        if (!CanScroll(viewer, down))
            return; // at the edge: let an outer viewer have it

        e.Handled = true;
        var state = (ScrollState?)viewer.GetValue(StateProperty);
        if (state is null)
        {
            state = new ScrollState(viewer);
            viewer.SetValue(StateProperty, state);
        }
        state.AddDelta(-e.Delta / 120d * PixelsPerNotch);
    }

    private static bool IsPixelBased(ScrollViewer viewer) =>
        VirtualizingPanel.GetScrollUnit(viewer) == ScrollUnit.Pixel ||
        (viewer.TemplatedParent is ItemsControl items && VirtualizingPanel.GetScrollUnit(items) == ScrollUnit.Pixel);

    private static bool CanScroll(ScrollViewer viewer, bool down) =>
        viewer.ScrollableHeight > 0 &&
        (down ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.5 : viewer.VerticalOffset > 0.5);

    private static bool HasInnerScrollableViewer(DependencyObject? source, ScrollViewer outer, bool down)
    {
        for (var node = source; node is not null && !ReferenceEquals(node, outer); node = Parent(node))
        {
            if (node is ScrollViewer inner && GetIsEnabled(inner) && CanScroll(inner, down))
                return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private sealed class ScrollState(ScrollViewer viewer)
    {
        private double _target = double.NaN;
        private TimeSpan _lastFrame;
        private bool _running;

        public void AddDelta(double delta)
        {
            // Continue from the in-flight target (accumulate) or from where the content is now.
            var start = _running && double.IsFinite(_target) ? _target : viewer.VerticalOffset;
            _target = Math.Clamp(start + delta, 0, viewer.ScrollableHeight);
            if (_running)
                return;
            _running = true;
            _lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _lastFrame)
                return; // same frame reported twice
            var dt = _lastFrame == TimeSpan.Zero ? 1 / 60d : Math.Min((now - _lastFrame).TotalSeconds, 0.05);
            _lastFrame = now;

            _target = Math.Clamp(_target, 0, viewer.ScrollableHeight);
            var current = viewer.VerticalOffset;
            var remaining = _target - current;
            if (Math.Abs(remaining) < 0.5 || !viewer.IsVisible)
            {
                viewer.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }

            // Frame-rate independent exponential approach: starts fast, eases out, never overshoots.
            var step = remaining * (1 - Math.Exp(-SettleRate * dt));
            viewer.ScrollToVerticalOffset(current + step);
        }

        private void Stop()
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
            _target = double.NaN;
        }
    }
}
