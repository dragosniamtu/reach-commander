# ReachCommander Single-Administrator Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-run single-administrator setup, password login, secure cookie sessions, password change, logout, protected APIs, and persistent cross-platform authentication data to ReachCommander.

**Architecture:** Angular owns setup/login/account user experience but never stores a password or bearer token. ASP.NET Core owns authentication and authorization through a same-origin encrypted HttpOnly cookie, antiforgery validation, rate-limited authentication endpoints, a file-backed single-account service, and persistent Data Protection keys outside the image.

**Tech Stack:** .NET 10, ASP.NET Core cookie authentication/Data Protection/antiforgery/rate limiting, `PasswordHasher<T>`, JSON file persistence, Angular 22 standalone components/signals/reactive forms/interceptors, Vitest, xUnit, Playwright, Docker Compose, Bash, Python installer renderer.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Support exactly one shared administrator account; do not add roles, multiple users, a database, OAuth/OIDC, LDAP, MFA, passkeys, or JWTs.
- Store only a versioned salted password hash. Never store or log a plaintext or reversibly encrypted password.
- Username length is 3–64 normalized characters; password length is 12–128 characters and passwords are never silently truncated.
- Require an operator-visible, cryptographically random one-time code before first account creation.
- Use an encrypted HttpOnly production cookie with `Secure`, `SameSite=Strict`, a 12-hour sliding lifetime, and no Remember Me option.
- Store no username/password, session ticket, password hash, setup code, or Data Protection key in the Docker image, Git, browser storage, environment variables, `.env`, or Compose.
- Persist production state below host `/opt/reachcommander/data`, mounted read/write at container `/data`; default native Windows state to `%LOCALAPPDATA%\ReachCommander\data`.
- A missing account file enables setup. A malformed, unreadable, or unsupported account file fails closed.
- Protect every `/api` operation except the explicit auth bootstrap surface; keep only the static PWA shell and minimal `/health` response anonymous.
- Use ASP.NET Core antiforgery validation for every unsafe API request and rate-limit setup/login.
- Never cache `/api/**`, authentication responses, file data, previews, or hardware data in the service worker.
- Preserve existing source RO/RW enforcement, source containment, non-root container execution, read-only root filesystem, capability drop, and `no-new-privileges`.
- Production still requires an HTTPS reverse proxy; proxy Basic Auth becomes optional defense in depth.
- Work red-green-refactor: observe every new behavior test failing before writing its production implementation.
- Commit after each task only when that task's focused tests pass; do not push without explicit user authorization.

---

## File Structure

### Backend contracts and persistence

- `src/ReachCommander.Application/Authentication/AuthenticationModels.cs`: account state, commands, and authenticated identity shared by API and infrastructure.
- `src/ReachCommander.Application/Authentication/IAdministratorAccountService.cs`: complete single-account use-case interface.
- `src/ReachCommander.Application/Authentication/AuthenticationExceptions.cs`: stable, non-sensitive authentication failures.
- `src/ReachCommander.Infrastructure/Authentication/AuthenticationDataPaths.cs`: Windows/Linux data-root resolution and narrow auth/key paths.
- `src/ReachCommander.Infrastructure/Authentication/AuthenticationDocuments.cs`: internal versioned account/bootstrap JSON documents.
- `src/ReachCommander.Infrastructure/Authentication/FileAuthenticationRepository.cs`: bounded JSON reads, file locking, restrictive modes, create/replace/delete operations.
- `src/ReachCommander.Infrastructure/Authentication/AdministratorAccountService.cs`: setup-code lifecycle, validation, hashing, login, session stamp checks, and password changes.
- `src/ReachCommander.Infrastructure/Authentication/AuthenticationBootstrapHostedService.cs`: startup setup-code rotation and operator-only logging.
- `src/ReachCommander.Infrastructure/DependencyInjection.cs`: authentication infrastructure registrations.

### ASP.NET Core boundary

- `src/ReachCommander.Api/Authentication/AuthenticationClaimTypes.cs`: claim names used by cookie issuance and validation.
- `src/ReachCommander.Api/Authentication/AccountCookieEvents.cs`: per-request account/security-stamp validation.
- `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`: Data Protection, cookie, antiforgery, authorization, and rate-limit configuration.
- `src/ReachCommander.Api/Contracts/Authentication/AuthenticationDtos.cs`: session, antiforgery, setup, login, and password-change DTOs.
- `src/ReachCommander.Api/Controllers/AuthenticationController.cs`: anonymous setup/login/session/token and authenticated logout/password endpoints.
- `src/ReachCommander.Api/Errors/AuthenticationExceptionHandler.cs`: sanitized Problem Details mapping.
- `src/ReachCommander.Api/Program.cs`: authentication/authorization middleware and endpoint policies.

### Backend tests

- `tests/ReachCommander.UnitTests/Authentication/AuthenticationDataPathsTests.cs`: Windows/Linux/explicit path behavior.
- `tests/ReachCommander.UnitTests/Authentication/FileAuthenticationRepositoryTests.cs`: missing/corrupt/bounded/atomic/concurrent persistence behavior.
- `tests/ReachCommander.UnitTests/Authentication/AdministratorAccountServiceTests.cs`: bootstrap, hashing, validation, login, stamp rotation, and failure behavior.
- `tests/ReachCommander.IntegrationTests/Support/TestAuthenticationHandler.cs`: authenticated principal for existing business-controller tests.
- `tests/ReachCommander.IntegrationTests/Support/TestAntiforgery.cs`: test-only antiforgery adapter for existing business-controller tests.
- `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`: isolated auth data and opt-in real security boundary.
- `tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs`: real setup/login/cookie/logout/password lifecycle.
- `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs`: anonymous denial across all application APIs.

### Angular authentication

- `client/reach-commander-ui/src/app/core/auth/authentication.models.ts`: discriminated UI/session/request types.
- `client/reach-commander-ui/src/app/core/auth/authentication-channel.ts`: in-memory antiforgery token and unauthorized event channel.
- `client/reach-commander-ui/src/app/core/auth/authentication-api.ts`: auth-only HTTP adapter.
- `client/reach-commander-ui/src/app/core/auth/authentication.interceptor.ts`: credentials, antiforgery header, and global `401` notification.
- `client/reach-commander-ui/src/app/core/auth/authentication-store.ts`: startup state machine and setup/login/logout/password operations.
- `client/reach-commander-ui/src/app/core/auth/protected-state-reset.service.ts`: immediate clearing/cancellation of commander state.
- `client/reach-commander-ui/src/app/features/auth/authentication-screen.component.*`: setup/login/unavailable UI.
- `client/reach-commander-ui/src/app/features/auth/account-menu.component.*`: username, logout, and change-password UI.
- `client/reach-commander-ui/src/app/app.*`: startup authentication gate.
- Existing commander stores/components: reset hooks and account-menu placement.

### Deployment, acceptance, and documentation

- `tests/e2e/support/authentication.ts`: deterministic E2E credential constants and artifact paths.
- `tests/e2e/support/seed-fixtures.ts`: isolated auth data plus setup-code log capture.
- `tests/e2e/specs/auth.setup.ts`: first-run setup and stored authenticated browser state.
- `tests/e2e/specs/authentication.spec.ts`: logout/login and locked-state acceptance.
- `tests/e2e/playwright.config.ts`: setup dependency and authenticated Chromium project.
- `Dockerfile`, `compose.yaml`, `deploy/compose.release.yaml`: persistent `/data` contract.
- `deploy/install.sh`, `deploy/reachcommander`, installer tests: data creation, permissions, diagnostics, preservation, backup, and uninstall.
- `.github/workflows/ci.yml`: writable data mount in hardened smoke testing.
- `README.md`, `SECURITY.md`, `docs/deployment/ubuntu.md`, proxy examples, and documentation contracts: public operator guidance.

---

### Task 1: Add bounded, fail-closed authentication persistence

**Files:**
- Create: `src/ReachCommander.Application/Authentication/AuthenticationModels.cs`
- Create: `src/ReachCommander.Application/Authentication/IAdministratorAccountService.cs`
- Create: `src/ReachCommander.Application/Authentication/AuthenticationExceptions.cs`
- Create: `src/ReachCommander.Infrastructure/Authentication/AuthenticationDataPaths.cs`
- Create: `src/ReachCommander.Infrastructure/Authentication/AuthenticationDocuments.cs`
- Create: `src/ReachCommander.Infrastructure/Authentication/FileAuthenticationRepository.cs`
- Create: `tests/ReachCommander.UnitTests/Authentication/AuthenticationDataPathsTests.cs`
- Create: `tests/ReachCommander.UnitTests/Authentication/FileAuthenticationRepositoryTests.cs`

**Interfaces:**
- Produces: `AdministratorAccountState`, `AdministratorIdentity`, `CreateAdministratorCommand`, `ChangeAdministratorPasswordCommand`, `IAdministratorAccountService`, `AuthenticationDataPaths`, `FileAuthenticationRepository`, `AdministratorAccountDocument`, and `BootstrapDocument`.
- `IAdministratorAccountService` exact methods:

```csharp
Task<string?> PrepareSetupAsync(CancellationToken cancellationToken);
Task<AdministratorAccountState> GetStateAsync(CancellationToken cancellationToken);
Task<AdministratorIdentity> CreateAsync(CreateAdministratorCommand command, CancellationToken cancellationToken);
Task<AdministratorIdentity?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
Task<bool> ValidateSessionAsync(string username, string securityStamp, CancellationToken cancellationToken);
Task<AdministratorIdentity> ChangePasswordAsync(ChangeAdministratorPasswordCommand command, CancellationToken cancellationToken);
```

- [ ] **Step 1: Write failing path and repository tests**

Create tests that establish the exact defaults and missing-versus-corrupt boundary:

```csharp
[Fact]
public void Resolve_uses_local_application_data_on_windows()
{
    var root = AuthenticationDataPaths.Resolve(null, isWindows: true, "C:/Users/Test/AppData/Local");
    Assert.Equal(Path.GetFullPath("C:/Users/Test/AppData/Local/ReachCommander/data"), root.RootPath);
}

[Fact]
public void Resolve_uses_data_mount_on_linux()
{
    var paths = AuthenticationDataPaths.Resolve(null, isWindows: false, null);
    Assert.Equal(Path.GetFullPath("/data"), paths.RootPath);
}

[Fact]
public async Task Missing_account_is_distinct_from_malformed_account()
{
    using var temporary = new TemporaryDirectory();
    var paths = AuthenticationDataPaths.ForRoot(temporary.Path);
    paths.EnsureDirectories();
    var repository = new FileAuthenticationRepository(paths);

    Assert.Null(await repository.ReadAccountAsync(CancellationToken.None));
    File.WriteAllText(paths.AccountPath, "{not-json");
    await Assert.ThrowsAsync<AuthenticationStateUnavailableException>(
        () => repository.ReadAccountAsync(CancellationToken.None).AsTask());
}
```

Add tests for a file above 64 KiB, unsupported schema version, `CreateAccountAsync` refusing a second account, replacement preserving the previous document when an injected write fails, bootstrap deletion, and Unix modes `0700`/`0600` when `OperatingSystem.IsLinux()`.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~AuthenticationDataPathsTests|FullyQualifiedName~FileAuthenticationRepositoryTests"
```

Expected: FAIL to compile because the authentication application and infrastructure types do not exist.

- [ ] **Step 3: Add application-level types and stable failures**

Use these exact public types:

```csharp
namespace ReachCommander.Application.Authentication;

public enum AdministratorAccountState { SetupRequired, AccountExists }

public sealed record AdministratorIdentity(string Username, string SecurityStamp);

public sealed record CreateAdministratorCommand(
    string SetupCode,
    string Username,
    string Password);

public sealed record ChangeAdministratorPasswordCommand(
    string Username,
    string CurrentPassword,
    string NewPassword);
```

Define `IAdministratorAccountService` with the six signatures in the Interfaces block. Define a base `AuthenticationException` carrying `Code` and safe `Detail`, then concrete `AuthenticationValidationException`, `AuthenticationStateUnavailableException`, `AdministratorAlreadyExistsException`, `InvalidSetupCodeException`, and `InvalidCurrentPasswordException`. Do not put submitted secrets in exception messages.

- [ ] **Step 4: Implement narrow path resolution and versioned documents**

`AuthenticationDataPaths` must expose `RootPath`, `AuthDirectory`, `AccountPath`, `BootstrapPath`, `LockPath`, and `KeysDirectory`. Its exact resolver signature is `Resolve(string? configuredRoot, bool isWindows, string? localApplicationData)`. It uses an explicit configured absolute path when supplied, `%LOCALAPPDATA%\ReachCommander\data` on Windows, and `/data` otherwise. Reject a relative explicit path.

Use these internal documents and keep the JSON schema at version `1`:

```csharp
internal sealed record AdministratorAccountDocument(
    int Version,
    string Username,
    string NormalizedUsername,
    string PasswordHash,
    string SecurityStamp);

internal sealed record BootstrapDocument(
    int Version,
    string Verifier,
    DateTimeOffset CreatedAt);
```

`EnsureDirectories()` creates only the root, `auth`, and `keys` directories. On Unix, set each to `UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute`.

- [ ] **Step 5: Implement bounded atomic repository operations**

`FileAuthenticationRepository` uses camel-case `System.Text.Json`, rejects files larger than `65_536` bytes before deserialization, validates every required field and version, and translates parse/read failures to `AuthenticationStateUnavailableException`.

All mutations acquire `auth/auth.lock` with `FileShare.None`, write a same-directory random temporary file using `FileOptions.WriteThrough`, flush it, set Unix mode `0600`, and rename it over the destination. Initial account creation uses `FileMode.CreateNew`, so only one caller can create the account. Always delete a known temporary file in `finally`; never recursively delete the auth directory.

The repository surface is:

```csharp
internal ValueTask<AdministratorAccountDocument?> ReadAccountAsync(CancellationToken cancellationToken);
internal ValueTask<BootstrapDocument?> ReadBootstrapAsync(CancellationToken cancellationToken);
internal Task CreateAccountAsync(AdministratorAccountDocument document, CancellationToken cancellationToken);
internal Task ReplaceAccountAsync(AdministratorAccountDocument document, CancellationToken cancellationToken);
internal Task ReplaceBootstrapAsync(BootstrapDocument document, CancellationToken cancellationToken);
internal Task DeleteBootstrapAsync(CancellationToken cancellationToken);
```

- [ ] **Step 6: Run tests and commit**

Run the Task 1 focused command again. Expected: PASS, including the Linux mode assertions on Linux and their guarded skip on Windows.

```powershell
git add src/ReachCommander.Application/Authentication src/ReachCommander.Infrastructure/Authentication tests/ReachCommander.UnitTests/Authentication
git commit -m "feat: add authentication persistence"
```

---

### Task 2: Implement setup codes, password hashing, login, and stamp rotation

**Files:**
- Create: `src/ReachCommander.Infrastructure/Authentication/AdministratorAccountService.cs`
- Create: `src/ReachCommander.Infrastructure/Authentication/AuthenticationBootstrapHostedService.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `tests/ReachCommander.UnitTests/Authentication/AdministratorAccountServiceTests.cs`

**Interfaces:**
- Consumes: all Task 1 interfaces/documents/repository methods.
- Produces: singleton `IAdministratorAccountService`; startup setup-code rotation through `AuthenticationBootstrapHostedService`.

- [ ] **Step 1: Write failing account lifecycle tests**

Cover the full service contract with a temporary data root and real `PasswordHasher<AdministratorAccountDocument>`:

```csharp
[Fact]
public async Task Setup_persists_only_a_hash_and_authenticates_the_account()
{
    var fixture = AuthenticationFixture.Create();
    var setupCode = await fixture.Service.PrepareSetupAsync(CancellationToken.None);

    var identity = await fixture.Service.CreateAsync(
        new(setupCode!, "dragos", "a-long-test-password"),
        CancellationToken.None);

    var json = File.ReadAllText(fixture.Paths.AccountPath);
    Assert.DoesNotContain("a-long-test-password", json, StringComparison.Ordinal);
    Assert.DoesNotContain(setupCode!, json, StringComparison.Ordinal);
    Assert.Equal(identity, await fixture.Service.AuthenticateAsync(
        "DRAGOS", "a-long-test-password", CancellationToken.None));
}

[Fact]
public async Task Password_change_rotates_stamp_and_rejects_the_old_session()
{
    var fixture = await AuthenticationFixture.WithAccountAsync();
    var before = await fixture.Service.AuthenticateAsync("dragos", "a-long-test-password", default);
    var after = await fixture.Service.ChangePasswordAsync(
        new("dragos", "a-long-test-password", "a-different-test-password"), default);

    Assert.NotEqual(before!.SecurityStamp, after.SecurityStamp);
    Assert.False(await fixture.Service.ValidateSessionAsync(
        before.Username, before.SecurityStamp, default));
    Assert.NotNull(await fixture.Service.AuthenticateAsync(
        "dragos", "a-different-test-password", default));
}
```

Also test: every startup preparation rotates the previous bootstrap; the verifier is not the plaintext code; fixed-time verification rejects wrong/reused codes; username trimming/NFKC normalization and invariant case matching; username/password boundaries; concurrent setup yields exactly one success; wrong login returns `null`; wrong current password throws only `InvalidCurrentPasswordException`; successful rehash preserves the stamp; deleting the account invalidates a session; corrupt account/bootstrap state fails closed.

- [ ] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~AdministratorAccountServiceTests
```

Expected: FAIL to compile because `AdministratorAccountService` and its startup host do not exist.

- [ ] **Step 3: Implement strict validation and bootstrap rotation**

Generate 32 random bytes with `RandomNumberGenerator.GetBytes(32)` and encode them as unpadded Base64URL. Persist only `SHA256.HashData(Encoding.UTF8.GetBytes(code))` as Base64. Compare decoded verifier bytes with `CryptographicOperations.FixedTimeEquals`.

Normalize a username with `Trim().Normalize(NormalizationForm.FormKC)` and derive `NormalizedUsername` with `ToUpperInvariant()`. Reject control characters and lengths outside 3–64. Reject passwords outside 12–128 without normalizing or truncating them.

`PrepareSetupAsync` reads the account first. It returns `null` and removes a stale bootstrap when an account exists; otherwise it atomically replaces `bootstrap.json` and returns the new plaintext code. `CreateAsync` rechecks account absence under the repository lock, validates the current bootstrap, hashes with `IPasswordHasher<AdministratorAccountDocument>`, creates a random 32-byte security stamp, creates `account.json`, and consumes `bootstrap.json`.

- [ ] **Step 4: Implement authentication and password change**

Use `PasswordHasher<AdministratorAccountDocument>.VerifyHashedPassword`. Return `null` for either an unknown username or a failed password without differentiating them. If verification reports `SuccessRehashNeeded`, replace only the password hash and preserve the security stamp.

`ValidateSessionAsync` returns true only when the normalized username and security stamp both match the current document. `ChangePasswordAsync` verifies the current password, hashes the new password, rotates the security stamp, and atomically replaces the account.

- [ ] **Step 5: Add startup code logging and DI**

The hosted service calls `PrepareSetupAsync` exactly once during `StartAsync`. Log the code only when non-null using this stable operator message, which E2E will parse:

```csharp
logger.LogWarning(
    "ReachCommander first-run setup code: {SetupCode}",
    setupCode);
```

Register the repository, `PasswordHasher<AdministratorAccountDocument>`, account service, and hosted service as singletons in `AddReachCommanderInfrastructure`. Do not register a background loop.

- [ ] **Step 6: Run tests and commit**

Run the focused Task 2 tests, then all unit tests:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release
```

Expected: all unit tests PASS.

```powershell
git add src/ReachCommander.Infrastructure/Authentication src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/Authentication
git commit -m "feat: add administrator account lifecycle"
```

---

### Task 3: Enforce cookie authentication, antiforgery, and rate limits in ASP.NET Core

**Files:**
- Create: `src/ReachCommander.Api/Authentication/AuthenticationClaimTypes.cs`
- Create: `src/ReachCommander.Api/Authentication/AccountCookieEvents.cs`
- Create: `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`
- Create: `src/ReachCommander.Api/Contracts/Authentication/AuthenticationDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/AuthenticationController.cs`
- Create: `src/ReachCommander.Api/Errors/AuthenticationExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/Program.cs`

**Interfaces:**
- Consumes: `IAdministratorAccountService` and `AuthenticationDataPaths` from Tasks 1–2.
- Produces: `GET /api/auth/session`, `GET /api/auth/antiforgery`, `POST /api/auth/setup`, `POST /api/auth/login`, `POST /api/auth/logout`, and `POST /api/auth/password`.
- Session JSON is exactly `{ "state": "setupRequired|anonymous|authenticated", "username": string|null }`.
- Antiforgery JSON is exactly `{ "requestToken": string }`; unsafe requests use `X-ReachCommander-CSRF`.

- [ ] **Step 1: Add failing real-boundary integration tests**

Create `AuthenticationApiTests` initially with a private factory configured with a temporary `Authentication:DataPath`. Prove the intended anonymous surface and initial state:

```csharp
[Fact]
public async Task Fresh_instance_exposes_only_setup_shell_and_health()
{
    await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
    using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sources")).StatusCode);
    var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session");
    Assert.Equal("setupRequired", session?.State);
}
```

Add a test proving unsafe setup without `X-ReachCommander-CSRF` is rejected and a test proving the cookie challenge is `401`, never a `302` HTML redirect.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~AuthenticationApiTests
```

Expected: FAIL because the auth routes return 404 and `/api/sources` is still anonymous.

- [ ] **Step 3: Configure Data Protection and the session cookie**

In `AuthenticationConfiguration.AddReachCommanderAuthentication`, resolve and create `AuthenticationDataPaths`, register it, and persist Data Protection keys to `paths.KeysDirectory` with application name `ReachCommander`.

Configure the cookie scheme with:

```csharp
options.Cookie.Name = "ReachCommander.Session";
options.Cookie.HttpOnly = true;
options.Cookie.SameSite = SameSiteMode.Strict;
options.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment("Testing")
    ? CookieSecurePolicy.SameAsRequest
    : CookieSecurePolicy.Always;
options.ExpireTimeSpan = TimeSpan.FromHours(12);
options.SlidingExpiration = true;
options.EventsType = typeof(AccountCookieEvents);
options.LoginPath = PathString.Empty;
options.AccessDeniedPath = PathString.Empty;
```

Override redirect events to set `401`/`403` without a Location redirect. Issued principals contain both `ClaimTypes.NameIdentifier` and `ClaimTypes.Name` with the normalized display username plus `AuthenticationClaimTypes.SecurityStamp`. `AccountCookieEvents.ValidatePrincipal` reads `NameIdentifier` and the security stamp; reject and sign out when either is absent or `ValidateSessionAsync` returns false.

- [ ] **Step 4: Configure authorization, antiforgery, and rate limiting**

Set an authorization fallback policy requiring an authenticated user. Configure antiforgery with header `X-ReachCommander-CSRF`, an HttpOnly strict-same-site cookie, and the same production Secure policy as the session cookie. Add `AutoValidateAntiforgeryTokenAttribute` as a global MVC filter so every unsafe controller action, including setup/login, is validated. Add an API no-store middleware before endpoint execution that sets `Cache-Control: no-store` and `Pragma: no-cache` for every `/api` response, including unknown routes; static PWA assets remain cacheable.

Add per-remote-IP fixed-window policies:

```csharp
public const string SetupPolicy = "authentication-setup"; // 5 attempts/minute
public const string LoginPolicy = "authentication-login"; // 10 attempts/minute
```

Use zero queue capacity and return `application/problem+json` with code `authentication_rate_limited` on `429`. Partition only on `HttpContext.Connection.RemoteIpAddress`; do not trust unconfigured forwarded headers.

- [ ] **Step 5: Add the controller and safe contracts**

Use `[AllowAnonymous]` only on session, antiforgery, setup, and login. Apply `[EnableRateLimiting]` to setup/login. Setup/login issue non-persistent cookies with `IsPersistent = false` and claims for username plus security stamp.

The controller action behavior is:

```csharp
GET session       -> setupRequired, anonymous, or authenticated
GET antiforgery   -> IAntiforgery.GetAndStoreTokens(HttpContext).RequestToken
POST setup        -> create account, SignInAsync, return authenticated session
POST login        -> generic 401 code invalid_credentials or SignInAsync
POST logout       -> SignOutAsync, return 204
POST password     -> rotate password/stamp, SignInAsync fresh ticket, return authenticated session
```

DTOs use `[Required]` and `[StringLength]` matching the approved limits. Add `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]` to the controller and explicit `Cache-Control: no-store` behavior for authentication responses.

- [ ] **Step 6: Map safe errors and wire middleware in the correct order**

Register `AuthenticationExceptionHandler` before the existing file handler. Map validation to 400, reused/unavailable setup to 409, invalid setup/current credentials to generic 401, corrupt/unreadable storage to 503, and never include submitted values.

`Program.cs` order must be:

```csharp
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.Map("/api/{**unmatched}", /* existing JSON 404 */).RequireAuthorization();
app.MapFallbackToFile("index.html").AllowAnonymous();
```

Keep OpenAPI Development-only and require the fallback authorization policy. The optional data-root override is the ordinary `Authentication:DataPath` configuration key; do not add a password, setup code, or authentication bypass to any settings file.

- [ ] **Step 7: Run focused tests and commit**

Run the Task 3 integration filter. Expected: the new boundary tests PASS; existing integration tests may now fail with `401` until Task 4 adds their test-only principal.

```powershell
git add src/ReachCommander.Api tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs
git commit -m "feat: enforce server authentication boundary"
```

---

### Task 4: Prove authentication lifecycle and preserve existing integration coverage

**Files:**
- Create: `tests/ReachCommander.IntegrationTests/Support/TestAuthenticationHandler.cs`
- Create: `tests/ReachCommander.IntegrationTests/Support/TestAntiforgery.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Expand: `tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs`

**Interfaces:**
- Consumes: Task 3 routes/cookie/CSRF contract.
- Produces: `ReachCommanderApiFactory(bool useRealSecurity = false, string? authenticationDataPath = null)`, isolated `AuthenticationDataPath`, and `GetFreshSetupCodeAsync()` for real-security tests. Supplying a path allows a deliberate stop/restart test to reuse only authentication state.

- [ ] **Step 1: Write the remaining failing lifecycle and boundary tests**

Add a helper that fetches `/api/auth/antiforgery`, stores the returned token in `X-ReachCommander-CSRF`, and uses a cookie-enabled client. Then cover:

```csharp
[Fact]
public async Task Setup_login_logout_and_password_change_rotate_expected_sessions()
{
    await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
    var first = factory.CreateCookieClient();
    var second = factory.CreateCookieClient();
    await first.SetAntiforgeryAsync();
    var code = await factory.GetFreshSetupCodeAsync();

    Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync(
        "/api/auth/setup", new { setupCode = code, username = "dragos", password = "a-long-test-password" })).StatusCode);
    await first.SetAntiforgeryAsync();
    await second.SetAntiforgeryAsync();
    Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync(
        "/api/auth/login", new { username = "dragos", password = "a-long-test-password" })).StatusCode);
    await second.SetAntiforgeryAsync();

    Assert.Equal(HttpStatusCode.OK, (await first.PostAsJsonAsync(
        "/api/auth/password", new { currentPassword = "a-long-test-password", newPassword = "a-different-test-password" })).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/sources")).StatusCode);
}
```

Add assertions for: generic wrong username/password responses; setup-code reuse; rate-limit `429`; 12-hour/sliding cookie options through DI; logout cookie expiry; account JSON excluding password/code; deletion and recreation invalidating old cookies; malformed account producing 503 rather than setup; key-ring replacement plus restart invalidating cookies; anonymous antiforgery tokens becoming invalid after sign-in until refreshed; `Cache-Control: no-store` on auth and file APIs; absence of permissive credentialed CORS; passwords/hashes/stamps never appearing in captured logs; static shell and health anonymous; OpenAPI unavailable outside Development.

`AuthorizationBoundaryTests` must enumerate representative GET/POST endpoints for sources, files, archives, metrics, upload limits/upload, batch rename preview/execute/undo, and archive extraction preview/execute/status/cancel and assert anonymous `401`. Include the unknown `/api/*` endpoint.

- [ ] **Step 2: Run all integration tests and verify RED**

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release
```

Expected: existing controller tests fail with `401`, and the newly added lifecycle assertions expose any unfinished behavior.

- [ ] **Step 3: Add isolated test security adapters**

`TestAuthenticationHandler` derives from `AuthenticationHandler<AuthenticationSchemeOptions>` and returns a principal containing name `integration-test` and a test-only security-stamp claim. `TestAntiforgery` implements `IAntiforgery` with no-op validation and deterministic token sets.

In the default factory only, override the default authenticate/challenge schemes with `IntegrationTest` and replace `IAntiforgery` with `TestAntiforgery`. When `useRealSecurity` is true, do neither; all real cookie and antiforgery services remain active. This keeps business-controller tests focused without weakening production or the new authentication suite.

Always add this isolated setting:

```csharp
["Authentication:DataPath"] = AuthenticationDataPath
```

`GetFreshSetupCodeAsync` starts the host, resolves `IAdministratorAccountService`, and calls `PrepareSetupAsync`; it throws if an account already exists. Never add a configuration switch that bypasses production authentication.

- [ ] **Step 4: Finish lifecycle behavior and assertions**

Fix only behavior found by the real-security tests. Ensure each request revalidates the current account/security stamp, password change reissues the current cookie, account deletion returns setup state only after cookie rejection, and corrupt state returns the sanitized 503 contract.

- [ ] **Step 5: Run all backend tests and commit**

```powershell
dotnet test ReachCommander.slnx -c Release
```

Expected: every unit and integration test PASS on Windows; the same command remains CI-valid on Ubuntu.

```powershell
git add tests/ReachCommander.IntegrationTests src/ReachCommander.Api src/ReachCommander.Infrastructure
git commit -m "test: verify authentication and authorization"
```

---

### Task 5: Add Angular authentication transport, CSRF interceptor, and state machine

**Files:**
- Create: `client/reach-commander-ui/src/app/core/auth/authentication.models.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication-channel.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication-api.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication.interceptor.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication.interceptor.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication-store.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/authentication-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.config.ts`

**Interfaces:**
- Produces: `AuthenticationStore.state()` with phases `checking`, `setupRequired`, `anonymous`, `authenticated`, and `unavailable`.
- Produces exact methods: `initialize()`, `retry()`, `setup(command)`, `login(command)`, `logout()`, `changePassword(command)`, and `lock()`.
- Produces `authenticationInterceptor` and `AuthenticationChannel` holding only an in-memory CSRF token plus unauthorized events.

- [ ] **Step 1: Write failing HTTP and state tests**

Test the exact auth routes, `withCredentials`, CSRF header on unsafe requests, no header on GET, and `401` notification. Test the store's three server states, unavailable state, generic errors, no password fields in store state, and duplicate-submit prevention.

```ts
it('adds the in-memory antiforgery token only to unsafe requests', () => {
  channel.setAntiforgeryToken('csrf-token');
  http.post('/api/auth/login', {}).subscribe();
  const request = controller.expectOne('/api/auth/login');
  expect(request.request.withCredentials).toBe(true);
  expect(request.request.headers.get('X-ReachCommander-CSRF')).toBe('csrf-token');
});

it('does not initialize protected state before the session is authenticated', async () => {
  api.sessionResult = { state: 'setupRequired', username: null };
  await store.initialize();
  expect(store.state().phase).toBe('setupRequired');
  expect(JSON.stringify(store.state())).not.toContain('password');
});
```

- [ ] **Step 2: Run and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include "src/app/core/auth/**/*.spec.ts"
Pop-Location
```

Expected: FAIL because the auth files do not exist.

- [ ] **Step 3: Add exact models and HTTP adapter**

Use discriminated state and request-only credentials:

```ts
export type SessionState = 'setupRequired' | 'anonymous' | 'authenticated';
export interface AuthenticationSessionDto { readonly state: SessionState; readonly username: string | null; }
export interface SetupCommand { readonly setupCode: string; readonly username: string; readonly password: string; }
export interface LoginCommand { readonly username: string; readonly password: string; }
export interface ChangePasswordCommand { readonly currentPassword: string; readonly newPassword: string; }
export type AuthenticationPhase = 'checking' | SessionState | 'unavailable';
export interface AuthenticationViewState {
  readonly phase: AuthenticationPhase;
  readonly username: string | null;
  readonly pending: boolean;
  readonly errorCode: string | null;
  readonly errorMessage: string | null;
}
```

`AuthenticationApi` uses `firstValueFrom` for the six routes from Task 3. `getAntiforgeryToken()` returns the string from `{ requestToken }`; it does not persist it.

- [ ] **Step 4: Implement the channel and functional interceptor**

`AuthenticationChannel` owns a private signal for the token and a private `Subject<void>` for unauthorized events. It exposes read-only `token`, `unauthorized$`, `setAntiforgeryToken`, `clearAntiforgeryToken`, and `notifyUnauthorized`.

The functional interceptor clones same-origin `/api/` requests with `withCredentials: true`, adds `X-ReachCommander-CSRF` to `POST`, `PUT`, `PATCH`, and `DELETE` when a token exists, and notifies the channel for `401` unless the response is an expected credential error (`invalid_credentials` or `setup_failed`) from login, setup, or password change. A stale-cookie `401` without one of those codes still locks the UI. It rethrows the original `HttpErrorResponse`.

Register it exactly with:

```ts
provideHttpClient(withInterceptors([authenticationInterceptor]))
```

- [ ] **Step 5: Implement the authentication store**

`initialize()` first fetches/stores a CSRF token, then fetches session state. A private `ensureAntiforgeryToken()` performs the same fetch whenever an unsafe auth operation begins without a token, so login still works after a local lock/logout. `setup` and `login` pass credentials directly to the API; after sign-in they discard the anonymous antiforgery token, fetch a new token bound to the authenticated identity, and only then expose the authenticated state. They never copy credentials into a signal. `logout` attempts the API call and then locks locally even if the network fails. `changePassword` keeps the authenticated state on a validation failure, accepts the freshly issued session on success, and refreshes the antiforgery token.

Subscribe once to `AuthenticationChannel.unauthorized$` and call `lock()`. `lock()` clears the channel token, sets `{ phase: 'anonymous', username: null, pending: false }`, and does not preserve sensitive server errors. `retry()` calls the same initialization path with duplicate calls coalesced.

- [ ] **Step 6: Run tests and commit**

Run the focused auth tests, then all Angular tests:

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: all Angular tests PASS.

```powershell
git add client/reach-commander-ui/src/app/core/auth client/reach-commander-ui/src/app/app.config.ts
git commit -m "feat: add Angular authentication state"
```

---

### Task 6: Gate the commander behind accessible setup and login screens

**Files:**
- Create: `client/reach-commander-ui/src/app/features/auth/authentication-screen.component.ts`
- Create: `client/reach-commander-ui/src/app/features/auth/authentication-screen.component.html`
- Create: `client/reach-commander-ui/src/app/features/auth/authentication-screen.component.scss`
- Create: `client/reach-commander-ui/src/app/features/auth/authentication-screen.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/app.ts`
- Modify: `client/reach-commander-ui/src/app/app.html`
- Modify: `client/reach-commander-ui/src/app/app.scss`
- Modify: `client/reach-commander-ui/src/app/app.spec.ts`

**Interfaces:**
- Consumes: `AuthenticationStore` Task 5 phases/actions.
- Produces: commander creation only for `authenticated`; setup/login/unavailable/checking screens otherwise.

- [ ] **Step 1: Write failing gate and form tests**

Prove that the commander component is absent in checking/setup/login/unavailable states, the correct fields/autocomplete attributes appear, mismatched passwords stay client-side, keyboard submission works, errors use an `aria-live` region, and credentials are cleared after success.

```ts
it('does not construct the commander until the session is authenticated', async () => {
  auth.setState({ phase: 'anonymous', username: null, pending: false, errorCode: null, errorMessage: null });
  const fixture = TestBed.createComponent(App);
  fixture.detectChanges();
  expect(fixture.nativeElement.querySelector('app-commander-shell')).toBeNull();
  expect(fixture.nativeElement.querySelector('app-authentication-screen')).not.toBeNull();
});
```

- [ ] **Step 2: Run and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false --include "src/app/app.spec.ts" --include "src/app/features/auth/authentication-screen.component.spec.ts"
Pop-Location
```

Expected: FAIL because the application still renders the commander unconditionally.

- [ ] **Step 3: Build the authentication screen**

Use `ReactiveFormsModule` with separate login and setup forms. Exact autocomplete values are `username`, `current-password`, and `new-password`; setup code uses `one-time-code`. The setup form has code, username, password, and confirmation. The login form has username and password. Keep password inputs hidden by default and provide labeled visibility buttons.

On submit, call the matching store method only when valid and not pending, then reset password/code fields. Use the approved copy: `Create administrator`, `Sign in`, `Connection required`, and `Retry`. Do not mention whether a submitted username exists.

- [ ] **Step 4: Add the root authentication gate**

`App` implements `OnInit`, injects `AuthenticationStore`, and calls `initialize()` once. Template behavior is:

```html
@if (auth.state().phase === 'authenticated') {
  <app-commander-shell />
} @else {
  <app-authentication-screen />
}
```

The authentication screen includes the ReachCommander mark/name even while offline, fits narrow screens without changing the commander's existing 680px minimum, and uses existing color tokens.

- [ ] **Step 5: Update app tests and commit**

Update `AppTestApi` only for commander APIs; provide a dedicated fake `AuthenticationStore` or mock auth API in app tests. Prove authenticated still renders two panels and anonymous never calls `getSources`.

Run all Angular tests. Expected: PASS.

```powershell
git add client/reach-commander-ui/src/app/app.* client/reach-commander-ui/src/app/features/auth
git commit -m "feat: add first-run and login UI"
```

---

### Task 7: Clear protected state and add account/password controls

**Files:**
- Create: `client/reach-commander-ui/src/app/core/auth/protected-state-reset.service.ts`
- Create: `client/reach-commander-ui/src/app/core/auth/protected-state-reset.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/auth/authentication-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/auth/authentication-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/system-metrics-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/system-metrics-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/upload-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/upload-store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/multi-rename-store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/archive-extraction-store.ts`
- Create: `client/reach-commander-ui/src/app/features/auth/account-menu.component.ts`
- Create: `client/reach-commander-ui/src/app/features/auth/account-menu.component.html`
- Create: `client/reach-commander-ui/src/app/features/auth/account-menu.component.scss`
- Create: `client/reach-commander-ui/src/app/features/auth/account-menu.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Produces `reset()` on commander, metrics, and upload stores; existing `close()` methods clear rename/extraction state.
- Produces `ProtectedStateResetService.reset()` and account controls in the existing right-side top actions.

- [ ] **Step 1: Write failing reset and account-menu tests**

Prove `401`, logout, and a `401` received by another browser after password-stamp rotation clear both panel entries/selections/previews, stop metrics polling, cancel upload subscriptions, close dialogs, and invalidate late async responses. The browser that successfully changes its own password receives a fresh cookie and keeps its current protected workspace. Prove account menu shows only the current username, requires current/new/confirmation password, and calls logout/change without browser storage.

```ts
it('clears every protected store when authentication is lost', () => {
  reset.reset();
  expect(commander.sources()).toEqual([]);
  expect(commander.leftPanel().entries).toEqual([]);
  expect(metrics.state().snapshot).toBeNull();
  expect(upload.state().phase).toBe('closed');
  expect(rename.state().open).toBe(false);
  expect(extraction.state().phase).toBe('closed');
});
```

- [ ] **Step 2: Run and verify RED**

Run the focused store/auth/commander-shell specs. Expected: FAIL because reset hooks and account menu do not exist.

- [ ] **Step 3: Add explicit reset hooks**

`CommanderStore.reset()` increments a session generation and request token, clears initialization, sources, both panels, active side, and counters. `initializeCore` captures the generation and ignores a late response after reset.

`SystemMetricsStore.reset()` calls `stop()` and restores the exact initial empty state. `UploadStore.reset()` unsubscribes even during uploading/finalizing, clears the completion callback/limit cache, and restores `closedState`. Multi-Rename and Archive Extraction use their existing `close()` methods, which already invalidate timers/polls.

`ProtectedStateResetService.reset()` calls these six operations synchronously. Inject it into `AuthenticationStore`; call it from `lock()` and after logout. Do not reset the current browser after a successful password change because the server issues that browser a fresh valid cookie; other browsers clear themselves when their next request receives `401`.

- [ ] **Step 4: Add account menu and change-password dialog**

Place `<app-account-menu />` in `.top-actions` before the system-metrics widget. Its trigger exposes the signed-in username and `aria-expanded`. Menu actions are `Change password` and `Logout`.

The modal uses a real form/dialog label, traps ordinary tab focus through its controls, restores trigger focus on close, rejects mismatch locally, and calls:

```ts
await auth.changePassword({ currentPassword, newPassword });
```

Clear all password controls on close/success. Disable close/submit while pending. Successful password change closes the modal and announces `Password changed. Other sessions were signed out.`

- [ ] **Step 5: Run all Angular tests and commit**

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
Pop-Location
```

Expected: all Angular tests PASS, including late-response/reset cases.

```powershell
git add client/reach-commander-ui/src/app/core client/reach-commander-ui/src/app/features/auth client/reach-commander-ui/src/app/features/commander/commander-shell
git commit -m "feat: add secure account controls"
```

---

### Task 8: Verify first-run/login behavior in the PWA and real browser

**Files:**
- Modify: `client/reach-commander-ui/tools/pwa-assets.test.mjs`
- Modify: `client/reach-commander-ui/tools/verify-pwa-build.mjs`
- Create: `tests/e2e/support/authentication.ts`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Create: `tests/e2e/specs/auth.setup.ts`
- Create: `tests/e2e/specs/authentication.spec.ts`
- Modify: `tests/e2e/specs/pwa.spec.ts`
- Modify: `tests/e2e/playwright.config.ts`

**Interfaces:**
- Produces: setup project that writes `artifacts/playwright-auth-state.json`; all existing Chromium tests consume it.
- Produces no production credential or deterministic setup-code override.

- [ ] **Step 1: Write failing PWA and Playwright contracts**

Extend PWA tests to assert `/api/auth/**` is covered by the existing `/api/**` exclusion and no auth/API response appears in `ngsw.json` data groups. Add Playwright acceptance for first-run setup, logout/login, password change, and offline shell with no commander rows.

- [ ] **Step 2: Run and verify RED**

```powershell
Push-Location client/reach-commander-ui
npm run test:pwa
npm run build
npm run verify:pwa
Pop-Location
Push-Location tests/e2e
npx playwright test specs/auth.setup.ts --workers=1
Pop-Location
```

Expected: PWA source tests remain green, but Playwright fails because setup-code capture and the setup project do not exist.

- [ ] **Step 3: Capture the random setup code only in the test harness**

Set the E2E server environment to `ASPNETCORE_ENVIRONMENT=Testing` and `Authentication__DataPath=<fixtureRoot>/auth-data`. Pipe stdout/stderr while mirroring them to the terminal. Resolve a promise only when this exact regex appears:

```ts
/ReachCommander first-run setup code:\s+([A-Za-z0-9_-]{40,})/
```

Write the captured code to `artifacts/e2e-setup-code.txt` with restrictive mode, and delete both code/auth-state artifacts during teardown. Do not add `Authentication__BootstrapCode` or another production configuration secret.

- [ ] **Step 4: Add setup dependency and authenticated storage state**

Create one setup test that reads the temporary code, completes UI account creation using test-only username `reachcommander-e2e` and password `ReachCommander-E2E-Password-2026!`, logs out, proves a wrong login fails generically, logs in correctly, changes the password and changes it back, then saves the final valid context to `artifacts/playwright-auth-state.json`.

Configure projects:

```ts
{ name: 'auth-setup', testMatch: /auth\.setup\.ts/ },
{
  name: 'chromium',
  dependencies: ['auth-setup'],
  use: Object.assign({}, devices['Desktop Chrome'], {
    viewport: { width: 1440, height: 900 },
    storageState: '../../artifacts/playwright-auth-state.json',
  }),
}
```

Exclude `auth.setup.ts` from the Chromium project's ordinary `testMatch`. `authentication.spec.ts` starts from the saved authenticated state, logs out, verifies no panel/file data remains, logs back in, and restores authenticated state without changing the password.

- [ ] **Step 5: Verify PWA and browser acceptance, then commit**

```powershell
Push-Location client/reach-commander-ui
npm run test:pwa
npm run build
npm run verify:pwa
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
```

Expected: source/build PWA checks PASS and every Playwright scenario PASS with the authenticated setup dependency.

```powershell
git add client/reach-commander-ui/tools tests/e2e
git commit -m "test: verify authenticated PWA workflows"
```

---

### Task 9: Add the narrow persistent data mount to every container path

**Files:**
- Modify: `Dockerfile`
- Modify: `compose.yaml`
- Modify: `deploy/compose.release.yaml`
- Modify: `tests/installer/test_render_config.py`
- Modify: `tests/installer/workflow-contract.test.mjs`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: image default `Authentication__DataPath=/data`; local named volume and release bind mount at `/data`; hardened smoke data mount.

- [ ] **Step 1: Add failing render and workflow tests**

Require one application-data mount independent of source mounts:

```python
self.assertIn("source: ./data", compose)
self.assertIn("target: /data", compose)
self.assertRegex(compose, r"target: /data\s+read_only: false")
```

Require CI smoke to create/chown a narrow data directory and bind it to `/data`, while retaining `--read-only`, non-root user, caps drop, and `no-new-privileges`.

- [ ] **Step 2: Run and verify RED**

```powershell
python -m unittest tests/installer/test_render_config.py -v
node --test tests/installer/workflow-contract.test.mjs
```

Expected: FAIL because no `/data` mount exists.

- [ ] **Step 3: Update image and Compose contracts**

In the runtime image, add `Authentication__DataPath=/data`, create `/data`, and chown it to `1000:1000` before `USER`. Keep the root filesystem read-only at runtime.

The release template adds this fixed mount before the source marker:

```yaml
      - type: bind
        source: ./data
        target: /data
        read_only: false
```

Local `compose.yaml` mounts named volume `reachcommander-data:/data` and declares it at document root. Do not bind a repository folder containing auth material.

- [ ] **Step 4: Update hardened container smoke**

Create `$smoke_root/data/auth` and `$smoke_root/data/keys`, chown them to `1000:1000`, and add:

```bash
--mount "type=bind,source=$smoke_root/data,target=/data" \
```

Keep health anonymous and do not parse or expose the generated setup code in publication logs beyond the application's normal first-run operator log.

- [ ] **Step 5: Run tests and commit**

Run renderer/workflow contracts again. If Docker is available, also run:

```powershell
docker compose config --quiet
docker build --tag reachcommander:auth-smoke .
```

Expected: contract tests PASS; Compose renders; image builds when Docker is available.

```powershell
git add Dockerfile compose.yaml deploy/compose.release.yaml tests/installer/test_render_config.py tests/installer/workflow-contract.test.mjs .github/workflows/ci.yml
git commit -m "feat: persist container authentication data"
```

---

### Task 10: Make installer lifecycle preserve, diagnose, back up, and reset authentication safely

**Files:**
- Modify: `deploy/install.sh`
- Modify: `deploy/reachcommander`
- Modify: `deploy/README.md`
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/test_package.sh`

**Interfaces:**
- Produces host directories `/opt/reachcommander/data/auth` and `/opt/reachcommander/data/keys`, owned by selected runtime UID/GID with mode `0700`.
- Uninstall offers `retain` or `backup` for validated authentication data; source directories remain excluded.

- [ ] **Step 1: Add failing installer and command tests**

Add cases proving:

- first install creates `data/auth` and `data/keys` with restrictive modes;
- reconfiguration preserves account/key fixture bytes;
- installer rejects symlinked/unexpected entries below `data`;
- generated Compose contains `/data` read/write but does not make a source writable;
- acknowledgement is exactly `I have HTTPS`, not `I have authenticated HTTPS`;
- `doctor` validates data ownership/read/write access for the runtime UID/GID;
- uninstall `retain` leaves only the inactive data tree and never touches sources;
- uninstall `backup` copies and byte-verifies account/bootstrap/key files before removing only the validated data tree;
- package assets contain no fixture credentials or generated auth state.

- [ ] **Step 2: Run and verify RED**

```powershell
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
```

Expected: FAIL on missing data lifecycle and old HTTPS acknowledgement.

- [ ] **Step 3: Create and validate application-owned data safely**

Extend `assert_generated_layout_safe` to require real, non-symlink directories at `data`, `data/auth`, and `data/keys`. Add `prepare_authentication_data` after staged files are installed and before Compose starts:

```bash
prepare_authentication_data() {
  local directory
  for directory in data data/auth data/keys; do
    [[ ! -L "$RC_INSTALL_ROOT/$directory" ]] || rc_die 'authentication data directories must not be symlinks'
    install -d -m 0700 -- "$RC_INSTALL_ROOT/$directory"
  done
  if (( EUID == 0 )); then
    chown -R -- "$RUNTIME_UID:$RUNTIME_GID" "$RC_INSTALL_ROOT/data"
  fi
}
```

Before the narrow recursive ownership change, validate with `find -xdev` that the tree contains only real directories and regular files under `auth`/`keys`, never symlinks, devices, sockets, or mount points. Reconfiguration does not include data in transaction replacement or cleanup; account/key bytes survive upgrades.

Change the prompt/output to `I have HTTPS` and explain that ReachCommander's own login protects the application while proxy authentication remains optional.

- [ ] **Step 4: Add doctor and uninstall data handling**

`doctor` checks: paths are real directories, no symlinks/special files exist, runtime UID/GID can traverse/read/write them, account/bootstrap are regular mode-0600 files when present, and keys follow `key-*.xml` regular-file naming. It reports missing `account.json` as setup mode, not a failure; malformed JSON is reported as a failure without printing contents.

Extend uninstall validation allowlists only for:

```text
data/
data/auth/account.json
data/auth/bootstrap.json
data/auth/auth.lock
data/keys/key-*.xml
```

Reject every symlink, special file, nested directory, or nonmatching filename before prompting. Ask whether to `retain` or `backup` authentication data, defaulting to retain. After confirmation, stop the application container before the final data-tree validation and backup so account/key files cannot change mid-copy. If validation or backup fails, restart the previous healthy service and preserve the deployment. Backup mode copies each validated regular file with mode `0600`, calls `sync -f`, verifies with `cmp`, and only then runs Compose teardown and removes those exact files plus empty auth/key/data directories. Retain mode tears down Compose, removes generated deployment files and the management command, leaves `/opt/reachcommander/data` intact, and prints its exact location. Never use `rm -rf` on the install root or a configured source.

- [ ] **Step 5: Run installer checks and ShellCheck**

```powershell
python -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh deploy/reachcommander deploy/lib/common.sh deploy/package-installer.sh tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/test_command.sh tests/installer/test_package.sh
```

Expected: every installer/package contract passes and ShellCheck reports no findings.

- [ ] **Step 6: Commit**

```powershell
git add deploy tests/installer
git commit -m "feat: manage persistent authentication data"
```

---

### Task 11: Update public security guidance and run the complete release gate

**Files:**
- Modify: `README.md`
- Modify: `SECURITY.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `docs/deployment/nginx.conf`
- Modify: `docs/deployment/Caddyfile`
- Modify: `docs/deployment/traefik.dynamic.yaml`
- Modify: `tests/installer/docs-contract.test.mjs`

**Interfaces:**
- Consumes: all preceding behavior.
- Produces: operator instructions matching built-in auth, setup/reset/backup, HTTPS, and optional proxy auth.

- [ ] **Step 1: Replace failing documentation contracts first**

Change contract assertions from `no built-in authentication` and mandatory `authenticated HTTPS reverse proxy` to required coverage of:

```text
built-in single-administrator authentication
first-run setup code
/opt/reachcommander/data/auth/account.json
/opt/reachcommander/data/keys
12-hour sliding session
HTTPS reverse proxy
optional proxy authentication
password change
account reset
retain
verified backup
```

Keep checks for loopback binding, secure PWA context, same origin, large upload settings, public GHCR, checksum verification, and never piping downloaded code into a shell.

- [ ] **Step 2: Run and verify RED**

```powershell
node --test tests/installer/docs-contract.test.mjs
```

Expected: FAIL because current public docs still claim ReachCommander has no built-in authentication.

- [ ] **Step 3: Update README, security policy, and deployment guide**

Document the exact flow:

1. Install/start behind HTTPS.
2. Read the active one-time code with `sudo reachcommander logs`.
3. Open the HTTPS URL and create the administrator.
4. Log in with the 12-hour sliding, non-Remember-Me session.
5. Change the password from the account menu.
6. Back up `account.json` and the Data Protection key ring together.
7. For emergency reset, stop the service, back up then remove only `data/auth/account.json`, restart, obtain the new code, and create the replacement administrator.

Warn that deleting only keys signs out sessions but retains the account, deleting the account requires restart/setup, malformed state must be preserved for recovery, and source data is never part of auth reset. State clearly that neither Docker image nor Compose contains the password.

Update the API list with all six auth endpoints. Move single-account authentication out of the roadmap; keep multi-user roles/per-source permissions deferred. Replace old security-boundary statements everywhere, including hardware telemetry guidance.

- [ ] **Step 4: Make proxy authentication explicitly optional**

Keep Nginx/Caddy/Traefik TLS and large-transfer examples. Label their Basic Auth blocks as optional defense in depth and show exactly which block/directives can be omitted when ReachCommander's login is the only authentication layer. Do not weaken HTTPS, upload limits, streaming/timeouts, same-origin proxying, or secure-cookie behavior.

- [ ] **Step 5: Run the complete verification gate**

Backend:

```powershell
dotnet test ReachCommander.slnx -c Release
```

Angular/PWA:

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
Pop-Location
```

Installer/docs:

```powershell
python -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh deploy/reachcommander deploy/lib/common.sh deploy/package-installer.sh tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/test_command.sh tests/installer/test_package.sh
```

Browser/publish:

```powershell
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/publish -p:BuildAngularOnPublish=false
Push-Location tests/e2e
npm test
Pop-Location
```

If Docker is available, build the real image and run the hardened smoke with a UID-owned `/data` bind. If Docker is unavailable, state that the CI `container-smoke` job is the mandatory remaining platform gate; do not claim it ran locally.

Expected: all locally available gates PASS with no credentials in tracked/generated artifacts and no API/auth cache entries.

- [ ] **Step 6: Perform final secret and regression scans**

```powershell
rg -n "no built-in authentication|I have authenticated HTTPS|Authentication__BootstrapCode|localStorage.*password|sessionStorage.*password|Bearer " README.md SECURITY.md docs/deployment deploy src client tests
git diff --check
git status --short
```

Expected: the first command has no obsolete boundary, bootstrap override, browser-password storage, or bearer-token implementation matches; `git diff --check` is clean; status contains only intended files.

- [ ] **Step 7: Commit the completed feature**

```powershell
git add README.md SECURITY.md docs/deployment tests/installer/docs-contract.test.mjs
git commit -m "docs: explain built-in authentication"
```

Record exact test totals and any unavailable Docker-only gate in the handoff. Do not push until the user explicitly asks.
