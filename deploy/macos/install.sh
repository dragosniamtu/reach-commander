#!/bin/bash
set -Eeuo pipefail
umask 077

RC_IMAGE_REPOSITORY='ghcr.io/dragosniamtu/reach-commander'
RC_TEMPLATE_URL='https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/compose.release.yaml'
RC_INSTALL_ROOT=''
RC_USER_HOME=''
RC_LOCK_DIRECTORY=''
RC_LOCK_OWNED=false
RC_BIND_ADDRESS='127.0.0.1'
RC_PORT='8080'
RC_CURRENT_PORT=''
RC_RECONFIGURING=false
RC_TRANSACTION_ROOT=''
RC_WORK_ROOT=''
RC_INSTALLED_IMAGE=''
RC_CURRENT_IMAGE=''
RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()
RC_GENERATED_FILES=(
  '.env'
  'compose.yaml'
  'config/sources.json'
  'state/source-mounts.json'
  'state/channel'
  'state/current-image'
  'state/previous-image'
)

rc_die() { printf 'ReachCommander: %s\n' "$1" >&2; }

rc_init_paths() {
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]]; then
    RC_INSTALL_ROOT="${REACHCOMMANDER_TEST_INSTALL_ROOT:?}"
    RC_USER_HOME="${REACHCOMMANDER_TEST_USER_HOME:?}"
  else
    [[ "$(uname -s)" == 'Darwin' ]] ||
      { rc_die 'this installer supports macOS only'; return 1; }
    RC_USER_HOME="${HOME:?}"
    RC_INSTALL_ROOT="$RC_USER_HOME/Library/Application Support/ReachCommander"
  fi
  [[ "$RC_USER_HOME" == /* && "$RC_INSTALL_ROOT" == /* ]] ||
    { rc_die 'installer paths must be absolute'; return 1; }
  RC_LOCK_DIRECTORY="$RC_INSTALL_ROOT/state/install.lock"
}

rc_validate_port() {
  local value="${1:-}"
  if [[ ! "$value" =~ ^[0-9]+$ ]]; then
    rc_die 'port must be an integer from 1 through 65535'
    return 1
  fi
  if (( 10#$value < 1 || 10#$value > 65535 )); then
    rc_die 'port must be an integer from 1 through 65535'
    return 1
  fi
}

rc_validate_architecture() {
  case "${1:-}" in
    x86_64 | arm64) return 0 ;;
    *) rc_die 'Docker Desktop must run on an Intel or Apple Silicon Mac'; return 1 ;;
  esac
}

rc_normalize_source_id() {
  local value
  value="$(
    printf '%s' "${1:-}" |
      LC_ALL=C tr '[:upper:]' '[:lower:]' |
      LC_ALL=C sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//' |
      LC_ALL=C cut -c 1-64
  )"
  [[ "$value" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]] ||
    { rc_die 'source name cannot produce a safe source identifier'; return 1; }
  printf '%s\n' "$value"
}

rc_canonical_directory() (
  local value="${1:-}"
  [[ -n "$value" && -d "$value" ]] ||
    { rc_die 'source path must be an existing directory'; return 1; }
  case "$value" in
    *$'\n'* | *$'\r'* | *$'\t'*)
      rc_die 'source paths cannot contain control characters'
      return 1
      ;;
  esac
  CDPATH='' cd -P -- "$value" >/dev/null 2>&1 ||
    { rc_die 'source path cannot be resolved'; return 1; }
  pwd -P
)

rc_path_relation() {
  local left="${1%/}"
  local right="${2%/}"
  if [[ "$left" == "$right" ]]; then
    printf 'same\n'
  elif [[ "$right" == "$left"/* ]]; then
    printf 'ancestor\n'
  elif [[ "$left" == "$right"/* ]]; then
    printf 'inside\n'
  else
    printf 'disjoint\n'
  fi
}

rc_validate_source_path() {
  local canonical
  local canonical_install_root
  local relation
  canonical="$(rc_canonical_directory "$1")" || return 1
  canonical_install_root="$(rc_canonical_directory "$RC_INSTALL_ROOT")" || return 1
  case "$canonical" in
    / | /System | /System/* | /Library | /Library/* | /private | /private/* | \
      /usr | /usr/* | /bin | /bin/* | /sbin | /sbin/* | /dev | /dev/*)
      rc_die 'source path resolves to a protected macOS location'
      return 1
      ;;
  esac
  relation="$(rc_path_relation "$canonical" "$canonical_install_root")"
  [[ "$relation" != 'same' && "$relation" != 'inside' ]] ||
    { rc_die 'source path cannot be the installer directory or one of its children'; return 1; }
}

rc_yaml_quote() {
  local escaped
  escaped="$(printf '%s' "$1" | sed "s/'/''/g")"
  printf "'%s'" "$escaped"
}

rc_add_source() {
  local source_id="$1"
  local source_name="$2"
  local source_path="$3"
  local source_access="$4"
  local canonical
  local existing
  local index=0
  local relation
  [[ "$source_id" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]] ||
    { rc_die 'source identifier is invalid'; return 1; }
  [[ -n "$source_name" && ${#source_name} -le 100 ]] ||
    { rc_die 'source name must contain 1 through 100 characters'; return 1; }
  case "$source_name" in
    *$'\n'* | *$'\r'* | *$'\t'*)
      rc_die 'source name cannot contain control characters'
      return 1
      ;;
  esac
  [[ "$source_access" == 'ro' || "$source_access" == 'rw' ]] ||
    { rc_die 'source access must be ro or rw'; return 1; }
  canonical="$(rc_canonical_directory "$source_path")" || return 1
  rc_validate_source_path "$canonical" || return 1
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    existing="${RC_SOURCE_IDS[$index]}"
    [[ "$existing" != "$source_id" ]] ||
      { rc_die 'source identifier is already in use'; return 1; }
    relation="$(rc_path_relation "$canonical" "${RC_SOURCE_PATHS[$index]}")"
    [[ "$relation" == 'disjoint' ]] ||
      { rc_die 'source paths cannot duplicate or contain one another'; return 1; }
    index=$((index + 1))
  done
  RC_SOURCE_IDS[${#RC_SOURCE_IDS[@]}]="$source_id"
  RC_SOURCE_NAMES[${#RC_SOURCE_NAMES[@]}]="$source_name"
  RC_SOURCE_PATHS[${#RC_SOURCE_PATHS[@]}]="$canonical"
  RC_SOURCE_ACCESS[${#RC_SOURCE_ACCESS[@]}]="$source_access"
}

rc_render_json() {
  local output="$1"
  local mounts_output="$2"
  local plist="$3"
  local mounts_plist="$4"
  local count="${#RC_SOURCE_IDS[@]}"
  local index=0
  local default_right=0
  local read_only
  local default_left
  local default_right_value
  (( count > 1 )) && default_right=1
  (( count > 0 )) || { rc_die 'at least one source is required'; return 1; }

  plutil -create xml1 "$plist" || return 1
  plutil -insert sources -array "$plist" || return 1
  plutil -create xml1 "$mounts_plist" || return 1
  plutil -insert sources -array "$mounts_plist" || return 1
  while (( index < count )); do
    read_only=false
    default_left=false
    default_right_value=false
    [[ "${RC_SOURCE_ACCESS[$index]}" == ro ]] && read_only=true
    (( index == 0 )) && default_left=true
    (( index == default_right )) && default_right_value=true

    plutil -insert "sources.$index" -dictionary "$plist" || return 1
    plutil -insert "sources.$index.id" -string "${RC_SOURCE_IDS[$index]}" "$plist" || return 1
    plutil -insert "sources.$index.name" -string "${RC_SOURCE_NAMES[$index]}" "$plist" || return 1
    plutil -insert "sources.$index.path" -string "/sources/${RC_SOURCE_IDS[$index]}" "$plist" || return 1
    plutil -insert "sources.$index.enabled" -bool true "$plist" || return 1
    plutil -insert "sources.$index.readOnly" -bool "$read_only" "$plist" || return 1
    plutil -insert "sources.$index.defaultLeft" -bool "$default_left" "$plist" || return 1
    plutil -insert "sources.$index.defaultRight" -bool "$default_right_value" "$plist" || return 1

    plutil -insert "sources.$index" -dictionary "$mounts_plist" || return 1
    plutil -insert "sources.$index.id" -string "${RC_SOURCE_IDS[$index]}" "$mounts_plist" || return 1
    plutil -insert "sources.$index.hostPath" -string "${RC_SOURCE_PATHS[$index]}" "$mounts_plist" || return 1
    plutil -insert "sources.$index.access" -string "${RC_SOURCE_ACCESS[$index]}" "$mounts_plist" || return 1
    index=$((index + 1))
  done
  plutil -convert json -o "$output" "$plist" || return 1
  plutil -convert json -o "$mounts_output" "$mounts_plist" || return 1
}

rc_render_compose() {
  local template="$1"
  local output="$2"
  local mounts="$3"
  local marker='      # installer-source-mounts'
  local index=0
  local relation
  local relative
  local target
  [[ "$(grep -Fxc "$marker" "$template")" == '1' ]] ||
    { rc_die 'Compose template must contain exactly one source marker'; return 1; }
  : >"$mounts"
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    {
      printf '      - type: bind\n'
      printf '        source: %s\n' "$(rc_yaml_quote "${RC_SOURCE_PATHS[$index]}")"
      printf '        target: %s\n' "$(rc_yaml_quote "/sources/${RC_SOURCE_IDS[$index]}")"
      printf '        read_only: %s\n' \
        "$([[ "${RC_SOURCE_ACCESS[$index]}" == ro ]] && printf true || printf false)"
    } >>"$mounts"
    relation="$(rc_path_relation "${RC_SOURCE_PATHS[$index]}" "$RC_INSTALL_ROOT")"
    if [[ "$relation" == 'ancestor' ]]; then
      relative="${RC_INSTALL_ROOT#"${RC_SOURCE_PATHS[$index]}"/}"
      target="/sources/${RC_SOURCE_IDS[$index]}/$relative"
      {
        printf '      - type: bind\n'
        printf "        source: './excluded'\n"
        printf '        target: %s\n' "$(rc_yaml_quote "$target")"
        printf '        read_only: true\n'
      } >>"$mounts"
    fi
    index=$((index + 1))
  done
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == "$marker" ]]; then
      command cat -- "$mounts"
    else
      printf '%s\n' "$line"
    fi
  done <"$template" >"$output"
}

rc_render_deployment() {
  local output="$1"
  local template="$2"
  local image="$3"
  local bind_address="$4"
  local port="$5"
  local uid="$6"
  local gid="$7"
  local temporary_plist
  local temporary_mounts_plist
  local mounts
  [[ "$image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]] ||
    { rc_die 'image must be an immutable trusted ReachCommander digest'; return 1; }
  [[ "$bind_address" == '127.0.0.1' || "$bind_address" == '0.0.0.0' ]] ||
    { rc_die 'bind address must be local-only or local-network'; return 1; }
  rc_validate_port "$port" || return 1
  [[ "$uid" =~ ^[0-9]+$ && "$gid" =~ ^[0-9]+$ ]] ||
    { rc_die 'UID and GID must be numeric'; return 1; }
  [[ -f "$template" && ! -L "$template" ]] ||
    { rc_die 'Compose template is missing or unsafe'; return 1; }
  if [[ -e "$output" ]]; then
    [[ -d "$output" && ! -L "$output" ]] ||
      { rc_die 'deployment output must be a real directory'; return 1; }
    [[ -z "$(find "$output" -mindepth 1 -print -quit)" ]] ||
      { rc_die 'deployment output directory must be empty'; return 1; }
  else
    mkdir -p -- "$output" || return 1
  fi

  mkdir -p -- \
    "$output/config" \
    "$output/data/auth" \
    "$output/data/keys" \
    "$output/state" \
    "$output/backups" \
    "$output/excluded" || return 1
  chmod 0700 "$output" "$output/data" "$output/data/auth" \
    "$output/data/keys" "$output/state" "$output/backups" || return 1
  chmod 0755 "$output/config" || return 1
  chmod 0555 "$output/excluded" || return 1

  printf '%s\n' \
    "REACHCOMMANDER_BIND_ADDRESS=$bind_address" \
    "REACHCOMMANDER_PORT=$port" \
    "REACHCOMMANDER_UID=$uid" \
    "REACHCOMMANDER_GID=$gid" \
    "REACHCOMMANDER_IMAGE=$image" >"$output/.env" || return 1
  chmod 0600 "$output/.env" || return 1

  temporary_plist="$output/state/sources.plist"
  temporary_mounts_plist="$output/state/source-mounts.plist"
  mounts="$output/state/compose-mounts.yaml"
  rc_render_json \
    "$output/config/sources.json" \
    "$output/state/source-mounts.json" \
    "$temporary_plist" \
    "$temporary_mounts_plist" || return 1
  rc_render_compose "$template" "$output/compose.yaml" "$mounts" || return 1
  rm -f -- "$temporary_plist" "$temporary_mounts_plist" "$mounts"

  printf 'stable\n' >"$output/state/channel"
  printf '%s\n' "$image" >"$output/state/current-image"
  : >"$output/state/previous-image"
  chmod 0644 "$output/config/sources.json" || return 1
  chmod 0600 \
    "$output/compose.yaml" \
    "$output/state/source-mounts.json" \
    "$output/state/channel" \
    "$output/state/current-image" \
    "$output/state/previous-image" || return 1
}

rc_prompt_value() {
  local prompt="$1"
  local default_value="${2:-}"
  local value=''
  if [[ -n "$default_value" ]]; then
    printf '%s [%s]: ' "$prompt" "$default_value" >&2
  else
    printf '%s: ' "$prompt" >&2
  fi
  IFS= read -r value || return 1
  [[ -n "$value" ]] || value="$default_value"
  printf '%s\n' "$value"
}

rc_prompt_access() {
  local label="$1"
  local choice
  while true; do
    printf '\nAccess for %s:\n1. Read-only (Recommended)\n2. Read/write\n' "$label" >&2
    choice="$(rc_prompt_value 'Choose access' '1')" || return 1
    case "$choice" in
      1 | ro | RO) printf 'ro\n'; return 0 ;;
      2 | rw | RW) printf 'rw\n'; return 0 ;;
      *) rc_die 'choose 1 for read-only or 2 for read/write' ;;
    esac
  done
}

rc_source_id_in_use() {
  local candidate="$1"
  local index=0
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    [[ "${RC_SOURCE_IDS[$index]}" != "$candidate" ]] || return 0
    index=$((index + 1))
  done
  return 1
}

rc_unique_source_id() {
  local base
  local candidate
  local suffix=2
  base="$(rc_normalize_source_id "$1")" || return 1
  candidate="$base"
  while rc_source_id_in_use "$candidate"; do
    candidate="${base:0:$((63 - ${#suffix}))}-$suffix"
    suffix=$((suffix + 1))
  done
  printf '%s\n' "$candidate"
}

rc_expand_user_path() {
  local value="$1"
  case "$value" in
    '~') printf '%s\n' "$RC_USER_HOME" ;;
    \~/*) printf '%s/%s\n' "$RC_USER_HOME" "${value#\~/}" ;;
    *) printf '%s\n' "$value" ;;
  esac
}

rc_collect_specific_folders() {
  local entered
  local expanded
  local canonical
  local default_name
  local source_name
  local source_id
  local source_access
  while true; do
    entered="$(rc_prompt_value 'Folder path (leave blank when finished)' '')" || return 1
    if [[ -z "$entered" ]]; then
      (( ${#RC_SOURCE_IDS[@]} > 0 )) && return 0
      rc_die 'add at least one source folder'
      continue
    fi
    expanded="$(rc_expand_user_path "$entered")"
    canonical="$(rc_canonical_directory "$expanded")" || continue
    rc_validate_source_path "$canonical" || continue
    default_name="${canonical##*/}"
    source_name="$(rc_prompt_value 'Display name' "$default_name")" || return 1
    source_id="$(rc_unique_source_id "$source_name")" || continue
    source_access="$(rc_prompt_access "$source_name")" || return 1
    rc_add_source "$source_id" "$source_name" "$canonical" "$source_access" || continue
  done
}

rc_discover_whole_drives() {
  local canonical_home
  local candidate
  local canonical
  canonical_home="$(rc_canonical_directory "$RC_USER_HOME")" || return 1
  printf '%s\n' "$canonical_home"
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' && -n "${REACHCOMMANDER_TEST_VOLUMES_FILE:-}" ]]; then
    while IFS= read -r candidate || [[ -n "$candidate" ]]; do
      [[ -n "$candidate" ]] || continue
      canonical="$(rc_canonical_directory "$candidate")" || continue
      [[ "$canonical" != '/' && "$canonical" != "$canonical_home" ]] || continue
      rc_validate_source_path "$canonical" >/dev/null 2>&1 || continue
      printf '%s\n' "$canonical"
    done <"$REACHCOMMANDER_TEST_VOLUMES_FILE"
  else
    while IFS= read -r -d '' candidate; do
      canonical="$(rc_canonical_directory "$candidate")" || continue
      [[ "$canonical" != '/' && "$canonical" != "$canonical_home" ]] || continue
      rc_validate_source_path "$canonical" >/dev/null 2>&1 || continue
      printf '%s\n' "$canonical"
    done < <(find /Volumes -mindepth 1 -maxdepth 1 -type d -print0 2>/dev/null)
  fi
}

rc_selection_contains() {
  local wanted="$1"
  shift
  local selected
  for selected in "$@"; do
    [[ "$selected" != "$wanted" ]] || return 0
  done
  return 1
}

rc_collect_whole_drives() {
  local available=()
  local selections=()
  local candidate
  local choice
  local selected
  local index
  local source_path
  local source_name
  local source_id
  local source_access
  local confirmation
  while IFS= read -r candidate; do
    [[ -n "$candidate" ]] && available[${#available[@]}]="$candidate"
  done < <(rc_discover_whole_drives)
  (( ${#available[@]} > 0 )) || { rc_die 'no eligible drives were found'; return 1; }

  printf '\nAvailable drives:\n' >&2
  index=0
  while (( index < ${#available[@]} )); do
    printf '%d. %s\n' "$((index + 1))" "${available[$index]}" >&2
    index=$((index + 1))
  done
  choice="$(rc_prompt_value 'Choose one or more drive numbers (space-separated)' '')" || return 1
  for selected in $choice; do
    if [[ ! "$selected" =~ ^[1-9][0-9]*$ ]] || (( selected > ${#available[@]} )); then
      rc_die 'drive selections must be valid menu numbers'
      return 1
    fi
    rc_selection_contains "$selected" "${selections[@]:-}" ||
      selections[${#selections[@]}]="$selected"
  done
  (( ${#selections[@]} > 0 )) || { rc_die 'select at least one drive'; return 1; }

  for selected in "${selections[@]}"; do
    source_path="${available[$((selected - 1))]}"
    if [[ "$source_path" == "$RC_USER_HOME" ]]; then
      source_name='Home'
    else
      source_name="${source_path##*/}"
    fi
    source_id="$(rc_unique_source_id "$source_name")" || return 1
    source_access="$(rc_prompt_access "$source_name")" || return 1
    if [[ "$source_access" == 'rw' ]]; then
      printf 'Read/write access to a whole drive can modify or delete many files.\n' >&2
      confirmation="$(rc_prompt_value "Type the exact path '$source_path' to continue" '')" || return 1
      [[ "$confirmation" == "$source_path" ]] ||
        { rc_die 'whole-drive read/write confirmation did not match'; return 1; }
    fi
    rc_add_source "$source_id" "$source_name" "$source_path" "$source_access" || return 1
  done
}

rc_collect_sources() {
  local choice
  RC_SOURCE_IDS=()
  RC_SOURCE_NAMES=()
  RC_SOURCE_PATHS=()
  RC_SOURCE_ACCESS=()
  while true; do
    printf '\nWhat should ReachCommander access?\n1. Whole drives\n2. Specific folders (Recommended)\n' >&2
    choice="$(rc_prompt_value 'Choose source mode' '2')" || return 1
    case "$choice" in
      1) rc_collect_whole_drives; return ;;
      2) rc_collect_specific_folders; return ;;
      *) rc_die 'choose 1 for whole drives or 2 for specific folders' ;;
    esac
  done
}

rc_port_in_use() {
  lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1
}

rc_collect_network() {
  local choice
  local requested_port
  while true; do
    printf '\nWho can access ReachCommander?\n1. This Mac only (Recommended)\n2. Devices on the local network\n' >&2
    choice="$(rc_prompt_value 'Choose network access' '1')" || return 1
    case "$choice" in
      1) RC_BIND_ADDRESS='127.0.0.1'; break ;;
      2) RC_BIND_ADDRESS='0.0.0.0'; break ;;
      *) rc_die 'choose 1 for this Mac or 2 for the local network' ;;
    esac
  done
  while true; do
    requested_port="$(rc_prompt_value 'Host port' '8080')" || return 1
    rc_validate_port "$requested_port" || continue
    if rc_port_in_use "$requested_port"; then
      if [[ "$RC_RECONFIGURING" == 'true' && "$requested_port" == "$RC_CURRENT_PORT" ]]; then
        RC_PORT="$requested_port"
        return 0
      fi
      rc_die "port $requested_port is already in use"
      continue
    fi
    RC_PORT="$requested_port"
    return 0
  done
}

rc_local_ip_address() {
  local interface=''
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' && -n "${REACHCOMMANDER_TEST_LOCAL_IP:-}" ]]; then
    printf '%s\n' "$REACHCOMMANDER_TEST_LOCAL_IP"
    return 0
  fi
  interface="$(route -n get default 2>/dev/null | sed -n 's/^[[:space:]]*interface:[[:space:]]*//p' | sed -n '1p')"
  [[ -n "$interface" ]] || return 1
  ipconfig getifaddr "$interface"
}

rc_preflight() {
  local architecture
  local command_name
  architecture="$(uname -m)"
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' && -n "${REACHCOMMANDER_TEST_ARCHITECTURE:-}" ]]; then
    architecture="$REACHCOMMANDER_TEST_ARCHITECTURE"
  fi
  rc_validate_architecture "$architecture" || return 1
  for command_name in \
    curl docker plutil lsof find grep sed tr cut mktemp chmod \
    id basename dirname route ipconfig cp mv rm rmdir cat sleep stat; do
    command -v "$command_name" >/dev/null 2>&1 ||
      { rc_die "required command is missing: $command_name"; return 1; }
  done
  docker info >/dev/null 2>&1 ||
    { rc_die 'Docker Desktop is not installed or is not running; install/start it and rerun'; return 1; }
  docker compose version >/dev/null 2>&1 ||
    { rc_die 'Docker Compose v2 is required'; return 1; }
}

rc_device_id() {
  if [[ "$(uname -s)" == 'Darwin' ]]; then
    stat -f '%d' "$1"
  else
    stat -c '%d' "$1"
  fi
}

rc_require_real_directory() {
  local path="$1"
  local mode="$2"
  if [[ -e "$path" || -L "$path" ]]; then
    [[ -d "$path" && ! -L "$path" ]] ||
      { rc_die 'installer directory layout is unsafe'; return 1; }
  else
    mkdir -- "$path" || return 1
  fi
  chmod "$mode" "$path" || return 1
}

rc_prepare_installer_root() {
  if [[ -e "$RC_INSTALL_ROOT" || -L "$RC_INSTALL_ROOT" ]]; then
    [[ -d "$RC_INSTALL_ROOT" && ! -L "$RC_INSTALL_ROOT" ]] ||
      { rc_die 'installer root must be a real directory'; return 1; }
  else
    mkdir -p -- "$RC_INSTALL_ROOT" || return 1
  fi
  chmod 0700 "$RC_INSTALL_ROOT" || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/config" 0755 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/data" 0700 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/data/auth" 0700 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/data/keys" 0700 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/state" 0700 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/backups" 0700 || return 1
  rc_require_real_directory "$RC_INSTALL_ROOT/excluded" 0555 || return 1
}

rc_validate_authentication_data_tree() {
  local data_root="$RC_INSTALL_ROOT/data"
  local data_device
  local installer_device
  local entry
  local relative
  local directory_device
  [[ -d "$data_root" && ! -L "$data_root" ]] ||
    { rc_die 'authentication data root is unsafe'; return 1; }
  installer_device="$(rc_device_id "$RC_INSTALL_ROOT")" || return 1
  data_device="$(rc_device_id "$data_root")" || return 1
  [[ "$data_device" == "$installer_device" ]] ||
    { rc_die 'authentication data cannot be a separate mount'; return 1; }
  for entry in "$data_root/auth" "$data_root/keys"; do
    [[ -d "$entry" && ! -L "$entry" ]] ||
      { rc_die 'authentication data directory is unsafe'; return 1; }
    directory_device="$(rc_device_id "$entry")" || return 1
    [[ "$directory_device" == "$data_device" ]] ||
      { rc_die 'authentication data cannot be a separate mount'; return 1; }
  done
  while IFS= read -r -d '' entry; do
    relative="${entry#"$data_root"/}"
    [[ ! -L "$entry" ]] ||
      { rc_die 'authentication data contains a symbolic link'; return 1; }
    case "$relative" in
      auth | keys)
        [[ -d "$entry" ]] ||
          { rc_die 'authentication data directory has an invalid type'; return 1; }
        ;;
      auth/account.json | auth/bootstrap.json | auth/auth.lock)
        [[ -f "$entry" ]] ||
          { rc_die 'authentication state has an invalid type'; return 1; }
        ;;
      keys/key-*.xml)
        [[ -f "$entry" ]] ||
          { rc_die 'Data Protection key has an invalid type'; return 1; }
        ;;
      *)
        rc_die 'authentication data contains an unexpected entry'
        return 1
        ;;
    esac
  done < <(find "$data_root" -mindepth 1 -maxdepth 2 -print0)
}

rc_fetch_template() {
  local destination="$1"
  local url="$RC_TEMPLATE_URL"
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]]; then
    url="${REACHCOMMANDER_TEST_TEMPLATE_URL:?}"
  fi
  curl --fail --show-error --silent --location \
    --proto '=https' --tlsv1.2 --output "$destination" "$url" ||
    { rc_die 'cannot download the ReachCommander Compose template'; return 1; }
  [[ -f "$destination" && ! -L "$destination" ]] ||
    { rc_die 'downloaded Compose template is unsafe'; return 1; }
  [[ "$(grep -Fxc '      # installer-source-mounts' "$destination")" == '1' ]] ||
    { rc_die 'downloaded Compose template is invalid'; return 1; }
}

rc_pull_digest() {
  local reference="$RC_IMAGE_REPOSITORY:${1:-stable}"
  local output
  local line
  local selected=''
  docker pull "$reference" >/dev/null ||
    { rc_die 'ReachCommander image pull failed; check network and GHCR visibility'; return 1; }
  output="$(docker image inspect \
    --format '{{range .RepoDigests}}{{println .}}{{end}}' "$reference")" || return 1
  while IFS= read -r line; do
    if [[ "$line" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]]; then
      [[ -z "$selected" || "$selected" == "$line" ]] ||
        { rc_die 'image inspection returned conflicting trusted digests'; return 1; }
      selected="$line"
    fi
  done <<<"$output"
  [[ -n "$selected" ]] ||
    { rc_die 'image inspection returned no trusted ReachCommander digest'; return 1; }
  printf '%s\n' "$selected"
}

rc_compose() {
  local root="$1"
  local project='reachcommander-preflight'
  shift
  [[ "$root" == "$RC_INSTALL_ROOT" ]] && project='reachcommander'
  docker compose --project-name "$project" \
    --project-directory "$root" --file "$root/compose.yaml" "$@"
}

rc_wait_healthy() {
  local root="$1"
  local timeout="$2"
  local container
  local status
  local deadline=$((SECONDS + timeout))
  container="$(rc_compose "$root" ps -q reachcommander)"
  [[ -n "$container" ]] ||
    { rc_die 'ReachCommander container was not created'; return 1; }
  while (( SECONDS <= deadline )); do
    status="$(docker inspect \
      --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' \
      "$container")" || return 1
    case "$status" in
      healthy) return 0 ;;
      unhealthy | missing)
        rc_die 'ReachCommander container is unhealthy'
        return 1
        ;;
      starting | created | restarting) ;;
      *)
        rc_die 'ReachCommander returned an invalid health status'
        return 1
        ;;
    esac
    sleep 1
  done
  rc_die 'ReachCommander health check timed out'
  return 1
}

rc_preflight_sources() {
  local root="$1"
  local index=0
  local target
  local mode
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    target="/sources/${RC_SOURCE_IDS[$index]}"
    mode="${RC_SOURCE_ACCESS[$index]}"
    if [[ "$mode" == 'rw' ]]; then
      # $1 is expanded by the container shell.
      # shellcheck disable=SC2016
      rc_compose "$root" run --rm --no-deps --entrypoint /bin/sh reachcommander \
        -c 'test -r "$1" && test -x "$1" && test -w "$1"' \
        reachcommander-probe "$target" ||
        { rc_die "Docker Desktop cannot use the selected read/write source: ${RC_SOURCE_PATHS[$index]}"; return 1; }
    else
      # $1 is expanded by the container shell.
      # shellcheck disable=SC2016
      rc_compose "$root" run --rm --no-deps --entrypoint /bin/sh reachcommander \
        -c 'test -r "$1" && test -x "$1"' \
        reachcommander-probe "$target" ||
        { rc_die "Docker Desktop cannot use the selected read-only source: ${RC_SOURCE_PATHS[$index]}"; return 1; }
    fi
    index=$((index + 1))
  done
}

rc_begin_generated_transaction() {
  local relative
  RC_TRANSACTION_ROOT="$RC_INSTALL_ROOT/backups/.install-transaction"
  [[ ! -e "$RC_TRANSACTION_ROOT" && ! -L "$RC_TRANSACTION_ROOT" ]] ||
    { rc_die 'an installer transaction already exists'; return 1; }
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -e "$RC_INSTALL_ROOT/$relative" || -L "$RC_INSTALL_ROOT/$relative" ]]; then
      [[ -f "$RC_INSTALL_ROOT/$relative" && ! -L "$RC_INSTALL_ROOT/$relative" ]] ||
        { rc_die "generated path is unsafe: $relative"; return 1; }
    fi
  done
  mkdir -p -- "$RC_TRANSACTION_ROOT/files/config" "$RC_TRANSACTION_ROOT/files/state"
  mkdir -p -- "$RC_TRANSACTION_ROOT/absent/config" "$RC_TRANSACTION_ROOT/absent/state"
  chmod 0700 "$RC_TRANSACTION_ROOT" "$RC_TRANSACTION_ROOT/files" \
    "$RC_TRANSACTION_ROOT/files/config" "$RC_TRANSACTION_ROOT/files/state" \
    "$RC_TRANSACTION_ROOT/absent" "$RC_TRANSACTION_ROOT/absent/config" \
    "$RC_TRANSACTION_ROOT/absent/state"
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -f "$RC_INSTALL_ROOT/$relative" ]]; then
      cp -p -- "$RC_INSTALL_ROOT/$relative" "$RC_TRANSACTION_ROOT/files/$relative" || return 1
    else
      : >"$RC_TRANSACTION_ROOT/absent/$relative" || return 1
      chmod 0600 "$RC_TRANSACTION_ROOT/absent/$relative" || return 1
    fi
  done
  printf 'active\n' >"$RC_INSTALL_ROOT/state/transaction-active"
  chmod 0600 "$RC_INSTALL_ROOT/state/transaction-active"
}

rc_replace_generated_file() {
  local source="$1"
  local destination="$2"
  local directory
  local temporary
  directory="$(dirname -- "$destination")"
  [[ -d "$directory" && ! -L "$directory" ]] ||
    { rc_die 'generated destination directory is unsafe'; return 1; }
  temporary="$(mktemp "$directory/.reachcommander-write.XXXXXX")" || return 1
  if ! cp -p -- "$source" "$temporary"; then
    rm -f -- "$temporary"
    return 1
  fi
  mv -f -- "$temporary" "$destination"
}

rc_commit_generated() {
  local stage="$1"
  local relative
  for relative in "${RC_GENERATED_FILES[@]}"; do
    [[ -f "$stage/$relative" && ! -L "$stage/$relative" ]] ||
      { rc_die "staged generated file is missing: $relative"; return 1; }
  done
  rc_begin_generated_transaction || return 1
  for relative in "${RC_GENERATED_FILES[@]}"; do
    rc_replace_generated_file "$stage/$relative" "$RC_INSTALL_ROOT/$relative" || return 1
  done
}

rc_restore_generated_files() {
  local relative
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -f "$RC_TRANSACTION_ROOT/files/$relative" && ! -L "$RC_TRANSACTION_ROOT/files/$relative" ]]; then
      rc_replace_generated_file \
        "$RC_TRANSACTION_ROOT/files/$relative" "$RC_INSTALL_ROOT/$relative" || return 1
    elif [[ -f "$RC_TRANSACTION_ROOT/absent/$relative" && ! -L "$RC_TRANSACTION_ROOT/absent/$relative" ]]; then
      rm -f -- "$RC_INSTALL_ROOT/$relative"
    else
      rc_die "transaction backup is incomplete: $relative"
      return 1
    fi
  done
}

rc_complete_transaction() {
  local relative
  [[ -n "$RC_TRANSACTION_ROOT" ]] || return 0
  for relative in "${RC_GENERATED_FILES[@]}"; do
    rm -f -- "$RC_TRANSACTION_ROOT/files/$relative" "$RC_TRANSACTION_ROOT/absent/$relative"
  done
  rm -f -- "$RC_INSTALL_ROOT/state/transaction-active"
  rmdir -- \
    "$RC_TRANSACTION_ROOT/files/config" \
    "$RC_TRANSACTION_ROOT/files/state" \
    "$RC_TRANSACTION_ROOT/absent/config" \
    "$RC_TRANSACTION_ROOT/absent/state" \
    "$RC_TRANSACTION_ROOT/files" \
    "$RC_TRANSACTION_ROOT/absent" \
    "$RC_TRANSACTION_ROOT" ||
    { rc_die 'transaction journal contains unexpected entries'; return 1; }
  RC_TRANSACTION_ROOT=''
}

rc_rollback_generated() {
  rc_restore_generated_files || return 1
  rc_complete_transaction
}

rc_recover_transaction() {
  local marker="$RC_INSTALL_ROOT/state/transaction-active"
  local entry
  local relative
  RC_TRANSACTION_ROOT="$RC_INSTALL_ROOT/backups/.install-transaction"
  if [[ ! -e "$marker" && ! -L "$marker" ]]; then
    [[ ! -e "$RC_TRANSACTION_ROOT" && ! -L "$RC_TRANSACTION_ROOT" ]] ||
      { rc_die 'orphaned installer transaction requires manual inspection'; return 1; }
    RC_TRANSACTION_ROOT=''
    return 0
  fi
  [[ -f "$marker" && ! -L "$marker" && -d "$RC_TRANSACTION_ROOT" && ! -L "$RC_TRANSACTION_ROOT" ]] ||
    { rc_die 'installer transaction journal is unsafe'; return 1; }
  for relative in "${RC_GENERATED_FILES[@]}"; do
    if [[ -f "$RC_TRANSACTION_ROOT/files/$relative" && ! -L "$RC_TRANSACTION_ROOT/files/$relative" ]]; then
      [[ ! -e "$RC_TRANSACTION_ROOT/absent/$relative" ]] ||
        { rc_die 'installer transaction journal is ambiguous'; return 1; }
    elif [[ -f "$RC_TRANSACTION_ROOT/absent/$relative" && ! -L "$RC_TRANSACTION_ROOT/absent/$relative" ]]; then
      [[ ! -e "$RC_TRANSACTION_ROOT/files/$relative" ]] ||
        { rc_die 'installer transaction journal is ambiguous'; return 1; }
    else
      rc_die 'installer transaction journal is incomplete'
      return 1
    fi
  done
  while IFS= read -r -d '' entry; do
    relative="${entry#"$RC_TRANSACTION_ROOT"/}"
    case "$relative" in
      files | files/config | files/state | absent | absent/config | absent/state) ;;
      files/.env | files/compose.yaml | files/config/sources.json | \
        files/state/source-mounts.json | files/state/channel | \
        files/state/current-image | files/state/previous-image | \
        absent/.env | absent/compose.yaml | absent/config/sources.json | \
        absent/state/source-mounts.json | absent/state/channel | \
        absent/state/current-image | absent/state/previous-image) ;;
      *) rc_die 'installer transaction journal contains an unexpected entry'; return 1 ;;
    esac
  done < <(find "$RC_TRANSACTION_ROOT" -mindepth 1 -maxdepth 3 -print0)
  rc_rollback_generated
}

rc_set_env_image() {
  local destination="$1"
  local image="$2"
  local directory
  local temporary
  local line
  local seen=0
  [[ "$image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]] ||
    { rc_die 'resolved image digest is invalid'; return 1; }
  [[ -f "$destination" && ! -L "$destination" ]] ||
    { rc_die 'installed environment file is unsafe'; return 1; }
  directory="$(dirname -- "$destination")"
  temporary="$(mktemp "$directory/.env-write.XXXXXX")" || return 1
  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in
      REACHCOMMANDER_IMAGE=*)
        printf 'REACHCOMMANDER_IMAGE=%s\n' "$image" >>"$temporary"
        seen=$((seen + 1))
        ;;
      REACHCOMMANDER_BIND_ADDRESS=* | REACHCOMMANDER_PORT=* | \
        REACHCOMMANDER_UID=* | REACHCOMMANDER_GID=*)
        printf '%s\n' "$line" >>"$temporary"
        ;;
      *)
        rm -f -- "$temporary"
        rc_die 'installed environment file is invalid'
        return 1
        ;;
    esac
  done <"$destination"
  [[ "$seen" == '1' ]] ||
    { rm -f -- "$temporary"; rc_die 'installed image setting is invalid'; return 1; }
  chmod 0600 "$temporary"
  mv -f -- "$temporary" "$destination"
}

rc_load_installed_environment() {
  local line
  local key
  local value
  local bind=''
  local port=''
  local uid=''
  local gid=''
  local image=''
  local count=0
  [[ -f "$RC_INSTALL_ROOT/.env" && ! -L "$RC_INSTALL_ROOT/.env" ]] ||
    { rc_die 'installed environment file is unsafe'; return 1; }
  while IFS= read -r line || [[ -n "$line" ]]; do
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$line" == *=* ]] || { rc_die 'installed environment file is invalid'; return 1; }
    case "$key" in
      REACHCOMMANDER_BIND_ADDRESS) [[ -z "$bind" ]] || return 1; bind="$value" ;;
      REACHCOMMANDER_PORT) [[ -z "$port" ]] || return 1; port="$value" ;;
      REACHCOMMANDER_UID) [[ -z "$uid" ]] || return 1; uid="$value" ;;
      REACHCOMMANDER_GID) [[ -z "$gid" ]] || return 1; gid="$value" ;;
      REACHCOMMANDER_IMAGE) [[ -z "$image" ]] || return 1; image="$value" ;;
      *) rc_die 'installed environment file contains an unknown setting'; return 1 ;;
    esac
    count=$((count + 1))
  done <"$RC_INSTALL_ROOT/.env"
  [[ "$count" == '5' ]] || { rc_die 'installed environment file must contain five settings'; return 1; }
  [[ "$bind" == '127.0.0.1' || "$bind" == '0.0.0.0' ]] ||
    { rc_die 'installed bind address is invalid'; return 1; }
  rc_validate_port "$port" || return 1
  [[ "$uid" =~ ^[0-9]+$ && "$gid" =~ ^[0-9]+$ ]] ||
    { rc_die 'installed UID or GID is invalid'; return 1; }
  [[ "$image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]] ||
    { rc_die 'installed image digest is invalid'; return 1; }
  RC_BIND_ADDRESS="$bind"
  RC_PORT="$port"
  RC_CURRENT_PORT="$port"
  RC_INSTALLED_IMAGE="$image"
}

rc_read_current_image() {
  local image
  [[ -f "$RC_INSTALL_ROOT/state/current-image" && ! -L "$RC_INSTALL_ROOT/state/current-image" ]] ||
    { rc_die 'installed image state is unsafe'; return 1; }
  IFS= read -r image <"$RC_INSTALL_ROOT/state/current-image" || true
  [[ "$image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]] ||
    { rc_die 'installed image state is invalid'; return 1; }
  printf '%s\n' "$image"
}

rc_validate_existing_deployment() {
  local relative
  local env_image
  local state_image
  local previous_image=''
  for relative in "${RC_GENERATED_FILES[@]}"; do
    [[ -f "$RC_INSTALL_ROOT/$relative" && ! -L "$RC_INSTALL_ROOT/$relative" ]] ||
      { rc_die "installed generated file is unsafe: $relative"; return 1; }
  done
  rc_load_installed_environment || return 1
  env_image="$RC_INSTALLED_IMAGE"
  state_image="$(rc_read_current_image)" || return 1
  [[ "$env_image" == "$state_image" ]] ||
    { rc_die 'installed image state does not match the environment'; return 1; }
  [[ "$(sed -n '1p' "$RC_INSTALL_ROOT/state/channel")" == 'stable' ]] ||
    { rc_die 'installed update channel is invalid'; return 1; }
  IFS= read -r previous_image <"$RC_INSTALL_ROOT/state/previous-image" || true
  if [[ -n "$previous_image" && \
    ! "$previous_image" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]]; then
    rc_die 'installed previous image state is invalid'
    return 1
  fi
  RC_CURRENT_IMAGE="$state_image"
}

rc_print_failure_diagnostics() {
  rc_compose "$RC_INSTALL_ROOT" logs --tail 200 reachcommander 2>&1 |
    sed -E \
      -e 's/([Ss]etup code:).*/\1 [redacted]/' \
      -e '/[Aa]uthorization:|[Bb]earer |[Pp]assword/d'
}

rc_update_existing() {
  local old_digest
  local new_digest
  rc_validate_existing_deployment || return 1
  old_digest="$RC_CURRENT_IMAGE"
  new_digest="$(rc_pull_digest stable)" || return 1
  if [[ "$new_digest" == "$old_digest" ]]; then
    printf 'ReachCommander is already up to date.\n'
    return 0
  fi
  rc_begin_generated_transaction || return 1
  rc_set_env_image "$RC_INSTALL_ROOT/.env" "$new_digest" ||
    { rc_rollback_generated || true; return 1; }
  printf '%s\n' "$old_digest" >"$RC_INSTALL_ROOT/state/previous-image"
  printf '%s\n' "$new_digest" >"$RC_INSTALL_ROOT/state/current-image"
  chmod 0600 "$RC_INSTALL_ROOT/state/previous-image" "$RC_INSTALL_ROOT/state/current-image"
  rc_validate_authentication_data_tree ||
    { rc_rollback_generated || true; return 1; }
  if rc_compose "$RC_INSTALL_ROOT" up -d reachcommander &&
    rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
    rc_complete_transaction || return 1
    printf 'ReachCommander updated successfully.\n'
    return 0
  fi
  rc_print_failure_diagnostics >&2 || true
  rc_rollback_generated || return 3
  rc_compose "$RC_INSTALL_ROOT" up -d reachcommander ||
    { rc_die 'update and automatic rollback are both unhealthy'; return 3; }
  rc_wait_healthy "$RC_INSTALL_ROOT" 60 ||
    { rc_die 'update and automatic rollback are both unhealthy'; return 3; }
  rc_die 'update was unhealthy; the previous image was restored'
  return 2
}

rc_cleanup_work_root() {
  local value="${1:-}"
  local canonical=''
  local temporary_base="${TMPDIR:-/tmp}"
  [[ -n "$value" && -d "$value" && ! -L "$value" ]] || return 0
  canonical="$(rc_canonical_directory "$value")" || return 1
  temporary_base="$(rc_canonical_directory "${temporary_base%/}")" || return 1
  case "$canonical" in
    "$temporary_base"/reachcommander-macos-install.*)
      rm -rf -- "$canonical"
      ;;
    *)
      rc_die 'refusing to remove an unexpected installer staging path'
      return 1
      ;;
  esac
}

rc_reconfigure_existing() {
  local current_digest
  local template
  local stage
  rc_validate_existing_deployment || return 1
  current_digest="$RC_CURRENT_IMAGE"
  RC_RECONFIGURING=true
  rc_collect_sources || { RC_RECONFIGURING=false; return 1; }
  rc_collect_network || { RC_RECONFIGURING=false; return 1; }
  RC_RECONFIGURING=false
  RC_WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/reachcommander-macos-install.XXXXXX")" || return 1
  template="$RC_WORK_ROOT/compose.release.yaml"
  stage="$RC_WORK_ROOT/deployment"
  if ! rc_fetch_template "$template" ||
    ! rc_render_deployment "$stage" "$template" "$current_digest" \
      "$RC_BIND_ADDRESS" "$RC_PORT" "$(id -u)" "$(id -g)" ||
    ! rc_compose "$stage" config --quiet ||
    ! rc_preflight_sources "$stage"; then
    rc_cleanup_work_root "$RC_WORK_ROOT" || true
    RC_WORK_ROOT=''
    return 1
  fi
  if ! rc_commit_generated "$stage"; then
    if [[ -e "$RC_INSTALL_ROOT/state/transaction-active" ]]; then
      rc_rollback_generated || true
    fi
    rc_cleanup_work_root "$RC_WORK_ROOT" || true
    RC_WORK_ROOT=''
    return 1
  fi
  rc_validate_authentication_data_tree ||
    { rc_rollback_generated || true; rc_cleanup_work_root "$RC_WORK_ROOT" || true; RC_WORK_ROOT=''; return 1; }
  if rc_compose "$RC_INSTALL_ROOT" up -d reachcommander &&
    rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
    rc_complete_transaction || return 1
    rc_cleanup_work_root "$RC_WORK_ROOT" || return 1
    RC_WORK_ROOT=''
    printf 'ReachCommander reconfigured successfully.\n'
    return 0
  fi
  rc_print_failure_diagnostics >&2 || true
  if ! rc_restore_generated_files; then
    rc_die 'reconfiguration failed and the transaction could not be restored'
    return 3
  fi
  if rc_compose "$RC_INSTALL_ROOT" up -d reachcommander &&
    rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
    rc_complete_transaction || return 3
    rc_cleanup_work_root "$RC_WORK_ROOT" || true
    RC_WORK_ROOT=''
    rc_die 'reconfiguration was unhealthy; the prior configuration was restored'
    return 2
  fi
  rc_die 'reconfiguration and automatic rollback are both unhealthy; transaction journal retained'
  return 3
}

rc_choose_existing_action() {
  local choice
  while true; do
    printf '\nReachCommander is already installed.\n1. Update (Recommended)\n2. Reconfigure\n3. Exit\n' >&2
    choice="$(rc_prompt_value 'Choose an action' '1')" || return 1
    case "$choice" in
      1) rc_update_existing; return ;;
      2) rc_reconfigure_existing; return ;;
      3) printf 'ReachCommander was left unchanged.\n'; return 0 ;;
      *) rc_die 'choose 1 to update, 2 to reconfigure, or 3 to exit' ;;
    esac
  done
}

rc_shell_quote() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//\$/\\\$}"
  value="${value//\`/\\\`}"
  printf '"%s"' "$value"
}

rc_print_completion() {
  local base
  local local_ip
  local index=0
  local policy
  base="docker compose --project-name reachcommander --project-directory $(rc_shell_quote "$RC_INSTALL_ROOT") --file $(rc_shell_quote "$RC_INSTALL_ROOT/compose.yaml")"
  printf '\nReachCommander is ready.\n'
  printf 'Open: http://127.0.0.1:%s\n' "$RC_PORT"
  if [[ "$RC_BIND_ADDRESS" == '0.0.0.0' ]]; then
    local_ip="$(rc_local_ip_address 2>/dev/null || true)"
    [[ -z "$local_ip" ]] || printf 'Local network: http://%s:%s\n' "$local_ip" "$RC_PORT"
  fi
  printf '\nSources:\n'
  while (( index < ${#RC_SOURCE_IDS[@]} )); do
    policy='RO'
    [[ "${RC_SOURCE_ACCESS[$index]}" == 'rw' ]] && policy='RW'
    printf '  - %s (%s): %s\n' "${RC_SOURCE_NAMES[$index]}" "$policy" "${RC_SOURCE_PATHS[$index]}"
    index=$((index + 1))
  done
  printf '\nInstaller-owned state: %s\n' "$RC_INSTALL_ROOT"
  printf 'Use the one-time first-run setup code shown by:\n  %s logs --tail 200 reachcommander\n' "$base"
  printf '\nStatus: %s ps\n' "$base"
  printf 'Start:  %s up -d reachcommander\n' "$base"
  printf 'Stop:   %s down\n' "$base"
  printf '\nFor updates, rerun:\n'
  # The command substitution is printed verbatim for the operator.
  # shellcheck disable=SC2016
  printf '%s\n' '/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/macos/install.sh)"'
  printf 'Choose option 1 for digest discovery with health-checked rollback.\n'
}

rc_handle_signal() {
  if [[ -f "$RC_INSTALL_ROOT/state/transaction-active" && -n "$RC_TRANSACTION_ROOT" ]]; then
    rc_rollback_generated || true
  fi
  rc_cleanup_work_root "$RC_WORK_ROOT" || true
  RC_WORK_ROOT=''
  rc_release_lock
  exit 130
}

rc_release_lock() {
  if [[ "$RC_LOCK_OWNED" == 'true' && -d "$RC_LOCK_DIRECTORY" && ! -L "$RC_LOCK_DIRECTORY" ]]; then
    rm -f -- "$RC_LOCK_DIRECTORY/pid"
    rmdir -- "$RC_LOCK_DIRECTORY"
  fi
  RC_LOCK_OWNED=false
}

rc_acquire_lock() {
  local state_directory="$RC_INSTALL_ROOT/state"
  local stale_pid=''
  mkdir -p -- "$state_directory"
  chmod 0700 "$RC_INSTALL_ROOT" "$state_directory"
  if ! mkdir -- "$RC_LOCK_DIRECTORY" 2>/dev/null; then
    [[ -f "$RC_LOCK_DIRECTORY/pid" && ! -L "$RC_LOCK_DIRECTORY/pid" ]] &&
      IFS= read -r stale_pid <"$RC_LOCK_DIRECTORY/pid"
    if [[ "$stale_pid" =~ ^[1-9][0-9]*$ ]] && kill -0 "$stale_pid" 2>/dev/null; then
      rc_die 'another ReachCommander installer operation is running'
      return 1
    fi
    rm -f -- "$RC_LOCK_DIRECTORY/pid"
    rmdir -- "$RC_LOCK_DIRECTORY" ||
      { rc_die 'installer lock is unsafe or cannot be recovered'; return 1; }
    mkdir -- "$RC_LOCK_DIRECTORY"
  fi
  chmod 0700 "$RC_LOCK_DIRECTORY"
  printf '%s\n' "$$" >"$RC_LOCK_DIRECTORY/pid"
  chmod 0600 "$RC_LOCK_DIRECTORY/pid"
  RC_LOCK_OWNED=true
}

main() {
  local template=''
  local stage=''
  local digest=''
  rc_init_paths || return 1
  rc_preflight || return 1
  rc_prepare_installer_root || return 1
  rc_validate_authentication_data_tree || return 1
  rc_acquire_lock || return 1
  trap 'rc_cleanup_work_root "$RC_WORK_ROOT" || true; rc_release_lock' EXIT
  trap rc_handle_signal HUP INT TERM
  rc_recover_transaction || return 1

  if [[ -f "$RC_INSTALL_ROOT/.env" && -f "$RC_INSTALL_ROOT/compose.yaml" ]]; then
    rc_choose_existing_action
    return
  fi
  if [[ -e "$RC_INSTALL_ROOT/.env" || -L "$RC_INSTALL_ROOT/.env" || \
    -e "$RC_INSTALL_ROOT/compose.yaml" || -L "$RC_INSTALL_ROOT/compose.yaml" ]]; then
    rc_die 'partial existing deployment requires manual inspection'
    return 1
  fi

  rc_collect_sources || return 1
  rc_collect_network || return 1
  RC_WORK_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/reachcommander-macos-install.XXXXXX")" || return 1
  template="$RC_WORK_ROOT/compose.release.yaml"
  stage="$RC_WORK_ROOT/deployment"
  rc_fetch_template "$template" || return 1
  digest="$(rc_pull_digest stable)" || return 1
  rc_render_deployment "$stage" "$template" "$digest" \
    "$RC_BIND_ADDRESS" "$RC_PORT" "$(id -u)" "$(id -g)" || return 1
  rc_compose "$stage" config --quiet || return 1
  rc_preflight_sources "$stage" || return 1
  if ! rc_commit_generated "$stage"; then
    if [[ -f "$RC_INSTALL_ROOT/state/transaction-active" ]]; then
      rc_rollback_generated || true
    fi
    return 1
  fi
  rc_validate_authentication_data_tree ||
    { rc_rollback_generated || true; return 1; }
  if ! rc_compose "$RC_INSTALL_ROOT" up -d reachcommander ||
    ! rc_wait_healthy "$RC_INSTALL_ROOT" 60; then
    rc_print_failure_diagnostics >&2 || true
    rc_compose "$RC_INSTALL_ROOT" down >/dev/null 2>&1 || true
    rc_complete_transaction || return 3
    rc_die 'initial startup was unhealthy; validated configuration was retained'
    return 2
  fi
  rc_complete_transaction || return 1
  rc_print_completion
}

if [[ "${REACHCOMMANDER_SOURCE_ONLY:-0}" != '1' ]]; then
  main "$@"
fi
