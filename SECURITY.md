# Security Policy

## Reporting a vulnerability

Please do not open a public issue containing exploit details, credentials, personal data, or filesystem contents. Contact the repository owner through the GitHub profile, or use GitHub's private vulnerability-reporting control when it is available for this repository.

Include the affected commit, deployment mode, reproduction steps, impact, and whether the issue exposes data outside a configured source. Do not include real private files; use a minimal temporary fixture.

## Deployment boundary

ReachCommander currently has no built-in authentication, authorization, or TLS. Run it only on a trusted network or behind an authenticated HTTPS reverse proxy. The checked-in source configuration and Compose mounts are read-only by default.

Enabling writes requires both `readOnly: false` for one explicit source and operating-system/container write permission for that same narrow root. Never mount `/`, a broad home directory, or `/var/run/docker.sock`.

## Supported version

Security fixes target the current `master` branch. Older commits and local modifications are not maintained as separate supported releases.
