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

        /// <summary>TURNTIME's answer, with its 190 degree figures.</summary>
        private static Dictionary<string, object> Turn(double turnS, double topRate)
        {
            return new Dictionary<string, object> { { "error", "" }, { "turn_time", turnS + 10 }, { "turn_s", turnS }, { "top_rate", topRate } };
        }

        /// <summary>TURNTIME's figures for the Duna ship (KSP.log 14:43): 190 degrees in 53.9 s at up to 4.1 deg/s.</summary>
        private static readonly Dictionary<string, object> DunaTurn = Turn(53.9, 4.1);

        /// <summary>TURNTIME's figures for the Mun craft (KSP.log 14:41): 190 degrees in 55.8 s at up to 3.9 deg/s.</summary>
        private static readonly Dictionary<string, object> MunTurn = Turn(55.8, 3.9);

        /// <summary>
        /// The nose's degrees off the burn direction at each cutoff, worked from each orbit at 40 km: the climb still to flatten
        /// (the flight path angle) plus the arc to apoapsis.
        /// </summary>
        private const double MunAngle = 39.7;
        private const double DunaAngle = 38.5;

        private static IDictionary<string, object> Decide(double ap, double sma, double eta, Dictionary<string, object> turn, double angle, List<object> stages, double targetAp = 80000, double atmHeight = 70000)
        {
            return LaunchBurnThrough.Call(new Dictionary<string, object>
            {
                { "ap", ap }, { "sma", sma }, { "gm", KerbinGm }, { "radius", KerbinRadius }, { "eta_ap", eta },
                { "target_ap", targetAp }, { "atm_height", atmHeight }, { "stages", stages }, { "turn", turn }, { "angle", angle },
            });
        }

        [Fact]
        public void TheDunaShipBurnsThrough()
        {
            var answer = Decide(79832, 457707, 145.1, DunaTurn, DunaAngle, DunaStages);
            Assert.Equal(true, answer["burn_through"]);
            // The figures it decided on, for the log: 644 m/s to circularize (the trace's orbit), half of it taking about 150 s, already over the 145.1 s left.
            Assert.InRange((double)answer["dv"], 640, 650);
            Assert.InRange((double)answer["half_burn"], 147, 154);
            Assert.Equal(145.1, (double)answer["eta_ap"]);
            Assert.Equal(DunaAngle, (double)answer["angle"]);
        }

        [Fact]
        public void TheMunCraftCoasts()
        {
            var answer = Decide(79819, 483505, 160.3, MunTurn, MunAngle, MunStages);
            Assert.Equal(false, answer["burn_through"]);
            Assert.InRange((double)answer["dv"], 518, 528);
        }

        [Fact]
        public void TheMunCraftCoastsOnItsWheelsAlone()
        {
            // TURNTIME's wheels-alone figures for this craft (KSP.log 14:33): 190 degrees in 187.1 s at up to 1.026 deg/s, turn_time 197.1 s.
            // A half burn of about 22 s plus that 190 degrees is over the 160.3 s left, but the craft is only 39.7 degrees off the burn at cutoff.
            var answer = Decide(79819, 483505, 160.3, Turn(187.1, 1.026), MunAngle, MunStages);
            Assert.Equal(false, answer["burn_through"]);
            // 39.7 degrees at 1.026 deg/s, 2 s to reach and leave that rate, and 10 s to settle.
            Assert.InRange((double)answer["turn_time"], 50, 52);
        }

        [Fact]
        public void TheTurnIsTheAngleAtTheCutoffNotTheWhole190Degrees()
        {
            Assert.InRange((double)Decide(79819, 483505, 160.3, MunTurn, 190, MunStages)["turn_time"], 65.7, 65.9);
            Assert.True((double)Decide(79819, 483505, 160.3, MunTurn, 20, MunStages)["turn_time"] < (double)Decide(79819, 483505, 160.3, MunTurn, MunAngle, MunStages)["turn_time"]);
        }

        [Fact]
        public void ATurnThatNeverReachesItsTopRateScalesAsTheSquareRootOfTheAngle()
        {
            // 190 degrees in 20 s peaking at 19 deg/s: a quarter of the angle takes half the turn, plus 10 s to settle.
            Assert.InRange((double)Decide(79819, 483505, 160.3, Turn(20, 19), 47.5, MunStages)["turn_time"], 19.9, 20.1);
        }

        [Fact]
        public void ACraftAlreadyOnTheBurnNeedsNoTurn()
        {
            Assert.Equal(0.0, (double)Decide(79819, 483505, 160.3, MunTurn, 0, MunStages)["turn_time"]);
        }

        [Fact]
        public void TheScaledTurnStillCountsWhenItTipsTheDecision()
        {
            // About 140 s of half burn against 160.3 s left: under it alone, over it with the turn at the real angle (about 27 s).
            var answer = Decide(79819, 483505, 160.3, MunTurn, MunAngle, Stages(Stage(2000, 815, 300)));
            Assert.InRange((double)answer["half_burn"], 135, 145);
            Assert.InRange((double)answer["turn_time"], 26, 29);
            Assert.Equal(true, answer["burn_through"]);
        }

        [Fact]
        public void TheMunCraftCoastsWithTheDunaShipsCircularize()
        {
            // The brief's bound: 646 m/s or less at normal thrust.
            Assert.Equal(false, Decide(79819, 457707, 160.3, MunTurn, MunAngle, MunStages)["burn_through"]);
        }

        [Fact]
        public void AHopNeverBurnsThrough()
        {
            Assert.Equal(false, Decide(60000, 457707, 20, DunaTurn, DunaAngle, DunaStages, 60000)["burn_through"]);
        }

        [Fact]
        public void ANearlyEmptyHighThrustStageDoesNotHideTheWeakStageAfterIt()
        {
            // On the first stage's thrust alone, half of 644 m/s would take about 12 s, well under the 145.1 s left.
            var answer = Decide(79832, 457707, 145.1, DunaTurn, DunaAngle, DunaStages);
            Assert.True((double)answer["half_burn"] > 100);
        }

        [Fact]
        public void ANearlyEmptyWeakStageDoesNotCondemnTheStrongStageAfterIt()
        {
            // 5 m/s of a 60 kN stage left, then a stage with about 20 m/s^2: the half burn is about 24 s, not the LV-N's 150.
            var stages = Stages(Stage(5, 3.5, 800), Stage(2000, 100, 300));
            var answer = Decide(79832, 457707, 145.1, DunaTurn, DunaAngle, stages);
            Assert.Equal(false, answer["burn_through"]);
            Assert.InRange((double)answer["half_burn"], 20, 30);
        }

        [Fact]
        public void ABurnPastTheLastStageRunsOnAtThatStagesRate()
        {
            // As claudia_staged_burn_time: 322 m/s asked of a 100 m/s stage that takes 50 s runs on at 0.5 s per m/s.
            var answer = Decide(79832, 457707, 145.1, DunaTurn, 0, Stages(Stage(100, 50, 300)));
            Assert.InRange((double)answer["half_burn"], 155, 167);
            Assert.Equal(true, answer["burn_through"]);
        }

        [Fact]
        public void NoStagesWithDeltaVCoastAsToday()
        {
            var answer = Decide(79832, 457707, 145.1, DunaTurn, DunaAngle, Stages(Stage(0, 0, 0)));
            Assert.Equal(false, answer["burn_through"]);
            Assert.Equal(-1.0, (double)answer["half_burn"]);
        }

        [Fact]
        public void ATurnTimeThatFailedCountsAsNone()
        {
            var answer = LaunchBurnThrough.Call(new Dictionary<string, object>
            {
                { "ap", 79832.0 }, { "sma", 457707.0 }, { "gm", KerbinGm }, { "radius", KerbinRadius }, { "eta_ap", 145.1 },
                { "target_ap", 80000.0 }, { "atm_height", 70000.0 }, { "stages", DunaStages }, { "angle", DunaAngle },
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
            Assert.Equal(false, Decide(79832, sma, 145.1, DunaTurn, DunaAngle, DunaStages)["burn_through"]);
        }
    }
}
