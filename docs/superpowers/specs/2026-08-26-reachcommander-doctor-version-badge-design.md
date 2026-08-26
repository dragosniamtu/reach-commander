# ReachCommander Doctor Mount Check and Version Badge Design

## Goal

Remove the Ubuntu Doctor false failure caused by testing application-data access through the protected host installation path, and show the running ReachCommander version visibly beside the system-update control.

## Doctor behavior

The Ubuntu installation keeps `/opt/reachcommander` protected from non-root host users. Docker bind-mounts `/opt/reachcommander/data` directly at container path `/data`, so the container runtime identity does not traverse the protected host parent. Doctor must therefore validate effective application-data access in the container mount namespace rather than impersonating the runtime UID against the host path.

Doctor retains all existing fail-closed host checks:

- the application-data tree accepts only the exact account, key, and file-operation allowlist;
- directories must be real, unmounted, owned by the configured runtime UID/GID, and mode `0700`;
- files must be regular allowlisted files, owned by the configured runtime UID/GID, and mode `0600`;
- authentication documents must be valid JSON.

For each recognized application-data directory, Doctor executes a fixed read/write/traverse probe inside the already-running `reachcommander` container as the configured numeric UID/GID. Container paths are derived only from Doctor's fixed allowlist: `/data`, `/data/auth`, `/data/keys`, and, when present, `/data/file-operations`, `/data/file-operations/plans`, and `/data/file-operations/operations`. No host path, source name, filename, shell fragment, or browser input is interpolated into the command. A failed or unavailable container probe remains a Doctor failure; Doctor remains non-mutating.

Doctor must not loosen `/opt/reachcommander`, application-data, or file permissions. It must not create files as part of the probe.

## Version badge

The existing `SystemUpdateStatusDto.currentVersion` is the authoritative displayed version. The system-update component renders a compact badge immediately beside its update icon:

- a known stable version is shown verbatim, for example `v1.0.1`;
- an edge version is shown verbatim and may be visually truncated with its full value retained in the tooltip and accessible label;
- before the first status response, the badge shows `v…` and announces that the current version is loading;
- when status is available but has no current version, the badge shows `Unknown` and announces that the current version is unavailable.

The update icon retains its existing semantics: it is enabled only for an update that is verified and currently applicable. The new badge is informational and does not become another action. It remains visible in the compact toolbar, uses the existing design tokens, and adopts square corners and high-contrast colors in the Norton theme.

## Data flow

The backend and API contract do not change. The application already obtains the current display version from the trusted host updater state and returns it in `SystemUpdateStatusDto`. The Angular system-update store continues to own loading and refresh behavior. The button component derives only presentation labels from its input status.

## Testing

Installer command contracts will reproduce the protected-parent condition by denying host-namespace `setpriv` access to installer-owned data while allowing normal configured sources. The healthy Doctor case must still pass and must prove that fixed `/data` container probes were issued for every recognized directory. A failed container data probe must cause Doctor to fail without exposing host paths or changing permissions.

Angular component tests will cover stable version, loading, unavailable, and long edge-version presentation, including accessible labels and tooltips. Existing tests continue to prove that the update action is disabled unless `canApply` is true.

## Release and migration

The fix will ship as `v1.0.2`. Updating the container supplies the version badge. Because Doctor is a root-owned host command, existing Ubuntu installations must also rerun the checksum-verified `v1.0.2` installer once to replace the host command. Reconfiguration preserves validated application data and sources according to the existing transactional installer contract.
