# ReachCommander Sanitized Support Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the signed-in administrator download a bounded, always-sanitized update/deployment diagnostic ZIP from the blocking updater overlay, with an equivalent host CLI fallback.

**Architecture:** A root-owned Python collector converts host observations into a strict allowlisted snapshot and exposes it through updater protocol v4. ASP.NET validates that snapshot, creates the five-entry ZIP in bounded memory, and streams it from an authenticated POST endpoint; Angular downloads it without dismissing the update overlay. The CLI reuses the collector and emits the same content contract to standard output.

**Tech Stack:** Python 3 standard library, Bash, Unix domain sockets, ASP.NET Core/.NET 10, `System.IO.Compression`, Angular signals/HttpClient, Vitest, xUnit, Playwright.

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Never stage or modify the untracked `NC-theme.png` file.
- Browser bundles are temporary and retained nowhere on the server.
- Include no raw logs, credentials, tokens, source metadata, filenames, paths, addresses, hostnames, environment values, command lines, command output, image digests, registry responses, container IDs, or arbitrary exception text.
- Cap each external host check at two seconds, the host collection at ten seconds, the host response at 256 KiB, and ZIP uncompressed content at one MiB.
- Keep updater status/apply protocol v1-v3 behavior exact and unchanged; diagnostics use protocol v4 only.
- Keep authentication, antiforgery, rate limiting, source confinement, updater socket isolation, and non-root application execution enabled.
- Never mount `/var/run/docker.sock` into the application and never restart Docker Engine.

---

### Task 1: Structured host diagnostic collector

**Files:**
- Create: `deploy/support_bundle.py`
- Create: `tests/installer/test_support_bundle.py`

**Interfaces:**
- Produces: `DiagnosticCheck`, `DiagnosticSnapshot`, and `HostDiagnosticCollector.collect() -> DiagnosticSnapshot`.
- Produces: `DiagnosticSnapshot.to_protocol() -> dict[str, object]` containing only exact allowlisted fields.
- Consumes: protected state and trace helpers already established under `/opt/reachcommander`.

- [ ] **Step 1: Write failing schema and redaction tests**

Cover exact check names/statuses, fixed reason codes, source counts rather than names, digest comparison without digest output, non-blocking trace projection, hostile command output containing paths/tokens/addresses, per-command timeout, total timeout, and partial collection.

```python
snapshot = HostDiagnosticCollector(
    install_root,
    command_runner=fake_runner,
    clock=fake_clock,
).collect()
payload = snapshot.to_protocol()
assert set(payload) == {
    "schemaVersion", "generatedAt", "complete", "updaterProtocolVersion",
    "channel", "currentVersion", "operationId", "trace", "checks",
}
assert "secret" not in json.dumps(payload)
assert all(set(check) == {"name", "status", "reasonCode"} for check in payload["checks"])
```

- [ ] **Step 2: Run the focused test and confirm RED**

Run: `python -m unittest tests.installer.test_support_bundle -v`

Expected: import failure for `deploy.support_bundle`.

- [ ] **Step 3: Implement the bounded collector**

Use immutable dataclasses and closed enum/string tables. Read protected state with no-follow size-bounded helpers. Run only fixed command tuples through an injected runner with the remaining global deadline and a two-second per-command cap. Convert command results to `healthy`, `warning`, `failed`, `timedOut`, `unavailable`, or `notApplicable`; never place command text or exception text in a model.

```python
@dataclass(frozen=True, slots=True)
class DiagnosticCheck:
    name: str
    status: str
    reason_code: str

@dataclass(frozen=True, slots=True)
class DiagnosticSnapshot:
    generated_at: datetime
    complete: bool
    updater_protocol_version: int
    channel: str | None
    current_version: str | None
    operation_id: str | None
    trace: Mapping[str, object] | None
    checks: tuple[DiagnosticCheck, ...]
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run: `python -m unittest tests.installer.test_support_bundle -v`

Expected: all collector tests pass without printing hostile fixture values.

- [ ] **Step 5: Commit the collector**

```bash
git add deploy/support_bundle.py tests/installer/test_support_bundle.py
git commit -m "feat: collect sanitized deployment diagnostics"
```

---

### Task 2: Protocol-v4 diagnostic action and CLI ZIP output

**Files:**
- Modify: `deploy/updater_protocol.py`
- Modify: `deploy/updater_service.py`
- Create: `deploy/support_bundle_cli.py`
- Modify: `deploy/reachcommander`
- Modify: `deploy/install.sh`
- Modify: `deploy/package-installer.sh`
- Modify: `tests/installer/test_updater_protocol.py`
- Modify: `tests/installer/test_updater_service.py`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/test_package.sh`

**Interfaces:**
- Consumes: `HostDiagnosticCollector.collect()` from Task 1.
- Produces: protocol-v4 `collectDiagnostics` response with exact fields `protocolVersion`, `requestId`, and `diagnostics`.
- Produces: `support_bundle_cli.py --stdout`, invoked only by `reachcommander support-bundle`.

- [ ] **Step 1: Add failing request/response and CLI contract tests**

Assert that v4 accepts only `collectDiagnostics`, older protocols reject it, v1-v3 check/apply response shapes remain byte-for-field compatible, collection does not acquire the update lock, and diagnostics remain available while a fake update worker blocks. For CLI, assert no arguments are accepted, TTY output is refused, redirected stdout begins with the ZIP signature, standard error contains no binary bytes, and the archive has exactly five entries.

```python
request = UpdaterRequest.parse(
    b'{"protocolVersion":4,"requestId":"11111111-1111-1111-1111-111111111111",'
    b'"action":"collectDiagnostics"}\n'
)
self.assertEqual("collectDiagnostics", request.action)
```

- [ ] **Step 2: Run focused protocol/service/shell tests and confirm RED**

Run:

```bash
python -m unittest tests.installer.test_updater_protocol tests.installer.test_updater_service -v
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_command.sh
```

Expected: v4 action and CLI command are missing.

- [ ] **Step 3: Implement protocol routing and ZIP writer**

Route `collectDiagnostics` before the update journal/status path. Serialize the strict snapshot under `diagnostics`; preserve existing protocol-specific status serialization for v1-v3. Build CLI ZIP entries with deterministic names, UTF-8, bounded JSON/text, and no filesystem staging. Refuse `sys.stdout.isatty()` and write bytes to `sys.stdout.buffer` only.

```python
if request.protocol_version == 4 and request.action == "collectDiagnostics":
    return diagnostics_response(request, collector.collect())
```

- [ ] **Step 4: Install and package both new Python files**

Install `support_bundle.py` as mode `0644` under `lib/` and `support_bundle_cli.py` as mode `0755` under `bin/`. Add both to required-file, staged-deployment, backup/recovery, package manifest, archive mode, doctor, and installer preservation contracts.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run:

```bash
python -m unittest tests.installer.test_support_bundle tests.installer.test_updater_protocol tests.installer.test_updater_service -v
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_command.sh
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_install.sh
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_package.sh
```

Expected: all pass; ZIP inspection shows exactly the five approved entries.

- [ ] **Step 6: Commit host protocol and CLI**

```bash
git add deploy tests/installer
git commit -m "feat: expose sanitized support bundles on Ubuntu"
```

---

### Task 3: Strict .NET gateway and bounded ZIP service

**Files:**
- Create: `src/ReachCommander.Application/SystemUpdates/SystemUpdateSupportBundle.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateDiagnosticsGateway.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateSupportBundleService.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/UnixSystemUpdaterTransport.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateDiagnosticsGatewayTests.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateSupportBundleServiceTests.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs`

**Interfaces:**
- Produces: `ISystemUpdateSupportBundleService.CreateAsync(CancellationToken) -> Task<SystemUpdateSupportBundle>`.
- Produces: `SystemUpdateSupportBundle(string FileName, byte[] Content)`.
- Produces: `ISystemUpdateDiagnosticsGateway.CollectAsync(CancellationToken) -> Task<SystemUpdateDiagnosticsSnapshot>`.
- Changes: `ISystemUpdaterTransport.ExchangeAsync(string request, int maximumResponseBytes, CancellationToken)`; status requests pass 65,536 and diagnostics pass 262,144.

- [ ] **Step 1: Write failing strict-parser and ZIP tests**

Test exact fields, duplicate/unknown fields, check-name/status/reason allowlists, maximum count and response bytes, timestamp/UUID/version bounds, protocol mismatch, unavailable transport partial fallback, five exact ZIP entries, one-MiB uncompressed limit, deterministic safe filenames, cancellation, and prohibited-value absence.

```csharp
var bundle = await service.CreateAsync(TestContext.Current.CancellationToken);
using var archive = new ZipArchive(new MemoryStream(bundle.Content));
Assert.Equal(
    ["README.txt", "deployment-health.json", "manifest.json", "summary.txt", "update-trace.json"],
    archive.Entries.Select(entry => entry.FullName).Order().ToArray());
```

- [ ] **Step 2: Run focused unit tests and confirm RED**

Run: `dotnet test tests/ReachCommander.UnitTests --filter "FullyQualifiedName~SystemUpdateDiagnostics|FullyQualifiedName~SystemUpdateSupportBundle"`

Expected: missing model/service/gateway types.

- [ ] **Step 3: Implement models and exact protocol parsing**

Use sealed records with enum values for diagnostic status. Send protocol v4 `collectDiagnostics` with a generated request ID. Validate the exact top-level and nested field sets, maximum 64 checks, bounded logical strings, UTC timestamps, optional UUID, trace schema through shared validation, and no arbitrary public detail strings.

- [ ] **Step 4: Implement partial fallback and ZIP packaging**

Catch only `SystemUpdaterUnavailableException` and `SystemUpdaterProtocolException` to construct a fixed partial snapshot. Serialize camel-case JSON into a `MemoryStream`, count entry source bytes before writing, reject totals above 1,048,576 bytes, and produce only fixed entry names. Generate summary and README from constant templates and enumerated results.

- [ ] **Step 5: Register services and preserve status transport behavior**

Register the real diagnostic gateway only when the existing updater socket is configured; otherwise register the unavailable implementation. Register the support-bundle service independently from the background update coordinator. Update status gateway tests to prove requests still use the 65,536-byte limit and exact v1-v3 fallback.

- [ ] **Step 6: Run unit tests and verify GREEN**

Run:

```bash
dotnet test tests/ReachCommander.UnitTests --filter "FullyQualifiedName~SystemUpdate"
dotnet test tests/ReachCommander.UnitTests --filter "FullyQualifiedName~UnixSystemUpdaterGateway"
```

Expected: all system-update and transport tests pass.

- [ ] **Step 7: Commit the application service**

```bash
git add src/ReachCommander.Application src/ReachCommander.Infrastructure tests/ReachCommander.UnitTests
git commit -m "feat: build bounded update support bundles"
```

---

### Task 4: Authenticated download API and rate limit

**Files:**
- Modify: `src/ReachCommander.Api/Controllers/SystemUpdatesController.cs`
- Modify: `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`
- Modify: `tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**
- Consumes: `ISystemUpdateSupportBundleService.CreateAsync` from Task 3.
- Produces: `POST /api/system-update/support-bundle`, `application/zip`, attachment filename, no-store and nosniff headers.

- [ ] **Step 1: Write failing API security and archive tests**

Test unauthenticated `401`, missing antiforgery rejection, non-empty body `400`, success `200`, content type/disposition/cache/nosniff headers, exact ZIP entries, partial ZIP success, cancellation, and dedicated fixed-window rate limiting.

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system-update/support-bundle");
request.Headers.Add("X-ReachCommander-CSRF", antiforgeryToken);
var response = await client.SendAsync(request, cancellationToken);
Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
```

- [ ] **Step 2: Run the API tests and confirm RED**

Run: `dotnet test tests/ReachCommander.IntegrationTests --filter "FullyQualifiedName~SystemUpdatesApiTests"`

Expected: endpoint returns 404.

- [ ] **Step 3: Implement endpoint and dedicated rate policy**

Inject both system-update services. Add `[EnableRateLimiting(AuthenticationConfiguration.SupportBundlePolicy)]` to the endpoint, enforce no body with the existing helper, set `X-Content-Type-Options: nosniff`, and return `File(content, "application/zip", fileName)`. Configure three requests per minute per remote client and return fixed `support_bundle_rate_limited` problem details for this route.

- [ ] **Step 4: Run integration and complete backend tests**

Run:

```bash
dotnet test tests/ReachCommander.IntegrationTests --filter "FullyQualifiedName~SystemUpdatesApiTests"
dotnet test ReachCommander.slnx
```

Expected: all backend tests pass.

- [ ] **Step 5: Commit the API**

```bash
git add src/ReachCommander.Api tests/ReachCommander.IntegrationTests
git commit -m "feat: download authenticated update diagnostics"
```

---

### Task 5: Download diagnostics from the blocking Angular overlay

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-support-bundle.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-support-bundle.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts`

**Interfaces:**
- Produces: `CommanderApiPort.downloadSystemUpdateSupportBundle() -> Promise<SystemUpdateSupportBundleDownload>`.
- Produces: `SystemUpdateSupportBundleDownload { content: Blob; fileName: string }`.
- Produces: overlay-local downloader signals `pending` and `error`, plus `download()`.

- [ ] **Step 1: Write failing HTTP and downloader tests**

Assert POST with `responseType: blob` and full response observation, strict safe filename parsing with a timestamp fallback, one active request, object-URL creation/revocation, anchor download, success reset, fixed failure text, and reset after logout/destruction.

```typescript
const promise = api.downloadSystemUpdateSupportBundle();
const request = http.expectOne('/api/system-update/support-bundle');
expect(request.request.method).toBe('POST');
request.flush(zipBlob, { headers: { 'Content-Disposition': 'attachment; filename="reachcommander-support-20260827T120000Z.zip"' } });
```

- [ ] **Step 2: Run focused Angular tests and confirm RED**

Run: `npm test -- --watch=false --include='**/reach-commander-api.spec.ts' --include='**/system-update-support-bundle.spec.ts' --include='**/system-update-overlay.component.spec.ts'`

Expected: missing API method/downloader/button.

- [ ] **Step 3: Implement browser download behavior**

Use `HttpClient.post(..., { observe: 'response', responseType: 'blob' })`; accept only `reachcommander-support-[0-9]{8}T[0-9]{6}Z.zip`, otherwise use a local UTC fallback. Create a hidden anchor, click once, remove it, and revoke the object URL in `finally`. Do not store bundle bytes in signals or browser storage.

- [ ] **Step 4: Add overlay button and states**

Place **Download diagnostics** inside Technical details. Keep it rendered for applying/completed/rolledBack/failed states, disable it only while its own request is pending, keep the overlay open, and show `Preparing diagnostics…` plus an `aria-live` error with `sudo reachcommander support-bundle > reachcommander-support.zip` fallback guidance.

- [ ] **Step 5: Run focused and full frontend verification**

Run:

```bash
npm test -- --watch=false
npm run build
```

Expected: all Angular tests pass and production build succeeds.

- [ ] **Step 6: Commit the Angular experience**

```bash
git add client/reach-commander-ui
git commit -m "feat: download diagnostics from update screen"
```

---

### Task 6: Browser acceptance, operations documentation, and full verification

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `tests/e2e/specs/system-update.spec.ts`
- Modify: `README.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `docs/operations.md`

**Interfaces:**
- Verifies the complete feature from authenticated overlay to ZIP response.

- [ ] **Step 1: Add failing browser acceptance coverage**

Intercept the support-bundle POST and return a small ZIP fixture. Verify the button is available while applying, after stale activity, during rollback, and on failure; clicking does not close the overlay; duplicate clicks are suppressed; failures show CLI guidance; compact viewport and both themes retain the control.

- [ ] **Step 2: Run targeted Playwright and confirm RED**

Run: `npm test -- --project=chromium specs/system-update.spec.ts --grep "diagnostics"` from `tests/e2e`.

Expected: support-bundle control is absent.

- [ ] **Step 3: Document operator workflow**

Add UI and CLI collection commands, privacy exclusions, partial-bundle behavior, the fact that no upload occurs, and the installer-refresh requirement for protocol v4. Keep raw-log commands as advanced root-only follow-up evidence rather than bundle content.

- [ ] **Step 4: Run complete verification**

Run:

```bash
python -m unittest discover -s tests/installer -p "test_*.py" -v
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_common.sh
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_install.sh
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_command.sh
"C:/Program Files/Git/bin/bash.exe" tests/installer/test_package.sh
dotnet test ReachCommander.slnx
npm test -- --watch=false
npm run build
npm test
git diff --check
```

Run frontend commands from `client/reach-commander-ui` and the last `npm test` from `tests/e2e`. Expected: all suites pass. On Windows, record Linux-only process/signal/socket skips transparently; Ubuntu CI remains authoritative for them.

- [ ] **Step 5: Perform privacy and repository checks**

Inspect generated ZIP strings with hostile fixtures and verify none of the prohibited values occur. Confirm no Docker daemon restart command, Docker socket mount, or untracked asset was introduced.

```bash
rg -n "systemctl restart docker|service docker restart|/var/run/docker.sock" deploy src
git status --short
git diff --check
```

- [ ] **Step 6: Commit documentation and acceptance tests**

```bash
git add README.md docs/deployment/ubuntu.md docs/operations.md tests/e2e
git commit -m "test: verify sanitized updater support bundles"
```

- [ ] **Step 7: Request final code review**

Ask a reviewer to inspect the complete support-bundle commit range for data disclosure, protocol compatibility, unbounded work/memory, unsafe ZIP behavior, update-worker contention, authentication/antiforgery/rate-limit regressions, Docker daemon changes, and accidental inclusion of `NC-theme.png`.
