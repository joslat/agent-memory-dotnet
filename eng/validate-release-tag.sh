#!/usr/bin/env bash
# Fail-fast checks for a release tag, run before the expensive build/test/pack pipeline:
#   - the tag looks like a SemVer release tag (stable or prerelease)
#   - the tagged commit is reachable from main
#   - CHANGELOG.md has a dated section for this version
set -euo pipefail

TAG="${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
VERSION="${TAG#v}"

echo "Validating release tag $TAG"

if ! [[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "::error::Invalid release tag: $TAG"
  exit 1
fi

git fetch origin main --no-tags

if ! git merge-base --is-ancestor "$GITHUB_SHA" origin/main; then
  echo "::error::The tagged commit $GITHUB_SHA is not reachable from origin/main."
  exit 1
fi

if ! grep -Eq "^## \[$VERSION\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" CHANGELOG.md; then
  echo "::error::CHANGELOG.md has no dated section for version $VERSION."
  exit 1
fi

echo "Tag $TAG is valid: SemVer shape OK, reachable from main, CHANGELOG has a dated section."
