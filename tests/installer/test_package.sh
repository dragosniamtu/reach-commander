#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
PACKAGER="$REPOSITORY_ROOT/deploy/package-installer.sh"

if [[ ! -f "$PACKAGER" ]]; then
  printf 'not ok - deploy/package-installer.sh must exist\n' >&2
  exit 1
fi

TEST_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEST_ROOT"' EXIT
FIRST_OUTPUT="$TEST_ROOT/first"
SECOND_OUTPUT="$TEST_ROOT/second"

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

for invalid_version in 1.2.3 v1.2 v01.2.3 v1.2.3-beta.1 'v1.2.3;id'; do
  if bash "$PACKAGER" "$invalid_version" "$TEST_ROOT/invalid" >/dev/null 2>&1; then
    fail "invalid package version '$invalid_version' was accepted"
  fi
done
pass "packager accepts only stable vX.Y.Z versions"

grep -q -- '--sort=name' "$PACKAGER" || fail "packager must request name-sorted tar entries"
grep -q -- "--mtime='UTC 1970-01-01'" "$PACKAGER" || fail "packager must normalize tar timestamps"
grep -q -- '--owner=0' "$PACKAGER" || fail "packager must normalize tar ownership"
grep -q -- '--numeric-owner' "$PACKAGER" || fail "packager must use numeric ownership"
grep -Fq "trap 'exit 143' TERM" "$PACKAGER" || fail "packager must exit immediately on termination"
pass "packager declares deterministic tar metadata"

bash "$PACKAGER" v1.2.3 "$FIRST_OUTPUT"
bash "$PACKAGER" v1.2.3 "$SECOND_OUTPUT"
for output in "$FIRST_OUTPUT" "$SECOND_OUTPUT"; do
  [[ -f "$output/reachcommander-installer.tar.gz" ]] || fail "archive missing"
  [[ -f "$output/SHA256SUMS" ]] || fail "checksum file missing"
  (cd -- "$output" && sha256sum --check SHA256SUMS >/dev/null)
done
assert_equal \
  "$(sha256sum "$FIRST_OUTPUT/reachcommander-installer.tar.gz" | cut -d' ' -f1)" \
  "$(sha256sum "$SECOND_OUTPUT/reachcommander-installer.tar.gz" | cut -d' ' -f1)" \
  "deterministic archive digest"
pass "repeated package builds are byte-for-byte deterministic"

mapfile -t archive_entries < <(
  tar -tzf "$FIRST_OUTPUT/reachcommander-installer.tar.gz" |
    sed '/\/$/d' |
    sort
)
expected_entries=(
  'reachcommander-installer/LICENSE'
  'reachcommander-installer/VERSION'
  'reachcommander-installer/compose.release.yaml'
  'reachcommander-installer/compose.updater.yaml'
  'reachcommander-installer/install.sh'
  'reachcommander-installer/lan_address.py'
  'reachcommander-installer/lib/common.sh'
  'reachcommander-installer/reachcommander'
  'reachcommander-installer/render_config.py'
  'reachcommander-installer/source_management.py'
  'reachcommander-installer/support_bundle.py'
  'reachcommander-installer/support_bundle_cli.py'
  'reachcommander-installer/systemd/reachcommander-updater.service'
  'reachcommander-installer/update_trace_cli.py'
  'reachcommander-installer/updater_protocol.py'
  'reachcommander-installer/updater_service.py'
  'reachcommander-installer/updater_trace.py'
)
assert_equal \
  "$(printf '%s\n' "${expected_entries[@]}")" \
  "$(printf '%s\n' "${archive_entries[@]}")" \
  "archive allowlist"
for archive_entry in "${archive_entries[@]}"; do
  [[ "$archive_entry" != /* && "$archive_entry" != *'/../'* && "$archive_entry" != '../'* ]] || fail "unsafe archive path"
done
pass "archive contains only the installer allowlist and safe relative paths"

if tar -tzf "$FIRST_OUTPUT/reachcommander-installer.tar.gz" |
  grep -Eq '(^|/)(data|account\.json|bootstrap\.json|auth\.lock|key-[^/]+\.xml)(/|$)'; then
  fail "archive contains generated authentication state"
fi
if tar -xOzf "$FIRST_OUTPUT/reachcommander-installer.tar.gz" 2>/dev/null |
  grep -Fq 'ReachCommander-E2E-Password-2026!'; then
  fail "archive contains fixture credentials"
fi
pass "package excludes credentials and generated authentication state"

EXTRACTED="$TEST_ROOT/extracted"
mkdir -p "$EXTRACTED"
tar -xzf "$FIRST_OUTPUT/reachcommander-installer.tar.gz" -C "$EXTRACTED"
PACKAGE_ROOT="$EXTRACTED/reachcommander-installer"
assert_equal "v1.2.3" "$(cat -- "$PACKAGE_ROOT/VERSION")" "packaged version"
grep -Eq 'rc_require_commands .*setsid' "$PACKAGE_ROOT/install.sh" ||
  fail "packaged installer does not require process-session support"

grep -Fq '/run/reachcommander-updater' "$PACKAGE_ROOT/compose.updater.yaml" ||
  fail "packaged source/update helper socket mount is missing"
if grep -Fq '/var/run/docker.sock' \
  "$PACKAGE_ROOT/compose.release.yaml" \
  "$PACKAGE_ROOT/compose.updater.yaml"; then
  fail "packaged Compose templates must not mount the Docker socket"
fi
pass "package exposes only the restricted helper socket to the application"

archive_permissions() {
  local archive_path="$1"
  tar -tvzf "$FIRST_OUTPUT/reachcommander-installer.tar.gz" |
    awk -v expected="$archive_path" '$NF == expected { print $1 }'
}

assert_equal "-rwxr-xr-x" "$(archive_permissions 'reachcommander-installer/install.sh')" "installer mode"
assert_equal "-rwxr-xr-x" "$(archive_permissions 'reachcommander-installer/reachcommander')" "command mode"
assert_equal "-rwxr-xr-x" "$(archive_permissions 'reachcommander-installer/update_trace_cli.py')" "update trace CLI mode"
assert_equal "-rwxr-xr-x" "$(archive_permissions 'reachcommander-installer/updater_service.py')" "updater service mode"
for data_file in LICENSE VERSION compose.release.yaml compose.updater.yaml lan_address.py render_config.py source_management.py updater_protocol.py updater_trace.py lib/common.sh systemd/reachcommander-updater.service; do
  assert_equal "-rw-r--r--" "$(archive_permissions "reachcommander-installer/$data_file")" "$data_file mode"
done
pass "package normalizes executable and data file modes"

checksum_lines="$(wc -l <"$FIRST_OUTPUT/SHA256SUMS" | tr -d ' ')"
assert_equal "1" "$checksum_lines" "checksum line count"
grep -Eq '^[0-9a-f]{64}  reachcommander-installer\.tar\.gz$' "$FIRST_OUTPUT/SHA256SUMS" || fail "checksum format"
pass "checksum file names only the release archive"

printf '1..%d\n' "$tests_run"
