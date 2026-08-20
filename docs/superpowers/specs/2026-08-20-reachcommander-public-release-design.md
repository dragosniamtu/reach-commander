# ReachCommander Public Portfolio Release Design

**Date:** 2026-08-20

**Status:** Approved for implementation

## Objective

Publish the existing ReachCommander `master` branch to the empty public repository at `https://github.com/dragosniamtu/reach-commander` as a polished, resume-ready engineering portfolio project. The release must explain the product quickly, demonstrate the interface visually, preserve its security boundaries, prove build quality through automated checks, and avoid publishing credentials, personal filesystem paths, generated artifacts, or private data.

## Scope

The public-release slice changes presentation, repository metadata, and continuous integration only. It does not change application behavior, API contracts, source defaults, Docker privileges, or write permissions.

The release adds:

- an MIT license with copyright attributed to Dragos Niamtu;
- committed overview and Multi-Rename screenshots under `docs/images/`;
- a recruiter-focused README opening with a concise product pitch, technology badges, screenshot gallery, engineering highlights, architecture, quick start, verification evidence, and explicit security limitations;
- a concise `SECURITY.md` explaining supported reporting and the trusted-network deployment boundary;
- a GitHub Actions workflow that restores, tests, builds, publishes, installs Chromium, and runs the deterministic Playwright suite;
- the GitHub repository as the local `origin`, followed by a direct push of `master` as already requested.

The release does not add contribution templates, issue templates, release automation, package publishing, hosted deployment, fabricated metrics, or marketing claims that cannot be verified from the repository.

## Public README Structure

The README keeps the existing operational depth but improves the first screen for reviewers:

1. Project name, one-sentence value proposition, and badges for license, .NET, Angular, Docker, and CI.
2. A committed 1440×900 overview screenshot displayed immediately below the introduction.
3. A short "Why this project" section focused on engineering depth: secure logical-path confinement, cross-platform hardware telemetry, authoritative batch mutation planning, streamed upload compensation, accessibility, and deterministic acceptance testing.
4. A compact feature table and technology stack.
5. The existing source configuration, Windows development, Ubuntu/Docker deployment, hardware monitoring, toolbar, Multi-Rename, upload, API, security, tests, and roadmap documentation.
6. A second committed screenshot showing the Total Commander-inspired Multi-Rename workspace near that feature's documentation.

Badges must use stable public URLs and must not claim CI success until the workflow exists. Test counts may state the freshly verified repository totals and should be phrased as current evidence rather than permanent guarantees.

## Screenshot Assets

The screenshots use the deterministic temporary E2E fixtures, never personal folders. The selected assets are:

- `docs/images/reachcommander-overview.png` — the 1440×900 dual-pane application with the active-panel toolbar, RO/RW source indicators, and hardware status visible;
- `docs/images/reachcommander-multi-rename.png` — the 1440×900 Multi-Rename workspace with complete New name preview and footer actions.

Images are PNG files captured from the production Angular build served by the real ASP.NET Core application. They contain only seeded names and no physical paths, usernames, hostnames, credentials, or machine-specific telemetry.

## Continuous Integration

One workflow at `.github/workflows/ci.yml` runs on pushes and pull requests targeting `master`. It uses Ubuntu, checks out the repository, installs .NET 10 and a supported Node 24 version, restores npm packages with `npm ci`, and executes:

1. `dotnet restore ReachCommander.slnx`;
2. `dotnet test ReachCommander.slnx -c Release --no-restore`;
3. Angular unit tests and production build;
4. `.NET` publish with the Angular build disabled because assets are already built;
5. E2E dependency and Chromium installation;
6. the full Playwright suite using its isolated temporary writable/read-only fixtures.

The workflow receives no repository secrets and does not deploy. Failed browser artifacts may be uploaded only when they are generated, with a short retention period.

## Security and Public-Safety Review

Before publication:

- inspect tracked filenames and contents for common secret formats, tokens, private keys, connection strings, credentials, and machine-specific absolute paths;
- inspect the complete Git history, not only the working tree, for credential-like content and sensitive paths;
- verify `.gitignore` excludes generated build, test, Playwright, local source, and environment artifacts;
- verify `config/sources.json` and `compose.yaml` remain read-only by default;
- verify screenshots contain only deterministic fixture data;
- verify API/client DTOs do not expose physical paths or staging names;
- document that ReachCommander has no built-in authentication or TLS and must remain on a trusted network or behind an authenticated HTTPS reverse proxy.

Git authentication uses the already configured Windows `wincred` helper through normal Git HTTPS operations. No credential is read, copied, logged, committed, or placed in a command argument.

## Publication Flow

The target repository is public, reachable, and currently empty. The local repository has no remote. Publication will:

1. keep the existing local branch name `master`;
2. add `origin` as `https://github.com/dragosniamtu/reach-commander.git`;
3. stage only the approved public-release files;
4. commit the release changes on `master`;
5. push `master` and set its upstream to `origin/master` using the configured credential helper;
6. verify the remote branch and public README after the push.

If authentication, branch protection, repository ownership, or network access blocks the push, stop without force-pushing, rewriting history, or extracting credentials.

## Acceptance Criteria

- The public repository contains the complete ReachCommander history on `master`.
- The GitHub landing page shows a useful project pitch and at least the overview screenshot without broken links.
- `LICENSE` contains the standard MIT text.
- `SECURITY.md` accurately describes the current no-auth trusted-network boundary.
- CI configuration matches locally verified commands and contains no secrets.
- Both screenshot files are tracked, readable PNG images made from deterministic fixtures, and contain no personal data.
- The full .NET, Angular, build, publish, and Playwright verification matrix passes before publication.
- Secret and path scans find no publish-blocking material in the working tree or Git history.
- Production sample sources and Compose mounts remain read-only.
- The local working tree is clean and tracks `origin/master` after publication.
