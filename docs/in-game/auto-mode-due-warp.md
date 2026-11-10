# Auto Mode approves at a Due Time the player warped to (#967)

If the player presses Warp to Due Time with Auto Mode on, Auto Mode approves the plan when that Due Time is reached. Core tests cover the rules (`MissionAutoModeTests`, the "A Due Time the player warped to" section). These checks need the game: the warp, the screen message, and save and load. Install and where to look are as in `phase-3-gate.md`.

You need a Mission whose Sequence Plan has a Due Time. A Duna Mission's launch is the easy one: pin "Orbit Duna", pick a window, Start and Approve the Mission Plan. The launch's Sequence Plan says "Waiting for the Duna window" and has a Due Time.

## Check 1: warp with Auto Mode on, and the plan flies on arrival

1. With the launch's Sequence Plan waiting and Auto Mode on, the first quick reply is Warp to Due Time. Press it.
2. The log has "Warping to the Due Time: Auto Mode approves it when the Due Time is reached and you're in the craft." The game warps.
3. On arrival warp drops to 1×. There is no "ready for your Approval" screen message or log line. The log has "Approved by Auto Mode at the Due Time." after the plan's card, then "Sequence Plan approved: the mod is starting the script.", and the script runs.

## Check 2: what still waits

1. Repeat Check 1 up to the warp, then turn Auto Mode off while warping. On arrival the log has "Due Time reached: ... is ready for your Approval." and Approve is on. Turn Auto Mode on again: it says "Auto Mode is waiting for you: a Due Time." and doesn't approve.
2. On a new plan with a Due Time and Auto Mode on, don't press Warp to Due Time. Warp there with the game's own warp buttons. On arrival it waits for the player the same way.
3. Press Warp to Due Time with Auto Mode on, then use Change something before arrival. When the revised plan arrives, warp there yourself with the game's warp: it waits for the player. Pressing Warp to Due Time again for the revised plan makes it Auto Mode's again.
5. Press Warp to Due Time with Auto Mode on during a Blackout (no link). There is no "Warping to the Due Time" line. Once the link is back, on arrival the plan waits for the player.
4. Press Warp to Due Time with Auto Mode on, then switch to another vessel (or go to the Space Center) before arrival. On arrival the quick reply is Go to the vessel and Auto Mode waits for a Due Time. Press Go to the vessel: back in the craft, the plan is approved with "Approved by Auto Mode at the Due Time."

## Check 3: save and load between warp and arrival

1. Press Warp to Due Time with Auto Mode on, then stop the warp part-way with the game's warp buttons.
2. Save, load and come back to the craft. Warp on to the Due Time with the game's warp. On arrival the plan is approved with "Approved by Auto Mode at the Due Time."

Tell the ticket whether the Auto Mode tooltip reads clearly: "... Transfer Window picks and Due Times still need you, but for a Coast Checkpoint's, or one you Warp to with Auto Mode on, when you're in the craft."
