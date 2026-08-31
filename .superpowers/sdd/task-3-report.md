# Task 3 report: Restricted source-management socket runtime

## Status

Implementation and verification are complete on `master`. The scoped commit is created after this report is written. Nothing is pushed.

## Delivered behavior

- Routes only source-management protocol v5 actions (`status`, `addSource`, and `getOperation`) through the existing group-restricted Unix socket. Existing updater and diagnostic request handling remains compatible.
- Discovers three explicit deployment states: supported installer-managed deployment, installer-managed deployment requiring a one-time latest-installer rerun, and unsupported deployment/platform.
- Reconstructs one fixed host command, `/usr/local/bin/reachcommander source add`, and supplies only bounded canonical JSON on stdin. Browser input cannot select a command, environment, image, configuration path, or host path.
- Accepts a valid source request quickly, executes it asynchronously, and persists one exact public operation record at `state/source-runtime-operation.json`.
- Keeps the runtime journal distinct from the source helper's recovery transaction journal at `state/source-operation.json`. The helper journal is read-only to the service and may only be projected into bounded public progress.
- Serializes source operations and applies a shared in-process mutation gate so an update cannot start during source mutation and source mutation cannot start during an update.
- Maps helper validation, rollback, timeout, launch, output, and unexpected failures to bounded public states without returning raw host paths, helper output, or private diagnostics.
- Eagerly converts a nonterminal runtime record found after updater-service restart into a bounded terminal failure before a new add can overwrite it. An unsafe unreadable startup record fails closed.
- Hardens runtime storage with exact duplicate-safe parsing, unknown-field rejection, size bounds, symlink/nonregular rejection, atomic same-directory replacement, `0600` files, `0700` state directory, and file-plus-parent `fsync`.
- Retains installer-root write access required by the update/source helpers' atomic sibling-file replacements, while adding nested systemd read-only protection for `bin`, `lib`, and application `data`. No Docker socket is added to the application container.
- Preserves the runtime journal through reconfiguration, rollback, backup, and uninstall allowlists.

## Strict TDD evidence

### RED

Tests were introduced before the implementation and initially failed because the source runtime, discovery, mutation gate, operation store, and v5 socket dispatch did not exist. Additional hardening RED cases exposed and drove fixes for:

- source-helper journal projection and noninterference;
- stale nonterminal operation recovery after service restart, including eager reconciliation before a new add;
- canonical helper-output validation and requested display-name correlation;
- release of the mutation gate when update startup fails;
- fail-closed v5 routing when duplicate top-level protocol versions are supplied;
- backup/uninstall allowlisting for the distinct runtime journal;
- successful reconfiguration preserving the runtime journal byte-for-byte.

An initially proposed systemd assertion that allowed writes only to exact files was rejected before production implementation because both update and source helpers require same-directory temporary-file creation and atomic rename. The final contract retains the necessary installer-root directory write semantics and protects immutable/nested trees explicitly.

### GREEN

- `python -m unittest tests.installer.test_updater_protocol tests.installer.test_updater_service tests.installer.test_support_bundle tests.installer.test_updater_trace`
  - 107 tests passed; 9 expected Windows skips for Unix/POSIX-only behavior.
- `tests/installer/test_install.sh`
  - 33/33 passed; expected Windows skips for POSIX filesystem properties.
- `tests/installer/test_command.sh`
  - 52/52 passed; one expected Windows skip for POSIX sessions.
- `tests/installer/test_package.sh`
  - 7/7 passed.
- `tests/installer/test_common.sh`
  - 17/17 passed.
- `python -m py_compile deploy/updater_service.py tests/installer/test_updater_service.py`
  - passed.
- `bash -n deploy/reachcommander tests/installer/test_command.sh tests/installer/test_install.sh`
  - passed.
- `git diff --check`
  - passed.

`shellcheck` and `systemd-analyze` are not installed in the local Windows environment. The shell behavior and systemd contract are covered by the repository harnesses; Linux CI remains the authoritative platform execution.

## Security and lifecycle review

- The v5 routing probe preserves duplicate top-level JSON pairs and does not weaken the authoritative exact protocol parser or message-size enforcement.
- The runner always uses an exact argv with `shell=False`, a sanitized fixed environment, bounded stdin, bounded timeout, process-tree termination, and bounded output reads.
- The operation store never shares or overwrites the helper's recovery transaction file.
- Unsafe state paths fail closed; existing symlinks, nonregular entries, unexpected keys, duplicate keys, wrong ownership/modes on POSIX, and oversized state are rejected.
- Public state and errors use enumerated reasons and generic bounded detail; raw command output and host paths are not logged or persisted.
- Source/update exclusion is enforced in both directions for one service process. The management command's existing host lock remains the cross-process authority.
- Update, diagnostic, support-bundle, and trace response schemas are unchanged.
- Reconfiguration replaces only fixed deployment files, so both source journals and trace/state artifacts survive successful reconfiguration and rollback.

## Files changed

- `deploy/updater_service.py`
- `deploy/systemd/reachcommander-updater.service`
- `deploy/reachcommander`
- `tests/installer/test_updater_service.py`
- `tests/installer/test_command.sh`
- `tests/installer/test_install.sh`
- `.superpowers/sdd/task-3-report.md`
- `.superpowers/sdd/progress.md`

The unrelated untracked `NC-theme.png` was not touched or staged.
