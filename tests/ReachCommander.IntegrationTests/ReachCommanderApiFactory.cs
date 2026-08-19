using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.IntegrationTests;

public sealed class ReachCommanderApiFactory : WebApplicationFactory<Program>
{
    private readonly TestHardwareMetricsSnapshotProvider _hardwareMetrics = new();

    public ReachCommanderApiFactory()
    {
        WorkspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-api-tests-{Guid.NewGuid():N}");
        MediaRoot = Path.Combine(WorkspaceRoot, "media");
        DownloadsRoot = Path.Combine(WorkspaceRoot, "downloads");
        MissingUsbRoot = Path.Combine(WorkspaceRoot, "usb-missing");

        Directory.CreateDirectory(Path.Combine(MediaRoot, "Movies"));
        Directory.CreateDirectory(Path.Combine(DownloadsRoot, "Complete"));
        File.WriteAllText(Path.Combine(MediaRoot, "Movies", "Gladiator II.mkv"), "video-data");

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

    public string MissingUsbRoot { get; }

    public string ConfigurationPath { get; }

    public string WebRoot { get; }

    public void SetHardwareSnapshot(HardwareMetricsSnapshot snapshot) =>
        _hardwareMetrics.Set(snapshot);

    public void SetHardwareNotReady() => _hardwareMetrics.SetNotReady();

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
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHardwareMetricsSnapshotProvider>();
            services.AddSingleton<IHardwareMetricsSnapshotProvider>(_hardwareMetrics);
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
}
