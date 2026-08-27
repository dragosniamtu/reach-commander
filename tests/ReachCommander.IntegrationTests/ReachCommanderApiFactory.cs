using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReachCommander.Application.Authentication;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Archives;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;

namespace ReachCommander.IntegrationTests;

public sealed class ReachCommanderApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _useRealSecurity;
    private readonly string _environmentName;
    private readonly IReadOnlyDictionary<string, string?> _configurationOverrides;
    private readonly TestLogCollector _logs = new();
    private readonly TestHardwareMetricsSnapshotProvider _hardwareMetrics = new();
    private readonly TestArchiveWorkerClient _archiveWorker = new();
    private readonly TestSystemUpdateService _systemUpdates = new();
    private readonly TestSystemUpdateSupportBundleService _systemUpdateSupportBundle = new();
    private readonly ManualTimeProvider _clock = new(
        new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

    public ReachCommanderApiFactory()
        : this(useRealSecurity: false, authenticationDataPath: null)
    {
    }

    internal ReachCommanderApiFactory(
        bool useRealSecurity,
        string? authenticationDataPath = null,
        string environmentName = "Testing",
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        _useRealSecurity = useRealSecurity;
        _environmentName = environmentName;
        _configurationOverrides = configurationOverrides ??
            new Dictionary<string, string?>();
        WorkspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-api-tests-{Guid.NewGuid():N}");
        MediaRoot = Path.Combine(WorkspaceRoot, "media");
        DownloadsRoot = Path.Combine(WorkspaceRoot, "downloads");
        ArchiveRoot = Path.Combine(WorkspaceRoot, "archive");
        MissingUsbRoot = Path.Combine(WorkspaceRoot, "usb-missing");
        AuthenticationDataPath = authenticationDataPath ??
            Path.Combine(WorkspaceRoot, "authentication-data");

        Directory.CreateDirectory(Path.Combine(MediaRoot, "Movies"));
        Directory.CreateDirectory(Path.Combine(MediaRoot, "Photos"));
        Directory.CreateDirectory(Path.Combine(DownloadsRoot, "Complete"));
        Directory.CreateDirectory(Path.Combine(DownloadsRoot, "backups"));
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
        File.WriteAllText(
            Path.Combine(DownloadsRoot, "backups", "photos.7z"),
            "archive-test-data");

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

    public string AuthenticationDataPath { get; }

    public string ConfigurationPath { get; }

    public string WebRoot { get; }

    public IReadOnlyList<string> LogMessages => _logs.Messages;

    internal TestSystemUpdateService SystemUpdates => _systemUpdates;

    public HttpClient CreateCookieClient() => CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public async Task<string> GetFreshSetupCodeAsync()
    {
        var code = await Services
            .GetRequiredService<IAdministratorAccountService>()
            .PrepareSetupAsync(CancellationToken.None);
        return code ?? throw new InvalidOperationException(
            "A setup code cannot be issued after the administrator account exists.");
    }

    public void SetHardwareSnapshot(HardwareMetricsSnapshot snapshot) =>
        _hardwareMetrics.Set(snapshot);

    public void SetHardwareNotReady() => _hardwareMetrics.SetNotReady();

    public void AdvanceTime(TimeSpan amount) => _clock.Advance(amount);

    public void ResetTime() => _clock.Reset();

    public int ArchiveExtractionCount => _archiveWorker.ExtractionCount;

    public int ArchiveInspectionCount => _archiveWorker.InspectionCount;

    public void BlockArchiveExtraction() => _archiveWorker.BlockExtraction();

    public void ReleaseArchiveExtraction() => _archiveWorker.ReleaseExtraction();

    public void ResetArchiveWorker() => _archiveWorker.Reset();

    public async Task<bool> BeginSystemUpdateDrainAsync() =>
        await Services.GetRequiredService<ISystemMutationGate>()
            .BeginDrainAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

    public void CancelSystemUpdateDrain() =>
        Services.GetRequiredService<ISystemMutationGate>().CancelDrain();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environmentName);
        builder.UseWebRoot(WebRoot);
        builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ReachCommander:SourcesPath"] = ConfigurationPath,
                ["Authentication:DataPath"] = AuthenticationDataPath,
                ["HardwareMetrics:Enabled"] = "false",
                ["Uploads:MaxFileBytes"] = "8",
                ["Uploads:MaxBatchBytes"] = "12",
                ["Uploads:MaxFilesPerBatch"] = "2",
                ["Uploads:MaxConcurrentBatches"] = "2",
            };
            foreach (var (key, value) in _configurationOverrides)
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHardwareMetricsSnapshotProvider>();
            services.AddSingleton<IHardwareMetricsSnapshotProvider>(_hardwareMetrics);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
            services.RemoveAll<IArchiveWorkerClient>();
            services.AddSingleton<IArchiveWorkerClient>(_archiveWorker);
            services.RemoveAll<ISystemUpdateService>();
            services.AddSingleton<ISystemUpdateService>(_systemUpdates);
            services.RemoveAll<ISystemUpdateSupportBundleService>();
            services.AddSingleton<ISystemUpdateSupportBundleService>(_systemUpdateSupportBundle);
            if (!_useRealSecurity)
            {
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
                services.RemoveAll<IAntiforgery>();
                services.AddSingleton<IAntiforgery, TestAntiforgery>();
            }
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
        private readonly object _gate = new();
        private TaskCompletionSource? _extractionGate;
        private int _extractionCount;
        private int _inspectionCount;

        public int ExtractionCount => Volatile.Read(ref _extractionCount);

        public int InspectionCount => Volatile.Read(ref _inspectionCount);

        public void BlockExtraction()
        {
            lock (_gate)
            {
                _extractionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseExtraction()
        {
            lock (_gate)
            {
                _extractionGate?.TrySetResult();
            }
        }

        public void Reset()
        {
            ReleaseExtraction();
            lock (_gate)
            {
                _extractionGate = null;
            }

            Interlocked.Exchange(ref _extractionCount, 0);
            Interlocked.Exchange(ref _inspectionCount, 0);
        }

        public ValueTask<ArchiveWorkerInspection> InspectAsync(
            ResolvedArchivePartSet partSet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _inspectionCount);
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
                "photos.7z" => new(new ArchiveWorkerInspection(
                    ArchiveFormat.SevenZip,
                    false,
                    [Entry(0, "Family/2025/photo.jpg")])),
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

        public async ValueTask ExtractAsync(
            ResolvedArchivePartSet partSet,
            IReadOnlyList<int> entryIndexes,
            IArchiveEntrySink sink,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _extractionCount);
            Task? wait;
            lock (_gate)
            {
                wait = _extractionGate?.Task;
            }

            if (wait is not null)
            {
                await wait.WaitAsync(cancellationToken);
            }

            var completed = 0;
            long bytes = 0;
            foreach (var entryIndex in entryIndexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sink.StartAsync(entryIndex, cancellationToken);
                await sink.WriteAsync(new byte[] { 1 }, cancellationToken);
                await sink.EndAsync(entryIndex, 1, cancellationToken);
                completed++;
                bytes++;
                await sink.ProgressAsync(completed, bytes, cancellationToken);
            }
        }

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

    internal sealed class TestSystemUpdateService : ISystemUpdateService
    {
        private static readonly DateTimeOffset Now =
            DateTimeOffset.Parse("2026-08-25T10:00:00Z");
        private SystemUpdateStatus _status = SystemUpdateStatusFactory.Unavailable(Now);
        private bool _backgroundOperationsActive;
        private bool _checkRateLimited;
        private int _applyCount;

        public int ApplyCount => Volatile.Read(ref _applyCount);

        public void SetAvailable() => _status = SystemUpdateStatusFactory.Available(
            "stable", "v1.3.0", "v1.4.0", Now, Now);

        public void SetApplying(
            SystemUpdateProgressStage progressStage,
            SystemUpdateTrace? trace = null) =>
            _status = SystemUpdateStatusFactory.Applying(
                "stable",
                "v1.3.0",
                "v1.4.0",
                "operation-1",
                Now,
                Now,
                progressStage,
                trace) with
            {
                ProtocolVersion = trace is null ? 2 : 3,
            };

        public void SetBackgroundOperationsActive(bool value) =>
            _backgroundOperationsActive = value;

        public void SetCheckRateLimited() => _checkRateLimited = true;

        public void SetRolledBack() => _status = SystemUpdateStatusFactory.RolledBack(
            "stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now);

        public Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_status);

        public Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken) =>
            _checkRateLimited
                ? throw new SystemUpdateCheckRateLimitedException()
                : Task.FromResult(_status);

        public Task<SystemUpdateStatus> ApplyAsync(CancellationToken cancellationToken)
        {
            if (_backgroundOperationsActive)
            {
                throw new SystemUpdateBlockedByOperationsException();
            }

            Interlocked.Increment(ref _applyCount);
            _status = SystemUpdateStatusFactory.Applying(
                "stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now);
            return Task.FromResult(_status);
        }
    }

    private sealed class TestSystemUpdateSupportBundleService : ISystemUpdateSupportBundleService
    {
        public Task<SystemUpdateSupportBundle> CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[]
                {
                    "README.txt",
                    "deployment-health.json",
                    "manifest.json",
                    "summary.txt",
                    "update-trace.json",
                })
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(Encoding.UTF8.GetBytes("sanitized\n"));
                }
            }

            return Task.FromResult(new SystemUpdateSupportBundle(
                "reachcommander-support-20260827T120000Z.zip",
                output.ToArray()));
        }
    }
}
