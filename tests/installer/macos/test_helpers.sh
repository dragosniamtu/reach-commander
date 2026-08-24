#!/bin/bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPOSITORY_ROOT="$(cd -P -- "$TEST_DIRECTORY/../../.." && pwd -P)"
INSTALLER="$REPOSITORY_ROOT/deploy/macos/install.sh"
TEST_PARENT="$(cd -P -- "${REACHCOMMANDER_TEST_TMPDIR:-${HOME:?}}" && pwd -P)"
TEST_ROOT="$(mktemp -d "$TEST_PARENT/reachcommander-macos-helpers.XXXXXX")"

cleanup() {
  case "$TEST_ROOT" in
    "$TEST_PARENT"/reachcommander-macos-helpers.*)
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
skip() { tests_run=$((tests_run + 1)); printf 'ok %d - %s # SKIP %s\n' "$tests_run" "$1" "$2"; }
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
expected_canonical="$(cd -P -- "$RC_USER_HOME/Pictures" && pwd -P)"
assert_equal "$expected_canonical" "$canonical" "canonical directory"
canonical_install_root="$(rc_canonical_directory "$RC_INSTALL_ROOT")"
assert_equal "ancestor" "$(rc_path_relation "$RC_USER_HOME" "$RC_INSTALL_ROOT")" "home relation"
assert_equal "inside" "$(rc_path_relation "$RC_INSTALL_ROOT/data" "$RC_INSTALL_ROOT")" "inside relation"
assert_equal "same" "$(rc_path_relation "$RC_INSTALL_ROOT" "$RC_INSTALL_ROOT")" "same relation"
assert_equal "disjoint" "$(rc_path_relation "$RC_USER_HOME/Pictures" "$RC_INSTALL_ROOT")" "disjoint relation"
rc_validate_source_path "$RC_USER_HOME"
assert_fails "installer root source must fail" rc_validate_source_path "$RC_INSTALL_ROOT"
assert_fails "physically resolved installer root source must fail" \
  rc_validate_source_path "$canonical_install_root"
assert_fails "installer child source must fail" rc_validate_source_path "$RC_INSTALL_ROOT/data"
for path in / /System /Library /private /usr /bin /sbin /dev; do
  assert_fails "protected path '$path' must fail" rc_validate_source_path "$path"
done
pass "path boundary permits a maskable ancestor only"

assert_equal "'Media'" "$(rc_yaml_quote 'Media')" "plain YAML scalar"
assert_equal "'Bob''s Media'" "$(rc_yaml_quote "Bob's Media")" "quoted YAML scalar"
pass "YAML scalars are single-quoted safely"

SOURCE_ONE="$TEST_ROOT/Family Media"
SOURCE_TWO="$TEST_ROOT/Café Bob's"
mkdir -p -- "$SOURCE_ONE" "$SOURCE_TWO"
RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()
rc_add_source family-media 'Family Media' "$SOURCE_ONE" ro
rc_add_source cafe-bob "Café Bob's" "$SOURCE_TWO" rw

COMPOSE_ONLY="$TEST_ROOT/compose-only.yaml"
MOUNTS_ONLY="$TEST_ROOT/compose-mounts.yaml"
rc_render_compose \
  "$REPOSITORY_ROOT/deploy/compose.release.yaml" \
  "$COMPOSE_ONLY" \
  "$MOUNTS_ONLY"
grep -A3 -F "target: '/sources/family-media'" "$COMPOSE_ONLY" |
  grep -Fq 'read_only: true' || fail "RO Compose policy missing"
grep -A3 -F "target: '/sources/cafe-bob'" "$COMPOSE_ONLY" |
  grep -Fq 'read_only: false' || fail "RW Compose policy missing"
! grep -Fq '# installer-source-mounts' "$COMPOSE_ONLY" ||
  fail "Compose source marker was not replaced"
pass "Compose binds preserve source access policy"

mkdir -p -- "$SOURCE_ONE/Nested"
assert_fails "duplicate source IDs must fail" \
  rc_add_source family-media Duplicate "$TEST_ROOT" ro
assert_fails "nested sources must fail" \
  rc_add_source nested Nested "$SOURCE_ONE/Nested" ro
pass "duplicate and nested sources are rejected"

if ! command -v plutil >/dev/null 2>&1; then
  skip "typed JSON preserves source policy and pane defaults" "macOS plutil is unavailable"
  skip "broad source masks installer-owned state" "macOS plutil is unavailable"
  printf '1..%d\n' "$tests_run"
  exit 0
fi

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
pass "typed JSON preserves source policy and pane defaults"

# Sourced installer functions consume these arrays.
# shellcheck disable=SC2034
RC_SOURCE_IDS=()
# shellcheck disable=SC2034
RC_SOURCE_NAMES=()
# shellcheck disable=SC2034
RC_SOURCE_PATHS=()
# shellcheck disable=SC2034
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

printf '1..%d\n' "$tests_run"
