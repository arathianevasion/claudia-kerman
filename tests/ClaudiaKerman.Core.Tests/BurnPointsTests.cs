// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
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
