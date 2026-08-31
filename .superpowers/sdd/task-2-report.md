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

## Security review follow-up

- Persisted paths now have a deliberately strict pathname trust contract: the leaf is a real directory and may be owned/writable by the configured runtime identity, while every ancestor through `/` must be a real, root-owned directory with no group/other write bits. All existing sources are revalidated, including pairwise separation, before an add; leaf device/inode identities are captured and checked again immediately before publication and service recreation. The renderer must preserve the exact canonical string, and canonicalized paths are capped at 1,024 characters. This excludes otherwise-accessible sources below user-writable ancestors because such path entries are replaceable across later service restarts.
- Publication records `recovery_required` before the first live replacement. Failures injected at each of the three replacements restore the exact digest-verified backup, verify the prior service, and permit retry. Failed restoration, invalid ancestry during recovery, missing/mismatched backup state, and failed secondary journal writes retain recovery material and fail closed.
- The transaction directory is protected and its parent `backups` directory is fsynced immediately, before backup population or publication. A strict manifest binds the three backup files and their SHA-256 digests to the journal transaction UUID.
- Journal parsing rejects duplicate keys, non-canonical UUIDs, wrong JSON types, out-of-bound source/display/timestamp values, non-UTC timestamps, and invalid phase/reason combinations. Clean prepublication failure is distinguished from recovery-required failure so a valid retry is not wedged.
- The whole public Python boundary, including stdin, renderer append/render, and initial/error journal operations, maps ordinary unexpected exceptions to fixed public details. The test-only interruption still crosses rollback handlers unchanged. `reachcommander doctor` now reports clear, terminal-journal, recoverable-transaction, and missing/invalid-recovery states with bounded guidance.

Follow-up RED checkpoints were captured before implementation for: canonical length and ancestry/identity validation; persisted pairwise overlap; swaps before publication/recreate; failures at live writes 1/2/3; missing parent fsync; prepublication retry wedging; duplicate/ill-typed/semantically invalid journals; transaction/digest manifest mismatch; leaked renderer/journal/stdin exceptions; unsafe ancestry during recovery; missing doctor status; and a failed secondary journal immediately before the first live write. Each focused regression is GREEN.

### Final minor review follow-up

- `doctor` now calls the same strict backup validator before describing any restore-bearing transaction as recoverable. Missing/corrupt manifests, missing files, digest mismatches, and transaction-ID mismatches report `recovery-unavailable` with verified-manual-restore/reinstall guidance. Staging and terminal transactions retain their cleanup-only retry semantics.
- The command wrapper drains at most 4,097 stdout bytes into one mode-0600 file below the already-validated installer root, discards excess stdout, suppresses stderr, removes the capture on every handled outcome, and releases stdout only after a successful bounded helper run. This does not impose a process-wide file limit on legitimate transaction writes. Nonzero or incompatible startup outcomes ignore all child content and emit fixed JSON selected only from allowlisted statuses 1 through 6. A protected but incompatible helper can no longer expose a traceback, installed path, or exception content; successful source JSON remains unchanged.
- RED evidence: a corrupt restore-bearing backup was incorrectly reported as `recovery-required` for each of the five material-corruption cases, and a protected incompatible helper exposed its complete Python traceback and injected private path. The focused material/status and command regressions are GREEN.

### Capture cleanup and success validation follow-up

- Source-add capture now has a scoped cleanup lifecycle with an exact validated temporary-name contract. Normal success, fixed helper failure, unexpected shell exit, `INT`, and `TERM` all close the bounded drain, give the tracked helper a bounded termination grace period with a kill fallback, reap it, remove the mode-0600 capture, and restore default traps before returning normally. Interrupted execution no longer leaves an entry that invalidates the uninstall allowlist.
- Exit-zero helper output is independently parsed by fixed inline validator code rather than trusted as public output. It rejects duplicate or extra fields, legacy/private shapes, invalid IDs, and untrimmed, non-printable, empty, or overlong names; valid Unicode is preserved. Only canonical compact `sourceId`/`displayName` JSON is emitted. Validator errors and helper stderr remain suppressed, and all invalid exit-zero output maps to the generic fixed status/result.
- RED evidence: duplicate-key exit-zero JSON returned status 0 unchanged, and a terminated source command retained `.source-add-stdout.*`. The validator, normal/failure cleanup, forced shell-abort, INT/TERM, and post-interruption uninstall-tree regressions are GREEN.

### Process-group and bounded-drain follow-up

- The helper and bounded stdout drain now run as separate `setsid` session/process-group leaders. Installer preflight requires `setsid`. Cleanup sends `TERM` to the complete helper group, closes the only parent writer, applies bounded grace periods, then sends `KILL` and conditionally reaps only processes already known to be stopped; no cleanup path performs an unconditional wait. The direct helper exit status is captured before group cleanup and remains the sole source for the fixed public status mapping.
- The drain incrementally reads with fixed `os.read` calls and immediately writes at most 4,097 bytes with `os.write`, while consuming all excess output. Valid output is therefore persisted before EOF even when a descendant inherits stdout. Normal exit, status 3, `INT`, and `TERM` regressions use a TERM-ignoring descendant and assert bounded return, no helper/descendant/drain/capture residue, exact signal status, and a valid uninstall tree.
- RED evidence: a direct helper that printed valid JSON, spawned a descendant holding stdout, and exited 0 timed out with status 124. The package contract also failed because installer preflight did not require session support. The POSIX behavioral cases execute on Linux CI; this Windows host explicitly skips that one capability because MSYS cannot group-signal native Python descendants, while the test shim starts no descendant in the skipped branch.

### Pre-capture signal follow-up

- Source-add installs its scoped `EXIT`/`INT`/`TERM` cleanup before invoking `mktemp`. If a signal arrives after the exact capture is created but before its pathname is assigned, cleanup uses the already-held shared lock to inspect only depth-one `.source-add-stdout.??????` entries below the trusted install root and removes only regular, non-symlink files whose six-character suffix is strictly alphanumeric. Assigned candidates retain the narrower single-path cleanup behavior.
- RED evidence: a fake `mktemp` created `.source-add-stdout.EARLY1`, signalled the exact command PID before returning its pathname, and the INT case retained the capture. Both INT and TERM now return 130/143 without residue, and the post-interruption uninstall confirmation remains reachable.

## Final verification

| Gate | Result |
|---|---|
| `python -m unittest tests.installer.test_source_management tests.installer.test_render_config tests.installer.test_updater_protocol -v` | 82 passed, 3 Windows capability skips |
| `tests/installer/test_command.sh` | 52/52 passed (1 Windows POSIX-session capability skip) |
| `tests/installer/test_install.sh` | 33/33 passed, 3 Windows capability skips |
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
- `tests/installer/fake-bin/setsid`
- `.superpowers/sdd/task-2-report.md`

## Self-review and concerns

- Source data crosses process boundaries only as strict JSON or as an argv element to a fixed access-check script; no request value is shell source.
- Docker commands are fixed argv lists, captured output is not returned publicly, and only the application service is recreated.
- Atomic writer directory fsync behavior is reused; backup files and transaction directories are also fsynced before publication.
- `NC-theme.png` remains untouched and unstaged.
- Ubuntu-only owner/mode, symlink, `setpriv`, Compose, and service behavior cannot be exercised natively on this Windows host. The Git Bash harnesses use fixed fakes, and the missing ShellCheck binary is the only planned static gate not run.
