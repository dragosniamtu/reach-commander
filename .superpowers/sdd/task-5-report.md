# Task 5 report: Angular Add source and reconnect flow

## Status

Implementation and local verification are complete on `master`. The scoped commit is created after this report is written. Nothing is pushed. Independent review by the parent workflow remains pending.

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
- Added source-management help copy and explicit Norton/Windows 95 dialog/backdrop compatibility. Compact toolbar, Norton, and Windows 95 browser acceptance remain green.

## Strict TDD evidence

### Initial RED

The six focused API, store, dialog, toolbar, shell, and commander-store specs were introduced before production code. Angular compilation failed exactly for the missing source request/capability/operation models, client methods, `CommanderStore.reloadSourceCatalog`, source-management store, source dialog, and toolbar output.

### Hardening RED

After the first GREEN pass, focused regressions proved two lifecycle/input gaps:

- a completed operation marked itself dismissible before its deferred source-catalog reload finished;
- `/proc`, `/sys`, `/dev`, `/run`, and `/var/run` descendants passed client validation.

Both REDs failed for the expected behavior and are now GREEN. Catalog refresh keeps the modal pending until replacement succeeds or a bounded public refresh error is available, and protected system paths receive immediate user-facing validation while host validation remains authoritative.

## Verification

| Gate | Result |
|---|---|
| Initial focused Angular slice | 6 files, 131/131 tests passed |
| Final focused store/dialog hardening | 2 files, 33/33 tests passed |
| `npm test -- --watch=false` | 57 files, 446/446 tests passed |
| `npm run build` | passed; 357.41 kB initial, 94.80 kB estimated transfer |
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
