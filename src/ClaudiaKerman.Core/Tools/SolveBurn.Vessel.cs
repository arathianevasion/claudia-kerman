/*
 * MechJeb2 Copyright (C) 2013 and the MechJeb contributors, GNU GPL version 3
 * match_velocities after MechJeb2/OrbitalManeuverCalculator.cs DeltaVToMatchVelocities (MechJeb2 a958383)
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The goals on the Mission's Target Vessel (#454, ADR 0013): <c>match_velocities</c>, the burn that zeroes the speed relative
    /// to it at the burn's time (by default the closest approach), and <c>course_correction</c>, the smallest burn that brings
    /// the next closest approach to <c>value</c> metres, by the Course Correction's solver with a distance constraint
    /// (<see cref="CorrectionBurn.FindApproach"/>). Both give <c>distance_predicted</c> and <c>relative_speed_predicted</c> in
    /// <c>achieved</c>, and the target helpers here let match_plane, resonant, intercept_at and the relative references take the
    /// Target Vessel as well as a body.
    /// </summary>
    internal sealed partial class SolveBurn
    {
        /// <summary>The target's orbit round the body it orbits: the Target Vessel's, or the body's.</summary>
        private static Conic TargetOrbit(PredictBurn.Setup setup)
        {
            return setup.Aim.OrbitIn(setup.System);
        }

        /// <summary>The target's name: the Target Vessel's, or the body's.</summary>
        private static string TargetName(PredictBurn.Setup setup)
        {
            return setup.Aim.Name;
        }

        /// <summary>The name of the body the target orbits.</summary>
        private static string TargetParent(PredictBurn.Setup setup)
        {
            return setup.Aim.Parent;
        }

        /// <summary>DeltaVToMatchVelocities: the target's velocity less the craft's, at <paramref name="at"/> (default closest_approach).</summary>
        private static ToolOutcome SolveMatchVelocities(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, JsonNode at)
        {
            string problem;
            Patch patch;
            Vec3 r, v;
            var ut = Place(setup, path, at ?? Word("closest_approach"), out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var vessel = setup.Vessel;
            var elsewhere = vessel.Elsewhere(patch.Body, "match_velocities");
            if (elsewhere != null) return PlanningJson.Error(elsewhere);
            Vec3 rt, vt;
            vessel.Orbit.State(ut.Value, out rt, out vt);
            return Answer(into, setup, "match_velocities", ut.Value, r, v, vt - v, null, (json, burn) =>
            {
                // The relative speed the propagated burn leaves, not the 0 the formula aims at.
                Vec3 rb, vb;
                burn.Orbit.State(burn.Ut, out rb, out vb);
                Predicted(into, json, (rb - rt).Length, (vb - vt).Length);
            });
        }

        /// <summary>The Course Correction to the Target Vessel at <paramref name="at"/> (default in 60 s): the least burn to a closest approach of <paramref name="distance"/> m.</summary>
        private static ToolOutcome SolveCourseCorrection(SolvedBurn into, PredictBurn.Setup setup, List<Patch> path, double distance, JsonNode at)
        {
            string problem;
            Patch patch;
            Vec3 r, v;
            var ut = Place(setup, path, at ?? Word(DefaultAt), out patch, out r, out v, out problem);
            if (!ut.HasValue) return PlanningJson.Error(problem);
            var vessel = setup.Vessel;
            var elsewhere = vessel.Elsewhere(patch.Body, "course_correction");
            if (elsewhere != null) return PlanningJson.Error(elsewhere);
            var plan = CorrectionBurn.FindApproach(setup.System, setup.Body, setup.Start, setup.Now, vessel,
                new CorrectionAsk { Target = vessel.Name, TargetDistance = distance, EarliestUt = ut.Value, LatestUt = ut.Value });
            if (plan.Error != null) return PlanningJson.Error(plan.Error);
            return Answer(into, setup, "course_correction", ut.Value, r, v, plan.Burn, null, (json, burn) =>
            {
                Predicted(into, json, plan.DistancePredicted, plan.RelativeSpeedPredicted);
                PlanningJson.Time(json, "approach", plan.ApproachUt, setup.Now);
            });
        }

        /// <summary>The two figures both goals give in achieved, rounded, and kept unrounded in <paramref name="into"/> for CORRECTIONBURN.</summary>
        private static void Predicted(SolvedBurn into, JsonWriter json, double distance, double relativeSpeed)
        {
            into.Distance = distance;
            into.RelativeSpeed = relativeSpeed;
            json.Field("distance_predicted", PlanningJson.Round(distance, 1)).Field("relative_speed_predicted", PlanningJson.Round(relativeSpeed, 3));
        }
    }
}
