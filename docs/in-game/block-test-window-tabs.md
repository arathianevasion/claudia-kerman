# Block test window: result tabs and the kOS bridge tab (#922)

Debug tools only. Set `debug_tools = true` in the player config first. Install and `KSP.log` location are as in `phase-3-gate.md`. `saves/claudia_block_tests/block-tests.log` is the source of the tabs.

## Check 1: the tabs

1. From the main menu, the window "Claudia Kerman: block test runner (debug_tools)" shows four tabs named exactly `Block Tests - Unvalidated (N)`, `Block Tests - Failed (N)`, `Block Tests - Passed (N)` and `kOS Bridge Tests`.
2. The three counts add up to the "N tests shipped" line. A test with no line in `block-tests.log` is under Unvalidated; its latest PASS puts it under Passed, its latest FAIL under Failed (a `tools/` test sorts the same way).
3. On Failed and Passed each button reads `Run <test> (<date>)`; Failed also shows the headline reason after the date, on one line, cut to fit.

## Check 2: a run moves its test

1. On Unvalidated, run one block test. When it ends, the test leaves Unvalidated and sits under Passed or Failed (matching its last line in `block-tests.log`), and the counts change.
2. Return to the main menu after the run (the window remembers the last tab picked) and re-run a test from Failed: if it passes it moves to Passed.

## Check 3: the kOS Bridge Tests tab

1. Outside Flight the tab says the bridge tests need Flight.
2. In Flight with a kOS processor, open the window ("Block tests (debug)" button, or the window if shown) and pick the tab: processor line, test buttons (`report`, `runtime_error`, `no_report`, `long_run`), Abort, status and result work as the old kOS bridge window did. Hide closes the window.
3. The old window "Claudia Kerman: kOS bridge test (debug_tools)" no longer opens in Flight.
