#!/bin/bash
# The merge gate (#623): one build, then every test and Script Check on the built output. No Category=Timing test runs
# here (tools/gates/gates-timing.sh runs those, once per wave, on an idle machine).
# Prints one line per gate and exits non-zero on any failure.
# Runs on the checkout it lives in (the repo root is two levels up); GATES_ROOT overrides that.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=$HOME/KSP_spike/GameData/kOS/Plugins"
fail=0
# On a failure: the error and summary lines, then each failing test's block (#681): its name, message and the top of its
# stack, so a flake can be diagnosed from the gate log.
failed_tests() { awk '/^ *Failed [^!]/ { p = 1; s = 0 } p && s && ++n > 3 { p = 0 } p { print } /^ *Stack Trace:/ { s = 1; n = 0 }' | head -300; }
run() { local name=$1; shift; if out=$("$@" 2>&1); then echo "PASS $name $(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)"; else echo "FAIL $name"; echo "$out" | grep -E 'error|Failed!|FAIL' | head -20; echo "$out" | failed_tests; fail=1; fi; }

run build dotnet build ClaudiaKermin.sln -warnaserror $K
[ $fail -ne 0 ] && exit 1   # nothing below can run on a broken build

run core dotnet test tests/ClaudiaKermin.Core.Tests --no-build $K --filter "Category!=Timing"
run kerboscript bash tools/gates/kerboscript.sh   # several test processes at once (#625)
run kerboscript-loc bash tools/gates/kerboscript-loc.sh   # the library may not grow (#705, ADR 0014)
run scriptcheck dotnet test tests/ClaudiaKermin.ScriptCheck.Tests --no-build $K

# KsCompile runs from the binary the build above made.
KS=$(ls -t tools/ClaudiaKermin.KsCompile/bin/*/*/ClaudiaKermin.KsCompile.dll 2>/dev/null | head -1)   # newest = the one just built
if [ -z "$KS" ]; then echo "FAIL ksc-binary not found under tools/ClaudiaKermin.KsCompile/bin"; exit 1; fi
run blocks dotnet "$KS" kerboscript tests/blocks
[ -d tests/tools ] && run tools dotnet "$KS" kerboscript tests/tools

# Every forbidden-call or syntax-error fixture must be rejected: one call, which names each failing file.
fixtures=$(find tools tests \( -name forbidden-call.ks -o -name syntax-error.ks \) | grep -v /bin/ | grep -v /obj/)
if [ -n "$fixtures" ]; then
  out=$(dotnet "$KS" $fixtures 2>&1); rc=$?
  for f in $fixtures; do
    if echo "$out" | grep -qxF "FAIL $f"; then echo "PASS fixture $f rejected"; else echo "FAIL fixture $f passed"; fail=1; fi
  done
  [ $rc -ne 1 ] && { echo "FAIL fixtures: KsCompile exited $rc, expected 1"; fail=1; }
fi
exit $fail
