#!/usr/bin/env bash
# Installs the developer toolchain Chalk needs beyond the .NET SDK.
#
# macOS (Homebrew):
#   - JDK 21              the planner sidecar's toolchain (D20). The temurin@21 *cask*
#                         runs a .pkg installer under sudo, which a non-interactive
#                         setup cannot answer, so this falls back to the sudo-free
#                         openjdk@21 *formula*. See docs/adr/0002-jdk21-openjdk-formula.md.
#   - Gradle              only to bootstrap the checked-in wrapper; the wrapper is used thereafter
#   - buf                 proto lint / format / breaking checks
#   - protobuf            protoc; Grpc.Tools 2.83.0 ships no arm64 macOS protoc (01-repo-and-toolchain.md §2)
#   - grpc                grpc_csharp_plugin, same reason
#
# Linux: prints what to install; CI runners already have JDK 21 and buf.
set -euo pipefail

say() { printf '\n==> %s\n' "$*"; }
run() { printf '+ %s\n' "$*"; "$@"; }

os="$(uname -s)"

if [[ "$os" != "Darwin" ]]; then
  cat <<'EOF'
This script automates the macOS/Homebrew path only.

On Linux install:
  - a JDK 21 (Temurin or your distribution's openjdk-21-jdk)
  - buf          https://buf.build/docs/installation
  - protobuf-compiler and the gRPC C# plugin are NOT needed: Grpc.Tools
    ships working linux-x64 binaries.
Gradle is not needed either: use ./planner/gradlew.
EOF
  exit 0
fi

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is required: https://brew.sh" >&2
  exit 1
fi

say "JDK 21"
if /usr/libexec/java_home -v 21 >/dev/null 2>&1; then
  echo "already installed: $(/usr/libexec/java_home -v 21)"
elif brew list --versions openjdk@21 >/dev/null 2>&1; then
  echo "already installed: $(brew --prefix openjdk@21)/libexec/openjdk.jdk/Contents/Home"
elif run brew install --cask temurin@21; then
  :
else
  echo "temurin@21 needs sudo for its .pkg installer; falling back to openjdk@21 (ADR 0002)"
  run brew install openjdk@21
fi

say "Gradle (bootstraps the wrapper only)"
command -v gradle >/dev/null 2>&1 && echo "already installed: $(command -v gradle)" || run brew install gradle

say "buf"
command -v buf >/dev/null 2>&1 && echo "already installed: $(command -v buf)" || run brew install bufbuild/buf/buf

say "protobuf (protoc)"
command -v protoc >/dev/null 2>&1 && echo "already installed: $(command -v protoc)" || run brew install protobuf

say "grpc (grpc_csharp_plugin)"
if [[ -x /opt/homebrew/bin/grpc_csharp_plugin || -x /usr/local/bin/grpc_csharp_plugin ]]; then
  echo "already installed"
else
  run brew install grpc
fi

say "Versions"
# Every line here ends in `|| true`: under `set -e` a false `[[ ]] && cmd` would end the script,
# and a missing optional tool is not a reason to fail an install that otherwise worked.
java_home="$("$(dirname "$0")/java-home.sh" 2>/dev/null || true)"
{ [[ -n "$java_home" ]] && "$java_home/bin/java" -version 2>&1 | head -1; } || true
{ command -v gradle >/dev/null 2>&1 && gradle --version 2>/dev/null | sed -n '3p'; } || true
{ command -v buf >/dev/null 2>&1 && echo "buf $(buf --version)"; } || true
{ command -v protoc >/dev/null 2>&1 && protoc --version; } || true
{ command -v grpc_csharp_plugin >/dev/null 2>&1 && echo "grpc_csharp_plugin $(command -v grpc_csharp_plugin)"; } || true

cat <<EOF

Done. Add to your shell profile if it is not already there:

  export JAVA_HOME="${java_home:-\$(/usr/libexec/java_home -v 21)}"

scripts/build.sh and scripts/test.sh discover the JDK themselves through
scripts/java-home.sh, so this is only for your own interactive use.
EOF
