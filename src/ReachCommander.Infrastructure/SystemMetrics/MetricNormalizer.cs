namespace ReachCommander.Infrastructure.SystemMetrics;

internal static class MetricNormalizer
{
    public const int MaximumLabelLength = 96;
    public const int MaximumSensorsPerFamily = 128;
    public const int MaximumGpuCount = 16;
    public const long MaximumSafeJsonInteger = 9_007_199_254_740_991;

    public static double? Percent(double? value) =>
        value is null || !double.IsFinite(value.Value)
            ? null
            : Math.Round(Math.Clamp(value.Value, 0, 100), 1, MidpointRounding.AwayFromZero);

    public static double? Celsius(double? value) =>
        value is null || !double.IsFinite(value.Value) || value is < -100 or > 250
            ? null
            : Math.Round(value.Value, 1, MidpointRounding.AwayFromZero);

    public static double? MillidegreesCelsius(long value) => Celsius(value / 1000d);

    public static long? NonNegative(long? value) =>
        value is >= 0 and <= MaximumSafeJsonInteger ? value : null;

    public static string Label(string? value, string fallback)
    {
        var cleaned = CleanLabel(value);

        if (cleaned.Length == 0)
        {
            cleaned = CleanLabel(fallback);
        }

        return cleaned.Length <= MaximumLabelLength
            ? cleaned
            : cleaned[..MaximumLabelLength];
    }

    private static string CleanLabel(string? value) => new string(
        (value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .ToArray()).Trim();
}
