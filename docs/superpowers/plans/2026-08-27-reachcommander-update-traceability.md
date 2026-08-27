# ReachCommander Update Traceability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ubuntu system updates time-bounded and diagnosable through protected root traces and a sanitized in-browser timeline, while proving the recreated container uses the expected image and is healthy.

**Architecture:** A focused Python trace module owns protected JSONL storage, strict event schemas, public projection, and retention. The root updater emits fixed event markers, supervises the complete update process group, and exposes protocol v3; ASP.NET validates the bounded public projection and Angular renders it without exposing host data. Protocols v1/v2 remain exact fallbacks and only the checksum-verified installer upgrades the root helper.

**Tech Stack:** Python 3 standard library, Bash, systemd, Docker Compose v2, ASP.NET Core 10/C# 14, Angular 22/TypeScript, xUnit, Vitest, Playwright, Node test runner.

## Global Constraints

- Work directly on `master`; do not create a branch or extra worktree.
- Keep updater commands fixed and target-free; the browser cannot provide images, channels, commands, paths, or operation IDs.
- Preserve exact protocol-v1 and protocol-v2 response shapes; add diagnostics only in protocol v3.
- Retain at most ten traces total, including the active trace, and at most ten megabytes; each trace is at most one megabyte and a fixed event count.
- Root trace storage is mode `0700` with regular non-symlink mode-`0600` files; unsafe entries are never followed or deleted.
- Browser trace data is authenticated, bounded, allowlisted, and contains no raw output, commands, paths, digests, credentials, environment values, source data, authentication state, or application logs.
- The fixed update command has a five-minute absolute deadline, a five-second `SIGTERM` grace period, a `SIGKILL` escalation, and bounded reader shutdown.
- Never restart Docker Engine; recreate only the ReachCommander container and verify exact image identity plus Docker health for both candidate and rollback.
- Preserve authentication, antiforgery, rate limiting, source confinement, updater socket restrictions, and non-root application execution.
- Preserve the unrelated untracked `NC-theme.png` file and never stage it.

The fixed event codes are `operationAccepted`, `downloadStarted`, `hostActivity`, `downloadCompleted`, `backupStarted`, `backupCompleted`, `installStarted`, `installCompleted`, `candidateRestartStarted`, `candidateRestartCompleted`, `candidateImageVerified`, `candidateHealthStarted`, `candidateHealthActivity`, `candidateHealthSucceeded`, `candidateHealthFailed`, `rollbackStarted`, `rollbackStateRestored`, `previousRestartStarted`, `previousRestartCompleted`, `previousImageVerified`, `recoveryHealthStarted`, `recoveryHealthActivity`, `recoveryHealthSucceeded`, `recoveryHealthFailed`, `commandTimedOut`, `terminationRequested`, `terminationForced`, `operationCompleted`, `operationRolledBack`, and `operationFailed`. Outcomes are exactly `started`, `activity`, `succeeded`, `failed`, and `timedOut`. Stages remain exactly `downloading`, `installing`, `restarting`, `healthChecking`, `restoring`, `restartingPrevious`, and `verifyingRecovery`.

## File map

- Create `deploy/updater_trace.py`: trace schema, protected JSONL persistence, public projection, and retention.
- Create `deploy/update_trace_cli.py`: fixed root-only latest-trace printing/following and doctor validation.
- Modify `deploy/updater_service.py`: bounded process-tree supervision, event-marker parsing, trace lifecycle, and protocol-v3 responses.
- Modify `deploy/updater_protocol.py`: protocol-v3 request acceptance while retaining v1/v2.
- Modify `deploy/reachcommander`: fixed trace markers, candidate/rollback image verification, update-log dispatch, and trace doctor check.
- Modify `deploy/install.sh` and `deploy/package-installer.sh`: install/package both new Python files without changing durable application state.
- Modify `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs`: strict v3 parsing and v3→v2→v1 fallback.
- Modify `src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs`: public trace enums and immutable models.
- Modify `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`: map trace snapshots monotonically for the matching operation.
- Modify `src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs`: authenticated sanitized trace DTOs.
- Modify `client/reach-commander-ui/src/app/core/api/api.models.ts`: trace contracts.
- Create `client/reach-commander-ui/src/app/features/system-update/system-update-trace.ts`: fixed labels and trace view-model derivation.
- Modify the system-update overlay TypeScript/template/styles: elapsed time and expandable details.
- Extend Python, Bash, .NET, Angular, integration, and Playwright tests named below.
- Update `README.md`, `deploy/README.md`, and `docs/deployment/ubuntu.md` with diagnostics, compatibility, and upgrade instructions.

---

### Task 1: Protected update trace store

**Files:**
- Create: `deploy/updater_trace.py`
- Create: `tests/installer/test_updater_trace.py`
- Modify: `deploy/package-installer.sh`
- Modify: `tests/installer/test_package.sh`

**Interfaces:**
- Produces: `TraceEvent`, `PublicTraceEvent`, `PublicTraceSnapshot`, `ProtectedUpdateTraceStore.start(operation_id, now)`, `append(operation_id, code, outcome, now, *, stage=None, exit_code=None, timeout_seconds=None)`, `public_snapshot(operation_id, now)`, `latest_path()`, `validate_tree()`, and `prune(active_operation_id)`.
- Produces constants `MAX_TRACE_FILES = 10`, `MAX_TRACE_DIRECTORY_BYTES = 10 * 1024 * 1024`, `MAX_TRACE_BYTES = 1024 * 1024`, `MAX_TRACE_EVENTS`, and `MAX_PUBLIC_EVENTS = 32`.

- [ ] **Step 1: Write failing storage, schema, and retention tests**

```python
def test_trace_is_private_ordered_and_public_projection_is_sanitized(self):
    store = ProtectedUpdateTraceStore(root, clock=lambda: NOW)
    store.start(OPERATION_ID, NOW)
    store.append(OPERATION_ID, "downloadStarted", "started", NOW, stage="downloading")
    store.append(OPERATION_ID, "hostActivity", "activity", NOW + SECOND, stage="downloading")
    snapshot = store.public_snapshot(OPERATION_ID, NOW + SECOND)
    self.assertEqual([1, 2, 3], [event.sequence for event in snapshot.events])
    self.assertNotIn("sha256:", json.dumps(dataclasses.asdict(snapshot)))
    self.assertEqual(0o600, stat.S_IMODE((root / f"{OPERATION_ID}.jsonl").stat().st_mode))

def test_retention_keeps_active_plus_nine_newest_within_limits(self):
    store.prune(active_operation_id=ACTIVE_ID)
    self.assertLessEqual(len(list(root.iterdir())), 10)
    self.assertTrue((root / f"{ACTIVE_ID}.jsonl").exists())
    self.assertLessEqual(sum(path.stat().st_size for path in root.iterdir()), 10 * 1024 * 1024)
```

Add independent tests for duplicate/out-of-order sequences, unknown event/outcome/stage, oversized files, excessive event counts, symlinked directory/file, unexpected filename, unsafe modes, malformed JSONL, active-trace preservation, and no-follow pruning.

- [ ] **Step 2: Run the tests and verify RED**

Run: `python -m unittest tests.installer.test_updater_trace -v`  
Expected: FAIL with `ModuleNotFoundError: No module named 'deploy.updater_trace'`.

- [ ] **Step 3: Implement the minimal protected trace module**

```python
@dataclasses.dataclass(frozen=True, slots=True)
class TraceEvent:
    schema_version: int
    sequence: int
    operation_id: str
    timestamp: str
    elapsed_milliseconds: int
    code: str
    stage: str | None
    outcome: str
    exit_code: int | None = None
    timeout_seconds: int | None = None

@dataclasses.dataclass(frozen=True, slots=True)
class PublicTraceEvent:
    sequence: int
    timestamp: str
    elapsed_seconds: int
    code: str
    stage: str | None
    outcome: str

@dataclasses.dataclass(frozen=True, slots=True)
class PublicTraceSnapshot:
    started_at: str
    elapsed_seconds: int
    last_activity_at: str | None
    events: tuple[PublicTraceEvent, ...]

def _trace_path(root: Path, operation_id: str) -> Path:
    try:
        canonical = str(uuid.UUID(operation_id))
    except (ValueError, AttributeError) as error:
        raise TraceError("the update trace operation identifier is invalid") from error
    if canonical != operation_id:
        raise TraceError("the update trace operation identifier is invalid")
    return root / f"{canonical}.jsonl"
```

Implement `ProtectedUpdateTraceStore` with the exact signatures in **Interfaces** using `lstat`, `O_NOFOLLOW`, `O_CLOEXEC`, append-only descriptors, `fsync`, `_trace_path`, strict JSON object fields, and oldest-terminal-first pruning. `public_snapshot` constructs `PublicTraceEvent` instances only, so root-only exit-code and timeout fields cannot cross the boundary.

- [ ] **Step 4: Package the module and verify GREEN**

Add `updater_trace.py` to installer input validation, staging, deterministic archive manifests, and mode assertions as `0644`.

Run: `python -m unittest tests.installer.test_updater_trace -v`  
Expected: all trace tests PASS.

Run: `bash tests/installer/test_package.sh`  
Expected: all checks PASS and the archive contains `reachcommander-installer/updater_trace.py` mode `0644`.

- [ ] **Step 5: Commit**

```bash
git add deploy/updater_trace.py tests/installer/test_updater_trace.py deploy/package-installer.sh tests/installer/test_package.sh
git commit -m "feat: persist protected update traces"
```

### Task 2: Bounded process-tree supervision

**Files:**
- Modify: `deploy/updater_service.py`
- Modify: `tests/installer/test_updater_service.py`

**Interfaces:**
- Consumes `ProtectedUpdateTraceStore` from Task 1.
- Produces `CommandTimedOut` and bounded `SubprocessCommandRunner.run(argv, *, env, timeout, shell, progress_callback=None, trace_callback=None)` with process-group termination.

- [ ] **Step 1: Write the retained-descendant-pipe regression**

```python
@unittest.skipIf(os.name == "nt", "Linux process groups are required")
def test_timeout_kills_descendant_and_never_waits_forever_for_output_pipe(self):
    child = (
        "import subprocess,sys,time;"
        "subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']);"
        "print('ready', flush=True);time.sleep(60)"
    )
    started = time.monotonic()
    with self.assertRaises(CommandTimedOut):
        SubprocessCommandRunner().run((sys.executable, "-c", child), env=os.environ, timeout=1, shell=False)
    self.assertLess(time.monotonic() - started, 8)
```

Add tests for valid `REACHCOMMANDER_UPDATE_EVENT=<code>:<outcome>` callbacks, invalid marker bounding, fifteen-second activity coalescing, TERM-before-KILL, Windows fallback, and bounded reader completion.

- [ ] **Step 2: Run the regression and verify RED**

Run: `python -m unittest tests.installer.test_updater_service.ServiceProcessContractTests.test_timeout_kills_descendant_and_never_waits_forever_for_output_pipe -v`  
Expected: FAIL because `CommandTimedOut` and process-group supervision do not exist.

- [ ] **Step 3: Implement bounded process supervision**

```python
class CommandTimedOut(TimeoutError):
    def __init__(self, timeout_seconds: int, output: str) -> None:
        super().__init__(f"the fixed updater command exceeded {timeout_seconds} seconds")
        self.timeout_seconds = timeout_seconds
        self.output = output

def _terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if os.name != "nt":
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
    else:
        process.terminate()
        try:
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=TERMINATION_GRACE_SECONDS)
```

Launch with `start_new_session=os.name != "nt"`. After termination, close the parent stream and call `reader.join(timeout=READER_JOIN_TIMEOUT_SECONDS)` with no later unbounded join. Keep `MAX_COMMAND_OUTPUT_CHARS` and never persist captured output.

- [ ] **Step 4: Verify GREEN**

Run: `python -m unittest tests.installer.test_updater_service -v`  
Expected: all tests PASS; the POSIX regression completes in under eight seconds.

- [ ] **Step 5: Commit**

```bash
git add deploy/updater_service.py tests/installer/test_updater_service.py
git commit -m "fix: bound updater process termination"
```

### Task 3: Trace host boundaries and verify the recreated image

**Files:**
- Modify: `deploy/reachcommander`
- Modify: `tests/installer/test_command.sh`

**Interfaces:**
- Produces fixed markers `REACHCOMMANDER_UPDATE_EVENT=<event-code>:<outcome>`.
- Produces `verify_running_image(expected_image)` and a bounded health loop with coalesced activity.

- [ ] **Step 1: Write failing command contracts**

```bash
run_command update stable
assert_ordered_markers \
  'downloadStarted:started' 'downloadCompleted:succeeded' \
  'candidateRestartStarted:started' 'candidateRestartCompleted:succeeded' \
  'candidateImageVerified:succeeded' 'candidateHealthSucceeded:succeeded'
assert_equal '0' "$last_status" 'verified candidate update status'
```

Add separate tests for mismatched candidate image rollback, previous-image verification, health activity, and absence of Docker daemon restart or sensitive marker values.

- [ ] **Step 2: Run and verify RED**

Run: `bash tests/installer/test_command.sh`  
Expected: FAIL because event markers and image verification are absent.

- [ ] **Step 3: Implement fixed event and identity functions**

```bash
report_update_event() {
  local code="${1:-}"
  local outcome="${2:-}"
  case "$code:$outcome" in
    downloadStarted:started|downloadCompleted:succeeded|backupStarted:started|\
    backupCompleted:succeeded|installStarted:started|installCompleted:succeeded|\
    candidateRestartStarted:started|candidateRestartCompleted:succeeded|\
    candidateImageVerified:succeeded|candidateImageVerified:failed|\
    candidateHealthStarted:started|candidateHealthActivity:activity|\
    candidateHealthSucceeded:succeeded|candidateHealthFailed:failed|\
    rollbackStarted:started|rollbackStateRestored:succeeded|\
    previousRestartStarted:started|previousRestartCompleted:succeeded|\
    previousImageVerified:succeeded|previousImageVerified:failed|\
    recoveryHealthStarted:started|recoveryHealthActivity:activity|\
    recoveryHealthSucceeded:succeeded|recoveryHealthFailed:failed)
      printf 'REACHCOMMANDER_UPDATE_EVENT=%s:%s\n' "$code" "$outcome" >&2 ;;
    *) rc_die 'internal update trace event is invalid'; return 1 ;;
  esac
}

verify_running_image() {
  local expected_image="$1"
  local expected_id running_id
  expected_id="$(docker image inspect --format '{{.Id}}' "$expected_image")" || return 1
  running_id="$(docker inspect --format '{{.Image}}' reachcommander)" || return 1
  [[ -n "$expected_id" && "$running_id" == "$expected_id" ]]
}
```

Verify the candidate immediately after Compose recreation and the previous image after rollback recreation, before each health check. Emit health activity at most every fifteen attempts. Never invoke a Docker Engine restart.

- [ ] **Step 4: Verify GREEN**

Run: `bash tests/installer/test_command.sh`  
Expected: all command tests PASS.

Run on Ubuntu: `shellcheck deploy/reachcommander`  
Expected: zero findings.

- [ ] **Step 5: Commit**

```bash
git add deploy/reachcommander tests/installer/test_command.sh
git commit -m "feat: verify traced container updates"
```

### Task 4: Trace lifecycle and updater protocol v3

**Files:**
- Modify: `deploy/updater_protocol.py`
- Modify: `deploy/updater_service.py`
- Modify: `tests/installer/test_updater_protocol.py`
- Modify: `tests/installer/test_updater_service.py`

**Interfaces:**
- Consumes Tasks 1–3.
- Produces `PROTOCOL_VERSION = 3`, exact v1/v2/v3 responses, nullable public `trace`, and `update_command_timeout`.

- [ ] **Step 1: Write failing compatibility and lifecycle tests**

```python
def test_accepts_v1_v2_v3_and_rejects_unadvertised_versions(self):
    self.assertEqual(3, PROTOCOL_VERSION)
    self.assertEqual(frozenset({1, 2, 3}), SUPPORTED_PROTOCOL_VERSIONS)

def test_v3_response_contains_only_bounded_public_trace(self):
    response = runtime.handle(request("check", 3))
    self.assertEqual({"startedAt", "elapsedSeconds", "lastActivityAt", "events"}, set(response["trace"]))
    self.assertNotIn("exitCode", json.dumps(response["trace"]))
    self.assertNotIn("sha256:", json.dumps(response["trace"]))
```

Add exact-shape tests: v1 has neither `progressStage` nor `trace`, v2 has `progressStage` only, and v3 has both. Add timeout terminal-state, ordering, operation-isolation, service-interruption, trace-write-failure, and maximum-response tests.

- [ ] **Step 2: Run and verify RED**

Run: `python -m unittest tests.installer.test_updater_protocol tests.installer.test_updater_service -v`  
Expected: FAIL because v3 and trace wiring are absent.

- [ ] **Step 3: Implement v3 and runtime trace wiring**

```python
LEGACY_PROTOCOL_VERSION = 1
DETAILED_PROTOCOL_VERSION = 2
PROTOCOL_VERSION = 3
SUPPORTED_PROTOCOL_VERSIONS = frozenset({1, 2, 3})
V3_RESPONSE_FIELDS = (*V2_RESPONSE_FIELDS, "trace")
```

Start the trace after `AtomicUpdateJournal.begin`; append validated marker/activity callbacks; append timeout, termination, and terminal events; prune after terminal persistence. `protocol_response` reads only the current operation projection for v3. Trace persistence failure prints a fixed warning without changing the update result.

- [ ] **Step 4: Verify GREEN**

Run: `python -m unittest tests.installer.test_updater_protocol tests.installer.test_updater_trace tests.installer.test_updater_service -v`  
Expected: all tests PASS, including exact legacy shapes.

- [ ] **Step 5: Commit**

```bash
git add deploy/updater_protocol.py deploy/updater_service.py tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py
git commit -m "feat: expose updater trace protocol"
```

### Task 5: Root update-log command, doctor, and installer upgrade

**Files:**
- Create: `deploy/update_trace_cli.py`
- Modify: `deploy/reachcommander`
- Modify: `deploy/install.sh`
- Modify: `deploy/package-installer.sh`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/test_package.sh`

**Interfaces:**
- Consumes `ProtectedUpdateTraceStore`.
- Produces `sudo reachcommander update-log`, `sudo reachcommander update-log --follow`, and doctor status.

- [ ] **Step 1: Write failing CLI/doctor/installer tests**

```bash
run_command update-log
assert_contains "$last_output" 'Elapsed' 'trace elapsed header'
run_command update-log --follow
assert_equal '0' "$last_status" 'follow latest active trace'
run_command update-log --path /etc/shadow
assert_equal '64' "$last_status" 'arbitrary path rejection'
```

Also test no-trace success, chronological output, terminal follow exit, unsafe storage failure without contents, doctor messages, file modes, and installer-refresh preservation of auth, keys, sources, journal, and file operations.

- [ ] **Step 2: Run and verify RED**

Run: `bash tests/installer/test_command.sh && bash tests/installer/test_install.sh && bash tests/installer/test_package.sh`  
Expected: FAIL because the CLI does not exist.

- [ ] **Step 3: Implement the fixed CLI**

```python
def main(argv: Sequence[str]) -> int:
    arguments = tuple(argv)
    if arguments not in {(), ("--follow",), ("--doctor",)}:
        print("Usage: reachcommander update-log [--follow]", file=sys.stderr)
        return 64
    store = ProtectedUpdateTraceStore(Path("/opt/reachcommander/state/update-traces"))
    if arguments == ("--doctor",):
        valid, detail = store.validate_tree()
        print(detail)
        return 0 if valid else 1
    return print_latest(store, follow=arguments == ("--follow",))
```

The management shell supplies the fixed root; no user path is accepted. Print validated formatted events, never raw JSONL. Follow at one-second intervals and stop on a terminal event.

- [ ] **Step 4: Install/package and verify GREEN**

Install `update_trace_cli.py` mode `0755` in `bin` and `updater_trace.py` mode `0644` in `lib`. Include both in backup/rollback lists and doctor validation without changing durable data.

Run: `bash tests/installer/test_command.sh && bash tests/installer/test_install.sh && bash tests/installer/test_package.sh`  
Expected: all suites PASS.

- [ ] **Step 5: Commit**

```bash
git add deploy/update_trace_cli.py deploy/reachcommander deploy/install.sh deploy/package-installer.sh tests/installer/test_command.sh tests/installer/test_install.sh tests/installer/test_package.sh
git commit -m "feat: add root update trace diagnostics"
```

### Task 6: Strict ASP.NET trace boundary

**Files:**
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs`
- Modify: `src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Modify: `src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs`

**Interfaces:**
- Consumes protocol v3.
- Produces `SystemUpdateTraceEventCode`, `SystemUpdateTraceOutcome`, `SystemUpdateTraceEvent`, `SystemUpdateTrace`, and nullable `Trace`.

- [ ] **Step 1: Write failing strict-boundary tests**

```csharp
[Fact]
public async Task Gateway_prefers_v3_and_falls_back_only_for_exact_incompatibility()
{
    var result = await gateway.CheckAsync(default);
    Assert.Equal([3, 2], transport.ProtocolVersions);
    Assert.Null(result.Trace);
}

[Fact]
public void Trace_serialization_contains_no_root_only_data()
{
    var json = JsonSerializer.Serialize(status, JsonOptions);
    Assert.Contains("\"code\":\"downloadStarted\"", json);
    Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("docker", json, StringComparison.OrdinalIgnoreCase);
}
```

Add rejection tests for unknown/duplicate fields, over 32 events, non-increasing sequence/elapsed/timestamps, unknown code/stage/outcome, raw diagnostic fields, malformed v3 without fallback, and cross-operation publication.

- [ ] **Step 2: Run and verify RED**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdate"`  
Expected: FAIL because trace contracts are absent.

- [ ] **Step 3: Add immutable public models**

```csharp
public enum SystemUpdateTraceOutcome { Started, Activity, Succeeded, Failed, TimedOut }

public sealed record SystemUpdateTraceEvent(
    int Sequence,
    DateTimeOffset Timestamp,
    long ElapsedSeconds,
    SystemUpdateTraceEventCode Code,
    SystemUpdateProgressStage? Stage,
    SystemUpdateTraceOutcome Outcome);

public sealed record SystemUpdateTrace(
    DateTimeOffset StartedAt,
    long ElapsedSeconds,
    DateTimeOffset? LastActivityAt,
    IReadOnlyList<SystemUpdateTraceEvent> Events);
```

Define `SystemUpdateTraceEventCode` with Task 1's public codes. Permit trace only on applying/terminal phases. DTOs copy immutable arrays with camel-case enum serialization.

- [ ] **Step 4: Implement parsing and monotonic mapping**

Request v3, then v2, then v1 only on exact v1-shaped incompatibility. Validate exact nested fields, 32-event maximum, sequences starting at one, nonnegative monotonic elapsed time, valid timestamps, and allowlisted enums. Coordinator publishes only matching-operation traces and never replaces a longer same-operation trace with a shorter one.

- [ ] **Step 5: Verify GREEN**

Run: `dotnet test ReachCommander.slnx -c Release`  
Expected: all .NET tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs
git commit -m "feat: publish sanitized update traces"
```

### Task 7: Browser technical-details timeline

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-trace.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-trace.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts`

**Interfaces:**
- Consumes nullable sanitized `trace`.
- Produces `buildSystemUpdateTrace(status, nowMilliseconds): SystemUpdateTraceView`.

- [ ] **Step 1: Write failing view/overlay tests**

```typescript
it('shows elapsed time, last host activity, and ordered safe events', () => {
  const view = buildSystemUpdateTrace(statusWithTrace(), Date.parse('2026-08-27T10:02:05Z'));
  expect(view.elapsedLabel).toBe('2m 5s');
  expect(view.events.map((event) => event.label)).toEqual([
    'Update accepted',
    'Downloading verified image',
    'Host download activity confirmed',
  ]);
  expect(view.stale).toBe(false);
});

it('opens technical details automatically after sixty silent seconds', () => {
  fixture.componentRef.setInput('status', staleStatusWithTrace());
  fixture.detectChanges();
  expect(fixture.nativeElement.querySelector('details').open).toBe(true);
});
```

Add v1/v2 guidance, timeout, no-trace, keyboard, live-region, compact, themes, reduced-motion, and host-string-absence cases.

- [ ] **Step 2: Run and verify RED**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include='src/app/features/system-update/system-update-trace.spec.ts' --include='src/app/features/system-update/system-update-overlay.component.spec.ts'`  
Expected: FAIL because the trace UI does not exist.

- [ ] **Step 3: Add API types and pure view model**

```typescript
export interface SystemUpdateTraceEventDto {
  readonly sequence: number;
  readonly timestamp: string;
  readonly elapsedSeconds: number;
  readonly code: SystemUpdateTraceEventCode;
  readonly stage: SystemUpdateProgressStage | null;
  readonly outcome: 'started' | 'activity' | 'succeeded' | 'failed' | 'timedOut';
}

export interface SystemUpdateTraceDto {
  readonly startedAt: string;
  readonly elapsedSeconds: number;
  readonly lastActivityAt: string | null;
  readonly events: readonly SystemUpdateTraceEventDto[];
}
```

Use a closed label record. Compute elapsed time from the greater of host elapsed and local delta; stale means applying with no host activity/event for sixty seconds.

- [ ] **Step 4: Render accessible details**

Add elapsed and last-activity summary. Render an expandable `details` with chronological list, relative times, fixed outcomes, and helper-refresh guidance when `protocolVersion < 3`. Auto-open only for stale/terminal attention. Bound internal scrolling and prevent horizontal overflow.

- [ ] **Step 5: Verify GREEN**

```bash
npm test -- --watch=false
npm run build
npm run test:pwa
npm run verify:pwa
```

Expected: all tests PASS; build/PWA checks exit `0`.

- [ ] **Step 6: Commit**

```bash
git add client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/features/system-update/system-update-trace.ts client/reach-commander-ui/src/app/features/system-update/system-update-trace.spec.ts client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts
git commit -m "feat: show update diagnostic timeline"
```

### Task 8: Acceptance, documentation, and release gates

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `tests/e2e/specs/system-update.spec.ts`
- Modify: `README.md`
- Modify: `deploy/README.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `tests/installer/docs-contract.test.mjs`
- Modify: `.github/workflows/ci.yml` only if its Python test list is explicit.

- [ ] **Step 1: Write failing acceptance/docs contracts**

Add browser cases for event growth, elapsed time, stale auto-open, timeout recovery, reconnection, legacy-v2 guidance, mismatch rollback, compact themes, reduced motion, and no raw values. Require:

```text
sudo reachcommander update-log
sudo reachcommander update-log --follow
sudo journalctl -u reachcommander-updater.service --since today
```

Require copy that the installer upgrades v3 helpers, old stuck events cannot be reconstructed, only the ReachCommander container is recreated, and Docker Engine is never restarted.

- [ ] **Step 2: Run and verify RED**

Run: `node --test tests/installer/docs-contract.test.mjs`  
Expected: FAIL on missing trace docs.

Run from `tests/e2e`: `npx playwright test specs/system-update.spec.ts`  
Expected: FAIL until fixtures/UI include traces.

- [ ] **Step 3: Complete fixtures and docs**

Seed only sanitized trace objects. Document immediate pre-v3 commands, checksum-verified installer refresh, retention/security, image/health proof, and root/UI visibility. Update test counts only after full suites.

- [ ] **Step 4: Run full verification**

Windows:

```powershell
python -m unittest tests.installer.test_lan_address tests.installer.test_render_config tests.installer.test_updater_protocol tests.installer.test_updater_trace tests.installer.test_updater_service -v
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
dotnet test ReachCommander.slnx -c Release
Set-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
npm run test:pwa
npm run verify:pwa
Set-Location ../../tests/e2e
npm test
```

Ubuntu/CI:

```bash
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
shellcheck deploy/install.sh deploy/reachcommander deploy/package-installer.sh
systemd-analyze verify deploy/systemd/reachcommander-updater.service
```

Expected: all exit `0` with no failures, lint findings, systemd errors, overflow, or leaked host strings.

- [ ] **Step 5: Commit scoped files**

```bash
git diff --check
git status --short
git add tests/e2e/support/seed-fixtures.ts tests/e2e/specs/system-update.spec.ts README.md deploy/README.md docs/deployment/ubuntu.md tests/installer/docs-contract.test.mjs
git commit -m "test: verify traceable system updates"
```

Stage a CI edit only if required. Never stage `NC-theme.png`.

## Final review checklist

- [ ] Reproduce the retained-output-pipe timeout and confirm terminal failure within deadline plus grace.
- [ ] Confirm trace, journal, and API use the same operation ID.
- [ ] Confirm pruning preserves the active trace and stays within ten files/ten megabytes.
- [ ] Confirm candidate and rollback success require exact image identity and Docker health.
- [ ] Confirm no code restarts Docker Engine.
- [ ] Confirm v1/v2 exact compatibility and v3 strict parsing/fallback.
- [ ] Confirm browser/API payloads contain no raw host diagnostics.
- [ ] Confirm `update-log`, doctor, installer refresh, and durable-state preservation.
- [ ] Confirm all test/build/PWA/browser/Ubuntu gates pass before completion.
