// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:CORRECTIONBURN</c>, the Course Correction of <c>transfer_to</c> and <c>return</c> (#449), through
    /// <see cref="BlockCalls"/>: from EjectionBurnTests' Mun transfer with its ejection flown 2 m/s off, seen five minutes after
    /// the burn, each answer is checked by flying its node through <c>predict_burn</c>, the propagation the solver does not share
    /// a line of code with past <see cref="BodySystem.Follow"/>.
    /// </summary>
    public sealed class CorrectionBurnTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double TargetPe = 30000;
        /// <summary>The acceptance's bounds: periapsis within 1 km, inclination within 0.5°, solved in under 100 ms.</summary>
        private const double PeBound = 1000;
        private const double IncBound = 0.5;
        private const double BudgetMs = 100;

        /// <summary>A circular, equatorial 100 km orbit at UT 0, the Mun at mean anomaly 1.7 (EjectionBurnTests' start).</summary>
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

        /// <summary>The perturbed transfer: the snapshot five minutes after the ejection, and the UT halfway from then to the Mun's sphere of influence.</summary>
        private sealed class Coast
        {
            public GameSnapshot Snapshot;
            public double Mid;
            public double SoiUt;
        }

        /// <summary>The ejection 2 m/s off in a direction with a share in every axis, so the arrival's inclination is off too.</summary>
        private static readonly Lazy<Coast> Perturbed = new Lazy<Coast>(() => Ejected(new Vec3(0.6, 0.48, 0.64) * 2));

        /// <summary>The ejection 2 m/s off in the Mun's plane alone.</summary>
        private static readonly Lazy<Coast> InPlane = new Lazy<Coast>(() => Ejected(new Vec3(0.6, 0.8, 0) * 2));

        private static Coast Ejected(Vec3 error)
        {
            var snapshot = Circular();
            var ejection = BlockCalls.EjectionBurn(new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", TargetPe }, { "earliest_ut", 0.0 } }, snapshot);
            Assert.Equal("", ejection["error"]);
            var ut = D(ejection, "ut");
            var start = Conic.FromElements(KerbinGm, KerbinR + 100000, 0, 0, 0, 0, 0, 0);
            Vec3 r, v;
            start.State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            var burn = along * D(ejection, "prograde") + up * D(ejection, "normal") + outward * D(ejection, "radial");
            var ejected = Conic.FromState(KerbinGm, r, v + burn + error, ut);

            var coast = new GameSnapshot { Ut = ut + 300, Situation = "ORBITING", Body = "Kerbin", Orbit = Elements(ejected, "Kerbin") };
            coast.Bodies.AddRange(snapshot.Bodies);
            var system = new BodySystem(coast.Bodies);
            var kerbin = system.Find("Kerbin");
            var patch = system.Follow(kerbin, Conic.FromElements(KerbinGm, coast.Orbit.SemiMajorAxis, coast.Orbit.Eccentricity, coast.Orbit.Inclination,
                coast.Orbit.Lan, coast.Orbit.ArgumentOfPeriapsis, coast.Orbit.MeanAnomalyAtEpoch, coast.Orbit.Epoch), coast.Ut, 1)[0];
            Assert.Equal(PatchEnd.Encounter, patch.End);
            return new Coast { Snapshot = coast, SoiUt = patch.EndUt, Mid = (coast.Ut + patch.EndUt) / 2 };
        }

        /// <summary>KSP's elements of <paramref name="orbit"/>, as the Game Snapshot carries the craft's orbit.</summary>
        private static SnapshotOrbit Elements(Conic orbit, string body)
        {
            var lan = Math.Atan2(orbit.W.X, -orbit.W.Y);
            var node = new Vec3(Math.Cos(lan), Math.Sin(lan), 0);
            var argPe = Math.Atan2(Vec3.Dot(orbit.P, Vec3.Cross(orbit.W, node)), Vec3.Dot(orbit.P, node));
            return new SnapshotOrbit
            {
                SemiMajorAxis = orbit.A, Eccentricity = orbit.E, Inclination = orbit.Inclination, Lan = lan * 180 / Math.PI,
                ArgumentOfPeriapsis = argPe * 180 / Math.PI, MeanAnomalyAtEpoch = orbit.M0, Epoch = orbit.Epoch, Body = body,
                Periapsis = orbit.PeR - KerbinR, Apoapsis = orbit.ApR - KerbinR,
            };
        }

        private static IDictionary<string, object> Correct(GameSnapshot snapshot, double earliest, double? latest = null, double? inc = null,
            string target = "Mun", double pe = TargetPe)
        {
            var input = new Dictionary<string, object> { { "target", target }, { "target_pe", pe }, { "earliest_ut", earliest } };
            if (latest.HasValue) input["latest_ut"] = latest.Value;
            if (inc.HasValue) input["target_inc"] = inc.Value;
            return BlockCalls.CorrectionBurn(input, snapshot);
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

        [Fact]
        public void The_perturbed_transfer_misses_target_pe_before_its_correction()
        {
            var coast = Perturbed.Value;
            double pe, inc;
            Flown(coast.Snapshot, new Dictionary<string, object> { { "ut", coast.Mid }, { "prograde", 0.0 }, { "normal", 0.0 }, { "radial", 0.0 } }, "Mun", out pe, out inc);
            Assert.True(Math.Abs(pe - TargetPe) > 10 * PeBound, "the 2 m/s error moved periapsis only to " + pe + " m");
        }

        [Fact]
        public void A_correction_halfway_reaches_target_pe_by_its_node()
        {
            var coast = Perturbed.Value;
            var node = Correct(coast.Snapshot, coast.Mid);
            Assert.Equal("", node["error"]);
            Assert.Equal(coast.Mid, D(node, "ut"));
            double pe, inc;
            Flown(coast.Snapshot, node, "Mun", out pe, out inc);
            Assert.InRange(pe, TargetPe - PeBound, TargetPe + PeBound);
            Assert.InRange(D(node, "pe_predicted"), TargetPe - PeBound, TargetPe + PeBound);
            Assert.InRange(D(node, "dv"), 0.01, 50);
            Assert.Equal(D(node, "dv"), Math.Sqrt(D(node, "prograde") * D(node, "prograde") + D(node, "normal") * D(node, "normal") + D(node, "radial") * D(node, "radial")), 6);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(150)]
        public void A_correction_halfway_reaches_target_pe_and_target_inc_by_its_node(double targetInc)
        {
            var coast = Perturbed.Value;
            var node = Correct(coast.Snapshot, coast.Mid, inc: targetInc);
            Assert.Equal("", node["error"]);
            double pe, inc;
            Flown(coast.Snapshot, node, "Mun", out pe, out inc);
            Assert.InRange(pe, TargetPe - PeBound, TargetPe + PeBound);
            Assert.InRange(inc, targetInc - IncBound, targetInc + IncBound);
            Assert.InRange(D(node, "inc_predicted"), targetInc - IncBound, targetInc + IncBound);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_correction_to_target_pe_and_target_inc_is_solved_in_under_100_ms()
        {
            var coast = Perturbed.Value;
            Timing.Within(BudgetMs, () => Assert.Equal("", Correct(coast.Snapshot, coast.Mid, inc: 30)["error"]));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_correction_to_target_pe_is_solved_in_under_100_ms()
        {
            var coast = Perturbed.Value;
            Timing.Within(BudgetMs, () => Assert.Equal("", Correct(coast.Snapshot, coast.Mid)["error"]));
        }

        [Fact]
        public void A_window_lets_the_solver_choose_a_burn_time_no_dearer_than_halfway()
        {
            var coast = Perturbed.Value;
            var earliest = coast.Snapshot.Ut + 60;
            var halfway = Correct(coast.Snapshot, coast.Mid);
            var free = Correct(coast.Snapshot, earliest, coast.Mid);
            Assert.Equal("", free["error"]);
            Assert.InRange(D(free, "ut"), earliest, coast.Mid);
            // The two solves' step tolerance, about 2 mm/s each.
            Assert.True(D(free, "dv") <= D(halfway, "dv") + 0.005, D(free, "dv") + " m/s free against " + D(halfway, "dv") + " m/s halfway");
            double pe, inc;
            Flown(coast.Snapshot, free, "Mun", out pe, out inc);
            Assert.InRange(pe, TargetPe - PeBound, TargetPe + PeBound);
        }

        /// <summary>
        /// return's correction, once the craft has left the moon: the body it orbits is the target, so the patch it burns on is
        /// the one whose periapsis is set. A path home from the Mun, apoapsis at the Mun's orbit, periapsis 45 km over Kerbin.
        /// </summary>
        [Fact]
        public void A_correction_round_the_body_orbited_sets_its_own_periapsis()
        {
            var snapshot = Circular();
            const double pe = KerbinR + 45000, ap = 12000000;
            snapshot.Ut = 1000;
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (pe + ap) / 2, Eccentricity = (ap - pe) / (ap + pe), Body = "Kerbin", Periapsis = 45000, Apoapsis = ap - KerbinR,
                ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = Math.PI, Epoch = 1000,
            };
            var node = Correct(snapshot, 4000, target: "Kerbin", pe: 35000);
            Assert.Equal("", node["error"]);
            double flownPe, inc;
            Flown(snapshot, node, "Kerbin", out flownPe, out inc);
            Assert.InRange(flownPe, 35000 - PeBound, 35000 + PeBound);
            // Near apoapsis, lowering periapsis 10 km takes a few m/s, retrograde.
            Assert.True(D(node, "prograde") < 0);
            Assert.InRange(D(node, "dv"), 0.5, 20);
        }

        // ---- what it refuses ----

        [Theory]
        [InlineData("target_pe", "found no correction: target_pe is required")]
        [InlineData("target", "found no correction: target must be a body's name")]
        public void A_missing_input_is_an_error(string leftOut, string error)
        {
            var coast = Perturbed.Value;
            var input = new Dictionary<string, object> { { "target", "Mun" }, { "target_pe", TargetPe }, { "earliest_ut", coast.Mid } };
            input.Remove(leftOut);
            var result = BlockCalls.CorrectionBurn(input, coast.Snapshot);
            Assert.Equal(error, result["error"]);
            Assert.Single(result);
        }

        [Fact]
        public void Bad_values_are_errors_with_their_reason()
        {
            var coast = Perturbed.Value;
            Assert.Equal("found no correction: target_inc must be from 0 to 180 degrees, not 200", Correct(coast.Snapshot, coast.Mid, inc: 200)["error"]);
            Assert.Equal("found no correction: target_pe must be metres above 0, not -5", Correct(coast.Snapshot, coast.Mid, pe: -5)["error"]);
            Assert.StartsWith("found no correction: the burn must come by latest_ut", (string)Correct(coast.Snapshot, coast.Mid, coast.Mid - 10)["error"]);
            Assert.StartsWith("found no correction: earliest_ut", (string)Correct(coast.Snapshot, coast.Snapshot.Ut - 10)["error"]);
            Assert.StartsWith("found no correction: there is no body named \"Eeloo\"", (string)Correct(coast.Snapshot, coast.Mid, target: "Eeloo")["error"]);
            Assert.Equal("found no correction: target_inc must be a number of degrees", BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Mun" }, { "target_pe", TargetPe }, { "target_inc", "polar" },
            }, coast.Snapshot)["error"]);
            Assert.Equal("found no correction: no Game Snapshot", BlockCalls.CorrectionBurn(new Dictionary<string, object>(), null)["error"]);
        }

        [Fact]
        public void A_path_with_no_encounter_has_nothing_to_correct()
        {
            var result = Correct(Circular(), 100);
            Assert.StartsWith("found no correction: the trajectory from the burn does not reach Mun", (string)result["error"]);
        }

        [Fact]
        public void A_window_across_the_change_of_sphere_of_influence_is_an_error()
        {
            var coast = Perturbed.Value;
            var result = Correct(coast.Snapshot, coast.Mid, coast.SoiUt + 60);
            Assert.StartsWith("found no correction: the burn window crosses the change of sphere of influence", (string)result["error"]);
        }

        /// <summary>The equatorial side a coast arrives on at the Mun without a correction: 0° prograde, 180° retrograde.</summary>
        private static double Equator(Coast coast)
        {
            double pe, inc;
            Flown(coast.Snapshot, new Dictionary<string, object> { { "ut", coast.Mid }, { "prograde", 0.0 }, { "normal", 0.0 }, { "radial", 0.0 } }, "Mun", out pe, out inc);
            return inc > 90 ? 180.0 : 0.0;
        }

        /// <summary>
        /// In patched conics the arrival round the Mun is exactly equatorial only when the Kerbin conic is in the Mun's plane
        /// (ADR 0010). A coast perturbed in that plane alone keeps it, so an equatorial arrival is met within the acceptance's 0.5°.
        /// </summary>
        [Fact]
        public void An_equatorial_arrival_from_a_coast_in_the_moons_plane_is_met()
        {
            var coast = InPlane.Value;
            var equator = Equator(coast);
            var node = Correct(coast.Snapshot, coast.Mid, inc: equator);
            Assert.Equal("", node["error"]);
            double pe, inc;
            Flown(coast.Snapshot, node, "Mun", out pe, out inc);
            Assert.InRange(pe, TargetPe - PeBound, TargetPe + PeBound);
            Assert.InRange(Math.Abs(inc - equator), 0, IncBound);
        }

        /// <summary>
        /// From a coast tilted out of the Mun's plane no burn arrives exactly equatorial, and the pair of normal components does
        /// not tell 0° from 180° (ADR 0010): the solve's best point is judged in degrees, so a best point that is not the
        /// inclination asked for is an error, and one that carries the best burn.
        /// </summary>
        [Fact]
        public void An_equatorial_arrival_from_a_tilted_coast_is_judged_in_degrees()
        {
            var coast = Perturbed.Value;
            var result = Correct(coast.Snapshot, coast.Mid, inc: Equator(coast));
            Assert.StartsWith("found no correction: the best burn", (string)result["error"]);
            Assert.True(result.ContainsKey("dv"));
            Assert.True(Math.Abs(D(result, "inc_predicted") - Equator(coast)) > IncBound);
        }
    }
}
