#!/usr/bin/env bash
# Regenerates corpus/plans/m1/* by planning every corpus query against a freshly
# started sidecar. Reviewers see the diff (the .json twin) in the pull request.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
config="${CHALK_CONFIGURATION:-Release}"

run() { printf '\n+ %s\n' "$*"; "$@"; }

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh." >&2
  exit 1
fi
export JAVA_HOME

jar="${CHALK_PLANNER_JAR:-}"
if [[ -z "$jar" ]]; then
  run "$root/planner/gradlew" -p "$root/planner" shadowJar
  jar="$(ls "$root"/planner/build/libs/chalk-planner-*-all.jar | head -1)"
fi
echo "Planner jar: $jar"

run dotnet build "$root/dotnet/tools/Chalk.CorpusTool" -c "$config"

log="$(mktemp -t chalk-planner)"
"$JAVA_HOME/bin/java" -jar "$jar" --port 0 > "$log" 2>&1 &
planner_pid=$!
trap 'kill "$planner_pid" 2>/dev/null || true; rm -f "$log"' EXIT

address=""
for _ in $(seq 1 100); do
  if line="$(grep -m1 'chalk-planner listening on ' "$log" 2>/dev/null)"; then
    address="http://${line##*listening on }"
    break
  fi
  kill -0 "$planner_pid" 2>/dev/null || { echo "planner exited:"; cat "$log"; exit 1; }
  sleep 0.1
done
[[ -n "$address" ]] || { echo "planner did not print its listening line:"; cat "$log"; exit 1; }
echo "Planner at $address"

run dotnet run --project "$root/dotnet/tools/Chalk.CorpusTool" -c "$config" --no-build -- \
  record --corpus "$root/corpus" --planner "$address"

echo
echo "Recorded. Review the diff in corpus/plans/."
