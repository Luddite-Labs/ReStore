using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// The CLI and every GUI surface share this formatter, so its output is user-visible in
/// several places at once.
/// </summary>
public class ByteFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1099511627776, "1 TB")]
    public void Format_ShouldUseBinaryUnits(long bytes, string expected)
    {
        ByteFormatter.Format(bytes).Should().Be(expected);
    }

    [Fact]
    public void Format_ShouldStopAtLargestUnit_RatherThanIndexingPastIt()
    {
        // Guards the unit-table bound: an unclamped loop would walk off the end.
        ByteFormatter.Format(long.MaxValue).Should().EndWith(" PB");
    }

    [Fact]
    public void Format_ShouldRenderNegativeValuesWithSign()
    {
        // Dedup savings arithmetic can momentarily produce a negative, which must not render
        // as a nonsense magnitude.
        ByteFormatter.Format(-1536).Should().Be("-1.5 KB");
    }

    [Fact]
    public void Format_ShouldHandleLongMinValue_WithoutOverflowing()
    {
        // -long.MinValue overflows a long, so the sign handling must widen first.
        var act = () => ByteFormatter.Format(long.MinValue);

        act.Should().NotThrow();
        ByteFormatter.Format(long.MinValue).Should().StartWith("-");
    }
}
