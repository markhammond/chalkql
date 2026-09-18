#!/usr/bin/env bash
# Runs the README quickstart end to end. The sample starts its own sidecar over a Unix
# domain socket, so all this has to do is
# make sure there is a jar to start and a JDK to start it with.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
config="${CHALK_CONFIGURATION:-Release}"

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh." >&2
  exit 1
fi
export JAVA_HOME

jar="${CHALK_PLANNER_JAR:-}"
if [[ -z "$jar" ]]; then
  jar="$(ls "$root"/planner/build/libs/chalk-planner-*-all.jar 2>/dev/null | head -1 || true)"
fi
if [[ -z "$jar" ]]; then
  printf '+ %s\n' "$root/planner/gradlew -p $root/planner shadowJar"
  "$root/planner/gradlew" -p "$root/planner" shadowJar
fi

dotnet run --project "$root/dotnet/samples/Chalk.Sample.Quickstart" -c "$config" --
