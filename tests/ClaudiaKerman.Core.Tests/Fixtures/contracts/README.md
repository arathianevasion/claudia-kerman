# Contract fixtures (#1037)

One Game Snapshot per accepted Contract, in the form the capture button writes them (`MissionFolder.WriteContractSnapshots`: the save's `ClaudiaKerman/contracts/<Kind>-<guid start>-ut<UT>.json`). Core tests read them as plain data; the stock type is the file name up to its first `-` or `.`.

**Every fixture here is provisional.** None was recorded in game yet. Each was written by hand from the research branches (`research/contract-api`, `research/stock-contract-types`, `research/contract-lifetime`): the Contract Requirement tree and the save keys the research saw, with made-up but plausible values. The snapshots carry no craft and no body table. The player records the real ones in a 1.12 career (`docs/in-game/contracts-in-snapshot.md`); a recorded snapshot replaces the provisional file of its type, keeps its name and moves to the recorded table, with any setting that differs from the research noted.

## Provisional

| File | Type | From | Not yet confirmed |
|---|---|---|---|
| `PartTest.json` | PartTest, part test | kRPC's 1.12.5 career save | `notes` text; whether stock sets `Optional` anywhere |
| `PartTest-haul.json` | PartTest, haul | kRPC's 1.12.5 career save | the haul's conditions are siblings of the PartTest Requirement, not its children. The key is `haul`, as the save shows; `research/stock-contract-types` calls it `hauled` |
| `ExplorationContract.json` | ExplorationContract | kRPC's 1.12.5 career save | `targetType` names beyond those seen |
| `SurveyContract.json` | SurveyContract | 1.12.5 and 1.4.3 saves | the `flightThreshold` units |
| `SatelliteContract.json` | SatelliteContract | a 1.4.3 save | 1.12 keys; `deviationWindow` units (percent or degrees) |
| `StationContract.json` | StationContract | a 1.0.5 save | 1.12 keys and tree |
| `BaseContract.json` | BaseContract | a 1.0.5 save | 1.12 keys and tree; the optional CrewTrait Requirement is a guess at how it shows |
| `TourismContract.json` | TourismContract | a 1.0.5 save | 1.12 keys and tree |
| `RecoverAsset.json` | RecoverAsset, rescue | a 1.0.5 save | 1.12 keys; `failOn` and `winOn` values |
| `CollectScience.json` | CollectScience | a 1.0.5 save | 1.12 keys |
| `PlantFlag.json` | PlantFlag | the research's type table (no save keys) | every key: `targetBody` is a guess |
| `OrbitalConstructionContract.json` | OrbitalConstructionContract | a 1.11+ save's tree (no save keys given) | every key of `ConstructionParameter` (`partName`, `vesselName`, `targetBody` are guesses) and `CrewTraitParameter`'s 1.12 keys |
| `VesselRepairContract.json` | VesselRepairContract | a 1.11+ save's tree (no save keys given) | every key of `RepairPartParameter` (`partName`, `vesselName`, `targetBody` are guesses) and `CrewTraitParameter`'s 1.12 keys |
| `ROCScienceArmContract.json` | ROCScienceArmContract (Breaking Ground) | the research's type table (no save keys) | every key: `targetBody`, `subject` and `rocType` are guesses at `CollectROCScienceArm`'s body, subject and surface-feature type |
| `ROCScienceRetrievalContract.json` | ROCScienceRetrievalContract (Breaking Ground) | the research's type table (no save keys) | every key, as for the arm scan; whether the sample must be recovered or transmitting it is enough |
| `GrandTour.json` | GrandTour | the research's type table (no save shows its tree) | every key: `VesselDestinationParameter` with `targetBody` (body index) and `targetType` (flight-log entry: Flyby, Orbit, Land) is the research's guess; whether the destinations sit under one parent |

No fixture yet for the types whose tree no public save shows: ISRUContract, ARMContract, RoverConstructionContract, SentinelContract, CometDetectionContract, CometSampleContract, and Breaking Ground's DeployedScienceContract. The in-game recording adds them.

## Recorded in game

None yet.
