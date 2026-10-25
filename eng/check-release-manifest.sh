#!/usr/bin/env bash
# Repository consistency for eng/release-packages.txt, run by CI and by the release workflow:
#   - every core and preview line names a project that exists;
#   - every src/ project is accounted for: on the core line, the preview line, or HELD on purpose;
#   - every HELD id names a src/ project;
#   - every preview project carries a 0.x <Version>: a preview package must never ride a 1.x or later number.
set -euo pipefail

missing=0
lines="$(bash eng/release-lines.sh core && bash eng/release-lines.sh preview)" || missing=1

while IFS='|' read -r package_id project; do
  [[ -z "$package_id" ]] && continue
  if [[ ! -f "$project" ]]; then
    echo "::error::eng/release-packages.txt references a missing project: $project"
    missing=1
  fi
done <<< "$lines"

while IFS='|' read -r package_id project; do
  [[ -z "$package_id" || ! -f "$project" ]] && continue
  if ! grep -Eq '<Version>0\.[0-9]+\.[0-9]+' "$project"; then
    echo "::error::preview package '$package_id' has no 0.x <Version> in $project"
    missing=1
  fi
  if ! grep -q 'PreviewVersion' "$project"; then
    echo "::error::preview package '$package_id' does not take its release version from PreviewVersion ($project)"
    missing=1
  fi
done <<< "$(bash eng/release-lines.sh preview)"

accounted_ids="$(bash eng/release-lines.sh all | sort -u)"
actual_ids="$(find src -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)"
unlisted="$(comm -23 <(echo "$actual_ids") <(echo "$accounted_ids"))"
if [[ -n "$unlisted" ]]; then
  echo "::error::src/ project(s) neither listed, PREVIEW nor HELD in eng/release-packages.txt:"
  echo "$unlisted"
  missing=1
fi

orphan_held="$(comm -13 <(echo "$actual_ids") <(bash eng/release-lines.sh held | sort -u | grep -v '^$' || true))"
if [[ -n "$orphan_held" ]]; then
  echo "::error::HELD entries with no project under src/:"
  echo "$orphan_held"
  missing=1
fi

[[ "$missing" -eq 0 ]] || exit 1
echo "eng/release-packages.txt accounts for every src/*/ project."
