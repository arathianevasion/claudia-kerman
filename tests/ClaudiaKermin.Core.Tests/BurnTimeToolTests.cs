// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary><c>predict_burn</c> and <c>solve_burn</c> taking the burn-time references of <see cref="BurnTime"/> (#442), through the Planning Tools entry point.</summary>
    public sealed class BurnTimeToolTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The craft in a 100 km by 1000 km orbit inclined 30° with its Pe 60° past the ascending node, at its Pe at UT 0.</summary>
        private static GameSnapshot Snapshot(double inclination = 30)
        {
            double rp = KerbinR + 100000, ra = KerbinR + 1000000;
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = inclination, Lan = 20, ArgumentOfPeriapsis = 60,
                MeanAnomalyAtEpoch = 0, Epoch = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 1000000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
            });
            return snapshot;
        }

        private JsonElement Run(string tool, GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run(tool, input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(string tool, GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run(tool, input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        private static string Burn(string at)
        {
            return "{\"burns\":[{\"at\":" + at + ",\"dv\":{\"prograde\":10}}]}";
        }

        [Fact]
        public void Predict_burn_places_a_burn_at_each_reference_and_a_later_one_after_it()
        {
            var snapshot = Snapshot();
            var craft = Conic.FromElements(KerbinGm, snapshot.Orbit.SemiMajorAxis, snapshot.Orbit.Eccentricity, 30, 20, 60, 0, 0);
            foreach (var at in new[] { "\"eq_an\"", "\"eq_dn\"", "\"eq_nearest\"", "\"eq_highest\"", "\"altitude 500000 ascending\"", "\"altitude 500000 descending\"" })
            {
                var ut = Run("predict_burn", snapshot, Burn(at)).GetProperty("burns")[0].GetProperty("at_ut").GetDouble();
                var r = craft.Position(ut);
                if (at.Contains("eq_")) Assert.True(Math.Abs(r.Z) / r.Length < 1e-2, at); // at_ut is rounded to the second
                else Assert.True(Math.Abs(r.Length - (KerbinR + 500000)) < 1000, at);
            }
            foreach (var at in new[] { "\"rel_an\"", "\"rel_dn\"", "\"closest_approach\"" })
            {
                var result = Run("predict_burn", snapshot, Burn(at).Replace("}]}", "}],\"target\":\"Minmus\"}"));
                Assert.True(result.GetProperty("burns")[0].GetProperty("at_ut").GetDouble() > 0, at);
            }
        }

        [Fact]
        public void A_second_burn_at_a_reference_is_after_the_first()
        {
            var snapshot = Snapshot();
            var burns = Run("predict_burn", snapshot, "{\"burns\":[{\"at\":\"in 2000\",\"dv\":{\"prograde\":0}},{\"at\":\"eq_an\",\"dv\":{\"prograde\":0}}]}").GetProperty("burns");
            Assert.True(burns[1].GetProperty("at_ut").GetDouble() > burns[0].GetProperty("at_ut").GetDouble());
        }

        [Fact]
        public void Predict_burn_says_why_a_reference_has_no_answer()
        {
            Assert.Contains("equatorial, so it has no equatorial nodes", Error("predict_burn", Snapshot(0), Burn("\"eq_an\"")));
            Assert.Contains("needs target", Error("predict_burn", Snapshot(), Burn("\"rel_an\"")));
            Assert.Contains("does not cross 5000000 m", Error("predict_burn", Snapshot(), Burn("\"altitude 5000000\"")));
            Assert.Contains("is not in orbit round Kerbin", Error("predict_burn", Snapshot(), Burn("\"rel_dn\"").Replace("}]}", "}],\"target\":\"Sun\"}")));
        }

        [Fact]
        public void Solve_burn_takes_the_references_for_next_patch_pe_and_validates_target()
        {
            var snapshot = Snapshot();
            Assert.Contains("equatorial, so it has no equatorial nodes",
                Error("solve_burn", Snapshot(0), "{\"goal\":\"next_patch_pe\",\"value\":50000,\"at\":\"eq_an\"}"));
            Assert.Contains("needs target", Error("solve_burn", snapshot, "{\"goal\":\"next_patch_pe\",\"value\":50000,\"at\":\"rel_an\"}"));
            Assert.Contains("target is for at rel_an", Error("solve_burn", snapshot, "{\"goal\":\"next_patch_pe\",\"value\":50000,\"at\":\"pe\",\"target\":\"Mun\"}"));
            Assert.Contains("target is for next_patch_pe", Error("solve_burn", snapshot, "{\"goal\":\"circularize\",\"at\":\"pe\",\"target\":\"Mun\"}"));
            Assert.Contains("no body named", Error("solve_burn", snapshot, "{\"goal\":\"next_patch_pe\",\"value\":50000,\"at\":\"rel_an\",\"target\":\"Moho\"}"));
        }

        [Fact]
        public void The_at_line_of_each_description_lists_every_reference()
        {
            foreach (var tool in new IPlanningTool[] { new PredictBurn(), new SolveBurn() })
            {
                var line = tool.Description;
                foreach (var word in new[] { "ap", "pe", "altitude", "eq_an", "eq_dn", "eq_nearest", "eq_highest", "rel_an", "rel_dn", "closest_approach" })
                    Assert.Contains(word, line);
                Assert.DoesNotContain("\n", tool.Description);
            }
        }
    }
}
