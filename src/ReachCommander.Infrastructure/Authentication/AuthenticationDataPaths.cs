using ReachCommander.Application.Authentication;

namespace ReachCommander.Infrastructure.Authentication;

public sealed record AuthenticationDataPaths
{
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private AuthenticationDataPaths(string rootPath)
    {
        RootPath = rootPath;
        AuthDirectory = Path.Combine(rootPath, "auth");
        AccountPath = Path.Combine(AuthDirectory, "account.json");
        BootstrapPath = Path.Combine(AuthDirectory, "bootstrap.json");
        LockPath = Path.Combine(AuthDirectory, "auth.lock");
        KeysDirectory = Path.Combine(rootPath, "keys");
    }

    public string RootPath { get; }

    public string AuthDirectory { get; }

    public string AccountPath { get; }

    public string BootstrapPath { get; }

    public string LockPath { get; }

    public string KeysDirectory { get; }

    public static AuthenticationDataPaths Resolve(
        string? configuredRoot,
        bool isWindows,
        string? localApplicationData)
    {
        string root;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredRoot.Trim());
            if (!Path.IsPathFullyQualified(expanded))
            {
                throw new AuthenticationStateUnavailableException(
                    "The configured authentication data path must be absolute.");
            }

            root = expanded;
        }
        else if (isWindows)
        {
            var localData = string.IsNullOrWhiteSpace(localApplicationData)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localApplicationData;
            if (string.IsNullOrWhiteSpace(localData))
            {
                throw new AuthenticationStateUnavailableException(
                    "The local application data directory is unavailable.");
            }

            root = Path.Combine(localData, "ReachCommander", "data");
        }
        else
        {
            root = "/data";
        }

        return ForRoot(root);
    }

    public static AuthenticationDataPaths ForRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return new AuthenticationDataPaths(Path.GetFullPath(rootPath));
    }

    public void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(AuthDirectory);
            Directory.CreateDirectory(KeysDirectory);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(RootPath, OwnerDirectoryMode);
                File.SetUnixFileMode(AuthDirectory, OwnerDirectoryMode);
                File.SetUnixFileMode(KeysDirectory, OwnerDirectoryMode);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AuthenticationStateUnavailableException(
                "Authentication storage could not be prepared.",
                exception);
        }
    }
}
