# Gate scripts (#623)

| Script | What it does | When |
|---|---|---|
| `gates.sh` | One `dotnet build -warnaserror`, then Core (`--filter "Category!=Timing"`), Kerboscript (through `kerboscript.sh`) and ScriptCheck tests with `--no-build`, KsCompile over `tests/blocks` and `tests/tools` from the built dll, and every forbidden-call / syntax-error fixture in one KsCompile call. After the build these steps run at once and their lines print in the fixed order (#702); `GATES_SERIAL=1` runs them one after another. `KOS_PLUGINS_DIR` names the folder holding `kOS.Safe.dll` (default `~/KSP_spike/GameData/kOS/Plugins`); `GATES_NO_KSP=1` builds every project but the `<NeedsKSP>true` ones (the Adapter and the kOS addon) and prints a SKIP line for them. Idle M-series machine, 2026-11-04, three runs each: serial median 178 s, parallel median 115 s (35% less). Parallel with `SHARDS=9` took 127 s and `SHARDS=8` 123 s, so `SHARDS` stays at the default (one per performance core, 10 here). | The merge gate, in a worktree or on main; CI runs it too, with `GATES_NO_KSP=1` and kOS.Safe from the kOS release (#1004). |
| `kerboscript.sh` | After a build: lists the Kerboscript tests, deals the test methods out to one test process per performance core (`SHARDS` overrides), runs them at once and checks the total run equals the total listed. kOS.Safe's static state stays isolated per process; inside a process `KosHarness.Gate` still serializes it (#681). | Called by `gates.sh` (#625), locally and in CI. |
| `kerboscript-loc.sh` | The Kerboscript line ratchet (#705, ADR 0014): fails when the raw `wc -l` total of `kerboscript/library/*.ks` (comments and `//|` lines included; not `debug` or `dock_hold`) is above the number in `kerboscript-loc.txt`, and prints both. A ticket that shrinks the library lowers the number in the same commit; one that must grow it raises the number with a reason in its commit message. | Called by `gates.sh`. |
| `gates-timing.sh` | Builds, then runs only `Category=Timing` (every `Timing.Within` or Stopwatch-budget test). One rerun allowed. | Once per wave, on an idle machine, before an install and before the Phase gates. |
| `merge-step.sh N "msg"` | Calls `rec-local.sh` (merge, gates, clean up). No Core rerun loop. rc 2 conflict, 3 gate failure. | The orchestrator's merge of one ticket. |
| `kerboscript-stress.sh` | After a build: runs ChangeOrbitTests, DockTests and RendezvousTests in one test process per run, `RUNS` times (default 20), with about half the cores loaded by `yes`; stops at the first failing run and prints each failing test's message. `FILTER` narrows it. | Repro for #681, or any time a Kerboscript flake is suspected. |

All of them run on the checkout they live in (`GATES_ROOT` overrides). A new wall-clock test must carry `[Trait("Category", "Timing")]`; check with
`grep -rLZ 'Category", "Timing' $(grep -rlE 'Timing\.Within\(' tests --include=*.cs)` (no file besides `Timing.cs` may be listed; a helper that wraps `Timing.Within` needs its callers tagged).

## What CI can't check

CI has no KSP, so it skips building the Adapter and the kOS addon (`GATES_NO_KSP=1`). By the player's ruling (2026-11-13), a ticket that touches `src/ClaudiaKerman.Adapter` or `src/ClaudiaKerman.KOS` merges from a Mac session, which runs the full local `gates.sh` with KSP installed; a merge from the cmux VM gates on CI and so may not include such a ticket.

## Repointing the `.scratch/v1/run-tools/` copies (the orchestrator, after the merge)

- `gates.sh`: `exec /Users/andrewwatson/Claude-Projects/claudia-kermin/tools/gates/gates.sh "$@"`
- `gates2.sh`: `exec /Users/andrewwatson/Claude-Projects/claudia-kermin/tools/gates/gates.sh "$@"` (drop the rerun loop)
- `merge-step.sh`: `exec /Users/andrewwatson/Claude-Projects/claudia-kermin/tools/gates/merge-step.sh "$@"`
- `rec-local.sh` already calls `gates2.sh`; leave it.
- a timing run: `/Users/andrewwatson/Claude-Projects/claudia-kermin/tools/gates/gates-timing.sh`

## Handover step for the dispatch brief (paste as the last step)

> **Last step: hand over a branch that is ready to merge.** After your final commit, in your worktree:
> 1. `git merge main` (a conflict you cannot resolve in your own files: stop, say so in notes). Rebuild. For a prefix change, set `TodaysEstimate` to the figure `TodaysEstimateIsTheMeasuredFigure` prints and add `docs/prefix-budget/<ticket>.md` (delta, reason, measured figure).
> 2. Run `tools/gates/gates.sh` in the worktree (it runs no Timing test). Every line must be PASS.
> 3. Commit the merge, then put `"tested_against_main": "<git rev-parse main>"` in the result file.
>
> The orchestrator then runs only `tools/gates/gates.sh` on main, with no extra build, when main is still at that SHA.
