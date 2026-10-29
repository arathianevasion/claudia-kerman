// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>land</c>'s Descent Shape picked from the terrain (#511, ADR 0011): the braking simulation's plan-time search, the pass
    /// choice, the site moved for terrain and <c>BRAKEPATH</c>, on the Mun with a fake body whose terrain is a function of where
    /// the track is. The site is on the equator at longitude 0 and an equatorial orbit approaches it from the west, so terrain
    /// "d m before the site" is at longitude -d / 3490.66 degrees.
    /// </summary>
    public sealed class BrakePathTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double MunTurn = 138984.38;
        private const double MPerDeg = MunR * Math.PI / 180;

        private static readonly SnapshotBody Mun = new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, RotationPeriod = MunTurn, SoiRadius = 2e6 };

        /// <summary>The reference lander of the kOS simulations: 60 kN, 5.4 t, Isp 345 s, a 1 s turn lag.</summary>
        private static BrakeCraft Lander()
        {
            return new BrakeCraft { ThrustN = 60000, MassKg = 5400, Isp = 345, LagS = 1 };
        }

        private sealed class Terrain : PlanningBodyBase
        {
            /// <summary>The terrain's height by m before the site along the equator (negative: past it); 0 off the equator's band.</summary>
            public Func<double, double> Before = d => 0;
            public Func<double, double, double> At;

            public override double TerrainAltitude(string body, double latitude, double longitude)
            {
                if (At != null) return At(latitude, longitude);
                return Before(-LandingSiteSearch.Wrap180(longitude) * MPerDeg);
            }

            public override string Biome(string body, double latitude, double longitude) { return "Midlands"; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 45; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private static DescentChoice Plan(Func<double, double> before, double? shape = null)
        {
            return DescentApproach.Plan(Lander(), Mun, new Terrain { Before = before }, MunR + 30000, 0, 0, 90, 9, shape);
        }

        // ---- The search: shapes from the terrain ----

        [Fact]
        public void A_flat_site_picks_a_large_shape_close_to_todays_glide()
        {
            var flat = Plan(d => 0);
            Assert.Null(flat.Error);
            // Today's glide held its height to a fraction of the distance left, which goes as the speed squared: shape 2.
            Assert.InRange(flat.Best.Shape, 1.2, 3);
            Assert.True(flat.Best.Result.Clears);
            Assert.InRange(flat.Best.Dv, 600, 800);
        }

        /// <summary>A site on a crater's floor, 1.5 km under the plain, a 2 km rim (500 m over the plain) 2 to 4 km before it.</summary>
        private static double Crater(double d)
        {
            if (d > 4000 || d < -4000) return 0;
            if (d > 2500) return 500 * (4000 - d) / 1500;
            if (d > 1500) return 500 - 2000 * (2500 - d) / 1000;
            return -1500;
        }

        [Fact]
        public void A_site_in_a_rimmed_crater_picks_a_smaller_shape_or_a_higher_periapsis_that_clears_the_rim_by_the_margin()
        {
            var flat = Plan(d => 0);
            var crater = Plan(Crater);
            Assert.Null(crater.Error);
            Assert.True(crater.Best.Shape < flat.Best.Shape || crater.Best.PeAlt > flat.Best.PeAlt,
                "crater " + crater.Best.Shape + " from " + crater.Best.PeAlt + " m, flat " + flat.Best.Shape + " from " + flat.Best.PeAlt + " m");
            // Over the rim by the margin while braking: every point at least 300 m (or 2 s of descent) over the ground.
            Assert.True(crater.Best.Result.LeastSpare >= 0);
            foreach (var p in crater.Best.Result.Path.Where(p => p.S > 1500 && p.S < 4000)) Assert.True(p.Alt - p.Ground >= 300, p.S + ": " + (p.Alt - p.Ground));
        }

        /// <summary>Twin Craters (m20261029-181119-0584): ground about 4.7 km high some 80 km before the site, which is at 0.</summary>
        private static double TwinCraters(double d)
        {
            return Math.Abs(d - 80000) < 3000 ? 4700 * (1 - Math.Abs(d - 80000) / 3000) : 0;
        }

        [Fact]
        public void The_Twin_Craters_approach_doesnt_start_braking_far_out_and_clears_its_path()
        {
            // A periapsis about 5 km up over the site: braking starts at the stopping distance, not 80 km out where the old
            // ground-close trigger fired, and the path it flies is the prediction, so no guard against it trips.
            var five = Plan(TwinCraters, null);
            Assert.Null(five.Error);
            var ground = DescentApproach.Read(new Terrain { Before = TwinCraters }, "Mun", MunR, 0, 0, 90, 100000, out _);
            var world = new BrakeWorld { Mu = MunGm, Radius = MunR, Ground = ground };
            var from = BrakePath.OnTransfer(MunGm, MunR, MunR + 30000, MunR + 5000, 95000, 9);
            var flown = BrakePath.Simulate(Lander(), world, from, five.Best.Shape);
            Assert.True(flown.Clears, flown.Failure + ", least spare " + flown.LeastSpare);
            Assert.InRange(flown.BrakeStartS, 10000, 30000);
            Assert.True(flown.LowestClearance > 300);
        }

        [Fact]
        public void The_search_takes_about_200_ms_a_pass_at_most()
        {
            Timing.Within(BrakePath.BudgetMs, () => Plan(Crater));
        }

        // ---- descent_shape ----

        [Fact]
        public void A_descent_shape_that_clears_is_flown_and_one_that_doesnt_is_refused_with_the_range_that_clears()
        {
            var flat = Plan(d => 0, 1.0);
            Assert.Null(flat.Error);
            Assert.Equal(1.0, flat.Best.Shape);
            Assert.True(flat.Best.Result.Clears);
            // A braking stage with about 646 m/s: a steep shape holds its height longer and hovers down from it, about 15 m/s
            // dearer on flat ground than a shallow one, so it runs dry short of the ground; the shallow shapes land.
            var stage = Lander();
            stage.DryMassKg = 5400 * Math.Exp(-646 / (345 * 9.80665));
            var steep = DescentApproach.Plan(stage, Mun, new Terrain(), MunR + 30000, 0, 0, 90, 9, 0.05);
            Assert.NotNull(steep.Error);
            Assert.StartsWith("descent_shape 0.05 doesn't clear the terrain on the approach; shapes from ", steep.Error);
            Assert.EndsWith(" to 3 clear", steep.Error);
            Assert.InRange(steep.ShapeLo, 0.2, 3);
            Assert.Equal(3, steep.ShapeHi);
            var shallow = DescentApproach.Plan(stage, Mun, new Terrain(), MunR + 30000, 0, 0, 90, 9, 3);
            Assert.Null(shallow.Error);
        }

        // ---- The pass choice ----

        [Theory]
        [InlineData(new[] { 110.0, 100, 95 }, 1)]
        [InlineData(new[] { 104.0, 120, 95 }, 0)]
        [InlineData(new[] { 120.0, 130, 95, 96 }, 2)]
        [InlineData(new[] { 100.0 }, 0)]
        public void Among_passes_in_the_window_the_soonest_within_10_percent_of_the_cheapest_is_taken(double[] landingDvs, int expected)
        {
            Assert.Equal(expected, LandingPass.ChoosePass(landingDvs));
        }

        // ---- The site moved for terrain, and the passes ----

        private static GameSnapshot MunOrbit()
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = MunR + 30000, Eccentricity = 0, Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = 1000, Body = "Mun", Periapsis = 30000, Apoapsis = 30000,
            };
            snapshot.Bodies.Add(Mun);
            return snapshot;
        }

        private static Dictionary<string, object> Ask(double lat, double lng, params object[] more)
        {
            var ask = new Dictionary<string, object>
            {
                { "lat", lat }, { "lng", lng }, { "r_pe", MunR + 3000.0 }, { "daylight", false }, { "within_hours", 6.0 },
                { "thrust", 60.0 }, { "mass", 5.4 }, { "isp", 345.0 }, { "turn_lag", 1.0 },
            };
            for (var i = 0; i < more.Length; i += 2) ask[(string)more[i]] = more[i + 1];
            return ask;
        }

        /// <summary>A ring wall 12 km high from 6 to 12 km round (0, 30): every approach to it, or to a site within 5 km of it, crosses the wall.</summary>
        private static double Tower(double lat, double lng)
        {
            var d = LandingSiteSearch.ArcDegrees(lat, lng, 0, 30) * MPerDeg;
            return d > 6000 && d < 12000 ? 12000 : 0;
        }

        [Fact]
        public void A_site_no_shape_clears_moves_to_the_nearest_clearing_site_within_5_km_for_coordinates()
        {
            // A tower west of the site, so the approach from the west crosses it; 2.5 km north the track misses it.
            Func<double, double, double> wall = (lat, lng) =>
                Math.Abs(lat) < 0.5 / 3490.66 * 1000 && lng > 29.5 && lng < 29.9 ? 12000 : 0;
            var moved = BlockCalls.LandingPass(Ask(0, 30, "move_site", true), MunOrbit(), new Terrain { At = wall });
            Assert.Equal("", moved["error"]);
            Assert.Equal(true, moved["moved_for_terrain"]);
            var off = LandingSiteSearch.ArcDegrees(0, 30, (double)moved["lat"], (double)moved["lng"]) * MPerDeg;
            Assert.InRange(off, 500, 5001);
            Assert.True((double)moved["descent_shape"] > 0);
            Assert.True((double)moved["predicted_dv"] > 0);
            Assert.NotEmpty((List<object>)moved["path"]);
            // Without move_site it stays put and fails: every pass's approach crosses the wall.
            var stays = BlockCalls.LandingPass(Ask(0, 30), MunOrbit(), new Terrain { At = wall });
            Assert.StartsWith("terrain on the approach: no Descent Shape clears it on any pass in the next ", (string)stays["error"]);
        }

        [Fact]
        public void With_no_site_clearing_on_any_pass_land_fails_before_the_deorbit_burn_with_terrain_on_the_approach()
        {
            var none = BlockCalls.LandingPass(Ask(0, 30, "move_site", true), MunOrbit(), new Terrain { At = Tower });
            Assert.StartsWith("terrain on the approach", (string)none["error"]);
            Assert.True((int)none["terrain_passes"] > 0);
        }

        [Fact]
        public void A_pass_failed_for_terrain_gives_way_to_the_next_that_clears_without_a_missed_pass()
        {
            // A 20 degree orbit over a site on the equator: northbound passes cross it near one node, southbound ones near the other,
            // half a turn of the Mun later, over different ground. A wall on the approach the plain search chose fails that pass,
            // and the search takes a pass on the other heading: no re-plan, just the next pass that clears (#511).
            var conic = Conic.FromElements(MunGm, MunR + 30000, 0, 20, 0, 0, 0, 1000);
            var plain = LandingPass.Find(Mun, conic, 1000, PassAsk(), new Terrain());
            Assert.Null(plain.Error);
            Assert.True(plain.Passes.Where(p => !double.IsNaN(p.Heading)).Select(p => Math.Round(p.Heading / 30)).Distinct().Count() >= 2
                || plain.Passes.Count(p => p.Verdict == "next") == 1);
            var heading = plain.Descent.Heading;
            var wall = new List<double[]>();
            for (var d = 2000.0; d <= 6000; d += 250)
            {
                double wLat, wLng;
                DescentApproach.Destination(0, 30, heading + 180, d, MunR, out wLat, out wLng);
                wall.Add(new[] { wLat, wLng });
            }
            var walled = new Terrain { At = (lat, lng) => wall.Any(w => LandingSiteSearch.ArcDegrees(lat, lng, w[0], w[1]) * MPerDeg < 600) ? 12000 : 0 };
            var found = LandingPass.Find(Mun, conic, 1000, PassAsk(), walled);
            Assert.Null(found.Error);
            Assert.True(found.TerrainPasses >= 1);
            Assert.True(Math.Abs(LandingSiteSearch.Wrap180(found.Descent.Heading - heading)) > 10, found.Descent.Heading + " against " + heading);
            Assert.StartsWith("terrain on the approach", found.Passes.First(p => Math.Abs(p.BurnUt - plain.BurnUt) < 1).Verdict);
        }

        private static LandingPassAsk PassAsk()
        {
            return new LandingPassAsk
            {
                Latitude = 0, Longitude = 30, PeRadius = MunR + 3000, EarliestUt = 1120, LatestUt = 1000 + 40 * 3600, Daylight = false,
                MaxCorrectionDv = 30, Craft = Lander(),
            };
        }

        // ---- BRAKEPATH: predicting again as braking begins ----

        [Fact]
        public void The_prediction_at_braking_start_switches_to_a_steeper_shape_when_the_planned_one_no_longer_clears_and_none_clearing_says_so()
        {
            // 18 km out at 575 m/s, 3 km up, a 2.6 km rim 3 km before the site: a shallow shape sinks into it, a steep one holds over it.
            Func<double, double> rim = d => d > 2000 && d < 4000 ? 2600 : 0;
            var terrain = new Terrain { Before = rim };
            var planned = BlockCalls.BrakePathCall(State("shape", 3.0), MunOrbit(), terrain);
            Assert.Equal("", planned["error"]);
            Assert.Equal(false, planned["clears"]);
            var auto = BlockCalls.BrakePathCall(State("shape", "auto"), MunOrbit(), terrain);
            Assert.Equal(true, auto["clears"]);
            Assert.True((double)auto["descent_shape"] < 3);
            // A wall over the craft's height: no shape clears, and land doesn't brake.
            var wall = BlockCalls.BrakePathCall(State("shape", "auto"), MunOrbit(), new Terrain { Before = d => d > 2000 && d < 4000 ? 9000 : 0 });
            Assert.Equal(false, wall["clears"]);
            Assert.Equal(-1.0, wall["shape_lo"]);
        }

        private static Dictionary<string, object> State(string key, object value)
        {
            return new Dictionary<string, object>
            {
                { "lat", 0.0 }, { "lng", 0.0 }, { "heading", 90.0 }, { "s", 18000.0 }, { "alt", 3000.0 }, { "v_h", 575.0 }, { "v_up", -5.0 },
                { "thrust", 60.0 }, { "mass", 5.4 }, { "isp", 345.0 }, { "turn_lag", 1.0 }, { key, value },
            };
        }

        [Fact]
        public void The_block_call_says_what_it_needs()
        {
            Assert.Equal("s is required", BlockCalls.BrakePathCall(new Dictionary<string, object> { { "lat", 0.0 }, { "lng", 0.0 }, { "heading", 90.0 } }, MunOrbit(), new Terrain())["error"]);
            Assert.Equal("shape must be \"auto\" or a Descent Shape over 0", BlockCalls.BrakePathCall(State("shape", "steep"), MunOrbit(), new Terrain())["error"]);
        }

        // ---- The approach's heading ----

        [Fact]
        public void An_equatorial_prograde_approach_heads_east_and_the_turn_takes_the_ground_speed_off()
        {
            double heading, vRot;
            DescentApproach.Heading(new Vec3(0, 0, 1), new Vec3(1, 0, 0), 584, MunR, MunTurn, 1, out heading, out vRot);
            Assert.Equal(90, heading, 6);
            Assert.Equal(2 * Math.PI * MunR / MunTurn, vRot, 6);
            // A polar orbit over the equator heads north, and the turn swings it a little west.
            DescentApproach.Heading(new Vec3(0, -1, 0), new Vec3(1, 0, 0), 584, MunR, MunTurn, 1, out heading, out vRot);
            Assert.InRange(heading, 358, 360);
        }
    }
}
