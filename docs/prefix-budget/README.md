# Mission prefix budget ledger

`MissionPrefixBudgetTests` holds two constants, `TodaysEstimate` (the measured figure) and `BudgetTokens` (the cap, never raised without a deliberate decision). The per-ticket history lives here, not in the test, so two branches that each change the prefix conflict only on the `TodaysEstimate` line.

**Each ticket that changes the prefix** adds `docs/prefix-budget/<ticket>.md` (a new file, so it never conflicts) with three lines: the delta in estimated tokens, the reason, and the measured figure on the tree merged with main. Then it sets `TodaysEstimate` to that figure. When `TodaysEstimate` is off, `TodaysEstimateIsTheMeasuredFigure` fails and its message prints the measured figure and the one-line change.

`git log -- docs/prefix-budget` shows the history from #623 on. The history before it is below, moved verbatim from the test's doc comment.

## History before #623

Set at 21,500 when the figure was 20,452 (1.05 x 20,452 = 21,475, rounded up to the next hundred), and not raised since:
#534's propose_mission_plan took the figure to 21,330, and #570's dropping check_script (917 characters, -306) with the Script Check sentence paid for inside HowToPlan (+1) to 21,019, and #577's finish_fraction docs for exec_node and
circularize (+113) to 21,132: one short burn finished instead of an Off-Nominal Debrief and corrective sequence saves $0.30 to $0.50.
#581's goal_steps on propose_mission_plan (169 characters, +57) to 21,189: the Mission Goal's steps are written once instead of
restated in every Debrief (about 260 characters each, read back on every later request), and the reply schema shrinks by about 70.
#599's falling-craft sentence (137 characters, +45) to 21,279: a craft that is falling with impact_in_s is told to thrust horizontal and up, never a
prograde node (Mission m...22cc died on one). The figure was 21,234 before it: #590 and #591 had added 45 since #581 without a record.
#557's reentry separate option, its failure and "drops the stack below the heat shield" in the catalog line (+54, paid down
by trimming reentry's own docs) to 21,288, measured against 21,234 for bece0db here: a reentry flown with the service module
on lost two Missions and cost b5f3 and 0de6 a corrective sequence each.
#597's ascend from flight (+58; this test read 21,234 on its base, not 21,189) to 21,346 merged with #557, then 21,335 after trimming ascend's summary line (-11) so BaseLayer fits GuidanceTests' cap: the abort to orbit for a falling
craft, which 22cc lost to a hand-written prograde node into the ground; one saved Mission outweighs a cache read per request.
Merged with #599 (its +45 on top of 21,335 from main) = 21,380 measured on the merged tree.
#596's drop_underhang option and early-drop rule in land's docs (+38) to 21,418, measured on the tree merged with main: a lander
that drops its transfer stage before the deorbit turns and brakes on its own, instead of hovering a stack dry and failing the leg.
#592's reentry "passes" result and its skip-out failures in one doc phrase (73 characters, +25) to 21,443 on the tree merged with main (21,418 + 25): a craft that skipped out ran
to max_duration and failed (b5f3, 0de6, 44e9), each a failed run, a Debrief and a new Sequence Plan.
#606's Script Check of Building Block options (+0, still 21,443): its problems are turn text, and no doc or tool description changed.
#610's capture refusal before a sphere-of-influence change and its transfer_to/tune_pe hints (+0, still 21,443): refusal
reasons and result fields are turn text, and no block doc changed.
#609's blocks trail (+0, still 21,443): Debrief turn text of about 20-40 tokens a block (12 at most), and no doc, prompt or schema changed.
#612's science sends data already held (+0, still 21,443): the new transmit, action and kept text in science's docs is paid for by trimming
the same block's docs (1,766 characters of //| lines before and after); the kept hints and the Debrief notice line are turn text.
#608's drop_parts allow_no_engine option, its stranding refusal and dropped_dv (-11, paid for by trimming drop_parts' own docs: two
self-evident failure strings and wordier lines) to 21,432: the refusal's detail is turn text. a107 dropped its engine with the periapsis at 601 km.
