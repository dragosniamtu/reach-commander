# Task 2 report: durable installer-managed source transaction

## Implementation

- Added the fixed `reachcommander source add` action. It accepts only the Task 1 bounded protocol-v5 JSON request on stdin, acquires the existing serialized deployment lock, and invokes fixed installer-owned Python files without evaluating or interpolating request data in shell.
- Added `deploy/source_management.py` for canonical path and overlap validation, exact broad/protected-root rejection, collision-safe normalized IDs, the 32-source cap, runtime UID/GID access checks with supplementary groups cleared, and fail-closed installer-state validation.
- Reused renderer invariants through `load_installed_request` and `append_source`. The transaction stages the catalog, mount state, and Compose file from the installed trusted template; validates with `docker compose config`; atomically publishes managed files; recreates only `reachcommander`; and verifies both the running image identity and health.
- Added a protected same-filesystem backup and bounded durable operation journal. Failure after publication restores the exact files and verifies the previous service; failed recovery preserves the backup. A later source add recovers interrupted publication before starting a new operation. One canonical transaction UUID is retained across all journal phases.
- Installed and packaged the source helper and renderer template with exact modes. Reconfiguration and recovery allowlists include both files, while older installations missing them fail closed until the installer performs its one-time upgrade. Updates and installer reconfiguration reject an outstanding source transaction.
- Public success includes only generated source ID and display name. Public failures use fixed bounded codes/details and never include host paths or captured command output.

## Strict TDD evidence

1. Initial focused RED:

   ```text
   python -m unittest tests.installer.test_render_config.RendererTestCase.test_load_installed_request_and_append_source_reuse_renderer_invariants tests.installer.test_render_config.RendererTestCase.test_load_installed_request_rejects_catalog_mount_mismatch tests.installer.test_source_management -v
   ImportError / AttributeError: deploy.source_management and load_installed_request did not exist
   FAILED
   ```

2. Fixed command RED:

   ```text
   tests/installer/test_command.sh
   malformed source request status: expected 2, got 64
   ```

3. Package RED:

   ```text
   tests/installer/test_package.sh
   archive missing source_management.py
   ```

4. Crash-state integration REDs:

   ```text
   installer: expected failure while backups/.source-transaction exists, got success
   command: update while backups/.source-transaction exists expected 1, got 0
   ```

5. Dependency-preflight RED:

   ```text
   missing installed renderer dependency expected status 1, got Python validation status 2
   ```

6. Journal identity RED:

   ```text
   validating transactionId 12345678-... != staging transactionId 87654321-...
   ```

All REDs were captured before the corresponding production changes. Focused GREEN coverage includes successful publication, Compose validation failure, unhealthy rollback, failed rollback persistence, interrupted recovery, journal identity validation, path/count/ID/access rules, installer state hardening, dependency preflight, shared-lock contention, and output sanitization.

## Final verification

| Gate | Result |
|---|---|
| `python -m unittest tests.installer.test_source_management tests.installer.test_render_config tests.installer.test_updater_protocol -v` | 66 passed, 3 Windows capability skips |
| `tests/installer/test_command.sh` | 46/46 passed |
| `tests/installer/test_install.sh` | 33/33 passed, 2 Windows capability skips |
| `tests/installer/test_package.sh` | 7/7 passed |
| `tests/installer/test_common.sh` | 17/17 passed |
| `bash -n` on changed production/test shell files | passed |
| `python -m py_compile` on source manager, renderer, and protocol | passed |
| `git diff --check` | passed |

ShellCheck was not installed in either PowerShell or Git Bash on this Windows host. The skipped tests require POSIX ownership/modes or symlink privileges unavailable here; their fail-closed logic remains covered by platform-neutral unit assertions where possible.

## Files

- `deploy/source_management.py`
- `deploy/render_config.py`
- `deploy/reachcommander`
- `deploy/install.sh`
- `deploy/package-installer.sh`
- `tests/installer/test_source_management.py`
- `tests/installer/test_render_config.py`
- `tests/installer/test_command.sh`
- `tests/installer/test_install.sh`
- `tests/installer/test_package.sh`
- `.superpowers/sdd/task-2-report.md`

## Self-review and concerns

- Source data crosses process boundaries only as strict JSON or as an argv element to a fixed access-check script; no request value is shell source.
- Docker commands are fixed argv lists, captured output is not returned publicly, and only the application service is recreated.
- Atomic writer directory fsync behavior is reused; backup files and transaction directories are also fsynced before publication.
- `NC-theme.png` remains untouched and unstaged.
- Ubuntu-only owner/mode, symlink, `setpriv`, Compose, and service behavior cannot be exercised natively on this Windows host. The Git Bash harnesses use fixed fakes, and the missing ShellCheck binary is the only planned static gate not run.
