// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The broken-plane transfer (#619): to a body far out of the craft's plane, <see cref="Transfer"/> also costs an ejection in
    /// the craft's plane with a plane change where the arc is slow, and the cheaper total wins; <c>transfer_to</c> flies the
    /// plane change with CORRECTIONBURN at its planned time. a107's geometry (m20261101-140219-a107): the craft at 80 km and 0.1°,
    /// Minmus arriving about 95° past its ascending node, 6° out of the craft's plane.
    /// </summary>
    public sealed class BrokenPlaneTests
    {
        private const double TargetPe = 30000;

        private static Dictionary<string, object> Ask(GameSnapshot snapshot, string target = "Minmus", bool planeChange = true)
        {
            return new Dictionary<string, object> { { "target", target }, { "target_pe", TargetPe }, { "earliest_ut", snapshot.Ut + 30 }, { "plane_change", planeChange } };
        }

        private static IDictionary<string, object> Solved(GameSnapshot snapshot, Dictionary<string, object> ask)
        {
            var result = BlockCalls.EjectionBurn(ask, snapshot);
            Assert.Equal("", result["error"]);
            return result;
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        private static double Total(IDictionary<string, object> plan)
        {
            return D(plan, "dv") + D(plan, "plane_dv") + D(plan, "arrival_dv");
        }

        /// <summary>How far, deg, Minmus is out of the craft's plane at <paramref name="ut"/>.</summary>
        private static double OutOfPlane(GameSnapshot snapshot, double ut)
        {
            var system = new BodySystem(snapshot.Bodies);
            var minmus = system.OrbitOf(system.Find("Minmus")).Position(ut).Normalized;
            return Math.Abs(Math.Asin(Vec3.Dot(minmus, Craft(snapshot).W))) * 180 / Math.PI;
        }

        private static Conic Craft(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            var kerbin = snapshot.Bodies.Single(b => b.Name == "Kerbin");
            return Conic.FromElements(kerbin.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        /// <summary>a107 with Minmus moved back along its orbit so that it reaches the craft's plane where the one-burn plan meets it.</summary>
        internal static GameSnapshot A107AtANode()
        {
            var snapshot = TransferTests.A107();
            var arrival = D(Solved(snapshot, Ask(snapshot, planeChange: false)), "arrival_ut");
            var system = new BodySystem(snapshot.Bodies);
            var orbit = system.OrbitOf(system.Find("Minmus"));
            var node = Vec3.Cross(Craft(snapshot).W, orbit.W).Normalized;
            var r = orbit.Position(arrival);
            // Minmus's orbit is circular, so its mean anomaly moves with its argument of latitude.
            snapshot.Bodies.Single(b => b.Name == "Minmus").MeanAnomalyAtEpoch -= Math.Atan2(Vec3.Dot(Vec3.Cross(node, r), orbit.W), Vec3.Dot(node, r));
            return snapshot;
        }

        /// <summary>The ejection flown on the craft's orbit (<paramref name="error"/> m/s off, inertial) and the snapshot five minutes on.</summary>
        private static GameSnapshot Ejected(GameSnapshot snapshot, IDictionary<string, object> plan, Vec3 error)
        {
            var craft = Craft(snapshot);
            var ut = D(plan, "ut");
            Vec3 r, v;
            craft.State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var burn = along * D(plan, "prograde") + up * D(plan, "normal") + Vec3.Cross(along, up) * D(plan, "radial");
            var ejected = Conic.FromState(craft.Mu, r, v + burn + error, ut);
            var coast = new GameSnapshot { Ut = ut + 300, Situation = "ORBITING", Body = "Kerbin", Orbit = CorrectionBurnTests.Elements(ejected, "Kerbin") };
            coast.Bodies.AddRange(snapshot.Bodies);
            return coast;
        }

        /// <summary>The plane change transfer_to asks for (transfer_to_plane_ask): at plane_ut, from the Lambert burn to arrive at arrival_ut.</summary>
        private static IDictionary<string, object> PlaneChange(GameSnapshot coast, IDictionary<string, object> plan)
        {
            return BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Minmus" }, { "target_pe", TargetPe }, { "earliest_ut", D(plan, "plane_ut") }, { "latest_ut", D(plan, "plane_ut") },
                { "aim_ut", D(plan, "arrival_ut") },
            }, coast);
        }

        // ---- a107: the broken plane wins ----

        [Fact]
        public void On_a107_s_geometry_the_broken_plane_costs_at_least_150_m_s_less_than_the_one_burn_ejection_and_is_chosen()
        {
            var snapshot = TransferTests.A107();
            var oneBurn = Solved(snapshot, Ask(snapshot, planeChange: false));
            var plan = Solved(snapshot, Ask(snapshot));
            // The #611 agent's figures, re-derived: Minmus 6° out of the craft's plane at arrival, the one-burn ejection about
            // 1,205 m/s; in plane the ejection is about 930 m/s, and the plane change where the arc is slow about 40 m/s.
            Assert.InRange(OutOfPlane(snapshot, D(oneBurn, "arrival_ut")), 5.5, 6.0);
            Assert.InRange(D(oneBurn, "dv"), 1150, 1250);
            Assert.Equal(-1.0, D(oneBurn, "plane_ut"));
            Assert.Equal(0.0, D(oneBurn, "plane_dv"));
            Assert.True(D(plan, "plane_ut") > D(plan, "ut") && D(plan, "plane_ut") < D(plan, "encounter_ut"));
            Assert.InRange(D(plan, "dv"), 900, 950);
            Assert.InRange(Math.Abs(D(plan, "normal")), 0, 0.01);
            Assert.InRange(D(plan, "plane_dv"), 20, 80);
            Assert.InRange(D(plan, "target_pe_actual"), TargetPe - 100, TargetPe + 100);
            Assert.True(Total(plan) <= Total(oneBurn) - 150, "broken plane " + Total(plan) + " m/s against one burn " + Total(oneBurn) + " m/s");
            // The departure alone (capture false) is costed the same way.
            var ask = Ask(snapshot);
            ask["capture"] = false;
            var alone = Solved(snapshot, ask);
            Assert.True(D(alone, "plane_ut") > 0);
            Assert.InRange(D(alone, "dv") + D(alone, "plane_dv"), 900, 1000);
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(0.3, -0.2, 0.4)]
        public void Transfer_to_s_plane_change_at_its_planned_time_arrives_within_tolerance_of_target_pe(double x, double y, double z)
        {
            // As transfer_to flies it: the ejection (exactly, or 0.5 m/s off as a real burn is), then CORRECTIONBURN at plane_ut,
            // which the in-plane path does not reach Minmus without; its node flown arrives at target_pe.
            var snapshot = TransferTests.A107();
            var plan = Solved(snapshot, Ask(snapshot));
            var coast = Ejected(snapshot, plan, new Vec3(x, y, z));
            Assert.StartsWith("found no correction: the trajectory from the burn does not reach Minmus", (string)BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Minmus" }, { "target_pe", TargetPe }, { "earliest_ut", D(plan, "plane_ut") },
            }, coast)["error"]);
            var change = PlaneChange(coast, plan);
            Assert.Equal("", change["error"]);
            Assert.Equal(D(plan, "plane_ut"), D(change, "ut"));
            Assert.InRange(D(change, "dv"), D(plan, "plane_dv") - 15, D(plan, "plane_dv") + 15);
            double pe, inc;
            CorrectionBurnTests.Flown(coast, change, "Minmus", out pe, out inc);
            Assert.InRange(pe, TargetPe - 1000, TargetPe + 1000);
        }

        // ---- in plane: as before ----

        public static IEnumerable<object[]> InPlane()
        {
            yield return new object[] { "Mun", TransferTests.A107() };
            yield return new object[] { "Minmus", A107AtANode() };
        }

        [Theory]
        [MemberData(nameof(InPlane))]
        public void An_in_plane_target_chooses_and_costs_exactly_as_the_one_burn_plan(string target, GameSnapshot snapshot)
        {
            var oneBurn = Solved(snapshot, Ask(snapshot, target, planeChange: false));
            Assert.True(OutOfPlane(snapshot, D(oneBurn, "arrival_ut")) < 1 || target == "Mun");
            foreach (var capture in new[] { true, false })
            {
                var ask = Ask(snapshot, target);
                ask["capture"] = capture;
                var off = Ask(snapshot, target, planeChange: false);
                off["capture"] = capture;
                var plan = Solved(snapshot, ask);
                Assert.Equal(Solved(snapshot, off).OrderBy(kv => kv.Key).ToList(), plan.OrderBy(kv => kv.Key).ToList());
                Assert.Equal(-1.0, D(plan, "plane_ut"));
            }
        }

        [Fact]
        public void The_broken_plane_is_costed_from_1_degree_out_of_plane()
        {
            Assert.Equal(1, Transfer.BrokenPlaneDegrees);
            // Minmus near its node: under the threshold, so the one-burn plan stands (the theory above); at a107's 6°, over it.
            var near = A107AtANode();
            Assert.True(OutOfPlane(near, D(Solved(near, Ask(near, planeChange: false)), "arrival_ut")) < Transfer.BrokenPlaneDegrees);
        }

        [Fact]
        public void Plane_change_must_be_true_or_false()
        {
            var snapshot = TransferTests.A107();
            var ask = Ask(snapshot);
            ask["plane_change"] = "yes";
            Assert.Equal("found no transfer: plane_change must be true or false", BlockCalls.EjectionBurn(ask, snapshot)["error"]);
        }

        // ---- the wall clock ----

        [Fact]
        [Trait("Category", "Timing")]
        public void The_broken_plane_search_and_its_plane_change_each_run_under_100_ms()
        {
            var snapshot = TransferTests.A107();
            var plan = Solved(snapshot, Ask(snapshot));
            var coast = Ejected(snapshot, plan, new Vec3(0, 0, 0));
            Timing.Within(100, () => Solved(snapshot, Ask(snapshot)));
            Timing.Within(100, () => Assert.Equal("", PlaneChange(coast, plan)["error"]));
            var near = A107AtANode();
            Timing.Within(100, () => Solved(near, Ask(near)));
        }
    }
}
