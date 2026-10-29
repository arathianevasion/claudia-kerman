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
                Debug.Log(ModInfo.LogTag + " removing the maneuver node '" + label + "' left at UT " + node.UT.ToString("F1") +
                          ", " + node.DeltaV.magnitude.ToString("F1") + " m/s (" + outcome.Kind + ")");
                try { vessel.patchedConicSolver.RemoveManeuverNode(node); }
                catch (System.Exception e) { Debug.LogWarning(ModInfo.LogTag + " couldn't remove the maneuver node: " + e.Message); }
                AddedBy.Remove(node);
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
