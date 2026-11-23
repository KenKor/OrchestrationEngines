#!/usr/bin/env bash
# The checks a change must pass, in the order CI runs them. Run it before a push:
#
#     scripts/gate.sh                              # the working tree against origin/main
#     scripts/gate.sh --base origin/release/1.0    # against another base branch
#     scripts/gate.sh -- --coverage --results-directory "$PWD/TestResults"   # extra arguments for dotnet test
#
# Steps: the line-length check's self-test, the line-length check of the lines the change adds (against the merge
# base with --base, uncommitted edits to tracked files included), restore, build (Release), and the tests on net10.0.
# The first step that fails stops the script with its exit code. The net48 tests need Windows: the Windows workflow
# runs them after this script, as it runs the pack.
#
# Both workflows call this script. A new home-made check goes here, not into the workflow files.
set -euo pipefail

base="origin/main"
test_args=()
while [ $# -gt 0 ]; do
  case "$1" in
    --base)
      [ $# -ge 2 ] || { echo "gate: --base needs a branch, for example --base origin/main" >&2; exit 2; }
      base="$2"
      shift 2
      ;;
    --)
      shift
      test_args=("$@")
      break
      ;;
    *)
      echo "gate: unknown argument '$1'. Usage: scripts/gate.sh [--base <branch>] [-- <dotnet test arguments>]" >&2
      exit 2
      ;;
  esac
done

cd "$(dirname "${BASH_SOURCE[0]}")/.."

# The first of python3 and python that runs Python 3: on Windows, python3 can be a store stub that does not run.
python=""
for candidate in python3 python; do
  if "$candidate" -c 'import sys; sys.exit(sys.version_info[0] != 3)' > /dev/null 2>&1; then
    python="$candidate"
    break
  fi
done
[ -n "$python" ] || { echo "gate: no Python 3 found as python3 or python; install it to run the checks" >&2; exit 2; }

step() { echo "== gate: $*"; }

step "line length, self-test"
"$python" scripts/check-line-length.py --selftest

step "line length of added lines, against $base"
"$python" scripts/check-line-length.py --base "$base"

step "restore"
dotnet restore

step "build (Release)"
dotnet build --no-restore --configuration Release

step "test (net10.0, Release)"
dotnet test --no-build --configuration Release --framework net10.0 ${test_args[@]+"${test_args[@]}"}

step "passed"
