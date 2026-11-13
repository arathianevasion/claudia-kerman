#!/bin/bash
# The Kerboscript tests as several test processes at once (#625). kOS.Safe keeps its state in statics, so inside one
# process every kOS run takes KosHarness.Gate (#681) and the project uses one core. Each process here has its own kOS,
# so the shards are isolated by process and never share that state.
# It lists the tests, deals the test methods out to SHARDS processes (default: one per performance core on Apple Silicon, else nproc) so each gets
# about as many test cases and a slice of every class, and runs them at once. The total run must equal the number listed,
# so no test is dropped or run twice. Prints each shard's summary, then a total in dotnet test's own format (gates.sh
# reads the last Passed!/Failed! line). Exits non-zero if any shard failed.
#   tools/gates/kerboscript.sh            (after a build; it runs --no-build)
#   SHARDS=1 tools/gates/kerboscript.sh   (one process, as before #625)
# Runs on the checkout it lives in (the repo root is two levels up); GATES_ROOT overrides that.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=${KOS_PLUGINS_DIR:-$HOME/KSP_spike/GameData/kOS/Plugins}"   # KOS_PLUGINS_DIR: where kOS.Safe.dll is (CI: the kOS release)
P=tests/ClaudiaKerman.Kerboscript.Tests
SHARDS=${SHARDS:-$(sysctl -n hw.perflevel0.physicalcpu 2>/dev/null || nproc 2>/dev/null || echo 4)}

if ! listed=$(dotnet test $P --no-build $K --list-tests 2>&1); then echo "Failed! listing the tests"; echo "$listed" | tail -20; exit 1; fi
# One "cases method" line per test method (a Theory lists once per case), most cases first.
methods=$(echo "$listed" | awk '/^The following Tests are available:/ { on = 1; next } on && /^    / { sub(/^ +/, ""); sub(/\(.*/, ""); n[$0]++ }
  END { for (m in n) print n[m], m }' | sort -k1,1nr -k2,2)
expected=$(echo "$methods" | awk '{ s += $1 } END { print s + 0 }')
[ "$expected" -gt 0 ] || { echo "Failed! no test listed"; echo "$listed" | tail -20; exit 1; }
# Each method to the shard with the fewest cases so far: one filter per line.
filters=()
while IFS= read -r f; do filters+=("$f"); done < <(echo "$methods" | awk -v n="$SHARDS" '
  { b = 0; for (i = 1; i < n; i++) if (load[i] < load[b]) b = i; load[b] += $1; f[b] = (f[b] == "" ? "" : f[b] "|") "FullyQualifiedName=" $2 }
  END { for (i = 0; i < n; i++) if (f[i] != "") print f[i] }')

dir=$(mktemp -d)
pids=()
# On any exit the shards still running are killed (background jobs ignore Ctrl-C), as in kerboscript-stress.sh.
cleanup() { [ ${#pids[@]} -gt 0 ] && kill "${pids[@]}" 2>/dev/null; wait 2>/dev/null; rm -rf "$dir"; }
trap cleanup EXIT
trap 'exit 130' INT TERM
for i in "${!filters[@]}"; do
  dotnet test $P --no-build $K --filter "${filters[$i]}" > "$dir/$i.out" 2>&1 &
  pids+=($!)
done

count() { echo "$2" | grep -oE "$1: +[0-9]+" | grep -oE '[0-9]+$' || echo 0; }
fail=0; failed=0; passed=0; skipped=0; total=0; logs=""
for i in "${!filters[@]}"; do
  wait "${pids[$i]}"; rc=$?
  out=$(cat "$dir/$i.out")
  line=$(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)
  if [ -z "$line" ]; then echo "shard $i: FAIL no summary (rc $rc)"; logs="$logs$(echo "$out" | tail -20)
"; fail=1; continue; fi
  echo "shard $i: $line"
  [ $rc -ne 0 ] && { fail=1; logs="$logs$out
"; }
  failed=$((failed + $(count Failed "$line"))); passed=$((passed + $(count Passed "$line")))
  skipped=$((skipped + $(count Skipped "$line"))); total=$((total + $(count Total "$line")))
done
pids=()
if [ $total -ne "$expected" ]; then echo "FAIL ran $total tests, listed $expected"; fail=1; fi
[ $fail -eq 0 ] && verdict=Passed! || verdict=Failed!
summary="$verdict  - Failed: $failed, Passed: $passed, Skipped: $skipped, Total: $total - ClaudiaKerman.Kerboscript.Tests.dll in ${#filters[@]} processes"
echo "$summary"
# Each failing shard's log follows the total (so gates.sh's first 20 error lines include it), then the total again as
# the last line, which gates.sh reads.
[ -n "$logs" ] && { echo "$logs"; echo "$summary"; }
exit $fail
