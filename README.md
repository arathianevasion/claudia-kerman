# Claudia Kerman
<img width="839" height="659" alt="Screenshot 2026-11-01 at 9 49 54 PM" src="https://github.com/user-attachments/assets/47f8e2cb-d407-462f-bece-83ce4acdcc33" />

A Kerbal Space Program mod that adds **Claudia**, an AI mission controller. You give her a goal in plain language, such as "Land on the Mun, run 3 experiments, return to Kerbin". She checks your craft, plans the mission, explains each step, asks your go-ahead, flies it through kOS and reports back.

> **Status: in development, not yet released.** The v1 design ([Spec: Claudia Kerman v1](https://github.com/arathianevasion/claudia-kerman/issues/36)) is built and flown in game on macOS: Missions from the Craft Check to the last Debrief, the Building Blocks below, Auto Mode, Blackouts, Manual Control, Child Vessels and the Missions list. Rendezvous and docking (`rendezvous`, `dock`) are the newest blocks and are still waiting on their in-game checks. There is no packaged release yet; build it yourself (see [Building](#building)).

## How a Mission works

1. **Mission Goal.** In the VAB/SPH or in Flight, open the Mission window from the toolbar button and tell Claudia what the Mission must achieve. Pick your Mission Settings (daylight landing, time versus delta-v, crewed or remote), a Spending Cap and the model she uses.
2. **Craft Check.** The mod computes the facts (delta-v and thrust per stage from the stock simulator, thrust over weight per body, parts, crew, experiments, science left) and Claudia judges them against the goal.
   - **Blockers** stop the Mission from starting.
   - **Warnings** must be acknowledged before you can start.
   - She suggests fixes but never edits your craft.
3. **Mission Plan.** She proposes the whole route as a list of Maneuver Sequences, each with its delta-v budget and timing. You approve it once.
4. **The loop.** Each Maneuver Sequence goes through four steps:
   - **Sequence Plan:** what she'll do, its targets and tolerances, what could go wrong, and the script. **Show script** lets you read it.
   - **Approval:** your go-ahead.
   - **Running:** the mod runs her kOS Mission Script, which calls the shipped Building Blocks. Waits for a window, daylight or a phase angle warp on rails inside the script.
   - **Debrief:** how it went, rated **Nominal**, **Off-Nominal** or **Failed**, followed by the next plan. Every Debrief includes the mod's own Margin check of the craft as it is now against the sequences still to fly. When that is tight or short, Claudia reruns the Craft Check.

Claudia is also a Kerbal: a female crew member with the **Super Intelligence** role, which combines Pilot, Engineer and Scientist. She flies aboard crewed craft, or remotely from Mission Control for probes. If she dies aboard, the Mission is **Lost**, and she returns to the roster later.

In Flight, the **Capcom strip** under the altimeter follows the Mission: Claudia's latest line, where the Mission is in its plan, the Spending Cap and what is locked. Its tab expands the Mission window docked to it, and it turns amber when she needs you. In the Space Center and Tracking Station, the **Missions list** shows every Mission in the save: the active one, Paused ones and finished ones with their final Debrief and cost.

## Building Blocks

Claudia's scripts fly the craft through a fixed, tested Core Library. Each block solves its goal again from the real orbit when it flies, rather than trusting numbers from planning time.

| Block | What it does |
| --- | --- |
| `launch` | From the ground of a body with an atmosphere to a coast out of it, with the apoapsis at the target. Can time the launch for a target plane. |
| `circularize` | A circular orbit at apoapsis, periapsis or an altitude; at periapsis in a new SOI it is the capture burn. |
| `change_orbit` | Inclination, plane match, LAN shift or a resonant orbit, in one solved burn. |
| `exec_node` | Flies a maneuver node with the main engines. The other burn blocks fly through it. |
| `coast` | Holds the craft on its path until a time, warping on rails. |
| `transfer_to` | From orbit to an encounter with a moon (the Mun, Minmus), with a mid-course correction. |
| `tune_pe` | Fine-tunes periapsis, this orbit's or the next patch's. |
| `rendezvous` | Meets the Mission's Target Vessel in orbit, to within physics range under 2 m/s. |
| `dock` | Docks with the Target Vessel on RCS, pairing ports by size. If the target has a kOS processor, the mod holds it steady. |
| `land` | Lands on an airless body: a site chosen by biome or position, a deorbit burn, and a powered descent shaped from the terrain ([ADR 0011](docs/adr/0011-landing-descent-shaped-from-the-terrain.md)). Can stage off a transfer stage hung below the legs before it starts. |
| `ascend` | From the ground, or from a fall over an airless body, to a circular orbit; also the abort to orbit when a landing fails. |
| `return` | Home from orbit round any moon to its planet, with a mid-course correction; can aim for KSC. |
| `deorbit` | One burn down from orbit round a body with an atmosphere, aimed with the mod's reentry predictor. |
| `reentry` | Through the atmosphere: drops the stack, holds the heat shield forward, opens parachutes only when safe. |
| `drop_parts` | Drops named parts now by firing the decoupler that holds each. |
| `science` | Runs the craft's experiments here and transmits or keeps the data, never overwriting data already held. |

The spec's `transfer` block is `transfer_to`, because kOS has a built-in `TRANSFER`. Call `return` as a value (`local res is return().`), since a bare `return().` is kOS's return statement.

While she plans, Claudia can also call read-only **Planning Tools** (`predict_burn`, `solve_burn`, `plan_transfer`, `plan_ascent`, `find_landing_site`). They never change the game; only Building Blocks fly ([ADR 0006](docs/adr/0006-planning-tools-read-blocks-fly.md)). Where no block fits, she may write a **Custom Script** with flight logic of its own; it goes through the same Script Check.

## You stay in charge

- No script runs without your Approval, with two exceptions:
  - **Auto Mode** is a per-Mission switch that gives your Approval in advance for every plan after the first Mission Plan, as long as the Spending Cap has room for what follows. It never starts a Mission, acknowledges a Warning, raises the cap or flies a manual stretch for you ([ADR 0009](docs/adr/0009-auto-mode-is-standing-approval-on-blackouts-path.md)).
  - In a **Blackout**, with no link to Kerbin, Claudia flies the Mission Plan you already approved, and shows you any changes she made when the link returns.
- The **Abort** button stops a running sequence and makes the craft safe.
- **Manual Control** lets you fly yourself at any time, if you have a link or other crew aboard. **Hand back to Claudia** when you're done, and she replans from where you left the craft.
- While a sequence runs, the mod watches its **Telemetry Checks**, such as delta-v used, duration and electric charge. If one is crossed, Claudia tells you whether to abort.
- A sequence still running at three times its planned duration is stopped.

A Mission survives scene changes, quicksaves, reverts, quits and crashes. A sequence that was Running at a load, quit or crash is marked **Interrupted** and Debriefed, never restarted. The mod never switches vessels itself.

## Safety

kOS scripts can do far more than fly a craft. Before you see a script, the mod compiles it and checks it against a forbidden list:

- save, load and revert
- switching vessels
- physics warp (except on a Coast, see [ADR 0008](docs/adr/0008-physics-warp-on-coasts.md))
- file operations and Archive writes
- kOS settings
- changing other vessels and other addons

If a script fails, Claudia rewrites it once automatically. If the rewrite fails too, the plan shows **Couldn't produce a safe script** with **Retry**, **Retry with a note** or **Re-plan**.

While Claudia's programs run, runtime gates block the most harmful calls. Your own kOS terminal and scripts are unaffected. This is a safety net against Claude's mistakes, not a security sandbox. See [ADR 0003](docs/adr/0003-script-check-and-runtime-gates.md).

## Requirements

- Kerbal Space Program **1.12.5** on **macOS**. Windows and Linux are untested and planned for a later release.
- [kOS](https://github.com/KSP-KOS/KOS) 1.6 or later.
- [HarmonyKSP](https://github.com/KSPModdingLibs/HarmonyKSP) 2.2.1 (the community Harmony build for KSP, installed as `GameData/000_Harmony`).
- ModuleManager (optional), for the compatibility patches.
- Your own **Anthropic API key**.
- The stock delta-v calculations turned on. The Craft Check stops and asks if they are off.

Sandbox, Science and Career all work.

## API key and cost

Claudia uses the Claude API with your own key. Each Mission has its own **Spending Cap**, $10 by default, which you can raise later; the mod never sends a request that could go over it. When the cap has no room, the Mission is **Paused** until you raise it.

The start form shows an estimate. As a rough guide, a Mun landing and return costs about $2.40 on Sonnet 5.5 (the default), about $0.25 on Haiku 5.5, or about $5 on Opus 5.5. Failed and retried legs cost the most, so a clean flight costs less.

The key is stored as **plain text** in `~/Library/Application Support/ClaudiaKerman/`, readable only by you. If the `ANTHROPIC_API_KEY` environment variable is set, it is used instead. The key is never written to logs, saves or Mission records, and never placed under GameData.

## Settings

The player config is `config.cfg` in the same folder, as `name = value` lines. Unknown names are ignored. The common ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `models` | `claude-sonnet-5-5, claude-haiku-5-5` | The models offered in the start form, default first. Add `claude-opus-5-5` to offer Opus. |
| `default_spending_cap` | `10` | The Spending Cap a new Mission starts with, in USD. |
| `daylight_landing`, `time_vs_dv`, `default_remote` | `true`, `Balanced`, `false` | Prefills for the Mission Settings. |
| `default_auto_mode` | `false` | New Missions start in Auto Mode. |
| `craft_check_margin` | `15` | The delta-v margin, in percent, the Craft Check wants over the Delta-v Map. |
| `margin_recheck` | `auto` | When a Debrief also asks Claudia to rerun the Craft Check. |
| `check_dv_used`, `check_duration`, `check_ec`, `check_fuel_margin` | `120`, `150`, `10`, `10` | The default Telemetry Checks, in percent. |
| `runaway_multiplier` | `300%` | When a running sequence is stopped, as a share of its planned duration. |
| `physics_warp` | `true` | Lets Mission Scripts physics-warp on a Coast. |
| `debug_tools` | `false` | Shows the debug windows: the basic chat, stream dumps, Game Snapshots and the block test runner. |

## Known gaps

- Stock contracts, EVA welding and the construction combine bonus count Claudia as a Pilot, Engineer and Scientist (Harmony patches). ModuleManager patches in `GameData/ClaudiaKerman/Compatibility` cover Kerbalism and Community Trait Icons.
- These mods check crew roles by name and **do not** count Claudia as a Pilot, Engineer or Scientist: KER, USI, Near Future Electrical, SystemHeat, SSPX, StationScience, WildBlue and Contract Configurator contract packs.
- Claudia doesn't go on EVA in v1. When a goal needs EVA science, she plans a manual stretch and asks you to do it by hand.
- `transfer_to` and `return` fly between a planet and its moons. Interplanetary transfers are not a Building Block yet.
- A Child Vessel's run has no Telemetry Checks yet.
- Read a Sequence Plan's script with **Show script** before you approve it. The checks are a safety net, not a guarantee.
- **Before uninstalling**, press **Prepare to uninstall** in Claudia's settings in every save that has her. Removing the mod without it is likely to break those saves.

## Building

You need the .NET 10 SDK. The adapter and kOS addon compile against your own KSP 1.12.5 and kOS 1.6 install; no KSP, Unity or kOS assemblies are in this repo. `KSPRoot` points at the install and defaults to `~/KSP_spike` (a macOS install with `KSP.app` inside). You can also set `KSPManagedDir` or `KOSPluginsDir` directly.

| What | Command |
| --- | --- |
| Build everything | `dotnet build ClaudiaKerman.sln -p:KSPRoot=/path/to/KSP` |
| Core tests (no KSP needed) | `dotnet test tests/ClaudiaKerman.Core.Tests` |
| Script Check tests (need only kOS.Safe) | `dotnet test tests/ClaudiaKerman.ScriptCheck.Tests -p:KSPRoot=/path/to/KSP` |
| Script Check the shipped kerboscript and block tests | `dotnet run --project tools/ClaudiaKerman.KsCompile -p:KSPRoot=/path/to/KSP -- kerboscript tests/blocks` |
| Core Library tests on kOS.Safe (no KSP needed) | `dotnet test tests/ClaudiaKerman.Kerboscript.Tests -p:KSPRoot=/path/to/KSP` |
| Every gate above, as one pass or fail | `tools/gates/gates.sh` |

A failing test, or a script that doesn't compile or uses something forbidden, exits non-zero. A build stages the mod in `dist/GameData/ClaudiaKerman/`. To install it, copy that folder into your KSP `GameData` folder, next to kOS and HarmonyKSP. kOS and HarmonyKSP are dependencies you install yourself; they are never bundled.

CI (`.github/workflows/ci.yml`) runs on every push: the core tests, then, with kOS.Safe from the public kOS release, the Script Check tests, a Script Check of every shipped script and block test script, the Core Library tests, and a check that the syntax-error and forbidden-call fixtures fail.

### In-game tests

With `debug_tools = true`, a block test runner window (main menu, Space Center, Flight) runs the tests in `tests/blocks/` and the Planning Tool scenarios in `tests/tools/`. Each test has a `test.cfg` (block, craft, orbit, timeout and the ranges to check), a `test.ks`, a `scenario.sfs` and a `reference.craft` built from stock parts plus kOS. **Run** loads a test save, spawns the craft, flies the block unattended and logs PASS or FAIL to KSP.log and `saves/claudia_block_tests/block-tests.log`. Run them whenever a block changes. Checklists for larger in-game checks are in [docs/in-game/](docs/in-game/).

## Repository layout

The layout follows [ADR 0001](docs/adr/0001-single-in-process-ksp-mod.md): one in-process C# mod, no companion process.

| Path | What it holds |
| --- | --- |
| `src/ClaudiaKerman.Core` | The Unity-free core: the Claude client, the Mission state machine (`Missions.MissionControl`), the Craft Check facts, the Planning Tools and the C# solvers. It never references Unity or KSP. |
| `src/ClaudiaKerman.Adapter` | The thin KSP adapter (`ClaudiaKerman.dll`): the toolbar button, Mission window, Capcom strip, Missions list, Game Snapshots and the `MissionDriver` that feeds the core. |
| `src/ClaudiaKerman.KOS` | The kOS addon (`ADDONS:CLAUDIA`), the bridge that runs scripts on a processor, the runtime gates and the block test runner. |
| `src/ClaudiaKerman.ScriptCheck` | The Script Check: compiles a script with kOS.Safe and reads its opcodes against the forbidden list. Block-internal calls are allowed only on the Core Library's lines of a bundle ([ADR 0007](docs/adr/0007-script-check-finds-the-core-library-by-the-bundles-tail.md)). |
| `kerboscript/library` | The Core Library: `claudia_lib.ks` with the shared helpers and conventions, and one file per Building Block. Their `//|` doc lines go into Claudia's prompt. |
| `kerboscript/dock_hold` | The program the mod runs on a Target Vessel's processor to hold it steady for docking. |
| `tests/` | The core, Script Check and Core Library test projects, plus the in-game `blocks/` and `tools/` tests. |
| `tools/` | `ClaudiaKerman.KsCompile` (the Script Check from the command line), the merge gates and the cost baseline script. |
| `GameData/ClaudiaKerman` | Static mod files: the Super Intelligence trait config and icon, the toolbar icons and portrait, and the compatibility patches. |

Each Mission's records live in `saves/<save>/ClaudiaKerman/missions/<id>/`: the transcript, `mission.json` and an append-only `events.jsonl`. The save file holds only a small snapshot per Mission.

## Project docs

- [GLOSSARY.md](GLOSSARY.md): the domain language (Mission, Craft Check, Maneuver Sequence, Debrief and more).
- [docs/adr/](docs/adr/): architecture decisions.
- [Map: Claudia Kerman v1](https://github.com/arathianevasion/claudia-kerman/issues/1): the design decisions, each with its own ticket.
- [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES): licenses for bundled and ported code.

## License

Claudia Kerman is free software under the GNU General Public License, version 3 or (at your option) any later version (`GPL-3.0-or-later`). See [LICENSE](LICENSE).

Bundled third-party scripts and ported code keep their own licenses and notices: KSLib's `lib_lazcalc` (MIT). Ported from MechJebLib (MechJeb2 a958383, multi-licensed, ported under GPL-3.0-or-later with its copyright and SPDX lines kept): `BrentRoot`, `BrentMin`, `Newton`, `Scale`, `Shepperd` (from `MechJebLib/TwoBody/Shepperd.cs`), `Bs3` (from `MechJebLib/ODE/BS3.cs`, with the parts of `AbstractRungeKutta.cs` and `AbstractIVP.cs` it uses), `OdeEvent` (from `MechJebLib/ODE/Event.cs`) and `PlaneTiming` (`TimeToPlane` and `MinimumTimeToPlane` from `MechJebLib/Functions/Astro.cs`) in `src/ClaudiaKerman.Core/Tools/Numerics/`, `MechJebLib/Lambert/Izzo.cs` (itself ported from poliastro, MIT) as `src/ClaudiaKerman.Core/Tools/Lambert.cs`, `MechJebLib/Maneuvers/Simple.cs`, `MechJebLib/Maneuvers/ChangeOrbitalElement.cs` and `Astro.VelocityForInclination` from `MechJebLib/Functions/Astro.cs` in `src/ClaudiaKerman.Core/Tools/OrbitChange.cs`, and the formulation of `MechJebLib/Maneuvers/TwoImpulseTransfer.cs` (without its alglib call) in `src/ClaudiaKerman.Core/Tools/Transfer.cs`. Ported from MechJeb2 itself (a958383, GPL-3, its copyright notice kept): `MechJeb2/Maneuver/TimeSelector.cs` and the `OrbitExtensions` node, radius and closest-approach helpers it calls, as `src/ClaudiaKerman.Core/Tools/BurnTime.cs`, and `MechJeb2/OrbitalManeuverCalculator.cs`'s plane-match, LAN-shift and resonant-orbit burns in `src/ClaudiaKerman.Core/Tools/OrbitChange.cs`. The Mission Scripts and Custom Scripts that Claudia writes for your Missions are yours; Claudia Kerman claims no license over them.
