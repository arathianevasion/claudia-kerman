// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1035, seam 1: Contracts that complete after the Mission. Sentinel, comet-detection and deployed-science Contracts are planned up
    /// to placement (a Sentinel telescope in solar orbit, a deployed science station landed and set up on EVA); the time-based Requirement
    /// stays open, shown "completes after the Mission", and the Contract counts only when Core's wait estimate fits its deadline with the
    /// spare rule.
    /// </summary>
    public sealed class AfterMissionContractTests
    {
        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            for (var i = 0; i < contract.Requirements.Count; i++) contract.Requirements[i].Key = ContractRequirement.KeyOf(id, new[] { i });
            return contract;
        }

        /// <summary>The Sentinel Contract's focus set to the body at <paramref name="index"/> in the game's body list.</summary>
        private static void FocusOn(SnapshotContract contract, string index)
        {
            var settings = contract.Requirements[0].Settings;
            var at = settings.FindIndex(s => s.Name == "FocusBody");
            settings[at] = new ContractSetting("FocusBody", index);
        }

        /// <summary>The pad craft carrying a Sentinel telescope, an antenna and a power source.</summary>
        private static GameSnapshot WithTelescope(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Parts[0].Tags.Add("antenna");
            snapshot.Craft.Parts[0].Tags.Add("electric_generator");
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990001, Name = PlacementKinds.TelescopePart, Title = "SENTINEL Infrared Telescope", ParentId = snapshot.Craft.Parts[0].Id });
            return snapshot;
        }

        /// <summary>#1245: the pad craft carrying a Central Station and a deployable experiment, with the kerbal the test snapshot has aboard.</summary>
        private static GameSnapshot WithStation(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            var root = snapshot.Craft.Parts[0].Id;
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990002, Name = "DeployedCentralStation", Tags = { PlacementKinds.DeployableTag, PlacementKinds.StationTag }, ParentId = root });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990003, Name = "DeployedSeismicSensor", Tags = { PlacementKinds.DeployableTag, PlacementKinds.ExperimentTag }, ParentId = root });
            return snapshot;
        }

        /// <summary>Remote: the craft itself may stay in solar orbit as the telescope.</summary>
        private static ContractCheck Remote(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", true, ContractCheckTests.Margin);
        }

        [Fact]
        public void ASentinelContractIsPlannedUpToPlacingTheTelescopeInSolarOrbitAndShownCompletesAfterTheMission()
        {
            var check = Remote(WithTelescope(Fixture("SentinelContract.json", "c-sentinel")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.True(row.AfterMission);
            Assert.Equal("Solar orbit near Kerbin", row.Phase);
            Assert.Contains("completes after the Mission", row.Detail(0));
        }

        [Fact]
        public void TheTelescopesPlacementIsPricedAsTheEscapeFromLowKerbinOrbitOnTopOfTheLaunch()
        {
            var orbit = ContractCheck.Work(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Kerbin:ORBIT")), "", true, ContractCheckTests.Margin).RouteDeltaV;

            var check = Remote(WithTelescope(Fixture("SentinelContract.json", "c-sentinel")));

            // Low Kerbin orbit at 80 km is 2,279 m/s; escaping from it is (√2 − 1) of that, 944 m/s.
            Assert.InRange(check.RouteDeltaV - orbit, 935, 955);
        }

        private static SnapshotContract Sentinel(string id, GameSnapshot pad, double deadlineDays)
        {
            var contract = Fixture("SentinelContract.json", id);
            contract.DeadlineUt = pad.Ut + deadlineDays * 21600;
            return contract;
        }

        [Fact]
        public void ASentinelContractCountsWhenTheEstimatedWaitFitsItsDeadlineWithTheSpare()
        {
            // 8 asteroids at 10 days each is an 80-day wait after a placement about 21 days out: done by day 102, inside 200 days less 20.
            var snapshot = WithTelescope();
            snapshot.Contracts.Accepted.Add(Sentinel("c-sentinel", snapshot, 200));

            var check = Remote(snapshot);

            Assert.True(Assert.Single(check.Fitting).Ticked);
        }

        [Fact]
        public void ASentinelContractWhoseWaitRunsPastItsDeadlineLessTheSpareCompletesLater()
        {
            // Done about day 102, past 110 days less the spare of 11.
            var snapshot = WithTelescope();
            snapshot.Contracts.Accepted.Add(Sentinel("c-sentinel", snapshot, 110));

            var check = Remote(snapshot);

            Assert.Empty(check.Fitting);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CompletesLater, left.Kind);
            Assert.StartsWith("completes later", left.Reason);
        }

        [Fact]
        public void ASentinelContractNamingNoCountOfAsteroidsIsNotYetForWantOfAWaitEstimate()
        {
            var contract = Fixture("SentinelContract.json", "c-sentinel");
            contract.Requirements[0].Settings.RemoveAll(s => s.Name.EndsWith("Discoveries", System.StringComparison.Ordinal));

            var check = Remote(WithTelescope(contract));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: the Sentinel Contract names no count of asteroids, so the wait after the Mission can't be estimated", left.Reason);
        }

        [Fact]
        public void ACraftWithoutTheTelescopeCantServeASentinelContract()
        {
            var snapshot = ContractCheckTests.With(Fixture("SentinelContract.json", "c-sentinel"));
            snapshot.Craft.Parts[0].Tags.Add("antenna");
            snapshot.Craft.Parts[0].Tags.Add("electric_generator");

            var check = Remote(snapshot);

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no SENTINEL Infrared Telescope on the Sentinel probe", left.Reason);
        }

        [Fact]
        public void ASentinelFocusedOnAPlanetTheBodyTableLacksIsNotYetRatherThanPricedFree()
        {
            var contract = Fixture("SentinelContract.json", "c-sentinel");
            FocusOn(contract, "6");
            var snapshot = WithTelescope(contract);
            snapshot.Bodies.RemoveAll(b => b.Name == "Duna" || b.Parent == "Duna");

            var left = Assert.Single(Remote(snapshot).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: the Contract's body isn't one Claudia knows", left.Reason);
        }

        [Fact]
        public void ASentinelNearDunasOrbitIsPricedAsTheTransferThereWithNoCapture()
        {
            var contract = Fixture("SentinelContract.json", "c-sentinel");
            FocusOn(contract, "6");
            var kerbin = Remote(WithTelescope(Fixture("SentinelContract.json", "c-near"))).RouteDeltaV;

            var check = Remote(WithTelescope(contract));

            Assert.Equal("Solar orbit near Duna", Assert.Single(check.Fitting).Phase);
            // Hohmann round the Sun: leaving low Kerbin orbit for Duna's orbit is 1,072 m/s, matching Duna's orbital speed there 827, the
            // plane about 7: 1,906 m/s, 962 more than the bare escape's 944.
            Assert.InRange(check.RouteDeltaV - kerbin, 950, 975);
        }

        [Fact]
        public void APlacementFromACraftAwayFromKerbinIsNotYet()
        {
            var snapshot = WithTelescope(Fixture("SentinelContract.json", "c-sentinel"));
            snapshot.Body = "Mun";
            snapshot.Orbit = null;

            var left = Assert.Single(Remote(snapshot).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal(PlacementKinds.AwayNotYet, left.Reason);
        }

        [Fact]
        public void ADeployedScienceContractWhoseWaitRunsPastItsDeadlineLessTheSpareCompletesLater()
        {
            var snapshot = WithStation();
            var contract = Fixture("DeployedScienceContract.json", "c-station");
            contract.DeadlineUt = snapshot.Ut + 40 * 21600;
            snapshot.Contracts.Accepted.Add(contract);

            var left = Assert.Single(ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin).NotThisFlight);

            Assert.Equal(LeftOutKind.CompletesLater, left.Kind);
        }

        [Fact]
        public void ACometDetectionContractIsPlannedUpToPlacingTheTelescopeAndShownCompletesAfterTheMission()
        {
            var check = Remote(WithTelescope(Fixture("CometDetectionContract.json", "c-comets")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.AfterMission);
            Assert.Equal("Solar orbit near Kerbin", row.Phase);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);
            Assert.Equal("the telescope detects 2 comets, about 60 days", chosen.AfterMission.Words);
        }

        [Fact]
        public void ACometDetectionContractNamingNoCountIsNotYet()
        {
            var contract = Fixture("CometDetectionContract.json", "c-comets");
            contract.Requirements[0].Settings.RemoveAll(s => s.Name == "count");

            var left = Assert.Single(Remote(WithTelescope(contract)).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Contains("names no count of comets", left.Reason);
        }

        [Fact]
        public void ADeployedScienceContractIsPlannedAsALandingAndAManualStretchToSetUpTheStationThatCompletesAfterTheMission()
        {
            var check = ContractCheck.Work(WithStation(Fixture("DeployedScienceContract.json", "c-station")), "", false, ContractCheckTests.Margin);

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            Assert.True(row.YouFly);
            Assert.True(row.AfterMission);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);
            Assert.Equal("set up the deployed science station on EVA", chosen.Manual);
            Assert.Equal("the station collects 50% of its subject's science, about 50 days", chosen.AfterMission.Words);
        }

        [Fact]
        public void ADeployedScienceContractOnACraftWithNoDeployableExperimentReadsThisCraftCant()
        {
            // #1245: a kerbal aboard isn't enough; the station has to be carried there. A Central Station alone sets up no experiment.
            var snapshot = ContractCheckTests.With(Fixture("DeployedScienceContract.json", "c-station"));
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990002, Name = "DeployedCentralStation", Tags = { PlacementKinds.DeployableTag, PlacementKinds.StationTag }, ParentId = snapshot.Craft.Parts[0].Id });

            var left = Assert.Single(ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no deployable science aboard", left.Reason);
        }

        [Fact]
        public void ADeployedScienceContractOnACraftWithExperimentsButNoCentralStationReadsThisCraftCant()
        {
            // #1245: the experiments report to a Central Station; without one aboard no station is set up for the game to confirm.
            var snapshot = ContractCheckTests.With(Fixture("DeployedScienceContract.json", "c-station"));
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 990003, Name = "DeployedSeismicSensor", Tags = { PlacementKinds.DeployableTag, PlacementKinds.ExperimentTag }, ParentId = snapshot.Craft.Parts[0].Id });

            var left = Assert.Single(ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin).NotThisFlight);

            Assert.Equal(PlacementKinds.NoDeployableScience, left.Reason);
        }

        [Fact]
        public void ADeployedScienceContractNamingNoShareOfScienceIsNotYet()
        {
            var contract = Fixture("DeployedScienceContract.json", "c-station");
            contract.Requirements[0].Settings.RemoveAll(s => s.Name == "percentage");

            var left = Assert.Single(ContractCheck.Work(WithStation(contract), "", false, ContractCheckTests.Margin).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Contains("names no share of science to collect", left.Reason);
        }

        [Fact]
        public void TheRequestPlansThePlacementOnlyAndSaysWhatCompletesAfterTheMission()
        {
            var check = Remote(WithTelescope(Fixture("SentinelContract.json", "c-sentinel")));

            var section = check.Choose(check.TickedGuids).Section;

            Assert.Contains("1. Kerbin, the launch: place a Sentinel telescope in solar orbit near Kerbin's orbit (", section);
            Assert.Contains(", open; completes after the Mission: the telescope maps 8 asteroids near Kerbin, about 80 days)", section);
            Assert.Contains("Plan the placement only; no leg waits for them.", section);
        }

        // ---- In flight ----

        private static ChosenRequirement StationChosen(out GameSnapshot snapshot)
        {
            snapshot = WithStation(Fixture("DeployedScienceContract.json", "c-station"));
            var check = ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
            return Assert.Single(check.Choose(check.TickedGuids).Requirements);
        }

        /// <summary>#1244: a Central Station and an experiment set up on <paramref name="body"/>.</summary>
        private static void StationSetUp(GameSnapshot snapshot, string body)
        {
            snapshot.DeployedScience.Add(new SnapshotDeployedPart { Name = "Central Station", Body = body, Situation = "LANDED", Station = true });
            snapshot.DeployedScience.Add(new SnapshotDeployedPart { Name = "Seismic Sensor", Body = body, Situation = "LANDED" });
        }

        [Fact]
        public void APlacedRequirementTheGameHasntMarkedMetReadsCompletesAfterTheMissionNotUnmet()
        {
            // "not met" is what holds a leg, or counts a miss at Hand back; this one is placed and left open for the game.
            var chosen = StationChosen(out var snapshot);
            StationSetUp(snapshot, "Mun");

            var reading = Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null));

            Assert.Equal("completes after the Mission", reading.State);
        }

        [Fact]
        public void ADeployedScienceRequirementWithNoStationSetUpIsNotMetWithTheGap()
        {
            var chosen = StationChosen(out var snapshot);

            var reading = Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null));

            Assert.Equal("not met", reading.State);
            Assert.Equal("no deployed science station is set up on the Mun", reading.Gap);
        }

        [Fact]
        public void AStationSetUpOnAnotherBodyOrWithNoExperimentIsNoPlacement()
        {
            var chosen = StationChosen(out var snapshot);
            StationSetUp(snapshot, "Minmus");
            snapshot.DeployedScience.Add(new SnapshotDeployedPart { Name = "Central Station", Body = "Mun", Situation = "LANDED", Station = true });

            Assert.Equal("not met", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void APlacedRequirementTheGameHasMarkedMetReadsMet()
        {
            var chosen = StationChosen(out var snapshot);
            snapshot.Contracts.Accepted[0].Requirements[0].State = "Complete";

            Assert.Equal("met", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void AtHandBackAPlacedStationIsSaidToBeSetUpAndToCompleteAfterTheMissionWithNoRetryOffered()
        {
            var chosen = StationChosen(out var snapshot);
            StationSetUp(snapshot, "Mun");

            var line = ContractWatch.HandBackLine(new ContractWatch().Read(snapshot, new[] { chosen }, null));

            Assert.Contains("\"state\":\"completes after the Mission\"", line);
            Assert.Contains("the game completes it after the Mission once its placement is made", line);
            Assert.DoesNotContain("retry", line);
        }

        [Fact]
        public void AtHandBackAStationNeverSetUpIsAMissWithItsGapAndARetryOffered()
        {
            var chosen = StationChosen(out var snapshot);

            var line = ContractWatch.HandBackLine(new ContractWatch().Read(snapshot, new[] { chosen }, null));

            Assert.Contains("\"state\":\"not met\",\"gap\":\"no deployed science station is set up on the Mun\"", line);
            Assert.Contains("retry", line);
        }

        // ---- A Sentinel telescope's placement ----

        private const double SunRadius = 261600000;
        private const double KerbinOrbit = 13599840256;

        private static ChosenRequirement SentinelChosen(out GameSnapshot snapshot)
        {
            snapshot = WithTelescope(Fixture("SentinelContract.json", "c-sentinel"));
            var check = Remote(snapshot);
            return Assert.Single(check.Choose(check.TickedGuids).Requirements);
        }

        /// <summary>A released telescope (a Child Vessel) at <paramref name="body"/>, its periapsis and apoapsis radii from the body's centre given.</summary>
        private static void Released(GameSnapshot snapshot, string body, double periapsisRadius, double apoapsisRadius, bool telescope = true)
        {
            var radius = body == "Sun" ? SunRadius : 600000;
            snapshot.Vessels.Add(new SnapshotVessel
            {
                VesselId = "telescope", Name = "Sentinel probe", Body = body, Situation = "ORBITING", CarriesTelescope = telescope,
                PeriapsisM = periapsisRadius - radius, ApoapsisM = apoapsisRadius - radius, Inclination = 0.1,
            });
        }

        [Fact]
        public void ASentinelRequirementWhoseTelescopeIsInSolarOrbitNearKerbinsReadsCompletesAfterTheMission()
        {
            var chosen = SentinelChosen(out var snapshot);
            Released(snapshot, "Sun", KerbinOrbit * 0.97, KerbinOrbit * 1.05);

            Assert.Equal("completes after the Mission", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void ASentinelRequirementWhoseTelescopeIsStillRoundKerbinIsNotMetWithTheGap()
        {
            var chosen = SentinelChosen(out var snapshot);
            Released(snapshot, "Kerbin", 680000, 700000);

            var reading = Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null));

            Assert.Equal("not met", reading.State);
            Assert.Equal("no SENTINEL Infrared Telescope is in solar orbit near Kerbin's orbit", reading.Gap);
        }

        [Fact]
        public void ATelescopeInSolarOrbitFarFromThePlanetsOrbitIsNoPlacement()
        {
            // Out past Duna's orbit (20.7 Gm): nowhere near Kerbin's 13.6 Gm.
            var chosen = SentinelChosen(out var snapshot);
            Released(snapshot, "Sun", 19e9, 21e9);

            Assert.Equal("not met", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void AVesselInTheRightOrbitWithNoTelescopeIsNoPlacement()
        {
            var chosen = SentinelChosen(out var snapshot);
            Released(snapshot, "Sun", KerbinOrbit * 0.97, KerbinOrbit * 1.05, telescope: false);

            Assert.Equal("not met", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void TheCraftItselfCarryingTheTelescopeInSolarOrbitIsThePlacement()
        {
            var chosen = SentinelChosen(out var snapshot);
            snapshot.Body = "Sun";
            snapshot.Situation = "ORBITING";
            snapshot.Orbit = new SnapshotOrbit { Body = "Sun", Periapsis = KerbinOrbit * 0.98 - SunRadius, Apoapsis = KerbinOrbit * 1.02 - SunRadius };

            Assert.Equal("completes after the Mission", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void ACometDetectionRequirementReadsCompletesAfterTheMissionOnlyWithItsTelescopeNearKerbinsOrbit()
        {
            var snapshot = WithTelescope(Fixture("CometDetectionContract.json", "c-comets"));
            var check = Remote(snapshot);
            var chosen = Assert.Single(check.Choose(check.TickedGuids).Requirements);

            Assert.Equal("not met", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
            Released(snapshot, "Sun", KerbinOrbit * 0.97, KerbinOrbit * 1.05);
            Assert.Equal("completes after the Mission", Assert.Single(new ContractWatch().Read(snapshot, new[] { chosen }, null)).State);
        }

        [Fact]
        public void AnUnplacedTelescopeHoldsNoLegAndGoesToTheDebriefAsAMissWithItsGap()
        {
            // The hold path: the conditions don't hold, so no wait for the game; the gap is the placement's.
            var chosen = SentinelChosen(out var snapshot);
            Released(snapshot, "Kerbin", 680000, 700000);

            Assert.False(ContractWatch.ConditionsHold(snapshot, chosen, "Kerbin"));
            Assert.Equal("no SENTINEL Infrared Telescope is in solar orbit near Kerbin's orbit", ContractWatch.Gap(snapshot, chosen));
            var line = ContractWatch.Line(new ContractWatch().Read(snapshot, new[] { chosen }, null));
            Assert.Contains("\"state\":\"not met\"", line);
            Assert.Contains("propose a trim burn or a retry leg", line);
        }

        [Fact]
        public void TheDeadlineRecheckTimesAPlacementWithItsWaitAfterTheMission()
        {
            // From the pad: the Mun landing is a few days out; with the 50-day wait it can't finish inside 40 days less the spare.
            var chosen = StationChosen(out var snapshot);
            snapshot.Contracts.Accepted[0].DeadlineUt = snapshot.Ut + 40 * 21600;

            var late = ContractRecheck.Check(snapshot, new[] { chosen }, "", "Kerbin", new System.Collections.Generic.HashSet<string>(), out _);

            Assert.Equal("c-station", Assert.Single(late).Guid);
        }
    }
}
