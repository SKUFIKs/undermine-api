#!/usr/bin/env bash
# Restores, builds and runs the UnderMine API test suite.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./run-tests.sh [Debug|Release] [filter]

  Debug|Release   Build configuration (default: Debug)
  filter          Optional dotnet test filter,
                  e.g. "FullyQualifiedName~ClientReport"

Environment:
  NO_RESTORE=1    Skip dotnet restore
EOF
}

for arg in "$@"; do
    case "$arg" in
        -h|--help) usage; exit 0 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT/UnderMineApi.sln"
CONFIGURATION="${1:-Debug}"
FILTER="${2:-}"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet CLI is not installed or not on PATH." >&2
    exit 1
fi

if [[ ! -f "$SOLUTION" ]]; then
    echo "Solution not found: $SOLUTION" >&2
    exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "==> UnderMine API tests ($CONFIGURATION)"

if [[ "${NO_RESTORE:-0}" != "1" ]]; then
    echo "==> dotnet restore"
    dotnet restore "$SOLUTION" --nologo
fi

echo "==> dotnet build"
dotnet build "$SOLUTION" -c "$CONFIGURATION" --no-restore --nologo

TEST_ARGS=("$SOLUTION" -c "$CONFIGURATION" --no-build --nologo)
if [[ -n "$FILTER" ]]; then
    TEST_ARGS+=(--filter "$FILTER")
fi

echo "==> dotnet test"
dotnet test "${TEST_ARGS[@]}"

echo "API tests passed."
