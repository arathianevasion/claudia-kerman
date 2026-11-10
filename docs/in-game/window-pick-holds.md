# A picked Transfer Window holds until its ejection (#966)

Once the player picks Soonest or Optimal, the pick holds for that transfer until its ejection burn has flown, even if the ejection now costs a little more. A revised Mission Plan for the same transfer carries it. Core tests cover the rules (`WindowPickHoldsTests`); these checks need the game. Install and where to look are as in `phase-3-gate.md`.

## Check 1: a revision in orbit carries the pick

1. On the pad with a craft that can reach Duna, pin "Orbit Duna", press Optimal on the Transfer Window card and Start. Approve the Mission Plan and fly the launch to orbit.
2. In Kerbin orbit, well before the Optimal window, change Time versus delta-v in Settings so Claudia revises the Mission Plan.
3. The revised plan's card is followed by the Transfer Window card with its Optimal button already on. Approve is on, and the status does not say "Pick a Transfer Window, then Approve". Nothing calls for the player.
4. Turn Auto Mode on and revise again: the plan is approved without waiting; Auto Mode never says "a Transfer Window to pick".
5. At a revised plan, press Soonest: the log has "Transfer Window to Duna: Soonest." and Approve stays on. After Approve, the next request says "The player picked the Soonest Transfer Window to Duna."
6. Save and load at a revised plan carrying the pick: the card is there with the pick's button on, and Approve is on.

## Check 2: an open window holds; a closed one asks again

1. As Check 1 to Kerbin orbit with Soonest, then warp past the Soonest row's departure date, a few days into the window, without approving the transfer.
2. Revise the plan (change Time versus delta-v). The window is still open: the Soonest row says "(now)", its button is on, and Approve is on.
3. Warp on until the Soonest row is about two years away (the window has closed), then revise again. Revise once more a few weeks later: it still asks (a closed window never reopens). The card shows fresh rows with neither button on, Approve is greyed out, and the status reads "Pick a Transfer Window, then Approve".

## Check 2b: a held Optimal stays in its window

1. Pick Optimal on the pad, warp on the pad to that Optimal window, launch to Kerbin orbit.
2. A day or more past the Optimal row's date, revise the plan. The Optimal row leaves "(now)" or within days, not about two years on, and nothing asks for a pick. Approve, and the transfer's `plan_transfer` row in the request (the Mission folder's request log) has `"pick":"optimal"` with a departure within days.
3. Approve the transfer's Sequence Plan (its script `transfer_to(lexicon("target","Duna","target_pe",60000))`, no window). The ejection flies within days, not after a warp of about two years; KSP.log's transfer_to step shows the ejection's UT within days of now.

## Check 3: after the ejection the pick is spent, and the return asks

1. Fly Check 1's Mission through its ejection. Once the craft is out of Kerbin's sphere of influence, the Mission folder's `events.jsonl` has a `window_spent` event with "Duna:Optimal" (or the pick made).
2. With the goal including a return to Kerbin (pin "Orbit Duna and return to Kerbin."), at Duna have Claudia revise the Mission Plan. The card is headed "Transfer Window to Kerbin: pick one" with neither button on, and Approve waits for a pick.
3. Tell the ticket whether anything asked for a pick between the start form and the Duna ejection.
