# ReachCommander Container Distribution and Ubuntu Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish verified multi-architecture ReachCommander images and provide a safe, interactive Ubuntu installation and lifecycle experience that never builds the application on the target host.

**Architecture:** Keep the existing modular monolith and Docker runtime. Add a Python standard-library renderer as the only writer of generated JSON/YAML/env configuration, thin Bash installer and lifecycle commands around hardened shared primitives, deterministic release packaging, and gated GitHub Actions publication to GHCR.

**Tech Stack:** Bash, Python 3 standard library, Node.js built-in test runner, Docker Compose v2, Docker Buildx, GitHub Actions, GHCR, ShellCheck.

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Use test-first development for each behavior and commit after each task passes.
- The production install root is fixed at `/opt/reachcommander`; the command path is fixed at `/usr/local/bin/reachcommander`.
- The production command lock is fixed at `/opt/.reachcommander.lock`, outside the replaceable installation tree.
- A path override is legal only when `REACHCOMMANDER_TESTING=1` and the caller is not root.
- Never recursively change ownership or permissions on user source directories.
- Never remove, move, or follow a configured source directory during uninstall or rollback.
- Never accept `/`, `/proc`, `/sys`, `/dev`, `/run`, `/var/run`, the Docker socket, or paths resolving inside them as a source.
- Serialize configuration structurally. Do not interpolate untrusted source names or paths into shell/YAML text.
- Persist exact image digests; use moving tags only for update discovery.
- Preserve the container controls already used by the project: non-root user, loopback binding, read-only root, bounded tmpfs, all capabilities dropped, and `no-new-privileges`.
- Do not publish, tag, create a GitHub Release, or change GHCR visibility without explicit user authorization.

## File Responsibility Map

- `deploy/compose.release.yaml`: hardened Compose template with a single controlled source-mount insertion marker.
- `deploy/render_config.py`: request validation and atomic `.env`, Compose, and `sources.json` rendering.
- `deploy/lib/common.sh`: fixed paths, validation, Docker helpers, locks, digest resolution, and health polling.
- `deploy/install.sh`: interactive collection plus transactional installation/reconfiguration.
- `deploy/reachcommander`: lifecycle, diagnostics, update/rollback, and uninstall commands.
- `deploy/release-tags.mjs`: pure Git ref-to-image-tag policy and GitHub output writer.
- `deploy/package-installer.sh`: deterministic release archive and checksum generation.
- `tests/installer/`: dependency-free unit, contract, fake-command, and transaction tests.
- `.github/workflows/ci.yml`: verified smoke, multi-architecture publication, attestations, and release assets.
- `docs/deployment/ubuntu.md`: end-user installation and operations guide.
- `docs/deployment/{nginx.conf,Caddyfile,traefik.static.yaml,traefik.dynamic.yaml}`: authenticated HTTPS reverse-proxy examples.

---

## Task 1: Generate Hardened Release Deployments

**Files:**

- Create: `deploy/compose.release.yaml`
- Create: `deploy/render_config.py`
- Create: `tests/installer/__init__.py`
- Create: `tests/installer/test_render_config.py`
- Create: `tests/installer/fixtures/valid-request.json`

- [ ] **Step 1: Write failing validation tests**

Define this request contract in `valid-request.json`:

```json
{
  "bindAddress": "127.0.0.1",
  "port": 8092,
  "uid": 1000,
  "gid": 1000,
  "image": "ghcr.io/dragosniamtu/reach-commander@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "sources": [
    {
      "id": "media",
      "name": "Media",
      "hostPath": "/srv/Family Media",
      "readOnly": true,
      "defaultLeft": true,
      "defaultRight": true
    }
  ]
}
```

Test public dataclasses `SourceRequest` and `DeploymentRequest`, plus `load_request(path)`. Assert:

- port is `1..65535`;
- UID/GID are `1..2147483647`;
- IDs match `^[a-z0-9][a-z0-9_-]{0,63}$` and are unique;
- canonical host paths are absolute, unique, and at least one source exists;
- names contain `1..100` non-control characters;
- exactly one source is the default for each pane;
- image references are `ghcr.io/dragosniamtu/reach-commander:stable`, `:edge`, `:vX.Y.Z[-prerelease]`, or that repository plus an exact 64-hex digest;
- dangerous canonical paths are rejected, including symlinks resolving into them.

Run and confirm failure:

```bash
python3 -m unittest tests/installer/test_render_config.py -v
```

- [ ] **Step 2: Implement typed validation**

Use `argparse`, `dataclasses`, `json`, `pathlib`, `re`, and `os` only. Error messages must name the invalid field without echoing secrets or arbitrary control characters.

Expose these subcommands:

```text
create-request --output --bind-address --port --uid --gid --image
add-source --request --id --name --host-path --access=ro|rw --default-left=true|false --default-right=true|false
render --request --template --output
set-image --env --image
source-paths --sources
```

`source-paths` reads installer-owned `state/source-mounts.json` and writes NUL-delimited canonical host paths. `set-image` atomically replaces only `REACHCOMMANDER_IMAGE` in a validated env file.

- [ ] **Step 3: Write failing rendering and injection tests**

Cover source names/paths containing spaces, apostrophes, colons, `#`, dollar signs, quotes, and leading hyphens. Assert generated files parse correctly and contain matching source IDs, target paths, access policy, and pane defaults. Assert `state/source-mounts.json` contains canonical host paths and modes, is not referenced by Compose, and `source-paths` emits its paths as NUL-delimited values. Assert the template has exactly one `# installer-source-mounts` marker.

- [ ] **Step 4: Implement atomic structured rendering**

Implement `yaml_scalar(value)` using single-quoted YAML scalars with apostrophes doubled. Write temporary files beside their destination, flush and `fsync`, apply restrictive modes, then `os.replace`.

Use this fixed Compose template:

```yaml
services:
  reachcommander:
    image: ${REACHCOMMANDER_IMAGE}
    container_name: reachcommander
    user: "${REACHCOMMANDER_UID}:${REACHCOMMANDER_GID}"
    ports:
      - "${REACHCOMMANDER_BIND_ADDRESS}:${REACHCOMMANDER_PORT}:8080"
    volumes:
      - type: bind
        source: ./config
        target: /config
        read_only: true
      # installer-source-mounts
    read_only: true
    tmpfs:
      - /tmp:size=32m,mode=1777
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    restart: unless-stopped
```

Each generated source mount must be a long-form bind mount targeting `/sources/<id>` with explicit `read_only`. Generate `config/sources.json` with container paths only, never host paths. Generate root-owned `state/source-mounts.json` with source IDs, canonical host paths, and access modes; never mount it into the container.

- [ ] **Step 5: Run tests and inspect representative output**

```bash
python3 -m unittest tests/installer/test_render_config.py -v
python3 deploy/render_config.py render \
  --request tests/installer/fixtures/valid-request.json \
  --template deploy/compose.release.yaml \
  --output /tmp/reachcommander-render
docker compose --project-directory /tmp/reachcommander-render config --quiet
```

Expected: tests pass; Compose accepts the generated deployment.

- [ ] **Step 6: Commit**

```bash
git add deploy/compose.release.yaml deploy/render_config.py tests/installer
git commit -m "feat: generate hardened release deployments"
```

## Task 2: Add Safe Installer Primitives

**Files:**

- Create: `deploy/lib/common.sh`
- Create: `tests/installer/test_common.sh`
- Create: `tests/installer/fake-bin/docker`

- [ ] **Step 1: Write failing shell tests**

Use a temporary directory and a fake `docker` placed first in `PATH`. Test these functions:

```text
rc_init_paths
rc_die
rc_require_commands
rc_require_root
rc_invoking_ids
rc_validate_port
rc_normalize_source_id
rc_canonical_source
rc_validate_source_path
rc_validate_channel
rc_acquire_lock
rc_compose
rc_pull_digest
rc_wait_healthy
rc_atomic_write
rc_assert_safe_install_root
```

The fake Docker program must log argv as NUL-delimited records, emulate only expected `compose`, `pull`, and `inspect` calls, and fail on unknown invocations.

Run and confirm failure:

```bash
bash tests/installer/test_common.sh
```

- [ ] **Step 2: Implement strict fixed-path initialization**

Start with `#!/usr/bin/env bash` and `set -Eeuo pipefail`. Production always assigns:

```bash
RC_INSTALL_ROOT=/opt/reachcommander
RC_COMMAND_PATH=/usr/local/bin/reachcommander
RC_BACKUP_ROOT=/var/backups/reachcommander
```

Honor test path variables only when `REACHCOMMANDER_TESTING=1` and `EUID != 0`; otherwise overwrite them with fixed production values. Canonicalize and reject symlinked or broad install/backup roots.

- [ ] **Step 3: Implement validators, lock, and Docker helpers**

`rc_acquire_lock` validates and opens the fixed external `/opt/.reachcommander.lock` on FD 9, then calls `flock -n 9`. The lock must not live inside the deployment directory because reconfiguration and uninstall replace or remove that tree. `rc_compose` always calls `docker compose --project-directory "$RC_INSTALL_ROOT"` and accepts only caller-supplied Compose arguments.

`rc_pull_digest(channel)` must:

1. validate `stable`, `edge`, or `vX.Y.Z[-prerelease]`;
2. pull exactly `ghcr.io/dragosniamtu/reach-commander:<channel>`;
3. inspect `RepoDigests`;
4. output only an exact `ghcr.io/dragosniamtu/reach-commander@sha256:<64 hex>` value.

`rc_wait_healthy(container, seconds)` polls `docker inspect` once per second, succeeds only on `healthy`, and stops immediately on `unhealthy`.

- [ ] **Step 4: Test permission and injection boundaries**

Verify root ignores test overrides, non-root tests may use them, command arguments with whitespace remain single arguments, parallel lock acquisition fails, malformed inspect output is rejected, and no validator invokes `eval`.

- [ ] **Step 5: Run ShellCheck and tests**

```bash
bash tests/installer/test_common.sh
shellcheck -x --source-path=SCRIPTDIR deploy/lib/common.sh tests/installer/test_common.sh tests/installer/fake-bin/docker
```

- [ ] **Step 6: Commit**

```bash
git add deploy/lib/common.sh tests/installer/test_common.sh tests/installer/fake-bin/docker
git commit -m "feat: add safe installer primitives"
```

## Task 3: Add the Interactive Transactional Ubuntu Installer

**Files:**

- Create: `deploy/install.sh`
- Create: `tests/installer/test_install.sh`
- Modify: `tests/installer/fake-bin/docker`

- [ ] **Step 1: Write failing prompt and preflight tests**

Drive stdin from fixtures and capture stdout/stderr. Assert the installer stops before persistent writes when any prerequisite is missing: Docker Engine, Compose v2, Python 3, `readlink`, `flock`, `install`, or `mktemp`.

Test this prompt sequence and its defaults:

1. bind address (`127.0.0.1`);
2. port (`8092`);
3. runtime UID/GID (non-root `SUDO_UID:SUDO_GID`);
4. repeated source name/path/access (`RO` or `RW`);
5. left and right defaults;
6. extra confirmation for broad-but-allowed roots such as `/srv`;
7. exact acknowledgement `I have authenticated HTTPS`.

Confirm failure:

```bash
bash tests/installer/test_install.sh
```

- [ ] **Step 2: Implement safe input collection**

Source `lib/common.sh` relative to the installer bundle, set `umask 077`, acquire the deployment lock, and install `trap` handlers that remove only the installer-owned temporary directory.

Keep source IDs, names, canonical paths, and access policies in separate indexed Bash arrays. Pass every value to `render_config.py` as a separately quoted argument. Generate IDs using `rc_normalize_source_id`; resolve collisions by prompting for a different ID, never by silently changing an existing source.

Reject runtime UID/GID zero. Check path traversal/access as the selected runtime identity without changing permissions. The acknowledgement must match exactly before proceeding.

- [ ] **Step 3: Write failing transaction tests**

Cover:

- successful first installation with mixed RO/RW mounts and spaces in paths;
- `docker compose config` failure;
- image pull or digest-resolution failure;
- unhealthy initial startup;
- interruption during staging;
- existing installation decline;
- successful reconfiguration;
- unhealthy reconfiguration restoring the full previous deployment;
- a write failure and signal interruption restoring the exact previous files;
- next-run recovery from a verified reconfiguration journal left by a hard failure.

Canary files in every source and each source ancestor must remain unchanged in all cases.

- [ ] **Step 4: Implement the staged install transaction**

Perform these operations in order:

1. collect and validate input;
2. create a request with the renderer;
3. render using a temporary `stable` reference;
4. run `docker compose --project-directory "$STAGE_ROOT" config --quiet` against staging;
5. resolve `stable` to a digest with `rc_pull_digest`;
6. replace the image with the immutable digest and rerender;
7. copy `render_config.py` to `bin/render_config.py` and `common.sh` to `lib/common.sh` in staging;
8. retain the rendered `state/source-mounts.json` and write `state/channel`, `state/current-image`, and `state/previous-image`;
9. validate the final staged deployment;
10. for reconfiguration, create a verified full backup and persistent transaction marker before replacing any file in the fixed install root;
11. install `reachcommander` at the fixed command path;
12. start only the ReachCommander Compose service and wait up to 60 seconds for health.

For an existing deployment, take a complete verified installer-owned backup before replacement. If a write, signal, or health failure occurs, restore the previous directory and command, recreate the previous service, and verify its health. If the process or host dies before cleanup, the next installer run recovers the verified journal before accepting input. For a failed first install, run `compose down` without `-v`, retain validated configuration plus bounded diagnostics, and report recovery commands.

- [ ] **Step 5: Run tests and static analysis**

```bash
bash tests/installer/test_install.sh
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh tests/installer/test_install.sh
```

- [ ] **Step 6: Commit**

```bash
git add deploy/install.sh tests/installer/test_install.sh tests/installer/fake-bin/docker
git commit -m "feat: add interactive Ubuntu installer"
```

## Task 4: Add ReachCommander Lifecycle Commands and Diagnostics

**Files:**

- Create: `deploy/reachcommander`
- Create: `tests/installer/test_command.sh`
- Modify: `tests/installer/fake-bin/docker`

- [ ] **Step 1: Write failing command-dispatch tests**

Test this exact interface:

```text
reachcommander status
reachcommander logs [--follow]
reachcommander start
reachcommander stop
reachcommander restart
reachcommander doctor
reachcommander update [stable|edge|vX.Y.Z[-prerelease]]
reachcommander uninstall
```

Unknown commands, extra arguments, or invalid log flags return usage status `64`. Lifecycle commands acquire the shared lock. `status` and `doctor` are read-only.

- [ ] **Step 2: Implement the fixed-path dispatcher and lifecycle commands**

Source `/opt/reachcommander/lib/common.sh` in production. The test-only adjacent-library fallback is legal only under the non-root testing gate. Map:

- `status` to `compose ps` plus current image/channel;
- `logs` to `compose logs --tail 200` and optional `--follow`;
- `start` to `compose up -d reachcommander` plus bounded health;
- `stop` to `compose stop reachcommander`;
- `restart` to `compose restart reachcommander` plus bounded health.

- [ ] **Step 3: Write failing `doctor` tests**

Assert `[PASS]`, `[WARN]`, and `[FAIL]` records for:

- Docker and Compose availability;
- fixed install root and required files;
- readable/valid `sources.json`;
- valid Compose rendering;
- source existence and runtime UID/GID access;
- loopback bind address;
- channel syntax and equality between `.env` image and `state/current-image`;
- container health.

Warnings do not fail the command; one or more failures return status `1`.

- [ ] **Step 4: Implement non-mutating diagnostics**

Read source paths using `bin/render_config.py source-paths --sources state/source-mounts.json`. Never repair permissions, rewrite files, pull images, or restart a service from `doctor`. Redact environment values except the public image, bind address, port, UID, and GID.

- [ ] **Step 5: Run tests and ShellCheck**

```bash
bash tests/installer/test_command.sh
shellcheck -x --source-path=SCRIPTDIR deploy/reachcommander tests/installer/test_command.sh
```

- [ ] **Step 6: Commit**

```bash
git add deploy/reachcommander tests/installer/test_command.sh tests/installer/fake-bin/docker
git commit -m "feat: add ReachCommander management commands"
```

## Task 5: Add Digest-Pinned Update and Automatic Rollback

**Files:**

- Modify: `deploy/reachcommander`
- Modify: `deploy/lib/common.sh`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/fake-bin/docker`

- [ ] **Step 1: Write failing update-state-machine tests**

Cover:

- no argument uses `state/channel`;
- explicit valid channel/version is saved only after success;
- malformed channels are rejected before Docker runs;
- a resolved digest equal to the current digest is a no-op;
- successful update writes `previous-image`, `current-image`, channel, and `.env` consistently;
- pull, write, Compose, or health failure restores every prior file;
- failed update followed by successful rollback returns a distinct nonzero status and message;
- failed rollback returns a more severe status and prints manual recovery commands;
- source paths and files remain byte-for-byte unchanged.

- [ ] **Step 2: Implement one locked update transaction**

Implement this state transition without shell evaluation:

```text
validate requested channel
  -> pull and resolve exact repository digest
  -> no-op when digest equals current-image
  -> copy .env/channel/current-image/previous-image to an owned temp backup
  -> atomically set .env image + previous/current/channel state
  -> compose up -d reachcommander
  -> bounded health check
  -> success: retain new state and remove temp backup
  -> failure: restore state, compose up previous digest, verify health
```

Use `bin/render_config.py set-image` for `.env`, `rc_atomic_write` for state, and `rc_wait_healthy` for both forward and rollback checks. Retain bounded `compose logs --tail 200` diagnostics for the failed digest.

- [ ] **Step 3: Test interruption and concurrency**

Inject termination after each atomic state write and before/after Compose recreation. At next invocation, detect an inconsistent image/state pair, refuse an update, and direct the administrator to `doctor`. Prove two simultaneous updates cannot run.

- [ ] **Step 4: Run focused tests and ShellCheck**

```bash
bash tests/installer/test_common.sh
bash tests/installer/test_command.sh
shellcheck -x --source-path=SCRIPTDIR deploy/lib/common.sh deploy/reachcommander tests/installer/test_command.sh
```

- [ ] **Step 5: Commit**

```bash
git add deploy/reachcommander deploy/lib/common.sh tests/installer/test_command.sh tests/installer/fake-bin/docker
git commit -m "feat: add health-checked container updates"
```

## Task 6: Add Source-Preserving Uninstall

**Files:**

- Modify: `deploy/reachcommander`
- Modify: `deploy/lib/common.sh`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/fake-bin/docker`

- [ ] **Step 1: Write destructive-boundary tests first**

Create temporary canary trees for the install root, backup root, each configured source, every source ancestor, and unrelated sibling directories. Fake `mv`, `rm`, and Docker to record NUL-delimited argv. Assert uninstall:

- exits without the exact phrase `uninstall ReachCommander`;
- refuses install/backup roots equal to, inside, or ancestors of any canonical source;
- rejects symlinked install and backup roots;
- never calls `docker compose down -v`;
- never passes a source or source ancestor to `mv`, `rm`, `chmod`, or `chown`;
- preserves all canaries on backup or Compose-down failure;
- removes only the fixed command and fixed install root after a successful external backup.

- [ ] **Step 2: Implement a fail-closed uninstall plan**

Use `bin/render_config.py source-paths --sources state/source-mounts.json` to obtain NUL-delimited canonical host paths. Before displaying a prompt, verify:

- install root, command path, and backup root equal their production constants;
- none is a symlink;
- neither install nor backup root overlaps a source in either direction;
- timestamp matches UTC `YYYYMMDDTHHMMSSZ` and destination does not exist.

Print exact container/network, command, deployment, backup destination, and an explicit statement that source directories are excluded.

- [ ] **Step 3: Implement ordered backup and removal**

After confirmation:

1. create `/var/backups/reachcommander` with mode `0700`;
2. copy only installer-owned `.env`, Compose, `config`, `state`, `bin`, `lib`, and `backups` into the timestamped backup;
3. verify backup presence before stopping the service;
4. call `docker compose down` without volume flags;
5. remove only the fixed install root using a one-filesystem, no-symlink safety check after the external backup is verified;
6. remove `/usr/local/bin/reachcommander` last.

If backup or Docker teardown fails, keep the command and deployment so the operation can be retried. Never offer an unattended purge flag.

- [ ] **Step 4: Run canary tests and ShellCheck**

```bash
bash tests/installer/test_command.sh
shellcheck -x --source-path=SCRIPTDIR deploy/reachcommander deploy/lib/common.sh tests/installer/test_command.sh
```

- [ ] **Step 5: Commit**

```bash
git add deploy/reachcommander deploy/lib/common.sh tests/installer/test_command.sh tests/installer/fake-bin/docker
git commit -m "feat: add source-preserving uninstall"
```

## Task 7: Package Versioned Installer Assets

**Files:**

- Create: `deploy/release-tags.mjs`
- Create: `deploy/package-installer.sh`
- Create: `tests/installer/release-tags.test.mjs`
- Create: `tests/installer/test_package.sh`
- Modify: `deploy/README.md`

- [ ] **Step 1: Write failing tag-policy tests**

Export `tagsForRef(ref, image)` and test exact results:

```text
refs/heads/master         -> <image>:edge
refs/tags/v1.2.3          -> v1.2.3, v1.2, v1, stable
refs/tags/v1.3.0-beta.1   -> v1.3.0-beta.1 only
pull request/other branch -> no publication tags
```

Reject leading zeroes, missing patch versions, invalid prerelease identifiers, build metadata, shell metacharacters, and newline injection. The CLI writes escaped GitHub outputs `tags` (multiline), `stableRelease`, and `version`.

Run and confirm failure:

```bash
node --test tests/installer/release-tags.test.mjs
```

- [ ] **Step 2: Implement the pure tag policy**

Match version refs with an anchored semantic-version expression equivalent to:

```regex
^refs/tags/(v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)$
```

Stable releases return the complete, major/minor, major, and `stable` tags. Prereleases return only their complete version. Preserve deterministic ordering.

- [ ] **Step 3: Write failing deterministic-package tests**

The archive must contain exactly:

```text
reachcommander-installer/VERSION
reachcommander-installer/LICENSE
reachcommander-installer/compose.release.yaml
reachcommander-installer/install.sh
reachcommander-installer/reachcommander
reachcommander-installer/render_config.py
reachcommander-installer/lib/common.sh
```

Test modes, no absolute or `..` paths, semantic stable version validation, a single matching line in `SHA256SUMS`, and identical hashes from two builds.

- [ ] **Step 4: Implement deterministic packaging**

Stage only the allowlisted files. Use GNU tar with:

```bash
--sort=name --mtime='UTC 1970-01-01' --owner=0 --group=0 --numeric-owner
```

Normalize executable modes for shell entry points and `0644` for data/Python/library files. Write the full `vX.Y.Z` to `VERSION`. Generate `SHA256SUMS` in the output directory with only the archive basename.

- [ ] **Step 5: Document bundle internals and test**

Explain entry points, installed files, channel semantics, and that the bundle has no credentials or user paths.

```bash
node --test tests/installer/release-tags.test.mjs
bash tests/installer/test_package.sh
shellcheck -x --source-path=SCRIPTDIR deploy/package-installer.sh tests/installer/test_package.sh
```

- [ ] **Step 6: Commit**

```bash
git add deploy/release-tags.mjs deploy/package-installer.sh deploy/README.md tests/installer
git commit -m "build: package versioned installer assets"
```

## Task 8: Gate and Publish Multi-Architecture Images

**Files:**

- Modify: `.github/workflows/ci.yml`
- Modify: `.dockerignore`
- Create: `tests/installer/workflow-contract.test.mjs`

- [ ] **Step 1: Write failing workflow contract tests**

Parse the workflow as text/JSON-safe YAML and assert:

- push triggers include `master` and `v*` tags;
- pull requests never receive package-write permissions or execute publication;
- installer verification runs in the existing `acceptance` job;
- `container-smoke` needs both `backend` and `acceptance`;
- `container-publish` needs `backend`, `acceptance`, and `container-smoke`;
- publication uses `contents: write`, `packages: write`, `attestations: write`, and `id-token: write` only in its job;
- Buildx targets `linux/amd64,linux/arm64`, enables `sbom: true`, and uses `provenance: mode=max`;
- tag builds prove the commit is contained in `origin/master`;
- stable tag builds prove the candidate is the newest stable version reachable from `origin/master`;
- one global concurrency group serializes candidate promotion;
- tag workflows are non-cancelling and a retry reuses an existing immutable version only when its OCI revision matches `GITHUB_SHA`;
- Buildx pushes a unique candidate tag, verifies it, and only then promotes the candidate digest to channel tags;
- conflicting immutable complete-version tags fail closed, while an older retry repairs only its exact tag and release assets without moving channel aliases backward;
- release assets run only for stable semantic tags;
- manifest verification distinguishes runnable platforms from attestation descriptors.

- [ ] **Step 2: Add installer verification to CI**

Install ShellCheck on the Ubuntu job and run:

```bash
python3 -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR \
  deploy/install.sh deploy/reachcommander deploy/lib/common.sh deploy/package-installer.sh \
  tests/installer/test_common.sh tests/installer/test_install.sh \
  tests/installer/test_command.sh tests/installer/test_package.sh
```

Keep all existing .NET, Angular, PWA, production-build, publish-layout, and Playwright gates.

- [ ] **Step 3: Add amd64 container smoke**

After backend and acceptance success, build the candidate image, allocate a loopback port dynamically, run with the production security controls, wait for Docker health, request `/health`, and always remove the test container. Do not mount the Docker socket into the candidate.

- [ ] **Step 4: Add GHCR publication**

Use checkout with full history, the pure tag script, job-scoped `GITHUB_TOKEN`, QEMU, Buildx, GHCR login, and build/push actions. Apply OCI labels for title, description, source, revision, version, license, and documentation. Use GitHub Actions cache. Push only a unique candidate tag when the tag list is non-empty.

After the candidate push, select a retry-safe source. A pre-existing complete-version tag is reusable only when its OCI revision matches the event commit; otherwise fail closed. Inspect the selected remote manifest and require runnable `linux/amd64` and `linux/arm64` entries plus the expected attestations. Permit non-runnable attestation descriptors. Only after verification, promote the selected digest to every safe tag in one serialized operation and verify each tag resolves to that digest. When a newer stable version now exists, a retry may repair only its exact immutable tag so moving aliases never regress.

- [ ] **Step 5: Add stable GitHub Release assets**

For non-prerelease `vX.Y.Z` only, run `deploy/package-installer.sh`, verify `sha256sum --check`, and upload `reachcommander-installer.tar.gz` plus `SHA256SUMS` to that exact GitHub Release. A prerelease image must not move `stable` or create stable installer assets.

- [ ] **Step 6: Keep installer sources outside the application image context**

Update `.dockerignore` so deployment scripts, documentation, and installer tests do not enlarge the runtime image. Confirm required application sources remain in context.

- [ ] **Step 7: Run contracts and inspect the workflow diff**

```bash
node --test tests/installer/workflow-contract.test.mjs
git diff --check
git diff -- .github/workflows/ci.yml .dockerignore
```

- [ ] **Step 8: Commit**

```bash
git add .github/workflows/ci.yml .dockerignore tests/installer/workflow-contract.test.mjs
git commit -m "ci: publish verified ReachCommander images"
```

## Task 9: Document Safe Ubuntu Installation and Proxy Integration

**Files:**

- Create: `docs/deployment/ubuntu.md`
- Create: `docs/deployment/nginx.conf`
- Create: `docs/deployment/Caddyfile`
- Create: `docs/deployment/traefik.dynamic.yaml`
- Create: `tests/installer/docs-contract.test.mjs`
- Modify: `README.md`

- [ ] **Step 1: Write failing documentation contracts**

Assert the guide contains:

- Docker Engine and Compose v2 prerequisites without auto-installing either;
- a versioned download, `SHA256SUMS`, `sha256sum --check`, archive inspection, and `sudo ./install.sh`;
- no `curl | sudo sh`, `wget | sh`, or equivalent remote-to-root pipeline;
- loopback binding plus authenticated HTTPS warning;
- RO/RW source permissions and the no-recursive-`chmod`/`chown` policy;
- stable, edge, exact-version, digest pinning, automatic rollback, `doctor`, and uninstall backup behavior;
- a one-time instruction to make the repository-linked GHCR package public;
- PWA secure-context and same-origin requirements;
- Nginx, Caddy, and Traefik examples.

Run and confirm failure:

```bash
node --test tests/installer/docs-contract.test.mjs
```

- [ ] **Step 2: Write the Ubuntu installation guide**

The recommended command sequence must be version-pinned, reviewable, and checksum-first:

```bash
(
set -Eeuo pipefail
INSTALL_WORKDIR="$(mktemp -d /tmp/reachcommander-install.XXXXXX)"
chmod 0700 "$INSTALL_WORKDIR"
trap 'rm -rf -- "$INSTALL_WORKDIR"' EXIT
cd "$INSTALL_WORKDIR"
REACHCOMMANDER_VERSION=v1.0.0
REACHCOMMANDER_RELEASE_URL="https://github.com/dragosniamtu/reach-commander/releases/download/${REACHCOMMANDER_VERSION}"
curl --fail --location --output reachcommander-installer.tar.gz \
  "${REACHCOMMANDER_RELEASE_URL}/reachcommander-installer.tar.gz"
curl --fail --location --output SHA256SUMS \
  "${REACHCOMMANDER_RELEASE_URL}/SHA256SUMS"
[[ "$(wc -l <SHA256SUMS)" -eq 1 ]]
grep --extended-regexp --quiet \
  '^[0-9a-f]{64}  reachcommander-installer\.tar\.gz$' SHA256SUMS
sha256sum --check --strict SHA256SUMS
tar -xzf reachcommander-installer.tar.gz
cd reachcommander-installer
less install.sh
sudo ./install.sh
)
```

Explain how to replace `v1.0.0` with a selected stable version. A separate, clearly marked convenience example may use `latest/download`, but the recommended root-install flow remains pinned to one release URL so the archive and checksum cannot be fetched from different releases.

Document `/opt/reachcommander`, `/usr/local/bin/reachcommander`, `/var/backups/reachcommander`, management commands, upgrade/rollback recovery, and that source directories are external and never deleted.

- [ ] **Step 3: Add authenticated HTTPS proxy examples**

Nginx must include Basic Auth (or an explicit replace-with-SSO marker), TLS placeholders, `client_max_body_size 50G`, `proxy_request_buffering off`, six-hour read/send timeouts, and standard forwarded headers to `127.0.0.1:8092`.

Caddy must include HTTPS host syntax, `basic_auth`, a 50 GB request-body limit, forwarded headers where needed, and `reverse_proxy 127.0.0.1:8092`.

Traefik static and dynamic configuration must define an HTTPS router, a trusted certificate resolver (or documented trusted-certificate alternative), BasicAuth middleware, service URL `http://127.0.0.1:8092`, six-hour upstream timeouts, body-size buffering policy compatible with large uploads, and forwarded-header behavior. State that host-network reachability may require the administrator's existing Traefik deployment to use a host gateway/address instead of literal loopback.

- [ ] **Step 4: Update the project README**

Add a concise “Install on Ubuntu” path linking to the guide. Keep the existing clone/build development path. Clearly state ReachCommander itself has no authentication or TLS and must not be exposed directly to the public internet.

- [ ] **Step 5: Run documentation contracts and link checks**

```bash
node --test tests/installer/docs-contract.test.mjs
rg -n "curl.*\|.*(sh|bash)|wget.*\|.*(sh|bash)" README.md docs/deployment deploy
git diff --check
```

Expected: contracts pass and the unsafe-pipeline search returns no matches.

- [ ] **Step 6: Commit**

```bash
git add README.md docs/deployment tests/installer/docs-contract.test.mjs
git commit -m "docs: add published Ubuntu installation guide"
```

## Task 10: Perform Full Verification and Release Readiness Review

**Files:**

- Modify only files required by failures or review findings.

- [ ] **Step 1: Run the complete installer suite**

```bash
python3 -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test \
  tests/installer/release-tags.test.mjs \
  tests/installer/workflow-contract.test.mjs \
  tests/installer/docs-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR \
  deploy/install.sh \
  deploy/reachcommander \
  deploy/lib/common.sh \
  deploy/package-installer.sh \
  tests/installer/test_common.sh \
  tests/installer/test_install.sh \
  tests/installer/test_command.sh \
  tests/installer/test_package.sh
```

- [ ] **Step 2: Run all existing product regressions**

```bash
dotnet restore ReachCommander.slnx
dotnet test ReachCommander.slnx -c Release --no-restore
npm --prefix client/reach-commander-ui ci
npm --prefix client/reach-commander-ui run test:pwa
npm --prefix client/reach-commander-ui test -- --watch=false
npm --prefix client/reach-commander-ui run build
npm --prefix client/reach-commander-ui run verify:pwa
dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj -c Release --no-restore -o artifacts/publish -p:BuildAngularOnPublish=false
npm --prefix tests/e2e ci
npm --prefix tests/e2e exec -- playwright install --with-deps chromium
npm --prefix tests/e2e test
```

- [ ] **Step 3: Exercise real container and package smoke paths**

```bash
docker build -t reachcommander:installer-smoke .
docker run --rm -d --name reachcommander-installer-smoke \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:size=32m,mode=1777 -p 127.0.0.1:38092:8080 \
  --mount type=bind,source="$PWD/config",target=/config,readonly \
  reachcommander:installer-smoke
docker inspect reachcommander-installer-smoke
curl --fail http://127.0.0.1:38092/health
docker rm -f reachcommander-installer-smoke
bash deploy/package-installer.sh v0.0.1 /tmp/reachcommander-package
cd /tmp/reachcommander-package
sha256sum --check SHA256SUMS
tar -tzf reachcommander-installer.tar.gz
```

Use a temporary Ubuntu environment or disposable VM to run one real installation with one RO and one RW source, then test `doctor`, update no-op, restart, and uninstall. Verify source canaries and ancestor metadata remain unchanged.

- [ ] **Step 4: Conduct an independent critical/important review**

Review every diff specifically for:

- source deletion, movement, permission, and symlink hazards;
- privileged environment/path injection;
- JSON, YAML, shell, and GitHub-output injection;
- interruption and partial-state recovery;
- digest correctness and health rollback;
- prerelease/stable tag promotion mistakes;
- GHCR token permissions, secret exposure, SBOM, and provenance;
- architecture-specific native dependencies;
- reverse-proxy authentication/TLS wording and upload behavior;
- download integrity and remote root-shell hazards.

Fix all critical and important findings test-first. Rerun the affected suite after each fix.

- [ ] **Step 5: Verify the final tree and history**

```bash
git diff --check
git status --short
git log --oneline --decorate -12
```

If review fixes required code changes, commit them with:

```bash
git add <exact-reviewed-files>
git commit -m "test: verify container installation releases"
```

- [ ] **Step 6: Prepare the first-publish handoff without publishing**

Report:

- exact passing test totals and container/package smoke evidence;
- expected `edge` and stable tags;
- the one-time GHCR public-visibility action;
- the release tag command that would trigger stable publication;
- confirmation that no image, Git tag, GitHub Release, or package visibility was changed.

Wait for explicit authorization before pushing commits, creating a tag, publishing release assets, or changing package visibility.

## Definition of Done

- A clean Ubuntu amd64 or arm64 host with Docker and Compose can install without a clone or local build.
- Generated Compose and `sources.json` agree on every source and RO/RW policy.
- The deployed image is digest-pinned, loopback-bound, non-root, read-only, capability-free, and healthy.
- Update is locked, health-checked, and automatically restores the previous healthy digest on failure.
- Uninstall backs up generated state and proves it never touches configured sources or their ancestors.
- `master`, stable, and prerelease refs produce exactly the approved tags.
- CI gates publication on the complete product and installer suites and publishes amd64/arm64 with SBOM and provenance.
- Documentation provides checksum-first installation and authenticated HTTPS proxy examples without a remote-to-root pipeline.
