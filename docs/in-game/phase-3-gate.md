# Phase 3 gate: rendezvous and dock in KSP (#457)

In-game checks for spec #435 Phase 3 (Rendezvous): the `rendezvous` and `dock` Building Blocks against a spawned Target Vessel, and then a real Mission Goal planned with Building Blocks alone. The criteria are as revised on #457 on 2026-11-01:

- `rendezvous` ends with the target loaded and under 2 m/s relative.
- `dock` ends docked, run once with a `dock_hold` target and once with a passive target.
- The recorded Mission Plan for "rendezvous with <vessel> and dock" uses `rendezvous` and `dock` and no Custom Script.

The agent run that wrote this did not install or launch KSP. The tests and the gate are green in the repo; everything below needs a person in the game.

## Install

You need KSP 1.12.5 with kOS 1.6 and HarmonyKSP installed (both are your own installs, never bundled), and the .NET 10 SDK.

1. Build: `dotnet build ClaudiaKermin.sln -p:KSPRoot=/path/to/KSP` (`KSPRoot` defaults to `~/KSP_spike`). The build stages the mod in `dist/GameData/ClaudiaKermin/`. It also embeds `tests/blocks/*` in the kOS addon DLL.
2. Copy the whole `dist/GameData/ClaudiaKermin/` folder into `<KSP>/GameData/`, next to `kOS` and `HarmonyKSP`. Replace the old folder; leave every other folder alone.
3. Set `debug_tools = true` in the player config (it turns on the block test runner window).
4. Start KSP. The block test runner window is in the main menu, the Space Center and Flight.

## Where to look

- `KSP.log` (grep `block test:`): each test logs `PASS <test>` or `FAIL <test>: <why>`, then one line per check: `ok   <check> (got <value>)` or `FAIL <check> (got <value>)`. Booleans read `True`/`False`.
- `saves/claudia_block_tests/block-tests.log`: the same lines with a UTC stamp.
- The test script also prints its own summary line in the kOS terminal (`rendezvous test: ...`, `dock test: ...`).

## Check 1: Run rendezvous

1. In the block test runner window pick `rendezvous` and press **Run**. It spawns the reference craft in a 100 km circular equatorial Kerbin orbit and "Block Test Target" 200 km behind it, 30 m/s faster and 6 m/s off plane. Allow up to 2400 real seconds; the block warps between burns.
2. Pass lines in `KSP.log`:

```
PASS rendezvous
  ok   success == true (got True)
  ok   distance_actual < 2250 (got <metres>)
  ok   relative_speed_actual < 2 (got <m/s>)
  ok   test_all_five == true (got True)
```

3. The terminal line `rendezvous test: success True, <m> m at <m/s> m/s, burns match_plane, phasing, transfer, correction, match_velocities, ...` lists the five steps (a second `correction` or a `kill_speed` besides is fine).
4. Fail: `FAIL rendezvous: <reason>`. Copy the whole block from `block-tests.log` into the issue.

## Check 2: Run dock (dock_hold target)

1. Pick `dock` and press **Run**. It spawns the dock reference craft in the same orbit and "Block Test Target", a copy of it, 150 m ahead at the same speed. The copy has a powered kOS processor, so the mod starts `dock_hold` on it. Allow up to 900 s.
2. Pass lines:

```
PASS dock
  ok   success == true (got True)
  ok   docked == true (got True)
  ok   test_ports == true (got True)
  ok   monoprop_used > 0 (got <kg or units>)
  ok   monoprop_used <= 11 (got <units>)
```

3. `KSP.log` also shows `starting dock_hold on part <id> of 'Block Test Target' for dock on part <id>` and, when `dock` returns, `stopping dock_hold on part <id>: <reason>`.
4. The terminal line reads `dock test: success True, docked True, port size0 to size0, monoprop <n>, <s> s, ...`.
5. #707: the block test's monopropellant bound (`<= 11`) is for the FL-R10's 20 units, about twice DOCKFUEL's 5.3 units from 150 m. It is not the 40-unit bound for the real dock in Check 8 below. #706: `KSP.log` has a `dock trace at UT ...: mode close, distance ..., along ..., lat ..., align_deg ..., speed ..., dv_error ..., translating ..., monoprop ..., hold ...` line about once a second from `starting dock_hold` to the result. Most lines read `translating false` (the craft coasts) and `monoprop` falls in steps, not steadily. #711: the trace's `hold` reads `active` until `KSP.log` shows `dock_hold on part <id> settled on its aim`, then `settled`; the craft holds still until then. The result is `success True` with no back-off after capture. About once a second there is also a `block call DOCKGUIDE took ... ms (the slowest of <n> calls ...)` line; the slowest should be well under 1 ms.
6. #706: watch both vessels inside 20 m. Neither should keep swinging back and forth: the target's port holds still (dock_hold re-aims only when the craft is over 15 degrees off it, more than 10 m out), and the craft's RCS puffs in short bursts, not continuously. Write down any sustained oscillation with the trace's `align_deg` figures.

## Check 3: Run dock (passive target)

1. Pick `dock_passive` and press **Run**. Same start, but the target is `target.craft`: the dock craft without its kOS KR-2042. The mod cannot run `dock_hold` on it, so `dock` meets it at its port's current facing.
2. Pass lines: the same five `ok` lines as Check 2 under `PASS dock_passive`, and the `dock trace` lines (with `hold passive`).
3. `KSP.log` must not contain `starting dock_hold` for this run.
4. The target's parts are hand-edited (the processor removed, its neighbours re-linked, a 0.35 m gap left where it sat). If the craft spawns broken or the target comes apart, say so: that is a test-craft bug, not a block bug.

## Check 4: the dock reference craft

The dock craft file (`tests/blocks/dock/reference.craft`) was written by hand in #456. In the VAB, load it from `saves/claudia_block_tests/Ships/VAB/` after a run (or open it from the test save) and check:

1. Parts: OKTO2 probe core, kOS KR-2042, two Z-200 batteries, Small Inline Reaction Wheel, FL-R10 monopropellant tank, Clamp-O-Tron Jr on the nose, TD-12 decoupler, FL-T800, LV-T45, three R-8 winglets and four RV-105 RCS blocks round the FL-T800.
2. The RV-105s draw the FL-R10's monopropellant: in the RCS part's right-click menu or the resource display, monopropellant is shown and falls as `dock` fires. `monoprop_used > 0` in Check 2 says the same.

## Check 5: dock_hold fault

1. Run `dock` as in Check 2. While the craft is between 10 and 100 m out, cut power to the target's processor (switch off its batteries and solar panels, or open the target and disable the kOS part's power).
2. Expected: the mod stops the hold (`KSP.log`: `stopping dock_hold on part <id>: dock_hold's processor lost power`), the craft backs off about 10 m, and the result is a failure with a reason like `dock_hold on Block Test Target stopped: ...; backed off to <n> m`. In a Mission the Debrief reports the failure.

## Check 6: Abort mid-dock

1. Run `dock` as in Check 2. At about 50 m press **Abort** in the Mission window (or stop the run from the runner window).
2. Expected: the craft is made safe and `KSP.log` shows `stopping dock_hold on part <id>: <reason>`. The target stops turning its port.

## Check 7: Craft Check findings

Start a Mission with the Mission Goal "rendezvous with <vessel> and dock" in the VAB or SPH (any in-orbit vessel named for <vessel>):

1. With a craft that has no docking port, RCS or monopropellant, the Craft Check shows **Blocker: Nothing to dock with**, and Start Mission stays closed.
2. With a docking port, RCS and monopropellant, but a port size the target has no free port for (a size 0 port against a station with free size 1 and size 2 ports only), it shows **Warning: Docking port sizes may not match**, which you acknowledge before Start Mission opens.

## Check 8: the player's Mission

1. Put a vessel with a free port in orbit as the Target Vessel (a station or the "Block Test Target"), and a craft with a docking port, RCS, monopropellant and a kOS processor in a different orbit.
2. Mission Goal: `rendezvous with <vessel> and dock`. Pick Mission Settings, a Spending Cap and a model, then approve.
3. Expected: Claudia proposes a Mission Plan of Maneuver Sequences built from Building Blocks alone. Read it in the Mission window and in `saves/<save>/ClaudiaKermin/missions/<id>/transcript.json`. Record: **the plan uses `rendezvous` and `dock`, and no Custom Script.** If a Custom Script appears, write down why she gave for it.
4. Approve each sequence and let it fly. Expected: the `rendezvous` Sequence ends with the target loaded and under 2 m/s relative, the `dock` Sequence ends docked, and each Debrief is rated Nominal.
5. #706: the `dock` from that rendezvous hand-off ends docked with monopropellant left: its `monoprop_used` is a modest share of what the craft carried (#706's failed flight used 121.7 of 128 units and stopped 11.6 m short), and `KSP.log` has its `dock trace` lines. Record `monoprop_used` and the units aboard at the start (the first trace line's `monoprop`).
6. #711: the bound for that real dock, from a rendezvous hand-off about 300 m out: **at most 40 units of monopropellant used** (about twice DOCKFUEL's 12 m/s on #711's craft; #711's flight used 95 of 121), and **at most 40% of the `dock trace` lines read `translating true`** (#711's read 484 of 522). Count them with `grep "dock trace" KSP.log | grep -ci "translating true"` against `grep -c "dock trace" KSP.log` over the dock's lines. Also check:
   - While the station is still turning to the craft, the trace's `hold` reads `active`, and the craft holds its place (speed near 0, `lat` not swinging). `KSP.log` has `dock_hold on part <id> settled on its aim` before the craft starts to close; from then on the trace reads `hold settled`.
   - Between stretches (beyond 60 m, to 15 m, inside 15 m) the trace's `translating` is mostly `false`: one burn at each edge, then a coast.
   - The block ends docked with `success True` and no `backed off` in its reason, even if the merged vessel turns after capture.
   - The Mission's events show `telemetry_check_disarmed` lines for the delta-v checks ("the craft docked with <vessel>, ..."), and no Telemetry Check crosses on the merged vessel's delta-v.

## Report

File the results on the "In-game verification pending: Phase 3 gate (#457)" issue: the `PASS`/`FAIL` blocks for Checks 1 to 3, a yes or no for each of Checks 4 to 8, and the Mission Plan's block list from Check 8. Do not close #457 until Checks 1 to 3 and 8 pass.
