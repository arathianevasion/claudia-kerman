// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #932: at launch's first cutoff, whether a coast and a circularize can work (<see cref="LaunchBurnThrough"/>). The figures are
    /// flights' own, from their launch traces and stage tables: m20261108-220659-ac85 (the Duna ship) and m20261108-202939-bfd5 (the Mun craft).
    /// </summary>
    public sealed class LaunchBurnThroughTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinRadius = 600000;

        /// <summary>A stage as claudia_burn_stages gives it.</summary>
        private static Dictionary<string, object> Stage(double dv, double duration, double isp)
        {
            return new Dictionary<string, object> { { "dv", dv }, { "duration", duration }, { "isp", isp } };
        }

        private static List<object> Stages(params Dictionary<string, object>[] stages)
        {
            var list = new List<object>();
            list.AddRange(stages);
            return list;
        }

        /// <summary>The Duna ship at its cutoff: the last of the 1000 kN stage (0.49 t of fuel left), then the 60 kN LV-N stages.</summary>
        private static readonly List<object> DunaStages = Stages(Stage(39.3, 1.51, 315), Stage(1038, 523, 800), Stage(1233, 523, 800));

        /// <summary>The Mun craft at its cutoff: the last of the 695 kN stage, then the 215 kN stage on 19.38 t.</summary>
        private static readonly List<object> MunStages = Stages(Stage(6.2, 0.22, 313), Stage(2277, 146, 320), Stage(2274, 196, 345));

        private static IDictionary<string, object> Decide(double ap, double sma, double eta, double turn, List<object> stages, double targetAp = 80000, double atmHeight = 70000)
        {
            return LaunchBurnThrough.Call(new Dictionary<string, object>
            {
                { "ap", ap }, { "sma", sma }, { "gm", KerbinGm }, { "radius", KerbinRadius }, { "eta_ap", eta },
                { "target_ap", targetAp }, { "atm_height", atmHeight }, { "stages", stages },
                { "turn", new Dictionary<string, object> { { "error", "" }, { "turn_time", turn } } },
            });
        }

        [Fact]
        public void TheDunaShipBurnsThrough()
        {
            var answer = Decide(79832, 457707, 145.1, 63.9, DunaStages);
            Assert.Equal(true, answer["burn_through"]);
            // The figures it decided on, for the log: 644 m/s to circularize (the trace's orbit), half of it taking about 150 s.
            Assert.InRange((double)answer["dv"], 640, 650);
            Assert.InRange((double)answer["half_burn"], 147, 154);
            Assert.Equal(63.9, (double)answer["turn_time"]);
            Assert.Equal(145.1, (double)answer["eta_ap"]);
        }

        [Fact]
        public void TheMunCraftCoasts()
        {
            var answer = Decide(79819, 483505, 160.3, 65.8, MunStages);
            Assert.Equal(false, answer["burn_through"]);
            Assert.InRange((double)answer["dv"], 518, 528);
        }

        [Fact]
        public void TheMunCraftCoastsWithTheDunaShipsCircularize()
        {
            // The brief's bound: 646 m/s or less at normal thrust.
            Assert.Equal(false, Decide(79819, 457707, 160.3, 65.8, MunStages)["burn_through"]);
        }

        [Fact]
        public void AHopNeverBurnsThrough()
        {
            Assert.Equal(false, Decide(60000, 457707, 20, 63.9, DunaStages, 60000)["burn_through"]);
        }

        [Fact]
        public void ANearlyEmptyHighThrustStageDoesNotHideTheWeakStageAfterIt()
        {
            // On the first stage's thrust alone, half of 644 m/s would take about 12 s, and 12 + 63.9 is under the 145.1 s left.
            var answer = Decide(79832, 457707, 145.1, 63.9, DunaStages);
            Assert.True((double)answer["half_burn"] > 100);
        }

        [Fact]
        public void ANearlyEmptyWeakStageDoesNotCondemnTheStrongStageAfterIt()
        {
            // 5 m/s of a 60 kN stage left, then a stage with about 20 m/s^2: the half burn is about 24 s, not the LV-N's 150.
            var stages = Stages(Stage(5, 3.5, 800), Stage(2000, 100, 300));
            var answer = Decide(79832, 457707, 145.1, 63.9, stages);
            Assert.Equal(false, answer["burn_through"]);
            Assert.InRange((double)answer["half_burn"], 20, 30);
        }

        [Fact]
        public void ABurnPastTheLastStageRunsOnAtThatStagesRate()
        {
            // As claudia_staged_burn_time: 322 m/s asked of a 100 m/s stage that takes 50 s runs on at 0.5 s per m/s.
            var answer = Decide(79832, 457707, 145.1, 0, Stages(Stage(100, 50, 300)));
            Assert.InRange((double)answer["half_burn"], 155, 167);
            Assert.Equal(true, answer["burn_through"]);
        }

        [Fact]
        public void NoStagesWithDeltaVCoastAsToday()
        {
            var answer = Decide(79832, 457707, 145.1, 63.9, Stages(Stage(0, 0, 0)));
            Assert.Equal(false, answer["burn_through"]);
            Assert.Equal(-1.0, (double)answer["half_burn"]);
        }

        [Fact]
        public void ATurnTimeThatFailedCountsAsNone()
        {
            var answer = LaunchBurnThrough.Call(new Dictionary<string, object>
            {
                { "ap", 79832.0 }, { "sma", 457707.0 }, { "gm", KerbinGm }, { "radius", KerbinRadius }, { "eta_ap", 145.1 },
                { "target_ap", 80000.0 }, { "atm_height", 70000.0 }, { "stages", DunaStages },
                { "turn", new Dictionary<string, object> { { "error", "no usable torque" } } },
            });
            Assert.Equal(0.0, (double)answer["turn_time"]);
            Assert.Equal(true, answer["burn_through"]);
        }

        [Fact]
        public void AnAnswerWithoutFiguresStillSaysToCoast()
        {
            var answer = LaunchBurnThrough.Call(null);
            Assert.Equal(false, answer["burn_through"]);
            Assert.NotEqual("", answer["error"]);
        }

        [Theory]
        [InlineData(-5000000)] // hyperbolic
        [InlineData(0)]
        public void AnOrbitWithNoApoapsisCoasts(double sma)
        {
            Assert.Equal(false, Decide(79832, sma, 145.1, 63.9, DunaStages)["burn_through"]);
        }
    }
}
