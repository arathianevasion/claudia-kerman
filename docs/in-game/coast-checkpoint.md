# Leaving a craft during a long coast (#775)

During a long coast of `transfer_to` or `return` the player can leave the craft. A KSP alarm calls them back before the next correction, and the mod checks the trajectory itself on their return. Core tests cover the rules (`CoastCheckpointTests`) and the resume mode (`TransferToFlightTests`). These checks need the game. Install and where to look are as in `phase-3-gate.md`.

**Block migration:** no logic moves out of Kerboscript. Every new decision is in Core (`CoastCheckpoints`, `TransferFlight`), and `transfer_to.ks` and `return.ks` get wiring only. The checks below are the player's flights. `tests/blocks/transfer_to_duna`, `return`, `return_ike` and `return_target` are unchanged and must still PASS: a block test's run never checkpoints, as only a Mission's bundle sets the option.

**The lead** before a correction is the turn time plus `BurnPoints.TurnMarginSeconds` (60 s), a few minutes. The Due Time is soft while the player is in the craft.

## Check 1: a Duna coast where the player leaves, takes the alarm and returns

1. Start a Mission from Kerbin orbit with the goal "Orbit Duna", with a second craft somewhere far away. Leave Auto Mode off. Approve as usual until `transfer_to` runs.
2. The block flies the ejection, leaves Kerbin's sphere of influence and flies the correction 5 % past its edge. It then stops on its own, with no warp to the next Course Correction. In `KSP.log`: `report from` the run with `checkpoint_ut`, then the run ends. The kOS terminal reads "Stopped at a Coast Checkpoint". Nothing after `transfer_to` in the script runs.
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
6. Note: the main menu and quitting still interrupt the run, as today (with the Leave/Stay dialog). A revert to the VAB or SPH shows no dialog at all and interrupts the run; revert never checkpoints. Once the block has stopped at a checkpoint, no run is going, so any way out is free.
## Check 3: leaving Flight for the Space Center or the Tracking Station mid-coast (#777)

1. As Check 1, step 1 to the Duna coast, with the run warping on rails and the safety test passing.
2. Press Esc and choose Space Center. The prompt reads "The craft is coasting and safe to leave" with **Leave at a checkpoint** and **Stay**. Stay: nothing changes and the run goes on.
3. Try again and choose Leave at a checkpoint. The Space Center loads. In the Missions list the Mission shows "Due ...: check pending", a KSP alarm is in the Alarm Clock, and the log has the mod's Nominal Debrief and no "interrupted" line. Quit to the main menu and reload the save at this point once to be sure: the Mission is still at the checkpoint with its Due Time.
4. Launch something else from the Space Center (VAB, then Launch), fly it a while, then take the alarm (or open the Tracking Station and Fly the Duna craft). Back in the craft: "Checked on return: ... is kept ..." with no request, as Check 1. Approve (or let Auto Mode start it): the correction burns.
5. Repeat with the Tracking Station instead of the Space Center.
6. Make the test fail (nudge the craft by hand so the encounter is lost, or cross a Telemetry Check) and choose Space Center again: the prompt is the old one with **Leave Flight** and the reason ("It isn't safe to leave at a checkpoint now: ..."), and Leave Flight interrupts the run as before.
7. Mid-coast, press Esc and choose Main Menu: the Leave/Stay dialog with no checkpoint, and the run is interrupted. Separately, Revert Flight (to VAB): no dialog at all, the run is interrupted, and no checkpoint. Revert to launch (Flight to Flight) never went through the guard and is unchanged.

Tell the ticket how the alarm's timing felt: the lead is short by design (the correction waits for a late player), so say if a longer one would have helped.
