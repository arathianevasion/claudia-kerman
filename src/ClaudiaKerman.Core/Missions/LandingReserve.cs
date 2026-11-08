// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What of the craft's delta-v the landing keeps (#893, #800's cases), for the auto <c>max_dv</c>'s reserve: <see cref="StageDeltaV"/> of the craft
    /// the Entry flies (<see cref="EntryCraft"/>'s parts) against the whole craft, only in stages <c>stage_limit</c> allows. A stage dropped before the
    /// landing carries no reserve; a craft that keeps no engine needs no powered landing; stages below <c>stage_limit</c> are neither spent nor reserved.
    /// </summary>
    public sealed class LandingReserve
    {
        /// <summary>The share of the delta-v the burn may use that the landing keeps; 1 when nothing is known (an older recording) or nothing is dropped.</summary>
        public double KeptShare = 1;
        /// <summary>Whether an engine in a stage the block may fire stays on the craft.</summary>
        public bool KeepsEngine = true;
        /// <summary>m/s in stages below <c>stage_limit</c>: held back, so the burn can't spend them; 0 for none.</summary>
        public double HeldDv;

        public static LandingReserve Of(GameSnapshot snapshot, IEnumerable<SnapshotPart> gone, int stageLimit, uint? home = null)
        {
            var reserve = new LandingReserve();
            var leaving = new HashSet<uint>((gone ?? new SnapshotPart[0]).Select(p => p.Id));
            var all = PartTree.OwnSide(snapshot.Craft.Parts, home);
            var kept = all.Where(p => !leaving.Contains(p.Id)).ToList();
            reserve.KeepsEngine = kept.Any(p => p.Tags.Contains("engine") && p.ThrustVacKn > 0 && p.Stage >= stageLimit);
            var whole = StageDeltaV.PerStage(all, snapshot.Craft.Stage, home);
            var wholeUsable = whole.Where(s => s.Key >= stageLimit).Sum(s => s.Value);
            reserve.HeldDv = whole.Where(s => s.Key < stageLimit).Sum(s => s.Value);
            if (wholeUsable <= 0) return reserve;
            var keptUsable = StageDeltaV.PerStage(kept, snapshot.Craft.Stage, home).Where(s => s.Key >= stageLimit).Sum(s => s.Value);
            reserve.KeptShare = Math.Max(0, Math.Min(1, keptUsable / wholeUsable));
            return reserve;
        }
    }
}
