#!/usr/bin/env bash
# Validates a tagged release before packages are pushed to NuGet:
#   - the tag looks like a SemVer release tag
#   - the tagged commit is reachable from main
#   - CHANGELOG.md has a dated section for this version
#   - the packed artifacts exactly match eng/release-packages.txt (no more, no fewer)
set -euo pipefail

TAG="${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
VERSION="${TAG#v}"
ARTIFACTS="${1:-artifacts}"
MANIFEST="eng/release-packages.txt"

echo "Validating release $VERSION from tag $TAG"

# 1. Validate SemVer-like release tag.
if ! [[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "::error::Invalid release tag: $TAG"
  exit 1
fi

# 2. Ensure the tagged commit is reachable from main.
git fetch origin main --no-tags

if ! git merge-base --is-ancestor "$GITHUB_SHA" origin/main; then
  echo "::error::The tagged commit $GITHUB_SHA is not reachable from origin/main."
  exit 1
fi

# 3. Ensure the changelog contains this exact version.
if ! grep -Eq "^## \[$VERSION\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" CHANGELOG.md; then
  echo "::error::CHANGELOG.md has no dated section for version $VERSION."
  exit 1
fi

# 4. Construct the exact expected package filenames from the manifest.
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

# 5. Detect missing and unexpected packages.
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
