#!/usr/bin/env bash
set -Eeuo pipefail

RC_IMAGE_REPOSITORY='ghcr.io/dragosniamtu/reach-commander'
RC_PRODUCTION_INSTALL_ROOT='/opt/reachcommander'
RC_PRODUCTION_COMMAND_PATH='/usr/local/bin/reachcommander'
RC_PRODUCTION_BACKUP_ROOT='/var/backups/reachcommander'

rc_die() {
  printf 'ReachCommander: %s\n' "$1" >&2
  return 1
}

rc_init_paths() {
  RC_INSTALL_ROOT="$RC_PRODUCTION_INSTALL_ROOT"
  RC_COMMAND_PATH="$RC_PRODUCTION_COMMAND_PATH"
  RC_BACKUP_ROOT="$RC_PRODUCTION_BACKUP_ROOT"

  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]] && (( EUID != 0 )); then
    [[ -n "${REACHCOMMANDER_TEST_BASE:-}" ]] || {
      rc_die 'test base is required'
      return 1
    }
    RC_INSTALL_ROOT="${REACHCOMMANDER_TEST_INSTALL_ROOT:-}"
    RC_COMMAND_PATH="${REACHCOMMANDER_TEST_COMMAND_PATH:-}"
    RC_BACKUP_ROOT="${REACHCOMMANDER_TEST_BACKUP_ROOT:-}"
    [[ -n "$RC_INSTALL_ROOT" && -n "$RC_COMMAND_PATH" && -n "$RC_BACKUP_ROOT" ]] || {
      rc_die 'all test paths are required'
      return 1
    }
  fi

  export RC_INSTALL_ROOT RC_COMMAND_PATH RC_BACKUP_ROOT
}

rc_require_commands() {
  local command_name
  for command_name in "$@"; do
    if ! command -v -- "$command_name" >/dev/null 2>&1; then
      rc_die "missing required command: $command_name"
      return 1
    fi
  done
}

rc_require_root() {
  if (( EUID != 0 )); then
    rc_die 'this command must run as root'
    return 1
  fi
}

rc_invoking_ids() {
  local invoking_uid="${SUDO_UID:-}"
  local invoking_gid="${SUDO_GID:-}"
  if [[ -z "$invoking_uid" || -z "$invoking_gid" ]]; then
    invoking_uid="$(id -u)"
    invoking_gid="$(id -g)"
  fi
  if [[ ! "$invoking_uid" =~ ^[1-9][0-9]*$ || ! "$invoking_gid" =~ ^[1-9][0-9]*$ ]]; then
    rc_die 'the invoking non-root UID and GID are required'
    return 1
  fi
  if (( invoking_uid > 2147483647 || invoking_gid > 2147483647 )); then
    rc_die 'the invoking UID or GID is out of range'
    return 1
  fi
  printf '%s:%s\n' "$invoking_uid" "$invoking_gid"
}

rc_validate_port() {
  local port="${1:-}"
  if [[ ! "$port" =~ ^[0-9]+$ ]] || (( 10#$port < 1 || 10#$port > 65535 )); then
    rc_die 'port must be an integer from 1 through 65535'
    return 1
  fi
}

rc_normalize_source_id() {
  local normalized
  normalized="$(
    LC_ALL=C printf '%s' "${1:-}" |
      tr '[:upper:]' '[:lower:]' |
      sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//; s/-+/-/g' |
      cut -c 1-64 |
      sed -E 's/[-_]+$//'
  )"
  if [[ -z "$normalized" || ! "$normalized" =~ ^[a-z0-9][a-z0-9_-]{0,63}$ ]]; then
    rc_die 'source name cannot be normalized to a valid ID'
    return 1
  fi
  printf '%s\n' "$normalized"
}

rc_canonical_source() {
  local source_path="${1:-}"
  local canonical
  if [[ -z "$source_path" || "$source_path" != /* ]]; then
    rc_die 'source path must be absolute'
    return 1
  fi
  if [[ ! -d "$source_path" ]]; then
    rc_die 'source path must be an existing directory'
    return 1
  fi
  canonical="$(readlink -f -- "$source_path")" || {
    rc_die 'source path cannot be resolved'
    return 1
  }
  rc_validate_source_path "$canonical" || return 1
  printf '%s\n' "$canonical"
}

rc_validate_source_path() {
  local source_path="${1:-}"
  local canonical
  if [[ -z "$source_path" || "$source_path" != /* ]]; then
    rc_die 'source path must be absolute'
    return 1
  fi
  canonical="$(readlink -m -- "$source_path")" || {
    rc_die 'source path cannot be normalized'
    return 1
  }
  case "$canonical" in
    / | /proc | /proc/* | /sys | /sys/* | /dev | /dev/* | /run | /run/* | /var/run | /var/run/*)
      rc_die 'source path resolves to a protected host location'
      return 1
      ;;
  esac
}

rc_validate_channel() {
  local channel="${1:-}"
  local number='(0|[1-9][0-9]*)'
  local identifier='(0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)'
  local version_pattern="^v${number}\\.${number}\\.${number}(-${identifier}(\\.${identifier})*)?$"
  if [[ "$channel" == 'stable' || "$channel" == 'edge' || "$channel" =~ $version_pattern ]]; then
    return 0
  fi
  rc_die 'channel must be stable, edge, or a semantic vX.Y.Z version'
  return 1
}

rc_acquire_lock() {
  mkdir -p -- "$RC_INSTALL_ROOT/state" || {
    rc_die 'cannot create command lock directory'
    return 1
  }
  exec 9>"$RC_INSTALL_ROOT/state/command.lock" || {
    rc_die 'cannot open command lock'
    return 1
  }
  if ! flock -n 9; then
    rc_die 'another ReachCommander operation is already running'
    return 1
  fi
}

rc_compose() {
  docker compose --project-directory "$RC_INSTALL_ROOT" "$@"
}

rc_pull_digest() {
  local channel="${1:-}"
  local reference
  local inspect_output
  local line
  local selected=''
  rc_validate_channel "$channel" || return 1
  reference="${RC_IMAGE_REPOSITORY}:${channel}"
  if ! docker pull "$reference" >/dev/null; then
    rc_die 'image pull failed'
    return 1
  fi
  if ! inspect_output="$(
    docker image inspect \
      --format '{{range .RepoDigests}}{{println .}}{{end}}' \
      "$reference"
  )"; then
    rc_die 'image digest inspection failed'
    return 1
  fi
  while IFS= read -r line; do
    if [[ "$line" =~ ^ghcr\.io/dragosniamtu/reach-commander@sha256:[0-9a-f]{64}$ ]]; then
      if [[ -n "$selected" && "$selected" != "$line" ]]; then
        rc_die 'image digest inspection returned conflicting values'
        return 1
      fi
      selected="$line"
    fi
  done <<<"$inspect_output"
  if [[ -z "$selected" ]]; then
    rc_die 'image digest inspection returned no trusted digest'
    return 1
  fi
  printf '%s\n' "$selected"
}

rc_wait_healthy() {
  local container="${1:-}"
  local timeout="${2:-}"
  local deadline
  local status
  if [[ -z "$container" || ! "$timeout" =~ ^[1-9][0-9]*$ ]]; then
    rc_die 'health check requires a container and positive timeout'
    return 1
  fi
  deadline=$((SECONDS + timeout))
  while (( SECONDS <= deadline )); do
    if ! status="$(
      docker inspect \
        --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}' \
        "$container"
    )"; then
      rc_die 'container health inspection failed'
      return 1
    fi
    case "$status" in
      healthy)
        return 0
        ;;
      unhealthy | missing)
        rc_die 'container is not healthy'
        return 1
        ;;
      starting | created | restarting)
        ;;
      *)
        rc_die 'container returned an invalid health status'
        return 1
        ;;
    esac
    sleep 1
  done
  rc_die 'container health check timed out'
  return 1
}

rc_atomic_write() {
  local destination="${1:-}"
  local content="${2-}"
  local directory
  local base_name
  local temporary
  if [[ -z "$destination" || "$destination" != /* ]]; then
    rc_die 'atomic destination must be absolute'
    return 1
  fi
  directory="$(dirname -- "$destination")"
  base_name="$(basename -- "$destination")"
  mkdir -p -- "$directory" || {
    rc_die 'cannot create atomic destination directory'
    return 1
  }
  temporary="$(mktemp "$directory/.${base_name}.tmp.XXXXXX")" || {
    rc_die 'cannot create atomic temporary file'
    return 1
  }
  if ! chmod 0600 -- "$temporary" || ! printf '%s' "$content" >"$temporary"; then
    rm -f -- "$temporary"
    rc_die 'cannot write atomic temporary file'
    return 1
  fi
  if ! sync -f -- "$temporary"; then
    rm -f -- "$temporary"
    rc_die 'cannot synchronize atomic temporary file'
    return 1
  fi
  if ! mv -f -- "$temporary" "$destination"; then
    rm -f -- "$temporary"
    rc_die 'cannot replace atomic destination'
    return 1
  fi
}

rc_assert_safe_install_root() {
  local install_root
  local command_path
  local backup_root
  local test_base
  if [[ "${REACHCOMMANDER_TESTING:-0}" == '1' ]] && (( EUID != 0 )); then
    test_base="$(readlink -m -- "${REACHCOMMANDER_TEST_BASE:-}")" || return 1
    install_root="$(readlink -m -- "$RC_INSTALL_ROOT")" || return 1
    command_path="$(readlink -m -- "$RC_COMMAND_PATH")" || return 1
    backup_root="$(readlink -m -- "$RC_BACKUP_ROOT")" || return 1
    case "$install_root" in "$test_base"/*) ;; *) rc_die 'unsafe test install root'; return 1 ;; esac
    case "$command_path" in "$test_base"/*) ;; *) rc_die 'unsafe test command path'; return 1 ;; esac
    case "$backup_root" in "$test_base"/*) ;; *) rc_die 'unsafe test backup root'; return 1 ;; esac
  else
    [[ "$RC_INSTALL_ROOT" == "$RC_PRODUCTION_INSTALL_ROOT" ]] || {
      rc_die 'production install root is not fixed'
      return 1
    }
    [[ "$RC_COMMAND_PATH" == "$RC_PRODUCTION_COMMAND_PATH" ]] || {
      rc_die 'production command path is not fixed'
      return 1
    }
    [[ "$RC_BACKUP_ROOT" == "$RC_PRODUCTION_BACKUP_ROOT" ]] || {
      rc_die 'production backup root is not fixed'
      return 1
    }
  fi
  for install_root in "$RC_INSTALL_ROOT" "$RC_COMMAND_PATH" "$RC_BACKUP_ROOT"; do
    if [[ "$install_root" == '/' || -L "$install_root" ]]; then
      rc_die 'installer-owned path is broad or symlinked'
      return 1
    fi
  done
}
