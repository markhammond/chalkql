#!/usr/bin/env bash
# Runs the tutorial end to end, then checks that docs/tutorial.md still says what it says.
#
# The sample starts its own sidecar over a Unix domain socket
#, so all this has to do is make sure there is a jar to
# start and a JDK to start it with. docs/tutorial.md quotes this script's output, and the comparison
# below is what stops it quoting an older one.
#
#   ./scripts/tutorial.sh          # all eighteen chapters, then the comparison
#   ./scripts/tutorial.sh 3        # chapter 3 alone; nothing is compared, because nothing is whole
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

if [[ $# -gt 0 ]]; then
  # One chapter: run it and stop. A partial run cannot be compared with a whole document.
  exec dotnet run --project "$root/dotnet/samples/Chalk.Sample.Tutorial" -c "$config" -- "$@"
fi

captured="$(mktemp -t chalk-tutorial)"
trap 'rm -f "$captured"' EXIT

set +e
dotnet run --project "$root/dotnet/samples/Chalk.Sample.Tutorial" -c "$config" -- 2>&1 | tee "$captured"
status=${PIPESTATUS[0]}
set -e
if [[ $status -ne 0 ]]; then
  exit "$status"
fi

echo
echo "-- comparing docs/tutorial.md with this run"

# Every fenced block in the document opened with a bare ``` is a recorded block and must appear,
# line for line and contiguously, in the run above. A block opened with a language — ```csharp,
# ```bash, ```text — is prose and is not compared. Trailing whitespace is ignored on both sides,
# because an editor strips it from the document and nobody should have to notice.
awk -v doc="$root/docs/tutorial.md" '
  function rstrip(s) { sub(/[ \t\r]+$/, "", s); return s }
  NR == FNR {
    line = rstrip($0)
    if (line ~ /^## /) { chapter = substr(line, 4) }
    if (line ~ /^```/) {
      if (infence) { infence = 0; recording = 0 }
      else {
        infence = 1
        lang = substr(line, 4)
        gsub(/[ \t]/, "", lang)
        if (lang == "") { recording = 1; blocks++; where[blocks] = chapter; at[blocks] = FNR; len[blocks] = 0 }
      }
      next
    }
    if (infence && recording) { len[blocks]++; block[blocks, len[blocks]] = line }
    next
  }
  { out[++lines] = rstrip($0) }
  END {
    if (infence) { print "docs/tutorial.md ends inside a fenced block"; exit 1 }
    failed = 0
    for (b = 1; b <= blocks; b++) {
      if (len[b] == 0) { continue }
      found = 0; best = 0; bestat = 0
      for (s = 1; s + len[b] - 1 <= lines; s++) {
        for (i = 1; i <= len[b]; i++) { if (out[s + i - 1] != block[b, i]) break }
        if (i > len[b]) { found = 1; break }
        if (i - 1 > best) { best = i - 1; bestat = s }
      }
      if (!found) {
        failed++
        printf "\ndocs/tutorial.md:%d, in \"%s\": this block is not in the run.\n", at[b], where[b]
        if (best > 0) {
          printf "  %d of its %d lines matched at the run'"'"'s line %d; the first that did not is\n", best, len[b], bestat
          printf "    document: %s\n", block[b, best + 1]
          printf "    run:      %s\n", out[bestat + best]
        } else {
          printf "  not one line matched. Its first line is\n    document: %s\n", block[b, 1]
        }
      }
    }
    if (failed > 0) {
      printf "\n%d of %d recorded blocks are stale. Re-record docs/tutorial.md from this run.\n", failed, blocks
      exit 1
    }
    printf "all %d recorded blocks are this run'"'"'s.\n", blocks
  }
' "$root/docs/tutorial.md" "$captured"
