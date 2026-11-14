#!/bin/bash
# Tests for the flaky-rerun and Quarantine steps of the merge gate (#1067), with a fake `dotnet` and `gh` on PATH so no
# real test runs. Run: bash tools/gates/test-flaky.sh   (prints PASS/FAIL per case; exits non-zero on any failure)
HERE=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin"
# Fake dotnet: the first call fails the tests named in $FAKE_FAIL_FIRST (space separated); a call whose filter names tests
# (FullyQualifiedName~) is a rerun and fails the ones named in $FAKE_FAIL_RERUN.
cat > "$tmp/bin/dotnet" <<'EOS'
#!/bin/bash
args="$*"
if [[ "$args" == *FullyQualifiedName~* ]]; then failing=$FAKE_FAIL_RERUN; echo "RERUN $args" >> "$FAKE_LOG"; else failing=$FAKE_FAIL_FIRST; echo "FIRST $args" >> "$FAKE_LOG"; fi
if [ -z "$failing" ]; then echo "Passed!  - Failed: 0, Passed: 10, Skipped: 1, Total: 11 - X.dll"; exit 0; fi
for t in $failing; do echo "  Failed $t [5 ms]"; echo "  Error Message:"; echo "   boom"; done
echo "Failed!  - Failed: $(echo $failing | wc -w | tr -d ' '), Passed: 8, Skipped: 1, Total: 11 - X.dll"; exit 1
EOS
chmod +x "$tmp/bin/dotnet"
export PATH="$tmp/bin:$PATH" FAKE_LOG="$tmp/log"
bad=0
check() { if eval "$2"; then echo "PASS $1"; else echo "FAIL $1"; echo "$out" | sed 's/^/    /'; bad=1; fi; }

: > "$FAKE_LOG"; out=$(FAKE_FAIL_FIRST="N.C.M1(x:1) N.C.M2" FAKE_FAIL_RERUN="" bash "$HERE/dotnet-test.sh" proj --no-build --filter "Category!=Timing"); rc=$?
check "fails once, passes on rerun: exit 0" '[ $rc -eq 0 ]'
check "flaky: a FLAKY line per test" 'echo "$out" | grep -qx "FLAKY N.C.M1" && echo "$out" | grep -qx "FLAKY N.C.M2"'
check "flaky: reruns only the failed tests, once" '[ "$(grep -c ^RERUN "$FAKE_LOG")" = 1 ] && grep ^RERUN "$FAKE_LOG" | grep -q "FullyQualifiedName~N.C.M1" && grep ^RERUN "$FAKE_LOG" | grep -q "FullyQualifiedName~N.C.M2"'
check "flaky: last summary line is Passed! with the full total" 'echo "$out" | grep -E "Passed!|Failed!" | tail -1 | grep -q "^Passed!.*Failed: 0, Passed: 10, Skipped: 1, Total: 11"'

: > "$FAKE_LOG"; out=$(FAKE_FAIL_FIRST="N.C.M1 N.C.M2" FAKE_FAIL_RERUN="N.C.M2" bash "$HERE/dotnet-test.sh" proj --no-build); rc=$?
check "fails twice: exit non-zero" '[ $rc -ne 0 ]'
check "fails twice: no FLAKY for it, FLAKY for the other" '! echo "$out" | grep -qx "FLAKY N.C.M2" && echo "$out" | grep -qx "FLAKY N.C.M1"'
check "fails twice: last summary line is Failed!" 'echo "$out" | grep -E "Passed!|Failed!" | tail -1 | grep -q "^Failed!"'

: > "$FAKE_LOG"; out=$(FAKE_FAIL_FIRST="" bash "$HERE/dotnet-test.sh" proj --no-build); rc=$?
check "green first time: exit 0, no rerun, no FLAKY" '[ $rc -eq 0 ] && ! grep -q ^RERUN "$FAKE_LOG" && ! echo "$out" | grep -q FLAKY'

: > "$FAKE_LOG"; cat > "$tmp/bin/dotnet" <<'EOS'
#!/bin/bash
echo "error CS1002: ; expected"; echo "Failed!  - nothing ran"; exit 1
EOS
out=$(bash "$HERE/dotnet-test.sh" proj --no-build); rc=$?
check "fails with no failed test named (e.g. a crash): fails, no rerun" '[ $rc -ne 0 ] && ! echo "$out" | grep -q FLAKY'

# --- quarantine.sh: the tracking-issue check (the test run itself is faked above and not repeated here)
mk() { rm -rf "$tmp/repo"; mkdir -p "$tmp/repo/tests/ClaudiaKerman.Core.Tests"; printf '%s\n' "$@" > "$tmp/repo/tests/ClaudiaKerman.Core.Tests/QTests.cs"; }
cat > "$tmp/bin/gh" <<'EOS'
#!/bin/bash
# gh issue view N --json state --jq .state
n=$3; case $n in 100) echo OPEN ;; 200) echo CLOSED ;; *) exit 1 ;; esac
EOS
chmod +x "$tmp/bin/gh"
q() { GATES_ROOT="$tmp/repo" QUARANTINE_NO_RUN=1 bash "$HERE/quarantine.sh"; }
mk '// Quarantine: #100 flaky on slow disks' '[Trait("Category", "Quarantine")]' 'public void A() {}'
out=$(q); rc=$?; check "open tracking issue: pass, test reported" '[ $rc -eq 0 ] && echo "$out" | grep -q "QTests.cs.*#100"'
mk '// Quarantine: #200' '[Trait("Category", "Quarantine")]' 'public void A() {}'
out=$(q); rc=$?; check "closed tracking issue fails the gate" '[ $rc -ne 0 ] && echo "$out" | grep -q "#200.*closed"'
mk '[Trait("Category", "Quarantine")]' 'public void A() {}'
out=$(q); rc=$?; check "no tracking issue fails the gate" '[ $rc -ne 0 ] && echo "$out" | grep -qi "no tracking issue"'
mk '// Quarantine: #300' '[Trait("Category", "Quarantine")]' 'public void A() {}'
out=$(q); rc=$?; check "issue that can't be read: warns, does not fail" '[ $rc -eq 0 ] && echo "$out" | grep -q WARN'
mk 'public void A() {}'
out=$(q); rc=$?; check "no Quarantine tests: pass" '[ $rc -eq 0 ]'
exit $bad
