// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>What the leg serving a Contract Requirement must achieve at its body, in the order a visit reaches them.</summary>
    public enum ContractReach
    {
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

        public bool Plannable
        {
            get { return NotYet.Length == 0; }
        }
    }

    /// <summary>
    /// #1022: the kinds of Contract Requirement Core can plan (ADR 0021: anything else is "not yet", never improvised). So far the
    /// exploration (world-first) ones: a ProgressTrackingParameter of an ExplorationContract, by its <c>targetType</c> and
    /// <c>targetBody</c>. Every other Contract type is "not yet" as a whole. Requirements met below orbit (reach space, records, the first
    /// launch) are "not yet" until the Craft Check can price a suborbital flight (#1048), since it prices every route to orbit.
    /// </summary>
    public static class ContractKinds
    {
        public const string Exploration = "ExplorationContract";
        public const string Suborbital = "not yet: suborbital flights aren't priced yet (#1048)";

        private static readonly Dictionary<string, ContractReach> Reaches = new Dictionary<string, ContractReach>
        {
            { "FLYBY", ContractReach.Flyby }, { "ORBIT", ContractReach.Orbit }, { "LANDING", ContractReach.Land },
            { "RETURNFROMFLYBY", ContractReach.Flyby }, { "RETURNFROMORBIT", ContractReach.Orbit }, { "RETURNFROMSURFACE", ContractReach.Land },
        };

        private static readonly HashSet<string> BelowOrbit = new HashSet<string>
        {
            "FIRSTLAUNCH", "REACHSPACE", "ALTITUDERECORD", "SPEEDRECORD", "DISTANCERECORD", "DEPTHRECORD",
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
            if (BelowOrbit.Contains(type)) one.NotYet = Suborbital;
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
