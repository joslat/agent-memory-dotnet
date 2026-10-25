#!/usr/bin/env bash
# The one reader of eng/release-packages.txt's grammar; CI, the release workflow and the inventory check all call it, so
# they cannot disagree about it (they did, twice, when each kept its own loop).
#
#   release-lines.sh core      "PackageId|ProjectPath" for the unprefixed lines: published with every v<semver> tag
#   release-lines.sh preview   "PackageId|ProjectPath" for the PREVIEW: lines: the 0.x line, published only with
#                              preview-v<0.x.y-prerelease> tags, at the tag's version (PreviewVersion)
#   release-lines.sh held      the HELD: ids, one per line: published on no line
#   release-lines.sh all       every id the file accounts for, one per line
#
# A core or preview line without a project path is an error: `dotnet pack ""` would pack the whole solution.
set -euo pipefail

LINE="${1:?usage: release-lines.sh core|preview|held|all}"
MANIFEST="${RELEASE_MANIFEST:-eng/release-packages.txt}"
status=0

while IFS= read -r raw || [[ -n "$raw" ]]; do
  entry="${raw%$'\r'}"
  [[ -z "$entry" || "$entry" == \#* ]] && continue
  case "$entry" in
    HELD:*) kind=held; entry="${entry#HELD:}" ;;
    PREVIEW:*) kind=preview; entry="${entry#PREVIEW:}" ;;
    *) kind=core ;;
  esac
  id="${entry%%|*}"
  path=""
  if [[ "$entry" == *"|"* ]]; then path="${entry#*|}"; fi
  if [[ "$kind" != held && -z "$path" ]]; then
    echo "::error::$MANIFEST line for '$id' has no project path." >&2
    status=1
    continue
  fi
  case "$LINE" in
    all) echo "$id" ;;
    held) if [[ "$kind" == held ]]; then echo "$id"; fi ;;
    core|preview) if [[ "$kind" == "$LINE" ]]; then echo "$id|$path"; fi ;;
    *) echo "::error::unknown release line '$LINE'" >&2; exit 2 ;;
  esac
done < "$MANIFEST"

exit "$status"
