#!/bin/bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPOSITORY_ROOT="$(cd -P -- "$TEST_DIRECTORY/../../.." && pwd -P)"
INSTALLER="$REPOSITORY_ROOT/deploy/macos/install.sh"
FAKE_BIN="$TEST_DIRECTORY/fake-bin"
TEST_PARENT="$(cd -P -- "${REACHCOMMANDER_TEST_TMPDIR:-${HOME:?}}" && pwd -P)"
TEST_ROOT="$(mktemp -d "$TEST_PARENT/reachcommander-macos-test.XXXXXX")"

cleanup() {
  case "$TEST_ROOT" in
    "$TEST_PARENT"/reachcommander-macos-test.*)
      chmod -R u+rwX "$TEST_ROOT" 2>/dev/null || true
      rm -rf -- "$TEST_ROOT"
      ;;
    *)
      printf 'Refusing to clean unexpected test path: %s\n' "$TEST_ROOT" >&2
      return 1
      ;;
  esac
}
trap cleanup EXIT

tests_run=0
last_status=0
fail() { printf 'not ok - %s\n' "$1" >&2; exit 1; }
pass() { tests_run=$((tests_run + 1)); printf 'ok %d - %s\n' "$tests_run" "$1"; }
skip() { tests_run=$((tests_run + 1)); printf 'ok %d - %s # SKIP %s\n' "$tests_run" "$1" "$2"; }
assert_equal() { [[ "$1" == "$2" ]] || fail "$3 (expected '$1', got '$2')"; }
assert_status() {
  local expected="$1"
  local output="$2"
  local message="$3"
  if [[ "$last_status" != "$expected" ]]; then
    sed -n '1,160p' "$output" >&2
    fail "$message (expected '$expected', got '$last_status')"
  fi
}
assert_contains() {
  if ! grep -Fq -- "$2" "$1"; then
    sed -n '1,200p' "$1" >&2
    fail "$3"
  fi
}
assert_not_contains() { ! grep -Fq -- "$2" "$1" || fail "$3"; }

run_installer() {
  local input="$1"
  local output="$2"
  set +e
  printf '%s\n' "$input" | /bin/bash "$INSTALLER" >"$output" 2>&1
  last_status=$?
  set -e
}

set_install_root() {
  export REACHCOMMANDER_TEST_INSTALL_ROOT="$TEST_ROOT/installs/$1"
  export FAKE_DOCKER_LOG="$TEST_ROOT/logs/$1.docker"
  mkdir -p -- "$(dirname -- "$REACHCOMMANDER_TEST_INSTALL_ROOT")" "$(dirname -- "$FAKE_DOCKER_LOG")"
  : >"$FAKE_DOCKER_LOG"
}

digest() {
  local character="$1"
  local index=0
  printf 'ghcr.io/dragosniamtu/reach-commander@sha256:'
  while (( index < 64 )); do
    printf '%s' "$character"
    index=$((index + 1))
  done
}

hash_files() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$@"
  else
    sha256sum "$@"
  fi
}

source_hashes() {
  hash_files "$SOURCE_ONE/canary.txt" "$SOURCE_TWO/canary.txt"
}

specific_input() {
  local network_choice="${1:-1}"
  local port="${2:-8080}"
  printf '2\n%s\nFamily Media\n1\n%s\nCafé Bob'"'"'s\n2\n\n%s\n%s' \
    "$SOURCE_ONE" "$SOURCE_TWO" "$network_choice" "$port"
}

single_source_input() {
  local network_choice="${1:-1}"
  local first_port="${2:-8080}"
  local second_port="${3:-}"
  printf '2\n%s\nFamily Media\n1\n\n%s\n%s' "$SOURCE_ONE" "$network_choice" "$first_port"
  [[ -z "$second_port" ]] || printf '\n%s' "$second_port"
}

assert_symlink_layout_rejected() {
  local slug="$1"
  local relative="$2"
  local link_path
  set_install_root "symlink-$slug"
  mkdir -p -- "$TEST_ROOT/symlink-target-$slug"
  case "$relative" in
    '.')
      link_path="$REACHCOMMANDER_TEST_INSTALL_ROOT"
      ;;
    data)
      mkdir -p -- "$REACHCOMMANDER_TEST_INSTALL_ROOT"
      link_path="$REACHCOMMANDER_TEST_INSTALL_ROOT/data"
      ;;
    data/auth | data/keys)
      mkdir -p -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data"
      link_path="$REACHCOMMANDER_TEST_INSTALL_ROOT/$relative"
      ;;
    *) fail "invalid symlink test fixture" ;;
  esac
  if ln -s "$TEST_ROOT/symlink-target-$slug" "$link_path" 2>/dev/null && [[ -L "$link_path" ]]; then
    run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/symlink-$slug.out"
    [[ "$last_status" -ne 0 ]] || fail "symlinked $slug path must fail"
    [[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "symlinked $slug path allowed writes"
    pass "symlinked installer $slug path is rejected"
  else
    skip "symlinked installer $slug path is rejected" "symlink creation is unavailable"
  fi
}

export PATH="$FAKE_BIN:$PATH"
export REACHCOMMANDER_TESTING=1
export REACHCOMMANDER_TEST_LOGICAL_CPUS=8
export REACHCOMMANDER_TEST_USER_HOME="$TEST_ROOT/user home"
export REACHCOMMANDER_TEST_TEMPLATE_URL='https://test.invalid/compose.release.yaml'
export REACHCOMMANDER_TEST_ARCHITECTURE=x86_64
export REACHCOMMANDER_TEST_LOCAL_IP=192.168.50.25
export FAKE_CURL_SOURCE="$REPOSITORY_ROOT/deploy/compose.release.yaml"

for cpu_case in '1:0.75' '2:1.5' '3:2.0' '4:3.0' '12:3.0'; do
  cpu_count="${cpu_case%%:*}"
  expected_limit="${cpu_case##*:}"
  # The positional parameters intentionally expand inside the child Bash process.
  # shellcheck disable=SC2016
  actual_limit="$(
    REACHCOMMANDER_SOURCE_ONLY=1 bash -c \
      'source "$1"; rc_default_cpu_limit "$2"' \
      reachcommander-test "$INSTALLER" "$cpu_count"
  )"
  assert_equal "$expected_limit" "$actual_limit" "CPU limit for $cpu_count logical CPUs"
done
export FAKE_DOCKER_INFO_EXIT=0
export FAKE_DOCKER_COMPOSE_VERSION_EXIT=0
export FAKE_DOCKER_PULL_EXIT=0
export FAKE_DOCKER_INSPECT_EXIT=0
export FAKE_DOCKER_CONFIG_EXIT=0
export FAKE_DOCKER_SOURCE_PREFLIGHT_EXIT=0
export FAKE_DOCKER_UP_EXIT=0
export FAKE_DOCKER_DOWN_EXIT=0
export FAKE_DOCKER_HEALTH=healthy
export FAKE_LSOF_EXIT=1
export FAKE_LSOF_OCCUPIED_PORTS=''
unset FAKE_DOCKER_HEALTH_SEQUENCE_FILE

SOURCE_ONE="$TEST_ROOT/sources/Family Media"
SOURCE_TWO="$TEST_ROOT/sources/Café Bob's"
EXTERNAL_ONE="$TEST_ROOT/Volumes/Media One"
EXTERNAL_TWO="$TEST_ROOT/Volumes/Archive Two"
VOLUMES_FILE="$TEST_ROOT/volumes.txt"
mkdir -p -- "$REACHCOMMANDER_TEST_USER_HOME" "$SOURCE_ONE" "$SOURCE_TWO" "$EXTERNAL_ONE" "$EXTERNAL_TWO"
printf 'family canary\n' >"$SOURCE_ONE/canary.txt"
printf 'archive canary\n' >"$SOURCE_TWO/canary.txt"
printf '%s\n%s\n' "$EXTERNAL_ONE" "$EXTERNAL_TWO" >"$VOLUMES_FILE"
export REACHCOMMANDER_TEST_VOLUMES_FILE="$VOLUMES_FILE"
INITIAL_SOURCE_HASHES="$(source_hashes)"
DIGEST_A="$(digest a)"
DIGEST_B="$(digest b)"
DIGEST_C="$(digest c)"
export FAKE_DOCKER_DIGESTS="$DIGEST_A"

set_install_root docker-stopped
export FAKE_DOCKER_INFO_EXIT=1
run_installer '' "$TEST_ROOT/docker-stopped.out"
[[ "$last_status" -ne 0 ]] || fail "stopped Docker must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "stopped Docker wrote deployment state"
pass "stopped Docker fails before deployment writes"
export FAKE_DOCKER_INFO_EXIT=0

set_install_root specific
run_installer "$(specific_input 1 8080)" "$TEST_ROOT/specific.out"
assert_status 0 "$TEST_ROOT/specific.out" "specific-folder installation"
assert_equal 'Family Media' \
  "$(plutil -extract sources.0.name raw -o - "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" \
  "first rendered source name"
assert_equal "Café Bob's" \
  "$(plutil -extract sources.1.name raw -o - "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" \
  "second rendered source name"
assert_equal true \
  "$(plutil -extract sources.0.readOnly raw -o - "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" \
  "read-only source policy"
assert_equal false \
  "$(plutil -extract sources.1.readOnly raw -o - "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" \
  "read/write source policy"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" 'REACHCOMMANDER_BIND_ADDRESS=127.0.0.1' "Mac-only bind is missing"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" 'REACHCOMMANDER_PORT=8080' "default port is missing"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" 'REACHCOMMANDER_CPU_LIMIT=3.0' "CPU safety limit is missing"
# shellcheck disable=SC2016 # Compose interpolation is intentionally asserted literally.
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" 'cpus: "${REACHCOMMANDER_CPU_LIMIT}"' "Compose CPU safety limit is missing"
assert_contains "$TEST_ROOT/specific.out" 'http://127.0.0.1:8080' "completion endpoint is missing"
assert_contains "$TEST_ROOT/specific.out" 'logs --tail 200 reachcommander' "setup-code logs command is missing"
assert_contains "$TEST_ROOT/specific.out" "$REACHCOMMANDER_TEST_INSTALL_ROOT" "state path is missing"
assert_contains "$TEST_ROOT/specific.out" 'Family Media (RO)' "RO completion policy is missing"
assert_contains "$TEST_ROOT/specific.out" "Café Bob's (RW)" "RW completion policy is missing"
assert_not_contains "$TEST_ROOT/specific.out" 'open http' "installer attempted to open a browser"
pass "specific sources install with matching policy and completion guidance"

ACCOUNT_FILE="$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/account.json"
KEY_FILE="$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys/key-test.xml"
printf 'account-canary\n' >"$ACCOUNT_FILE"
printf 'key-canary\n' >"$KEY_FILE"
GENERATED_BEFORE_EXIT="$(source_hashes; hash_files "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml")"
run_installer '3' "$TEST_ROOT/exit.out"
assert_equal 0 "$last_status" "existing-deployment exit"
GENERATED_AFTER_EXIT="$(source_hashes; hash_files "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml")"
assert_equal "$GENERATED_BEFORE_EXIT" "$GENERATED_AFTER_EXIT" "declined reconfiguration"
pass "existing deployment can be left byte-identical"

export FAKE_DOCKER_DIGESTS="$DIGEST_A"
run_installer '1' "$TEST_ROOT/update-noop.out"
assert_equal 0 "$last_status" "no-op update"
assert_contains "$TEST_ROOT/update-noop.out" 'already up to date' "no-op update message is missing"
pass "identical digest update is a no-op"

export FAKE_DOCKER_DIGESTS="$DIGEST_B"
run_installer '1' "$TEST_ROOT/update-success.out"
assert_equal 0 "$last_status" "healthy update"
assert_equal "$DIGEST_B" "$(sed -n '1p' "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-image")" "updated digest"
assert_equal "$DIGEST_A" "$(sed -n '1p' "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/previous-image")" "previous digest"
assert_equal 'account-canary' "$(sed -n '1p' "$ACCOUNT_FILE")" "account persistence"
assert_equal 'key-canary' "$(sed -n '1p' "$KEY_FILE")" "key persistence"
pass "healthy update advances digest without touching authentication"

printf 'unhealthy\nhealthy\n' >"$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_HEALTH_SEQUENCE_FILE="$TEST_ROOT/update-health-sequence"
export FAKE_DOCKER_DIGESTS="$DIGEST_C"
run_installer '1' "$TEST_ROOT/update-rollback.out"
assert_equal 2 "$last_status" "unhealthy update result"
assert_equal "$DIGEST_B" "$(sed -n '1p' "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/current-image")" "rolled-back digest"
assert_equal 'account-canary' "$(sed -n '1p' "$ACCOUNT_FILE")" "rollback account persistence"
unset FAKE_DOCKER_HEALTH_SEQUENCE_FILE
pass "unhealthy update restores the prior deployment"

export FAKE_DOCKER_DIGESTS="$DIGEST_B"
RECONFIGURE_INPUT="$(printf '2\n2\n%s\nRenamed Media\n1\n\n1\n8080' "$SOURCE_ONE")"
export FAKE_LSOF_OCCUPIED_PORTS=8080
run_installer "$RECONFIGURE_INPUT" "$TEST_ROOT/reconfigure.out"
assert_status 0 "$TEST_ROOT/reconfigure.out" "healthy reconfiguration on the current occupied port"
export FAKE_LSOF_OCCUPIED_PORTS=''
assert_equal 'Renamed Media' \
  "$(plutil -extract sources.0.name raw -o - "$REACHCOMMANDER_TEST_INSTALL_ROOT/config/sources.json")" \
  "reconfigured source name"
assert_equal 'account-canary' "$(sed -n '1p' "$ACCOUNT_FILE")" "reconfiguration account persistence"
assert_equal 'key-canary' "$(sed -n '1p' "$KEY_FILE")" "reconfiguration key persistence"
pass "reconfiguration preserves authentication and keys"

set_install_root lan
export FAKE_LSOF_OCCUPIED_PORTS=8080
run_installer "$(single_source_input 2 8080 8081)" "$TEST_ROOT/lan.out"
assert_equal 0 "$last_status" "LAN installation"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" 'REACHCOMMANDER_BIND_ADDRESS=0.0.0.0' "LAN bind is missing"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" 'REACHCOMMANDER_PORT=8081' "replacement port is missing"
assert_contains "$TEST_ROOT/lan.out" 'http://192.168.50.25:8081' "LAN completion URL is missing"
export FAKE_LSOF_OCCUPIED_PORTS=''
pass "LAN mode reports the discovered address and resolves port conflicts"

export REACHCOMMANDER_TEST_INSTALL_ROOT="$REACHCOMMANDER_TEST_USER_HOME/Library/Application Support/ReachCommander"
export FAKE_DOCKER_LOG="$TEST_ROOT/logs/whole-home.docker"
: >"$FAKE_DOCKER_LOG"
WHOLE_HOME_INPUT="$(printf '1\n1\n1\n1\n8080')"
run_installer "$WHOLE_HOME_INPUT" "$TEST_ROOT/whole-home.out"
assert_equal 0 "$last_status" "whole-home installation"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" "source: './excluded'" "installer mask source is missing"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" "/sources/home/Library/Application Support/ReachCommander" "installer mask target is missing"
pass "whole-home access masks installer-owned state"

set_install_root whole-external
WHOLE_EXTERNAL_INPUT="$(printf '1\n2 3\n1\n1\n1\n8080')"
run_installer "$WHOLE_EXTERNAL_INPUT" "$TEST_ROOT/whole-external.out"
assert_equal 0 "$last_status" "external-volume installation"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" "source: '$EXTERNAL_ONE'" "first external volume is missing"
assert_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" "source: '$EXTERNAL_TWO'" "second external volume is missing"
assert_not_contains "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" "source: '/Volumes'" "the /Volumes parent was mounted"
pass "external volumes are mounted independently"

set_install_root broad-rw-denied
BROAD_RW_INPUT="$(printf '1\n1\n2\nwrong path')"
run_installer "$BROAD_RW_INPUT" "$TEST_ROOT/broad-rw-denied.out"
[[ "$last_status" -ne 0 ]] || fail "wrong broad-RW confirmation must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "wrong broad-RW confirmation installed files"
pass "broad read/write access requires exact-path confirmation"

set_install_root preflight-denied
export FAKE_DOCKER_SOURCE_PREFLIGHT_EXIT=1
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/preflight-denied.out"
[[ "$last_status" -ne 0 ]] || fail "source preflight denial must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "source preflight denial activated deployment"
export FAKE_DOCKER_SOURCE_PREFLIGHT_EXIT=0
pass "Docker source preflight failure leaves no active deployment"

set_install_root unhealthy-first
export FAKE_DOCKER_HEALTH=unhealthy
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/unhealthy-first.out"
assert_equal 2 "$last_status" "unhealthy first startup"
[[ -f "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "validated unhealthy configuration was not retained"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/transaction-active" ]] || fail "unhealthy first install retained transaction marker"
export FAKE_DOCKER_HEALTH=healthy
pass "unhealthy first startup retains validated configuration only"

set_install_root unexpected-auth
mkdir -p -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth" "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys"
printf 'unexpected\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth/not-allowed.txt"
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/unexpected-auth.out"
[[ "$last_status" -ne 0 ]] || fail "unexpected authentication entry must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "unexpected authentication entry allowed writes"
pass "unexpected authentication data fails closed"

set_install_root operation-data
mkdir -p -- \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations"
printf '{"schemaVersion":1,"kind":"copy"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json"
printf '{"schemaVersion":1,"phase":"completed"}\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
chmod 0644 \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations/fedcba9876543210fedcba9876543210.json"
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/operation-data.out"
assert_equal 0 "$last_status" "legitimate file-operation data"
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) ;;
  *)
    assert_equal \
      600 \
      "$(stat -f '%Lp' "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/0123456789abcdef0123456789abcdef.json")" \
      "operation plan mode"
    ;;
esac
pass "exact durable file-operation state is preserved and protected"

set_install_root unexpected-operation
mkdir -p -- \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/auth" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/keys" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans" \
  "$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/operations"
printf 'unexpected\n' \
  >"$REACHCOMMANDER_TEST_INSTALL_ROOT/data/file-operations/plans/not-an-operation.json"
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/unexpected-operation.out"
[[ "$last_status" -ne 0 ]] || fail "unexpected file-operation entry must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "unexpected file-operation entry allowed writes"
pass "file-operation data outside the exact allowlist fails closed"

assert_symlink_layout_rejected root '.'
assert_symlink_layout_rejected data data
assert_symlink_layout_rejected auth data/auth
assert_symlink_layout_rejected keys data/keys

set_install_root active-lock
mkdir -p -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install.lock"
printf '%s\n' "$$" >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install.lock/pid"
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/active-lock.out"
[[ "$last_status" -ne 0 ]] || fail "active lock must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "active lock allowed writes"
pass "active installer lock blocks concurrent writes"

set_install_root stale-lock
mkdir -p -- "$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install.lock"
printf '99999999\n' >"$REACHCOMMANDER_TEST_INSTALL_ROOT/state/install.lock/pid"
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/stale-lock.out"
assert_equal 0 "$last_status" "stale lock recovery"
pass "stale safe lock is recovered"

set_install_root unsupported-architecture
export REACHCOMMANDER_TEST_ARCHITECTURE=i386
run_installer "$(single_source_input 1 8080)" "$TEST_ROOT/unsupported-architecture.out"
[[ "$last_status" -ne 0 ]] || fail "unsupported architecture must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "unsupported architecture wrote deployment files"
export REACHCOMMANDER_TEST_ARCHITECTURE=x86_64
pass "unsupported architecture fails before writes"

assert_equal "$INITIAL_SOURCE_HASHES" "$(source_hashes)" "source canary hashes"
pass "all installer paths leave source canaries unchanged"

printf '1..%d\n' "$tests_run"
