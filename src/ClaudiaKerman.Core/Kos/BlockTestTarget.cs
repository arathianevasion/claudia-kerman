// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Kos
{
    /// <summary>
    /// Where the debug block test runner puts a test's second craft, the target of <c>rendezvous</c> (#455) and <c>dock</c> (#456):
    /// relative to the craft the test flies, at the moment both are spawned. The target starts on the craft's own orbit, moved
    /// <c>target_orbit_along</c> metres round it (ahead in the direction of motion; negative behind), and then has the relative
    /// velocity <c>target_orbit_prograde</c>, <c>target_orbit_normal</c> and <c>target_orbit_radial</c> (m/s, in the maneuver-node
    /// frame of the craft's orbit at that point) added to its own. Elements in and out are KSP's, as the Game Snapshot reads them.
    /// </summary>
    public static class BlockTestTarget
    {
        /// <summary>
        /// The target's orbit round the body of gravitational parameter <paramref name="gm"/> (m³/s²), at epoch <paramref name="ut"/>,
        /// for a craft on <paramref name="craft"/> then; <paramref name="along"/> in m, the relative velocity in m/s.
        /// </summary>
        public static SnapshotOrbit Place(double gm, SnapshotOrbit craft, double ut, double along, double prograde, double normal, double radial)
        {
            var orbit = Conic.FromElements(gm, craft.SemiMajorAxis, craft.Eccentricity, craft.Inclination, craft.Lan, craft.ArgumentOfPeriapsis,
                craft.MeanAnomalyAtEpoch, craft.Epoch);
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            // So many metres round the orbit: on a circle, exactly that arc; on an ellipse, that arc at the craft's radius.
            orbit.StateAtTrue(orbit.TrueAnomaly(ut) + along / r.Length, out r, out v);
            var forward = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(forward, up);
            var target = Conic.FromState(gm, r, v + forward * prograde + up * normal + outward * radial, ut);
            return Elements(target, craft.Body);
        }

        /// <summary>KSP's elements of <paramref name="orbit"/> (inclination, LAN and argument of periapsis in degrees, mean anomaly in radians).</summary>
        internal static SnapshotOrbit Elements(Conic orbit, string body)
        {
            var lan = Math.Atan2(orbit.W.X, -orbit.W.Y);
            var node = new Vec3(Math.Cos(lan), Math.Sin(lan), 0);
            var argPe = Math.Atan2(Vec3.Dot(orbit.P, Vec3.Cross(orbit.W, node)), Vec3.Dot(orbit.P, node));
            return new SnapshotOrbit
            {
                SemiMajorAxis = orbit.A,
                Eccentricity = orbit.E,
                Inclination = orbit.Inclination,
                Lan = Wrap(lan * 180 / Math.PI),
                ArgumentOfPeriapsis = Wrap(argPe * 180 / Math.PI),
                MeanAnomalyAtEpoch = orbit.M0,
                Epoch = orbit.Epoch,
                Body = body ?? "",
            };
        }

        private static double Wrap(double degrees)
        {
            return degrees - 360 * Math.Floor(degrees / 360);
        }
    }
}
