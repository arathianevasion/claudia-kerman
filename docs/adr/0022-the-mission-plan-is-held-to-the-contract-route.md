# The Mission Plan is held to the Contract Route

When a Mission has Chosen Contracts, the mod holds Claudia's Mission Plan to the Contract Route: each body is visited once, in the Route's order, and every Chosen Requirement at a body is served in its one Body Visit, whichever Contract it comes from. Inside a visit, the order is the one the Route prices: arrive or capture, work in orbit, land, work on the ground, leave. A plan or revision that departs from the Route, or keeps a leg serving no open Requirement and none of the player's words, is sent back with the problem named. A revision is checked against the Route rebuilt from where the craft is, over the Requirements still open. This continues ADR 0021: the Route was already deterministic mod logic, and now the plan is checked against it too.

- **Repeat visits.** A body is visited again only when the player's words ask for it. The Contract Requirements are served at the first visit; the repeat serves only the words.
- **Passing a body.** A flyby that bends the path toward the next Body Visit is not a visit and doesn't count as a repeat. It still needs to serve the next visit.
- **Leaving the Route.** Claudia may propose a plan that departs from the Route, naming the departure and why, and it then waits for the player's Approval even under Auto Mode, as a proposal to drop a Contract does (#1306).
- **Without Contracts.** A Mission planned only from the player's words is not held to its Route, which prices it.
- **Order the game enforces.** The only order kept is that a Return comes last. A Contract whose steps the game takes in order is not modelled: the player gives the order in their words, whose "A then B" rules shape the Route.

Decided in grilling on 2026-11-26, after Mission m20261126-040610-a4b7. Its Ferry Contract's Requirements were listed in the game's order with phase tags, and Claudia (on Haiku) planned Mun, Minmus, Mun again, home, while the Route had priced Mun, Minmus, home. After the first Mun leg the mod reported both Mun Requirements met, and she kept the second Mun visit anyway.

We rejected showing the Route's grouping without checking the plan: the flight above shows Claudia drifting from mod facts she was given. We rejected holding the plan to one visit per body while leaving the order free: the Route's order is the one checked against every deadline, so a reordered plan can miss a deadline the Route kept.
