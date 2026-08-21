using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Authentication;
using ReachCommander.Infrastructure;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Authentication;

public sealed class AdministratorAccountServiceTests
{
    private const string Password = "a-long-test-password";
    private const string NewPassword = "a-different-test-password";

    [Fact]
    public async Task Setup_persists_only_hashes_and_authenticates_the_account()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var identity = await fixture.Service.CreateAsync(
            new(setupCode!, "dragos", Password),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(fixture.Paths.AccountPath);
        Assert.DoesNotContain(Password, json, StringComparison.Ordinal);
        Assert.DoesNotContain(setupCode!, json, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Paths.BootstrapPath));
        Assert.Equal(
            identity,
            await fixture.Service.AuthenticateAsync("DRAGOS", Password, CancellationToken.None));
    }

    [Fact]
    public async Task Every_startup_preparation_rotates_the_bootstrap_code()
    {
        using var fixture = AuthenticationFixture.Create();
        var first = await fixture.Service.PrepareSetupAsync(CancellationToken.None);
        var firstDocument = await File.ReadAllTextAsync(fixture.Paths.BootstrapPath);
        var second = await fixture.Service.PrepareSetupAsync(CancellationToken.None);
        var secondDocument = await File.ReadAllTextAsync(fixture.Paths.BootstrapPath);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(first!, firstDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(second!, secondDocument, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidSetupCodeException>(() =>
            fixture.Service.CreateAsync(new(first!, "dragos", Password), CancellationToken.None));

        await fixture.Service.CreateAsync(new(second!, "dragos", Password), CancellationToken.None);
        await Assert.ThrowsAsync<AdministratorAlreadyExistsException>(() =>
            fixture.Service.CreateAsync(new(second!, "dragos", Password), CancellationToken.None));
    }

    [Fact]
    public async Task Username_is_trimmed_nfkc_normalized_and_matched_invariantly()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var identity = await fixture.Service.CreateAsync(
            new(setupCode!, "  Dra\uff47os  ", Password),
            CancellationToken.None);

        Assert.Equal("Dragos", identity.Username);
        Assert.NotNull(await fixture.Service.AuthenticateAsync("drAGOS", Password, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a\u0000bc")]
    public async Task Invalid_usernames_are_rejected(string username)
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            fixture.Service.CreateAsync(new(setupCode!, username, Password), CancellationToken.None));

        Assert.Equal("invalid_username", exception.Code);
    }

    [Fact]
    public async Task Username_longer_than_sixty_four_normalized_characters_is_rejected()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            fixture.Service.CreateAsync(new(setupCode!, new string('a', 65), Password), CancellationToken.None));

        Assert.Equal("invalid_username", exception.Code);
    }

    [Theory]
    [InlineData("short-pass!")]
    public async Task Passwords_shorter_than_twelve_characters_are_rejected(string password)
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            fixture.Service.CreateAsync(new(setupCode!, "dragos", password), CancellationToken.None));

        Assert.Equal("invalid_password", exception.Code);
    }

    [Fact]
    public async Task Passwords_longer_than_one_hundred_twenty_eight_characters_are_rejected()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AuthenticationValidationException>(() =>
            fixture.Service.CreateAsync(
                new(setupCode!, "dragos", new string('p', 129)),
                CancellationToken.None));

        Assert.Equal("invalid_password", exception.Code);
    }

    [Fact]
    public async Task Exact_username_and_password_boundaries_are_accepted()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);
        var minimumPassword = new string('p', 12);

        await fixture.Service.CreateAsync(
            new(setupCode!, "abc", minimumPassword),
            CancellationToken.None);
        var identity = await fixture.Service.ChangePasswordAsync(
            new("ABC", minimumPassword, new string('q', 128)),
            CancellationToken.None);

        Assert.Equal("abc", identity.Username);
    }

    [Fact]
    public async Task Consumed_setup_code_cannot_create_a_replacement_account()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();
        var consumedCode = fixture.SetupCode!;
        File.Delete(fixture.Paths.AccountPath);

        await Assert.ThrowsAsync<InvalidSetupCodeException>(() =>
            fixture.Service.CreateAsync(
                new(consumedCode, "dragos", Password),
                CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_setup_yields_exactly_one_success()
    {
        using var fixture = AuthenticationFixture.Create();
        var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        var results = await Task.WhenAll(
            AttemptSetupAsync(fixture.Service, setupCode!),
            AttemptSetupAsync(fixture.Service, setupCode!));

        Assert.Single(results, result => result is AdministratorIdentity);
        Assert.Single(results, result => result is AdministratorAlreadyExistsException);
    }

    [Fact]
    public async Task Wrong_username_or_password_returns_null()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();

        Assert.Null(await fixture.Service.AuthenticateAsync("nobody", Password, CancellationToken.None));
        Assert.Null(await fixture.Service.AuthenticateAsync("dragos", "this-password-is-wrong", CancellationToken.None));
    }

    [Fact]
    public async Task Password_change_rotates_stamp_and_rejects_the_old_session()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();
        var before = await fixture.Service.AuthenticateAsync("dragos", Password, CancellationToken.None);

        var after = await fixture.Service.ChangePasswordAsync(
            new("dragos", Password, NewPassword),
            CancellationToken.None);

        Assert.NotEqual(before!.SecurityStamp, after.SecurityStamp);
        Assert.False(await fixture.Service.ValidateSessionAsync(
            before.Username,
            before.SecurityStamp,
            CancellationToken.None));
        Assert.Null(await fixture.Service.AuthenticateAsync("dragos", Password, CancellationToken.None));
        Assert.NotNull(await fixture.Service.AuthenticateAsync("dragos", NewPassword, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_current_password_throws_only_the_generic_credentials_exception()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();

        var exception = await Assert.ThrowsAsync<InvalidCurrentPasswordException>(() =>
            fixture.Service.ChangePasswordAsync(
                new("dragos", "this-password-is-wrong", NewPassword),
                CancellationToken.None));

        Assert.Equal("invalid_credentials", exception.Code);
    }

    [Fact]
    public async Task Successful_rehash_preserves_the_security_stamp()
    {
        using var fixture = AuthenticationFixture.Create(new RehashingPasswordHasher());
        var account = new AdministratorAccountDocument(1, "dragos", "DRAGOS", "old-hash", "fixed-stamp");
        await fixture.Repository.CreateAccountAsync(account, CancellationToken.None);

        var identity = await fixture.Service.AuthenticateAsync("dragos", Password, CancellationToken.None);
        var updated = await fixture.Repository.ReadAccountAsync(CancellationToken.None);

        Assert.Equal("fixed-stamp", identity!.SecurityStamp);
        Assert.Equal("fixed-stamp", updated!.SecurityStamp);
        Assert.Equal("new-hash", updated.PasswordHash);
    }

    [Fact]
    public async Task Deleting_the_account_invalidates_an_existing_session()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();
        var identity = await fixture.Service.AuthenticateAsync("dragos", Password, CancellationToken.None);

        File.Delete(fixture.Paths.AccountPath);

        Assert.False(await fixture.Service.ValidateSessionAsync(
            identity!.Username,
            identity.SecurityStamp,
            CancellationToken.None));
        Assert.Equal(
            AdministratorAccountState.SetupRequired,
            await fixture.Service.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_account_and_bootstrap_state_fail_closed()
    {
        using var fixture = AuthenticationFixture.Create();
        fixture.Paths.EnsureDirectories();
        await File.WriteAllTextAsync(fixture.Paths.AccountPath, "{bad-account");
        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(() =>
            fixture.Service.GetStateAsync(CancellationToken.None));

        File.Delete(fixture.Paths.AccountPath);
        await File.WriteAllTextAsync(fixture.Paths.BootstrapPath, "{bad-bootstrap");
        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(() =>
            fixture.Service.CreateAsync(new("unused-code", "dragos", Password), CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_password_hash_fails_closed()
    {
        using var fixture = AuthenticationFixture.Create();
        await fixture.Repository.CreateAccountAsync(
            new AdministratorAccountDocument(1, "dragos", "DRAGOS", "not-a-password-hash", "stamp"),
            CancellationToken.None);

        await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(() =>
            fixture.Service.AuthenticateAsync("dragos", Password, CancellationToken.None));
    }

    [Fact]
    public async Task Existing_account_removes_stale_bootstrap_without_issuing_a_code()
    {
        using var fixture = await AuthenticationFixture.WithAccountAsync();
        await File.WriteAllTextAsync(
            fixture.Paths.BootstrapPath,
            "{\"version\":1,\"verifier\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"createdAt\":\"2026-08-21T00:00:00Z\"}");

        var code = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

        Assert.Null(code);
        Assert.False(File.Exists(fixture.Paths.BootstrapPath));
    }

    [Fact]
    public async Task Bootstrap_host_prepares_once_and_logs_only_the_operator_message()
    {
        using var fixture = AuthenticationFixture.Create();
        var logger = new RecordingLogger<AuthenticationBootstrapHostedService>();
        var hostedService = new AuthenticationBootstrapHostedService(fixture.Service, logger);

        await hostedService.StartAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.StartsWith("ReachCommander first-run setup code: ", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Infrastructure_registers_one_account_service_and_the_bootstrap_host()
    {
        using var temporary = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:DataPath"] = temporary.Path,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReachCommanderInfrastructure(configuration);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(AuthenticationBootstrapHostedService));

        using var provider = services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<IAdministratorAccountService>(),
            provider.GetRequiredService<IAdministratorAccountService>());
    }

    private static async Task<object> AttemptSetupAsync(
        IAdministratorAccountService service,
        string setupCode)
    {
        try
        {
            return await service.CreateAsync(
                new(setupCode, "dragos", Password),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class AuthenticationFixture : IDisposable
    {
        private AuthenticationFixture(
            TemporaryDirectory temporary,
            AuthenticationDataPaths paths,
            FileAuthenticationRepository repository,
            AdministratorAccountService service,
            string? setupCode = null)
        {
            Temporary = temporary;
            Paths = paths;
            Repository = repository;
            Service = service;
            SetupCode = setupCode;
        }

        private TemporaryDirectory Temporary { get; }

        public AuthenticationDataPaths Paths { get; }

        public FileAuthenticationRepository Repository { get; }

        public AdministratorAccountService Service { get; }

        public string? SetupCode { get; private set; }

        public static AuthenticationFixture Create(
            IPasswordHasher<AdministratorAccountDocument>? passwordHasher = null)
        {
            var temporary = new TemporaryDirectory();
            var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
            paths.EnsureDirectories();
            var repository = new FileAuthenticationRepository(paths);
            passwordHasher ??= new PasswordHasher<AdministratorAccountDocument>(
                Options.Create(new PasswordHasherOptions()));
            var service = new AdministratorAccountService(repository, passwordHasher, TimeProvider.System);
            return new AuthenticationFixture(temporary, paths, repository, service);
        }

        public static async Task<AuthenticationFixture> WithAccountAsync()
        {
            var fixture = Create();
            var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);
            await fixture.Service.CreateAsync(
                new(setupCode!, "dragos", Password),
                CancellationToken.None);
            fixture.SetupCode = setupCode;
            return fixture;
        }

        public void Dispose() => Temporary.Dispose();
    }

    private sealed class RehashingPasswordHasher : IPasswordHasher<AdministratorAccountDocument>
    {
        public string HashPassword(AdministratorAccountDocument user, string password) => "new-hash";

        public PasswordVerificationResult VerifyHashedPassword(
            AdministratorAccountDocument user,
            string hashedPassword,
            string providedPassword) => PasswordVerificationResult.SuccessRehashNeeded;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
