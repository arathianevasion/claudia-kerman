// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #757: each Maneuver Sequence matched to its Delta-v Map legs and judged against them, from the player's Ike Mission
    /// (m20261105-032927-1d2c): "Capture at Ike and land" was budgeted at 370 m/s and flew 771.
    /// </summary>
    public sealed class PlanLegsTests
    {
        private const string PlannedAscent = "{\"target_ap\": 80000, \"stage_limit\": 0}: {\"body\":\"Kerbin\",\"reaches_orbit\":true,\"dv_to_orbit\":3395,\"dv_left\":4357}";

        /// <summary>The Mun Shot's stages on the pad: 7,871 m/s vacuum, firing from stage 7 down, at its thrust and mass in stock's figures.</summary>
        private static GameSnapshot Pad()
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "PRELAUNCH", DeltaVSimulator = true, Bodies = DeltaVMapTests.StockSystem() };
            snapshot.Craft.Name = "Mun Shot";
            snapshot.Stages.Add(new SnapshotStage { Number = 7, DeltaVVac = 780, ThrustVacKn = 1175, StartMassT = 71, EndMassT = 55 });
            snapshot.Stages.Add(new SnapshotStage { Number = 6, DeltaVVac = 1180, ThrustVacKn = 695, StartMassT = 50, EndMassT = 34 });
            snapshot.Stages.Add(new SnapshotStage { Number = 5, DeltaVVac = 1315, ThrustVacKn = 215, StartMassT = 29, EndMassT = 19 });
            snapshot.Stages.Add(new SnapshotStage { Number = 4, DeltaVVac = 975, ThrustVacKn = 60, StartMassT = 16, EndMassT = 12 });
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 1495, ThrustVacKn = 60, StartMassT = 11, EndMassT = 7 });
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = 2126, ThrustVacKn = 60, StartMassT = 6.4, EndMassT = 3 });
            return snapshot;
        }

        private static PlannedSequence Leg(string name, double deltaV, string intent = "", bool manual = false, string vessel = "")
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV, Manual = manual, Vessel = vessel };
        }

        private static List<PlannedSequence> IkePlan()
        {
            return new List<PlannedSequence>
            {
                Leg("Launch to 80 km orbit", 3400, "Launch from the pad to a circular 80 km orbit, then circularize."),
                Leg("Transfer to Duna", 1100),
                Leg("Capture at Duna", 620),
                Leg("Transfer to Ike", 300),
                Leg("Capture at Ike and land", 370),
                Leg("Crew report on Ike", 0),
                Leg("Ascend from Ike", 400),
                Leg("Transfer to Kerbin", 650),
            };
        }

        private static JudgedLeg Named(IList<JudgedLeg> legs, string name)
        {
            return legs.Single(l => l.Sequence.Name == name);
        }

        [Fact]
        public void TheIkeMissionsPlanRaisesAPerLegFindingAgainstTheMap()
        {
            var finding = PlanLegs.Findings(PlanLegs.Judge(Pad(), IkePlan(), PlannedAscent)).Single();

            Assert.Equal(CraftCheckRules.LegUnderMap, finding.Code);
            Assert.Equal(CraftFindingSeverity.Warning, finding.Severity);
            Assert.Contains("\"Capture at Ike and land\"", finding.Explanation);
            Assert.Contains("370 m/s", finding.Explanation);
            Assert.Contains("capture:Ike + land:Ike", finding.Explanation);
        }

        [Fact]
        public void TheFindingsFigureIsCaptureAndLandingWithTheStagesGravityLoss()
        {
            var ike = Named(PlanLegs.Judge(Pad(), IkePlan(), PlannedAscent), "Capture at Ike and land");
            // Capture about 178 and landing about 390 with v/TWR on top: the last stage's TWR at Ike is about 8.5, so 436. 771 was flown.
            Assert.InRange(ike.Figure, 600, 630);
            Assert.True(ike.Under);
        }

        [Fact]
        public void EveryOtherLegOfThePlanIsMatchedAndWithinTheTolerance()
        {
            var legs = PlanLegs.Judge(Pad(), IkePlan(), PlannedAscent);

            Assert.Equal("launch:Kerbin", Named(legs, "Launch to 80 km orbit").Legs);
            Assert.Equal("transfer:Duna", Named(legs, "Transfer to Duna").Legs);
            Assert.Equal("capture:Duna", Named(legs, "Capture at Duna").Legs);
            Assert.Equal("transfer:Duna>Ike", Named(legs, "Transfer to Ike").Legs);
            Assert.Equal("ascend:Ike", Named(legs, "Ascend from Ike").Legs);
            Assert.Equal("return:Ike", Named(legs, "Transfer to Kerbin").Legs);
            Assert.False(Named(legs, "Crew report on Ike").Matched);
            Assert.Single(legs, l => l.Under);
        }

        [Fact]
        public void TheLaunchIsPlanAscentsFigureWithItsAllowance()
        {
            var legs = PlanLegs.Judge(Pad(), IkePlan(), PlannedAscent);
            var launch = Named(legs, "Launch to 80 km orbit");
            // 3,395 and 6%: within 1% of the 3,570 flown.
            Assert.Equal(3395 * 1.06, launch.Figure, 3);
            Assert.False(launch.Under);
            Assert.Equal(launch.Figure, launch.Need, 3);
        }

        [Fact]
        public void WithNoPlanAscentTheLaunchIsTheMapsFigure()
        {
            var legs = PlanLegs.Judge(Pad(), IkePlan(), "");
            Assert.InRange(Named(legs, "Launch to 80 km orbit").Figure, 3300, 3500);
        }

        [Fact]
        public void TheNeedIsTheLargerOfTheBudgetAndTheFigureForEachLeg()
        {
            var plan = new List<PlannedSequence> { Leg("Launch to orbit", 4000), Leg("Transfer to Duna", 900) };
            var legs = PlanLegs.Judge(Pad(), plan, "");
            Assert.Equal(4000, legs[0].Need);
            Assert.Equal(legs[1].Figure, legs[1].Need);
            // An unmatched leg counts at its budget.
            var phasing = PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Phase 120 degrees from Relay Sat 1", 400) }, "");
            Assert.False(phasing[0].Matched);
            Assert.Equal(400, phasing[0].Need);
        }

        [Fact]
        public void WithinTheTenPercentToleranceRaisesNothing()
        {
            var at = PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Transfer to Duna", 0) }, "")[0].Figure;
            Assert.False(PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Transfer to Duna", at * 0.91) }, "")[0].Under);
            Assert.True(PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Transfer to Duna", at * 0.89) }, "")[0].Under);
        }

        [Fact]
        public void ASmallLegUnderByLessThanTheFloorRaisesNothing()
        {
            // Gilly's landing is about 30 m/s: half of it is under by far more than 10% but by a few m/s.
            var plan = new List<PlannedSequence> { Leg("Capture at Gilly", 10000), Leg("Land on Gilly", 15) };
            Assert.False(PlanLegs.Judge(Pad(), plan, "")[1].Under);
        }

        [Fact]
        public void AReturnHomeHasNoCaptureAndItsReentryIsFree()
        {
            var inOrbit = new List<PlannedSequence> { Leg("Return to Kerbin", 620, "Eject from Duna orbit with a 35 km periapsis"), Leg("Kerbin reentry", 0) };
            var snapshot = Pad();
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Duna";
            var legs = PlanLegs.Judge(snapshot, inOrbit, "");
            Assert.Equal("return:Duna", legs[0].Legs);
            Assert.InRange(legs[0].Figure, 590, 660);
            Assert.False(legs[1].Matched);
        }

        [Fact]
        public void AMoonsWayBackToItsPlanetIsATripThereNotAReturnHome()
        {
            var snapshot = Pad();
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Ike";
            var legs = PlanLegs.Judge(snapshot, new List<PlannedSequence>
            {
                Leg("Return to Duna", 170, "Leave Ike for a Duna periapsis, for the Kerbin transfer"),
                Leg("Capture at Duna", 280),
            }, "");
            Assert.Equal("transfer:Ike>Duna", legs[0].Legs);
            Assert.InRange(legs[0].Figure, 150, 210);
            Assert.Equal("capture:Duna", legs[1].Legs);
        }

        [Fact]
        public void AMissionThatEndsInReentryAtEvePaysNoCapture()
        {
            var plan = new List<PlannedSequence> { Leg("Launch to orbit", 3400), Leg("Transfer to Eve", 1350), Leg("Reenter at Eve and land", 0) };
            var legs = PlanLegs.Judge(Pad(), plan, "");
            // Departure about 1,037 and the plane change about 312; the 1,402 capture stays off.
            Assert.InRange(legs[1].Figure, 1250, 1400);
            Assert.False(legs[1].Under);
            Assert.False(legs[2].Under);
        }

        [Fact]
        public void ATripToAPlanetWhoseNextLegOrbitsItPaysTheCapture()
        {
            var plan = new List<PlannedSequence> { Leg("Launch to orbit", 3400), Leg("Transfer to Eve", 1100), Leg("Crew report in orbit of Eve", 0) };
            var legs = PlanLegs.Judge(Pad(), plan, "");
            Assert.InRange(legs[1].Figure, 2300, 2900);
            Assert.True(legs[1].Under);
        }

        [Fact]
        public void TheArrivalRuleReadsTheMissionPlanForAnEntryAtAnotherBody()
        {
            var snapshot = Pad();
            Assert.False(ArrivalRule.EndsInReentry(snapshot, "Eve"));
            Assert.True(ArrivalRule.EndsInReentry(snapshot, "Eve", new[] { Leg("Land on Eve", 0) }));
            Assert.True(ArrivalRule.EndsInReentry(snapshot, "Laythe", new[] { Leg("Crew report", 0), Leg("Reentry at Laythe", 0) }));
            Assert.False(ArrivalRule.EndsInReentry(snapshot, "Eve", new[] { Leg("Capture at Eve", 1400), Leg("Land on Eve", 0) }));
            Assert.False(ArrivalRule.EndsInReentry(snapshot, "Eve", new[] { Leg("Aerobrake at Eve", 0) }));
            // Airless: there is nothing to enter.
            Assert.False(ArrivalRule.EndsInReentry(snapshot, "Dres", new[] { Leg("Land on Dres", 0) }));
            Assert.True(ArrivalRule.EndsInReentry(snapshot, "Kerbin", new PlannedSequence[0]));
        }

        [Fact]
        public void AnAerocaptureAtACaptureLegCostsNothing()
        {
            var plan = new List<PlannedSequence> { Leg("Launch to orbit", 3400), Leg("Transfer to Duna", 1100), Leg("Aerocapture at Duna", 0) };
            var legs = PlanLegs.Judge(Pad(), plan, "");
            Assert.False(legs[1].Under);
            Assert.False(legs[2].Under);
        }

        [Fact]
        public void AFlybyPaysNoCapture()
        {
            var legs = PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Launch to orbit", 3400), Leg("Transfer to Mun for a flyby", 880) }, "");
            Assert.False(legs[1].Under);
        }

        [Fact]
        public void ALandingWithNoCaptureNamedCarriesTheCapture()
        {
            var legs = PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Launch to orbit", 3400), Leg("Transfer to Mun", 880), Leg("Land at Twin Craters", 700), Leg("Ascend from the Mun", 650) }, "");
            Assert.Equal("transfer:Mun", legs[1].Legs);
            Assert.False(legs[1].Under);
            Assert.Equal("capture:Mun + land:Mun", legs[2].Legs);
            Assert.True(legs[2].Under);
        }

        [Fact]
        public void ALegThatDoesItAllAddsItsPieces()
        {
            var snapshot = Pad();
            snapshot.Situation = "LANDED";
            snapshot.Body = "Mun";
            var legs = PlanLegs.Judge(snapshot, new List<PlannedSequence> { Leg("Ascend, return and reentry", 1000) }, "");
            Assert.Equal("ascend:Mun + return:Mun", legs[0].Legs);
        }

        [Fact]
        public void ManualLegsAndChildVesselsLegsAreNotJudged()
        {
            var legs = PlanLegs.Judge(Pad(), new List<PlannedSequence>
            {
                Leg("Land on the Mun", 5, "", manual: true),
                Leg("Land on Minmus", 5, "", vessel: "Lander"),
            }, "");
            Assert.All(legs, l => Assert.False(l.Matched));
            Assert.Equal(10, legs.Sum(l => l.Need));
        }

        [Fact]
        public void TheIntentDecidesOnlyWhenTheNameSaysNothing()
        {
            var legs = PlanLegs.Judge(Pad(), new List<PlannedSequence>
            {
                Leg("Launch to orbit", 3400, "Launch, then land on the Mun later in the plan"),
                Leg("Leg two", 900, "Transfer to Duna at the window"),
            }, "");
            Assert.Equal("launch:Kerbin", legs[0].Legs);
            Assert.Equal("transfer:Duna", legs[1].Legs);
        }

        [Fact]
        public void WithNoBodyTableNothingIsJudged()
        {
            var snapshot = Pad();
            snapshot.Bodies = new List<SnapshotBody>();
            var legs = PlanLegs.Judge(snapshot, IkePlan(), PlannedAscent);
            Assert.All(legs, l => Assert.False(l.Matched));
            Assert.Equal(IkePlan().Sum(s => s.DeltaV), legs.Sum(l => l.Need));
        }

        [Fact]
        public void ALegARevisedPlanListsAsFlownIsNotJudged()
        {
            var legs = PlanLegs.Judge(Pad(), new List<PlannedSequence>
            {
                Leg("Launch to orbit (flown)", 10),
                Leg("Transfer to Mun", 10, "Done: ejected onto a Mun encounter."),
            }, "");
            Assert.All(legs, l => Assert.False(l.Matched));
        }

        [Fact]
        public void TheNoteIsThePlansUnderLegsAndEmptyWhenThereAreNone()
        {
            var note = PlanLegs.Note(PlanLegs.Judge(Pad(), IkePlan(), PlannedAscent));
            Assert.Contains("Capture at Ike and land", note);
            Assert.Equal("", PlanLegs.Note(PlanLegs.Judge(Pad(), new List<PlannedSequence> { Leg("Launch to orbit", 3600) }, PlannedAscent)));
        }

        private static ToolUseBlock PlanCall()
        {
            const string input = "{\"why\":\"w\",\"sequences\":[{\"name\":\"Land on the Mun\",\"intent\":\"i\",\"delta_v\":100,\"timing\":\"t\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}],"
                + "\"total_delta_v\":100,\"expected_duration\":\"d\",\"mission_time_s\":-1,\"new_goal\":\"\",\"child_vessels\":[],\"goal_steps\":[\"a\"]}";
            return new ToolUseBlock { Id = "p", Name = ProposedPlans.ToolName, InputJson = input };
        }

        [Fact]
        public void APlanUnderTheMapIsStillAcceptedWithTheNoteAndIsTheOneTheReplyTakes()
        {
            var use = PlanCall();
            var outcome = ProposedPlans.Check(use, new List<string>(), false, true, false, plan => "\"Land on the Mun\" is under");
            Assert.StartsWith("{\"ok\":true,\"note\":", outcome.Json);
            Assert.Contains("under", outcome.Json);

            var messages = new List<Message>
            {
                new Message { Role = Role.Assistant, Content = { use } },
                new Message { Role = Role.User, Content = { new ToolResultBlock { ToolUseId = "p", Content = outcome.Json } } },
            };
            Assert.Equal("Land on the Mun", ProposedPlans.LastAccepted(messages).Sequences.Single().Name);
        }

        [Fact]
        public void APlanWithNoLegsUnderTheMapGetsThePlainAnswer()
        {
            Assert.Equal(ProposedPlans.AcceptedJson, ProposedPlans.Check(PlanCall(), new List<string>(), false, true, false, plan => "").Json);
        }
    }
}
