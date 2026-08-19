namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface ITrustedPathResolver
{
    string GetCanonicalPath(string path);
    bool IsWithinRoot(string root, string candidate);
}

internal sealed class TrustedPathResolver(IHostPlatform platform) : ITrustedPathResolver
{
    public string GetCanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        FileSystemInfo entry = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);
        var target = entry.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? fullPath);
    }

    public bool IsWithinRoot(string root, string candidate) =>
        IsCanonicalPathWithinRoot(
            GetCanonicalPath(root),
            GetCanonicalPath(candidate),
            platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool IsCanonicalPathWithinRoot(
        string canonicalRoot,
        string canonicalCandidate,
        StringComparison comparison)
    {
        var relative = Path.GetRelativePath(canonicalRoot, canonicalCandidate);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", comparison) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", comparison);
    }
}
