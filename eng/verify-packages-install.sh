#!/usr/bin/env bash
# Installs every manifest package from a freshly-packed artifacts directory into throwaway
# consumer projects (one per target framework) and builds them. Catches packaging/dependency
# issues an in-solution project-reference build can't see: missing lib/ assets for a TFM,
# unresolvable transitive dependencies, version conflicts across the 12 packages, etc.
set -euo pipefail

VERSION="${1:?usage: verify-packages-install.sh <version> [artifacts-dir]}"
ARTIFACTS="${2:-artifacts}"
MANIFEST="eng/release-packages.txt"
TFMS=(net8.0 net9.0 net10.0)

ARTIFACTS_ABS="$(cd "$ARTIFACTS" && pwd)"
workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT

for tfm in "${TFMS[@]}"; do
  consumer="$workdir/consumer-$tfm"
  mkdir -p "$consumer"
  dotnet new console -f "$tfm" -o "$consumer" --force >/dev/null

  cat > "$consumer/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="release-artifacts" value="$ARTIFACTS_ABS" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

  echo "== $tfm: adding manifest packages =="
  while IFS='|' read -r package_id project; do
    [[ -z "$package_id" || "$package_id" == \#* ]] && continue
    dotnet add "$consumer" package "$package_id" --version "$VERSION" --source "$ARTIFACTS_ABS"
  done < "$MANIFEST"

  echo "== $tfm: restore + build =="
  dotnet build "$consumer" -c Release
done

echo "All target frameworks (${TFMS[*]}) installed and built cleanly from the packed artifacts."
