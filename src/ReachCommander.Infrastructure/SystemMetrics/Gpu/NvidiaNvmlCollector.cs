using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal sealed class NvidiaNvmlCollector(
    IOptions<HardwareMetricsOptions> options,
    INvidiaNvmlApi nvmlApi,
    IHostPlatform platform) : IHardwareMetricsCollector
{
    public string Name => "nvidia-nvml";
    public bool IsSupported => platform.IsLinux && options.Value.GpusEnabled;

    public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return ValueTask.FromResult(HardwareMetricsContribution.Unsupported(Name));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var samples = nvmlApi.TryReadDevices();
            if (samples is null || samples.Count == 0)
            {
                return ValueTask.FromResult(HardwareMetricsContribution.Unsupported(Name));
            }

            var gpus = samples
                .Take(MetricNormalizer.MaximumGpuCount)
                .Select(MapGpu)
                .ToArray();
            return ValueTask.FromResult(new HardwareMetricsContribution(
                new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
                Gpus: Array.AsReadOnly(gpus)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return ValueTask.FromResult(new HardwareMetricsContribution(
                new HardwareCollectorStatus(
                    Name,
                    HardwareCollectorState.Unavailable,
                    "nvidia_metrics_unavailable")));
        }
    }

    private static GpuMetrics MapGpu(NvidiaDeviceSample sample, int index)
    {
        var used = MetricNormalizer.NonNegative(sample.MemoryUsedBytes);
        var total = MetricNormalizer.NonNegative(sample.MemoryTotalBytes);
        if (used is not null && total is not null && used > total)
        {
            used = null;
        }

        return new GpuMetrics(
            $"gpu-nvidia-{index + 1:D3}",
            "NVIDIA",
            MetricNormalizer.Label(sample.Name, $"NVIDIA GPU {index + 1}"),
            MetricNormalizer.Percent(sample.UtilizationPercent),
            used,
            total,
            MetricNormalizer.Celsius(sample.TemperatureCelsius),
            null,
            null,
            false,
            false);
    }
}
