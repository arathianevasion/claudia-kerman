// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Whether the craft has changed beyond a pending plan's tolerances since the plan was offered (spec: between
    /// sequences, Approve becomes Re-plan). A plan's tolerances are Claudia's words, so the mod measures what it can
    /// against the player's configured tolerance: the part list, the body, grounded or flying, the stock simulator's
    /// vacuum delta-v, and the size of a stable orbit.
    /// </summary>
    public static class PlanDrift
    {
        private static readonly string[] Stable = { "ORBITING", "ESCAPING", "DOCKED" };

        /// <summary>Masses this close, t, are the same craft: its delta-v can't have changed.</summary>
        private const double SameMassT = 0.001;

        /// <summary>The first way <paramref name="now"/> differs from <paramref name="baseline"/> beyond <paramref name="tolerancePercent"/>, in words; "" when it doesn't.</summary>
        public static string Exceeded(GameSnapshot baseline, GameSnapshot now, double tolerancePercent)
        {
            if (baseline == null || now == null) return "";
            if (baseline.Body != now.Body) return "the craft is now at " + now.Body + ", not " + baseline.Body;
            if (!new HashSet<uint>(baseline.Craft.Parts.Select(p => p.Id)).SetEquals(now.Craft.Parts.Select(p => p.Id)))
                return "parts have been added or lost";
            if (baseline.IsGrounded != now.IsGrounded)
                return "the craft is now " + now.Situation + ", not " + baseline.Situation;

            var tolerance = tolerancePercent / 100.0;
            // #980: a reading that changed with no change of mass is stock catching up, not the craft changing.
            if (baseline.DeltaVSimulator && now.DeltaVSimulator && !baseline.DeltaVUnreliable && !now.DeltaVUnreliable
                && Math.Abs(baseline.Craft.MassTonnes - now.Craft.MassTonnes) > SameMassT)
            {
                var before = baseline.Stages.Sum(s => s.DeltaVVac);
                var after = now.Stages.Sum(s => s.DeltaVVac);
                if (Math.Abs(after - before) > tolerance * Math.Max(before, after))
                    return "delta-v changed from " + Math.Round(before) + " to " + Math.Round(after) + " m/s";
            }
            if (baseline.Orbit != null && now.Orbit != null
                && Array.IndexOf(Stable, baseline.Situation) >= 0 && Array.IndexOf(Stable, now.Situation) >= 0)
            {
                if (Moved(baseline.Orbit.Apoapsis, now.Orbit.Apoapsis, tolerance)) return "the apoapsis moved from " + Math.Round(baseline.Orbit.Apoapsis) + " to " + Math.Round(now.Orbit.Apoapsis) + " m";
                if (Moved(baseline.Orbit.Periapsis, now.Orbit.Periapsis, tolerance)) return "the periapsis moved from " + Math.Round(baseline.Orbit.Periapsis) + " to " + Math.Round(now.Orbit.Periapsis) + " m";
            }
            return "";
        }

        // Relative to the orbit's size, but never finer than 10 km, so a low circular orbit isn't jumpy.
        private static bool Moved(double before, double after, double tolerance)
        {
            return Math.Abs(after - before) > tolerance * Math.Max(Math.Max(Math.Abs(before), Math.Abs(after)), 10000);
        }
    }
}
