#!/usr/bin/env bash
# Builds both toolchains: the JVM planner sidecar (shadow jar) and the .NET client.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
config="${CHALK_CONFIGURATION:-Release}"

run() { printf '\n+ %s\n' "$*"; "$@"; }

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh (macOS) or install a JDK 21." >&2
  exit 1
fi
export JAVA_HOME
echo "JAVA_HOME=$JAVA_HOME"

run "$root/planner/gradlew" -p "$root/planner" build -x test

run dotnet build "$root/dotnet/Chalk.slnx" -c "$config"

echo
echo "Planner jar: $(ls "$root"/planner/build/libs/chalk-planner-*-all.jar)"
