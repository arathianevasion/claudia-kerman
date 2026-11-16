// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Contracts;
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
            var flight = Fly(snapshot, targetAp, false, out why);
            return flight != null && why == null ? flight.DeltaVToOrbitOrHop : (double?)null;
        }

        /// <summary>
        /// #1029: the hop flown, its climb tracked when <paramref name="track"/>, or (with <paramref name="targetAp"/> null) the launch to
        /// orbit as plan_ascent flies it by default; null with <paramref name="why"/> when it can't be flown, and with <paramref name="why"/>
        /// set when it doesn't get there (its track up to there is still the climb flown).
        /// </summary>
        internal static AscentFlight Fly(GameSnapshot snapshot, double? targetAp, bool track, out string why)
        {
            return Fly(snapshot, targetAp, track, null, out why);
        }

        /// <summary>
        /// #1160: the flight the launch options Claudia wrote fly (<paramref name="written"/>, over what the defaults give): a hop when
        /// their target apoapsis is below orbit, else the launch to orbit, at their turn_shape when they give one or by the search.
        /// </summary>
        internal static AscentFlight Fly(GameSnapshot snapshot, WrittenLaunch written, bool track, out string why)
        {
            return Fly(snapshot, null, track, written, out why);
        }

        private static AscentFlight Fly(GameSnapshot snapshot, double? targetAp, bool track, WrittenLaunch written, out string why)
        {
            PlanAscent.Setup setup;
            var refusal = PlanAscent.Prepare(JsonReader.Parse("{}"), snapshot, out setup);
            if (refusal != null)
            {
                why = refusal.Summary;
                return null;
            }
            setup.Options.Track = track;
            var shape = (double?)null;
            if (written != null)
            {
                var o = setup.Options;
                if (written.TurnStartSpeed.HasValue) o.TurnStartSpeed = written.TurnStartSpeed.Value;
                if (written.StageLimit.HasValue) o.StageLimit = written.StageLimit.Value;
                if (written.MaxQ.HasValue) o.MaxQ = written.MaxQ.Value;
                if (written.TargetAp.HasValue) o.TargetAp = written.TargetAp.Value;
                shape = written.TurnShape;
                if (written.Suborbital(setup.Body)) targetAp = written.TargetAp;
            }
            if (targetAp.HasValue)
            {
                setup.Options.TargetAp = targetAp.Value;
                if (written == null || !written.TurnStartSpeed.HasValue) setup.Options.TurnStartSpeed = double.PositiveInfinity;
                if (shape.HasValue) setup.Options.TurnShape = shape.Value;
                setup.Options.Hop = true;
            }
            AscentFlight flight;
            if (targetAp.HasValue) flight = AscentSim.Fly(setup.Options, setup.Air, setup.Drag, setup.Stages);
            else
            {
                // The launch to orbit by plan_ascent's search: the shape that reaches orbit with the most left, else the highest.
                var tried = new System.Collections.Generic.List<AscentFlight>();
                foreach (var each in shape.HasValue ? new System.Collections.Generic.List<double> { shape.Value } : PlanAscent.Shapes())
                {
                    setup.Options.TurnShape = each;
                    tried.Add(AscentSim.Fly(setup.Options, setup.Air, setup.Drag, setup.Stages));
                }
                flight = tried.Where(f => f.ReachesOrbit).OrderByDescending(f => Math.Round(f.DeltaVLeft)).FirstOrDefault() ?? tried.OrderByDescending(f => f.BestAp).First();
            }
            var reached = targetAp.HasValue ? flight.ReachesHop : flight.ReachesOrbit;
            why = reached ? null : (targetAp.HasValue ? "the hop to " + Math.Round(targetAp.Value) + " m: " : "the launch to orbit: ") + flight.Failure;
            return flight;
        }
    }
}
