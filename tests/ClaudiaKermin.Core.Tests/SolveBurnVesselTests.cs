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
    /// <c>solve_burn</c>'s goals on the Target Vessel (#454), <c>match_velocities</c> and <c>course_correction</c>, and the
    /// CORRECTIONBURN block call that re-solves them at fly time, on hand-built Game Snapshots with a synthetic target orbit.
    /// Each answer is checked by its own Kepler propagation of the burn, not by the solver's figures.
    /// </summary>
    public sealed class SolveBurnVesselTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double CraftPe = KerbinR + 100000;
        private const double CraftAp = KerbinR + 150000;
        /// <summary>The target's circular orbit, at the craft's Ap, and how far ahead of the craft it is there, m.</summary>
        private const double TargetR = CraftAp;
        private const double Lead = 3000;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>
        /// A craft at its Pe at UT 0 on a 100 by 150 km orbit, and a Target Vessel on a circle at 150 km that is <see cref="Lead"/>
        /// m ahead of the craft when the craft reaches its Ap: a closest approach of a few km half an orbit on.
        /// </summary>
        private static GameSnapshot Snapshot(string targetBody = "Kerbin", string situation = "ORBITING", bool withTarget = true, double targetInc = 0, double targetLan = 0)
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            var sma = (CraftPe + CraftAp) / 2;
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = (CraftAp - CraftPe) / (CraftAp + CraftPe), MeanAnomalyAtEpoch = 0, Epoch = 0, Body = "Kerbin",
                Periapsis = CraftPe - KerbinR, Apoapsis = CraftAp - KerbinR,
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
                MeanAnomalyAtEpoch = 3,
            });
            if (!withTarget) return snapshot;
            var halfOrbit = Math.PI * Math.Sqrt(sma * sma * sma / KerbinGm);
            var n = Math.Sqrt(KerbinGm / (TargetR * TargetR * TargetR));
            var gm = targetBody == "Mun" ? 6.5138398e10 : KerbinGm;
            snapshot.Target = new SnapshotTarget
            {
                Name = "Station", Body = targetBody, Situation = situation,
                Orbit = new SnapshotOrbit
                {
                    SemiMajorAxis = targetBody == "Mun" ? 300000 : TargetR, Eccentricity = 0, Epoch = 0, Body = targetBody, Inclination = targetInc, Lan = targetLan,
                    // The argument of latitude is LAN + argPe + anomaly: keep the target where it is in the plane.
                    ArgumentOfPeriapsis = (360 - targetLan) % 360,
                    MeanAnomalyAtEpoch = targetBody == "Mun" ? 0 : Math.PI + Lead / TargetR - n * halfOrbit,
                },
            };
            return snapshot;
        }

        private static Conic CraftOrbit(GameSnapshot s)
        {
            var o = s.Orbit;
            return Conic.FromElements(KerbinGm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static Conic TargetOrbit(GameSnapshot s)
        {
            var o = s.Target.Orbit;
            return Conic.FromElements(KerbinGm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        /// <summary>The craft's orbit after a burn of the given maneuver-node components at <paramref name="ut"/>, in stock's node frame.</summary>
        private static Conic Burned(Conic craft, double ut, double prograde, double normal, double radial)
        {
            Vec3 r, v;
            craft.State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            return Conic.FromState(KerbinGm, r, v + along * prograde + up * normal + outward * radial, ut);
        }

        /// <summary>The nearest the two orbits come between the UTs, by brute-force sampling at 1 s and then at 1 ms round the best sample.</summary>
        private static double Nearest(Conic a, Conic b, double from, double to)
        {
            Func<double, double> gap = t => (a.Position(t) - b.Position(t)).Length;
            double best = double.PositiveInfinity, bestT = from;
            for (var t = from; t <= to; t += 1)
                if (gap(t) < best) { best = gap(t); bestT = t; }
            for (var t = bestT - 1; t <= bestT + 1; t += 1e-3)
                best = Math.Min(best, gap(t));
            return best;
        }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        private static double Dv(JsonElement result, string component)
        {
            return result.GetProperty("dv").GetProperty(component).GetDouble();
        }

        [Fact]
        public void The_fixture_has_a_closest_approach_of_a_few_km_half_an_orbit_on()
        {
            var s = Snapshot();
            var craft = CraftOrbit(s);
            var d = Nearest(craft, TargetOrbit(s), 0, craft.Period);
            Assert.InRange(d, 500, Lead + 1);
        }

        [Fact]
        public void Match_velocities_at_the_closest_approach_leaves_under_0_1_m_s_relative_speed()
        {
            var s = Snapshot();
            var result = Run(s, "{\"goal\":\"match_velocities\"}");
            var ut = result.GetProperty("at_ut").GetDouble();
            var craft = CraftOrbit(s);
            // The burn is at the closest approach, half an orbit on.
            Assert.InRange(ut, craft.Period / 2 - 300, craft.Period / 2 + 300);
            var after = Burned(craft, ut, Dv(result, "prograde"), Dv(result, "normal"), Dv(result, "radial"));
            Vec3 r, v, rt, vt;
            after.State(ut, out r, out v);
            TargetOrbit(s).State(ut, out rt, out vt);
            Assert.True((v - vt).Length < 0.1, "relative speed " + (v - vt).Length);
            var achieved = result.GetProperty("achieved");
            Assert.True(achieved.GetProperty("relative_speed_predicted").GetDouble() < 0.1);
            Assert.Equal((r - rt).Length, achieved.GetProperty("distance_predicted").GetDouble(), 0);
        }

        [Fact]
        public void Course_correction_to_100_m_brings_the_propagated_closest_approach_within_10_m_of_it()
        {
            var s = Snapshot();
            var result = Run(s, "{\"goal\":\"course_correction\",\"value\":100}");
            var ut = result.GetProperty("at_ut").GetDouble();
            Assert.Equal(60, ut, 3);
            var craft = CraftOrbit(s);
            var after = Burned(craft, ut, Dv(result, "prograde"), Dv(result, "normal"), Dv(result, "radial"));
            var d = Nearest(after, TargetOrbit(s), ut, ut + craft.Period);
            Assert.InRange(d, 90, 110);
            var achieved = result.GetProperty("achieved");
            Assert.InRange(achieved.GetProperty("distance_predicted").GetDouble(), 90, 110);
            Assert.True(achieved.GetProperty("relative_speed_predicted").GetDouble() > 0);
            Assert.True(achieved.TryGetProperty("approach_ut", out _));
            // A small correction: a few km over half an orbit is a few m/s.
            Assert.InRange(result.GetProperty("magnitude").GetDouble(), 0.01, 20);
        }

        [Fact]
        public void Course_correction_and_match_velocities_reach_an_inclined_target_out_of_the_craft_plane()
        {
            // 0.02 degrees with the nodes 90 degrees from the approach puts the target about 2.6 km out of plane there.
            var s = Snapshot(targetInc: 0.02, targetLan: 90);
            var craft = CraftOrbit(s);
            var result = Run(s, "{\"goal\":\"course_correction\",\"value\":100}");
            var ut = result.GetProperty("at_ut").GetDouble();
            Assert.NotEqual(0, Dv(result, "normal"));
            var after = Burned(craft, ut, Dv(result, "prograde"), Dv(result, "normal"), Dv(result, "radial"));
            Assert.InRange(Nearest(after, TargetOrbit(s), ut, ut + craft.Period), 90, 110);

            var match = Run(s, "{\"goal\":\"match_velocities\"}");
            var at = match.GetProperty("at_ut").GetDouble();
            var matched = Burned(craft, at, Dv(match, "prograde"), Dv(match, "normal"), Dv(match, "radial"));
            Vec3 r, v, rt, vt;
            matched.State(at, out r, out v);
            TargetOrbit(s).State(at, out rt, out vt);
            Assert.True((v - vt).Length < 0.1, "relative speed " + (v - vt).Length);
            // The burn is at the closest approach: the distance there is the brute-force nearest to within a metre.
            Assert.Equal(Nearest(craft, TargetOrbit(s), 0, craft.Period), (r - rt).Length, 0);
        }

        [Fact]
        public void The_target_may_name_the_Target_Vessel_for_closest_approach()
        {
            var s = Snapshot();
            var matched = Run(s, "{\"goal\":\"match_velocities\",\"target\":\"station\"}").GetProperty("at_ut").GetDouble();
            var sma = Run(s, "{\"goal\":\"set_sma\",\"value\":725000,\"at\":\"closest_approach\",\"target\":\"Station\"}").GetProperty("at_ut").GetDouble();
            Assert.Equal(matched, sma, 1);
        }

        [Fact]
        public void A_snapshot_without_a_Target_Vessel_is_an_error()
        {
            var s = Snapshot(withTarget: false);
            Assert.Contains("no Target Vessel", Error(s, "{\"goal\":\"match_velocities\"}"));
            Assert.Contains("no Target Vessel", Error(s, "{\"goal\":\"course_correction\",\"value\":100}"));
        }

        [Fact]
        public void A_Target_Vessel_in_another_sphere_of_influence_is_an_error()
        {
            var s = Snapshot(targetBody: "Mun");
            Assert.Contains("another sphere of influence", Error(s, "{\"goal\":\"match_velocities\"}"));
            Assert.Contains("another sphere of influence", Error(s, "{\"goal\":\"course_correction\",\"value\":100}"));
        }

        [Fact]
        public void A_landed_Target_Vessel_or_a_body_target_is_an_error()
        {
            Assert.Contains("landed", Error(Snapshot(situation: "LANDED"), "{\"goal\":\"match_velocities\"}"));
            Assert.Contains("is a body", Error(Snapshot(), "{\"goal\":\"match_velocities\",\"target\":\"Mun\"}"));
            Assert.Contains("needs a value", Error(Snapshot(), "{\"goal\":\"course_correction\"}"));
            Assert.Contains("takes no value", Error(Snapshot(), "{\"goal\":\"match_velocities\",\"value\":3}"));
        }

        [Fact]
        public void CORRECTIONBURN_solves_both_goals_at_fly_time()
        {
            var s = Snapshot();
            var correction = BlockCalls.CorrectionBurn(new Dictionary<string, object> { { "target_distance", 100.0 }, { "earliest_ut", 60.0 } }, s);
            Assert.Equal("", correction["error"]);
            var craft = CraftOrbit(s);
            var ut = (double)correction["ut"];
            var after = Burned(craft, ut, (double)correction["prograde"], (double)correction["normal"], (double)correction["radial"]);
            Assert.InRange(Nearest(after, TargetOrbit(s), ut, ut + craft.Period), 90, 110);
            Assert.InRange((double)correction["distance_predicted"], 90, 110);

            var match = BlockCalls.CorrectionBurn(new Dictionary<string, object> { { "match_velocities", true } }, s);
            Assert.Equal("", match["error"]);
            Assert.True((double)match["relative_speed_predicted"] < 0.1);
        }

        [Fact]
        public void CORRECTIONBURN_with_a_window_lets_the_solver_choose_the_burn_time()
        {
            var s = Snapshot();
            var correction = BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target_distance", 100.0 }, { "earliest_ut", 60.0 }, { "latest_ut", 600.0 },
            }, s);
            Assert.Equal("", correction["error"]);
            Assert.InRange((double)correction["ut"], 60, 600);
            Assert.InRange((double)correction["distance_predicted"], 90, 110);
        }

        [Fact]
        public void CORRECTIONBURN_and_ORBITBURN_refuse_what_they_do_not_take()
        {
            var s = Snapshot();
            Assert.Contains("approach", (string)BlockCalls.CorrectionBurn(new Dictionary<string, object> { { "target_distance", 100.0 }, { "earliest_ut", 2000.0 } }, s)["error"]);
            Assert.Contains("unknown input target_pe", (string)BlockCalls.CorrectionBurn(new Dictionary<string, object> { { "target_distance", 100.0 }, { "target_pe", 1.0 } }, s)["error"]);
            Assert.Contains("no Target Vessel", (string)BlockCalls.CorrectionBurn(new Dictionary<string, object> { { "target_distance", 100.0 } }, Snapshot(withTarget: false))["error"]);
            Assert.Contains("CORRECTIONBURN", (string)BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", "match_velocities" } }, s)["error"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_course_correction_to_the_Target_Vessel_is_solved_in_under_100_ms()
        {
            var s = Snapshot();
            Timing.Within(100, () => Assert.False(_tools.Run("solve_burn", "{\"goal\":\"course_correction\",\"value\":100}", s, new NoPlanningBody()).IsError));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void Match_velocities_is_solved_in_under_20_ms()
        {
            var s = Snapshot();
            Timing.Within(20, () => Assert.False(_tools.Run("solve_burn", "{\"goal\":\"match_velocities\"}", s, new NoPlanningBody()).IsError));
        }
    }
}
