#!/bin/bash
# The merge gate (#623): one build, then every test and Script Check on the built output. No Category=Timing test runs
# here (tools/gates/gates-timing.sh runs those, once per wave, on an idle machine).
# Prints one line per gate and exits non-zero on any failure.
# After the build the steps run at once (#702): each writes its output to a file in a temp directory, then the lines are
# printed in the fixed order below. GATES_SERIAL=1 runs them one after another instead (the order of the lines is the same).
# Runs on the checkout it lives in (the repo root is two levels up); GATES_ROOT overrides that.
# KOS_PLUGINS_DIR names the folder holding kOS.Safe.dll (default ~/KSP_spike/GameData/kOS/Plugins). GATES_NO_KSP=1 builds
# every project except those that need KSP's own assemblies (<NeedsKSP>true: the Adapter and the kOS addon) and prints a
# SKIP line for them: CI uses it (#1004), since KSP's assemblies can't go on a public runner. Everything else runs as here.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=${KOS_PLUGINS_DIR:-$HOME/KSP_spike/GameData/kOS/Plugins}"   # KOS_PLUGINS_DIR: where kOS.Safe.dll is (CI: the kOS release)
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
  if [ "${GATES_SERIAL:-}" = 1 ]; then if [ "$name" = fixtures ] || [ "$name" = undefined-fixture ]; then collect "$name"; else report "$name"; fi; fi; }
# collect NAME: waits for the job once; leaves its status in $rc and its output in $out.
collect() { [ -e "$dir/$1.rc" ] || { wait "$(cat "$dir/$1.pid")"; echo $? > "$dir/$1.rc"; }; rc=$(cat "$dir/$1.rc"); out=$(cat "$dir/$1.out"); }
# report NAME: prints the PASS or FAIL lines for a job (once), in the format the callers grep.
report() { [ -e "$dir/$1.shown" ] && return; : > "$dir/$1.shown"; collect "$1"
  if [ "$rc" -eq 0 ]; then echo "PASS $1 $(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)"; else echo "FAIL $1"; echo "$out" | grep -E 'error|Failed!|FAIL' | head -20; echo "$out" | failed_tests; fail=1; fi; }

# A tree that already passed is not gated again (#1005). Each pass records the committed tree it ran on, with the settings
# below, in the repository's shared git directory, which every worktree of the clone sees. So when the orchestrator merges a
# branch and main's tree comes out identical to the tree the agent gated in its worktree, the second run is skipped. Only a
# clean checkout counts: no change, tracked or untracked, in anything the build or the tests read. GATES_NO_CACHE=1 runs anyway.
record="$(git rev-parse --git-common-dir 2>/dev/null)/claudia-gates-passed"
gate_key() {
  [ -z "$(git status --porcelain --untracked-files=all -- src tests tools kerboscript ClaudiaKerman.sln 'Directory.Build.*' global.json 2>/dev/null)" ] || return 1
  echo "$(git rev-parse 'HEAD^{tree}') no_ksp=${GATES_NO_KSP:-0} $K sdk=$(dotnet --version 2>/dev/null)"
}
key=$(gate_key)
if [ -n "$key" ] && [ "${GATES_NO_CACHE:-}" != 1 ] && [ -f "$record" ]; then
  hit=$(awk -F'\t' -v k="$key" '$1 == k { line = $0 } END { print line }' "$record")
  if [ -n "$hit" ]; then
    echo "PASS cached: this tree ($(echo "$key" | cut -c1-12)) passed gates.sh $(echo "$hit" | cut -f2) in $(echo "$hit" | cut -f3) at $(echo "$hit" | cut -f4) (GATES_NO_CACHE=1 runs it again)"
    exit 0
  fi
fi

target=ClaudiaKerman.sln; skipped_ksp=""
if [ "${GATES_NO_KSP:-}" = 1 ]; then
  # A solution filter of every project in the solution but the <NeedsKSP>true ones, made fresh so a new project is included.
  keep=(); for p in $(grep -oE '"[^"]+\.csproj"' ClaudiaKerman.sln | tr -d '"' | tr '\\' /); do
    if grep -q '<NeedsKSP>true</NeedsKSP>' "$p"; then skipped_ksp="$skipped_ksp $(basename "$p" .csproj)"; else keep+=("\"$p\""); fi; done
  target="$dir/no-ksp.slnf"
  printf '{"solution":{"path":"%s","projects":[%s]}}\n' "$ROOT/ClaudiaKerman.sln" "$(IFS=,; echo "${keep[*]}")" > "$target"
fi
start build dotnet build "$target" -warnaserror $K
report build
[ -n "$skipped_ksp" ] && echo "SKIP build of the projects that need KSP:$skipped_ksp (GATES_NO_KSP=1)"
[ $fail -ne 0 ] && exit 1   # nothing below can run on a broken build

start core dotnet test tests/ClaudiaKerman.Core.Tests --no-build $K --filter "Category!=Timing"
start kerboscript bash tools/gates/kerboscript.sh   # several test processes at once (#625)
start kerboscript-loc bash tools/gates/kerboscript-loc.sh   # the library may not grow (#705, ADR 0014)
start scriptcheck dotnet test tests/ClaudiaKerman.ScriptCheck.Tests --no-build $K

# KsCompile runs from the binary the build above made.
KS=$(ls -t tools/ClaudiaKerman.KsCompile/bin/*/*/ClaudiaKerman.KsCompile.dll 2>/dev/null | head -1)   # newest = the one just built
if [ -z "$KS" ]; then report core; report kerboscript; report kerboscript-loc; report scriptcheck; echo "FAIL ksc-binary not found under tools/ClaudiaKerman.KsCompile/bin"; exit 1; fi
start blocks dotnet "$KS" kerboscript tests/blocks
[ -d tests/tools ] && start tools dotnet "$KS" kerboscript tests/tools
start undefined-calls dotnet "$KS" --undefined-calls tests/blocks tests/tools   # a test script calling a function nothing defines (#928)
start undefined-fixture dotnet "$KS" --undefined-calls tools/ClaudiaKerman.KsCompile/fixtures/undefined-call.ks

# Every forbidden-call or syntax-error fixture must be rejected: one call, which names each failing file.
fixtures=$(find tools tests \( -name forbidden-call.ks -o -name syntax-error.ks \) | grep -v /bin/ | grep -v /obj/)
if [ -n "$fixtures" ]; then start fixtures dotnet "$KS" $fixtures; fi

report core; report kerboscript; report kerboscript-loc; report scriptcheck; report blocks
[ -d tests/tools ] && report tools
report undefined-calls
# The fixture must fail, naming the function and its line, or the gate above could be passing for nothing.
collect undefined-fixture
if [ "$rc" -eq 1 ] && echo "$out" | grep -qF "undefined-call.ks line 4: deorbit_ksc"; then echo "PASS fixture undefined-call.ks rejected"; else echo "FAIL fixture undefined-call.ks passed or didn't name line 4: deorbit_ksc"; fail=1; fi
if [ -n "$fixtures" ]; then
  collect fixtures
  for f in $fixtures; do
    if echo "$out" | grep -qxF "FAIL $f"; then echo "PASS fixture $f rejected"; else echo "FAIL fixture $f passed"; fail=1; fi
  done
  [ "$rc" -ne 1 ] && { echo "FAIL fixtures: KsCompile exited $rc, expected 1"; fail=1; }
fi
# Record the pass only if the checkout was clean when the run started and still is (#1005).
if [ $fail -eq 0 ] && [ -n "$key" ] && [ "$(gate_key)" = "$key" ]; then
  printf '%s\t%s\t%s\t%s\n' "$key" "$(date -u +%Y-%m-%dT%H:%MZ)" "$ROOT" "$(git rev-parse --short HEAD)" >> "$record"
fi
exit $fail
