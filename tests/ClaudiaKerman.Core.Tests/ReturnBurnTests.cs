// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:RETURNBURN</c>, <c>return</c>'s ejection from any moon to its planet (#450), through
    /// <see cref="BlockCalls"/>: from circular orbits round the Mun, Minmus and Ike (stock body values), each answer is checked by
    /// flying its node through <c>predict_burn</c>, the propagation the solver does not share a line of code with past
    /// <see cref="BodySystem.Follow"/>.
    /// </summary>
    public sealed class ReturnBurnTests
    {
        /// <summary>The acceptance's bounds: periapsis within 1 km, inclination within 0.5°; about 100 ms typical, under 200 ms at worst (ADR 0010).</summary>
        private const double PeBound = 1000;
        private const double IncBound = 0.5;
        private const double TypicalMs = 100;
        private const double WorstMs = 200;

        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };
        private static readonly SnapshotBody Mun = new SnapshotBody
        {
            Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000,
            MeanAnomalyAtEpoch = 1.7,
        };
        private static readonly SnapshotBody Minmus = new SnapshotBody
        {
            Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
            Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
        };
        private static readonly SnapshotBody Duna = new SnapshotBody
        {
            Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, SoiRadius = 47921949,
            RotationPeriod = 65517.859, Sma = 20726155264, Eccentricity = 0.051, Inclination = 0.06, Lan = 135.5, MeanAnomalyAtEpoch = 3.14,
        };
        private static readonly SnapshotBody Ike = new SnapshotBody
        {
            Name = "Ike", Parent = "Duna", Gm = 1.8568369e10, Radius = 130000, SoiRadius = 1049598.9, RotationPeriod = 65517.862, Sma = 3200000,
            Eccentricity = 0.03, Inclination = 0.2, MeanAnomalyAtEpoch = 1.7,
        };

        /// <summary>A circular orbit <paramref name="altitude"/> m over <paramref name="moon"/> at UT 1000, inclined <paramref name="inc"/>°.</summary>
        private static GameSnapshot Orbiting(SnapshotBody moon, double altitude, double inc = 0, double ut = 1000)
        {
            var snapshot = new GameSnapshot { Ut = ut, Situation = "ORBITING", Body = moon.Name };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = moon.Radius + altitude, Eccentricity = 0, Inclination = inc, Body = moon.Name, Periapsis = altitude, Apoapsis = altitude,
                Epoch = ut,
            };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin, Mun, Minmus, Duna, Ike });
            return snapshot;
        }

        private static IDictionary<string, object> Solve(GameSnapshot snapshot, double pe, double? inc = null, double? earliest = null, double? latest = null)
        {
            var input = new Dictionary<string, object> { { "target_pe", pe } };
            if (inc.HasValue) input["target_inc"] = inc.Value;
            if (earliest.HasValue) input["earliest_ut"] = earliest.Value;
            if (latest.HasValue) input["latest_ut"] = latest.Value;
            return BlockCalls.ReturnBurn(input, snapshot);
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        /// <summary>The periapsis (m) and inclination (degrees) of the first patch round <paramref name="target"/> after flying the node, by predict_burn.</summary>
        private static void Flown(GameSnapshot snapshot, IDictionary<string, object> node, string target, out double pe, out double inc)
        {
            var input = string.Format(CultureInfo.InvariantCulture,
                "{{\"burns\":[{{\"at\":{0:R},\"dv\":{{\"prograde\":{1:R},\"normal\":{2:R},\"radial\":{3:R}}}}}]}}",
                D(node, "ut"), D(node, "prograde"), D(node, "normal"), D(node, "radial"));
            var outcome = PlanningTools.Default().Run("predict_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json))
            {
                foreach (var patch in doc.RootElement.GetProperty("patches").EnumerateArray())
                {
                    if (patch.GetProperty("body").GetString() != target) continue;
                    pe = patch.GetProperty("pe").GetDouble();
                    inc = patch.GetProperty("inclination").GetDouble();
                    return;
                }
            }
            throw new Xunit.Sdk.XunitException("the flown node never reaches " + target + ": " + outcome.Json);
        }

        [Theory]
        [InlineData("Mun", 30000, "Kerbin", 35000)]
        [InlineData("Minmus", 20000, "Kerbin", 35000)]
        [InlineData("Ike", 30000, "Duna", 35000)]
        public void A_return_from_a_moon_reaches_target_pe_at_its_planet_by_its_node(string moonName, double altitude, string planet, double targetPe)
        {
            var snapshot = Orbiting(MoonNamed(moonName), altitude);
            var node = Solve(snapshot, targetPe);
            Assert.Equal("", node["error"]);
            Assert.Equal(planet, node["planet"]);
            double pe, inc;
            Flown(snapshot, node, planet, out pe, out inc);
            Assert.InRange(pe, targetPe - PeBound, targetPe + PeBound);
            Assert.InRange(D(node, "pe_predicted"), targetPe - PeBound, targetPe + PeBound);
            Assert.InRange(D(node, "ut"), snapshot.Ut, snapshot.Ut + Period(snapshot));
            Assert.Equal(D(node, "dv"), Math.Sqrt(D(node, "prograde") * D(node, "prograde") + D(node, "normal") * D(node, "normal") + D(node, "radial") * D(node, "radial")), 6);
        }

        /// <summary>
        /// The least burn is no dearer than the patched-conic sums the kerboscript seed had (return.ks's return_ejection: escape to
        /// infinity at the moon's centre, then a Hohmann-like descent), and dearer than reaching the edge of the moon's sphere of
        /// influence at all. The sums overstate it: the craft leaves through the sphere's edge on the planet's side, short of the
        /// moon's distance. Mun: the sums give 307 m/s, the edge 184 m/s. Ike, whose sphere reaches a third of the way to Duna:
        /// 171 m/s and 103 m/s.
        /// </summary>
        [Theory]
        [InlineData("Mun", 184, 307.5)]
        [InlineData("Ike", 103, 171.5)]
        public void A_return_costs_no_more_than_the_patched_conic_sums_give(string moonName, double edge, double sums)
        {
            var node = Solve(Orbiting(MoonNamed(moonName), 30000), 35000);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), edge, sums);
            Assert.True(D(node, "prograde") > 0.95 * D(node, "dv"));
        }

        /// <summary>The coarse grid the solve falls back on when the analytic seed is undefined (ADR 0010) reaches the same answer.</summary>
        [Fact]
        public void A_return_started_from_the_grid_reaches_target_pe_by_its_node()
        {
            var snapshot = Orbiting(Mun, 30000);
            var plan = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, EarliestUt = snapshot.Ut, FromGrid = true });
            Assert.Null(plan.Error);
            Assert.True(plan.FromGrid);
            double pe, inc;
            Flown(snapshot, new Dictionary<string, object> { { "ut", plan.Ut }, { "prograde", plan.Prograde }, { "normal", plan.Normal }, { "radial", plan.Radial } },
                "Kerbin", out pe, out inc);
            Assert.InRange(pe, 35000 - PeBound, 35000 + PeBound);
            var seeded = Solve(snapshot, 35000);
            // Two minima of one problem, each within the solve's step tolerance.
            Assert.InRange(plan.DeltaV, D(seeded, "dv") - 0.05, D(seeded, "dv") + 0.05);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_return_started_from_the_grid_with_target_inc_is_solved_in_under_200_ms()
        {
            var snapshot = Orbiting(Mun, 30000, 20);
            Timing.Within(WorstMs, () => Assert.Null(ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 35000, TargetInc = 30, EarliestUt = snapshot.Ut, FromGrid = true }).Error));
        }

        /// <summary>
        /// From an orbit inclined or not: from the Mun's own plane the seed arrives equatorial, where cos(inc) has no slope in the
        /// out-of-plane burn (ADR 0010), and the solve stalled there until the seed was tilted off it.
        /// </summary>
        [Theory]
        [InlineData("Mun", 20, 30)]
        [InlineData("Mun", 20, 150)]
        [InlineData("Mun", 0, 30)]
        [InlineData("Ike", 20, 10)]
        public void A_return_reaches_target_inc_too_by_its_node(string moonName, double craftInc, double targetInc)
        {
            var moon = MoonNamed(moonName);
            var snapshot = Orbiting(moon, 30000, craftInc);
            var node = Solve(snapshot, 35000, targetInc);
            Assert.Equal("", node["error"]);
            double pe, inc;
            Flown(snapshot, node, moon.Parent, out pe, out inc);
            Assert.InRange(pe, 35000 - PeBound, 35000 + PeBound);
            Assert.InRange(inc, targetInc - IncBound, targetInc + IncBound);
            Assert.InRange(D(node, "inc_predicted"), targetInc - IncBound, targetInc + IncBound);
        }

        [Fact]
        public void A_window_keeps_the_burn_inside_it()
        {
            var snapshot = Orbiting(Mun, 30000);
            var earliest = snapshot.Ut + 3000;
            var node = Solve(snapshot, 35000, earliest: earliest, latest: earliest + Period(snapshot));
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "ut"), earliest, earliest + Period(snapshot));
        }

        /// <summary>
        /// return asks from the block's start plus the turn time; the call may come a moment after that. As transfer_to's
        /// departure (Transfer), an earliest_ut already past is now, not an error.
        /// </summary>
        [Fact]
        public void An_earliest_ut_already_past_burns_from_now()
        {
            var snapshot = Orbiting(Mun, 30000);
            var node = Solve(snapshot, 35000, earliest: snapshot.Ut - 10);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "ut"), snapshot.Ut, snapshot.Ut + Period(snapshot));
        }

        // ---- the Ike Mission's stall (#759) ----

        /// <summary>
        /// Duna and Ike as that Mission's body table had them, and its craft's orbit at each of the four refused runs (m20261105-032927-1d2c,
        /// the Game Snapshots of the Debriefs), with the periapsis each asked: a low orbit, inclined 18°, far from the tests' circular ones.
        /// </summary>
        private static GameSnapshot MissionOrbit(int run)
        {
            var uts = new[] { 31003751.429473974, 31003757.84947383, 31003764.58947368, 31003772.329473507 };
            var smas = new[] { 148619.81190082472, 148619.8103701323, 148619.8090414372, 148619.80740749335 };
            var eccs = new[] { 0.0005587174274757639, 0.00055866201228586408, 0.00055861362362367409, 0.000558555232551169 };
            var lans = new[] { 88.461477049027849, 88.4614765859053, 88.4614761849096, 88.4614757017489 };
            var argPes = new[] { 131.29871650363029, 131.29754432227273, 131.29643658873084, 131.2949907985855 };
            var mnas = new[] { 3.3351011583893033, 3.3503904921593866, 3.3664397654256568, 3.3848732687393035 };
            var snapshot = new GameSnapshot { Ut = uts[run], Situation = "ORBITING", Body = "Ike" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = smas[run], Eccentricity = eccs[run], Inclination = 18.103046, Lan = lans[run], ArgumentOfPeriapsis = argPes[run],
                MeanAnomalyAtEpoch = mnas[run], Epoch = uts[run], Body = "Ike", Periapsis = 18537, Apoapsis = 18703,
            };
            snapshot.Bodies.AddRange(new[]
            {
                new SnapshotBody { Name = "Sun", Gm = 1.1723327948324905E+18, Radius = 261600000, RotationPeriod = 432000 },
                new SnapshotBody
                {
                    Name = "Duna", Parent = "Sun", Gm = 301363211975.09772, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0666666677961107,
                    SoiRadius = 47921949.369738035, RotationPeriod = 65517.859375, Sma = 20726155264, Eccentricity = 0.0509999990463257,
                    Inclination = 0.0599999986588955, Lan = 135.5, MeanAnomalyAtEpoch = 3.14000010490417,
                },
                new SnapshotBody
                {
                    Name = "Ike", Parent = "Duna", Gm = 18568368573.144009, Radius = 130000, SoiRadius = 1049598.9393116189, RotationPeriod = 65517.862134808071,
                    Sma = 3200000, Eccentricity = 0.0299999993294477, Inclination = 0.200000002980232, MeanAnomalyAtEpoch = 1.70000004768372,
                },
            });
            return snapshot;
        }

        /// <summary>The asked periapses of the four runs, each with the soonest burn a moment before, at and after the snapshot, as the block's start and turn time move it.</summary>
        public static IEnumerable<object[]> MissionAsks()
        {
            var targets = new[] { 300000.0, 310000, 318000, 323095 };
            for (var run = 0; run < targets.Length; run++)
                foreach (var lead in new[] { -6.0, 0, 8, 30, 300, 1500 })
                    yield return new object[] { run, targets[run], lead };
        }

        /// <summary>
        /// The refused runs: from that orbit the least burn sits where the path's apoapsis meets Ike's sphere of influence (114.33 m/s),
        /// and at a 300 km periapsis the solve ran into that edge. Below it the path never leaves, so no point is defined; just above it
        /// the periapsis has an infinite slope in the burn (10 km in 0.05 m/s), and the SQP stopped 1 to 8 km off.
        /// </summary>
        [Theory]
        [MemberData(nameof(MissionAsks))]
        public void The_Ike_Missions_return_solves_within_1_km(int run, double targetPe, double lead)
        {
            var snapshot = MissionOrbit(run);
            var node = Solve(snapshot, targetPe, earliest: snapshot.Ut + lead);
            Assert.Equal("", node["error"]);
            double pe, inc;
            Flown(snapshot, node, "Duna", out pe, out inc);
            Assert.InRange(pe, targetPe - PeBound, targetPe + PeBound);
        }

        /// <summary>
        /// A solve that passed still flew nothing: a 114.35 m/s answer was 0.02 m/s over the edge, and a burn ends up to 0.1 m/s out, so the
        /// craft could stay in Ike's sphere. The burn the solver gives leaves it even 0.5 m/s short, and that shortfall moves the periapsis
        /// by what the correction halfway can take back.
        /// </summary>
        [Theory]
        [MemberData(nameof(MissionAsks))]
        public void The_answer_stays_clear_of_the_sphere_of_influences_edge(int run, double targetPe, double lead)
        {
            var snapshot = MissionOrbit(run);
            var node = Solve(snapshot, targetPe, earliest: snapshot.Ut + lead);
            Assert.Equal("", node["error"]);
            var shortBy = 0.5 / D(node, "dv");
            var underburnt = new Dictionary<string, object>
            {
                { "ut", D(node, "ut") }, { "prograde", D(node, "prograde") * (1 - shortBy) }, { "normal", D(node, "normal") * (1 - shortBy) }, { "radial", D(node, "radial") * (1 - shortBy) },
            };
            double pe, inc;
            Flown(snapshot, underburnt, "Duna", out pe, out inc);
            Assert.InRange(pe, targetPe - 60000, targetPe + 60000);
        }

        /// <summary>The floor costs about 2 m/s over the edge, and the 35 km return that flew (124 m/s) is not moved by it.</summary>
        [Fact]
        public void The_clear_answer_costs_little_more_than_the_edge()
        {
            var snapshot = MissionOrbit(0);
            Assert.InRange(D(Solve(snapshot, 300000), "dv"), 114.33, 118);
            Assert.InRange(D(Solve(snapshot, 35000), "dv"), 123, 125.5);
        }

        private static void AssertFlownWithin1Km(GameSnapshot snapshot, ReturnPlan plan, double targetPe)
        {
            double pe, inc;
            Flown(snapshot, new Dictionary<string, object> { { "ut", plan.Ut }, { "prograde", plan.Prograde }, { "normal", plan.Normal }, { "radial", plan.Radial } },
                "Duna", out pe, out inc);
            Assert.InRange(pe, targetPe - PeBound, targetPe + PeBound);
        }

        /// <summary>A best point the SQP stops short at, in a window with no room in time, is finished by a search on its prograde component alone, and says so.</summary>
        [Fact]
        public void A_solve_stopped_short_is_finished_along_prograde()
        {
            var snapshot = MissionOrbit(0);
            var ut = snapshot.Ut + 2000;
            var stopped = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 300000, EarliestUt = ut, LatestUt = ut, SolverIterations = 3, FinishSearch = false });
            Assert.NotNull(stopped.Error);
            Assert.Null(stopped.FinishedBy);

            var plan = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 300000, EarliestUt = ut, LatestUt = ut, SolverIterations = 3 });
            Assert.Null(plan.Error);
            Assert.Equal("a search on the prograde component", plan.FinishedBy);
            Assert.Equal(stopped.Ut, plan.Ut);
            Assert.Equal(stopped.Normal, plan.Normal);
            Assert.Equal(stopped.Radial, plan.Radial);
            AssertFlownWithin1Km(snapshot, plan, 300000);
        }

        /// <summary>One the prograde floor holds, as a stall at the edge would be, cannot be finished by less burn: a time shift does it.</summary>
        [Fact]
        public void A_solve_stopped_short_at_the_floor_is_finished_by_a_time_shift()
        {
            var snapshot = MissionOrbit(0);
            var stopped = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 300000, EarliestUt = snapshot.Ut, SolverIterations = 4, FinishSearch = false });
            Assert.InRange(Math.Abs(stopped.PePredicted - 300000), 1000, 8000);

            var plan = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 300000, EarliestUt = snapshot.Ut, SolverIterations = 4 });
            Assert.Null(plan.Error);
            Assert.Equal("a time shift", plan.FinishedBy);
            Assert.Equal(stopped.Prograde, plan.Prograde);
            Assert.NotEqual(stopped.Ut, plan.Ut);
            AssertFlownWithin1Km(snapshot, plan, 300000);
        }

        /// <summary>A solve that reaches its answer has nothing to finish.</summary>
        [Fact]
        public void A_converged_solve_is_not_finished()
        {
            Assert.Equal("", Solve(MissionOrbit(0), 300000)["finished_by"]);
        }

        /// <summary>A periapsis out of the searches' reach stays a failure with its figure: the finish never hides a real miss.</summary>
        [Fact]
        public void A_finish_that_cannot_reach_the_target_leaves_the_failure()
        {
            var snapshot = MissionOrbit(0);
            var ut = snapshot.Ut + 1000;
            var plan = ReturnBurn.Find(snapshot, new ReturnAsk { TargetPe = 300000, EarliestUt = ut, LatestUt = ut });
            Assert.StartsWith("found no return: the best burn", plan.Error);
            Assert.Null(plan.FinishedBy);
        }

        // ---- the flown result (#759) ----

        private static string Miss(double targetPe, double pe, double? targetInc = null, double? inc = null, double? peTolerance = null)
        {
            var input = new Dictionary<string, object> { { "target_pe", targetPe }, { "pe", pe } };
            if (targetInc.HasValue) input["target_inc"] = targetInc.Value;
            if (inc.HasValue) input["inc"] = inc.Value;
            if (peTolerance.HasValue) input["pe_tolerance"] = peTolerance.Value;
            return (string)BlockCalls.ReturnMiss(input)["error"];
        }

        [Fact]
        public void A_flown_periapsis_outside_the_tolerance_is_a_failure_with_the_figure()
        {
            Assert.Equal("the return flown arrives at periapsis 35440 m, not 300000 m", Miss(300000, 35440));
            Assert.Equal("the return flown arrives at periapsis 301001 m, not 300000 m", Miss(300000, 301001));
            Assert.Equal("the return flown arrives at periapsis 298999 m, not 300000 m", Miss(300000, 298999));
        }

        [Fact]
        public void A_flown_periapsis_within_the_tolerance_is_no_miss()
        {
            Assert.Equal("", Miss(35000, 35440));
            Assert.Equal("", Miss(300000, 301000));
            Assert.Equal("", Miss(300000, 299000));
            Assert.Equal("", Miss(300000, 301500, peTolerance: 2000));
        }

        /// <summary>What return_needs_correction decided in the block (its table moved here with it): off target_pe, or off a target_inc given.</summary>
        [Fact]
        public void A_flown_inclination_counts_only_when_one_was_asked()
        {
            Assert.Equal("", Miss(35000, 35900, inc: 12));
            Assert.NotEqual("", Miss(35000, 36100, inc: 12));
            Assert.Equal("", Miss(35000, 35000, 30, 30.4));
            Assert.Equal("the return flown arrives at periapsis 35000 m and inclination 30.60°, not 35000 m and 30.00°", Miss(35000, 35000, 30, 30.6));
        }

        [Fact]
        public void A_flown_check_without_its_figures_is_an_error()
        {
            Assert.Equal("the return's check: target_pe and pe must be numbers", BlockCalls.ReturnMiss(new Dictionary<string, object> { { "target_pe", 300000.0 } })["error"]);
        }

        // ---- the budget (ADR 0010) ----

        [Fact]
        [Trait("Category", "Timing")]
        public void A_mun_return_is_solved_in_about_100_ms()
        {
            var snapshot = Orbiting(Mun, 30000);
            Timing.Within(TypicalMs, () => Assert.Equal("", Solve(snapshot, 35000)["error"]));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void An_ike_return_is_solved_in_about_100_ms()
        {
            var snapshot = Orbiting(Ike, 30000);
            Timing.Within(TypicalMs, () => Assert.Equal("", Solve(snapshot, 35000)["error"]));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_return_with_target_inc_is_solved_in_under_200_ms()
        {
            var snapshot = Orbiting(Mun, 30000, 20);
            Timing.Within(WorstMs, () => Assert.Equal("", Solve(snapshot, 35000, 30)["error"]));
        }

        // ---- what it refuses ----

        [Fact]
        public void Bad_values_are_errors_with_their_reason()
        {
            var snapshot = Orbiting(Mun, 30000);
            Assert.Equal("found no return: target_pe is required", BlockCalls.ReturnBurn(new Dictionary<string, object>(), snapshot)["error"]);
            Assert.Equal("found no return: target_pe must be metres above 0, not -5", Solve(snapshot, -5)["error"]);
            Assert.Equal("found no return: target_inc must be from 0 to 180 degrees, not 200", Solve(snapshot, 35000, 200)["error"]);
            Assert.StartsWith("found no return: the burn must come by latest_ut", (string)Solve(snapshot, 35000, earliest: snapshot.Ut + 100, latest: snapshot.Ut + 50)["error"]);
            Assert.StartsWith("found no return: target_pe must be under", (string)Solve(snapshot, 9e7)["error"]);
            Assert.Equal("found no return: target_inc must be a number", BlockCalls.ReturnBurn(new Dictionary<string, object>
            {
                { "target_pe", 35000.0 }, { "target_inc", "polar" },
            }, snapshot)["error"]);
            Assert.Equal("found no return: no Game Snapshot", BlockCalls.ReturnBurn(new Dictionary<string, object>(), null)["error"]);
        }

        [Fact]
        public void A_craft_round_a_planet_has_no_moon_to_return_from()
        {
            var snapshot = Orbiting(Kerbin, 100000);
            Assert.Equal("found no return: the craft orbits Kerbin, not a moon: return starts from orbit round a moon", Solve(snapshot, 35000)["error"]);
        }

        private static SnapshotBody MoonNamed(string name)
        {
            return name == "Mun" ? Mun : name == "Minmus" ? Minmus : Ike;
        }

        private static double Period(GameSnapshot snapshot)
        {
            var moon = MoonNamed(snapshot.Body);
            return 2 * Math.PI * Math.Sqrt(Math.Pow(snapshot.Orbit.SemiMajorAxis, 3) / moon.Gm);
        }
    }
}
