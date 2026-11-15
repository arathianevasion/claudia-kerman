// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>What the leg serving a Contract Requirement must achieve at its body, in the order a visit reaches them.</summary>
    public enum ContractReach
    {
        /// <summary>#1048: fly through a band below orbit over the launch body, on a hop (<see cref="ReadRequirement.Band"/>).</summary>
        Hop = 0,
        /// <summary>Pass the body: a trip there, no capture.</summary>
        Flyby = 1,
        /// <summary>Be in orbit round it: the trip and its capture.</summary>
        Orbit = 2,
        /// <summary>Land on it.</summary>
        Land = 3,
    }

    /// <summary>One Contract Requirement as the kind table reads it: what its serving leg must achieve, or why it is "not yet".</summary>
    public sealed class ReadRequirement
    {
        public ContractRequirement Requirement;
        /// <summary>"not yet: …" when Core can't plan it; "" when it can.</summary>
        public string NotYet = "";
        /// <summary>The body it is met at; "" for none.</summary>
        public string Body = "";
        public ContractReach Reach;
        /// <summary>It is met only once the craft is home again (a return from the body).</summary>
        public bool Return;
        /// <summary>The phase of flight it falls in, for the card: the body, or "Kerbin orbit", or "Return".</summary>
        public string Phase = "";
        /// <summary>What it asks, in words for the card and the request: "orbit the Mun".</summary>
        public string Words = "";
        /// <summary>#1048: the band a <see cref="ContractReach.Hop"/> flies through; null for every other reach.</summary>
        public HopBand Band;
        /// <summary>#1029: the part test or haul it is; null for every other kind.</summary>
        public PartTestSpec Test;
        /// <summary>#1029: "this craft can't: …" when the craft can't meet it (the part isn't aboard, the band isn't passed); "" otherwise.</summary>
        public string CraftCant = "";

        public bool Plannable
        {
            get { return NotYet.Length == 0; }
        }
    }

    /// <summary>
    /// #1022: the kinds of Contract Requirement Core can plan (ADR 0021: anything else is "not yet", never improvised). So far the
    /// exploration (world-first) ones: a ProgressTrackingParameter of an ExplorationContract, by its <c>targetType</c> and
    /// <c>targetBody</c>; and (#1048) the Requirements met on a hop below orbit over the launch body: reaching space, and a part test
    /// FLYING or SUB_ORBITAL in an altitude band, read whole from its PartTest node's conditions. Every other Contract type is "not
    /// yet" as a whole, and so are records and the first launch, whose saved settings give no band to price a hop to.
    /// </summary>
    public static class ContractKinds
    {
        public const string Exploration = "ExplorationContract";
        public const string PartTest = "PartTest";
        public const string RecordNotYet = "not yet: records and the first launch aren't planned yet";
        public const string AwayHopNotYet = "not yet: hops over other bodies aren't planned yet";
        /// <summary>The launch body's index in the game's body list, as a part test's <c>body</c> setting names it: Kerbin, in the stock system.</summary>
        public const string HomeIndex = "1";

        private static readonly Dictionary<string, ContractReach> Reaches = new Dictionary<string, ContractReach>
        {
            { "FLYBY", ContractReach.Flyby }, { "ORBIT", ContractReach.Orbit }, { "LANDING", ContractReach.Land },
            { "RETURNFROMFLYBY", ContractReach.Flyby }, { "RETURNFROMORBIT", ContractReach.Orbit }, { "RETURNFROMSURFACE", ContractReach.Land },
        };

        private static readonly HashSet<string> BelowOrbit = new HashSet<string>
        {
            "FIRSTLAUNCH", "ALTITUDERECORD", "SPEEDRECORD", "DISTANCERECORD", "DEPTHRECORD",
        };

        private static readonly Dictionary<string, string> OtherReasons = new Dictionary<string, string>
        {
            { "SPACEWALK", "needs an EVA in a Manual stretch, not planned yet" },
            { "SURFACEEVA", "needs an EVA in a Manual stretch, not planned yet" },
            { "FLAGPLANT", "needs a flag planted in a Manual stretch, not planned yet" },
            { "SPLASHDOWN", "splashdowns away from home aren't planned yet" },
            { "SCIENCE", "collecting science isn't planned yet" },
            { "CREWRECOVERY", "recovering crew isn't planned yet" },
            { "RENDEZVOUS", "needs a second vessel" },
            { "DOCKING", "needs a second vessel" },
            { "ESCAPE", "escaping a body isn't priced yet" },
            { "BASECONSTRUCTION", "building a base isn't planned yet" },
            { "STATIONCONSTRUCTION", "building a station isn't planned yet" },
        };

        /// <summary>What each stock type is called on the card's "not yet" line.</summary>
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { "PartTest", "part tests" }, { "SurveyContract", "surveys" }, { "SatelliteContract", "satellites" },
            { "StationContract", "stations" }, { "BaseContract", "bases" }, { "TourismContract", "tourism" },
            { "RecoverAsset", "rescues and recoveries" }, { "CollectScience", "science collection" }, { "PlantFlag", "flag planting" },
            { "OrbitalConstructionContract", "orbital construction" }, { "VesselRepairContract", "repairs" },
            { "RoverConstructionContract", "rover construction" }, { "ISRUContract", "mining (ISRU)" }, { "ARMContract", "asteroid capture" },
            { "GrandTour", "Grand Tours" }, { "SentinelContract", "Sentinel scans" }, { "CometDetectionContract", "comet detection" },
            { "CometSampleContract", "comet samples" },
        };

        /// <summary>Why a Contract of a type Core doesn't plan yet is left out: "not yet: part tests aren't planned yet".</summary>
        public static string TypeNotYet(string kind)
        {
            string name;
            return "not yet: " + (TypeNames.TryGetValue(kind ?? "", out name) ? name : "this kind of Contract (" + kind + ")") + " aren't planned yet";
        }

        /// <summary>
        /// The Contract's Requirements still to meet (met ones are left out), read by the kind table, leaves of the tree only: a node with
        /// Requirements under it is read through them.
        /// </summary>
        public static List<ReadRequirement> Read(SnapshotContract contract, string home)
        {
            var read = new List<ReadRequirement>();
            Walk(contract, contract.Requirements, home, read);
            return read;
        }

        private static void Walk(SnapshotContract contract, List<ContractRequirement> requirements, string home, List<ReadRequirement> read)
        {
            foreach (var requirement in requirements)
            {
                if (requirement.State == "Complete") continue;
                if (contract.Kind == PartTest && requirement.Kind == PartTest)
                {
                    read.Add(ReadPartTest(requirement, requirements.Where(r => TestConditions.Contains(r.Kind)), home));
                    continue;
                }
                // A haul's conditions sit beside its PartTest Requirement: read with it.
                if (contract.Kind == PartTest && TestConditions.Contains(requirement.Kind) && requirements.Any(r => r.Kind == PartTest)) continue;
                if (requirement.Requirements.Count > 0)
                {
                    Walk(contract, requirement.Requirements, home, read);
                    continue;
                }
                read.Add(ReadOne(contract, requirement, home));
            }
        }

        private static ReadRequirement ReadOne(SnapshotContract contract, ContractRequirement requirement, string home)
        {
            var one = new ReadRequirement { Requirement = requirement };
            if (contract.Kind != Exploration || requirement.Kind != "ProgressTrackingParameter")
            {
                one.NotYet = contract.Kind != Exploration ? TypeNotYet(contract.Kind) : "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet";
                return one;
            }
            var type = Setting(requirement, "targetType").ToUpperInvariant();
            var body = Setting(requirement, "targetBody");
            ContractReach reach;
            string reason;
            if (type == "REACHSPACE") return body != home ? NotYet(one, AwayHopNotYet) : Hop(one, home, new HopBand { AboveAir = true });
            if (BelowOrbit.Contains(type)) one.NotYet = RecordNotYet;
            else if (OtherReasons.TryGetValue(type, out reason)) one.NotYet = "not yet: " + reason;
            else if (!Reaches.TryGetValue(type, out reach)) one.NotYet = "not yet: " + (type.Length > 0 ? type : "an unnamed milestone") + " isn't a Contract Requirement Claudia can plan yet";
            else if (body.Length == 0) one.NotYet = "not yet: the Contract names no body";
            else if (body == home && reach != ContractReach.Orbit) one.NotYet = "not yet: " + Verb(reach) + " " + home + " isn't planned yet";
            else
            {
                one.Body = body;
                one.Reach = reach;
                one.Return = type.StartsWith("RETURNFROM", System.StringComparison.Ordinal);
                one.Phase = one.Return ? "Return" : body == home ? home + " orbit" : body;
                one.Words = !one.Return ? Verb(reach) + " " + Named(body)
                    : "come home from " + (reach == ContractReach.Flyby ? "a flyby of " : reach == ContractReach.Orbit ? "orbit of " : "the surface of ") + Named(body);
            }
            return one;
        }

        /// <summary>The stock system's bodies by their index in the game's body list, as a part test's <c>body</c> setting names them.</summary>
        private static readonly string[] StockBodies =
            { "Sun", "Kerbin", "Mun", "Minmus", "Moho", "Eve", "Duna", "Ike", "Jool", "Laythe", "Vall", "Bop", "Tylo", "Gilly", "Pol", "Dres", "Eeloo" };

        /// <summary>The conditions a part test's tree holds beside or under its PartTest Requirement: read with it, never on their own.</summary>
        private static readonly HashSet<string> TestConditions = new HashSet<string>
            { "ReachDestination", "ReachSituation", "ReachAltitudeEnvelope", "ReachSpeedEnvelope" };

        /// <summary>#1029: a part test needing level flight in the air, or a run along the ground.</summary>
        public const string AircraftNotYet = "not yet: needs sustained flight in the atmosphere (aircraft)";
        public const string TestAwayNotYet = "not yet: tests in orbit or away from Kerbin aren't planned yet (launch and reentry run them)";

        /// <summary>
        /// #1048, #1029: a part test or haul, whole, its conditions read beside or under it (<paramref name="siblings"/>). Over the launch body,
        /// FLYING or SUB_ORBITAL in an altitude band (and speed band, when given) is a band the craft flies through on its ascent or its descent
        /// (<see cref="PartTestSpec"/>; the Contract check picks which); LANDED or SPLASHED there is met home again after the reentry. A haul
        /// carries the part, so ORBITING or LANDED elsewhere is that visit. A test elsewhere or in orbit, one along the ground at speed and an
        /// ESCAPING one are "not yet".
        /// </summary>
        private static ReadRequirement ReadPartTest(ContractRequirement requirement, IEnumerable<ContractRequirement> siblings, string home)
        {
            var one = new ReadRequirement { Requirement = requirement };
            var haul = Setting(requirement, "haul").ToUpperInvariant() == "TRUE";
            var situation = Setting(requirement, "situation").ToUpperInvariant();
            var conditions = requirement.Requirements.Concat(siblings).ToList();
            var altitude = conditions.FirstOrDefault(c => c.Kind == "ReachAltitudeEnvelope");
            var speed = conditions.FirstOrDefault(c => c.Kind == "ReachSpeedEnvelope");
            var body = BodyAt(Setting(requirement, "body"));
            var part = Setting(requirement, "part");
            one.Test = new PartTestSpec { Part = part, Haul = haul, Situation = situation };
            if (body == null) return NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            var athome = body == home;
            if (situation == "FLYING" || situation == "SUB_ORBITAL")
            {
                if (altitude == null) return NotYet(one, TypeNotYet(PartTest));
                if (!athome) return NotYet(one, AwayHopNotYet);
                var band = new HopBand
                {
                    MinAltitude = Number(altitude, "minAlt"), MaxAltitude = Number(altitude, "maxAlt"),
                    MinSpeed = speed == null ? null : Number(speed, "minSpd"), MaxSpeed = speed == null ? null : Number(speed, "maxSpd"),
                };
                if (!band.MinAltitude.HasValue) return NotYet(one, TypeNotYet(PartTest));
                one.Test.Band = band;
                Hop(one, home, band);
                one.Words = (haul ? "carry " : "test ") + part + " " + band.Words(home).Substring("hop ".Length);
                return one;
            }
            if (situation == "LANDED" || situation == "SPLASHED")
            {
                if (speed != null || altitude != null) return NotYet(one, AircraftNotYet);
                if (athome)
                {
                    one.Body = home;
                    one.Reach = ContractReach.Land;
                    one.Return = true;
                    one.Phase = "Return";
                    one.Words = (haul ? "bring " + part + " home, " : "test " + part + " home, ") + (situation == "LANDED" ? "landed" : "splashed down");
                    return one;
                }
                if (!haul || situation == "SPLASHED") return NotYet(one, TestAwayNotYet);
                return Visit(one, body, ContractReach.Land, "carry " + part + " to the surface of " + Named(body), home);
            }
            if (situation == "ORBITING" && haul) return Visit(one, body, ContractReach.Orbit, "carry " + part + " into orbit round " + Named(body), home);
            return NotYet(one, situation == "ORBITING" ? TestAwayNotYet : TypeNotYet(PartTest));
        }

        private static ReadRequirement Visit(ReadRequirement one, string body, ContractReach reach, string words, string home)
        {
            one.Body = body;
            one.Reach = reach;
            one.Phase = body == home ? body + " orbit" : body;
            one.Words = words;
            return one;
        }

        /// <summary>The body a part test's <c>body</c> index names in the stock system; null when unknown.</summary>
        private static string BodyAt(string index)
        {
            int at;
            return int.TryParse(index, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out at) && at >= 0 && at < StockBodies.Length ? StockBodies[at] : null;
        }

        private static ReadRequirement Hop(ReadRequirement one, string home, HopBand band)
        {
            one.Body = home;
            one.Reach = ContractReach.Hop;
            one.Band = band;
            one.Phase = home + " hop";
            one.Words = band.Words(home);
            return one;
        }

        private static ReadRequirement NotYet(ReadRequirement one, string why)
        {
            one.NotYet = why;
            return one;
        }

        private static double? Number(ContractRequirement requirement, string name)
        {
            double value;
            return double.TryParse(Setting(requirement, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : (double?)null;
        }

        private static string Verb(ContractReach reach)
        {
            return reach == ContractReach.Flyby ? "fly by" : reach == ContractReach.Orbit ? "orbit" : "land on";
        }

        /// <summary>"the Mun", and every other body by its own name.</summary>
        public static string Named(string body)
        {
            return body == "Mun" ? "the Mun" : body;
        }

        private static string Setting(ContractRequirement requirement, string name)
        {
            var setting = requirement.Settings.FirstOrDefault(s => s.Name == name);
            return setting == null ? "" : setting.Value.Trim();
        }
    }
}
