# Script Check and runtime gates instead of a sandbox

kOS has no sandbox: a script can revert or overwrite saves, switch vessels, use physics warp, write or delete anything in the shared `Ships/Script` folder, change global kOS settings and alter other loaded vessels. We guard against Claude's mistakes, not a hostile author, with two layers: a **Script Check** that reads the mod's own compiled opcodes against a forbidden list before Approval, and a few Harmony patches on kOS internals that refuse the most harmful calls (KUNIVERSE save/load/revert/switch, Archive writes, `CONFIG` setters, physics warp, `debugfreezegame`, changes to other vessels, and acting on another kOS processor) only while a mod-started program runs on that processor. We rejected a check-only approach because runtime-computed names and `run` of written files slip past it, and a full sandbox because kOS offers no hook for one and the threat doesn't warrant it. The cost is coupling to kOS internals, which a kOS upgrade can break; the Core Library and RSVP must pass the same check in CI.

**Amendment (the gate list as built, #49):** the runtime gates refuse, from a processor running a mod-started program:

- KUNIVERSE save, load, revert, switch and launch: `QUICKSAVE`, `QUICKLOAD`, `QUICKSAVETO`, `QUICKLOADFROM`, `REVERTTOLAUNCH`, `REVERTTOEDITOR`, `REVERTTO`, `FORCESETACTIVEVESSEL`/`FORCEACTIVE`, `LAUNCHCRAFT*`, and setting `ACTIVEVESSEL`.
- Archive writes and deletes, however they are reached (`copypath`, `deletepath`, `log`, `writejson`, `compile`...).
- Every `CONFIG` setter (`IPU` included).
- Physics warp: setting the warp mode to `PHYSICS`, or a warp rate above 1× while in physics mode, through `WARP`, `WARPMODE` or any `TIMEWARP` structure; and `TIMEWARP:WARPTO` or `WARPTO()` while in physics mode, since KSP's warp-to warps in the mode already set (#133, [ADR 0004](0004-in-script-warp-replaces-wait-cap.md)). On-rails warp, `WARPTO` included, is allowed. *Superseded by [ADR 0008](0008-physics-warp-on-coasts.md): physics warp is allowed on a Coast, under the rules there.*
- `debugfreezegame`.
- Other-vessel mutators: any suffix set, and the calls that act (`DOEVENT`, `DOACTION`, `SETFIELD`, engine, docking-port, science and part calls), on a vessel, part or part module that isn't on the running processor's vessel.
- Other processors (decided in triage): `DOEVENT`, `DOACTION`, `SETFIELD`, `ACTIVATE`, `DEACTIVATE` and any suffix set on a **kOS processor module other than the one running the program**, on the craft itself as well as on other vessels, so a script can't cut Claudia off from a Child Vessel's processor. A script may act on its own processor; if it turns itself off it just ends without a Script Result, like any script that fails.

The gates are keyed on the run id of the mod's start, so they come down when that program ends, and a call from anywhere else (the player's terminal or programs on a processor with no mod run, a kOS window) is never gated. A trip refuses the call, stops the program, makes the craft safe and Debriefs the run with the reason "blocked: *what it tried*". Every patched member is in the startup kOS internals check, and the mod starts no run if the gates could not be installed. The runaway hard stop (300% of planned duration, waits included) is not a gate: the Mission loop times it in game time and aborts through the normal abort path; it never changes the IPU setting.

## Amendment (the other-vessel taint, #654, 2026-11-04)

The Script Check's other-vessel rule, read from the compiled opcodes in `src/ClaudiaKermin.ScriptCheck/OpcodeReader.cs` and pinned by the `#654` tests in `tests/ClaudiaKermin.ScriptCheck.Tests/KosScriptCheckTests.cs`. A value is another vessel when:

- it is `target`, or a variable that holds another vessel;
- it is a call to `VESSEL(...)`, a `BUILDLIST` of `"targets"`, or a user function that returns another vessel (a delegate call may, when the reader can't name the function);
- it is a suffix or index read from another vessel (`target:parts[0]`).

The reader does not track a vessel reached through the mod's own `ADDONS:CLAUDIA` calls (the Core Library's `targetvessel():vessel` route, for one). A Mission Script can't make those calls (they are block-internal, so the check refuses them there), so the gap is only in the library's own code.

Assignment follows kOS's scopes. `LOCAL` binds the name in the scope the code is in, and `GLOBAL` in the global scope. `SET` binds it in the innermost scope that has surely declared the name on this path, else the global scope; a scope inside that one that declares the name somewhere is also tainted, since it may hold the name on this path. Each PushScope names its scope and its parent, so a read is taken in the same scopes. A reader that doesn't know which scopes the code is in (a call's or trigger's code, two paths that meet with different scopes, a parent it never saw) taints the name in every scope when it stores a tainted value there, and counts the name tainted in any scope when it reads it there.

The rule is not path-sensitive: taint only accumulates, so a clean value stored later does not clear a name already tainted in that scope.

A parameter holds another vessel when a call passes another vessel to its function. A call the reader can't name (a delegate, or a name resolved at run time) may pass it to any function. Code the reader can't place in a function takes its parameters as tainted whenever any function has been passed another vessel. A function that returns another vessel makes a call to it return one. The reader walks all the code, then again while a pass learns something new about a name, parameter or return; the findings come from one more pass after that.

A call that reaches another vessel's mutator (`DOEVENT` and the others under "Other-vessel mutators" above) is refused whether it names the vessel directly or through any of these routes. A script's own parts (`ship:partstagged`) are not tainted.
