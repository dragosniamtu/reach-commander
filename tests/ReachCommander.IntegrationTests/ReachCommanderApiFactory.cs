using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ReachCommander.IntegrationTests;

public sealed class ReachCommanderApiFactory : WebApplicationFactory<Program>
{
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
    }

    public string WorkspaceRoot { get; }

    public string MediaRoot { get; }

    public string DownloadsRoot { get; }

    public string MissingUsbRoot { get; }

    public string ConfigurationPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReachCommander:SourcesPath"] = ConfigurationPath,
            });
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
}
