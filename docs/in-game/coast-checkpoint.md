# Leaving a craft during a long coast (#775)

During a long coast of `transfer_to` or `return` the player can leave the craft. A KSP alarm calls them back before the next correction, and the mod checks the trajectory itself on their return. Core tests cover the rules (`CoastCheckpointTests`) and the resume mode (`TransferToFlightTests`). These checks need the game. Install and where to look are as in `phase-3-gate.md`.

**Block migration:** no logic moves out of Kerboscript. Every new decision is in Core (`CoastCheckpoints`, `TransferFlight`), and `transfer_to.ks` and `return.ks` get wiring only. The checks below are the player's flights; no `tests/blocks` scenario changes.

**The lead** before a correction is the turn time plus `BurnPoints.TurnMarginSeconds` (60 s), a few minutes. The Due Time is soft while the player is in the craft.

## Check 1: a Duna coast where the player leaves, takes the alarm and returns

1. Start a Mission from Kerbin orbit with the goal "Orbit Duna", with a second craft somewhere far away. Leave Auto Mode off. Approve as usual until `transfer_to` runs.
2. The block flies the ejection, leaves Kerbin's sphere of influence and flies the correction 5 % past its edge. It then stops on its own, with no warp to the mid-course correction. In `KSP.log`: `report from` the run with `checkpoint_ut`, then the run ends. The kOS terminal reads "Stopped at a Coast Checkpoint". Nothing after `transfer_to` in the script runs.
3. The log has a **Debrief: Nominal** card written by the mod (its Why says so), a Sequence Plan with the same name and step, and "Coast Checkpoint: the next correction is due ... Fly other Missions meanwhile; a KSP alarm calls you back." No request was made: the cost does not move.
4. Check: the Capcom strip reads "Waiting for the next correction: <date>", the KSP Alarm Clock has an alarm at that date, and the quick replies offer both Approve and Warp to Due Time.
5. Switch to the other craft through the map. Nothing asks first, as no run is going. Open the Missions list: the Mission shows "Due <date>: check pending".
6. Warp until the alarm fires. Warp drops. Use Go to, or switch by hand.
7. As soon as you are back in the craft: "Checked on return: the Duna encounter is kept: its periapsis is ... km. The resume stands, with no request." Still no request.
8. Approve. The script enters the flight at the coast with no ejection (`ejection_dv_used` stays -1) and flies the correction it stopped for.
9. Repeat from step 5 at the next stop, and come back a few days after the alarm instead: the line adds "You are ... late for the correction", and the correction burns as soon as the craft can turn.
10. To check the other path, before coming back nudge the craft by hand (a few m/s with the keyboard, on a third switch to it) so the map shows the encounter gone or the periapsis far off. Back in the craft, the mod's line says "lost" or "has drifted", and one request goes to Claudia with the figures, the lateness among them. Tell the ticket what she plans.

## Check 2: a Duna coast where the player stays with Auto Mode on

1. As Check 1, with Auto Mode on from the start of the transfer.
2. At each stop the mod's Debrief and resume appear and the next run starts at once, "Approved by Auto Mode", with no alarm left behind and no request. The script warps on rails to the correction.
3. In `KSP.log`, the wait's `block call` lines show the Encounter Watch checks (`ENCOUNTERWATCHDROPPED`) as in `transfer-to.md`.
4. During that warp, press the map's Switch To on the far craft. The prompt reads "The craft is coasting and safe to leave" with **Leave at a checkpoint** and **Stay**. Choose Leave: the same Debrief and alarm as Check 1, with no "interrupted" line, and the run's program is stopped. Back in the craft, the check runs, and Auto Mode starts the resume at once.
5. During a burn, or the turn to one, press Switch To: the old prompt, with **Switch** and **Stay**; Switch interrupts the run as before.
6. Note: leaving Flight for the Space Center or the Tracking Station during a run still asks first and interrupts, as today. Only a far switch can be a checkpoint. Once the block has stopped at a checkpoint, no run is going, so any way out is free.

Tell the ticket how the alarm's timing felt: the lead is short by design (the correction waits for a late player), so say if a longer one would have helped.
