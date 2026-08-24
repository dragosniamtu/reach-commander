# ReachCommander One-Command macOS Installer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a one-command, interactive macOS installer that runs the existing ReachCommander image through Docker Desktop on Intel and Apple Silicon Macs.

**Architecture:** Add one self-contained Bash 3.2 installer under `deploy/macos/`. It uses macOS `plutil` for typed JSON generation, consumes the existing hardened release Compose template, stores its per-user deployment below `~/Library/Application Support/ReachCommander`, and delegates application execution to Docker Desktop. A macOS CI job runs the installer against fake Docker/network commands while existing Linux CI continues to validate the real multi-architecture image.

**Tech Stack:** macOS Bash 3.2, `plutil`, Docker Desktop with Docker Compose v2, the existing `ghcr.io/dragosniamtu/reach-commander` image, shell/TAP tests, Node.js contract tests, ShellCheck, GitHub Actions.

## Global Constraints

- Work directly on `master`; do not create a worktree or feature branch.
- Keep the Ubuntu installer and management command behavior unchanged.
- The production installer is self-contained and must run under Apple's Bash 3.2: no associative arrays, `mapfile`, `readarray`, `globstar`, or Bash 4 case-conversion operators.
- Require an installed and running Docker Desktop plus Docker Compose v2; never install Docker or Homebrew.
- Never invoke `sudo`, modify shell startup files, create a system-wide command, or open Docker Desktop/a browser.
- Use `ghcr.io/dragosniamtu/reach-commander:stable` only for discovery and persist a resolved `@sha256:` digest containing exactly 64 lowercase hexadecimal characters.
- Store installer-owned state at `~/Library/Application Support/ReachCommander` with test-only path overrides gated by `REACHCOMMANDER_TESTING=1`.
- Default to `127.0.0.1:8080`; LAN mode must be an explicit `0.0.0.0` selection.
- Never configure public-internet exposure, TLS, a reverse proxy, router, VPN, or native macOS telemetry.
- Never mount `/`, protected macOS system roots, `/Volumes` as a parent, Docker Desktop internals, or the Docker socket.
- Every source has the same `RO`/`RW` value in `config/sources.json` and the Compose bind mount.
- Never copy, move, delete, recursively chmod/chown, probe-write, or otherwise mutate a selected source.
- A source equal to or inside the installer root is rejected. A broad ancestor source receives a mandatory nested empty bind mount that masks the installer root from ReachCommander's file APIs.
- Preserve `data/auth/account.json` and `data/keys` across reconfiguration, container recreation, and updates.
- A failed update or reconfiguration restores the previous generated configuration and image digest.
- Do not add a host agent, native `.app`, database, new JavaScript/Python runtime prerequisite, or commercial updater.

---

## File Structure

### Production

- Create `deploy/macos/install.sh` — self-contained bootstrap, prompts, source model, plist/JSON renderer, Compose renderer, Docker lifecycle, transaction, and completion output.
- Reuse `deploy/compose.release.yaml` — hardened service template; do not duplicate it.

### Tests and CI

- Create `tests/installer/macos/test_helpers.sh` — path, identifier, source-policy, JSON, YAML, and exclusion-unit contracts using real macOS `plutil`.
- Create `tests/installer/macos/test_install.sh` — end-to-end prompt, Docker failure, idempotency, rollback, and source-canary contracts.
- Create `tests/installer/macos/fake-bin/docker` — deterministic Docker/Compose/digest/health fake.
- Create `tests/installer/macos/fake-bin/curl` — copies the checked-in Compose template to the requested output.
- Create `tests/installer/macos/fake-bin/lsof` — controls occupied-port behavior.
- Create `tests/installer/macos/fake-bin/route` and `tests/installer/macos/fake-bin/ipconfig` — deterministic LAN address discovery.
- Modify `tests/installer/workflow-contract.test.mjs` — require the macOS job, tests, ShellCheck coverage, and publication dependency.
- Modify `tests/installer/docs-contract.test.mjs` — require the macOS guide and README/security contracts.
- Modify `.github/workflows/ci.yml` — run real macOS command-line tools with Docker/network mocked.

### Documentation

- Create `docs/deployment/macos.md` — prerequisites, one-command/inspect-first flows, source/network choices, setup code, lifecycle, troubleshooting, backup, and safe removal.
- Modify `README.md` — advertise Docker Desktop macOS support accurately and link the guide.
- Modify `deploy/README.md` — distinguish the Ubuntu bundle from the macOS bootstrap.
- Modify `SECURITY.md` — document LAN exposure and mandatory masking of installer-owned state.

---

### Task 1: Add the Bash 3.2-safe macOS foundation

**Files:**
- Create: `deploy/macos/install.sh`
- Create: `tests/installer/macos/test_helpers.sh`

**Interfaces:**
- Consumes: macOS `/bin/bash`, `pwd`, `grep`, `sed`, `tr`, `cut`, `mkdir`, `mktemp`, `chmod`.
- Produces: `rc_init_paths()`, `rc_validate_architecture(value)`, `rc_canonical_directory(path)`, `rc_validate_port(port)`, `rc_normalize_source_id(name)`, `rc_path_relation(left,right)`, `rc_validate_source_path(path)`, `rc_yaml_quote(value)`, `rc_acquire_lock()`, and `rc_release_lock()`.

- [ ] **Step 1: Write the failing helper contract**

Create `tests/installer/macos/test_helpers.sh` with a TAP-style harness. Source the production file without running `main` and assert exact paths, ports, identifier normalization, canonical paths, protected-root rejection, installer-root exclusion, and YAML quoting:

```bash
#!/bin/bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPOSITORY_ROOT="$(cd -P -- "$TEST_DIRECTORY/../../.." && pwd -P)"
INSTALLER="$REPOSITORY_ROOT/deploy/macos/install.sh"
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/reachcommander-macos-helpers.XXXXXX")"
trap 'chmod -R u+rwX -- "$TEST_ROOT" 2>/dev/null || true; rm -rf -- "$TEST_ROOT"' EXIT

export REACHCOMMANDER_SOURCE_ONLY=1
export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_INSTALL_ROOT="$TEST_ROOT/user home/Library/Application Support/ReachCommander"
export REACHCOMMANDER_TEST_USER_HOME="$TEST_ROOT/user home"

# shellcheck source=/dev/null
source "$INSTALLER"
rc_init_paths

tests_run=0
fail() { printf 'not ok - %s\n' "$1" >&2; exit 1; }
pass() { tests_run=$((tests_run + 1)); printf 'ok %d - %s\n' "$tests_run" "$1"; }
assert_equal() { [[ "$1" == "$2" ]] || fail "$3 (expected '$1', got '$2')"; }
assert_fails() {
  local message="$1"
  shift
  if "$@" >/dev/null 2>&1; then fail "$message"; fi
}

assert_equal "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$RC_INSTALL_ROOT" "test install root"
assert_equal "$REACHCOMMANDER_TEST_USER_HOME" "$RC_USER_HOME" "test user home"
pass "test-only paths are explicit"

for port in 1 8080 65535; do rc_validate_port "$port"; done
for port in '' 0 65536 text +1 1.5; do
  assert_fails "invalid port '$port' must fail" rc_validate_port "$port"
done
pass "ports are bounded"

assert_equal "family-media" "$(rc_normalize_source_id 'Family Media')" "normalized ID"
assert_equal "media-2026" "$(rc_normalize_source_id '  MEDIA__2026  ')" "separator normalization"
assert_fails "punctuation-only names must fail" rc_normalize_source_id '***'
pass "source IDs are stable"

for architecture in x86_64 arm64; do
  rc_validate_architecture "$architecture"
done
assert_fails "unsupported architecture must fail" rc_validate_architecture i386
pass "Intel and Apple Silicon architectures are accepted"

mkdir -p -- "$RC_USER_HOME/Pictures" "$RC_INSTALL_ROOT"
canonical="$(rc_canonical_directory "$RC_USER_HOME/Pictures")"
assert_equal "$RC_USER_HOME/Pictures" "$canonical" "canonical directory"
assert_equal "ancestor" "$(rc_path_relation "$RC_USER_HOME" "$RC_INSTALL_ROOT")" "home relation"
assert_equal "inside" "$(rc_path_relation "$RC_INSTALL_ROOT/data" "$RC_INSTALL_ROOT")" "inside relation"
assert_equal "same" "$(rc_path_relation "$RC_INSTALL_ROOT" "$RC_INSTALL_ROOT")" "same relation"
assert_equal "disjoint" "$(rc_path_relation "$RC_USER_HOME/Pictures" "$RC_INSTALL_ROOT")" "disjoint relation"
rc_validate_source_path "$RC_USER_HOME"
assert_fails "installer root source must fail" rc_validate_source_path "$RC_INSTALL_ROOT"
assert_fails "installer child source must fail" rc_validate_source_path "$RC_INSTALL_ROOT/data"
for path in / /System /Library /private /usr /bin /sbin /dev; do
  assert_fails "protected path '$path' must fail" rc_validate_source_path "$path"
done
pass "path boundary permits a maskable ancestor only"

assert_equal "'Media'" "$(rc_yaml_quote 'Media')" "plain YAML scalar"
assert_equal "'Bob''s Media'" "$(rc_yaml_quote "Bob's Media")" "quoted YAML scalar"
pass "YAML scalars are single-quoted safely"

printf '1..%d\n' "$tests_run"
```

- [ ] **Step 2: Run the helper contract to verify it fails**

Run on macOS:

```bash
/bin/bash tests/installer/macos/test_helpers.sh
```

Expected: FAIL because `deploy/macos/install.sh` does not exist.

- [ ] **Step 3: Implement the minimal portable foundation**

Create `deploy/macos/install.sh` with strict mode, fixed production paths, test-only overrides, no Bash 4 features, and the following complete helper behavior:

```bash
#!/bin/bash
set -Eeuo pipefail
umask 077

RC_IMAGE_REPOSITORY='ghcr.io/dragosniamtu/reach-commander'
RC_TEMPLATE_URL='https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/compose.release.yaml'
RC_INSTALL_ROOT=''
RC_USER_HOME=''
RC_LOCK_DIRECTORY=''
RC_LOCK_OWNED=false

rc_die() { printf 'ReachCommander: %s\n' "$1" >&2; }

rc_init_paths() {
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]]; then
    RC_INSTALL_ROOT="${REACHCOMMANDER_TEST_INSTALL_ROOT:?}"
    RC_USER_HOME="${REACHCOMMANDER_TEST_USER_HOME:?}"
  else
    [[ "$(uname -s)" == 'Darwin' ]] ||
      { rc_die 'this installer supports macOS only'; return 1; }
    RC_USER_HOME="${HOME:?}"
    RC_INSTALL_ROOT="$RC_USER_HOME/Library/Application Support/ReachCommander"
  fi
  [[ "$RC_USER_HOME" == /* && "$RC_INSTALL_ROOT" == /* ]] ||
    { rc_die 'installer paths must be absolute'; return 1; }
  RC_LOCK_DIRECTORY="$RC_INSTALL_ROOT/state/install.lock"
}

rc_validate_port() {
  local value="${1:-}"
  [[ "$value" =~ ^[0-9]+$ ]] && (( value >= 1 && value <= 65535 )) ||
    { rc_die 'port must be an integer from 1 through 65535'; return 1; }
}

rc_validate_architecture() {
  case "${1:-}" in
    x86_64 | arm64) return 0 ;;
    *) rc_die 'Docker Desktop must run on an Intel or Apple Silicon Mac'; return 1 ;;
  esac
}

rc_normalize_source_id() {
  local value
  value="$(
    printf '%s' "${1:-}" |
      LC_ALL=C tr '[:upper:]' '[:lower:]' |
      LC_ALL=C sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//' |
      LC_ALL=C cut -c 1-64
  )"
  [[ "$value" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]] ||
    { rc_die 'source name cannot produce a safe source identifier'; return 1; }
  printf '%s\n' "$value"
}

rc_canonical_directory() (
  local value="${1:-}"
  [[ -n "$value" && -d "$value" ]] ||
    { rc_die 'source path must be an existing directory'; return 1; }
  case "$value" in *$'\n'* | *$'\r'* | *$'\t'*)
    rc_die 'source paths cannot contain control characters'
    return 1
  esac
  CDPATH= cd -P -- "$value" >/dev/null 2>&1 ||
    { rc_die 'source path cannot be resolved'; return 1; }
  pwd -P
)

rc_path_relation() {
  local left="${1%/}"
  local right="${2%/}"
  if [[ "$left" == "$right" ]]; then printf 'same\n'
  elif [[ "$right" == "$left"/* ]]; then printf 'ancestor\n'
  elif [[ "$left" == "$right"/* ]]; then printf 'inside\n'
  else printf 'disjoint\n'
  fi
}

rc_validate_source_path() {
  local canonical="$1"
  local relation
  case "$canonical" in
    / | /System | /System/* | /Library | /Library/* | /private | /private/* | \
    /usr | /usr/* | /bin | /bin/* | /sbin | /sbin/* | /dev | /dev/*)
      rc_die 'source path resolves to a protected macOS location'
      return 1
      ;;
  esac
  relation="$(rc_path_relation "$canonical" "$RC_INSTALL_ROOT")"
  [[ "$relation" != 'same' && "$relation" != 'inside' ]] ||
    { rc_die 'source path cannot be the installer directory or one of its children'; return 1; }
}

rc_yaml_quote() {
  local escaped="${1//\'/\'\'}"
  printf "'%s'" "$escaped"
}

rc_release_lock() {
  if [[ "$RC_LOCK_OWNED" == 'true' && -d "$RC_LOCK_DIRECTORY" && ! -L "$RC_LOCK_DIRECTORY" ]]; then
    rm -f -- "$RC_LOCK_DIRECTORY/pid"
    rmdir -- "$RC_LOCK_DIRECTORY"
  fi
  RC_LOCK_OWNED=false
}

rc_acquire_lock() {
  local state_directory="$RC_INSTALL_ROOT/state"
  local stale_pid=''
  mkdir -p -- "$state_directory"
  chmod 0700 -- "$RC_INSTALL_ROOT" "$state_directory"
  if ! mkdir -- "$RC_LOCK_DIRECTORY" 2>/dev/null; then
    [[ -f "$RC_LOCK_DIRECTORY/pid" && ! -L "$RC_LOCK_DIRECTORY/pid" ]] &&
      IFS= read -r stale_pid <"$RC_LOCK_DIRECTORY/pid"
    if [[ "$stale_pid" =~ ^[1-9][0-9]*$ ]] && kill -0 "$stale_pid" 2>/dev/null; then
      rc_die 'another ReachCommander installer operation is running'
      return 1
    fi
    rm -f -- "$RC_LOCK_DIRECTORY/pid"
    rmdir -- "$RC_LOCK_DIRECTORY" ||
      { rc_die 'installer lock is unsafe or cannot be recovered'; return 1; }
    mkdir -- "$RC_LOCK_DIRECTORY"
  fi
  chmod 0700 -- "$RC_LOCK_DIRECTORY"
  printf '%s\n' "$$" >"$RC_LOCK_DIRECTORY/pid"
  chmod 0600 -- "$RC_LOCK_DIRECTORY/pid"
  RC_LOCK_OWNED=true
}

main() {
  rc_init_paths
  rc_die 'installer implementation is incomplete'
  return 1
}

if [[ "${REACHCOMMANDER_SOURCE_ONLY:-0}" != '1' ]]; then
  main "$@"
fi
```

- [ ] **Step 4: Run the helper contract to verify it passes**

```bash
/bin/bash tests/installer/macos/test_helpers.sh
/bin/bash -n deploy/macos/install.sh tests/installer/macos/test_helpers.sh
```

Expected: TAP output ending in `1..6` and both commands exit 0.

- [ ] **Step 5: Commit the foundation**

```bash
git add deploy/macos/install.sh tests/installer/macos/test_helpers.sh
git commit -m "feat: add macOS installer foundation"
```

---

### Task 2: Add source collection and typed deployment rendering

**Files:**
- Modify: `deploy/macos/install.sh`
- Modify: `tests/installer/macos/test_helpers.sh`

**Interfaces:**
- Consumes: Task 1 helpers and the checked-in `deploy/compose.release.yaml` marker `# installer-source-mounts`.
- Produces: indexed arrays `RC_SOURCE_IDS`, `RC_SOURCE_NAMES`, `RC_SOURCE_PATHS`, `RC_SOURCE_ACCESS`; `rc_prompt_value(prompt,default)`; `rc_add_source(id,name,path,ro|rw)`; `rc_render_deployment(output,template,image,bind,port,uid,gid)`; `rc_collect_sources()`; and `rc_collect_network()`.

- [ ] **Step 1: Extend the helper test with a failing rendering contract**

Append setup that creates two source directories named `Family Media` and `Café Bob's`, adds one `RO` and one `RW` source, renders a deployment, and checks typed JSON plus Compose:

```bash
SOURCE_ONE="$TEST_ROOT/Family Media"
SOURCE_TWO="$TEST_ROOT/Café Bob's"
mkdir -p -- "$SOURCE_ONE" "$SOURCE_TWO"
RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()
rc_add_source family-media 'Family Media' "$SOURCE_ONE" ro
rc_add_source cafe-bob "Café Bob's" "$SOURCE_TWO" rw

STAGE="$TEST_ROOT/rendered"
rc_render_deployment \
  "$STAGE" \
  "$REPOSITORY_ROOT/deploy/compose.release.yaml" \
  "ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})" \
  127.0.0.1 8080 "$(id -u)" "$(id -g)"

assert_equal 'Family Media' \
  "$(plutil -extract sources.0.name raw -o - "$STAGE/config/sources.json")" \
  "first source name"
assert_equal 'true' \
  "$(plutil -extract sources.0.readOnly raw -o - "$STAGE/config/sources.json")" \
  "first source policy"
assert_equal 'false' \
  "$(plutil -extract sources.1.readOnly raw -o - "$STAGE/config/sources.json")" \
  "second source policy"
grep -A3 -F "target: '/sources/family-media'" "$STAGE/compose.yaml" |
  grep -Fq 'read_only: true' || fail "RO Compose policy missing"
grep -A3 -F "target: '/sources/cafe-bob'" "$STAGE/compose.yaml" |
  grep -Fq 'read_only: false' || fail "RW Compose policy missing"
assert_equal 'true' \
  "$(plutil -extract sources.0.defaultLeft raw -o - "$STAGE/config/sources.json")" \
  "left default"
assert_equal 'true' \
  "$(plutil -extract sources.1.defaultRight raw -o - "$STAGE/config/sources.json")" \
  "right default"
pass "typed JSON and Compose share one source policy"

RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()
rc_add_source home Home "$RC_USER_HOME" ro
MASKED_STAGE="$TEST_ROOT/masked"
rc_render_deployment \
  "$MASKED_STAGE" \
  "$REPOSITORY_ROOT/deploy/compose.release.yaml" \
  "ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'b%.0s' {1..64})" \
  127.0.0.1 8080 "$(id -u)" "$(id -g)"
mask_target="/sources/home/${RC_INSTALL_ROOT#"$RC_USER_HOME"/}"
grep -A3 -F "source: './excluded'" "$MASKED_STAGE/compose.yaml" |
  grep -Fq "target: $(rc_yaml_quote "$mask_target")" ||
  fail "installer state exclusion mount missing"
[[ -d "$MASKED_STAGE/excluded" ]] || fail "empty exclusion directory missing"
[[ -z "$(find "$MASKED_STAGE/excluded" -mindepth 1 -print -quit)" ]] ||
  fail "exclusion directory is not empty"
pass "broad source masks installer-owned state"
```

- [ ] **Step 2: Run the rendering contract to verify it fails**

```bash
/bin/bash tests/installer/macos/test_helpers.sh
```

Expected: FAIL with `rc_add_source: command not found`.

- [ ] **Step 3: Implement the source model**

Add Bash 3.2 indexed arrays and reject IDs, duplicates, nested duplicates, control characters, and mismatched policies:

```bash
RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()

rc_add_source() {
  local source_id="$1"
  local source_name="$2"
  local source_path="$3"
  local source_access="$4"
  local existing
  local relation
  [[ "$source_id" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]] ||
    { rc_die 'source identifier is invalid'; return 1; }
  [[ -n "$source_name" && ${#source_name} -le 100 ]] ||
    { rc_die 'source name must contain 1 through 100 characters'; return 1; }
  case "$source_name" in *$'\n'* | *$'\r'* | *$'\t'*)
    rc_die 'source name cannot contain control characters'; return 1
  esac
  [[ "$source_access" == 'ro' || "$source_access" == 'rw' ]] ||
    { rc_die 'source access must be ro or rw'; return 1; }
  rc_validate_source_path "$source_path"
  for existing in "${RC_SOURCE_IDS[@]:-}"; do
    [[ "$existing" != "$source_id" ]] ||
      { rc_die 'source identifier is already in use'; return 1; }
  done
  for existing in "${RC_SOURCE_PATHS[@]:-}"; do
    relation="$(rc_path_relation "$source_path" "$existing")"
    [[ "$relation" == 'disjoint' ]] ||
      { rc_die 'source paths cannot duplicate or contain one another'; return 1; }
  done
  RC_SOURCE_IDS[${#RC_SOURCE_IDS[@]}]="$source_id"
  RC_SOURCE_NAMES[${#RC_SOURCE_NAMES[@]}]="$source_name"
  RC_SOURCE_PATHS[${#RC_SOURCE_PATHS[@]}]="$source_path"
  RC_SOURCE_ACCESS[${#RC_SOURCE_ACCESS[@]}]="$source_access"
}
```

- [ ] **Step 4: Implement typed JSON and Compose rendering**

Implement `rc_render_json` with `plutil` as the only serializer. Each source contains exactly `id`, `name`, container `path`, `enabled`, `readOnly`, `defaultLeft`, and `defaultRight`. The first source defaults left; the second defaults right when present, otherwise the first defaults both. Generate `state/source-mounts.json` from a second plist containing only `id`, canonical `hostPath`, and `access`:

```bash
rc_render_json() {
  local output="$1"
  local mounts_output="$2"
  local plist="$3"
  local mounts_plist="$4"
  local count="${#RC_SOURCE_IDS[@]}"
  local index=0
  local default_right=0
  local read_only default_left default_right_value
  (( count > 1 )) && default_right=1
  (( count > 0 )) || { rc_die 'at least one source is required'; return 1; }

  plutil -create xml1 "$plist"
  plutil -insert sources -array "$plist"
  plutil -create xml1 "$mounts_plist"
  plutil -insert sources -array "$mounts_plist"
  while (( index < count )); do
    read_only=false
    default_left=false
    default_right_value=false
    [[ "${RC_SOURCE_ACCESS[$index]}" == ro ]] && read_only=true
    (( index == 0 )) && default_left=true
    (( index == default_right )) && default_right_value=true

    plutil -insert "sources.$index" -dictionary "$plist"
    plutil -insert "sources.$index.id" -string "${RC_SOURCE_IDS[$index]}" "$plist"
    plutil -insert "sources.$index.name" -string "${RC_SOURCE_NAMES[$index]}" "$plist"
    plutil -insert "sources.$index.path" -string "/sources/${RC_SOURCE_IDS[$index]}" "$plist"
    plutil -insert "sources.$index.enabled" -bool true "$plist"
    plutil -insert "sources.$index.readOnly" -bool "$read_only" "$plist"
    plutil -insert "sources.$index.defaultLeft" -bool "$default_left" "$plist"
    plutil -insert "sources.$index.defaultRight" -bool "$default_right_value" "$plist"

    plutil -insert "sources.$index" -dictionary "$mounts_plist"
    plutil -insert "sources.$index.id" -string "${RC_SOURCE_IDS[$index]}" "$mounts_plist"
    plutil -insert "sources.$index.hostPath" -string "${RC_SOURCE_PATHS[$index]}" "$mounts_plist"
    plutil -insert "sources.$index.access" -string "${RC_SOURCE_ACCESS[$index]}" "$mounts_plist"
    index=$((index + 1))
  done
  plutil -convert json -o "$output" "$plist"
  plutil -convert json -o "$mounts_output" "$mounts_plist"
}
```

Implement `rc_render_compose` by replacing exactly one template marker:

```bash
rc_render_compose() {
  local template="$1"
  local output="$2"
  local mounts="$3"
  local marker='      # installer-source-mounts'
  local index=0
  local relation
  local relative
  local target
  [[ "$(grep -Fxc "$marker" "$template")" == '1' ]] ||
    { rc_die 'Compose template must contain exactly one source marker'; return 1; }
  : >"$mounts"
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    {
      printf '      - type: bind\n'
      printf '        source: %s\n' "$(rc_yaml_quote "${RC_SOURCE_PATHS[$index]}")"
      printf '        target: %s\n' "$(rc_yaml_quote "/sources/${RC_SOURCE_IDS[$index]}")"
      printf '        read_only: %s\n' \
        "$([[ "${RC_SOURCE_ACCESS[$index]}" == ro ]] && printf true || printf false)"
    } >>"$mounts"
    relation="$(rc_path_relation "${RC_SOURCE_PATHS[$index]}" "$RC_INSTALL_ROOT")"
    if [[ "$relation" == 'ancestor' ]]; then
      relative="${RC_INSTALL_ROOT#"${RC_SOURCE_PATHS[$index]}"/}"
      target="/sources/${RC_SOURCE_IDS[$index]}/$relative"
      {
        printf '      - type: bind\n'
        printf "        source: './excluded'\n"
        printf '        target: %s\n' "$(rc_yaml_quote "$target")"
        printf '        read_only: true\n'
      } >>"$mounts"
    fi
    index=$((index + 1))
  done
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == "$marker" ]]; then cat -- "$mounts"
    else printf '%s\n' "$line"
    fi
  done <"$template" >"$output"
}
```

`rc_render_deployment` creates `config`, `data/auth`, `data/keys`, `state`, `backups`, and empty `excluded`; writes the five-line `.env` keys `REACHCOMMANDER_BIND_ADDRESS`, `REACHCOMMANDER_PORT`, `REACHCOMMANDER_UID`, `REACHCOMMANDER_GID`, and `REACHCOMMANDER_IMAGE`; calls both renderers; writes `stable`, the immutable image, and an empty previous image to `state/channel`, `state/current-image`, and `state/previous-image`; and applies these exact modes: deployment/state/data/auth/keys `0700`, excluded `0555`, `.env`/state files `0600`, config `0755`, and `sources.json` `0644`.

- [ ] **Step 5: Implement interactive source and network collection**

Add these exact menus:

```text
What should ReachCommander access?
1. Whole drives
2. Specific folders (Recommended)

Who can access ReachCommander?
1. This Mac only (Recommended)
2. Devices on the local network
```

Implement:

- `rc_collect_specific_folders`: expand only the exact value `~` or a path beginning `~/`, canonicalize, default display name to `basename`, normalize/collision-check the ID, ask `RO`/`RW`, and stop only after at least one source.
- `rc_discover_whole_drives`: return canonical current-user home followed by each direct directory below `/Volumes` whose resolved path is neither `/` nor protected; never add `/Volumes`.
- `rc_collect_whole_drives`: accept one or more unique numbered selections, ask access for each, and require typing the exact canonical path before accepting `RW`.
- `rc_collect_network`: map option 1 to `127.0.0.1` and option 2 to `0.0.0.0`, default to port `8080`, and loop while `lsof -nP -iTCP:"$port" -sTCP:LISTEN` reports a conflict. During reconfiguration, permit the currently deployed port when it is unchanged; never treat an unrelated occupied port as safe.
- Test-only `REACHCOMMANDER_TEST_VOLUMES_FILE` and `REACHCOMMANDER_TEST_LOCAL_IP` are honored only with `REACHCOMMANDER_TESTING=1`; production discovers volumes using `find /Volumes -mindepth 1 -maxdepth 1 -type d -print0` and LAN IP using `route -n get default` plus `ipconfig getifaddr`.

- [ ] **Step 6: Run rendering and prompt contracts**

```bash
/bin/bash tests/installer/macos/test_helpers.sh
/bin/bash -n deploy/macos/install.sh tests/installer/macos/test_helpers.sh
```

Expected: all TAP assertions pass, `plutil` parses both JSON documents, and no template marker remains.

- [ ] **Step 7: Commit source configuration**

```bash
git add deploy/macos/install.sh tests/installer/macos/test_helpers.sh
git commit -m "feat: render macOS source configuration"
```

---

### Task 3: Add Docker installation, update, rollback, and end-to-end tests

**Files:**
- Modify: `deploy/macos/install.sh`
- Create: `tests/installer/macos/test_install.sh`
- Create: `tests/installer/macos/fake-bin/docker`
- Create: `tests/installer/macos/fake-bin/curl`
- Create: `tests/installer/macos/fake-bin/lsof`
- Create: `tests/installer/macos/fake-bin/route`
- Create: `tests/installer/macos/fake-bin/ipconfig`

**Interfaces:**
- Consumes: rendered deployment from Task 2 and Docker Compose v2.
- Produces: `rc_preflight()`, `rc_prepare_installer_root()`, `rc_validate_authentication_data_tree()`, `rc_fetch_template(path)`, `rc_pull_digest(channel)`, variadic `rc_compose(root,argv)`, `rc_preflight_sources(root)`, `rc_wait_healthy(root,timeout)`, `rc_set_env_image(path,image)`, `rc_begin_generated_transaction()`, `rc_commit_generated(stage)`, `rc_rollback_generated()`, `rc_complete_transaction()`, `rc_recover_transaction()`, `rc_update_existing()`, `rc_reconfigure_existing()`, `rc_choose_existing_action()`, `rc_cleanup_work_root(path)`, `rc_print_completion()`, and complete `main()`.

- [ ] **Step 1: Create deterministic command fakes**

Create executable fake commands that log NUL-delimited arguments. The Docker fake supports:

```bash
case "${1:-}" in
  info) exit "${FAKE_DOCKER_INFO_EXIT:-0}" ;;
  pull) exit "${FAKE_DOCKER_PULL_EXIT:-0}" ;;
  image)
    printf '%s\n' "${FAKE_DOCKER_DIGESTS:-}"
    exit "${FAKE_DOCKER_INSPECT_EXIT:-0}"
    ;;
  inspect)
    printf '%s\n' "${FAKE_DOCKER_HEALTH:-healthy}"
    exit "${FAKE_DOCKER_INSPECT_EXIT:-0}"
    ;;
  compose)
    for argument in "$@"; do
      case "$argument" in
        version) exit "${FAKE_DOCKER_COMPOSE_VERSION_EXIT:-0}" ;;
        config) exit "${FAKE_DOCKER_CONFIG_EXIT:-0}" ;;
        run) exit "${FAKE_DOCKER_SOURCE_PREFLIGHT_EXIT:-0}" ;;
        up) exit "${FAKE_DOCKER_UP_EXIT:-0}" ;;
        down) exit "${FAKE_DOCKER_DOWN_EXIT:-0}" ;;
        ps) printf '%s\n' reachcommander-test-id; exit 0 ;;
        logs) printf '%s\n' 'ReachCommander setup code: TEST-SETUP-CODE'; exit 0 ;;
      esac
    done
    ;;
esac
printf 'fake docker: unsupported invocation\n' >&2
exit 97
```

Before the `case`, append every argument and an invocation separator to `FAKE_DOCKER_LOG` with `printf '%s\0'`. The curl fake parses `--output` and its following destination and copies `FAKE_CURL_SOURCE` there. The lsof fake exits `FAKE_LSOF_EXIT`. Route prints `interface: en0`; ipconfig prints `192.168.50.25`. No fake executes an argument.

- [ ] **Step 2: Write failing end-to-end installation tests**

In `tests/installer/macos/test_install.sh`:

1. Create a test user home, application-support root, two source canaries, an external-volume fixture, and a Compose-template fixture.
2. Put `fake-bin` first in `PATH`.
3. Set only gated test variables and deterministic Docker digests.
4. Execute the installer as a subprocess with newline-delimited prompt input.
5. Assert:
   - missing/stopped Docker creates no `.env`;
   - specific-folder installation writes exact Unicode/apostrophe names and matching `RO`/`RW` policies;
   - Mac-only mode writes `127.0.0.1:8080`;
   - LAN mode writes `0.0.0.0` and prints `192.168.50.25`;
   - occupied `8080` prompts for and accepts `8081`;
   - whole-home installation emits the nested `./excluded` mask;
   - each external volume is mounted independently and `source: '/Volumes'` never appears;
   - a wrong broad-RW confirmation cannot install;
   - Docker source-preflight failure leaves no active deployment;
   - all source canary hashes remain unchanged after every success/failure;
   - authentication and key-ring canaries survive update and reconfiguration;
   - symlinked installer/data/auth/key directories and unexpected authentication-data entries fail before writes;
   - identical-digest update is a no-op;
   - healthy update advances current/previous digests;
   - unhealthy update restores the old digest;
   - declined reconfiguration leaves every generated file unchanged;
   - stale safe-lock recovery succeeds while an active lock fails; and
   - `x86_64` and `arm64` pass architecture validation while any other architecture fails before writes;
   - completion output contains the endpoint, setup-code logs command, state path, source policies, and no browser-open command.

Use these safe test helpers:

```bash
run_installer() {
  local input="$1"
  local output="$2"
  set +e
  printf '%s\n' "$input" | /bin/bash "$INSTALLER" >"$output" 2>&1
  last_status=$?
  set -e
}

source_hashes() {
  shasum -a 256 "$SOURCE_ONE/canary.txt" "$SOURCE_TWO/canary.txt"
}
```

The cleanup trap validates that `TEST_ROOT` matches `${TMPDIR:-/tmp}/reachcommander-macos-test.*` before any recursive removal. It never receives a production path or selected source outside that fixture root.

- [ ] **Step 3: Run the end-to-end test to verify it fails**

```bash
/bin/bash tests/installer/macos/test_install.sh
```

Expected: FAIL because `main` still reports that installation is incomplete.

- [ ] **Step 4: Implement prerequisite, fetch, digest, and health helpers**

Add:

```bash
rc_preflight() {
  local command_name
  rc_validate_architecture "$(uname -m)"
  for command_name in \
    curl docker plutil lsof find grep sed tr cut mktemp chmod \
    id basename route ipconfig cp mv rm rmdir cat sleep; do
    command -v "$command_name" >/dev/null 2>&1 ||
      { rc_die "required command is missing: $command_name"; return 1; }
  done
  docker info >/dev/null 2>&1 ||
    { rc_die 'Docker Desktop is not installed or is not running; install/start it and rerun'; return 1; }
  docker compose version >/dev/null 2>&1 ||
    { rc_die 'Docker Compose v2 is required'; return 1; }
}

rc_fetch_template() {
  local destination="$1"
  local url="$RC_TEMPLATE_URL"
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]]; then
    url="${REACHCOMMANDER_TEST_TEMPLATE_URL:?}"
  fi
  curl --fail --show-error --silent --location \
    --proto '=https' --tlsv1.2 --output "$destination" "$url" ||
    { rc_die 'cannot download the ReachCommander Compose template'; return 1; }
  [[ "$(grep -Fxc '      # installer-source-mounts' "$destination")" == '1' ]] ||
    { rc_die 'downloaded Compose template is invalid'; return 1; }
}

rc_pull_digest() {
  local reference="$RC_IMAGE_REPOSITORY:${1:-stable}"
  local output line selected=''
  docker pull "$reference" >/dev/null ||
    { rc_die 'ReachCommander image pull failed; check network and GHCR visibility'; return 1; }
  output="$(docker image inspect \
    --format '{{range .RepoDigests}}{{println .}}{{end}}' "$reference")" || return 1
  while IFS= read -r line; do
    if [[ "$line" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]]; then
      [[ -z "$selected" || "$selected" == "$line" ]] ||
        { rc_die 'image inspection returned conflicting trusted digests'; return 1; }
      selected="$line"
    fi
  done <<<"$output"
  [[ -n "$selected" ]] ||
    { rc_die 'image inspection returned no trusted ReachCommander digest'; return 1; }
  printf '%s\n' "$selected"
}

rc_compose() {
  local root="$1"
  local project='reachcommander-preflight'
  shift
  [[ "$root" == "$RC_INSTALL_ROOT" ]] && project='reachcommander'
  docker compose --project-name "$project" \
    --project-directory "$root" --file "$root/compose.yaml" "$@"
}

rc_wait_healthy() {
  local root="$1"
  local timeout="$2"
  local container status
  local deadline=$((SECONDS + timeout))
  container="$(rc_compose "$root" ps -q reachcommander)"
  [[ -n "$container" ]] ||
    { rc_die 'ReachCommander container was not created'; return 1; }
  while (( SECONDS <= deadline )); do
    status="$(docker inspect \
      --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' \
      "$container")" || return 1
    case "$status" in
      healthy) return 0 ;;
      unhealthy | missing) rc_die 'ReachCommander container is unhealthy'; return 1 ;;
      starting | created | restarting) ;;
      *) rc_die 'ReachCommander returned an invalid health status'; return 1 ;;
    esac
    sleep 1
  done
  rc_die 'ReachCommander health check timed out'
  return 1
}
```

`rc_prepare_installer_root` rejects the installer root when it is a symlink or non-directory, creates it when absent, and then rejects symlinks/non-directories at `config`, `data`, `data/auth`, `data/keys`, `state`, `backups`, and `excluded`. `rc_validate_authentication_data_tree` walks only the fixed `data` root and accepts these entries:

```text
auth/
keys/
auth/account.json
auth/bootstrap.json
auth/auth.lock
keys/key-*.xml
```

Every accepted directory must be a real directory and every accepted file must be a real regular file. Any symlink, socket, device, nested directory, mount-like unexpected entry, or unrecognized name fails closed without printing file contents. These checks run before locking an existing deployment and again immediately before container startup.

- [ ] **Step 5: Implement non-mutating source preflight**

Run the digest-pinned ReachCommander image through staged Compose with Alpine `/bin/sh` as the entrypoint. Check only permission state; never create a probe file:

```bash
rc_preflight_sources() {
  local root="$1"
  local index=0
  local target mode
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    target="/sources/${RC_SOURCE_IDS[$index]}"
    mode="${RC_SOURCE_ACCESS[$index]}"
    if [[ "$mode" == 'rw' ]]; then
      rc_compose "$root" run --rm --no-deps --entrypoint /bin/sh reachcommander \
        -c 'test -r "$1" && test -x "$1" && test -w "$1"' \
        reachcommander-probe "$target" ||
        { rc_die "Docker Desktop cannot use the selected read/write source: ${RC_SOURCE_PATHS[$index]}"; return 1; }
    else
      rc_compose "$root" run --rm --no-deps --entrypoint /bin/sh reachcommander \
        -c 'test -r "$1" && test -x "$1"' \
        reachcommander-probe "$target" ||
        { rc_die "Docker Desktop cannot use the selected read-only source: ${RC_SOURCE_PATHS[$index]}"; return 1; }
    fi
    index=$((index + 1))
  done
}
```

- [ ] **Step 6: Implement generated-file transaction and rollback**

Keep `data/` outside generated-file replacement. Define the allowlist exactly:

```text
.env
compose.yaml
config/sources.json
state/source-mounts.json
state/channel
state/current-image
state/previous-image
```

`rc_commit_generated(stage)`:

1. creates `backups/.install-transaction` below the validated installer root;
2. copies each existing allowlisted file into that transaction;
3. writes `state/transaction-active` only after the backup completes;
4. replaces each generated file through a same-directory temporary file plus `mv`;
5. leaves `data/`, `excluded/`, sources, and the lock untouched; and
6. retains the transaction until startup is healthy.

`rc_rollback_generated()` restores or removes each allowlisted file according to the recorded pre-transaction manifest. `rc_complete_transaction()` removes only the marker and known backup files with `rm -f` and empties directories with `rmdir`. Signal handlers call rollback when the marker exists. No transaction function accepts a source path, wildcard, or arbitrary deployment root.

Initial installation creates `data`, `data/auth`, and `data/keys` at `0700`. Initial unhealthy startup prints bounded logs, runs Compose `down`, retains the validated generated configuration and authentication directories for diagnosis, and clears the transaction journal without deleting a source. Only an unhealthy reconfiguration restores the previous generated files.

Use this fixed allowlist and transaction structure:

```bash
RC_GENERATED_FILES=(
  '.env'
  'compose.yaml'
  'config/sources.json'
  'state/source-mounts.json'
  'state/channel'
  'state/current-image'
  'state/previous-image'
)
RC_TRANSACTION_ROOT=''

rc_begin_generated_transaction() {
  local relative
  RC_TRANSACTION_ROOT="$RC_INSTALL_ROOT/backups/.install-transaction"
  [[ ! -e "$RC_TRANSACTION_ROOT" && ! -L "$RC_TRANSACTION_ROOT" ]] ||
    { rc_die 'an installer transaction already exists'; return 1; }
  mkdir -p -- "$RC_TRANSACTION_ROOT/files/config" "$RC_TRANSACTION_ROOT/files/state"
  mkdir -p -- "$RC_TRANSACTION_ROOT/absent/config" "$RC_TRANSACTION_ROOT/absent/state"
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -f "$RC_INSTALL_ROOT/$relative" && ! -L "$RC_INSTALL_ROOT/$relative" ]]; then
      cp -p -- "$RC_INSTALL_ROOT/$relative" "$RC_TRANSACTION_ROOT/files/$relative"
    elif [[ ! -e "$RC_INSTALL_ROOT/$relative" ]]; then
      : >"$RC_TRANSACTION_ROOT/absent/$relative"
    else
      rc_die "generated path is unsafe: $relative"
      return 1
    fi
  done
  printf 'active\n' >"$RC_INSTALL_ROOT/state/transaction-active"
  chmod 0600 -- "$RC_INSTALL_ROOT/state/transaction-active"
}

rc_replace_generated_file() {
  local source="$1"
  local destination="$2"
  local directory temporary
  directory="$(dirname -- "$destination")"
  mkdir -p -- "$directory"
  temporary="$(mktemp "$directory/.reachcommander-write.XXXXXX")"
  cp -p -- "$source" "$temporary"
  mv -f -- "$temporary" "$destination"
}

rc_commit_generated() {
  local stage="$1"
  local relative
  rc_begin_generated_transaction
  for relative in "${RC_GENERATED_FILES[@]}"; do
    [[ -f "$stage/$relative" && ! -L "$stage/$relative" ]] ||
      { rc_die "staged generated file is missing: $relative"; return 1; }
    rc_replace_generated_file "$stage/$relative" "$RC_INSTALL_ROOT/$relative"
  done
}

rc_rollback_generated() {
  local relative
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -f "$RC_TRANSACTION_ROOT/files/$relative" ]]; then
      rc_replace_generated_file \
        "$RC_TRANSACTION_ROOT/files/$relative" "$RC_INSTALL_ROOT/$relative"
    elif [[ -f "$RC_TRANSACTION_ROOT/absent/$relative" ]]; then
      rm -f -- "$RC_INSTALL_ROOT/$relative"
    else
      rc_die "transaction backup is incomplete: $relative"
      return 1
    fi
  done
  rc_complete_transaction
}
```

`rc_complete_transaction` removes each fixed `files/<relative>` and `absent/<relative>`, removes `state/transaction-active`, then removes only the known empty `files/config`, `files/state`, `absent/config`, `absent/state`, `files`, `absent`, and transaction directories with `rmdir`. `rc_recover_transaction` sees `state/transaction-active`, sets the fixed transaction root, validates every backup/absent entry against the allowlist, and calls `rc_rollback_generated` before accepting new input.

- [ ] **Step 7: Implement update and main orchestration**

The existing-deployment menu is:

```text
ReachCommander is already installed.
1. Update (Recommended)
2. Reconfigure
3. Exit
```

`rc_update_existing` validates the fixed `.env` and state files, pulls `stable`, and uses this rollback:

```bash
rc_set_env_image() {
  local destination="$1"
  local image="$2"
  local directory temporary line seen=0
  [[ "$image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]] ||
    { rc_die 'resolved image digest is invalid'; return 1; }
  directory="$(dirname -- "$destination")"
  temporary="$(mktemp "$directory/.env-write.XXXXXX")"
  while IFS= read -r line; do
    case "$line" in
      REACHCOMMANDER_IMAGE=*)
        printf 'REACHCOMMANDER_IMAGE=%s\n' "$image" >>"$temporary"
        seen=$((seen + 1))
        ;;
      REACHCOMMANDER_BIND_ADDRESS=* | REACHCOMMANDER_PORT=* | \
      REACHCOMMANDER_UID=* | REACHCOMMANDER_GID=*)
        printf '%s\n' "$line" >>"$temporary"
        ;;
      *) rm -f -- "$temporary"; rc_die 'installed environment file is invalid'; return 1 ;;
    esac
  done <"$destination"
  [[ "$seen" == '1' ]] ||
    { rm -f -- "$temporary"; rc_die 'installed image setting is invalid'; return 1; }
  chmod 0600 -- "$temporary"
  mv -f -- "$temporary" "$destination"
}
```

Then apply:

```bash
if [[ "$new_digest" == "$old_digest" ]]; then
  printf 'ReachCommander is already up to date.\n'
  return 0
fi
rc_begin_generated_transaction
rc_set_env_image "$RC_INSTALL_ROOT/.env" "$new_digest"
printf '%s\n' "$old_digest" >"$RC_INSTALL_ROOT/state/previous-image"
printf '%s\n' "$new_digest" >"$RC_INSTALL_ROOT/state/current-image"
if rc_compose "$RC_INSTALL_ROOT" up -d reachcommander &&
   rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
  rc_complete_transaction
  printf 'ReachCommander updated successfully.\n'
  return 0
fi
rc_rollback_generated
rc_compose "$RC_INSTALL_ROOT" up -d reachcommander
rc_wait_healthy "$RC_INSTALL_ROOT" 60 ||
  { rc_die 'update and automatic rollback are both unhealthy'; return 3; }
rc_die 'update was unhealthy; the previous image was restored'
return 2
```

`rc_reconfigure_existing` reruns source/network collection, fetches and validates the current template, reads and validates the existing `state/current-image` digest without changing channels, renders a stage pinned to that digest, executes `docker compose config --quiet`, runs the non-mutating source preflight under the separate `reachcommander-preflight` Compose project, and starts a generated-file transaction. A healthy service commits the new generated files while leaving `data/` byte-identical. An unhealthy service restores the previous allowlist, recreates the previous container, waits for it to become healthy, and returns 2. If the previous service cannot be restored, it returns 3 with the transaction journal intact for diagnosis.

Failure output uses a bounded and redacted helper:

```bash
rc_print_failure_diagnostics() {
  rc_compose "$RC_INSTALL_ROOT" logs --tail 200 reachcommander 2>&1 |
    sed -E \
      -e 's/([Ss]etup code:).*/\1 [redacted]/' \
      -e '/[Aa]uthorization:|[Bb]earer |[Pp]assword/d'
}
```

It never prints authentication files, JWT/cookie values, or a directory listing from a selected source. The normal completion output deliberately gives the host operator a logs command for retrieving the first-run setup code.

`rc_choose_existing_action` loops on `rc_prompt_value 'Choose an action' '1'`, invokes `rc_update_existing` for `1`, invokes `rc_reconfigure_existing` for `2`, prints `ReachCommander was left unchanged.` and returns 0 for `3`, and prints a validation error for every other value.

`rc_cleanup_work_root` is the only recursive-cleanup helper. It canonicalizes an existing path and removes it only when it matches the exact `${TMPDIR:-/tmp}/reachcommander-macos-install.*` prefix:

```bash
rc_cleanup_work_root() {
  local value="${1:-}"
  local canonical=''
  local temporary_base="${TMPDIR:-/tmp}"
  temporary_base="${temporary_base%/}"
  [[ -n "$value" && -d "$value" && ! -L "$value" ]] || return 0
  canonical="$(rc_canonical_directory "$value")" || return 1
  case "$canonical" in
    "$temporary_base"/reachcommander-macos-install.*)
      rm -rf -- "$canonical"
      ;;
    *)
      rc_die 'refusing to remove an unexpected installer staging path'
      return 1
      ;;
  esac
}
```

`main` performs this exact order:

```bash
main() {
  local work_root='' template='' stage='' digest=''
  rc_init_paths
  rc_preflight
  rc_prepare_installer_root
  rc_validate_authentication_data_tree
  rc_acquire_lock
  trap 'rc_release_lock' EXIT
  rc_recover_transaction

  if [[ -f "$RC_INSTALL_ROOT/.env" && -f "$RC_INSTALL_ROOT/compose.yaml" ]]; then
    rc_choose_existing_action
    return
  fi

  rc_collect_sources
  rc_collect_network
  work_root="$(mktemp -d "${TMPDIR:-/tmp}/reachcommander-macos-install.XXXXXX")"
  trap 'rc_cleanup_work_root "$work_root"; rc_release_lock' EXIT
  template="$work_root/compose.release.yaml"
  stage="$work_root/deployment"
  rc_fetch_template "$template"
  digest="$(rc_pull_digest stable)"
  rc_render_deployment "$stage" "$template" "$digest" \
    "$RC_BIND_ADDRESS" "$RC_PORT" "$(id -u)" "$(id -g)"
  rc_compose "$stage" config --quiet
  rc_preflight_sources "$stage"
  rc_commit_generated "$stage"
  if ! rc_compose "$RC_INSTALL_ROOT" up -d reachcommander ||
     ! rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
    rc_print_failure_diagnostics >&2 || true
    rc_compose "$RC_INSTALL_ROOT" down >/dev/null 2>&1 || true
    rc_complete_transaction
    rc_die 'initial startup was unhealthy; validated configuration was retained'
    return 2
  fi
  rc_complete_transaction
  rc_print_completion
}
```

`rc_print_completion` prints the localhost URL, optional LAN URL, each source with `RO`/`RW`, the exact state path, and copyable status/logs/start/stop commands based on:

```bash
docker compose --project-name reachcommander \
  --project-directory "$RC_INSTALL_ROOT" \
  --file "$RC_INSTALL_ROOT/compose.yaml"
```

It tells the operator to run `logs --tail 200 reachcommander` for the one-time setup code. It must not execute `open`, `osascript`, Docker Desktop, or a browser.

For updates it prints `/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh)"` and explains that option 1 performs digest discovery plus health-checked rollback. It never recommends raw `docker compose pull`, because that would bypass the installer transaction.

- [ ] **Step 8: Run all macOS installer tests**

```bash
/bin/bash tests/installer/macos/test_helpers.sh
/bin/bash tests/installer/macos/test_install.sh
/bin/bash -n \
  deploy/macos/install.sh \
  tests/installer/macos/test_helpers.sh \
  tests/installer/macos/test_install.sh \
  tests/installer/macos/fake-bin/docker \
  tests/installer/macos/fake-bin/curl \
  tests/installer/macos/fake-bin/lsof \
  tests/installer/macos/fake-bin/route \
  tests/installer/macos/fake-bin/ipconfig
```

Expected: both TAP suites pass, syntax checks exit 0, source canaries are unchanged, and fake Docker reports no unsupported invocation.

- [ ] **Step 9: Commit lifecycle support**

```bash
git add deploy/macos/install.sh tests/installer/macos
git commit -m "feat: install ReachCommander through Docker Desktop"
```

---

### Task 4: Add macOS CI and publication gates

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/workflow-contract.test.mjs`

**Interfaces:**
- Consumes: macOS tests/fakes from Tasks 1–3.
- Produces: required `macos-installer` CI job and publication dependency.

- [ ] **Step 1: Write failing workflow contract assertions**

```javascript
test('macOS installer contracts run before container publication', async () => {
  const workflow = await readRequired('.github/workflows/ci.yml');
  for (const required of [
    'macos-installer:',
    'runs-on: macos-latest',
    'bash tests/installer/macos/test_helpers.sh',
    'bash tests/installer/macos/test_install.sh',
    'deploy/macos/install.sh',
    'tests/installer/macos/test_helpers.sh',
    'tests/installer/macos/test_install.sh',
    'needs: [backend, acceptance, macos-installer]',
  ]) {
    assert.ok(workflow.includes(required), `CI is missing macOS contract: ${required}`);
  }
});
```

- [ ] **Step 2: Run the workflow contract to verify it fails**

```bash
node --test tests/installer/workflow-contract.test.mjs
```

Expected: FAIL because `macos-installer:` is absent.

- [ ] **Step 3: Add the macOS workflow job**

Insert before `container-smoke`:

```yaml
  macos-installer:
    name: macOS installer contracts
    runs-on: macos-latest
    timeout-minutes: 15

    steps:
      - name: Check out repository
        uses: actions/checkout@v6

      - name: Validate macOS installer syntax
        shell: bash
        run: |
          bash -n \
            deploy/macos/install.sh \
            tests/installer/macos/test_helpers.sh \
            tests/installer/macos/test_install.sh \
            tests/installer/macos/fake-bin/docker \
            tests/installer/macos/fake-bin/curl \
            tests/installer/macos/fake-bin/lsof \
            tests/installer/macos/fake-bin/route \
            tests/installer/macos/fake-bin/ipconfig

      - name: Test macOS installer helpers and rendering
        shell: bash
        run: bash tests/installer/macos/test_helpers.sh

      - name: Test macOS installer lifecycle
        shell: bash
        run: bash tests/installer/macos/test_install.sh
```

Add `deploy/macos/install.sh`, both macOS tests, and every fake to the existing Ubuntu ShellCheck command. Change `container-smoke` to:

```yaml
needs: [backend, acceptance, macos-installer]
```

Do not run Docker on the hosted Mac. Existing container smoke/publication remains the real image and architecture test.

- [ ] **Step 4: Run workflow and ShellCheck contracts**

```bash
node --test tests/installer/workflow-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR \
  deploy/macos/install.sh \
  tests/installer/macos/test_helpers.sh \
  tests/installer/macos/test_install.sh \
  tests/installer/macos/fake-bin/docker \
  tests/installer/macos/fake-bin/curl \
  tests/installer/macos/fake-bin/lsof \
  tests/installer/macos/fake-bin/route \
  tests/installer/macos/fake-bin/ipconfig
```

Expected: Node reports zero failures and ShellCheck exits 0.

- [ ] **Step 5: Commit CI coverage**

```bash
git add .github/workflows/ci.yml tests/installer/workflow-contract.test.mjs
git commit -m "ci: validate macOS installer contracts"
```

---

### Task 5: Document one-command macOS installation and security boundaries

**Files:**
- Create: `docs/deployment/macos.md`
- Modify: `README.md:96`
- Modify: `deploy/README.md:1`
- Modify: `SECURITY.md`
- Modify: `tests/installer/docs-contract.test.mjs`

**Interfaces:**
- Consumes: exact menus, paths, commands, failures, and limits implemented in Tasks 1–4.
- Produces: public operator documentation that distinguishes Docker Desktop support from a native app.

- [ ] **Step 1: Write failing documentation contracts**

Add:

```javascript
test('macOS guide documents the one-command Docker Desktop boundary', async () => {
  const guide = await readRequired('docs/deployment/macos.md');
  for (const required of [
    'Docker Desktop',
    'Intel',
    'Apple Silicon',
    '/bin/bash -c "$(curl -fsSL',
    '~/Library/Application Support/ReachCommander',
    'Whole drives',
    'Specific folders',
    'Read-only',
    'Read/write',
    'This Mac only',
    'local network',
    '127.0.0.1:8080',
    'first-run setup code',
    'data/auth/account.json',
    'data/keys',
    'stable',
    'digest',
    'rollback',
    'Docker Desktop file sharing',
    'does not configure public internet access',
    'Linux container/VM',
    'not a native macOS application',
  ]) {
    assert.ok(
      guide.toLowerCase().includes(required.toLowerCase()),
      `macOS guide is missing: ${required}`,
    );
  }
  assert.match(guide, /installer-owned[\s\S]*masked/i);
  assert.doesNotMatch(guide, /(?:curl|wget)[^\r\n|]*\|[^\r\n]*(?:sh|bash)/i);
});

test('README and security policy advertise macOS without weakening boundaries', async () => {
  const [readme, policy] = await Promise.all([
    readRequired('README.md'),
    readRequired('SECURITY.md'),
  ]);
  assert.match(readme, /Install on macOS/i);
  assert.match(readme, /docs\/deployment\/macos\.md/);
  assert.match(readme, /Docker Desktop/i);
  assert.match(policy, /installer-owned[\s\S]*mask/i);
  assert.match(policy, /local network[\s\S]*authentication/i);
});
```

- [ ] **Step 2: Run documentation contracts to verify they fail**

```bash
node --test tests/installer/docs-contract.test.mjs
```

Expected: FAIL because `docs/deployment/macos.md` does not exist.

- [ ] **Step 3: Write the macOS deployment guide**

Create `docs/deployment/macos.md` with:

1. Docker Desktop prerequisite and the statement that Docker selects Intel/Apple Silicon image variants.
2. The approved one-command installer:

   ```bash
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh)"
   ```

3. An explicit warning that the convenience command executes a mutable `master` script and is not cryptographically pinned.
4. An inspect-first flow using `mktemp`, `curl --output`, `less`, and `/bin/bash "$INSTALLER"` with no download-to-shell pipe.
5. Exact whole-drive/specific-folder and Mac-only/LAN menus. Clarify that the internal “whole drive” choice means the current user's home, not the protected macOS system volume.
6. A warning that whole-home access includes hidden private data, requires exact-path confirmation for `RW`, and masks ReachCommander's own application-support directory.
7. Docker Desktop Files/Resources sharing instructions for an external path such as `/Volumes/Media` and macOS privacy prompts.
8. First-run instructions: obtain the random setup code with the printed logs command, then enter it with username/password in the browser.
9. State layout and account/key backup guidance.
10. Rerun behavior for update/reconfigure/exit, digest pinning, and rollback.
11. Start, stop, status, and logs commands using quoted `--project-directory` and `--file` paths.
12. Safe removal by stopping Compose and moving the installer-owned directory to a timestamped backup; never remove a configured source.
13. Troubleshooting for missing/stopped Docker, private GHCR package, port conflict, file-sharing denial, disconnected external drive, unavailable setup code, unhealthy update, HTTP LAN PWA limitations, and Linux VM/container telemetry.

- [ ] **Step 4: Update project and security documentation**

- Add `Install on macOS` after the Ubuntu section in `README.md` with the one command, guide link, Docker Desktop/non-native wording, and local-only default.
- Change the architecture/deployment summary from “Windows and Ubuntu support” to “native Windows development plus Docker deployment on Ubuntu and macOS.”
- Preserve the general recommendation for narrow mounts. Clarify that the macOS installer's advanced whole-home choice has a warning and mandatory installer-state mask.
- Update `deploy/README.md` so `deploy/` is not described as Ubuntu-only and identify `deploy/macos/install.sh` as the unprivileged bootstrap.
- Add `SECURITY.md` paragraphs stating that LAN mode is explicit/authenticated but not TLS, installer-owned authentication/configuration state is masked from broad sources, and specific sources remain recommended.

- [ ] **Step 5: Run documentation contracts**

```bash
node --test tests/installer/docs-contract.test.mjs
```

Expected: all documentation contract tests pass.

- [ ] **Step 6: Commit documentation**

```bash
git add \
  docs/deployment/macos.md \
  README.md \
  deploy/README.md \
  SECURITY.md \
  tests/installer/docs-contract.test.mjs
git commit -m "docs: add macOS Docker Desktop installation"
```

---

### Task 6: Run full verification and inspect the final change set

**Files:**
- Verify only; modify failing files narrowly if verification exposes a defect.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: evidence that the installer is syntactically valid, shell-clean, documented, CI-wired, and does not regress existing contracts.

- [ ] **Step 1: Run macOS-specific verification**

On macOS:

```bash
/bin/bash tests/installer/macos/test_helpers.sh
/bin/bash tests/installer/macos/test_install.sh
/bin/bash -n deploy/macos/install.sh tests/installer/macos/*.sh tests/installer/macos/fake-bin/*
```

Expected: both TAP suites pass and syntax validation exits 0.

- [ ] **Step 2: Run installer lint and contract tests**

On Ubuntu or the existing CI-compatible environment:

```bash
shellcheck -x --source-path=SCRIPTDIR \
  deploy/install.sh \
  deploy/reachcommander \
  deploy/lib/common.sh \
  deploy/package-installer.sh \
  deploy/macos/install.sh \
  tests/installer/test_common.sh \
  tests/installer/test_install.sh \
  tests/installer/test_command.sh \
  tests/installer/test_package.sh \
  tests/installer/macos/test_helpers.sh \
  tests/installer/macos/test_install.sh \
  tests/installer/macos/fake-bin/*
node --test \
  tests/installer/release-tags.test.mjs \
  tests/installer/workflow-contract.test.mjs \
  tests/installer/docs-contract.test.mjs
```

Expected: ShellCheck exits 0 and Node reports zero failed tests.

- [ ] **Step 3: Run existing installer and application regression suites**

```bash
python3 -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
dotnet test ReachCommander.slnx -c Release
```

Then from `client/reach-commander-ui`:

```bash
npm test -- --watch=false
```

Expected: every existing installer, .NET, and Angular test passes.

- [ ] **Step 4: Complete the documented Docker Desktop release smoke test**

On a real Intel or Apple Silicon Mac with Docker Desktop, use two dedicated disposable fixture directories—one `RO`, one `RW`, with at least one space in a path—and run the public one-command installer. After first-run account creation:

```bash
RC_MAC_ROOT="$HOME/Library/Application Support/ReachCommander"
RC_MAC_PORT="$(
  sed -n 's/^REACHCOMMANDER_PORT=//p' "$RC_MAC_ROOT/.env"
)"
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" config --quiet
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" ps
curl --fail --show-error --silent "http://127.0.0.1:$RC_MAC_PORT/health"
docker compose --project-name reachcommander \
  --project-directory "$RC_MAC_ROOT" \
  --file "$RC_MAC_ROOT/compose.yaml" up -d --force-recreate reachcommander
```

Expected: health succeeds; the UI can list both fixtures; a controlled write succeeds only in the `RW` fixture; the `RO` write is denied; a path containing spaces works; container recreation preserves the account/session keys; rerunning the installer with Update reaches a healthy digest; Mac-only mode is not reachable from another device; and optional LAN mode is reachable only after authentication. Record the Mac model, architecture, macOS version, Docker Desktop version, image digest, and result in the release notes.

- [ ] **Step 5: Inspect repository and deployment safety**

```bash
git diff --check
git status --short
git diff --stat HEAD~5..HEAD
git grep -nE '(^|[^[:alnum:]_])sudo([^[:alnum:]_]|$)' -- deploy/macos
git grep -nE '(^|[^[:alnum:]_])(eval|open|osascript)([^[:alnum:]_]|$)' -- deploy/macos
```

Expected:

- `git diff --check` prints nothing.
- Only intended installer/test/CI/documentation files are changed.
- Production macOS installer contains no `sudo`, `eval`, `open`, or `osascript`.
- The unrelated untracked `NC-theme.png` remains untouched and uncommitted.

- [ ] **Step 6: Commit any verification-only corrections**

If and only if verification required a narrow correction:

```bash
git add \
  deploy/macos/install.sh \
  tests/installer/macos \
  .github/workflows/ci.yml \
  tests/installer/workflow-contract.test.mjs \
  tests/installer/docs-contract.test.mjs \
  docs/deployment/macos.md \
  README.md \
  deploy/README.md \
  SECURITY.md
git commit -m "fix: harden macOS installer contracts"
```

If no corrections were needed, do not create an empty commit.

- [ ] **Step 7: Push and verify GitHub Actions when authorized**

```bash
git push origin master
RC_CI_RUN_ID="$(
  gh run list --workflow CI --branch master --limit 1 \
    --json databaseId --jq '.[0].databaseId'
)"
gh run watch "$RC_CI_RUN_ID" --exit-status
```

Expected: backend Windows/Ubuntu, frontend/browser acceptance, macOS installer contracts, hardened container smoke, and publication jobs all complete successfully according to their event conditions.
