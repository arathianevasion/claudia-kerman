#!/bin/bash
# Stress repro for the Kerboscript gate flakes (#681): runs ChangeOrbitTests, DockTests and RendezvousTests together, in one
# test process per run (so each run starts kOS's process-wide tables fresh), with xUnit's class parallelism on, while a load
# generator keeps about half the cores busy. Stops at the first failing run, or after RUNS runs.
# Prints one line per run and, for a failing run, each failing test's message. Exits 1 if any run failed.
#   RUNS=20 tools/gates/kerboscript-stress.sh                          (after a build; it runs --no-build)
#   FILTER="FullyQualifiedName~DockTests" tools/gates/kerboscript-stress.sh
# LOAD=0 turns the load generator off. The load processes are killed on any exit.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=${KOS_PLUGINS_DIR:-$HOME/KSP_spike/GameData/kOS/Plugins}"   # KOS_PLUGINS_DIR: where kOS.Safe.dll is (CI: the kOS release)
RUNS=${RUNS:-20}
FILTER=${FILTER:-"FullyQualifiedName~ChangeOrbitTests|FullyQualifiedName~DockTests|FullyQualifiedName~RendezvousTests"}
CORES=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 2)
LOAD=${LOAD:-$(( CORES / 2 ))}

pids=()
cleanup() { [ ${#pids[@]} -gt 0 ] && kill "${pids[@]}" 2>/dev/null; wait 2>/dev/null; }
trap cleanup EXIT
trap 'exit 130' INT TERM
for ((i = 0; i < LOAD; i++)); do yes > /dev/null & pids+=($!); done
echo "load: $LOAD of $CORES cores; runs: up to $RUNS; filter: $FILTER"

for ((run = 1; run <= RUNS; run++)); do
  out=$(dotnet test tests/ClaudiaKerman.Kerboscript.Tests --no-build $K --filter "$FILTER" 2>&1)
  rc=$?
  summary=$(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)
  if [ $rc -eq 0 ]; then
    echo "run $run PASS $summary"
  else
    echo "run $run FAIL $summary"
    # Each failing test's name, then its message and the top of its stack (as gates.sh prints them).
    echo "$out" | awk '/^ *Failed [^!]/ { p = 1; s = 0 } p && s && ++n > 3 { p = 0 } p { print } /^ *Stack Trace:/ { s = 1; n = 0 }' | head -300
    exit 1
  fi
done
echo "all $RUNS runs passed"
