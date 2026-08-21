#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
COMMAND_SOURCE="$REPOSITORY_ROOT/deploy/reachcommander"
RENDERER="$REPOSITORY_ROOT/deploy/render_config.py"
TEMPLATE="$REPOSITORY_ROOT/deploy/compose.release.yaml"
FIXTURE="$TEST_DIRECTORY/fixtures/valid-request.json"
FAKE_BIN="$TEST_DIRECTORY/fake-bin"

if [[ ! -f "$COMMAND_SOURCE" ]]; then
  printf 'not ok - deploy/reachcommander must exist\n' >&2
  exit 1
fi

export PATH="$FAKE_BIN:$PATH"

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEST_ROOT"' EXIT
INSTALL_ROOT="$TEST_ROOT/install root"
COMMAND_PATH="$TEST_ROOT/bin/reachcommander"
SOURCE_PATH="$TEST_ROOT/source data"
mkdir -p "$INSTALL_ROOT/bin" "$INSTALL_ROOT/lib" "$INSTALL_ROOT/state" "$SOURCE_PATH" "$(dirname -- "$COMMAND_PATH")"
cp -- "$REPOSITORY_ROOT/deploy/lib/common.sh" "$INSTALL_ROOT/lib/common.sh"
cp -- "$RENDERER" "$INSTALL_ROOT/bin/render_config.py"
cp -- "$COMMAND_SOURCE" "$COMMAND_PATH"
chmod +x "$COMMAND_PATH"
printf 'source canary\n' >"$SOURCE_PATH/canary.txt"

REQUEST="$TEST_ROOT/request.json"
python3 "$RENDERER" create-request \
  --output "$REQUEST" \
  --bind-address 127.0.0.1 \
  --port 8092 \
  --uid 1000 \
  --gid 1000 \
  --image "ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})"
python3 "$RENDERER" add-source \
  --request "$REQUEST" \
  --id media \
  --name Media \
  --host-path "$SOURCE_PATH" \
  --access rw \
  --default-left true \
  --default-right true
python3 "$RENDERER" render --request "$REQUEST" --template "$TEMPLATE" --output "$INSTALL_ROOT"
printf 'stable\n' >"$INSTALL_ROOT/state/channel"
grep '^REACHCOMMANDER_IMAGE=' "$INSTALL_ROOT/.env" | cut -d= -f2- >"$INSTALL_ROOT/state/current-image"
: >"$INSTALL_ROOT/state/previous-image"
: >"$INSTALL_ROOT/state/command.lock"

export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_BASE="$TEST_ROOT"
export REACHCOMMANDER_TEST_INSTALL_ROOT="$INSTALL_ROOT"
export REACHCOMMANDER_TEST_COMMAND_PATH="$COMMAND_PATH"
export REACHCOMMANDER_TEST_BACKUP_ROOT="$TEST_ROOT/backups"
export FAKE_DOCKER_LOG="$TEST_ROOT/docker.log"
export FAKE_FLOCK_LOG="$TEST_ROOT/flock.log"
export FAKE_DOCKER_HEALTH=healthy
export FAKE_DOCKER_COMPOSE_EXIT=0
export FAKE_DOCKER_CONFIG_EXIT=0
export FAKE_DOCKER_VERSION_EXIT=0
export FAKE_DOCKER_INSPECT_EXIT=0
export FAKE_FLOCK_EXIT=0

tests_run=0
last_status=0
last_output=''

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

run_command() {
  set +e
  last_output="$(bash "$COMMAND_SOURCE" "$@" 2>&1)"
  last_status=$?
  set -e
}

for invocation in \
  'unknown' \
  'status extra' \
  'logs --invalid' \
  'start extra' \
  'doctor extra'; do
  read -r -a invocation_arguments <<<"$invocation"
  run_command "${invocation_arguments[@]}"
  assert_equal "64" "$last_status" "usage status for $invocation"
done
pass "command dispatcher rejects unknown commands and extra arguments"

: >"$FAKE_DOCKER_LOG"
run_command status
assert_equal "0" "$last_status" "status command"
[[ "$last_output" == *'Channel: stable'* ]] || fail "status channel missing"
[[ "$last_output" == *'Image: ghcr.io/dragosniamtu/reach-commander@sha256:'* ]] || fail "status image missing"
mapfile -d '' status_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${status_args[@]}" | grep -q '^ps$' || fail "status did not call Compose ps"
pass "status reports immutable image, channel, and Compose state"

: >"$FAKE_DOCKER_LOG"
run_command logs
assert_equal "0" "$last_status" "logs command"
mapfile -d '' logs_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${logs_args[@]}" | grep -q '^--tail$' || fail "logs tail missing"
printf '%s\n' "${logs_args[@]}" | grep -q '^200$' || fail "logs tail count missing"

: >"$FAKE_DOCKER_LOG"
run_command logs --follow
assert_equal "0" "$last_status" "follow logs command"
mapfile -d '' follow_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${follow_args[@]}" | grep -q '^--follow$' || fail "logs follow missing"
pass "logs supports only bounded output and explicit follow mode"

for lifecycle in start stop restart; do
  : >"$FAKE_DOCKER_LOG"
  : >"$FAKE_FLOCK_LOG"
  run_command "$lifecycle"
  assert_equal "0" "$last_status" "$lifecycle command"
  [[ -s "$FAKE_FLOCK_LOG" ]] || fail "$lifecycle did not acquire command lock"
  mapfile -d '' lifecycle_args <"$FAKE_DOCKER_LOG"
  case "$lifecycle" in
    start)
      printf '%s\n' "${lifecycle_args[@]}" | grep -q '^up$' || fail "start did not call Compose up"
      ;;
    stop)
      printf '%s\n' "${lifecycle_args[@]}" | grep -q '^stop$' || fail "stop did not call Compose stop"
      ;;
    restart)
      printf '%s\n' "${lifecycle_args[@]}" | grep -q '^restart$' || fail "restart did not call Compose restart"
      ;;
  esac
done
pass "lifecycle commands are locked and map to bounded Compose operations"

deployment_before="$(find "$INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
: >"$FAKE_FLOCK_LOG"
run_command doctor
if (( last_status != 0 )); then
  printf '%s\n' "$last_output" >&2
fi
assert_equal "0" "$last_status" "healthy doctor status"
[[ "$last_output" == *'[PASS] Docker Engine is available'* ]] || fail "doctor Docker pass missing"
[[ "$last_output" == *'[PASS] Container is healthy'* ]] || fail "doctor health pass missing"
[[ "$last_output" != *'[FAIL]'* ]] || fail "healthy doctor reported a failure"
[[ ! -s "$FAKE_FLOCK_LOG" ]] || fail "doctor acquired a mutating lock"
assert_equal "$deployment_before" "$(find "$INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "doctor deployment mutation"
pass "doctor reports a healthy deployment without mutating it"

cp -- "$INSTALL_ROOT/.env" "$TEST_ROOT/env.backup"
sed 's/^REACHCOMMANDER_BIND_ADDRESS=.*/REACHCOMMANDER_BIND_ADDRESS=0.0.0.0/' "$TEST_ROOT/env.backup" >"$INSTALL_ROOT/.env"
run_command doctor
assert_equal "0" "$last_status" "doctor warning status"
[[ "$last_output" == *'[WARN] Bind address is not loopback'* ]] || fail "doctor loopback warning missing"
cp -- "$TEST_ROOT/env.backup" "$INSTALL_ROOT/.env"
pass "doctor warns without failing for non-loopback exposure"

cp -- "$INSTALL_ROOT/state/current-image" "$TEST_ROOT/current-image.backup"
printf 'ghcr.io/dragosniamtu/reach-commander@sha256:%s\n' "$(printf 'b%.0s' {1..64})" >"$INSTALL_ROOT/state/current-image"
run_command doctor
assert_equal "1" "$last_status" "doctor image mismatch status"
[[ "$last_output" == *'[FAIL] Environment image does not match current-image state'* ]] || fail "doctor image mismatch failure missing"
cp -- "$TEST_ROOT/current-image.backup" "$INSTALL_ROOT/state/current-image"
pass "doctor fails on inconsistent immutable image state"

mv -- "$SOURCE_PATH" "$TEST_ROOT/source-away"
run_command doctor
assert_equal "1" "$last_status" "doctor missing source status"
[[ "$last_output" == *'[FAIL] Source path is missing'* ]] || fail "doctor missing source failure missing"
mv -- "$TEST_ROOT/source-away" "$SOURCE_PATH"
pass "doctor fails when a configured host source is unavailable"

cp -- "$INSTALL_ROOT/config/sources.json" "$TEST_ROOT/sources.backup"
printf '{ invalid json\n' >"$INSTALL_ROOT/config/sources.json"
run_command doctor
assert_equal "1" "$last_status" "doctor invalid JSON status"
[[ "$last_output" == *'[FAIL] Application source configuration is invalid JSON'* ]] || fail "doctor JSON failure missing"
cp -- "$TEST_ROOT/sources.backup" "$INSTALL_ROOT/config/sources.json"
pass "doctor rejects invalid application source JSON"

mv -- "$COMMAND_PATH" "$TEST_ROOT/reachcommander-away"
run_command doctor
assert_equal "1" "$last_status" "doctor missing management command status"
[[ "$last_output" == *'[FAIL] Management command is missing, symlinked, or not executable'* ]] || fail "doctor command failure missing"
mv -- "$TEST_ROOT/reachcommander-away" "$COMMAND_PATH"
pass "doctor fails when the fixed management command is unavailable"

assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "command source canary"
printf '1..%d\n' "$tests_run"
