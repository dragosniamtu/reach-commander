# ReachCommander Public Portfolio Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish ReachCommander as a polished, secure, resume-ready public GitHub repository with MIT licensing, committed screenshots, recruiter-focused documentation, and continuous integration.

**Architecture:** Keep application behavior and hardened deployment defaults unchanged. Add a thin public-repository layer—license, security policy, deterministic images, README presentation, and one GitHub Actions workflow—then verify the complete repository and push the existing `master` history through Git's configured Windows credential helper.

**Tech Stack:** Markdown, PNG, GitHub Actions, .NET 10, Angular 22, Node.js 24, Playwright/Chromium, Git HTTPS with Windows Credential Manager.

## Global Constraints

- Work directly on `master`; do not create a branch or Git worktree.
- Target only `https://github.com/dragosniamtu/reach-commander.git`; do not modify the credential-reference repository.
- Use the configured `wincred` Git helper normally; never read, print, copy, or place credentials in command arguments.
- Preserve all application code, API contracts, `config/sources.json`, `compose.yaml`, Docker privileges, and read-only production defaults.
- Screenshots must contain only deterministic E2E fixtures and no physical paths, usernames, hostnames, credentials, or host telemetry.
- Stage explicit approved paths only. Never use `git add .`, `git add -A`, or `git add --all`.
- Do not force-push or rewrite history. Stop on authentication, ownership, branch-protection, or remote-state conflicts.
- The repository remains intentionally self-hosted and unauthenticated; public documentation must require a trusted network or authenticated HTTPS reverse proxy.

## File Structure

```text
.github/workflows/ci.yml                     public CI verification only; no deployment or secrets
LICENSE                                      standard MIT license
SECURITY.md                                  vulnerability reporting and deployment boundary
README.md                                    recruiter-first landing page plus existing operator docs
docs/images/reachcommander-overview.png      deterministic 1440×900 application overview
docs/images/reachcommander-multi-rename.png  deterministic 1440×900 Multi-Rename workspace
```

---

### Task 1: Add the license, security policy, and deterministic screenshots

**Files:**

- Create: `LICENSE`
- Create: `SECURITY.md`
- Create: `docs/images/reachcommander-overview.png`
- Create: `docs/images/reachcommander-multi-rename.png`

**Interfaces:**

- Consumes: successful Playwright screenshots named `toolbar-1440.png` and `multi-rename-1440.png` beneath ignored `artifacts/playwright-results/`.
- Produces: stable public image paths consumed by `README.md` and the MIT/security files consumed by GitHub.

- [ ] **Step 1: Verify the source screenshots exist and contain only seeded data**

Run:

```powershell
$overview = Get-ChildItem artifacts/playwright-results -Recurse -File -Filter toolbar-1440.png | Select-Object -First 1
$rename = Get-ChildItem artifacts/playwright-results -Recurse -File -Filter multi-rename-1440.png | Select-Object -First 1
if (-not $overview -or -not $rename) { throw 'Run the active-toolbar Playwright visual test first.' }
$overview.FullName
$rename.FullName
```

Expected: both absolute paths resolve beneath this repository's ignored `artifacts/playwright-results` directory. Inspect both with the image viewer and reject them if they contain anything other than the temporary Downloads/Media/Archive fixture names.

- [ ] **Step 2: Create the stable image directory and copy the reviewed PNG files**

Run:

```powershell
New-Item -ItemType Directory -Path docs/images -Force | Out-Null
Copy-Item -LiteralPath $overview.FullName -Destination docs/images/reachcommander-overview.png
Copy-Item -LiteralPath $rename.FullName -Destination docs/images/reachcommander-multi-rename.png
```

Expected: both destination files exist, are non-empty PNG files, and `git status --short` reports only the new `docs/images/` assets.

- [ ] **Step 3: Add the standard MIT license**

Create `LICENSE` exactly as:

```text
MIT License

Copyright (c) 2026 Dragos Niamtu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 4: Add a security policy matching the current product boundary**

Create `SECURITY.md` exactly as:

```markdown
# Security Policy

## Reporting a vulnerability

Please do not open a public issue containing exploit details, credentials, personal data, or filesystem contents. Contact the repository owner through the GitHub profile, or use GitHub's private vulnerability-reporting control when it is available for this repository.

Include the affected commit, deployment mode, reproduction steps, impact, and whether the issue exposes data outside a configured source. Do not include real private files; use a minimal temporary fixture.

## Deployment boundary

ReachCommander currently has no built-in authentication, authorization, or TLS. Run it only on a trusted network or behind an authenticated HTTPS reverse proxy. The checked-in source configuration and Compose mounts are read-only by default.

Enabling writes requires both `readOnly: false` for one explicit source and operating-system/container write permission for that same narrow root. Never mount `/`, a broad home directory, or `/var/run/docker.sock`.

## Supported version

Security fixes target the current `master` branch. Older commits and local modifications are not maintained as separate supported releases.
```

- [ ] **Step 5: Validate and commit the public foundation**

Run:

```powershell
$pngSignature = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
foreach ($path in 'docs/images/reachcommander-overview.png','docs/images/reachcommander-multi-rename.png') {
  $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $path))
  if ($bytes.Length -lt 8) { throw "$path is not a PNG" }
  for ($index = 0; $index -lt 8; $index++) {
    if ($bytes[$index] -ne $pngSignature[$index]) { throw "$path is not a PNG" }
  }
}
rg -n "BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|github_pat_|gh[pousr]_[A-Za-z0-9_]{20,}" LICENSE SECURITY.md docs/images
git diff --check
git status --short
git add -- LICENSE SECURITY.md docs/images/reachcommander-overview.png docs/images/reachcommander-multi-rename.png
git commit -m "docs: add public project foundation"
```

Expected: signature validation and `git diff --check` exit 0; the secret scan returns no matches; the commit contains only the four named public assets.

---

### Task 2: Refocus the README for recruiters without losing operator detail

**Files:**

- Modify: `README.md`

**Interfaces:**

- Consumes: the stable images from Task 1 and the workflow path from Task 3.
- Produces: the GitHub landing page and badge/image links verified after publication.

- [ ] **Step 1: Record the expected broken-link baseline before editing**

Run:

```powershell
rg -n "docs/images/reachcommander-overview.png|docs/images/reachcommander-multi-rename.png|actions/workflows/ci.yml" README.md
```

Expected: no matches because the public landing-page references have not been added.

- [ ] **Step 2: Replace the README opening with the exact portfolio header**

Replace the title, introductory paragraph, and trusted-network warning at the top of `README.md` with:

```markdown
# ReachCommander

[![CI](https://github.com/dragosniamtu/reach-commander/actions/workflows/ci.yml/badge.svg)](https://github.com/dragosniamtu/reach-commander/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![Angular 22](https://img.shields.io/badge/Angular-22-DD0031)](https://angular.dev/)
[![Docker](https://img.shields.io/badge/Docker-ready-2496ED)](Dockerfile)

ReachCommander is a production-oriented, self-hosted dual-pane file manager inspired by Total Commander. It pairs an Angular 22 interface with an ASP.NET Core 10 backend to deliver authoritative batch rename, bounded streamed uploads, wildcard search, cross-platform hardware telemetry, and hardened filesystem confinement on Windows and Linux.

![ReachCommander dual-pane interface](docs/images/reachcommander-overview.png)

> **Security boundary:** ReachCommander has no built-in authentication, authorization, or TLS. Keep it on a trusted network or place it behind an authenticated HTTPS reverse proxy. Checked-in sources and Docker mounts remain read-only until an administrator explicitly opts one narrow source into writes.
```

- [ ] **Step 3: Add recruiter-facing engineering highlights before the existing feature inventory**

Insert before `## What ReachCommander includes`:

```markdown
## Why this project

ReachCommander demonstrates more than a file-browser UI:

- **Server-authoritative mutations:** previews are short-lived plans; execution revalidates paths, fingerprints, conflicts, source policy, and write access.
- **Safe batch algorithms:** two-phase temporary renames support swaps, cycles, and case-only changes with compensation and one-level Undo.
- **Streamed upload safety:** multipart files are bounded, staged beside their destination, committed all-or-nothing, and serialized with renames through a shared directory lock.
- **Cross-platform observability:** Windows and Linux collectors normalize CPU, memory, storage, GPU, temperature, fan, network, and uptime data without shelling out to vendor tools.
- **Testable accessibility:** keyboard-first pane control, focus trapping/restoration, live regions, explicit RO/RW semantics, and deterministic browser acceptance at desktop and compact widths.

| Layer | Technology |
|---|---|
| Frontend | Angular 22 standalone components, Signals, RxJS, Angular CDK A11y |
| Backend | ASP.NET Core 10, layered application/domain/infrastructure projects |
| Storage boundary | Configured local roots, canonical path confinement, symlink rejection |
| Deployment | Single-origin publish, hardened Docker Compose, Windows and Ubuntu support |
| Quality | 240 .NET tests, 136 Angular tests, 12 Playwright scenarios |
```

- [ ] **Step 4: Add the feature screenshot to the Multi-Rename section**

Immediately after the `## Multi-Rename` heading, insert:

```markdown
![ReachCommander Multi-Rename workspace](docs/images/reachcommander-multi-rename.png)
```

Keep every existing operational section after these additions. Do not remove source configuration, Windows/Ubuntu/Docker guidance, security limitations, API routes, tests, or roadmap information.

- [ ] **Step 5: Validate and commit the README**

Run:

```powershell
rg -n "^# ReachCommander$|^## Why this project$|reachcommander-overview.png|reachcommander-multi-rename.png|actions/workflows/ci.yml|240 .NET tests|136 Angular tests|12 Playwright" README.md
foreach ($path in 'docs/images/reachcommander-overview.png','docs/images/reachcommander-multi-rename.png','LICENSE') { if (-not (Test-Path $path)) { throw "Broken README target: $path" } }
git diff --check
git status --short
git add -- README.md
git commit -m "docs: polish portfolio readme"
```

Expected: every marker and target is present, `git diff --check` exits 0, and the commit modifies only `README.md`.

---

### Task 3: Add continuous integration for the complete verification matrix

**Files:**

- Create: `.github/workflows/ci.yml`

**Interfaces:**

- Consumes: `global.json`, both npm lockfiles, Angular production build, `.NET` solution/projects, and deterministic Playwright global setup.
- Produces: the `CI` workflow referenced by the README badge; it uses no repository secrets and performs no deployment.

- [ ] **Step 1: Confirm no CI workflow currently exists**

Run:

```powershell
Test-Path .github/workflows/ci.yml
```

Expected: `False`.

- [ ] **Step 2: Create the exact least-privilege workflow**

Create `.github/workflows/ci.yml` as:

```yaml
name: CI

on:
  push:
    branches: [master]
  pull_request:
    branches: [master]

permissions:
  contents: read

concurrency:
  group: ci-${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  verify:
    runs-on: ubuntu-latest
    timeout-minutes: 30

    steps:
      - name: Check out repository
        uses: actions/checkout@v6

      - name: Set up .NET
        uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json

      - name: Set up Node.js
        uses: actions/setup-node@v7
        with:
          node-version: 24
          cache: npm
          cache-dependency-path: |
            client/reach-commander-ui/package-lock.json
            tests/e2e/package-lock.json

      - name: Restore .NET dependencies
        run: dotnet restore ReachCommander.slnx

      - name: Install Angular dependencies
        run: npm ci
        working-directory: client/reach-commander-ui

      - name: Install E2E dependencies
        run: npm ci
        working-directory: tests/e2e

      - name: Test .NET
        run: dotnet test ReachCommander.slnx -c Release --no-restore

      - name: Test Angular
        run: npm test -- --watch=false
        working-directory: client/reach-commander-ui

      - name: Build Angular
        run: npm run build
        working-directory: client/reach-commander-ui

      - name: Publish API
        run: dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/publish -p:BuildAngularOnPublish=false

      - name: Install Chromium
        run: npx playwright install --with-deps chromium
        working-directory: tests/e2e

      - name: Run browser acceptance
        run: npm test
        working-directory: tests/e2e

      - name: Upload browser diagnostics
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: playwright-diagnostics
          path: |
            artifacts/playwright-results
            artifacts/playwright-report
          if-no-files-found: ignore
          retention-days: 7
```

- [ ] **Step 3: Validate YAML syntax and workflow invariants locally**

Run with the bundled supported Node runtime:

```powershell
$node = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
& $node 'client\reach-commander-ui\node_modules\prettier\bin\prettier.cjs' --check '.github\workflows\ci.yml'
rg -n "permissions:|contents: read|actions/checkout@v6|actions/setup-dotnet@v5|actions/setup-node@v7|actions/upload-artifact@v4|working-directory: tests/e2e|with-deps chromium" .github/workflows/ci.yml
rg -n "secrets\.|environment:|deploy|pull_request_target|permissions:\s*write" .github/workflows/ci.yml
```

Expected: Prettier reports the file is formatted; every required invariant is found; the final unsafe/deployment scan returns no matches.

- [ ] **Step 4: Commit the workflow**

Run:

```powershell
git diff --check
git status --short
git add -- .github/workflows/ci.yml
git commit -m "ci: verify public project"
```

Expected: the commit creates only `.github/workflows/ci.yml`.

---

### Task 4: Run the public-safety and complete release verification gates

**Files:**

- Verify: all tracked files and Git history
- Verify: `config/sources.json`
- Verify: `compose.yaml`
- Verify: both committed screenshots

**Interfaces:**

- Consumes: Tasks 1–3 and the complete existing application/test suite.
- Produces: evidence required before adding the remote or publishing publicly.

- [ ] **Step 1: Scan the current tree for credential material and personal paths**

Run:

```powershell
rg -n -i --hidden -g '!.git/**' -g '!artifacts/**' -g '!**/node_modules/**' "BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|password\s*[:=]\s*[^\s]+|client_secret\s*[:=]" .
rg -n -i --hidden -g '!.git/**' -g '!artifacts/**' -g '!**/node_modules/**' -g '!docs/superpowers/plans/2026-08-20-reachcommander-public-release.md' "[A-Za-z]:\\\\(Users|Work)\\\\|/home/[^/]+/|AppData[/\\\\]" .
```

Expected: no credential-value matches. Any word-only documentation matches must be reviewed; no machine-specific absolute path may remain in tracked public content.

- [ ] **Step 2: Scan complete Git history without checking out old revisions**

Run:

```powershell
git log --all -p -- . ':!*.lock' | rg -n -i "BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|password\s*[:=]\s*[^\s]+|client_secret\s*[:=]"
git log --all -p -- . ':!*.lock' ':!docs/superpowers/plans/2026-08-20-reachcommander-public-release.md' | rg -n -i "[A-Za-z]:\\\\(Users|Work)\\\\|/home/[^/]+/|AppData[/\\\\]"
```

Expected: no credential values or developer-machine absolute paths. If found, stop before publication and report the exact commit/path without printing secret values.

- [ ] **Step 3: Reconfirm hardened defaults and non-leaking contracts**

Run:

```powershell
rg -n '"readOnly": false' config/sources.json
rg -n '/sources/(downloads|media):rw|docker.sock|source: /$|target: /$' compose.yaml
rg -n "physicalPath|rootPath|\.partial" src/ReachCommander.Api/Contracts src/ReachCommander.Api/Controllers client/reach-commander-ui/src/app/core/api
```

Expected: all three commands return no matches; the checked-in public defaults are read-only and safe DTOs contain no physical/staging paths.

- [ ] **Step 4: Run the fresh complete verification matrix**

Run:

```powershell
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx -c Release --no-restore
Push-Location client/reach-commander-ui
$node = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
& $node '.\node_modules\@angular\cli\bin\ng.js' test --watch=false
& $node '.\node_modules\@angular\cli\bin\ng.js' build
Pop-Location
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/publish -p:BuildAngularOnPublish=false
Push-Location tests/e2e
& $node '.\node_modules\@playwright\test\cli.js' test --workers=1
Pop-Location
```

Expected: 207 unit plus 33 integration tests pass, all 136 Angular tests pass, the production Angular build and `.NET` publish exit 0, and all 12 Chromium acceptance scenarios pass.

- [ ] **Step 5: Verify repository integrity before publication**

Run:

```powershell
git diff --check
git status --short
git log -5 --oneline --decorate
git remote -v
```

Expected: working tree clean, the public-release commits are present on `master`, and no remote exists yet.

---

### Task 5: Publish `master` and verify the public repository

**Files:**

- Modify Git metadata only: add remote `origin`
- External write: push `master` to `https://github.com/dragosniamtu/reach-commander.git`

**Interfaces:**

- Consumes: a clean, fully verified local `master` and Git's configured Windows `wincred` helper.
- Produces: `origin/master` containing the complete local history and a local `master` tracking that remote branch.

- [ ] **Step 1: Reconfirm the target remains empty and the local repository has no remote**

Run:

```powershell
git ls-remote https://github.com/dragosniamtu/reach-commander.git
git remote -v
git status --short --branch
```

Expected: `ls-remote` and `git remote -v` print no refs/remotes; local output is clean on `master`.

- [ ] **Step 2: Add the exact HTTPS remote**

Run:

```powershell
git remote add origin https://github.com/dragosniamtu/reach-commander.git
git remote get-url --all origin
```

Expected: the only URL is `https://github.com/dragosniamtu/reach-commander.git`.

- [ ] **Step 3: Push without exposing or manually retrieving credentials**

Run:

```powershell
git push -u origin master
```

Expected: Git uses `wincred`, creates `origin/master`, and reports that local `master` now tracks it. On failure, stop; do not force-push, retry with tokens in arguments, or inspect the credential store.

- [ ] **Step 4: Verify local/remote commit identity and public assets**

Run:

```powershell
$local = git rev-parse HEAD
$remote = git ls-remote origin refs/heads/master | ForEach-Object { ($_ -split '\s+')[0] }
if ($local -ne $remote) { throw "Remote master does not match local HEAD." }
git status --short --branch
git remote -v
```

Expected: commit hashes match, the tree is clean, and status reports `master...origin/master` without ahead/behind counts.

Open and verify these public URLs:

```text
https://github.com/dragosniamtu/reach-commander
https://github.com/dragosniamtu/reach-commander/blob/master/docs/images/reachcommander-overview.png
https://github.com/dragosniamtu/reach-commander/actions/workflows/ci.yml
```

Expected: the README renders with badges and both screenshots, the MIT license and security policy are visible, and the CI workflow is recognized by GitHub. Report the workflow as queued/running unless GitHub has already completed it; do not claim CI success without observing a successful run.
