#!/usr/bin/env bash
# Prints a JDK 21+ home, or nothing (exit 1) if none is found.
# Order: $JAVA_HOME, /usr/libexec/java_home -v 21 (macOS), Homebrew openjdk@21, `java` on PATH.
set -euo pipefail

version_of() {
  local home="$1"
  [[ -x "$home/bin/java" ]] || return 1
  "$home/bin/java" -version 2>&1 | head -1 | sed -E 's/.*version "([0-9]+).*/\1/'
}

ok() { local v; v="$(version_of "$1" 2>/dev/null || true)"; [[ -n "$v" && "$v" -ge 21 ]]; }

if [[ -n "${JAVA_HOME:-}" ]] && ok "$JAVA_HOME"; then echo "$JAVA_HOME"; exit 0; fi

if [[ -x /usr/libexec/java_home ]]; then
  if h="$(/usr/libexec/java_home -v 21 2>/dev/null)" && ok "$h"; then echo "$h"; exit 0; fi
fi

for prefix in /opt/homebrew /usr/local; do
  for cellar in "$prefix/opt/openjdk@21" "$prefix/opt/openjdk"; do
    for h in "$cellar/libexec/openjdk.jdk/Contents/Home" "$cellar"; do
      if ok "$h"; then echo "$h"; exit 0; fi
    done
  done
done

if command -v java >/dev/null 2>&1; then
  h="$(dirname "$(dirname "$(readlink -f "$(command -v java)" 2>/dev/null || command -v java)")")"
  if ok "$h"; then echo "$h"; exit 0; fi
fi

exit 1
