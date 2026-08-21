# ReachCommander Single-Administrator Authentication Design

**Date:** 2026-08-21

**Status:** Approved

**Target:** Same-origin Angular PWA and ASP.NET Core API on Windows development hosts and self-hosted Ubuntu containers

## Problem

ReachCommander currently serves its Angular application and file-management API without built-in authentication. The container installer compensates by requiring an authenticated HTTPS reverse proxy, but that makes a basic single-owner deployment harder to operate and leaves native development without an application-level sign-in boundary.

ReachCommander needs simple username-and-password authentication without a database. The browser must not retain credentials or bearer tokens, the public container image must not contain deployment credentials, and the first visitor to a newly exposed instance must not be able to claim the administrator account.

## Goals

- Support exactly one shared administrator account.
- Ask the owner to create the account on first run.
- Protect every file, archive, upload, rename, search, and hardware API on the server.
- Use an encrypted, HttpOnly session cookie suitable for the same-origin Angular PWA and ASP.NET Core API.
- Store only a salted password hash in a small local JSON record; never store a plaintext or reversibly encrypted password.
- Prevent an arbitrary first web visitor from claiming an unconfigured instance.
- Persist the account and cookie-encryption keys outside the Docker image and across container upgrades.
- Work during native Windows development and in the Ubuntu container deployment.
- Provide predictable account reset, password change, logout, failure, and recovery behavior.
- Preserve the PWA's existing rule that API responses and file data are never cached.

## Non-goals

- Multiple accounts, roles, permissions, groups, invitations, or per-source authorization.
- A database, external identity provider, OAuth, OpenID Connect, LDAP, Active Directory, passkeys, or multifactor authentication.
- Cross-origin API access or third-party API clients.
- JWT access or refresh tokens.
- Storing credentials in Angular local storage, session storage, IndexedDB, service-worker caches, environment variables, Compose manifests, or the container image.
- Providing TLS termination inside ReachCommander. Production deployments still require HTTPS at the reverse proxy.
- Making the anonymous PWA shell useful offline; protected file data always requires a live authenticated API connection.

## Selected approach

ASP.NET Core cookie authentication is the selected approach. Angular owns the setup and login experience, while ASP.NET Core remains the security boundary and authorizes every protected API request.

An encrypted HttpOnly cookie is preferable to a JWT for this same-origin application:

- the browser sends it automatically without exposing the credential to Angular code;
- ASP.NET Core provides established cookie authentication, ticket validation, expiration, and Data Protection integration;
- a JWT in local storage would expose a reusable bearer token to injected JavaScript;
- a JWT in an HttpOnly cookie would retain cookie and CSRF concerns while adding token issuance, rotation, and revocation complexity with no current multi-service benefit.

The application remains a modular monolith. No authentication service or database is added.

## Trust boundaries and authorization

The Angular application is an untrusted client. Hiding the commander UI is useful user experience but is not authorization. ASP.NET Core applies an authenticated-user policy to all application APIs except the minimum anonymous surface:

- authentication state/setup status;
- account setup;
- login;
- the static PWA shell and its public assets;
- a minimal `/health` liveness response.

Logout, password change, and every file-management or hardware operation require authentication. OpenAPI and detailed diagnostics are authenticated or available only in the Development environment.

All state-changing requests use ASP.NET Core antiforgery validation in addition to the strict same-site cookie. Angular obtains the antiforgery token through the same origin and sends it in a dedicated request header. The service accepts no cross-origin credentials and does not enable permissive CORS.

## Authentication state machine

On application startup, Angular calls `GET /api/auth/session`. The response describes exactly one of these states:

| State | Meaning | Angular behavior |
|---|---|---|
| `setupRequired` | No administrator account exists and setup is available | Show first-run account creation |
| `anonymous` | An account exists but the request has no valid session | Show login |
| `authenticated` | A valid administrator session exists | Initialize and show ReachCommander |

The commander shell, file stores, hardware polling, previews, and directory requests are not initialized before the authenticated state. If any protected request returns `401`, Angular clears file listings, selections, previews, operation state, and other in-memory data before returning to login.

The static shell may load while offline, but it shows a connection-required state. It never treats a locally remembered UI state as proof of authentication and never displays cached API or file data.

## First-run setup

When no account record exists, the server enters setup mode and creates a cryptographically random, one-time setup code. Only a hash of the active setup code is persisted. The plaintext code is shown in native-development console output or container logs so a person with host/operator access can claim the instance; it is not exposed by an anonymous API response.

The Angular setup form asks for:

- one-time setup code;
- username;
- password;
- password confirmation.

Usernames contain 3 to 64 normalized characters. Passwords contain 12 to 128 characters and are not silently truncated. The application permits password-manager generated values and does not impose composition rules that reduce usability.

The server verifies the setup code using constant-time comparison and rate-limits failed setup attempts. A successful setup operation:

1. validates the code and submitted fields;
2. creates a versioned, salted password hash with ASP.NET Core's password-hashing facilities;
3. generates a new random account security identifier;
4. atomically creates the single account record under an exclusive lock;
5. consumes the setup code;
6. signs in the new administrator with a fresh session cookie.

Only one concurrent setup request can succeed. Setup endpoints reject account creation after the account record exists.

If setup remains incomplete, a restart rotates the active setup code and writes the new code to the operator-visible console/log. Previously printed setup codes then fail.

## Login, cookie, and logout

Login accepts the username and password only over the same HTTPS origin in production. Authentication failures use one generic response so callers cannot distinguish an unknown username from a wrong password. Setup and login have separate, bounded rate-limit policies keyed conservatively by available request information and do not trust arbitrary forwarded headers unless the proxy is explicitly configured as trusted.

The session cookie is:

- encrypted and signed by ASP.NET Core Data Protection;
- `HttpOnly`;
- `Secure` in production;
- `SameSite=Strict`;
- scoped to the application;
- configured for a 12-hour sliding lifetime;
- non-persistent beyond the configured session behavior; there is no Remember Me option.

Native Windows development may relax `Secure` only for an explicitly configured localhost Development profile. Deployed Ubuntu/container configurations require HTTPS. ReachCommander fails safe rather than silently weakening production cookie settings.

Each authenticated request validates that the account still exists and that the session's security identifier matches the current account record. Consequently, deleting or replacing the account immediately makes old cookies unusable even if their cryptographic ticket has not expired.

Logout ends the current session and expires its cookie. It is available from the top toolbar beside the signed-in username, near the hardware-monitoring area.

## Password change

The authenticated account menu includes Change password. It requires the current password, a new password, and confirmation. On success, the server atomically replaces the password hash and rotates the account security identifier. All older sessions become invalid, and the current browser receives a newly issued cookie for the new identifier.

The username remains fixed in this first version. Resetting the account is the supported way to choose a different username.

## Persistent data layout

No credential is added to an image layer. Production authentication state lives on the Ubuntu host under:

```text
/opt/reachcommander/data/
├── auth/
│   ├── account.json
│   └── bootstrap.json       # exists only while setup is incomplete
└── keys/
    └── ...                  # ASP.NET Core Data Protection key ring
```

The deployment mounts this directory read/write at `/data` while retaining a read-only container root filesystem. The account record contains the normalized username, versioned salted password hash, random security identifier, and schema metadata. `bootstrap.json` contains only verifier material for the currently active one-time setup code. The Data Protection key ring preserves the ability to decrypt valid cookies across container replacement.

The installer creates the data tree for the dedicated container UID/GID using restrictive permissions: directories are owner-only and authentication files are owner-readable/writable only. It never places password values in environment variables, `.env`, Compose YAML, command-line arguments, image labels, or Git-tracked configuration.

Native Windows development defaults to `%LOCALAPPDATA%\ReachCommander\data`, outside the repository. A documented application setting can override the authentication data root for isolated tests and deliberate deployments; production startup validates that the resolved directory is usable.

## Reset and corruption behavior

Deleting only `auth/account.json` and restarting is the supported emergency account reset:

1. all existing sessions are rejected because no current account exists;
2. ReachCommander re-enters setup mode;
3. a new one-time setup code is generated and printed to the operator-visible log;
4. the next successful setup creates a new account and security identifier;
5. configured file sources and user files remain untouched.

An operator who can delete this host-owned file already controls the deployment's authentication state. ReachCommander nevertheless distinguishes intentional absence from corruption:

- a missing account file enters setup mode;
- a malformed, partially written, unreadable, or unsupported account file fails closed and reports an operator recovery error;
- a missing or unwritable data directory fails startup rather than using transient in-container credentials;
- deleting the Data Protection key ring invalidates all cookies but does not remove the account.

Account creation and password changes use file locking, temporary files in the same directory, flush/close, and atomic replacement so an interrupted write cannot silently become an unconfigured instance.

## Angular user experience

Setup and login appear as compact, responsive dark-themed screens consistent with ReachCommander's existing visual language. They provide:

- semantic labels and keyboard navigation;
- correct username/current-password/new-password autocomplete attributes;
- password-manager compatibility;
- password visibility controls that default to hidden;
- inline validation and accessible error announcements;
- disabled duplicate submission while a request is pending;
- generic credential errors and specific non-sensitive validation errors;
- a clear server-unavailable state.

After authentication, the top toolbar shows the username and Logout action without displacing core two-panel controls. Authentication state is held only in memory; the encrypted HttpOnly cookie is the sole browser credential. Angular does not store the password, session ticket, password hash, or setup code.

## PWA and cache behavior

The service worker may cache versioned static shell assets according to the existing PWA policy. It must not cache, queue, replay, or synthesize responses for:

- `/api/**`;
- authentication and antiforgery endpoints;
- file contents, previews, thumbnails, archives, searches, or hardware metrics.

Logging out or receiving `401` clears application memory and any non-sensitive transient UI state associated with protected responses. Browser HTTP responses for authentication endpoints and sensitive API data use appropriate no-store/private cache headers.

## Installer and container changes

The published-image Compose template gains only the narrow `/data` read/write mount; configured file sources retain their existing explicit read-only/read-write mounts. The container remains non-root, capability-free, `no-new-privileges`, and read-only outside approved temporary and data mounts.

The installer:

- creates and validates the persistent authentication data directory;
- preserves it during reconfiguration and image upgrades;
- prints instructions for obtaining the active first-run code from container logs;
- continues to bind to loopback by default;
- requires acknowledgement of an HTTPS reverse proxy, but no longer claims that proxy-level authentication is mandatory;
- warns that proxy authentication may still be used as an optional additional layer;
- offers to retain or create a protected backup of authentication data during uninstall rather than silently deleting it.

Authentication data is included in operational backup/restore documentation and treated as sensitive. Configured source directories are never included in, modified by, or deleted during an authentication reset.

This design supersedes the statements in the 2026-08-21 container distribution design that built-in authentication and persistent application data are absent. Its remaining isolation, publication, update, rollback, and source-safety decisions stay in force.

## Failure and logging behavior

- Passwords, cookies, antiforgery tokens, password hashes, and account security identifiers are never logged.
- The plaintext setup code is logged only while setup is incomplete and is invalidated when setup succeeds or a later startup rotates it.
- Authentication errors returned to anonymous callers do not reveal whether the account exists beyond the intentional `setupRequired` state.
- Rate-limit responses do not include sensitive details and use a bounded retry indication.
- Health checks disclose only basic availability, not usernames, paths, account state, hardware details, or authentication configuration.
- A storage, locking, hashing, Data Protection, or serialization failure rejects the operation and preserves the last valid account record.
- Corrupt authentication state produces an explicit operator-facing recovery message and never silently deletes or overwrites evidence.

## Testing strategy

### ASP.NET Core unit and integration tests

- Missing account produces `setupRequired`; an existing account produces `anonymous` without a valid cookie.
- Setup code generation uses a cryptographically secure source; only verifier material is persisted.
- Wrong, expired/rotated, reused, and rate-limited setup codes fail.
- Exactly one of multiple concurrent setup attempts succeeds.
- Account JSON contains no plaintext password or setup code.
- Valid and invalid logins behave correctly and return generic authentication failures.
- Session tickets carry the expected claims and respect the 12-hour sliding policy.
- Logout expires the current session.
- Password change requires the current password, rotates the security identifier, and invalidates other cookies.
- Account deletion, account recreation, and key-ring replacement invalidate the appropriate sessions.
- Missing account and malformed account files follow their distinct setup versus fail-closed paths.
- Atomic-write and injected I/O failure tests preserve the last valid record.
- Antiforgery validation rejects missing or invalid tokens on state changes.
- Anonymous requests receive `401` or the appropriate non-redirecting API response for every protected controller.
- Static assets and minimal health remain anonymous; diagnostics and OpenAPI follow their restricted policy.
- Authentication responses use the required cookie and cache headers.

### Angular tests

- Startup renders setup, login, authenticated, and server-unavailable states correctly.
- Commander services and hardware polling do not start before authentication.
- Setup, login, logout, and password-change forms validate and submit correctly.
- A protected-request `401` clears sensitive in-memory state and returns to login.
- No browser storage API receives credentials or session tokens.
- Forms satisfy keyboard, label, autocomplete, focus, and accessible-error contracts.

### PWA and browser acceptance tests

- API and authentication URLs remain excluded from service-worker caching.
- First-run setup, automatic sign-in, logout, login, password change, and re-login work in Chromium.
- Old sessions fail after password change and account recreation.
- Refresh and container restart preserve a valid session when account and key data remain intact.
- Offline startup never reveals previously returned file data.

### Installer and packaging tests

- Generated Compose mounts only the application data directory read/write in addition to explicitly writable sources.
- The image, installer bundle, `.env`, and generated Compose contain no username, password, hash, cookie, setup code, or Data Protection key.
- Installation creates expected UID/GID ownership and restrictive modes.
- Upgrade and reconfiguration preserve account and key data.
- Uninstall retention/backup choices do not alter configured source directories.
- Windows development uses an external data directory and test overrides do not leak into production defaults.

## Documentation changes

The root README and Ubuntu deployment guide remove the claim that ReachCommander lacks built-in authentication. They document:

- the first-run setup-code flow;
- the location and sensitivity of authentication data;
- login, logout, password change, account reset, and cookie lifetime;
- HTTPS as a production requirement;
- optional defense-in-depth proxy authentication;
- backup and restore of the account record and Data Protection keys together;
- the difference between deleting the account record and deleting the key ring;
- recovery from a malformed account record without touching user files.

Documentation never recommends embedding a password in Docker, Compose, environment variables, or command history.

## Acceptance criteria

The design is complete when all of the following are demonstrated:

1. A fresh Windows development run and a fresh Ubuntu container deployment require the operator-visible one-time setup code before an administrator can be created.
2. No file-management or hardware API can be used without a valid server-issued session.
3. The browser and persisted server files contain no plaintext or reversibly encrypted password.
4. The Docker image and deployment manifest are identical for every owner and contain no account credentials or key material.
5. Account and Data Protection state survive image upgrades through the narrow host-mounted data directory.
6. The encrypted HttpOnly cookie uses the approved production flags and 12-hour sliding lifetime.
7. Logout, password change, account deletion/recreation, and key loss invalidate sessions according to this design.
8. Missing account state enables setup, while corrupt or unreadable state fails closed.
9. Angular, ASP.NET Core, PWA, browser, installer, and packaging tests cover the agreed authentication boundary.
10. Existing file-source RO/RW rules, container hardening, archive behavior, PWA installation, and user data remain intact.
