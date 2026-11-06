#!/bin/bash
# The Kerboscript line ratchet (#705, ADR 0014): the raw line count of kerboscript/library/*.ks (comments and //| doc lines
# included; kslib, debug and dock_hold are not counted) must not exceed the limit in kerboscript-loc.txt.
# A ticket that shrinks the library lowers the limit in the same commit; one that must grow it raises the limit and gives a reason in its commit message.
ROOT=${GATES_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
limit=$(tr -d '[:space:]' < "$ROOT/tools/gates/kerboscript-loc.txt")
total=$(cat "$ROOT"/kerboscript/library/*.ks | wc -l | tr -d '[:space:]')
if [ "$total" -gt "$limit" ]; then
  echo "FAIL kerboscript-loc $total lines in kerboscript/library, limit $limit (ADR 0014: move flight logic to C#, or raise tools/gates/kerboscript-loc.txt with a reason in the commit message)"
  exit 1
fi
echo "PASS kerboscript-loc $total lines in kerboscript/library, limit $limit"
