# Gate scripts (#623)

| Script | What it does | When |
|---|---|---|
| `gates.sh` | One `dotnet build -warnaserror`, then Core (`--filter "Category!=Timing"`), Kerboscript (through `kerboscript.sh`) and ScriptCheck tests with `--no-build`, KsCompile over `tests/blocks` and `tests/tools` from the built dll, and every forbidden-call / syntax-error fixture in one KsCompile call. After the build these steps run at once and their lines print in the fixed order (#702); `GATES_SERIAL=1` runs them one after another. `KOS_PLUGINS_DIR` names the folder holding `kOS.Safe.dll` (default `~/KSP_spike/GameData/kOS/Plugins`); `GATES_NO_KSP=1` builds every project but the `<NeedsKSP>true` ones (the Adapter and the kOS addon) and prints a SKIP line for them. A pass is recorded against the committed tree (with those settings and the SDK version) in the clone's shared git directory, `claudia-gates-passed`; a later run on the same tree, clean, in any worktree prints `PASS cached` and stops (#1005). `GATES_NO_CACHE=1` runs it anyway. Idle M-series machine, 2026-11-04, three runs each: serial median 178 s, parallel median 115 s (35% less). Parallel with `SHARDS=9` took 127 s and `SHARDS=8` 123 s, so `SHARDS` stays at the default (one per performance core, 10 here). | The merge gate, in a worktree or on main; CI runs it too, with `GATES_NO_KSP=1` and kOS.Safe from the kOS release (#1004). |
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

## Handover step for the dispatch brief, Mac sessions (paste as the last step)

> **Last step: hand over a branch that is ready to merge.** After your final commit, in your worktree:
> 1. `git merge main` (a conflict you cannot resolve in your own files: stop, say so in notes). Rebuild. For a prefix change, set `TodaysEstimate` to the figure `TodaysEstimateIsTheMeasuredFigure` prints and add `docs/prefix-budget/<ticket>.md` (delta, reason, measured figure).
> 2. Commit everything, so the worktree is clean.
> 3. Run `tools/gates/gates.sh` in the worktree (it runs no Timing test). Every line must be PASS. A fix means a new commit and a new run.
> 4. Put `"tested_against_main": "<git rev-parse main>"` in the result file.
>
> The orchestrator then merges and runs `tools/gates/gates.sh` on main. When the merged tree is the one the agent gated (main had not moved, or the merge changed nothing), that run prints `PASS cached` and takes a second; otherwise it runs in full (#1005).

## Merging from the cmux VM: CI is the gate

The player's policy (2026-11-13): sessions on the Mac gate 100% locally, with the full suite, and use no CI until a PR is opened. Sessions on the cmux VM gate on CI, never on the VM itself. The player's rulings for the VM flow (2026-11-13): agents may use `dotnet` on the VM for builds and single targeted tests only, never the full suite or `gates.sh`; the orchestrator batches a wave's tickets into one CI run; the batch merges through its PR, not by an admin push to main.

On the VM, set `KOS_PLUGINS_DIR` to a folder holding `kOS.Safe.dll` from the kOS release CI uses (`KOS_RELEASE_TAG` in `.github/workflows/ci.yml`), for building the projects that need kOS. Pass `-p:KOSPluginsDir="$KOS_PLUGINS_DIR"` to a `dotnet build` or `dotnet test` by hand.

### Handover step for the dispatch brief, VM sessions (paste as the last step)

> **Last step: hand over a branch that CI passed.** Your branch is `ci/<ticket>-<slug>`; never push an `agent/*` branch. In your worktree:
> 1. `git merge main` (a conflict you cannot resolve in your own files: stop, say so in notes). For a prefix change, set `TodaysEstimate` to the figure `TodaysEstimateIsTheMeasuredFigure` prints and add `docs/prefix-budget/<ticket>.md` (delta, reason, measured figure).
> 2. Commit everything. Don't run `tools/gates/gates.sh` or the whole test suite on the VM; build, and run single tests with `--filter`, only.
> 3. Push the branch and open a draft PR: `gh pr create --draft --base main --title "<ticket>: <what it does>" --body "Part of #<ticket>. Merges in a batch."`. CI runs on it.
> 4. Wait for the run (`gh pr checks <pr> --watch`). Red: fix, commit, push, wait again.
> 5. Put `"ci_run": "<run URL>"`, `"ci_sha": "<the commit CI passed>"` and `"pr": <number>` in the result file.

### The orchestrator's merge of a wave

1. **Hold back** each ticket whose branch changes `src/ClaudiaKerman.Adapter` or `src/ClaudiaKerman.KOS` (`git diff --name-only main...<branch>`): CI can't build them, so they merge from a Mac session (see What CI can't check). List them for the player.
2. **Batch the rest.** Cut `ci/batch-<date>-<k>` from main and merge each green ticket branch into it, one `git merge --no-ff` per ticket. A textual conflict goes back to that ticket's agent ("merge main into your branch"). No `dotnet` runs here.
3. **One PR for the batch**, its body listing each ticket with one line on what it does. CI runs once for the wave.
4. **Keep or abort.**
   - Green, and main has not moved since the batch was cut: merge the batch PR with a merge commit (`gh pr merge <pr> --merge`), so each ticket's commits stay visible. Close each ticket's draft PR with "Merged in batch #<pr>".
   - Green, but main moved: merge main into the batch branch, push, and wait for CI again.
   - Red: don't merge. Match the failing tests to a ticket, drop that ticket from the batch (cut the batch again without it), send it back to its agent with the failure, and rerun CI on the rest. If no ticket clearly owns the failure, split the batch in half and run CI on each half.
5. **After the merge**, pull main on the VM. The in-game check (install into `~/KSP_spike`, the player's checklist) stays on the Mac.
