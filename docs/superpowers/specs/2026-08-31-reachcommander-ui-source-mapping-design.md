# ReachCommander UI Source Mapping Design

## Goal

Let an authenticated administrator add one real host-folder source from the ReachCommander UI on an installer-managed Ubuntu deployment. The new source becomes a persistent Docker bind mount and appears in both pane source selectors after the application restarts. Manual Docker, Windows, macOS, and unsupported installations remain read-only from this UI and explain why source management is unavailable.

## User experience

Add an **Add source** control to the top toolbar. It opens a blocking dialog with:

- display name;
- absolute Ubuntu host folder;
- access policy: Read only (default) or Read/write;
- an explicit confirmation that a read/write mapping lets ReachCommander change that host folder.

The source ID and `/sources/<id>` container path are generated on the trusted host. The dialog never accepts Compose fragments, container paths, command-line options, image names, or arbitrary environment values.

Before submission, the UI explains that the folder must already exist on the Ubuntu host and be accessible to the configured ReachCommander runtime UID/GID. Unsupported installations disable the control with a precise reason and direct the administrator to the installer/reconfiguration workflow.

After acceptance, the UI shows a full-screen configuration/restart state. The host applies the change transactionally, restarts only the ReachCommander container, and performs the same health validation used by installer-managed operations. The browser reconnects, reloads `/api/sources`, and reports the added source. A failed change rolls back the prior files/container and returns a bounded public error plus a support-diagnostics reference.

## Recommended architecture

Keep the container unprivileged and reuse the existing root-owned, Unix-socket host helper. Do not mount the Docker socket and do not let the API write `/opt/reachcommander`, Compose files, or host paths directly.

Add a distinct, versioned source-management protocol on the existing local socket. The authenticated API acts only as a narrow transport:

1. `GET /api/source-management/status` asks whether this installation supports managed sources.
2. `POST /api/source-management/sources` sends one strict request containing only `displayName`, `hostPath`, and `access`.
3. The host helper validates the request and starts one serialized transaction, returning an operation ID before restart.
4. `GET /api/source-management/operations/{id}` returns bounded status before and after reconnect.

The helper invokes only a fixed ReachCommander management action with `shell=False`; user input is structured data, never an executable command. Source transactions and updates share the existing deployment lock, so they cannot mutate Compose state concurrently.

## Host validation

The host is authoritative and validates again even when the UI/API already did basic shape checks:

- request byte size, exact JSON fields, protocol version, UUID, and enum values;
- display name after trimming: non-empty, bounded, and free of control characters;
- host path: bounded absolute path, existing directory, canonicalized with symlinks resolved;
- reject `/`, `/proc`, `/sys`, `/dev`, `/run`, `/var/run`, installer-owned paths, and their protected descendants;
- reject exact broad roots such as `/home`, `/srv`, and `/mnt` from the UI workflow; a specific child directory is required;
- reject duplicate or overlapping configured source paths;
- verify read/execute access, plus write access for RW, as the configured non-root runtime UID/GID with supplementary groups cleared;
- enforce a bounded source count and generate a collision-safe lowercase source ID;
- reject any unsafe/symlinked installer state before reading or writing it.

## Transaction and recovery

Under the fixed deployment lock:

1. Revalidate the current installer state and confirm no system update or other source transaction is active.
2. Create a protected same-filesystem transaction directory and durably copy `config/sources.json`, `state/source-mounts.json`, `compose.yaml`, and any generated override state needed for rollback.
3. Append the generated source definition and bind-mount metadata to staged files.
4. Render Compose through the existing renderer and run `docker compose config` against the staged result.
5. Atomically replace the managed files, `fsync` their directories, and recreate only the ReachCommander service.
6. Verify the expected image/container identity and health.
7. Mark the operation completed and remove the transaction backup.

On any failure after replacement, restore the exact protected backup, recreate the previous container configuration, verify recovery health, and mark the operation rolled back or failed. An interrupted transaction is detected and recovered by the host helper before accepting another mutation. Persistent `/data` is never replaced, so accounts, keys, Trash metadata, and file-operation history survive.

## Application contracts

The API/application layer exposes platform-neutral models:

- support: `supported`, `reasonCode`, `detail`;
- request: `displayName`, `hostPath`, `access` (`readOnly | readWrite`);
- operation: `operationId`, generated source ID/name, phase (`accepted | validating | applying | restarting | healthChecking | completed | rolledBack | failed`), reason code, bounded detail, and timestamps.

The existing JSON source catalog is intentionally not hot-reloaded. A successful transaction restarts the container, causing the catalog to load the new protected configuration once at startup.

## Security review summary

- Existing authentication fallback policy and automatic antiforgery validation protect the new API.
- Existing rate limiting applies; the coordinator additionally permits only one source mutation at a time.
- Backend operation eligibility blocks a restart while copy, move, extraction, rename, upload, Trash, or other tracked file mutations are active.
- The Unix socket remains group-restricted and read-only-mounted; the Docker socket remains absent.
- Responses and logs never include Compose contents, command output, runtime tokens, cookies, or unrelated host paths. The requested and canonical source path may appear only in root-owned diagnostics, not general API errors.
- The helper fails closed on old/incompatible protocol versions and unsupported/manual deployments.

## Installer compatibility

The current image-only updater does not replace the root-owned helper, systemd unit, or management CLI. Therefore an existing installation whose helper predates the source-management protocol cannot gain this privileged capability from a container update alone. It must run the latest checksum-verified installer once to upgrade the host integration; application data, configured sources, port, access mode, and update channel are preserved by the existing reconfiguration transaction. The UI reports this state explicitly instead of presenting a broken Add source action.

New clean installations made from the source-management release include the compatible helper immediately. This release also records the host-helper capability/version in status and diagnostics so future work can add a separately verified host-integration update path without guessing from the container version.

## Testing

Coverage will include:

1. strict protocol parsing and bounded responses;
2. protected/broad/duplicate/overlapping path rejection and runtime-identity access checks;
3. transaction success, Compose validation failure, container health failure, rollback, and interrupted recovery in the fake installer harness;
4. API authorization, antiforgery, rate limiting, unsupported deployment, active-operation blocking, and protocol mismatch;
5. Angular dialog validation, RO/RW warning, unsupported state, restart/reconnect, success/error behavior, and source refresh;
6. browser acceptance using a fake installer-managed host protocol without mounting the Docker socket;
7. old-helper capability messaging plus installer/package contracts and Ubuntu ShellCheck gates.

## What stays simple for now

- Add one source per operation; no bulk editor.
- No rename, removal, access-policy editing, or default-pane changes from the UI.
- No folder picker: browsers cannot safely browse the Ubuntu host filesystem, so the administrator enters an absolute path.
- No support for manual Docker Compose, native Windows/macOS installers, Kubernetes, or remote Docker hosts in this slice.

## Scaling and migration

The operation is administrative and serialized, so throughput is irrelevant at 10x or 100x user activity. The important bottleneck is restart coordination, handled by the operation gate and durable journal. The platform-neutral API and separate protocol version leave room for native Windows/macOS helpers later without exposing Docker implementation details to Angular.
