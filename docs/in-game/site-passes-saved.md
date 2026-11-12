# find_landing_site's passes survive a restart (#975)

`deorbit` follows the pass `find_landing_site` gave for a site (#974). The record of those answers is saved in the scenario node as one game-level value with the save's UT (not in a Mission's state), so a game restart keeps it, with or without a Mission in the save. Core tests cover the record's save, load, cut to 8 and forgetting later answers (`SitePassesTests`); this check needs the game. Install and where to look are as in `phase-3-gate.md`.

## Check: a restart keeps the site's pass

1. In orbit round Eve (or Duna, a body with air) on a Mission, have Claudia run `find_landing_site`. Note the site she picks and the pass's landing time and sun elevation.
2. Save, quit KSP and start it again, load the save and resume the Mission and the flight.
3. Run `deorbit` for that site with a numeric `max_wait`. Its result takes the site's pass: the burn is for the pass `find_landing_site` gave, and there is no `pass_note` about a missing match.
4. Quickload an earlier save (made before step 1) and run `deorbit` for the same site: it has no recorded pass and says so in a `pass_note`, as before.
