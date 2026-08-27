# File operations and managed Trash runbook

This runbook covers ReachCommander's durable Copy, Move, Delete, Restore, Empty Trash, and MkDir behavior. It is for administrators of self-hosted Windows, Ubuntu, macOS/Docker, and container deployments.

## Safety model

- The browser sends configured source IDs and normalized logical paths, never host filesystem roots.
- Copy/Move previews expire and execution revalidates source fingerprints, destination state, read-only policy, containment, reserved names, links, and free space where available.
- One persisted FIFO executes Copy, Move, managed Trash, Restore, permanent Delete, and Empty Trash. A second job waits instead of racing an overlapping mutation.
- Copy stages data beside its final destination and commits atomically. Directory merges stage individual children and never replace an unrelated destination tree as a unit.
- Cross-device Move records a durable commit before removing the source. A post-copy removal failure is reported as `copiedButNotRemoved`; it is not disguised as a successful move.
- Cancellation stops at defined checkpoints. Atomic publish/removal steps are intentionally non-cancellable.
- API status, logs, and recovery output contain logical names only. Do not add physical source paths to support tickets or public issue reports.

## Function keys and conflicts

| Key | Operation |
|---|---|
| F5 | Extract an eligible archive; otherwise Copy to the opposite pane |
| F6 | Move to the opposite pane |
| F7 | Create one directory in the active folder |
| F8 | Review Delete, defaulting to managed Trash |

Copy, Move, and Restore conflicts support Overwrite, Skip, and Create Unique Name. A decision must be supplied for every conflict. Create Unique Name uses the first available sibling such as `file (2).txt`; the server chooses and revalidates the final name.

The progress modal may be sent to **Background**. This changes only browser presentation. Closing the browser or logging out does not cancel the server job. After authentication, ReachCommander reloads unacknowledged jobs and resumes a single 750 ms polling lifecycle.

## Managed Trash

For a writable source, ReachCommander creates and owns a hidden `.reachcommander-trash` root only when the location is safe. It contains ownership metadata, manifests, staged work, and deleted item payloads. It is application data, not a freedesktop, Windows Recycle Bin, or macOS Trash integration.

Trash never expires automatically. Use the toolbar Trash view to:

- filter one source or show all configured sources;
- restore selected items, including missing parent creation and conflict handling;
- permanently delete selected Trash entries;
- Empty Trash for the filtered source or all sources.

Permanent deletion and Empty Trash require the acknowledgement: **“This deletion is permanent, cannot be undone, and is unrecoverable.”** The API independently requires `permanentDeleteConfirmed: true`.

Do not rename, move, edit, or selectively copy files inside `.reachcommander-trash` while ReachCommander is running. If its ownership marker or safe layout cannot be verified, Trash is reported unavailable and ReachCommander will not adopt or erase the unknown tree.

## Backups

Back up two distinct classes of data:

1. The `/data` mount (or native equivalent) contains authentication state, cookie keys, operation plans/status, and queue metadata.
2. Every writable source's `.reachcommander-trash` contains recoverable deleted payloads and manifests.

Stop ReachCommander for a consistent cold backup. Preserve permissions and hidden files. Back up a Trash root together with its source identity/configuration; a manifest without its payload is not recoverable. Verify backup hashes and perform restore drills on isolated source roots.

Installer uninstall/update backups cover installer-owned deployment and `/data` according to the platform guide. They deliberately do not copy or remove configured source directories, including source-local `.reachcommander-trash`. Retain those source backups separately.

## Restart and interruption recovery

On startup, queued jobs remain queued. A job found in a nonterminal in-flight phase from a previous process is marked interrupted. ReachCommander attempts cleanup only for staging/quarantine entries whose persisted identity still matches; ambiguous data is preserved and reported by logical recovery name.

When a task reports `interrupted`, `completedWithErrors`, or a recovery warning:

1. Stop new mutations to the affected source.
2. Record the operation ID, phase, logical outcomes, and recovery names.
3. Check that the expected source and destination files are intact. For Move, explicitly check whether both copies exist.
4. Stop ReachCommander before manually inspecting a reported hidden staging/quarantine entry.
5. Remove or relocate only the exact reported entry after verifying it belongs to the operation. Never delete by wildcard.
6. Restart, refresh both panes, and acknowledge the terminal task only after review.

Do not delete operation metadata to make a warning disappear. Preserve `/data` and the affected source tree for diagnosis.

## Full-stack system update operations

Only an Ubuntu installer-managed deployment provides the in-app system update boundary. Its `reachcommander-updater.service` performs automatic checks at startup and every six hours, while applying a discovered digest requires administrator confirmation. Exact version pins remain pinned. Windows, macOS, and manual container deployments report the feature as unsupported and continue to use their documented platform update commands.

The application can ask the helper only for status, Check, Apply, or the fixed sanitized diagnostic snapshot through `/run/reachcommander-updater/updater.sock`; it never mounts `/var/run/docker.sock`. Apply blocks new file mutations, drains existing request leases, and rechecks the durable Copy/Move/Trash/archive queues before the host update begins. The browser reconnects after restart. A failed candidate is rolled back to the prior healthy digest when possible.

From the blocking update screen, open **Technical details** and choose **Download diagnostics**. The authenticated, antiforgery-protected, rate-limited request creates a temporary ZIP in memory and downloads it directly; ReachCommander neither retains nor uploads it. Its five files contain only allowlisted deployment-health statuses and the public update trace. They exclude raw logs, secrets, source metadata, filenames, paths, network identity, environment values, Docker identifiers, and file contents. If an older helper cannot supply the host snapshot, the ZIP is partial and directs the operator to refresh the checksum-verified Ubuntu installer.

For diagnosis, do not edit the helper journal or transaction state. Capture these outputs and keep physical source paths out of public reports:

```bash
sudo systemctl status reachcommander-updater.service
sudo journalctl -u reachcommander-updater.service --since today
sudo reachcommander status
sudo reachcommander support-bundle > reachcommander-support.zip
sudo reachcommander doctor
```

Review `reachcommander-support.zip` before sharing it. The root-only service journal and update-log commands below are deeper follow-up evidence and should not be treated as the sanitized shareable bundle.

Existing installations must run the checksum-verified installer once to receive this boundary. Updater helper changes also require a future installer refresh because an unprivileged application update cannot replace root-owned code.

## Capacity and permissions

An `RW` badge means application policy permits controlled writes; it does not grant host access. The process/container UID must have the required permissions and Docker bind mounts must be `rw`. Copy may read from an `RO` source, but Move, Delete, MkDir, Restore destinations, and Trash require writable policy and filesystem access.

Keep source mounts narrow. Never expose `/`, a complete home directory containing unrelated secrets, or `/var/run/docker.sock`. Maintain ordinary independent backups for all important source data.
