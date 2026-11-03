// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using UnityEngine;

namespace ClaudiaKermin.Adapter
{
    /// <summary>
    /// The craft's maneuver nodes on KSP's patched-conic solver, for the run lifecycle (#522): which were there when a run started,
    /// which the run added, and removing the ones a dead run left behind. Remembers, for this game session, which run added a node.
    /// Main thread only.
    /// </summary>
    public static class ManeuverPlan
    {
        /// <summary>The run that added each node, by KSP's own node object; forgotten for a node the solver no longer holds.</summary>
        private static readonly Dictionary<ManeuverNode, string> AddedBy = new Dictionary<ManeuverNode, string>();

        /// <summary>The craft's nodes now, in plan order; empty when it is not loaded or has no solver.</summary>
        public static List<ManeuverNode> Nodes(Vessel vessel)
        {
            if (vessel == null || !vessel.loaded || vessel.patchedConicSolver == null) return new List<ManeuverNode>();
            return vessel.patchedConicSolver.maneuverNodes.ToList();
        }

        /// <summary>
        /// A run has ended: remembers its label against the nodes it added, then, if it ended in a way that leaves a mess
        /// (<see cref="NodeCleanup"/>) and the craft is still loaded, removes those nodes, logging each.
        /// </summary>
        public static void RunEnded(Vessel vessel, IList<ManeuverNode> atStart, string label, RunOutcome outcome)
        {
            if (vessel == null || !vessel.loaded || vessel.patchedConicSolver == null || atStart == null) return;
            var now = Nodes(vessel);
            var before = new HashSet<ManeuverNode>(atStart);
            foreach (var node in now)
                if (!before.Contains(node)) AddedBy[node] = label;
            foreach (var node in NodeCleanup.ToRemove(atStart, now, outcome))
            {
                Pending.Add(new Leftover { Vessel = vessel, Node = node, Label = label, Kind = outcome.Kind });
            }
            RemovePending();
        }

        private sealed class Leftover
        {
            public Vessel Vessel;
            public ManeuverNode Node;
            public string Label;
            public RunOutcomeKind Kind;
        }

        /// <summary>Nodes due for removal, waiting for their craft to unpack (#695).</summary>
        private static readonly List<Leftover> Pending = new List<Leftover>();

        /// <summary>
        /// Removes the pending leftover nodes whose craft is loaded and unpacked, the way KSP's UI does (<c>RemoveSelf</c>, which also
        /// detaches the gizmo and map icon). A packed craft's nodes wait; an unloaded craft's are dropped. Call each frame, main thread.
        /// </summary>
        public static void RemovePending()
        {
            if (Pending.Count == 0) return;
            foreach (var item in Pending.ToList())
            {
                var vessel = item.Vessel;
                var loaded = vessel != null && vessel.loaded && vessel.patchedConicSolver != null;
                var decision = NodeCleanup.WhenToRemove(loaded, loaded && vessel.packed);
                if (decision == NodeCleanup.Removal.Wait) continue;
                Pending.Remove(item);
                if (decision == NodeCleanup.Removal.Drop)
                {
                    Debug.Log(ModInfo.LogTag + " the craft was unloaded before the maneuver node '" + item.Label + "' could be removed");
                    AddedBy.Remove(item.Node);
                    continue;
                }
                // The player may have removed it meanwhile.
                if (!vessel.patchedConicSolver.maneuverNodes.Contains(item.Node)) { AddedBy.Remove(item.Node); continue; }
                Debug.Log(ModInfo.LogTag + " removing the maneuver node '" + item.Label + "' left at UT " + item.Node.UT.ToString("F1") +
                          ", " + item.Node.DeltaV.magnitude.ToString("F1") + " m/s (" + item.Kind + ")");
                try { item.Node.RemoveSelf(); }
                catch (System.Exception e) { Debug.LogWarning(ModInfo.LogTag + " couldn't remove the maneuver node: " + e.Message); }
                AddedBy.Remove(item.Node);
            }
        }

        /// <summary>The craft's planned nodes as the Game Snapshot lists them.</summary>
        public static List<SnapshotNode> ForSnapshot(Vessel vessel)
        {
            var nodes = Nodes(vessel);
            foreach (var gone in AddedBy.Keys.Where(k => !nodes.Contains(k)).ToList()) AddedBy.Remove(gone);
            return nodes.OrderBy(n => n.UT).Select(n =>
            {
                string label;
                return new SnapshotNode
                {
                    Ut = n.UT,
                    DeltaV = n.DeltaV.magnitude,
                    // KSP's node delta-v: x radial, y normal, z prograde.
                    Radial = n.DeltaV.x,
                    Normal = n.DeltaV.y,
                    Prograde = n.DeltaV.z,
                    AddedBy = AddedBy.TryGetValue(n, out label) ? label : "",
                };
            }).ToList();
        }
    }
}
