# Waiting for a Transfer Window on the ground (#774)

A long wait for a Transfer Window is spent on the ground, under a Due Time with a KSP alarm. Core only: no prompt, tool text or block changed. Core tests cover the rule (`WindowWaitTests`); these checks need the game. Install and where to look are as in `phase-3-gate.md`.

**The ascent lead** is what the ascent's Due Time leaves before the departure: 600 s for the ascent, one low orbit of the body to reach the burn point, and 900 s of margin (`WindowWaits.AscentLeadS`). That is about 70 minutes from Duna or Ike. From a planet the ascent is due at DeparturePlane's launch into an orbit holding the asymptote (`at_ut` in `plan_transfer`) when that is earlier, so up to one turn of the body earlier. From a moon no launch plane applies, and the departure is the burn out of its orbit.

## Check 1: a goal at Ike, on the pad

1. On the pad in a new game, with a craft that can reach Ike, start a Mission with the goal "Go to Ike".
2. Approve the Mission Plan. When the launch's Sequence Plan arrives, the log has "Waiting for the Duna window: the launch is due ...", an hour before the ejection burn.
3. The Capcom strip reads "Waiting for the Duna window: <date>", the Missions list shows it as Due, and the KSP Alarm Clock has an alarm at that date. Approve before then starts nothing.
4. Do the same with "Orbit the Mun": no Due Time, and the launch can be approved at once.

## Check 2: landed on Duna, returning to Kerbin

1. Land a craft on Duna, by a flight or a save edit, with the Kerbin window more than a day off. Start a Mission with the goal "Return to Kerbin". Check that the plan is an ascent and then a return or transfer to Kerbin.
2. When the ascent's Sequence Plan arrives, the log has "Waiting for the Kerbin window: the ascent is due ..., <span> before the departure". The strip, the Missions list and the KSP alarm show that date.
3. Warp to the alarm. Warp drops, and the Mission says the ascent is ready for Approval. Auto Mode does not approve it.
4. Approve. The ascent flies, and the next sequence's ejection comes within about the ascent lead, with no long wait in Duna orbit.
5. Tell the ticket how long the craft spent in orbit before the ejection, against the ascent lead.

## Check 3: a stretch by hand comes first

1. Landed on Duna, ask for "hand over control for surface operations, then return to Kerbin". Approve the manual stretch: it has no Due Time, and no alarm is set.
2. Time-warp past the Kerbin window, then Hand back. When the ascent's Sequence Plan arrives, Approve. The log says "The Kerbin window of ... was missed" and either moves the Due Time and the alarm to the next window, or Claudia revises the Mission Plan.

## Check 4: landed on Ike

Repeat Check 2 on Ike. The ascent is due the ascent lead before `plan_transfer`'s `direct` `depart_ut` as asked once the craft is in low orbit round Ike, or before the same figure on the two-step row if the plan names a capture at Duna. Tell the ticket the time between reaching orbit and that burn.
