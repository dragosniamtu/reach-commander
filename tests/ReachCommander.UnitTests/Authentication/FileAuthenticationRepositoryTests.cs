using ReachCommander.Application.Authentication;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Authentication;

public sealed class FileAuthenticationRepositoryTests
{
    [Fact]
    public async Task Missing_account_is_distinct_from_malformed_account()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        var repository = new FileAuthenticationRepository(paths);

        Assert.Null(await repository.ReadAccountAsync(CancellationToken.None));

        File.WriteAllText(paths.AccountPath, "{not-json");
        var exception = await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(
            () => repository.ReadAccountAsync(CancellationToken.None).AsTask());

        Assert.Equal("authentication_state_unavailable", exception.Code);
        Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_document_fails_closed_before_deserialization()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        await File.WriteAllBytesAsync(paths.AccountPath, new byte[65_537]);
        var repository = new FileAuthenticationRepository(paths);

        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(
            () => repository.ReadAccountAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Unsupported_document_version_fails_closed()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.AccountPath,
            """
            {
              "version": 2,
              "username": "admin",
              "normalizedUsername": "ADMIN",
              "passwordHash": "hash",
              "securityStamp": "stamp"
            }
            """);
        var repository = new FileAuthenticationRepository(paths);

        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(
            () => repository.ReadAccountAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Create_account_is_exclusive_and_round_trips_document()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        var repository = new FileAuthenticationRepository(paths);
        var account = Account("first-stamp");

        await repository.CreateAccountAsync(account, CancellationToken.None);

        Assert.Equal(account, await repository.ReadAccountAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AdministratorAlreadyExistsException>(() =>
            repository.CreateAccountAsync(Account("second-stamp"), CancellationToken.None));
        Assert.Equal(account, await repository.ReadAccountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_replacement_preserves_previous_document()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        var repository = new FileAuthenticationRepository(paths);
        var original = Account("original-stamp");
        await repository.CreateAccountAsync(original, CancellationToken.None);
        var failing = new FileAuthenticationRepository(paths, new FailingAtomicWriter());

        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(() =>
            failing.ReplaceAccountAsync(Account("replacement-stamp"), CancellationToken.None));

        Assert.Equal(original, await repository.ReadAccountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_can_be_replaced_read_and_deleted()
    {
        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
        paths.EnsureDirectories();
        var repository = new FileAuthenticationRepository(paths);
        var first = new BootstrapDocument(1, Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow);
        var second = first with
        {
            Verifier = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
        };

        await repository.ReplaceBootstrapAsync(first, CancellationToken.None);
        await repository.ReplaceBootstrapAsync(second, CancellationToken.None);

        Assert.Equal(second, await repository.ReadBootstrapAsync(CancellationToken.None));
        await repository.DeleteBootstrapAsync(CancellationToken.None);
        Assert.Null(await repository.ReadBootstrapAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Authentication_files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var paths = AuthenticationDataPaths.ForRoot(Path.Combine(temporary.Path, "data"));
        paths.EnsureDirectories();
        var repository = new FileAuthenticationRepository(paths);

        await repository.CreateAccountAsync(Account("stamp"), CancellationToken.None);

        const UnixFileMode directoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(directoryMode, File.GetUnixFileMode(paths.RootPath));
        Assert.Equal(directoryMode, File.GetUnixFileMode(paths.AuthDirectory));
        Assert.Equal(directoryMode, File.GetUnixFileMode(paths.KeysDirectory));
        Assert.Equal(fileMode, File.GetUnixFileMode(paths.AccountPath));
        Assert.Equal(fileMode, File.GetUnixFileMode(paths.LockPath));
    }

    private static AdministratorAccountDocument Account(string stamp) => new(
        Version: 1,
        Username: "admin",
        NormalizedUsername: "ADMIN",
        PasswordHash: "AQAAAA-test-hash",
        SecurityStamp: stamp);

    private sealed class FailingAtomicWriter : IAtomicAuthenticationFileWriter
    {
        public Task CreateAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new IOException("Injected create failure.");

        public Task ReplaceAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new IOException("Injected replacement failure.");
    }
}
