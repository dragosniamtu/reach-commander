# ReachCommander Windows Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a checksum-verified, one-command, per-user Windows installer for ReachCommander that uses Docker Desktop, supports whole eligible drives or specific folders, and safely manages the complete deployment lifecycle.

**Architecture:** Add a Windows PowerShell 5.1-compatible adapter around the existing published Linux container and `deploy/compose.release.yaml`. Keep path/source validation, rendering, and lifecycle transactions in focused PowerShell modules; keep `install.ps1` as the interactive and command-line entry point; publish a deterministic ZIP with its own SHA-256 asset. Windows state lives only under `%LOCALAPPDATA%\ReachCommander`, while destructive account backups live in the adjacent external backup root.

**Tech Stack:** Windows PowerShell 5.1, PowerShell 7, Docker Desktop with Compose v2, GitHub Actions, Node.js contract tests

## Global Constraints

- Execute directly on `master`; do not create a branch, worktree, or subagent.
- Preserve the unrelated untracked `NC-theme.png`; never stage or modify it.
- Do not install, start, stop, or reconfigure Docker Desktop.
- Do not request Administrator elevation or modify execution policy persistently, ACLs outside installer-owned paths, `PATH`, registry, profiles, services, shortcuts, or Windows Firewall.
- Keep production syntax compatible with Windows PowerShell 5.1 and test production scripts under both Windows PowerShell 5.1 and PowerShell 7.
- Use explicit test roots and fakes; tests must never write to the real `%LOCALAPPDATA%` or invoke a real Docker daemon.
- Keep source paths outside every recursive mutation, rollback, backup-cleanup, and uninstall allowlist.
- Preserve `data\auth` and `data\keys` byte-for-byte across install reruns, reconfiguration, repair, and update.
- Use test-driven development for every behavior change: write the failing contract, observe the expected failure, implement the smallest complete behavior, then rerun the focused and surrounding suites.
- Commit after each task using only the files named by that task.

---

## Task 1: Establish the PowerShell test harness and safe path primitives

**Files:**

- Create: `tests/installer/windows/TestHarness.ps1`
- Create: `tests/installer/windows/Common.Tests.ps1`
- Create: `tests/installer/windows/Parse.Tests.ps1`
- Create: `deploy/windows/Installer.Common.psm1`

**Interfaces:**

```powershell
Get-RcPaths -TestRoot <string?> -> hashtable
Resolve-RcCanonicalDirectory -Path <string> -> string
Get-RcPathRelationship -Left <string> -Right <string> -> Same|Ancestor|Descendant|Disjoint
Assert-RcSafeOwnedPath -Path <string> -AllowedRoot <string> -> void
ConvertTo-RcSourceId -Name <string> -> string
ConvertTo-RcPort -Value <string> -> int
```

- [ ] **Step 1: Add a dependency-free TAP-style harness**

Create assertions that throw on failure and print `ok` records on success:

```powershell
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:RcTestNumber = 0

function Assert-Equal {
    param([object]$Expected, [object]$Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        throw "$Message`nexpected: $Expected`nactual:   $Actual"
    }
    $script:RcTestNumber += 1
    Write-Output "ok $script:RcTestNumber - $Message"
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:RcTestNumber += 1
        Write-Output "ok $script:RcTestNumber - $Message"
        return
    }
    throw "$Message did not throw"
}
```

- [ ] **Step 2: Write failing common-path contracts**

Cover:

```powershell
$paths = Get-RcPaths -TestRoot (Join-Path $env:TEMP 'rc-contract-root')
Assert-Equal (Join-Path $paths.LocalAppData 'ReachCommander') $paths.InstallRoot 'test root is honored'
Assert-Equal 'family-media' (ConvertTo-RcSourceId "Family Media") 'source ID is normalized'
Assert-Equal 8092 (ConvertTo-RcPort '8092') 'unprivileged port is accepted'
Assert-Throws { ConvertTo-RcPort '80' } '1024' 'privileged port is rejected'
Assert-Throws { ConvertTo-RcPort '65536' } '65535' 'out-of-range port is rejected'
Assert-Equal 'Ancestor' (Get-RcPathRelationship -Left 'C:\Media' -Right 'c:\media\Films') 'comparison is ordinal case-insensitive'
```

Also create a junction inside the test tree and assert that `Assert-RcSafeOwnedPath` rejects every reparse point in the owned path chain. Skip only the junction creation assertion when the runner cannot create a junction, and print an explicit TAP skip reason.

- [ ] **Step 3: Run the tests and observe the missing-module failure**

Run in Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
```

Expected: non-zero exit because `deploy/windows/Installer.Common.psm1` or its exported functions do not exist.

- [ ] **Step 4: Implement safe path primitives**

Use `[System.IO.Path]::GetFullPath`, `DirectoryInfo.FullName`, and ordinal case-insensitive comparison. `Get-RcPaths` must derive production paths from `LOCALAPPDATA`, but use only `TestRoot` when supplied:

```powershell
function Get-RcPaths {
    [CmdletBinding()]
    param([string]$TestRoot)
    $local = if ($TestRoot) { [IO.Path]::GetFullPath($TestRoot) } else { $env:LOCALAPPDATA }
    if (-not [IO.Path]::IsPathRooted($local)) { throw 'LOCALAPPDATA must be an absolute local path' }
    $install = Join-Path $local 'ReachCommander'
    @{
        LocalAppData = $local
        InstallRoot = $install
        ExternalBackupRoot = Join-Path $local 'ReachCommander Backups'
        LockPath = Join-Path $local 'ReachCommander.install.lock'
    }
}
```

Reject empty names, IDs longer than 64 characters, device/UNC paths for installer state, invalid ports, and owned paths whose existing chain contains `ReparsePoint`. Export only the six declared functions.

- [ ] **Step 5: Add parser and AST safety checks**

`Parse.Tests.ps1` must parse every `deploy/windows/*.ps1` and `*.psm1` with `[System.Management.Automation.Language.Parser]::ParseFile`, fail on parse errors, and walk command AST nodes to reject `Start-Process -Verb RunAs`, `Set-ExecutionPolicy`, `Set-Acl` outside the ACL helper, `netsh`, `sc.exe`, and registry providers.

- [ ] **Step 6: Run focused tests in both shells**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Parse.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Common.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Parse.Tests.ps1
```

Expected: all records are `ok`, with zero exit codes.

- [ ] **Step 7: Commit**

```bash
git add deploy/windows/Installer.Common.psm1 tests/installer/windows/TestHarness.ps1 tests/installer/windows/Common.Tests.ps1 tests/installer/windows/Parse.Tests.ps1
git commit -m "test: establish Windows installer contracts"
```

## Task 2: Implement source discovery and access policy

**Files:**

- Modify: `deploy/windows/Installer.Common.psm1`
- Modify: `tests/installer/windows/Common.Tests.ps1`
- Create: `tests/installer/windows/fixtures/drives.json`

**Interfaces:**

```powershell
Get-RcEligibleDrive -InstallRoot <string> -SystemDrive <string> -DriveProvider <scriptblock?> -> PSCustomObject[]
Assert-RcSourcePath -Path <string> -InstallRoot <string> -ExternalBackupRoot <string> -> string
New-RcSource -Name <string> -Path <string> -Access ro|rw -Existing <object[]> -Broad <bool> -Confirmation <string?> -OwnedPaths <string[]> -> PSCustomObject
Test-RcBroadSource -Path <string> -Mode Whole|Specific -> bool
Get-RcOwnedPathExclusion -SourcePath <string> -OwnedPaths <string[]> -> PSCustomObject[]
```

- [ ] **Step 1: Add failing drive-filtering tests**

Load fixture records with `Root`, `DriveType`, `Provider`, `IsReady`, and `Label`. Assert that discovery includes only ready fixed/removable filesystem drives and excludes:

- the system-drive root;
- optical, network, disconnected, and non-filesystem drives;
- roots matching Docker Desktop or WSL internal paths;
- roots that are the same as, ancestors of, or descendants of installer-owned state.

Assert stable sorting by canonical root and no parent such as `C:\` is synthesized.

- [ ] **Step 2: Add failing source-policy tests**

Cover duplicate paths, nested sources, unsafe reparse points, installer/backup relationships, and access policy normalization. A source that is the same as or inside an owned path is rejected. An approved broad source that is an ancestor of an owned path records a nested exclusion; it is accepted only when every relative exclusion target can be represented safely. The broad read-write confirmation must be the exact canonical path:

```powershell
$source = New-RcSource -Name 'Media' -Path 'D:\' -Access rw -Existing @() -Broad $true -Confirmation 'D:\' -OwnedPaths @()
Assert-Equal 'rw' $source.Access 'exact canonical path permits broad write access'
Assert-Throws {
    New-RcSource -Name 'Media' -Path 'D:\' -Access rw -Existing @() -Broad $true -Confirmation 'D:' -OwnedPaths @()
} 'exact canonical path' 'abbreviated confirmation is rejected'
```

- [ ] **Step 3: Run and observe missing interface failures**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
```

Expected: non-zero exit identifying `Get-RcEligibleDrive` first.

- [ ] **Step 4: Implement injectable drive discovery and source validation**

Production discovery uses `Get-CimInstance Win32_LogicalDisk`; tests pass a scriptblock that returns fixture records. Do not use `Get-PSDrive` as the authority for physical drive type. Canonicalize first, then compare with `[StringComparer]::OrdinalIgnoreCase`. Reject nested source pairs in either direction so a broad mount cannot shadow a narrow mount.

`New-RcSource` must return exactly:

```powershell
[pscustomobject]@{
    Id = ConvertTo-RcSourceId $Name
    Name = $Name
    HostPath = $canonical
    Access = $Access.ToLowerInvariant()
    DefaultLeft = $false
    DefaultRight = $false
    Exclusions = @(Get-RcOwnedPathExclusion -SourcePath $canonical -OwnedPaths $OwnedPaths)
}
```

The caller sets exactly one left and one right default after collection. Exclusions remain installer-only in-memory metadata and are never serialized into `config\sources.json` or `state\source-mounts.json`.

- [ ] **Step 5: Run both-shell contracts**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Common.Tests.ps1
```

Expected: all source and drive contracts pass.

- [ ] **Step 6: Commit**

```bash
git add deploy/windows/Installer.Common.psm1 tests/installer/windows/Common.Tests.ps1 tests/installer/windows/fixtures/drives.json
git commit -m "feat: validate Windows installer sources"
```

## Task 3: Render the Windows deployment and verify Docker access

**Files:**

- Create: `deploy/windows/Installer.Rendering.psm1`
- Create: `tests/installer/windows/Rendering.Tests.ps1`
- Create: `tests/installer/windows/fake-bin/docker.cmd`
- Modify: `tests/installer/windows/Parse.Tests.ps1`

**Interfaces:**

```powershell
Write-RcDeployment -StageRoot <string> -TemplatePath <string> -Image <digest-ref> -BindAddress <ip> -Port <int> -Sources <object[]> -> void
Invoke-RcDockerPreflight -StageRoot <string> -Sources <object[]> -DockerCommand <string?> -> void
Test-RcImageReference -Image <string> -> bool
Resolve-RcImageDigest -Channel stable|edge|vX.Y.Z -DockerCommand <string?> -> string
```

- [ ] **Step 1: Write failing serialization contracts**

Use source names and paths containing spaces, apostrophes, Unicode, `#`, `$`, and quotes. Assert:

- `.env` contains `127.0.0.1`, the chosen port, fixed `1000:1000`, and a digest-pinned image;
- `compose.yaml` replaces exactly one `# installer-source-mounts` marker with long-syntax bind mounts;
- YAML single quotes are escaped by doubling apostrophes;
- `config/sources.json` has only `id`, `name`, `/sources/<id>`, `enabled`, `readOnly`, `defaultLeft`, `defaultRight`;
- `state/source-mounts.json` has only `id`, canonical `hostPath`, and `access`;
- an approved broad source that contains installer state receives a nested bind mount from an installer-owned empty mask directory over each owned descendant beneath `/sources/<id>`;
- the system-drive root remains unavailable in whole-drive mode, and an exclusion that cannot be safely expressed causes specific-folder validation to fail;
- JSON is UTF-8 without a byte-order mark and ends with one newline;
- duplicate IDs/paths and nested sources fail before any output is committed.

Use the existing Python renderer output as a golden behavioral contract, not as a Windows runtime dependency.

- [ ] **Step 2: Write failing Docker preflight contracts**

The fake Docker command records arguments and returns scripted exit codes. Assert calls for:

```text
docker version
docker compose version
docker pull ghcr.io/dragosniamtu/reach-commander:<resolved-channel-tag>
docker image inspect --format <RepoDigests format> ghcr.io/dragosniamtu/reach-commander:<resolved-channel-tag>
docker image inspect <digest>
docker run --rm --user 1000:1000 ... <digest> sh -c <access checks>
docker compose --project-directory <stage> --env-file <stage>\.env -f <stage>\compose.yaml config
```

Assert that RO checks never write, RW checks create and remove a unique canary only inside the selected source, and failure names the affected host path without changing its ACL.

- [ ] **Step 3: Run and observe the missing renderer failure**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Rendering.Tests.ps1
```

Expected: non-zero exit because `Installer.Rendering.psm1` is absent.

- [ ] **Step 4: Implement deterministic rendering**

Write files to a fresh staging directory. Use `System.Text.UTF8Encoding($false)`, `ConvertTo-Json -Depth 8`, and atomic same-directory temporary files. Render YAML scalars with:

```powershell
function ConvertTo-RcYamlScalar([string]$Value) {
    "'" + $Value.Replace("'", "''") + "'"
}
```

Reject non-digest images with this anchored form:

```regex
^ghcr\.io/dragosniamtu/reach-commander@sha256:[a-f0-9]{64}$
```

Give `data`, `data\auth`, `data\keys`, `config`, `state`, `backups`, `state\masks`, and `bin` explicit creation functions; do not create content beneath configured sources. For every `Exclusions` record, render the source bind first and then a long-syntax read-only bind from its corresponding empty `state\masks` directory to the descendant container target. Docker Compose configuration and the real access preflight must succeed with every mask present.

- [ ] **Step 5: Implement Docker verification without daemon mutation**

Do not start Docker Desktop. Validate `stable`, `edge`, or stable `vX.Y.Z`, pull that exact channel tag, accept only a matching repository digest returned by inspection, and use the immutable digest for all generated configuration. Fail with actionable messages when the CLI, daemon, Compose v2, pull/inspection, Compose validation, or effective access check fails. Run every external command as an argument array and test `$LASTEXITCODE`; never build a command string for `Invoke-Expression`.

- [ ] **Step 6: Run rendering, parser, and common tests in both shells**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Rendering.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Parse.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Rendering.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Parse.Tests.ps1
```

Expected: zero exits; fake Docker log contains only the allowlisted commands.

- [ ] **Step 7: Commit**

```bash
git add deploy/windows/Installer.Rendering.psm1 tests/installer/windows/Rendering.Tests.ps1 tests/installer/windows/fake-bin/docker.cmd tests/installer/windows/Parse.Tests.ps1
git commit -m "feat: render and verify Windows deployments"
```

## Task 4: Build transactional first installation

**Files:**

- Create: `deploy/windows/Installer.Lifecycle.psm1`
- Create: `deploy/windows/install.ps1`
- Create: `tests/installer/windows/Lifecycle.Tests.ps1`
- Modify: `tests/installer/windows/fake-bin/docker.cmd`
- Modify: `tests/installer/windows/Parse.Tests.ps1`

**Interfaces:**

```powershell
Enter-RcInstallLock -LockPath <string> -> FileStream
Install-RcDeployment -Paths <hashtable> -BundleRoot <string> -Request <object> -> void
Invoke-RcCompose -InstallRoot <string> -Arguments <string[]> -> void
Wait-RcHealthy -InstallRoot <string> -TimeoutSeconds <int> -> void
Set-RcOwnedAcl -Path <string> -> void
install.ps1 [-Action Install|Menu|Status|Logs|Start|Stop|Restart|Update|Reconfigure|Repair|Uninstall] [-TestRoot <string>]
```

- [ ] **Step 1: Write failing first-install transaction tests**

Test a first install under a temporary `LOCALAPPDATA` substitute. Assert:

- a second lock holder fails immediately;
- staging is adjacent to, but not inside, the live root;
- bundle files are copied to `bin` and the shared template is not left as a runtime input;
- final state contains `.env`, `compose.yaml`, `config\sources.json`, `state\source-mounts.json`, `data\auth`, `data\keys`, `backups`, and `bin\install.ps1`;
- the root ACL inheritance is disabled and only the current user and `SYSTEM` retain full control;
- Compose starts only after rendering and preflight pass;
- health success commits the staged generation and removes transaction residue;
- injected failure before start leaves no partial live deployment;
- injected failure after start tears down the candidate and restores the previous state;
- canary files in every selected source are unchanged.

- [ ] **Step 2: Add failing interactive-flow tests**

Pipe scripted answers into `install.ps1 -Action Install -TestRoot ...` and cover:

1. source mode `Whole drives` and `Specific folders`;
2. default RO and explicit RW;
3. exact canonical broad-RW confirmation;
4. loopback default and explicit LAN binding;
5. occupied-port retry through an injectable listener probe;
6. exactly one left and one right default;
7. first-run URL, account-creation guidance, and the exact installed command that shows the one-time setup code.

- [ ] **Step 3: Run and observe the missing lifecycle failure**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Lifecycle.Tests.ps1
```

Expected: non-zero exit identifying the absent lifecycle module.

- [ ] **Step 4: Implement lock, ACL, and stage/commit boundaries**

Hold an exclusive `FileStream` for the complete mutation. `Set-RcOwnedAcl` must operate only after `Assert-RcSafeOwnedPath`, disable inheritance, and grant `FullControl` to current-user SID and `S-1-5-18`. Stage generated configuration outside the live root, preserve existing auth/key directories by never copying them into generated staging, and move only allowlisted generated paths.

Record `state\transaction.json` with exact keys:

```json
{
  "operation": "install",
  "phase": "staged",
  "previousImage": null,
  "candidateImage": "ghcr.io/dragosniamtu/reach-commander@sha256:...",
  "startedUtc": "2026-08-25T12:00:00Z"
}
```

Update phase atomically at `validated`, `stopped`, `committed`, and `healthy`. Recovery dispatches only these known phases and rejects unknown fields.

- [ ] **Step 5: Implement the entry point and first install**

Use `[CmdletBinding()] param(...)`, import modules relative to `$PSScriptRoot`, and reject elevation rather than requiring it. The fixed runtime identity is `1000:1000`. Resolve a tag to a digest with Docker inspection before writing `.env`. Print Docker Desktop prerequisites and stop if the daemon is unavailable.

For LAN mode, bind `0.0.0.0`, print a best-effort private-interface URL, warn that no TLS/firewall change was made, and keep loopback as the default.

- [ ] **Step 6: Run both-shell lifecycle suites**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Lifecycle.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Lifecycle.Tests.ps1
```

Expected: all transaction cases pass; test roots contain no active lock or staging directory.

- [ ] **Step 7: Commit**

```bash
git add deploy/windows/Installer.Lifecycle.psm1 deploy/windows/install.ps1 tests/installer/windows/Lifecycle.Tests.ps1 tests/installer/windows/fake-bin/docker.cmd tests/installer/windows/Parse.Tests.ps1
git commit -m "feat: install ReachCommander on Windows"
```

## Task 5: Complete lifecycle, rollback, and uninstall safety

**Files:**

- Modify: `deploy/windows/Installer.Lifecycle.psm1`
- Modify: `deploy/windows/install.ps1`
- Modify: `tests/installer/windows/Lifecycle.Tests.ps1`

**Interfaces:**

```powershell
Get-RcStatus -Paths <hashtable> -> PSCustomObject
Update-RcDeployment -Paths <hashtable> -Image <string> -> void
Reconfigure-RcDeployment -Paths <hashtable> -Request <object> -> void
Repair-RcDeployment -Paths <hashtable> -> void
Uninstall-RcDeployment -Paths <hashtable> -RemoveAuthentication <bool> -Confirmation <string?> -> void
Backup-RcAuthentication -Paths <hashtable> -Destination <string> -> string
```

- [ ] **Step 1: Extend failing lifecycle contracts**

Cover status, logs, start, stop, restart, no-op rerun, reconfiguration, repair, digest update, forced unhealthy rollback, interrupted-transaction recovery, and uninstall. Assert:

- routine actions translate to bounded `docker compose` arguments;
- a new digest is pulled/inspected/preflighted before stopping the healthy deployment;
- unhealthy update restores the prior digest and generated files, then restarts it;
- repair regenerates only allowlisted deployment state;
- auth and key hashes stay identical in every non-destructive operation;
- default uninstall retains auth/keys in an inactive install root;
- destructive uninstall accepts only `DELETE AUTHENTICATION <canonical install root>`;
- destructive backup is created at `ReachCommander Backups\<UTC timestamp>`, flushed, byte-compared, and ACL-restricted before originals are removable;
- every removal target is a literal member of the allowlist and is disjoint from all configured sources;
- source canaries survive successful and injected-failure uninstalls.

- [ ] **Step 2: Run and observe the first missing action**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Lifecycle.Tests.ps1
```

Expected: non-zero exit on `Update-RcDeployment` or the first unimplemented action.

- [ ] **Step 3: Implement lifecycle actions using one transaction engine**

Reuse the lock, stage, validate, stop, commit, health, rollback sequence. Never create a second update-specific mutation path. Persist bounded rollback metadata under `backups\transactions`; retain the newest three healthy generated-state snapshots and never place auth, keys, or source content there.

`Get-RcStatus` must validate JSON/config first and return `Installed`, `ContainerState`, `Health`, `Image`, `BindAddress`, and `Port` without mutating state.

- [ ] **Step 4: Implement verified external auth backup and uninstall**

Enumerate auth files without following reparse points, copy with relative paths, flush both file and directory handles where supported, compare relative-name/length/SHA-256 manifests, then write `backup-manifest.json`. If any compare, teardown, or allowlist check fails, stop deletion and attempt to restart the previous deployment.

After retained uninstall, leave only `data\auth`, `data\keys`, and a short `RESTORE.txt` inside the inactive root. After destructive uninstall, remove the now-empty owned root only after the external backup is verified.

- [ ] **Step 5: Run the complete Windows suite twice**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Rendering.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Lifecycle.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Parse.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Common.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Rendering.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Lifecycle.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Parse.Tests.ps1
```

Expected: both runs pass and produce identical generated fixtures.

- [ ] **Step 6: Commit**

```bash
git add deploy/windows/Installer.Lifecycle.psm1 deploy/windows/install.ps1 tests/installer/windows/Lifecycle.Tests.ps1
git commit -m "feat: manage Windows installer lifecycle"
```

## Task 6: Add the verified bootstrap and deterministic release bundle

**Files:**

- Create: `deploy/windows/bootstrap.ps1`
- Create: `deploy/windows/package-installer.ps1`
- Create: `tests/installer/windows/Bootstrap.Tests.ps1`
- Create: `tests/installer/windows/Package.Tests.ps1`
- Modify: `tests/installer/windows/Parse.Tests.ps1`

**Interfaces:**

```powershell
bootstrap.ps1 [-Version latest|vX.Y.Z] [-Repository dragosniamtu/reach-commander] [-DownloadRoot <test path>]
package-installer.ps1 -Version <vX.Y.Z> -OutputDirectory <absolute path>
```

- [ ] **Step 1: Write failing bootstrap contracts**

Inject downloads through a test-only environment root and local fixture URLs. Assert that bootstrap:

- runs unelevated and never changes execution policy;
- accepts only `latest` or `v<major>.<minor>.<patch>`;
- downloads `reachcommander-windows-installer.zip` and `reachcommander-windows-installer.zip.sha256`;
- accepts exactly `<64 lowercase hex><two spaces><filename><newline>`;
- verifies `Get-FileHash -Algorithm SHA256` before opening the archive;
- rejects absolute, drive-qualified, `..`, duplicate, and reparse-producing ZIP entries;
- extracts to a random owned temporary directory;
- invokes extracted `install.ps1` with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File`;
- removes its temporary directory on success and failure.

- [ ] **Step 2: Write failing deterministic-package contracts**

Build the ZIP twice into separate test directories and assert equal SHA-256 values. Assert the archive has only:

```text
LICENSE
VERSION
compose.release.yaml
install.ps1
Installer.Common.psm1
Installer.Rendering.psm1
Installer.Lifecycle.psm1
```

Assert sorted entry order, fixed `1980-01-01T00:00:00Z` timestamps, no source/test/user files, and one exact checksum line.

- [ ] **Step 3: Run and observe missing scripts**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Bootstrap.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Package.Tests.ps1
```

Expected: non-zero exits because bootstrap and package scripts do not exist.

- [ ] **Step 4: Implement safe bootstrap download and extraction**

Use `Invoke-WebRequest -UseBasicParsing`, `Get-FileHash`, and `System.IO.Compression.ZipArchive`. Validate every entry name before any extraction, create files one at a time beneath the canonical extraction root, and reject links/reparse points. The bootstrap itself must be compatible with Windows PowerShell 5.1.

- [ ] **Step 5: Implement deterministic ZIP creation**

Sort the explicit allowlist, create entries with `System.IO.Compression.ZipArchive`, set every `LastWriteTime` to the ZIP epoch, and write `VERSION` with exactly the supplied version plus newline. Hash the final closed archive and write the dedicated checksum asset with UTF-8 without BOM.

- [ ] **Step 6: Run bootstrap/package/parser tests in both shells**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Bootstrap.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Package.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Parse.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Bootstrap.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Package.Tests.ps1
```

Expected: deterministic hashes match and traversal fixtures are rejected before extraction.

- [ ] **Step 7: Commit**

```bash
git add deploy/windows/bootstrap.ps1 deploy/windows/package-installer.ps1 tests/installer/windows/Bootstrap.Tests.ps1 tests/installer/windows/Package.Tests.ps1 tests/installer/windows/Parse.Tests.ps1
git commit -m "feat: package verified Windows installer"
```

## Task 7: Gate releases on Windows installer contracts

**Files:**

- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/workflow-contract.test.mjs`

- [ ] **Step 1: Add failing workflow-contract assertions**

Assert the workflow contains a `windows-installer` job on `windows-latest` that:

- runs every behavior suite plus bootstrap/package/parse tests in Windows PowerShell 5.1;
- reruns shell-sensitive suites under PowerShell 7;
- never invokes Docker Desktop or claims a Windows container smoke test;
- builds the deterministic Windows ZIP and checksum;
- uploads both as test artifacts;
- is required by hardened container smoke and verified multi-architecture publication;
- attaches both Windows assets to stable GitHub releases without changing the existing Ubuntu `SHA256SUMS` format.

- [ ] **Step 2: Run and observe the missing-job failure**

```bash
node --test tests/installer/workflow-contract.test.mjs
```

Expected: failure naming the absent `windows-installer` job.

- [ ] **Step 3: Add the Windows CI job and release steps**

Use explicit commands rather than a matrix that could silently omit Windows PowerShell:

```yaml
windows-installer:
  name: Windows installer contracts
  runs-on: windows-latest
  steps:
    - uses: actions/checkout@v4
    - name: Test with Windows PowerShell 5.1
      shell: powershell
      run: |
        ./tests/installer/windows/Common.Tests.ps1
        ./tests/installer/windows/Rendering.Tests.ps1
        ./tests/installer/windows/Lifecycle.Tests.ps1
        ./tests/installer/windows/Bootstrap.Tests.ps1
        ./tests/installer/windows/Package.Tests.ps1
        ./tests/installer/windows/Parse.Tests.ps1
    - name: Test shell-sensitive contracts with PowerShell 7
      shell: pwsh
      run: |
        ./tests/installer/windows/Common.Tests.ps1
        ./tests/installer/windows/Rendering.Tests.ps1
        ./tests/installer/windows/Lifecycle.Tests.ps1
        ./tests/installer/windows/Bootstrap.Tests.ps1
        ./tests/installer/windows/Package.Tests.ps1
```

Have this job build and upload a deterministic `v0.0.0` test bundle so the package output is inspectable on every run. In `container-publish`, after all required gates pass, build the release-version Windows assets with `pwsh` using the same validated stable version as the Ubuntu bundle, verify the dedicated checksum, and attach both Windows files. Add `windows-installer` to every publication `needs` chain.

- [ ] **Step 4: Run workflow and full Node contracts**

```bash
node --test tests/installer/workflow-contract.test.mjs
node --test tests/installer/docs-contract.test.mjs tests/installer/release-contract.test.mjs
```

Expected: all contracts pass.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml tests/installer/workflow-contract.test.mjs
git commit -m "ci: gate releases on Windows installer"
```

## Task 8: Document, verify, and prepare the Windows handoff

**Files:**

- Create: `docs/deployment/windows.md`
- Modify: `README.md`
- Modify: `deploy/README.md`
- Modify: `SECURITY.md`
- Modify: `tests/installer/docs-contract.test.mjs`

- [ ] **Step 1: Add failing documentation contracts**

Require the Windows guide to include:

- Docker Desktop and Compose v2 prerequisites;
- the mutable latest bootstrap warning;
- a pinned `vX.Y.Z` download/checksum/inspect/run sequence;
- process-scoped execution-policy behavior and no Administrator requirement;
- `%LOCALAPPDATA%\ReachCommander` and external backup locations;
- whole-drive and specific-folder modes, RO default, exact-path broad RW confirmation;
- loopback and LAN behavior, no firewall/TLS automation, and PWA HTTPS guidance;
- first-run account creation;
- installed lifecycle commands for status/logs/start/stop/restart/update/reconfigure/repair/uninstall;
- recovery and auth/key backup guidance;
- Docker Desktop file-sharing/access troubleshooting;
- a manual Docker Desktop release-smoke checklist.

Update cross-platform docs to describe Windows/macOS/Ubuntu as Docker-based deployments, never native desktop applications.

- [ ] **Step 2: Run and observe missing Windows documentation**

```bash
node --test tests/installer/docs-contract.test.mjs
```

Expected: failure because `docs/deployment/windows.md` is absent.

- [ ] **Step 3: Write operator documentation and security boundaries**

Use the release asset names and exact commands implemented in Task 6. Keep the latest bootstrap concise but label it convenience-only. Put the pinned checksum path first in security-sensitive guidance. Explain that credentials are stored in bind-mounted `data\auth`, keys in `data\keys`, and neither is baked into the image.

- [ ] **Step 4: Run all local verification available on Windows**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Common.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Rendering.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Lifecycle.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Bootstrap.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Package.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer/windows/Parse.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Common.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Rendering.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Lifecycle.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Bootstrap.Tests.ps1
pwsh -NoProfile -File tests/installer/windows/Package.Tests.ps1
node --test tests/installer/docs-contract.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/release-contract.test.mjs
```

Expected: every command exits zero.

- [ ] **Step 5: Perform the manual Docker Desktop release smoke**

On a non-production Windows test account with Docker Desktop already running:

1. install loopback-only with one RO source;
2. create the first account and verify sign-in;
3. reconfigure with a second RW source and write/delete a canary through ReachCommander;
4. verify a whole removable drive is mounted independently;
5. force an unhealthy candidate and verify rollback;
6. restart Docker Desktop and verify auth/key persistence;
7. uninstall retaining authentication, reinstall, and verify account continuity;
8. destructively uninstall and verify the external backup plus unchanged source canaries.

Record the tested Docker Desktop version and Windows build in the release notes; do not encode either as a runtime requirement unless a reproducible incompatibility is found.

- [ ] **Step 6: Review the diff and commit documentation**

```bash
git diff --check
git status --short
git diff -- README.md deploy/README.md SECURITY.md docs/deployment/windows.md tests/installer/docs-contract.test.mjs
git add README.md deploy/README.md SECURITY.md docs/deployment/windows.md tests/installer/docs-contract.test.mjs
git commit -m "docs: add Windows deployment guide"
```

- [ ] **Step 7: Final regression gate after the Ubuntu plan is implemented**

Run the complete repository verification listed in the Ubuntu plan's final task. Confirm `git status --short` shows only the pre-existing untracked `NC-theme.png` before asking the user whether to push.
