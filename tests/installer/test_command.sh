#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
COMMAND_SOURCE="$REPOSITORY_ROOT/deploy/reachcommander"
RENDERER="$REPOSITORY_ROOT/deploy/render_config.py"
SOURCE_MANAGEMENT="$REPOSITORY_ROOT/deploy/source_management.py"
TEMPLATE="$REPOSITORY_ROOT/deploy/compose.release.yaml"
UPDATER_COMPOSE="$REPOSITORY_ROOT/deploy/compose.updater.yaml"
UPDATER_PROTOCOL="$REPOSITORY_ROOT/deploy/updater_protocol.py"
UPDATER_SERVICE="$REPOSITORY_ROOT/deploy/updater_service.py"
UPDATER_TRACE="$REPOSITORY_ROOT/deploy/updater_trace.py"
UPDATE_TRACE_CLI="$REPOSITORY_ROOT/deploy/update_trace_cli.py"
SUPPORT_BUNDLE="$REPOSITORY_ROOT/deploy/support_bundle.py"
SUPPORT_BUNDLE_CLI="$REPOSITORY_ROOT/deploy/support_bundle_cli.py"
UPDATER_UNIT="$REPOSITORY_ROOT/deploy/systemd/reachcommander-updater.service"
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
RUNTIME_UID="$(id -u)"
RUNTIME_GID="$(id -g)"
if [[ "$RUNTIME_UID" == '0' ]]; then
  RUNTIME_UID=1000
  RUNTIME_GID=1000
fi
mkdir -p \
  "$INSTALL_ROOT/bin" \
  "$INSTALL_ROOT/lib" \
  "$INSTALL_ROOT/state" \
  "$INSTALL_ROOT/data/auth" \
  "$INSTALL_ROOT/data/keys" \
  "$INSTALL_ROOT/data/file-operations/plans" \
  "$INSTALL_ROOT/data/file-operations/operations" \
  "$SOURCE_PATH" \
  "$(dirname -- "$COMMAND_PATH")"
cp -- "$REPOSITORY_ROOT/deploy/lib/common.sh" "$INSTALL_ROOT/lib/common.sh"
cp -- "$RENDERER" "$INSTALL_ROOT/bin/render_config.py"
cp -- "$SOURCE_MANAGEMENT" "$INSTALL_ROOT/bin/source_management.py"
cp -- "$TEMPLATE" "$INSTALL_ROOT/lib/compose.release.yaml"
cp -- "$UPDATER_SERVICE" "$INSTALL_ROOT/bin/updater_service.py"
cp -- "$SUPPORT_BUNDLE_CLI" "$INSTALL_ROOT/bin/support_bundle_cli.py"
cp -- "$UPDATE_TRACE_CLI" "$INSTALL_ROOT/bin/update_trace_cli.py"
cp -- "$SUPPORT_BUNDLE" "$INSTALL_ROOT/lib/support_bundle.py"
cp -- "$UPDATER_PROTOCOL" "$INSTALL_ROOT/lib/updater_protocol.py"
cp -- "$UPDATER_TRACE" "$INSTALL_ROOT/lib/updater_trace.py"
cp -- "$UPDATER_COMPOSE" "$INSTALL_ROOT/compose.override.yaml"
cp -- "$COMMAND_SOURCE" "$COMMAND_PATH"
chmod +x "$COMMAND_PATH"
printf 'source canary\n' >"$SOURCE_PATH/canary.txt"

REQUEST="$TEST_ROOT/request.json"
python3 "$RENDERER" create-request \
  --output "$REQUEST" \
  --bind-address 127.0.0.1 \
  --port 8092 \
  --uid "$RUNTIME_UID" \
  --gid "$RUNTIME_GID" \
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
mkdir -p -- "$INSTALL_ROOT/backups"
chmod 0700 -- "$INSTALL_ROOT" "$INSTALL_ROOT/bin" "$INSTALL_ROOT/lib" "$INSTALL_ROOT/state" "$INSTALL_ROOT/backups"
chmod 0600 -- "$INSTALL_ROOT/.env" "$INSTALL_ROOT/compose.yaml" "$INSTALL_ROOT/compose.override.yaml" "$INSTALL_ROOT/state/source-mounts.json" "$INSTALL_ROOT/lib/compose.release.yaml"
chmod 0755 -- "$INSTALL_ROOT/config" "$INSTALL_ROOT/bin/render_config.py" "$INSTALL_ROOT/bin/source_management.py"
chmod 0644 -- "$INSTALL_ROOT/config/sources.json"
printf 'stable\n' >"$INSTALL_ROOT/state/channel"
grep '^REACHCOMMANDER_IMAGE=' "$INSTALL_ROOT/.env" | cut -d= -f2- >"$INSTALL_ROOT/state/current-image"
: >"$INSTALL_ROOT/state/previous-image"
printf 'v1.3.0\n' >"$INSTALL_ROOT/state/current-version"
: >"$INSTALL_ROOT/state/previous-version"
printf '{"username":"command-test","passwordHash":"fixture","securityStamp":"fixture"}\n' \
  >"$INSTALL_ROOT/data/auth/account.json"
printf '{"verifier":"fixture"}\n' >"$INSTALL_ROOT/data/auth/bootstrap.json"
printf '<key id="command-fixture" />\n' >"$INSTALL_ROOT/data/keys/key-command.xml"
printf '{"schemaVersion":1,"kind":"copy"}\n' \
  >"$INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json"
printf '{"schemaVersion":1,"phase":"completed"}\n' \
  >"$INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
chmod 0700 \
  "$INSTALL_ROOT/data" \
  "$INSTALL_ROOT/data/auth" \
  "$INSTALL_ROOT/data/keys" \
  "$INSTALL_ROOT/data/file-operations" \
  "$INSTALL_ROOT/data/file-operations/plans" \
  "$INSTALL_ROOT/data/file-operations/operations"
chmod 0600 \
  "$INSTALL_ROOT/data/auth/account.json" \
  "$INSTALL_ROOT/data/auth/bootstrap.json" \
  "$INSTALL_ROOT/data/keys/key-command.xml" \
  "$INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
  "$INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
if (( EUID == 0 )); then
  chown -R "$RUNTIME_UID:$RUNTIME_GID" "$INSTALL_ROOT/data"
fi

export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_BASE="$TEST_ROOT"
export REACHCOMMANDER_TEST_INSTALL_ROOT="$INSTALL_ROOT"
export REACHCOMMANDER_TEST_COMMAND_PATH="$COMMAND_PATH"
export REACHCOMMANDER_TEST_BACKUP_ROOT="$TEST_ROOT/backups"
export REACHCOMMANDER_TEST_LOCK_PATH="$TEST_ROOT/.reachcommander.lock"
export REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH="$TEST_ROOT/systemd/reachcommander-updater.service"
export REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY="$TEST_ROOT/run/reachcommander-updater"
export REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH="$REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY/updater.sock"
export FAKE_DOCKER_LOG="$TEST_ROOT/docker.log"
export FAKE_FLOCK_LOG="$TEST_ROOT/flock.log"
export FAKE_SYSTEMCTL_LOG="$TEST_ROOT/systemctl.log"
export FAKE_DOCKER_HEALTH=healthy
export FAKE_DOCKER_COMPOSE_EXIT=0
export FAKE_DOCKER_CONFIG_EXIT=0
export FAKE_DOCKER_VERSION_EXIT=0
export FAKE_DOCKER_INSPECT_EXIT=0
export FAKE_FLOCK_EXIT=0
export FAKE_DOCKER_VERSION_LABEL=v1.4.0
export FAKE_DOCKER_REVISION_LABEL=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
mkdir -p -- "$(dirname -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH")" "$REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY"
cp -- "$UPDATER_UNIT" "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH"
: >"$REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH"

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

assert_update_stages() {
  local expected="$1"
  local actual
  actual="$(
    printf '%s\n' "$last_output" |
      sed -n 's/^REACHCOMMANDER_UPDATE_STAGE=//p'
  )"
  assert_equal "$expected" "$actual" 'update progress stage order'
}

assert_ordered_update_events() {
  local remaining
  local expected
  remaining="$(
    printf '%s\n' "$last_output" |
      sed -n 's/^REACHCOMMANDER_UPDATE_EVENT=//p'
  )"
  for expected in "$@"; do
    if [[ "$remaining" != *"$expected"* ]]; then
      fail "update trace event is missing or out of order: $expected"
    fi
    remaining="${remaining#*"$expected"}"
  done
}

assert_update_stage_precedes_event() {
  local stage="$1"
  local event="$2"
  local stage_line
  local event_line
  stage_line="$(printf '%s\n' "$last_output" | grep -n -m1 -F "REACHCOMMANDER_UPDATE_STAGE=$stage" | cut -d: -f1)"
  event_line="$(printf '%s\n' "$last_output" | grep -n -m1 -F "REACHCOMMANDER_UPDATE_EVENT=$event" | cut -d: -f1)"
  [[ -n "$stage_line" ]] || fail "update stage is missing: $stage"
  [[ -n "$event_line" ]] || fail "update trace event is missing: $event"
  (( stage_line < event_line )) || fail "update stage $stage was not visible before event $event"
}

assert_update_events_are_sanitized() {
  local markers
  markers="$(printf '%s\n' "$last_output" | sed -n '/^REACHCOMMANDER_UPDATE_EVENT=/p')"
  [[ -n "$markers" ]] || fail 'update trace markers are missing'
  [[ "$markers" != *'/'* ]] || fail 'update trace marker exposed a path'
  [[ "$markers" != *'@sha256:'* ]] || fail 'update trace marker exposed an image digest'
}

run_command() {
  set +e
  last_output="$(bash "$COMMAND_SOURCE" "$@" 2>&1)"
  last_status=$?
  set -e
}

run_command_with_input() {
  local input="$1"
  shift
  set +e
  last_output="$(printf '%s\n' "$input" | bash "$COMMAND_SOURCE" "$@" 2>&1)"
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

for invocation in 'source' 'source add extra' 'source remove'; do
  read -r -a invocation_arguments <<<"$invocation"
  run_command "${invocation_arguments[@]}"
  assert_equal "64" "$last_status" "source usage status for $invocation"
done
pass "source dispatcher exposes only the fixed add action"

: >"$FAKE_DOCKER_LOG"
: >"$FAKE_FLOCK_LOG"
run_command_with_input '{"malformed":true}' source add
assert_equal "2" "$last_status" "malformed source request status"
[[ -s "$FAKE_FLOCK_LOG" ]] || fail "source add did not acquire the shared command lock"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "malformed source request invoked Docker"
[[ "$last_output" != *"$INSTALL_ROOT"* ]] || fail "source failure exposed the install root"
[[ "$last_output" != *'/srv/private'* ]] || fail "source failure exposed a requested path"
pass "source add accepts only bounded structured stdin under the shared lock"

source_request='{"protocolVersion":5,"requestId":"12345678-1234-4234-8234-123456789abc","action":"addSource","displayName":"Archive","hostPath":"/srv/private","access":"readOnly"}'
for required_source_file in \
  "$INSTALL_ROOT/bin/render_config.py" \
  "$INSTALL_ROOT/lib/updater_protocol.py" \
  "$INSTALL_ROOT/lib/compose.release.yaml"; do
  mv -- "$required_source_file" "$required_source_file.missing"
  run_command_with_input "$source_request" source add
  assert_equal "1" "$last_status" "missing source dependency status"
  mv -- "$required_source_file.missing" "$required_source_file"
done
pass "source add rejects missing trusted dependencies before Python imports or Docker access"

export FAKE_FLOCK_EXIT=1
: >"$FAKE_DOCKER_LOG"
run_command_with_input '{"protocolVersion":5,"requestId":"12345678-1234-4234-8234-123456789abc","action":"addSource","displayName":"Archive","hostPath":"/srv/private","access":"readOnly"}' source add
assert_equal "1" "$last_status" "contended source add status"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "contended source add invoked Docker"
export FAKE_FLOCK_EXIT=0
pass "concurrent source add is rejected before validation or Docker access"

if grep -Eq '(^|[^[:alnum:]_])eval([^[:alnum:]_]|$)' "$COMMAND_SOURCE" "$SOURCE_MANAGEMENT"; then
  fail "source command path must not use shell evaluation"
fi
pass "source command never evaluates request data as shell code"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    ;;
  *)
    NEW_SOURCE_PATH="$TEST_ROOT/new source"
    mkdir -p -- "$NEW_SOURCE_PATH"
    : >"$FAKE_DOCKER_LOG"
    run_command_with_input \
      '{"protocolVersion":5,"requestId":"12345678-1234-4234-8234-123456789abc","action":"addSource","displayName":"New Source","hostPath":"'"$NEW_SOURCE_PATH"'","access":"readOnly"}' \
      source add
    assert_equal "0" "$last_status" "successful source add status"
    [[ "$last_output" == *'"sourceId":"new-source"'* ]] || fail "source add result ID missing"
    [[ "$last_output" != *"$NEW_SOURCE_PATH"* ]] || fail "source add result exposed its host path"
    grep -q '"id": "new-source"' "$INSTALL_ROOT/config/sources.json" || fail "new source catalog entry missing"
    pass "source add publishes one validated source without exposing its host path"
    ;;
esac

run_command support-bundle extra
assert_equal "64" "$last_status" "support-bundle extra argument status"
support_archive="$TEST_ROOT/reachcommander-support.zip"
set +e
last_output="$(bash "$COMMAND_SOURCE" support-bundle 2>&1 >"$support_archive")"
last_status=$?
set -e
assert_equal "0" "$last_status" "support-bundle redirected status"
python3 -c '
import sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as archive:
    expected = ["README.txt", "deployment-health.json", "manifest.json", "summary.txt", "update-trace.json"]
    assert sorted(archive.namelist()) == expected
' "$support_archive" || fail "support-bundle ZIP contract"
[[ "$last_output" == *'sanitized support bundle created'* ]] || fail "support-bundle completion missing"
pass "support-bundle writes only the sanitized five-entry ZIP to stdout"

run_command update-log --path /etc/shadow
assert_equal "64" "$last_status" "update-log arbitrary path rejection status"

run_command update-log
assert_equal "0" "$last_status" "no-trace update-log status"
[[ "$last_output" == *'No ReachCommander update trace is available.'* ]] ||
  fail "no-trace update-log message missing"

PYTHONDONTWRITEBYTECODE=1 python3 - "$INSTALL_ROOT/state/update-traces" <<'PY'
import datetime as dt
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]).parents[1] / "lib"))
from updater_trace import ProtectedUpdateTraceStore

now = dt.datetime(2026, 8, 27, 10, 0, tzinfo=dt.timezone.utc)
operation_id = str(uuid.UUID("12345678-1234-4234-8234-123456789abc"))
store = ProtectedUpdateTraceStore(Path(sys.argv[1]), clock=lambda: now)
store.start(operation_id, now)
store.append(operation_id, "downloadStarted", "started", now, stage="downloading")
store.append(
    operation_id,
    "commandTimedOut",
    "timedOut",
    now,
    stage="downloading",
    timeout_seconds=300,
)
store.append(
    operation_id,
    "operationFailed",
    "failed",
    now,
    stage="downloading",
    exit_code=1,
)
PY

run_command update-log
assert_equal "0" "$last_status" "terminal update-log status"
[[ "$last_output" == *'Elapsed:'* ]] || fail "trace elapsed header missing"
[[ "$last_output" == *'downloadStarted'* ]] || fail "trace event missing"
[[ "$last_output" == *'timeout=300s'* ]] || fail "trace timeout detail missing"
[[ "$last_output" == *'exit=1'* ]] || fail "trace exit detail missing"

run_command update-log --follow
assert_equal "0" "$last_status" "terminal update-log follow status"

PYTHONDONTWRITEBYTECODE=1 python3 - "$INSTALL_ROOT/state/update-traces" <<'PY'
import datetime as dt
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]).parents[1] / "lib"))
from updater_trace import ProtectedUpdateTraceStore

now = dt.datetime(2026, 8, 27, 10, 1, tzinfo=dt.timezone.utc)
operation_id = str(uuid.UUID("87654321-1234-4234-8234-123456789abc"))
ProtectedUpdateTraceStore(Path(sys.argv[1]), clock=lambda: now).start(operation_id, now)
PY
(
  sleep 0.2
  PYTHONDONTWRITEBYTECODE=1 python3 - "$INSTALL_ROOT/state/update-traces" <<'PY'
import datetime as dt
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]).parents[1] / "lib"))
from updater_trace import ProtectedUpdateTraceStore

now = dt.datetime(2026, 8, 27, 10, 1, 1, tzinfo=dt.timezone.utc)
operation_id = str(uuid.UUID("87654321-1234-4234-8234-123456789abc"))
store = ProtectedUpdateTraceStore(Path(sys.argv[1]), clock=lambda: now)
store.append(operation_id, "operationCompleted", "succeeded", now, exit_code=0)
PY
) &
trace_writer_pid=$!
run_command update-log --follow
wait "$trace_writer_pid"
assert_equal "0" "$last_status" "active update-log follow status"
[[ "$last_output" == *'operationCompleted'* ]] ||
  fail "active update-log follow did not observe the terminal event"
pass "update-log prints and follows only the latest validated trace"

: >"$FAKE_DOCKER_LOG"
run_command status
assert_equal "0" "$last_status" "status command"
[[ "$last_output" == *'Channel: stable'* ]] || fail "status channel missing"
[[ "$last_output" == *'Version: v1.3.0'* ]] || fail "status version missing"
[[ "$last_output" == *'Image: ghcr.io/dragosniamtu/reach-commander@sha256:'* ]] || fail "status image missing"
mapfile -d '' status_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${status_args[@]}" | grep -q '^ps$' || fail "status did not call Compose ps"
pass "status reports display version, immutable image, channel, and Compose state"

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
export FAKE_SETPRIV_DENY_PREFIX="$INSTALL_ROOT/data"
export FAKE_DOCKER_EXEC_DENY_PREFIX="$INSTALL_ROOT/data"
: >"$FAKE_DOCKER_LOG"
run_command doctor
unset FAKE_SETPRIV_DENY_PREFIX
unset FAKE_DOCKER_EXEC_DENY_PREFIX
if (( last_status != 0 )); then
  printf '%s\n' "$last_output" >&2
fi
assert_equal "0" "$last_status" "healthy doctor status"
[[ "$last_output" == *'[PASS] Docker Engine is available'* ]] || fail "doctor Docker pass missing"
[[ "$last_output" == *'[PASS] Container is healthy'* ]] || fail "doctor health pass missing"
[[ "$last_output" == *'[PASS] No source operation journal or recovery transaction is present'* ]] ||
  fail "doctor clear source transaction status missing"
[[ "$last_output" != *'[FAIL]'* ]] || fail "healthy doctor reported a failure"
[[ ! -s "$FAKE_FLOCK_LOG" ]] || fail "doctor acquired a mutating lock"
assert_equal "$deployment_before" "$(find "$INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "doctor deployment mutation"
mapfile -d '' doctor_args <"$FAKE_DOCKER_LOG"
doctor_log="$(printf '%s\n' "${doctor_args[@]}")"
[[ "$doctor_log" == *$'exec\n--user\n'"$RUNTIME_UID:$RUNTIME_GID"* ]] ||
  fail "doctor did not probe application data as the configured container identity"
for container_path in \
  /data \
  /data/auth \
  /data/keys \
  /data/file-operations \
  /data/file-operations/plans \
  /data/file-operations/operations; do
  printf '%s\n' "${doctor_args[@]}" | grep -Fxq "$container_path" ||
    fail "doctor did not probe fixed container path: $container_path"
done
pass "doctor reports a healthy deployment without mutating it"

printf '%s\n' '{"schemaVersion":1,"transactionId":"12345678-1234-4234-8234-123456789abc","sourceId":"archive","displayName":"Archive","phase":"completed","reasonCode":"completed","updatedAt":"2026-08-31T00:00:00Z"}' \
  >"$INSTALL_ROOT/state/source-operation.json"
chmod 0600 -- "$INSTALL_ROOT/state/source-operation.json"
run_command doctor
assert_equal "0" "$last_status" "doctor terminal source journal status"
[[ "$last_output" == *'[WARN] Source operation journal is present; no recovery transaction is pending'* ]] ||
  fail "doctor terminal source journal warning missing"

mkdir -p -- "$INSTALL_ROOT/backups/.source-transaction"
chmod 0700 -- "$INSTALL_ROOT/backups/.source-transaction"
run_command doctor
assert_equal "1" "$last_status" "doctor incomplete source transaction status"
[[ "$last_output" == *'[FAIL] Source transaction recovery is required; retry the original source add'* ]] ||
  fail "doctor actionable source recovery failure missing"
rm -rf -- "$INSTALL_ROOT/backups/.source-transaction"
rm -f -- "$INSTALL_ROOT/state/source-operation.json"
pass "doctor reports source journal state and actionable transaction recovery"

printf 'do-not-print-/etc/shadow\n' >"$INSTALL_ROOT/state/update-traces/not-a-trace"
run_command update-log
assert_equal "1" "$last_status" "unsafe update-log status"
[[ "$last_output" == *"run 'sudo reachcommander doctor'"* ]] ||
  fail "unsafe update-log guidance missing"
[[ "$last_output" != *'do-not-print'* ]] || fail "unsafe update-log exposed contents"
run_command doctor
assert_equal "1" "$last_status" "doctor unsafe trace status"
[[ "$last_output" == *'[FAIL] Update trace storage contains an unsafe entry.'* ]] ||
  fail "doctor unsafe trace failure missing"
[[ "$last_output" != *'do-not-print'* ]] || fail "doctor exposed unsafe trace contents"
rm -f -- "$INSTALL_ROOT/state/update-traces/not-a-trace"
pass "update-log and doctor reject unsafe trace storage without exposing contents"

export FAKE_DOCKER_EXEC_EXIT=1
run_command doctor
unset FAKE_DOCKER_EXEC_EXIT
assert_equal "1" "$last_status" "doctor inaccessible container data status"
[[ "$last_output" == *'[FAIL] Application data directory is not accessible to the runtime identity'* ]] ||
  fail "doctor container data access failure missing"
[[ "$last_output" != *"$INSTALL_ROOT"* ]] ||
  fail "doctor exposed its host installation path"
pass "doctor validates runtime access at the application data mount boundary"

printf 'unexpected\n' >"$INSTALL_ROOT/data/file-operations/plans/not-an-operation.json"
run_command doctor
assert_equal "1" "$last_status" "doctor unexpected file-operation data status"
[[ "$last_output" == *'[FAIL] Application data tree contains an unsafe entry'* ]] ||
  fail "doctor unsafe file-operation tree failure missing"
rm -f -- "$INSTALL_ROOT/data/file-operations/plans/not-an-operation.json"
pass "doctor rejects file-operation state outside the exact allowlist"

mv -- "$INSTALL_ROOT/data/auth/account.json" "$TEST_ROOT/account.json"
run_command doctor
assert_equal "0" "$last_status" "doctor setup-mode status"
[[ "$last_output" == *'[WARN] Administrator account is not configured; first-run setup mode is active'* ]] ||
  fail "doctor setup-mode warning missing"
mv -- "$TEST_ROOT/account.json" "$INSTALL_ROOT/data/auth/account.json"
pass "doctor reports missing account state as first-run setup mode"

cp -- "$INSTALL_ROOT/data/auth/account.json" "$TEST_ROOT/account-valid.json"
printf '{ malformed authentication state\n' >"$INSTALL_ROOT/data/auth/account.json"
run_command doctor
assert_equal "1" "$last_status" "doctor malformed account status"
[[ "$last_output" == *'[FAIL] Administrator account state is invalid JSON'* ]] ||
  fail "doctor malformed account failure missing"
[[ "$last_output" != *'malformed authentication state'* ]] ||
  fail "doctor exposed malformed account contents"
cp -- "$TEST_ROOT/account-valid.json" "$INSTALL_ROOT/data/auth/account.json"
pass "doctor rejects malformed account state without exposing contents"

printf 'unexpected\n' >"$INSTALL_ROOT/data/unexpected.txt"
run_command doctor
assert_equal "1" "$last_status" "doctor unexpected authentication data status"
[[ "$last_output" == *'[FAIL] Application data tree contains an unsafe entry'* ]] ||
  fail "doctor unsafe application tree failure missing"
rm -f -- "$INSTALL_ROOT/data/unexpected.txt"
pass "doctor rejects unexpected application data entries"

printf '%s\n' "$INSTALL_ROOT/backups/.reconfigure-transaction" >"$INSTALL_ROOT/state/install-transaction"
run_command doctor
assert_equal "1" "$last_status" "doctor interrupted reconfiguration status"
[[ "$last_output" == *'[FAIL] Incomplete reconfiguration transaction requires installer recovery'* ]] || fail "doctor reconfiguration transaction failure missing"
rm -f -- "$INSTALL_ROOT/state/install-transaction"
pass "doctor detects an interrupted installer reconfiguration"

cp -- "$INSTALL_ROOT/.env" "$TEST_ROOT/env.backup"
sed \
  -e 's/^REACHCOMMANDER_ACCESS_MODE=.*/REACHCOMMANDER_ACCESS_MODE=trusted-lan-http/' \
  -e 's/^REACHCOMMANDER_BIND_ADDRESS=.*/REACHCOMMANDER_BIND_ADDRESS=0.0.0.0/' \
  -e 's/^REACHCOMMANDER_ALLOW_INSECURE_HTTP=.*/REACHCOMMANDER_ALLOW_INSECURE_HTTP=true/' \
  "$TEST_ROOT/env.backup" >"$INSTALL_ROOT/.env"
run_command doctor
assert_equal "0" "$last_status" "doctor trusted LAN status"
[[ "$last_output" == *'[WARN] Trusted LAN HTTP listens on every host interface'* ]] || fail "doctor trusted LAN warning missing"

sed \
  's/^REACHCOMMANDER_ALLOW_INSECURE_HTTP=.*/REACHCOMMANDER_ALLOW_INSECURE_HTTP=false/' \
  "$INSTALL_ROOT/.env" >"$TEST_ROOT/env.inconsistent"
cp -- "$TEST_ROOT/env.inconsistent" "$INSTALL_ROOT/.env"
run_command doctor
assert_equal "1" "$last_status" "doctor inconsistent access policy status"
[[ "$last_output" == *'[FAIL] Network access policy is inconsistent'* ]] || fail "doctor inconsistent access policy failure missing"
cp -- "$TEST_ROOT/env.backup" "$INSTALL_ROOT/.env"
pass "doctor validates secure and trusted LAN access policies"

cp -- "$INSTALL_ROOT/state/current-image" "$TEST_ROOT/current-image.backup"
printf 'ghcr.io/dragosniamtu/reach-commander@sha256:%s\n' "$(printf 'b%.0s' {1..64})" >"$INSTALL_ROOT/state/current-image"
run_command doctor
assert_equal "1" "$last_status" "doctor image mismatch status"
[[ "$last_output" == *'[FAIL] Environment image does not match current-image state'* ]] || fail "doctor image mismatch failure missing"
cp -- "$TEST_ROOT/current-image.backup" "$INSTALL_ROOT/state/current-image"
pass "doctor fails on inconsistent immutable image state"

mv -- "$INSTALL_ROOT/state/current-version" "$TEST_ROOT/current-version.backup"
run_command doctor
assert_equal "1" "$last_status" "doctor missing current version status"
[[ "$last_output" == *'[FAIL] Required deployment file is missing or symlinked: state/current-version'* ]] ||
  fail "doctor missing current version failure missing"
mv -- "$TEST_ROOT/current-version.backup" "$INSTALL_ROOT/state/current-version"

printf 'latest\n' >"$INSTALL_ROOT/state/current-version"
run_command doctor
assert_equal "1" "$last_status" "doctor invalid current version status"
[[ "$last_output" == *'[FAIL] Current display version state is invalid'* ]] ||
  fail "doctor invalid current version failure missing"
printf 'v1.3.0\n' >"$INSTALL_ROOT/state/current-version"

printf 'not-a-version\n' >"$INSTALL_ROOT/state/previous-version"
run_command doctor
assert_equal "1" "$last_status" "doctor invalid previous version status"
[[ "$last_output" == *'[FAIL] Previous display version state is invalid'* ]] ||
  fail "doctor invalid previous version failure missing"
: >"$INSTALL_ROOT/state/previous-version"
pass "doctor requires and validates display version state"

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

digest_a="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})"
digest_b="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'b%.0s' {1..64})"
digest_c="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'c%.0s' {1..64})"
digest_d="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'd%.0s' {1..64})"
digest_e="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'e%.0s' {1..64})"

reset_update_baseline() {
  local image="$digest_a"
  local channel='stable'
  local version='v1.3.0'
  python3 "$INSTALL_ROOT/bin/render_config.py" set-image --env "$INSTALL_ROOT/.env" --image "$image"
  printf '%s\n' "$channel" >"$INSTALL_ROOT/state/channel"
  printf '%s\n' "$image" >"$INSTALL_ROOT/state/current-image"
  : >"$INSTALL_ROOT/state/previous-image"
  printf '%s\n' "$version" >"$INSTALL_ROOT/state/current-version"
  : >"$INSTALL_ROOT/state/previous-version"
  rm -f -- "$INSTALL_ROOT/state/failed-image" "$INSTALL_ROOT/state/update-transaction"
  rm -rf -- "$INSTALL_ROOT/backups"
  mkdir -p "$INSTALL_ROOT/backups"
  unset \
    FAKE_DOCKER_HEALTH_FILE \
    FAKE_DOCKER_RUNNING_IMAGE_ID \
    FAKE_DOCKER_RUNNING_IMAGE_ID_FILE \
    REACHCOMMANDER_TEST_INTERRUPT_AFTER
  export FAKE_DOCKER_HEALTH=healthy
  export FAKE_DOCKER_PULL_EXIT=0
  export FAKE_FLOCK_EXIT=0
  export FAKE_DOCKER_VERSION_LABEL=v1.4.0
  export FAKE_DOCKER_REVISION_LABEL=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
}

reset_update_baseline
: >"$FAKE_DOCKER_LOG"
run_command update latest
assert_equal "1" "$last_status" "invalid update channel status"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "invalid channel invoked Docker"
[[ "$last_output" != *'REACHCOMMANDER_UPDATE_STAGE='* ]] || fail "invalid channel emitted update progress"
pass "update rejects malformed channels before Docker access"

export FAKE_DOCKER_DIGESTS="$digest_a"
: >"$FAKE_DOCKER_LOG"
run_command update stable
assert_equal "0" "$last_status" "no-op update status"
[[ "$last_output" == *'already running'* ]] || fail "no-op update message missing"
mapfile -d '' noop_args <"$FAKE_DOCKER_LOG"
if printf '%s\n' "${noop_args[@]}" | grep -q '^up$'; then
  fail "no-op update recreated the service"
fi
pass "update is a no-op when the resolved digest is current"

export FAKE_DOCKER_DIGESTS="$digest_b"
: >"$FAKE_DOCKER_LOG"
printf 'starting\nhealthy\n' >"$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/update-health-sequence"
run_command update edge
unset FAKE_DOCKER_HEALTH_FILE
assert_equal "0" "$last_status" "successful explicit update status"
assert_equal "edge" "$(cat -- "$INSTALL_ROOT/state/channel")" "successful update channel"
assert_equal "$digest_b" "$(cat -- "$INSTALL_ROOT/state/current-image")" "successful current image"
assert_equal "$digest_a" "$(cat -- "$INSTALL_ROOT/state/previous-image")" "successful previous image"
assert_equal "edge@aaaaaaaaaaaa" "$(cat -- "$INSTALL_ROOT/state/current-version")" "successful current version"
assert_equal "v1.3.0" "$(cat -- "$INSTALL_ROOT/state/previous-version")" "successful previous version"
grep -Fxq "REACHCOMMANDER_IMAGE=$digest_b" "$INSTALL_ROOT/.env" || fail "successful environment image missing"
[[ ! -e "$INSTALL_ROOT/state/update-transaction" ]] || fail "successful update marker leaked"
assert_update_stages $'downloading\ninstalling\nrestarting\nhealthChecking'
assert_ordered_update_events \
  'downloadStarted:started' \
  'downloadCompleted:succeeded' \
  'backupStarted:started' \
  'backupCompleted:succeeded' \
  'installStarted:started' \
  'installCompleted:succeeded' \
  'candidateRestartStarted:started' \
  'candidateRestartCompleted:succeeded' \
  'candidateImageVerified:succeeded' \
  'candidateHealthStarted:started' \
  'candidateHealthActivity:activity' \
  'candidateHealthSucceeded:succeeded'
assert_update_events_are_sanitized
mapfile -d '' successful_update_args <"$FAKE_DOCKER_LOG"
if printf '%s\n' "${successful_update_args[@]}" | grep -Fxq 'restart'; then
  fail 'update restarted Docker instead of recreating the application service'
fi
pass "successful update atomically advances image, version, and channel state"

export FAKE_DOCKER_DIGESTS="$digest_c"
export FAKE_DOCKER_REVISION_LABEL=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
run_command update
assert_equal "0" "$last_status" "saved-channel update status"
assert_equal "edge" "$(cat -- "$INSTALL_ROOT/state/channel")" "saved update channel"
assert_equal "$digest_c" "$(cat -- "$INSTALL_ROOT/state/current-image")" "saved-channel image"
assert_equal "$digest_b" "$(cat -- "$INSTALL_ROOT/state/previous-image")" "saved-channel previous image"
assert_equal "edge@bbbbbbbbbbbb" "$(cat -- "$INSTALL_ROOT/state/current-version")" "saved-channel version"
assert_equal "edge@aaaaaaaaaaaa" "$(cat -- "$INSTALL_ROOT/state/previous-version")" "saved-channel previous version"
pass "update without an argument discovers from the saved channel"

state_before="$(sha256sum "$INSTALL_ROOT/.env" "$INSTALL_ROOT/state/channel" "$INSTALL_ROOT/state/current-image" "$INSTALL_ROOT/state/previous-image" "$INSTALL_ROOT/state/current-version" "$INSTALL_ROOT/state/previous-version")"
export FAKE_DOCKER_PULL_EXIT=1
export FAKE_DOCKER_DIGESTS="$digest_d"
run_command update stable
(( last_status != 0 )) || fail "pull failure must fail update"
assert_equal "$state_before" "$(sha256sum "$INSTALL_ROOT/.env" "$INSTALL_ROOT/state/channel" "$INSTALL_ROOT/state/current-image" "$INSTALL_ROOT/state/previous-image" "$INSTALL_ROOT/state/current-version" "$INSTALL_ROOT/state/previous-version")" "pull failure state"
export FAKE_DOCKER_PULL_EXIT=0
pass "pull failure leaves all deployed state unchanged"

export FAKE_DOCKER_DIGESTS="$digest_d"
for invalid_version_label in '' 'invalid label' 'v1.4.0-beta.1'; do
  export FAKE_DOCKER_VERSION_LABEL="$invalid_version_label"
  run_command update stable
  assert_equal "1" "$last_status" "invalid image version label status"
  [[ "$last_output" == *'image version label is invalid'* ]] || fail "invalid version label message missing"
  assert_equal "$state_before" "$(sha256sum "$INSTALL_ROOT/.env" "$INSTALL_ROOT/state/channel" "$INSTALL_ROOT/state/current-image" "$INSTALL_ROOT/state/previous-image" "$INSTALL_ROOT/state/current-version" "$INSTALL_ROOT/state/previous-version")" "invalid version label state"
done
export FAKE_DOCKER_VERSION_LABEL=v1.4.0

export FAKE_DOCKER_VERSION_LABEL=v1.5.0
run_command update v1.4.0
assert_equal "1" "$last_status" "mismatched pinned image label status"
[[ "$last_output" == *'does not match the pinned channel'* ]] || fail "pinned label mismatch message missing"
assert_equal "$state_before" "$(sha256sum "$INSTALL_ROOT/.env" "$INSTALL_ROOT/state/channel" "$INSTALL_ROOT/state/current-image" "$INSTALL_ROOT/state/previous-image" "$INSTALL_ROOT/state/current-version" "$INSTALL_ROOT/state/previous-version")" "pinned label mismatch state"
export FAKE_DOCKER_VERSION_LABEL=v1.4.0

export FAKE_DOCKER_REVISION_LABEL='invalid revision'
run_command update edge
assert_equal "1" "$last_status" "invalid edge revision status"
[[ "$last_output" == *'edge image revision is invalid'* ]] || fail "invalid edge revision message missing"
assert_equal "$state_before" "$(sha256sum "$INSTALL_ROOT/.env" "$INSTALL_ROOT/state/channel" "$INSTALL_ROOT/state/current-image" "$INSTALL_ROOT/state/previous-image" "$INSTALL_ROOT/state/current-version" "$INSTALL_ROOT/state/previous-version")" "invalid edge revision state"
export FAKE_DOCKER_REVISION_LABEL=cccccccccccccccccccccccccccccccccccccccc
pass "invalid OCI display labels fail before deployment state changes"

printf '%s\n%s\n' \
  "sha256:$(printf 'f%.0s' {1..64})" \
  "sha256:$(printf 'c%.0s' {1..64})" \
  >"$TEST_ROOT/update-running-image-sequence"
export FAKE_DOCKER_RUNNING_IMAGE_ID_FILE="$TEST_ROOT/update-running-image-sequence"
export FAKE_DOCKER_DIGESTS="$digest_d"
run_command update stable
unset FAKE_DOCKER_RUNNING_IMAGE_ID_FILE
assert_equal "2" "$last_status" "candidate image mismatch rollback status"
assert_ordered_update_events \
  'candidateRestartCompleted:succeeded' \
  'candidateImageVerified:failed' \
  'rollbackStarted:started' \
  'rollbackStateRestored:succeeded' \
  'previousRestartCompleted:succeeded' \
  'previousImageVerified:succeeded' \
  'recoveryHealthSucceeded:succeeded'
assert_update_stage_precedes_event restoring 'rollbackStarted:started'
assert_update_stage_precedes_event verifyingRecovery 'previousRestartCompleted:succeeded'
pass "update rolls back when Compose recreates the wrong container image"

printf '1\n0\n' >"$TEST_ROOT/update-compose-sequence"
export FAKE_DOCKER_COMPOSE_SEQUENCE_FILE="$TEST_ROOT/update-compose-sequence"
export FAKE_DOCKER_DIGESTS="$digest_d"
run_command update stable
assert_equal "2" "$last_status" "Compose failure rollback status"
assert_equal "edge" "$(cat -- "$INSTALL_ROOT/state/channel")" "Compose rollback channel"
assert_equal "$digest_c" "$(cat -- "$INSTALL_ROOT/state/current-image")" "Compose rollback current image"
assert_equal "edge@bbbbbbbbbbbb" "$(cat -- "$INSTALL_ROOT/state/current-version")" "Compose rollback current version"
assert_equal "edge@aaaaaaaaaaaa" "$(cat -- "$INSTALL_ROOT/state/previous-version")" "Compose rollback previous version"
unset FAKE_DOCKER_COMPOSE_SEQUENCE_FILE
assert_update_stages $'downloading\ninstalling\nrestarting\nrestoring\nrestartingPrevious\nverifyingRecovery'
pass "Compose recreation failure restores image and version state"

printf 'unhealthy\nhealthy\n' >"$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_DIGESTS="$digest_d"
run_command update stable
assert_equal "2" "$last_status" "automatic rollback status"
[[ "$last_output" == *'previous deployment was restored'* ]] || fail "rollback success message missing"
assert_equal "edge" "$(cat -- "$INSTALL_ROOT/state/channel")" "rollback channel"
assert_equal "$digest_c" "$(cat -- "$INSTALL_ROOT/state/current-image")" "rollback current image"
assert_equal "edge@bbbbbbbbbbbb" "$(cat -- "$INSTALL_ROOT/state/current-version")" "rollback current version"
assert_equal "edge@aaaaaaaaaaaa" "$(cat -- "$INSTALL_ROOT/state/previous-version")" "rollback previous version"
grep -Fxq "REACHCOMMANDER_IMAGE=$digest_c" "$INSTALL_ROOT/.env" || fail "rollback environment image missing"
assert_equal "$digest_d" "$(cat -- "$INSTALL_ROOT/state/failed-image")" "failed digest record"
assert_update_stages $'downloading\ninstalling\nrestarting\nhealthChecking\nrestoring\nrestartingPrevious\nverifyingRecovery'
pass "unhealthy update restores the previous healthy deployment"

printf 'unhealthy\nunhealthy\n' >"$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_DIGESTS="$digest_e"
run_command update stable
assert_equal "3" "$last_status" "failed rollback status"
[[ "$last_output" == *'manual recovery'* ]] || fail "failed rollback recovery message missing"
assert_equal "$digest_c" "$(cat -- "$INSTALL_ROOT/state/current-image")" "failed rollback restored state"
assert_equal "edge@bbbbbbbbbbbb" "$(cat -- "$INSTALL_ROOT/state/current-version")" "failed rollback restored version"
assert_equal "$digest_e" "$(cat -- "$INSTALL_ROOT/state/failed-image")" "failed rollback digest record"
unset FAKE_DOCKER_HEALTH_FILE
export FAKE_DOCKER_HEALTH=healthy
pass "failed automatic rollback reports manual recovery without losing prior state"

reset_update_baseline
: >"$FAKE_DOCKER_LOG"
mkdir -p -- "$INSTALL_ROOT/backups/.source-transaction"
run_command update stable
assert_equal "1" "$last_status" "incomplete source transaction update status"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "incomplete source transaction update invoked Docker"
rm -rf -- "$INSTALL_ROOT/backups/.source-transaction"
pass "updates fail closed while a source transaction requires recovery"

export FAKE_DOCKER_DIGESTS="$digest_b"
export FAKE_FLOCK_EXIT=1
: >"$FAKE_DOCKER_LOG"
run_command update edge
(( last_status != 0 )) || fail "lock contention must fail update"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "contended update invoked Docker"
[[ "$last_output" != *'REACHCOMMANDER_UPDATE_STAGE='* ]] || fail "contended update emitted update progress"
export FAKE_FLOCK_EXIT=0
pass "concurrent update is rejected before registry access"

for interrupt_phase in previous-version environment current-image current-version channel; do
  reset_update_baseline
  export FAKE_DOCKER_DIGESTS="$digest_b"
  export REACHCOMMANDER_TEST_INTERRUPT_AFTER="$interrupt_phase"
  run_command update edge
  assert_equal "143" "$last_status" "interruption status after $interrupt_phase"
  [[ -f "$INSTALL_ROOT/state/update-transaction" ]] || fail "interruption marker missing after $interrupt_phase"
  unset REACHCOMMANDER_TEST_INTERRUPT_AFTER
  : >"$FAKE_DOCKER_LOG"
  run_command update edge
  assert_equal "1" "$last_status" "incomplete transaction refusal after $interrupt_phase"
  [[ "$last_output" == *'reachcommander doctor'* ]] || fail "doctor direction missing after $interrupt_phase"
  [[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "incomplete transaction accessed Docker after $interrupt_phase"
done
pass "interrupted state writes block later updates until diagnosis"

reset_update_baseline
export FAKE_DOCKER_DIGESTS="$digest_b"
export FAKE_DOCKER_VERSION_LABEL=v1.4.0-beta.1
run_command update v1.4.0-beta.1
assert_equal "0" "$last_status" "pinned prerelease update status"
assert_equal "v1.4.0-beta.1" "$(cat -- "$INSTALL_ROOT/state/channel")" "pinned prerelease channel"
assert_equal "v1.4.0-beta.1" "$(cat -- "$INSTALL_ROOT/state/current-version")" "pinned prerelease display version"
pass "pinned prerelease display versions are preserved"

reset_update_baseline
printf 'ancestor canary\n' >"$TEST_ROOT/ancestor-canary.txt"
deployment_before="$(find "$INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$COMMAND_PATH")"

printf 'unexpected application data\n' >"$INSTALL_ROOT/data/unexpected.txt"
: >"$FAKE_DOCKER_LOG"
run_command_with_input $'retain\nuninstall ReachCommander' uninstall
(( last_status != 0 )) || fail "unsafe application data must stop uninstall"
[[ "$last_output" == *'application data tree contains an unsafe entry'* ]] ||
  fail "unsafe application data failure missing"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "unsafe application data invoked Docker"
rm -f -- "$INSTALL_ROOT/data/unexpected.txt"
pass "uninstall rejects unexpected application data before stopping the service"

: >"$FAKE_DOCKER_LOG"
run_command_with_input $'\ncancel' uninstall
(( last_status != 0 )) || fail "uninstall cancellation must be nonzero"
assert_equal "$deployment_before" "$(find "$INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "cancelled uninstall deployment"
assert_equal "$command_before" "$(sha256sum "$COMMAND_PATH")" "cancelled uninstall command"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "cancelled uninstall invoked Docker"
pass "uninstall defaults to retaining application data and requires the exact destructive confirmation"

cp -- "$INSTALL_ROOT/state/source-mounts.json" "$TEST_ROOT/source-mounts.backup"
sed -E \
  's#"hostPath": "[^"]+"#"hostPath": "'"$TEST_ROOT"'"#' \
  "$TEST_ROOT/source-mounts.backup" \
  >"$INSTALL_ROOT/state/source-mounts.json"
: >"$FAKE_DOCKER_LOG"
run_command_with_input $'retain\nuninstall ReachCommander' uninstall
(( last_status != 0 )) || fail "overlapping uninstall paths must be rejected"
[[ ! -s "$FAKE_DOCKER_LOG" ]] || fail "overlap rejection invoked Docker"
cp -- "$TEST_ROOT/source-mounts.backup" "$INSTALL_ROOT/state/source-mounts.json"
assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "overlap uninstall source canary"
pass "uninstall rejects installer paths that overlap a configured source"

printf 'not a directory\n' >"$REACHCOMMANDER_TEST_BACKUP_ROOT"
run_command_with_input $'backup\nuninstall ReachCommander' uninstall
(( last_status != 0 )) || fail "backup creation failure must stop uninstall"
[[ -d "$INSTALL_ROOT" && -x "$COMMAND_PATH" ]] || fail "backup failure removed deployment"
rm -f -- "$REACHCOMMANDER_TEST_BACKUP_ROOT"
pass "backup failure preserves the active deployment and command"

export FAKE_DOCKER_COMPOSE_EXIT=1
run_command_with_input $'retain\nuninstall ReachCommander' uninstall
(( last_status != 0 )) || fail "Compose-down failure must stop uninstall"
[[ -d "$INSTALL_ROOT" && -x "$COMMAND_PATH" ]] || fail "Compose failure removed deployment"
assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "Compose failure source canary"
rm -rf -- "$REACHCOMMANDER_TEST_BACKUP_ROOT"
export FAKE_DOCKER_COMPOSE_EXIT=0
pass "Compose-down failure preserves deployment after external backup"

: >"$FAKE_DOCKER_LOG"
run_command_with_input $'backup\nuninstall ReachCommander' uninstall
if ((last_status != 0)); then
  printf '%s\n' "$last_output" >&2
fi
assert_equal "0" "$last_status" "successful uninstall status"
[[ ! -e "$INSTALL_ROOT" ]] || fail "successful uninstall retained install root"
[[ ! -e "$COMMAND_PATH" ]] || fail "successful uninstall retained command"
[[ -f "$REACHCOMMANDER_TEST_LOCK_PATH" ]] || fail "external lock was removed while held"
backup_count="$(find "$REACHCOMMANDER_TEST_BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
assert_equal "1" "$backup_count" "successful uninstall backup count"
backup_destination="$(find "$REACHCOMMANDER_TEST_BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -print -quit)"
[[ "$(basename -- "$backup_destination")" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || fail "backup timestamp is not UTC"
[[ -f "$backup_destination/deployment/config/sources.json" ]] || fail "backup source configuration missing"
[[ -f "$backup_destination/deployment/state/source-mounts.json" ]] || fail "backup source metadata missing"
[[ -f "$backup_destination/deployment/state/update-traces/12345678-1234-4234-8234-123456789abc.jsonl" ]] ||
  fail "backup update trace missing"
[[ -f "$backup_destination/reachcommander-command" ]] || fail "backup management command missing"
for application_file in \
  auth/account.json \
  auth/bootstrap.json \
  keys/key-command.xml \
  file-operations/plans/0123456789abcdef0123456789abcdef.json \
  file-operations/operations/fedcba9876543210fedcba9876543210.json; do
  [[ -f "$backup_destination/application-data/$application_file" ]] ||
    fail "backup application file missing: $application_file"
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
      ;;
    *)
      [[ "$(stat -c '%a' "$backup_destination/application-data/$application_file")" == '600' ]] ||
        fail "backup application file mode is not 0600: $application_file"
      ;;
  esac
done
cmp -s \
  "$TEST_ROOT/account-valid.json" \
  "$backup_destination/application-data/auth/account.json" ||
  fail "backup account bytes changed"
cmp -s \
  "$backup_destination/application-data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
  <(printf '{"schemaVersion":1,"kind":"copy"}\n') ||
  fail "backup file-operation plan bytes changed"
mapfile -d '' uninstall_args <"$FAKE_DOCKER_LOG"
printf '%s\n' "${uninstall_args[@]}" | grep -q '^down$' || fail "successful uninstall did not call Compose down"
if printf '%s\n' "${uninstall_args[@]}" | grep -q '^-v$'; then
  fail "uninstall requested Compose volume deletion"
fi
[[ ! -e "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" ]] || fail "uninstall retained updater unit"
systemctl_calls="$(tr '\0' '\n' <"$FAKE_SYSTEMCTL_LOG")"
[[ "$systemctl_calls" == *'disable'* && "$systemctl_calls" == *'--now'* ]] || fail "uninstall did not disable updater service"
assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "successful uninstall source canary"
assert_equal "ancestor canary" "$(cat -- "$TEST_ROOT/ancestor-canary.txt")" "successful uninstall ancestor canary"
pass "uninstall backs up generated state and preserves every source ancestor"

mkdir -p \
  "$INSTALL_ROOT/bin" \
  "$INSTALL_ROOT/lib" \
  "$INSTALL_ROOT/state" \
  "$INSTALL_ROOT/data/auth" \
  "$INSTALL_ROOT/data/keys" \
  "$INSTALL_ROOT/data/file-operations/plans" \
  "$INSTALL_ROOT/data/file-operations/operations"
cp -- "$REPOSITORY_ROOT/deploy/lib/common.sh" "$INSTALL_ROOT/lib/common.sh"
cp -- "$RENDERER" "$INSTALL_ROOT/bin/render_config.py"
cp -- "$SOURCE_MANAGEMENT" "$INSTALL_ROOT/bin/source_management.py"
cp -- "$TEMPLATE" "$INSTALL_ROOT/lib/compose.release.yaml"
cp -- "$UPDATE_TRACE_CLI" "$INSTALL_ROOT/bin/update_trace_cli.py"
cp -- "$SUPPORT_BUNDLE_CLI" "$INSTALL_ROOT/bin/support_bundle_cli.py"
cp -- "$UPDATER_SERVICE" "$INSTALL_ROOT/bin/updater_service.py"
cp -- "$SUPPORT_BUNDLE" "$INSTALL_ROOT/lib/support_bundle.py"
cp -- "$UPDATER_PROTOCOL" "$INSTALL_ROOT/lib/updater_protocol.py"
cp -- "$UPDATER_TRACE" "$INSTALL_ROOT/lib/updater_trace.py"
cp -- "$UPDATER_COMPOSE" "$INSTALL_ROOT/compose.override.yaml"
cp -- "$COMMAND_SOURCE" "$COMMAND_PATH"
chmod +x "$COMMAND_PATH"
python3 "$RENDERER" render --request "$REQUEST" --template "$TEMPLATE" --output "$INSTALL_ROOT"
mkdir -p -- "$INSTALL_ROOT/backups"
chmod 0700 -- "$INSTALL_ROOT" "$INSTALL_ROOT/bin" "$INSTALL_ROOT/lib" "$INSTALL_ROOT/state" "$INSTALL_ROOT/backups"
chmod 0600 -- "$INSTALL_ROOT/.env" "$INSTALL_ROOT/compose.yaml" "$INSTALL_ROOT/compose.override.yaml" "$INSTALL_ROOT/state/source-mounts.json" "$INSTALL_ROOT/lib/compose.release.yaml"
chmod 0755 -- "$INSTALL_ROOT/config" "$INSTALL_ROOT/bin/render_config.py" "$INSTALL_ROOT/bin/source_management.py"
chmod 0644 -- "$INSTALL_ROOT/config/sources.json"
printf 'stable\n' >"$INSTALL_ROOT/state/channel"
grep '^REACHCOMMANDER_IMAGE=' "$INSTALL_ROOT/.env" | cut -d= -f2- >"$INSTALL_ROOT/state/current-image"
: >"$INSTALL_ROOT/state/previous-image"
printf 'v1.3.0\n' >"$INSTALL_ROOT/state/current-version"
: >"$INSTALL_ROOT/state/previous-version"
mkdir -p -- "$(dirname -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH")" "$REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY"
cp -- "$UPDATER_UNIT" "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH"
: >"$REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH"
cp -- "$TEST_ROOT/account-valid.json" "$INSTALL_ROOT/data/auth/account.json"
printf '{"verifier":"fixture"}\n' >"$INSTALL_ROOT/data/auth/bootstrap.json"
printf '<key id="command-fixture" />\n' >"$INSTALL_ROOT/data/keys/key-command.xml"
printf '{"schemaVersion":1,"kind":"copy"}\n' \
  >"$INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json"
printf '{"schemaVersion":1,"phase":"completed"}\n' \
  >"$INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
chmod 0700 \
  "$INSTALL_ROOT/data" \
  "$INSTALL_ROOT/data/auth" \
  "$INSTALL_ROOT/data/keys" \
  "$INSTALL_ROOT/data/file-operations" \
  "$INSTALL_ROOT/data/file-operations/plans" \
  "$INSTALL_ROOT/data/file-operations/operations"
chmod 0600 \
  "$INSTALL_ROOT/data/auth/"*.json \
  "$INSTALL_ROOT/data/keys/"*.xml \
  "$INSTALL_ROOT/data/file-operations/plans/"*.json \
  "$INSTALL_ROOT/data/file-operations/operations/"*.json

: >"$FAKE_DOCKER_LOG"
run_command_with_input $'retain\nuninstall ReachCommander' uninstall
assert_equal "0" "$last_status" "retain uninstall status"
[[ -d "$INSTALL_ROOT/data/auth" && -d "$INSTALL_ROOT/data/keys" ]] ||
  fail "retain uninstall removed authentication data directories"
[[ -d "$INSTALL_ROOT/data/file-operations/plans" && -d "$INSTALL_ROOT/data/file-operations/operations" ]] ||
  fail "retain uninstall removed file-operation data directories"
[[ -f "$INSTALL_ROOT/data/auth/account.json" ]] || fail "retain uninstall removed account state"
[[ -f "$INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" ]] ||
  fail "retain uninstall removed file-operation state"
[[ ! -e "$COMMAND_PATH" ]] || fail "retain uninstall kept management command"
unexpected_retained="$(find "$INSTALL_ROOT" -mindepth 1 \
  ! -path "$INSTALL_ROOT/data" \
  ! -path "$INSTALL_ROOT/data/auth" \
  ! -path "$INSTALL_ROOT/data/auth/account.json" \
  ! -path "$INSTALL_ROOT/data/auth/bootstrap.json" \
  ! -path "$INSTALL_ROOT/data/keys" \
  ! -path "$INSTALL_ROOT/data/keys/key-command.xml" \
  ! -path "$INSTALL_ROOT/data/file-operations" \
  ! -path "$INSTALL_ROOT/data/file-operations/plans" \
  ! -path "$INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
  ! -path "$INSTALL_ROOT/data/file-operations/operations" \
  ! -path "$INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json" \
  -print -quit)"
[[ -z "$unexpected_retained" ]] || fail "retain uninstall left generated deployment state"
[[ "$last_output" == *"Application data retained at: $INSTALL_ROOT/data"* ]] ||
  fail "retain uninstall did not print the retained data location"
assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "retain uninstall source canary"
pass "uninstall retain leaves only inactive application data and preserves sources"

assert_equal "source canary" "$(cat -- "$SOURCE_PATH/canary.txt")" "command source canary"
printf '1..%d\n' "$tests_run"
