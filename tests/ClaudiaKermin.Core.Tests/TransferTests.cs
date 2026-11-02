// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The transfer solver (#448) through <c>ADDONS:CLAUDIA:EJECTIONBURN</c>'s new inputs: to an orbit round the craft's body
    /// (rendezvous and intercept, capture in and out) and to Minmus from an inclined parking orbit. Each node is checked again
    /// by propagating it: the arc must arrive where the solver says, and the arrival burn must be the velocity it says.
    /// <see cref="EjectionBurnTests"/> keeps the moon transfers <c>transfer_to</c> asks for.
    /// </summary>
    public sealed class TransferTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;

        /// <summary>A circular, equatorial 100 km orbit at its periapsis at UT 0, with Kerbin and the Mun.</summary>
        private static GameSnapshot Circular()
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = KerbinR + 100000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000,
                MeanAnomalyAtEpoch = 1.7,
            });
            return snapshot;
        }

        private static GameSnapshot BlockTestStart()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "kerbin-orbit-100km.json")));
        }

        /// <summary>A target orbit round Kerbin: circular at <paramref name="altitude"/>, <paramref name="mnaDeg"/> degrees on at UT 0.</summary>
        private static Dictionary<string, object> ToOrbit(double altitude, double inc = 0, double lan = 0, double mnaDeg = 0, string mode = "intercept", bool capture = true)
        {
            return new Dictionary<string, object>
            {
                { "target_sma", KerbinR + altitude }, { "target_ecc", 0.0 }, { "target_inc", inc }, { "target_lan", lan }, { "target_argpe", 0.0 },
                { "target_mna", mnaDeg }, { "target_epoch", 0.0 }, { "mode", mode }, { "capture", capture }, { "earliest_ut", 0.0 },
            };
        }

        private static IDictionary<string, object> Solved(GameSnapshot snapshot, Dictionary<string, object> input)
        {
            var result = BlockCalls.EjectionBurn(input, snapshot);
            Assert.Equal("", result["error"]);
            return result;
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return (double)result[key];
        }

        private static Conic CraftOrbit(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            return Conic.FromElements(KerbinGm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static Conic TargetOrbit(Dictionary<string, object> input)
        {
            return Conic.FromElements(KerbinGm, (double)input["target_sma"], (double)input["target_ecc"], (double)input["target_inc"], (double)input["target_lan"],
                (double)input["target_argpe"], (double)input["target_mna"] * Math.PI / 180, (double)input["target_epoch"]);
        }

        /// <summary>The node flown from the craft's orbit and coasted to its <c>arrival_ut</c>: where the craft is then, and how fast.</summary>
        private static void Flown(GameSnapshot snapshot, IDictionary<string, object> node, out Vec3 r, out Vec3 v)
        {
            var ut = D(node, "ut");
            Vec3 r0, v0;
            CraftOrbit(snapshot).State(ut, out r0, out v0);
            var along = v0.Normalized;
            var up = Vec3.Cross(r0, v0).Normalized;
            var outward = Vec3.Cross(along, up);
            var burned = Conic.FromState(KerbinGm, r0, v0 + along * D(node, "prograde") + up * D(node, "normal") + outward * D(node, "radial"), ut);
            burned.State(D(node, "arrival_ut"), out r, out v);
        }

        /// <summary>A circular orbit's velocity at <paramref name="r"/> on it.</summary>
        private static Vec3 CircularVelocity(Conic orbit, Vec3 r)
        {
            return Vec3.Cross(orbit.W, r).Normalized * Math.Sqrt(KerbinGm / orbit.A);
        }

        // ---- a Hohmann transfer ----

        [Fact]
        public void A_Hohmann_to_a_200_km_circular_orbit_from_100_km_is_the_closed_form_within_1_m_s()
        {
            // r1 = 700 km, r2 = 800 km: 2246.1 and 2101.1 m/s circular, so dv1 = v1 (sqrt(2 r2 / (r1 + r2)) - 1) = 73.7 m/s and
            // dv2 = v2 (1 - sqrt(2 r1 / (r1 + r2))) = 71.2 m/s.
            double r1 = 700000, r2 = 800000;
            var dv1 = Math.Sqrt(KerbinGm / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1);
            var dv2 = Math.Sqrt(KerbinGm / r2) * (1 - Math.Sqrt(2 * r1 / (r1 + r2)));
            Assert.Equal(73.7, dv1, 1);
            Assert.Equal(71.2, dv2, 1);
            var snapshot = Circular();
            var input = ToOrbit(200000);
            var node = Solved(snapshot, input);
            Assert.InRange(D(node, "dv"), dv1 - 1, dv1 + 1);
            Assert.InRange(D(node, "prograde"), dv1 - 1, dv1 + 1);
            Assert.InRange(D(node, "arrival_dv"), dv2 - 1, dv2 + 1);
            Assert.InRange(D(node, "ut"), 0, CraftOrbit(snapshot).Period);
            Assert.Equal(-1, D(node, "target_pe_actual"));
            Assert.Equal(D(node, "encounter_ut"), D(node, "arrival_ut"));

            // Flown: it arrives on the 800 km orbit at apoapsis, half an orbit of the transfer later.
            Vec3 r, v;
            Flown(snapshot, node, out r, out v);
            Assert.InRange(r.Length, r2 - 100, r2 + 100);
            var hohmannTime = Math.PI * Math.Sqrt(Math.Pow((r1 + r2) / 2, 3) / KerbinGm);
            Assert.InRange(D(node, "arrival_ut") - D(node, "ut"), hohmannTime - 30, hohmannTime + 30);
            Assert.InRange((CircularVelocity(TargetOrbit(input), r) - v).Length, D(node, "arrival_dv") - 0.5, D(node, "arrival_dv") + 0.5);
        }

        // ---- intercept and rendezvous to an inclined orbit ----

        /// <summary>A 300 km orbit at 10° whose ascending node is a quarter turn round from the craft's start, the target a third of a turn on.</summary>
        private static Dictionary<string, object> Inclined(string mode, bool capture = true)
        {
            return ToOrbit(300000, inc: 10, lan: 90, mnaDeg: 120, mode: mode, capture: capture);
        }

        [Fact]
        public void A_rendezvous_with_an_inclined_orbit_arrives_where_the_target_is()
        {
            var snapshot = Circular();
            var input = Inclined("rendezvous");
            var node = Solved(snapshot, input);
            Vec3 r, v, rt, vt;
            Flown(snapshot, node, out r, out v);
            TargetOrbit(input).State(D(node, "arrival_ut"), out rt, out vt);
            Assert.InRange((r - rt).Length, 0, 1000);
            Assert.InRange((vt - v).Length, D(node, "arrival_dv") - 0.5, D(node, "arrival_dv") + 0.5);
            Assert.InRange(D(node, "ut"), 0, D(node, "synodic"));
        }

        [Fact]
        public void An_intercept_of_an_inclined_orbit_arrives_on_it_but_not_at_the_target_and_costs_no_more_than_the_rendezvous()
        {
            var snapshot = Circular();
            var input = Inclined("intercept");
            var meet = Solved(snapshot, Inclined("rendezvous"));
            var node = Solved(snapshot, input);
            var target = TargetOrbit(input);
            Vec3 r, v, rt, vt;
            Flown(snapshot, node, out r, out v);
            target.State(D(node, "arrival_ut"), out rt, out vt);
            // On the target orbit: at its radius and in its plane.
            Assert.InRange(r.Length, target.A - 1000, target.A + 1000);
            Assert.InRange(Math.Abs(Vec3.Dot(r, target.W)), 0, 1000);
            // Not where the target is: the phase is free.
            Assert.True((r - rt).Length > 100000, "the intercept arrived " + (r - rt).Length + " m from the target");
            Assert.InRange((CircularVelocity(target, r) - v).Length, D(node, "arrival_dv") - 0.5, D(node, "arrival_dv") + 0.5);
            // Free to arrive anywhere, it is never dearer, and it goes within one orbit of the craft.
            Assert.True(D(node, "dv") + D(node, "arrival_dv") <= D(meet, "dv") + D(meet, "arrival_dv") + 0.5,
                "intercept " + (D(node, "dv") + D(node, "arrival_dv")) + " m/s, rendezvous " + (D(meet, "dv") + D(meet, "arrival_dv")) + " m/s");
            Assert.InRange(D(node, "ut"), 0, CraftOrbit(snapshot).Period);
        }

        /// <summary>Degrees round the craft's equatorial orbit from UT 0's periapsis to where it is at <paramref name="ut"/>.</summary>
        private static double AngleAt(GameSnapshot snapshot, double ut)
        {
            var r = CraftOrbit(snapshot).Position(ut);
            return Math.Atan2(r.Y, r.X) * 180 / Math.PI;
        }

        [Fact]
        public void An_intercept_departing_alone_is_a_Hohmann_from_a_node_with_the_plane_change_at_arrival()
        {
            // The target plane crosses the craft's at 90° and 270° round from UT 0's periapsis. The cheapest departure alone
            // leaves from one of them on a Hohmann in the craft's plane (dv1 = 136.3 m/s to 900 km) and arrives at the other,
            // in both planes; the arrival then matches the target orbit and turns 10° at once: from vApo = 1853 m/s to
            // vCirc = 1981 m/s, sqrt(vApo² + vCirc² - 2 vApo vCirc cos 10°) = 357.6 m/s. Either node will do; both are tried.
            double r1 = 700000, r2 = 900000;
            var dv1 = Math.Sqrt(KerbinGm / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1);
            var vApo = Math.Sqrt(KerbinGm / r2) * Math.Sqrt(2 * r1 / (r1 + r2));
            var vCirc = Math.Sqrt(KerbinGm / r2);
            var dv2 = Math.Sqrt(vApo * vApo + vCirc * vCirc - 2 * vApo * vCirc * Math.Cos(10 * Math.PI / 180));
            Assert.Equal(136.3, dv1, 1);
            Assert.Equal(357.6, dv2, 1);
            var snapshot = Circular();
            var node = Solved(snapshot, Inclined("intercept", capture: false));
            Assert.InRange(D(node, "dv"), dv1 - 1, dv1 + 1);
            Assert.InRange(Math.Abs(D(node, "normal")), 0, 1);
            Assert.InRange(D(node, "arrival_dv"), dv2 - 1, dv2 + 1);
            var angle = AngleAt(snapshot, D(node, "ut"));
            Assert.True(Math.Abs(Math.Abs(angle) - 90) < 2, "departs at " + angle + "°, not at a node");
        }

        [Fact]
        public void An_intercept_with_capture_splits_the_plane_change_and_beats_the_Hohmann_with_it_all_at_arrival()
        {
            // With the arrival counted, a few degrees of the plane change move to the departure, where they cost less than they
            // save at arrival (the textbook split): the two burns come to less than 136.3 + 357.6 = 493.9 m/s.
            var snapshot = Circular();
            var alone = Solved(snapshot, Inclined("intercept", capture: false));
            var node = Solved(snapshot, Inclined("intercept", capture: true));
            var both = D(node, "dv") + D(node, "arrival_dv");
            Assert.True(both < D(alone, "dv") + D(alone, "arrival_dv") - 10, "with capture " + both + " m/s");
            Assert.True(Math.Abs(D(node, "normal")) > 10, "no plane change at departure: normal " + D(node, "normal"));
            Assert.True(D(node, "dv") > D(alone, "dv"));
            var angle = AngleAt(snapshot, D(node, "ut"));
            Assert.True(Math.Abs(Math.Abs(angle) - 90) < 5, "departs at " + angle + "°, not near a node");
        }

        /// <summary>An orbit of e 0.5 from 750 to 2,250 km out, at 10°, its periapsis at its ascending node's quarter turn.</summary>
        private static Dictionary<string, object> Eccentric()
        {
            var input = ToOrbit(0, inc: 10);
            input["target_sma"] = 1500000.0;
            input["target_ecc"] = 0.5;
            input["target_argpe"] = 90.0;
            return input;
        }

        [Fact]
        public void An_intercept_of_an_eccentric_orbit_finds_the_cheapest_arrival_on_the_whole_orbit()
        {
            // #494: the arrival point was held within 60° of the point opposite the departure. Here the cheapest arrival is
            // about 80° from it: a brute force over departure, arrival point and flight time finds about 771 m/s, and over
            // 10 m/s more within 60° (877 m/s was found before).
            var snapshot = Circular();
            var input = Eccentric();
            var node = Solved(snapshot, input);
            var found = D(node, "dv") + D(node, "arrival_dv");
            var craft = CraftOrbit(snapshot);
            var target = TargetOrbit(input);
            double best = double.PositiveInfinity, within60 = double.PositiveInfinity;
            for (var i = 0; i < 36; i++)
            {
                var t = craft.Period * i / 36;
                Vec3 r1, vc;
                craft.State(t, out r1, out vc);
                var opposite = Math.Atan2(-Vec3.Dot(r1, target.Q), -Vec3.Dot(r1, target.P));
                for (var k = 0; k < 72; k++)
                {
                    var nu = 2 * Math.PI * k / 72;
                    Vec3 r2, vt;
                    target.StateAtTrue(nu, out r2, out vt);
                    var swept = Math.Atan2(Vec3.Dot(Vec3.Cross(r1, r2), craft.W), Vec3.Dot(r1, r2));
                    if (swept <= 0) swept += 2 * Math.PI;
                    var hohmann = Math.Sqrt(Math.Pow((r1.Length + r2.Length) / 2, 3) / KerbinGm) * swept;
                    for (var j = 0; j < 15; j++)
                    {
                        var path = Lambert.Solve(KerbinGm, r1, r2, hohmann * (0.2 + 1.8 * j / 14), craft.W);
                        if (path.Error != null) continue;
                        var cost = (path.V1 - vc).Length + (vt - path.V2).Length;
                        best = Math.Min(best, cost);
                        if (Math.Abs(Math.IEEERemainder(nu - opposite, 2 * Math.PI)) <= Math.PI / 3) within60 = Math.Min(within60, cost);
                    }
                }
            }
            Assert.True(found <= best + 1, "found " + found + " m/s, the brute force " + best + " m/s");
            Assert.True(found < within60 - 10, "found " + found + " m/s, the brute force within 60° " + within60 + " m/s");

            // Flown, it arrives on the target orbit more than 60° from the point opposite the departure, at the arrival burn's speed.
            Vec3 r, v, r0, v0, rt, vt0;
            Flown(snapshot, node, out r, out v);
            craft.State(D(node, "ut"), out r0, out v0);
            var arrival = Math.Atan2(Vec3.Dot(r, target.Q), Vec3.Dot(r, target.P));
            var away = Math.Atan2(-Vec3.Dot(r0, target.Q), -Vec3.Dot(r0, target.P));
            Assert.True(Math.Abs(Math.IEEERemainder(arrival - away, 2 * Math.PI)) > Math.PI / 3, "arrives " + (arrival - away) * 180 / Math.PI + "° from the opposite point");
            target.StateAtTrue(arrival, out rt, out vt0);
            Assert.InRange((r - rt).Length, 0, 1000);
            Assert.InRange((vt0 - v).Length, D(node, "arrival_dv") - 0.5, D(node, "arrival_dv") + 0.5);
        }

        // ---- capture in and out ----

        [Fact]
        public void Capture_false_minimises_the_departure_and_true_the_two_burns_each_checked_by_flying_the_node()
        {
            var snapshot = Circular();
            var withCapture = Inclined("rendezvous", capture: true);
            var without = Inclined("rendezvous", capture: false);
            var both = Solved(snapshot, withCapture);
            var departing = Solved(snapshot, without);
            // The documented two figures: dv the departure, arrival_dv the burn that matches the target at arrival.
            foreach (var pair in new[] { Tuple.Create(both, withCapture), Tuple.Create(departing, without) })
            {
                Vec3 r, v, rt, vt;
                Flown(snapshot, pair.Item1, out r, out v);
                TargetOrbit(pair.Item2).State(D(pair.Item1, "arrival_ut"), out rt, out vt);
                Assert.InRange((r - rt).Length, 0, 1000);
                Assert.InRange((vt - v).Length, D(pair.Item1, "arrival_dv") - 0.5, D(pair.Item1, "arrival_dv") + 0.5);
            }
            Assert.True(D(departing, "dv") <= D(both, "dv") + 0.01, "departure alone " + D(departing, "dv") + ", with capture " + D(both, "dv"));
            Assert.True(D(both, "dv") + D(both, "arrival_dv") <= D(departing, "dv") + D(departing, "arrival_dv") + 0.01);
        }

        [Fact]
        public void A_moon_s_arrival_burn_is_the_circular_capture_at_the_periapsis_reached()
        {
            var snapshot = Circular();
            var node = Solved(snapshot, new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", 30000.0 }, { "earliest_ut", 0.0 } });
            // From predict_burn's patch round the Mun: periapsis speed sqrt(gm (1 + e) / rp) less the circular sqrt(gm / rp).
            var input = string.Format(CultureInfo.InvariantCulture,
                "{{\"burns\":[{{\"at\":{0:R},\"dv\":{{\"prograde\":{1:R},\"normal\":{2:R},\"radial\":{3:R}}}}}]}}",
                D(node, "ut"), D(node, "prograde"), D(node, "normal"), D(node, "radial"));
            var outcome = PlanningTools.Default().Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
            {
                var mun = doc.RootElement.GetProperty("patches").EnumerateArray().First(p => p.GetProperty("body").GetString() == "Mun");
                const double gm = 6.5138398e10;
                var rp = 200000 + mun.GetProperty("pe").GetDouble();
                var e = mun.GetProperty("eccentricity").GetDouble();
                var capture = Math.Sqrt(gm * (1 + e) / rp) - Math.Sqrt(gm / rp);
                Assert.InRange(D(node, "arrival_dv"), capture - 1, capture + 1);
            }
            // The Mun from 100 km: about 840 m/s out and about 260 m/s to capture at 30 km.
            Assert.InRange(D(node, "arrival_dv"), 230, 290);
        }

        // ---- Minmus from an inclined parking orbit ----

        [Fact]
        public void Minmus_is_reached_from_an_inclined_parking_orbit()
        {
            var snapshot = BlockTestStart();
            snapshot.Orbit.Inclination = 25;
            snapshot.Orbit.Lan = 40;
            var node = Solved(snapshot, new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 20000.0 }, { "earliest_ut", snapshot.Ut + 30 } });
            var input = string.Format(CultureInfo.InvariantCulture,
                "{{\"burns\":[{{\"at\":{0:R},\"dv\":{{\"prograde\":{1:R},\"normal\":{2:R},\"radial\":{3:R}}}}}],\"target\":\"Minmus\"}}",
                D(node, "ut"), D(node, "prograde"), D(node, "normal"), D(node, "radial"));
            var outcome = PlanningTools.Default().Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
                Assert.InRange(doc.RootElement.GetProperty("closest_approach").GetProperty("altitude").GetDouble(), 19900, 20100);
            // In plane it would be 907 m/s. Leaving where the parking plane crosses Minmus's orbit, the arc reaches it without
            // turning the whole way at 2.2 km/s (a 19° turn there would be about 730 m/s more); the capture takes the rest.
            Assert.InRange(D(node, "dv"), 900, 1100);
            Assert.InRange(D(node, "arrival_dv"), 150, 400);
        }

        // ---- Minmus from an equatorial parking orbit (#611, m20261101-140219-a107) ----

        [Theory]
        [InlineData(true, 1329.045, 837.989, 260.815)]
        [InlineData(false, 1322.941, 837.508, 259.936)]
        public void The_coplanar_Mun_answer_is_the_one_before_611(bool capture, double ut, double dv, double arrival)
        {
            // From the block test's 100 km orbit, as the solver gave them before #611 (the #452 gate's transfer).
            var snapshot = BlockTestStart();
            var node = Solved(snapshot, new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut + 30 }, { "capture", capture } });
            Assert.InRange(D(node, "ut"), ut - 0.01, ut + 0.01);
            Assert.InRange(D(node, "dv"), dv - 0.01, dv + 0.01);
            Assert.InRange(D(node, "arrival_dv"), arrival - 0.01, arrival + 0.01);
        }

        /// <summary>a107's 80 km orbit at 0.1° when it asked plan_transfer for Minmus, with Minmus's real elements (inclined 6°).</summary>
        internal static GameSnapshot A107()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "minmus-a107-kerbin-orbit-80km.json")));
        }

        /// <summary>a107's ask for Minmus at 30 km with <c>plane_change</c> false: the one-burn plan alone, as the correction after a miss asks (#619).</summary>
        internal static Dictionary<string, object> OneBurn(GameSnapshot snapshot)
        {
            return new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut + 30 }, { "plane_change", false } };
        }

        [Fact]
        public void Minmus_inclined_6_degrees_is_reached_from_a107_s_equatorial_orbit_in_one_burn_and_the_node_flies_to_target_pe()
        {
            var snapshot = A107();
            var node = Solved(snapshot, OneBurn(snapshot));
            var input = string.Format(CultureInfo.InvariantCulture,
                "{{\"burns\":[{{\"at\":{0:R},\"dv\":{{\"prograde\":{1:R},\"normal\":{2:R},\"radial\":{3:R}}}}}],\"target\":\"Minmus\"}}",
                D(node, "ut"), D(node, "prograde"), D(node, "normal"), D(node, "radial"));
            var outcome = PlanningTools.Default().Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
                Assert.InRange(doc.RootElement.GetProperty("closest_approach").GetProperty("altitude").GetDouble(), 29900, 30100);
            // Minmus arrives about 95° past its ascending node, 6° above the craft's plane, so the arc carries the plane change:
            // about a107's two legs, match_plane's 240 m/s and the in-plane 930 m/s. Its cheapest arc used to dive through Kerbin.
            Assert.InRange(D(node, "dv"), 1100, 1250);
        }

        /// <summary>
        /// a107 (or <paramref name="snapshot"/>, a variant of it) with the Mun parked where the cheapest one-burn arc to Minmus
        /// crosses its orbit, its sphere of influence swollen to <paramref name="soi"/> m.
        /// </summary>
        internal static GameSnapshot A107MunInTheWay(double soi, GameSnapshot snapshot = null)
        {
            snapshot = snapshot ?? A107();
            var node = Solved(snapshot, OneBurn(snapshot));
            var ut = D(node, "ut");
            Vec3 r0, v0;
            CraftOrbit(snapshot).State(ut, out r0, out v0);
            var along = v0.Normalized;
            var up = Vec3.Cross(r0, v0).Normalized;
            var arc = Conic.FromState(KerbinGm, r0, v0 + along * D(node, "prograde") + up * D(node, "normal") + Vec3.Cross(along, up) * D(node, "radial"), ut);
            var mun = snapshot.Bodies.Single(b => b.Name == "Mun");
            var crossing = arc.TimeAtRadius(mun.Sma, ut, true).Value;
            var there = arc.Position(crossing);
            // The Mun's orbit is circular and equatorial with its periapsis on the x axis: its mean anomaly is its longitude.
            mun.MeanAnomalyAtEpoch = Math.Atan2(there.Y, there.X) - crossing * Math.Sqrt(KerbinGm / Math.Pow(mun.Sma, 3));
            mun.SoiRadius = soi;
            return snapshot;
        }

        [Fact]
        public void When_the_cheapest_window_can_t_be_aimed_the_next_one_that_can_is_given()
        {
            // The Mun, at its own sphere of influence, sits across the cheapest window's arc but not the next one's.
            var snapshot = A107MunInTheWay(2429559.1);
            var cheapest = Solved(A107(), OneBurn(snapshot));
            var node = Solved(snapshot, OneBurn(snapshot));
            Assert.NotEqual(D(cheapest, "ut"), D(node, "ut"));
            Assert.InRange(D(node, "target_pe_actual"), 29900, 30100);
        }

        [Fact]
        public void A_target_no_window_can_reach_fails_with_how_far_its_orbit_is_inclined_to_the_craft_s()
        {
            // Every window's arc, and every burn tried round it, meets the Mun first.
            var snapshot = A107MunInTheWay(11000000);
            var ask = new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut + 30 } };
            Assert.Equal("found no transfer: Minmus's orbit is inclined 6.0° to yours; match_plane first, or try the next window", BlockCalls.EjectionBurn(ask, snapshot)["error"]);
        }

        // ---- the wall clock ----

        [Fact]
        [Trait("Category", "Timing")]
        public void Each_solve_runs_under_100_ms()
        {
            var snapshot = Circular();
            var parking = BlockTestStart();
            parking.Orbit.Inclination = 25;
            parking.Orbit.Lan = 40;
            Timing.Within(100, () => Solved(snapshot, ToOrbit(200000)));
            Timing.Within(100, () => Solved(snapshot, Inclined("intercept")));
            Timing.Within(100, () => Solved(snapshot, Inclined("intercept", capture: false)));
            Timing.Within(100, () => Solved(snapshot, Eccentric()));
            Timing.Within(100, () => Solved(snapshot, Inclined("rendezvous")));
            Timing.Within(100, () => Solved(snapshot, Inclined("rendezvous", capture: false)));
            Timing.Within(100, () => Solved(parking, new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 20000.0 }, { "earliest_ut", parking.Ut + 30 } }));
            var a107 = A107();
            Timing.Within(100, () => Solved(a107, new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 30000.0 }, { "earliest_ut", a107.Ut + 30 } }));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_search_that_tries_every_window_and_every_aim_and_fails_runs_under_500_ms()
        {
            // The worst case #611 added: three windows, each with the prograde scan at the Lambert normal and six normals round
            // it, about 1,100 patched-conic trials (before it, one window's 121, about 50 ms). Measured at about 430 ms.
            var blocked = A107MunInTheWay(11000000);
            var ask = new Dictionary<string, object> { { "target", "Minmus" }, { "target_pe", 30000.0 }, { "earliest_ut", blocked.Ut + 30 } };
            Timing.Within(500, () => Assert.NotEqual("", BlockCalls.EjectionBurn(ask, blocked)["error"]));
        }

        // ---- failures: "found no transfer: <why>" ----

        [Fact]
        public void An_intercept_of_a_body_has_no_transfer()
        {
            var input = new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", 30000.0 }, { "mode", "intercept" } };
            Assert.Equal("found no transfer: intercept is for a target orbit; a body is met where it is", BlockCalls.EjectionBurn(input, Circular())["error"]);
        }

        [Fact]
        public void A_target_body_and_a_target_orbit_together_have_no_transfer()
        {
            var input = ToOrbit(200000);
            input["target"] = "Mun";
            Assert.Equal("found no transfer: give a target body or a target orbit, not both", BlockCalls.EjectionBurn(input, Circular())["error"]);
        }

        [Theory]
        [InlineData(0.0, 0.0, "found no transfer: the target orbit must be closed: a semi-major axis over 0 and an eccentricity from 0 to under 1")]
        [InlineData(800000.0, 1.0, "found no transfer: the target orbit must be closed: a semi-major axis over 0 and an eccentricity from 0 to under 1")]
        [InlineData(500000.0, 0.0, "found no transfer: the target orbit's periapsis is below Kerbin's surface")]
        [InlineData(90000000.0, 0.0, "found no transfer: the target orbit's apoapsis is outside Kerbin's sphere of influence")]
        [InlineData(double.NaN, 0.0, "found no transfer: the target orbit's elements must be numbers")]
        public void A_target_orbit_that_is_not_a_closed_orbit_inside_the_sphere_of_influence_has_no_transfer(double sma, double ecc, string error)
        {
            var input = ToOrbit(200000);
            input["target_sma"] = sma;
            input["target_ecc"] = ecc;
            Assert.Equal(error, BlockCalls.EjectionBurn(input, Circular())["error"]);
        }

        [Fact]
        public void A_mode_or_capture_it_can_t_read_has_no_transfer()
        {
            var input = ToOrbit(200000);
            input["mode"] = "flyby";
            Assert.Equal("found no transfer: mode must be \"rendezvous\" or \"intercept\", not flyby", BlockCalls.EjectionBurn(input, Circular())["error"]);
            input = ToOrbit(200000);
            input["capture"] = "yes";
            Assert.Equal("found no transfer: capture must be true or false", BlockCalls.EjectionBurn(input, Circular())["error"]);
            input = ToOrbit(200000);
            input["target_inc"] = "steep";
            Assert.Equal("found no transfer: target_inc must be a number", BlockCalls.EjectionBurn(input, Circular())["error"]);
            input = ToOrbit(200000);
            input.Remove("target_epoch");
            Assert.Equal("found no transfer: target_epoch is required with target_sma: the target orbit takes all seven elements", BlockCalls.EjectionBurn(input, Circular())["error"]);
        }
    }
}
