# ReachCommander Ubuntu Bootstrap and Whole-Mount Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a one-command, checksum-verifying Ubuntu bootstrap and extend the existing root-owned installer with safe whole-mount discovery while preserving every current lifecycle and security invariant.

**Architecture:** Keep `deploy/install.sh`, `deploy/render_config.py`, and `deploy/reachcommander` as the single Ubuntu deployment format. Add an unprivileged bootstrap that verifies the existing deterministic release archive before invoking its installer through `sudo`, plus a small Python parser for `findmnt --json` so mount metadata is handled as structured data. Extend the existing source collector rather than creating another installer.

**Tech Stack:** Bash, Python 3 standard library, util-linux `findmnt`, Docker Engine with Compose v2, ShellCheck, GitHub Actions

## Global Constraints

- Apply this plan after the Windows installer plan so shared workflow and documentation edits are resolved once.
- Execute directly on `master`; do not create a branch, worktree, or subagent.
- Preserve the unrelated untracked `NC-theme.png`; never stage or modify it.
- Keep production installation root-owned at `/opt/reachcommander`, command `/usr/local/bin/reachcommander`, backup root `/var/backups/reachcommander`, and the existing non-root container UID/GID behavior.
- The bootstrap remains unprivileged through download, grammar validation, checksum verification, archive-structure validation, and extraction; `sudo` is invoked only for the already verified extracted `install.sh`.
- Do not install, start, stop, or reconfigure Docker Engine, Compose, a firewall, reverse proxy, TLS, or operating-system packages.
- Never offer `/`, protected system trees, pseudo-filesystems, network filesystems, Docker internals, installer-owned paths, or dynamic mount parents such as `/mnt` and `/media` in whole-mount mode.
- Specific-folder mode may accept an explicitly mounted network-backed directory only after canonicalization, overlap checks, and the existing real container access preflight.
- Default sources to RO. Require the exact canonical path for RW on a whole mount, a home/profile ancestor, or another approved broad source.
- Preserve existing install, reconfigure, update, rollback, doctor, auth/key backup, and uninstall contracts.
- Use test-driven development and commit after each task using only the listed files.

---

## Task 1: Add a checksum-verifying unprivileged Ubuntu bootstrap

**Files:**

- Create: `deploy/ubuntu/bootstrap.sh`
- Create: `tests/installer/test_bootstrap.sh`
- Create: `tests/installer/fake-bin/curl`
- Create: `tests/installer/fake-bin/sudo`

**Interface:**

```text
deploy/ubuntu/bootstrap.sh [latest|vX.Y.Z]
```

Test-only injection is enabled only when `REACHCOMMANDER_TESTING=1` and uses `REACHCOMMANDER_TEST_DOWNLOAD_ROOT`, `REACHCOMMANDER_TEST_RELEASE_BASE_URL`, and the fake-command `PATH`.

- [ ] **Step 1: Write failing bootstrap contracts**

Create a temporary release directory containing the real deterministic test archive and `SHA256SUMS`. Assert that bootstrap:

- accepts no argument as `latest` and accepts stable `vX.Y.Z` only;
- downloads exactly `reachcommander-installer.tar.gz` and `SHA256SUMS` from the matching release URL;
- requires exactly one checksum record with 64 lowercase hexadecimal characters, two spaces, the exact archive name, and one newline;
- computes and compares SHA-256 before inspecting or extracting the archive;
- rejects a checksum mismatch, extra checksum line, renamed asset, absolute archive entry, `..` traversal, symlink, hard link, device, and unexpected archive member;
- extracts beneath a random `mktemp -d` directory with mode `0700`;
- invokes `sudo -- <verified-root>/reachcommander-installer/install.sh` only after all unprivileged checks pass;
- forwards no environment-controlled installer path through `sudo`;
- removes temporary files on success, failure, HUP, INT, and TERM;
- never pipes downloaded content to a shell.

The fake `sudo` logs its argument array and executes the target with a test root; the fake `curl` copies fixture assets and records requested URLs.

- [ ] **Step 2: Run and observe the absent-bootstrap failure**

```bash
bash tests/installer/test_bootstrap.sh
```

Expected: non-zero exit because `deploy/ubuntu/bootstrap.sh` does not exist.

- [ ] **Step 3: Implement strict version, download, and checksum validation**

Start with:

```bash
#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

CHANNEL="${1:-latest}"
NUMBER='(0|[1-9][0-9]*)'
if [[ "$CHANNEL" != 'latest' && ! "$CHANNEL" =~ ^v${NUMBER}\.${NUMBER}\.${NUMBER}$ ]]; then
  printf 'ReachCommander bootstrap: expected latest or a stable vX.Y.Z version\n' >&2
  exit 64
fi
```

Require `curl`, `sha256sum`, `python3`, `mktemp`, and `sudo`. Download with `curl --fail --silent --show-error --location --output`. Parse `SHA256SUMS` as data; do not call `sha256sum --check` until its exact single-record grammar is confirmed.

- [ ] **Step 4: Validate and extract the fixed archive manifest without privilege**

Use an embedded Python 3 standard-library block with `tarfile.open(..., 'r:gz')`. Require exactly these normalized members and types after Task 4 adds mount discovery:

```text
reachcommander-installer/
reachcommander-installer/LICENSE
reachcommander-installer/VERSION
reachcommander-installer/compose.release.yaml
reachcommander-installer/install.sh
reachcommander-installer/lib/
reachcommander-installer/lib/common.sh
reachcommander-installer/mount_discovery.py
reachcommander-installer/reachcommander
reachcommander-installer/render_config.py
```

Reject duplicate names and every link/device type. Extract each regular file by streaming bytes into a newly created path beneath a canonical extraction root; apply `0755` only to directories, `install.sh`, and `reachcommander`, and `0644` to other files. Do not call `TarFile.extract` or `extractall`.

- [ ] **Step 5: Invoke the verified installer through the privilege boundary**

Use an absolute canonical target and:

```bash
sudo -- "$EXTRACT_ROOT/reachcommander-installer/install.sh"
```

Do not preserve bootstrap test variables in production `sudo`. Tests may replace the `sudo` executable through the already controlled fake `PATH`.

- [ ] **Step 6: Run the focused suite and ShellCheck**

```bash
bash tests/installer/test_bootstrap.sh
shellcheck -x --source-path=SCRIPTDIR deploy/ubuntu/bootstrap.sh tests/installer/test_bootstrap.sh tests/installer/fake-bin/curl tests/installer/fake-bin/sudo
```

Expected: every TAP assertion passes and ShellCheck exits zero.

- [ ] **Step 7: Commit**

```bash
git add deploy/ubuntu/bootstrap.sh tests/installer/test_bootstrap.sh tests/installer/fake-bin/curl tests/installer/fake-bin/sudo
git commit -m "feat: add verified Ubuntu bootstrap"
```

## Task 2: Parse and filter whole-mount candidates as structured data

**Files:**

- Create: `deploy/mount_discovery.py`
- Create: `tests/installer/test_mount_discovery.py`
- Create: `tests/installer/fixtures/findmnt.json`

**Interfaces:**

```python
def flatten_filesystems(document: object) -> tuple[dict[str, object], ...]
def eligible_mounts(document: object, excluded_paths: tuple[str, ...]) -> tuple[Mount, ...]

@dataclasses.dataclass(frozen=True)
class Mount:
    target: str
    source: str
    fstype: str
```

CLI:

```text
python3 deploy/mount_discovery.py --exclude /opt/reachcommander --exclude /var/backups/reachcommander --output nul < findmnt.json
```

`--output nul` emits repeating UTF-8 `target\0source\0fstype\0` fields for safe Bash ingestion.

- [ ] **Step 1: Write failing unit tests for nested `findmnt` JSON**

The fixture must include root, ext4/xfs/btrfs local data mounts, nested children, `/mnt` and `/media` parent mounts, tmpfs/proc/sysfs/cgroup/overlay/squashfs, Docker internals, install/backup overlaps, NFS/CIFS/SSHFS/9p network filesystems, escaped Unicode/spaces, duplicate targets, relative targets, and malformed records.

Assert:

- recursive child records are flattened;
- eligible local mounts are canonical absolute targets sorted by target;
- root, protected prefixes, pseudo/network types, dynamic parents, Docker internals, and owned-path overlaps are excluded;
- duplicate canonical targets and malformed/unknown structures fail closed;
- control characters in target/source/fstype fail closed;
- NUL output round-trips spaces and Unicode without shell evaluation.

- [ ] **Step 2: Run and observe the missing-module failure**

```bash
python3 -m unittest tests/installer/test_mount_discovery.py -v
```

Expected: import failure for `deploy/mount_discovery.py`.

- [ ] **Step 3: Implement the pure parser and explicit exclusion sets**

Use `json.load(sys.stdin)`, `os.path.normpath`, and `os.path.commonpath`; do not call a shell. Define explicit sets:

```python
NETWORK_TYPES = frozenset({"9p", "cifs", "fuse.sshfs", "nfs", "nfs4", "smb3"})
PSEUDO_TYPES = frozenset({"autofs", "cgroup", "cgroup2", "devpts", "devtmpfs", "overlay", "proc", "securityfs", "squashfs", "sysfs", "tmpfs", "tracefs"})
DYNAMIC_PARENTS = frozenset({"/media", "/mnt"})
PROTECTED_PREFIXES = ("/boot", "/dev", "/etc", "/proc", "/root", "/run", "/sys", "/usr", "/var")
```

Always exclude `/`, `/var/lib/docker`, `/var/lib/containers`, `/run/docker.sock`, the three installer-owned paths passed by the caller, and any same/ancestor/descendant overlap. `findmnt --real` is still required at the caller; the parser's exclusion rules are defense in depth.

- [ ] **Step 4: Run unit tests and syntax checks**

```bash
python3 -m unittest tests/installer/test_mount_discovery.py -v
python3 -m py_compile deploy/mount_discovery.py
```

Expected: all fixture cases pass.

- [ ] **Step 5: Commit**

```bash
git add deploy/mount_discovery.py tests/installer/test_mount_discovery.py tests/installer/fixtures/findmnt.json
git commit -m "feat: discover safe Ubuntu data mounts"
```

## Task 3: Integrate source modes and exact broad-write confirmation

**Files:**

- Modify: `deploy/install.sh`
- Modify: `deploy/lib/common.sh`
- Modify: `deploy/render_config.py`
- Modify: `tests/installer/test_common.sh`
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/test_render_config.py`
- Modify: `tests/installer/fixtures/valid-request.json`
- Create: `tests/installer/fake-bin/findmnt`
- Modify: `tests/installer/fake-bin/python3`

**Interfaces:**

```bash
rc_source_path_has_symlink <canonical-input-path>
rc_paths_overlap <left> <right>
source_requires_rw_confirmation <path> <whole|specific>
discover_whole_mounts
collect_specific_sources
collect_whole_mount_sources
collect_sources
```

Renderer additions:

```python
@dataclasses.dataclass(frozen=True)
class ExclusionRequest:
    host_path: str
    relative_target: str

@dataclasses.dataclass(frozen=True)
class SourceRequest:
    # Existing fields remain unchanged.
    exclusions: tuple[ExclusionRequest, ...]
```

The renderer CLI gains `add-exclusion --request <path> --source-id <id> --host-path <mask-path> --relative-target <path>`, called once per exclusion after `add-source`. Separate arguments keep every path as data even when it contains punctuation. Each request source carries an exact `exclusions` array; generated `config/sources.json` and `state/source-mounts.json` retain their current exact schemas.

- [ ] **Step 1: Extend common-library tests before production code**

Add contracts that reject source paths whose input chain contains a symbolic link, reject duplicate and nested source pairs in both directions, and identify broad RW sources. Add `"exclusions": []` to every valid request source fixture. Add renderer contracts that reject exclusion paths outside the installer-owned mask root, absolute/empty/parent-traversing relative targets, duplicate targets, and exclusions not strictly beneath their source. Assert ordinary narrow directories remain accepted. Keep tests inside the existing `TEST_ROOT` and preserve source canaries.

- [ ] **Step 2: Extend installer tests for the source-mode prompt**

Update `source_prompt_input()` so existing specific-folder tests answer `2` before source details:

```bash
printf '%s\n' \
  '' '' '' '' \
  '2' \
  "$first_name" "$SOURCE_ONE" '' "$first_access" \
  'y' "$second_name" "$SOURCE_TWO" '' "$second_access" \
  'n' "$first_id" "$second_id" "$https_acknowledgement"
```

Add whole-mount cases using fake `findmnt --json --real --output TARGET,SOURCE,FSTYPE`. Cover:

- menu option `1` lists only eligible fixture mounts;
- comma-separated numeric selection is normalized, deduplicated, and bounds-checked;
- each selected mount is added independently, never through `/mnt` or `/media`;
- default display name uses the final path component, with source ID normalization;
- RO needs no confirmation;
- RW requires typing the exact canonical mount path;
- a network filesystem is absent from the menu but its existing directory succeeds in specific-folder mode when Docker preflight passes;
- renderer output keeps Compose and `sources.json` access policy identical;
- no source canary changes on success or any injected failure.

- [ ] **Step 3: Run and observe prompt/discovery failures**

```bash
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
```

Expected: existing installer tests fail at the new source-mode answer until the collector is refactored; new whole-mount tests fail because discovery is absent.

- [ ] **Step 4: Harden canonical source handling**

Before `readlink -f`, walk each existing path component with `lstat` semantics (`[[ -L ... ]]`) and reject symbolic links rather than silently following them. Keep spaces and Unicode quoted as data. Add `rc_paths_overlap` using canonical absolute paths and slash-boundary comparison.

For approved broad sources that are ancestors of installer-owned state, compute nested exclusion mounts instead of exposing state. Reject same paths or sources inside owned paths. Store exclusion metadata in the temporary render request only; keep generated `config/sources.json` and `state/source-mounts.json` at their current exact schemas.

- [ ] **Step 5: Implement structured discovery and menu collection**

Add `findmnt` and `mount_discovery.py` to preflight requirements. Execute:

```bash
findmnt --json --real --output TARGET,SOURCE,FSTYPE >"$findmnt_json"
python3 "$MOUNT_DISCOVERY" \
  --exclude "$RC_INSTALL_ROOT" \
  --exclude "$RC_BACKUP_ROOT" \
  --exclude "$(dirname -- "$RC_COMMAND_PATH")" \
  --output nul <"$findmnt_json" >"$eligible_mounts"
```

Check each exit status before reading output. Use `mapfile -d ''` and consume fields in groups of three. Never evaluate emitted data.

- [ ] **Step 6: Refactor one shared source-details collector**

`collect_sources` prompts:

```text
Source mode (1 whole eligible mounts, 2 specific folders) [2]
```

Both modes pass canonical paths into one function that obtains/validates name, ID, and access. For RW where `source_requires_rw_confirmation` succeeds, prompt `Type the exact canonical path to allow broad RW access` and compare byte-for-byte. The existing runtime UID/GID access check and real temporary-container preflight remain authoritative; never call `chown`, `chmod`, or ACL tools on a source.

Extend `render_config.py` so each ordinary source bind is followed by its validated nested exclusion binds. Use an installer-owned empty `state/masks/<source-id>/<stable-id>` directory as `host_path` and a target beneath `/sources/<id>/<relative-owned-path>`; render the exclusion read-only. Validate masks through `docker compose config` and access preflight. If a relative exclusion cannot be represented safely, reject the broad source. Do not expose exclusion metadata to the application JSON or lifecycle source allowlist.

- [ ] **Step 7: Run the Ubuntu installer regression suites**

```bash
python3 -m unittest tests/installer/test_mount_discovery.py tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
```

Expected: new modes pass and all existing transaction/auth/source invariants remain green.

- [ ] **Step 8: Run ShellCheck and commit**

```bash
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh deploy/lib/common.sh tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/fake-bin/findmnt
git add deploy/install.sh deploy/lib/common.sh deploy/render_config.py tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/test_render_config.py tests/installer/fixtures/valid-request.json tests/installer/fake-bin/findmnt tests/installer/fake-bin/python3
git commit -m "feat: select whole mounts on Ubuntu"
```

## Task 4: Package the mount helper and document the Ubuntu bootstrap

**Files:**

- Modify: `deploy/package-installer.sh`
- Modify: `tests/installer/test_package.sh`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `README.md`
- Modify: `deploy/README.md`
- Modify: `SECURITY.md`
- Modify: `tests/installer/docs-contract.test.mjs`

- [ ] **Step 1: Add failing package assertions**

Require `mount_discovery.py` as a regular `0644` archive member in sorted deterministic position. Build twice, compare hashes, validate `SHA256SUMS`, and assert neither `deploy/ubuntu/bootstrap.sh` nor any test/source/user data is embedded in the privileged installer archive.

- [ ] **Step 2: Add failing Ubuntu documentation contracts**

Require:

- the concise mutable-bootstrap command and its warning;
- the existing pinned release/checksum/inspect path as the audited recommendation;
- an explanation that download/checksum/extraction are unprivileged and only verified `install.sh` receives `sudo`;
- both source modes, eligible/excluded mount rules, RO default, exact broad-RW confirmation, and explicit network-folder behavior;
- loopback/LAN, HTTPS/PWA, first-run account, lifecycle, backup/recovery, and uninstall guidance;
- unchanged `/opt/reachcommander`, `/usr/local/bin/reachcommander`, and `/var/backups/reachcommander` locations.

- [ ] **Step 3: Run and observe failures**

```bash
bash tests/installer/test_package.sh
node --test tests/installer/docs-contract.test.mjs
```

Expected: package test misses `mount_discovery.py`; docs contract misses bootstrap/whole-mount text.

- [ ] **Step 4: Add the helper to the deterministic archive allowlist**

Install it into the staging root with mode `0644`, append it in sorted tar order, and leave the archive name plus single-line `SHA256SUMS` contract unchanged. Update bootstrap's exact archive manifest test in the same commit if package member ordering reveals a mismatch.

- [ ] **Step 5: Update operator and security documentation**

Use an on-disk bootstrap invocation rather than `curl | bash`:

```bash
tmp_script="$(mktemp)"
curl --fail --location --output "$tmp_script" \
  https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/ubuntu/bootstrap.sh
less "$tmp_script"
bash "$tmp_script"
rm -f -- "$tmp_script"
```

Keep the pinned GitHub release flow first in the audited section. Explain that the account file and Data Protection keys are bind-mounted host state, not image contents.

- [ ] **Step 6: Run package/bootstrap/docs tests and commit**

```bash
bash tests/installer/test_package.sh
bash tests/installer/test_bootstrap.sh
node --test tests/installer/docs-contract.test.mjs
git add deploy/package-installer.sh tests/installer/test_package.sh docs/deployment/ubuntu.md README.md deploy/README.md SECURITY.md tests/installer/docs-contract.test.mjs deploy/ubuntu/bootstrap.sh tests/installer/test_bootstrap.sh
git commit -m "docs: add one-command Ubuntu deployment"
```

## Task 5: Make both platform installers required publication gates

**Files:**

- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/workflow-contract.test.mjs`
- Modify: `tests/installer/release-contract.test.mjs` if it owns the release-asset assertions

- [ ] **Step 1: Add failing final-gate contracts**

Assert:

- Ubuntu acceptance runs `test_bootstrap.sh` and `test_mount_discovery.py`;
- ShellCheck includes `deploy/ubuntu/bootstrap.sh` and its fake commands/tests;
- `windows-installer` is a required job from the Windows plan;
- `container-smoke.needs` contains `backend`, `acceptance`, `macos-installer`, and `windows-installer`;
- `container-publish.needs` contains all installer gates plus `container-smoke`;
- stable release publication builds and verifies the unchanged Ubuntu TAR/`SHA256SUMS` and the Windows ZIP/dedicated checksum;
- GitHub release upload names exactly all four assets;
- no publication job can run after a skipped/failed installer gate.

- [ ] **Step 2: Run and observe the new Ubuntu-gate failures**

```bash
node --test tests/installer/workflow-contract.test.mjs tests/installer/release-contract.test.mjs
```

Expected: failures naming missing bootstrap/mount-discovery coverage and incomplete publication dependencies.

- [ ] **Step 3: Extend acceptance and publication dependencies**

Add:

```yaml
- name: Test Ubuntu bootstrap contracts
  run: python3 tools/run_with_annotations.py "Ubuntu bootstrap contracts failed" bash tests/installer/test_bootstrap.sh

- name: Test mount discovery
  run: python3 tools/run_with_annotations.py "Mount discovery contracts failed" python3 -m unittest tests/installer/test_mount_discovery.py -v
```

Keep real container smoke on Ubuntu. Windows hosted CI must remain fake-Docker contract coverage and must not advertise Docker Desktop end-to-end verification.

In the stable release block, call the Ubuntu packager plus the Windows PowerShell packager with the same validated version, verify both checksum formats, and upload:

```text
reachcommander-installer.tar.gz
SHA256SUMS
reachcommander-windows-installer.zip
reachcommander-windows-installer.zip.sha256
```

- [ ] **Step 4: Run workflow/release/docs contracts**

```bash
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/release-contract.test.mjs tests/installer/docs-contract.test.mjs
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml tests/installer/workflow-contract.test.mjs tests/installer/release-contract.test.mjs
git commit -m "ci: require Windows and Ubuntu installer gates"
```

Omit `tests/installer/release-contract.test.mjs` from `git add` if its assertions already pass unchanged.

## Task 6: Run complete cross-platform regression and release checks

**Files:**

- Verify only; edit a failing file only after adding or tightening the focused regression that demonstrates the defect.

- [ ] **Step 1: Run all Ubuntu installer and contract suites**

```bash
python3 -m unittest tests/installer/test_render_config.py tests/installer/test_mount_discovery.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
bash tests/installer/test_bootstrap.sh
bash tests/installer/macos/test_helpers.sh
bash tests/installer/macos/test_install.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/release-contract.test.mjs tests/installer/docs-contract.test.mjs
```

Expected: all pass.

- [ ] **Step 2: Run ShellCheck across every packaged shell path**

Use the exact file list from `.github/workflows/ci.yml`, including Ubuntu bootstrap, existing Ubuntu installer/command/package files, macOS installer files, and every shell fake/test. Expected: zero diagnostics.

- [ ] **Step 3: Run Windows installer contracts on the Windows development machine**

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
```

Expected: all pass without touching real `%LOCALAPPDATA%` or Docker Desktop.

- [ ] **Step 4: Run application build and test regressions**

```bash
dotnet restore ReachCommander.slnx
dotnet build ReachCommander.slnx --configuration Release --no-restore
dotnet test ReachCommander.slnx --configuration Release --no-build
npm ci --prefix client/reach-commander-ui
npm run build --prefix client/reach-commander-ui
npm test --prefix client/reach-commander-ui -- --watch=false
```

Expected: backend and frontend build/tests pass.

- [ ] **Step 5: Build release assets twice and compare**

On Ubuntu or WSL:

```bash
bash deploy/package-installer.sh v0.0.0 /tmp/rc-release-a
bash deploy/package-installer.sh v0.0.0 /tmp/rc-release-b
sha256sum --check /tmp/rc-release-a/SHA256SUMS
cmp /tmp/rc-release-a/reachcommander-installer.tar.gz /tmp/rc-release-b/reachcommander-installer.tar.gz
```

On Windows PowerShell:

```powershell
./deploy/windows/package-installer.ps1 -Version v0.0.0 -OutputDirectory (Join-Path $env:TEMP 'rc-win-a')
./deploy/windows/package-installer.ps1 -Version v0.0.0 -OutputDirectory (Join-Path $env:TEMP 'rc-win-b')
$a = (Get-FileHash (Join-Path $env:TEMP 'rc-win-a\reachcommander-windows-installer.zip') -Algorithm SHA256).Hash
$b = (Get-FileHash (Join-Path $env:TEMP 'rc-win-b\reachcommander-windows-installer.zip') -Algorithm SHA256).Hash
if ($a -ne $b) { throw 'Windows installer archives are not deterministic' }
```

Expected: byte-identical platform archives and valid exact checksum assets.

- [ ] **Step 6: Perform the Ubuntu manual release smoke**

On a disposable Ubuntu Docker Engine host:

1. run the on-disk bootstrap without root and confirm `sudo` appears only after verification;
2. install loopback-only using one whole local data mount as RO;
3. create the first account and verify persistence;
4. reconfigure with a specific RW folder and exact confirmation;
5. confirm an NFS/CIFS mount is not offered, then add a narrow directory explicitly and preflight it;
6. run status, doctor, logs, stop, start, restart, and update;
7. force an unhealthy update and verify rollback;
8. uninstall retaining auth, reinstall, then destructively uninstall with verified external backup;
9. compare all source canaries before and after.

- [ ] **Step 7: Inspect the final repository state**

```bash
git diff --check
git status --short
git log --oneline -12
```

Expected: no whitespace errors, all implementation commits are on `master`, and the only unrelated untracked file is `NC-theme.png`. Do not push until the user explicitly requests it.
