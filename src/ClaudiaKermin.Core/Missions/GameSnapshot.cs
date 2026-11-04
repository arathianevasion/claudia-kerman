// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Roster;

namespace ClaudiaKermin.Core.Missions
{
    public sealed class SnapshotPart
    {
        /// <summary>The part's <c>persistentId</c>.</summary>
        public uint Id;
        /// <summary>
        /// The part's <c>craftID</c>: set in the editor and carried through launch (the proto part copies it), unlike
        /// <c>persistentId</c>, which launch may reassign. 0: unknown.
        /// </summary>
        public uint CraftId;
        /// <summary>The parent part's <c>persistentId</c>; null for the root.</summary>
        public uint? ParentId;
        /// <summary>The part's internal name.</summary>
        public string Name = "";
        /// <summary>The part's title as the player sees it.</summary>
        public string Title = "";
        /// <summary>The part has a kOS processor.</summary>
        public bool HasKosProcessor;
        /// <summary>The stage the part is in (its inverse stage number): 0 is the last to go.</summary>
        public int Stage;
        /// <summary>What the part can do, as the adapter reads it: command, crew_seat, probe_core, parachute, landing_leg,
        /// heat_shield, antenna, electric_generator, reaction_wheel, rcs, aero_stabiliser, gimbal, engine, experiment, kos_processor, decoupler, docking_port.</summary>
        public List<string> Tags = new List<string>();
        public int CrewCapacity;
        /// <summary>An antenna's power; 0 for other parts.</summary>
        public double AntennaPower;
        /// <summary>The boot file set on a kOS processor; "" for none.</summary>
        public string BootFile = "";
        /// <summary>A docking port's size (its node type, e.g. size1), as the Target Vessel's free_ports count them; "" for other parts.</summary>
        public string PortSize = "";

        // ---- Child Vessel facts (#100): enough to work out a separated vessel's own figures ----

        /// <summary>Mass with its resources, tonnes.</summary>
        public double MassT;
        /// <summary>Mass without its resources, tonnes; 0 means it carries none (the same as <see cref="MassT"/>).</summary>
        public double DryMassT;
        /// <summary>An engine's thrust in vacuum and at sea level (kN) and vacuum Isp (s); 0 for other parts.</summary>
        public double ThrustVacKn;
        public double ThrustAslKn;
        public double IspVac;
        /// <summary>An engine's Isp against pressure: (atmospheres, seconds) keys of its atmosphere curve; empty when not read.</summary>
        public List<double[]> IspCurve = new List<double[]>();
        /// <summary>The electric charge it can store.</summary>
        public double ChargeMax;
        /// <summary>
        /// A parachute's own numbers for the reentry predictor, as the adapter's ReentryCraft reads them, held retrograde (#626:
        /// the Chute Allocation divides the chutes by them); null for other parts, a cut chute, or when not read.
        /// Only the recording JSON carries it.
        /// </summary>
        public Tools.ReentryChute Chute;
    }

    public sealed class SnapshotCraft
    {
        public string Name = "";
        public double MassTonnes;
        /// <summary>The current stage number.</summary>
        public int Stage;
        /// <summary>Crew seats on the craft.</summary>
        public int Seats;
        /// <summary>The vessel's id (its persistent GUID) in Flight, for the Go to button; "" in the editor.</summary>
        public string VesselId = "";
        /// <summary>The vessel is a Kerbal on EVA (#262). Not in the JSON.</summary>
        public bool Eva;
        public List<SnapshotPart> Parts = new List<SnapshotPart>();
        /// <summary>
        /// The craft's drag flying nose first: (Mach, m²) keys, drag (N) = ½ρv² × the area, stock's multipliers included and
        /// its pseudo-Reynolds curve (1 in the air a launch climbs through) left out. Read once for the whole craft as it
        /// stands; empty when not read (an older recording). Only the recording JSON carries it: Planning Tools read it.
        /// </summary>
        public List<double[]> NoseDrag = new List<double[]>();
    }

    /// <summary>Distances in metres (from sea level), angles in degrees, times in seconds.</summary>
    public sealed class SnapshotOrbit
    {
        public double Apoapsis;
        public double Periapsis;
        public double Inclination;
        public double Eccentricity;
        public double Period;
        public double SemiMajorAxis;
        /// <summary>Longitude of the ascending node, degrees; with the next four it lets a tool propagate the orbit.</summary>
        public double Lan;
        /// <summary>Argument of periapsis, degrees.</summary>
        public double ArgumentOfPeriapsis;
        /// <summary>Mean anomaly at <see cref="Epoch"/>, radians (KSP's own unit).</summary>
        public double MeanAnomalyAtEpoch;
        /// <summary>The UT the mean anomaly is given at.</summary>
        public double Epoch;
        /// <summary>The body the orbit is round; "" when not read (an older recording).</summary>
        public string Body = "";
    }

    /// <summary>A maneuver node on the craft's flight plan. Delta-v in m/s.</summary>
    public sealed class SnapshotNode
    {
        public double Ut;
        /// <summary>The burn's total delta-v.</summary>
        public double DeltaV;
        public double Prograde;
        public double Normal;
        public double Radial;
        /// <summary>The label of the Mission Script run that added it, when this game session knows it; "" otherwise (the player's, or from before a load).</summary>
        public string AddedBy = "";
    }

    public sealed class SnapshotResource
    {
        public string Name = "";
        public double Amount;
        public double Max;
    }

    /// <summary>
    /// A Game Snapshot: plain facts about the craft (parts, orbit, situation, resources) read by the
    /// adapter on the main thread. The same JSON is what the debug capture button writes and what
    /// recorded test snapshots hold; vessel and part names inside it reach Claude as quoted JSON data.
    /// </summary>
    public sealed class GameSnapshot : MissionInput
    {
        public double Ut;
        /// <summary>KSP's situation name, e.g. PRELAUNCH, ORBITING, LANDED.</summary>
        public string Situation = "";
        /// <summary>The body the craft is at.</summary>
        public string Body = "";
        public SnapshotCraft Craft = new SnapshotCraft();
        /// <summary>Altitude above sea level, m; 0 in the editor.</summary>
        public double Altitude;
        /// <summary>Latitude of the craft over its body, degrees; 0 in the editor. With <see cref="CelestialLongitude"/> it places a pad against a plane (#464).</summary>
        public double Latitude;
        /// <summary>Longitude plus the body's rotation angle, degrees: the craft's longitude against the stars, as an orbit's LAN is; 0 in the editor.</summary>
        public double CelestialLongitude;
        /// <summary>Vertical speed, m/s, negative going down; 0 in the editor.</summary>
        public double VerticalSpeed;
        /// <summary>Speed over the surface, vertical included, m/s (#671): a landed craft that has stopped is under half a metre per second. 0 in the editor and in snapshots recorded before it.</summary>
        public double SurfaceSpeed;
        /// <summary>Null when there is no orbit to read (e.g. in the editor).</summary>
        public SnapshotOrbit Orbit;

        /// <summary>The figure a parsed snapshot carried: Claudia's turns leave out the body table that <see cref="ImpactInS"/> is worked from.</summary>
        private double? _parsedImpactInS;

        /// <summary>
        /// Seconds to impact on the current trajectory with no thrust, for a craft that is falling (#599): not landed, coming down
        /// with its periapsis below sea level, and not in the atmosphere (a Kerbin reentry under parachutes is the normal case). The
        /// orbit's crossing of the body's sea-level radius, so terrain above sea level makes the real impact earlier; null when not falling.
        /// </summary>
        public double? ImpactInS
        {
            get { return Bodies.Count > 0 ? FallingImpact() : _parsedImpactInS; }
        }

        /// <summary>The craft is falling: <see cref="ImpactInS"/> has a figure. Such a craft is sent a fresh snapshot on every turn.</summary>
        public bool Falling
        {
            get { return ImpactInS.HasValue; }
        }

        private double? FallingImpact()
        {
            if (Orbit == null || Situation == "LANDED" || Situation == "SPLASHED" || Situation == "PRELAUNCH") return null;
            if (Orbit.Periapsis >= 0 || VerticalSpeed >= 0) return null;
            var name = !string.IsNullOrEmpty(Orbit.Body) ? Orbit.Body : Body;
            var at = Bodies.FirstOrDefault(b => b.Name == name);
            if (at == null || at.Radius <= 0) return null;
            if (at.AtmosphereHeight > 0 && Altitude < at.AtmosphereHeight) return null;
            var conic = Tools.Conic.FromElements(at.Gm, Orbit.SemiMajorAxis, Orbit.Eccentricity, Orbit.Inclination, Orbit.Lan,
                Orbit.ArgumentOfPeriapsis, Orbit.MeanAnomalyAtEpoch, Orbit.Epoch);
            var impact = conic.TimeAtRadius(at.Radius, Ut, false);
            return impact.HasValue ? System.Math.Round(impact.Value - Ut, 1) : (double?)null;
        }
        public List<SnapshotResource> Resources = new List<SnapshotResource>();
        /// <summary>The maneuver nodes planned on the craft, soonest first; empty when there are none (#522).</summary>
        public List<SnapshotNode> Nodes = new List<SnapshotNode>();
        /// <summary>The RCS switch on the craft: true on, false off, null when it isn't read (the editor). Claudia plans around it; the mod never changes it.</summary>
        public bool? RcsOn;

        /// <summary>The Mission's Target Vessel, read live by its id (ADR 0013); null when the Mission has none, so the JSON has no entry.</summary>
        public SnapshotTarget Target;
        /// <summary>KSP's target vessel as the snapshot was read, which a Mission starting now takes; null for none. Not in the JSON.</summary>
        public SnapshotTarget KspTarget;
        /// <summary>The start form's Target Vessel as read before Start (#643), so the Craft Check judges the craft against it; null otherwise. Not in the JSON.</summary>
        public SnapshotTarget FormTarget;
        /// <summary>The other vessels in the game a Mission Goal may name (ADR 0013); empty when not read. Not in the JSON.</summary>
        public List<SnapshotVessel> Vessels = new List<SnapshotVessel>();
        /// <summary>The craft's <c>persistentId</c>, so it is never in the Target Vessel list (#709); 0 when not read. Not in the JSON.</summary>
        public uint CraftPersistentId;

        /// <summary>
        /// What the Mission Settings give a Planning Tool for the inputs Claudia leaves out: daylight landing on, and the time cap in
        /// hours. The Mission sets them on the turn's snapshot before a tool runs; they are not part of the snapshot's JSON.
        /// </summary>
        public bool DaylightLandingDefault = true;
        public double TimeCapHours = MissionSettingsValues.BalancedSearchHours;
        /// <summary>The Mission Setting that picks a planet's Transfer Window, Soonest or Optimal (#458).</summary>
        public TimeVersusDeltaV TimeVersusDeltaV = TimeVersusDeltaV.Balanced;
        /// <summary>The Craft Check's delta-v margin, %, that Soonest's total must fit within over Optimal's; PlayerConfig's default until the Mission sets it.</summary>
        public int MarginPercent = DefaultMarginPercent;
        /// <summary>The delta-v, m/s, of the Mission Plan's legs after the one this turn plans, flown by the craft; null with no Mission Plan. Set by the Mission before a tool runs; not in the JSON.</summary>
        public double? LaterLegsDv;
        private static readonly int DefaultMarginPercent = new Config.PlayerConfig().CraftCheckMarginPercent;

        // ---- Craft Check facts, computed by the adapter from the game ----

        /// <summary>SANDBOX, SCIENCE_SANDBOX or CAREER.</summary>
        public string GameMode = "";
        /// <summary>The stock delta-v simulator is on, so <see cref="Stages"/> is real.</summary>
        public bool DeltaVSimulator;
        /// <summary>Per-stage figures from the stock simulator.</summary>
        public List<SnapshotStage> Stages = new List<SnapshotStage>();
        /// <summary>The main throttle, 0 to 1; 0 when it isn't read.</summary>
        public double Throttle;
        /// <summary>Some stage's delta-v is unknown: stock reads 0 at a trickle throttle with an engine lit (#234).</summary>
        public bool DeltaVUnreliable
        {
            get { return Stages.Any(s => s.DeltaVUnreliable); }
        }
        public List<SnapshotCrewMember> Crew = new List<SnapshotCrewMember>();

        // ---- Aboard and remote facts (spec stories 41 to 44) ----

        /// <summary>Claudia's standing on the roster: Available, Assigned, Missing or Dead; "" when the adapter didn't read it.</summary>
        public string ClaudiaStanding = "";
        /// <summary>CommNet is on in the save. Off counts as linked.</summary>
        public bool CommNetEnabled = true;
        /// <summary>The craft has a CommNet link to Kerbin. Read as true when CommNet is off.</summary>
        public bool HasLink = true;

        /// <summary>The craft has a link to Kerbin, or the save has no CommNet to lose one.</summary>
        public bool Linked
        {
            get { return !CommNetEnabled || HasLink; }
        }

        /// <summary>Claudia is among the crew aboard (in the editor, in the ship's crew manifest).</summary>
        public bool ClaudiaAboard
        {
            get { return Crew.Any(c => c.Name == ClaudiaIdentity.Name); }
        }

        /// <summary>Crew seats not taken by anyone aboard.</summary>
        public int FreeSeats
        {
            get { return System.Math.Max(0, Craft.Seats - Crew.Count); }
        }

        /// <summary>The crew aboard other than Claudia: they ride as passengers while she flies.</summary>
        public int Passengers
        {
            get { return Crew.Count(c => c.Name != ClaudiaIdentity.Name); }
        }
        public List<SnapshotExperiment> Experiments = new List<SnapshotExperiment>();
        /// <summary>The game's bodies, for the Delta-v Map and thrust over weight.</summary>
        public List<SnapshotBody> Bodies = new List<SnapshotBody>();

        /// <summary>Crew aboard other than Claudia herself and Tourists: the Kerbals who could really fly the craft.</summary>
        public bool HasOtherCrew
        {
            get { return Crew.Any(c => c.Name != ClaudiaIdentity.Name && c.Trait != "Tourist"); }
        }

        /// <summary>The kOS processor Claudia would use: the one nearest the root. Null if the craft has none.</summary>
        public uint? ProcessorPartId
        {
            get { return ProcessorFor(null); }
        }

        /// <summary>The player's chosen processor if it is still a processor on the craft, otherwise the one nearest the root.</summary>
        public uint? ProcessorFor(uint? preferred)
        {
            return ProcessorChoice.Choose(Craft.Parts.Select(p => new VesselPart(p.Id, p.ParentId, p.HasKosProcessor)).ToList(), preferred);
        }

        /// <summary>The snapshot as JSON, with the game's bodies (the recording format).</summary>
        public string ToJson()
        {
            return ToJson(true);
        }

        /// <summary>A copy of the snapshot's own fields at <paramref name="ut"/>, sharing its lists: for a search from another time (#616) without touching a snapshot a tool thread may be reading.</summary>
        internal GameSnapshot At(double ut)
        {
            var copy = (GameSnapshot)MemberwiseClone();
            copy.Ut = ut;
            return copy;
        }

        /// <summary>
        /// The same; Claudia's turns leave out the bodies (they already carry the Delta-v Map) and the craft's drag table (only
        /// Planning Tools read it).
        /// </summary>
        public string ToJson(bool withBodies)
        {
            return ToJson(withBodies, true);
        }

        /// <summary>The same; <paramref name="withParts"/> false leaves out craft.parts, for a turn whose parts Claudia already has (#256).</summary>
        public string ToJson(bool withBodies, bool withParts)
        {
            var json = new JsonWriter().BeginObject()
                .Field("ut", Ut)
                .Field("situation", Situation)
                .Field("body", Body)
                .Field("mode", GameMode)
                .Field("dv_sim", DeltaVSimulator)
                .Field("throttle", Throttle)
                .Field("claudia_standing", ClaudiaStanding)
                .Field("commnet", CommNetEnabled)
                .Field("link", HasLink)
                .Field("altitude", Altitude)
                .Field("latitude", Latitude)
                .Field("celestial_longitude", CelestialLongitude)
                .Field("vertical_speed", VerticalSpeed);
            // Claudia's turns never carried it (the Debrief's wait reads it, #671): only the capture and the tools' snapshots write it.
            if (withBodies) json.Field("surface_speed", SurfaceSpeed);
            if (RcsOn.HasValue) json.Field("rcs_on", RcsOn.Value);
            var impact = ImpactInS;
            if (impact.HasValue) json.Field("impact_in_s", impact.Value);
            if (DeltaVUnreliable)
                json.Field("dv_reading", "unreliable at low throttle: stock delta-v reads 0 while an engine is lit at a trickle throttle, so the stages' delta-v is unknown, not 0");
            json.Name("craft").BeginObject()
                .Field("name", Craft.Name)
                .Field("mass_t", Craft.MassTonnes)
                .Field("stage", Craft.Stage)
                .Field("seats", Craft.Seats);
            if (Craft.VesselId.Length > 0) json.Field("vessel_id", Craft.VesselId);
            if (withParts) json.Name("parts").BeginArray();
            foreach (var part in withParts ? Craft.Parts : Enumerable.Empty<SnapshotPart>())
            {
                json.BeginObject().Field("id", part.Id).Name("parent");
                if (part.ParentId.HasValue) json.Number(part.ParentId.Value);
                else json.Null();
                json.Field("name", part.Name).Field("title", part.Title).Field("kos", part.HasKosProcessor)
                    .Field("stage", part.Stage).Field("seats", part.CrewCapacity).Field("antenna", part.AntennaPower).Field("boot", part.BootFile);
                // Written only when set, so a part's line stays short in Claudia's facts.
                if (part.MassT > 0) json.Field("mass_t", part.MassT);
                if (part.DryMassT > 0 && part.DryMassT != part.MassT) json.Field("dry_t", part.DryMassT);
                if (part.ThrustVacKn > 0) json.Field("thrust_vac_kn", part.ThrustVacKn).Field("thrust_asl_kn", part.ThrustAslKn).Field("isp_vac", part.IspVac);
                if (part.IspCurve.Count > 0)
                {
                    json.Name("isp_curve").BeginArray();
                    foreach (var key in part.IspCurve) json.BeginArray().Number(key[0]).Number(key[1]).EndArray();
                    json.EndArray();
                }
                if (part.ChargeMax > 0) json.Field("ec_max", part.ChargeMax);
                if (part.PortSize.Length > 0) json.Field("port", part.PortSize);
                if (withBodies && part.Chute != null) WriteChute(json, part.Chute);
                json.Name("tags").BeginArray();
                foreach (var tag in part.Tags) json.String(tag);
                json.EndArray().EndObject();
            }
            if (withParts) json.EndArray();
            if (withBodies && Craft.NoseDrag.Count > 0)
            {
                json.Name("nose_drag").BeginArray();
                foreach (var key in Craft.NoseDrag) json.BeginArray().Number(key[0]).Number(key[1]).EndArray();
                json.EndArray();
            }
            json.EndObject();
            json.Name("stages").BeginArray();
            foreach (var stage in Stages)
            {
                json.BeginObject().Field("stage", stage.Number);
                if (stage.DeltaVUnreliable) json.Name("dv_vac").Null().Name("dv_asl").Null().Field("dv_unreliable", true);
                else json.Field("dv_vac", stage.DeltaVVac).Field("dv_asl", stage.DeltaVAsl);
                json.Field("thrust_vac_kn", stage.ThrustVacKn)
                    .Field("start_mass_t", stage.StartMassT).Field("end_mass_t", stage.EndMassT).EndObject();
            }
            json.EndArray();
            json.Name("crew").BeginArray();
            foreach (var member in Crew) json.BeginObject().Field("name", member.Name).Field("trait", member.Trait).EndObject();
            json.EndArray();
            json.Name("experiments").BeginArray();
            foreach (var experiment in Experiments)
            {
                json.BeginObject().Field("part", experiment.PartId).Field("id", experiment.ExperimentId).Field("title", experiment.Title)
                    .Name("science_left");
                if (experiment.ScienceLeft.HasValue) json.Number(experiment.ScienceLeft.Value);
                else json.Null();
                json.EndObject();
            }
            json.EndArray();
            if (withBodies)
            {
                json.Name("bodies").BeginArray();
                foreach (var body in Bodies)
                {
                    json.BeginObject().Field("name", body.Name).Field("parent", body.Parent).Field("gm", body.Gm).Field("radius", body.Radius)
                        .Field("atmosphere_height", body.AtmosphereHeight).Field("sea_level_atm", body.SeaLevelAtm).Field("sma", body.Sma).Field("inclination", body.Inclination)
                        .Field("soi_radius", body.SoiRadius).Field("rotation_period", body.RotationPeriod)
                        .Field("eccentricity", body.Eccentricity).Field("lan", body.Lan).Field("arg_pe", body.ArgumentOfPeriapsis)
                        .Field("mean_anomaly_at_epoch", body.MeanAnomalyAtEpoch).Field("epoch", body.Epoch);
                    if (body.Air.Count > 0)
                    {
                        json.Name("air").BeginArray();
                        foreach (var row in body.Air)
                        {
                            json.BeginArray();
                            foreach (var value in row) json.Number(value);
                            json.EndArray();
                        }
                        json.EndArray();
                    }
                    json.EndObject();
                }
                json.EndArray();
            }
            json.Name("orbit");
            if (Orbit == null) json.Null();
            else
            {
                json.BeginObject()
                    .Field("apoapsis", Orbit.Apoapsis)
                    .Field("periapsis", Orbit.Periapsis)
                    .Field("inclination", Orbit.Inclination)
                    .Field("eccentricity", Orbit.Eccentricity)
                    .Field("period", Orbit.Period)
                    .Field("semi_major_axis", Orbit.SemiMajorAxis)
                    .Field("lan", Orbit.Lan)
                    .Field("arg_pe", Orbit.ArgumentOfPeriapsis)
                    .Field("mean_anomaly_at_epoch", Orbit.MeanAnomalyAtEpoch)
                    .Field("epoch", Orbit.Epoch)
                    .Field("orbit_body", Orbit.Body)
                    .EndObject();
            }
            json.Name("resources").BeginArray();
            foreach (var resource in Resources)
                json.BeginObject().Field("name", resource.Name).Field("amount", resource.Amount).Field("max", resource.Max).EndObject();
            json.EndArray();
            if (Nodes.Count > 0)
            {
                json.Name("nodes").BeginArray();
                foreach (var node in Nodes)
                {
                    json.BeginObject().Field("ut", node.Ut).Field("dv", node.DeltaV)
                        .Field("prograde", node.Prograde).Field("normal", node.Normal).Field("radial", node.Radial);
                    if (node.AddedBy.Length > 0) json.Field("added_by", node.AddedBy);
                    json.EndObject();
                }
                json.EndArray();
            }
            // The recording form (with bodies) is the one that keeps the target's id.
            if (Target != null) WriteTarget(json, Target, withBodies);
            return json.EndObject().ToString();
        }

        /// <summary>The Target Vessel's entry, kept short: its orbit's elements at the snapshot time, free ports as counts by size.</summary>
        private static void WriteTarget(JsonWriter json, SnapshotTarget target, bool withId)
        {
            json.Name("target").BeginObject();
            if (withId) json.Field("id", target.Id);
            json.Field("name", target.Name).Field("body", target.Body).Field("situation", target.Situation);
            if (target.Orbit != null)
            {
                var orbit = target.Orbit;
                json.Name("orbit").BeginObject().Field("apoapsis", orbit.Apoapsis).Field("periapsis", orbit.Periapsis)
                    .Field("inclination", orbit.Inclination).Field("eccentricity", orbit.Eccentricity).Field("semi_major_axis", orbit.SemiMajorAxis)
                    .Field("lan", orbit.Lan).Field("arg_pe", orbit.ArgumentOfPeriapsis).Field("mean_anomaly_at_epoch", orbit.MeanAnomalyAtEpoch)
                    .Field("epoch", orbit.Epoch).EndObject();
            }
            // A landed or splashed target's place (#662), to about a metre; an orbiting one's entry is unchanged.
            if (target.IsDown && target.Position != null)
                json.Field("lat", Math.Round(target.Position.Latitude, 5)).Field("lng", Math.Round(target.Position.Longitude, 5))
                    .Field("alt", Math.Round(target.Position.Altitude, 1));
            json.Name("free_ports").BeginObject();
            foreach (var size in target.FreePorts) json.Field(size.Key, size.Value);
            json.EndObject();
            if (target.PortTags.Count > 0)
            {
                json.Name("port_tags").BeginObject();
                foreach (var tag in target.PortTags) json.Field(tag.Key, tag.Value);
                json.EndObject();
            }
            json.EndObject();
        }

        private static SnapshotTarget ReadTarget(JsonNode target)
        {
            if (target == null || target.Kind != JsonKind.Object) return null;
            var read = new SnapshotTarget
            {
                Id = (uint)(JsonNode.Long(target["id"]) ?? 0),
                Name = JsonNode.Str(target["name"]) ?? "",
                Body = JsonNode.Str(target["body"]) ?? "",
                Situation = JsonNode.Str(target["situation"]) ?? "",
            };
            var orbit = target["orbit"];
            if (orbit != null && orbit.Kind == JsonKind.Object)
            {
                read.Orbit = new SnapshotOrbit
                {
                    Apoapsis = JsonNode.Double(orbit["apoapsis"]) ?? 0,
                    Periapsis = JsonNode.Double(orbit["periapsis"]) ?? 0,
                    Inclination = JsonNode.Double(orbit["inclination"]) ?? 0,
                    Eccentricity = JsonNode.Double(orbit["eccentricity"]) ?? 0,
                    SemiMajorAxis = JsonNode.Double(orbit["semi_major_axis"]) ?? 0,
                    Lan = JsonNode.Double(orbit["lan"]) ?? 0,
                    ArgumentOfPeriapsis = JsonNode.Double(orbit["arg_pe"]) ?? 0,
                    MeanAnomalyAtEpoch = JsonNode.Double(orbit["mean_anomaly_at_epoch"]) ?? 0,
                    Epoch = JsonNode.Double(orbit["epoch"]) ?? 0,
                    Body = read.Body,
                };
            }
            var lat = JsonNode.Double(target["lat"]);
            var lng = JsonNode.Double(target["lng"]);
            if (lat.HasValue && lng.HasValue)
                read.Position = new SnapshotPosition { Latitude = lat.Value, Longitude = lng.Value, Altitude = JsonNode.Double(target["alt"]) ?? 0 };
            var ports = target["free_ports"];
            if (ports != null && ports.Kind == JsonKind.Object)
                foreach (var size in ports.Members) read.FreePorts[size.Key] = (int)(JsonNode.Long(size.Value) ?? 0);
            var tags = target["port_tags"];
            if (tags != null && tags.Kind == JsonKind.Object)
                foreach (var tag in tags.Members) read.PortTags[tag.Key] = JsonNode.Str(tag.Value) ?? "";
            return read;
        }

        private static void WriteChute(JsonWriter json, Tools.ReentryChute chute)
        {
            json.Name("chute").BeginObject().Field("drogue", chute.Drogue).Field("state", chute.State.ToString().ToLowerInvariant())
                .Field("min_atm", chute.MinPressureAtm).Field("deploy_agl", chute.DeployAltitude).Field("semi_m2", chute.SemiArea).Field("full_m2", chute.FullArea)
                .Field("semi_s", chute.SemiSeconds).Field("full_s", chute.FullSeconds).Field("curve", chute.Curve).EndObject();
        }

        private static Tools.ReentryChute ReadChute(JsonNode chute)
        {
            if (chute == null) return null;
            Tools.ReentryChuteState state;
            if (!Enum.TryParse(JsonNode.Str(chute["state"]) ?? "", true, out state)) state = Tools.ReentryChuteState.Stowed;
            return new Tools.ReentryChute
            {
                Drogue = JsonNode.Bool(chute["drogue"]) ?? false,
                State = state,
                MinPressureAtm = JsonNode.Double(chute["min_atm"]) ?? 0,
                DeployAltitude = JsonNode.Double(chute["deploy_agl"]) ?? 0,
                SemiArea = JsonNode.Double(chute["semi_m2"]) ?? 0,
                FullArea = JsonNode.Double(chute["full_m2"]) ?? 0,
                SemiSeconds = JsonNode.Double(chute["semi_s"]) ?? 0,
                FullSeconds = JsonNode.Double(chute["full_s"]) ?? 0,
                Curve = JsonNode.Double(chute["curve"]) ?? 1,
            };
        }

        private static List<double[]> Keys(JsonNode keys)
        {
            if (keys == null) return new List<double[]>();
            return keys.Items.Where(k => k.Items.Count >= 2)
                .Select(k => new[] { JsonNode.Double(k.Items[0]) ?? 0, JsonNode.Double(k.Items[1]) ?? 0 }).ToList();
        }

        /// <summary>Rows of <paramref name="width"/> numbers; a shorter row is left out.</summary>
        private static List<double[]> Rows(JsonNode rows, int width)
        {
            if (rows == null) return new List<double[]>();
            return rows.Items.Where(r => r.Items.Count >= width)
                .Select(r => r.Items.Take(width).Select(v => JsonNode.Double(v) ?? 0).ToArray()).ToList();
        }

        /// <summary>Reads snapshot JSON; missing fields keep their defaults. Throws <see cref="System.FormatException"/> on bad JSON.</summary>
        public static GameSnapshot Parse(string text)
        {
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException e)
            {
                throw new System.FormatException("not a Game Snapshot: " + e.Message);
            }
            var snapshot = new GameSnapshot
            {
                Ut = JsonNode.Double(root["ut"]) ?? 0,
                Situation = JsonNode.Str(root["situation"]) ?? "",
                Body = JsonNode.Str(root["body"]) ?? "",
                GameMode = JsonNode.Str(root["mode"]) ?? "",
                DeltaVSimulator = JsonNode.Bool(root["dv_sim"]) ?? false,
                Throttle = JsonNode.Double(root["throttle"]) ?? 0,
                ClaudiaStanding = JsonNode.Str(root["claudia_standing"]) ?? "",
                CommNetEnabled = JsonNode.Bool(root["commnet"]) ?? true,
                HasLink = JsonNode.Bool(root["link"]) ?? true,
                Altitude = JsonNode.Double(root["altitude"]) ?? 0,
                Latitude = JsonNode.Double(root["latitude"]) ?? 0,
                CelestialLongitude = JsonNode.Double(root["celestial_longitude"]) ?? 0,
                VerticalSpeed = JsonNode.Double(root["vertical_speed"]) ?? 0,
                SurfaceSpeed = JsonNode.Double(root["surface_speed"]) ?? 0,
                RcsOn = JsonNode.Bool(root["rcs_on"]),
                _parsedImpactInS = JsonNode.Double(root["impact_in_s"]),
                Target = ReadTarget(root["target"]),
            };
            var craft = root["craft"];
            if (craft != null)
            {
                snapshot.Craft.Name = JsonNode.Str(craft["name"]) ?? "";
                snapshot.Craft.MassTonnes = JsonNode.Double(craft["mass_t"]) ?? 0;
                snapshot.Craft.Stage = (int)(JsonNode.Long(craft["stage"]) ?? 0);
                snapshot.Craft.Seats = (int)(JsonNode.Long(craft["seats"]) ?? 0);
                snapshot.Craft.VesselId = JsonNode.Str(craft["vessel_id"]) ?? "";
                snapshot.Craft.NoseDrag = Keys(craft["nose_drag"]);
                var parts = craft["parts"];
                if (parts != null)
                {
                    foreach (var part in parts.Items)
                    {
                        var parent = JsonNode.Long(part["parent"]);
                        var tags = part["tags"];
                        var mass = JsonNode.Double(part["mass_t"]) ?? 0;
                        snapshot.Craft.Parts.Add(new SnapshotPart
                        {
                            Id = (uint)(JsonNode.Long(part["id"]) ?? 0),
                            ParentId = parent.HasValue ? (uint?)parent.Value : null,
                            Name = JsonNode.Str(part["name"]) ?? "",
                            Title = JsonNode.Str(part["title"]) ?? "",
                            HasKosProcessor = JsonNode.Bool(part["kos"]) ?? false,
                            Stage = (int)(JsonNode.Long(part["stage"]) ?? 0),
                            CrewCapacity = (int)(JsonNode.Long(part["seats"]) ?? 0),
                            AntennaPower = JsonNode.Double(part["antenna"]) ?? 0,
                            BootFile = JsonNode.Str(part["boot"]) ?? "",
                            PortSize = JsonNode.Str(part["port"]) ?? "",
                            Tags = tags == null ? new List<string>() : tags.Items.Select(JsonNode.Str).Where(t => t != null).ToList(),
                            MassT = mass,
                            DryMassT = JsonNode.Double(part["dry_t"]) ?? mass,
                            ThrustVacKn = JsonNode.Double(part["thrust_vac_kn"]) ?? 0,
                            ThrustAslKn = JsonNode.Double(part["thrust_asl_kn"]) ?? 0,
                            IspVac = JsonNode.Double(part["isp_vac"]) ?? 0,
                            IspCurve = Keys(part["isp_curve"]),
                            ChargeMax = JsonNode.Double(part["ec_max"]) ?? 0,
                            Chute = ReadChute(part["chute"]),
                        });
                    }
                }
            }
            var stages = root["stages"];
            if (stages != null)
            {
                foreach (var stage in stages.Items)
                {
                    snapshot.Stages.Add(new SnapshotStage
                    {
                        Number = (int)(JsonNode.Long(stage["stage"]) ?? 0),
                        DeltaVUnreliable = JsonNode.Bool(stage["dv_unreliable"]) ?? false,
                        DeltaVVac = JsonNode.Double(stage["dv_vac"]) ?? 0,
                        DeltaVAsl = JsonNode.Double(stage["dv_asl"]) ?? 0,
                        ThrustVacKn = JsonNode.Double(stage["thrust_vac_kn"]) ?? 0,
                        StartMassT = JsonNode.Double(stage["start_mass_t"]) ?? 0,
                        EndMassT = JsonNode.Double(stage["end_mass_t"]) ?? 0,
                    });
                }
            }
            var crew = root["crew"];
            if (crew != null)
            {
                foreach (var member in crew.Items)
                    snapshot.Crew.Add(new SnapshotCrewMember { Name = JsonNode.Str(member["name"]) ?? "", Trait = JsonNode.Str(member["trait"]) ?? "" });
            }
            var experiments = root["experiments"];
            if (experiments != null)
            {
                foreach (var experiment in experiments.Items)
                {
                    snapshot.Experiments.Add(new SnapshotExperiment
                    {
                        PartId = (uint)(JsonNode.Long(experiment["part"]) ?? 0),
                        ExperimentId = JsonNode.Str(experiment["id"]) ?? "",
                        Title = JsonNode.Str(experiment["title"]) ?? "",
                        ScienceLeft = JsonNode.Double(experiment["science_left"]),
                    });
                }
            }
            var bodies = root["bodies"];
            if (bodies != null)
            {
                foreach (var body in bodies.Items)
                {
                    snapshot.Bodies.Add(new SnapshotBody
                    {
                        Name = JsonNode.Str(body["name"]) ?? "",
                        Parent = JsonNode.Str(body["parent"]) ?? "",
                        Gm = JsonNode.Double(body["gm"]) ?? 0,
                        Radius = JsonNode.Double(body["radius"]) ?? 0,
                        AtmosphereHeight = JsonNode.Double(body["atmosphere_height"]) ?? 0,
                        SeaLevelAtm = JsonNode.Double(body["sea_level_atm"]) ?? 0,
                        Sma = JsonNode.Double(body["sma"]) ?? 0,
                        Inclination = JsonNode.Double(body["inclination"]) ?? 0,
                        SoiRadius = JsonNode.Double(body["soi_radius"]) ?? 0,
                        RotationPeriod = JsonNode.Double(body["rotation_period"]) ?? 0,
                        Eccentricity = JsonNode.Double(body["eccentricity"]) ?? 0,
                        Lan = JsonNode.Double(body["lan"]) ?? 0,
                        ArgumentOfPeriapsis = JsonNode.Double(body["arg_pe"]) ?? 0,
                        MeanAnomalyAtEpoch = JsonNode.Double(body["mean_anomaly_at_epoch"]) ?? 0,
                        Epoch = JsonNode.Double(body["epoch"]) ?? 0,
                        Air = Rows(body["air"], 4),
                    });
                }
            }
            var orbit = root["orbit"];
            if (orbit != null && orbit.Kind == JsonKind.Object)
            {
                snapshot.Orbit = new SnapshotOrbit
                {
                    Apoapsis = JsonNode.Double(orbit["apoapsis"]) ?? 0,
                    Periapsis = JsonNode.Double(orbit["periapsis"]) ?? 0,
                    Inclination = JsonNode.Double(orbit["inclination"]) ?? 0,
                    Eccentricity = JsonNode.Double(orbit["eccentricity"]) ?? 0,
                    Period = JsonNode.Double(orbit["period"]) ?? 0,
                    SemiMajorAxis = JsonNode.Double(orbit["semi_major_axis"]) ?? 0,
                    Lan = JsonNode.Double(orbit["lan"]) ?? 0,
                    ArgumentOfPeriapsis = JsonNode.Double(orbit["arg_pe"]) ?? 0,
                    MeanAnomalyAtEpoch = JsonNode.Double(orbit["mean_anomaly_at_epoch"]) ?? 0,
                    Epoch = JsonNode.Double(orbit["epoch"]) ?? 0,
                    Body = JsonNode.Str(orbit["orbit_body"]) ?? "",
                };
            }
            var resources = root["resources"];
            if (resources != null)
            {
                foreach (var resource in resources.Items)
                {
                    snapshot.Resources.Add(new SnapshotResource
                    {
                        Name = JsonNode.Str(resource["name"]) ?? "",
                        Amount = JsonNode.Double(resource["amount"]) ?? 0,
                        Max = JsonNode.Double(resource["max"]) ?? 0,
                    });
                }
            }
            var nodes = root["nodes"];
            if (nodes != null)
            {
                foreach (var node in nodes.Items)
                {
                    snapshot.Nodes.Add(new SnapshotNode
                    {
                        Ut = JsonNode.Double(node["ut"]) ?? 0,
                        DeltaV = JsonNode.Double(node["dv"]) ?? 0,
                        Prograde = JsonNode.Double(node["prograde"]) ?? 0,
                        Normal = JsonNode.Double(node["normal"]) ?? 0,
                        Radial = JsonNode.Double(node["radial"]) ?? 0,
                        AddedBy = JsonNode.Str(node["added_by"]) ?? "",
                    });
                }
            }
            return snapshot;
        }
    }
}
