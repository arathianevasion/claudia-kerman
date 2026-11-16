// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1031: what a satellite, station or base Contract needs of the vessel it is met by, checked per Contract against the craft.</summary>
    public sealed class VesselNeeds
    {
        /// <summary>"satellite", "station" or "base", for the words.</summary>
        public string Noun = "";
        /// <summary>The part tags it needs aboard (antenna, electric_generator, docking_port, wheel).</summary>
        public List<string> Tags = new List<string>();
        /// <summary>Crew seats it needs; 0 for none.</summary>
        public int Capacity;
        /// <summary>A crew trait and how many kerbals with it it needs aboard; "" and 0 for none.</summary>
        public string Trait = "";
        public int TraitCount;
        /// <summary>The named parts it needs aboard, by internal name.</summary>
        public List<string> Parts = new List<string>();
        /// <summary>It must fly with no crew: a probe.</summary>
        public bool Unmanned;
        /// <summary>A Child Vessel the craft releases may meet it instead of the craft (a satellite).</summary>
        public bool ChildMay;
    }

    /// <summary>
    /// #1031: satellite, station and base Contract Requirements (ADR 0021: Core's kind table). A satellite is one new vessel, the craft
    /// itself or one Child Vessel, in the Contract's orbit (<see cref="ContractOrbit"/>); networks and repositioning are "not yet". A
    /// station is the craft in orbit round the body; one asking for a part docked on to an existing station is "not yet". A base, mobile
    /// ones included, is the craft landed on the body. Every Requirement of one is met by the same leg: the vessel there, with what it
    /// needs (<see cref="VesselNeeds"/>), held there until the game marks it met (#1025's hold).
    /// </summary>
    internal static class StructureKinds
    {
        public const string Satellite = "SatelliteContract";
        public const string Station = "StationContract";
        public const string Base = "BaseContract";

        public const string NetworkNotYet = "not yet: satellite networks aren't planned yet";
        public const string RepositionNotYet = "not yet: repositioning an existing satellite isn't planned yet";
        public const string DockedStationNotYet = "not yet: a station needing a part docked on isn't planned yet";
        public const string DockedBaseNotYet = "not yet: a base needing a part added to an existing one isn't planned yet";

        /// <summary>A VesselSystemsParameter's module types, as the part tags that meet them.</summary>
        private static readonly Dictionary<string, string> ModuleTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Antenna", "antenna" }, { "Generator", "electric_generator" }, { "Power", "electric_generator" }, { "Dock", "docking_port" }, { "Wheel", "wheel" },
        };

        /// <summary>What each part tag is called in "this craft can't: no … aboard".</summary>
        internal static readonly Dictionary<string, string> TagWords = new Dictionary<string, string>
        {
            { "antenna", "antenna" }, { "electric_generator", "power source" }, { "docking_port", "docking port" }, { "wheel", "wheel" },
        };

        public static bool Plans(string kind)
        {
            return kind == Satellite || kind == Station || kind == Base;
        }

        public static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            var all = Leaves(contract.Requirements).ToList();
            var whole = Whole(contract, all, home);
            if (whole.Length > 0) return ContractKinds.NotYet(one, whole);
            one.ChildMay = contract.Kind == Satellite;
            var noun = contract.Kind == Satellite ? "satellite" : contract.Kind == Station ? "station" : "base";
            var location = all.First(r => r.Kind == "SpecificOrbitParameter" || r.Kind == "LocationAndSituationParameter");
            var body = BodyOf(location);
            var reach = contract.Kind == Base ? ContractReach.Land : ContractReach.Orbit;
            var where = (reach == ContractReach.Land ? "landed on " : "in orbit round ") + ContractKinds.Named(body);
            switch (requirement.Kind)
            {
                case "SpecificOrbitParameter":
                    one.Orbit = new ContractOrbit
                    {
                        Key = requirement.Key, Body = body,
                        Inclination = ContractKinds.Number(requirement, "inclination") ?? 0, Eccentricity = ContractKinds.Number(requirement, "eccentricity") ?? 0,
                        Sma = ContractKinds.Number(requirement, "sma") ?? 0, Window = ContractKinds.Number(requirement, "deviationWindow") ?? 0,
                    };
                    return ContractKinds.Visit(one, body, reach, "put the satellite into the Contract's orbit round " + ContractKinds.Named(body), home);
                case "LocationAndSituationParameter":
                    return ContractKinds.Visit(one, body, reach, "bring the new " + noun + " " + where, home);
                case "VesselSystemsParameter":
                    one.Vessel = Needs(noun, requirement, contract.Kind == Satellite);
                    return ContractKinds.Visit(one, body, reach, "fly a new " + noun + " " + where + Fitted(one.Vessel), home);
                case "CrewCapacityParameter":
                    one.Vessel = new VesselNeeds { Noun = noun, Capacity = (int)(ContractKinds.Number(requirement, "targetCapacity") ?? 0) };
                    return ContractKinds.Visit(one, body, reach, "the " + noun + " seats " + one.Vessel.Capacity + " kerbals", home);
                case "CrewTraitParameter":
                    one.Vessel = new VesselNeeds
                    {
                        Noun = noun, Trait = ContractKinds.Setting(requirement, "targetTrait"), TraitCount = (int)(ContractKinds.Number(requirement, "targetCount") ?? 1),
                    };
                    return ContractKinds.Visit(one, body, reach, "the " + noun + " carries " + one.Vessel.TraitCount + " " + one.Vessel.Trait + (one.Vessel.TraitCount == 1 ? "" : "s"), home);
                case "PartRequestParameter":
                    one.Vessel = new VesselNeeds { Noun = noun, Parts = PartNames(requirement) };
                    if (one.Vessel.Parts.Count == 0) return ContractKinds.NotYet(one, "not yet: the Contract names no part to carry");
                    return ContractKinds.Visit(one, body, reach, "the " + noun + " carries " + string.Join(", ", one.Vessel.Parts.ToArray()), home);
                case "StabilityParameter":
                    var hold = ContractKinds.Number(requirement, "holdSeconds") ?? 10;
                    return ContractKinds.Visit(one, body, reach, "hold the " + noun + " " + where + " for " + hold + " s", home);
                default:
                    return ContractKinds.NotYet(one, "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet");
            }
        }

        /// <summary>Why the whole Contract is "not yet" (a network, a repositioning, a part docked on, a place Claudia doesn't plan); "" when it isn't.</summary>
        private static string Whole(SnapshotContract contract, List<ContractRequirement> all, string home)
        {
            var orbits = all.Count(r => r.Kind == "SpecificOrbitParameter");
            var locations = all.Where(r => r.Kind == "LocationAndSituationParameter").ToList();
            var systems = all.Where(r => r.Kind == "VesselSystemsParameter").ToList();
            var addsToOld = systems.Any(s => ContractKinds.Setting(s, "requireNew").ToUpperInvariant() == "FALSE");
            if (contract.Kind == Satellite)
            {
                if (orbits > 1 || systems.Count > 1) return NetworkNotYet;
                if (addsToOld) return RepositionNotYet;
                if (orbits == 0) return "not yet: the satellite Contract names no orbit";
            }
            else
            {
                if (addsToOld || all.Any(r => r.Kind.IndexOf("Dock", StringComparison.OrdinalIgnoreCase) >= 0)) return contract.Kind == Station ? DockedStationNotYet : DockedBaseNotYet;
                if (locations.Count != 1) return "not yet: the " + (contract.Kind == Station ? "station" : "base") + " Contract names no one place";
                var situation = ContractKinds.Setting(locations[0], "targetSituation").ToUpperInvariant();
                if (situation != (contract.Kind == Station ? "ORBITING" : "LANDED")) return "not yet: a " + (contract.Kind == Station ? "station" : "base") + " " + situation.ToLowerInvariant() + " isn't planned yet";
            }
            var location = all.FirstOrDefault(r => r.Kind == "SpecificOrbitParameter" || r.Kind == "LocationAndSituationParameter");
            var body = BodyOf(location);
            if (body == null || body == "Sun") return "not yet: the Contract's body isn't one Claudia knows";
            if (contract.Kind == Base && body == home) return "not yet: bases on " + home + " aren't planned yet";
            var unknown = systems.SelectMany(s => Modules(s)).FirstOrDefault(m => !ModuleTags.ContainsKey(m));
            if (unknown != null) return "not yet: Claudia can't check a craft for " + unknown + " yet";
            return "";
        }

        private static VesselNeeds Needs(string noun, ContractRequirement systems, bool satellite)
        {
            return new VesselNeeds
            {
                Noun = noun, Tags = Modules(systems).Select(m => ModuleTags[m]).Distinct().ToList(),
                Unmanned = ContractKinds.Setting(systems, "mannedStatus").ToUpperInvariant() == "UNMANNED", ChildMay = satellite,
            };
        }

        /// <summary>" with an antenna and power source", or "" when it needs no part.</summary>
        private static string Fitted(VesselNeeds needs)
        {
            var words = needs.Tags.Select(t => TagWords[t]).ToList();
            var crew = needs.Unmanned ? ", uncrewed" : "";
            return words.Count == 0 ? crew : crew + " with " + string.Join(" and ", words.ToArray());
        }

        private static IEnumerable<string> Modules(ContractRequirement systems)
        {
            return ContractKinds.Setting(systems, "checkModuleTypes").Split('|').Select(m => m.Trim()).Where(m => m.Length > 0);
        }

        private static List<string> PartNames(ContractRequirement requirement)
        {
            return requirement.Settings.Where(s => s.Name == "part" || s.Name == "partNames")
                .SelectMany(s => s.Value.Split('|', ',')).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        }

        private static string BodyOf(ContractRequirement location)
        {
            if (location == null) return null;
            var index = ContractKinds.Setting(location, "TargetBody");
            if (index.Length == 0) index = ContractKinds.Setting(location, "targetBody");
            return ContractKinds.BodyAt(index);
        }

        private static IEnumerable<ContractRequirement> Leaves(IEnumerable<ContractRequirement> requirements)
        {
            foreach (var requirement in requirements)
            {
                if (requirement.Requirements.Count == 0) yield return requirement;
                else foreach (var under in Leaves(requirement.Requirements)) yield return under;
            }
        }

        /// <summary>
        /// "this craft can't: …" for what <paramref name="needs"/> asks that the craft lacks; "" when it has it all. A satellite may be the
        /// craft itself or one Child Vessel it releases: one of them must have everything (a command part, and no crew when uncrewed). The
        /// craft itself only when it may stay there (<paramref name="craftMayStay"/>): with Claudia aboard it comes home.
        /// </summary>
        public static string Lacks(GameSnapshot snapshot, VesselNeeds needs, bool craftMayStay)
        {
            var parts = snapshot.Craft.Parts;
            if (needs.ChildMay)
            {
                var units = new List<List<SnapshotPart>>();
                if (craftMayStay && (!needs.Unmanned || snapshot.Crew.Count == 0)) units.Add(parts);
                var byId = parts.ToDictionary(p => p.Id);
                foreach (var separation in ChildVessels.Staging(snapshot, snapshot.Crew.Count == 0, snapshot.ProcessorPartId).Separations)
                {
                    var child = separation.PartIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                    if (!needs.Unmanned || child.All(p => p.Crew <= 0 && !p.Tags.Contains("crew_seat"))) units.Add(child);
                }
                var noun = (needs.Unmanned ? "uncrewed " : "") + (needs.Noun.Length > 0 ? needs.Noun : "satellite");
                var able = units.FirstOrDefault(u => u.Any(p => p.Tags.Contains("command")) && MissingAny(u, needs) == null);
                if (able != null) return "";
                if (units.Count == 0) return "this craft can't: no Child Vessel can be the " + noun;
                var best = units.OrderBy(u => needs.Tags.Count(t => !u.Any(p => p.Tags.Contains(t))) + needs.Parts.Count(n => u.All(p => p.Name != n))).First();
                var missing = MissingAny(best, needs);
                return "this craft can't: no " + (missing ?? "command part") + " on the " + noun;
            }
            var lacking = Missing(parts, needs.Tags);
            if (lacking != null) return "this craft can't: no " + lacking + " aboard";
            var seats = Math.Max(snapshot.Craft.Seats, parts.Sum(p => p.CrewCapacity));
            if (seats < needs.Capacity) return "this craft can't: seats " + seats + " kerbals, the " + needs.Noun + " needs " + needs.Capacity;
            if (needs.TraitCount > 0)
            {
                var have = snapshot.Crew.Count(c => string.Equals(c.Trait, needs.Trait, StringComparison.OrdinalIgnoreCase));
                if (have < needs.TraitCount) return "this craft can't: carries " + have + " " + needs.Trait + (have == 1 ? "" : "s") + ", the " + needs.Noun + " needs " + needs.TraitCount;
            }
            var part = needs.Parts.FirstOrDefault(n => parts.All(p => p.Name != n));
            return part == null ? "" : "this craft can't: no " + part + " aboard";
        }

        /// <summary>#1035: the words for the first tag or named part (by internal name) none of <paramref name="parts"/> has; null when they have them all.</summary>
        private static string MissingAny(List<SnapshotPart> parts, VesselNeeds needs)
        {
            var part = needs.Parts.FirstOrDefault(n => parts.All(p => p.Name != n));
            return Missing(parts, needs.Tags) ?? (part == null ? null : PartWords(part));
        }

        /// <summary>#1035: a named part as the player knows it: the Sentinel telescope by its title, any other by its internal name.</summary>
        private static string PartWords(string name)
        {
            return name == PlacementKinds.TelescopePart ? "SENTINEL Infrared Telescope" : name;
        }

        /// <summary>The words for the first tag none of <paramref name="parts"/> has; null when they have them all.</summary>
        private static string Missing(List<SnapshotPart> parts, List<string> tags)
        {
            var tag = tags.FirstOrDefault(t => !parts.Any(p => p.Tags.Contains(t)));
            return tag == null ? null : TagWords[tag];
        }
    }
}
