using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Infrastructure.Authentication;

internal sealed class AdministratorAccountService(
    FileAuthenticationRepository repository,
    IPasswordHasher<AdministratorAccountDocument> passwordHasher,
    TimeProvider timeProvider) : IAdministratorAccountService
{
    private const int DocumentVersion = 1;
    private const int MinimumUsernameLength = 3;
    private const int MaximumUsernameLength = 64;
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordLength = 128;
    private const int MaximumSetupCodeLength = 256;

    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public async Task<string?> PrepareSetupAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (await repository.ReadAccountAsync(cancellationToken) is not null)
            {
                await repository.DeleteBootstrapAsync(cancellationToken);
                return null;
            }

            var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var verifier = Convert.ToBase64String(HashSetupCode(code));
            await repository.ReplaceBootstrapAsync(
                new BootstrapDocument(DocumentVersion, verifier, timeProvider.GetUtcNow()),
                cancellationToken);
            return code;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AdministratorAccountState> GetStateAsync(CancellationToken cancellationToken) =>
        await repository.ReadAccountAsync(cancellationToken) is null
            ? AdministratorAccountState.SetupRequired
            : AdministratorAccountState.AccountExists;

    public async Task<AdministratorIdentity> CreateAsync(
        CreateAdministratorCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var username = ValidateAndNormalizeUsername(command.Username);
        ValidatePassword(command.Password);

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (await repository.ReadAccountAsync(cancellationToken) is not null)
            {
                throw new AdministratorAlreadyExistsException();
            }

            var bootstrap = await repository.ReadBootstrapAsync(cancellationToken);
            if (bootstrap is null || !MatchesSetupCode(bootstrap, command.SetupCode))
            {
                throw new InvalidSetupCodeException();
            }

            var account = new AdministratorAccountDocument(
                DocumentVersion,
                username,
                NormalizeForComparison(username),
                PasswordHash: "pending",
                SecurityStamp: CreateSecurityStamp());
            account = account with
            {
                PasswordHash = passwordHasher.HashPassword(account, command.Password),
            };

            await repository.CreateAccountAsync(account, cancellationToken);
            await repository.DeleteBootstrapAsync(cancellationToken);
            return ToIdentity(account);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AdministratorIdentity?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeCredentialUsername(username, out var normalizedUsername) ||
            string.IsNullOrEmpty(password) ||
            password.Length > MaximumPasswordLength)
        {
            return null;
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var account = await repository.ReadAccountAsync(cancellationToken);
            if (account is null ||
                !string.Equals(account.NormalizedUsername, normalizedUsername, StringComparison.Ordinal))
            {
                return null;
            }

            var result = VerifyPassword(account, password);
            if (result == PasswordVerificationResult.Failed)
            {
                return null;
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                account = account with
                {
                    PasswordHash = passwordHasher.HashPassword(account, password),
                };
                await repository.ReplaceAccountAsync(account, cancellationToken);
            }

            return ToIdentity(account);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<bool> ValidateSessionAsync(
        string username,
        string securityStamp,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeCredentialUsername(username, out var normalizedUsername) ||
            string.IsNullOrEmpty(securityStamp))
        {
            return false;
        }

        var account = await repository.ReadAccountAsync(cancellationToken);
        return account is not null &&
            string.Equals(account.NormalizedUsername, normalizedUsername, StringComparison.Ordinal) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(account.SecurityStamp),
                Encoding.UTF8.GetBytes(securityStamp));
    }

    public async Task<AdministratorIdentity> ChangePasswordAsync(
        ChangeAdministratorPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidatePassword(command.NewPassword);

        if (!TryNormalizeCredentialUsername(command.Username, out var normalizedUsername) ||
            string.IsNullOrEmpty(command.CurrentPassword) ||
            command.CurrentPassword.Length > MaximumPasswordLength)
        {
            throw new InvalidCurrentPasswordException();
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var account = await repository.ReadAccountAsync(cancellationToken);
            if (account is null ||
                !string.Equals(account.NormalizedUsername, normalizedUsername, StringComparison.Ordinal) ||
                VerifyPassword(account, command.CurrentPassword) == PasswordVerificationResult.Failed)
            {
                throw new InvalidCurrentPasswordException();
            }

            var updated = account with
            {
                PasswordHash = passwordHasher.HashPassword(account, command.NewPassword),
                SecurityStamp = CreateSecurityStamp(),
            };
            await repository.ReplaceAccountAsync(updated, cancellationToken);
            return ToIdentity(updated);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static string ValidateAndNormalizeUsername(string username)
    {
        if (username is null)
        {
            throw InvalidUsername();
        }

        string normalized;
        try
        {
            normalized = username.Trim().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            throw InvalidUsername();
        }

        if (normalized.Length is < MinimumUsernameLength or > MaximumUsernameLength ||
            normalized.Any(char.IsControl))
        {
            throw InvalidUsername();
        }

        return normalized;
    }

    private static bool TryNormalizeCredentialUsername(
        string username,
        out string normalizedUsername)
    {
        try
        {
            var displayName = ValidateAndNormalizeUsername(username);
            normalizedUsername = NormalizeForComparison(displayName);
            return true;
        }
        catch (AuthenticationValidationException)
        {
            normalizedUsername = string.Empty;
            return false;
        }
    }

    private static string NormalizeForComparison(string username) => username.ToUpperInvariant();

    private static void ValidatePassword(string password)
    {
        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            throw new AuthenticationValidationException(
                "invalid_password",
                "The password must contain between 12 and 128 characters.");
        }
    }

    private static bool MatchesSetupCode(BootstrapDocument bootstrap, string setupCode)
    {
        if (string.IsNullOrEmpty(setupCode) || setupCode.Length > MaximumSetupCodeLength)
        {
            return false;
        }

        var expected = Convert.FromBase64String(bootstrap.Verifier);
        var supplied = HashSetupCode(setupCode);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static byte[] HashSetupCode(string setupCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(setupCode));

    private static string CreateSecurityStamp() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private PasswordVerificationResult VerifyPassword(
        AdministratorAccountDocument account,
        string password)
    {
        try
        {
            return passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new AuthenticationStateUnavailableException(
                "Authentication state contains an invalid password verifier.",
                exception);
        }
    }

    private static AdministratorIdentity ToIdentity(AdministratorAccountDocument account) =>
        new(account.Username, account.SecurityStamp);

    private static AuthenticationValidationException InvalidUsername() =>
        new(
            "invalid_username",
            "The username must contain between 3 and 64 characters and cannot contain control characters.");
}
