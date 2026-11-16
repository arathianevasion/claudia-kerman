// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1034, seam 1: tourists fly with Claudia, and Grand Tours are possible. A tourism Contract is each tourist's destinations, then the
    /// tourist brought home; the tourist must be aboard, in a seat. The high-gee (blackout) variant is "not yet". A Grand Tour is one
    /// craft visiting every body it lists, priced as one route, so the Contract check judges whether one craft reaches them all.
    /// </summary>
    public sealed class TourContractTests
    {
        private const string Tourist = "Lodfrid Kerman";

        private static ContractCheck Check(GameSnapshot snapshot)
        {
            return ContractCheck.Work(snapshot, "", false, ContractCheckTests.Margin);
        }

        private static SnapshotContract Tourism(string id = "c-tour")
        {
            var contract = ContractCheckTests.Fixture("TourismContract.json");
            contract.Guid = id;
            contract.DeadlineUt = null;
            return contract;
        }

        /// <summary>The pad craft with a seat for the tourist and the tourist in it.</summary>
        private static GameSnapshot Aboard(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Seats = 2;
            snapshot.Crew.Add(new SnapshotCrewMember { Name = Tourist, Trait = "Tourist" });
            return snapshot;
        }

        private static double OrbitOfKerbinAndHome()
        {
            return Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, "Kerbin:ORBIT"))).RouteDeltaV;
        }

        // ---- Tourism ----

        [Fact]
        public void ATourismContractWithItsTouristAboardIsPlannedAsTheirDestinationsAndTheTripHome()
        {
            var check = Check(Aboard(Tourism()));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal(OrbitOfKerbinAndHome(), check.RouteDeltaV, 3);
            var words = check.Choose(check.TickedGuids).Requirements.Select(r => r.Words).ToList();
            Assert.Contains("orbit Kerbin", words);
            Assert.Contains("bring tourist " + Tourist + " home", words);
        }

        private static MissionPlan Plan(params string[] legs)
        {
            var plan = new MissionPlan();
            foreach (var leg in legs) plan.Sequences.Add(new PlannedSequence { Name = leg, Intent = "Fly it.", DeltaV = 100 });
            return plan;
        }

        [Fact]
        public void APlanThatOrbitsAndComesHomeServesTheTourAndTheTripHomeIsServedByTheReturn()
        {
            var snapshot = Aboard(Tourism());
            var check = Check(snapshot);
            var chosen = check.Choose(check.TickedGuids).Requirements;

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit", "Return to Kerbin"), chosen, "", "Kerbin");

            Assert.Empty(served.Problems);
            Assert.Equal(1, served.Record.Single(r => r.Words == "bring tourist " + Tourist + " home").Leg);
            Assert.Equal(0, served.Record.Single(r => r.Words == "orbit Kerbin").Leg);
        }

        [Fact]
        public void APlanThatNeverComesHomeGoesBackToClaudiaAsAFindingForTheTourist()
        {
            var snapshot = Aboard(Tourism());
            var check = Check(snapshot);
            var chosen = check.Choose(check.TickedGuids).Requirements;

            var served = PlanServes.Check(snapshot, Plan("Launch to low Kerbin orbit"), chosen, "", "Kerbin");

            var problem = Assert.Single(served.Problems);
            Assert.Contains("bring tourist " + Tourist + " home", problem);
            Assert.Contains("add the return to Kerbin", problem);
        }

        [Fact]
        public void ATouristNotAboardIsThisCraftCantWithASeatFreeAndNoSeatForThemWithoutOne()
        {
            var seated = ContractCheckTests.With(Tourism());
            seated.Craft.Seats = 2;
            var full = ContractCheckTests.With(Tourism());

            var free = Assert.Single(Check(seated).NotThisFlight);
            var none = Assert.Single(Check(full).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, free.Kind);
            Assert.Equal("this craft can't: tourist " + Tourist + " isn't aboard", free.Reason);
            Assert.Equal("this craft can't: no seat for tourist " + Tourist, none.Reason);
        }

        [Fact]
        public void TheHighGeeVariantIsNotYetOnTheCardAndInMissionControl()
        {
            var gee = Tourism("c-gee");
            gee.Requirements[0].Requirements.Add(new ContractRequirement
            {
                Key = "c-gee/0.2", Kind = "KerbalGeeAdventureParameter", State = "Incomplete",
                Settings = { new ContractSetting("name", "KerbalGeeAdventureParameter") },
            });

            var left = Assert.Single(Check(Aboard(gee)).NotThisFlight);
            var mark = ContractMarks.Of(gee, false);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal(TourKinds.GeeNotYet, left.Reason);
            Assert.False(mark.Plannable);
            Assert.Equal(TourKinds.GeeNotYet, mark.Hover);
        }

        [Fact]
        public void AHighGeeRequirementWithItsOwnConditionsUnderItIsNotYetToo()
        {
            var gee = Tourism("c-gee");
            gee.Requirements.Add(new ContractRequirement
            {
                Key = "c-gee/1", Kind = "KerbalGeeAdventureParameter", State = "Incomplete",
                Requirements = { new ContractRequirement { Key = "c-gee/1.0", Kind = "KerbalDestinationParameter", State = "Incomplete",
                    Settings = { new ContractSetting("targetBody", "1"), new ContractSetting("targetType", "Suborbit") } } },
            });

            var left = Assert.Single(Check(Aboard(gee)).NotThisFlight);

            Assert.Equal(TourKinds.GeeNotYet, left.Reason);
        }

        [Fact]
        public void MissionControlMarksATourismContractPlannable()
        {
            var mark = ContractMarks.Of(Tourism(), false);

            Assert.True(mark.Plannable);
            Assert.Equal("Claudia can plan this: reach space over Kerbin, orbit Kerbin, bring tourist " + Tourist + " home", mark.Hover);
        }

        [Fact]
        public void ATouristsDestinationAwayFromKerbinIsAVisitThereAndTheTripHomeIsFromIt()
        {
            var away = Tourism("c-away");
            away.Requirements[0].Requirements[1].Settings = new[] { new ContractSetting("targetBody", "2"), new ContractSetting("targetType", "Flyby") }.ToList();

            var check = Check(Aboard(away));

            var row = Assert.Single(check.Fitting);
            Assert.Contains("Mun", row.Phase);
            var home = check.Choose(check.TickedGuids).Requirements.Single(r => r.Return);
            Assert.Equal("Mun", home.Body);
            Assert.Equal(ContractReach.Flyby, home.Reach);
        }

        // ---- Grand Tours ----

        private static SnapshotContract GrandTour(params string[] extra)
        {
            var tour = ContractCheckTests.Fixture("GrandTour.json");
            tour.Guid = "c-grand";
            tour.DeadlineUt = null;
            foreach (var destination in extra)
            {
                var parts = destination.Split(':');
                tour.Requirements.Add(new ContractRequirement
                {
                    Key = "c-grand/" + tour.Requirements.Count, Kind = "VesselDestinationParameter", State = "Incomplete",
                    Settings = { new ContractSetting("targetBody", parts[0]), new ContractSetting("targetType", parts[1]) },
                });
            }
            return tour;
        }

        [Fact]
        public void AGrandTourIsPlannedAsOneRouteVisitingEveryBodyItLists()
        {
            var check = Check(ContractCheckTests.With(GrandTour()));
            var both = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-both", 10000, "Mun:ORBIT", "Minmus:ORBIT")));

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal(both.RouteDeltaV, check.RouteDeltaV, 3);
            Assert.Equal(new[] { "orbit Minmus", "orbit the Mun" }, check.Choose(check.TickedGuids).Requirements.Select(r => r.Words).OrderBy(w => w));
            Assert.True(ContractMarks.Of(GrandTour(), false).Plannable);
        }

        [Fact]
        public void AGrandTourThisCraftCantFlyAllTheWayIsThisCraftCantWithTheShortfallNotPartOfIt()
        {
            // Eeloo (index 16) is far past what the Mun Shot carries: the tour is left out whole, never flown to the bodies it can reach.
            var check = Check(ContractCheckTests.With(GrandTour("16:Land")));

            Assert.Empty(check.Fitting);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Matches("^this craft can't: [0-9,]+ m/s short$", left.Reason);
        }

        [Fact]
        public void AGrandTourDestinationClaudiaCantPlanMakesTheWholeTourNotYet()
        {
            var check = Check(ContractCheckTests.With(GrandTour("1:Land")));

            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: land on Kerbin isn't planned yet", left.Reason);
        }
    }
}
