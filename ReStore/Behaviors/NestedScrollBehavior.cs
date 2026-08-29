using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReStore.Behaviors
{
    /// <summary>
    /// Lets a wheel notch fall through to the outer scroller once an inner
    /// <see cref="ScrollViewer"/> has reached the edge it is being pushed against.
    /// <para>
    /// Attach with <c>behaviors:NestedScrollBehavior.CooperativeScrolling="True"</c> on any
    /// <see cref="ScrollViewer"/> nested inside another one. See
    /// <see cref="NestedScrollGesture"/> for why this is needed.
    /// </para>
    /// </summary>
    public static class NestedScrollBehavior
    {
        public static readonly DependencyProperty CooperativeScrollingProperty =
            DependencyProperty.RegisterAttached(
                "CooperativeScrolling",
                typeof(bool),
                typeof(NestedScrollBehavior),
                new PropertyMetadata(false, OnCooperativeScrollingChanged));

        public static void SetCooperativeScrolling(DependencyObject element, bool value)
            => element.SetValue(CooperativeScrollingProperty, value);

        public static bool GetCooperativeScrolling(DependencyObject element)
            => (bool)element.GetValue(CooperativeScrollingProperty);

        private static void OnCooperativeScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer scrollViewer)
            {
                return;
            }

            // Detach first so re-setting the property cannot double-subscribe.
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;

            if (e.NewValue is true)
            {
                scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            var decision = NestedScrollGesture.Decide(
                e.Delta,
                scrollViewer.VerticalOffset,
                scrollViewer.ScrollableHeight,
                scrollViewer.ViewportHeight,
                SystemParameters.WheelScrollLines);

            switch (decision.Action)
            {
                case NestedScrollAction.ScrollSelf:
                    // Handle it ourselves rather than letting the default handler run, so the
                    // bubble case below stays the only other outcome.
                    scrollViewer.ScrollToVerticalOffset(decision.TargetOffset);
                    e.Handled = true;
                    break;

                case NestedScrollAction.BubbleToParent:
                    RaiseOnParent(scrollViewer, e);
                    break;

                case NestedScrollAction.Ignore:
                    break;
            }
        }

        /// <summary>
        /// Marks the event handled here and re-raises an equivalent one from the parent, which is
        /// what actually lets the outer scroller see a notch the inner one declined.
        /// </summary>
        private static void RaiseOnParent(ScrollViewer scrollViewer, MouseWheelEventArgs e)
        {
            if (scrollViewer.Parent is not UIElement parent)
            {
                return;
            }

            e.Handled = true;

            var bubbled = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = scrollViewer
            };

            parent.RaiseEvent(bubbled);
        }
    }
}
