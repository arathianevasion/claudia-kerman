# How KSP exposes accepted Contracts and their Contract Requirements

Research ticket #1011 for map #1010 (contract-driven Missions in Career). Public sources only: no KSP DLL was decompiled and no stock source is quoted. Where a fact comes from KSP's own API, the source is an open-source mod that calls it, a public save file that KSP wrote, or Anatid's public XML docs. KSP calls a Contract Requirement a `ContractParameter`; that name appears below only when citing the API.

## Summary

- **Listing accepted Contracts is public and simple.** `Contracts.ContractSystem.Instance.GetCurrentActiveContracts<Contract>()` returns the accepted Contracts. `ContractSystem.Instance.Contracts` holds offered and active ones, and `ContractsFinished` holds finished ones. Each `Contract` has public `Title`, `Description`, `Notes`, `Synopsys` (sic), `Keywords`, `ContractState`, `Prestige`, `ContractGuid`, the dates `DateAccepted`, `DateDeadline`, `DateExpire` and `DateFinished`, and the rewards `FundsAdvance`, `FundsCompletion`, `FundsFailure`, `ScienceCompletion`, `ReputationCompletion` and `ReputationFailure`. [kRPC `Contract.cs`, `ContractManager.cs`]
- **A Contract's type is its C# class.** kRPC uses `contract.GetType()`. The save writes the short name as `type = PartTest`, `SurveyContract` and so on. The stock types in 1.12.5 are `PartTest`, `CollectScience`, `PlantFlag`, `RecoverAsset`, `SatelliteContract`, `StationContract`, `BaseContract`, `SurveyContract`, `TourismContract`, `ISRUContract`, `GrandTour`, `ARMContract`, `SentinelContract`, `CometDetectionContract`, `CometSampleContract`, `VesselRepairContract`, `RoverConstructionContract` and `OrbitalConstructionContract`. The progression Contracts are `ExplorationContract`, `WorldFirstContract` and `ExploreBody`. `ContractSystem.ContractTypes` lists the registered types at runtime. [kRPC save, `WEIGHTS` node; public saves]
- **The Requirement tree is public.** Walk it with `ParameterCount` and `GetParameter(i)` on the `Contract` and on each `ContractParameter`. Each parameter has public `State` (`ParameterState.Incomplete`, `Complete` or `Failed`), `Optional`, `Title`, `Notes`, `Parent`, `Root` and `enabled`, plus per-Requirement rewards and penalties. A parameter has no id, and KSP rebuilds the objects when a game loads. So hold the Contract's `ContractGuid` plus an index path, not object references. kRPC does exactly this. [kRPC `ContractParameter.cs`, `Contract.cs`]
- **The shape of a Requirement tree varies within one type.** In a non-haul `PartTest`, the body, situation, altitude band and speed band are child Requirements of the `PartTest` parameter. In a haul `PartTest` (`haul = True`), they are siblings of it at the Contract's top level. Code must walk the tree generically, not assume one layout. [kRPC test save, 1.12.5]
- **Each Requirement's settings aren't uniformly public as typed fields.** Some are public: `SurveyWaypointParameter.wp`, `VesselSystemsParameter.requireNew`, and `SentinelParameter.RemainingDiscoveries` and `TotalDiscoveries`. Others are private, and Contract Parser reads them by reflection at a field index: `PartRequestParameter`'s part and module lists, `VesselSystemsParameter`'s module types, and `StationaryPointParameter`'s waypoint. That breaks silently if KSP reorders fields. [Contract Parser `parameterContainer.cs`]
- **The robust public route to settings is `Contract.Save(ConfigNode)`.** Serialise the Contract to a `ConfigNode` and read the keys KSP writes to the save. MKS does exactly this to read a rescue Contract's `recoveryType` and `kerbalName`, and LunaMultiplayer does it to ship Contracts across the network. The keys are the save-file keys, which are public and stable across 1.4 to 1.12 for the types sampled (see the table below). [MKS `RescueKerbalFilter.cs`; LMP `ShareContractsMessageSender.cs`]
- **The game marks Requirements met, and mods are told.** `GameEvents.Contract.onParameterChange(Contract, ContractParameter)` fires when a Requirement changes state. Each parameter also has its own `OnStateChange` event. The Contract-level events are `onAccepted`, `onCompleted`, `onFailed`, `onFinished` (success or failure), `onCancelled`, `onDeclined`, `onOffered`, `onContractsListChanged`, `onContractsLoaded`, `onRead` and `onSeen`. [Anatid XML; LMP `ShareContractsSystem.cs`; CC `ContractConfiguratorParameter.cs`] Caveat: Contract Configurator's own parameters set their state without firing the stock event, for performance. Mod-added Contracts may therefore not raise `onParameterChange`, so polling `State` is the safe confirmation. Stock Contracts are what matters here (mod Contracts are out of scope).
- **Threading.** This is all Unity and KSP game state, so it must be read on the main thread. That matches the Adapter's existing rule (`SnapshotReader`: "Main thread only"). The `ContractSystem` scenario runs in Flight, Tracking Station, Space Center and the Editor (`scene = 7, 8, 5, 6` in the save), so the VAB and SPH, where Start Mission happens, can read it. Before it loads, `ContractSystem.Instance` is null, and `ContractSystem.loaded` says when it has loaded. A scene change or quickload rebuilds every `Contract` and `ContractParameter` object, which is why kRPC holds GUID plus index path. Core should receive a plain snapshot (DTOs) built on the main thread, never live KSP objects.

## Sources

- **kRPC** (`main`): [`Contract.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/Contract.cs), [`ContractParameter.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractParameter.cs), [`ContractManager.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractManager.cs), [`ContractStateExtensions.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/ExtensionMethods/ContractStateExtensions.cs). Also kRPC's [KSP 1.12.5 career test save](https://github.com/krpc/krpc/blob/main/tools/krpctools/krpctools/game/ksp/saves/krpctest_career.sfs), written by the game.
- **Contract Parser** (DMagic, the library behind Contracts Window +): [`parameterContainer.cs`](https://github.com/DMagic1/KSP_Contract_Parser/blob/master/Source/parameterContainer.cs).
- **Contract Configurator**: [`ContractConfiguratorParameter.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/Parameter/ContractConfiguratorParameter.cs).
- **MKS**: [`RescueKerbalFilter.cs`](https://github.com/UmbraSpaceIndustries/MKS/blob/main/Source/KolonyTools/KolonyOptions/RescueKerbalFilter.cs). It calls `contract.Save(ConfigNode)` and then `GetValue`.
- **LunaMultiplayer**: [`ShareContractsSystem.cs`](https://github.com/LunaMultiplayer/LunaMultiplayer/blob/master/LmpClient/Systems/ShareContracts/ShareContractsSystem.cs) (every `GameEvents.Contract` event it subscribes to) and [`ShareContractsMessageSender.cs`](https://github.com/LunaMultiplayer/LunaMultiplayer/blob/master/LmpClient/Systems/ShareContracts/ShareContractsMessageSender.cs).
- **Anatid's XML docs** for the KSP API: [`Assembly-CSharp.xml`](https://github.com/Anatid/XML-Documentation-for-the-KSP-API/blob/master/Assembly-CSharp.xml). They are old (pre-1.0), but they hold the `GameEvents.Contract` event descriptions. The official 1.12.4 Doxygen at kerbalspaceprogram.com/ksp/api/ now returns 404 and is not archived.
- **Public career saves** (written by KSP): [CommNetConstellation 1.4.3 career](https://github.com/KSP-TaxiService/CommNetConstellation/blob/develop/saves/KSP1.4.3%20Career/CNCCareer/persistent.sfs) and [skasti 1.0.5 career](https://github.com/skasti/KSP_Saves/blob/master/saves/ThinkPad/persistent.sfs).
- The KSP wiki's Contracts page was behind bot protection and was not read.

## Listing Contracts

| Need | Public API | Notes |
|---|---|---|
| Accepted Contracts | `ContractSystem.Instance.GetCurrentActiveContracts<Contract>()` | Null-check `Instance` first. |
| State | `contract.ContractState` (`Contract.State`: `Generated`, `Offered`, `OfferExpired`, `Declined`, `Cancelled`, `Active`, `Completed`, `DeadlineExpired`, `Failed`, `Withdrawn`) | kRPC maps every value. |
| Stable id | `contract.ContractGuid` | It is written as `guid` in the save and survives loads. |
| Type | `contract.GetType()`, or the `type` key from `Save()` | Short class name. |
| Title and text | `Title`, `Description`, `Notes`, `Synopsys`, `Keywords` | `Title` is localised display text. Don't key logic on it. |
| Deadline | `DateDeadline` (UT seconds) | 0 when the Contract has no deadline (`deadlineType = None`, as in the progression Contracts). `DateAccepted` + the deadline span = `DateDeadline`. In the save, accepted 21837.94 + 9201600 = 9223437.94. |
| Rewards | `FundsAdvance`, `FundsCompletion`, `FundsFailure`, `ScienceCompletion`, `ReputationCompletion`, `ReputationFailure` | These are base values. Active Strategies change what the player actually gets. Contract Parser gets the effective value with `CurrencyModifierQuery.RunQuery(TransactionReasons.ContractReward, funds, sci, rep).GetEffectDelta(Currency.X)`. |
| Prestige | `contract.Prestige` (Trivial, Significant, Exceptional; the save writes `prestige = 0/1/2`) | |

## Walking the Requirement tree

```
for i in 0..contract.ParameterCount: p = contract.GetParameter(i)
  for j in 0..p.ParameterCount: p.GetParameter(j)   // recurse; Contract Parser stops at depth 4
```

Per Requirement: `State`, `Optional`, `enabled`, `Title`, `Notes`, `Parent`, `Root`, `FundsCompletion`, `FundsFailure`, `ScienceCompletion`, `ReputationCompletion`, `ReputationFailure`, and the class name (its kind). The save also writes `disableOnStateChange`. When it is true, a Requirement freezes once met and doesn't revert if the vessel leaves the condition. When it is false, as on the `PartTest` children, the Requirement can go back to Incomplete. That matters for "don't leave the leg until it's met". Stock `PartTest` children are written with `disableOnStateChange = False`.

None of the sampled stock saves has a Requirement written as optional. The `Optional` property exists, and Contract Configurator uses it, but these samples don't show whether any stock type sets it.

## What each stock Requirement's settings are (save keys)

Read these from `Contract.Save(node)` (each `PARAM` child node, `name` = class) or from the matching public field where one exists. Contract-level keys come first where they carry the setting.

| Requirement kind (class) | Seen under | Settings (save keys) | Sample |
|---|---|---|---|
| `PartTest` | PartTest | `part` (part name), `body`, `situation`, `haul`, `repeatability`, `notes` (whether to stage it or use Run Test). The Contract also has `part`, `haul`, `dest`, `sit` and `exp` | 1.12.5 |
| `ReachDestination` | PartTest | `dest` (body index) | 1.12.5 |
| `ReachSituation` | PartTest | `sit` (`FLYING`, `PRELAUNCH`, `LANDED`, ...) | 1.12.5 |
| `ReachAltitudeEnvelope` | PartTest | `minAlt`, `maxAlt` (m) | 1.12.5 |
| `ReachSpeedEnvelope` | PartTest | `minSpd`, `maxSpd` (m/s) | 1.12.5 |
| `ProgressTrackingParameter` | Exploration, WorldFirst | `targetBody`, `targetType` (`FIRSTLAUNCH`, `REACHSPACE`, `ORBIT`, `SCIENCE`, `FLYBY`, ...) | 1.12.5 |
| `SurveyWaypointParameter` | SurveyContract | `targetBody`, `experiment`, `flightThreshold` (m), `band`, `wpLatitude`, `wpLongitude`, `wpLandlocked`, `wpNavigationId`. The Contract also has `targetBand` (`LOW`/`HIGH`) and `centerLatitude`/`centerLongitude`. The public field `.wp` is a `FinePrint.Waypoint` | 1.12.5, 1.4.3 |
| `SpecificOrbitParameter` | SatelliteContract | `TargetBody`, `orbitType`, `inclination`, `eccentricity`, `sma`, `lan`, `argumentOfPeriapsis`, `meanAnomalyAtEpoch`, `epoch`, `deviationWindow` | 1.4.3 |
| `StationaryPointParameter` | SatelliteContract | `targetBody`, `longitude`, `deviation` (the waypoint is a private field) | 1.0.5 |
| `VesselSystemsParameter` | Satellite | `typeString`, `mannedStatus`, `requireNew`, `checkModuleTypes` (e.g. `Antenna\|Generator`), `launchID` | 1.4.3 |
| `PartRequestParameter` | Satellite | `partNames`, `partDescription` (the lists are private fields) | 1.4.3 |
| `SpecificVesselParameter` | Satellite | `targetVesselID`, `targetVesselName`, `targetVesselSituation` | 1.4.3 |
| `StabilityParameter` | Satellite | `holdSeconds` (a duration to hold) | 1.4.3 |
| `LocationAndSituationParameter` | Station | `targetBody`, `targetSituation`, `noun` | 1.0.5 |
| `CrewCapacityParameter` / `CrewTraitParameter` | Station | `targetCapacity` / `targetTrait`, `targetCount` | 1.0.5 |
| `ResourcePossessionParameter` | Station | `resourceName`, `goalResource` | 1.0.5 |
| `KerbalTourParameter` → `KerbalDestinationParameter` | Tourism | `kerbalName`; the child has `targetBody`, `targetType` (`Suborbit`, ...) | 1.0.5 |
| `AcquireCrew`, `RecoverKerbal` | RecoverAsset | `title`, `failOn`, `winOn`. The Contract has `kerbalName`, `partName` and `recoveryType` | 1.0.5 |
| `CollectScience`, `EnterOrbit`, `LandOnBody` | CollectScience, ExploreBody | `body` (index), `location` (`Space`/`Surface`) | 1.0.5 |

The 1.0.5 and 1.4.3 rows still need checking against a 1.12 save. Those types (Station, Base, Tourism, RecoverAsset, the 1.4+ construction, repair and comet types, ISRU, GrandTour, ARM, Sentinel) did not appear in the 1.12.5 sample. One quick in-game dump of `Contract.Save()` for each type would close this.

## Events

`GameEvents.Contract` has `onAccepted`, `onCancelled`, `onCompleted`, `onContractsListChanged`, `onContractsLoaded`, `onDeclined`, `onFailed`, `onFinished`, `onOffered`, `onParameterChange (Contract, ContractParameter)`, `onRead` and `onSeen`. Every parameter also exposes `OnStateChange (ContractParameter, ParameterState)`. Deadline expiry shows up as `ContractState == DeadlineExpired`, then `onFailed` and `onFinished`. Events fire on the main thread during game updates. The Adapter should turn them into Mission events (or a re-read) and not hold the objects.

## Implications for the map's decisions

- **Reasoning versus Core.** Everything above is deterministic data, so the read is Adapter code and the classification is Core code. That means a typed `ContractSnapshot`: GUID, type, deadline, rewards, and a Requirement tree with kind, settings dictionary, state, optional and index path. Mapping a Requirement kind to a phase, leg and Building Block is a lookup table on the class name. Pricing a set (delta-v, deadline) is Core work. Reasoning is only needed for the player-facing explanation, and for Contracts the table doesn't know.
- **Classification key.** Use the Requirement class name plus its save keys. Don't use `Title`, which is localised. Every stock kind seen has machine-readable settings in its save keys, so none needs reasoning from prose.
- **"The game decides when it's met."** Poll `State` each frame or tick, or listen to `onParameterChange`. Remember that `disableOnStateChange = False` Requirements can fall back to Incomplete, so for `PartTest` the leg must hold every sibling condition at once while the part is staged or tested.

## Only decompiling could confirm (flagged, not done)

- The exact completion and failure logic of each stock Requirement. For example: whether `PartTest` needs every sibling condition true at the instant of staging; the units of `deviationWindow` and `deviation` (percent or degrees); how `StabilityParameter.holdSeconds` resets; which Requirements fail rather than revert (`failOn`/`winOn` on `RecoverKerbal`).
- Whether a non-optional Requirement that fails always fails the stock Contract. Contract Configurator's `fakeFailures` workaround implies it does, but that isn't documented.
- The private fields' names and order (`PartRequestParameter`, `StationaryPointParameter`, `VesselSystemsParameter`). Avoid them by using `Save()`.
- The meaning of the positional `values = ...` list in the save. From the samples it is inferred as expiry span, deadline span, funds advance, completion and failure, science, reputation completion and failure, then date expire, accepted, deadline and finished. The public properties make this unnecessary.
- The full key set of the 1.4+ Contract types (construction, repair, comet, ISRU, GrandTour, ARM). An in-game `Save()` dump closes this without decompiling.
