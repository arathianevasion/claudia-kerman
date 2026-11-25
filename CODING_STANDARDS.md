# Coding standards

Product level: how code is written in this repo, changed by PR. Read by workers and verifiers at the point of use.

## Always ask

Things that must always come back to the Artisan. Only the Artisan changes this list (setup or a retro lesson).
- Always ask about any new libraries that aren't aleady included.
- Growing kerboscript/library past its line limit
- Decompiling KSP or posting stock game code
- Porting code under another license (such as MechJebLib)
- Merging Adapter or KOS changes from anywhere but the Mac

## Guardrails

- `pr-boundaries`: one PR per slice (set at setup).
- An in-game scenario or block test checks its own starting state before the sequence runs: the situation it is named for (landed, orbiting), ground contact settled, slope under the runner's limit, and the stage whose engines the block will burn. It stops with a message naming the failed precondition.

## Checks

- `bash tools/gates/gates.sh`: proven to fail on 2026-11-13, breaking: flipped Assert.Empty to NotEmpty on clear.Decouplers in LiftoffClearTests.cs

