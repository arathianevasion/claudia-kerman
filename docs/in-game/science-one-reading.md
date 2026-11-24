# science takes one reading per experiment, on a free part first (#1297)

In-game check for #1297. At each call, `science` runs at most one part per experiment, a part holding no data first, and keeps the data aboard. Under "auto" it transmits only when every part of the experiment holds data and a reading is needed here: it sends one part's data, given a link and the charge, then runs that part. With no part free and none freed, the experiment says "no free part", in `kept`, and a named experiment fails the block. Core makes the choice (`ADDONS:CLAUDIA:SCIENCECHOICE`, `ScienceChoice`); the block's Kerboscript only carries it out. The agent run that wrote this did not launch KSP.

Install as in `phase-3-gate.md` (build, copy `dist/GameData/ClaudiaKerman/`, `debug_tools = true`). Results are in `block-tests.log` and `KSP.log`.

## Check 1: run science_copies

The craft is the `science` test's probe with a second 2HOT thermometer, placed by hand without the VAB: if it loads with the thermometer misplaced, open it in the VAB, fix and re-save it.

1. Pick `science_copies` and press **Run** (up to 300 s).
2. It passes (`PASS science_copies`, every `ok` line): calls 1 and 2 each ran one thermometer and kept the data; call 3 failed with "no free part"; call 4 ("auto") sent one thermometer's data to free it and ran it again.

## Check 2: the science test still passes

Run `science`. It passes as before: in Sandbox each experiment runs once, and the second call runs nothing.

## Check 3: the Mun gravity survey

Fly the #1287 playtest again (Career Test save, "Mun Shot" with six Gravity Scan parts and a crew report, the Mun gravity survey with five waypoints). At each waypoint the run report's `results` show one gravityScan entry with action "ran" and transmit "kept", and the others "one reading per experiment; another part took it". After the fifth waypoint five Gravity Scan parts hold data, and none was transmitted. The crew report part, which holds data from the first site, is sent to free it at later sites where crew-report science is left. If a site shows no gravityScan reading, bring that run report to the player.
