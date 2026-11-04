# The rename to Claudia Kerman carries an old install over (#665, ADR 0015)

Use a save made before the rename, with Claudia on the roster, at least one past Mission in the Missions list, and your API key stored in Settings (not only in `ANTHROPIC_API_KEY`). Build as in `phase-3-gate.md`: the build stages `dist/GameData/ClaudiaKerman/`. Results are in `KSP.log` (lines start `[ClaudiaKerman]`).

Before you start, note Claudia's experience level, flight log entries and (if she is assigned) her craft and seat in the Astronaut Complex or the save's `ROSTER` node. Back up the save folder and `~/Library/Application Support/ClaudiaKermin` first.

## Check 1: an old install carries over

1. Quit KSP. Delete `<KSP>/GameData/ClaudiaKermin`. Copy `dist/GameData/ClaudiaKerman/` into `<KSP>/GameData/`.
2. Start KSP. `KSP.log` shows `moved .../Application Support/ClaudiaKermin to .../Application Support/ClaudiaKerman`. That folder now holds `api_key` and `config.cfg`; `ls -l` shows `-rw-------` on `api_key`.
3. Load the old save. `KSP.log` shows `moved .../saves/<save>/ClaudiaKermin to .../saves/<save>/ClaudiaKerman`.
4. In the Astronaut Complex she is **Claudia Kerman**, with the Super Intelligence role, the same experience level and flight log, in the same seat if she was assigned. No rename dialog appears.
5. Open the Missions list: the past Missions are there and open as before.
6. Send a chat message (or start a Mission): it works without entering the API key again.
7. A vessel that flew the Claudia Flag, and the agency flag if a Mission was under way, show the flag (not a blank or white one).
8. Save, quit, and check the save file: `ROSTER` has `name = Claudia Kerman`; the `MissionScenario` and `RosterScenario` nodes are still there.

## Check 2: both GameData folders installed

1. Quit KSP. Copy an old build's `ClaudiaKermin` folder back into `<KSP>/GameData/` beside `ClaudiaKerman`.
2. Start KSP. At the main menu one dialog says Claudia Kerman is installed twice and to delete `GameData/ClaudiaKermin`. `KSP.log` has the same text as an error.
3. Quit KSP, delete `GameData/ClaudiaKermin`, start again: no dialog.

## Check 3: Prepare to uninstall on an old save

1. Restore the backed-up old save (Claudia still named Claudia Kermin) and load it with only `GameData/ClaudiaKerman` installed.
2. Open Settings at the Space Center and press **Prepare to uninstall** at once. The line says 1 Kerbal with the Super Intelligence role is now a Pilot. Save: her `ROSTER` entry has `trait = Pilot`.
