using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed class TextEncodingPlanner(
    IPathSecurityService pathSecurity,
    ITextEncodingFileSystem fileSystem,
    TextEncodingPlanStore planStore,
    TimeProvider clock)
{
    private static readonly HashSet<string> SupportedExtensions = new(
        [".srt", ".sub", ".txt", ".csv", ".nfo", ".md", ".json"],
        StringComparer.OrdinalIgnoreCase);

    public async ValueTask<TextEncodingPreview> PreviewAsync(
        TextEncodingPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var rows = new List<TextEncodingPreviewRow>(request.FilePaths.Count);
        var entries = new List<StoredTextEncodingEntry>(request.FilePaths.Count);
        var normalizedPaths = new HashSet<string>(StringComparer.Ordinal);
        string? commonDirectory = null;

        foreach (var requestedPath in request.FilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = await pathSecurity.ResolveAsync(
                request.SourceId,
                requestedPath,
                cancellationToken);
            if (resolved.Source.IsReadOnly)
            {
                throw new OperationSourceReadOnlyException();
            }

            if (!normalizedPaths.Add(resolved.LogicalPath))
            {
                throw TextEncodingException.InvalidRequest();
            }

            var logicalDirectory = GetLogicalDirectory(resolved.LogicalPath);
            commonDirectory ??= logicalDirectory;
            if (!logicalDirectory.Equals(commonDirectory, StringComparison.Ordinal))
            {
                throw TextEncodingException.InvalidRequest();
            }

            var fileName = GetLogicalFileName(resolved.LogicalPath);
            if (!SupportedExtensions.Contains(Path.GetExtension(fileName)))
            {
                rows.Add(InvalidRow(
                    resolved.LogicalPath,
                    fileName,
                    TextEncodingException.UnsupportedExtension()));
                continue;
            }

            try
            {
                var snapshot = await fileSystem.ReadSnapshotAsync(
                    resolved.LogicalPath,
                    resolved.PhysicalPath,
                    PathTraversedSymbolicLink(resolved),
                    cancellationToken);
                if (snapshot.IsSymbolicLink)
                {
                    throw TextEncodingException.SymbolicLinkRejected();
                }

                var analysis = TextEncodingCodec.Analyze(
                    snapshot.Bytes,
                    request.SourceEncoding,
                    request.OutputEncoding);
                var status = analysis.RequiresReview
                    ? TextEncodingPreviewStatus.Warning
                    : TextEncodingPreviewStatus.Ready;
                var code = analysis.RequiresReview
                    ? "legacy_encoding_review_required"
                    : null;
                var detail = analysis.RequiresReview
                    ? "The legacy encoding is ambiguous. Verify the preview before converting."
                    : null;

                rows.Add(new TextEncodingPreviewRow(
                    snapshot.LogicalPath,
                    snapshot.Name,
                    analysis.SourceEncoding,
                    analysis.Confidence,
                    status,
                    code,
                    detail,
                    analysis.PreviewText));
                entries.Add(new StoredTextEncodingEntry(
                    snapshot.LogicalPath,
                    snapshot.PhysicalPath,
                    snapshot.LogicalDirectory,
                    snapshot.PhysicalDirectory,
                    snapshot.Name,
                    snapshot.Fingerprint,
                    analysis.SourceEncoding,
                    request.OutputEncoding,
                    status));
            }
            catch (TextEncodingException exception)
            {
                rows.Add(InvalidRow(resolved.LogicalPath, fileName, exception));
            }
            catch (IOException)
            {
                rows.Add(InvalidRow(
                    resolved.LogicalPath,
                    fileName,
                    TextEncodingException.FileNotRegular()));
            }
            catch (UnauthorizedAccessException)
            {
                rows.Add(InvalidRow(
                    resolved.LogicalPath,
                    fileName,
                    TextEncodingException.FileNotRegular()));
            }
        }

        var now = clock.GetUtcNow();
        var planId = Guid.NewGuid();
        var readyCount = rows.Count(row => row.Status == TextEncodingPreviewStatus.Ready);
        var warningCount = rows.Count(row => row.Status == TextEncodingPreviewStatus.Warning);
        var invalidCount = rows.Count(row => row.Status == TextEncodingPreviewStatus.Invalid);
        var preview = new TextEncodingPreview(
            planId,
            now.AddMinutes(10),
            rows,
            readyCount,
            warningCount,
            invalidCount,
            CanExecute: entries.Count > 0);
        planStore.Add(new StoredTextEncodingPlan(
            planId,
            now,
            preview.ExpiresAt,
            request.SourceId,
            entries,
            preview,
            BoundOperationId: null));
        return preview;
    }

    private static void ValidateRequest(TextEncodingPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceId) ||
            request.FilePaths is null ||
            request.FilePaths.Count is < 1 or > 100 ||
            request.FilePaths.Any(string.IsNullOrWhiteSpace) ||
            !Enum.IsDefined(request.SourceEncoding) ||
            !Enum.IsDefined(request.OutputEncoding) ||
            request.OutputEncoding is TextEncodingKind.Auto or TextEncodingKind.Utf16BigEndian)
        {
            throw TextEncodingException.InvalidRequest();
        }
    }

    private static TextEncodingPreviewRow InvalidRow(
        string logicalPath,
        string fileName,
        TextEncodingException exception) => new(
            logicalPath,
            fileName,
            DetectedSourceEncoding: null,
            Confidence: null,
            TextEncodingPreviewStatus.Invalid,
            exception.Code,
            exception.PublicDetail,
            PreviewText: string.Empty);

    private static string GetLogicalDirectory(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }

    private static string GetLogicalFileName(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return logicalPath[(separator + 1)..];
    }

    private static bool PathTraversedSymbolicLink(ResolvedSourcePath resolved)
    {
        var relative = resolved.LogicalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var lexicalPath = Path.GetFullPath(Path.Combine(resolved.Source.RootPath, relative));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return !lexicalPath.Equals(Path.GetFullPath(resolved.PhysicalPath), comparison);
    }
}
