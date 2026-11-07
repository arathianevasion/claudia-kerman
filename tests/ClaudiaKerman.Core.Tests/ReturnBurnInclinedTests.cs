// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #816: <c>return</c> from an orbit inclined to the moon's own plane (found from a 66.43° Mun orbit, which burned 735 m/s where
    /// the Delta-v Map says 310). Its windows differ in cost, the cheap one coming round every half orbit of the moon, so the block
    /// keeps the windows within the cheapest plus 50 m/s and takes the nearest landing, a 5 km tie to the sooner; "auto" max_wait runs
    /// to the next cheap alignment plus 3.5 h; with no target and no window given it waits for the cheap window. Through
    /// <see cref="BlockCalls.ReturnBurn"/> with stock body values, as <see cref="ReturnBurnTests"/>.
    /// </summary>
    public sealed class ReturnBurnInclinedTests
    {
        private const double KscLat = -0.0972;
        private const double KscLng = -74.5577;
        /// <summary>The acceptance's figure for a Mun return: under about 320 m/s, against the cheapest window's 266.</summary>
        private const double MunCeilingMs = 320;

        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };

        private static SnapshotBody Mun(double phase)
        {
            return new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = phase };
        }

        private static SnapshotBody Minmus(double phase)
        {
            return new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = phase,
            };
        }

        /// <summary>A circular orbit <paramref name="altitude"/> m over the moon at UT 1000, inclined <paramref name="inc"/>° with its node at <paramref name="lan"/>°.</summary>
        private static GameSnapshot Orbiting(SnapshotBody moon, double inc, double lan, double altitude = 30000, double ut = 1000)
        {
            var snapshot = new GameSnapshot { Ut = ut, Situation = "ORBITING", Body = moon.Name };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = moon.Radius + altitude, Eccentricity = 0, Inclination = inc, Lan = lan, Body = moon.Name, Periapsis = altitude, Apoapsis = altitude, Epoch = ut,
            };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, moon });
            return snapshot;
        }

        /// <summary>The body interface as the frame reads it: longitude with the frame's angle (<paramref name="handed"/> 1) or against it (-1).</summary>
        private sealed class FrameBody : PlanningBodyBase
        {
            private readonly double _handed;

            public FrameBody(double handed = 1)
            {
                _handed = handed;
            }

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(_handed * Math.Atan2(y, x) * 180 / Math.PI);
                return true;
            }
        }

        private static IDictionary<string, object> Aimed(GameSnapshot snapshot, object maxWait = null)
        {
            var input = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut }, { "lat", KscLat }, { "lng", KscLng } };
            if (maxWait != null) input["max_wait"] = maxWait;
            return BlockCalls.ReturnBurn(input, snapshot, new FrameBody(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
        }

        private static IDictionary<string, object> NoTarget(GameSnapshot snapshot, bool windowGiven)
        {
            var input = new Dictionary<string, object> { { "target_pe", 35000.0 }, { "earliest_ut", snapshot.Ut } };
            if (!windowGiven) input["cheap_window"] = true;
            return BlockCalls.ReturnBurn(input, snapshot);
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        private static void Setup(GameSnapshot snapshot, out BodySystem system, out SnapshotBody moon, out Conic orbit)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            Assert.NotNull(setup);
            system = setup.System;
            moon = setup.Body;
            orbit = setup.Start;
        }

        /// <summary>The periapsis (m above sea level) the node's path has round Kerbin, flown afresh through the patches.</summary>
        private static double FlownPe(GameSnapshot snapshot, IDictionary<string, object> node)
        {
            BodySystem system;
            SnapshotBody moon;
            Conic orbit;
            Setup(snapshot, out system, out moon, out orbit);
            var ut = D(node, "ut");
            var arrival = SingleShooting.Arrive(system, moon, orbit, ut, SingleShooting.FromNodeFrame(orbit, ut, D(node, "prograde"), D(node, "normal"), D(node, "radial")), system.Find("Kerbin"), 2);
            Assert.NotNull(arrival);
            return arrival.Orbit.PeR - Kerbin.Radius;
        }

        /// <summary>
        /// The acceptance: from a 66.43° orbit (the flight's) and a polar one, at Mun phases and nodes round the orbit, a return to KSC burns
        /// under about 320 m/s, its burn over the cheapest window (aim_dv) within the 50 m/s cap, and reaches target_pe. Before #816 the
        /// same returns burned up to 735 m/s, and some answered thousands.
        /// </summary>
        [Theory]
        [InlineData(66.43, 0, 1.7)]
        [InlineData(66.43, 30, 0.9)]
        [InlineData(66.43, 90, 1.7)]
        [InlineData(66.43, 150, 3.0)]
        [InlineData(66.43, 240, 4.5)]
        [InlineData(66.43, 300, 5.5)]
        [InlineData(90, 0, 0.9)]
        [InlineData(90, 120, 3.0)]
        [InlineData(90, 240, 4.5)]
        public void An_inclined_mun_return_to_ksc_costs_no_more_than_the_cheap_window_plus_the_cap(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), inc, lan);
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 264, MunCeilingMs);
            Assert.InRange(D(node, "aim_dv"), 0, ReturnAim.CostCapMs);
            Assert.True(D(node, "error_km") >= 0, "the landing is predicted");
            Assert.InRange(FlownPe(snapshot, node), 30000 - 1000, 30000 + 1000);
            Assert.Contains("; windows ", (string)node["plan_log"]);
            Assert.Contains("cheapest 26", (string)node["plan_log"]);
        }

        /// <summary>
        /// Where a capped window's landing falls near KSC, the rule takes it, near the cheap alignment. Off KSC's latitude the least burn's
        /// path keeps a couple of degrees, so the best of these is tens of km, which the Course Correction takes out.
        /// </summary>
        [Theory]
        [InlineData(66.43, 120, 1.7)]
        [InlineData(66.43, 240, 1.7)]
        [InlineData(66.43, 300, 1.7)]
        [InlineData(90, 240, 4.5)]
        public void The_cost_cap_rule_takes_a_window_landing_near_ksc_by_the_cheap_alignment(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), inc, lan);
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "error_km"), 0, 30);
            Assert.InRange(D(node, "aim_dv"), 0, ReturnAim.CostCapMs);
            BodySystem system;
            SnapshotBody moon;
            Conic orbit;
            Setup(snapshot, out system, out moon, out orbit);
            var ut = D(node, "ut");
            // Within about 3.5 h of a cheap alignment (the cap reaches a little past it at these inclinations).
            Assert.InRange(ReturnAim.NextAlignment(system, moon, orbit, ut - 4.5 * 3600) - ut, -4.5 * 3600, 4.5 * 3600);
        }

        /// <summary>A longer max_wait sees more windows: the one it takes lands no further off (a 5 km tie aside), or the cheapest it found moved the cap under the shorter's pick.</summary>
        [Theory]
        [InlineData(66.43, 120, 1.7)]
        [InlineData(66.43, 0, 1.7)]
        [InlineData(90, 240, 4.5)]
        public void A_longer_max_wait_is_never_worse_on_the_rule(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), inc, lan);
            IDictionary<string, object> shorter = null;
            foreach (var hours in new[] { 4.0, 8.0, 14.0, 22.0 })
            {
                var node = Aimed(snapshot, hours * 3600);
                Assert.Equal("", node["error"]);
                if (shorter != null)
                {
                    var nearer = D(node, "error_km") <= D(shorter, "error_km") + ReturnAim.TieKm;
                    var capMoved = D(shorter, "dv") > D(node, "dv") - D(node, "aim_dv") + ReturnAim.CostCapMs;
                    Assert.True(nearer || capMoved, hours + " h lands " + D(node, "error_km") + " km off against " + D(shorter, "error_km") + " km");
                }
                shorter = node;
            }
        }

        /// <summary>The rule's tie: landings within 5 km of the nearest count as one, and the sooner window goes.</summary>
        [Fact]
        public void A_tie_within_5_km_goes_to_the_sooner_window()
        {
            var nearestLater = new ReturnPlan { Ut = 3000, ErrorKm = 4 };
            var soonerTied = new ReturnPlan { Ut = 1000, ErrorKm = 8.9 };
            var soonerFar = new ReturnPlan { Ut = 500, ErrorKm = 9.5 };
            Assert.Same(soonerTied, ReturnAim.Tied(new List<ReturnPlan> { nearestLater, soonerTied, soonerFar }));
            Assert.Same(nearestLater, ReturnAim.Tied(new List<ReturnPlan> { nearestLater, soonerFar }));
        }

        /// <summary>
        /// No target and no window given (#816, grilled): the next pass costs 783 m/s here, so the block waits 7 h for the cheap window,
        /// 266 m/s. Claudia's own window "next" keeps its meaning and leaves on the next pass.
        /// </summary>
        [Theory]
        [InlineData(66.43, 30, 0.9)]
        [InlineData(66.43, 90, 1.7)]
        [InlineData(90, 0, 3.0)]
        public void With_no_target_and_no_window_an_inclined_return_waits_for_the_cheap_window(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), inc, lan);
            var next = NoTarget(snapshot, true);
            Assert.Equal("", next["error"]);
            var plain = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut });
            Assert.Equal(plain.Ut, D(next, "ut"));
            Assert.True(plain.DeltaV > 400, "this pass is a dear one: " + plain.DeltaV);

            var cheap = NoTarget(snapshot, false);
            Assert.Equal("", cheap["error"]);
            Assert.InRange(D(cheap, "dv"), 264, 264 + ReturnAim.CostCapMs);
            Assert.True(D(cheap, "ut") > plain.Ut + 3600, "it waits for the cheap window");
            Assert.Contains("; windows ", (string)cheap["plan_log"]);
        }

        /// <summary>When the next pass is already within the cap, the block leaves on it, with no target or window as with one.</summary>
        [Fact]
        public void With_no_target_a_cheap_next_pass_leaves_on_it()
        {
            var snapshot = Orbiting(Mun(1.7), 66.43, 0);
            var plain = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut });
            Assert.InRange(plain.DeltaV, 264, 290);
            Assert.Equal(plain.Ut, D(NoTarget(snapshot, false), "ut"));
        }

        /// <summary>From the Mun's own plane nothing changes: no window note, the burn as before #816 (<see cref="ReturnBurnTests"/> holds the rest).</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void From_the_moons_plane_the_windows_step_as_before(double inc)
        {
            var snapshot = Orbiting(Mun(1.7), inc, 0);
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.DoesNotContain("; windows ", (string)node["plan_log"]);
            Assert.InRange(D(node, "error_km"), 0, 5);
            Assert.InRange(D(node, "aim_dv"), 0, 2);
            var plain = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut });
            Assert.Equal(plain.Ut, D(NoTarget(snapshot, false), "ut"));
        }

        /// <summary>
        /// Minmus with its own figures: its cheapest is about 160 m/s and its alignment can be days off. From a steep orbit the cap holds
        /// as at the Mun; from Kerbin's equator (6° off Minmus's plane) the plane costs little, so "auto" stays one turn (about 6.1 h).
        /// </summary>
        [Theory]
        [InlineData(60, 0, 0.9)]
        [InlineData(60, 90, 1.7)]
        [InlineData(90, 120, 4.5)]
        public void An_inclined_minmus_return_costs_no_more_than_its_cheap_window_plus_the_cap(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Minmus(phase), inc, lan, 20000);
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 155, 160 + ReturnAim.CostCapMs);
            Assert.InRange(D(node, "aim_dv"), 0, ReturnAim.CostCapMs);
            Assert.Contains("cheapest 15", (string)node["plan_log"]);
        }

        [Fact]
        public void A_minmus_return_in_kerbins_equator_waits_one_turn_at_most()
        {
            var snapshot = Orbiting(Minmus(5.5), 0, 0, 20000);
            BodySystem system;
            SnapshotBody moon;
            Conic orbit;
            Setup(snapshot, out system, out moon, out orbit);
            Assert.InRange(ReturnAim.PlaneAngle(system, moon, orbit, snapshot.Ut), 5.5, 6.5);
            double alignment;
            Assert.False(ReturnAim.AlignmentCosts(system, moon, orbit, snapshot.Ut, snapshot.Ut, 30000, out alignment));
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "ut"), snapshot.Ut, snapshot.Ut + 6.2 * 3600 + 150);
            Assert.InRange(D(node, "error_km"), 0, 10);
        }

        /// <summary>"auto" from an inclined orbit: to the next cheap alignment plus 3.5 h, so up to about 23 h at the Mun.</summary>
        [Theory]
        [InlineData(66.43, 0, 1.7)]
        [InlineData(66.43, 30, 0.9)]
        [InlineData(90, 0, 3.0)]
        public void Auto_max_wait_from_an_inclined_orbit_runs_to_the_cheap_alignment_plus_3_5_h(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), inc, lan);
            BodySystem system;
            SnapshotBody moon;
            Conic orbit;
            Setup(snapshot, out system, out moon, out orbit);
            var wait = ReturnAim.AutoWaitFor(snapshot, system.Find("Kerbin"), snapshot.Ut, 30000, new FrameBody());
            Assert.Equal(ReturnAim.NextAlignment(system, moon, orbit, snapshot.Ut) - snapshot.Ut + ReturnAim.PastAlignmentS, wait, 3);
            Assert.InRange(wait / 3600, 3.5, 23.0);
        }

        /// <summary>
        /// #802: the landing's turn depends on which way the planet's frame turns. The plan's wait reads it from the planning body as the
        /// block does: about 7.1 h at the Mun in a frame whose longitude grows with its angle, 5.2 h in a mirrored one; it used to take 7.1 h for both.
        /// </summary>
        [Fact]
        public void The_plans_auto_wait_takes_the_frames_handedness()
        {
            var snapshot = Orbiting(Mun(1.7), 0, 0);
            var system = new BodySystem(snapshot.Bodies);
            var kerbin = system.Find("Kerbin");
            var mun = system.Find("Mun");
            var turning = ReturnAim.AutoWaitFor(snapshot, kerbin, snapshot.Ut, 30000, new FrameBody(1));
            var mirrored = ReturnAim.AutoWaitFor(snapshot, kerbin, snapshot.Ut, 30000, new FrameBody(-1));
            Assert.Equal(ReturnAim.LongitudeTurn(system, kerbin, mun, 1), turning, 6);
            Assert.Equal(ReturnAim.LongitudeTurn(system, kerbin, mun, -1), mirrored, 6);
            Assert.InRange(turning / 3600, 7.0, 7.2);
            Assert.InRange(mirrored / 3600, 5.1, 5.3);
        }

        /// <summary>
        /// The 6,196 m/s answer (#816's evidence): from a 66.43° orbit at some Mun phases and nodes, the first window's solve ended on a
        /// burn of 4,000 to 7,500 m/s at UT 1000 with no landing, and the aimed return flew it. The solve now starts again from a burn
        /// straight onto the way home, and the aimed return compares windows.
        /// </summary>
        [Theory]
        [InlineData(90, 1.7)]
        [InlineData(150, 3.0)]
        [InlineData(30, 0.9)]
        [InlineData(240, 4.5)]
        [InlineData(300, 5.5)]
        public void The_thousands_of_m_s_answer_at_ut_1000_does_not_come_back(double lan, double phase)
        {
            var snapshot = Orbiting(Mun(phase), 66.43, lan);
            var first = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 30000, EarliestUt = snapshot.Ut });
            Assert.Null(first.Error);
            Assert.InRange(first.DeltaV, 264, 1000);
            var node = Aimed(snapshot);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 264, MunCeilingMs);
            Assert.True(D(node, "error_km") >= 0, "the aim has a landing");
        }

        /// <summary>The search's cost per return (stated in #816): the worst Mun case above, with every window solved and the capped ones predicted.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void An_inclined_mun_return_to_ksc_is_searched_in_under_a_second()
        {
            var snapshot = Orbiting(Mun(4.5), 66.43, 270);
            Timing.Within(1000, () => Assert.Equal("", Aimed(snapshot)["error"]));
        }
    }
}
