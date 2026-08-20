using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;

namespace ReachCommander.IntegrationTests;

public sealed class ReachCommanderApiFactory : WebApplicationFactory<Program>
{
    private readonly TestHardwareMetricsSnapshotProvider _hardwareMetrics = new();
    private readonly ManualTimeProvider _clock = new(
        new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

    public ReachCommanderApiFactory()
    {
        WorkspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-api-tests-{Guid.NewGuid():N}");
        MediaRoot = Path.Combine(WorkspaceRoot, "media");
        DownloadsRoot = Path.Combine(WorkspaceRoot, "downloads");
        ArchiveRoot = Path.Combine(WorkspaceRoot, "archive");
        MissingUsbRoot = Path.Combine(WorkspaceRoot, "usb-missing");

        Directory.CreateDirectory(Path.Combine(MediaRoot, "Movies"));
        Directory.CreateDirectory(Path.Combine(DownloadsRoot, "Complete"));
        Directory.CreateDirectory(ArchiveRoot);
        File.WriteAllText(Path.Combine(MediaRoot, "Movies", "Gladiator II.mkv"), "video-data");
        foreach (var archiveName in new[]
        {
            "sample.zip",
            "invalid.zip",
            "unsupported.zip",
            "encrypted.zip",
            "unsafe.zip",
            "limit.zip",
            "episodes.part02.rar",
            "missing.part01.rar",
            "missing.part03.rar",
        })
        {
            File.WriteAllText(Path.Combine(DownloadsRoot, archiveName), "archive-test-data");
        }

        var configurationPath = Path.Combine(WorkspaceRoot, "sources.json");
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(new
        {
            sources = new object[]
            {
                new
                {
                    id = "downloads",
                    name = "Downloads",
                    path = DownloadsRoot,
                    enabled = true,
                    readOnly = false,
                    defaultLeft = true,
                },
                new
                {
                    id = "media",
                    name = "Media",
                    path = MediaRoot,
                    enabled = true,
                    readOnly = false,
                    defaultRight = true,
                },
                new
                {
                    id = "archive",
                    name = "Archive",
                    path = ArchiveRoot,
                    enabled = true,
                    readOnly = true,
                },
                new
                {
                    id = "usb",
                    name = "USB",
                    path = MissingUsbRoot,
                    enabled = true,
                    readOnly = true,
                },
            },
        }));

        ConfigurationPath = configurationPath;
        WebRoot = Path.Combine(WorkspaceRoot, "wwwroot");
        Directory.CreateDirectory(WebRoot);
        File.WriteAllText(
            Path.Combine(WebRoot, "index.html"),
            "<!doctype html><html><body>ReachCommander test shell</body></html>");
    }

    public string WorkspaceRoot { get; }

    public string MediaRoot { get; }

    public string DownloadsRoot { get; }

    public string ArchiveRoot { get; }

    public string MissingUsbRoot { get; }

    public string ConfigurationPath { get; }

    public string WebRoot { get; }

    public void SetHardwareSnapshot(HardwareMetricsSnapshot snapshot) =>
        _hardwareMetrics.Set(snapshot);

    public void SetHardwareNotReady() => _hardwareMetrics.SetNotReady();

    public void AdvanceTime(TimeSpan amount) => _clock.Advance(amount);

    public void ResetTime() => _clock.Reset();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(WebRoot);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReachCommander:SourcesPath"] = ConfigurationPath,
                ["HardwareMetrics:Enabled"] = "false",
                ["Uploads:MaxFileBytes"] = "8",
                ["Uploads:MaxBatchBytes"] = "12",
                ["Uploads:MaxFilesPerBatch"] = "2",
                ["Uploads:MaxConcurrentBatches"] = "2",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHardwareMetricsSnapshotProvider>();
            services.AddSingleton<IHardwareMetricsSnapshotProvider>(_hardwareMetrics);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
            services.RemoveAll<IArchiveWorkerClient>();
            services.AddSingleton<IArchiveWorkerClient, TestArchiveWorkerClient>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            Directory.Delete(WorkspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly DateTimeOffset _initial;
        private DateTimeOffset _current;

        public ManualTimeProvider(DateTimeOffset initial)
        {
            _initial = initial;
            _current = initial;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _current;
            }
        }

        public void Advance(TimeSpan amount)
        {
            lock (_gate)
            {
                _current += amount;
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _current = _initial;
            }
        }
    }

    private sealed class TestHardwareMetricsSnapshotProvider : IHardwareMetricsSnapshotProvider
    {
        private readonly object _gate = new();
        private HardwareMetricsSnapshot? _snapshot;

        public HardwareMetricsSnapshot GetCurrent()
        {
            lock (_gate)
            {
                return _snapshot ?? throw new HardwareMetricsNotReadyException();
            }
        }

        public void Set(HardwareMetricsSnapshot snapshot)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
            }
        }

        public void SetNotReady()
        {
            lock (_gate)
            {
                _snapshot = null;
            }
        }
    }

    private sealed class TestArchiveWorkerClient : IArchiveWorkerClient
    {
        public ValueTask<ArchiveWorkerInspection> InspectAsync(
            ResolvedArchivePartSet partSet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(partSet.PrimaryLogicalPath);
            return name switch
            {
                "invalid.zip" => throw new ArchiveInvalidException(),
                "unsupported.zip" => throw new ArchiveUnsupportedException(),
                "encrypted.zip" => throw new ArchiveEncryptedException(),
                "limit.zip" => throw new ArchiveLimitExceededException(
                    "The archive exceeds a configured inspection limit."),
                "unsafe.zip" => new(new ArchiveWorkerInspection(
                    ArchiveFormat.Zip,
                    false,
                    [Entry(0, "../escape.txt")])),
                _ => new(new ArchiveWorkerInspection(
                    partSet.Format,
                    false,
                    [
                        Entry(0, "Family/one.txt"),
                        Entry(1, "Family/Child/two.txt"),
                        Entry(2, "root.txt"),
                    ])),
            };
        }

        public ValueTask ExtractAsync(
            ResolvedArchivePartSet partSet,
            IReadOnlyList<int> entryIndexes,
            IArchiveEntrySink sink,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static UntrustedArchiveEntry Entry(int index, string key) => new(
            index,
            key,
            false,
            false,
            false,
            false,
            1,
            1,
            DateTimeOffset.Parse("2026-08-20T08:00:00Z"));
    }
}
