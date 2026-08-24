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
RC_SOURCE_IDS=()
RC_SOURCE_NAMES=()
RC_SOURCE_PATHS=()
RC_SOURCE_ACCESS=()

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
  [[ "$value" =~ ^[0-9]+$ ]] &&
    (( 10#$value >= 1 && 10#$value <= 65535 )) ||
    { rc_die 'port must be an integer from 1 through 65535'; return 1; }
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
  CDPATH= cd -P -- "$value" >/dev/null 2>&1 ||
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
  local canonical="$1"
  local relation
  case "$canonical" in
    / | /System | /System/* | /Library | /Library/* | /private | /private/* | \
      /usr | /usr/* | /bin | /bin/* | /sbin | /sbin/* | /dev | /dev/*)
      rc_die 'source path resolves to a protected macOS location'
      return 1
      ;;
  esac
  relation="$(rc_path_relation "$canonical" "$RC_INSTALL_ROOT")"
  [[ "$relation" != 'same' && "$relation" != 'inside' ]] ||
    { rc_die 'source path cannot be the installer directory or one of its children'; return 1; }
}

rc_yaml_quote() {
  local escaped="${1//\'/\'\'}"
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
  chmod 0700 -- "$output" "$output/data" "$output/data/auth" \
    "$output/data/keys" "$output/state" "$output/backups" || return 1
  chmod 0755 -- "$output/config" || return 1
  chmod 0555 -- "$output/excluded" || return 1

  printf '%s\n' \
    "REACHCOMMANDER_BIND_ADDRESS=$bind_address" \
    "REACHCOMMANDER_PORT=$port" \
    "REACHCOMMANDER_UID=$uid" \
    "REACHCOMMANDER_GID=$gid" \
    "REACHCOMMANDER_IMAGE=$image" >"$output/.env" || return 1
  chmod 0600 -- "$output/.env" || return 1

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
  chmod 0644 -- "$output/config/sources.json" || return 1
  chmod 0600 -- \
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
    '~/'*) printf '%s/%s\n' "$RC_USER_HOME" "${value#\~/}" ;;
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
    [[ "$selected" =~ ^[1-9][0-9]*$ ]] && (( selected <= ${#available[@]} )) ||
      { rc_die 'drive selections must be valid menu numbers'; return 1; }
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
  chmod 0700 -- "$RC_INSTALL_ROOT" "$state_directory"
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
  chmod 0700 -- "$RC_LOCK_DIRECTORY"
  printf '%s\n' "$$" >"$RC_LOCK_DIRECTORY/pid"
  chmod 0600 -- "$RC_LOCK_DIRECTORY/pid"
  RC_LOCK_OWNED=true
}

main() {
  rc_init_paths
  rc_die 'installer implementation is incomplete'
  return 1
}

if [[ "${REACHCOMMANDER_SOURCE_ONLY:-0}" != '1' ]]; then
  main "$@"
fi
