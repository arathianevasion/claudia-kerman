// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #935 (ADR 0014): the one burn-timing rule (<see cref="StagedBurnTime"/>), which <c>claudia_burn_timing</c> and <c>launch</c>'s burn-through both use.
    /// These are the cases the Kerboscript function's tests and #932's parity test held.
    /// </summary>
    public sealed class StagedBurnTimeTests
    {
        private const double G0 = 9.80665;

        private static Dictionary<string, object> Stage(double dv, double duration, double isp)
        {
            return new Dictionary<string, object> { { "dv", dv }, { "duration", duration }, { "isp", isp } };
        }

        /// <summary>A stage as stock gives it from its masses (t), thrust (kN) and isp: the delta-v, the time to burn it all, and its isp.</summary>
        private static Dictionary<string, object> Rocket(double mass, double dry, double thrust, double isp)
        {
            var ve = isp * G0;
            return Stage(ve * Math.Log(mass / dry), (mass - dry) * ve / thrust, isp);
        }

        /// <summary>The time to burn dv from mass at thrust: the rocket equation.</summary>
        private static double RocketEquation(double dv, double mass, double thrust, double isp)
        {
            var ve = isp * G0;
            return mass * ve / thrust * (1 - Math.Exp(-dv / ve));
        }

        private static List<object> Stages(params Dictionary<string, object>[] stages)
        {
            var list = new List<object>();
            list.AddRange(stages);
            return list;
        }

        [Fact]
        public void ABurnWithinOneStageTakesTheRocketEquationTime()
        {
            Assert.Equal(RocketEquation(500, 10, 100, 300), StagedBurnTime.Of(500, Stages(Rocket(10, 5, 100, 300))), 6);
        }

        [Fact]
        public void ABurnWithinOneStageWithNoIspIsAStraightShareOfItsDuration()
        {
            Assert.Equal(25.0, StagedBurnTime.Of(100, Stages(Stage(400, 100, 0))), 9);
        }

        [Fact]
        public void ABurnPastAStageBurnsItAllThenTheNextFromItsOwnMass()
        {
            var firstDv = 250 * G0 * Math.Log(20.0 / 14);
            var expected = (20.0 - 14) * 250 * G0 / 60 + RocketEquation(1200 - firstDv, 6, 200, 320);
            Assert.Equal(expected, StagedBurnTime.Of(1200, Stages(Rocket(20, 14, 60, 250), Rocket(6, 3, 200, 320))), 6);
        }

        [Fact]
        public void StagesWithNoDeltaVAreSkippedAndABurnPastTheLastStageRunsOnAtItsRate()
        {
            var empty = Stage(0, 0, 0);
            var dv = 300 * G0 * Math.Log(2);
            var duration = 5.0 * 300 * G0 / 100;
            Assert.Equal(duration + 100 * duration / dv, StagedBurnTime.Of(dv + 100, Stages(empty, Rocket(10, 5, 100, 300))), 6);
            // 322 m/s asked of a 100 m/s stage that takes 50 s runs on at 0.5 s per m/s.
            Assert.Equal(50 + 222 * 0.5, StagedBurnTime.Of(322, Stages(Stage(100, 50, 300))), 9);
        }

        [Fact]
        public void WithNoThrustThereIsNoBurnTime()
        {
            Assert.Equal(-1.0, StagedBurnTime.Of(100, Stages(Stage(0, 0, 0))));
            Assert.Equal(-1.0, StagedBurnTime.Of(100, Stages()));
            Assert.Equal(-1.0, StagedBurnTime.Of(100, null));
        }

        // The figures #932's parity test used: Duna ship's last stages, a weak stage then a strong one, and a stage with no isp.
        [Theory]
        [InlineData(39.3, 1.51, 315, 1038, 523, 800)]
        [InlineData(100, 50, 300, 100, 60, 320)]
        [InlineData(5, 3.5, 800, 2000, 100, 0)]
        public void ABurnAcrossTwoStagesMatchesTheirParts(double dv1, double seconds1, double isp1, double dv2, double seconds2, double isp2)
        {
            var stages = Stages(Stage(dv1, seconds1, isp1), Stage(dv2, seconds2, isp2));
            var ve1 = isp1 * G0;
            Assert.Equal(seconds1 * (1 - Math.Exp(-dv1 / 2 / ve1)) / (1 - Math.Exp(-dv1 / ve1)), StagedBurnTime.Of(dv1 / 2, stages), 9);
            Assert.Equal(seconds1 + seconds2, StagedBurnTime.Of(dv1 + dv2, stages), 9);
        }

        [Fact]
        public void HalfTheDeltaVTakesMoreThanHalfTheTimeWhenAWeakStageComesFirst()
        {
            var stages = Stages(Rocket(20, 14, 60, 250), Rocket(6, 3, 200, 320));
            Assert.True(StagedBurnTime.Of(600, stages) > StagedBurnTime.Of(1200, stages) / 2 + 10);
        }

        [Fact]
        public void TheCallGivesTheBurnAndItsHalfBurnFromOneStageTable()
        {
            var stages = Stages(Rocket(20, 14, 60, 250), Rocket(6, 3, 200, 320));
            var answer = StagedBurnTime.Call(new Dictionary<string, object> { { "dv", 1200.0 }, { "stages", stages } });
            Assert.Equal("", answer["error"]);
            Assert.Equal(StagedBurnTime.Of(1200, stages), (double)answer["burn_time"], 9);
            Assert.Equal(StagedBurnTime.Of(600, stages), (double)answer["half_burn"], 9);
        }

        [Fact]
        public void TheCallWithNoThrustGivesMinusOneForBoth()
        {
            var answer = StagedBurnTime.Call(new Dictionary<string, object> { { "dv", 500.0 }, { "stages", Stages() } });
            Assert.Equal(-1.0, (double)answer["burn_time"]);
            Assert.Equal(-1.0, (double)answer["half_burn"]);
        }

        [Fact]
        public void TheCallWithoutADeltaVSaysSoAndGivesNoBurnTime()
        {
            var answer = StagedBurnTime.Call(new Dictionary<string, object> { { "stages", Stages(Stage(100, 50, 300)) } });
            Assert.Equal("dv is required", answer["error"]);
            Assert.Equal(-1.0, (double)answer["burn_time"]);
            Assert.Equal("nothing to time", (string)StagedBurnTime.Call(null)["error"]);
        }
    }
}
