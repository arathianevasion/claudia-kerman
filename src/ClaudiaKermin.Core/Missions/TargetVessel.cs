// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>Its distance from the craft in m (#709); null when not read (the editor has no craft in the world).</summary>
        public double? Distance;
        /// <summary>Its saved orbit's periapsis altitude in m (#710); null when landed, splashed or not read.</summary>
        public double? PeriapsisM;
        /// <summary>Its saved orbit's apoapsis altitude in m (#710); null when landed, splashed, on an escape orbit or not read.</summary>
        public double? ApoapsisM;
        /// <summary>Where a landed or splashed vessel is saved (#710); null when not read.</summary>
        public SnapshotPosition Position;

        /// <summary>Whether it is landed or splashed, so its place, not its orbit, tells it apart (#710).</summary>
        public bool IsDown
        {
            get { return Situation == "LANDED" || Situation == "SPLASHED"; }
        }
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
        /// <summary>The craft's <c>persistentId</c>, never a choice; 0 when not read.</summary>
        public uint CraftId;
        /// <summary>KSP's target as the snapshot read it; null for none.</summary>
        public SnapshotVessel KspTarget;
        public IList<SnapshotVessel> Vessels = new List<SnapshotVessel>();

        public static TargetFacts Of(GameSnapshot snapshot)
        {
            if (snapshot == null) return new TargetFacts();
            var ksp = snapshot.KspTarget;
            // KSP's target as the game's vessels list it, with its distance, when it is there.
            var listed = ksp == null ? null : snapshot.Vessels.FirstOrDefault(v => v.Id == ksp.Id);
            return new TargetFacts
            {
                CraftName = snapshot.Craft.Name ?? "",
                CraftBody = snapshot.Body ?? "",
                CraftId = snapshot.CraftPersistentId,
                KspTarget = ksp == null ? null
                    : listed ?? new SnapshotVessel { Id = ksp.Id, Name = ksp.Name ?? "", Body = ksp.Body ?? "", Situation = ksp.Situation ?? "" },
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
        /// <summary>Another listed vessel shares the target's name, so the line adds its distance (#709).</summary>
        internal bool SharedName;

        /// <summary>The start form's line under the Mission Goal; "" when the goal needs no target.</summary>
        public string Line
        {
            get
            {
                if (!Needed) return "";
                if (Target == null) return "Target Vessel: none (" + Reason + ")";
                return "Target Vessel: " + TargetVessel.Describe(Vessel, SharedName) + " (" + Reason + ")" + (Note.Length > 0 ? "; " + Note : "");
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
        /// The Target Vessel for this goal (ADR 0013, #643, #709): the one choice the start form shows and Start hands the Mission.
        /// Only a goal that asks for rendezvous or docking, or names a vessel other than by the craft's own name, has one. Then the
        /// player's pick comes first, else the vessel the goal names (the longest name found, whole words, any case; a name other
        /// than the craft's before the craft's), else KSP's target. Auto never guesses: several vessels of the name the goal gives,
        /// or only near-misses (<see cref="NearMiss"/>), choose none and ask the player to Choose, those vessels heading the list.
        /// </summary>
        public static TargetChoice Choose(string goal, TargetPick pick, TargetFacts facts)
        {
            goal = goal ?? "";
            pick = pick ?? TargetPick.Auto;
            facts = facts ?? new TargetFacts();
            var ranked = Ranked(goal, facts);
            var options = ranked.Select(r => r.Vessel).ToList();
            // The craft's own name ("fly Mun Lander to the Mun") asks for no target, so KSP's target can't slip in on it.
            var choice = new TargetChoice
            {
                Needed = AsksForRendezvousOrDocking(goal) || ranked.Any(r => r.Exact && !Same(r.Vessel.Name, facts.CraftName)),
            };
            if (!choice.Needed) return choice;

            if (pick.Kind == TargetPickKind.Cleared) return None(choice, "cleared");
            if (pick.Kind == TargetPickKind.Vessel)
            {
                var picked = options.FirstOrDefault(v => v.Id == pick.Id);
                return picked != null ? Chosen(choice, picked, "your pick", goal, options)
                    : None(choice, "\"" + pick.Name.Trim() + "\", your pick, is no longer in the list");
            }

            var named = ranked.Where(r => r.Rank == RankNamed).ToList();
            if (named.Count == 1) return Chosen(choice, named[0].Vessel, "named in the Mission Goal", goal, options);
            if (named.Count > 1) return None(choice, "several vessels are named \"" + named[0].Vessel.Name.Trim() + "\": Choose one");
            var near = ranked.FirstOrDefault(r => r.Rank == RankNear);
            if (near != null)
                return None(choice, "no vessel is named \"" + near.Phrase + "\": did you mean " + near.Vessel.Name.Trim() + "? Choose one");

            // KSP's target is one vessel, so a shared name doesn't matter; it is never the craft itself.
            var ksp = facts.KspTarget;
            if (ksp != null && (facts.CraftId == 0 || ksp.Id != facts.CraftId)) return Chosen(choice, ksp, "KSP's target", goal, options);
            return None(choice, "no vessel matches");
        }

        /// <summary>
        /// The vessels the player may pick (#643, #709), for the start form's and the Mission window's list: every one the adapter
        /// read (it leaves out the craft, debris, flags, asteroids and Kerbals on EVA), shared names and the craft's name included,
        /// never the craft itself. The vessels the goal names come first (the name <see cref="Choose"/> takes, then other
        /// whole-word names, then near-misses, fewest edits first), then the rest; within each, nearest first (unread distances
        /// last, the craft's own body first among them), then by name, then id.
        /// </summary>
        public static IList<SnapshotVessel> Options(TargetFacts facts, string goal = "")
        {
            return Ranked(goal ?? "", facts ?? new TargetFacts()).Select(r => r.Vessel).ToList();
        }

        /// <summary>
        /// A list entry or the line's target: name · body · situation, then its distance from the craft when another vessel in
        /// <paramref name="listed"/> shares its name, so the player can tell them apart (#709).
        /// </summary>
        public static string Describe(SnapshotVessel vessel, IList<SnapshotVessel> listed = null)
        {
            return Describe(vessel, listed != null && SharesName(vessel, listed));
        }

        /// <summary>Periapsis and apoapsis altitudes in m from a saved orbit's semi-major axis, eccentricity and its body's radius (#710). The apoapsis is null on an escape orbit.</summary>
        public static void OrbitAltitudes(double semiMajorAxis, double eccentricity, double bodyRadius, out double periapsis, out double? apoapsis)
        {
            periapsis = semiMajorAxis * (1 - eccentricity) - bodyRadius;
            apoapsis = eccentricity < 1 ? semiMajorAxis * (1 + eccentricity) - bodyRadius : (double?)null;
        }

        internal static string Describe(SnapshotVessel vessel, bool sharedName)
        {
            var withDistance = sharedName;
            var parts = new List<string> { vessel.Name.Trim() };
            if (vessel.Body.Length > 0) parts.Add(vessel.Body);
            if (vessel.Situation.Length > 0) parts.Add(vessel.Situation.ToLowerInvariant().Replace('_', '-'));
            if (withDistance && vessel.Distance.HasValue) parts.Add(Distance(vessel.Distance.Value));
            // No distance (the editor): tell same-named vessels apart by their saved orbit, or place if down (#710).
            else if (sharedName && vessel.IsDown && vessel.Position != null)
                parts.Add(Place(vessel.Position));
            else if (sharedName && !vessel.IsDown && vessel.PeriapsisM.HasValue && vessel.ApoapsisM.HasValue)
                parts.Add(Km(vessel.PeriapsisM.Value) + " × " + Km(vessel.ApoapsisM.Value) + " km");
            return string.Join(" · ", parts.ToArray());
        }

        private static string Km(double metres)
        {
            return Math.Round(metres / 1000).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Place(SnapshotPosition position)
        {
            var invariant = CultureInfo.InvariantCulture;
            return position.Latitude.ToString("0.0", invariant) + "°, " + position.Longitude.ToString("0.0", invariant) + "°";
        }

        /// <summary>Under 1 km in whole m, under 10 km to a tenth of a km, else in whole km.</summary>
        private static string Distance(double metres)
        {
            var invariant = CultureInfo.InvariantCulture;
            if (metres < 1000) return Math.Round(metres).ToString("0", invariant) + " m";
            var km = metres / 1000;
            return (km < 10 ? km.ToString("0.0", invariant) : km.ToString("#,0", invariant)) + " km";
        }

        private const int RankNamed = 0;
        private const int RankOtherName = 1;
        private const int RankNear = 2;
        private const int RankRest = 3;

        /// <summary>A pickable vessel and how the Mission Goal matches it.</summary>
        private sealed class RankedVessel
        {
            public SnapshotVessel Vessel;
            public int Rank = RankRest;
            /// <summary>The goal holds its name as whole words.</summary>
            public bool Exact;
            /// <summary>A near-miss's edits.</summary>
            public int Edits;
            /// <summary>The goal's words a near-miss came from, as typed.</summary>
            public string Phrase = "";
        }

        /// <summary>The pickable vessels, with how the goal matches each, in the list's order (<see cref="Options"/>).</summary>
        private static List<RankedVessel> Ranked(string goal, TargetFacts facts)
        {
            var ranked = facts.Vessels.GroupBy(v => v.Id).Select(g => g.First())
                .Where(v => v.Name.Trim().Length > 0 && (facts.CraftId == 0 || v.Id != facts.CraftId))
                .Select(v => new RankedVessel { Vessel = v })
                .ToList();
            foreach (var r in ranked)
            {
                var name = r.Vessel.Name.Trim();
                if (name.Length < ShortestName) continue;
                if (Names(goal, name))
                {
                    r.Exact = true;
                    r.Rank = RankOtherName;
                    continue;
                }
                var near = NearMiss(goal, name);
                if (near == null) continue;
                r.Rank = RankNear;
                r.Edits = near.Item1;
                r.Phrase = near.Item2;
            }
            // The name the goal gives: the longest found, one other than the craft's before the craft's.
            var exact = ranked.Where(r => r.Exact).ToList();
            var others = exact.Where(r => !Same(r.Vessel.Name, facts.CraftName)).ToList();
            var candidates = others.Count > 0 ? others : exact;
            if (candidates.Count > 0)
            {
                var longest = candidates.Max(r => r.Vessel.Name.Trim().Length);
                var given = candidates.First(r => r.Vessel.Name.Trim().Length == longest).Vessel.Name;
                foreach (var r in exact)
                    if (Same(r.Vessel.Name, given)) r.Rank = RankNamed;
            }
            return ranked
                .OrderBy(r => r.Rank)
                .ThenBy(r => r.Edits)
                .ThenBy(r => r.Vessel.Distance.HasValue ? 0 : string.Equals(r.Vessel.Body, facts.CraftBody, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(r => r.Vessel.Distance ?? 0)
                .ThenBy(r => r.Vessel.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Vessel.Id)
                .ToList();
        }

        private static readonly Regex Word = new Regex(@"[\p{L}\p{N}]+");

        /// <summary>
        /// A near-miss (#709): as many consecutive goal words as the name has, each starting with its name word's first letter
        /// (any case) and within the pair's allowance of edits (letters added, dropped or changed), set by the shorter of the two
        /// words: none under 4 letters, 1 for 4 or 5, 2 for 6 or more. So "Kerbal Station Alpha" near-misses "Kerbin Station
        /// Alpha", but "Relay Sat 2" never "Relay Sat 1", "over" never "Rover" and "orbit" never "Orbiter". Returns the closest such phrase's total edits and its text as typed;
        /// null for none. Names under <see cref="ShortestName"/> letters never near-miss.
        /// </summary>
        internal static Tuple<int, string> NearMiss(string goal, string name)
        {
            goal = goal ?? "";
            var want = Word.Matches(name ?? "").Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToList();
            var words = Word.Matches(goal).Cast<Match>().ToList();
            if (want.Count == 0 || (name ?? "").Trim().Length < ShortestName) return null;
            Tuple<int, string> best = null;
            for (var i = 0; i + want.Count <= words.Count; i++)
            {
                var edits = 0;
                for (var j = 0; j < want.Count && edits >= 0; j++)
                {
                    var word = words[i + j].Value.ToLowerInvariant();
                    var shorter = Math.Min(word.Length, want[j].Length);
                    var allowed = shorter < 4 ? 0 : shorter < 6 ? 1 : 2;
                    var d = word[0] == want[j][0] ? Edits(word, want[j]) : allowed + 1;
                    edits = d <= allowed ? edits + d : -1;
                }
                if (edits < 0 || (best != null && edits >= best.Item1)) continue;
                var from = words[i].Index;
                var last = words[i + want.Count - 1];
                best = Tuple.Create(edits, goal.Substring(from, last.Index + last.Length - from));
            }
            return best;
        }

        /// <summary>The Levenshtein distance: the letters added, dropped or changed to turn one word into the other.</summary>
        private static int Edits(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) row[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var diagonal = row[0];
                row[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var above = row[j];
                    row[j] = Math.Min(Math.Min(above + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }
            return row[b.Length];
        }

        private static TargetChoice Chosen(TargetChoice choice, SnapshotVessel vessel, string how, string goal, IList<SnapshotVessel> listed)
        {
            choice.Target = new TargetVessel(vessel.Id, vessel.Name);
            choice.Vessel = vessel;
            choice.SharedName = SharesName(vessel, listed);
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

        /// <summary>Another vessel in <paramref name="listed"/> has its name.</summary>
        private static bool SharesName(SnapshotVessel vessel, IList<SnapshotVessel> listed)
        {
            return listed.Any(o => o.Id != vessel.Id && Same(o.Name, vessel.Name));
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
