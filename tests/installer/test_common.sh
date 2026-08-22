#!/usr/bin/env bash
# The production-path probe intentionally re-sources the library in a subshell.
# shellcheck disable=SC2031
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
COMMON="$REPOSITORY_ROOT/deploy/lib/common.sh"
FAKE_BIN="$TEST_DIRECTORY/fake-bin"

if [[ ! -f "$COMMON" ]]; then
  printf 'not ok - deploy/lib/common.sh must exist\n' >&2
  exit 1
fi

export PATH="$FAKE_BIN:$PATH"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEST_ROOT"' EXIT
export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_BASE="$TEST_ROOT"
export REACHCOMMANDER_TEST_INSTALL_ROOT="$TEST_ROOT/install root"
export REACHCOMMANDER_TEST_COMMAND_PATH="$TEST_ROOT/bin/reachcommander"
export REACHCOMMANDER_TEST_BACKUP_ROOT="$TEST_ROOT/backups"
export REACHCOMMANDER_TEST_LOCK_PATH="$TEST_ROOT/.reachcommander.lock"

# shellcheck source=../../deploy/lib/common.sh
source "$COMMON"

tests_run=0

pass() {
  tests_run=$((tests_run + 1))
  printf 'ok %d - %s\n' "$tests_run" "$1"
}

fail() {
  printf 'not ok - %s\n' "$1" >&2
  exit 1
}

assert_equal() {
  local expected="$1"
  local actual="$2"
  local message="$3"
  [[ "$expected" == "$actual" ]] || fail "$message (expected '$expected', got '$actual')"
}

assert_fails() {
  local message="$1"
  shift
  if "$@" >/dev/null 2>&1; then
    fail "$message"
  fi
}

for fake_command in docker flock python3 setpriv sync; do
  [[ -x "$FAKE_BIN/$fake_command" ]] || fail "fake command is not executable: $fake_command"
done
pass "fake installer commands are executable"

rc_init_paths
assert_equal "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$RC_INSTALL_ROOT" "test install root"
assert_equal "$REACHCOMMANDER_TEST_COMMAND_PATH" "$RC_COMMAND_PATH" "test command path"
assert_equal "$REACHCOMMANDER_TEST_BACKUP_ROOT" "$RC_BACKUP_ROOT" "test backup root"
assert_equal "$REACHCOMMANDER_TEST_LOCK_PATH" "$RC_LOCK_PATH" "test lock path"
pass "test-only path overrides are explicit"

production_paths="$({
  unset REACHCOMMANDER_TESTING REACHCOMMANDER_TEST_BASE
  unset REACHCOMMANDER_TEST_INSTALL_ROOT REACHCOMMANDER_TEST_COMMAND_PATH
  unset REACHCOMMANDER_TEST_BACKUP_ROOT REACHCOMMANDER_TEST_LOCK_PATH
  # shellcheck source=../../deploy/lib/common.sh
  source "$COMMON"
  rc_init_paths
  printf '%s|%s|%s|%s' "$RC_INSTALL_ROOT" "$RC_COMMAND_PATH" "$RC_BACKUP_ROOT" "$RC_LOCK_PATH"
})"
assert_equal "/opt/reachcommander|/usr/local/bin/reachcommander|/var/backups/reachcommander|/opt/.reachcommander.lock" "$production_paths" "production paths"
pass "production ignores arbitrary path environment"

rc_require_commands bash printf
assert_fails "missing prerequisite must fail" rc_require_commands reachcommander-command-that-does-not-exist
pass "prerequisite validation is fail-closed"

if (( EUID != 0 )); then
  assert_fails "non-root caller must be rejected" rc_require_root
fi
pass "root requirement is enforced"

invoking_ids="$({ SUDO_UID=1234 SUDO_GID=5678 rc_invoking_ids; })"
assert_equal "1234:5678" "$invoking_ids" "sudo invoking identity"
assert_fails "zero sudo UID must be rejected" env SUDO_UID=0 SUDO_GID=5678 bash -c "source '$COMMON'; rc_invoking_ids"
pass "invoking identity rejects root"

for port in 1 8092 65535; do
  rc_validate_port "$port"
done
for port in 0 65536 +1 1.5 text ''; do
  assert_fails "invalid port '$port' must fail" rc_validate_port "$port"
done
pass "port validation is bounded"

assert_equal "family-media" "$(rc_normalize_source_id 'Family Media')" "normalized ID"
assert_equal "media-2026" "$(rc_normalize_source_id '  MEDIA__2026  ')" "normalized separators"
assert_fails "empty normalized source ID must fail" rc_normalize_source_id '***'
pass "source IDs are normalized predictably"

mkdir -p "$TEST_ROOT/sources/family media"
canonical="$(rc_canonical_source "$TEST_ROOT/sources/family media")"
assert_equal "$(readlink -f -- "$TEST_ROOT/sources/family media")" "$canonical" "canonical source"
rc_validate_source_path "$canonical"
for dangerous in / /proc /proc/1 /sys/class /dev/null /run /var/run/docker.sock; do
  assert_fails "dangerous source '$dangerous' must fail" rc_validate_source_path "$dangerous"
done
pass "source paths are canonical and dangerous roots are rejected"

for channel in stable edge v1.2.3 v1.2.3-beta.1; do
  rc_validate_channel "$channel"
done
for channel in latest v1.2 v01.2.3 'stable;id' $'edge\nnext'; do
  assert_fails "invalid channel '$channel' must fail" rc_validate_channel "$channel"
done
pass "release channels are strictly validated"

export FAKE_FLOCK_LOG="$TEST_ROOT/flock.log"
rc_acquire_lock
[[ -f "$RC_LOCK_PATH" ]] || fail "external lock file was not created"
[[ ! -e "$RC_INSTALL_ROOT/state/command.lock" ]] || fail "lock must not live inside the replaceable deployment tree"
mapfile -d '' flock_arguments <"$FAKE_FLOCK_LOG"
assert_equal "-n" "${flock_arguments[0]}" "flock nonblocking argument"
assert_equal "9" "${flock_arguments[1]}" "flock descriptor"
pass "management lock uses descriptor 9 nonblockingly"

export FAKE_DOCKER_LOG="$TEST_ROOT/docker.log"
export FAKE_DOCKER_PRINT_ARGS=1
compose_output="$(rc_compose logs 'file name with spaces')"
assert_equal $'[compose]\n[--project-directory]\n['"$RC_INSTALL_ROOT"$']\n[logs]\n[file name with spaces]' "$compose_output" "Compose argv"
mapfile -d '' docker_arguments <"$FAKE_DOCKER_LOG"
assert_equal "file name with spaces" "${docker_arguments[4]}" "whitespace remains one argument"
pass "Compose always uses the fixed project directory and quoted argv"

export FAKE_DOCKER_PRINT_ARGS=0
FAKE_DOCKER_DIGESTS=$'docker.io/untrusted/image@sha256:'"$(printf 'f%.0s' {1..64})"$'\nghcr.io/dragosniamtu/reach-commander@sha256:'"$(printf 'a%.0s' {1..64})"
export FAKE_DOCKER_DIGESTS
digest="$(rc_pull_digest stable)"
assert_equal "ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})" "$digest" "resolved digest"
export FAKE_DOCKER_DIGESTS='malformed'
assert_fails "malformed digest output must fail" rc_pull_digest stable
pass "digest resolution accepts only the ReachCommander repository"

export FAKE_DOCKER_HEALTH=healthy
rc_wait_healthy reachcommander 1
export FAKE_DOCKER_HEALTH=unhealthy
assert_fails "unhealthy container must fail immediately" rc_wait_healthy reachcommander 2
pass "health polling accepts only healthy containers"

atomic_target="$TEST_ROOT/state/value"
export FAKE_SYNC_LOG="$TEST_ROOT/sync.log"
: >"$FAKE_SYNC_LOG"
rc_atomic_write "$atomic_target" $'value with spaces\nand a newline\n'
assert_equal $'value with spaces\nand a newline' "$(cat -- "$atomic_target")" "atomic content"
[[ ! -e "$TEST_ROOT/state/.value.tmp" ]] || fail "atomic temporary file leaked"
[[ -s "$FAKE_SYNC_LOG" ]] || fail "atomic write must synchronize its temporary file"
mapfile -d '' sync_arguments <"$FAKE_SYNC_LOG"
assert_equal "-f" "${sync_arguments[0]}" "file sync mode"
assert_equal "--" "${sync_arguments[1]}" "file sync option terminator"
printf 'old value' >"$atomic_target"
export FAKE_SYNC_EXIT=1
assert_fails "sync failure must prevent replacement" rc_atomic_write "$atomic_target" 'new value'
assert_equal "old value" "$(cat -- "$atomic_target")" "sync failure preserves destination"
export FAKE_SYNC_EXIT=0
pass "atomic writes preserve exact content"

rc_assert_safe_install_root
original_install_root="$RC_INSTALL_ROOT"
original_lock_path="$RC_LOCK_PATH"
RC_INSTALL_ROOT=/
assert_fails "broad install root must fail" rc_assert_safe_install_root
RC_INSTALL_ROOT="$original_install_root"
RC_LOCK_PATH=/reachcommander.lock
assert_fails "test lock outside the test base must fail" rc_assert_safe_install_root
RC_LOCK_PATH="$original_lock_path"
pass "install root safety rejects broad targets"

if grep -Eq '(^|[^[:alnum:]_])eval([^[:alnum:]_]|$)' "$COMMON"; then
  fail "common helpers must not use eval"
fi
pass "helpers do not use shell evaluation"

printf '1..%d\n' "$tests_run"
