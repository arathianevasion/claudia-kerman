# Contract fixtures (#1037)

One Game Snapshot per accepted Contract, in the form the capture button writes them (`MissionFolder.WriteContractSnapshots`: the save's `ClaudiaKerman/contracts/<Kind>-<guid start>-ut<UT>.json`). Core tests read them as plain data; the stock type is the file name up to its first `-` or `.`.

**The fixtures without a GUID in their name are provisional**: none of those was recorded in game. Each was written by hand from the research branches (`research/contract-api`, `research/stock-contract-types`, `research/contract-lifetime`): the Contract Requirement tree and the save keys the research saw, with made-up but plausible values. The snapshots carry no craft and no body table. The player records the real ones in a 1.12 career (`docs/in-game/contracts-in-snapshot.md`); a recorded snapshot joins the table below under the name the capture gave it, with any setting that differs from the research noted. A provisional file stays while tests read it.

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
| `SentinelContract.json` | SentinelContract | the research's type table (no save keys); the solar-orbit rule from the 1.12 localization data | every key of `SentinelParameter` (`FocusBody` as a body index, `TotalDiscoveries` and `RemainingDiscoveries` as the API's public fields name them); whether a `SpecificOrbitParameter` comes with it |
| `CometDetectionContract.json` | CometDetectionContract (1.10) | the research's type table (no save keys) | every key of `CometDetectionParameter` (`count`, `partName` are guesses) |
| `DeployedScienceContract.json` | DeployedScienceContract (Breaking Ground) | the research's type table (no save keys) | every key of `CollectDeployedScience` (`targetBody`, `subject`, `percentage` are guesses) |

#1033 reads the Target Vessel Contracts' vessel by these provisional keys: a rescue's `kerbalName` (the kerbal aboard the spawned vessel) and `recoveryType` (`1`, a kerbal: any other, or an `AcquirePart` or `RecoverPart` Requirement, is part recovery), and `vesselName` on a repair's `RepairPartParameter` or a construction's `ConstructionParameter` (or `cometName` / `targetName` for a comet sample). The recording confirms them.

No fixture yet for the types whose tree no public save shows: ISRUContract, ARMContract, RoverConstructionContract and CometSampleContract. The in-game recording adds them.

## Recorded in game

Captured by the Adapter in the Career Test save (KSP 1.12, 2026-11-14), copied byte for byte from `ClaudiaKerman/contracts/`. They predate the Contract `title` field (#1260), so they carry none.

| File | Type | Differs from the provisional fixture |
|---|---|---|
| `ExplorationContract-e087d8d1-ut789.json` | ExplorationContract, REACHSPACE over Kerbin | `targetBody` is the body's index (`1`), not its name |
| `ExplorationContract-36a2d4f7-ut24.json` | ExplorationContract, SCIENCE over Kerbin | as above |
| `PartTest-20ebfabe-ut789.json` | PartTest, haul of the RT-10 "Hammer" FLYING at 51–56 km, 100–260 m/s | the PartTest Requirement's `body` is the body's name (`Kerbin`); the conditions are its siblings, ReachDestination's `dest` an index |
| `SurveyContract-9f444a7c-ut789.json` | SurveyContract, a gravity scan high over the Mun | each waypoint's `band` is the game's `FlightBand` number (`3`, HIGH; the Contract's own `targetBand` is the name); `targetBody` an index |
| `TourismContract-1fbf2657-ut545547.json` | TourismContract, the Ferry of Mission m20261126-040610-a4b7 (2026-11-26): two tourists, the Mun, Kerbin orbit and Minmus (#1377) | captured after #1260, so it carries its `title`; each KerbalDestinationParameter's `targetBody` an index, with its `kerbalName` |

`../contract-card/2026-11-19-career-test.json` is the whole Game Snapshot of the Artisan's flight of 2026-11-19 (build e01560f), with its four accepted Contracts: the three above, its PartTest Requirement by then `Complete` (the part aboard), and an ORBIT exploration Contract over Kerbin.
