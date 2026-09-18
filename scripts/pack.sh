#!/usr/bin/env bash
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
out="${1:-$root/dist/nuget}"
configuration=Release

run() { printf '\n+ %s\n' "$*"; "$@"; }

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh (macOS) or install a JDK 21." >&2
  exit 1
fi
export JAVA_HOME

main_package="$root/dotnet/packaging/ChalkQL.Package/ChalkQL.Package.csproj"
sources_package="$root/dotnet/packaging/ChalkQL.Sources.Package/ChalkQL.Sources.Package.csproj"

run "$root/planner/gradlew" -p "$root/planner" shadowJar -q

run python3 "$here/third-party-notices.py" "$root"
if ! git -C "$root" diff --quiet -- dotnet/src/Chalk.Planner/THIRD-PARTY-NOTICES.txt; then
  echo "note: THIRD-PARTY-NOTICES.txt moved — the jar's dependencies changed; review and commit it." >&2
fi

# Build the constituent assemblies and produce each project's project.assets.json.
run dotnet build "$root/dotnet/Chalk.slnx" -c "$configuration" -p:RequireEmbeddedPlannerJar=true
  
# A wide package must explicitly carry the union of its constituent assemblies' direct
# third-party package dependencies. Generate those from the already-restored projects.
run python3 "$here/nuget-package-dependencies.py" "$root"

rm -rf "$out"
mkdir -p "$out"

# Restore/pack the core package first.
run dotnet restore "$main_package" \
  -p:RepoRoot="$root/" \
  -p:Configuration="$configuration"

run dotnet pack "$main_package" \
  -c "$configuration" \
  --no-restore \
  -p:RepoRoot="$root/" \
  -o "$out"

# ChalkQL.Sources has an exact-version PackageReference on ChalkQL. Add the freshly packed
# directory as an additional source without replacing the repository's configured feeds.
run dotnet restore "$sources_package" \
  -p:RepoRoot="$root/" \
  -p:Configuration="$configuration" \
  -p:RestoreAdditionalProjectSources="$out"

run dotnet pack "$sources_package" \
  -c "$configuration" \
  --no-restore \
  -p:RepoRoot="$root/" \
  -o "$out"

mapfile -t packages < <(find "$out" -maxdepth 1 -type f -name '*.nupkg' ! -name '*.symbols.nupkg' | sort)

if [[ "${#packages[@]}" -ne 2 ]]; then
  printf '\nexpected exactly two packages, found %d:\n' "${#packages[@]}" >&2
  printf '  %s\n' "${packages[@]}" >&2
  exit 1
fi

printf '\npackages in %s:\n' "$out"
printf '%s\n' "${packages[@]}"
