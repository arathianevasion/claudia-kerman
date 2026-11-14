# Accepted Contracts in the Game Snapshot, and a snapshot per stock Contract type (#1037)

The mod now reads the player's accepted Contracts into the Game Snapshot (ADR 0021), from each Contract's saved form and the game's public members, and the debug capture button also writes one Game Snapshot per accepted Contract. Core tests cover the snapshot's form and the per-Contract writer (`SnapshotContractsTests`, `ContractFixturesTests`, `MissionTests`); these checks need the game. Install and where to look are as in `phase-3-gate.md`. The snapshots are in the save's `ClaudiaKerman/snapshots` folder and the per-Contract snapshots in `ClaudiaKerman/contracts`.

## Check 1: the snapshot carries the accepted Contracts

1. In a 1.12 Career save, accept two Contracts in Mission Control (a part test and an exploration Contract are easy).
2. Put a craft on the pad, open the Mission window and press "Capture Game Snapshot (debug)".
3. The log says a Game Snapshot was saved, and that one for each accepted Contract (2) was saved to the `contracts` folder.
4. In the whole snapshot, `contracts` has `limit` 2 at Mission Control level 1 (7 at level 2, `null` at level 3), `active` 2, and both Contracts under `accepted`, each with its `guid`, `kind`, `state` "Active", a `deadline_ut` (none for the exploration Contract), `rewards`, `settings` and its `requirements` tree. A part test's altitude and speed bands are in its Requirements' `settings` (`minAlt`, `maxAlt`, `minSpd`, `maxSpd`).
5. With an active Strategy that changes Contract funds (e.g. one from the Administration building), the `rewards.funds` figure matches what Mission Control shows for the Contract, not the base figure.
6. Nothing in Mission Control changed: no Contract was accepted, declined or cancelled by the mod.

## Check 2: the keys survive a save and load, and a scene change

1. After Check 1, note each Requirement's `key` in the snapshot.
2. Go to the Space Center and back to the pad, then capture again: every key is the same.
3. Quicksave, quickload, and capture again: every key is the same.

## Check 3: no Contract, no change

1. In the same Career, with no Contract accepted, capture a snapshot: it has no `contracts` entry.
2. In a Science save and a Sandbox save, capture a snapshot: no `contracts` entry, and no `contracts` folder is written.

## Check 4: a snapshot of each stock Contract type (slice 1)

For each stock Contract type the Career offers you (accept as many as Mission Control allows, capture, then cancel or finish them to make room for the next), capture once with it accepted. Commit each per-Contract snapshot from `ClaudiaKerman/contracts` to `tests/ClaudiaKerman.Core.Tests/Fixtures/contracts/`, named for its type as the provisional ones are, replacing the provisional file of that type, and move its row in that folder's `README.md` to "Recorded in game", noting any setting that differs from the research's.

Tell the ticket which types were recorded, and any setting that differs from the provisional fixture.
