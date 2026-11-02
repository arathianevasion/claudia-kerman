# finished_short passed up from composite blocks

`exec_node` reports `finished_short` when a burn stops a few percent short (#577). Blocks that fly an ejection through it and then go on, `transfer_to` and `return`, don't copy that key into their own results.

## Why this is out of scope

These blocks already report what the Debrief needs: whether the encounter, capture or reentry periapsis came out as planned (`encounter_ut`, `planet_pe`, `reentry_ut` and their success or reason). A short ejection that the block's correction absorbed changes nothing Claudia would do next. One that it couldn't absorb already shows up as a missed target.

Every documented result key is part of the block docs in the Mission prefix, which is sent with every request in a Mission. Spec #565's cost rule is that the prefix grows only for something that changes a decision, and this key doesn't.

## Prior requests

- #594: "transfer_to and return report the ejection's finished_short in their results"
