#!/bin/bash
# Tests for ci-pass-record.sh (#1050), with a fake `gh` on PATH. Run: bash tools/gates/test-ci-pass-record.sh
HERE=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT; mkdir "$tmp/bin"
# Fake gh: echoes the URL it was asked for, then the real jq filter applied to $FAKE_JSON.
cat > "$tmp/bin/gh" <<'EOS'
#!/bin/bash
echo "$2" >> "$FAKE_LOG"
jq -r "$4" <<< "$FAKE_JSON"
EOS
chmod +x "$tmp/bin/gh"; export PATH="$tmp/bin:$PATH" FAKE_LOG="$tmp/log" GITHUB_REPOSITORY=o/r
bad=0
check() { if eval "$2"; then echo "PASS $1"; else echo "FAIL $1 ($out)"; bad=1; fi; }
cd "$HERE/../.."
out=$(GATES_NO_KSP=1 KOS_RELEASE_TAG=1.6.0.1 bash "$HERE/ci-pass-record.sh" key); a=$out
check "key names the tree and a settings hash" '[[ "$out" == gates-pass-$(git rev-parse "HEAD^{tree}")-???????????? ]]'
out=$(GATES_NO_KSP=1 KOS_RELEASE_TAG=1.6.0.1 bash "$HERE/ci-pass-record.sh" key)
check "same settings, same key" '[ "$out" = "$a" ]'
out=$(GATES_NO_KSP=1 KOS_RELEASE_TAG=1.7.0 bash "$HERE/ci-pass-record.sh" key)
check "a new kOS tag misses" '[ "$out" != "$a" ]'
out=$(GATES_NO_KSP=0 KOS_RELEASE_TAG=1.6.0.1 bash "$HERE/ci-pass-record.sh" key)
check "GATES_NO_KSP changes the key" '[ "$out" != "$a" ]'
export FAKE_JSON='{"artifacts":[{"expired":false,"created_at":"2026-01-01T00:00:00Z","workflow_run":{"id":11}},{"expired":false,"created_at":"2026-01-02T00:00:00Z","workflow_run":{"id":22}},{"expired":true,"created_at":"2026-01-03T00:00:00Z","workflow_run":{"id":33}}]}'
out=$(bash "$HERE/ci-pass-record.sh" lookup k); check "lookup: newest unexpired run id" '[ "$out" = 22 ]'
grep -q 'name=k' "$FAKE_LOG" && echo "PASS lookup asks for the key by name" || { echo "FAIL lookup url"; bad=1; }
export FAKE_JSON='{"artifacts":[]}'
out=$(bash "$HERE/ci-pass-record.sh" lookup k); check "lookup: none found prints nothing" '[ -z "$out" ]'
exit $bad
