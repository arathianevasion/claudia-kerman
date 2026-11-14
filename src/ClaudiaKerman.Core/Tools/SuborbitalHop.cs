// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// #1048: what a hop below orbit costs from the pad, flown in plan_ascent's own <see cref="AscentSim"/> on the craft's stage figures,
    /// drag table and the body's air: straight up (no turn), the throttle and staging as launch flies them, until the apoapsis reaches
    /// the hop's. No hand-tuned table: the craft's own climb.
    /// </summary>
    internal static class SuborbitalHop
    {
        /// <summary>
        /// The vacuum delta-v the craft on the pad spends putting its apoapsis at <paramref name="targetAp"/> m; null with
        /// <paramref name="why"/> when the sim can't fly it (no drag table, no air, no stage figures) or the craft can't reach it.
        /// </summary>
        public static double? DeltaV(GameSnapshot snapshot, double targetAp, out string why)
        {
            PlanAscent.Setup setup;
            var refusal = PlanAscent.Prepare(JsonReader.Parse("{}"), snapshot, out setup);
            if (refusal != null)
            {
                why = refusal.Summary;
                return null;
            }
            setup.Options.TargetAp = targetAp;
            setup.Options.TurnStartSpeed = double.PositiveInfinity;
            setup.Options.Hop = true;
            var flight = AscentSim.Fly(setup.Options, setup.Air, setup.Drag, setup.Stages);
            why = flight.ReachesHop ? null : "the hop to " + Math.Round(targetAp) + " m: " + flight.Failure;
            return flight.ReachesHop ? flight.DeltaVToOrbit : (double?)null;
        }
    }
}
