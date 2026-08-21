#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
INSTALLER_SOURCE="$REPOSITORY_ROOT/deploy/install.sh"
FAKE_BIN="$TEST_DIRECTORY/fake-bin"

if [[ ! -f "$INSTALLER_SOURCE" ]]; then
  printf 'not ok - deploy/install.sh must exist\n' >&2
  exit 1
fi

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEST_ROOT"' EXIT
BUNDLE="$TEST_ROOT/bundle"
mkdir -p "$BUNDLE/lib"
cp -- "$INSTALLER_SOURCE" "$BUNDLE/install.sh"
cp -- "$REPOSITORY_ROOT/deploy/render_config.py" "$BUNDLE/render_config.py"
cp -- "$REPOSITORY_ROOT/deploy/compose.release.yaml" "$BUNDLE/compose.release.yaml"
cp -- "$REPOSITORY_ROOT/deploy/lib/common.sh" "$BUNDLE/lib/common.sh"
printf '#!/usr/bin/env bash\nprintf "management placeholder\\n"\n' >"$BUNDLE/reachcommander"
chmod +x "$BUNDLE/install.sh" "$BUNDLE/reachcommander"

SOURCE_ONE="$TEST_ROOT/source one"
SOURCE_TWO="$TEST_ROOT/source-two"
mkdir -p "$SOURCE_ONE" "$SOURCE_TWO"
printf 'keep one\n' >"$SOURCE_ONE/canary.txt"
printf 'keep two\n' >"$SOURCE_TWO/canary.txt"

export PATH="$FAKE_BIN:$PATH"
export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_BASE="$TEST_ROOT"
export REACHCOMMANDER_TEST_INSTALL_ROOT="$TEST_ROOT/install root"
export REACHCOMMANDER_TEST_COMMAND_PATH="$TEST_ROOT/bin/reachcommander"
export REACHCOMMANDER_TEST_BACKUP_ROOT="$TEST_ROOT/external backups"
export SUDO_UID=1000
export SUDO_GID=1000
export FAKE_DOCKER_LOG="$TEST_ROOT/docker.log"
export FAKE_FLOCK_LOG="$TEST_ROOT/flock.log"
export FAKE_SYNC_LOG="$TEST_ROOT/sync.log"
export FAKE_DOCKER_DIGESTS="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})"
export FAKE_DOCKER_HEALTH=healthy
export FAKE_DOCKER_COMPOSE_EXIT=0
export FAKE_DOCKER_CONFIG_EXIT=0
export FAKE_DOCKER_VERSION_EXIT=0
export FAKE_DOCKER_PULL_EXIT=0
export FAKE_DOCKER_INSPECT_EXIT=0
export FAKE_FLOCK_EXIT=0
export FAKE_SYNC_EXIT=0

tests_run=0
last_status=0

pass() {
  tests_run=$((tests_run + 1))
  printf 'ok %d - %s\n' "$tests_run" "$1"
}

skip() {
  tests_run=$((tests_run + 1))
  printf 'ok %d - %s # SKIP %s\n' "$tests_run" "$1" "$2"
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

run_installer() {
  local input="$1"
  local output="$2"
  set +e
  printf '%s\n' "$input" | bash "$BUNDLE/install.sh" >"$output" 2>&1
  last_status=$?
  set -e
}

source_prompt_input() {
  local first_name="${1:-Family Media}"
  local first_access="${2:-RO}"
  local second_name="${3:-Movies}"
  local second_access="${4:-RW}"
  local first_id="${5:-family-media}"
  local second_id="${6:-movies}"
  printf '%s\n' \
    '' \
    '' \
    '' \
    '' \
    "$first_name" \
    "$SOURCE_ONE" \
    '' \
    "$first_access" \
    'y' \
    "$second_name" \
    "$SOURCE_TWO" \
    '' \
    "$second_access" \
    'n' \
    "$first_id" \
    "$second_id" \
    'I have authenticated HTTPS'
}

rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
NO_DOCKER_BIN="$TEST_ROOT/no-docker-bin"
mkdir -p "$NO_DOCKER_BIN"
for command_name in python3 flock setpriv sync; do
  cp -- "$FAKE_BIN/$command_name" "$NO_DOCKER_BIN/$command_name"
done
chmod +x "$NO_DOCKER_BIN"/*
set +e
PATH="$NO_DOCKER_BIN:/usr/bin" bash "$BUNDLE/install.sh" </dev/null >"$TEST_ROOT/preflight.out" 2>&1
preflight_status=$?
set -e
(( preflight_status != 0 )) || fail "missing Docker must fail preflight"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "preflight failure wrote deployment"
pass "missing prerequisite stops before deployment writes"

collision_input="$(printf '%s\n' \
  '' '' '' '' \
  'Media' "$SOURCE_ONE" '' 'RO' 'y' \
  'Media' "$SOURCE_TWO" '' 'media-two' 'RW' 'n' \
  'media' 'media-two' 'I have authenticated HTTPS')"
run_installer "$collision_input" "$TEST_ROOT/collision.out"
assert_equal "0" "$last_status" "source ID collision recovery status"
grep -q '"id": "media-two"' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json" || fail "replacement source ID missing"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "source ID collisions prompt for a distinct replacement"

overlap_input="$(printf '%s\n' \
  '' '' '' '' \
  'Installer Parent' "$TEST_ROOT" 'installer-parent' 'RO' 'n' \
  'installer-parent' 'installer-parent' 'I have authenticated HTTPS')"
run_installer "$overlap_input" "$TEST_ROOT/overlap.out"
(( last_status != 0 )) || fail "source ancestor of installer paths must be rejected"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "overlapping source installed deployment"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "overlap source canary"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "sources cannot overlap installer-owned paths"

mkdir -p "$REACHCOMMANDER_TEST_INSTALL_ROOT"
if ln -s -- "$SOURCE_ONE" "$REACHCOMMANDER_TEST_INSTALL_ROOT/config" 2>/dev/null && [[ -L "$REACHCOMMANDER_TEST_INSTALL_ROOT/config" ]]; then
  run_installer "$(source_prompt_input)" "$TEST_ROOT/symlink-layout.out"
  (( last_status != 0 )) || fail "symlinked generated directory must be rejected"
  [[ ! -e "$SOURCE_ONE/sources.json" ]] || fail "installer followed a generated-directory symlink"
  pass "symlinked installer layout is rejected before writes"
else
  skip "symlinked installer layout is rejected before writes" "filesystem does not expose POSIX symlinks"
fi
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"

install_input="$(source_prompt_input)"
run_installer "$install_input" "$TEST_ROOT/install.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/install.out" >&2
fi
assert_equal "0" "$last_status" "first installation status"
for required_file in \
  .env compose.yaml config/sources.json state/source-mounts.json \
  state/channel state/current-image state/previous-image state/command.lock \
  bin/render_config.py lib/common.sh; do
  [[ -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/$required_file" ]] || fail "missing installed $required_file"
done
[[ -x "$REACHCOMMANDER_TEST_COMMAND_PATH" ]] || fail "management command was not installed"
grep -q '^REACHCOMMANDER_BIND_ADDRESS=127.0.0.1$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "loopback default missing"
grep -q '^REACHCOMMANDER_PORT=8092$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "port default missing"
grep -q '^REACHCOMMANDER_UID=1000$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "UID default missing"
grep -q '^REACHCOMMANDER_GID=1000$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "GID default missing"
grep -q '^REACHCOMMANDER_IMAGE=ghcr.io/dragosniamtu/reach-commander@sha256:a\{64\}$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "digest pin missing"
grep -q 'read_only: true' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "RO mount missing"
grep -q 'read_only: false' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "RW mount missing"
assert_equal "stable" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel")" "saved channel"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "first source canary"
assert_equal "keep two" "$(cat -- "$SOURCE_TWO/canary.txt")" "second source canary"
pass "first install renders mixed sources and starts a digest-pinned service"

deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer $'n\n' "$TEST_ROOT/decline.out"
(( last_status == 0 )) || fail "declining reconfiguration should exit cleanly"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "declined deployment"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "declined command"
pass "existing deployment is never overwritten without confirmation"

reconfigure_success_input=$'y\n'"$(source_prompt_input 'Renamed Media' 'RO' 'Renamed Movies' 'RW' 'renamed-media' 'renamed-movies')"
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-success.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/reconfigure-success.out" >&2
fi
assert_equal "0" "$last_status" "successful reconfiguration status"
grep -q 'Renamed Media' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json" || fail "reconfigured source name missing"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "reconfiguration source canary"
pass "healthy reconfiguration replaces generated configuration"

export FAKE_DOCKER_CONFIG_EXIT=1
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer "$install_input" "$TEST_ROOT/config-failure.out"
(( last_status != 0 )) || fail "Compose validation failure must fail install"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "invalid deployment was installed"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT" ]] || fail "Compose validation leaked the lock scaffold"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "config failure source canary"
export FAKE_DOCKER_CONFIG_EXIT=0
pass "Compose validation failure leaves no active deployment"

export FAKE_DOCKER_PULL_EXIT=1
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer "$install_input" "$TEST_ROOT/pull-failure.out"
(( last_status != 0 )) || fail "pull failure must fail install"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "pull failure installed deployment"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT" ]] || fail "pull failure leaked the lock scaffold"
export FAKE_DOCKER_PULL_EXIT=0
pass "image pull failure leaves staged state unpublished"

rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
export FAKE_DOCKER_HEALTH=unhealthy
: >"$FAKE_DOCKER_LOG"
run_installer "$install_input" "$TEST_ROOT/unhealthy-first.out"
(( last_status != 0 )) || fail "unhealthy first start must fail"
[[ -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "validated configuration should remain for diagnosis"
mapfile -d '' unhealthy_docker_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${unhealthy_docker_args[@]}" | grep -q '^down$' || fail "failed first start did not tear down Compose"
assert_equal "keep two" "$(cat -- "$SOURCE_TWO/canary.txt")" "unhealthy source canary"
export FAKE_DOCKER_HEALTH=healthy
pass "unhealthy first start retains diagnostics and removes failed service"

rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer "$install_input" "$TEST_ROOT/install-for-reconfigure.out"
assert_equal "0" "$last_status" "baseline reconfiguration install"
deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
printf 'unhealthy\nhealthy\n' >"$TEST_ROOT/health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/health-sequence"
reconfigure_input=$'y\n'"$(source_prompt_input 'Changed Media' 'RO' 'Changed Movies' 'RW' 'changed-media' 'changed-movies')"
run_installer "$reconfigure_input" "$TEST_ROOT/reconfigure-failure.out"
(( last_status != 0 )) || fail "unhealthy reconfiguration must report failure"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "rolled-back deployment"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "rolled-back command"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "rollback source canary"
unset FAKE_DOCKER_HEALTH_FILE
pass "unhealthy reconfiguration restores the complete prior deployment"

rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
interrupted_input="$(source_prompt_input | sed '$d')"
run_installer "$interrupted_input" "$TEST_ROOT/interrupted.out"
(( last_status != 0 )) || fail "interrupted acknowledgement must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "interrupted input installed deployment"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT" ]] || fail "interrupted input leaked the lock scaffold"
assert_equal "keep two" "$(cat -- "$SOURCE_TWO/canary.txt")" "interruption source canary"
pass "interrupted input cleans only installer-owned staging"

rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
signal_fifo="$TEST_ROOT/signal-input"
mkfifo "$signal_fifo"
bash "$BUNDLE/install.sh" <"$signal_fifo" >"$TEST_ROOT/signal.out" 2>&1 &
signal_pid=$!
exec 7>"$signal_fifo"
sleep 1
kill -TERM "$signal_pid"
exec 7>&-
set +e
wait "$signal_pid"
signal_status=$?
set -e
assert_equal "143" "$signal_status" "TERM exit status"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "terminated installer wrote deployment"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "termination source canary"
pass "termination exits immediately and preserves sources"

printf '1..%d\n' "$tests_run"
