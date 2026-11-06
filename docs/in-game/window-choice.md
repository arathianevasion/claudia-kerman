# The player picks the Transfer Window (#686)

For a transfer between bodies with the same parent, the player picks Soonest or Optimal. Within one sphere of influence the Mission Setting Time versus delta-v still picks. Core tests cover the rules (`WindowChoiceTests`); these checks need the game, for how the rows draw and read. Install and where to look are as in `phase-3-gate.md`.

## Check 1: a Duna Mission shows both rows, and Start waits for a pick

1. In the VAB, or on the pad in a new game, with a craft that can reach Duna, pin the Mission Goal "Orbit Duna".
2. Under the Craft Check there is a card headed "Transfer Window to Duna: pick one" with a Soonest and an Optimal row. Each row has a button and a line: departure date and wait, ejection delta-v and burn time, capture delta-v, total and flight time. Neither button is on.
3. Start Mission is greyed out and reads "Start Mission · pick a Transfer Window". Check craft still works.
4. Press Optimal. Its button is on and Start opens. If a check had already run, its card says "Stale: the Transfer Window changed since this check. Check again."
5. Press Check craft. In the save's newest `craft-checks/` folder, the request's `transfer:Duna` line has the Optimal row's total and says "(the player's Optimal window)".
6. Tell the ticket whether the rows read well at the panel's usual width, and whether the burn time looks right for the stage that would fly the ejection.

## Check 2: the pick drives the Mission Plan and the Due Time

1. Start the Mission from Check 1. The log has "Transfer Window to Duna: Optimal.".
2. The Mission Plan arrives with no second ask, and Approve is on. In the Mission's request dump, the `plan_transfer` row for Duna has `"pick":"optimal"` and `"setting":"Player"`.
3. Approve. The launch's Sequence Plan says "Waiting for the Duna window", and its date is the Optimal row's departure less an hour, whatever the Time versus delta-v setting says.

## Check 3: a revised Mission Plan offers the choice again

1. At the Mission Plan or the launch's Sequence Plan, change Time versus delta-v in Settings, so Claudia revises the Mission Plan.
2. The revised plan's card is followed by the Transfer Window card, with the rows worked out again and neither button on. Approve is greyed out, and the status reads "Pick a Transfer Window, then Approve".
3. Turn Auto Mode on. The Mission does not approve, and it says "Auto Mode is waiting for you: a Transfer Window to pick."
4. Press Soonest. The log has "Transfer Window to Duna: Soonest." With Auto Mode on, the plan is approved at once; with it off, Approve is on again. The next request says "The player picked the Soonest Transfer Window to Duna."
5. At a revised plan waiting for a pick, save, load and come back: the card is there with neither button on, and Approve is still greyed out. After a pick, save and load: the pick stands and is not asked for again.

## Check 4: the Mun and Ike

1. Pin "Orbit the Mun": no card, and Start needs nothing more. The request's rows have no `"setting":"Player"`.
2. Pin "Go to Ike": the card is headed "Transfer Window to Duna". Start the Mission with a pick and Approve the plan: the launch's Due Time is that Duna window's.

## Check 5: a departure flown straight on asks at its Sequence Plan (#782)

1. Land a craft with fuel for the trip home on Duna, by any means (a cheat or an earlier Mission). Pin the goal "Explore and come home.": the goal names no way home, so no card shows before Start and none with the Mission Plan. If the Mission Plan's steps are not an ascent and then a return to Kerbin, ask for that with Change something.
2. Approve the Mission Plan. Under the ascent's Sequence Plan card is "Transfer Window to Kerbin: pick one" with a Soonest and an Optimal row, each reading "capture 0 m/s", and neither button on. There is no Due Time yet, Approve is greyed out, and the status reads "Pick a Transfer Window, then Approve".
3. Turn Auto Mode on. Nothing is approved or picked, and it says "Auto Mode is waiting for you: a Transfer Window to pick." Turn it off again.
4. Save, load and come back: the card is there with neither button on.
5. Press Optimal. The log has "Transfer Window to Kerbin: Optimal." and "Waiting for the Kerbin window: the ascent is due ...", the card shows that Due Time, and a KSP alarm is set for it. Press Soonest: the Due Time and the alarm move to the earlier date.
6. Save, load and come back: the pick stands, its button is on, and the Due Time is unchanged.
7. Warp to the Due Time and Approve. After the ascent, the first request in the Mission's dump opens with "The player picked the Soonest Transfer Window to Kerbin.", once. The Sequence Plan for the departure asks nothing, and its `transfer_to` leaves at about the Soonest row's date.
8. Repeat from Duna orbit with a plan whose first step is the return: the card is under that Sequence Plan, and its script's `window`, if it has one, reads the picked row's date after a pick. Tell the ticket whether the rows' figures read right there.
9. From the Mun or Minmus, landed and in orbit, with the same goal: no card, and Approve is on from the start.
