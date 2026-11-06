// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#696: the wait to a Mission Script's first burn point, from the Game Snapshot, before Approval.</summary>
    public sealed class BurnPointsTests
    {
        private const double Now = 3769816;
        private const double Period = 700000;

        /// <summary>An orbiting craft <paramref name="toAp"/> s from its apoapsis (so half a period less, or more, from its periapsis).</summary>
        internal static GameSnapshot Orbiting(double toAp, double periapsis = 1000000)
        {
            var n = 2 * Math.PI / Period;
            return new GameSnapshot
            {
                Ut = Now,
                Situation = "ORBITING",
                Body = "Kerbin",
                Orbit = new SnapshotOrbit { Apoapsis = 30000000, Periapsis = periapsis, Eccentricity = 0.8, Period = Period, Epoch = Now, MeanAnomalyAtEpoch = Math.PI - n * toAp, Body = "Kerbin" },
            };
        }

        [Fact]
        public void TunePeWithItsDefaultPatchWaitsForTheApoapsis()
        {
            // Mission m20261103-015018-6567's script, about 304,000 s from apoapsis.
            var script = "if hasnode { remove nextnode. } local res is tune_pe(lexicon(\"value\", 19780000, \"max_dv\", 5)). addons:claudia:report(res).";
            var wait = BurnPoints.First(script, Orbiting(304000), Now, 5);
            Assert.NotNull(wait);
            Assert.Equal("tune_pe", wait.Block);
            Assert.Equal(304000, wait.WaitSeconds, 0);
        }

        [Fact]
        public void TunePeTakesTheNextApoapsisWhenThisOneIsTooClose()
        {
            var wait = BurnPoints.First("tune_pe(lexicon(\"value\", 50000)).", Orbiting(60), Now, 5);
            Assert.Equal(60 + Period, wait.WaitSeconds, 0);
        }

        [Fact]
        public void TunePeOnTheNextPatchOrAlreadyInToleranceHasNoFixedPoint()
        {
            Assert.Null(BurnPoints.First("tune_pe(lexicon(\"value\", 50000, \"patch\", \"next\")).", Orbiting(304000), Now, 5));
            Assert.Null(BurnPoints.First("tune_pe(lexicon(\"value\", 1000500)).", Orbiting(304000), Now, 5));
            // A periapsis below the surface reached before the (next) apoapsis: tune_pe burns early, at no fixed point.
            Assert.Null(BurnPoints.First("tune_pe(lexicon(\"value\", 50000)).", Orbiting(60, periapsis: -1000), Now, 5));
        }

        [Fact]
        public void CircularizeWaitsForTheApsisItNamesOrItsDefault()
        {
            Assert.Equal(1000, BurnPoints.First("local res is circularize(lexicon(\"at\", \"ap\")).", Orbiting(1000), Now, 50).WaitSeconds, 0);
            Assert.Equal(1000, BurnPoints.First("local res is circularize().", Orbiting(1000), Now, 50).WaitSeconds, 0);
            Assert.Equal(1000 + Period / 2, BurnPoints.First("circularize(lexicon(\"at\", \"pe\")).", Orbiting(1000), Now, 50).WaitSeconds, 0);
            Assert.Null(BurnPoints.First("circularize(lexicon(\"at\", 80000)).", Orbiting(1000), Now, 50));
        }

        [Fact]
        public void ChangeOrbitWaitsOnlyForAnApsisItNames()
        {
            Assert.Equal(2000, BurnPoints.First("change_orbit(lexicon(\"goal\", \"set_pe\", \"value\", 90000, \"at\", \"ap\")).", Orbiting(2000), Now, 30).WaitSeconds, 0);
            Assert.Null(BurnPoints.First("change_orbit(lexicon(\"goal\", \"set_inc\", \"value\", 90)).", Orbiting(2000), Now, 30));
        }

        [Fact]
        public void AnAddedNodeThenExecNodeWaitsForTheNodeUt()
        {
            Assert.Equal(5000, BurnPoints.First("add node(" + (Now + 5000) + ", 0, 0, 120). local res is exec_node().", Orbiting(1000), Now, 120).WaitSeconds, 0);
            Assert.Equal(600, BurnPoints.First("add node(time:seconds + 600, 0, 0, 120). local res is exec_node().", Orbiting(1000), Now, 120).WaitSeconds, 0);
            Assert.Null(BurnPoints.First("add node(time:seconds + eta:apoapsis, 0, 0, 120). exec_node().", Orbiting(1000), Now, 120));
        }

        [Fact]
        public void ExecNodeAloneFliesTheNodeAlreadyPlanned()
        {
            var snapshot = Orbiting(1000);
            snapshot.Nodes.Add(new SnapshotNode { Ut = Now + 9000, DeltaV = 300 });
            Assert.Equal(9000, BurnPoints.First("local res is exec_node().", snapshot, Now, 0).WaitSeconds, 0);
            Assert.Null(BurnPoints.First("remove nextnode. exec_node().", snapshot, Now, 0));
        }

        [Fact]
        public void AScriptWithNoRecognisableBurnPointGivesNothing()
        {
            Assert.Null(BurnPoints.First("print \"hop\".", Orbiting(1000), Now, 0));
            Assert.Null(BurnPoints.First("local o is lexicon(\"at\", \"ap\"). circularize(o).", Orbiting(1000), Now, 0));
            Assert.Null(BurnPoints.First("launch(lexicon(\"target_ap\", 80000)). circularize().", Orbiting(1000), Now, 0));
            Assert.Null(BurnPoints.First("local res is exec_node().", Orbiting(1000), Now, 0));
            var landed = Orbiting(1000);
            landed.Situation = "LANDED";
            Assert.Null(BurnPoints.First("circularize().", landed, Now, 0));
        }

        [Fact]
        public void TheMinimumDurationIsTheWaitTheBurnAndTheTurnRoundedUp()
        {
            var wait = BurnPoints.First("circularize().", Orbiting(1000), Now, 50);
            // No stages to estimate the burn from: the allowance.
            Assert.Equal(BurnPoints.BurnAllowanceSeconds, wait.BurnSeconds);
            Assert.Equal(1000 + 120 + 60, wait.MinimumDurationSeconds);
        }

        [Fact]
        public void APlanIsShortOnlyWhenItsDurationDoesntCoverTheMinimum()
        {
            var plan = new SequencePlan { Script = "circularize().", ExpectedDurationSeconds = 1179, ExpectedDeltaV = 50 };
            Assert.NotNull(BurnPoints.ShortOf(plan, Orbiting(1000), Now));
            plan.ExpectedDurationSeconds = 1180;
            Assert.Null(BurnPoints.ShortOf(plan, Orbiting(1000), Now));
        }

        // ---- #749: transfer_to and return wait inside their block ----

        private const double SunAltitude = 13599840256 - 261600000;

        private static string Script(double window)
        {
            return "local res is transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 50000, \"window\", "
                + window.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + ")). addons:claudia:report(res).";
        }

        [Fact]
        public void TransferToFromASunOrbitWaitsForTheEjectionCoreSolves()
        {
            // The player's Duna flight: from a Sun orbit, the window 41M s away.
            var rig = TransferRig.Circular("Sun", SunAltitude, 0);
            var wait = BurnPoints.First(Script(41e6), rig.Snapshot(), 0, 1000);
            Assert.NotNull(wait);
            Assert.Equal("transfer_to", wait.Block);
            Assert.True(wait.WaitSeconds > 30e6 && wait.WaitSeconds < 60e6, wait.WaitSeconds.ToString());
            Assert.Equal(wait.WaitSeconds, wait.DepartureUt.Value, 0);
            Assert.Equal(wait.DepartureUt.Value - WindowWaits.LaunchLeadS, BurnPoints.DueTime(wait, 0).Value, 0);
        }

        [Fact]
        public void ALongTransferWaitIsADueTimeAndOnlyTheLeadNeedsCovering()
        {
            var rig = TransferRig.Circular("Sun", SunAltitude, 0);
            // expected_duration_s of 8,000,000 s, as Claudia wrote it, is no cover for the lead's burn... but the 41M s wait is not asked of it.
            var plan = new SequencePlan { Script = Script(41e6), ExpectedDurationSeconds = 100, ExpectedDeltaV = 1000 };
            var short_ = BurnPoints.ShortOf(plan, rig.Snapshot(), 0);
            Assert.NotNull(short_);
            Assert.Equal(WindowWaits.LaunchLeadS, short_.WaitSeconds);
            plan.ExpectedDurationSeconds = 8000000;
            Assert.Null(BurnPoints.ShortOf(plan, rig.Snapshot(), 0));
        }

        [Fact]
        public void AShorterTransferWaitMustBeCoveredByTheDuration()
        {
            // A return whose window is 2 h away (a number; transfer_to's own search lands where the solver does).
            var rig = TransferRig.Circular("Mun", 50000, 0);
            var script = "local res is return(lexicon(\"window\", 7200)).";
            var wait = BurnPoints.First(script, rig.Snapshot(), 0, 100);
            Assert.NotNull(wait);
            Assert.False(BurnPoints.DueTime(wait, 0).HasValue);
            var plan = new SequencePlan { Script = script, ExpectedDurationSeconds = 3600, ExpectedDeltaV = 100 };
            var short_ = BurnPoints.ShortOf(plan, rig.Snapshot(), 0);
            Assert.NotNull(short_);
            Assert.Equal(wait.MinimumDurationSeconds, short_.MinimumDurationSeconds);
            Assert.True(short_.MinimumDurationSeconds > 7200);
            plan.ExpectedDurationSeconds = wait.MinimumDurationSeconds;
            Assert.Null(BurnPoints.ShortOf(plan, rig.Snapshot(), 0));
        }

        [Fact]
        public void ReturnWaitsOnlyForAWindowItNames()
        {
            var rig = TransferRig.Circular("Mun", 50000, 0);
            Assert.Null(BurnPoints.First("local res is return().", rig.Snapshot(), 0, 100));
            var wait = BurnPoints.First("local res is return(lexicon(\"window\", 9000000)).", rig.Snapshot(), 0, 100);
            Assert.Equal("return", wait.Block);
            Assert.Equal(9000000, wait.WaitSeconds, 0);
        }

        // ---- a return aimed at a landing (#792) ----

        private sealed class AimFrame : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private static GameSnapshot AtTheMun(double phase)
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = 230000, Eccentricity = 0, Body = "Mun", Periapsis = 30000, Apoapsis = 30000, Epoch = 1000, Period = 2 * Math.PI * Math.Sqrt(Math.Pow(230000, 3) / 6.5138398e10) };
            snapshot.Bodies.AddRange(new[]
            {
                new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 },
                new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48, RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14 },
                new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = phase },
            });
            return snapshot;
        }

        /// <summary>The window the block itself picks from there, with the research's capsule for a craft.</summary>
        private static double BlockPick(GameSnapshot snapshot, double earliest)
        {
            var node = BlockCalls.ReturnBurn(new Dictionary<string, object> { { "target_pe", 30000.0 }, { "earliest_ut", earliest }, { "lat", -0.0972 }, { "lng", -74.5577 } },
                snapshot, new AimFrame(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", node["error"]);
            return Convert.ToDouble(node["ut"], CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData("\"ksc\"")]
        [InlineData("latlng(-0.0972, -74.5577)")]
        [InlineData("LATLNG(-0.0972,-74.5577)")]
        [InlineData("lexicon(\"lat\", -0.0972, \"lng\", -74.5577)")]
        public void AReturnWithATargetWaitsForTheWindowItWillPick(string target)
        {
            var snapshot = AtTheMun(3.0);
            var wait = BurnPoints.First("local res is return(lexicon(\"target\", " + target + ")).", snapshot, 1000, 5, new AimFrame());
            Assert.NotNull(wait);
            Assert.Equal("return", wait.Block);
            Assert.Equal("the ejection", wait.Point);
            Assert.InRange(wait.WaitSeconds, 0, 7.2 * 3600);
            Assert.Equal(1000 + wait.WaitSeconds, wait.DepartureUt.Value, 3);
            // The block's own craft may pick another window (here, with the capsule, a whole turn on): the duration covers any of them.
            Assert.InRange(wait.PickSpanSeconds, 7 * 3600, 7.2 * 3600);
            Assert.InRange(BlockPick(snapshot, 1000) + 150 - 1000, 0, wait.MinimumDurationSeconds);
        }

        [Fact]
        public void AnAimedReturnWithAShortDurationGoesBack()
        {
            var plan = new SequencePlan { Script = "local res is return(lexicon(\"target\", \"ksc\")).", ExpectedDurationSeconds = 900, ExpectedDeltaV = 300 };
            var short_ = BurnPoints.ShortOf(plan, AtTheMun(3.0), 1000, new AimFrame());
            Assert.NotNull(short_);
            Assert.True(short_.MinimumDurationSeconds > 7 * 3600, "the duration covers the whole of max_wait: " + short_.MinimumDurationSeconds);
            plan.ExpectedDurationSeconds = short_.MinimumDurationSeconds;
            Assert.Null(BurnPoints.ShortOf(plan, AtTheMun(3.0), 1000, new AimFrame()));
        }

        /// <summary>#749: over WindowWaits.LongWaitS from now the wait is a Due Time an hour before; ShortOf then asks only for that hour and the window after it.</summary>
        [Fact]
        public void AnAimedWaitOverTheLongWaitIsADueTime()
        {
            // Somewhere in the Mun's orbit, KSC's window is the last of the turn, from a burn asked to wait two hours first.
            const string script = "local res is return(lexicon(\"target\", \"ksc\", \"window\", 8200)).";
            BurnWait wait = null;
            var phase = 0.0;
            for (; phase < 2 * Math.PI && (wait == null || !BurnPoints.DueTime(wait, 1000).HasValue); phase += 0.05)
                wait = BurnPoints.First(script, AtTheMun(phase), 1000, 5, new AimFrame());
            Assert.True(wait.WaitSeconds > WindowWaits.LongWaitS + WindowWaits.LaunchLeadS, "the longest wait found is " + wait.WaitSeconds);
            Assert.Equal(wait.DepartureUt.Value - WindowWaits.LaunchLeadS, BurnPoints.DueTime(wait, 1000).Value, 3);
            var short_ = BurnPoints.ShortOf(new SequencePlan { Script = script, ExpectedDurationSeconds = 100, ExpectedDeltaV = 300 }, AtTheMun(phase - 0.05), 1000, new AimFrame());
            Assert.Equal(WindowWaits.LaunchLeadS, short_.WaitSeconds);
            // The hour before the window or the whole of max_wait from there, not the wait to the run's start.
            Assert.InRange(short_.MinimumDurationSeconds, 7 * 3600, 7.3 * 3600);
        }

        [Theory]
        [InlineData("local res is return().")]
        [InlineData("local res is return(lexicon(\"target\", \"none\")).")]
        public void AReturnWithNoTargetGivesNoCheck(string script)
        {
            Assert.Null(BurnPoints.First(script, AtTheMun(3.0), 1000, 5, new AimFrame()));
        }

        /// <summary>A target it can't read, or no frame to read it in, still waits up to max_wait: the plan covers all of it, with no window to give a Due Time from.</summary>
        [Theory]
        [InlineData("local res is return(lexicon(\"target\", site)).", true)]
        [InlineData("local res is return(lexicon(\"target\", ship:geoposition)).", true)]
        [InlineData("local res is return(lexicon(\"target\", \"ksc\", \"max_wait\", w)).", false)]
        [InlineData("local res is return(lexicon(\"target\", \"ksc\", \"target_pe\", pe)).", true)]
        [InlineData("local res is return(lexicon(\"target\", \"ksc\")).", false)]
        public void AnAimedReturnItCannotPlaceCoversAllOfMaxWait(string script, bool withFrame)
        {
            var wait = BurnPoints.First(script, AtTheMun(3.0), 1000, 5, withFrame ? new AimFrame() : null);
            if (script.Contains("max_wait"))
            {
                // A max_wait it can't read but a target it can: the estimate takes "auto", without a frame none.
                Assert.InRange(wait.PickSpanSeconds, 7 * 3600, 7.2 * 3600);
                return;
            }
            Assert.NotNull(wait);
            Assert.Null(wait.DepartureUt);
            Assert.Equal(0, wait.WaitSeconds);
            Assert.InRange(wait.LongestWaitSeconds, 7 * 3600, 7.2 * 3600);
        }

        /// <summary>The block refuses "ksc" from a moon of another planet at once: there is no wait to cover.</summary>
        [Fact]
        public void KscFromAnotherPlanetsMoonIsNoWait()
        {
            var snapshot = AtTheMun(3.0);
            snapshot.Body = "Ike";
            snapshot.Orbit.Body = "Ike";
            snapshot.Bodies.AddRange(new[]
            {
                new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, SoiRadius = 47921949, RotationPeriod = 65517.859, Sma = 20726155264, MeanAnomalyAtEpoch = 3.14 },
                new SnapshotBody { Name = "Ike", Parent = "Duna", Gm = 1.8568369e10, Radius = 130000, SoiRadius = 1049598.9, RotationPeriod = 65517.862, Sma = 3200000, MeanAnomalyAtEpoch = 1.7 },
            });
            Assert.Null(BurnPoints.First("local res is return(lexicon(\"target\", \"ksc\")).", snapshot, 1000, 5, new AimFrame()));
        }

        /// <summary>A far-off window is a Due Time: the run starts an hour before it, so the duration is max_wait, not the 100 days to the run.</summary>
        [Fact]
        public void AFarOffAimedWindowAsksForMaxWaitNotTheWaitToTheRun()
        {
            const string script = "local res is return(lexicon(\"target\", \"ksc\", \"window\", 8640000)).";
            var short_ = BurnPoints.ShortOf(new SequencePlan { Script = script, ExpectedDurationSeconds = 100, ExpectedDeltaV = 300 }, AtTheMun(3.0), 1000, new AimFrame());
            Assert.InRange(short_.MinimumDurationSeconds, 7 * 3600, 7.3 * 3600);
        }

        /// <summary>An explicit max_duration bounds the pick as the block's does.</summary>
        [Fact]
        public void AnAimedReturnHonoursItsMaxDuration()
        {
            var wait = BurnPoints.First("local res is return(lexicon(\"target\", \"ksc\", \"max_duration\", 3600)).", AtTheMun(3.0), 1000, 5, new AimFrame());
            Assert.Equal(3600, wait.PickSpanSeconds);
        }

        /// <summary>A short max_wait is the one the block will use.</summary>
        [Fact]
        public void AnAimedReturnHonoursItsMaxWait()
        {
            var wait = BurnPoints.First("local res is return(lexicon(\"target\", \"ksc\", \"max_wait\", 3600)).", AtTheMun(3.0), 1000, 5, new AimFrame());
            Assert.InRange(wait.WaitSeconds, 0, 3600);
        }

        [Fact]
        public void ATransferWithAnUnreadableTargetOrWindowGivesNoCheck()
        {
            var rig = TransferRig.Circular("Sun", SunAltitude, 0);
            Assert.Null(BurnPoints.First("local res is transfer_to(lexicon(\"target\", dest, \"target_pe\", 5)).", rig.Snapshot(), 0, 100));
            Assert.Null(BurnPoints.First("local res is transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 5, \"window\", w)).", rig.Snapshot(), 0, 100));
        }

        [Fact]
        public void BlockCallsReadTheirOptionsAndDefaults()
        {
            var calls = BlockOptionCheck.Calls("local a is circularize(). local b is tune_pe(lexicon(\"value\", 5)). local c is exec_node(opts).");
            Assert.Equal(new[] { "circularize", "tune_pe", "exec_node" }, new List<BlockCall>(calls).ConvertAll(c => c.Block));
            Assert.Equal("\"ap\"", calls[0].Option("at"));
            Assert.Equal("5", calls[1].Option("value"));
            Assert.Equal("\"current\"", calls[1].Option("patch"));
            Assert.Null(calls[2].Option("node"));
        }
    }
}
