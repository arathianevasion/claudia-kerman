// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Contracts;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1029: Core decides the moment a planned part test runs (ADR 0021): every condition holding at once, in the block its window names;
    /// the block only stages or runs the part's Run Test at the signal. A window that passes unrun is explained for the Debrief.
    /// </summary>
    public sealed class PartTestWatchTests
    {
        private static ChosenRequirement Booster(TestWindow window, bool chute = false, double minSpd = 210, double maxSpd = 280)
        {
            return new ChosenRequirement
            {
                Key = "c-test/0", Title = "PartTest", Words = "test solidBooster.sm.v2 through 8,000–12,000 m", Body = "Kerbin", Reach = ContractReach.Hop,
                Test = new PartTestSpec
                {
                    Part = chute ? "parachuteSingle" : "solidBooster.sm.v2", Situation = "FLYING", Chute = chute, Window = window,
                    Band = new HopBand { MinAltitude = 8000, MaxAltitude = 12000, MinSpeed = minSpd, MaxSpeed = maxSpd },
                },
            };
        }

        private static TestCraft At(double altitude, double speed, bool canRun = true, bool canStage = false, bool chuteOpen = false, string situation = "FLYING", string part = "solidBooster.sm.v2")
        {
            return new TestCraft
            {
                Body = "Kerbin", Altitude = altitude, Speed = speed, Situation = situation,
                Parts = new Dictionary<string, TestPart> { { part, new TestPart { Uid = "42", CanRun = canRun, CanStage = canStage, ChuteOpen = chuteOpen } } },
            };
        }

        [Fact]
        public void TheTestRunsAtTheFirstTickEveryConditionHoldsAndOnlyOnce()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Ascent) }, "Kerbin");

            Assert.Equal("", watch.Tick("launch", At(7000, 230)).How);
            Assert.Equal("", watch.Tick("launch", At(8500, 300)).How);
            var signal = watch.Tick("launch", At(9000, 250));
            Assert.Equal("run", signal.How);
            Assert.Equal("42", signal.Uid);
            Assert.Equal("", watch.Tick("launch", At(9100, 250)).How);
            Assert.True(watch.Ran("c-test/0"));
        }

        [Fact]
        public void ATestIsRunOnlyInTheBlockItsWindowNames()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Descent) }, "Kerbin");

            Assert.Equal("", watch.Tick("launch", At(9000, 250)).How);
            Assert.Equal("run", watch.Tick("reentry", At(9000, 250)).How);
        }

        [Fact]
        public void AWindowPassedTooFastIsExplainedWithTheSpeedThroughTheBand()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Ascent) }, "Kerbin");

            foreach (var tick in new[] { At(7000, 400), At(8200, 460), At(11000, 520), At(12500, 560) }) Assert.Equal("", watch.Tick("launch", tick).How);

            Assert.False(watch.Ran("c-test/0"));
            Assert.Equal("speed 460 m/s, above the band, through 8–12 km", watch.Gap("c-test/0"));
        }

        [Fact]
        public void APartWithNoRunTestThatIsTheNextStageIsStaged()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Ascent) }, "Kerbin");

            Assert.Equal("stage", watch.Tick("launch", At(9000, 250, false, true)).How);
        }

        /// <summary>ADR 0019: the chute opens at reentry's own timing; the test runs on the open chute, and a stage is never fired for it.</summary>
        [Fact]
        public void AChuteTestWaitsForReentryToOpenTheChuteAndNeverStages()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Descent, true) }, "Kerbin");

            Assert.Equal("", watch.Tick("reentry", At(11000, 250, false, true, false, "FLYING", "parachuteSingle")).How);
            Assert.Equal("", watch.Tick("reentry", At(10000, 240, false, true, true, "FLYING", "parachuteSingle")).How);
            Assert.Equal("run", watch.Tick("reentry", At(9500, 230, true, true, true, "FLYING", "parachuteSingle")).How);
        }

        [Fact]
        public void AnOpenChuteOfferingNoRunTestIsTheGapNotAShutChute()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Descent, true) }, "Kerbin");

            watch.Tick("reentry", At(10000, 240, false, false, true, "FLYING", "parachuteSingle"));
            watch.Tick("reentry", At(7000, 200, false, false, true, "FLYING", "parachuteSingle"));

            Assert.Equal("the part had no Run Test and wasn't the next stage through 8–12 km", watch.Gap("c-test/0"));
        }

        [Fact]
        public void ATestHomeAfterSplashdownRunsOnceTheCraftIsSplashed()
        {
            var splash = Booster(TestWindow.Descent);
            splash.Test.Band = null;
            splash.Test.Situation = "SPLASHED";
            var watch = new PartTestWatch(new[] { splash }, "Kerbin");

            Assert.Equal("", watch.Tick("reentry", At(200, 40)).How);
            Assert.Equal("run", watch.Tick("reentry", At(0, 1, true, false, false, "SPLASHED")).How);
        }

        /// <summary>ADR 0014 guardrail 6: PARTTEST is asked each tick of launch and reentry; Core's part stays well under 0.1 ms a tick.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void ATickStaysWithinItsBudget()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Ascent), Booster(TestWindow.Descent, true) }, "Kerbin");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10000; i++) watch.Tick("launch", At(7000 + i, 600));
            Assert.True(clock.Elapsed.TotalMilliseconds / 10000 < 0.1, "a tick took " + clock.Elapsed.TotalMilliseconds / 10000 + " ms");
        }

        [Fact]
        public void AMissingPartInTheBandIsTheGap()
        {
            var watch = new PartTestWatch(new[] { Booster(TestWindow.Ascent) }, "Kerbin");

            watch.Tick("launch", At(9000, 250, part: "other"));
            watch.Tick("launch", At(13000, 250, part: "other"));

            Assert.Equal("the part solidBooster.sm.v2 wasn't aboard through 8–12 km", watch.Gap("c-test/0"));
        }
    }
}
