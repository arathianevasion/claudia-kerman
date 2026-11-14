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

## Checks

- `bash tools/gates/gates.sh`: proven to fail on 2026-11-13, breaking: flipped Assert.Empty to NotEmpty on clear.Decouplers in LiftoffClearTests.cs

