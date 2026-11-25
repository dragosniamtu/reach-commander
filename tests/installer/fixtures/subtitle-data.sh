#!/usr/bin/env bash

SUBTITLE_FIXTURE_FILES=(
  'subtitle-generation/jobs/0123456789abcdef0123456789abcdef.json'
  'subtitle-generation/receipts/ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789.json'
  'subtitle-generation/transactions/ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789-0123456789abcdef0123456789abcdef.json'
)

create_subtitle_fixture() {
  local data_root="$1" relative
  mkdir -p -- "$data_root/subtitle-generation/jobs" \
    "$data_root/subtitle-generation/receipts" "$data_root/subtitle-generation/transactions" || return 1
  chmod 0700 "$data_root/subtitle-generation" "$data_root/subtitle-generation/jobs" \
    "$data_root/subtitle-generation/receipts" "$data_root/subtitle-generation/transactions" || return 1
  for relative in "${SUBTITLE_FIXTURE_FILES[@]}"; do
    printf '{"subtitleStateCanary":true}\n' >"$data_root/$relative"
    chmod 0600 "$data_root/$relative" || return 1
  done
}

assert_subtitle_fixture() {
  local data_root="$1" relative
  for relative in "${SUBTITLE_FIXTURE_FILES[@]}"; do
    [[ -f "$data_root/$relative" ]] || return 1
    cmp -s "$data_root/$relative" <(printf '{"subtitleStateCanary":true}\n') || return 1
  done
}
