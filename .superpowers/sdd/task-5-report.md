# Task 5 report: Angular Add source and reconnect flow

## Status

Implementation and local verification are complete on `master`. The original slice is commit `83107b8`, lifecycle hardening is commit `8a3e421`, timeout hardening is commit `8f2eda8`, and the final deadline-value coverage commit is created after this report update. Nothing is pushed. Independent final confirmation by the parent workflow remains pending.

## Delivered behavior

- Added typed Angular contracts and client methods for source-management capability discovery, one narrow source-add request, and operation status polling.
- Added a protected-state-aware source-management store with single-start capability loading, duplicate-submission prevention, injected deterministic polling, bounded exponential reconnect handling, terminal success/rollback/failure presentation, and actionable timeout guidance.
- Added a full-screen blocking, focus-trapped Add source dialog. It restores its toolbar opener on close and blocks Escape, Enter resubmission, and closing while submission, restart, reconnect, or catalog refresh is active.
- Validates a trimmed 1–80 character display name and a bounded absolute Ubuntu path. The UI rejects broad roots, backslashes, controls, and obvious protected system trees; the trusted host remains authoritative for canonicalization, existence, overlap, permissions, and installer-state checks.
- Defaults every source to read-only. Choosing read/write reveals a persistent warning and requires a separate explicit acknowledgement that ReachCommander can change or delete files in the mapped host folder.
- Added one compact **Add source** control on the left-side active-panel toolbar. Unsupported/manual/old-helper deployments keep it disabled inside a keyboard-focusable wrapper whose tooltip and accessible description expose the precise backend capability reason.
- Added phase-specific progress copy for validation, configuration, restart, health checking, reconnect, completion, rollback, and failure. Only bounded API problem details are surfaced; arbitrary exception text is replaced with fixed client copy.
- Replaces the in-memory source catalog from a fresh `/api/sources` response after host completion. Both pane selectors receive the new immutable list without merging stale definitions, while current pane locations are retained.
- Holds the blocking state until the completed operation's catalog refresh finishes, preventing a user from dismissing the dialog before the source is visible.
- Confirms completion against the generated `sourceId` in each freshly replaced catalog. Missing IDs and transient catalog failures use a separate deterministic 12-attempt retry budget; success is never inferred from a stale or merged list, and exhaustion returns fixed page-refresh plus `reachcommander doctor` guidance.
- Restricts dialog-wide Enter handling to text inputs. Cancel, access radios, and the read/write acknowledgement keep their native keyboard behavior and cannot accidentally submit.
- Moves focus to a stable, tabbable in-dialog progress target when the form becomes an operation and again on each terminal transition. The CDK focus trap returns attempted Tab traversal to the dialog instead of the background toolbar.
- Makes capability discovery retryable after structured server errors or connection failures. Shell reconnect retries call discovery again, and a capability-only failure turns the existing compact toolbar control into an explicit retry action for the current session.
- Gives each read-only capability, operation-status, and catalog request a bounded 15-second deadline through a separate injected timer. Operation timeouts consume the existing reconnect budget; catalog timeouts consume their independent refresh budget; capability timeouts release the startup latch and enable the existing toolbar retry.
- Keeps the mutation POST deliberately outside the read-deadline mechanism so an ambiguous accepted/unknown outcome is never presented as a safe retry. Optional capability discovery runs beside, but never blocks, required shell initialization and task restoration.
- Cancels read-deadline handles separately from scheduled poll handles on authentication reset and store destruction. Both resolution and rejection handlers stay attached to expired/cancelled requests, so late settlement cannot mutate current state or produce an unhandled rejection.
- Added source-management help copy and explicit Norton/Windows 95 dialog/backdrop compatibility. Compact toolbar, Norton, and Windows 95 browser acceptance remain green.

## Strict TDD evidence

### Initial RED

The six focused API, store, dialog, toolbar, shell, and commander-store specs were introduced before production code. Angular compilation failed exactly for the missing source request/capability/operation models, client methods, `CommanderStore.reloadSourceCatalog`, source-management store, source dialog, and toolbar output.

### Hardening RED

After the first GREEN pass, focused regressions proved two lifecycle/input gaps:

- a completed operation marked itself dismissible before its deferred source-catalog reload finished;
- `/proc`, `/sys`, `/dev`, `/run`, and `/var/run` descendants passed client validation.

Both REDs failed for the expected behavior and are now GREEN. Catalog refresh keeps the modal pending until replacement succeeds or a bounded public refresh error is available, and protected system paths receive immediate user-facing validation while host validation remains authoritative.

### Final-review REDs

Final review identified four gaps. New regressions failed before the fixes because:

- any successful `/api/sources` request marked the operation refreshed even when its returned replacement list did not contain the host-generated source ID, and no bounded catalog retry existed;
- the host-level Enter listener submitted from Cancel, radio, and checkbox focus;
- replacing the form with progress content left focus on `body`, with no explicit terminal focus transition;
- a failed capability request left the store's `started` latch set and shell reconnect did not retry discovery.

The focused review suite initially failed all seven affected behavioral cases. A separate follow-up RED also proved that structured 503 problem responses must expose retry, not only status-zero connection failures. The final implementation uses separate reconnect and catalog counters, deterministic injected scheduling, generated-ID membership checks against fresh replacement lists, text-input-only Enter handling, explicit transition focus, focus-trap boundary coverage, retryable store startup, shell retry integration, and a same-toolbar capability retry action.

### Timeout-review REDs

The timeout review added never-settling deferred requests for every read-only path. After introducing only the injectable deadline contract, the focused store/shell suite failed five behavioral cases: capability, operation, and catalog reads registered no bounded deadline; protected reset left the read pending; and required shell task restoration remained blocked behind optional capability discovery. A cleanup regression was also verified RED with deadline cancellation temporarily removed: reset and destruction each retained one live deadline handle.

GREEN uses a dedicated injected deadline timer, distinct from the poll scheduler and poll handle. Store-owned deadline wrappers attach resolve and reject handlers before awaiting, expire into the correct capability/reconnect/catalog branch, and cancel on the shared reset/destroy invalidation path. Tests exercise late resolution and rejection after expiry/cancellation and assert that a pending mutation POST owns no read-deadline handle.

### Deadline-value coverage hardening

The deterministic deadline timer now records every requested delay, and the never-settling capability, operation-status, and catalog tests each assert exactly `15_000` milliseconds. These assertions passed immediately before any production change, confirming the intended value was already wired correctly; this is recorded as coverage hardening rather than a new RED/fix cycle.

## Verification

| Gate | Result |
|---|---|
| Initial focused Angular slice | 6 files, 131/131 tests passed |
| Final focused store/dialog hardening | 2 files, 33/33 tests passed |
| Final focused store/shell timeout slice | 2 files, 52/52 tests passed |
| Deadline-value coverage | 1 file, 17/17 tests passed; all three reads assert 15,000 ms |
| Final focused Task 5 slice after timeout hardening | 6 files, 152/152 tests passed |
| `npm test -- --watch=false` | 57 files, 461/461 tests passed |
| `npm run build` | passed; 357.41 kB initial, 94.81 kB estimated transfer |
| `npm run test:pwa` | 2/2 passed |
| `npm run verify:pwa` | passed |
| Scoped Chromium active-toolbar acceptance | 6/6 passed |
| Final scoped Chromium toolbar + Norton + Windows 95 acceptance | 15/15 passed |

## Files

- `client/reach-commander-ui/src/app/core/api/api.models.ts`
- `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- `client/reach-commander-ui/src/app/core/state/commander-store.ts`
- `client/reach-commander-ui/src/app/core/state/commander-store.spec.ts`
- `client/reach-commander-ui/src/app/core/state/source-management.store.ts`
- `client/reach-commander-ui/src/app/core/state/source-management.store.spec.ts`
- `client/reach-commander-ui/src/app/features/source-management/*`
- `client/reach-commander-ui/src/app/features/commander/active-panel-toolbar/*`
- `client/reach-commander-ui/src/app/features/commander/commander-shell/*`
- `client/reach-commander-ui/src/app/testing/commander-api-test-base.ts`
- `client/reach-commander-ui/src/styles.scss`
- `.superpowers/sdd/task-5-report.md`
- `.superpowers/sdd/progress.md`

## Remaining scope and risk

- Task 6 owns installer-managed browser fixtures for real supported RO/RW transactions, generated IDs, rollback messaging, and Docker-socket absence. Task 5's browser run intentionally exercises the unsupported testing deployment plus toolbar/theme/layout integration.
- The browser timeout is a recovery message, not an operation cancellation. The trusted host journal remains authoritative, and `reachcommander doctor`/support diagnostics are the recovery path after the browser gives up reconnecting.
- Existing installs with an older host helper correctly remain unsupported until the latest installer is rerun once; the Angular image does not claim to upgrade root-owned host integration.

The unrelated untracked `NC-theme.png` was not touched or staged.
