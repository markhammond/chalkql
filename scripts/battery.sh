#!/usr/bin/env bash
# The battery: what a tree is put through before it lands and again once it has. The planner is built
# with its tests re-run rather than taken from Gradle's cache, so the count is this run's; the .NET
# solution is built in Release and every unit suite, then the integration and Akade suites against a
# sidecar from the shadow jar, are run; the unit suites run again in Debug; the README quickstart runs;
# every corpus plan is re-recorded and must not move; and the tutorial runs and is compared with
# docs/tutorial.md. A phase that fails does not stop the ones after it. The summary at the end says how
# each fared, and the exit status is non-zero if any failed.
#
#   ./scripts/battery.sh                       # every phase
#   ./scripts/battery.sh --skip tutorial       # leave phases out; the names are
#                                              #   planner release debug quickstart plans tutorial
#   ./scripts/battery.sh --log battery.log     # and keep a copy of the output
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"

skip=""
log=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip) skip="$skip,$2"; shift 2 ;;
    --skip=*) skip="$skip,${1#--skip=}"; shift ;;
    --log) log="$2"; shift 2 ;;
    --log=*) log="${1#--log=}"; shift ;;
    -h|--help)
      echo "usage: scripts/battery.sh [--skip <phase>[,<phase>...]] [--log <file>]"
      echo "phases: planner release debug quickstart plans tutorial"
      exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
if [[ -n "$log" ]]; then
  exec > >(tee -a "$log") 2>&1
fi

if ! JAVA_HOME="$("$here/java-home.sh")"; then
  echo "No JDK 21+ found. Run scripts/install-tools.sh (macOS) or install a JDK 21." >&2
  exit 1
fi
export JAVA_HOME

run() { printf '\n+ %s\n' "$*"; "$@"; }

names=()
results=()
durations=()

skipped() { [[ ",$skip," == *",$1,"* ]]; }

# phase <name> <function>: runs it, records the outcome, carries on to the next.
phase() {
  local name="$1" body="$2"
  names+=("$name")
  if skipped "$name"; then
    results+=("skipped"); durations+=("-")
    return
  fi
  printf '\n==== %s  (%s)\n' "$name" "$(date '+%H:%M:%S')"
  local started=$SECONDS
  if "$body"; then
    results+=("PASS")
  else
    results+=("FAIL")
  fi
  local took=$(( SECONDS - started ))
  durations+=("$(( took / 60 ))m$(( took % 60 ))s")
}

# Every unit suite: the test projects that do not need a sidecar.
unit_projects=()
for p in "$root"/dotnet/tests/Chalk.*.Tests; do
  case "$p" in *Integration*|*Akade*) continue ;; esac
  unit_projects+=("$p")
done

# suites <configuration> <project>...: every suite runs even if an earlier one failed.
suites() {
  local config="$1"; shift
  local failed=0 p
  for p in "$@"; do
    run dotnet test "$p" -c "$config" --no-build || failed=1
  done
  return $failed
}

jar() { ls "$root"/planner/build/libs/chalk-planner-*-all.jar 2>/dev/null | head -1; }

# One numeric attribute of a JUnit report's testsuite element, or 0.
attribute() {
  local value
  value="$(sed -n "s/.*<testsuite[^>]* $1=\"\([0-9]*\)\".*/\1/p" "$2" | head -1)"
  echo "${value:-0}"
}

planner() {
  run "$root/planner/gradlew" -p "$root/planner" cleanTest build || return 1
  # Gradle prints no total; the JUnit reports carry one.
  local tests=0 failed=0 f
  for f in "$root"/planner/build/test-results/test/*.xml; do
    [[ -f "$f" ]] || continue
    tests=$(( tests + $(attribute tests "$f") ))
    failed=$(( failed + $(attribute failures "$f") + $(attribute errors "$f") ))
  done
  echo
  echo "planner: $tests tests, $failed failed"
  [[ $failed -eq 0 ]]
}

release() {
  run dotnet build "$root/dotnet/Chalk.slnx" -c Release || return 1
  local failed=0
  suites Release "${unit_projects[@]}" || failed=1
  local j
  j="$(jar)"
  if [[ -z "$j" ]]; then
    echo "no planner jar under planner/build/libs; the integration and Akade suites cannot run" >&2
    return 1
  fi
  export CHALK_PLANNER_JAR="$j"
  suites Release "$root/dotnet/tests/Chalk.Integration.Tests" "$root/dotnet/tests/Chalk.Sources.Akade.Tests" || failed=1
  return $failed
}

debug() {
  run dotnet build "$root/dotnet/Chalk.slnx" -c Debug || return 1
  suites Debug "${unit_projects[@]}"
}

quickstart() { run "$here/quickstart.sh"; }

# Re-recording must leave corpus/plans as it found it: a moved recording is a planner change that
# nobody wrote down.
plans() {
  local before after
  before="$(git -C "$root" status --porcelain -- corpus/plans)"
  run "$here/record-plans.sh" || return 1
  after="$(git -C "$root" status --porcelain -- corpus/plans)"
  if [[ "$before" != "$after" ]]; then
    echo
    echo "re-recording moved these plans:"
    diff <(printf '%s\n' "$before") <(printf '%s\n' "$after") | sed -n 's/^> //p'
    return 1
  fi
  echo
  echo "nothing moved."
}

tutorial() { run "$here/tutorial.sh"; }

started=$SECONDS
phase planner planner
phase release release
phase debug debug
phase quickstart quickstart
phase plans plans
phase tutorial tutorial

took=$(( SECONDS - started ))
printf '\n==== battery  (%dm%ds)\n' $(( took / 60 )) $(( took % 60 ))
failed=0
for i in "${!names[@]}"; do
  printf '  %-11s %-8s %s\n' "${names[$i]}" "${results[$i]}" "${durations[$i]}"
  [[ "${results[$i]}" == "FAIL" ]] && failed=1
done
if [[ $failed -eq 0 ]]; then
  echo "All green."
else
  echo "Not green."
fi
exit $failed
