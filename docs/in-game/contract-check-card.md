# The Contract check card at Start Mission (#1022)

With accepted exploration (world-first) Contracts, Start Mission now posts Mission Control's own Contract check card in the chat before any request to Claude: the Contracts this craft can fly in one Mission, pre-ticked, and every other one under "Not this flight" with its reason. "Plan these N" hands the set to Claudia with your words. Core tests cover the choice, the pricing and the request (`ContractCheckTests`, `ContractCheckCardTests`); these checks need the game, and are #1044's. Install and where to look are as in `phase-3-gate.md`; the Mission's requests are in its folder's stream dumps with `debug_tools = true`.

## Check 1: the card, with nothing typed

1. In a 1.12 Career save, accept two exploration Contracts (e.g. "Explore the Mun" and "Explore Minmus") and one Contract of another type (a part test).
2. Put a craft that can reach the Mun and come back (Claudia aboard) on the pad, open the Mission window, type nothing, and press Start Mission.
3. A card labelled "MISSION CONTROL · CONTRACT CHECK (the mod, no Claude)" grows in at the bottom of the chat, and the transcript follows it. No Craft Check runs, and the spend meter doesn't move (no request was sent).
4. The card's first line reads "N of 3 Contracts · route … m/s of … · reserve … (15%) · … m/s to spare", with a bar under it: green for the route, amber for the reserve.
5. Each Contract that fits has a ticked box with its phase (the body, e.g. "Mun"), "+… m/s" and its deadline (or "no deadline").
6. "Not this flight" lists the part test as "not yet: part tests aren't planned yet". A Contract the craft can't reach says "this craft can't: … m/s short"; one whose deadline is too near says "completes later: …".
7. The buttons read "Plan these N" and "Plan without Contracts" (greyed, with "Type your own words to plan without Contracts.").

## Check 2: Plan these

1. Press "Plan these N".
2. The card folds to one line: "Mission Control · Contract check: N of 3 Contracts chosen · route … m/s · … m/s to spare · ✓ …".
3. The Craft Check runs on the Contracts' route ("Contracts: orbit the Mun; …; return to Kerbin") and the Mission starts; the folded line stays in the Mission's transcript.
4. Claudia's Mission Plan is the Mission's first request. In its stream dump, the request has a "Contract Route" section with each chosen Contract's Requirements and phases, and "No words typed".

## Check 3: your words, and Plan without Contracts

1. End the Mission. Type "Also run a crew report in Mun orbit." and press Start Mission: the card says "with your words".
2. The boxes show the suggested set ticked.
3. Change your words and press Start Mission again: a new card replaces the old one, with the new words.
4. Press "Plan without Contracts": the card goes, the Craft Check judges your words alone and Claudia plans them with no Contract Route.

## Check 4: no Contracts, no change

1. With no Contract accepted, Start Mission behaves as before: Start needs words, and the Craft Check runs at once.
2. With Contracts accepted and words the craft can't fly (e.g. "Land on Eeloo and return to Kerbin" on a small craft), no card is posted: the Craft Check fails with its ordinary delta-v Blocker.

Tell #1044 what you saw, with a screenshot of the card before and after "Plan these".

## Check 5: ticking, unticking and Swap in (#1023; the in-game check is #1045)

Core tests cover the pricing and the inputs (`ContractCheckTests`, `ContractCheckCardTests`); these checks need the game.

1. With a card showing two or more Contracts, untick one: the headline, the route bar and the spare change at once, with the other Contracts' "+… m/s" re-priced. The spend meter doesn't move and no request appears in the stream dumps.
2. Tick it again: the card returns to what it was.
3. Under "Not this flight", a Contract the craft can't fly with the others has a box. Tick it: the headline reads "… m/s short", the bar goes red, "Plan these N" is greyed and the shortfall shows beside it.
4. Where a left-out Contract fits in place of ticked ones, a "Swap in X for Y" button is there. One click ticks X, unticks Y and the card fits again. No button shows where X fits with a plain tick, or can't fit even alone.
5. Press "Plan these N" on a set you edited: the folded line and the Craft Check's route name only the ticked Contracts, and the request's Contract Route section lists them (the unticked ones as "the player left it out").

Tell #1045 what you saw, with a screenshot of the card after an untick and after a Swap in.

## Check 6: your words and Contracts together (#1036)

Core tests cover the reading and the pricing (`ContractWordsTests`); these checks need the game.

1. Accept "Explore the Mun" (a landing). Type "Land on the Mun and return to Kerbin" and press Start Mission. The card pre-ticks it with "+0 m/s": your words already fly that leg.
2. Press "Plan these 1". In the Mission's first request (stream dump, `debug_tools = true`) the Contract Route section lists "land on the Mun [serves: your words; "Explore the Mun"]".
3. End the Mission. Type "Orbit Minmus and don't land on the Mun" and press Start Mission. "Explore the Mun" is under "Not this flight" with a "your words win" chip and "can't with your words: …". The route still goes to Minmus.
