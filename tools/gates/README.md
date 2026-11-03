# Gate scripts (#623)

| Script | What it does | When |
|---|---|---|
| `gates.sh` | One `dotnet build -warnaserror`, then Core (`--filter "Category!=Timing"`), Kerboscript (through `kerboscript.sh`) and ScriptCheck tests with `--no-build`, KsCompile over `tests/blocks` and `tests/tools` from the built dll, and every forbidden-call / syntax-error fixture in one KsCompile call. | The merge gate, in a worktree or on main. |
| `kerboscript.sh` | After a build: lists the Kerboscript tests, deals the test methods out to one test process per performance core (`SHARDS` overrides), runs them at once and checks the total run equals the total listed. kOS.Safe's static state stays isolated per process; inside a process `KosHarness.Gate` still serializes it (#681). | Called by `gates.sh` (#625). CI still runs the project in one `dotnet test`. |
| `gates-timing.sh` | Builds, then runs only `Category=Timing` (every `Timing.Within` or Stopwatch-budget test). One rerun allowed. | Once per wave, on an idle machine, before an install and before the Phase gates. |
| `merge-step.sh N "msg"` | Calls `rec-local.sh` (merge, gates, clean up). No Core rerun loop. rc 2 conflict, 3 gate failure. | The orchestrator's merge of one ticket. |
| `kerboscript-stress.sh` | After a build: runs ChangeOrbitTests, DockTests and RendezvousTests in one test process per run, `RUNS` times (default 20), with about half the cores loaded by `yes`; stops at the first failing run and prints each failing test's message. `FILTER` narrows it. | Repro for #681, or any time a Kerboscript flake is suspected. |

All of them run on the checkout they live in (`GATES_ROOT` overrides). A new wall-clock test must carry `[Trait("Category", "Timing")]`; check with
`grep -rLZ 'Category", "Timing' $(grep -rlE 'Timing\.Within\(' tests --include=*.cs)` (no file besides `Timing.cs` may be listed; a helper that wraps `Timing.Within` needs its callers tagged).

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
