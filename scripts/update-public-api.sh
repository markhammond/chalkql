#!/usr/bin/env bash
# Rebuilds the public packages and folds every RS0016 ("not part of the declared public API")
# and RS0017 ("part of the declared public API but is not public") finding back into each project's
# PublicAPI.Unshipped.txt, then sorts it the way the analyser expects.
#
#   scripts/update-public-api.sh
#
# The file is checked in on purpose (01-repo-and-toolchain.md §7): a public-API change should show
# up in the diff. This script does the typing, not the deciding — read what it added.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found." >&2
  exit 1
fi
export JAVA_HOME

projects=(
  "$root/dotnet/src/Chalk.Catalog"
  "$root/dotnet/src/Chalk.Sources.Abstractions"
  "$root/dotnet/src/Chalk.Sources.Poco"
  "$root/dotnet/src/Chalk.Sources.Ado"
  "$root/dotnet/src/Chalk.Sources.DuckDb"
  "$root/dotnet/src/Chalk.Sources.Conformance"
  "$root/dotnet/src/Chalk.Client"
  "$root/dotnet/src/Chalk.Entitlements"
  "$root/dotnet/src/Chalk.Entitlements.Tenancy"
)

log="$(mktemp)"
trap 'rm -f "$log"' EXIT

for _ in 1 2 3; do
  changed=0
  for project in "${projects[@]}"; do
    name="$(basename "$project")"
    api="$project/PublicAPI.Unshipped.txt"
    dotnet build "$project/$name.csproj" -c "${CHALK_CONFIGURATION:-Release}" >"$log" 2>&1 || true

    added="$(grep -oE "error RS0016: Symbol '[^']+'" "$log" \
      | sed -E "s/error RS0016: Symbol '(.*)'/\1/" | sort -u || true)"
    removed="$(grep -oE "error RS0017: Symbol '[^']+'" "$log" \
      | sed -E "s/error RS0017: Symbol '(.*)'/\1/" | sort -u || true)"

    if [[ -n "$added" || -n "$removed" ]]; then
      changed=1
      {
        echo "#nullable enable"
        {
          tail -n +2 "$api"
          [[ -n "$added" ]] && echo "$added"
        } | grep -v '^$' | { [[ -n "$removed" ]] && grep -vxF "$removed" || cat; } | LC_ALL=C sort -u
      } >"$api.new"
      mv "$api.new" "$api"
      echo "$name: +$(echo "$added" | grep -c . || true) -$(echo "$removed" | grep -c . || true)"
    fi
  done
  [[ "$changed" == 0 ]] && break
done

echo "public API files are up to date"
