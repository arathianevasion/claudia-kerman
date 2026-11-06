// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #825: a drop that takes delta-v the rest of the Mission Plan still needs is refused. From flight m20261106-203509-34a6 (Duna landing
    /// and return): a drop took 3,310 m/s while Ascend and Return still needed 1,959. The legs here match no Delta-v Map leg by words, so
    /// each counts at its budget, which makes the need exact.
    /// </summary>
    public sealed class DropGuardTests
    {
        private static GameSnapshot Craft(string name = "Duna Lander")
        {
            var snapshot = new GameSnapshot { Body = "Duna", Situation = "ORBITING", DeltaVSimulator = true, Bodies = DeltaVMapTests.StockSystem() };
            snapshot.Craft.Name = name;
            return snapshot;
        }

        private static PlannedSequence Leg(string name, double deltaV, string vessel = "")
        {
            return new PlannedSequence { Name = name, DeltaV = deltaV, Vessel = vessel };
        }

        /// <summary>The sequence in hand is index 1; Ascend and Return (1,959) is all that follows.</summary>
        private static List<PlannedSequence> DunaPlan()
        {
            return new List<PlannedSequence> { Leg("Capture at Duna", 600), Leg("Drop the lander stage", 0), Leg("Prepare", 1959) };
        }

        private static DropVerdict Check(IList<PlannedSequence> plan, double before, double left, GameSnapshot snapshot = null)
        {
            return DropGuard.Check(snapshot ?? Craft(), plan, 1, before, left);
        }

        [Fact]
        public void ThisFlightsDropIsRefusedWithTheFigures()
        {
            var verdict = Check(DunaPlan(), 3310, 0);

            Assert.True(verdict.Refused);
            Assert.Equal("this drop would take 3310 m/s and leave 0; the plan still needs 1959 m/s: revise the Mission Plan first", verdict.Reason);
            Assert.Equal(3310, verdict.Taken);
            Assert.Equal(1959, verdict.Need);
        }

        [Fact]
        public void AnEmptyTankPassesOnACraftAlreadyShort()
        {
            // 500 m/s left against 1,959 needed, but the drop takes nothing, or gains.
            Assert.False(Check(DunaPlan(), 500, 500).Refused);
            Assert.False(Check(DunaPlan(), 500, 530).Refused);
            Assert.False(Check(DunaPlan(), 500, 500 - DropGuard.Allowance).Refused);
        }

        [Fact]
        public void ADropJustOverTheAllowanceIsChecked()
        {
            Assert.True(Check(DunaPlan(), 500, 500 - DropGuard.Allowance - 1).Refused);
        }

        [Fact]
        public void EnoughLeftForThePlanPassesAndTheMarginIsNotCounted()
        {
            // 1,959 needed: 2,000 left is under the 15% margin (2,253) and still goes.
            Assert.False(Check(DunaPlan(), 3310, 2000).Refused);
            Assert.False(Check(DunaPlan(), 3310, 1959).Refused);
            Assert.True(Check(DunaPlan(), 3310, 1958).Refused);
        }

        [Fact]
        public void NothingLeftInThePlanPasses()
        {
            var plan = new List<PlannedSequence> { Leg("Capture at Duna", 600), Leg("Drop the lander stage", 0) };

            Assert.False(Check(plan, 3310, 0).Refused);
        }

        [Fact]
        public void NoMissionPlanPasses()
        {
            Assert.False(DropGuard.Check(Craft(), null, 1, 3310, 0).Refused);
            Assert.False(DropGuard.Check(Craft(), new List<PlannedSequence>(), -1, 3310, 0).Refused);
        }

        [Fact]
        public void ARevisedPlanWithALowerNeedPasses()
        {
            var revised = new List<PlannedSequence> { Leg("Capture at Duna", 600), Leg("Drop the lander stage", 0), Leg("Prepare", 0) };

            Assert.True(Check(DunaPlan(), 3310, 100).Refused);
            Assert.False(Check(revised, 3310, 100).Refused);
        }

        [Fact]
        public void OnlyTheSequencesOfTheVesselTheDropIsOnCount()
        {
            var plan = new List<PlannedSequence> { Leg("Drop", 0), Leg("Main return", 1959), Leg("Probe hop", 300, "Probe") };

            // The main craft: the probe's 300 is not its need, and a probe with its own engine drops free of the main craft's.
            var main = DropGuard.Check(Craft("Duna Lander"), plan, 0, 3310, 1959);
            Assert.False(main.Refused);
            Assert.Equal(1959, main.Need);
            // The Child Vessel: only its own sequence counts, not the main craft's 1,959.
            var child = DropGuard.Check(Craft("Probe"), plan, 0, 800, 350);
            Assert.False(child.Refused);
            Assert.Equal(300, child.Need);
            Assert.True(DropGuard.Check(Craft("Probe"), plan, 0, 800, 250).Refused);
        }

        [Fact]
        public void ABlockTestGivesItsOwnPlanWithPlanLines()
        {
            var spec = Core.Kos.BlockTestSpec.Parse("t", "block = drop_parts\nplan = Ascend and Return | 99999 | Burns still planned.\nplan = Coast | 0\n");

            Assert.Equal(2, spec.Plan.Count);
            Assert.Equal("Ascend and Return", spec.Plan[0].Name);
            Assert.Equal(99999, spec.Plan[0].DeltaV);
            Assert.Equal("Burns still planned.", spec.Plan[0].Intent);
            Assert.Equal("", spec.Plan[1].Intent);
            Assert.Throws<System.FormatException>(() => Core.Kos.BlockTestSpec.Parse("t", "block = drop_parts\nplan = no delta-v\n"));
        }

        [Fact]
        public void TheBlockCallTakesBeforeAndLeftAndGivesTheVerdict()
        {
            var input = new Dictionary<string, object> { { "before", 3310.0 }, { "left", 0.0 } };
            var result = BlockCalls.DropCheck(input, Craft(), DunaPlan(), 1);

            Assert.Equal("", result["error"]);
            Assert.Equal(true, result["refused"]);
            Assert.Contains("the plan still needs 1959 m/s", (string)result["reason"]);
            Assert.Equal("left is required", (string)BlockCalls.DropCheck(new Dictionary<string, object> { { "before", 1.0 } }, Craft(), DunaPlan(), 1)["error"]);
        }
    }
}
