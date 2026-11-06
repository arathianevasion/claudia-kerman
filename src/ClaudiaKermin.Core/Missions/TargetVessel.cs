// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Target Vessel as the Game Snapshot carries it (ADR 0013): read live by its <c>persistentId</c>, only while the Mission
    /// has one, so other Missions pay nothing for it.
    /// </summary>
    public sealed class SnapshotTarget
    {
        /// <summary>The vessel's <c>persistentId</c>; only the recording JSON carries it.</summary>
        public uint Id;
        public string Name = "";
        /// <summary>The body it is at.</summary>
        public string Body = "";
        /// <summary>KSP's situation name, e.g. ORBITING, LANDED.</summary>
        public string Situation = "";
        /// <summary>Its orbit at the snapshot time; null when not read.</summary>
        public SnapshotOrbit Orbit;
        /// <summary>Free docking ports by size (the port's node type, e.g. size1): how many.</summary>
        public SortedDictionary<string, int> FreePorts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        /// <summary>The name tags of its free ports, each with the port's size.</summary>
        public SortedDictionary<string, string> PortTags = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Where it is on <see cref="Body"/>; null when not read. The entry carries it only when <see cref="IsDown"/> (#662).</summary>
        public SnapshotPosition Position;

        /// <summary>Whether it is landed or splashed, so its position, not its orbit, says where it is (#662).</summary>
        public bool IsDown
        {
            get { return Situation == "LANDED" || Situation == "SPLASHED"; }
        }
    }

    /// <summary>A vessel's place on its body: latitude and longitude in degrees, altitude in m above sea level.</summary>
    public sealed class SnapshotPosition
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
    }

    /// <summary>A vessel in the game other than the craft, which a Mission Goal may name or the player pick (not in the snapshot's JSON).</summary>
    public sealed class SnapshotVessel
    {
        public uint Id;
        public string Name = "";
        /// <summary>The body it is at; "" when not read.</summary>
        public string Body = "";
        /// <summary>KSP's situation name, e.g. ORBITING, LANDED; "" when not read.</summary>
        public string Situation = "";
    }

    /// <summary>The start form's Target Vessel setting: Auto (the mod's choice), Clear (none), or a vessel the player chose.</summary>
    public enum TargetPickKind
    {
        Auto,
        Cleared,
        Vessel,
    }

    /// <summary>The player's Target Vessel pick in the start form (#643). It lives only in the form: never a saved default (#639).</summary>
    public sealed class TargetPick
    {
        public static readonly TargetPick Auto = new TargetPick(TargetPickKind.Auto, 0, "");
        public static readonly TargetPick Cleared = new TargetPick(TargetPickKind.Cleared, 0, "");

        public readonly TargetPickKind Kind;
        public readonly uint Id;
        public readonly string Name;

        private TargetPick(TargetPickKind kind, uint id, string name)
        {
            Kind = kind;
            Id = id;
            Name = name ?? "";
        }

        public static TargetPick Of(SnapshotVessel vessel)
        {
            return new TargetPick(TargetPickKind.Vessel, vessel.Id, vessel.Name);
        }
    }

    /// <summary>
    /// What the Target Vessel is chosen from: the craft's name and body, KSP's target and the other vessels in the game. Built
    /// from a Game Snapshot, so the start form and Start choose from the same facts.
    /// </summary>
    public sealed class TargetFacts
    {
        public string CraftName = "";
        public string CraftBody = "";
        /// <summary>KSP's target as the snapshot read it; null for none.</summary>
        public SnapshotVessel KspTarget;
        public IList<SnapshotVessel> Vessels = new List<SnapshotVessel>();

        public static TargetFacts Of(GameSnapshot snapshot)
        {
            if (snapshot == null) return new TargetFacts();
            var ksp = snapshot.KspTarget;
            return new TargetFacts
            {
                CraftName = snapshot.Craft.Name ?? "",
                CraftBody = snapshot.Body ?? "",
                KspTarget = ksp == null ? null : new SnapshotVessel { Id = ksp.Id, Name = ksp.Name ?? "", Body = ksp.Body ?? "", Situation = ksp.Situation ?? "" },
                Vessels = snapshot.Vessels.ToList(),
            };
        }
    }

    /// <summary>The Target Vessel a choice came to, or why there is none, and the start form's line for it.</summary>
    public sealed class TargetChoice
    {
        /// <summary>The Mission Goal needs a target (it asks for rendezvous or docking, or names a vessel): only then is there a line.</summary>
        public bool Needed;
        /// <summary>The target; null for none.</summary>
        public TargetVessel Target;
        /// <summary>How the target was found ("your pick", "named in the Mission Goal", "KSP's target"), or why there is none.</summary>
        public string Reason = "";
        /// <summary>The target's entry (name, body, situation); null for none.</summary>
        public SnapshotVessel Vessel;
        /// <summary>The goal asks for rendezvous or docking and the target is on the ground (mod text, not a Craft Check rule); "" otherwise.</summary>
        public string Note = "";

        /// <summary>The start form's line under the Mission Goal; "" when the goal needs no target.</summary>
        public string Line
        {
            get
            {
                if (!Needed) return "";
                if (Target == null) return "Target Vessel: none (" + Reason + ")";
                return "Target Vessel: " + TargetVessel.Describe(Vessel) + " (" + Reason + ")" + (Note.Length > 0 ? "; " + Note : "");
            }
        }

        /// <summary>The Mission log's line at Start, with the same reason as the form's line; "" when the goal needs no target.</summary>
        public string LogLine
        {
            get
            {
                if (!Needed) return "";
                return Target != null ? "Target Vessel: " + Target.Name + " (" + Reason + ")." : "No Target Vessel: " + Reason + ".";
            }
        }
    }

    /// <summary>The one other vessel a Mission rendezvouses or docks with (ADR 0013): kept by <c>persistentId</c> and name.</summary>
    public sealed class TargetVessel
    {
        public readonly uint Id;
        public readonly string Name;

        public TargetVessel(uint id, string name)
        {
            Id = id;
            Name = name ?? "";
        }

        /// <summary>Shorter names than this are never matched in a Mission Goal: "A" would match the word.</summary>
        private const int ShortestName = 3;

        private static readonly Regex Rendezvous = new Regex(@"\brendez-?vous(es|ed|ing)?\b", RegexOptions.IgnoreCase);

        /// <summary>The goal asks for rendezvous or docking.</summary>
        public static bool AsksForRendezvousOrDocking(string goal)
        {
            goal = goal ?? "";
            return Rendezvous.IsMatch(goal) || SequenceWords.Docking.IsMatch(goal);
        }

        /// <summary>
        /// The Target Vessel for this goal (ADR 0013, #643): the one choice the start form shows and Start hands the Mission. Only a
        /// goal that asks for rendezvous or docking, or names a vessel, has one. Then the player's pick comes first, else the one
        /// vessel the goal names (the longest name found, whole words, any case), else KSP's target. A vessel named as the craft is
        /// (an earlier flight of it), or whose name another vessel shares (kOS reads the target by name, #655), is never chosen.
        /// </summary>
        public static TargetChoice Choose(string goal, TargetPick pick, TargetFacts facts)
        {
            goal = goal ?? "";
            pick = pick ?? TargetPick.Auto;
            facts = facts ?? new TargetFacts();
            var craft = facts.CraftName;
            var named = facts.Vessels.Where(v => v.Name.Trim().Length >= ShortestName && Names(goal, v.Name.Trim())).ToList();
            // The craft's own name ("fly Mun Lander to the Mun") asks for no target, so KSP's target can't slip in on it.
            var choice = new TargetChoice { Needed = AsksForRendezvousOrDocking(goal) || named.Any(v => !Same(v.Name, craft)) };
            if (!choice.Needed) return choice;
            var options = Options(facts);

            if (pick.Kind == TargetPickKind.Cleared) return None(choice, "cleared");
            if (pick.Kind == TargetPickKind.Vessel)
            {
                var picked = options.FirstOrDefault(v => v.Id == pick.Id);
                return picked != null ? Chosen(choice, picked, "your pick", goal)
                    : None(choice, "\"" + pick.Name.Trim() + "\", your pick, is no longer in the list");
            }

            var others = named.Where(v => !Same(v.Name, craft)).ToList();
            if (others.Count > 0)
            {
                var longest = others.Max(v => v.Name.Trim().Length);
                var vessel = others.First(v => v.Name.Trim().Length == longest);
                var option = options.FirstOrDefault(v => v.Id == vessel.Id);
                return option != null ? Chosen(choice, option, "named in the Mission Goal", goal) : None(choice, Shared(vessel));
            }

            var ksp = facts.KspTarget;
            if (ksp != null)
            {
                if (Same(ksp.Name, craft)) return None(choice, "KSP's target \"" + ksp.Name.Trim() + "\" is the craft's own name");
                if (facts.Vessels.Any(v => v.Id != ksp.Id && Same(v.Name, ksp.Name))) return None(choice, Shared(ksp));
                return Chosen(choice, ksp, "KSP's target", goal);
            }
            return None(choice, named.Count > 0 ? "\"" + named[0].Name.Trim() + "\" is the craft's own name" : "no vessel matches");
        }

        /// <summary>
        /// The vessels the player may pick (#643), for the start form's list: not one named as the craft is, nor one whose name another
        /// vessel shares (the adapter leaves out the craft, debris, flags, asteroids and Kerbals on EVA). Those at the craft's own body
        /// come first, then by name.
        /// </summary>
        public static IList<SnapshotVessel> Options(TargetFacts facts)
        {
            if (facts == null) return new List<SnapshotVessel>();
            var vessels = facts.Vessels.GroupBy(v => v.Id).Select(g => g.First()).ToList();
            return vessels
                .Where(v => v.Name.Trim().Length > 0 && !Same(v.Name, facts.CraftName) && !vessels.Any(o => o.Id != v.Id && Same(o.Name, v.Name)))
                .OrderBy(v => string.Equals(v.Body, facts.CraftBody, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.Id)
                .ToList();
        }

        /// <summary>A list entry or the line's target: name · body · situation.</summary>
        public static string Describe(SnapshotVessel vessel)
        {
            var parts = new List<string> { vessel.Name.Trim() };
            if (vessel.Body.Length > 0) parts.Add(vessel.Body);
            if (vessel.Situation.Length > 0) parts.Add(vessel.Situation.ToLowerInvariant().Replace('_', '-'));
            return string.Join(" · ", parts.ToArray());
        }

        private static TargetChoice Chosen(TargetChoice choice, SnapshotVessel vessel, string how, string goal)
        {
            choice.Target = new TargetVessel(vessel.Id, vessel.Name);
            choice.Vessel = vessel;
            choice.Reason = how;
            var grounded = vessel.Situation == "LANDED" || vessel.Situation == "SPLASHED" || vessel.Situation == "PRELAUNCH";
            if (grounded && AsksForRendezvousOrDocking(goal)) choice.Note = "landed: rendezvous and docking need it in orbit";
            return choice;
        }

        private static TargetChoice None(TargetChoice choice, string why)
        {
            choice.Reason = why;
            return choice;
        }

        private static string Shared(SnapshotVessel vessel)
        {
            return "several vessels are named \"" + vessel.Name.Trim() + "\"";
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The goal holds the name as whole words.</summary>
        private static bool Names(string goal, string name)
        {
            for (var at = goal.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = goal.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = at + name.Length;
                if ((at == 0 || !char.IsLetterOrDigit(goal[at - 1])) && (end == goal.Length || !char.IsLetterOrDigit(goal[end]))) return true;
            }
            return false;
        }
    }
}
