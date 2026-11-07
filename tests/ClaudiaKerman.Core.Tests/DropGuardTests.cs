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

        /// <summary>#835's second case: a 10 t craft keeping 5 t, 2 t of it propellant, at Isp 300, with nothing the burn can draw on in what goes.</summary>
        private static DropBurn KeptPays(double spent)
        {
            return new DropBurn { Spent = spent, Whole = 10, Kept = 5, KeptPropellant = 2, Isp = 300 };
        }

        /// <summary>What the kept craft of <see cref="KeptPays"/> has before the burn.</summary>
        private static readonly double KeptLeft = 300 * 9.80665 * System.Math.Log(5.0 / 3.0);

        private static List<PlannedSequence> PlanNeeding(double need)
        {
            return new List<PlannedSequence> { Leg("Capture at Duna", 600), Leg("Drop the lander stage", 0), Leg("Prepare", need) };
        }

        [Fact]
        public void ABurnFlownByTheStageBeingDroppedTakesNothingFromTheKeptTanks()
        {
            // #835's false refusal: 2,100 left, 1,959 needed, a 300 m/s burn. The dropped stage's 3 t pays (about 1.2 t burned), so 2,100 really remains.
            var burn = new DropBurn { Spent = 300, Whole = 12, Kept = 6, KeptPropellant = 3, Isp = 300, GonePropellant = 3 };

            Assert.Equal(2100, DropGuard.AfterBurn(2100, burn), 6);
            var verdict = DropGuard.Check(Craft(), DunaPlan(), 1, 3310, 2100, burn);
            Assert.False(verdict.Refused);
            Assert.Equal(1959, verdict.Need);
        }

        [Fact]
        public void ABurnPaidFromTheKeptTanksCostsTheKeptCraftMoreThanTheBurn()
        {
            // #835's under-count: 100 m/s on the whole 10 t burns 0.334 t, about 203 m/s to the 5 t kept craft: 1,300 left, not 1,403.
            Assert.InRange(KeptLeft, 1502, 1504);
            Assert.InRange(DropGuard.AfterBurn(KeptLeft, KeptPays(100)), 1299, 1301);

            var verdict = DropGuard.Check(Craft(), PlanNeeding(1350), 1, 3000, KeptLeft, KeptPays(100));
            Assert.True(verdict.Refused);
            Assert.Equal("this drop would take 1497 m/s and leave 1299 after the 100 m/s burn; the plan still needs 1350 m/s: revise the Mission Plan first", verdict.Reason);
        }

        [Fact]
        public void AStageThatRunsDryMidBurnPaysWhatItHasAndTheKeptTanksTheRest()
        {
            var part = KeptPays(100);
            part.GonePropellant = 0.1; // of the 0.334 t the burn wants

            // By hand: 0.1 t buys 29.6 m/s on 10 t; the other 70.4 on 9.9 t burns 0.2342 t of the kept 2 t.
            var expected = 300 * 9.80665 * System.Math.Log((5 - 0.2342) / 3);
            Assert.InRange(DropGuard.AfterBurn(KeptLeft, part), expected - 1, expected + 1);
        }

        [Fact]
        public void AnyBurningEngineThatStaysHasTheKeptTanksPay()
        {
            var both = KeptPays(100);
            both.GonePropellant = 3;
            both.BurnersKept = 1;

            Assert.Equal(DropGuard.AfterBurn(KeptLeft, KeptPays(100)), DropGuard.AfterBurn(KeptLeft, both), 6);
        }

        [Fact]
        public void AKeptEngineThatBurnsCostsTheKeptTanksAtItsOwnIspNotTheMeanOfAllKeptEngines()
        {
            // A lit 200 kN engine at 300 s and an unlit 60 kN one at 800 s: the mean is 350 s, which is what measures the 1,753 m/s left,
            // but the burn is the lit engine's: 100 m/s on 10 t burns 0.334 t, not the 0.287 t the mean would say (about 34 m/s too high).
            var left = 350 * 9.80665 * System.Math.Log(5.0 / 3.0);
            var burn = KeptPays(100);
            burn.BurnersKept = 1;

            var expected = 350 * 9.80665 * System.Math.Log((5 - 10 * (1 - System.Math.Exp(-100 / (300 * 9.80665)))) / 3);
            Assert.Equal(expected, DropGuard.AfterBurn(left, burn), 6);
            var byMean = 350 * 9.80665 * System.Math.Log((5 - 10 * (1 - System.Math.Exp(-100 / (350 * 9.80665)))) / 3);
            Assert.InRange(byMean - expected, 33, 35);
        }

        [Fact]
        public void WhatTheDroppedStageLeavesIsFlownAtTheKeptEnginesIsp()
        {
            // An 800 s stage with 0.1 t buys 78 m/s of a 300 m/s burn; a 300 s kept engine burns far more for the other 222 than the 800 s figure says.
            var burn = KeptPays(300);
            burn.Isp = 800;
            burn.GonePropellant = 0.1;

            var dryStage = 800 * 9.80665 * System.Math.Log(10 / 9.9);
            var fromKept = 9.9 * (1 - System.Math.Exp(-(300 - dryStage) / (300 * 9.80665)));
            var expected = 300 * 9.80665 * System.Math.Log((5 - fromKept) / 3);
            Assert.Equal(expected, DropGuard.AfterBurn(KeptLeft, burn), 6);
        }

        [Fact]
        public void WithNoIspForTheBurnTheDroppedTanksCountForNothingAndTheKeptTanksPay()
        {
            var noIsp = KeptPays(100);
            noIsp.Isp = 0;
            noIsp.GonePropellant = 3;

            Assert.Equal(DropGuard.AfterBurn(KeptLeft, KeptPays(100)), DropGuard.AfterBurn(KeptLeft, noIsp), 6);
        }

        [Fact]
        public void WithNothingDroppedAroundItTheBurnCostsItsOwnFigure()
        {
            // Kept craft = whole craft: the kept tanks pay at the kept mass, so 300 m/s costs 300.
            var exhaust = 350 * 9.80665;
            var burn = new DropBurn { Spent = 300, Whole = 10, Kept = 10, KeptPropellant = 10 * (1 - System.Math.Exp(-2100 / exhaust)), Isp = 350 };

            Assert.Equal(1800, DropGuard.AfterBurn(2100, burn), 6);
            var verdict = DropGuard.Check(Craft(), DunaPlan(), 1, 3310, 2100, burn);
            Assert.True(verdict.Refused);
            Assert.Equal("this drop would take 1210 m/s and leave 1800 after the 300 m/s burn; the plan still needs 1959 m/s: revise the Mission Plan first", verdict.Reason);
        }

        [Fact]
        public void ABurnThatWantsMoreThanTheKeptTanksHoldLeavesNothing()
        {
            Assert.Equal(0, DropGuard.AfterBurn(KeptLeft, KeptPays(5000)));
        }

        [Fact]
        public void WithoutTheCraftsFiguresTheBurnComesOffFlat()
        {
            Assert.Equal(1800, DropGuard.AfterBurn(2100, new DropBurn { Spent = 300 }), 6);
            Assert.Equal(0, DropGuard.AfterBurn(200, new DropBurn { Spent = 300 }), 6);
        }

        [Fact]
        public void TheBurnDoesNotTurnADropThatTakesNothingIntoARefusal()
        {
            // Taken is what the drop takes, not the burn: an empty tank still goes, however large the burn.
            var atAllowance = DropGuard.Check(Craft(), DunaPlan(), 1, 500, 500 - DropGuard.Allowance, KeptPays(300));
            Assert.False(atAllowance.Refused);
            Assert.Equal(DropGuard.Allowance, atAllowance.Taken);
            var gains = DropGuard.Check(Craft(), DunaPlan(), 1, 500, 530, KeptPays(300));
            Assert.False(gains.Refused);
            Assert.Equal(0, gains.Taken);
        }

        [Fact]
        public void ANegativeBurnCreditsNothing()
        {
            Assert.Equal(1958, DropGuard.AfterBurn(1958, KeptPays(-500)));
            var verdict = DropGuard.Check(Craft(), DunaPlan(), 1, 3310, 1958, KeptPays(-500));
            Assert.True(verdict.Refused);
            Assert.Equal("this drop would take 1352 m/s and leave 1958; the plan still needs 1959 m/s: revise the Mission Plan first", verdict.Reason);
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

        /// <summary>
        /// #837's follow-up: under a Mission Plan a step's later legs are counted from its plan_step. A Sequence Plan naming plan_step N
        /// is the step in hand at index N - 1 (MissionControl passes <c>CurrentSequence</c>, StepsBegun - 1), so its later legs are the
        /// plan after N. Here plan_step 2 leaves Prepare (1,959) to fly; plan_step 3 leaves none.
        /// </summary>
        [Fact]
        public void UnderAMissionPlanTheLaterLegsCountFromThePlanStepAndABurnThatSpendsThemIsRefused()
        {
            // plan_step 2 (index 1): the drop leaves 2,100, 1,959 is still needed, and a 300 m/s burn takes it to 1,800: refused.
            var burn = new DropBurn { Spent = 300 };

            var verdict = DropGuard.Check(Craft(), DunaPlan(), 1, 3310, 2100, burn);

            Assert.True(verdict.Refused);
            Assert.Equal(1959, verdict.Need);
            Assert.Equal("this drop would take 1210 m/s and leave 1800 after the 300 m/s burn; the plan still needs 1959 m/s: revise the Mission Plan first", verdict.Reason);
        }

        [Fact]
        public void UnderAMissionPlanAStepWithNoLaterLegsLetsTheSameBurnGo()
        {
            // plan_step 3 (index 2) is the last step: nothing after it is counted, so the drop and the burn both pass.
            var burn = new DropBurn { Spent = 300 };

            var verdict = DropGuard.Check(Craft(), DunaPlan(), 2, 3310, 2100, burn);

            Assert.False(verdict.Refused);
            Assert.Equal(0, verdict.Need);
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
        public void TheBlockCallTakesTheBurnAndItsFigures()
        {
            Dictionary<string, object> Ask(double goneProp) => new Dictionary<string, object>
            {
                { "before", 3000.0 }, { "left", KeptLeft }, { "spent", 100.0 }, { "whole", 10.0 }, { "kept", 5.0 }, { "prop", 2.0 }, { "isp", 300.0 }, { "gone_prop", goneProp },
            };

            var refused = BlockCalls.DropCheck(Ask(0), Craft(), PlanNeeding(1350), 1);
            Assert.Equal(true, refused["refused"]);
            Assert.Contains("leave 1299 after the 100 m/s burn", (string)refused["reason"]);
            Assert.Equal(false, BlockCalls.DropCheck(Ask(1), Craft(), PlanNeeding(1350), 1)["refused"]);
            var keptEngineBurns = Ask(1);
            keptEngineBurns["burners_kept"] = 1.0;
            Assert.Equal(true, BlockCalls.DropCheck(keptEngineBurns, Craft(), PlanNeeding(1350), 1)["refused"]);

            // Spent alone comes off flat; taken at the allowance and a negative burn go through the call as through Check.
            var flat = new Dictionary<string, object> { { "before", 3310.0 }, { "left", 2100.0 }, { "spent", 300.0 } };
            Assert.Contains("leave 1800 after the 300 m/s burn", (string)BlockCalls.DropCheck(flat, Craft(), DunaPlan(), 1)["reason"]);
            var atAllowance = new Dictionary<string, object> { { "before", 500.0 }, { "left", 490.0 }, { "spent", 300.0 } };
            Assert.Equal(false, BlockCalls.DropCheck(atAllowance, Craft(), DunaPlan(), 1)["refused"]);
            var negative = new Dictionary<string, object> { { "before", 3310.0 }, { "left", 1958.0 }, { "spent", -500.0 } };
            Assert.Contains("leave 1958; the plan", (string)BlockCalls.DropCheck(negative, Craft(), DunaPlan(), 1)["reason"]);
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
