using ReachCommander.Application.Authentication;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Authentication;

public sealed class AuthenticationDataPathsTests
{
    [Fact]
    public void Resolve_uses_explicit_absolute_path()
    {
        using var temporary = new TemporaryDirectory();

        var paths = AuthenticationDataPaths.Resolve(
            Path.Combine(temporary.Path, "configured"),
            isWindows: false,
            localApplicationData: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(temporary.Path, "configured")),
            paths.RootPath);
    }

    [Fact]
    public void Resolve_uses_local_application_data_on_windows()
    {
        var paths = AuthenticationDataPaths.Resolve(
            configuredRoot: null,
            isWindows: true,
            localApplicationData: "C:/Users/Test/AppData/Local");

        Assert.Equal(
            Path.GetFullPath("C:/Users/Test/AppData/Local/ReachCommander/data"),
            paths.RootPath);
    }

    [Fact]
    public void Resolve_uses_data_mount_on_linux()
    {
        var paths = AuthenticationDataPaths.Resolve(
            configuredRoot: null,
            isWindows: false,
            localApplicationData: null);

        Assert.Equal(Path.GetFullPath("/data"), paths.RootPath);
    }

    [Fact]
    public void Resolve_rejects_relative_override()
    {
        var exception = Assert.Throws<AuthenticationStateUnavailableException>(() =>
            AuthenticationDataPaths.Resolve(
                configuredRoot: "relative/auth-data",
                isWindows: false,
                localApplicationData: null));

        Assert.Equal("authentication_state_unavailable", exception.Code);
    }

    [Fact]
    public void Ensure_directories_creates_only_narrow_authentication_paths()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "state");
        var paths = AuthenticationDataPaths.ForRoot(root);

        paths.EnsureDirectories();

        Assert.True(Directory.Exists(paths.RootPath));
        Assert.True(Directory.Exists(paths.AuthDirectory));
        Assert.True(Directory.Exists(paths.KeysDirectory));
        Assert.False(File.Exists(paths.AccountPath));
        Assert.False(File.Exists(paths.BootstrapPath));
    }
}
