#!/usr/bin/env bash
# Runs the whole suite: JVM planner tests, .NET unit tests, then the integration
# tests against a sidecar spawned from the shadow jar, which is the only sidecar
# distribution there is (D21, D133).
#
#   scripts/test.sh                 everything
#   scripts/test.sh --no-integration    skip the tests that need the jar
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
config="${CHALK_CONFIGURATION:-Release}"
integration=1
[[ "${1:-}" == "--no-integration" ]] && integration=0

run() { printf '\n+ %s\n' "$*"; "$@"; }

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh (macOS) or install a JDK 21." >&2
  exit 1
fi
export JAVA_HOME
echo "JAVA_HOME=$JAVA_HOME"

run "$root/planner/gradlew" -p "$root/planner" build

jar="$(ls "$root"/planner/build/libs/chalk-planner-*-all.jar | head -1)"
echo "Planner jar: $jar"

unit_projects=(
  "$root/dotnet/tests/Chalk.Ir.Tests"
  "$root/dotnet/tests/Chalk.Catalog.Tests"
  "$root/dotnet/tests/Chalk.Entitlements.Tenancy.Tests"
  "$root/dotnet/tests/Chalk.Sources.Poco.Tests"
  "$root/dotnet/tests/Chalk.Sources.Ado.Tests"
  "$root/dotnet/tests/Chalk.Execution.Tests"
  "$root/dotnet/tests/Chalk.Architecture.Tests"
)

run dotnet build "$root/dotnet/Chalk.slnx" -c "$config"
for p in "${unit_projects[@]}"; do
  run dotnet test "$p" -c "$config" --no-build
done

if [[ "$integration" == "1" ]]; then
  export CHALK_PLANNER_JAR="$jar"
  run dotnet test "$root/dotnet/tests/Chalk.Integration.Tests" -c "$config" --no-build
else
  echo
  echo "Skipped Chalk.Integration.Tests (--no-integration)."
fi

echo
echo "All green."
