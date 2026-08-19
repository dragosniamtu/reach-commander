using System.Globalization;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics.Linux;

internal sealed class LinuxProcCollector(
    IOptions<HardwareMetricsOptions> options,
    BoundedTextFileReader reader,
    TimeProvider timeProvider,
    IHostPlatform platform) : IHardwareMetricsCollector
{
    private const int MaximumLines = 4096;
    private const int MaximumInterfaces = 256;
    private readonly object _counterGate = new();
    private CpuCounters? _previousCpu;
    private TimedNetworkCounters? _previousNetwork;

    public string Name => "linux-proc";
    public bool IsSupported => platform.IsLinux;

    public async ValueTask<HardwareMetricsContribution> CollectAsync(
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return HardwareMetricsContribution.Unsupported(Name);
        }

        try
        {
            var root = options.Value.LinuxProcRoot;
            var cpuText = await reader.ReadRequiredAsync(Path.Combine(root, "stat"), cancellationToken);
            var memoryText = await reader.ReadRequiredAsync(Path.Combine(root, "meminfo"), cancellationToken);
            var uptimeText = await reader.ReadRequiredAsync(Path.Combine(root, "uptime"), cancellationToken);

            var cpuCounters = ParseCpuCounters(cpuText);
            var memory = ParseMemory(memoryText);
            var uptime = ParseUptime(uptimeText);
            NetworkCounters? networkCounters = null;

            if (options.Value.NetworkEnabled)
            {
                networkCounters = await TryReadNetworkAsync(root, cancellationToken);
            }

            double? cpuUtilization;
            NetworkMetrics? network;
            var sampledAt = timeProvider.GetUtcNow();

            lock (_counterGate)
            {
                cpuUtilization = CalculateCpuUtilization(_previousCpu, cpuCounters);
                _previousCpu = cpuCounters;
                network = CalculateNetwork(_previousNetwork, networkCounters, sampledAt);
                if (networkCounters is not null)
                {
                    _previousNetwork = new TimedNetworkCounters(networkCounters.Value, sampledAt);
                }
            }

            return new HardwareMetricsContribution(
                new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
                uptime,
                new CpuMetrics(cpuUtilization, null, null, null, false, false),
                memory,
                Network: network);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Failed("metrics_input_invalid");
        }
        catch (FormatException)
        {
            return Failed("metrics_input_invalid");
        }
        catch (OverflowException)
        {
            return Failed("metrics_input_invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("metrics_input_unavailable");
        }
        catch (IOException)
        {
            return Failed("metrics_input_unavailable");
        }
    }

    private async ValueTask<NetworkCounters?> TryReadNetworkAsync(
        string root,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await reader.ReadRequiredAsync(
                Path.Combine(root, "net", "dev"),
                cancellationToken);
            return ParseNetworkCounters(text);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private HardwareMetricsContribution Failed(string code) => new(
        new HardwareCollectorStatus(Name, HardwareCollectorState.Failed, code));

    private static CpuCounters ParseCpuCounters(string text)
    {
        foreach (var line in ReadBoundedLines(text))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !fields[0].Equals("cpu", StringComparison.Ordinal))
            {
                continue;
            }

            if (fields.Length < 5)
            {
                throw new FormatException("Aggregate CPU counters are incomplete.");
            }

            Span<ulong> counters = stackalloc ulong[8];
            var count = Math.Min(counters.Length, fields.Length - 1);
            for (var index = 0; index < count; index++)
            {
                if (!ulong.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counters[index]))
                {
                    throw new FormatException("Aggregate CPU counter is invalid.");
                }
            }

            ulong total = 0;
            checked
            {
                for (var index = 0; index < count; index++)
                {
                    total += counters[index];
                }
            }

            var idle = checked(counters[3] + (count > 4 ? counters[4] : 0));
            return new CpuCounters(total, idle);
        }

        throw new FormatException("Aggregate CPU counters are missing.");
    }

    private static MemoryMetrics ParseMemory(string text)
    {
        long? totalKiB = null;
        long? availableKiB = null;

        foreach (var line in ReadBoundedLines(text))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                totalKiB = ParseMemoryKiB(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                availableKiB = ParseMemoryKiB(line);
            }
        }

        if (totalKiB is null or <= 0 || availableKiB is null or < 0)
        {
            throw new FormatException("Required memory counters are missing.");
        }

        var totalBytes = checked(totalKiB.Value * 1024);
        var availableBytes = Math.Min(checked(availableKiB.Value * 1024), totalBytes);
        var usedBytes = totalBytes - availableBytes;
        var normalizedTotal = MetricNormalizer.NonNegative(totalBytes)
            ?? throw new FormatException("Memory total is outside the supported range.");
        var normalizedAvailable = MetricNormalizer.NonNegative(availableBytes)
            ?? throw new FormatException("Memory availability is outside the supported range.");
        var normalizedUsed = MetricNormalizer.NonNegative(usedBytes)
            ?? throw new FormatException("Memory usage is outside the supported range.");

        return new MemoryMetrics(
            normalizedUsed,
            normalizedAvailable,
            normalizedTotal,
            MetricNormalizer.Percent(100d * usedBytes / totalBytes));
    }

    private static long ParseMemoryKiB(string line)
    {
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 1 ||
            !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormatException("Memory counter is invalid.");
        }

        return parsed;
    }

    private static long ParseUptime(string text)
    {
        var firstLine = ReadBoundedLines(text).FirstOrDefault()
            ?? throw new FormatException("Uptime is missing.");
        var value = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds < 0 ||
            seconds > MetricNormalizer.MaximumSafeJsonInteger)
        {
            throw new FormatException("Uptime is invalid.");
        }

        return checked((long)Math.Floor(seconds));
    }

    private static NetworkCounters ParseNetworkCounters(string text)
    {
        ulong receive = 0;
        ulong transmit = 0;
        var interfaces = 0;

        foreach (var line in ReadBoundedLines(text))
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var interfaceName = line[..separator].Trim();
            if (interfaceName.Equals("lo", StringComparison.Ordinal))
            {
                continue;
            }

            interfaces++;
            if (interfaces > MaximumInterfaces)
            {
                throw new InvalidDataException("Network interface count exceeds its limit.");
            }

            var fields = line[(separator + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 ||
                !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var interfaceReceive) ||
                !ulong.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var interfaceTransmit))
            {
                throw new FormatException("Network counters are invalid.");
            }

            checked
            {
                receive += interfaceReceive;
                transmit += interfaceTransmit;
            }
        }

        return new NetworkCounters(receive, transmit);
    }

    private static double? CalculateCpuUtilization(
        CpuCounters? previous,
        CpuCounters current)
    {
        if (previous is null ||
            current.Total < previous.Value.Total ||
            current.Idle < previous.Value.Idle)
        {
            return null;
        }

        var totalDelta = current.Total - previous.Value.Total;
        var idleDelta = current.Idle - previous.Value.Idle;
        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return null;
        }

        return MetricNormalizer.Percent(100d * (totalDelta - idleDelta) / totalDelta);
    }

    private static NetworkMetrics? CalculateNetwork(
        TimedNetworkCounters? previous,
        NetworkCounters? current,
        DateTimeOffset sampledAt)
    {
        if (current is null)
        {
            return null;
        }

        if (previous is null)
        {
            return new NetworkMetrics(null, null);
        }

        var elapsedSeconds = (sampledAt - previous.Value.SampledAt).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return new NetworkMetrics(null, null);
        }

        var receive = current.Value.Receive < previous.Value.Counters.Receive
            ? 0
            : ToRate(current.Value.Receive - previous.Value.Counters.Receive, elapsedSeconds);
        var transmit = current.Value.Transmit < previous.Value.Counters.Transmit
            ? 0
            : ToRate(current.Value.Transmit - previous.Value.Counters.Transmit, elapsedSeconds);
        return new NetworkMetrics(receive, transmit);
    }

    private static long? ToRate(ulong delta, double elapsedSeconds)
    {
        var rate = Math.Floor(delta / elapsedSeconds);
        return rate > MetricNormalizer.MaximumSafeJsonInteger
            ? null
            : MetricNormalizer.NonNegative(checked((long)rate));
    }

    private static IReadOnlyList<string> ReadBoundedLines(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaximumLines)
        {
            throw new InvalidDataException("Hardware metrics line count exceeds its limit.");
        }

        return lines;
    }

    private readonly record struct CpuCounters(ulong Total, ulong Idle);
    private readonly record struct NetworkCounters(ulong Receive, ulong Transmit);
    private readonly record struct TimedNetworkCounters(
        NetworkCounters Counters,
        DateTimeOffset SampledAt);
}
