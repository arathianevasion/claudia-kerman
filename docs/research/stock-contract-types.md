# The stock Contract types and what each requires (KSP 1.12)

Answers [#1012](https://github.com/arathianevasion/claudia-kerman/issues/1012), for the map
[#1010](https://github.com/arathianevasion/claudia-kerman/issues/1010) "Contract-driven Missions in Career".
Researched 2026-11-13 from public sources only. No KSP DLL was decompiled and no stock source is copied here.
Class and member names come from the public KSP API reference. Default values are the field initialisers shown
there; the game loads the live values from `GameData/Squad/Contracts/Contracts.cfg`, which can override them.

## Sources

- **[KSP wiki, Contracts](https://web.archive.org/web/20250924071344/https://wiki.kerbalspaceprogram.com/wiki/Contracts)**
  (Wayback copy, because the live wiki blocks automated clients). It gives the player-facing type list and behaviour.
- **KSP 1.12.4 API reference.** The official `kerbalspaceprogram.com/ksp/api` is offline (404), so this uses the
  community mirror [KSPModdingLibs/KSPDocsSite](https://kspmoddinglibs.github.io/KSPDocsSite/). It covers the namespaces
  [Contracts.Templates](https://kspmoddinglibs.github.io/KSPDocsSite/namespace_contracts_1_1_templates.html),
  [FinePrint.Contracts](https://kspmoddinglibs.github.io/KSPDocsSite/namespace_fine_print_1_1_contracts.html),
  [FinePrint.Contracts.Parameters](https://kspmoddinglibs.github.io/KSPDocsSite/namespace_fine_print_1_1_contracts_1_1_parameters.html),
  [Contracts.Parameters](https://kspmoddinglibs.github.io/KSPDocsSite/namespace_contracts_1_1_parameters.html),
  [Expansions.Serenity.Contracts](https://kspmoddinglibs.github.io/KSPDocsSite/namespace_expansions_1_1_serenity_1_1_contracts.html)
  (Breaking Ground), SentinelMission ([SentinelContract](https://kspmoddinglibs.github.io/KSPDocsSite/class_sentinel_mission_1_1_sentinel_contract.html)),
  and [FinePrint.ContractDefs](https://kspmoddinglibs.github.io/KSPDocsSite/class_fine_print_1_1_contract_defs.html),
  which holds the per-type generation settings.
- **Open-source mods that list the stock types.**
  - LRTR's "disable stock contracts" list:
    [pehvbot/LRTR Groups.cfg](https://github.com/pehvbot/LRTR/blob/HEAD/GameData/LRTR/contracts/Groups.cfg).
  - RP-AXE's list, which names the 27 types its install exposed. Those are the 22 stock types below plus 5 from
    Kerbalism:
    [AxeDisableStockContracts.cfg](https://github.com/miskoltrans/RP-AXE/blob/HEAD/GameData/RP-AXE/design/Contracts/AxeDisableStockContracts.cfg).
  - Contracts Window + / DMagic's [contractContainer.cs](https://github.com/DMagic1/KSP_Contract_Parser/blob/master/Source/contractContainer.cs),
    which handles each stock type in turn.
  - kRPC's [ContractParameter service](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractParameter.cs).
- **Real Contract Requirement trees from public save files.** Each `CONTRACT` node lists its `PARAM` children, so these
  are the observed shapes:
  - [krpctest_career.sfs](https://github.com/krpc/krpc/blob/main/tools/krpctools/krpctools/game/ksp/saves/krpctest_career.sfs)
  - [kk-ground-station.sfs](https://github.com/lhervier/KSP-Fix-TerrainPrecision/blob/HEAD/diag/statics/non-regression/kerbal-konstructs/a-ground-station-opened-in-flight/kk-ground-station.sfs)
    (a 1.11+ save with OrbitalConstruction and VesselRepair)
  - [anirul/KSP_saves](https://github.com/anirul/KSP_saves/blob/HEAD/Beta%20-%20Normal/persistent.sfs)
  - [skasti/KSP_Saves](https://github.com/skasti/KSP_Saves/blob/HEAD/saves/ThinkPad/quicksave.sfs)
  - [DanielVF ksp_1_0_2_save.sfs](https://github.com/DanielVF/kerbal_science_remaining/blob/HEAD/testing_data/ksp_1_0_2_save.sfs)
  - [CodeReptile STREAM 002.sfs](https://github.com/codereptile/CodeReptile_KSP/blob/HEAD/saves/epic_playthrough_series/STREAM%20002.sfs)
  - [CommNetConstellation persistent.sfs](https://github.com/KSP-TaxiService/CommNetConstellation/blob/HEAD/saves/KSP1.4.3%20Career/CNCCareer/persistent.sfs)

Some of those saves are pre-1.2. They use old names: `ProbeSystemsParameter` and `FacilitySystemsParameter`, which are
now `VesselSystemsParameter`, and `WorldFirstContract`, `ExploreBody` and `RescueKerbal`, which are now
`ExplorationContract` and `RecoverAsset`. The table uses the 1.12 names.

## The list

KSP 1.12 has **22 stock Contract types**:

- 16 base-game types, in `Contracts.Templates` and `FinePrint.Contracts`.
- 3 from the bundled SentinelMission assembly. It ships with the base game; the Sentinel types arrived in 1.3 and the
  comet types in 1.10.
- 3 from **Breaking Ground**. "Serenity" is that expansion's internal name.

**Making History adds no Career Contract types.** Its code is in `Expansions.Missions`: the Mission Builder, made of
nodes and actions. Nothing in that namespace subclasses `Contract`, and none of the 22 type names lists come from it.
The 1.11 types (OrbitalConstruction, RoverConstruction and VesselRepair) are base game. They need the EVA Construction
features that 1.11 brought to the base game, not an expansion.

`BaseContract` here means a surface *outpost* Contract. It is not a base class.

## Per-type table

Key: **2nd V** = needs a second vessel (pre-spawned or player-made) besides the one you launch.
**Crew** = needs specific crew beyond a pilot. **Build** = parts must be attached in flight (EVA Construction / docking).
**Air/WP/Surf** = involves aircraft, waypoints or surface travel. Every Contract can also fail when its deadline
passes (wiki); that is not repeated per row.

| # | Type (wiki name) | Contract Requirements carried (KSP `ContractParameter` names) | Settings and time-held conditions | Failure conditions beyond deadline | 2nd V | Crew | Build | Air/WP/Surf |
|---|---|---|---|---|---|---|---|---|
| 1 | `ExplorationContract` (Progression / world firsts) | One or more `ProgressTrackingParameter`. Each wraps a progress milestone (first launch, reach space, flyby / orbit / land / return from a body, records). | Milestone per body and type: `FIRSTLAUNCH`, `REACHSPACE`, `FLYBY`, `LANDING`, `LANDINGRETURN`... (observed). Records are split into `RecordSplit`=5 steps up to altitude, speed, distance and depth caps (`ContractDefs.Progression`). No hold time. No deadline once accepted (wiki). | None; it only expires while it is still an offer (wiki). | No | No (crewed variants need *a* kerbal) | No | Some are surface records (distance, depth); otherwise no |
| 2 | `PartTest` (Part testing / Part hauling) | `PartTest`, with children `ReachDestination`, `ReachSituation`, `ReachAltitudeEnvelope`, `ReachSpeedEnvelope` (and `ReachBiome` when a biome is set). These are needed together, at the same moment. | A body and situation (`PRELAUNCH`, `LANDED`, `SPLASHED`, `FLYING`, `SUB_ORBITAL`, `ORBITING`, ...), and optional altitude and speed windows (observed e.g. 6–10 km at 210–280 m/s; 49–61 km at 1880–2250 m/s). The test is a stage activation or a "Run test" action while every condition holds (wiki). `hauled`=true makes it a **haul**: be there with the part, no test (`TrivialHaulChance` 50, `SignificantHaulChance` 25). | None stated. Unlocked "experimental" part is withdrawn after the Contract ends (wiki). | No | No | No | **Often aircraft**: FLYING tests in Kerbin's atmosphere at set altitude and speed. Splashed / landed variants are surface. |
| 3 | `CollectScience` (Science) | `CollectScience` (body + `BodyLocation` Surface / Space). | Run *any* experiment at the location; transmit or recover. No science needs to be earned (wiki). No hold. | None stated. | No | No | No | Surface if `BodyLocation`=Surface |
| 4 | `SurveyContract` (Survey, incl. aerial) | 1–3+ `SurveyWaypointParameter` (experiment, body, waypoint, `FlightBand`). | Each waypoint needs a named experiment (`crewReport`, `evaReport`, `temperatureScan`, `seismicScan`, ... observed) taken inside the waypoint's trigger range and altitude band. Trivial / Significant / Exceptional give 1 / 2 / 3 waypoints, spread over 2 / 4 / 6 km. The trigger range is 500–15 000 m; the band thresholds are 1 400–30 000 m (`ContractDefs.Survey`). No hold. | None stated. | No | crewReport / evaReport need a kerbal | No | **Waypoints, always**. Bands are ground, low or high flight, so **aircraft** (aerial survey) or **surface travel** (rover / EVA). |
| 5 | `PlantFlag` (Flag planting) | `PlantFlag` (body). | Any flag, planted by EVA on the body. | None stated. | No | One kerbal on EVA | No | Landing on the body |
| 6 | `RecoverAsset` (Rescue / Part recovery) | Kerbal rescue: `AcquireCrew` + `RecoverKerbal` (older saves: `BoardAnyVessel`). Part recovery: `AcquirePart` + `RecoverPart`. | The game spawns the stranded kerbal or part, in orbit or landed (`AllowLandedVacuum` / `AllowLandedAtmosphere`, `AllowCompoundRecovery` = both). Board or grab it, then recover it on Kerbin. The stranded kerbal can't be controlled until you're within 2.25 km (wiki). | **Fails if the kerbal dies** (wiki). `RecoverPart` has a fail condition when the parts are destroyed (API enum). | **Yes**: the spawned derelict | A free seat for the rescued kerbal; Klaw / grapple for parts | Docking / grapple for part recovery | Surface when the target is landed |
| 7 | `SatelliteContract` (Satellite / network / repositioning) | `VesselSystemsParameter` (needs `Antenna` + `Generator`, unmanned, `requireNew`), `SpecificOrbitParameter`, optional `PartRequestParameter` (carry an experiment), optional `StationaryPointParameter` (stationary over a longitude), **`StabilityParameter` holdSeconds=10**. Repositioning adds `SpecificVesselParameter` and `requireNew`=false. | Orbit types include equatorial, polar, Molniya, Tundra, (Kerbin-)synchronous and stationary, and solar. The **deviation window** is 7 / 5 / 3 % (Trivial / Significant / Exceptional); the API shows these as `TrivialDeviation` / `SignificantDeviation` / `ExceptionalDeviation`. Networks need 3–4 satellites. **Must hold the orbit 10 s**, then it is not monitored again. A vessel must be rolled out after acceptance. Power must be *generated*; batteries don't count (wiki). | None stated beyond deadline. | Repositioning: yes, an existing named satellite. Network: several new ones. | Unmanned required | No | No |
| 8 | `StationContract` (Station construction) | `LocationAndSituationParameter` (body, ORBITING), `VesselSystemsParameter` (`Antenna`/`Power`/`Dock`, crewed-capable, new), `CrewCapacityParameter`, optional `ResourcePossessionParameter` (e.g. 1000 MonoPropellant), optional `PartRequestParameter` (lab etc.), optional `AsteroidParameter` (forStation), **`StabilityParameter` holdSeconds=10**. | Capacity observed 8–9. Asteroid variant chance 0 / 10 / 20 % by prestige. Can be solar orbit. **Hold 10 s.** | None stated. | Asteroid variant: yes (capture an asteroid into the station) | Capacity only, not crew aboard | Often assembled by docking; asteroid variant needs a Klaw | No |
| 9 | `BaseContract` (Outpost construction) | `LocationAndSituationParameter` (body, LANDED), `VesselSystemsParameter` (`Antenna`/`Power`/`Dock`, new) and optional mobile `MobileBaseParameter`, `CrewCapacityParameter`, optional `CrewTraitParameter` (e.g. 2 Pilots), optional `PartRequestParameter`, **`StabilityParameter` holdSeconds=10**. | Capacity observed 5–7. The mobile-base chance is 0 / 10 / 30 % by prestige; mobile means wheels. **Hold 10 s.** | None stated. | No | Sometimes (CrewTrait) | Usually a heavy lander, built or docked | **Landed**; mobile variant = surface rover |
| 10 | `ISRUContract` (Resource extraction) | `ResourceExtractionParameter` (resource, goal amount, body, harvester modules); often `ResourcePossessionParameter` to haul it somewhere (wiki). Exact tree **not observed** in a save. | Ore only in stock (wiki). The amount must be *harvested* on the body. | None stated. | Sometimes a tanker to move it | No | No | Landed on the body |
| 11 | `ARMContract` (Asteroid recovery) | Asteroid capture plus destination. The API shows `AsteroidParameter` (size) and `LocationAndSituationParameter`. Exact tree **not observed**. | Destination: an orbit, a body, a **landing on Kerbin** (`HomeLandingChance` 20 %) or a **solar ejection** (10 / 20 %). | None stated. | **Yes**: an untracked asteroid | No | Klaw (grapple) | Kerbin landing variant |
| 12 | `GrandTour` (Grand tour) | One per body: flyby / orbit / land on each listed body, **with one vessel** (wiki). Probably `VesselDestinationParameter` (body, `FlightLog.EntryType`), which is the only 1.12 Contract Requirement of that shape. **Not observed.** | `Rarity` 5 (rare). | None stated. | No | No (a crewed tour is likely; unconfirmed) | No | Landing variants |
| 13 | `TourismContract` (Tourism / High-gee adventure) | One `KerbalTourParameter` per tourist, each with `KerbalDestinationParameter` children (body + `Suborbit` / `Orbit` / `Flyby` / `Land`...). The gee variant is `KerbalGeeAdventureParameter` (pass out from g-force, survive). | Up to 6 tourists observed; each has own destinations. Must **return and recover** them (wiki). `GeeAdventureChance` 0.15. | **Fails if a tourist dies or is knocked unconscious by g** (`FailOnInactive`=true; wiki). The gee variant inverts this: they *must* pass out. | No | Tourists can't pilot: need a pilot or probe core (wiki) | No | Mostly no; landing destinations are surface |
| 14 | `OrbitalConstructionContract` (Satellite upgrade, 1.11) | `CrewTraitParameter` (Engineer ×1), `ConstructionParameter` (part, vessel, body). | A pre-built vessel is spawned in orbit (altitude and inclination factors 0.5, e = 0). Rendezvous, then **attach the named part by EVA Construction** (observed part: large battery pack). Needs tech `advConstruction`. Max 1 offered, 1 active. | None stated. | **Yes**: the spawned vessel | **Engineer** on EVA | **Yes** | No |
| 15 | `RoverConstructionContract` (Rover repair / construction, 1.11) | Expected from the API: `CrewTraitParameter` (Engineer), `ConstructionParameter`, `RoverWayPointParameter` (start and end lat/lon). **Not observed.** | A broken rover is spawned on a body with surface gravity 0.02–0.4 g. Attach the missing command part by EVA, then **drive to a waypoint 900–1100 m away (trigger 50 m)**. Needs tech `advExploration`. Max 1 offered, 1 active. | None stated. | **Yes**: the spawned rover | **Engineer** | **Yes** | **Surface travel + waypoint** |
| 16 | `VesselRepairContract` (Vessel repair, 1.11) | `CrewTraitParameter` (Engineer ×1), `RepairPartParameter` (part, vessel, body). | A pre-built vessel is spawned in orbit or on the ground (`spawnInOrbit` / `spawnOnGround`). An engineer **repairs the named part with a repair kit** (observed part: `longAntenna`). Max 1 offered, 1 active. | None stated. | **Yes** | **Engineer** | Repair kit (EVA) | Ground variant: landing next to it |
| 17 | `SentinelContract` (Sentinel) | `SentinelParameter` (focus body, scan type, target size class, min eccentricity, min inclination, discovery count). Sometimes a `SpecificOrbitParameter` too (wiki). | A SENTINEL telescope in orbit of a non-Sun body must discover N asteroids that match. Its orbit deviation is 7 / 5 / 3. Max 2 offered, 3 active. | None stated. | Sometimes two Sentinels (wiki) | Unmanned fine | No | No |
| 18 | `CometDetectionContract` (comet scan, 1.10) | `CometDetectionParameter` (count, Sentinel part). | Discover N comets with the Sentinel. Max 1 offered, 1 active. | None stated. | No | No | No | No |
| 19 | `CometSampleContract` (Comet sample, 1.10) | `CometScienceParameter` (comet id, science subject). | Get a **scientist** close to a named comet, take the sample, then transmit or recover it (wiki). `SciencePercentage` 50. Max 1 offered, 1 active. | None stated. | **Yes**: the comet (a target, not a craft) | **Scientist** | No | No |
| 20 | `DeployedScienceContract` (Breaking Ground) | `CollectDeployedScience` (body, subject, % of the subject's science). | Set up a Deployed Science station on the body and let it collect `SciencePercentage` 50 % of the subject's science. That takes time, because the station transmits over days. Max 5. | None stated. | The deployed station becomes a separate vessel | A kerbal deploys it on EVA (Scientist boosts it) | Deploy by EVA | Landed |
| 21 | `ROCScienceArmContract` (Breaking Ground) | `CollectROCScienceArm` (body, subject, surface-feature type, %). | Scan a named **surface feature** (ROC) in given biomes with a robotic scanner arm part. The share required is 40 % for a simple arm and 70 % for a complex one. Max 3. | None stated. | No | No (arm scan) | No | **Surface travel** to find the feature |
| 22 | `ROCScienceRetrievalContract` (Breaking Ground) | `CollectROCScienceRetrieval` (body, subject, ROC type). | A kerbal picks up a sample of a named surface feature, then it is returned or transmitted (exact rule unconfirmed). Max 2. | None stated. | No | A kerbal on EVA | No | **Surface travel** to find the feature |

Some Contract Requirements are generic: `KerbalDeaths`, `EnterOrbit`, `EnterSOI`, `LandOnBody`, `ReachBiome`, `OR`
and `XOR` exist in `Contracts.Parameters`. None of them appeared in an observed 1.12-era tree. Old `ExploreBody` used
`EnterOrbit`, `LandOnBody` and `CollectScience`.

## Gist for classification

- **One new craft, no second vessel and no special crew:** Exploration (most), PartTest (non-aircraft), CollectScience,
  PlantFlag, Satellite (new), CometDetection, Sentinel (one telescope).
- **Time-held:** Satellite, Station and Base each need `StabilityParameter` **10 s** held. PartTest needs every
  condition true at the moment of the test. DeployedScience accrues over time. No other type has a hold.
- **Aircraft:** PartTest FLYING tests in Kerbin's atmosphere, and aerial Survey waypoints.
- **Waypoints:** Survey (always), RoverConstruction.
- **Surface travel:** Survey ground bands, RoverConstruction, mobile Base, both ROC types.
- **Second vessel or target:** RecoverAsset, ARM, Station (asteroid), OrbitalConstruction, RoverConstruction,
  VesselRepair, CometSample, Satellite repositioning and networks.
- **Specific crew:** Engineer (OrbitalConstruction, RoverConstruction, VesselRepair), Scientist (CometSample), tourists
  plus a pilot (Tourism), some Base Contracts (CrewTrait), and a free seat for Rescue.
- **Building in flight:** OrbitalConstruction, RoverConstruction, VesselRepair (repair kit), Station and Base by
  docking, and DeployedScience (EVA deploy).
- **Failure beyond the deadline:** only Tourism (a tourist dies or passes out) and Rescue (the kerbal dies) are
  documented.

## Uncertain without decompiling (flagged, not resolved)

1. **Exact Contract Requirement trees not seen in any public save:** GrandTour, ISRU, ARM, RoverConstruction, the
   Sentinel and comet types, and the three Breaking Ground types. Those rows are inferred from the API class list,
   constructor signatures and wiki text. A live 1.12 save with these Contracts on offer would settle them by reading
   its `CONTRACT` nodes; that is not decompiling.
2. **Live tuning values.** The numbers above are API field initialisers. `Contracts.cfg` can override them, so a quick
   look at the installed `GameData/Squad/Contracts/Contracts.cfg` (data, not code) would confirm them.
3. **Failure conditions for most types.** Public sources only document deadline expiry plus the Tourism and Rescue
   deaths. Whether the others fail when the target vessel or part is destroyed (VesselRepair, OrbitalConstruction,
   Satellite repositioning) is unconfirmed.
4. **Which `ExplorationContract` milestones exist in 1.12.** The full list is the wiki's
   [List of story line contracts](https://wiki.kerbalspaceprogram.com/wiki/List_of_story_line_contracts), which could
   not be fetched (bot wall, no readable archive).
5. **ROCScienceRetrieval completion rule.** It is unclear whether recovery is required or transmission is enough.
