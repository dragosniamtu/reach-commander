using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsOptionsValidator : IValidateOptions<HardwareMetricsOptions>
{
    public ValidateOptionsResult Validate(string? name, HardwareMetricsOptions options)
    {
        var failures = new List<string>();

        if (options.SampleIntervalSeconds < 5)
        {
            failures.Add("HardwareMetrics:SampleIntervalSeconds must be at least 5.");
        }

        if (options.StaleAfterSeconds <= options.SampleIntervalSeconds)
        {
            failures.Add("HardwareMetrics:StaleAfterSeconds must exceed the sample interval.");
        }

        if (options.CollectorTimeoutMilliseconds <= 0 ||
            options.CollectorTimeoutMilliseconds >= options.SampleIntervalSeconds * 1000)
        {
            failures.Add("HardwareMetrics:CollectorTimeoutMilliseconds must be positive and shorter than the sample interval.");
        }

        if (!IsTrustedAbsoluteRoot(options.LinuxProcRoot))
        {
            failures.Add("HardwareMetrics:LinuxProcRoot must be absolute.");
        }

        if (!IsTrustedAbsoluteRoot(options.LinuxSysRoot))
        {
            failures.Add("HardwareMetrics:LinuxSysRoot must be absolute.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsTrustedAbsoluteRoot(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.StartsWith("/", StringComparison.Ordinal) || Path.IsPathFullyQualified(value));
}
