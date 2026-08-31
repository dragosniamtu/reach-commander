# Task 4 report: .NET source-management gateway and authenticated API

## Status

Implementation and local verification are complete on `master`. Nothing is pushed. The first independent review found two restart-drain safety issues and one strict-framing issue; all three are fixed and covered by deterministic regressions. Final independent re-review follows the hardening commit.

## Delivered behavior

- Added platform-neutral source-management capability, request, operation, phase, access, exception, gateway, service, eligibility, request-ID, and monitor-delay contracts.
- Added a coordinator that serializes source changes, validates the narrow request shape, rejects active system updates, and reuses the existing restart-operation probe plus mutation gate before and after draining.
- Excluded only `POST /api/source-management/sources` from the general mutation-lease middleware so the coordinator can own the drain without waiting on its own request. Ordinary file mutations remain blocked while a source restart is active.
- Retains the drain for accepted nonterminal operations and starts a bounded internal monitor. Browser polling is not required to release the drain after host validation failure or another terminal result. A reconnect can join an already active operation, and timeout, failure, cancellation, or service disposal releases the local drain.
- Fixed an atomic monitor handoff race so completion of one operation cannot orphan the monitor for a newly accepted operation.
- Tracks monitor ownership by operation ID, so a terminal browser poll followed immediately by another accepted operation starts a new monitor even while the prior monitor is still unwinding.
- Replaced the shared Boolean drain with an exclusive owner-scoped async lease used by both source changes and system updates. A concurrent restart cannot acquire a second drain, and disposing a stale lease cannot reopen a successor's drain.
- Shields caller cancellation after a source mutation begins transmission. A post-send disconnect or timeout is classified as an ambiguous outcome, returns only a sanitized public failure, and retains the exact drain lease for a bounded safety window so file mutations cannot race a host restart.
- Added a strict source-management v5 Unix-socket gateway on the existing bounded transport. It emits only fixed actions and narrow JSON, enforces a 4 KiB message cap, exact duplicate-free schemas, canonical request/operation UUID correlation, exact action/version matching, phase/reason/detail allowlists, source identity rules, UTC timestamp order, and sanitized host errors.
- Hardened the shared Unix transport to require exactly one newline-terminated frame followed by EOF and to reject malformed UTF-8 instead of replacement-decoding it.
- Maps an older host protocol to the explicit `supported: false`, `installer_upgrade_required` capability. Other request/action/identifier correlation failures remain fail-closed.
- Registers the Unix gateway only for enabled Linux deployments whose configured installer socket exists. Windows, macOS, and manual/missing-socket deployments receive a platform/deployment-specific unavailable gateway without probing privileged host state.
- Added authenticated status, add, and operation endpoints. Mutation is protected by the existing automatic antiforgery filter and a dedicated fixed-window, per-remote-IP rate limit with a stable sanitized 429 response.
- Added a dedicated exception handler with stable public status/code/detail mappings. No Compose content, Docker command/output, socket path, requested host path, or internal exception detail is returned.
- The source catalog remains immutable in-process; no source configuration is hot-edited by the API.

## Strict TDD evidence

### Initial application/gateway RED

The focused unit suite was added before production source-management types existed. It failed with `CS0234`/`CS0246` for the missing application namespace, coordinator, gateway, and request-ID abstractions.

### API RED

The controller integration suite was added before the API surface. Eleven behavior cases failed at the secured unmatched API route with `404 Not Found`; the unauthenticated fallback-policy case already returned `401`, confirming the test host used the real authorization boundary.

### Hardening REDs

- An accepted operation that reached a terminal host state without browser polling retained the drain because no internal monitor existed.
- A deterministic completion/acceptance race timed out because the first monitor cleared operation state before clearing its task, leaving the next operation without a monitor.
- An old protocol-v3 helper response threw a generic incompatibility failure instead of returning the installer-upgrade capability.
- Numeric JSON access value `0` was accepted as `readOnly` by the global enum converter instead of being rejected as an invalid public contract.
- A terminal browser operation poll could release operation one while its monitor was still active; operation two then saw the stale monitor and received no monitor of its own.
- The trailing-slash form of the Add route retained a general mutation lease, causing the source coordinator to wait on its own request. The optional single trailing slash is now recognized without exempting arbitrary child paths.
- The shared Boolean drain allowed source/update overlap and let delayed cleanup reopen a newer restart's gate. An exclusive owner lease now rejects the second drain and makes stale disposal idempotent.
- Caller cancellation or a transport disconnect after host acceptance could release the drain with the host worker still running. Submission is now cancellation-shielded after preflight, and post-send uncertainty retains the owner lease for a bounded fail-closed window.
- The Unix transport accepted a JSON prefix before a newline while ignoring trailing frames and replacement-decoded invalid UTF-8. It now requires EOF after the sole frame and uses exception-fallback decoding.
- A syntactically received Add response with duplicate fields or broken request/action/operation correlation was still treated as a definite rejection. Only an exact, fully validated host `error` frame is now definite; every other Add response-validation failure is classified as ambiguous and retains the bounded safety drain.
- Add response protocol-version mismatch used the status/read incompatibility path and could release the mutation drain. Status still maps an old helper to the installer-upgrade capability, while Add now classifies a version mismatch as an ambiguous outcome and retains the drain.

Each RED failed for the expected missing or unsafe behavior before the corresponding production change. All focused regressions are now GREEN.

## Security and architecture review

- Authentication is enforced by the existing fallback policy; source-management has no anonymous override.
- Cookie-authenticated POST requests retain global antiforgery validation.
- Browser input cannot select a command, Compose fragment, image, container path, socket path, or environment variable. The gateway sends only `displayName`, absolute Ubuntu `hostPath`, and exact `readOnly`/`readWrite` access to one fixed action.
- Numeric enum coercion is rejected at the API boundary by using an exact string DTO contract.
- The Unix socket transport is reused only when the existing installer-managed Linux socket configuration is enabled and present; the Docker socket remains unrelated and unmounted.
- The existing operation probe remains the authority for tracked copy/move/trash jobs and archive extractions. Request-scoped upload, rename, delete, and directory work remains represented by mutation-gate leases.
- Source and update coordinators both recheck their observable state; the host v5 shared mutation gate remains the final cross-process authority for the narrow race after application checks.
- Public responses use only fixed allowlisted reason/detail values. Logs record stable codes and exception types, not the requested host path or privileged output.

## Verification

| Gate | Result |
|---|---|
| Focused source-management unit tests | 33/33 passed |
| Focused source-management API integration tests | 14/14 passed |
| Full Debug unit tests | 677/677 passed |
| Full Debug integration tests before final numeric-contract regression | 124/124 passed |
| Impacted source/update unit tests | 94/94 passed |
| Impacted source/update API integration tests | 28/28 passed |
| `dotnet test ReachCommander.slnx -c Release --no-restore` | 689 unit + 126 integration passed; zero failures/skips |
| `dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/task4-publish -p:BuildAngularOnPublish=false` | passed |
| Scoped `dotnet format ... whitespace --verify-no-changes --no-restore` | passed |
| `git diff --check` | passed |

The Linux-only real Unix-domain-socket exchange remains behind the existing transport abstraction. Its native socket behavior executes on Linux CI; the Windows matrix exercises the strict gateway through the complete transport contract and the unavailable default.

## Files

- `src/ReachCommander.Application/SourceManagement/*`
- `src/ReachCommander.Infrastructure/SourceManagement/*`
- `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationProbe.cs`
- `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- `src/ReachCommander.Api/Contracts/SourceManagement/SourceManagementDtos.cs`
- `src/ReachCommander.Api/Controllers/SourceManagementController.cs`
- `src/ReachCommander.Api/Errors/SourceManagementExceptionHandler.cs`
- `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`
- `src/ReachCommander.Api/SystemUpdates/SystemMutationGateMiddleware.cs`
- `src/ReachCommander.Api/Program.cs`
- `tests/ReachCommander.UnitTests/SourceManagement/*`
- `tests/ReachCommander.IntegrationTests/SourceManagementApiTests.cs`
- `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- `.superpowers/sdd/task-4-report.md`
- `.superpowers/sdd/progress.md`

The unrelated untracked `NC-theme.png` was not touched or staged.
