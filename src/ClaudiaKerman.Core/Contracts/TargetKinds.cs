// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1033: the vessel a Target Vessel Contract sends Claudia to, and what the Manual stretch there needs aboard.</summary>
    public sealed class ContractTarget
    {
        /// <summary>The Contract's GUID: the Adapter tags the vessel the game spawned for it with it (<see cref="SnapshotVessel.ContractGuid"/>).</summary>
        public string Guid = "";
        /// <summary>The Contract's type, as <see cref="SnapshotContract.Kind"/>.</summary>
        public string Kind = "";
        /// <summary>The vessel as the Contract names it ("Relay Sat Q-7"), "" when it names none.</summary>
        public string VesselName = "";
        /// <summary>A rescue's kerbal, "" for every other kind.</summary>
        public string Kerbal = "";
        /// <summary>A construction's part to attach or a repair's part to fix, by internal name; "" when none is named.</summary>
        public string Part = "";
        /// <summary>The vessel in the game, once the Contract check has found it; null until then, or when it isn't in the game.</summary>
        public SnapshotVessel Vessel;

        /// <summary>The vessel by its name in the game, else the Contract's name for it.</summary>
        public string Name
        {
            get { return Vessel != null && Vessel.Name.Trim().Length > 0 ? Vessel.Name.Trim() : VesselName.Length > 0 ? VesselName : Kind == TargetKinds.Comet ? "the comet" : "the Contract's vessel"; }
        }
    }

    /// <summary>
    /// #1033: Target Vessel Contracts (ADR 0021: Core's kind table; ADR 0013's amendment for #1033). An orbital rescue, a repair, a
    /// construction or a comet sample sends Claudia to one vessel the game spawned for it (the comet, for a sample), the Mission's one
    /// Target Vessel: she flies the rendezvous, and the player does the EVA in a Manual stretch (a rover's drive is the player's too). A rescue
    /// then brings the kerbal home in an empty seat; a repair needs an Engineer and an EVA repair kit, a construction an Engineer and the
    /// part to attach. The kind table reads what the Contract says; the Contract check finds the vessel in the Game Snapshot
    /// (<see cref="Locate"/>) and prices the rendezvous with it. Ground rescues and part recovery are "not yet", and so is a comet whose
    /// intercept the planner can't price (one in solar orbit).
    /// </summary>
    internal static class TargetKinds
    {
        public const string Rescue = "RecoverAsset";
        public const string OrbitalConstruction = "OrbitalConstructionContract";
        public const string RoverConstruction = "RoverConstructionContract";
        public const string Repair = "VesselRepairContract";
        public const string Comet = "CometSampleContract";

        /// <summary>The EVA repair kit's part name, carried in an inventory or on the craft.</summary>
        public const string RepairKitPart = "evaRepairKit";

        public const string GroundRescueNotYet = "not yet: ground rescues aren't planned yet";
        public const string PartRecoveryNotYet = "not yet: part recovery isn't planned yet";
        public const string CometNotYet = "not yet: this comet's intercept is beyond what the planner prices";
        public const string NotFoundNotYet = "not yet: the Contract's vessel isn't in the game";
        public const string HomeLandedNotYet = "not yet: a vessel landed on Kerbin isn't planned yet";
        public const string OneTarget = "not this flight: one Target Vessel per Mission";
        public const string NoEngineer = "this craft can't: no Engineer aboard";
        public const string NoRepairKit = "this craft can't: no EVA repair kit aboard";
        public const string NoKerbalForSample = "this craft can't: a comet sample needs a kerbal aboard to go on EVA";

        public static bool Plans(string kind)
        {
            return kind == Rescue || kind == OrbitalConstruction || kind == RoverConstruction || kind == Repair || kind == Comet;
        }

        /// <summary>The Contract's vessel as its settings name it; the Contract check finds it in the game.</summary>
        private static ContractTarget TargetOf(SnapshotContract contract, ContractRequirement requirement)
        {
            return new ContractTarget
            {
                Guid = contract.Guid, Kind = contract.Kind,
                VesselName = First(contract, requirement, "vesselName", "VesselName", "cometName", "targetName"),
                Kerbal = First(contract, requirement, "kerbalName", "KerbalName"),
                Part = First(contract, requirement, "partName", "PartName", "part"),
            };
        }

        public static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            if (contract.Kind == Rescue)
            {
                var type = ContractKinds.Setting(Root(contract), "recoveryType");
                if ((type.Length > 0 && type != "1") || Leaves(contract).Any(r => r.Kind == "AcquirePart" || r.Kind == "RecoverPart")) return ContractKinds.NotYet(one, PartRecoveryNotYet);
            }
            one.Target = TargetOf(contract, requirement);
            return Describe(one, requirement.Kind);
        }

        /// <summary>What the Requirement asks, in words, from its kind and the Contract's vessel as known so far.</summary>
        private static ReadRequirement Describe(ReadRequirement one, string kind)
        {
            var name = one.Target.Name;
            switch (kind)
            {
                case "AcquireCrew":
                    one.Rendezvous = true;
                    one.Manual = "bring " + Kerbal(one.Target) + " aboard on EVA";
                    one.Words = "rendezvous with " + name + " and bring " + Kerbal(one.Target) + " aboard";
                    return one;
                case "RecoverKerbal":
                    one.Return = true;
                    one.Words = "bring " + Kerbal(one.Target) + " home";
                    return one;
                case "CrewTraitParameter":
                    one.Engineer = true;
                    one.Words = "carry an Engineer to " + name;
                    return one;
                case "ConstructionParameter":
                    one.Rendezvous = true;
                    one.Manual = "attach " + Part(one.Target) + " to " + name + " on EVA";
                    one.Words = "rendezvous with " + name + " and attach " + Part(one.Target);
                    return one;
                case "RepairPartParameter":
                    one.Rendezvous = true;
                    one.Manual = "repair " + Part(one.Target) + " on " + name + " on EVA";
                    one.Words = "rendezvous with " + name + " and repair it";
                    return one;
                default:
                    if (one.Target.Kind != Comet) return ContractKinds.NotYet(one, "not yet: " + kind + " isn't a Contract Requirement Claudia can plan yet");
                    one.Rendezvous = true;
                    one.Manual = "take a sample from the comet on EVA";
                    one.Words = "rendezvous with " + name + " and take a sample from it";
                    return one;
            }
        }

        /// <summary>
        /// The Contract check's read of where <paramref name="one"/>'s vessel is: the vessel the Adapter tagged with the Contract's GUID in
        /// <paramref name="snapshot"/>'s <see cref="GameSnapshot.ContractVessels"/> (ADR 0013's amendment: by the game's identity, so the
        /// name-search skips don't apply). An orbiting vessel is a rendezvous in orbit round its body, its orbit what the rendezvous is priced
        /// to; a landed one (a rover) a landing beside it away from home. "not yet" when the vessel isn't there, a rescue is on the ground,
        /// or a comet is in solar orbit (or round a body the planner doesn't know).
        /// </summary>
        public static void Locate(ReadRequirement one, GameSnapshot snapshot, string home)
        {
            var target = one.Target;
            var vessel = target.Vessel = snapshot.ContractVessels.FirstOrDefault(v => v.ContractGuid == target.Guid);
            if (vessel == null)
            {
                one.NotYet = target.Kind == Comet ? CometNotYet : NotFoundNotYet;
                return;
            }
            Rename(one);
            var body = snapshot.Bodies.FirstOrDefault(b => b.Name == vessel.Body);
            var down = vessel.IsDown;
            if (target.Kind == Rescue && down)
            {
                one.NotYet = GroundRescueNotYet;
                return;
            }
            if (body == null || body.Name == "Sun" || (!down && (!vessel.PeriapsisM.HasValue || !vessel.ApoapsisM.HasValue)))
            {
                one.NotYet = target.Kind == Comet ? CometNotYet : "not yet: the Contract's vessel is round a body Claudia doesn't know";
                return;
            }
            if (down && body.Name == home)
            {
                one.NotYet = HomeLandedNotYet;
                return;
            }
            one.Body = body.Name;
            one.Reach = down ? ContractReach.Land : ContractReach.Orbit;
            one.Phase = one.Return ? "Return" : body.Name == home ? home + " orbit" : body.Name;
            if (down && one.Manual.Length > 0) one.Manual = "drive to " + target.Name + " and " + one.Manual;
            if (!down && one.Rendezvous)
            {
                var pe = vessel.PeriapsisM.Value + body.Radius;
                var ap = vessel.ApoapsisM.Value + body.Radius;
                one.RendezvousOrbit = new ContractOrbit
                {
                    Key = one.Requirement.Key, Body = body.Name, Sma = (pe + ap) / 2, Eccentricity = (ap - pe) / (ap + pe), Inclination = vessel.Inclination ?? 0,
                };
            }
        }

        /// <summary>The words again, now the vessel's own name is known.</summary>
        private static void Rename(ReadRequirement one)
        {
            var read = Describe(new ReadRequirement { Requirement = one.Requirement, Target = one.Target }, one.Requirement.Kind);
            if (read.Words.Length > 0) one.Words = read.Words;
            if (read.Manual.Length > 0) one.Manual = read.Manual;
        }

        /// <summary>"this craft can't: …" for what the Requirement needs aboard that the craft lacks; "" when it has it.</summary>
        public static string Lacks(GameSnapshot snapshot, ReadRequirement one)
        {
            var parts = snapshot.Craft.Parts;
            var target = one.Target;
            if (target.Kind == Rescue && one.Rendezvous && snapshot.FreeSeats == 0) return "this craft can't: no empty seat for " + Kerbal(target);
            if (one.Engineer && !snapshot.Crew.Any(c => string.Equals(c.Trait, "Engineer", StringComparison.OrdinalIgnoreCase))) return NoEngineer;
            if (!one.Rendezvous) return "";
            if (target.Kind == Repair && !Has(parts, RepairKitPart)) return NoRepairKit;
            if ((target.Kind == OrbitalConstruction || target.Kind == RoverConstruction) && target.Part.Length > 0 && !Has(parts, target.Part))
                return "this craft can't: no " + target.Part + " aboard to attach";
            if (target.Kind == Comet && snapshot.Crew.Count == 0) return NoKerbalForSample;
            return "";
        }

        /// <summary>A part by internal name is on the craft or stowed in one of its inventories.</summary>
        private static bool Has(System.Collections.Generic.List<SnapshotPart> parts, string name)
        {
            return parts.Any(p => p.Name == name || p.Stowed.Contains(name));
        }

        private static string Kerbal(ContractTarget target)
        {
            return target.Kerbal.Length > 0 ? target.Kerbal : "the kerbal";
        }

        private static string Part(ContractTarget target)
        {
            return target.Part.Length > 0 ? target.Part : "the part";
        }

        /// <summary>A setting from the Requirement, else the Contract's own; "" when neither has one.</summary>
        private static string First(SnapshotContract contract, ContractRequirement requirement, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ContractKinds.Setting(requirement, name);
                if (value.Length == 0 && contract != null) value = ContractKinds.Setting(Root(contract), name);
                if (value.Length > 0) return value;
            }
            return "";
        }

        /// <summary>The Contract's own settings, read as a Requirement's.</summary>
        private static ContractRequirement Root(SnapshotContract contract)
        {
            return new ContractRequirement { Settings = contract.Settings };
        }

        private static System.Collections.Generic.IEnumerable<ContractRequirement> Leaves(SnapshotContract contract)
        {
            var stack = new System.Collections.Generic.Stack<ContractRequirement>(contract.Requirements);
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                if (r.Requirements.Count == 0) yield return r;
                foreach (var under in r.Requirements) stack.Push(under);
            }
        }
    }
}
