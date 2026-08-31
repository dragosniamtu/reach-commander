# Task 6 report: installer-managed acceptance and operator documentation

## Status

Implementation and local verification are complete on `master`. Nothing is pushed. The Task 6 commit is created with this report; independent review by the parent workflow remains pending.

## Delivered coverage and documentation

- Added a reusable Playwright fake for the narrow installer-managed source capability, add-operation, operation-status, and refreshed source-catalog contracts.
- Added browser acceptance for unsupported deployments with precise disabled-control guidance.
- Added a read-only success scenario that proves the default policy, one narrow request, a simulated restart disconnect, automatic reconnect, host-generated source ID, and visibility in both pane selectors.
- Added a read/write success scenario that proves the destructive warning, explicit confirmation, exact three-field request payload, host-generated ID, and writable policy in both selectors.
- Added duplicate-submission and active-operation toolbar blocking coverage.
- Added rollback coverage proving that the previous configuration message is visible and no requested mapping appears.
- Added bounded timeout/failure guidance coverage proving `reachcommander doctor` and support-diagnostics guidance without `/opt`, `/srv`, Docker, Compose, or digest leakage.
- Added package coverage proving the published Compose templates mount only `/run/reachcommander-updater` and never `/var/run/docker.sock`.
- Added the durable source-transaction Python suite to the required Ubuntu acceptance job; publication can no longer proceed without it.
- Documented the authenticated **Add source** workflow, absolute specific-child-path requirement, runtime UID/GID read/traverse/write checks, read-only default, explicit read/write risk, managed restart/reconnect/catalog refresh, serialization, rollback, troubleshooting, support diagnostics, and advanced structured CLI fallback.
- Documented that existing older installs must rerun the latest checksum-verified installer once because an image-only update cannot replace root-owned integration, while clean installs include the helper, CLI, restricted socket, and systemd service immediately.
- Corrected the README quality totals from fresh complete local evidence: 815 .NET tests, 461 Angular unit tests, 2 PWA contracts, and 64 Chromium scenarios.

## TDD and gate evidence

### RED

- The new documentation contract failed because operator documentation did not yet mention **Add source** or the required installer-managed security and recovery boundary.
- The workflow contract failed because CI did not run `tests/installer/test_source_management.py` before publication.
- The first focused browser run produced five passes and two failures. Inspection showed both failures were test-locator mistakes after the dialog's accessible name changed from form state to operation state; stable operation-dialog and actual phase-heading locators fixed the acceptance tests without changing production code.
- The package socket-boundary assertion passed immediately, recording an existing deployment invariant as a publication regression gate rather than claiming a behavior fix.

### GREEN

| Gate | Result |
|---|---|
| Focused source-management Playwright | 7/7 total: authentication setup plus 6 Chromium scenarios |
| Documentation contracts | 12/12 passed |
| Workflow contracts | 17/17 passed |
| Release/workflow/documentation Node contracts | 38/38 passed |
| Package contracts | 8/8 passed |
| Full .NET Release matrix | 689 unit + 126 integration passed; zero failures/skips |
| Full Angular matrix | 57 files, 461/461 passed |
| PWA source contracts | 2/2 passed |
| Angular production build and generated PWA verification | passed |
| Full browser acceptance | 65/65 total: 1 authentication setup + 64 Chromium scenarios |
| Python installer/updater/CI matrix | 179 total, 168 passed and 11 Windows platform skips |
| Ubuntu common shell contracts | 17/17 passed |
| Ubuntu installation shell contracts | 33/33 passed; 3 Windows POSIX skips |
| Ubuntu management-command shell contracts | 52/52 passed; 1 Windows POSIX-session skip |
| Ubuntu package shell contracts | 8/8 passed |
| macOS helper shell contracts | 10/10 passed; 2 macOS-only `plutil` skips |
| macOS installer shell contracts | 24/24 passed; 4 unavailable-symlink skips |
| Bash syntax for every CI-linted installer/helper fixture | passed |
| Release API publish with bundled archive worker | passed |
| `git diff --check` | passed |

ShellCheck and `systemd-analyze verify` are required in Ubuntu CI but were unavailable on this Windows development host. No local substitute is claimed; the workflow and its contract tests retain both gates.

## Security-boundary self-review

- Browser acceptance sends only `displayName`, `hostPath`, and `access`; generated IDs remain host output.
- Documentation does not suggest Docker-socket mounting, arbitrary Compose edits, broad root mounts, recursive permission weakening, authentication bypass, or host-helper replacement from the application image.
- The CLI fallback invokes one fixed root-owned command with exact JSON on standard input; it does not evaluate shell input or accept a command/image/container path.
- Read/write documentation explicitly requires both permission verification and destructive-risk confirmation.
- Failure acceptance asserts that privileged host paths, Docker/Compose details, and digests are absent from the visible public result.
- Package and hardened-container workflow gates independently reject `/var/run/docker.sock`; the only application-visible host integration remains the group-restricted, read-only `/run/reachcommander-updater` mount.
- Existing-install migration language fails closed: old helpers remain unsupported until the latest checksum-verified installer is rerun once.

## Files

- `tests/e2e/support/source-management-fixture.ts`
- `tests/e2e/specs/source-management.spec.ts`
- `tests/installer/docs-contract.test.mjs`
- `tests/installer/test_package.sh`
- `tests/installer/workflow-contract.test.mjs`
- `.github/workflows/ci.yml`
- `README.md`
- `docs/INSTALL.md`
- `docs/deployment/ubuntu.md`
- `deploy/README.md`
- `.superpowers/sdd/task-6-report.md`
- `.superpowers/sdd/progress.md`

The unrelated untracked `NC-theme.png` was not touched or staged.
