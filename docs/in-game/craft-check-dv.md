# The Craft Check's delta-v figures and kept dump in KSP (#776)

#776: the Craft Check counted a moon's capture twice and left the airless landing's gravity loss to Claudia. Now a transfer's line holds its capture (no separate `capture:` line follows it), the mod gives `land:` and `ascend:` on an airless body with the craft's gravity loss already in, and each Craft Check's request and reply are kept.

Install as in `phase-3-gate.md` ("Install"). Use the save the Mun Shot v2 was red in (7,393 m/s in vacuum).

## Check 1: Mun Shot v2 is no longer red

1. In the VAB open Mun Shot v2 with the stock delta-v readout on. Goal: "Land on the Mun and return to Kerbin". Press **Check craft**.
2. The check should not report `delta_v_short`. Its summary should read a need of about 6,300 m/s (launch about 3,420 + transfer about 1,170 with its capture + Mun landing about 680 + ascent about 680 + return about 310 + Kerbin landing about 40), about 7,250 with the 15% margin, against 7,393.
3. Before this fix the same check read about 6,620 and was red.

## Check 2: the figures the mod gave

1. Find the dump (Check 3) and open the `request-*.json`. In the Delta-v Map text:
   - `transfer:Mun` says "(includes the capture, ...)" and no `capture:Mun` line follows it;
   - `land:Mun` and `ascend:Mun` each say "includes this craft's gravity loss at TWR 6.0; 5xx without" (about 677 at TWR 6);
   - the sentence "add to a landing or ascent 10% and 0.4 ..." is gone.
2. For a craft whose stages the simulator can't give (stock delta-v readout off is refused earlier), or one with no thrust, the line reads "bare figure: this craft's TWR there is unknown".
3. A goal at a body with air (Duna) shows `land:Duna` with no such note.

## Check 3: the kept request and reply

1. After the check, look in `saves/<save>/ClaudiaKerman/craft-checks/`: one folder per check named by its time (`20261105-141502-123`), each with `request-*.json` (the request, the API key redacted) and `request-*.sse` (the raw reply). A check that calls tools or takes a second look has one pair per request.
2. Run the check 12 times: only the last 10 folders stay.
3. `Player.log` has one line per check: `Craft-check verdict: Pass; delta-v findings: none; dump: <folder>` (or `Fail; delta-v findings: delta_v_short, ...`).
4. Press **Start Mission** from a passing check. The Mission's folder (`ClaudiaKerman/missions/<id>/`) has `craft-check/` with the same files as the latest check's folder.
5. This runs without `debug_tools`; the Mission's own `stream-dumps/` still needs it.
