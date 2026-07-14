#!/usr/bin/env bash
# Validates that the packed artifacts in ARTIFACTS exactly match eng/release-packages.txt
# (no missing packages, no unexpected ones). Run after packing; tag/changelog/ancestry checks
# live in eng/validate-release-tag.sh and run earlier, before the expensive build/test steps.
set -euo pipefail

TAG="${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
VERSION="${TAG#v}"
ARTIFACTS="${1:-artifacts}"
MANIFEST="eng/release-packages.txt"

echo "Validating package inventory for $VERSION against $MANIFEST"

# Construct the exact expected package filenames from the manifest.
expected="$(mktemp)"
actual="$(mktemp)"

while IFS='|' read -r package_id project; do
  [[ -z "$package_id" || "$package_id" == \#* ]] && continue
  printf '%s.%s.nupkg\n' "$package_id" "$VERSION"
done < "$MANIFEST" | sort > "$expected"

find "$ARTIFACTS" -maxdepth 1 -type f -name '*.nupkg' \
  ! -name '*.symbols.nupkg' \
  -printf '%f\n' | sort > "$actual"

echo "Expected packages:"
cat "$expected"

echo "Actual packages:"
cat "$actual"

# Detect missing and unexpected packages.
missing="$(comm -23 "$expected" "$actual")"
unexpected="$(comm -13 "$expected" "$actual")"

if [[ -n "$missing" ]]; then
  echo "::error::Missing release packages:"
  echo "$missing"
  exit 1
fi

if [[ -n "$unexpected" ]]; then
  echo "::error::Unexpected release packages:"
  echo "$unexpected"
  exit 1
fi

expected_count="$(wc -l < "$expected")"
actual_count="$(wc -l < "$actual")"

if [[ "$expected_count" -ne "$actual_count" ]]; then
  echo "::error::Package count mismatch: expected $expected_count, found $actual_count."
  exit 1
fi

echo "Release inventory is valid."
