namespace ReStore.Behaviors
{
    /// <summary>
    /// What a wheel notch over a nested scroller should do.
    /// </summary>
    public enum NestedScrollAction
    {
        /// <summary>No movement and no interception; leave the event untouched.</summary>
        Ignore,

        /// <summary>The inner scroller can move in the requested direction, so it consumes the notch.</summary>
        ScrollSelf,

        /// <summary>The inner scroller is at its limit; the notch belongs to the outer scroller.</summary>
        BubbleToParent
    }

    /// <summary>
    /// Outcome of <see cref="NestedScrollGesture.Decide"/>.
    /// <see cref="TargetOffset"/> is only meaningful for <see cref="NestedScrollAction.ScrollSelf"/>.
    /// </summary>
    public readonly record struct NestedScrollDecision(NestedScrollAction Action, double TargetOffset);

    /// <summary>
    /// Decides whether a nested scroller keeps a wheel notch or hands it to its parent.
    /// <para>
    /// WPF's <c>ScrollViewer.OnMouseWheel</c> marks the event handled whenever it has scroll info,
    /// even when it is pinned at an edge and cannot move. Inside a page-wide scroller that means the
    /// wheel dies over any inner card and page scrolling appears to seize up until the scrollbar is
    /// dragged by hand.
    /// </para>
    /// <para>
    /// Kept free of WPF types so the arithmetic is unit-testable; the WPF wiring lives in
    /// <c>NestedScrollBehavior</c>.
    /// </para>
    /// </summary>
    public static class NestedScrollGesture
    {
        /// <summary>Pixels per scrolled line, matching <c>ScrollViewer</c>'s internal line delta.</summary>
        public const double LinePixels = 16.0;

        /// <summary>Delta reported by one detent of a standard notched wheel.</summary>
        private const double WheelDelta = 120.0;

        /// <summary>
        /// Slack when comparing an offset against an edge. Layout rounding can leave a fraction of a
        /// pixel of scrollable height behind; without slack that residue reads as "still scrollable"
        /// and the notch keeps getting swallowed, which is the very stall we are fixing.
        /// </summary>
        private const double EdgeTolerance = 0.5;

        /// <param name="delta">Wheel delta; positive scrolls up, as in <c>MouseWheelEventArgs.Delta</c>.</param>
        /// <param name="verticalOffset">Current vertical offset of the inner scroller.</param>
        /// <param name="scrollableHeight">Maximum vertical offset of the inner scroller.</param>
        /// <param name="viewportHeight">Viewport height, used when Windows requests page-at-a-time scrolling.</param>
        /// <param name="wheelScrollLines">
        /// Lines per notch from <c>SystemParameters.WheelScrollLines</c>. Negative means
        /// "one screen at a time", so a notch travels a full viewport.
        /// </param>
        public static NestedScrollDecision Decide(
            double delta,
            double verticalOffset,
            double scrollableHeight,
            double viewportHeight,
            int wheelScrollLines)
        {
            if (delta == 0 || double.IsNaN(delta))
            {
                return new NestedScrollDecision(NestedScrollAction.Ignore, verticalOffset);
            }

            // Nothing to scroll here, so the notch was never ours to take.
            if (scrollableHeight <= EdgeTolerance)
            {
                return new NestedScrollDecision(NestedScrollAction.BubbleToParent, verticalOffset);
            }

            bool scrollingDown = delta < 0;

            bool pinnedAtEdge = scrollingDown
                ? verticalOffset >= scrollableHeight - EdgeTolerance
                : verticalOffset <= EdgeTolerance;

            if (pinnedAtEdge)
            {
                return new NestedScrollDecision(NestedScrollAction.BubbleToParent, verticalOffset);
            }

            double notches = Math.Abs(delta) / WheelDelta;
            double travel = wheelScrollLines < 0
                ? notches * viewportHeight
                : notches * wheelScrollLines * LinePixels;

            double target = scrollingDown
                ? verticalOffset + travel
                : verticalOffset - travel;

            target = Math.Clamp(target, 0, scrollableHeight);

            return new NestedScrollDecision(NestedScrollAction.ScrollSelf, target);
        }
    }
}
