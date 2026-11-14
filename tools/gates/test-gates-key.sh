#!/bin/bash
# Tests for the pass record's key (#1075), with a fake `dotnet` on PATH so no real build runs: a miss reaches the build, which
# the fake fails, so "FAIL build" means the gate ran in full and "PASS cached" means a hit.
# Run: bash tools/gates/test-gates-key.sh   (prints PASS/FAIL per case; exits non-zero on any failure)
HERE=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
mkdir "$tmp/bin"
printf '#!/bin/bash\n[ "$1" = --version ] && { echo 9.0.0; exit 0; }\necho "error: fake build"; exit 1\n' > "$tmp/bin/dotnet"; chmod +x "$tmp/bin/dotnet"
export PATH="$tmp/bin:$PATH" GATES_ROOT="$tmp/repo" GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@t
unset GATES_NO_CACHE GATES_ONLY
bad=0
check() { if eval "$2"; then echo "PASS $1"; else echo "FAIL $1"; echo "$out" | sed 's/^/    /'; bad=1; fi; }
g() { git -C "$tmp/repo" "$@" >/dev/null; }
commit() { mkdir -p "$(dirname "$tmp/repo/$1")"; echo "$2" > "$tmp/repo/$1"; g add -A; g commit -m "$1"; }
run() { bash "$HERE/gates.sh" 2>&1; }

mkdir "$tmp/repo"; g init -q; commit src/a.cs 1; commit tests/t.cs 1; commit docs/d.md 1; commit README.md 1
rec="$tmp/repo/.git/claudia-gates-passed"
key=$(GATES_PRINT_KEY=1 bash "$HERE/gates.sh")
printf '%s\tnow\t%s\tabc\n' "$key" "$tmp/repo" > "$rec"

out=$(run); check "the recorded tree is a hit" 'echo "$out" | grep -q "^PASS cached"'
commit docs/d.md 2; out=$(run); check "a commit changing only docs/ is a hit" 'echo "$out" | grep -q "^PASS cached"'
commit docs/new/e.md 1; commit README.md 2; commit CHANGELOG.md 1
out=$(run); check "new docs files and root markdown are hits" 'echo "$out" | grep -q "^PASS cached"'
commit tests/t.cs 2; out=$(run); check "a commit changing tests/ runs in full" '! echo "$out" | grep -q "PASS cached" && echo "$out" | grep -q "FAIL build"'
commit src/a.cs 2; commit Directory.Build.props 1; k2=$(GATES_PRINT_KEY=1 bash "$HERE/gates.sh")
check "src and the build props change the key" '[ "$k2" != "$key" ]'

# A record under the old key (the whole tree's hash, no prefix) is never a hit, even for the tree it names.
old="$(git -C "$tmp/repo" rev-parse 'HEAD^{tree}') ${k2#* }"
printf '%s\tnow\t%s\tabc\n' "$old" "$tmp/repo" > "$rec"
out=$(run); check "an old-format record is not a hit" '! echo "$out" | grep -q "PASS cached" && echo "$out" | grep -q "FAIL build"'
printf '%s\tnow\t%s\tabc\n' "$k2" "$tmp/repo" > "$rec"
out=$(run); check "a new-format record for it is a hit" 'echo "$out" | grep -q "^PASS cached"'
echo x > "$tmp/repo/tests/untracked.cs"; out=$(run); check "an untracked file under tests/ is not a hit" '! echo "$out" | grep -q "PASS cached"'
exit $bad
