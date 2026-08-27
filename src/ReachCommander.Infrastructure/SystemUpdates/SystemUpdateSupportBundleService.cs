using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal sealed class SystemUpdateSupportBundleService(
    ISystemUpdateDiagnosticsGateway gateway,
    TimeProvider timeProvider) : ISystemUpdateSupportBundleService
{
    internal const int MaximumUncompressedBytes = 1_048_576;
    private static readonly string[] EntryNames =
    [
        "README.txt",
        "deployment-health.json",
        "manifest.json",
        "summary.txt",
        "update-trace.json",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<SystemUpdateSupportBundle> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SystemUpdateDiagnosticsSnapshot snapshot;
        var helperUnavailable = false;
        try
        {
            snapshot = await gateway.CollectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SystemUpdaterUnavailableException)
        {
            helperUnavailable = true;
            snapshot = PartialSnapshot();
        }
        catch (SystemUpdaterProtocolException)
        {
            helperUnavailable = true;
            snapshot = PartialSnapshot();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var content = BuildArchive(snapshot, helperUnavailable);
        var stamp = timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        return new SystemUpdateSupportBundle($"reachcommander-support-{stamp}.zip", content);
    }

    private SystemUpdateDiagnosticsSnapshot PartialSnapshot()
    {
        var now = timeProvider.GetUtcNow();
        return new SystemUpdateDiagnosticsSnapshot(
            1,
            now,
            false,
            null,
            null,
            null,
            null,
            null,
            SystemUpdateDiagnostics.CheckNames.Select(name =>
                new SystemUpdateDiagnosticCheck(
                    name,
                    SystemUpdateDiagnosticStatus.Unavailable,
                    SystemUpdateDiagnostics.ReasonCode(
                        name,
                        SystemUpdateDiagnosticStatus.Unavailable)))
                .ToArray());
    }

    private static byte[] BuildArchive(
        SystemUpdateDiagnosticsSnapshot snapshot,
        bool helperUnavailable)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Json(new
            {
                bundleSchemaVersion = 1,
                generatedAt = snapshot.GeneratedAt,
                hostSnapshotComplete = snapshot.Complete,
                updaterProtocolVersion = snapshot.UpdaterProtocolVersion,
                snapshot.Channel,
                snapshot.CurrentVersion,
                snapshot.OperationId,
            }),
            ["update-trace.json"] = Json(new
            {
                available = snapshot.Trace is not null,
                trace = snapshot.Trace,
            }),
            ["deployment-health.json"] = Json(new
            {
                schemaVersion = snapshot.SchemaVersion,
                snapshot.Checks,
            }),
            ["summary.txt"] = Utf8(Summary(snapshot, helperUnavailable)),
            ["README.txt"] = Utf8(Readme),
        };
        if (!EntryNames.Order().SequenceEqual(entries.Keys.Order(), StringComparer.Ordinal) ||
            entries.Values.Sum(value => value.LongLength) > MaximumUncompressedBytes)
        {
            throw new InvalidOperationException("The support bundle contract is invalid.");
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in EntryNames)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(entries[name]);
            }
        }

        return output.ToArray();
    }

    private static byte[] Json(object value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string Summary(
        SystemUpdateDiagnosticsSnapshot snapshot,
        bool helperUnavailable)
    {
        var failed = snapshot.Checks.Count(check =>
            check.Status == SystemUpdateDiagnosticStatus.Failed);
        var delayed = snapshot.Checks.Count(check => check.Status is
            SystemUpdateDiagnosticStatus.TimedOut or SystemUpdateDiagnosticStatus.Unavailable);
        var guidance = helperUnavailable
            ? "The installed updater helper could not provide its sanitized snapshot.\n" +
              "To restore full diagnostics, refresh the checksum-verified Ubuntu installer.\n\n"
            : string.Empty;
        return
            "ReachCommander sanitized update diagnostics\n" +
            $"Snapshot: {(snapshot.Complete ? "complete" : "partial")}\n" +
            $"Failed checks: {failed}\n" +
            $"Timed out or unavailable checks: {delayed}\n\n" +
            guidance +
            "Safe next commands:\n" +
            "  sudo reachcommander update-log\n" +
            "  sudo reachcommander doctor\n" +
            "  sudo reachcommander support-bundle > reachcommander-support.zip\n" +
            "No data was uploaded automatically.\n";
    }

    private const string Readme =
        "ReachCommander support bundle\n\n" +
        "This archive contains allowlisted update stages and deployment-health status codes.\n" +
        "It intentionally excludes raw logs, credentials, tokens, paths, filenames, addresses,\n" +
        "hostnames, environment values, image digests, container identifiers, and file contents.\n" +
        "No data was uploaded automatically. Review the files before sharing the archive.\n";
}
