// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The Mission's Target Vessel (ADR 0013) as the planning tools aim at it: its name, the body it orbits and its orbit there,
    /// read from the Game Snapshot's <c>target</c> entry. A body target is a <see cref="SnapshotBody"/>; this is the other kind,
    /// for <c>solve_burn</c>'s <c>match_velocities</c> and <c>course_correction</c>, the relative references in <see cref="BurnTime"/>
    /// and the Course Correction's distance constraint (#454).
    /// </summary>
    internal sealed class VesselTarget
    {
        /// <summary>Its vessel name, as the snapshot's entry gives it.</summary>
        public string Name;
        /// <summary>The body it orbits.</summary>
        public SnapshotBody Body;
        /// <summary>Its orbit round <see cref="Body"/>, from the snapshot's elements as the craft's own orbit is (so both are in one frame).</summary>
        public Conic Orbit;

        /// <summary>
        /// The Target Vessel the snapshot carries, or null with <paramref name="why"/>: there is none, or it is landed, or its
        /// orbit or body is missing. <paramref name="name"/>, when given, must be its name (any case).
        /// </summary>
        public static VesselTarget Read(GameSnapshot snapshot, BodySystem system, string name, out string why)
        {
            why = null;
            var entry = snapshot != null ? snapshot.Target : null;
            if (entry == null)
            {
                why = "the Game Snapshot has no Target Vessel (the Mission has none, or it is no longer in the game)";
                return null;
            }
            if (!string.IsNullOrEmpty(name) && !string.Equals(name.Trim(), entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                why = "the Target Vessel is " + entry.Name + ", not " + name;
                return null;
            }
            var situation = entry.Situation ?? "";
            if (GameSnapshot.IsGroundedSituation(situation))
            {
                why = "the Target Vessel " + entry.Name + " is " + situation.ToLowerInvariant() + ", not in orbit";
                return null;
            }
            var orbit = entry.Orbit;
            var body = system.Find(string.IsNullOrEmpty(orbit != null ? orbit.Body : null) ? entry.Body : orbit.Body);
            if (orbit == null || orbit.SemiMajorAxis == 0 || body == null || !(body.Gm > 0))
            {
                why = "the Game Snapshot has no orbit for the Target Vessel " + entry.Name;
                return null;
            }
            return new VesselTarget
            {
                Name = entry.Name,
                Body = body,
                Orbit = Conic.FromElements(body.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan,
                    orbit.ArgumentOfPeriapsis, orbit.MeanAnomalyAtEpoch, orbit.Epoch),
            };
        }

        /// <summary>Why a craft round <paramref name="craftBody"/> can't aim at it (it is in another sphere of influence), or null.</summary>
        public string Elsewhere(SnapshotBody craftBody, string reference)
        {
            if (craftBody == Body || string.Equals(craftBody.Name, Body.Name, StringComparison.OrdinalIgnoreCase)) return null;
            return "the Target Vessel " + Name + " is round " + Body.Name + ", in another sphere of influence than the craft (round "
                + craftBody.Name + " then), so " + reference + " is not defined";
        }

        /// <summary>
        /// The craft's nearest approach to the vessel between <paramref name="from"/> and <paramref name="to"/> on
        /// <paramref name="craft"/> (both round <see cref="Body"/>): centre-to-centre distance, its UT and the relative speed then.
        /// </summary>
        public void Approach(BodySystem system, Conic craft, double from, double to, out double distance, out double ut, out double relativeSpeed)
        {
            system.ClosestApproach(new Patch { Body = Body, Orbit = craft, StartUt = from, EndUt = to, End = PatchEnd.Stable }, Orbit, out distance, out ut);
            Vec3 r, v, tr, tv;
            craft.State(ut, out r, out v);
            Orbit.State(ut, out tr, out tv);
            relativeSpeed = (v - tv).Length;
        }
    }
}
