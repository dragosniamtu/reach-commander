#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
# shellcheck source=fixtures/text-encoding-data.sh
source "$TEST_DIRECTORY/fixtures/text-encoding-data.sh"
TEST_PARENT="$(cd -- "${TMPDIR:-/tmp}" && pwd)"
TEST_ROOT="$(mktemp -d "$TEST_PARENT/reachcommander-text-encoding-data.XXXXXX")"
[[ "$TEST_ROOT" == "$TEST_PARENT"/reachcommander-text-encoding-data.* ]] || exit 1
trap 'rm -rf -- "$TEST_ROOT"' EXIT
RC_INSTALL_ROOT="$TEST_ROOT/install"
# shellcheck disable=SC2034 # Consumed by the loaded production functions.
RUNTIME_UID="$(id -u)"
# shellcheck disable=SC2034
RUNTIME_GID="$(id -g)"
RECORD_ID=0123456789abcdef0123456789abcdef
MOUNTED_PATH=''
tests_run=0

fail() { printf 'not ok - %s\n' "$1" >&2; exit 1; }
pass() { tests_run=$((tests_run + 1)); printf 'ok %d - %s\n' "$tests_run" "$1"; }
skip() { tests_run=$((tests_run + 1)); printf 'ok %d - %s # SKIP %s\n' "$tests_run" "$1" "$2"; }
rc_die() { printf 'ReachCommander: %s\n' "$1" >&2; }

load_function() {
  local source="$1" name="$2" definition
  definition="$(sed -n "/^${name}() {$/,/^}$/p" "$source")"
  [[ -n "$definition" ]] || fail "missing production function $name"
  eval "$definition"
}
load_function "$REPOSITORY_ROOT/deploy/install.sh" validate_application_data_tree
load_function "$REPOSITORY_ROOT/deploy/install.sh" prepare_application_data
load_function "$REPOSITORY_ROOT/deploy/reachcommander" application_data_tree_is_safe
load_function "$REPOSITORY_ROOT/deploy/reachcommander" validate_uninstall_tree
load_function "$REPOSITORY_ROOT/deploy/lib/common.sh" rc_subtitles
load_function "$REPOSITORY_ROOT/deploy/reachcommander" backup_application_file
load_function "$REPOSITORY_ROOT/deploy/reachcommander" backup_application_data
load_function "$REPOSITORY_ROOT/deploy/reachcommander" remove_application_data
load_function "$REPOSITORY_ROOT/deploy/macos/install.sh" rc_validate_application_data_tree
load_function "$REPOSITORY_ROOT/deploy/macos/install.sh" rc_prepare_application_data

authentication_path_is_mount_point() { [[ -n "$MOUNTED_PATH" && "$1" == "$MOUNTED_PATH" ]]; }
rc_device_id() { if [[ -n "$MOUNTED_PATH" && "$1" == "$MOUNTED_PATH" ]]; then printf '2'; else printf '1'; fi; }
run_update_trace_cli() { return 0; }
# shellcheck disable=SC2034 # Consumed by the loaded validate_uninstall_tree.
RC_COMMAND_PATH="$TEST_ROOT/command"
RC_BACKUP_ROOT="$TEST_ROOT/backups"
RC_UPDATER_UNIT_PATH="$TEST_ROOT/updater.service"
printf 'fixture\n' >"$RC_UPDATER_UNIT_PATH"

accept_all() {
  local validator
  for validator in validate_application_data_tree application_data_tree_is_safe rc_validate_application_data_tree validate_uninstall_tree; do
    "$validator" || fail "$validator rejected $1"
  done
}
reject_all() {
  local validator
  for validator in validate_application_data_tree application_data_tree_is_safe rc_validate_application_data_tree validate_uninstall_tree; do
    if "$validator" >"$TEST_ROOT/rejection.log" 2>&1; then fail "$validator accepted $1"; fi
  done
}

mkdir -p "$RC_INSTALL_ROOT/data/auth" "$RC_INSTALL_ROOT/data/keys"
accept_all 'a legacy tree without text-encoding state'
mkdir -p "$RC_INSTALL_ROOT/data/text-encodings/staging"
chmod 0755 "$RC_INSTALL_ROOT/data/text-encodings"
chmod 0700 "$RC_INSTALL_ROOT/data/text-encodings/staging"
accept_all 'the empty registry created by the application'
pass 'all validators accept the application-created text-encoding directory layout'

create_text_encoding_fixture "$RC_INSTALL_ROOT/data"
accept_all 'exact registry manifests, quarantine files, and atomic temporary files'
pass 'all validators accept every filename produced by the text-encoding staging registry'

for relative in \
  staging/ABCDEF0123456789ABCDEF0123456789AB.json \
  staging/0123456789abcdef0123456789abcdef0.json \
  "staging/$RECORD_ID.json.invalid-unknown" \
  "staging/$RECORD_ID.json.invalid.invalid" \
  "staging/$RECORD_ID.json.tmp" \
  "staging/.$RECORD_ID.tmp" \
  "staging/.$RECORD_ID-$RECORD_ID.tmp.extra" \
  "staging/.$RECORD_ID-ABCDEF0123456789ABCDEF0123456789AB.tmp" \
  "$RECORD_ID.json" \
  staging/unexpected.json; do
  path="$RC_INSTALL_ROOT/data/text-encodings/$relative"
  printf 'unexpected\n' >"$path"
  reject_all "$relative"
  rm -- "$path"
done
pass 'all validators reject unexpected text-encoding filenames and suffixes'

mkdir -p "$RC_INSTALL_ROOT/data/text-encodings/staging/unknown/deeper"
reject_all 'an unexpected descendant'
rmdir "$RC_INSTALL_ROOT/data/text-encodings/staging/unknown/deeper" "$RC_INSTALL_ROOT/data/text-encodings/staging/unknown"
manifest="$RC_INSTALL_ROOT/data/text-encodings/staging/$RECORD_ID.json"
mv "$manifest" "$TEST_ROOT/manifest.json"
mkdir "$manifest"
reject_all 'a directory masquerading as a manifest'
rmdir "$manifest"
mv "$TEST_ROOT/manifest.json" "$manifest"
pass 'all validators reject unexpected descendants and non-file text-encoding records'

for directory in text-encodings text-encodings/staging; do
  MOUNTED_PATH="$RC_INSTALL_ROOT/data/$directory"
  reject_all 'a separate text-encoding mount'
done
MOUNTED_PATH=''
pass 'all validators reject separate mounts for text-encoding directories'

mv "$manifest" "$TEST_ROOT/manifest.json"
if ln -s "$TEST_ROOT/manifest.json" "$manifest" 2>/dev/null && [[ -L "$manifest" ]]; then
  reject_all 'a symlinked manifest'
  rm "$manifest"
  mv "$RC_INSTALL_ROOT/data/text-encodings/staging" "$TEST_ROOT/staging"
  ln -s "$TEST_ROOT/staging" "$RC_INSTALL_ROOT/data/text-encodings/staging"
  reject_all 'a symlinked staging directory'
  rm "$RC_INSTALL_ROOT/data/text-encodings/staging"
  mv "$TEST_ROOT/staging" "$RC_INSTALL_ROOT/data/text-encodings/staging"
  pass 'all validators reject symlinked text-encoding files and directories'
else
  rm -f "$manifest"
  skip 'all validators reject symlinked text-encoding files and directories' 'symlink creation is unavailable'
fi
mv "$TEST_ROOT/manifest.json" "$manifest"

if ln "$manifest" "$TEST_ROOT/linked-manifest.json" 2>/dev/null; then
  reject_all 'a hardlinked manifest'
  rm "$TEST_ROOT/linked-manifest.json"
  pass 'all validators reject hardlinked text-encoding records before chmod or backup'
else
  skip 'all validators reject hardlinked text-encoding records' 'hardlink creation is unavailable'
fi

for preparer in prepare_application_data rc_prepare_application_data; do
  if [[ -d "$RC_INSTALL_ROOT/data/media-previews" ]]; then rmdir "$RC_INSTALL_ROOT/data/media-previews"; fi
  chmod 0755 "$RC_INSTALL_ROOT/data/text-encodings" "$RC_INSTALL_ROOT/data/text-encodings/staging"
  for relative in "${TEXT_ENCODING_FIXTURE_FILES[@]}"; do chmod 0644 "$RC_INSTALL_ROOT/data/$relative"; done
  "$preparer" || fail "$preparer rejected valid text-encoding state"
  assert_text_encoding_fixture "$RC_INSTALL_ROOT/data" || fail "$preparer changed registry bytes"
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) ;;
    *)
      while IFS= read -r -d '' path; do
        mode="$(stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path")"
        [[ "$mode" == 700 ]] || fail "$preparer left a text-encoding directory with mode $mode"
      done < <(find "$RC_INSTALL_ROOT/data/text-encodings" -type d -print0)
      while IFS= read -r -d '' path; do
        mode="$(stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path")"
        [[ "$mode" == 600 ]] || fail "$preparer left a text-encoding file with mode $mode"
      done < <(find "$RC_INSTALL_ROOT/data/text-encodings" -type f -print0)
      ;;
  esac
done
pass 'both installers prepare text-encoding state without changing contents'
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) skip 'both installers restore text-encoding modes to 0700/0600' 'Windows does not expose POSIX modes' ;;
  *) pass 'both installers restore text-encoding modes to 0700/0600' ;;
esac

backup_application_data "$RC_BACKUP_ROOT" || fail 'text-encoding backup failed'
assert_text_encoding_fixture "$RC_BACKUP_ROOT/application-data" || fail 'backup changed text-encoding state'
remove_application_data || fail 'text-encoding state removal left directories behind'
[[ ! -e "$RC_INSTALL_ROOT/data" ]] || fail 'application data was not removed after verified backup'
pass 'uninstall backup preserves registry bytes and removes every text-encoding directory'
printf '1..%d\n' "$tests_run"
