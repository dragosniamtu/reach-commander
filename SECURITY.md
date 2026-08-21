# Security Policy

## Reporting a vulnerability

Please do not open a public issue containing exploit details, credentials, personal data, or filesystem contents. Contact the repository owner through the GitHub profile, or use GitHub's private vulnerability-reporting control when it is available for this repository.

Include the affected commit, deployment mode, reproduction steps, impact, and whether the issue exposes data outside a configured source. Do not include real private files; use a minimal temporary fixture.

## Deployment boundary

ReachCommander has built-in single-administrator authentication and an authenticated-by-default API, but it does not terminate TLS. Bind the application to `127.0.0.1` and publish it through an HTTPS reverse proxy. Optional proxy authentication is useful defense in depth, but it does not replace ReachCommander's own login or HTTPS.

The administrator password is never stored in the image, Compose model, browser storage, or configuration. The persisted record at `/opt/reachcommander/data/auth/account.json` contains a salted password hash and security stamp; `/opt/reachcommander/data/keys` contains the ASP.NET Core Data Protection key ring used for cookies. Both paths contain security-sensitive state. Back them up together, protect backup files as credentials, and use the Ubuntu guide's verified backup procedure.

First-run setup uses a random one-time code written to the server log. Login and setup are rate limited, mutations require antiforgery validation, and the non-persistent HttpOnly session cookie is `SameSite=Strict`, Secure in production, and renewed within a 12-hour sliding lifetime. A password change or account replacement invalidates older sessions.

For an account reset, stop ReachCommander, preserve and verify a backup, remove only `data/auth/account.json`, restart, and use the new setup code. Deleting only `/opt/reachcommander/data/keys` signs out sessions but does not reset the account. Preserve malformed state for investigation; never delete or modify configured source data as part of authentication recovery.

Enabling writes requires both `readOnly: false` for one explicit source and operating-system/container write permission for that same narrow root. Never mount `/`, a broad home directory, or `/var/run/docker.sock`.

## Supported version

Security fixes target the current `master` branch. Older commits and local modifications are not maintained as separate supported releases.
