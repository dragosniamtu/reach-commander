#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/deploy/lib/common.sh"
TEMP="$(mktemp -d)"
trap 'rm -rf -- "$TEMP"' EXIT
RC_INSTALL_ROOT="$TEMP/install"
mkdir -p "$RC_INSTALL_ROOT/bin" "$RC_INSTALL_ROOT/state"
touch "$RC_INSTALL_ROOT/bin/subtitle_management.py"
printf 'true\n' >"$RC_INSTALL_ROOT/state/subtitles-enabled"
# Unit tests exercise the real Python manager. This fixture isolates shell dispatch.
python3() {
  printf '%s\n' "$*" >>"$TEMP/helper.log"
  case "${2:-}" in
    enabled) cat "$RC_INSTALL_ROOT/state/subtitles-enabled" ;;
    *) return 0 ;;
  esac
}
docker() {
  printf '%s\n' "$*" >>"$TEMP/docker.log"
  if [[ "$1" == inspect ]]; then printf 'healthy\n'; fi
}
for action in up stop restart; do
  rc_compose "$action" -d reachcommander
  tail -1 "$TEMP/docker.log" | grep -q 'reachcommander subtitle-worker$' || {
    printf 'not ok - %s must maintain enabled subtitle worker\n' "$action" >&2; exit 1;
  }
done
rc_wait_healthy reachcommander 60
grep -q 'verify' "$TEMP/helper.log" || { printf 'not ok - worker health must gate deployment\n'; exit 1; }
printf 'false\n' >"$RC_INSTALL_ROOT/state/subtitles-enabled"
rc_compose up -d reachcommander
if tail -1 "$TEMP/docker.log" | grep -q 'subtitle-worker'; then
  printf 'not ok - disabled worker must remain stopped\n'; exit 1
fi
printf 'ok - start stop restart and health gates maintain only enabled worker\n'
