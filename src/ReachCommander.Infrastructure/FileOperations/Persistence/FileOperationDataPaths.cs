namespace ReachCommander.Infrastructure.FileOperations.Persistence;

internal sealed record FileOperationDataPaths(
    string Root,
    string PlansDirectory,
    string OperationsDirectory)
{
    internal static FileOperationDataPaths FromAuthenticationRoot(string authenticationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationRoot);
        var root = Path.Combine(Path.GetFullPath(authenticationRoot), "file-operations");
        return new(
            root,
            Path.Combine(root, "plans"),
            Path.Combine(root, "operations"));
    }

    internal string PlanPath(Guid planId) =>
        Path.Combine(PlansDirectory, $"{planId:N}.json");

    internal string OperationPath(Guid operationId) =>
        Path.Combine(OperationsDirectory, $"{operationId:N}.json");

    internal void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PlansDirectory);
        Directory.CreateDirectory(OperationsDirectory);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(Root, mode);
            File.SetUnixFileMode(PlansDirectory, mode);
            File.SetUnixFileMode(OperationsDirectory, mode);
        }
    }
}
