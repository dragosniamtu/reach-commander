#!/bin/bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPOSITORY_ROOT="$(cd -P -- "$TEST_DIRECTORY/../../.." && pwd -P)"
INSTALLER="$REPOSITORY_ROOT/deploy/macos/install.sh"
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/reachcommander-macos-helpers.XXXXXX")"

cleanup() {
  case "$TEST_ROOT" in
    "${TMPDIR:-/tmp}"/reachcommander-macos-helpers.*)
      chmod -R u+rwX -- "$TEST_ROOT" 2>/dev/null || true
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
