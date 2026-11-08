// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The snapshot's part tree seen from the command part the Mission flies from (#909), not from its root: a craft rooted at a tank or service
    /// module, or a docked pair, has the pod somewhere below the root. "Parent" is the next part toward home, so what hangs below a part is what
    /// is on its far side from home. With no home (or one not aboard) the root is home, which is the tree as stored.
    /// </summary>
    public static class PartTree
    {
        /// <summary>Each part's id to the id of its neighbour toward <paramref name="home"/> (null for home itself); parts not connected to home keep their stored parent.</summary>
        public static Dictionary<uint, uint?> TowardHome(IList<SnapshotPart> parts, uint? home)
        {
            var toward = parts.ToDictionary(p => p.Id, p => p.ParentId);
            var byId = parts.ToDictionary(p => p.Id);
            if (!home.HasValue || !byId.ContainsKey(home.Value) || !toward[home.Value].HasValue) return toward;
            var edges = Edges(parts, byId);
            toward[home.Value] = null;
            var queue = new Queue<uint>();
            queue.Enqueue(home.Value);
            var seen = new HashSet<uint> { home.Value };
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var next in edges[id])
                    if (seen.Add(next))
                    {
                        toward[next] = id;
                        queue.Enqueue(next);
                    }
            }
            return toward;
        }

        /// <summary>
        /// The parts on the Mission's own craft: those reached from <paramref name="home"/> along any attachment without crossing a docking (see Docked:
        /// a port used as a structural joint is crossed; the port on the near side of a docking is reached, what is docked beyond it is another vessel's). All of <paramref name="parts"/> with no home or one not aboard.
        /// </summary>
        public static List<SnapshotPart> OwnSide(IList<SnapshotPart> parts, uint? home)
        {
            var byId = parts.ToDictionary(p => p.Id);
            if (!home.HasValue || !byId.ContainsKey(home.Value)) return parts.ToList();
            var edges = Edges(parts, byId);
            var seen = new HashSet<uint> { home.Value };
            var queue = new Queue<uint>();
            queue.Enqueue(home.Value);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var next in edges[id])
                    if (!Docked(byId[id], byId[next]) && seen.Add(next)) queue.Enqueue(next);
            }
            return parts.Where(p => seen.Contains(p.Id)).ToList();
        }

        /// <summary>
        /// Whether the attachment between two parts is a docking, not a structural joint: two docking ports joined to each other, one of which
        /// the adapter read Docked to another vessel (PortState). With no state read at all (an old snapshot) two joined ports count. A PreAttached pair
        /// (a joint built in the VAB) and a port beside non-port parts are not one.
        /// </summary>
        private static bool Docked(SnapshotPart a, SnapshotPart b)
        {
            if (!a.Tags.Contains("docking_port") || !b.Tags.Contains("docking_port")) return false;
            if (a.PortState.Length == 0 && b.PortState.Length == 0) return true;
            return DockedState(a.PortState) || DockedState(b.PortState);
        }

        /// <summary>A port docked to another vessel: not PreAttached, and not "Docked (same vessel)", which is the craft's own.</summary>
        private static bool DockedState(string state)
        {
            return state.StartsWith("Docked") && !state.Contains("same vessel");
        }

        /// <summary>The parts hanging below <paramref name="id"/> (its far side from home, by <paramref name="toward"/>), itself included.</summary>
        public static HashSet<uint> Below(uint id, IList<SnapshotPart> parts, Dictionary<uint, uint?> toward)
        {
            var gone = new HashSet<uint> { id };
            var grew = true;
            while (grew)
            {
                grew = false;
                foreach (var p in parts)
                    if (toward[p.Id].HasValue && gone.Contains(toward[p.Id].Value) && gone.Add(p.Id)) grew = true;
            }
            return gone;
        }

        private static Dictionary<uint, List<uint>> Edges(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> byId)
        {
            var edges = parts.ToDictionary(p => p.Id, p => new List<uint>());
            foreach (var p in parts)
                if (p.ParentId.HasValue && byId.ContainsKey(p.ParentId.Value))
                {
                    edges[p.Id].Add(p.ParentId.Value);
                    edges[p.ParentId.Value].Add(p.Id);
                }
            return edges;
        }
    }
}
