// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1341, at the Survey Schedule seam: the Survey Plane for an orbital survey's remaining waypoints and the one burn that reaches it.
    /// Each check flies the answer by hand: a circular Kepler orbit over a turning Mun, the node's delta-v added to the craft's velocity at
    /// the node's UT, and the ground track of the orbit after it sampled over one revolution. None of it is the code under test's own maths.
    /// </summary>
    public sealed class SurveyPlaneTests
    {
        private const double MunRadius = 200000;
        private const double MunGm = 6.5138398e10;
        private const double MunTurn = 138984.38;
        private const double Now = 448944;
        private const double Range = 15000;

        /// <summary>The 24 Nov playtest's five Mun waypoints, latitude and longitude.</summary>
        private static readonly string[] Playtest = { "10.0,58.2", "12.5,12.5", "7.1,6.0", "13.2,4.2", "19.2,0.7" };

        private static SurveyBody Mun()
        {
            return new SurveyBody { Radius = MunRadius, RotationPeriod = MunTurn, Gm = MunGm };
        }

        private static List<SurveyWaypoint> Waypoints(params string[] where)
        {
            return where.Select((w, i) =>
            {
                var parts = w.Split(',');
                return new SurveyWaypoint { Key = "wp" + i, Body = "Mun", Latitude = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), Longitude = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) };
            }).ToList();
        }

        /// <summary>A circular orbit's elements; the mean anomaly is its phase from the ascending node (argument of periapsis 0).</summary>
        private static SnapshotOrbit Circular(double altitude, double inclination, double lan, double phase)
        {
            return new SnapshotOrbit { SemiMajorAxis = MunRadius + altitude, Eccentricity = 0, Inclination = inclination, Lan = lan, ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = phase * Math.PI / 180, Epoch = Now };
        }

        /// <summary>The orbit's frame on the Mun: z north, x on longitude 0, y on 90° E, as the game's reads give it.</summary>
        private static bool Under(double x, double y, double z, out double latitude, out double longitude)
        {
            latitude = Math.Asin(z / Math.Sqrt(x * x + y * y + z * z)) * 180 / Math.PI;
            longitude = Math.Atan2(y, x) * 180 / Math.PI;
            return true;
        }

        private static SurveyPlane Pick(SnapshotOrbit orbit, List<SurveyWaypoint> waypoints)
        {
            return SurveySchedule.Plane(Mun(), orbit, Now, Under, waypoints, Range);
        }

        // ---- flown by hand ----

        private static double[] Add(double[] a, double[] b, double k = 1) { return new[] { a[0] + k * b[0], a[1] + k * b[1], a[2] + k * b[2] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double[] Unit(double[] a) { var l = Math.Sqrt(Dot(a, a)); return new[] { a[0] / l, a[1] / l, a[2] / l }; }

        /// <summary>Position and velocity on a circular orbit at <paramref name="ut"/>.</summary>
        private static void State(SnapshotOrbit orbit, double ut, out double[] r, out double[] v)
        {
            var a = orbit.SemiMajorAxis;
            var n = Math.Sqrt(MunGm / (a * a * a));
            var u = orbit.MeanAnomalyAtEpoch + n * (ut - orbit.Epoch);
            double i = orbit.Inclination * Math.PI / 180, o = orbit.Lan * Math.PI / 180;
            r = new[] { a * (Math.Cos(o) * Math.Cos(u) - Math.Sin(o) * Math.Sin(u) * Math.Cos(i)), a * (Math.Sin(o) * Math.Cos(u) + Math.Cos(o) * Math.Sin(u) * Math.Cos(i)), a * Math.Sin(u) * Math.Sin(i) };
            v = new[] { a * n * (-Math.Cos(o) * Math.Sin(u) - Math.Sin(o) * Math.Cos(u) * Math.Cos(i)), a * n * (-Math.Sin(o) * Math.Sin(u) + Math.Cos(o) * Math.Cos(u) * Math.Cos(i)), a * n * Math.Cos(u) * Math.Sin(i) };
        }

        /// <summary>The craft's velocity just after the node: its delta-v along prograde, normal and radial out at the node's UT.</summary>
        private static void AfterNode(SnapshotOrbit orbit, SurveyNode node, out double[] r, out double[] v)
        {
            double[] v0;
            State(orbit, node.Ut, out r, out v0);
            var prograde = Unit(v0);
            var normal = Unit(Cross(r, v0));
            var radial = Cross(prograde, normal);
            v = Add(Add(Add(v0, prograde, node.Prograde), normal, node.Normal), radial, node.Radial);
        }

        private static double Inclination(double[] r, double[] v)
        {
            var h = Unit(Cross(r, v));
            return Math.Acos(h[2]) * 180 / Math.PI;
        }

        private static double Lan(double[] r, double[] v)
        {
            var h = Cross(r, v);
            return (Math.Atan2(h[0], -h[1]) * 180 / Math.PI + 360) % 360;
        }

        /// <summary>How many waypoints pass within range along the ground in one revolution from <paramref name="t0"/>, flying (r, v) from then, over the turning Mun.</summary>
        private static int PassedInOneRevolution(double[] r0, double[] v0, double t0, List<SurveyWaypoint> waypoints)
        {
            var a = Math.Sqrt(Dot(r0, r0));
            var n = Math.Sqrt(Dot(v0, v0)) / a;
            var x = Unit(r0);
            var y = Unit(Cross(Cross(r0, v0), r0));
            var period = 2 * Math.PI / n;
            var passed = new HashSet<SurveyWaypoint>();
            for (var k = 0; k <= 20000; k++)
            {
                var t = period * k / 20000;
                var c = Add(Add(new double[3], x, Math.Cos(n * t)), y, Math.Sin(n * t));
                var lat = Math.Asin(c[2]);
                var lon = Math.Atan2(c[1], c[0]) - 2 * Math.PI * (t0 + t - Now) / MunTurn;
                foreach (var w in waypoints)
                {
                    double wl = w.Latitude * Math.PI / 180, wn = w.Longitude * Math.PI / 180;
                    var cos = Math.Sin(lat) * Math.Sin(wl) + Math.Cos(lat) * Math.Cos(wl) * Math.Cos(lon - wn);
                    if (MunRadius * Math.Acos(Math.Max(-1, Math.Min(1, cos))) <= Range) passed.Add(w);
                }
            }
            return passed.Count;
        }

        /// <summary>The waypoints the plane reached by changing only the inclination at the craft's next node passes over in one revolution after it.</summary>
        private static int PassedAfterInclinationChange(SnapshotOrbit orbit, double inclination, List<SurveyWaypoint> waypoints)
        {
            // The craft's ascending node, the line both planes share, comes at a phase of 0 (mod 360°).
            var n = Math.Sqrt(MunGm / Math.Pow(orbit.SemiMajorAxis, 3));
            var phase = orbit.MeanAnomalyAtEpoch;
            var t = Now + ((2 * Math.PI - phase % (2 * Math.PI)) % (2 * Math.PI)) / n;
            double[] r, v;
            var tilted = Circular(orbit.SemiMajorAxis - MunRadius, inclination, orbit.Lan, 0);
            tilted.Epoch = t;
            State(tilted, t, out r, out v);
            return PassedInOneRevolution(r, v, t, waypoints);
        }

        // ---- the playtest ----

        [Fact]
        public void ThePlaytestsFiveMunWaypointsGetASurveyPlaneThatPassesOverMoreThanThe20DegreePlane()
        {
            // #1341: the 24 Nov playtest flew a 30 km, 0.9° orbit and changed to 20° with no node; the sites took several orbits to come under her.
            var orbit = Circular(30000, 0.9, 0, 200);
            var waypoints = Waypoints(Playtest);

            var plane = Pick(orbit, waypoints);

            Assert.NotNull(plane.Node);
            double[] r, v;
            AfterNode(orbit, plane.Node, out r, out v);
            var picked = PassedInOneRevolution(r, v, plane.Node.Ut, waypoints);
            var twenty = PassedAfterInclinationChange(orbit, 20, waypoints);
            Assert.True(picked > twenty, "the Survey Plane passes over " + picked + ", the 20° plane " + twenty);
            Assert.Equal(picked, plane.Passed.Count);
        }

        [Fact]
        public void TheNodesOneBurnReachesTheSurveyPlane()
        {
            var orbit = Circular(30000, 0.9, 40, 200);

            var plane = Pick(orbit, Waypoints(Playtest));

            Assert.NotNull(plane.Node);
            Assert.InRange(plane.Node.Ut, Now, Now + 2 * Math.PI * Math.Sqrt(Math.Pow(orbit.SemiMajorAxis, 3) / MunGm));
            double[] r, v;
            AfterNode(orbit, plane.Node, out r, out v);
            Assert.Equal(plane.Inclination, Inclination(r, v), 2);
            var lan = Math.IEEERemainder(Lan(r, v) - plane.Lan, 360);
            Assert.InRange(lan, -0.01, 0.01);
            // One combined burn that turns the plane: the speed is kept, so the orbit's size is.
            double[] r0, v0;
            State(orbit, plane.Node.Ut, out r0, out v0);
            Assert.Equal(Math.Sqrt(Dot(v0, v0)), Math.Sqrt(Dot(v, v)), 3);
            Assert.Equal(plane.Node.DeltaV, Math.Sqrt(Dot(Add(v, v0, -1), Add(v, v0, -1))), 3);
        }

        [Fact]
        public void WhenTheCraftsOwnPlaneAlreadyPassesOverTheMostThereIsNoNode()
        {
            // An equatorial orbit passes over waypoints on the equator in its first revolution, whatever the body's turn.
            var orbit = Circular(30000, 0, 0, 0);
            var waypoints = Waypoints("0,30", "0.05,120", "-0.05,-100");

            var plane = Pick(orbit, waypoints);

            Assert.Null(plane.Node);
            Assert.Equal(3, plane.Passed.Count);
            Assert.Equal(0, plane.Inclination, 3);
            double[] r, v;
            State(orbit, Now, out r, out v);
            Assert.Equal(3, PassedInOneRevolution(r, v, Now, waypoints));
        }

        [Fact]
        public void WhenNoPlaneDoesBetterThanTheCraftsOwnThereIsNoNode()
        {
            // The one waypoint lies under the craft's own track 40° on from its ascending node (latitude asin(sin 10° sin 40°), less the
            // Mun's 0.6° turn while it gets there): another plane can pass over it too, but none over more.
            var orbit = Circular(30000, 10, 0, 0);
            var waypoints = Waypoints("6.37,39.0");

            var plane = Pick(orbit, waypoints);

            Assert.Null(plane.Node);
            Assert.Single(plane.Passed);
            Assert.Equal(10, plane.Inclination, 3);
        }
    }
}
