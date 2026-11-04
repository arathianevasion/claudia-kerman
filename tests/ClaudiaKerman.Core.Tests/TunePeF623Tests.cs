// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #538: the Mun encounter of mission f623 (Mun Shot v2) rebuilt from its events: Kerbin Ap 17173 km, Pe 76 km, then Mun Pe about 1.7 Mm.
    /// tune_pe patch next burned halfway to the sphere-of-influence change, where the retrograde burn that solve_burn finds near the
    /// start for about 25 m/s costs over a hundred: past tune_pe's max_dv, so it reported no burn. These pin the numbers the block's
    /// burn points (kerboscript/library/tune_pe.ks, tune_pe_next_times) were chosen from.
    /// </summary>
    public sealed class TunePeF623Tests
    {
        private const double KerbinR = 600000;

        private static GameSnapshot Encounter()
        {
            double rp = KerbinR + 76000, ra = KerbinR + 17173000;
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = 0, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = 0, Body = "Kerbin", Periapsis = 76000, Apoapsis = 17173000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000,
                MeanAnomalyAtEpoch = 1.945,
            });
            return snapshot;
        }

        private static double? RetrogradeDv(string at)
        {
            var outcome = PlanningTools.Default().Run("solve_burn", "{\"goal\":\"next_patch_pe\",\"value\":30000,\"direction\":\"retrograde\",\"at\":\"" + at + "\"}", Encounter(), new NoPlanningBody());
            if (outcome.IsError) return null;
            using (var doc = JsonDocument.Parse(outcome.Json)) return -doc.RootElement.GetProperty("dv").GetProperty("prograde").GetDouble();
        }

        [Fact]
        public void The_encounter_is_the_one_f623_had()
        {
            var outcome = PlanningTools.Default().Run("predict_burn", "{\"burns\":[{\"at\":\"in 600\",\"dv\":{\"prograde\":0}}],\"target\":\"Mun\"}", Encounter(), new NoPlanningBody());
            using (var doc = JsonDocument.Parse(outcome.Json))
            {
                var patches = doc.RootElement.GetProperty("patches");
                Assert.Equal("Mun", patches[1].GetProperty("body").GetString());
                Assert.InRange(patches[1].GetProperty("pe").GetDouble(), 1400000, 2000000);
            }
        }

        [Fact]
        public void A_retrograde_burn_near_the_start_fixes_it_in_about_the_f623_delta_v_and_one_halfway_does_not()
        {
            var start = RetrogradeDv("in 300");
            Assert.NotNull(start);
            Assert.InRange(start.Value, 16, 40);
            // Halfway to the sphere-of-influence change (about 8000 s on): over tune_pe's default max_dv of 50.
            var halfway = RetrogradeDv("in 8000");
            Assert.NotNull(halfway);
            Assert.True(halfway.Value > 50, "halfway costs " + halfway.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
