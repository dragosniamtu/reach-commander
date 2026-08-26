# ReachCommander Doctor Mount Check and Version Badge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `reachcommander doctor` validate application-data access in the container namespace where the runtime actually uses `/data`, and show the backend-reported current ReachCommander version beside the system-update toolbar button.

**Architecture:** Keep host-side structural, ownership, and mode validation unchanged, but move only the runtime read/write/traverse probe across the existing Docker bind-mount boundary with a fixed `docker exec --user UID:GID` command. Render `SystemUpdateStatusDto.currentVersion` inside the existing Angular system-update control so the UI and backend remain on the same version source and no new API is introduced.

**Tech Stack:** Bash, Docker Compose v2, shell contract tests, Angular 22 standalone components, TypeScript 6, Vitest, SCSS, GitHub Actions.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Preserve the unrelated untracked `NC-theme.png`; do not stage, modify, or remove it.
- Keep `/opt/reachcommander` root-owned and mode `0700`; never weaken its host permissions to make a host-namespace probe pass.
- Retain the exact application-data allowlist, symlink rejection, owner checks, directory mode `0700`, file mode `0600`, and administrator JSON validation.
- Probe only fixed container paths derived from the existing closed allowlist: `/data`, `/data/auth`, `/data/keys`, and the optional file-operation directories.
- Pass the container path as a positional argument to a fixed shell program. Do not interpolate a host path, source path, filename, browser value, or API value into shell code.
- The Doctor command remains non-mutating and must not reveal host installation paths in an access-probe failure.
- The version badge is informative and non-clickable. It must not change update-button enablement, confirmation, or update execution semantics.
- Display the backend value verbatim when present, `v…` while status is loading, and `Unknown` when status is available but has no current version.
- Keep the full value in the badge's accessible label and tooltip even when long edge-channel text is visually truncated.
- Preserve both default and Norton themes and compact toolbar behavior.
- Use test-driven development: establish the intended failure before changing production code.
- Do not tag or publish `v1.0.2` until complete local verification and pushed `master` CI pass.

---

### Task 1: Reproduce the protected-parent Doctor failure at the Docker boundary

**Files:**

- Modify: `tests/installer/fake-bin/setpriv`
- Modify: `tests/installer/fake-bin/docker`
- Modify: `tests/installer/test_command.sh`

- [ ] **Step 1: Add a narrowly scoped protected-parent behavior to fake `setpriv`**

Preserve normal argument parsing, collect the command arguments after `--`, and fail only when a test configures an inaccessible prefix:

```bash
command_arguments=("$@")
if [[ -n "${FAKE_SETPRIV_DENY_PREFIX:-}" ]]; then
  for argument in "${command_arguments[@]}"; do
    if [[ "$argument" == "$FAKE_SETPRIV_DENY_PREFIX" || "$argument" == "$FAKE_SETPRIV_DENY_PREFIX/"* ]]; then
      exit 96
    fi
  done
fi
exec "${command_arguments[@]}"
```

This simulates a runtime identity that cannot traverse the host-only installation parent while leaving configured source checks unaffected.

- [ ] **Step 2: Give fake Docker an explicit `exec` contract**

Add this case before the unsupported fallback:

```bash
  exec)
    if [[ -n "${FAKE_DOCKER_EXEC_DENY_PREFIX:-}" ]]; then
      for argument in "$@"; do
        if [[ "$argument" == "$FAKE_DOCKER_EXEC_DENY_PREFIX" || "$argument" == "$FAKE_DOCKER_EXEC_DENY_PREFIX/"* ]]; then
          exit 98
        fi
      done
    fi
    exit "${FAKE_DOCKER_EXEC_EXIT:-0}"
    ;;
```

The existing NUL-delimited `FAKE_DOCKER_LOG` records every argument.

- [ ] **Step 3: Add a failing protected-parent regression to the healthy Doctor test**

Before `run_command doctor`, configure the fake host access failure and clear the Docker log:

```bash
export FAKE_SETPRIV_DENY_PREFIX="$INSTALL_ROOT/data"
export FAKE_DOCKER_EXEC_DENY_PREFIX="$INSTALL_ROOT/data"
: >"$FAKE_DOCKER_LOG"
run_command doctor
unset FAKE_SETPRIV_DENY_PREFIX
unset FAKE_DOCKER_EXEC_DENY_PREFIX
```

Keep the existing success and non-mutation assertions. Add assertions that Doctor used Docker exec with the configured numeric identity and only the six expected container paths:

```bash
mapfile -d '' doctor_args <"$FAKE_DOCKER_LOG"
doctor_log="$(printf '%s\n' "${doctor_args[@]}")"
[[ "$doctor_log" == *$'exec\n--user\n'"$RUNTIME_UID:$RUNTIME_GID"* ]] ||
  fail "doctor did not probe application data as the configured container identity"
for container_path in \
  /data \
  /data/auth \
  /data/keys \
  /data/file-operations \
  /data/file-operations/plans \
  /data/file-operations/operations; do
  printf '%s\n' "${doctor_args[@]}" | grep -Fxq "$container_path" ||
    fail "doctor did not probe fixed container path: $container_path"
done
```

The fake Docker denial proves that no `exec` argument points into the host application-data tree without incorrectly rejecting the Compose configuration arguments that legitimately reference the installation root.

- [ ] **Step 4: Add a failing container-probe error contract**

After the healthy case, force `docker exec` to fail and assert a sanitized failure:

```bash
export FAKE_DOCKER_EXEC_EXIT=1
run_command doctor
unset FAKE_DOCKER_EXEC_EXIT
assert_equal "1" "$last_status" "doctor inaccessible container data status"
[[ "$last_output" == *'[FAIL] Application data directory is not accessible to the runtime identity'* ]] ||
  fail "doctor container data access failure missing"
[[ "$last_output" != *"$INSTALL_ROOT"* ]] ||
  fail "doctor exposed its host installation path"
pass "doctor validates runtime access at the application data mount boundary"
```

- [ ] **Step 5: Run the command contract and confirm RED**

```powershell
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_command.sh
```

Expected: FAIL in the healthy Doctor regression because production still calls host `setpriv` for `$INSTALL_ROOT/data`; the fixed container `exec` assertions also fail.

---

### Task 2: Probe application data inside the running container

**Files:**

- Modify: `deploy/reachcommander`
- Modify: `tests/installer/test_command.sh`
- Modify: `docs/deployment/ubuntu.md`

- [ ] **Step 1: Replace only the application-data host access probe**

In `doctor_application_data()`, keep the host-side allowlist, structure, ownership, and mode checks. For each recognized directory, convert the safe relative path to its fixed bind-mount destination and execute the access check inside the running container:

```bash
local container_path
container_path="/$relative_path"

if docker exec \
  --user "$runtime_uid:$runtime_gid" \
  reachcommander \
  sh -c 'test -r "$1" && test -w "$1" && test -x "$1"' \
  reachcommander-doctor \
  "$container_path" >/dev/null 2>&1; then
  doctor_pass 'Application data directory is accessible to the runtime identity'
else
  doctor_fail 'Application data directory is not accessible to the runtime identity'
fi
```

Do not modify the separate configured-source host accessibility check. It tests a different deployment boundary and already passes on the affected Ubuntu host.

- [ ] **Step 2: Run the focused Doctor command contract and confirm GREEN**

```powershell
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_command.sh
```

Expected: PASS. The protected host parent no longer causes six false failures, a failed container access probe still fails Doctor, and the deployment hash remains byte-identical.

- [ ] **Step 3: Document the namespace being checked**

Update the Doctor paragraph in `docs/deployment/ubuntu.md` to state that host paths are checked for the exact allowlist, ownership, and modes; read/write/traverse access is checked as the configured numeric identity at the container's fixed `/data` mount; and `/opt/reachcommander` remains protected and need not be traversable by the container user.

- [ ] **Step 4: Run installer documentation and shell contracts**

```powershell
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_command.sh
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_install.sh
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_package.sh
```

Expected: all contracts PASS; no generated deployment, updater, installer, or package behavior regresses.

- [ ] **Step 5: Commit the Doctor fix**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add deploy/reachcommander tests/installer/fake-bin/setpriv tests/installer/fake-bin/docker tests/installer/test_command.sh docs/deployment/ubuntu.md
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "fix: validate application data inside container"
```

Expected: the commit contains only the Doctor fix, its fakes/contracts, and Ubuntu operator documentation.

---

### Task 3: Specify the toolbar version presentation

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts`

- [ ] **Step 1: Add the stable-version badge contract**

Add a test that supplies `currentVersion: 'v1.0.2'` and expects a non-interactive badge immediately beside the trigger:

```typescript
it('shows the backend current version beside the update action', () => {
  fixture.componentRef.setInput('status', status({ currentVersion: 'v1.0.2' }));
  fixture.detectChanges();

  const trigger = fixture.nativeElement.querySelector('[data-testid="system-update-trigger"]');
  const badge = fixture.nativeElement.querySelector('[data-testid="current-version"]');
  expect(trigger.nextElementSibling).toBe(badge);
  expect(badge.textContent.trim()).toBe('v1.0.2');
  expect(badge.getAttribute('aria-label')).toBe('Current ReachCommander version v1.0.2');
  expect(badge.title).toBe('Current ReachCommander version v1.0.2');
});
```

- [ ] **Step 2: Add loading, unavailable, and long-edge contracts**

```typescript
it('shows a compact loading version before update status arrives', () => {
  fixture.detectChanges();
  const badge = fixture.nativeElement.querySelector('[data-testid="current-version"]');
  expect(badge.textContent.trim()).toBe('v…');
  expect(badge.getAttribute('aria-label')).toBe('Current ReachCommander version is loading');
});

it('shows an unavailable version when status omits currentVersion', () => {
  fixture.componentRef.setInput('status', status({ currentVersion: null }));
  fixture.detectChanges();
  const badge = fixture.nativeElement.querySelector('[data-testid="current-version"]');
  expect(badge.textContent.trim()).toBe('Unknown');
  expect(badge.title).toBe('Current ReachCommander version is unavailable');
});

it('keeps a long edge version complete for assistive text and the tooltip', () => {
  const version = 'edge@0123456789abcdef';
  fixture.componentRef.setInput('status', status({ currentVersion: version }));
  fixture.detectChanges();
  const badge = fixture.nativeElement.querySelector('[data-testid="current-version"]');
  expect(badge.textContent.trim()).toBe(version);
  expect(badge.getAttribute('aria-label')).toContain(version);
  expect(badge.title).toContain(version);
});
```

- [ ] **Step 3: Run the focused component test and confirm RED**

From `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include=src/app/features/system-update/system-update-button.component.spec.ts
```

Expected: FAIL because `[data-testid="current-version"]` is not rendered.

---

### Task 4: Render and style the current-version badge

**Files:**

- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.html`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts`

- [ ] **Step 1: Derive a total version presentation from update status**

Add a typed presentation and computed signal:

```typescript
interface CurrentVersionPresentation {
  readonly label: string;
  readonly accessibleLabel: string;
}

readonly currentVersion = computed<CurrentVersionPresentation>(() => {
  const status = this.status();
  if (!status) {
    return {
      label: 'v…',
      accessibleLabel: 'Current ReachCommander version is loading',
    };
  }

  const currentVersion = status.currentVersion?.trim();
  if (!currentVersion) {
    return {
      label: 'Unknown',
      accessibleLabel: 'Current ReachCommander version is unavailable',
    };
  }

  return {
    label: currentVersion,
    accessibleLabel: `Current ReachCommander version ${currentVersion}`,
  };
});
```

Use only the existing backend status payload. Do not read package metadata, service-worker metadata, image labels, or a new endpoint in Angular.

- [ ] **Step 2: Render the badge after the existing update button**

Inside `.update-control`, leave the button behavior intact and add:

```html
@let version = currentVersion();
<span
  class="version-badge"
  data-testid="current-version"
  [attr.aria-label]="version.accessibleLabel"
  [attr.title]="version.accessibleLabel"
>
  {{ version.label }}
</span>
```

The badge is not a button, link, or extra tab stop.

- [ ] **Step 3: Style it as one compact connected toolbar control**

Extend the component SCSS:

```scss
.version-badge {
  display: block;
  max-width: 96px;
  height: 28px;
  padding: 0 6px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-left: 0;
  border-radius: 0 4px 4px 0;
  color: var(--text-3);
  background: var(--surface-3);
  font: 700 9px/26px var(--font-mono);
  text-overflow: ellipsis;
  white-space: nowrap;
}

button {
  border-radius: 4px 0 0 4px;
}

:host-context([data-theme='norton']) .version-badge {
  border-radius: 0;
  box-shadow: none;
}
```

Merge the new declarations with the existing compact CSS rather than duplicating the full button rule.

- [ ] **Step 4: Run focused and surrounding Angular tests**

```powershell
npm test -- --watch=false --include=src/app/features/system-update --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts
```

Expected: PASS. Existing update phases, disabled focus behavior, toolbar ordering, and the new badge states all remain correct.

- [ ] **Step 5: Build the production frontend**

```powershell
npm run build
```

Expected: Angular production build succeeds with no template, typing, accessibility, or style-budget error.

- [ ] **Step 6: Commit the UI feature**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add client/reach-commander-ui/src/app/features/system-update/system-update-button.component.ts client/reach-commander-ui/src/app/features/system-update/system-update-button.component.html client/reach-commander-ui/src/app/features/system-update/system-update-button.component.scss client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "feat: show current version in toolbar"
```

Expected: the commit contains only the component implementation and tests.

---

### Task 5: Verify, push, and publish the installer-aware v1.0.2 update

**Files:**

- Verify: all modified files
- Preserve: `NC-theme.png`

- [ ] **Step 1: Run the full local verification matrix**

```powershell
dotnet test ReachCommander.slnx -c Release
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_command.sh
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_install.sh
& 'C:\Program Files\Git\bin\bash.exe' tests/installer/test_package.sh
Set-Location client/reach-commander-ui
npm test -- --watch=false
npm run test:pwa
npm run build
Set-Location ../..
Set-Location tests/e2e
npm test
Set-Location ../..
```

Expected: all commands exit `0`. If the solution or E2E package exposes a more specific existing script, use it and record the exact command/output.

- [ ] **Step 2: Perform a scope and security review**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' status --short
git -c safe.directory='D:/Work/Personal/Reach Commander' diff origin/master...HEAD --check
git -c safe.directory='D:/Work/Personal/Reach Commander' diff --stat origin/master...HEAD
rg -n "INSTALL_ROOT/data|/opt/reachcommander/data|docker exec|current-version" deploy tests client docs
```

Confirm that only fixed `/data` destinations reach `docker exec`; the shell source string is constant and uses positional `$1`; host structure/owner/mode checks remain fail-closed; Doctor remains non-mutating; no error includes the host installation path; the badge never enables or invokes an update; `NC-theme.png` remains untracked and unstaged; and no incomplete implementation markers were introduced.

- [ ] **Step 3: Commit this plan if needed**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' add docs/superpowers/plans/2026-08-26-reachcommander-doctor-version-badge.md
git -c safe.directory='D:/Work/Personal/Reach Commander' commit -m "docs: plan doctor mount check and version badge"
```

Skip this commit only if the plan is already part of an earlier documentation commit.

- [ ] **Step 4: Push `master` and wait for CI**

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' push origin master
$masterRun = gh run list --branch master --workflow CI --limit 1 --json databaseId --jq '.[0].databaseId'
gh run watch $masterRun --exit-status
```

Expected: Ubuntu backend, Windows backend, frontend/browser acceptance, macOS installer contracts, hardened amd64 container smoke, and verified multi-architecture publication succeed as defined by the workflow.

- [ ] **Step 5: Publish v1.0.2 only after `master` is green**

First verify that neither the local nor remote tag exists. Then:

```powershell
git -c safe.directory='D:/Work/Personal/Reach Commander' tag -a v1.0.2 -m "ReachCommander v1.0.2"
git -c safe.directory='D:/Work/Personal/Reach Commander' push origin v1.0.2
$releaseRun = gh run list --branch v1.0.2 --limit 1 --json databaseId --jq '.[0].databaseId'
gh run watch $releaseRun --exit-status
```

Expected: release CI succeeds and publishes the multi-architecture image plus `reachcommander-installer.tar.gz` and `SHA256SUMS`.

- [ ] **Step 6: Verify the public release contract**

Download both v1.0.2 release assets into a fresh temporary directory and run:

```bash
sha256sum --check --strict SHA256SUMS
tar -xzf reachcommander-installer.tar.gz
```

Confirm the bundle includes the corrected `reachcommander` command and the image reports v1.0.2. An existing Ubuntu installation must rerun this checksum-verified installer once because Doctor is host-owned; after that, `sudo reachcommander doctor` should pass the application-data access checks without changing `/opt/reachcommander` mode. The image update supplies the toolbar version badge.

## Coverage Matrix

| Requirement | Verification |
|---|---|
| Protected root-owned install parent does not create false failures | `FAKE_SETPRIV_DENY_PREFIX` healthy Doctor regression |
| Runtime access checked where the app actually runs | Logged `docker exec --user UID:GID` fixed `/data` path assertions |
| Genuine container access failure remains fail-closed | `FAKE_DOCKER_EXEC_EXIT=1` Doctor failure contract |
| No weakened host permissions | Host owner/mode checks retained; scope review rejects chmod workaround |
| No path injection or leakage | Closed allowlist, positional `$1`, no host path in exec log/output |
| Current version visible in toolbar | Angular stable-version DOM test |
| Loading and absent metadata are explicit | Angular `v…` and `Unknown` tests |
| Long edge value remains accessible | Full tooltip and `aria-label` test plus CSS ellipsis |
| Update behavior unchanged | Existing update-button and commander-shell tests |
| Both backend and host command reach users | v1.0.2 image plus checksum-verified installer release |
