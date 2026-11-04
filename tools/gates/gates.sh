#!/bin/bash
# The merge gate (#623): one build, then every test and Script Check on the built output. No Category=Timing test runs
# here (tools/gates/gates-timing.sh runs those, once per wave, on an idle machine).
# Prints one line per gate and exits non-zero on any failure.
# After the build the steps run at once (#702): each writes its output to a file in a temp directory, then the lines are
# printed in the fixed order below. GATES_SERIAL=1 runs them one after another instead (the order of the lines is the same).
# Runs on the checkout it lives in (the repo root is two levels up); GATES_ROOT overrides that.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=$HOME/KSP_spike/GameData/kOS/Plugins"
fail=0
dir=$(mktemp -d)
pids=()
# On any exit the jobs still running are killed (background jobs ignore Ctrl-C), as kerboscript.sh does.
cleanup() { [ ${#pids[@]} -gt 0 ] && kill "${pids[@]}" 2>/dev/null; wait 2>/dev/null; rm -rf "$dir"; }
trap cleanup EXIT
trap 'exit 130' INT TERM
# On a failure: the error and summary lines, then each failing test's block (#681): its name, message and the top of its
# stack, so a flake can be diagnosed from the gate log.
failed_tests() { awk '/^ *Failed [^!]/ { p = 1; s = 0 } p && s && ++n > 3 { p = 0 } p { print } /^ *Stack Trace:/ { s = 1; n = 0 }' | head -300; }

# start NAME CMD...: runs CMD in the background, its output in $dir/NAME.out. GATES_SERIAL=1 waits for it and prints it now.
start() { local name=$1; shift; "$@" > "$dir/$name.out" 2>&1 & echo $! > "$dir/$name.pid"; pids+=($!)
  if [ "${GATES_SERIAL:-}" = 1 ]; then report "$name"; fi; }
# collect NAME: waits for the job once; leaves its status in $rc and its output in $out.
collect() { [ -e "$dir/$1.rc" ] || { wait "$(cat "$dir/$1.pid")"; echo $? > "$dir/$1.rc"; }; rc=$(cat "$dir/$1.rc"); out=$(cat "$dir/$1.out"); }
# report NAME: prints the PASS or FAIL lines for a job (once), in the format the callers grep.
report() { [ -e "$dir/$1.shown" ] && return; : > "$dir/$1.shown"; collect "$1"
  if [ "$rc" -eq 0 ]; then echo "PASS $1 $(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)"; else echo "FAIL $1"; echo "$out" | grep -E 'error|Failed!|FAIL' | head -20; echo "$out" | failed_tests; fail=1; fi; }

start build dotnet build ClaudiaKermin.sln -warnaserror $K
report build
[ $fail -ne 0 ] && exit 1   # nothing below can run on a broken build

start core dotnet test tests/ClaudiaKermin.Core.Tests --no-build $K --filter "Category!=Timing"
start kerboscript bash tools/gates/kerboscript.sh   # several test processes at once (#625)
start kerboscript-loc bash tools/gates/kerboscript-loc.sh   # the library may not grow (#705, ADR 0014)
start scriptcheck dotnet test tests/ClaudiaKermin.ScriptCheck.Tests --no-build $K

# KsCompile runs from the binary the build above made.
KS=$(ls -t tools/ClaudiaKermin.KsCompile/bin/*/*/ClaudiaKermin.KsCompile.dll 2>/dev/null | head -1)   # newest = the one just built
if [ -z "$KS" ]; then echo "FAIL ksc-binary not found under tools/ClaudiaKermin.KsCompile/bin"; exit 1; fi
start blocks dotnet "$KS" kerboscript tests/blocks
[ -d tests/tools ] && start tools dotnet "$KS" kerboscript tests/tools

# Every forbidden-call or syntax-error fixture must be rejected: one call, which names each failing file.
fixtures=$(find tools tests \( -name forbidden-call.ks -o -name syntax-error.ks \) | grep -v /bin/ | grep -v /obj/)
if [ -n "$fixtures" ]; then start fixtures dotnet "$KS" $fixtures; fi

report core; report kerboscript; report kerboscript-loc; report scriptcheck; report blocks
[ -d tests/tools ] && report tools
if [ -n "$fixtures" ]; then
  collect fixtures
  for f in $fixtures; do
    if echo "$out" | grep -qxF "FAIL $f"; then echo "PASS fixture $f rejected"; else echo "FAIL fixture $f passed"; fail=1; fi
  done
  [ "$rc" -ne 1 ] && { echo "FAIL fixtures: KsCompile exited $rc, expected 1"; fail=1; }
fi
exit $fail
