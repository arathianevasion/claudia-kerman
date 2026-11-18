// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1032: Contract Requirements met by hands-on work (ADR 0021: Core's kind table), each a Manual stretch Claudia plans. Claudia lands on
    /// the body as near the spot as she can; the player's Manual stretch does the rest: plants the flag on EVA (a plant-flag Contract, or an
    /// exploration Contract's flag), drives to a surface feature and scans it with the robotic arm (Breaking Ground), or walks to one and takes
    /// a sample on EVA (Breaking Ground). #1246: so is every other EVA-only Requirement: an exploration Contract's spacewalk (in orbit of
    /// its body) and surface EVA (after the landing), and a survey waypoint needing an EVA report or a surface sample (<see cref="ScienceKinds"/>).
    /// On Kerbin's ground they are "not yet", as ground surveys and bases there are.
    /// </summary>
    internal static class ManualKinds
    {
        public const string PlantFlag = "PlantFlag";
        public const string ArmScan = "ROCScienceArmContract";
        public const string Sample = "ROCScienceRetrievalContract";
        private const string ArmScanKind = "CollectROCScienceArm";
        private const string SampleKind = "CollectROCScienceRetrieval";

        /// <summary>The part tag the Adapter gives a robotic scanner arm (Breaking Ground's ModuleRobotArmScanner).</summary>
        public const string ArmTag = "arm_scanner";

        public const string HomeFlagNotYet = "not yet: planting a flag on Kerbin isn't planned yet";
        public const string HomeSurfaceNotYet = "not yet: surface features on Kerbin aren't planned yet";
        public const string HomeEvaNotYet = "not yet: an EVA on Kerbin's surface isn't planned yet";
        public const string NoArm = "this craft can't: no robotic scanner arm aboard";
        public const string NoKerbalForFlag = "this craft can't: planting a flag needs a kerbal aboard to go on EVA";
        public const string NoKerbalForSample = "this craft can't: a surface sample needs a kerbal aboard to go on EVA";

        /// <summary>#1246: the Manual stretch for a spacewalk milestone, and for a surface EVA one.</summary>
        public const string SpacewalkStretch = "take a spacewalk on EVA";
        public const string SurfaceEvaStretch = "step out onto the surface on EVA";

        public static bool Plans(string kind)
        {
            return kind == PlantFlag || kind == ArmScan || kind == Sample;
        }

        public static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            if (contract.Kind == PlantFlag && requirement.Kind == PlantFlag) return Flag(one, BodyOf(requirement), home);
            if (contract.Kind == ArmScan && requirement.Kind == ArmScanKind) return Surface(one, requirement, home, true);
            if (contract.Kind == Sample && requirement.Kind == SampleKind) return Surface(one, requirement, home, false);
            return ContractKinds.NotYet(one, "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet");
        }

        /// <summary>A flag planted on <paramref name="body"/>: a landing there, then the player's EVA. Also an exploration Contract's FLAGPLANT.</summary>
        public static ReadRequirement Flag(ReadRequirement one, string body, string home)
        {
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (body == home) return ContractKinds.NotYet(one, HomeFlagNotYet);
            one.Manual = "plant a flag on EVA";
            return ContractKinds.Visit(one, body, ContractReach.Land, "plant a flag on " + ContractKinds.Named(body), home);
        }

        /// <summary>#1246: an exploration Contract's spacewalk on <paramref name="body"/>: an orbit of it (Kerbin's too), then the player's EVA.</summary>
        public static ReadRequirement Spacewalk(ReadRequirement one, string body, string home)
        {
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            one.Manual = SpacewalkStretch;
            return ContractKinds.Visit(one, body, ContractReach.Orbit, "take a spacewalk in orbit round " + ContractKinds.Named(body), home);
        }

        /// <summary>#1246: an exploration Contract's surface EVA on <paramref name="body"/>: a landing there, then the player's EVA.</summary>
        public static ReadRequirement SurfaceWalk(ReadRequirement one, string body, string home)
        {
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (body == home) return ContractKinds.NotYet(one, HomeEvaNotYet);
            one.Manual = SurfaceEvaStretch;
            return ContractKinds.Visit(one, body, ContractReach.Land, "step out onto the surface of " + ContractKinds.Named(body) + " on EVA", home);
        }

        private static ReadRequirement Surface(ReadRequirement one, ContractRequirement requirement, string home, bool arm)
        {
            var body = BodyOf(requirement);
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (body == home) return ContractKinds.NotYet(one, HomeSurfaceNotYet);
            var feature = Feature(requirement);
            one.Manual = arm ? "drive to the " + feature + " and scan it with the robotic arm" : "walk to the " + feature + " and take a sample of it on EVA";
            one.ArmScan = arm;
            return ContractKinds.Visit(one, body, ContractReach.Land,
                (arm ? "scan a " + feature + " with a robotic arm on " : "take a sample of a " + feature + " on ") + ContractKinds.Named(body), home);
        }

        /// <summary>
        /// "this craft can't: …" for what the Manual stretch needs that the craft lacks: a kerbal to go on EVA for a flag, a sample, a
        /// spacewalk, a surface EVA or an EVA report, the robotic scanner arm for a scan; "" when it has it.
        /// </summary>
        public static string Lacks(GameSnapshot snapshot, ReadRequirement read)
        {
            if (read.ArmScan) return snapshot.Craft.Parts.Any(p => p.Tags.Contains(ArmTag)) ? "" : NoArm;
            if (snapshot.Crew.Count > 0) return "";
            if (read.Manual.StartsWith("plant", System.StringComparison.Ordinal)) return NoKerbalForFlag;
            if (read.Manual == SpacewalkStretch) return NoKerbal("a spacewalk");
            if (read.Manual == SurfaceEvaStretch) return NoKerbal("a surface EVA");
            return read.Manual.Contains("EVA report") ? NoKerbal("an EVA report") : NoKerbalForSample;
        }

        private static string NoKerbal(string what)
        {
            return "this craft can't: " + what + " needs a kerbal aboard to go on EVA";
        }

        /// <summary>The surface feature the Contract names, in words: its ROC type as the save gives it, else "surface feature".</summary>
        private static string Feature(ContractRequirement requirement)
        {
            var named = new[] { "rocType", "ROCType", "rocName", "type" }.Select(n => ContractKinds.Setting(requirement, n)).FirstOrDefault(v => v.Length > 0);
            return named == null ? "surface feature" : named;
        }

        private static string BodyOf(ContractRequirement requirement)
        {
            var index = new[] { "targetBody", "body", "TargetBody" }.Select(n => ContractKinds.Setting(requirement, n)).FirstOrDefault(v => v.Length > 0);
            // A saved setting names the body by its index in the game's body list.
            return index == null ? null : ContractKinds.BodyAt(index);
        }
    }
}
