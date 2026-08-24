#!/bin/bash
set -Eeuo pipefail
umask 077

RC_IMAGE_REPOSITORY='ghcr.io/dragosniamtu/reach-commander'
RC_TEMPLATE_URL='https://raw.githubusercontent.com/dragosniamtu/reach-commander/master/deploy/compose.release.yaml'
RC_INSTALL_ROOT=''
RC_USER_HOME=''
RC_LOCK_DIRECTORY=''
RC_LOCK_OWNED=false

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
