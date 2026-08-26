#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
COMMON_LIBRARY="$SCRIPT_DIRECTORY/lib/common.sh"
RENDERER="$SCRIPT_DIRECTORY/render_config.py"
LAN_ADDRESS_HELPER="$SCRIPT_DIRECTORY/lan_address.py"
COMPOSE_TEMPLATE="$SCRIPT_DIRECTORY/compose.release.yaml"
UPDATER_COMPOSE_TEMPLATE="$SCRIPT_DIRECTORY/compose.updater.yaml"
MANAGEMENT_COMMAND="$SCRIPT_DIRECTORY/reachcommander"
UPDATER_PROTOCOL="$SCRIPT_DIRECTORY/updater_protocol.py"
UPDATER_SERVICE="$SCRIPT_DIRECTORY/updater_service.py"
UPDATER_UNIT="$SCRIPT_DIRECTORY/systemd/reachcommander-updater.service"
BUNDLE_VERSION_FILE="$SCRIPT_DIRECTORY/VERSION"
WORK_ROOT=''
INSTALL_COMMITTED=false
INSTALL_TRANSACTION_ACTIVE=false
HAD_EXISTING=false
RECONFIGURE_BACKUP_DIRECTORY=''
BUNDLE_VERSION=''
UPDATER_UNIT_PATH=''
UPDATER_RUNTIME_DIRECTORY=''
UPDATER_SOCKET_PATH=''
ACCESS_MODE=''
BIND_ADDRESS=''

if [[ ! -f "$COMMON_LIBRARY" ]]; then
  printf 'ReachCommander: installer bundle is missing lib/common.sh\n' >&2
  exit 1
fi

# shellcheck source=lib/common.sh
source "$COMMON_LIBRARY"

cleanup() {
  local work_parent
  local canonical_work
  local unexpected_entry
  if [[ -n "$WORK_ROOT" && -d "$WORK_ROOT" ]]; then
    work_parent="$(readlink -m -- "$(dirname -- "$RC_INSTALL_ROOT")")"
    canonical_work="$(readlink -m -- "$WORK_ROOT")"
    case "$canonical_work" in
      "$work_parent"/.reachcommander-install.*)
        rm -rf -- "$canonical_work"
        ;;
      *)
        printf 'ReachCommander: refusing to remove unexpected staging path\n' >&2
        ;;
    esac
  fi

  if [[
    "$INSTALL_COMMITTED" == 'false' &&
    -n "${RC_INSTALL_ROOT:-}" &&
    -d "$RC_INSTALL_ROOT/state" &&
    ! -L "$RC_INSTALL_ROOT" &&
    ! -L "$RC_INSTALL_ROOT/state"
  ]]; then
    unexpected_entry="$(
      find "$RC_INSTALL_ROOT" \
        -mindepth 1 \
        -maxdepth 2 \
        ! -path "$RC_INSTALL_ROOT/state" \
        ! -path "$RC_INSTALL_ROOT/state/command.lock" \
        -print \
        -quit
    )"
    if [[ -z "$unexpected_entry" ]]; then
      rm -f -- "$RC_INSTALL_ROOT/state/command.lock"
      rmdir -- "$RC_INSTALL_ROOT/state" "$RC_INSTALL_ROOT" 2>/dev/null || true
    fi
  fi
}
trap cleanup EXIT

prompt_value() {
  local prompt="$1"
  local default_value="${2-}"
  local value
  if [[ -n "$default_value" ]]; then
    printf '%s [%s]: ' "$prompt" "$default_value" >&2
  else
    printf '%s: ' "$prompt" >&2
  fi
  if ! IFS= read -r value; then
    rc_die 'installer input ended before confirmation'
    return 1
  fi
  REPLY_VALUE="${value:-$default_value}"
}

require_bundle() {
  local bundle_file
  for bundle_file in \
    "$RENDERER" \
    "$LAN_ADDRESS_HELPER" \
    "$COMPOSE_TEMPLATE" \
    "$UPDATER_COMPOSE_TEMPLATE" \
    "$MANAGEMENT_COMMAND" \
    "$UPDATER_PROTOCOL" \
    "$UPDATER_SERVICE" \
    "$UPDATER_UNIT" \
    "$BUNDLE_VERSION_FILE"; do
    if [[ ! -f "$bundle_file" || -L "$bundle_file" ]]; then
      rc_die 'installer bundle is incomplete'
      return 1
    fi
  done
}

read_bundle_version() {
  local number='(0|[1-9][0-9]*)'
  BUNDLE_VERSION="$(<"$BUNDLE_VERSION_FILE")"
  if [[ ! "$BUNDLE_VERSION" =~ ^v${number}\.${number}\.${number}$ ]]; then
    rc_die 'installer bundle VERSION must be one stable semantic version'
    return 1
  fi
}

init_updater_paths() {
  UPDATER_UNIT_PATH='/etc/systemd/system/reachcommander-updater.service'
  UPDATER_RUNTIME_DIRECTORY='/run/reachcommander-updater'
  UPDATER_SOCKET_PATH="$UPDATER_RUNTIME_DIRECTORY/updater.sock"
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]] && (( EUID != 0 )); then
    UPDATER_UNIT_PATH="${REACHCOMMANDER_TEST_SYSTEMD_UNIT_PATH:-}"
    UPDATER_RUNTIME_DIRECTORY="${REACHCOMMANDER_TEST_UPDATER_RUNTIME_DIRECTORY:-}"
    UPDATER_SOCKET_PATH="${REACHCOMMANDER_TEST_UPDATER_SOCKET_PATH:-}"
    if [[ -z "$UPDATER_UNIT_PATH" || -z "$UPDATER_RUNTIME_DIRECTORY" || -z "$UPDATER_SOCKET_PATH" ]]; then
      rc_die 'all updater test paths are required'
      return 1
    fi
  fi
  export UPDATER_UNIT_PATH UPDATER_RUNTIME_DIRECTORY UPDATER_SOCKET_PATH
}

assert_updater_layout_safe() {
  local path
  for path in \
    "$(dirname -- "$UPDATER_UNIT_PATH")" \
    "$UPDATER_RUNTIME_DIRECTORY"; do
    if [[ -L "$path" || -e "$path" && ! -d "$path" ]]; then
      rc_die 'updater-owned directories must not be symlinks or files'
      return 1
    fi
  done
  if [[ -L "$UPDATER_UNIT_PATH" || -e "$UPDATER_UNIT_PATH" && ! -f "$UPDATER_UNIT_PATH" ]]; then
    rc_die 'updater service unit path is unsafe'
    return 1
  fi
}

preflight() {
  rc_require_commands docker python3 readlink flock install mktemp setpriv sync find systemctl
  require_bundle
  read_bundle_version
  if ! docker compose version >/dev/null 2>&1; then
    rc_die 'Docker Compose v2 is required'
    return 1
  fi
}

validate_runtime_id() {
  local value="$1"
  local field="$2"
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]] || (( value > 2147483647 )); then
    rc_die "$field must identify a non-root account"
    return 1
  fi
}

source_id_exists() {
  local candidate="$1"
  local existing
  for existing in "${SOURCE_IDS[@]:-}"; do
    [[ "$existing" == "$candidate" ]] && return 0
  done
  return 1
}

reject_installer_path_overlap() {
  local source_path="$1"
  local owned_path
  local canonical_owned
  for owned_path in \
    "$RC_INSTALL_ROOT" \
    "$RC_BACKUP_ROOT" \
    "$(dirname -- "$RC_COMMAND_PATH")" \
    "$UPDATER_UNIT_PATH" \
    "$UPDATER_RUNTIME_DIRECTORY"; do
    canonical_owned="$(readlink -m -- "$owned_path")"
    if [[
      "$source_path" == "$canonical_owned" ||
      "$source_path" == "$canonical_owned"/* ||
      "$canonical_owned" == "$source_path"/*
    ]]; then
      rc_die 'source path overlaps an installer-owned location'
      return 1
    fi
  done
}

authentication_path_is_mount_point() {
  local path="$1"
  python3 - "$path" <<'PY'
import os
import sys

candidate = os.path.realpath(sys.argv[1])
try:
    with open('/proc/self/mountinfo', encoding='utf-8') as mounts:
        for line in mounts:
            fields = line.split()
            if len(fields) < 5:
                continue
            mount_path = fields[4]
            for escaped, value in ((r'\040', ' '), (r'\011', '\t'), (r'\012', '\n'), (r'\134', '\\')):
                mount_path = mount_path.replace(escaped, value)
            if os.path.realpath(mount_path) == candidate:
                raise SystemExit(0)
except FileNotFoundError:
    pass
raise SystemExit(1)
PY
}

validate_application_data_tree() {
  local data_root="$RC_INSTALL_ROOT/data"
  local path
  local relative_path
  local invalid=0
  if [[ ! -e "$data_root" ]]; then
    return 0
  fi
  if [[ ! -d "$data_root" || -L "$data_root" ]] || authentication_path_is_mount_point "$data_root"; then
    rc_die 'application data root must be a real, unmounted directory'
    return 1
  fi
  while IFS= read -r -d '' path; do
    if [[ -L "$path" ]] || authentication_path_is_mount_point "$path"; then
      invalid=1
      break
    fi
    relative_path="${path#"$data_root"/}"
    case "$relative_path" in
      auth | keys | file-operations | file-operations/plans | file-operations/operations)
        [[ -d "$path" ]] || invalid=1
        ;;
      auth/account.json | auth/bootstrap.json | auth/auth.lock | keys/key-*.xml)
        [[ -f "$path" ]] || invalid=1
        ;;
      *)
        if [[ "$relative_path" =~ ^file-operations/(plans|operations)/[0-9a-f]{32}\.json$ ]]; then
          [[ -f "$path" ]] || invalid=1
        else
          invalid=1
        fi
        ;;
    esac
    (( invalid == 0 )) || break
  done < <(find "$data_root" -xdev -mindepth 1 -print0)
  if (( invalid != 0 )); then
    rc_die 'application data tree contains an unsafe entry'
    return 1
  fi
  if [[ -e "$data_root/file-operations" ]] &&
    [[ ! -d "$data_root/file-operations/plans" || ! -d "$data_root/file-operations/operations" ]]; then
    rc_die 'application data tree contains an unsafe entry'
    return 1
  fi
}

assert_generated_layout_safe() {
  local generated_path
  if [[ -e "$RC_INSTALL_ROOT" && ! -d "$RC_INSTALL_ROOT" ]] || [[ -L "$RC_INSTALL_ROOT" ]]; then
    rc_die 'install root must be a real directory'
    return 1
  fi
  for generated_path in \
    "$RC_INSTALL_ROOT/config" \
    "$RC_INSTALL_ROOT/state" \
    "$RC_INSTALL_ROOT/bin" \
    "$RC_INSTALL_ROOT/lib" \
    "$RC_INSTALL_ROOT/backups" \
    "$RC_INSTALL_ROOT/data" \
    "$RC_INSTALL_ROOT/data/auth" \
    "$RC_INSTALL_ROOT/data/keys" \
    "$(dirname -- "$RC_COMMAND_PATH")"; do
    if [[ -L "$generated_path" ]] || [[ -e "$generated_path" && ! -d "$generated_path" ]]; then
      rc_die 'installer-owned directories must not be symlinks or files'
      return 1
    fi
  done
  validate_application_data_tree
}

prepare_application_data() {
  local directory
  local file
  validate_application_data_tree || return 1
  for directory in data data/auth data/keys; do
    if [[ -L "$RC_INSTALL_ROOT/$directory" ]]; then
      rc_die 'application data directories must not be symlinks'
      return 1
    fi
    mkdir -p -- "$RC_INSTALL_ROOT/$directory" || return 1
    chmod 0700 -- "$RC_INSTALL_ROOT/$directory" || return 1
  done
  for directory in data/file-operations data/file-operations/plans data/file-operations/operations; do
    if [[ -d "$RC_INSTALL_ROOT/$directory" ]]; then
      chmod 0700 -- "$RC_INSTALL_ROOT/$directory" || return 1
    fi
  done
  while IFS= read -r -d '' file; do
    chmod 0600 -- "$file" || return 1
  done < <(find "$RC_INSTALL_ROOT/data" -xdev -mindepth 2 -type f -print0)
  validate_application_data_tree || return 1
  if (( EUID == 0 )); then
    chown -R -- "$RUNTIME_UID:$RUNTIME_GID" "$RC_INSTALL_ROOT/data" || return 1
  fi
}

check_source_access() {
  local path="$1"
  local access="$2"
  local runtime_uid="$3"
  local runtime_gid="$4"
  local access_test="test -r \"\$1\" && test -x \"\$1\""
  if [[ "$access" == 'rw' ]]; then
    access_test="test -r \"\$1\" && test -w \"\$1\" && test -x \"\$1\""
  fi
  if ! setpriv \
    --reuid="$runtime_uid" \
    --regid="$runtime_gid" \
    --clear-groups \
    -- sh -c "$access_test" reachcommander-access "$path"; then
    rc_die 'selected runtime identity cannot access a source with the requested policy'
    return 1
  fi
}

collect_sources() {
  SOURCE_IDS=()
  SOURCE_NAMES=()
  SOURCE_PATHS=()
  SOURCE_ACCESS=()
  local add_another='y'
  local source_name
  local source_path
  local suggested_id
  local source_id
  local access
  local broad_confirmation

  while [[ "$add_another" =~ ^[Yy]$ ]]; do
    prompt_value 'Source display name' ''
    source_name="$REPLY_VALUE"
    [[ -n "$source_name" ]] || {
      rc_die 'source display name is required'
      return 1
    }

    prompt_value 'Absolute source directory' ''
    source_path="$(rc_canonical_source "$REPLY_VALUE")" || return 1
    reject_installer_path_overlap "$source_path" || return 1
    case "$source_path" in
      /home | /srv | /mnt)
        prompt_value "Type 'mount broad source' to allow $source_path" ''
        broad_confirmation="$REPLY_VALUE"
        [[ "$broad_confirmation" == 'mount broad source' ]] || {
          rc_die 'broad source confirmation did not match'
          return 1
        }
        ;;
    esac

    suggested_id="$(rc_normalize_source_id "$source_name")" || return 1
    while true; do
      prompt_value 'Source ID' "$suggested_id"
      source_id="$REPLY_VALUE"
      if [[ ! "$source_id" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]]; then
        printf 'Source ID is invalid; use lowercase letters, digits, hyphens, or underscores.\n' >&2
        continue
      fi
      if source_id_exists "$source_id"; then
        printf 'Source ID is already in use; choose a distinct ID.\n' >&2
        continue
      fi
      break
    done
    local existing_path
    for existing_path in "${SOURCE_PATHS[@]:-}"; do
      if [[ "$existing_path" == "$source_path" ]]; then
        rc_die 'source directory is already configured'
        return 1
      fi
    done

    prompt_value 'Access policy (RO or RW)' 'RO'
    access="${REPLY_VALUE,,}"
    if [[ "$access" != 'ro' && "$access" != 'rw' ]]; then
      rc_die 'source access policy must be RO or RW'
      return 1
    fi
    check_source_access "$source_path" "$access" "$RUNTIME_UID" "$RUNTIME_GID" || return 1

    SOURCE_IDS+=("$source_id")
    SOURCE_NAMES+=("$source_name")
    SOURCE_PATHS+=("$source_path")
    SOURCE_ACCESS+=("$access")

    prompt_value 'Add another source? (y/N)' 'n'
    add_another="$REPLY_VALUE"
  done
}

validate_default_source() {
  local candidate="$1"
  local field="$2"
  if ! source_id_exists "$candidate"; then
    rc_die "$field must be one of the configured source IDs"
    return 1
  fi
}

write_request() {
  local request_path="$1"
  local image="$2"
  local index
  local default_left
  local default_right
  python3 "$RENDERER" create-request \
    --output "$request_path" \
    --access-mode "$ACCESS_MODE" \
    --bind-address "$BIND_ADDRESS" \
    --port "$PORT" \
    --uid "$RUNTIME_UID" \
    --gid "$RUNTIME_GID" \
    --image "$image"
  for index in "${!SOURCE_IDS[@]}"; do
    default_left=false
    default_right=false
    [[ "${SOURCE_IDS[$index]}" == "$DEFAULT_LEFT" ]] && default_left=true
    [[ "${SOURCE_IDS[$index]}" == "$DEFAULT_RIGHT" ]] && default_right=true
    python3 "$RENDERER" add-source \
      --request "$request_path" \
      --id "${SOURCE_IDS[$index]}" \
      --name "${SOURCE_NAMES[$index]}" \
      --host-path "${SOURCE_PATHS[$index]}" \
      --access "${SOURCE_ACCESS[$index]}" \
      --default-left "$default_left" \
      --default-right "$default_right"
  done
}

copy_atomic() {
  local source="$1"
  local destination="$2"
  local mode="$3"
  local destination_directory
  local destination_name
  local temporary
  destination_directory="$(dirname -- "$destination")"
  destination_name="$(basename -- "$destination")"
  mkdir -p -- "$destination_directory"
  temporary="$(mktemp "$destination_directory/.${destination_name}.reachcommander-copy.XXXXXX")"
  if ! install -m "$mode" -- "$source" "$temporary" || ! sync -f -- "$temporary"; then
    rm -f -- "$temporary"
    rc_die 'failed to stage an installer-owned file'
    return 1
  fi
  if ! mv -f -- "$temporary" "$destination"; then
    rm -f -- "$temporary"
    rc_die 'failed to replace an installer-owned file'
    return 1
  fi
}

LEGACY_DEPLOYMENT_FILES=(
  '.env'
  'compose.yaml'
  'config/sources.json'
  'state/source-mounts.json'
  'state/channel'
  'state/current-image'
  'state/previous-image'
  'bin/render_config.py'
  'lib/common.sh'
)

UPDATER_DEPLOYMENT_FILES=(
  'compose.override.yaml'
  'state/current-version'
  'state/previous-version'
  'bin/updater_service.py'
  'lib/updater_protocol.py'
)

DEPLOYMENT_FILES=(
  "${LEGACY_DEPLOYMENT_FILES[@]}"
  "${UPDATER_DEPLOYMENT_FILES[@]}"
)

file_mode() {
  case "$1" in
    bin/*)
      printf '0755\n'
      ;;
    config/sources.json)
      printf '0644\n'
      ;;
    lib/updater_protocol.py)
      printf '0644\n'
      ;;
    *)
      printf '0600\n'
      ;;
  esac
}

backup_existing_deployment() {
  local backup_directory="$1"
  local relative_path
  mkdir -p -- \
    "$backup_directory/deployment" \
    "$backup_directory/command" \
    "$backup_directory/systemd"
  for relative_path in "${LEGACY_DEPLOYMENT_FILES[@]}"; do
    if [[ ! -f "$RC_INSTALL_ROOT/$relative_path" || -L "$RC_INSTALL_ROOT/$relative_path" ]]; then
      rc_die 'existing deployment is incomplete or symlinked'
      return 1
    fi
    mkdir -p -- "$backup_directory/deployment/$(dirname -- "$relative_path")"
    if ! install -m "$(file_mode "$relative_path")" -- \
      "$RC_INSTALL_ROOT/$relative_path" \
      "$backup_directory/deployment/$relative_path" ||
      ! sync -f -- "$backup_directory/deployment/$relative_path" ||
      ! cmp -s -- \
        "$RC_INSTALL_ROOT/$relative_path" \
        "$backup_directory/deployment/$relative_path"; then
      rc_die 'existing deployment backup could not be verified'
      return 1
    fi
  done
  for relative_path in "${UPDATER_DEPLOYMENT_FILES[@]}"; do
    if [[ ! -e "$RC_INSTALL_ROOT/$relative_path" ]]; then
      continue
    fi
    if [[ ! -f "$RC_INSTALL_ROOT/$relative_path" || -L "$RC_INSTALL_ROOT/$relative_path" ]]; then
      rc_die 'existing updater deployment state is unsafe'
      return 1
    fi
    mkdir -p -- "$backup_directory/deployment/$(dirname -- "$relative_path")"
    if ! install -m "$(file_mode "$relative_path")" -- \
      "$RC_INSTALL_ROOT/$relative_path" \
      "$backup_directory/deployment/$relative_path" ||
      ! sync -f -- "$backup_directory/deployment/$relative_path" ||
      ! cmp -s -- \
        "$RC_INSTALL_ROOT/$relative_path" \
        "$backup_directory/deployment/$relative_path"; then
      rc_die 'existing updater deployment backup could not be verified'
      return 1
    fi
  done
  if [[ ! -f "$RC_COMMAND_PATH" || -L "$RC_COMMAND_PATH" ]]; then
    rc_die 'existing management command is missing or symlinked'
    return 1
  fi
  if [[ -e "$UPDATER_UNIT_PATH" ]]; then
    if [[ ! -f "$UPDATER_UNIT_PATH" || -L "$UPDATER_UNIT_PATH" ]] ||
      ! install -m 0644 -- \
        "$UPDATER_UNIT_PATH" \
        "$backup_directory/systemd/reachcommander-updater.service" ||
      ! sync -f -- "$backup_directory/systemd/reachcommander-updater.service" ||
      ! cmp -s -- \
        "$UPDATER_UNIT_PATH" \
        "$backup_directory/systemd/reachcommander-updater.service"; then
      rc_die 'existing updater unit backup could not be verified'
      return 1
    fi
  fi
  if ! install -m 0755 -- \
    "$RC_COMMAND_PATH" \
    "$backup_directory/command/reachcommander" ||
    ! sync -f -- "$backup_directory/command/reachcommander" ||
    ! cmp -s -- \
      "$RC_COMMAND_PATH" \
      "$backup_directory/command/reachcommander"; then
    rc_die 'existing management command backup could not be verified'
    return 1
  fi
}

install_staged_deployment() {
  local stage_root="$1"
  local relative_path
  for relative_path in "${DEPLOYMENT_FILES[@]}"; do
    copy_atomic \
      "$stage_root/$relative_path" \
      "$RC_INSTALL_ROOT/$relative_path" \
      "$(file_mode "$relative_path")" || return 1
    maybe_interrupt_install "$relative_path"
  done
  chmod 0755 -- "$RC_INSTALL_ROOT/config" || return 1
  chmod 0644 -- "$RC_INSTALL_ROOT/config/sources.json" || return 1
  copy_atomic "$MANAGEMENT_COMMAND" "$RC_COMMAND_PATH" 0755 || return 1
}

restore_deployment() {
  local backup_directory="$1"
  local relative_path
  for relative_path in "${LEGACY_DEPLOYMENT_FILES[@]}"; do
    [[ -f "$backup_directory/deployment/$relative_path" ]] || {
      rc_die 'reconfiguration recovery backup is incomplete'
      return 1
    }
    copy_atomic \
      "$backup_directory/deployment/$relative_path" \
      "$RC_INSTALL_ROOT/$relative_path" \
      "$(file_mode "$relative_path")" || return 1
  done
  for relative_path in "${UPDATER_DEPLOYMENT_FILES[@]}"; do
    if [[ -f "$backup_directory/deployment/$relative_path" ]]; then
      copy_atomic \
        "$backup_directory/deployment/$relative_path" \
        "$RC_INSTALL_ROOT/$relative_path" \
        "$(file_mode "$relative_path")" || return 1
    else
      rm -f -- "$RC_INSTALL_ROOT/$relative_path" || return 1
    fi
  done
  chmod 0755 -- "$RC_INSTALL_ROOT/config" || return 1
  chmod 0644 -- "$RC_INSTALL_ROOT/config/sources.json" || return 1
  [[ -f "$backup_directory/command/reachcommander" ]] || {
    rc_die 'reconfiguration command backup is incomplete'
    return 1
  }
  copy_atomic \
    "$backup_directory/command/reachcommander" \
    "$RC_COMMAND_PATH" \
    0755 || return 1
}

restore_updater_unit() {
  local backup_directory="$1"
  local backed_up_unit="$backup_directory/systemd/reachcommander-updater.service"
  if [[ -f "$backed_up_unit" && ! -L "$backed_up_unit" ]]; then
    copy_atomic "$backed_up_unit" "$UPDATER_UNIT_PATH" 0644 || return 1
    systemctl daemon-reload || return 1
    systemctl restart reachcommander-updater.service || return 1
    wait_for_updater_socket || return 1
    return 0
  fi
  systemctl disable --now reachcommander-updater.service >/dev/null 2>&1 || true
  rm -f -- "$UPDATER_UNIT_PATH" || return 1
  systemctl daemon-reload || return 1
  rm -f -- "$UPDATER_SOCKET_PATH" || return 1
  if [[ -d "$UPDATER_RUNTIME_DIRECTORY" && ! -L "$UPDATER_RUNTIME_DIRECTORY" ]]; then
    rmdir -- "$UPDATER_RUNTIME_DIRECTORY" 2>/dev/null || true
  fi
}

updater_socket_is_ready() {
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]] && (( EUID != 0 )); then
    [[ -f "$UPDATER_SOCKET_PATH" && ! -L "$UPDATER_SOCKET_PATH" ]]
  else
    [[ -S "$UPDATER_SOCKET_PATH" ]]
  fi
}

wait_for_updater_socket() {
  local attempt
  for (( attempt = 0; attempt < 100; attempt++ )); do
    if updater_socket_is_ready; then
      return 0
    fi
    sleep 0.1
  done
  rc_die 'updater service socket did not become ready'
  return 1
}

install_updater_service() {
  copy_atomic "$UPDATER_UNIT" "$UPDATER_UNIT_PATH" 0644 || return 1
  systemctl daemon-reload || return 1
  systemctl enable --now reachcommander-updater.service || return 1
  systemctl restart reachcommander-updater.service || return 1
  wait_for_updater_socket
}

remove_initial_updater_service() {
  systemctl disable --now reachcommander-updater.service >/dev/null 2>&1 || true
  rm -f -- "$UPDATER_UNIT_PATH" "$UPDATER_SOCKET_PATH" || return 1
  systemctl daemon-reload || return 1
  if [[ -d "$UPDATER_RUNTIME_DIRECTORY" && ! -L "$UPDATER_RUNTIME_DIRECTORY" ]]; then
    rmdir -- "$UPDATER_RUNTIME_DIRECTORY" 2>/dev/null || true
  fi
}

remove_reconfiguration_backup() {
  local backup_directory="$1"
  local expected="$RC_INSTALL_ROOT/backups/.reconfigure-transaction"
  local directory
  local relative_path
  [[ "$(readlink -m -- "$backup_directory")" == "$(readlink -m -- "$expected")" ]] || {
    rc_die 'refusing to remove an unexpected reconfiguration backup'
    return 1
  }
  [[ -d "$backup_directory" && ! -L "$backup_directory" ]] || {
    rc_die 'reconfiguration backup is missing or symlinked'
    return 1
  }
  for directory in \
    deployment \
    deployment/config \
    deployment/state \
    deployment/bin \
    deployment/lib \
    command \
    systemd; do
    if [[ -L "$backup_directory/$directory" || -e "$backup_directory/$directory" && ! -d "$backup_directory/$directory" ]]; then
      rc_die 'reconfiguration backup contains an unsafe directory'
      return 1
    fi
  done
  for relative_path in "${DEPLOYMENT_FILES[@]}"; do
    rm -f -- "$backup_directory/deployment/$relative_path"
  done
  rm -f -- "$backup_directory/command/reachcommander"
  rm -f -- "$backup_directory/systemd/reachcommander-updater.service"
  for directory in \
    deployment/config \
    deployment/state \
    deployment/bin \
    deployment/lib \
    deployment \
    command \
    systemd; do
    if [[ -d "$backup_directory/$directory" ]]; then
      rmdir -- "$backup_directory/$directory" || return 1
    fi
  done
  rmdir -- "$backup_directory"
}

begin_reconfiguration_transaction() {
  local marker="$RC_INSTALL_ROOT/state/install-transaction"
  RECONFIGURE_BACKUP_DIRECTORY="$RC_INSTALL_ROOT/backups/.reconfigure-transaction"
  if [[ -e "$marker" || -e "$RECONFIGURE_BACKUP_DIRECTORY" ]]; then
    rc_die 'an incomplete reconfiguration transaction requires recovery'
    return 1
  fi
  mkdir -p -- "$RC_INSTALL_ROOT/backups"
  chmod 0700 -- "$RC_INSTALL_ROOT/backups"
  backup_existing_deployment "$RECONFIGURE_BACKUP_DIRECTORY" || return 1
  rc_atomic_write "$marker" "$RECONFIGURE_BACKUP_DIRECTORY"$'\n' || return 1
  INSTALL_TRANSACTION_ACTIVE=true
}

complete_reconfiguration_transaction() {
  local marker="$RC_INSTALL_ROOT/state/install-transaction"
  rm -f -- "$marker"
  remove_reconfiguration_backup "$RECONFIGURE_BACKUP_DIRECTORY"
  INSTALL_TRANSACTION_ACTIVE=false
}

rollback_reconfiguration_transaction() {
  local restart_previous="${1:-true}"
  local marker="$RC_INSTALL_ROOT/state/install-transaction"
  if ! restore_deployment "$RECONFIGURE_BACKUP_DIRECTORY"; then
    rc_die 'reconfiguration rollback could not restore the previous files'
    return 1
  fi
  if ! restore_updater_unit "$RECONFIGURE_BACKUP_DIRECTORY"; then
    rc_die 'reconfiguration rollback could not restore the previous updater service'
    return 1
  fi
  if [[ "$restart_previous" == 'true' ]]; then
    if ! rc_compose up -d reachcommander || ! rc_wait_healthy reachcommander 60; then
      rc_die 'reconfiguration rollback could not restore the previous healthy service'
      return 1
    fi
  fi
  rm -f -- "$marker"
  remove_reconfiguration_backup "$RECONFIGURE_BACKUP_DIRECTORY" || return 1
  INSTALL_TRANSACTION_ACTIVE=false
}

recover_incomplete_reconfiguration() {
  local marker="$RC_INSTALL_ROOT/state/install-transaction"
  local expected="$RC_INSTALL_ROOT/backups/.reconfigure-transaction"
  local recorded=''
  if [[ ! -e "$marker" && ! -e "$expected" ]]; then
    return 0
  fi
  if [[ ! -e "$marker" && -d "$expected" && ! -L "$expected" ]]; then
    remove_reconfiguration_backup "$expected"
    return 0
  fi
  if [[ ! -f "$marker" || -L "$marker" || ! -d "$expected" || -L "$expected" ]]; then
    rc_die 'incomplete reconfiguration state is unsafe; manual recovery is required'
    return 1
  fi
  IFS= read -r recorded <"$marker" || true
  if [[ "$recorded" != "$expected" ]]; then
    rc_die 'incomplete reconfiguration marker is invalid; manual recovery is required'
    return 1
  fi
  RECONFIGURE_BACKUP_DIRECTORY="$expected"
  INSTALL_TRANSACTION_ACTIVE=true
  if ! rollback_reconfiguration_transaction true; then
    rc_die 'automatic reconfiguration recovery failed; manual recovery is required'
    return 1
  fi
  printf 'Recovered an interrupted ReachCommander reconfiguration.\n'
}

remove_partial_initial_deployment() {
  local relative_path
  local directory
  for relative_path in "${DEPLOYMENT_FILES[@]}" 'state/install-transaction'; do
    rm -f -- "$RC_INSTALL_ROOT/$relative_path"
  done
  rm -f -- "$RC_COMMAND_PATH"
  for directory in config state bin lib backups; do
    if [[ -d "$RC_INSTALL_ROOT/$directory" ]]; then
      rmdir -- "$RC_INSTALL_ROOT/$directory" 2>/dev/null || true
    fi
  done
  if [[ -d "$RC_INSTALL_ROOT" ]]; then
    rmdir -- "$RC_INSTALL_ROOT" 2>/dev/null || true
  fi
  INSTALL_TRANSACTION_ACTIVE=false
}

maybe_interrupt_install() {
  local relative_path="$1"
  local phase=''
  case "$relative_path" in
    state/current-image) phase='current-image' ;;
  esac
  if [[
    -n "$phase" &&
    "${REACHCOMMANDER_TESTING:-0}" == '1' &&
    "${REACHCOMMANDER_TEST_INSTALL_INTERRUPT_AFTER:-}" == "$phase"
  ]] && (( EUID != 0 )); then
    kill -TERM "$$"
  fi
}

handle_install_signal() {
  local status="$1"
  trap - HUP INT TERM
  if [[ "$INSTALL_TRANSACTION_ACTIVE" == 'true' ]]; then
    if [[ "$HAD_EXISTING" == 'true' ]]; then
      if ! rollback_reconfiguration_transaction true; then
        printf 'ReachCommander: interrupted reconfiguration requires manual recovery.\n' >&2
      fi
    else
      rc_compose down >/dev/null 2>&1 || true
      remove_initial_updater_service || true
      remove_partial_initial_deployment
    fi
  fi
  exit "$status"
}

handle_start_failure() {
  local had_existing="$1"
  rc_compose logs --tail 200 reachcommander >&2 || true
  if [[ "$had_existing" == 'true' ]]; then
    rollback_reconfiguration_transaction true || {
      rc_die 'reconfiguration failed and the previous files could not be restored'
      return 3
    }
    rc_die 'reconfiguration was unhealthy; the previous deployment was restored'
    return 2
  fi
  rc_compose down >/dev/null 2>&1 || true
  rc_die 'initial startup was unhealthy; validated configuration was retained'
  return 2
}

trap 'handle_install_signal 129' HUP
trap 'handle_install_signal 130' INT
trap 'handle_install_signal 143' TERM

preflight
rc_init_paths
init_updater_paths
rc_assert_safe_install_root
assert_generated_layout_safe
assert_updater_layout_safe
if [[ "${REACHCOMMANDER_TESTING:-0}" != '1' ]] || (( EUID == 0 )); then
  rc_require_root
fi
rc_acquire_lock
recover_incomplete_reconfiguration

if [[ -f "$RC_INSTALL_ROOT/.env" || -f "$RC_INSTALL_ROOT/compose.yaml" ]]; then
  HAD_EXISTING=true
  prompt_value 'A ReachCommander deployment exists. Reconfigure it? (y/N)' 'n'
  if [[ ! "$REPLY_VALUE" =~ ^[Yy]$ ]]; then
    printf 'ReachCommander deployment left unchanged.\n'
    exit 0
  fi
fi
INSTALL_CHANNEL='stable'
if [[ "$HAD_EXISTING" == 'true' ]]; then
  if [[ ! -f "$RC_INSTALL_ROOT/state/channel" || -L "$RC_INSTALL_ROOT/state/channel" ]] ||
    ! IFS= read -r INSTALL_CHANNEL <"$RC_INSTALL_ROOT/state/channel" ||
    [[ -z "$INSTALL_CHANNEL" ]]; then
    rc_die 'existing update channel could not be read'
    exit 1
  fi
  if ! rc_validate_channel "$INSTALL_CHANNEL" >/dev/null 2>&1; then
    rc_die 'existing update channel is invalid'
    exit 1
  fi
fi
if [[ "$HAD_EXISTING" == 'false' && -e "$RC_COMMAND_PATH" ]]; then
  rc_die 'management command exists without a complete deployment'
  exit 1
fi
if [[ "$HAD_EXISTING" == 'false' && -e "$UPDATER_UNIT_PATH" ]]; then
  rc_die 'updater service unit exists without a ReachCommander deployment'
  exit 1
fi

invoking_ids="$(rc_invoking_ids)"
default_uid="${invoking_ids%%:*}"
default_gid="${invoking_ids##*:}"

printf 'Network access mode:\n'
printf '  1. Secure HTTPS reverse proxy (recommended)\n'
printf '  2. Direct HTTP on trusted LAN\n'
prompt_value 'Select network access mode' '1'
case "$REPLY_VALUE" in
  1)
    ACCESS_MODE='secure-https'
    BIND_ADDRESS='127.0.0.1'
    ;;
  2)
    ACCESS_MODE='trusted-lan-http'
    BIND_ADDRESS='0.0.0.0'
    printf 'WARNING: trusted LAN HTTP listens on every host interface.\n' >&2
    printf 'Credentials, cookies, filenames, and file contents are not encrypted in transit.\n' >&2
    printf 'Do not expose this port through router forwarding or a public interface.\n' >&2
    ;;
  *)
    rc_die 'network access mode must be 1 or 2'
    exit 1
    ;;
esac
prompt_value 'Host port' '8092'
PORT="$REPLY_VALUE"
rc_validate_port "$PORT"
prompt_value 'Container runtime UID' "$default_uid"
RUNTIME_UID="$REPLY_VALUE"
validate_runtime_id "$RUNTIME_UID" 'runtime UID'
prompt_value 'Container runtime GID' "$default_gid"
RUNTIME_GID="$REPLY_VALUE"
validate_runtime_id "$RUNTIME_GID" 'runtime GID'

declare -a SOURCE_IDS SOURCE_NAMES SOURCE_PATHS SOURCE_ACCESS
collect_sources

prompt_value 'Default left source ID' "${SOURCE_IDS[0]}"
DEFAULT_LEFT="$REPLY_VALUE"
validate_default_source "$DEFAULT_LEFT" 'default left source'
prompt_value 'Default right source ID' "${SOURCE_IDS[0]}"
DEFAULT_RIGHT="$REPLY_VALUE"
validate_default_source "$DEFAULT_RIGHT" 'default right source'

printf 'ReachCommander includes its own administrator login; proxy authentication is optional.\n'
if [[ "$ACCESS_MODE" == 'secure-https' ]]; then
  prompt_value "Type 'I have HTTPS' to confirm encrypted transport" ''
  if [[ "$REPLY_VALUE" != 'I have HTTPS' ]]; then
    rc_die 'HTTPS acknowledgement did not match'
    exit 1
  fi
else
  prompt_value "Type 'I understand LAN HTTP is unencrypted' to continue" ''
  if [[ "$REPLY_VALUE" != 'I understand LAN HTTP is unencrypted' ]]; then
    rc_die 'trusted LAN HTTP acknowledgement did not match'
    exit 1
  fi
fi

install_parent="$(dirname -- "$RC_INSTALL_ROOT")"
mkdir -p -- "$install_parent"
WORK_ROOT="$(mktemp -d "$install_parent/.reachcommander-install.XXXXXX")"
REQUEST_PATH="$WORK_ROOT/request.json"
STAGE_ROOT="$WORK_ROOT/deployment"

write_request "$REQUEST_PATH" "$RC_IMAGE_REPOSITORY:$INSTALL_CHANNEL"
python3 "$RENDERER" render \
  --request "$REQUEST_PATH" \
  --template "$COMPOSE_TEMPLATE" \
  --output "$STAGE_ROOT"
install -m 0600 -- "$UPDATER_COMPOSE_TEMPLATE" "$STAGE_ROOT/compose.override.yaml"
docker compose --project-directory "$STAGE_ROOT" config --quiet

RESOLVED_IMAGE="$(rc_pull_digest "$INSTALL_CHANNEL")"
RESOLVED_VERSION="$(rc_image_display_version "$RESOLVED_IMAGE" "$INSTALL_CHANNEL")"
if [[ "$INSTALL_CHANNEL" == 'stable' && "$RESOLVED_VERSION" != "$BUNDLE_VERSION" ]]; then
  rc_die 'installer bundle version does not match the trusted image label'
  exit 1
fi
python3 "$RENDERER" set-image --env "$STAGE_ROOT/.env" --image "$RESOLVED_IMAGE"
mkdir -p -- "$STAGE_ROOT/bin" "$STAGE_ROOT/lib"
install -m 0755 -- "$RENDERER" "$STAGE_ROOT/bin/render_config.py"
install -m 0755 -- "$UPDATER_SERVICE" "$STAGE_ROOT/bin/updater_service.py"
install -m 0600 -- "$COMMON_LIBRARY" "$STAGE_ROOT/lib/common.sh"
install -m 0644 -- "$UPDATER_PROTOCOL" "$STAGE_ROOT/lib/updater_protocol.py"
rc_atomic_write "$STAGE_ROOT/state/channel" "$INSTALL_CHANNEL"$'\n'
rc_atomic_write "$STAGE_ROOT/state/current-image" "$RESOLVED_IMAGE"$'\n'
rc_atomic_write "$STAGE_ROOT/state/previous-image" ''
rc_atomic_write "$STAGE_ROOT/state/current-version" "$RESOLVED_VERSION"$'\n'
rc_atomic_write "$STAGE_ROOT/state/previous-version" ''
docker compose --project-directory "$STAGE_ROOT" config --quiet

if [[ "$HAD_EXISTING" == 'true' ]]; then
  begin_reconfiguration_transaction
else
  INSTALL_TRANSACTION_ACTIVE=true
fi

if ! install_staged_deployment "$STAGE_ROOT" || ! prepare_application_data; then
  if [[ "$HAD_EXISTING" == 'true' ]]; then
    if rollback_reconfiguration_transaction true; then
      rc_die 'reconfiguration write failed; the previous deployment was restored'
      exit 2
    fi
    rc_die 'reconfiguration write failed and rollback requires manual recovery'
    exit 3
  fi
  remove_partial_initial_deployment
  rc_die 'initial deployment files could not be installed'
  exit 1
fi

if ! install_updater_service; then
  if [[ "$HAD_EXISTING" == 'true' ]]; then
    if rollback_reconfiguration_transaction true; then
      rc_die 'updater service failed to start; the previous deployment was restored'
      exit 2
    fi
    rc_die 'updater service failed to start and rollback requires manual recovery'
    exit 3
  fi
  remove_initial_updater_service || true
  remove_partial_initial_deployment
  rc_die 'updater service failed to start; initial deployment was removed'
  exit 1
fi
INSTALL_COMMITTED=true

if ! rc_compose up -d reachcommander || ! rc_wait_healthy reachcommander 60; then
  failure_status=0
  if [[ "$HAD_EXISTING" == 'false' ]]; then
    INSTALL_TRANSACTION_ACTIVE=false
  fi
  handle_start_failure "$HAD_EXISTING" || failure_status=$?
  exit "$failure_status"
fi

if [[ "$HAD_EXISTING" == 'true' ]]; then
  complete_reconfiguration_transaction
else
  INSTALL_TRANSACTION_ACTIVE=false
fi

if [[ "$ACCESS_MODE" == 'trusted-lan-http' ]]; then
  printf 'ReachCommander is ready on the trusted LAN:\n'
  detected_addresses=''
  if detected_addresses="$(python3 "$LAN_ADDRESS_HELPER" 2>/dev/null)"; then
    while IFS= read -r detected_address; do
      [[ -n "$detected_address" ]] || continue
      printf '  http://%s:%s\n' "$detected_address" "$PORT"
    done <<<"$detected_addresses"
  fi
  printf '  http://<server-lan-ip>:%s\n' "$PORT"
  printf 'Trusted LAN HTTP is unencrypted and listens on every host interface.\n'
else
  printf 'ReachCommander is healthy at http://127.0.0.1:%s\n' "$PORT"
  printf 'Publish this endpoint through an HTTPS reverse proxy; proxy authentication is optional.\n'
fi
printf 'Run reachcommander doctor to verify the deployment.\n'
