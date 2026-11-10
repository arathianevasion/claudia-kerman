// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Stock's per-stage delta-v can lag a burn (#980): after the Duna ascent its stage 4 still started at 12.49 t on an 11.58 t
    /// craft and read 684 m/s too many until a warp later. A stage that starts heavier than the whole craft is such a stale
    /// reading. The mod works its figure out again from the craft's mass, its propellant and the stage's own Isp, before the
    /// Game Snapshot feeds a plan, a margin or a tolerance gate. Only the snapshot's own figures are used.
    /// </summary>
    public static class StaleStageReading
    {
        /// <summary>How much heavier than the craft a stage may start before its reading counts as stale, t: stock's masses differ in the last digits.</summary>
        public const double MarginT = 0.01;

        /// <summary>The stages with delta-v whose start mass is above the craft's mass; none without the simulator or a mass.</summary>
        public static List<SnapshotStage> StaleStages(GameSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.DeltaVSimulator || snapshot.Craft.MassTonnes <= 0) return new List<SnapshotStage>();
            var craft = snapshot.Craft.MassTonnes;
            return snapshot.Stages.Where(s => !s.DeltaVUnreliable && s.DeltaVVac > 0 && s.StartMassT > craft + MarginT && s.EndMassT > 0).ToList();
        }

        /// <summary>
        /// Gives each stale stage a fresh start mass and the delta-v that goes with it. The start mass is its end mass plus the
        /// propellant the craft's engines burn that the stages stock read right don't account for, given first to the stages that
        /// burn last; it never goes above the stale figure or the craft's mass. With no parts to weigh, the craft's mass is the
        /// start. The Isp is the stage's own, read back from its stale figures, so the vacuum and sea-level delta-v scale alike.
        /// </summary>
        public static void Freshen(GameSnapshot snapshot)
        {
            var stale = StaleStages(snapshot);
            if (stale.Count == 0) return;
            var craft = snapshot.Craft.MassTonnes;
            var left = PropellantT(snapshot);
            if (left.HasValue)
                left = left.Value - snapshot.Stages.Where(s => !stale.Contains(s) && s.DeltaVVac > 0).Sum(s => Math.Max(0, s.StartMassT - s.EndMassT));
            // Less than the stages stock read right burn: the parts don't add up, so only the craft's mass bounds the stale stages.
            if (left < 0) left = null;
            // Stages burn from the highest number down: the lower stale ones are still full, the highest takes what is left.
            foreach (var stage in stale.OrderBy(s => s.Number))
            {
                var start = Math.Min(stage.StartMassT, craft);
                if (left.HasValue)
                {
                    var burn = Math.Max(0, Math.Min(left.Value, stage.StartMassT - stage.EndMassT));
                    start = Math.Min(start, stage.EndMassT + burn);
                    left = left.Value - burn;
                }
                Rescale(stage, start);
            }
        }

        private static void Rescale(SnapshotStage stage, double start)
        {
            var was = Math.Log(stage.StartMassT / stage.EndMassT);
            var now = start > stage.EndMassT ? Math.Log(start / stage.EndMassT) : 0;
            var ratio = was > 0 ? now / was : 0;
            stage.DeltaVVac *= ratio;
            stage.DeltaVAsl *= ratio;
            stage.StartMassT = Math.Max(start, stage.EndMassT);
        }

        /// <summary>The mass of the resources the craft's engines burn, t; null when the snapshot lists no parts or no engine propellant.</summary>
        private static double? PropellantT(GameSnapshot snapshot)
        {
            var burned = new HashSet<string>(snapshot.Craft.Parts.SelectMany(p => p.EnginePropellants));
            if (burned.Count == 0) return null;
            return snapshot.Craft.Parts.SelectMany(p => p.Resources).Where(r => burned.Contains(r.Name)).Sum(r => r.Amount * r.Density);
        }
    }
}
