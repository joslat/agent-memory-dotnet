#!/usr/bin/env bash
# Fail-fast checks for a release tag, run before the expensive build/test/pack pipeline:
#   - the tag looks like a SemVer release tag (stable or prerelease)
#   - the tagged commit IS the current tip of main (not merely an ancestor of it) -- both stable
#     and prerelease tags use the same current-head-only policy: it is simpler and harder to
#     misuse than allowing prereleases to point at an older ancestor commit, since that would let
#     a stable tag get promoted from a commit that was never itself validated as a prerelease if
#     anything landed on main in between.
#   - CHANGELOG.md has a dated section for this version
set -euo pipefail

TAG="${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"

echo "Validating release tag $TAG"

# Two lines: the core (v<semver>) and the 0.x preview line (preview-v0.x.y-<prerelease>, always a prerelease, 40.27).
if [[ "$TAG" =~ ^preview-v0\.[0-9]+\.[0-9]+-[0-9A-Za-z.-]+$ ]]; then
  VERSION="${TAG#preview-v}"
  # The preview packages depend on the core version this commit carries; that release must be on NuGet first, or
  # nobody could restore them.
  core="$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props)"
  if ! curl -fsS "https://api.nuget.org/v3-flatcontainer/agentmemory.abstractions/index.json" | grep -q "\"${core,,}\""; then
    echo "::error::The preview line depends on core $core, which is not on NuGet yet. Release core first."
    exit 1
  fi
elif [[ "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  VERSION="${TAG#v}"
else
  echo "::error::Invalid release tag: $TAG"
  exit 1
fi

git fetch origin main --no-tags

main_head="$(git rev-parse origin/main)"
if [[ "$GITHUB_SHA" != "$main_head" ]]; then
  echo "::error::Tag commit $GITHUB_SHA is not the current tip of origin/main ($main_head). Re-tag main's HEAD."
  exit 1
fi

if ! grep -Eq "^## \[$VERSION\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" CHANGELOG.md; then
  echo "::error::CHANGELOG.md has no dated section for version $VERSION."
  exit 1
fi

echo "Tag $TAG is valid: SemVer shape OK, points at current main HEAD, CHANGELOG has a dated section."
