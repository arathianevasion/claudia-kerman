// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// What <see cref="CorrectionBurn"/> and <see cref="ReturnBurn"/> share: following a burn's path, the first patch round a
    /// target, the node frame and the tolerance verdict.
    /// </summary>
    internal static class SingleShooting
    {
        /// <summary>
        /// <see cref="BodySystem.Follow"/>, or null where it throws on a radial path (<see cref="Conic.FromState"/> rejects it;
        /// a last patch that is a hyperbola round the star no longer throws, #471).
        /// </summary>
        public static List<Patch> Follow(BodySystem system, SnapshotBody body, Conic orbit, double ut, int patches)
        {
            try
            {
                return system.Follow(body, orbit, ut, patches);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// The first patch round <paramref name="target"/> after <paramref name="burn"/> (inertial, m/s) at <paramref name="ut"/>
        /// on <paramref name="orbit"/>, following at most <paramref name="patches"/> patches; null when it does not get there.
        /// </summary>
        public static Patch Arrive(BodySystem system, SnapshotBody body, Conic orbit, double ut, Vec3 burn, SnapshotBody target, int patches)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            Conic after;
            try
            {
                after = Conic.FromState(body.Gm, r, v + burn, ut);
            }
            catch (ArgumentException)
            {
                return null;
            }
            var path = Follow(system, body, after, ut, patches);
            if (path == null) return null;
            foreach (var patch in path)
            {
                if (patch.Body == target) return patch;
                if (patch.Error != null) return null;
            }
            return null;
        }

        /// <summary><paramref name="burn"/> in stock's maneuver-node frame at <paramref name="ut"/> on <paramref name="orbit"/>.</summary>
        public static void NodeFrame(Conic orbit, double ut, Vec3 burn, out double prograde, out double normal, out double radial)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            OrbitChange.NodeComponents(r, v, burn, out prograde, out normal, out radial);
        }

        /// <summary>
        /// The error when the best burn misses the caller's tolerances (metres and degrees; the solver's own status is not the
        /// test), or null when it meets them. Inclination is compared in degrees, so an equatorial target is not met by
        /// arriving the other way round (the normal's pair can't tell).
        /// </summary>
        public static string ToleranceError(string failure, double deltaV, double pe, double inc, double targetPe, double? targetInc,
            double peTolerance, double incTolerance, string solverReason)
        {
            var withInc = targetInc.HasValue;
            var peOff = Math.Abs(pe - targetPe);
            var incOff = withInc ? Math.Abs(inc - targetInc.Value) : 0;
            if (peOff <= peTolerance && incOff <= incTolerance) return null;
            return failure + ": the best burn (" + Num(deltaV) + " m/s) arrives at periapsis " + Num(pe) + " m"
                + (withInc ? " and inclination " + Deg(inc) + "°" : "") + ", not " + Num(targetPe) + " m"
                + (withInc ? " and " + Deg(targetInc.Value) + "°" : "") + " (" + solverReason + ")";
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Deg(double x)
        {
            return x.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
