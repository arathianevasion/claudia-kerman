# Auto Mode carries on at a reached Due Time (#967)

With Auto Mode on and the player in the Mission's craft, Auto Mode approves the plan in hand when its Due Time is reached: landed, orbiting or escaping, in a Blackout or not, whether or not Warp to Due Time was pressed, and whenever Auto Mode was ticked (fix round 2, the player's ruling). Core tests cover the rules (`MissionAutoModeTests`, "A reached Due Time"; `WindowWaitTests`, `WindowWaitSurfaceTests`, `WindowChoiceDepartureTests`, `CoastCheckpointTests`). These checks need the game: the warp, the screen message, and the two liftoffs that waited in flight m20261111-172716-a40a. Install and where to look are as in `phase-3-gate.md`.

## Check 1: liftoff from the Kerbin pad starts at its Due Time

1. Pin "Orbit Duna", pick a window, Start and Approve the Mission Plan with Auto Mode **off**. The launch's Sequence Plan says "Waiting for the Duna window" and has a Due Time.
2. Press Warp to Due Time, then tick Auto Mode while the game warps.
3. On arrival warp drops to 1×. There is no "ready for your Approval" screen message or log line and no Approve to press. The log has "Approved by Auto Mode at the Due Time." after the plan's card, then "Sequence Plan approved: the mod is starting the script.", and the rocket lifts off.

## Check 2: liftoff from Duna starts at its Due Time

1. Landed on Duna with Auto Mode on, the ascent's Sequence Plan waits with a Due Time (pick a Transfer Window if asked; the Soonest pick is the one that waited in flight).
2. Press Warp to Due Time. The log has "Warping to the Due Time: Auto Mode approves it there if you're in the craft."
3. On arrival there is no Approve: the log has "Approved by Auto Mode at the Due Time." and the ascent starts.

## Check 3: no warp pressed

1. On a plan with a Due Time and Auto Mode on, don't press Warp to Due Time; warp there with the game's own warp buttons. On arrival the plan is approved the same way, with no Approve.

## Check 4: what still waits

1. Press Warp to Due Time with Auto Mode on, then turn Auto Mode off while warping. On arrival the log has "Due Time reached: ... is ready for your Approval." and Approve is on. Ticking Auto Mode again then approves it.
2. Press Warp to Due Time with Auto Mode on, then switch to another vessel (or go to the Space Center) before arrival. On arrival the quick reply is Go to the vessel and Auto Mode is waiting for a Due Time. Press Go to the vessel: back in the craft, the plan is approved with "Approved by Auto Mode at the Due Time."
3. A Manual stretch with a Due Time still waits for Approve.

## Check 5: a Due Time reached in a Blackout

1. On a Duna return whose Due Time falls while the craft is behind Duna (no link), with Auto Mode on, warp to the Due Time (the button or the game's warp). The log has "No link to Kerbin: Blackout." on the way.
2. On arrival, inside the Blackout, there is no "ready for your Approval" line or Approve. The log has "Approved by Auto Mode at the Due Time." and the script runs.
3. When the link is back, the "While there was no link" card lists the plan as flown without Approval.

## Check 6: a Coast Checkpoint in a Blackout

1. On a Duna transfer under Auto Mode, let the coast reach a Coast Checkpoint while there is no link, with the player in the craft. The resumed sequence starts at once, as it does with a link.

Tell the ticket whether the Auto Mode tooltip reads clearly: "... Manual stretches and Transfer Window picks still need you. At a Due Time it carries on if you're in the craft."
