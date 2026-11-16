// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1031: the orbit a satellite Contract asks for (its SpecificOrbitParameter) and its deviation window. Within the window means the
    /// apoapsis and periapsis are each within the window's percent of the semi-major axis of the asked ones, and the inclination within the
    /// window's percent of 180°. The window's units are not yet confirmed in game (the fixtures README); this is the one place they are read.
    /// The LAN and argument of periapsis are not judged: a launch picks the LAN by its time, and the plan proposal writes no argument.
    /// </summary>
    public sealed class ContractOrbit
    {
        /// <summary>The Contract Requirement's key.</summary>
        public string Key = "";
        public string Body = "";
        /// <summary>Degrees.</summary>
        public double Inclination;
        public double Eccentricity;
        /// <summary>m, from the body's centre.</summary>
        public double Sma;
        /// <summary>Percent.</summary>
        public double Window;

        /// <summary>Apoapsis and periapsis radii, m, from the body's centre.</summary>
        public double ApR
        {
            get { return Sma * (1 + Eccentricity); }
        }

        public double PeR
        {
            get { return Sma * (1 - Eccentricity); }
        }

        /// <summary>How far the apoapsis or periapsis may be off, m.</summary>
        public double RadiusSlack
        {
            get { return Window / 100 * Sma; }
        }

        /// <summary>How far the inclination may be off, degrees.</summary>
        public double InclinationSlack
        {
            get { return Window / 100 * 180; }
        }

        /// <summary>"apoapsis 563.8 km, periapsis 536.2 km, inclination 88.4°", altitudes over <paramref name="body"/>.</summary>
        public string Words(SnapshotBody body)
        {
            return "apoapsis " + Km(ApR - body.Radius) + ", periapsis " + Km(PeR - body.Radius) + ", inclination " + Deg(Inclination);
        }

        /// <summary>
        /// Why an orbit with these figures (altitudes over <paramref name="body"/>, m, and degrees) is outside the window, "inclination 3.4°
        /// outside the 5% window"; null when it is inside. A figure not given (null) isn't judged.
        /// </summary>
        public string Miss(SnapshotBody body, double? apoapsis, double? periapsis, double? inclination)
        {
            var misses = new List<string>();
            if (apoapsis.HasValue) Off(misses, "apoapsis", apoapsis.Value + body.Radius - ApR, RadiusSlack, Km);
            if (periapsis.HasValue) Off(misses, "periapsis", periapsis.Value + body.Radius - PeR, RadiusSlack, Km);
            if (inclination.HasValue) Off(misses, "inclination", inclination.Value - Inclination, InclinationSlack, Deg);
            return misses.Count == 0 ? null : string.Join(", ", misses.ToArray()) + " outside the " + Window.ToString("0.##", CultureInfo.InvariantCulture) + "% window";
        }

        private static void Off(List<string> misses, string what, double off, double slack, Func<double, string> unit)
        {
            if (Math.Abs(off) > slack) misses.Add(what + " " + unit(Math.Abs(off) - slack));
        }

        internal static string Km(double m)
        {
            return (m / 1000).ToString("#,0.#", CultureInfo.InvariantCulture) + " km";
        }

        private static string Deg(double degrees)
        {
            return degrees.ToString("0.#", CultureInfo.InvariantCulture) + "°";
        }
    }

    /// <summary>
    /// #1031 (spec #1021 decision 4): a satellite's orbit priced by change_orbit's burn solver (<see cref="OrbitChange"/>) from the low
    /// parking orbit: raise the apoapsis, turn the plane at the apoapsis, raise the periapsis. A launch from home goes into the plane
    /// instead when that is cheaper (the parking orbit is then in the plane): what it costs is the ground's eastward spin it no longer
    /// gets. Away from home, a turn in low orbit when cheaper than at the apoapsis, and never more than the Map's polar row.
    /// </summary>
    internal static class SatelliteOrbits
    {
        /// <summary>The delta-v over the low orbit, m/s, and the seconds from it until the orbit is reached.</summary>
        public static double Price(ContractOrbit orbit, SnapshotBody body, DeltaVMap map, bool launchedHere, out double seconds)
        {
            var mu = body.Gm;
            var r0 = DeltaVMap.LowOrbit(body);
            var rA = Math.Max(orbit.ApR, r0);
            var rP = Math.Max(orbit.PeR, r0);
            var v0 = Math.Sqrt(mu / r0);
            var raise = OrbitChange.Ellipticize(mu, new Vec3(r0, 0, 0), new Vec3(0, v0, 0), r0, rA).Length;
            var transfer = (r0 + rA) / 2;
            var va = Math.Sqrt(mu * (2 / rA - 1 / transfer));
            var atAp = new Vec3(-rA, 0, 0);
            var vAp = new Vec3(0, -va, 0);
            string why;
            var turned = OrbitChange.ChangeInclination(atAp, vAp, orbit.Inclination, out why);
            var turnHigh = turned.HasValue ? turned.Value.Length : OrbitChange.PlaneChangeDeltaV(va, orbit.Inclination);
            var turnLow = OrbitChange.PlaneChangeDeltaV(v0, orbit.Inclination);
            var plane = Math.Min(turnHigh, turnLow);
            if (launchedHere && body.RotationPeriod > 0)
            {
                var spin = 2 * Math.PI * body.Radius / body.RotationPeriod;
                var incRad = orbit.Inclination * Math.PI / 180;
                plane = Math.Min(plane, Math.Sqrt(v0 * v0 + spin * spin - 2 * v0 * spin * Math.Cos(incRad)) - (v0 - spin));
            }
            var polar = map == null ? null : map.Leg("polar:" + body.Name);
            if (polar != null && orbit.Inclination <= 90) plane = Math.Min(plane, polar.DeltaV);
            var circle = OrbitChange.Ellipticize(mu, atAp, vAp, rP, rA).Length;
            seconds = Math.PI * Math.Sqrt(transfer * transfer * transfer / mu);
            return raise + plane + circle;
        }
    }
}
