using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class MetricNormalizerTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(43.126, 43.1)]
    [InlineData(101, 100)]
    public void Percent_clamps_and_rounds_finite_values(double input, double expected) =>
        Assert.Equal(expected, MetricNormalizer.Percent(input));

    [Fact]
    public void Percent_rejects_non_finite_values()
    {
        Assert.Null(MetricNormalizer.Percent(double.NaN));
        Assert.Null(MetricNormalizer.Percent(double.PositiveInfinity));
    }

    [Fact]
    public void Label_removes_controls_bounds_length_and_uses_fallback()
    {
        Assert.Equal("CPU Fan", MetricNormalizer.Label(" CPU\0 Fan ", "Fan"));
        Assert.Equal("Fan", MetricNormalizer.Label(" \0 ", "Fan"));
        Assert.Equal(96, MetricNormalizer.Label(new string('x', 120), "Fan").Length);
    }

    [Fact]
    public void Non_negative_integer_rejects_values_that_are_unsafe_in_json_clients()
    {
        Assert.Equal(0, MetricNormalizer.NonNegative(0));
        Assert.Equal(9_007_199_254_740_991, MetricNormalizer.NonNegative(9_007_199_254_740_991));
        Assert.Null(MetricNormalizer.NonNegative(-1));
        Assert.Null(MetricNormalizer.NonNegative(long.MaxValue));
    }

    [Theory]
    [InlineData(55000, 55.0)]
    [InlineData(-1000, -1.0)]
    public void Millidegrees_converts_to_celsius(long input, double expected) =>
        Assert.Equal(expected, MetricNormalizer.MillidegreesCelsius(input));
}
