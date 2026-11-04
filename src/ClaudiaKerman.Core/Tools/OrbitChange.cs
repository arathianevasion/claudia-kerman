/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 * Ported from MechJebLib/Maneuvers/Simple.cs, MechJebLib/Maneuvers/ChangeOrbitalElement.cs and MechJebLib/Functions/Astro.cs (MechJeb2 a958383)
 */
/*
 * MechJeb2 Copyright (C) 2013 and the MechJeb contributors, GNU GPL version 3
 * Ported from MechJeb2/OrbitalManeuverCalculator.cs (MechJeb2 a958383)
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The single-burn orbit changes <c>solve_burn</c>'s goals make, as a velocity change at a point (<c>r</c>, <c>v</c>
    /// relative to the body, in the body's inertial frame with +Z through the north pole). Ported from
    /// MechJebLib's <c>Simple</c> (<c>DeltaVToEllipticize</c>, <c>DeltaVToChangeInclination</c> with <c>Astro.VelocityForInclination</c>,
    /// <c>DeltaVToCircularize</c>), the eccentricity goal of <c>ChangeOrbitalElement</c> (as a 1-D <see cref="BrentRoot"/> on the
    /// prograde burn rather than its 2-variable alglib solve), and MechJeb2's <c>OrbitalManeuverCalculator</c>
    /// (<c>DeltaVAndTimeToMatchPlanes*</c>, <c>DeltaVToShiftLAN</c>, <c>DeltaVToResonantOrbit</c>'s period ratio). Unlike
    /// MechJeb's, which clamps an impossible ask or returns a burn that falls short, each returns null with a reason in
    /// <c>why</c>, in all builds.
    /// </summary>
    internal static class OrbitChange
    {
        /// <summary>BrentRoot's tolerance on the burn size, relative to max(|burn|, 1) m/s.</summary>
        private const double Tolerance = 1e-10;
        private const int Iterations = 100;
        /// <summary>A unit cross product under this is two directions the same line (about 6e-5 degrees).</summary>
        private const double Parallel = 1e-6;

        /// <summary>The velocity change <paramref name="dv"/> in stock's maneuver-node frame at (r, v), as predict_burn applies it.</summary>
        public static void NodeComponents(Vec3 r, Vec3 v, Vec3 dv, out double prograde, out double normal, out double radial)
        {
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            prograde = Vec3.Dot(dv, along);
            normal = Vec3.Dot(dv, up);
            radial = Vec3.Dot(dv, Vec3.Cross(along, up));
        }

        /// <summary>The eccentricity of the orbit through (r, v): the length of the eccentricity vector.</summary>
        public static double Eccentricity(double mu, Vec3 r, Vec3 v)
        {
            var rLen = r.Length;
            return ((Vec3.Dot(v, v) - mu / rLen) * r - Vec3.Dot(r, v) * v).Length / mu;
        }

        /// <summary>
        /// The prograde burn (negative: retrograde) to the semi-major axis <paramref name="sma"/>, m, by vis-viva: the speed for
        /// that axis at this radius, in the direction the craft already moves. Null when the axis is under half the radius, which
        /// no speed reaches.
        /// </summary>
        public static double? ProgradeForSma(double mu, Vec3 r, Vec3 v, double sma, out string why)
        {
            why = null;
            var squared = mu * (2 / r.Length - 1 / sma);
            if (!(squared > 0))
            {
                why = "a semi-major axis of " + Num(sma) + " m is under half the burn's radius of " + Num(r.Length) + " m, which no speed reaches";
                return null;
            }
            return Math.Sqrt(squared) - v.Length;
        }

        /// <summary>
        /// The prograde burn (negative: retrograde) to eccentricity <paramref name="ecc"/>, the cheaper when two reach it. A burn
        /// along the velocity keeps the flight path angle γ, and the eccentricity it leaves falls to |sin γ| at the local circular
        /// speed and rises either side of it, so each side is a bracket for <see cref="BrentRoot"/>.
        /// </summary>
        public static double? ProgradeForEccentricity(double mu, Vec3 r, Vec3 v, double ecc, out string why)
        {
            why = null;
            var speed = v.Length;
            var along = v.Normalized;
            Func<double, double> error = s => Eccentricity(mu, r, along * (speed + s)) - ecc;
            var circular = Math.Sqrt(mu / r.Length) - speed;
            var lowest = error(circular) + ecc;
            if (ecc < lowest - 1e-9)
            {
                why = "the lowest eccentricity a prograde or retrograde burn at that time reaches is " + lowest.ToString("0.####", CultureInfo.InvariantCulture)
                    + ", as the craft is climbing or falling there; burn at ap or pe, where it is 0";
                return null;
            }
            if (Math.Abs(error(circular)) <= 1e-12) return circular;
            double? best = null;
            // Slower than circular: the eccentricity rises to 1 at a standstill, so a bound orbit's goal has a root here.
            if (ecc < 1)
                best = Cheaper(best, Root(error, -speed, circular));
            // Faster: it rises without bound; widen the bracket until it passes the goal.
            var high = circular + Math.Max(speed, 1);
            for (var i = 0; i < 60 && error(high) < 0; i++) high = circular + (high - circular) * 2;
            if (error(high) >= 0) best = Cheaper(best, Root(error, circular, high));
            if (!best.HasValue) why = "the search for a burn to an eccentricity of " + ecc.ToString("0.####", CultureInfo.InvariantCulture) + " did not converge";
            return best;
        }

        private static double? Root(Func<double, double> error, double lo, double hi)
        {
            var found = BrentRoot.Solve(error, lo, hi, Tolerance, Iterations);
            if (!found.Converged || Math.Abs(found.Fx) > 1e-7) return null;
            return found.X;
        }

        private static double? Cheaper(double? a, double? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return Math.Abs(b.Value) < Math.Abs(a.Value) ? b : a;
        }

        /// <summary>
        /// Simple.DeltaVToEllipticize: the burn to the orbit with periapsis and apoapsis radii <paramref name="peR"/> and
        /// <paramref name="apR"/> through this point, in the same plane: the transverse speed from the new angular momentum, the
        /// radial speed from the new energy, climbing or falling, whichever costs less. The caller keeps the radius between them.
        /// </summary>
        public static Vec3 Ellipticize(double mu, Vec3 r, Vec3 v, double peR, double apR)
        {
            var rm = r.Length;
            // orbital energy
            var e = -mu / (peR + apR);
            // orbital angular momentum
            var l = Math.Sqrt(Math.Abs((Math.Pow(e * (apR - peR), 2) - mu * mu) / (2 * e)));
            // orbital kinetic energy
            var ke = e + mu / rm;
            var vtransverse = l / rm;
            var vradial = Math.Sqrt(Math.Abs(2 * ke - vtransverse * vtransverse));
            var radialhat = r.Normalized;
            var transversehat = Vec3.Cross(Vec3.Cross(radialhat, v), radialhat).Normalized;
            var one = vtransverse * transversehat + vradial * radialhat - v;
            var two = vtransverse * transversehat - vradial * radialhat - v;
            return one.Length < two.Length ? one : two;
        }

        /// <summary>Simple.DeltaVToCircularize: to the circular speed at this radius, horizontal, in the orbit's plane.</summary>
        public static Vec3 Circularize(double mu, Vec3 r, Vec3 v)
        {
            return Vec3.Cross(Vec3.Cross(r, v), r).Normalized * Math.Sqrt(mu / r.Length) - v;
        }

        /// <summary>
        /// Astro.VelocityForInclination: the horizontal velocity turned to the heading that gives inclination
        /// <paramref name="incDeg"/> at this latitude, its size and the vertical speed kept. Of the two headings (going north or
        /// south) it takes the nearer, the cheaper; MechJeb picks by the sign of the inclination. Null when the latitude is
        /// higher than the inclination allows, where MechJeb would quietly burn due east or west.
        /// </summary>
        public static Vec3? ChangeInclination(Vec3 r, Vec3 v, double incDeg, out string why)
        {
            why = null;
            Vec3 up, east, north;
            if (!Enu(r, out up, out east, out north, out why)) return null;
            var lat = Math.Asin(Math.Max(-1, Math.Min(1, up.Z)));
            var cosAngle = Math.Cos(incDeg * Math.PI / 180) / Math.Cos(lat);
            if (Math.Abs(cosAngle) > 1 + 1e-12)
            {
                var latDeg = Math.Abs(lat) * 180 / Math.PI;
                why = "the burn is at latitude " + Deg(latDeg) + ", and no orbit through it has an inclination under " + Deg(latDeg)
                    + " or over " + Deg(180 - latDeg) + "; burn nearer the equator (eq_highest)";
                return null;
            }
            // From east, positive northward.
            var angle = Math.Acos(Math.Max(-1, Math.Min(1, cosAngle)));
            double e = Vec3.Dot(v, east), n = Vec3.Dot(v, north);
            var horizontal = east * e + north * n;
            var size = horizontal.Length;
            var northward = (east * Math.Cos(angle) + north * Math.Sin(angle)) * size;
            var southward = (east * Math.Cos(angle) - north * Math.Sin(angle)) * size;
            return ((northward - horizontal).Length <= (southward - horizontal).Length ? northward : southward) - horizontal;
        }

        /// <summary>
        /// DeltaVAndTimeToMatchPlanes*: at a node with the plane of normal <paramref name="normal"/>, the horizontal velocity
        /// turned into that plane, its size and the vertical speed kept.
        /// </summary>
        public static Vec3 MatchPlane(Vec3 r, Vec3 v, Vec3 normal)
        {
            var up = r.Normalized;
            var horizontal = v - up * Vec3.Dot(v, up);
            return Vec3.Cross(normal, up).Normalized * horizontal.Length - horizontal;
        }

        /// <summary>
        /// DeltaVToShiftLAN: the horizontal velocity turned into the one plane through this point and the equator at longitude
        /// <paramref name="lanDeg"/>, oriented so that point is its ascending node. MechJeb steers by a great-circle heading to
        /// that point (or the opposite one); the plane is the same. Null when the plane is the equator (the burn is on it) or the
        /// burn would turn the orbit round (more than a right angle), where MechJeb gives the burn anyway.
        /// </summary>
        public static Vec3? ShiftLan(Vec3 r, Vec3 v, double lanDeg, out string why)
        {
            why = null;
            var up = r.Normalized;
            var node = new Vec3(Math.Cos(lanDeg * Math.PI / 180), Math.Sin(lanDeg * Math.PI / 180), 0);
            var normal = Vec3.Cross(up, node);
            if (normal.Length < Parallel || Vec3.Cross(new Vec3(0, 0, 1), normal.Normalized).Length < Parallel)
            {
                why = "the burn is on the equator, so the orbit through it and the new node would be equatorial, with no LAN; leave at out to burn where the planes cross";
                return null;
            }
            normal = normal.Normalized;
            // The ascending node is where the orbit crosses the equator northward: along +Z × normal.
            if (Vec3.Dot(Vec3.Cross(new Vec3(0, 0, 1), normal), node) < 0) normal = normal * -1;
            if (Vec3.Dot(normal, Vec3.Cross(r, v).Normalized) <= 0)
            {
                why = "from where the craft is at that time, a LAN of " + Deg(lanDeg) + " needs the orbit to turn round; leave at out to burn where the planes cross";
                return null;
            }
            var horizontal = v - up * Vec3.Dot(v, up);
            return Vec3.Cross(normal, up) * horizontal.Length - horizontal;
        }

        /// <summary>The normal of the orbit plane with inclination <paramref name="incDeg"/> and LAN <paramref name="lanDeg"/>, as <see cref="Conic.FromElements"/> builds it.</summary>
        public static Vec3 PlaneNormal(double incDeg, double lanDeg)
        {
            double i = incDeg * Math.PI / 180, o = lanDeg * Math.PI / 180;
            return new Vec3(Math.Sin(o) * Math.Sin(i), -Math.Cos(o) * Math.Sin(i), Math.Cos(i));
        }

        /// <summary>The cost, m/s, of turning a velocity of <paramref name="speed"/> m/s through <paramref name="deltaIncDeg"/> degrees of plane: 2 v sin(Δi/2).</summary>
        public static double PlaneChangeDeltaV(double speed, double deltaIncDeg)
        {
            return 2 * speed * Math.Sin(Math.Abs(deltaIncDeg) * Math.PI / 360);
        }

        /// <summary>The radius, m, at which a circular orbit's period is <paramref name="period"/> s: Kepler's third law.</summary>
        public static double RadiusForPeriod(double mu, double period)
        {
            return Math.Pow(mu * Math.Pow(period / (2 * Math.PI), 2), 1.0 / 3);
        }

        /// <summary>Up, east and north at <paramref name="r"/>; false at a pole, where east and north have no direction.</summary>
        private static bool Enu(Vec3 r, out Vec3 up, out Vec3 east, out Vec3 north, out string why)
        {
            why = null;
            up = r.Normalized;
            east = Vec3.Cross(new Vec3(0, 0, 1), up);
            north = default(Vec3);
            if (east.Length < Parallel)
            {
                why = "the burn is over a pole, where no heading sets an inclination; burn at another time";
                return false;
            }
            east = east.Normalized;
            north = Vec3.Cross(up, east);
            return true;
        }

        private static string Deg(double degrees)
        {
            return degrees.ToString("0.##", CultureInfo.InvariantCulture) + "°";
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
