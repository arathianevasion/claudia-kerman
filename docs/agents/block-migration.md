# Block migration line

ADR 0014: Building Blocks keep their Kerboscript interface while their flight logic moves into C# in Core as tickets rework them. So any agent brief that touches `kerboscript/library/` carries a **Block migration** line naming:

- **the logic that moves to C#**, or "none" with the reason, such as "steering, throttle or staging control only (guardrail 2)";
- **the parity test** that runs the Kerboscript and the C# on the same inputs (the Kerboscript and that test are deleted in the same ticket);
- **the `tests/blocks` scenarios the player must fly**, which the ticket also adds to the in-game checklist in `docs/in-game/`.

Example: `Block migration: land's slope scoring moves to Core (SlopeScore); parity test SlopeScoreParityTests; player flies tests/blocks/land-slope.ks.`

The same briefs follow the rest of ADR 0014: a changed `//|` line updates its snapshot under `tests/ClaudiaKerman.Core.Tests/Fixtures/block-docs/` and says so in the commit message; a ticket that shrinks the library lowers `tools/gates/kerboscript-loc.txt`, one that grows it raises it with a reason in the commit message.
