#!/bin/bash
# dotnet test, then one rerun of only the tests that failed (#1067). Same arguments as `dotnet test`: dotnet-test.sh PROJECT --no-build ... [--filter F]
# A test that fails and then passes on the rerun is FLAKY: the step passes and prints a `FLAKY <test>` line for it. A test
# that fails again fails the step. Every other failure (no test named: a crash, a build error) is not rerun.
# The rerun takes the failed tests' names from dotnet's console output ("Failed Namespace.Class.Method(args) [n ms]"); a Theory
# is rerun as a whole (every case), since a case's arguments can't go in a filter. The last Passed!/Failed! line printed is a
# summary for the whole run (the totals of the first run, with the failures that remain), which gates.sh and kerboscript.sh read.
# Which tests failed in a run: their names, one per line, without the arguments and the time.
failed_names() { sed -nE 's/^ *Failed ([^!].*)$/\1/p' | sed -E 's/ \[[<0-9. ,]+ ?m?s\]$//; s/\(.*$//' | sort -u; }
count() { echo "$2" | grep -oE "$1: +[0-9]+" | grep -oE '[0-9]+$' | head -1; }

out=$(dotnet test "$@" 2>&1); rc=$?
echo "$out"
[ $rc -eq 0 ] && exit 0
names=$(echo "$out" | failed_names)
[ -n "$names" ] || exit $rc   # nothing named, nothing to rerun

args=(); skip=0
for a in "$@"; do
  if [ $skip -eq 1 ]; then skip=0; continue; fi
  if [ "$a" = --filter ]; then skip=1; continue; fi
  args+=("$a")
done
filter=$(echo "$names" | sed 's/^/FullyQualifiedName~/' | paste -sd'|' -)
echo "--- rerunning the $(echo "$names" | wc -l | tr -d ' ') failed test(s) once ---"
out2=$(dotnet test "${args[@]}" --filter "($filter)&Category!=Quarantine" 2>&1); rc2=$?
echo "$out2"
still=$(echo "$out2" | failed_names)
while IFS= read -r n; do echo "$still" | grep -qxF "$n" || echo "FLAKY $n"; done <<< "$names"
first=$(echo "$out" | grep -E 'Passed!|Failed!' | tail -1)
total=$(count Total "$first"); skipped=$(count Skipped "$first"); nfail=0
[ $rc2 -ne 0 ] && nfail=$(echo "$still" | grep -c .)
[ $rc2 -ne 0 ] && [ "$nfail" -eq 0 ] && nfail=1   # the rerun failed without naming a test
[ $rc2 -eq 0 ] && verdict=Passed! || verdict=Failed!
echo "$verdict  - Failed: $nfail, Passed: $(( ${total:-0} - ${skipped:-0} - nfail )), Skipped: ${skipped:-0}, Total: ${total:-0} - after one rerun of the failed tests"
exit $rc2
