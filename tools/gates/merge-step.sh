#!/bin/bash
# merge-step.sh N "msg" (#623): the orchestrator's merge of one ticket into main, with no Core rerun loop (the merge gate
# runs no Timing test, so a Core failure is a real failure). rc 2 = conflict, 3 = a gate failed, 0 = merged.
# REC_LOCAL names the script that merges the branch, runs the gates and cleans up (default: the .scratch rec-local.sh,
# which should call tools/gates/gates.sh); the repo root is two levels up from here, or GATES_ROOT.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$ROOT" || exit 1
n=$1; t=$(cd .scratch/v1/issues 2>/dev/null && ls $n-*.md 2>/dev/null | sed 's/\.md$//')
[ -n "$t" ] && [ "$(echo "$t" | wc -l)" -eq 1 ] || { echo "no single ticket file for '$n' under $ROOT/.scratch/v1/issues (run this on the main checkout)"; exit 1; }
REC_LOCAL=${REC_LOCAL:-.scratch/v1/run-tools/rec-local.sh}
out=$("$REC_LOCAL" "$t" "$2" merge 2>&1); rc=$?
echo "$n rc=$rc"; echo "$out" | tail -14
exit $rc
