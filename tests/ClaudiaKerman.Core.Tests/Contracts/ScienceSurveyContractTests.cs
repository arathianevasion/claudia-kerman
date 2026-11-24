// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1030, seam 1: collect-science and survey Contracts ride along. Collect science is the science block run at the body; a survey's
    /// waypoints in a space band are passes over them from orbit (the plane change that puts the ground track over them and the wait for
    /// the pass), on the ground landings at them; low-flying surveys are "not yet"; the experiment must be on the craft.
    /// </summary>
    public sealed class ScienceSurveyContractTests
    {
        /// <summary>The Mun's GM and radius in the pad fixture's body table, for figures worked by hand.</summary>
        private const double MunGm = 6.5138398e10;
        private const double MunRadius = 200000;
        private const double MunRotation = 138984.37657447575;

        private static ContractCheck Check(GameSnapshot snapshot, string words = "")
        {
            return ContractCheck.Work(snapshot, words, false, ContractCheckTests.Margin);
        }

        private static SnapshotContract Science(string id, string body, string location)
        {
            var contract = ContractCheckTests.Fixture("CollectScience.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            var requirement = contract.Requirements[0];
            requirement.Key = ContractRequirement.KeyOf(id, new[] { 0 });
            requirement.Settings = requirement.Settings.Select(s => s.Name == "body" ? new ContractSetting("body", body) : s.Name == "location" ? new ContractSetting("location", location) : s).ToList();
            return contract;
        }

        /// <summary>
        /// The SurveyContract fixture with its own GUID and one waypoint per <paramref name="waypoints"/> entry, "lat,lon": each a copy of
        /// the fixture's first, on body index <paramref name="body"/> in <paramref name="band"/> with <paramref name="experiment"/>.
        /// </summary>
        internal static SnapshotContract Survey(string id, string body, string band, string experiment, double threshold, params string[] waypoints)
        {
            var contract = ContractCheckTests.Fixture("SurveyContract.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            var template = contract.Requirements[0];
            contract.Requirements = waypoints.Select((w, i) =>
            {
                var at = w.Split(',');
                var values = new Dictionary<string, string>
                {
                    { "targetBody", body }, { "band", band }, { "experiment", experiment }, { "wpLatitude", at[0] }, { "wpLongitude", at[1] },
                    { "flightThreshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                };
                return new ContractRequirement
                {
                    Key = ContractRequirement.KeyOf(id, new[] { i }), Kind = template.Kind, State = "Incomplete",
                    Settings = template.Settings.Select(s => values.ContainsKey(s.Name) ? new ContractSetting(s.Name, values[s.Name]) : s).ToList(),
                };
            }).ToList();
            return contract;
        }

        private static DeltaVMap Map(GameSnapshot snapshot)
        {
            return DeltaVMap.Compute(snapshot.Bodies, "Kerbin");
        }

        [Fact]
        public void ACollectScienceContractInSpaceNearTheMunIsPlannedAsAnOrbitOfTheMun()
        {
            var snapshot = ContractCheckTests.With(ContractCheckTests.Fixture("CollectScience.json"));
            var orbit = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Mun:ORBIT")));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("Mun", row.Phase);
            Assert.Equal(orbit.RouteDeltaV, check.RouteDeltaV, 3);
            Assert.Empty(check.NotThisFlight);
        }
    

        [Fact]
        public void ASurveysExperimentMustBeOnTheCraftOrItIsThisCraftCant()
        {
            // The pad craft carries a crew report (its pod, Claudia aboard) and no thermometer.
            var snapshot = ContractCheckTests.With(Survey("c-temp", "2", "LOW", "temperatureScan", 30000, "3,10"), Survey("c-crew", "2", "LOW", "crewReport", 30000, "3,10"));

            var check = Check(snapshot);

            Assert.Equal("c-crew", Assert.Single(check.Fitting).Guid);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal("c-temp", left.Guid);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no temperatureScan experiment aboard", left.Reason);
        }

        [Fact]
        public void CollectScienceNeedsSomeExperimentOrAKerbalAboard()
        {
            var snapshot = ContractCheckTests.With(Science("c-sci", "2", "Space"));
            snapshot.Experiments.Clear();
            snapshot.Crew.Clear();

            var left = Assert.Single(Check(snapshot).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no science experiment aboard", left.Reason);
        }

        [Fact]
        public void LowFlyingSurveysAreNotYetWithTheReason()
        {
            // The fixture: a LOW band over Kerbin, below 17.5 km, in the air.
            var check = Check(ContractCheckTests.With(ContractCheckTests.Fixture("SurveyContract.json")));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: low-flying surveys need an aircraft over the waypoint", left.Reason);
            Assert.Empty(check.Fitting);
        }
    

        private static double OrbitOfTheMun()
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Mun:ORBIT"))).RouteDeltaV;
        }

        [Fact]
        public void ASpaceBandWaypointNearTheEquatorAddsThePlaneChangeThatPutsTheGroundTrackOverIt()
        {
            var check = Check(ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            // A turn of 3 degrees in low Mun orbit (214 km from the centre, 551.7 m/s): 2 * 551.7 * sin(1.5 deg) = 28.9 m/s.
            Assert.InRange(check.RouteDeltaV - OrbitOfTheMun(), 28.4, 29.4);
        }

        [Fact]
        public void AHighLatitudeSpaceBandWaypointCostsNoMoreThanThePolarRow()
        {
            var snapshot = ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "45,10"));

            var check = Check(snapshot);

            // A 45 degree turn in low orbit is 422 m/s; aiming the plane on the approach is the Map's polar row, cheaper.
            Assert.InRange(check.RouteDeltaV - OrbitOfTheMun(), Map(snapshot).Leg("polar:Mun").DeltaV - 0.5, Map(snapshot).Leg("polar:Mun").DeltaV + 0.5);
        }

        [Fact]
        public void SpaceBandWaypointsAreFlownInTheOrderOfTheirPasses()
        {
            // The body turns east under the orbit, so the track sweeps west: from 170 W (after the widest gap) to 100 E, then 10 E.
            var check = Check(ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "0,10", "0,100", "0,-170")));

            var section = check.Choose(check.TickedGuids).Section;

            var order = section.Substring(section.IndexOf("Survey waypoints", StringComparison.Ordinal));
            Assert.True(order.IndexOf("170.0° W", StringComparison.Ordinal) < order.IndexOf("100.0° E", StringComparison.Ordinal), section);
            Assert.True(order.IndexOf("100.0° E", StringComparison.Ordinal) < order.IndexOf("0.0° N 10.0° E", StringComparison.Ordinal), section);
            Assert.Contains("0.0° N 170.0° W", order);
        }

        [Fact]
        public void AChosenSurveyYieldsOneRequirementPerWaypointAndTheRouteWordsNameEach()
        {
            var check = Check(ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "0,10", "0,100", "0,-170")));

            var choice = check.Choose(check.TickedGuids);

            Assert.Equal(3, choice.Requirements.Count);
            Assert.All(choice.Requirements, r => Assert.NotNull(r.Waypoint));
            Assert.Equal("orbit the Mun; survey 3 waypoints over the Mun in this order: 0.0° N 170.0° W; 0.0° N 100.0° E; 0.0° N 10.0° E; return to Kerbin", choice.RouteWords);
            Assert.Contains("- survey 3 waypoints over the Mun in this order: 0.0° N 170.0° W; 0.0° N 100.0° E; 0.0° N 10.0° E [serves: ", choice.Section);
        }

        [Fact]
        public void AChosenOrbitalSurveyTellsClaudiaToFlyTheSurveyScheduleFromOneAddonCall()
        {
            var check = Check(ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "0,10", "0,100", "0,-170")));

            var section = check.Choose(check.TickedGuids).Section;

            Assert.Contains("ADDONS:CLAUDIA:SURVEYSCHEDULE()", section);
            Assert.Contains("Coast to each pass's ut and run science there", section);
            Assert.Contains("report each waypoint you skip: each \"unreachable\" one with the inclination that would reach it", section);
            Assert.Contains("\"below 10 km\" for a pass higher than that", section);
        }

        [Fact]
        public void AGroundSurveyGetsTheLandingNoteAndNotThePassNote()
        {
            var check = Check(ContractCheckTests.With(Survey("c-g", "2", "GROUND", "crewReport", 0, "0,0", "0,0.5729578")));
            var section = check.Choose(check.TickedGuids).Section;

            Assert.Contains(MissionPrompts.SurveyLandingNote, section);
            Assert.DoesNotContain("Coast to each pass's ut", section);
        }

        [Fact]
        public void TheWaitForThePassCountsAgainstTheDeadline()
        {
            // About 29,000 s to low Mun orbit; the first pass is up to half a turn of the Mun (69,492 s) later. A deadline 60,000 s out
            // keeps the orbit with its day to spare, not the survey.
            var survey = Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10");
            var orbit = ContractCheckTests.Exploration("c-orbit", 10000, "Mun:ORBIT");
            var snapshot = ContractCheckTests.With(survey, orbit);
            survey.DeadlineUt = snapshot.Ut + 60000;
            orbit.DeadlineUt = snapshot.Ut + 60000;

            var check = Check(snapshot);

            Assert.Equal("c-orbit", Assert.Single(check.Fitting).Guid);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal("c-s", left.Guid);
            Assert.Equal(LeftOutKind.CompletesLater, left.Kind);
        }
    

        private static double LandOnTheMun()
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-land", 10000, "Mun:LANDING"))).RouteDeltaV;
        }

        [Fact]
        public void GroundWaypointsAreALandingThenAHopToTheNextSite()
        {
            // 0.5729578 degrees of longitude on the equator is 2,000 m on the Mun (200 km radius). Its surface gravity is 1.628 m/s²,
            // so a hop there and down again is 2 * sqrt(1.628 * 2000) = 114.1 m/s.
            var check = Check(ContractCheckTests.With(Survey("c-g", "2", "GROUND", "crewReport", 0, "0,0", "0,0.5729578")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            Assert.InRange(check.RouteDeltaV - LandOnTheMun(), 113.6, 114.6);
        }

        [Fact]
        public void GroundWaypointsAreOrderedByLeastTravelBetweenLandingSites()
        {
            var check = Check(ContractCheckTests.With(Survey("c-g", "2", "GROUND", "crewReport", 0, "0,0", "0,2", "0,1")));

            var section = check.Choose(check.TickedGuids).Section;

            var order = section.Substring(section.IndexOf("Survey waypoints", StringComparison.Ordinal));
            var middle = order.IndexOf("0.0° N 1.0° E", StringComparison.Ordinal);
            Assert.True(middle > 0, section);
            Assert.True(order.IndexOf("0.0° N 0.0° E", StringComparison.Ordinal) < middle == middle < order.IndexOf("0.0° N 2.0° E", StringComparison.Ordinal), section);
        }

        [Fact]
        public void AGroundWaypointAtAHighLatitudeAddsTheMapsPolarRow()
        {
            var snapshot = ContractCheckTests.With(Survey("c-g", "2", "GROUND", "crewReport", 0, "70,0"));

            var check = Check(snapshot);

            var polar = Map(snapshot).Leg("polar:Mun").DeltaV;
            Assert.InRange(check.RouteDeltaV - LandOnTheMun(), polar - 0.5, polar + 0.5);
        }

        [Fact]
        public void AGroundSurveyOnKerbinIsNotYet()
        {
            var left = Assert.Single(Check(ContractCheckTests.With(Survey("c-g", "1", "GROUND", "crewReport", 0, "0,-74"))).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: ground surveys on Kerbin aren't planned yet", left.Reason);
        }
    

        private static PlanServes PlanCheck(GameSnapshot snapshot, params string[] legs)
        {
            var check = Check(snapshot);
            var choice = check.Choose(check.TickedGuids);
            var plan = new MissionPlan();
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });
            return PlanServes.Check(snapshot, plan, choice.Requirements, "", "Kerbin");
        }

        [Fact]
        public void APlanThatReachesTheSurveysOrbitButRunsNoScienceThereGoesBackAsAFinding()
        {
            var snapshot = ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10"));

            var served = PlanCheck(snapshot, "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("crewReport", problem);
            Assert.Contains("no leg runs the science block", problem);
        }

        [Fact]
        public void APlanThatRunsTheScienceBlockAtTheBodyServesTheSurveyAndCollectScience()
        {
            var snapshot = ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10"), Science("c-sci", "2", "Space"));

            var served = PlanCheck(snapshot, "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit",
                "Run science over the survey waypoint", "Return to Kerbin");

            Assert.Empty(served.Problems);
            // #1304: the waypoint is tied to the leg that flies it, the science run; collect science near the Mun to the capture.
            Assert.Equal(2, served.Record.Single(r => r.Key.StartsWith("c-s/", StringComparison.Ordinal)).Leg);
            Assert.Equal(1, served.Record.Single(r => r.Key.StartsWith("c-sci/", StringComparison.Ordinal)).Leg);
        }

        [Fact]
        public void EachSurveyWaypointIsTiedToTheLegThatFliesIt()
        {
            var snapshot = ContractCheckTests.With(Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10", "-5,60", "8,120"));

            var served = PlanCheck(snapshot, "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit",
                "Survey: take a crewReport over each waypoint", "Return to Kerbin");

            Assert.Empty(served.Problems);
            Assert.Equal(3, served.Record.Count);
            Assert.All(served.Record, r => Assert.Equal(2, r.Leg));
            Assert.NotEmpty(served.Legs[2]);
            Assert.Empty(served.Legs[1]);
        }

        [Fact]
        public void ScienceRunAfterLeavingTheBodyDoesNotServeIt()
        {
            var snapshot = ContractCheckTests.With(Science("c-sci", "2", "Space"));

            var served = PlanCheck(snapshot, "Launch to low Kerbin orbit", "Transfer to the Mun and capture into low orbit", "Return to Kerbin", "Run science on the way home");

            Assert.Contains("no leg runs the science block", Assert.Single(served.Problems));
        }
    

        [Fact]
        public void ASurveyAddedInARevisedPlanStillNeedsTheScienceBlockThere()
        {
            var mun = ContractCheckTests.Exploration("mun", 10000, "Mun:ORBIT");
            var survey = Survey("c-s", "3", "LOW", "crewReport", 30000, "3,10");
            var snapshot = ContractCheckTests.With(mun, survey);
            var chosen = ContractCheck.Work(ContractCheckTests.With(mun), "", false, ContractCheckTests.Margin).Choose(new[] { "mun" }).Requirements;
            var plan = new MissionPlan { Why = "Adds SurveyContract, as the player asked." };
            foreach (var leg in new[] { "Transfer to the Mun and capture into low orbit", "Transfer to Minmus and capture into low orbit" })
                plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });

            var replan = ContractReplan.Check(snapshot, plan, chosen, "", "Kerbin", new Dictionary<string, string>(), new HashSet<string>(), new List<string> { "also do the Minmus survey" });

            Assert.Equal("crewReport", Assert.Single(replan.Added).Experiment);
            Assert.Contains("no leg runs the science block", Assert.Single(replan.Problems));
        }
    

        [Fact]
        public void TheDeadlineRecheckCountsTheWaitForThePass()
        {
            // As at the Contract check: about 29,000 s to low Mun orbit, then up to half a turn of the Mun (69,492 s) for the pass.
            var survey = Survey("c-s", "2", "LOW", "crewReport", 30000, "3,10");
            var snapshot = ContractCheckTests.With(survey);
            var chosen = Check(snapshot).Choose(new[] { "c-s" }).Requirements;
            survey.DeadlineUt = snapshot.Ut + 60000;

            List<string> rechecked;
            var late = ContractRecheck.Check(snapshot, chosen, "", "Kerbin", new HashSet<string>(), out rechecked);

            Assert.Equal("c-s", Assert.Single(late).Guid);
            Assert.True(Assert.Single(late).DoneUt > snapshot.Ut + 29000 + 69000, "done " + (Assert.Single(late).DoneUt - snapshot.Ut));
        }
    

        [Fact]
        public void ARaiseToAHighBandServesALowBandWhoseCeilingIsAboveIt()
        {
            // Low Mun orbit is 14 km up. Above 20 km is a raise to 22 km (two burns, 214 to 222 km from the centre: 10.0 m/s); below
            // 30 km is met there too, so no way back down. The 3 degree turn is 28.9 m/s, as before.
            var snapshot = ContractCheckTests.With(Survey("c-high", "2", "HIGH", "crewReport", 20000, "3,10"), Survey("c-low", "2", "LOW", "crewReport", 30000, "3,20"));

            var check = Check(snapshot);

            Assert.Equal(2, check.Fitting.Count);
            Assert.InRange(check.RouteDeltaV - OrbitOfTheMun(), 28.9 + 10.0 - 0.5, 28.9 + 10.0 + 0.5);
        }
    }
}
