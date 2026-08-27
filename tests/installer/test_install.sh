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
mkdir -p "$BUNDLE/lib" "$BUNDLE/systemd"
cp -- "$INSTALLER_SOURCE" "$BUNDLE/install.sh"
cp -- "$REPOSITORY_ROOT/deploy/lan_address.py" "$BUNDLE/lan_address.py"
cp -- "$REPOSITORY_ROOT/deploy/render_config.py" "$BUNDLE/render_config.py"
cp -- "$REPOSITORY_ROOT/deploy/compose.release.yaml" "$BUNDLE/compose.release.yaml"
cp -- "$REPOSITORY_ROOT/deploy/lib/common.sh" "$BUNDLE/lib/common.sh"
for updater_source in \
  compose.updater.yaml \
  support_bundle.py \
  support_bundle_cli.py \
  update_trace_cli.py \
  updater_protocol.py \
  updater_service.py \
  updater_trace.py; do
  if [[ -f "$REPOSITORY_ROOT/deploy/$updater_source" ]]; then
    cp -- "$REPOSITORY_ROOT/deploy/$updater_source" "$BUNDLE/$updater_source"
  fi
done
if [[ -f "$REPOSITORY_ROOT/deploy/systemd/reachcommander-updater.service" ]]; then
  cp -- \
    "$REPOSITORY_ROOT/deploy/systemd/reachcommander-updater.service" \
    "$BUNDLE/systemd/reachcommander-updater.service"
fi
printf 'v1.4.0\n' >"$BUNDLE/VERSION"
printf '#!/usr/bin/env bash\nprintf "management placeholder\\n"\n' >"$BUNDLE/reachcommander"
chmod +x "$BUNDLE/install.sh" "$BUNDLE/reachcommander"
chmod +x "$FAKE_BIN/ip"

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
export REACHCOMMANDER_TEST_LOCK_PATH="$TEST_ROOT/.reachcommander.lock"
export REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH="$TEST_ROOT/systemd/reachcommander-updater.service"
export REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY="$TEST_ROOT/run/reachcommander-updater"
export REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH="$REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY/updater.sock"
export SUDO_UID=1000
export SUDO_GID=1000
export FAKE_DOCKER_LOG="$TEST_ROOT/docker.log"
export FAKE_FLOCK_LOG="$TEST_ROOT/flock.log"
export FAKE_SYNC_LOG="$TEST_ROOT/sync.log"
export FAKE_SYSTEMCTL_LOG="$TEST_ROOT/systemctl.log"
FAKE_DOCKER_DIGESTS="ghcr.io/dragosniamtu/reach-commander@sha256:$(printf 'a%.0s' {1..64})"
export FAKE_DOCKER_DIGESTS
export FAKE_DOCKER_HEALTH=healthy
export FAKE_DOCKER_COMPOSE_EXIT=0
export FAKE_DOCKER_CONFIG_EXIT=0
export FAKE_DOCKER_VERSION_EXIT=0
export FAKE_DOCKER_PULL_EXIT=0
export FAKE_DOCKER_INSPECT_EXIT=0
export FAKE_FLOCK_EXIT=0
export FAKE_SYNC_EXIT=0
export FAKE_SYSTEMCTL_START_EXIT=0
export FAKE_SYSTEMCTL_RESTART_EXIT=0
export FAKE_SYSTEMCTL_STOP_EXIT=0
export FAKE_DOCKER_VERSION_LABEL=v1.4.0
export FAKE_DOCKER_REVISION_LABEL=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export FAKE_IP_ADDRESS_JSON='[{"ifname":"ens18","flags":["UP","LOWER_UP"],"addr_info":[{"family":"inet","local":"192.168.44.12","prefixlen":24,"scope":"global"}]}]'
export FAKE_IP_ROUTE_JSON='[{"dst":"default","dev":"ens18","metric":100}]'

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
  if [[
    ! -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" &&
    ! -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" &&
    ! -e "$REACHCOMMANDER_TEST_COMMAND_PATH"
  ]]; then
    rm -f -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" "$REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH"
    rm -rf -- "$REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY"
  fi
  set +e
  printf '%s\n' "$input" | bash "$BUNDLE/install.sh" >"$output" 2>&1
  last_status=$?
  set -e
}

active_deployment_fingerprint() {
  find "$REACHCOMMANDER_TEST_INSTALL_ROOT" \
    -path "$REACHCOMMANDER_TEST_INSTALL_ROOT/backups" -prune -o \
    -type f -print0 |
    sort -z |
    xargs -0 sha256sum
}

source_prompt_input() {
  local first_name="${1:-Family Media}"
  local first_access="${2:-RO}"
  local second_name="${3:-Movies}"
  local second_access="${4:-RW}"
  local first_id="${5:-family-media}"
  local second_id="${6:-movies}"
  local https_acknowledgement="${7:-I have HTTPS}"
  local access_mode="${8:-1}"
  printf '%s\n' \
    "$access_mode" \
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
    "$https_acknowledgement"
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

printf 'v01.4.0\n' >"$BUNDLE/VERSION"
run_installer '' "$TEST_ROOT/invalid-bundle-version.out"
(( last_status != 0 )) || fail "invalid bundle VERSION must fail preflight"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "invalid bundle VERSION wrote deployment"
printf 'v1.4.0\n' >"$BUNDLE/VERSION"
pass "installer requires one stable semantic bundle VERSION"

collision_input="$(printf '%s\n' \
  '' '' '' '' \
  'Media' "$SOURCE_ONE" '' 'RO' 'y' \
  'Media' "$SOURCE_TWO" '' 'media-two' 'RW' 'n' \
  'media' 'media-two' 'I have HTTPS')"
run_installer "$collision_input" "$TEST_ROOT/collision.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/collision.out" >&2
fi
assert_equal "0" "$last_status" "source ID collision recovery status"
grep -q '"id": "media-two"' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json" || fail "replacement source ID missing"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "source ID collisions prompt for a distinct replacement"

overlap_input="$(printf '%s\n' \
  '' '' '' '' \
  'Installer Parent' "$TEST_ROOT" 'installer-parent' 'RO' 'n' \
  'installer-parent' 'installer-parent' 'I have HTTPS')"
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

legacy_acknowledgement_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I have authenticated ''HTTPS')"
run_installer "$legacy_acknowledgement_input" "$TEST_ROOT/legacy-acknowledgement.out"
(( last_status != 0 )) || fail "legacy HTTPS acknowledgement must be rejected"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "legacy acknowledgement installed deployment"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "installer requires the exact I have HTTPS acknowledgement"

invalid_mode_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I have HTTPS' '9')"
run_installer "$invalid_mode_input" "$TEST_ROOT/invalid-access-mode.out"
(( last_status != 0 )) || fail "invalid access mode must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "invalid mode installed deployment"
pass "installer rejects an invalid network access mode before writes"

wrong_lan_ack_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I trust my LAN' '2')"
run_installer "$wrong_lan_ack_input" "$TEST_ROOT/wrong-lan-ack.out"
(( last_status != 0 )) || fail "wrong LAN acknowledgement must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "wrong LAN acknowledgement installed deployment"
pass "installer requires the exact trusted LAN HTTP acknowledgement"

lan_install_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I understand LAN HTTP is unencrypted' '2')"
run_installer "$lan_install_input" "$TEST_ROOT/lan-install.out"
assert_equal "0" "$last_status" "trusted LAN installation status"
grep -q '^REACHCOMMANDER_ACCESS_MODE=trusted-lan-http$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN access mode missing"
grep -q '^REACHCOMMANDER_BIND_ADDRESS=0.0.0.0$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN wildcard bind missing"
grep -q '^REACHCOMMANDER_PORT=8092$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN host port missing"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=true$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN HTTP policy missing"
grep -q 'http://192.168.44.12:8092' "$TEST_ROOT/lan-install.out" || fail "detected LAN URL missing"
grep -q 'http://<server-lan-ip>:8092' "$TEST_ROOT/lan-install.out" || fail "generic LAN URL missing"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "trusted LAN mode publishes the Radarr-style host port and completion URLs"

install_input="$(source_prompt_input)"
run_installer "$install_input" "$TEST_ROOT/install.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/install.out" >&2
fi
assert_equal "0" "$last_status" "first installation status"
for required_file in \
  .env compose.yaml compose.override.yaml config/sources.json state/source-mounts.json \
  state/channel state/current-image state/previous-image \
  state/current-version state/previous-version \
  bin/render_config.py bin/support_bundle_cli.py bin/update_trace_cli.py bin/updater_service.py \
  lib/common.sh lib/support_bundle.py lib/updater_protocol.py lib/updater_trace.py; do
  [[ -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/$required_file" ]] || fail "missing installed $required_file"
done
[[ -f "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" ]] || fail "updater systemd unit missing"
[[ -f "$REACHCOMMANDER_TEST_LOCK_PATH" ]] || fail "fixed external lock missing"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/command.lock" ]] || fail "deployment contains a replaceable lock inode"
[[ -x "$REACHCOMMANDER_TEST_COMMAND_PATH" ]] || fail "management command was not installed"
for authentication_directory in data data/auth data/keys; do
  [[ -d "$REACHCOMMANDER_TEST_INSTALL_ROOT/$authentication_directory" ]] ||
    fail "missing authentication directory $authentication_directory"
  [[ ! -L "$REACHCOMMANDER_TEST_INSTALL_ROOT/$authentication_directory" ]] ||
    fail "authentication directory is symlinked: $authentication_directory"
done
grep -q '^REACHCOMMANDER_BIND_ADDRESS=127.0.0.1$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "loopback default missing"
grep -q '^REACHCOMMANDER_ACCESS_MODE=secure-https$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "secure access mode missing"
grep -q '^REACHCOMMANDER_PORT=8092$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "port default missing"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=false$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "secure HTTP policy missing"
grep -q '^REACHCOMMANDER_UID=1000$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "UID default missing"
grep -q '^REACHCOMMANDER_GID=1000$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "GID default missing"
grep -q '^REACHCOMMANDER_IMAGE=ghcr.io/dragosniamtu/reach-commander@sha256:a\{64\}$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "digest pin missing"
grep -q 'read_only: true' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "RO mount missing"
grep -q 'read_only: false' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "RW mount missing"
grep -q 'Authentication__AllowInsecureHttp' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "backend HTTP policy missing"
grep -A2 -F 'target: /data' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" |
  grep -q 'read_only: false' || fail "authentication data mount is not writable"
grep -q '/run/reachcommander-updater' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.override.yaml" || fail "updater socket mount missing"
! grep -q '/var/run/docker.sock' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.override.yaml" || fail "Docker socket mount must not be installed"
assert_equal "stable" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel")" "saved channel"
assert_equal "v1.4.0" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-version")" "saved version"
[[ ! -s "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/previous-version" ]] || fail "initial previous version is not empty"
systemctl_calls="$(tr '\0' '\n' <"$FAKE_SYSTEMCTL_LOG")"
[[ "$systemctl_calls" == *'daemon-reload'* ]] || fail "systemd daemon reload missing"
[[ "$systemctl_calls" == *'enable'* && "$systemctl_calls" == *'--now'* ]] || fail "updater service enable/start missing"
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "first source canary"
assert_equal "keep two" "$(cat -- "$SOURCE_TWO/canary.txt")" "second source canary"
pass "first install renders mixed sources and starts a digest-pinned service"

case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*)
    skip "runtime configuration modes permit non-root container reads" "Windows does not expose POSIX host modes"
    ;;
  *)
    assert_equal "755" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config")" "config directory mode"
    assert_equal "644" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" "source configuration mode"
    assert_equal "600" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env")" "environment mode"
    assert_equal "755" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/bin/updater_service.py")" "updater service mode"
    assert_equal "755" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/bin/update_trace_cli.py")" "update trace CLI mode"
    assert_equal "644" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/lib/updater_protocol.py")" "updater protocol mode"
    assert_equal "644" "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/lib/updater_trace.py")" "updater trace library mode"
    assert_equal "644" "$(stat -c '%a' "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH")" "updater unit mode"
    for authentication_directory in data data/auth data/keys; do
      assert_equal \
        "700" \
        "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/$authentication_directory")" \
        "$authentication_directory mode"
    done
    pass "runtime configuration modes permit non-root container reads"
    ;;
esac

printf '{"username":"installer-test","passwordHash":"fixture","securityStamp":"fixture"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/account.json"
printf '{"verifier":"fixture"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/bootstrap.json"
printf '<key id="installer-fixture" />\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-installer.xml"
mkdir -p \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations"
printf '{"schemaVersion":1,"kind":"copy"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json"
printf '{"schemaVersion":1,"phase":"completed"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
chmod 0700 \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations"
chmod 0600 \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/account.json" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/bootstrap.json" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-installer.xml" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
application_data_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)"

run_installer $'n\n' "$TEST_ROOT/decline-with-operation-data.out"
assert_equal "0" "$last_status" "legitimate file-operation data validation status"
assert_equal \
  "$application_data_before" \
  "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)" \
  "legitimate file-operation data preservation"
pass "installer accepts exact durable file-operation state without mutating it"

printf 'unexpected\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/not-an-operation.json"
run_installer $'n\n' "$TEST_ROOT/unexpected-operation-data.out"
(( last_status != 0 )) || fail "unexpected file-operation data entry must be rejected"
rm -f -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/not-an-operation.json"
pass "installer rejects file-operation state outside the exact allowlist"

printf 'unexpected\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/unexpected.txt"
run_installer $'n\n' "$TEST_ROOT/unexpected-auth-data.out"
(( last_status != 0 )) || fail "unexpected authentication data entry must be rejected"
rm -f -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/unexpected.txt"
pass "installer rejects unexpected authentication data entries before writes"

if ln -s -- "$SOURCE_ONE/canary.txt" "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-linked.xml" 2>/dev/null &&
  [[ -L "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-linked.xml" ]]; then
  run_installer $'n\n' "$TEST_ROOT/symlink-auth-data.out"
  (( last_status != 0 )) || fail "symlinked authentication data must be rejected"
  rm -f -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-linked.xml"
  pass "installer rejects symlinks below authentication data"
else
  rm -f -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-linked.xml"
  skip "installer rejects symlinks below authentication data" "filesystem does not expose POSIX symlinks"
fi

deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer $'n\n' "$TEST_ROOT/decline.out"
(( last_status == 0 )) || fail "declining reconfiguration should exit cleanly"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "declined deployment"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "declined command"
pass "existing deployment is never overwritten without confirmation"

reconfigure_success_input=$'y\n'"$(source_prompt_input 'Renamed Media' 'RO' 'Renamed Movies' 'RW' 'renamed-media' 'renamed-movies')"
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) ;;
  *)
    chmod 0644 \
      "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
      "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
    ;;
esac
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-success.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/reconfigure-success.out" >&2
fi
assert_equal "0" "$last_status" "successful reconfiguration status"
grep -q 'Renamed Media' "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json" || fail "reconfigured source name missing"
assert_equal \
  "$application_data_before" \
  "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)" \
  "reconfiguration application data"
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) ;;
  *)
    assert_equal \
      "600" \
      "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json")" \
      "reconfigured operation plan mode"
    assert_equal \
      "600" \
      "$(stat -c '%a' "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json")" \
      "reconfigured operation status mode"
    ;;
esac
assert_equal "keep one" "$(cat -- "$SOURCE_ONE/canary.txt")" "reconfiguration source canary"
pass "healthy reconfiguration replaces generated configuration and preserves application data"

deployment_before="$(active_deployment_fingerprint)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
unit_before="$(sha256sum "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH")"
: >"$FAKE_SYSTEMCTL_LOG"
export FAKE_SYSTEMCTL_START_EXIT=1
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-updater-start-failure.out"
(( last_status != 0 )) || fail "updater service start failure must fail reconfiguration"
assert_equal "$deployment_before" "$(active_deployment_fingerprint)" "updater-start-failure deployment rollback"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "updater-start-failure command rollback"
assert_equal "$unit_before" "$(sha256sum "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH")" "updater-start-failure unit rollback"
systemctl_calls="$(tr '\0' '\n' <"$FAKE_SYSTEMCTL_LOG")"
[[ "$systemctl_calls" == *'restart'* ]] || fail "previous updater service was not restarted after rollback"
export FAKE_SYSTEMCTL_START_EXIT=0
pass "failed updater service startup restores the previous deployment and unit"

for legacy_file in \
  compose.override.yaml \
  state/current-version \
  state/previous-version \
  bin/update_trace_cli.py \
  bin/updater_service.py \
  lib/updater_protocol.py \
  lib/updater_trace.py; do
  rm -f -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/$legacy_file"
done
rm -f -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" "$REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH"
: >"$FAKE_SYSTEMCTL_LOG"
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-legacy-migration.out"
if (( last_status != 0 )); then
  cat -- "$TEST_ROOT/reconfigure-legacy-migration.out" >&2
fi
assert_equal "0" "$last_status" "legacy updater migration status"
for migrated_file in \
  compose.override.yaml \
  state/current-version \
  state/previous-version \
  bin/update_trace_cli.py \
  bin/updater_service.py \
  lib/updater_protocol.py \
  lib/updater_trace.py; do
  [[ -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/$migrated_file" ]] || fail "legacy migration missing $migrated_file"
done
[[ -f "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" ]] || fail "legacy migration missing systemd unit"
assert_equal "v1.4.0" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-version")" "migrated current version"
pass "legacy installation is migrated to updater support in one transaction"

printf 'edge\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel"
export FAKE_DOCKER_REVISION_LABEL=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-edge-preservation.out"
assert_equal "0" "$last_status" "edge channel preservation status"
assert_equal "edge" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel")" "preserved edge channel"
assert_equal "edge@bbbbbbbbbbbb" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-version")" "edge display version"

printf 'v1.3.0-beta.1\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel"
export FAKE_DOCKER_VERSION_LABEL=v1.3.0-beta.1
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-pin-preservation.out"
assert_equal "0" "$last_status" "exact pin preservation status"
assert_equal "v1.3.0-beta.1" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel")" "preserved exact pin"
assert_equal "v1.3.0-beta.1" "$(cat -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-version")" "pinned display version"
printf 'stable\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/channel"
printf 'v1.4.0\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-version"
export FAKE_DOCKER_VERSION_LABEL=v1.4.0
export FAKE_DOCKER_REVISION_LABEL=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
pass "reconfiguration preserves edge and exact-version channels"

printf '{"schemaVersion":1,"phase":"current"}\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/system-update.json"
journal_before="$(sha256sum "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/system-update.json")"
PYTHONDONTWRITEBYTECODE=1 python3 - \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/update-traces" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/lib" <<'PY'
import datetime as dt
import sys
import uuid
from pathlib import Path

sys.path.insert(0, sys.argv[2])
from updater_trace import ProtectedUpdateTraceStore

now = dt.datetime(2026, 8, 27, 10, 0, tzinfo=dt.timezone.utc)
operation_id = str(uuid.UUID("aaaaaaaa-1234-4234-8234-123456789abc"))
store = ProtectedUpdateTraceStore(Path(sys.argv[1]), clock=lambda: now)
store.start(operation_id, now)
store.append(operation_id, "operationCompleted", "succeeded", now, exit_code=0)
PY
trace_before="$(sha256sum "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/update-traces/"*.jsonl)"
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-journal-preservation.out"
assert_equal "0" "$last_status" "journal preservation reconfiguration status"
assert_equal "$journal_before" "$(sha256sum "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/system-update.json")" "updater journal preservation"
assert_equal \
  "$trace_before" \
  "$(sha256sum "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/update-traces/"*.jsonl)" \
  "updater trace preservation"
pass "reconfiguration preserves the host updater journal and traces"

deployment_before="$(active_deployment_fingerprint)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
export FAKE_SYNC_FAIL_MATCH="$REACHCOMMANDER_TEST_INSTALL_ROOT/backups/.reconfigure-transaction/deployment/.env"
export FAKE_SYNC_FAIL_ONCE_FILE="$TEST_ROOT/backup-sync-failed-once"
run_installer "$reconfigure_success_input" "$TEST_ROOT/reconfigure-backup-failure.out"
(( last_status != 0 )) || fail "reconfiguration backup failure must stop installation"
assert_equal "$deployment_before" "$(active_deployment_fingerprint)" "backup-failure active deployment"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "backup-failure command"
[[ -d "$REACHCOMMANDER_TEST_INSTALL_ROOT/backups/.reconfigure-transaction" ]] || fail "partial backup fixture was not retained"
unset FAKE_SYNC_FAIL_MATCH FAKE_SYNC_FAIL_ONCE_FILE
run_installer $'n\n' "$TEST_ROOT/reconfigure-backup-retry.out"
assert_equal "0" "$last_status" "next installer run should clear an orphaned partial backup"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/backups/.reconfigure-transaction" ]] || fail "orphaned partial backup remains"
assert_equal "$deployment_before" "$(active_deployment_fingerprint)" "orphan-cleanup active deployment"
pass "partial reconfiguration backup is cleaned safely on the next run"

deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
export FAKE_SYNC_FAIL_MATCH="$REACHCOMMANDER_TEST_INSTALL_ROOT/state/.current-image"
export FAKE_SYNC_FAIL_ONCE_FILE="$TEST_ROOT/sync-failed-once"
reconfigure_write_failure_input=$'y\n'"$(source_prompt_input 'Write Failure Media' 'RO' 'Write Failure Movies' 'RW' 'write-failure-media' 'write-failure-movies')"
run_installer "$reconfigure_write_failure_input" "$TEST_ROOT/reconfigure-write-failure.out"
(( last_status != 0 )) || fail "reconfiguration write failure must be reported"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)" "write-failure deployment rollback"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "write-failure command rollback"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install-transaction" ]] || fail "completed rollback retained install marker"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/backups/.reconfigure-transaction" ]] || fail "completed rollback retained install backup"
unset FAKE_SYNC_FAIL_MATCH FAKE_SYNC_FAIL_ONCE_FILE
pass "reconfiguration write failure restores the complete prior deployment"

deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
export REACHCOMMANDER_TEST_INSTALL_INTERRUPT_AFTER=current-image
run_installer "$reconfigure_write_failure_input" "$TEST_ROOT/reconfigure-interrupt.out"
assert_equal "143" "$last_status" "reconfiguration interruption status"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)" "interrupted deployment rollback"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "interrupted command rollback"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install-transaction" ]] || fail "interrupted rollback retained install marker"
unset REACHCOMMANDER_TEST_INSTALL_INTERRUPT_AFTER
pass "reconfiguration interruption restores the complete prior deployment"

deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)"
recovery_backup="$REACHCOMMANDER_TEST_INSTALL_ROOT/backups/.reconfigure-transaction"
mkdir -p \
  "$recovery_backup/deployment/config" \
  "$recovery_backup/deployment/state" \
  "$recovery_backup/deployment/bin" \
  "$recovery_backup/deployment/lib" \
  "$recovery_backup/command" \
  "$recovery_backup/systemd"
for recovery_file in \
  .env compose.yaml compose.override.yaml config/sources.json state/source-mounts.json \
  state/channel state/current-image state/previous-image \
  state/current-version state/previous-version \
  bin/render_config.py bin/support_bundle_cli.py bin/update_trace_cli.py bin/updater_service.py \
  lib/common.sh lib/support_bundle.py lib/updater_protocol.py lib/updater_trace.py; do
  cp -- \
    "$REACHCOMMANDER_TEST_INSTALL_ROOT/$recovery_file" \
    "$recovery_backup/deployment/$recovery_file"
done
cp -- "$REACHCOMMANDER_TEST_COMMAND_PATH" "$recovery_backup/command/reachcommander"
cp -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" "$recovery_backup/systemd/reachcommander-updater.service"
printf '%s\n' "$recovery_backup" >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install-transaction"
printf 'corrupt interrupted state\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/.env"
run_installer $'n\n' "$TEST_ROOT/recover-incomplete-reconfiguration.out"
assert_equal "0" "$last_status" "incomplete reconfiguration recovery status"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f -print0 | sort -z | xargs -0 sha256sum)" "recovered deployment"
[[ ! -e "$recovery_backup" ]] || fail "recovery backup remains after successful recovery"
grep -q 'Recovered an interrupted ReachCommander reconfiguration' "$TEST_ROOT/recover-incomplete-reconfiguration.out" || fail "recovery message missing"
pass "next installer run recovers a crash-persistent reconfiguration journal"

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

export FAKE_DOCKER_VERSION_LABEL=v1.5.0
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
rm -f -- "$REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH" "$REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
run_installer "$install_input" "$TEST_ROOT/version-mismatch.out"
(( last_status != 0 )) || fail "bundle/image version mismatch must fail install"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "version mismatch installed deployment"
export FAKE_DOCKER_VERSION_LABEL=v1.4.0
pass "bundle version must match the trusted image version label"

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
authentication_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
printf 'unhealthy\nhealthy\n' >"$TEST_ROOT/health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/health-sequence"
reconfigure_input=$'y\n'"$(source_prompt_input \
  'Changed Media' 'RO' 'Changed Movies' 'RW' 'changed-media' 'changed-movies' \
  'I understand LAN HTTP is unencrypted' '2')"
run_installer "$reconfigure_input" "$TEST_ROOT/reconfigure-failure.out"
(( last_status != 0 )) || fail "unhealthy LAN reconfiguration must report failure"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "rolled-back deployment"
assert_equal "$authentication_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)" "rolled-back authentication data"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "rolled-back command"
grep -q '^REACHCOMMANDER_ACCESS_MODE=secure-https$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "rollback did not restore secure mode"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=false$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "rollback did not restore secure cookie policy"
unset FAKE_DOCKER_HEALTH_FILE
pass "unhealthy LAN reconfiguration restores the secure deployment and durable state"

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
