#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

if (($# != 2)); then
  printf 'Usage: package-installer.sh <vX.Y.Z> <output-directory>\n' >&2
  exit 64
fi

VERSION="$1"
OUTPUT_DIRECTORY="$2"
NUMBER='(0|[1-9][0-9]*)'
if [[ ! "$VERSION" =~ ^v${NUMBER}\.${NUMBER}\.${NUMBER}$ ]]; then
  printf 'ReachCommander packager: version must be a stable vX.Y.Z value\n' >&2
  exit 1
fi

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
mkdir -p -- "$OUTPUT_DIRECTORY"
OUTPUT_DIRECTORY="$(readlink -m -- "$OUTPUT_DIRECTORY")"
STAGING_ROOT="$(mktemp -d)"
PACKAGE_ROOT="$STAGING_ROOT/reachcommander-installer"

cleanup() {
  local canonical_staging
  canonical_staging="$(readlink -m -- "$STAGING_ROOT")"
  case "$canonical_staging" in
    /tmp/* | "${TMPDIR:-/tmp}"/*)
      rm -rf -- "$canonical_staging"
      ;;
    *)
      printf 'ReachCommander packager: refusing to remove unexpected staging path\n' >&2
      ;;
  esac
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

required_sources=(
  "$REPOSITORY_ROOT/LICENSE"
  "$SCRIPT_DIRECTORY/compose.release.yaml"
  "$SCRIPT_DIRECTORY/compose.updater.yaml"
  "$SCRIPT_DIRECTORY/install.sh"
  "$SCRIPT_DIRECTORY/lan_address.py"
  "$SCRIPT_DIRECTORY/reachcommander"
  "$SCRIPT_DIRECTORY/render_config.py"
  "$SCRIPT_DIRECTORY/support_bundle.py"
  "$SCRIPT_DIRECTORY/support_bundle_cli.py"
  "$SCRIPT_DIRECTORY/update_trace_cli.py"
  "$SCRIPT_DIRECTORY/updater_protocol.py"
  "$SCRIPT_DIRECTORY/updater_service.py"
  "$SCRIPT_DIRECTORY/updater_trace.py"
  "$SCRIPT_DIRECTORY/lib/common.sh"
  "$SCRIPT_DIRECTORY/systemd/reachcommander-updater.service"
)
for required_source in "${required_sources[@]}"; do
  if [[ ! -f "$required_source" || -L "$required_source" ]]; then
    printf 'ReachCommander packager: required source is missing or symlinked\n' >&2
    exit 1
  fi
done

install -d -m 0755 -- "$PACKAGE_ROOT" "$PACKAGE_ROOT/lib" "$PACKAGE_ROOT/systemd"
install -m 0644 -- "$REPOSITORY_ROOT/LICENSE" "$PACKAGE_ROOT/LICENSE"
printf '%s\n' "$VERSION" >"$PACKAGE_ROOT/VERSION"
chmod 0644 -- "$PACKAGE_ROOT/VERSION"
install -m 0644 -- "$SCRIPT_DIRECTORY/compose.release.yaml" "$PACKAGE_ROOT/compose.release.yaml"
install -m 0644 -- "$SCRIPT_DIRECTORY/compose.updater.yaml" "$PACKAGE_ROOT/compose.updater.yaml"
install -m 0755 -- "$SCRIPT_DIRECTORY/install.sh" "$PACKAGE_ROOT/install.sh"
install -m 0644 -- "$SCRIPT_DIRECTORY/lan_address.py" "$PACKAGE_ROOT/lan_address.py"
install -m 0755 -- "$SCRIPT_DIRECTORY/reachcommander" "$PACKAGE_ROOT/reachcommander"
install -m 0644 -- "$SCRIPT_DIRECTORY/render_config.py" "$PACKAGE_ROOT/render_config.py"
install -m 0644 -- "$SCRIPT_DIRECTORY/support_bundle.py" "$PACKAGE_ROOT/support_bundle.py"
install -m 0755 -- "$SCRIPT_DIRECTORY/support_bundle_cli.py" "$PACKAGE_ROOT/support_bundle_cli.py"
install -m 0755 -- "$SCRIPT_DIRECTORY/update_trace_cli.py" "$PACKAGE_ROOT/update_trace_cli.py"
install -m 0644 -- "$SCRIPT_DIRECTORY/updater_protocol.py" "$PACKAGE_ROOT/updater_protocol.py"
install -m 0755 -- "$SCRIPT_DIRECTORY/updater_service.py" "$PACKAGE_ROOT/updater_service.py"
install -m 0644 -- "$SCRIPT_DIRECTORY/updater_trace.py" "$PACKAGE_ROOT/updater_trace.py"
install -m 0644 -- "$SCRIPT_DIRECTORY/lib/common.sh" "$PACKAGE_ROOT/lib/common.sh"
install -m 0644 -- "$SCRIPT_DIRECTORY/systemd/reachcommander-updater.service" "$PACKAGE_ROOT/systemd/reachcommander-updater.service"
chmod 0755 -- "$PACKAGE_ROOT" "$PACKAGE_ROOT/lib" "$PACKAGE_ROOT/systemd" "$PACKAGE_ROOT/install.sh" "$PACKAGE_ROOT/reachcommander" "$PACKAGE_ROOT/support_bundle_cli.py" "$PACKAGE_ROOT/update_trace_cli.py" "$PACKAGE_ROOT/updater_service.py"
chmod 0644 -- \
  "$PACKAGE_ROOT/LICENSE" \
  "$PACKAGE_ROOT/VERSION" \
  "$PACKAGE_ROOT/compose.release.yaml" \
  "$PACKAGE_ROOT/compose.updater.yaml" \
  "$PACKAGE_ROOT/lan_address.py" \
  "$PACKAGE_ROOT/render_config.py" \
  "$PACKAGE_ROOT/support_bundle.py" \
  "$PACKAGE_ROOT/updater_protocol.py" \
  "$PACKAGE_ROOT/updater_trace.py" \
  "$PACKAGE_ROOT/lib/common.sh" \
  "$PACKAGE_ROOT/systemd/reachcommander-updater.service"

ARCHIVE_NAME='reachcommander-installer.tar.gz'
ARCHIVE_PATH="$OUTPUT_DIRECTORY/$ARCHIVE_NAME"
ARCHIVE_TEMPORARY="$(mktemp "$OUTPUT_DIRECTORY/.reachcommander-installer.XXXXXX")"
TAR_TEMPORARY="$(mktemp "$OUTPUT_DIRECTORY/.reachcommander-installer-tar.XXXXXX")"
tar_options=(
  --sort=name
  --mtime='UTC 1970-01-01'
  --owner=0
  --group=0
  --numeric-owner
  --format=gnu
  --no-recursion
  -C "$STAGING_ROOT"
)
tar "${tar_options[@]}" --mode=0755 -cf "$TAR_TEMPORARY" reachcommander-installer
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/LICENSE
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/VERSION
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/compose.release.yaml
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/compose.updater.yaml
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/install.sh
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/lan_address.py
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/lib
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/lib/common.sh
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/reachcommander
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/render_config.py
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/support_bundle.py
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/support_bundle_cli.py
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/update_trace_cli.py
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/systemd
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/systemd/reachcommander-updater.service
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/updater_protocol.py
tar "${tar_options[@]}" --mode=0755 -rf "$TAR_TEMPORARY" reachcommander-installer/updater_service.py
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/updater_trace.py
if ! gzip -n <"$TAR_TEMPORARY" >"$ARCHIVE_TEMPORARY"; then
  rm -f -- "$TAR_TEMPORARY"
  rm -f -- "$ARCHIVE_TEMPORARY"
  printf 'ReachCommander packager: archive creation failed\n' >&2
  exit 1
fi
rm -f -- "$TAR_TEMPORARY"
chmod 0644 -- "$ARCHIVE_TEMPORARY"
sync -f -- "$ARCHIVE_TEMPORARY"
mv -f -- "$ARCHIVE_TEMPORARY" "$ARCHIVE_PATH"

CHECKSUM_PATH="$OUTPUT_DIRECTORY/SHA256SUMS"
CHECKSUM_TEMPORARY="$(mktemp "$OUTPUT_DIRECTORY/.SHA256SUMS.XXXXXX")"
(
  cd -- "$OUTPUT_DIRECTORY"
  sha256sum --text "$ARCHIVE_NAME"
) >"$CHECKSUM_TEMPORARY"
chmod 0644 -- "$CHECKSUM_TEMPORARY"
sync -f -- "$CHECKSUM_TEMPORARY"
mv -f -- "$CHECKSUM_TEMPORARY" "$CHECKSUM_PATH"

printf 'Created %s and %s for %s\n' "$ARCHIVE_PATH" "$CHECKSUM_PATH" "$VERSION"
