#!/bin/bash
# The Quarantine step of the merge gate (#1067). A test known to be flaky carries [Trait("Category", "Quarantine")] and a
# comment on that line or the two above it naming its tracking issue ("// Quarantine: #1234 why"). The other steps leave
# these tests out (Category!=Quarantine); this step runs them and prints the results, and a failing one never fails the gate.
# What does fail the gate: a Quarantine test with no tracking issue in its comment, or whose issue is closed (the flake was
# fixed, or the issue was closed without it: un-quarantine the test, or reopen the issue). If gh can't read an issue
# (offline, not signed in) that prints a WARN and doesn't fail, since the check can't tell.
# QUARANTINE_NO_RUN=1 only checks the issues (the tests of this script). GATES_ROOT overrides the repo root.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
K="-p:KOSPluginsDir=${KOS_PLUGINS_DIR:-$HOME/KSP_spike/GameData/kOS/Plugins}"
fail=0; n=0
files=$(grep -rlF 'Trait("Category", "Quarantine")' tests --include='*.cs' 2>/dev/null | sort)
for f in $files; do
  # One line per trait: "line<TAB>issue" (issue empty when the comment is missing).
  while IFS=$'\t' read -r line issue; do
    n=$((n + 1))
    if [ -z "$issue" ]; then echo "FAIL $f:$line Quarantine test has no tracking issue (put \"// Quarantine: #N\" on the line above it)"; fail=1; continue; fi
    state=$(${GATES_GH:-gh} issue view "$issue" --json state --jq .state 2>/dev/null)
    case $state in
      OPEN) echo "quarantined: $f:$line tracked by #$issue (open)" ;;
      CLOSED) echo "FAIL $f:$line tracking issue #$issue is closed: fix and un-quarantine the test, or reopen the issue"; fail=1 ;;
      *) echo "WARN $f:$line couldn't read the state of tracking issue #$issue; not checked" ;;
    esac
  done < <(awk '{ prev[NR] = $0 } /Trait\("Category", "Quarantine"\)/ {
      issue = ""; for (i = NR; i >= NR - 2 && i >= 1; i--) if (match(prev[i], /#[0-9]+/)) { issue = substr(prev[i], RSTART + 1, RLENGTH - 1); break }
      print NR "\t" issue }' "$f")
done
[ $n -eq 0 ] && echo "quarantine: no Quarantine tests"
if [ -z "${QUARANTINE_NO_RUN:-}" ] && [ $n -gt 0 ]; then
  # Each test project that has one, results printed; the exit status of these runs is ignored.
  for p in $(echo "$files" | cut -d/ -f1-2 | sort -u); do
    echo "--- Quarantine tests in $p (can't fail the gate) ---"
    dotnet test "$p" --no-build $K --filter "Category=Quarantine" 2>&1 | grep -E "Passed!|Failed!|^ *(Failed|Passed) " | head -50 |
      sed -E 's/^Failed!/quarantined tests failed, not gated:/; s/^ *Failed /  quarantined test failed (not gated): /'
  done
fi
exit $fail
