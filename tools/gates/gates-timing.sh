#!/bin/bash
# The wall-clock tests (Category=Timing) alone (#623). Run once per wave on an IDLE machine (no other agent building or
# testing), before an install and before the Phase gates. They are not part of the merge gate (gates.sh).
# One rerun is allowed: a load spike during the first run is the only thing a rerun can excuse, and the budgets never move.
# Runs on the checkout it lives in; GATES_ROOT overrides that.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=$HOME/KSP_spike/GameData/kOS/Plugins"
if ! out=$(dotnet build ClaudiaKermin.sln -warnaserror $K 2>&1); then echo "FAIL build"; echo "$out" | grep -E 'error' | head -20; exit 1; fi
for attempt in 1 2; do
  out=$(dotnet test tests/ClaudiaKermin.Core.Tests --no-build $K --filter "Category=Timing" 2>&1); rc=$?
  line=$(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)
  if [ $rc -eq 0 ] && ! echo "$line" | grep -qE 'Passed: +[1-9]'; then echo "FAIL timing: no Category=Timing test ran (tags missing?)"; exit 1; fi
  if [ $rc -eq 0 ]; then echo "PASS timing (attempt $attempt) $line"; exit 0; fi
  echo "FAIL timing (attempt $attempt) $line"; echo "$out" | grep -E '\[FAIL\]' | head -20
done
exit 1
