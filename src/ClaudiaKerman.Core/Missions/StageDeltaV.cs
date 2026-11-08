// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Vacuum delta-v per stage, worked from the snapshot's parts (#893): their masses, per-part resources (#870) and engines, so a craft that
    /// has lost parts (<see cref="EntryCraft"/>'s) has figures of its own, which stock's per-stage readout can't give. Stages fire from the
    /// craft's current one down to 0: each stage's decouplers drop what hangs below them, then the engines in that stage or an earlier one
    /// still aboard burn what their own tanks hold (fuel flows through the stack, not across a decoupler) until they run dry.
    /// </summary>
    public static class StageDeltaV
    {
        private const double G0 = 9.80665;
        private const double Empty = 1e-9;

        /// <summary>Delta-v (m/s) by stage number for <paramref name="parts"/> at stage <paramref name="current"/>; stages that burn nothing are 0.</summary>
        public static Dictionary<int, double> PerStage(IList<SnapshotPart> parts, int current)
        {
            var result = new Dictionary<int, double>();
            for (var n = current; n >= 0; n--) result[n] = 0;
            var all = parts.ToDictionary(p => p.Id);
            var propellants = new HashSet<string>(parts.Where(p => p.ThrustVacKn > 0).SelectMany(p => p.EnginePropellants));
            var fuel = parts.ToDictionary(p => p.Id, p => p.Resources.Where(r => propellants.Count == 0 ? r.Name != "ElectricCharge" : propellants.Contains(r.Name)).Sum(r => r.Amount * r.Density));
            var mass = parts.ToDictionary(p => p.Id, p => p.MassT);
            var present = new HashSet<uint>(all.Keys);
            var group = Groups(parts, all);

            for (var n = current; n >= 0; n--)
            {
                foreach (var decoupler in parts.Where(p => p.Stage == n && present.Contains(p.Id) && p.Tags.Contains("decoupler")).ToList())
                    foreach (var id in Below(decoupler.Id, parts)) present.Remove(id);
                var engines = parts.Where(p => present.Contains(p.Id) && p.ThrustVacKn > 0 && p.IspVac > 0 && p.Stage >= n).ToList();
                for (var guard = 0; guard < 64; guard++)
                {
                    var live = engines.Where(e => fuel.Where(f => present.Contains(f.Key) && group[f.Key] == group[e.Id]).Sum(f => f.Value) > Empty).ToList();
                    if (live.Count == 0) break;
                    var flows = live.ToDictionary(e => e.Id, e => e.ThrustVacKn / (e.IspVac * G0));
                    var byGroup = live.GroupBy(e => group[e.Id]).ToList();
                    var time = byGroup.Min(g => fuel.Where(f => present.Contains(f.Key) && group[f.Key] == g.Key).Sum(f => f.Value) / g.Sum(e => flows[e.Id]));
                    var thrust = live.Sum(e => e.ThrustVacKn);
                    var flow = flows.Values.Sum();
                    var start = present.Sum(id => mass[id]);
                    var burned = flow * time;
                    if (!(start > burned) || burned <= 0) break;
                    result[n] += thrust / flow * Math.Log(start / (start - burned));
                    foreach (var g in byGroup)
                    {
                        var members = fuel.Keys.Where(id => present.Contains(id) && group[id] == g.Key && fuel[id] > 0).ToList();
                        var held = members.Sum(id => fuel[id]);
                        var take = g.Sum(e => flows[e.Id]) * time;
                        foreach (var id in members)
                        {
                            var share = Math.Min(fuel[id], take * fuel[id] / held);
                            fuel[id] -= share;
                            mass[id] -= share;
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>The parts hanging below <paramref name="id"/> in the craft's tree, itself included.</summary>
        private static IEnumerable<uint> Below(uint id, IList<SnapshotPart> parts)
        {
            var gone = new HashSet<uint> { id };
            var grew = true;
            while (grew)
            {
                grew = false;
                foreach (var p in parts)
                    if (p.ParentId.HasValue && gone.Contains(p.ParentId.Value) && gone.Add(p.Id)) grew = true;
            }
            return gone;
        }

        /// <summary>The parts whose fuel is shared: joined through the tree, but not across a decoupler.</summary>
        private static Dictionary<uint, uint> Groups(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> all)
        {
            var root = parts.ToDictionary(p => p.Id, p => p.Id);
            Func<uint, uint> find = null;
            find = id => root[id] == id ? id : (root[id] = find(root[id]));
            foreach (var p in parts)
            {
                if (!p.ParentId.HasValue || !all.ContainsKey(p.ParentId.Value)) continue;
                if (p.Tags.Contains("decoupler") || all[p.ParentId.Value].Tags.Contains("decoupler")) continue;
                root[find(p.Id)] = find(p.ParentId.Value);
            }
            return parts.ToDictionary(p => p.Id, p => find(p.Id));
        }
    }
}
