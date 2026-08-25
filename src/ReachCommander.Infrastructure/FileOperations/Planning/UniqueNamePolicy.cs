namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal static class UniqueNamePolicy
{
    internal static string Find(string requestedLogicalPath, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLogicalPath);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(requestedLogicalPath))
        {
            return requestedLogicalPath;
        }

        var separator = requestedLogicalPath.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : requestedLogicalPath[..separator];
        var name = requestedLogicalPath[(separator + 1)..];
        var extension = Path.GetExtension(name);
        var stem = extension.Length == name.Length ? name : name[..^extension.Length];
        if (extension.Length == name.Length)
        {
            extension = string.Empty;
        }
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidateName = $"{stem} ({suffix}){extension}";
            var candidate = parent == "/" ? $"/{candidateName}" : $"{parent}/{candidateName}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("A unique destination name could not be allocated.");
    }
}
