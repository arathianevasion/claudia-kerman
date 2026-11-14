# Spike: compile the Core Library once per test process (#1074)

*Measured 2026-11-14 on the Mac (M-series, 14 cores), Release build, one `dotnet test` process per class.*

**Answer: sharing works.** `KosHarness.Run` now compiles the Core Library once per process and links each test's freshly compiled
Mission Script after it. All 1,229 existing Kerboscript tests pass with no assertion changed (1,232 with the three new `HarnessTests`). The whole Kerboscript
project takes 79 s in one process, and the sharded gate step (`tools/gates/kerboscript.sh`, 10 processes) takes 39 s. The rest of
the full local gate (`tools/gates/gates.sh`) is left to the run's merge gate on the Mac.

## 1. Where a test's time went: compile against run

The figures come from a temporary stopwatch round the compile (`Compile` + `AddParts`) and the run loop in `KosHarness.Run`.
That instrumentation was not committed.

| Class | Tests | `Run` calls | Compile | Run | Compile share |
|---|---:|---:|---:|---:|---:|
| TunePeTests | 55 | 67 | 105.9 s (1.58 s each) | 0.7 s | 99.3 % |
| DeorbitTests | 60 | 55 | 39.4 s (0.72 s each) | 0.1 s | 99.7 % |
| LandAscendTests | 188 | 452 | 472.8 s (1.05 s each) | 51.9 s | 90.1 % |
| ReentryBrakeFlowTests | 31 | 23 | 27.9 s (1.21 s each) | 0.3 s | 99.1 % |

Each bundle compiles 10,635 library lines plus a few test lines, so each compile costs about as much as compiling the library
alone. Running the script is almost free, except in LandAscendTests, whose rig simulates whole descents.

## 2. Sharing the compiled library

### How it works (`tests/ClaudiaKerman.Kerboscript.Tests/KosHarness.cs`, `Link`)

- `Run` recognises a bundle with `CoreLibrary.LibraryStartLine`; `CoreLibrary.Bundle` is read, not changed. A source that is not a
  bundle compiles whole, as before.
- **The library is compiled once** from `CoreLibrary.Bundle("")`, in its own `KSScript` context. The compiled parts are cached
  together with the context's `LabelIndex` and `MaxScopeIdSoFar`.
- **Each test's Mission Script is compiled alone** in a fresh `Context` whose two counters start where the library's ended. The two
  units are then linked as one program: library parts first, then the script's.
- **Why the counters:** kOS numbers labels (`@0001`) and scope ids per context. Linking two separate compiles fails with the
  output below. Continuing the counters gives the same unique numbering a single compile gives.

  ```
  kOS.Safe.Exceptions.KOSCompileException : ProgramBuilder.ReplaceLabels: Cannot add label @0001, label already exists.  Opcode: pushscope 1 0
  ```

  kOS doesn't expose the per-`KSScript` context, so the harness reads the private `KSScript.contexts` field by reflection. If a
  kOS release renames that field, the harness throws at once and names the field.
- **Hoisting:** kOS hoists each file-level function into a `PushDelegate` / `store $name*` pair at the top of its file's main code.
  The library's parts come first, so all its functions are stored before the Mission Script's first statement, as they are at the
  top of a bundle. A Mission Script function with the same name as a block wins in both forms. `HarnessTests` pins that.
- **Directives:** in a bundle the library compiles under the Mission Script's leading `@lazyglobal` / `@clobberbuiltins`, because
  `Bundle` drops the library's own. So the cache is keyed by those leading directives, and each set gets its own library compile.
  The Mission Script still compiles with them too.
- **Registered rig functions:** the compiler emits `call name()` (a built-in) only when kOS's function table knows the name, and
  emits a user-function call otherwise. Rigs `Register` their stand-in built-ins while the process runs. So the cache key also
  includes the set of registered rigs: after a new registration, the next bundle compiles the library again. Today that means at
  most a few library compiles per process.
- **Reuse is safe across tests:** linking replaces the relocatable opcodes in the new program list and sets branch distances, which
  are relative. Every test also gets a fresh CPU. 1,229 tests passing in any order, in one process and in ten shards, is the
  evidence for this.
- `CompileError` still compiles the whole text. It is a compile-only check, and the compile is all of its cost.

### Where the shared form differs from a bundle (no test depends on either)

- **Line numbers in errors from library code** count from the start of the library's own compile, not from the end of the Mission
  Script. Errors in Mission Script lines are unchanged, because the script still starts at line 1.
- **Locks and triggers:** the Mission Script compiles in a fresh context that carries over only the library's label and scope
  counters, not its locks or user functions. In a bundle, a `lock` the Mission Script and a block both declare under the same name
  shares one entry; with sharing, each half gets its own. No current test declares such a lock.
- **`defined <block name>`:** in a bundle this probe failed with "Number of arguments passed in didn't match"; with sharing it
  prints `False`. Neither form is a meaningful way to test for a block.

## 3. Cache keyed on the full source text

Not needed, because parts can be shared. Such a cache would also help little: almost every test bundles a different script, so the
compile it would save is rare.

## 4. Before and after, one process per class

| Class | Before | After | Change |
|---|---:|---:|---:|
| TunePeTests | 108 s | 1 s | about 100× |
| DeorbitTests | 39 s | 1 s | about 40× |
| LandAscendTests | 529 s | 35 s | 15×; what remains is run time |
| ReentryBrakeFlowTests | 28 s | 1 s | about 28× |
| Whole Kerboscript project, 1 process | not measured; CI took 11 min sharded across cores (run 43627861755) | 79 s | |
| `tools/gates/kerboscript.sh`, 10 processes | | 39 s | |

## Recommendation

Merge the shared library into `KosHarness`. It changes no test, no `CoreLibrary` code and nothing the game runs, and it takes the
Kerboscript step from the longest part of CI to well under a minute. The remaining time is LandAscendTests' run time, and with
that, one process per core buys only 2× (79 s → 39 s). Reducing the shard count is out of scope here; it went to the run as a finding for triage.
