#!/usr/bin/env bash
# Installs each dedicated consumer project (eng/package-consumers/Consumer.*) from a freshly-packed
# artifacts directory and runs it for every target framework. Each consumer references only the
# packages a real user of that entry point would install (not all 12 at once, which would mask
# package-boundary mistakes), and actually calls the public DI registration surface, validating the
# graph via ServiceProviderOptions.ValidateOnBuild rather than merely restoring/building.
set -euo pipefail

VERSION="${1:?usage: verify-packages-install.sh <version> [artifacts-dir]}"
ARTIFACTS="${2:-artifacts}"
CONSUMERS_DIR="eng/package-consumers"
TFMS=(net8.0 net9.0 net10.0)

ARTIFACTS_ABS="$(cd "$ARTIFACTS" && pwd)"
# NuGet expects a native filesystem path for a local feed on Windows.
case "${OSTYPE:-}" in
  msys*|cygwin*) ARTIFACTS_ABS="$(cygpath -w "$ARTIFACTS_ABS")" ;;
esac

# Keep feeds as separate configuration entries. Passing multiple --source arguments can be
# normalized as one filesystem path by the Windows SDK, corrupting the HTTPS feed URL.
# Use the NuGet CLI to encode paths (including spaces/XML characters), and exclude ambient feeds.
sources_config="$(mktemp)"
trap 'rm -f "$sources_config"' EXIT
printf '<configuration><packageSources><clear /></packageSources></configuration>\n' > "$sources_config"
dotnet nuget add source "$ARTIFACTS_ABS" --name freshly-packed --configfile "$sources_config"
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org --configfile "$sources_config"

for consumer in "$CONSUMERS_DIR"/Consumer.*/; do
  name="$(basename "$consumer")"
  proj="${consumer}${name}.csproj"

  echo "== $name: restore =="
  dotnet restore "$proj" \
    -p:ConsumerPackageVersion="$VERSION" \
    --configfile "$sources_config"

  for tfm in "${TFMS[@]}"; do
    echo "== $name ($tfm): build =="
    dotnet build "$proj" -c Release --no-restore -f "$tfm" -p:ConsumerPackageVersion="$VERSION"

    echo "== $name ($tfm): run =="
    output="$(dotnet run --project "$proj" -c Release --no-build -f "$tfm" -p:ConsumerPackageVersion="$VERSION")"
    echo "$output"

    if [[ "$output" != *"AgentMemory.Abstractions.Services.IMemoryService"* ]]; then
      echo "::error::$name ($tfm) did not print the expected IMemoryService type name -- got: $output"
      exit 1
    fi
  done
done

echo "All consumers (${TFMS[*]}) installed, built, and resolved their DI graph cleanly."
