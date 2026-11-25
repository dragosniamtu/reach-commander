#!/usr/bin/env bash
set -Eeuo pipefail

TEST_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$TEST_DIRECTORY/../.." && pwd)"
TEST_PARENT="$(cd -- "${TMPDIR:-/tmp}" && pwd)"
TEST_ROOT="$(mktemp -d "$TEST_PARENT/reachcommander-subtitle-data.XXXXXX")"
[[ "$TEST_ROOT" == "$TEST_PARENT"/reachcommander-subtitle-data.* ]] || exit 1
trap 'rm -rf -- "$TEST_ROOT"' EXIT
RC_INSTALL_ROOT="$TEST_ROOT/install"
# These variables are consumed by the production functions loaded below.
# shellcheck disable=SC2034
RUNTIME_UID="$(id -u)"
# shellcheck disable=SC2034
RUNTIME_GID="$(id -g)"
JOB_ID=0123456789abcdef0123456789abcdef
VIDEO_KEY=ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789
MOUNTED_PATH=''
tests_run=0

fail() { printf 'not ok - %s\n' "$1" >&2; exit 1; }
pass() { tests_run=$((tests_run + 1)); printf 'ok %d - %s\n' "$tests_run" "$1"; }
skip() { tests_run=$((tests_run + 1)); printf 'ok %d - %s # SKIP %s\n' "$tests_run" "$1" "$2"; }
rc_die() { printf 'ReachCommander: %s\n' "$1" >&2; }

# Load the actual functions without starting the interactive installers or CLI.
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
load_function "$REPOSITORY_ROOT/deploy/reachcommander" backup_application_file
load_function "$REPOSITORY_ROOT/deploy/reachcommander" backup_application_data
load_function "$REPOSITORY_ROOT/deploy/reachcommander" remove_application_data
load_function "$REPOSITORY_ROOT/deploy/macos/install.sh" rc_validate_application_data_tree
load_function "$REPOSITORY_ROOT/deploy/macos/install.sh" rc_prepare_application_data

# Model a foreign mount explicitly without requiring privileged mount operations.
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
accept_all 'a legacy tree without subtitle state'
mkdir -p "$RC_INSTALL_ROOT/data/subtitle-generation/jobs"
printf '{"phase":"completed"}\n' >"$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
accept_all 'job-only lazy initialization'
pass 'all validators accept legacy and lazily initialized subtitle job trees'

mkdir -p "$RC_INSTALL_ROOT/data/subtitle-generation/receipts" "$RC_INSTALL_ROOT/data/subtitle-generation/transactions"
printf '{"version":1}\n' >"$RC_INSTALL_ROOT/data/subtitle-generation/receipts/$VIDEO_KEY.json"
printf '{"state":"committed"}\n' >"$RC_INSTALL_ROOT/data/subtitle-generation/transactions/$VIDEO_KEY-$JOB_ID.json"
accept_all 'exact persisted subtitle filenames'
pass 'all validators accept job, receipt, and transaction records with exact names'

for relative in \
  jobs/ABCDEF0123456789ABCDEF0123456789AB.json \
  jobs/0123456789abcdef0123456789abcdef0.json \
  receipts/abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456788.json \
  "receipts/$JOB_ID.json" \
  "transactions/$VIDEO_KEY-ABCDEF0123456789ABCDEF0123456789AB.json" \
  "transactions/$VIDEO_KEY.json" \
  "jobs/$JOB_ID.json.tmp" \
  unexpected.json; do
  path="$RC_INSTALL_ROOT/data/subtitle-generation/$relative"
  printf 'unexpected\n' >"$path"
  reject_all "$relative"
  rm -- "$path"
done
pass 'all validators reject incorrect lengths, casing, suffixes, and unexpected files'

mkdir "$RC_INSTALL_ROOT/data/subtitle-generation/unknown"
reject_all 'an unexpected subtree'
rmdir "$RC_INSTALL_ROOT/data/subtitle-generation/unknown"
mv "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" "$TEST_ROOT/job.json"
mkdir "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
reject_all 'a directory masquerading as a job file'
rmdir "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
mv "$TEST_ROOT/job.json" "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
pass 'all validators reject unexpected directories and non-file state records'

MOUNTED_PATH="$RC_INSTALL_ROOT/data/subtitle-generation/transactions"
reject_all 'a separate subtitle transaction mount'
MOUNTED_PATH=''
pass 'all validators preserve foreign-mount rejection for subtitle directories'

mv "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" "$TEST_ROOT/job.json"
if ln -s "$TEST_ROOT/job.json" "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" 2>/dev/null &&
  [[ -L "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" ]]; then
  reject_all 'a symlinked job file'
  rm "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
  mv "$RC_INSTALL_ROOT/data/subtitle-generation/receipts" "$TEST_ROOT/receipts"
  ln -s "$TEST_ROOT/receipts" "$RC_INSTALL_ROOT/data/subtitle-generation/receipts"
  reject_all 'a symlinked receipt directory'
  rm "$RC_INSTALL_ROOT/data/subtitle-generation/receipts"
  mv "$TEST_ROOT/receipts" "$RC_INSTALL_ROOT/data/subtitle-generation/receipts"
  pass 'all validators reject symlinked subtitle files and directories'
else
  rm -f "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"
  skip 'all validators reject symlinked subtitle files and directories' 'symlink creation is unavailable'
fi
mv "$TEST_ROOT/job.json" "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json"

cp "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" "$TEST_ROOT/job-before.json"
cp "$RC_INSTALL_ROOT/data/subtitle-generation/receipts/$VIDEO_KEY.json" "$TEST_ROOT/receipt-before.json"
cp "$RC_INSTALL_ROOT/data/subtitle-generation/transactions/$VIDEO_KEY-$JOB_ID.json" "$TEST_ROOT/transaction-before.json"
for preparer in prepare_application_data rc_prepare_application_data; do
  # The Ubuntu preparer creates a separate media-preview cache; this fixture is
  # limited to subtitle storage, which is shared by the two installer variants.
  if [[ -d "$RC_INSTALL_ROOT/data/media-previews" ]]; then rmdir "$RC_INSTALL_ROOT/data/media-previews"; fi
  chmod 0755 "$RC_INSTALL_ROOT/data/subtitle-generation" "$RC_INSTALL_ROOT/data/subtitle-generation/jobs" \
    "$RC_INSTALL_ROOT/data/subtitle-generation/receipts" "$RC_INSTALL_ROOT/data/subtitle-generation/transactions"
  chmod 0644 "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" \
    "$RC_INSTALL_ROOT/data/subtitle-generation/receipts/$VIDEO_KEY.json" \
    "$RC_INSTALL_ROOT/data/subtitle-generation/transactions/$VIDEO_KEY-$JOB_ID.json"
  "$preparer" || fail "$preparer rejected valid subtitle state"
  cmp "$TEST_ROOT/job-before.json" "$RC_INSTALL_ROOT/data/subtitle-generation/jobs/$JOB_ID.json" || fail "$preparer changed job bytes"
  cmp "$TEST_ROOT/receipt-before.json" "$RC_INSTALL_ROOT/data/subtitle-generation/receipts/$VIDEO_KEY.json" || fail "$preparer changed receipt bytes"
  cmp "$TEST_ROOT/transaction-before.json" "$RC_INSTALL_ROOT/data/subtitle-generation/transactions/$VIDEO_KEY-$JOB_ID.json" || fail "$preparer changed transaction bytes"
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) ;;
    *)
      while IFS= read -r -d '' path; do
        mode="$(stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path")"
        [[ "$mode" == 700 ]] || fail "$preparer left a subtitle directory with mode $mode"
      done < <(find "$RC_INSTALL_ROOT/data/subtitle-generation" -type d -print0)
      while IFS= read -r -d '' path; do
        mode="$(stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path")"
        [[ "$mode" == 600 ]] || fail "$preparer left a subtitle file with mode $mode"
      done < <(find "$RC_INSTALL_ROOT/data/subtitle-generation" -type f -print0)
      ;;
  esac
done
pass 'both installers prepare narrowly accepted subtitle state without changing contents'
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) skip 'both installers restore subtitle directory/file modes to 0700/0600' 'Windows does not expose POSIX modes' ;;
  *) pass 'both installers restore subtitle directory/file modes to 0700/0600' ;;
esac

backup_application_data "$RC_BACKUP_ROOT" || fail 'subtitle backup failed'
for relative in "jobs/$JOB_ID.json" "receipts/$VIDEO_KEY.json" "transactions/$VIDEO_KEY-$JOB_ID.json"; do
  cmp "$RC_INSTALL_ROOT/data/subtitle-generation/$relative" \
    "$RC_BACKUP_ROOT/application-data/subtitle-generation/$relative" || fail "backup changed $relative"
done
remove_application_data || fail 'verified subtitle state removal left directories behind'
[[ ! -e "$RC_INSTALL_ROOT/data" ]] || fail 'application data was not removed after verified backup'
pass 'uninstall backup preserves subtitle bytes and removal handles every subtitle directory'
printf '1..%d\n' "$tests_run"
