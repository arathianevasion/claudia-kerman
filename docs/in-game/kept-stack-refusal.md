# Script Check refuses a kept stack on reentry without a reason (#971)

A `reentry` with `separate: "none"` keeps the whole stack, so the Script Check refuses it unless the Sequence Plan's `custom_script_reason` is non-empty (#959 only warned, and Claudia kept a 41 t stack on Duna anyway). Tests cover the rules (`KosScriptCheckTests`, `MissionPersistenceTests`); this check needs the game, for Claudia's revision. Install and where to look are as in `phase-3-gate.md`.

## Check 1: no reason is refused, and Claudia revises

1. Fly a craft whose Dart stage lands and lifts off, with a decoupled stack below its heat shield (the Duna craft of flight m20261109-200745-96f6 will do). Pin a Mission Goal that ends in a landing, such as "Land on Duna and return".
2. Wait for the Sequence Plan that calls `reentry`. If Claudia drops the stack ("auto"), it passes and there is nothing to refuse; add a note at the Mission Plan or Sequence Plan, such as "Keep the stack, nothing gets dropped", to push her to `separate: "none"`.
3. When her script has `separate: "none"` and no reason, the player never sees that plan: the Mission's log shows one automatic rewrite. In the newest request dump the correction carries the line `line N: reentry with separate "none" keeps the whole stack, and the plan gives no custom_script_reason. Either drop the stack: leave separate off or set it to "auto"...`.
4. The revised plan either leaves `separate` off or sets "auto", or keeps "none" with a reason. A second failure shows "Couldn't produce a safe script" with the same line.
5. Tell the ticket which she chose, and whether the reason she gave (if any) is true for the craft.

## Check 2: with a reason it passes with the warning

1. Repeat, and make the stack's staying the point of the note: "The whole stack lands and flies back; give the reason."
2. The plan is offered, and the Mission window has a "Script Check: line N: reentry with separate "none" keeps the whole stack..." event.
3. Reload the save at that Sequence Plan: it is offered again (the reason is saved as `reason_given`).

A pending plan from a save made before this change, with separate "none" and a reason, is refused on restore ("Couldn't produce a safe script") and needs Retry.
