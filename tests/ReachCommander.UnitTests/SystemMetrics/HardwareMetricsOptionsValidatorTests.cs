using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class HardwareMetricsOptionsValidatorTests
{
    private readonly HardwareMetricsOptionsValidator _validator = new();

    [Fact]
    public void Validate_accepts_the_approved_defaults()
    {
        var result = _validator.Validate(null, new HardwareMetricsOptions());

        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(4, 15, 2000)]
    [InlineData(5, 5, 2000)]
    [InlineData(5, 15, 5000)]
    public void Validate_rejects_unsafe_timing_combinations(
        int sampleSeconds,
        int staleSeconds,
        int timeoutMilliseconds)
    {
        var options = new HardwareMetricsOptions
        {
            SampleIntervalSeconds = sampleSeconds,
            StaleAfterSeconds = staleSeconds,
            CollectorTimeoutMilliseconds = timeoutMilliseconds,
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("proc")]
    [InlineData("../proc")]
    [InlineData("")]
    public void Validate_rejects_non_absolute_linux_roots(string root)
    {
        var options = new HardwareMetricsOptions { LinuxProcRoot = root };

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
