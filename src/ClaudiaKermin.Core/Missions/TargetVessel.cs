// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

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
    }

    /// <summary>A vessel in the game other than the craft, which a Mission Goal may name (not in the snapshot's JSON).</summary>
    public sealed class SnapshotVessel
    {
        public uint Id;
        public string Name = "";
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

        /// <summary>
        /// The Mission's Target Vessel when it starts: KSP's target, or else the one vessel the Mission Goal names (the longest
        /// name found, whole words, any case). Null for none; <paramref name="note"/> says how it was found, or why a name the goal
        /// gives stands for no one vessel.
        /// </summary>
        public static TargetVessel Choose(string goal, GameSnapshot snapshot, out string note)
        {
            note = "";
            if (snapshot == null) return null;
            if (snapshot.KspTarget != null)
            {
                note = "KSP's target";
                return new TargetVessel(snapshot.KspTarget.Id, snapshot.KspTarget.Name);
            }
            var named = snapshot.Vessels.Where(v => v.Name.Trim().Length >= ShortestName && Names(goal ?? "", v.Name.Trim())).ToList();
            if (named.Count == 0) return null;
            var longest = named.Max(v => v.Name.Trim().Length);
            var chosen = named.Where(v => v.Name.Trim().Length == longest).GroupBy(v => v.Id).ToList();
            if (chosen.Count > 1)
            {
                note = "several vessels are named \"" + chosen[0].First().Name.Trim() + "\"; set the one you mean as KSP's target and start again";
                return null;
            }
            note = "named in the Mission Goal";
            return new TargetVessel(chosen[0].Key, chosen[0].First().Name);
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
