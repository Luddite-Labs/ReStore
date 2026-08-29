using FluentAssertions;
using ReStore.Behaviors;

namespace ReStore.Tests;

public class NestedScrollGestureTests
{
    private const int DefaultLines = 3;
    private const double WheelNotch = 120.0;

    // One notch with the default three-lines-per-notch setting.
    private const double NotchShift = 3 * NestedScrollGesture.LinePixels;

    [Fact]
    public void ContentThatFitsBubblesImmediately()
    {
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 0, scrollableHeight: 0, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.BubbleToParent);
    }

    [Fact]
    public void MidContentWheelDownScrollsSelf()
    {
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 100, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(100 + NotchShift);
    }

    [Fact]
    public void MidContentWheelUpScrollsSelf()
    {
        var decision = NestedScrollGesture.Decide(WheelNotch, verticalOffset: 100, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(100 - NotchShift);
    }

    [Fact]
    public void AtBottomWheelDownBubblesToParent()
    {
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 300, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.BubbleToParent);
    }

    [Fact]
    public void AtTopWheelUpBubblesToParent()
    {
        var decision = NestedScrollGesture.Decide(WheelNotch, verticalOffset: 0, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.BubbleToParent);
    }

    [Fact]
    public void AtBottomWheelUpStillScrollsSelf()
    {
        var decision = NestedScrollGesture.Decide(WheelNotch, verticalOffset: 300, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(300 - NotchShift);
    }

    [Fact]
    public void SubPixelRemainderCountsAsAtLimit()
    {
        // Layout rounding can leave a hair of scrollable height behind; treating it as
        // scrollable would keep swallowing the wheel and re-create the stuck feel.
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 299.9, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.BubbleToParent);
    }

    [Fact]
    public void ScrollSelfClampsToScrollableHeight()
    {
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 290, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(300);
    }

    [Fact]
    public void ScrollSelfClampsToZero()
    {
        var decision = NestedScrollGesture.Decide(WheelNotch, verticalOffset: 10, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(0);
    }

    [Fact]
    public void NegativeWheelScrollLinesMeansPageScroll()
    {
        // SystemParameters.WheelScrollLines reports -1 when Windows is set to
        // "one screen at a time".
        var decision = NestedScrollGesture.Decide(-WheelNotch, verticalOffset: 0, scrollableHeight: 900, viewportHeight: 220, wheelScrollLines: -1);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().Be(220);
    }

    [Fact]
    public void PrecisionTouchpadDeltaScrollsProportionally()
    {
        var decision = NestedScrollGesture.Decide(-40, verticalOffset: 100, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.ScrollSelf);
        decision.TargetOffset.Should().BeApproximately(100 + (NotchShift / 3.0), 0.001);
    }

    [Fact]
    public void ZeroDeltaIsIgnored()
    {
        var decision = NestedScrollGesture.Decide(0, verticalOffset: 100, scrollableHeight: 300, viewportHeight: 220, DefaultLines);

        decision.Action.Should().Be(NestedScrollAction.Ignore);
    }
}
