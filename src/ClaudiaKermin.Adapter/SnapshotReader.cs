// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using KSP.UI.Screens;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// Reads a Game Snapshot: plain facts for the core, from a vessel in Flight or the ship in the editor.
    /// It only reads; the Craft Check's facts (the stock simulator's per-stage figures, the part inventory,
    /// crew, experiments) are computed here and Claude judges them. In particular it never sets the delta-v
    /// app's body or any other delta-v setting. Main thread only.
    /// </summary>
    public static class SnapshotReader
    {
        /// <summary>kOS's processor part module, found by name so the adapter needn't reference kOS.</summary>
        private const string KosProcessorModule = "kOSProcessor";

        public static GameSnapshot Read(Vessel vessel)
        {
            var snapshot = new GameSnapshot
            {
                Ut = Planetarium.GetUniversalTime(),
                Situation = vessel.situation.ToString(),
                Body = vessel.mainBody != null ? vessel.mainBody.bodyName : "",
                Altitude = vessel.altitude,
                Latitude = vessel.latitude,
                CelestialLongitude = vessel.mainBody != null ? ((vessel.longitude + vessel.mainBody.rotationAngle) % 360 + 360) % 360 : 0,
                VerticalSpeed = vessel.verticalSpeed,
                SurfaceSpeed = vessel.srfSpeed,
            };
            snapshot.Craft.Name = vessel.vesselName ?? "";
            snapshot.Craft.VesselId = vessel.id.ToString("N");
            snapshot.Craft.Eva = vessel.isEVA;
            snapshot.Craft.MassTonnes = vessel.GetTotalMass();
            snapshot.Craft.Stage = vessel.currentStage;
            snapshot.RcsOn = vessel.ActionGroups[KSPActionGroup.RCS];
            snapshot.Throttle = vessel.ctrlState.mainThrottle;
            snapshot.Nodes = ManeuverPlan.ForSnapshot(vessel);
            ReadParts(snapshot, vessel.parts, vessel.VesselDeltaV);
            ReadChutes(snapshot, vessel);
            MarkTrickleStages(snapshot, vessel.parts);
            // plan_ascent's drag, for a launch: only a craft on the ground of a body with air needs it.
            if (vessel.mainBody != null && vessel.mainBody.atmosphere && OnTheGround(vessel.situation))
                snapshot.Craft.NoseDrag = NoseDrag.Read(vessel.parts, vessel.ReferenceTransform.up, false);
            snapshot.Crew.AddRange(vessel.GetVesselCrew().Select(Crew));
            ReadAboard(snapshot);
            // A save with CommNet off counts as linked, whatever the connection says.
            snapshot.HasLink = LinkedHome(vessel);

            if (vessel.orbit != null) snapshot.Orbit = OrbitOf(vessel.orbit);
            ReadTargets(snapshot, vessel);
            return snapshot;
        }

        private static SnapshotOrbit OrbitOf(Orbit orbit)
        {
            return new SnapshotOrbit
            {
                Apoapsis = orbit.ApA,
                Periapsis = orbit.PeA,
                Inclination = orbit.inclination,
                Eccentricity = orbit.eccentricity,
                Period = orbit.period,
                SemiMajorAxis = orbit.semiMajorAxis,
                Lan = orbit.LAN,
                ArgumentOfPeriapsis = orbit.argumentOfPeriapsis,
                MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
                Epoch = orbit.epoch,
                Body = orbit.referenceBody != null ? orbit.referenceBody.bodyName : "",
            };
        }

        // ---- The Target Vessel (ADR 0013) ----

        /// <summary>
        /// KSP's target, the Mission's Target Vessel live by its persistentId (<see cref="MissionDriver.TargetVesselId"/>, so the
        /// block calls' own snapshots read it too), and the other vessels a Mission Goal may name. The core keeps the entry only
        /// for the Mission in play's target. A vessel is never its own target.
        /// </summary>
        private static void ReadTargets(GameSnapshot snapshot, Vessel vessel)
        {
            // Debris, a flag, an asteroid or a Kerbal on EVA set as KSP's target is never a Target Vessel (#643).
            var ksp = vessel.targetObject != null ? vessel.targetObject.GetVessel() : null;
            if (ksp != null && ksp != vessel && Nameable(ksp.vesselType)) snapshot.KspTarget = TargetOf(ksp);
            var id = MissionDriver.TargetVesselId ?? MissionDriver.BlockTestTargetId;
            if (id.HasValue && id.Value != vessel.persistentId) snapshot.Target = LiveTarget(snapshot, id.Value);
            // The start form's target, before Start: the Craft Check judges the craft against it.
            var form = id.HasValue ? null : MissionDriver.FormTargetId;
            if (form.HasValue && form.Value != vessel.persistentId) snapshot.FormTarget = LiveTarget(snapshot, form.Value);
            snapshot.CraftPersistentId = vessel.persistentId;
            var here = vessel.GetWorldPos3D();
            foreach (var other in FlightGlobals.Vessels)
                if (other != null && other != vessel && Nameable(other.vesselType))
                    snapshot.Vessels.Add(new SnapshotVessel
                    {
                        Id = other.persistentId,
                        Name = other.vesselName ?? "",
                        Body = other.mainBody != null ? other.mainBody.bodyName : "",
                        Situation = other.situation.ToString(),
                        // From the craft, so same-named vessels can be told apart and the list puts the nearest first (#709).
                        Distance = Vector3d.Distance(other.GetWorldPos3D(), here),
                    });
        }

        private static SnapshotTarget LiveTarget(GameSnapshot snapshot, uint id)
        {
            Vessel target;
            if (snapshot.KspTarget != null && snapshot.KspTarget.Id == id) return snapshot.KspTarget;
            return FlightGlobals.FindVessel(id, out target) && target != null ? TargetOf(target) : null;
        }

        /// <summary>
        /// The vessels in the save a Mission Goal may name or the player pick before launch (the editor has no FlightGlobals
        /// vessels), and the start form's target among them.
        /// </summary>
        private static void ReadSavedVessels(GameSnapshot snapshot)
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.flightState == null) return;
            var form = MissionDriver.FormTargetId;
            foreach (var saved in game.flightState.protoVessels)
            {
                if (saved == null || !Nameable(saved.vesselType)) continue;
                var body = SavedBody(saved);
                var entry = new SnapshotVessel { Id = saved.persistentId, Name = saved.vesselName ?? "", Body = body, Situation = saved.situation.ToString() };
                if (entry.IsDown) entry.Position = new SnapshotPosition { Latitude = saved.latitude, Longitude = saved.longitude, Altitude = saved.altitude };
                else ReadSavedOrbit(entry, saved);
                snapshot.Vessels.Add(entry);
                if (form.HasValue && form.Value == saved.persistentId)
                {
                    snapshot.FormTarget = new SnapshotTarget { Id = saved.persistentId, Name = saved.vesselName ?? "", Body = body, Situation = saved.situation.ToString() };
                    ReadSavedPorts(snapshot.FormTarget, saved);
                }
            }
        }

        /// <summary>Periapsis and apoapsis altitudes from the saved orbit, to tell same-named vessels apart in the editor (#710).</summary>
        private static void ReadSavedOrbit(SnapshotVessel entry, ProtoVessel saved)
        {
            var orbit = saved.orbitSnapShot;
            var bodies = FlightGlobals.Bodies;
            if (orbit == null || bodies == null || orbit.ReferenceBodyIndex < 0 || orbit.ReferenceBodyIndex >= bodies.Count || bodies[orbit.ReferenceBodyIndex] == null) return;
            double pe;
            double? ap;
            TargetVessel.OrbitAltitudes(orbit.semiMajorAxis, orbit.eccentricity, bodies[orbit.ReferenceBodyIndex].Radius, out pe, out ap);
            entry.PeriapsisM = pe;
            entry.ApoapsisM = ap;
        }

        private static string SavedBody(ProtoVessel saved)
        {
            var bodies = FlightGlobals.Bodies;
            var index = saved.orbitSnapShot != null ? saved.orbitSnapShot.ReferenceBodyIndex : -1;
            return bodies != null && index >= 0 && index < bodies.Count && bodies[index] != null ? bodies[index].bodyName : "";
        }

        /// <summary>Vessels a Mission could rendezvous or dock with: not debris, flags, asteroids or Kerbals on EVA.</summary>
        private static bool Nameable(VesselType type)
        {
            return type != VesselType.Debris && type != VesselType.Flag && type != VesselType.SpaceObject && type != VesselType.EVA
                && type != VesselType.Unknown && type != VesselType.DroppedPart && type != VesselType.DeployedScienceController
                && type != VesselType.DeployedSciencePart;
        }

        private static SnapshotTarget TargetOf(Vessel vessel)
        {
            var target = new SnapshotTarget
            {
                Id = vessel.persistentId,
                Name = vessel.vesselName ?? "",
                Body = vessel.mainBody != null ? vessel.mainBody.bodyName : "",
                Situation = vessel.situation.ToString(),
            };
            if (vessel.orbit != null) target.Orbit = OrbitOf(vessel.orbit);
            // Where a landed or splashed target is, for land's "vessel" target (#662).
            if (target.IsDown) target.Position = new SnapshotPosition { Latitude = vessel.latitude, Longitude = vessel.longitude, Altitude = vessel.altitude };
            if (vessel.loaded) ReadLoadedPorts(target, vessel);
            else if (vessel.protoVessel != null) ReadSavedPorts(target, vessel.protoVessel);
            return target;
        }

        /// <summary>kOS's name tag module and field, by name so the adapter needn't reference kOS.</summary>
        private const string NameTagModule = "KOSNameTag";
        private const string NameTagField = "nameTag";

        private static void ReadLoadedPorts(SnapshotTarget target, Vessel vessel)
        {
            foreach (var part in vessel.parts)
            {
                string tag = null;
                foreach (var module in part.Modules)
                    if (module.moduleName == NameTagModule && module.Fields[NameTagField] != null) tag = module.Fields[NameTagField].GetValue(module) as string;
                foreach (var port in part.FindModulesImplementing<ModuleDockingNode>())
                    if (Free(port.state)) AddPort(target, port.nodeType, tag);
            }
        }

        /// <summary>An unloaded vessel's ports: the size from the part's prefab, whether it's free from its saved state.</summary>
        private static void ReadSavedPorts(SnapshotTarget target, ProtoVessel vessel)
        {
            foreach (var part in vessel.protoPartSnapshots)
            {
                var prefab = part.partInfo != null ? part.partInfo.partPrefab : null;
                if (prefab == null) continue;
                var ports = prefab.FindModulesImplementing<ModuleDockingNode>();
                string tag = null;
                foreach (var module in part.modules)
                    if (module.moduleName == NameTagModule && module.moduleValues != null) tag = module.moduleValues.GetValue(NameTagField);
                // Saved modules go by class name, a docking port's subclass included: pair them in order with the prefab's ports.
                var index = 0;
                foreach (var module in part.modules)
                {
                    if (index >= ports.Count || module.moduleName != ports[index].moduleName) continue;
                    var state = module.moduleValues != null ? module.moduleValues.GetValue("state") : null;
                    if (Free(state)) AddPort(target, ports[index].nodeType, tag);
                    index++;
                }
            }
        }

        /// <summary>Docked, acquiring, disengaging or shut: only "Ready" is free.</summary>
        private static bool Free(string state)
        {
            return state != null && state.StartsWith("Ready", StringComparison.Ordinal);
        }

        private static void AddPort(SnapshotTarget target, string size, string tag)
        {
            size = string.IsNullOrEmpty(size) ? "unknown" : size.Trim();
            int count;
            target.FreePorts[size] = target.FreePorts.TryGetValue(size, out count) ? count + 1 : 1;
            if (!string.IsNullOrEmpty(tag)) target.PortTags[tag.Trim()] = size;
        }

        /// <summary>
        /// A link to Kerbin; CommNet off in the save counts as linked (spec: Manual Control and Blackout). Aboard, this is the
        /// link of the vessel Claudia is in; remote, the probe's control path home to KSC.
        /// </summary>
        public static bool LinkedHome(Vessel vessel)
        {
            if (!CommNet.CommNetScenario.CommNetEnabled) return true;
            return vessel.connection != null && vessel.connection.IsConnectedHome;
        }

        /// <summary>
        /// Whether Claudia can reach <paramref name="target"/> by the Child Vessel link test (<see cref="ChildVessels.LinkedToClaudia"/>):
        /// remote, its link home; aboard, a CommNet path to <paramref name="craft"/> (both loaded), as VesselWatch reads a Child
        /// Vessel's. CommNet off links every vessel. For dock's Target Vessel (#456).
        /// </summary>
        public static bool LinkedToClaudia(Vessel target, Vessel craft, bool remote)
        {
            var craftId = craft.id.ToString("N");
            var seen = new SeenVessel { Id = target.id.ToString("N"), LinkedHome = LinkedHome(target) };
            var commNet = CommNet.CommNetScenario.CommNetEnabled && CommNet.CommNetNetwork.Instance != null ? CommNet.CommNetNetwork.Instance.CommNet : null;
            var from = target.connection != null ? target.connection.Comm : null;
            var to = craft.connection != null ? craft.connection.Comm : null;
            if (!CommNet.CommNetScenario.CommNetEnabled
                || (commNet != null && target.loaded && craft.loaded && from != null && to != null && commNet.FindPath(from, new CommNet.CommPath(), to)))
                seen.LinkedTo.Add(craftId);
            return ChildVessels.LinkedToClaudia(remote, craftId, seen);
        }

        /// <summary>The ship being built in the VAB/SPH; null when there is none or it has no parts yet.</summary>
        public static GameSnapshot ReadEditor(ShipConstruct ship)
        {
            if (ship == null || ship.parts.Count == 0) return null;
            var snapshot = new GameSnapshot
            {
                Ut = Planetarium.GetUniversalTime(),
                Situation = "PRELAUNCH",
                Body = DeltaVMap.DefaultHome,
            };
            snapshot.Craft.Name = ship.shipName ?? "";
            snapshot.Craft.MassTonnes = ship.parts.Sum(p => (double)(p.mass + p.GetResourceMass()));
            snapshot.Craft.Stage = StageManager.LastStage;
            ReadParts(snapshot, ship.parts, ship.vesselDeltaV);
            // The root part's up is the craft's nose, as the control point's is in Flight.
            var nose = ship.parts[0].transform.up;
            snapshot.Craft.NoseDrag = NoseDrag.Read(ship.parts, nose, true);
            // The chutes too, for the Craft Check's Chute Allocation (#641), held the same way.
            for (var i = 0; i < ship.parts.Count && i < snapshot.Craft.Parts.Count; i++)
                if (snapshot.Craft.Parts[i].Tags.Contains("parachute")) snapshot.Craft.Parts[i].Chute = ReentryCraft.ChuteOf(ship.parts[i], nose);
            var manifest = ShipConstruction.ShipManifest;
            if (manifest != null) snapshot.Crew.AddRange(manifest.GetAllCrew(false).Where(c => c != null).Select(Crew));
            ReadAboard(snapshot);
            ReadSavedVessels(snapshot);
            return snapshot;
        }

        private static bool OnTheGround(Vessel.Situations situation)
        {
            return situation == Vessel.Situations.PRELAUNCH || situation == Vessel.Situations.LANDED || situation == Vessel.Situations.SPLASHED;
        }

        /// <summary>Claudia's roster standing and whether the save has CommNet: the facts the aboard and remote rules judge.</summary>
        private static void ReadAboard(GameSnapshot snapshot)
        {
            var game = HighLogic.CurrentGame;
            if (game != null && game.CrewRoster != null) snapshot.ClaudiaStanding = Roster.RosterGateway.ClaudiaStanding(game.CrewRoster);
            snapshot.CommNetEnabled = CommNet.CommNetScenario.CommNetEnabled;
        }

        private static SnapshotCrewMember Crew(ProtoCrewMember crew)
        {
            return new SnapshotCrewMember
            {
                Name = crew.name ?? "",
                // The config name, as RosterGateway.IsClaudia compares: experienceTrait.TypeName gives the title in game ("Super Intelligence").
                Trait = crew.trait ?? "",
            };
        }

        /// <summary>Each parachute's own numbers, held retrograde, for the Chute Allocation (#626); the editor's are read in <see cref="ReadEditor"/>.</summary>
        private static void ReadChutes(GameSnapshot snapshot, Vessel vessel)
        {
            var parts = vessel.parts;
            for (var i = 0; i < parts.Count && i < snapshot.Craft.Parts.Count; i++)
                if (snapshot.Craft.Parts[i].Tags.Contains("parachute")) snapshot.Craft.Parts[i].Chute = ReentryCraft.ChuteOf(vessel, parts[i]);
        }

        private static void ReadParts(GameSnapshot snapshot, IList<Part> parts, VesselDeltaV deltaV)
        {
            var mode = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode : Game.Modes.SANDBOX;
            snapshot.GameMode = mode.ToString();
            snapshot.DeltaVSimulator = GameSettings.DELTAV_CALCULATIONS_ENABLED;

            var resources = new Dictionary<string, SnapshotResource>();
            var order = new List<string>();
            foreach (var part in parts)
            {
                var info = new SnapshotPart
                {
                    Id = part.persistentId,
                    CraftId = part.craftID,
                    ParentId = part.parent != null ? (uint?)part.parent.persistentId : null,
                    Name = part.partInfo != null ? part.partInfo.name : part.name,
                    Title = part.partInfo != null ? part.partInfo.title : "",
                    Stage = part.inverseStage,
                    CrewCapacity = part.CrewCapacity,
                    // Part.mass is the dry mass; its resources' mass comes on top.
                    DryMassT = part.mass,
                    MassT = part.mass + part.GetResourceMass(),
                };
                if (part.Resources.Contains("ElectricCharge")) info.ChargeMax = part.Resources["ElectricCharge"].maxAmount;
                if (part.CrewCapacity > 0) info.Tags.Add("crew_seat");
                snapshot.Craft.Seats += part.CrewCapacity;
                foreach (var module in part.Modules) Classify(snapshot, part, info, module);
                snapshot.Craft.Parts.Add(info);

                foreach (var resource in part.Resources)
                {
                    SnapshotResource total;
                    if (!resources.TryGetValue(resource.resourceName, out total))
                    {
                        total = new SnapshotResource { Name = resource.resourceName };
                        resources.Add(resource.resourceName, total);
                        order.Add(resource.resourceName);
                    }
                    total.Amount += resource.amount;
                    total.Max += resource.maxAmount;
                }
            }
            foreach (var name in order) snapshot.Resources.Add(resources[name]);

            if (snapshot.DeltaVSimulator && deltaV != null) ReadStages(snapshot, deltaV);
            ReadBodies(snapshot);
        }

        /// <summary>The stock simulator's figures per stage. Vacuum delta-v does not depend on the delta-v app's chosen body; nothing is written back.</summary>
        private static void ReadStages(GameSnapshot snapshot, VesselDeltaV deltaV)
        {
            if (deltaV.OperatingStageInfo == null) return;
            foreach (var stage in deltaV.OperatingStageInfo)
            {
                snapshot.Stages.Add(new SnapshotStage
                {
                    Number = stage.stage,
                    DeltaVVac = stage.deltaVinVac,
                    DeltaVAsl = stage.deltaVatASL,
                    ThrustVacKn = stage.thrustVac,
                    StartMassT = stage.startMass,
                    EndMassT = stage.endMass,
                });
            }
        }

        /// <summary>The throttle under which stock's delta-v reads 0 with an engine lit (#234).</summary>
        private const double TrickleThrottle = 0.05;

        /// <summary>
        /// Stock's delta-v calculation burns at the current fuel flow while an engine is lit and the throttle is above 0, so a trickle
        /// throttle reads 0 m/s. A stage reading 0 whose lit engine still has propellant is marked unknown, not 0.
        /// </summary>
        private static void MarkTrickleStages(GameSnapshot snapshot, IList<Part> parts)
        {
            if (snapshot.Throttle <= 0 || snapshot.Throttle >= TrickleThrottle) return;
            foreach (var stage in snapshot.Stages)
            {
                if (stage.DeltaVVac > 0) continue;
                stage.DeltaVUnreliable = parts.Any(part => part.inverseStage == stage.Number && HasLitEngineWithPropellant(part));
            }
        }

        private static bool HasLitEngineWithPropellant(Part part)
        {
            foreach (var module in part.Modules)
            {
                var engine = module as ModuleEngines;
                if (engine == null || !engine.EngineIgnited || engine.flameout) continue;
                if (engine.propellants.All(p => p.totalResourceAvailable > 0)) return true;
            }
            return false;
        }

        private static void ReadBodies(GameSnapshot snapshot)
        {
            foreach (var body in FlightGlobals.Bodies)
            {
                var orbit = body.orbit;
                snapshot.Bodies.Add(new SnapshotBody
                {
                    Name = body.bodyName,
                    Parent = body.referenceBody != null && body.referenceBody != body ? body.referenceBody.bodyName : "",
                    Gm = body.gravParameter,
                    Radius = body.Radius,
                    AtmosphereHeight = body.atmosphere ? body.atmosphereDepth : 0,
                    // Sea-level pressure in atmospheres, so thrust never depends on the body the delta-v app has selected.
                    SeaLevelAtm = body.atmosphere ? body.GetPressure(0) / 101.325 : 0,
                    Sma = orbit != null && body.referenceBody != body ? orbit.semiMajorAxis : 0,
                    Inclination = orbit != null ? orbit.inclination : 0,
                    SoiRadius = body.sphereOfInfluence,
                    RotationPeriod = body.rotationPeriod,
                    Eccentricity = orbit != null && body.referenceBody != body ? orbit.eccentricity : 0,
                    Lan = orbit != null && body.referenceBody != body ? orbit.LAN : 0,
                    ArgumentOfPeriapsis = orbit != null && body.referenceBody != body ? orbit.argumentOfPeriapsis : 0,
                    MeanAnomalyAtEpoch = orbit != null && body.referenceBody != body ? orbit.meanAnomalyAtEpoch : 0,
                    Epoch = orbit != null && body.referenceBody != body ? orbit.epoch : 0,
                    Air = ReadAir(body),
                    BiomeNames = BiomeNamesOf(body),
                });
            }
        }

        /// <summary>Each body's biome names, read once: they don't change in a game (#699).</summary>
        private static readonly Dictionary<string, List<string>> BiomeNamesRead = new Dictionary<string, List<string>>();

        /// <summary>The body's biome names (a copy of those read once), as <see cref="PlanningBody.BiomeNames"/> gives them; empty with no biome map.</summary>
        private static List<string> BiomeNamesOf(CelestialBody body)
        {
            List<string> names;
            if (BiomeNamesRead.TryGetValue(body.bodyName, out names)) return new List<string>(names);
            // A map not loaded yet is read again next time, never kept as no biomes.
            if (body.BiomeMap == null || body.BiomeMap.Attributes == null) return new List<string>();
            names = body.BiomeMap.Attributes.Select(a => a.name).ToList();
            BiomeNamesRead[body.bodyName] = names;
            return new List<string>(names);
        }

        /// <summary>Rows of the body's air for plan_ascent: every fiftieth of its depth, as <see cref="ReentryAir"/> reads it at the equator.</summary>
        private static List<double[]> ReadAir(CelestialBody body)
        {
            var rows = new List<double[]>();
            if (!body.atmosphere || body.atmosphereDepth <= 0) return rows;
            var air = new ReentryAir(body);
            const int Steps = 50;
            for (var i = 0; i <= Steps; i++)
            {
                var altitude = body.atmosphereDepth * i / Steps;
                double density, sound;
                air.Air(altitude, 0, out density, out sound);
                var atm = altitude < body.atmosphereDepth ? body.GetPressure(altitude) / 101.325 : 0;
                rows.Add(new[] { altitude, atm, density, sound });
            }
            return rows;
        }

        /// <summary>Tags what a part module makes the part able to do; the names are the stock (and kOS) module classes.</summary>
        private static void Classify(GameSnapshot snapshot, Part part, SnapshotPart info, PartModule module)
        {
            if (Is(module, KosProcessorModule))
            {
                info.HasKosProcessor = true;
                info.Tags.Add("kos_processor");
                info.BootFile = BootFile(module);
            }
            else if (Is(module, "ModuleParachute")) Tag(info, "parachute");
            else if (Is(module, "ModuleWheelBase"))
            {
                var wheel = module as ModuleWheelBase;
                if (wheel != null && wheel.wheelType == WheelType.LEG) Tag(info, "landing_leg");
            }
            else if (Is(module, "ModuleAblator")) Tag(info, "heat_shield");
            else if (Is(module, "ModuleDataTransmitter"))
            {
                Tag(info, "antenna");
                var transmitter = module as ModuleDataTransmitter;
                if (transmitter != null) info.AntennaPower = transmitter.antennaPower;
            }
            else if (Is(module, "ModuleCommand"))
            {
                Tag(info, "command");
                if (part.CrewCapacity == 0) Tag(info, "probe_core");
            }
            else if (Is(module, "ModuleDeployableSolarPanel") || Is(module, "ModuleGenerator") || Is(module, "ModuleResourceConverter"))
                Tag(info, "electric_generator");
            else if (Is(module, "ModuleReactionWheel")) Tag(info, "reaction_wheel");
            else if (Is(module, "ModuleRCS")) Tag(info, "rcs");
            else if (Is(module, "ModuleDockingNode"))
            {
                // Its size as the Target Vessel's free_ports count them (#456): dock and the Craft Check match the two.
                Tag(info, "docking_port");
                var port = module as ModuleDockingNode;
                if (port != null && info.PortSize.Length == 0) info.PortSize = string.IsNullOrEmpty(port.nodeType) ? "unknown" : port.nodeType.Trim();
            }
            else if (Is(module, "ModuleLiftingSurface")) Tag(info, "aero_stabiliser");
            else if (Is(module, "ModuleGimbal")) Tag(info, "gimbal");
            else if (Is(module, "ModuleEngines"))
            {
                Tag(info, "engine");
                var engine = module as ModuleEngines;
                // A multi-mode engine has a module per mode: its strongest stands for the part.
                if (engine != null && engine.maxThrust > info.ThrustVacKn)
                {
                    var ispVac = engine.atmosphereCurve.Evaluate(0f);
                    var ispAsl = engine.atmosphereCurve.Evaluate(1f);
                    info.ThrustVacKn = engine.maxThrust;
                    info.IspVac = ispVac;
                    info.ThrustAslKn = ispVac > 0 ? engine.maxThrust * ispAsl / ispVac : 0;
                    info.IspCurve.Clear();
                    // The curve's keys are (atmospheres, seconds); the mod does the interpolating.
                    foreach (var key in engine.atmosphereCurve.Curve.keys) info.IspCurve.Add(new double[] { key.time, key.value });
                }
            }
            else if (Is(module, "ModuleDecouplerBase") || Is(module, "ModuleDecouple") || Is(module, "ModuleAnchoredDecoupler")) Tag(info, "decoupler");
            else if (Is(module, "ModuleScienceExperiment"))
            {
                Tag(info, "experiment");
                var experiment = module as ModuleScienceExperiment;
                if (experiment != null) snapshot.Experiments.Add(ReadExperiment(part, experiment));
            }
        }

        private static void Tag(SnapshotPart info, string tag)
        {
            if (!info.Tags.Contains(tag)) info.Tags.Add(tag);
        }

        /// <summary>The module is, or derives from, the named class.</summary>
        private static bool Is(PartModule module, string className)
        {
            for (var type = module.GetType(); type != null; type = type.BaseType)
                if (type.Name == className) return true;
            return false;
        }

        /// <summary>kOS keeps a processor's boot file in its public <c>bootFile</c> field; "None" means no boot file.</summary>
        private static string BootFile(PartModule processor)
        {
            var field = processor.GetType().GetField("bootFile");
            var value = field != null ? field.GetValue(processor) as string : null;
            return string.IsNullOrEmpty(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }

        /// <summary>
        /// An experiment part and, outside Sandbox, the science still to gain from the subjects of that
        /// experiment already started. Null means none of it has been collected yet (or Sandbox, which tracks nothing).
        /// </summary>
        private static SnapshotExperiment ReadExperiment(Part part, ModuleScienceExperiment module)
        {
            var experiment = new SnapshotExperiment { PartId = part.persistentId, ExperimentId = module.experimentID ?? "" };
            var definition = ResearchAndDevelopment.GetExperiment(experiment.ExperimentId);
            experiment.Title = definition != null ? definition.experimentTitle : part.partInfo != null ? part.partInfo.title : "";
            if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance != null)
            {
                var prefix = experiment.ExperimentId + "@";
                var subjects = ResearchAndDevelopment.GetSubjects().Where(s => s.id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (subjects.Count > 0) experiment.ScienceLeft = subjects.Sum(s => (double)(s.scienceCap - s.science));
            }
            return experiment;
        }
    }
}
