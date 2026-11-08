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
    /// #880: a return to Kerbin from an orbit inclined to the Mun's takes the earliest window it can afford that lands within 50 km of the target, up to the cheap alignment plus 60 h, and #816's rule
    /// when none does. Through <see cref="BlockCalls.ReturnBurn"/>, as <see cref="ReturnBurnInclinedTests"/>; <c>dv_left</c> is what turns the search on, so that file's returns keep #816's rule.
    /// </summary>
    public sealed class ReturnWindowReachTests
    {
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

        private static GameSnapshot Orbiting(double inc, double lan, double phase)
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = 230000, Eccentricity = 0, Inclination = inc, Lan = lan, Body = "Mun", Periapsis = 30000, Apoapsis = 30000, Epoch = 1000 };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, Mun(phase) });
            return snapshot;
        }

        /// <summary>A capsule with chutes: nothing is kept back for a powered landing.</summary>
        private sealed class Chuted : IReentryCraft
        {
            private readonly ReentrySim.Capsule _capsule = new ReentrySim.Capsule();
            public double MassKg { get { return _capsule.MassKg; } }
            public double DragArea(double mach, double densitySpeed) { return _capsule.DragArea(mach, densitySpeed); }
            public double LiftArea(double mach, double angleOfAttack) { return _capsule.LiftArea(mach, angleOfAttack); }
            public IList<ReentryChute> Chutes { get { return new[] { new ReentryChute() }; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        private sealed class FrameBody : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(Math.Atan2(y, x) * 180 / Math.PI);
                return true;
            }
        }

        private static IDictionary<string, object> ToKsc(GameSnapshot snapshot, double? dvLeft)
        {
            var input = new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", snapshot.Ut }, { "lat", ReturnAim.KscLatitude }, { "lng", ReturnAim.KscLongitude } };
            if (dvLeft.HasValue) input["dv_left"] = dvLeft.Value;
            return BlockCalls.ReturnBurn(input, snapshot, new FrameBody(), new ReentrySim.Atmosphere(), new Chuted());
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        private static double WaitH(GameSnapshot snapshot, IDictionary<string, object> node)
        {
            return (D(node, "ut") - snapshot.Ut) / 3600;
        }

        /// <summary>The cheap alignment's alignment + 60 h, the hours the search may wait from the snapshot.</summary>
        private static double CapHours(GameSnapshot snapshot)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            return (ReturnAim.NextAlignment(setup.System, setup.Body, setup.Start, setup.Now) - snapshot.Ut) / 3600 + 60;
        }

        [Fact]
        public void A_return_with_no_stated_target_aims_at_ksc_and_with_little_spare_waits_for_a_band_landing_within_50_km()
        {
            var snapshot = Orbiting(66.43, 0, 3.0);
            Assert.Equal(HomeLanding.Ksc, BlockCalls.LandingTarget(new Dictionary<string, object> { { "planet", "Kerbin" } }, snapshot)["target"]);

            var node = ToKsc(snapshot, 390);

            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "error_km"), 0, ReturnAim.ReachKm);
            Assert.InRange(WaitH(snapshot, node), 20, CapHours(snapshot));
            Assert.InRange(D(node, "aim_dv"), 0, ReturnAim.CostCapMs);
            var note = (string)node["aim_note"];
            Assert.Contains("window waits " + WaitH(snapshot, node).ToString("0.0", CultureInfo.InvariantCulture) + " h", note);
            Assert.Contains(D(node, "dv").ToString("0.0", CultureInfo.InvariantCulture) + " m/s", note);
            Assert.Contains(D(node, "error_km").ToString("0.0", CultureInfo.InvariantCulture) + " km from the target", note);
        }

        [Fact]
        public void With_plenty_of_spare_it_leaves_earlier_on_a_costlier_window_landing_within_50_km_and_keeps_the_buffer()
        {
            var snapshot = Orbiting(66.43, 0, 3.0);
            var little = ToKsc(snapshot, 390);

            var plenty = ToKsc(snapshot, 480);

            Assert.Equal("", plenty["error"]);
            Assert.InRange(D(plenty, "error_km"), 0, ReturnAim.ReachKm);
            Assert.True(D(plenty, "ut") < D(little, "ut") - 3600, "it leaves sooner: " + WaitH(snapshot, plenty) + " h against " + WaitH(snapshot, little));
            Assert.True(D(plenty, "aim_dv") > ReturnAim.CostCapMs, "a window over the cheapest + 50: " + D(plenty, "aim_dv"));
            // The spend rule (the reserve), then the brief's buffer, the larger of 50 m/s or 10%.
            var spare = DeorbitBurn.SpareDv(480, 0, 0, null) - Math.Max(50, 0.1 * 480);
            Assert.True(D(plenty, "dv") <= spare, D(plenty, "dv") + " m/s, keeping the buffer: " + spare);
        }

        [Fact]
        public void With_no_window_within_50_km_before_the_cap_it_falls_back_to_816s_rule_and_reports_the_miss()
        {
            // From this orbit the bands near the Mun don't land near KSC at any cost a craft of 600 m/s can pay.
            var snapshot = Orbiting(90, 120, 3.0);
            var ruleOf816 = ToKsc(snapshot, null);

            var node = ToKsc(snapshot, 600);

            Assert.Equal("", node["error"]);
            Assert.Equal(D(ruleOf816, "ut"), D(node, "ut"), 3);
            Assert.Equal(D(ruleOf816, "dv"), D(node, "dv"), 3);
            Assert.True(D(node, "error_km") > ReturnAim.ReachKm, "a miss: " + D(node, "error_km"));
            var note = (string)node["aim_note"];
            Assert.Contains("no window it can afford lands within 50 km in the ", note);
            Assert.Contains(D(node, "error_km").ToString("0.0", CultureInfo.InvariantCulture) + " km from the target", note);
            Assert.Contains("wait", (string)node["plan_log"]);
        }

        [Theory]
        [InlineData(66.43, 30, 0.9)]
        [InlineData(90, 0, 3.0)]
        public void A_stated_no_target_behaves_as_816(double inc, double lan, double phase)
        {
            var snapshot = Orbiting(inc, lan, phase);
            var statedNone = new Dictionary<string, object> { { "target_pe", 35000.0 }, { "earliest_ut", snapshot.Ut }, { "cheap_window", true } };
            var before = BlockCalls.ReturnBurn(statedNone, snapshot, new FrameBody());
            var withDeltaV = new Dictionary<string, object>(statedNone) { { "dv_left", 5000.0 } };

            var node = BlockCalls.ReturnBurn(withDeltaV, snapshot, new FrameBody());

            Assert.Equal("", node["error"]);
            Assert.Equal(D(before, "ut"), D(node, "ut"), 6);
            Assert.Equal(D(before, "dv"), D(node, "dv"), 6);
            Assert.InRange(D(node, "dv"), 264, 264 + ReturnAim.CostCapMs);
            Assert.Equal("", node["aim_note"]);
        }

        [Fact]
        public void The_missions_landing_target_counts_as_stated_for_an_auto_return_and_for_the_plan()
        {
            var toKsc = WithStockDv(Orbiting(66.43, 0, 3.0), 480);
            var snapshot = WithStockDv(Orbiting(66.43, 0, 3.0), 480);
            snapshot.LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = 3.1, Longitude = 24.6 };

            var target = (IDictionary<string, object>)BlockCalls.LandingTarget(new Dictionary<string, object> { { "planet", "Kerbin" }, { "target_pe", 35000.0 } }, snapshot)["target"];

            Assert.Equal(3.1, Convert.ToDouble(target["lat"], CultureInfo.InvariantCulture));
            Assert.Equal(24.6, Convert.ToDouble(target["lng"], CultureInfo.InvariantCulture));
            var wait = BurnPoints.First("local res is return().", snapshot, 1000, 5, new FrameBody());
            Assert.NotNull(wait);
            Assert.True(wait.PickSpanSeconds > 60 * 3600, "the plan covers the search to the cap: " + wait.PickSpanSeconds);
            Assert.NotEqual(BurnPoints.First("local res is return().", toKsc, 1000, 5, new FrameBody()).DepartureUt, wait.DepartureUt);
        }

        [Fact]
        public void The_missions_landing_target_leaves_deorbit_as_it_was()
        {
            var snapshot = Orbiting(66.43, 0, 3.0);
            snapshot.LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = 3.1, Longitude = 24.6 };

            Assert.Equal(HomeLanding.Ksc, BlockCalls.LandingTarget(new Dictionary<string, object> { { "planet", "Kerbin" } }, snapshot)["target"]);
        }

        /// <summary>
        /// The plan reads the craft's delta-v from its stages, and its chutes for what the landing keeps, and runs the block's search: here a 3 t craft leads the 4.6 h window the block takes.
        /// The plan's craft and air are estimates, so a window near 50 km can differ; the wait is covered either way.
        /// </summary>
        [Fact]
        public void The_plan_runs_the_blocks_search_from_the_stages_and_the_chutes()
        {
            var snapshot = WithStockDv(Orbiting(66.43, 0, 3.0), 480);
            snapshot.Craft = new SnapshotCraft { MassTonnes = 3 };
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Name = "pod", MassT = 3, Chute = new ReentryChute() });
            var block = ToKsc(snapshot, 480);

            var wait = BurnPoints.First("local res is return().", snapshot, 1000, 5, new FrameBody());

            Assert.True(wait.DepartureUt.HasValue, "the plan reads a departure");
            Assert.True(Math.Abs(wait.DepartureUt.Value - D(block, "ut")) < 1200, "plan UT " + wait.DepartureUt + " against the block's " + D(block, "ut"));
        }

        [Fact]
        public void When_stock_cannot_say_the_delta_v_it_keeps_816s_rule()
        {
            var snapshot = Orbiting(66.43, 0, 3.0);

            var node = ToKsc(snapshot, -1);

            Assert.Equal(D(ToKsc(snapshot, null), "ut"), D(node, "ut"), 3);
            Assert.Equal("", node["aim_note"]);
        }

        [Fact]
        public void A_return_from_the_moons_plane_states_its_wait_too()
        {
            var snapshot = Orbiting(0, 0, 1.7);

            var node = ToKsc(snapshot, 800);

            Assert.Equal("", node["error"]);
            Assert.Contains("window waits ", (string)node["aim_note"]);
            Assert.Contains(" km from the target", (string)node["aim_note"]);
        }

        private static GameSnapshot WithStockDv(GameSnapshot snapshot, double deltaV)
        {
            snapshot.DeltaVSimulator = true;
            snapshot.Stages.Add(new SnapshotStage { Number = 0, DeltaVVac = deltaV, StartMassT = 2, EndMassT = 1 });
            return snapshot;
        }

        [Fact]
        public void Another_planets_moon_with_a_landing_keeps_816s_rule()
        {
            var duna = new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, SoiRadius = 47921949, RotationPeriod = 65517.859, Sma = 20726155264 };
            Assert.False(ReturnAim.IsHome(duna));
            Assert.True(ReturnAim.IsHome(Kerbin));
        }
    }
}
