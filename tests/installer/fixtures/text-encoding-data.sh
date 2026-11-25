#!/usr/bin/env bash

TEXT_ENCODING_FIXTURE_FILES=(
  'text-encodings/staging/0123456789abcdef0123456789abcdef.json'
  'text-encodings/staging/0123456789abcdef0123456789abcdef.json.invalid'
  'text-encodings/staging/0123456789abcdef0123456789abcdef.json.invalid-fedcba9876543210fedcba9876543210'
  'text-encodings/staging/.0123456789abcdef0123456789abcdef-fedcba9876543210fedcba9876543210.tmp'
)

create_text_encoding_fixture() {
  local data_root="$1" relative
  mkdir -p -- "$data_root/text-encodings/staging" || return 1
  chmod 0700 "$data_root/text-encodings" "$data_root/text-encodings/staging" || return 1
  for relative in "${TEXT_ENCODING_FIXTURE_FILES[@]}"; do
    printf '{"textEncodingStateCanary":true}\n' >"$data_root/$relative"
    chmod 0600 "$data_root/$relative" || return 1
  done
}

assert_text_encoding_fixture() {
  local data_root="$1" relative
  for relative in "${TEXT_ENCODING_FIXTURE_FILES[@]}"; do
    [[ -f "$data_root/$relative" ]] || return 1
    cmp -s "$data_root/$relative" <(printf '{"textEncodingStateCanary":true}\n') || return 1
  done
}
