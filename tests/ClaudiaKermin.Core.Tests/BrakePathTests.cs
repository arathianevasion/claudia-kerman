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
            /// <summary>m of the equator a degree of longitude spans: the Mun's unless set.</summary>
            public double PerDegree = MPerDeg;

            public override double TerrainAltitude(string body, double latitude, double longitude)
            {
                if (At != null) return At(latitude, longitude);
                return Before(-LandingSiteSearch.Wrap180(longitude) * PerDegree);
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

        // ---- Twin Craters as flown (#546, m20261031-022433-3dc5) ----

        /// <summary>
        /// The ground under Mun Shot v2's fall into Twin Craters, recorded: the transfer's height less ALT:RADAR at each fall trace
        /// (m before the site, m above sea level). A highland plateau about 4 km up out to 94 km (held there beyond), falling to
        /// the site's hollow at 400 m.
        /// </summary>
        private static readonly double[,] TwinCratersRecorded =
        {
            { -3000, 400 }, { 0, 400 }, { 22433, 980 }, { 23606, 1108 }, { 24780, 1461 }, { 27126, 1614 }, { 30643, 2450 },
            { 47016, 3674 }, { 50514, 3511 }, { 54008, 3939 }, { 56335, 4166 }, { 59821, 3701 }, { 64462, 4050 }, { 70250, 4293 },
            { 73930, 4379 }, { 77430, 4370 }, { 84411, 3906 }, { 93724, 4317 },
        };

        private static double TwinCratersAsFlown(double d)
        {
            var last = TwinCratersRecorded.GetLength(0) - 1;
            if (d <= TwinCratersRecorded[0, 0]) return TwinCratersRecorded[0, 1];
            for (var i = 1; i <= last; i++)
                if (d <= TwinCratersRecorded[i, 0])
                    return TwinCratersRecorded[i - 1, 1] + (TwinCratersRecorded[i, 1] - TwinCratersRecorded[i - 1, 1])
                        * (d - TwinCratersRecorded[i - 1, 0]) / (TwinCratersRecorded[i, 0] - TwinCratersRecorded[i - 1, 0]);
            return TwinCratersRecorded[last, 1];
        }

        /// <summary>
        /// Mun Shot v2 as land measured it in orbit before the stack was staged off: the Swivel stage (215 kN, about 12 t with the
        /// lander) and TURNTIME's 190 degrees in 27.7 s at up to 21.3 deg/s, the Underhang dropping at 2000 m to the 60 kN, 6.1 t lander.
        /// </summary>
        private static BrakeCraft MunShotStack()
        {
            return new BrakeCraft
            {
                ThrustN = 215000, MassKg = 12000, Isp = 320, LagS = BrakePath.TurnLag(21.3, 27.7),
                DropAgl = 2000, LanderThrustN = 60000, LanderMassKg = 6100, LanderIsp = 345,
            };
        }

        /// <summary>
        /// The craft that brakes once land stages that short Swivel stage off before the fall, Underhang and all (#382): the lander
        /// alone, 60 kN, 6.1 t, still flying the stack's turn lag (TURNTIME read it before staging).
        /// </summary>
        private static BrakeCraft MunShotLander()
        {
            return new BrakeCraft { ThrustN = 60000, MassKg = 6100, Isp = 345, LagS = BrakePath.TurnLag(21.3, 27.7) };
        }

        [Fact]
        public void A_Twin_Craters_pass_from_a_30_km_orbit_is_planned_for_the_lander_over_a_fall_that_clears_the_plateau_by_the_margin()
        {
            // #546: every pass was refused as terrain on the approach, planned for the stack that land then staged off before the
            // fall. The stack, braking until its Underhang drops 2 km up, hit the ground on its last drop from every periapsis.
            // Since #547's sink limit it comes down slowly enough for the lander to stop after the drop, so it plans too; the
            // refusal over periapses whose fall is too low still says it was the fall, not the braking.
            var terrain = new Terrain { Before = TwinCratersAsFlown };
            var stack = DescentApproach.Plan(MunShotStack(), Mun, terrain, MunR + 30000, 0, 0, 90, 9, null);
            Assert.Null(stack.Error);
            Assert.True(stack.Best.Result.LeastStopRoom >= 0 && stack.Best.Result.TouchdownSpeed <= 5,
                "room " + Math.Round(stack.Best.Result.LeastStopRoom) + " m, touchdown " + stack.Best.Result.TouchdownSpeed + " m/s");
            var world = new BrakeWorld
            {
                Mu = MunGm, Radius = MunR, Ground = DescentApproach.Read(terrain, "Mun", MunR, 0, 0, 90, 100000, out _),
                FallGround = DescentApproach.Read(terrain, "Mun", MunR, 0, 0, 90, 200000, out _, DescentApproach.FallProfileStep),
            };
            var low = BrakePath.Search(MunShotStack(), world, MunR + 30000, 9, 1500, 4000);
            Assert.NotNull(low.Error);
            Assert.Contains("from a periapsis under 4500 m", low.Error);
            // The lander that brakes is planned.
            var plan = DescentApproach.Plan(MunShotLander(), Mun, new Terrain { Before = TwinCratersAsFlown }, MunR + 30000, 0, 0, 90, 9, null);
            Assert.Null(plan.Error);
            Assert.True(plan.Best.Result.Clears);
            // The fall to that periapsis, by hand down the transfer: over the plateau 74 km out, where the in-game fall to 3 km came
            // within 329 m and set off the safety net, it keeps the margin and the slack (400 m).
            var rPe = MunR + plan.Best.PeAlt;
            var r0 = MunR + 30000;
            var e = (r0 - rPe) / (r0 + rPe);
            var alt74 = rPe * (1 + e) / (1 + e * Math.Cos(74000.0 / MunR)) - MunR;
            Assert.True(alt74 - 4379 >= 400, "periapsis " + plan.Best.PeAlt + " m: " + Math.Round(alt74 - 4379) + " m over the plateau 74 km out");
        }

        [Fact]
        public void On_Twin_Craters_braking_predicted_again_where_the_fall_brings_it_due_clears_as_the_plan_did()
        {
            // #546: the braking-start re-check accepts what the plan accepted when nothing changed, the lander planned and flown.
            // The plan flew the fall in the flat frame, about 1 m/s down off the conic at braking start: there 850 m/s against
            // the re-check's 1155. It now brakes from where the fall on the conic brings braking due, as the block does.
            var terrain = new Terrain { Before = TwinCratersAsFlown };
            var plan = DescentApproach.Plan(MunShotLander(), Mun, terrain, MunR + 30000, 0, 0, 90, 9, null);
            Assert.Null(plan.Error);
            var fall = DescentApproach.Read(terrain, "Mun", MunR, 0, 0, 90, 200000, out _, DescentApproach.FallProfileStep);
            bool early;
            var at = BrakePath.FallDue(MunShotLander(), new BrakeWorld { Mu = MunGm, Radius = MunR, Ground = fall }, MunR + 30000, MunR + plan.Best.PeAlt, 9, 200000, out early);
            Assert.False(early);
            Assert.True(Math.Abs(at.S - plan.Best.Result.BrakeStartS) <= 500, "braking due " + Math.Round(at.S) + " m out, planned " + Math.Round(plan.Best.Result.BrakeStartS));
            var again = BlockCalls.BrakePathCall(new Dictionary<string, object>
            {
                { "lat", 0.0 }, { "lng", 0.0 }, { "heading", 90.0 }, { "s", at.S }, { "alt", at.Alt }, { "v_h", at.VH }, { "v_up", at.VUp },
                { "thrust", 60.0 }, { "mass", 6.1 }, { "isp", 345.0 }, { "turn_lag", MunShotLander().LagS }, { "shape", plan.Best.Shape },
            }, MunOrbit(), terrain);
            Assert.Equal("", again["error"]);
            Assert.Equal(true, again["clears"]);
            Assert.InRange((double)again["brake_dv"] / plan.Best.Result.DvUsed, 0.95, 1.05);
        }

        [Theory]
        [InlineData(5000.0)]
        [InlineData(6000.0)]
        public void From_the_78_km_twin_craters_braking_start_the_last_drop_keeps_room_to_stop_over_the_ground_and_lands_on_the_site(double massKg)
        {
            // #547, m20261031-022433-3dc5 from a 78 km orbit: periapsis 4.1 km, shape 2.35, braking 21.9 km out at 599.5 m/s and 9 m/s
            // down, 3344 m over the ground (h 3206, h_ref 3310), Mun Shot v2's lander (60 kN, about 5 to 6 t, TURNTIME's 190 degrees
            // in 9.9 s at up to 59.5 deg/s). Above circular speed it can't sink with its shape, so it holds about 3.4 km to within a
            // km of the site and drops 3 km there at about 90 m/s. The sink rate wanted stopped it at the ground: falling, full thrust
            // after the 2.6 s turn would have stopped it 7 m up. Now it keeps the room to stop by about h_end.
            var ground = DescentApproach.Read(new Terrain { Before = TwinCratersAsFlown }, "Mun", MunR, 0, 0, 90, 30000, out _);
            var world = new BrakeWorld { Mu = MunGm, Radius = MunR, Ground = ground };
            var craft = new BrakeCraft { ThrustN = 60000, MassKg = massKg, Isp = 345, LagS = BrakePath.TurnLag(59.5, 9.9) };
            var start = new BrakeStart { S = 21870, Alt = 3344 + TwinCratersAsFlown(21870), VH = 599.5, VUp = -9, Braking = true };
            var flown = BrakePath.Simulate(craft, world, start, 2.345);
            Assert.True(flown.Clears, flown.Failure + ", least spare " + flown.LeastSpare);
            Assert.InRange(flown.StopS, -300, 300);
            Assert.True(flown.LeastStopRoom >= BrakeGuidance.HEnd / 2, "least room to stop " + Math.Round(flown.LeastStopRoom) + " m");
        }

        // ---- Minmus Flats (#530, m20261030-013514-be46) ----

        private const double MinmusGm = 1.7658e9;
        private const double MinmusR = 60000;
        private const double MinmusTurn = 40400;
        private static readonly double MinmusVRot = 2 * Math.PI * MinmusR / MinmusTurn;
        private static readonly SnapshotBody Minmus = new SnapshotBody { Name = "Minmus", Parent = "Kerbin", Gm = MinmusGm, Radius = MinmusR, RotationPeriod = MinmusTurn, SoiRadius = 2247428.4 };

        /// <summary>Mun Shot v2 as it braked: 60 kN, about 5 t, Isp 345 s, and TURNTIME's 190 degrees in 9.8 s at up to 59.9 deg/s.</summary>
        private static BrakeCraft MunShot()
        {
            return new BrakeCraft { ThrustN = 60000, MassKg = 5000, Isp = 345, LagS = BrakePath.TurnLag(59.9, 9.8) };
        }

        /// <summary>
        /// The Flats approach as the trace read it: plain ground at sea level, a ridge 1650 m high some 20 km before the site
        /// (the descent orbit to a 1.5 km periapsis passes about 300 m over it), then the Flats at 1100 m to the site and past it.
        /// </summary>
        private static double Flats(double d)
        {
            if (d > 21000) return 0;
            if (d > 20400) return 1650 * (21000 - d) / 600;
            if (d >= 19600) return 1650;
            if (d > 18600) return 1650 - 550 * (19600 - d) / 1000;
            return 1100;
        }

        private static Terrain FlatsTerrain()
        {
            return new Terrain { Before = Flats, PerDegree = MinmusR * Math.PI / 180 };
        }

        private static BrakeWorld FlatsWorld()
        {
            var ground = DescentApproach.Read(FlatsTerrain(), "Minmus", MinmusR, 0, 0, 90, 80000, out _);
            return new BrakeWorld { Mu = MinmusGm, Radius = MinmusR, Ground = ground };
        }

        /// <summary>The most a braking path climbs over the lowest it has been since braking began, m.</summary>
        private static double Climb(BrakeResult flown)
        {
            var lowest = double.PositiveInfinity;
            var most = 0.0;
            foreach (var p in flown.Path.Where(p => p.S <= flown.BrakeStartS && (double.IsNaN(flown.BrakeEndS) || p.S >= flown.BrakeEndS)))
            {
                lowest = Math.Min(lowest, p.Alt);
                most = Math.Max(most, p.Alt - lowest);
            }
            return most;
        }

        [Fact]
        public void On_minmus_flats_the_whole_fall_from_the_deorbit_brakes_where_the_plan_says_and_comes_down_on_the_site_without_climbing()
        {
            // #530: the plan simulated only the last 9 km of the fall, so it took a 1.5 km periapsis whose descent orbit passes
            // 300 m over the ridge 20 km out; there the ground-close safety net started braking at 192 m/s, 22.8 km short.
            var plan = DescentApproach.Plan(MunShot(), Minmus, FlatsTerrain(), MinmusR + 30000, 0, 0, 90, MinmusVRot, 0.2);
            Assert.Null(plan.Error);
            var world = FlatsWorld();
            // The whole fall the block flies after the deorbit burn, from 70 km out: braking comes due by the block's own check
            // where the plan's simulation starts it, and is flown from there.
            bool early;
            var due = BrakePath.FallDue(MunShot(), world, MinmusR + 30000, MinmusR + plan.Best.PeAlt, MinmusVRot, 70000, out early);
            Assert.True(Math.Abs(due.S - plan.Best.Result.BrakeStartS) <= 500,
                "braking comes due " + Math.Round(due.S) + " m out" + (early ? " (the ground-close safety net)" : "") + ", planned " + Math.Round(plan.Best.Result.BrakeStartS)
                + " m out from a periapsis of " + plan.Best.PeAlt + " m");
            Assert.False(early);
            var flown = BrakePath.Simulate(MunShot(), world, due, plan.Best.Shape);
            Assert.True(flown.Clears, flown.Failure + ", least spare " + flown.LeastSpare);
            // Stopped over the site, and down through h_end (200 m over the Flats) there, never climbing on the way.
            Assert.InRange(flown.BrakeEndS, -300, 300);
            Assert.InRange(flown.HEndS, -300, 300);
            Assert.InRange(Climb(flown), 0, 25);
            // Shape 0.2 holds its height until nearly stopped, then comes straight down: the planned figure, near the 279 m/s planned in-game.
            Assert.InRange(flown.DvUsed, 150, 350);
        }

        [Fact]
        public void Braking_begun_22_km_short_at_192_m_s_crosses_without_flip_flopping_and_lands_on_the_site()
        {
            // The trace's braking start, as the safety net could still begin it: 22.8 km out at 191.7 m/s, 10.4 m/s down, 297 m
            // over the ridge ahead, shape 0.2. The climbing rule braked it to 50 m/s in 16 s; under 50 m/s the guidance pushed for
            // its starting speed, and over it the cone turned that push up and back: 430 s at 50 m/s, climbing 2.4 km, 687 m/s.
            var start = new BrakeStart { S = 22808, Alt = 2097, VH = 191.7, VUp = -10.4, Braking = true };
            var flown = BrakePath.Simulate(MunShot(), FlatsWorld(), start, 0.2);
            Assert.True(flown.Clears, flown.Failure + ", least spare " + flown.LeastSpare);
            Assert.InRange(flown.StopS, -300, 300);
            Assert.InRange(flown.HEndS, -300, 300);
            // It never speeds up again once braking, the flip-flop's mark.
            var points = flown.Path.Where(p => p.S > flown.BrakeEndS).ToList();
            for (var i = 1; i < points.Count; i++) Assert.True(points[i].VH <= points[i - 1].VH + 1, points[i].S + ": " + points[i].VH + " after " + points[i - 1].VH);
            // Above circular speed only braking brings it down, so arresting its 10 m/s fall leaves a climb; well inside the path
            // guard's 1 km, and it holds its speed across rather than crawling at 50 m/s.
            Assert.InRange(Climb(flown), 0, 300);
            Assert.InRange(flown.DvUsed, 150, 300);
            Assert.InRange(flown.Seconds, 0, 250);
        }

        [Fact]
        public void The_braking_figure_at_plan_time_and_at_braking_start_agree_for_the_same_craft_and_path()
        {
            // #530: 262 m/s at plan time against 687 m/s from BRAKEPATH at braking start, because the plan never saw where braking
            // began. BRAKEPATH from where the whole fall begins braking (as the block calls it) gives the plan's figure.
            var plan = DescentApproach.Plan(MunShot(), Minmus, FlatsTerrain(), MinmusR + 30000, 0, 0, 90, MinmusVRot, 0.2);
            Assert.Null(plan.Error);
            bool early;
            var at = BrakePath.FallDue(MunShot(), FlatsWorld(), MinmusR + 30000, MinmusR + plan.Best.PeAlt, MinmusVRot, 70000, out early);
            Assert.NotNull(at);
            var again = BlockCalls.BrakePathCall(new Dictionary<string, object>
            {
                { "lat", 0.0 }, { "lng", 0.0 }, { "heading", 90.0 }, { "s", at.S }, { "alt", at.Alt }, { "v_h", at.VH }, { "v_up", at.VUp },
                { "thrust", 60.0 }, { "mass", 5.0 }, { "isp", 345.0 }, { "turn_lag", MunShot().LagS }, { "shape", plan.Best.Shape },
            }, MinmusOrbit(), FlatsTerrain());
            Assert.Equal("", again["error"]);
            Assert.Equal(true, again["clears"]);
            var brakeDv = (double)again["brake_dv"];
            Assert.InRange(brakeDv / plan.Best.Result.DvUsed, 0.95, 1.05);
        }

        private static GameSnapshot MinmusOrbit()
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Minmus" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = MinmusR + 30000, Eccentricity = 0, Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = 1000, Body = "Minmus", Periapsis = 30000, Apoapsis = 30000,
            };
            snapshot.Bodies.Add(Minmus);
            return snapshot;
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
        public void A_biome_site_no_shape_clears_gives_way_to_the_next_best_site_in_the_biome()
        {
            // The site search's first site, then a ring wall round it that no approach clears: with the craft, the search
            // filters it out and takes the next site in the biome, with its predicted landing.
            var conic = Conic.FromElements(MunGm, MunR + 30000, 0, 0, 0, 0, 0, 1000);
            var query = new LandingSiteQuery { Biome = "Midlands", Daylight = false, MaxSlopeDeg = 5, WithinHours = 6, EdgeMarginM = 0, MaxSites = 1 };
            var plain = LandingSiteSearch.Find(Mun, conic, 1000, query, new Terrain());
            Assert.Null(plain.Error);
            var first = plain.Sites[0];
            Assert.True(double.IsNaN(first.PredictedDv));
            var walled = new Terrain
            {
                At = (lat, lng) =>
                {
                    var d = LandingSiteSearch.ArcDegrees(lat, lng, first.Latitude, first.Longitude) * MPerDeg;
                    return d > 6000 && d < 12000 ? 12000 : 0;
                },
            };
            query.Craft = Lander();
            var found = LandingSiteSearch.Find(Mun, conic, 1000, query, walled);
            Assert.Null(found.Error);
            var next = found.Sites[0];
            // Another site (the next on the track; the flat top of the ring is one, its approach clear of anything higher).
            Assert.True(LandingSiteSearch.ArcDegrees(next.Latitude, next.Longitude, first.Latitude, first.Longitude) * MPerDeg > 5000);
            Assert.Equal("Midlands", next.Biome);
            Assert.True(next.PredictedDv > 0);
            Assert.True(next.DescentShape > 0);
        }

        private static Dictionary<string, object> BiomeAsk(params object[] more)
        {
            var ask = new Dictionary<string, object> { { "biome", "Midlands" }, { "daylight", false }, { "within_hours", 6.0 }, { "edge_margin_m", 0.0 } };
            for (var i = 0; i < more.Length; i += 2) ask[(string)more[i]] = more[i + 1];
            return ask;
        }

        /// <summary>The reference lander as land_craft gives it, turning in <paramref name="turnLag"/> s.</summary>
        private static Dictionary<string, object> CraftAsk(double turnLag)
        {
            return new Dictionary<string, object> { { "thrust", 60.0 }, { "mass", 5.4 }, { "isp", 345.0 }, { "turn_lag", turnLag }, { "burn_margin", 1.1 } };
        }

        [Fact]
        public void LANDINGSITE_keeps_the_sites_the_craft_land_will_fly_clears_so_two_craft_that_disagree_get_different_sites()
        {
            // #528: a 9 km ring wall 6 to 12 km round the search's first site. The reference lander turning in 1 s clears it and
            // keeps the site; the same lander turning in 15 s doesn't, and the search gives the biome's next site instead.
            var plain = BlockCalls.LandingSite(BiomeAsk(), MunOrbit(), new Terrain());
            Assert.Equal("", plain["error"]);
            double lat0 = (double)plain["lat"], lng0 = (double)plain["lng"];
            var ring = new Terrain
            {
                At = (lat, lng) =>
                {
                    var d = LandingSiteSearch.ArcDegrees(lat, lng, lat0, lng0) * MPerDeg;
                    return d > 6000 && d < 12000 ? 9000 : 0;
                },
            };
            var quick = BlockCalls.LandingSite(BiomeAsk("craft", CraftAsk(1)), MunOrbit(), ring);
            var slow = BlockCalls.LandingSite(BiomeAsk("craft", CraftAsk(15)), MunOrbit(), ring);
            Assert.Equal("", quick["error"]);
            Assert.Equal("", slow["error"]);
            Assert.True(LandingSiteSearch.ArcDegrees(lat0, lng0, (double)quick["lat"], (double)quick["lng"]) * MPerDeg < 100);
            Assert.True(LandingSiteSearch.ArcDegrees(lat0, lng0, (double)slow["lat"], (double)slow["lng"]) * MPerDeg > 5000);
            Assert.True((double)slow["descent_shape"] > 0);
        }

        [Fact]
        public void LANDINGSITE_skips_the_sites_land_has_tried_and_takes_the_biomes_next()
        {
            var first = BlockCalls.LandingSite(BiomeAsk("craft", CraftAsk(1)), MunOrbit(), new Terrain());
            var tried = new List<object> { new Dictionary<string, object> { { "lat", first["lat"] }, { "lng", first["lng"] } } };
            var second = BlockCalls.LandingSite(BiomeAsk("craft", CraftAsk(1), "exclude", tried), MunOrbit(), new Terrain());
            Assert.Equal("", second["error"]);
            tried.Add(new Dictionary<string, object> { { "lat", second["lat"] }, { "lng", second["lng"] } });
            var third = BlockCalls.LandingSite(BiomeAsk("craft", CraftAsk(1), "exclude", tried), MunOrbit(), new Terrain());
            Assert.Equal("", third["error"]);
            foreach (var site in new[] { second, third })
                foreach (Dictionary<string, object> at in tried.Take(site == second ? 1 : 2))
                    Assert.True(LandingSiteSearch.ArcDegrees((double)at["lat"], (double)at["lng"], (double)site["lat"], (double)site["lng"]) >= LandingSiteSearch.MinSeparationDeg);
        }

        [Fact]
        public void The_find_landing_site_tools_schema_and_description_are_unchanged_by_the_block_inputs()
        {
            // #528: craft and exclude are LANDINGSITE's alone; the tool Claude sees is byte-identical to main's before it.
            var tool = new FindLandingSite();
            Assert.Equal(ToolTextHash, Sha256(tool.Description + "\u0000" + tool.InputSchemaJson));
            Assert.DoesNotContain("exclude", tool.InputSchemaJson);
            Assert.DoesNotContain("\"craft\"", tool.InputSchemaJson);
        }

        /// <summary>SHA-256 of find_landing_site's description, a NUL and its input schema, as on main at 9c1d0d5.</summary>
        private const string ToolTextHash = "E66D5DCF9082BE36E59A90379657D201296C23152A54D5E6E9265A0BA1378232";

        private static string Sha256(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        /// <summary>The Mun orbit with a transfer stage (60 kN, 8 t to 6 t, 900 m/s) over a lander stage (<paramref name="landerThrust"/> kN, 3 t to 1.8 t, <paramref name="landerDv"/> m/s).</summary>
        private static GameSnapshot Stacked(double landerThrust, double landerDv)
        {
            var snapshot = MunOrbit();
            snapshot.Craft = new SnapshotCraft { MassTonnes = 8 };
            snapshot.Stages.Add(new SnapshotStage { Number = 2, ThrustVacKn = 60, StartMassT = 8, EndMassT = 6, DeltaVVac = 900 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, ThrustVacKn = landerThrust, StartMassT = 3, EndMassT = 1.8, DeltaVVac = landerDv });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, StartMassT = 1.8, EndMassT = 1.8 });
            return snapshot;
        }

        [Fact]
        public void From_the_snapshot_a_stage_land_would_drop_before_the_deorbit_burn_leaves_the_lander_alone()
        {
            // #528 (#596's rule as far as the snapshot has it): an engine below the firing stage, with the landing's delta-v below
            // it, so land stages the firing one off first and the lander brakes alone, dry at its stage's end mass.
            var lander = DescentApproach.FromSnapshot(Stacked(20, 1700));
            Assert.Equal(20000, lander.ThrustN, 6);
            Assert.Equal(3000, lander.MassKg, 6);
            Assert.Equal(1800, lander.DryMassKg, 6);
            Assert.Equal(1700 / (9.80665 * Math.Log(3 / 1.8)), lander.Isp, 6);
        }

        [Fact]
        public void From_the_snapshot_without_a_droppable_stage_the_whole_craft_brakes_on_the_firing_stage()
        {
            // No engine below the firing stage, or one short of the landing's delta-v: the whole craft on the firing stage, as
            // before, and now dry once that stage burns its 2 t.
            foreach (var snapshot in new[] { Stacked(0, 1700), Stacked(20, 300) })
            {
                var craft = DescentApproach.FromSnapshot(snapshot);
                Assert.Equal(60000, craft.ThrustN, 6);
                Assert.Equal(8000, craft.MassKg, 6);
                Assert.Equal(6000, craft.DryMassKg, 6);
                Assert.Equal(900 / (9.80665 * Math.Log(8 / 6.0)), craft.Isp, 6);
                Assert.Equal(2, craft.LagS, 6);
            }
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
