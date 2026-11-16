// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1031, seam 1: satellite, station and base Contracts. A satellite (the craft itself or one Child Vessel) is put into the Contract's
    /// orbit, priced by change_orbit's burn solver from the parking orbit, and the plan check confirms the orbit leg lands inside the
    /// deviation window; a station is the craft in orbit; a base, mobile ones included, the craft landed. What each needs of the craft is
    /// "this craft can't: …"; networks, repositioning and docked-station variants are "not yet".
    /// </summary>
    public sealed class StructureContractTests
    {
        private static ContractCheck Check(GameSnapshot snapshot, bool remote = false)
        {
            return ContractCheck.Work(snapshot, "", remote, ContractCheckTests.Margin);
        }

        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            contract.DeadlineUt = null;
            Rekey(contract.Requirements, id, new List<int>());
            return contract;
        }

        private static void Rekey(List<ContractRequirement> requirements, string id, List<int> path)
        {
            for (var i = 0; i < requirements.Count; i++)
            {
                var at = path.Concat(new[] { i }).ToList();
                requirements[i].Key = ContractRequirement.KeyOf(id, at);
                Rekey(requirements[i].Requirements, id, at);
            }
        }

        private static void Set(SnapshotContract contract, string kind, string name, string value)
        {
            var requirement = contract.Requirements.First(r => r.Kind == kind);
            requirement.Settings = requirement.Settings.Where(s => s.Name != name).Concat(new[] { new ContractSetting(name, value) }).ToList();
        }

        /// <summary>The pad craft flown remote with no crew: it may be the satellite itself (its pod has an antenna, its panels power).</summary>
        private static GameSnapshot Uncrewed(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Crew.Clear();
            return snapshot;
        }

        /// <summary>The pad craft with a docking port and <paramref name="seats"/> seats: enough for the station and base fixtures.</summary>
        private static GameSnapshot Outfitted(int seats, params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Parts[0].Tags.Add("docking_port");
            snapshot.Craft.Parts[0].CrewCapacity = seats;
            snapshot.Craft.Seats = seats;
            return snapshot;
        }

        private static double OrbitOf(string body, bool remote)
        {
            var snapshot = ContractCheckTests.With(ContractCheckTests.Exploration("c-orbit", 10000, body + ":ORBIT"));
            snapshot.Crew.Clear();
            return Check(snapshot, remote).RouteDeltaV;
        }

        // ---- Satellites ----

        [Fact]
        public void ASatelliteIsPricedAsTheClimbFromTheParkingOrbitToTheContractsOrbit()
        {
            var check = Check(Uncrewed(Fixture("SatelliteContract.json", "c-sat")), true);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.Equal("Kerbin orbit", row.Phase);
            // Worked by hand from low Kerbin orbit (680 km from the centre) to 1,136.2 × 1,163.8 km at 88.4°: the raise to the apoapsis
            // 281.6 m/s, the periapsis raised there 235.4 m/s, and the plane: launched into it, the 176.8 m/s of Kerbin's spin it loses.
            Assert.InRange(check.RouteDeltaV - OrbitOf("Kerbin", true), 692.8, 694.8);
        }

        [Fact]
        public void ASatelliteWithClaudiaAboardMustBeAChildVesselAndThePadCraftHasNoProbeToRelease()
        {
            var left = Assert.Single(Check(ContractCheckTests.With(Fixture("SatelliteContract.json", "c-sat"))).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.StartsWith("this craft can't: no ", left.Reason);
            Assert.EndsWith(" on the uncrewed satellite", left.Reason);
        }

        [Fact]
        public void ASatelliteWithNoAntennaIsThisCraftCant()
        {
            var snapshot = Uncrewed(Fixture("SatelliteContract.json", "c-sat"));
            foreach (var part in snapshot.Craft.Parts) part.Tags.Remove("antenna");

            var left = Assert.Single(Check(snapshot, true).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no antenna on the uncrewed satellite", left.Reason);
        }

        [Fact]
        public void SatelliteNetworksAndRepositioningAreNotYet()
        {
            var network = Fixture("SatelliteContract.json", "c-net");
            var orbit = network.Requirements.First(r => r.Kind == "SpecificOrbitParameter");
            network.Requirements.Add(new ContractRequirement { Key = "c-net/3", Kind = orbit.Kind, State = "Incomplete", Settings = orbit.Settings.ToList() });
            var move = Fixture("SatelliteContract.json", "c-move");
            Set(move, "VesselSystemsParameter", "requireNew", "False");

            var check = Check(Uncrewed(network, move), true);

            Assert.Empty(check.Fitting);
            Assert.Equal(StructureKinds.NetworkNotYet, check.NotThisFlight.Single(l => l.Guid == "c-net").Reason);
            Assert.Equal(StructureKinds.RepositionNotYet, check.NotThisFlight.Single(l => l.Guid == "c-move").Reason);
            Assert.All(check.NotThisFlight, l => Assert.Equal(LeftOutKind.NotYet, l.Kind));
        }

        [Fact]
        public void TheMissionControlMarkPlansASatelliteAndHoldsANetworkNotYet()
        {
            var network = Fixture("SatelliteContract.json", "c-net");
            Set(network, "VesselSystemsParameter", "requireNew", "False");

            Assert.True(ContractMarks.Of(Fixture("SatelliteContract.json", "c-sat"), false).Plannable);
            Assert.Equal(StructureKinds.RepositionNotYet, ContractMarks.Of(network, false).Hover);
        }

        // ---- The plan check and the hold ----

        private static PlanServes PlanCheck(GameSnapshot snapshot, bool remote, params PlannedSequence[] legs)
        {
            var check = Check(snapshot, remote);
            var choice = check.Choose(check.TickedGuids);
            var plan = new MissionPlan();
            plan.Sequences.AddRange(legs);
            return PlanServes.Check(snapshot, plan, choice.Requirements, "", "Kerbin");
        }

        private static PlannedSequence Leg(string name, string intent = "Fly it.", string vessel = "")
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = 100, Vessel = vessel };
        }

        [Fact]
        public void AnOrbitLegInsideTheDeviationWindowServesEveryRequirementOfTheSatellite()
        {
            var snapshot = Uncrewed(Fixture("SatelliteContract.json", "c-sat"));

            var served = PlanCheck(snapshot, true, Leg("Launch to low Kerbin orbit"),
                Leg("Raise to the satellite's orbit", "change_orbit to apoapsis 563 km, periapsis 537 km, inclination 88°."));

            Assert.Empty(served.Problems);
            Assert.Equal(3, served.Record.Count);
            Assert.All(served.Record, r => Assert.Equal(1, r.Leg));
        }

        [Fact]
        public void AnOrbitLegOutsideTheDeviationWindowGoesBackAsAFindingWithTheGap()
        {
            var snapshot = Uncrewed(Fixture("SatelliteContract.json", "c-sat"));

            var served = PlanCheck(snapshot, true, Leg("Launch to low Kerbin orbit"),
                Leg("Raise to the satellite's orbit", "apoapsis 563 km, periapsis 537 km, inclination 70°."));

            var problem = Assert.Single(served.Problems);
            // The window is 5%: 9° of inclination; 70° is 18.4° off, 9.4° outside it.
            Assert.Contains("inclination 9.4° outside the 5% window", problem);
            Assert.Contains("leg 2", problem);
        }

        [Fact]
        public void APlanThatNeverWritesTheSatellitesOrbitIsAFinding()
        {
            var snapshot = Uncrewed(Fixture("SatelliteContract.json", "c-sat"));

            var problem = Assert.Single(PlanCheck(snapshot, true, Leg("Launch to low Kerbin orbit")).Problems);

            Assert.Contains("no leg of the plan writes the orbit", problem);
            Assert.Contains("apoapsis 563.8 km, periapsis 536.2 km, inclination 88.4°", problem);
        }

        [Fact]
        public void AChildVesselsLegMayPutTheSatelliteIntoItsOrbit()
        {
            var snapshot = ContractCheckTests.With(Fixture("SatelliteContract.json", "c-sat"));
            var plan = new MissionPlan();
            plan.Sequences.Add(Leg("Launch to low Kerbin orbit"));
            plan.Sequences.Add(Leg("Release the probe and raise it to its orbit", "apoapsis 564 km, periapsis 536 km, inclination 88.4°.", "Probe"));
            plan.Sequences.Add(Leg("Return to Kerbin"));
            var chosen = ContractKinds.Read(snapshot.Contracts.Accepted[0], "Kerbin").Select(r => new ChosenRequirement
                { Key = r.Requirement.Key, Title = "SatelliteContract", Words = r.Words, Body = r.Body, Reach = r.Reach, Orbit = r.Orbit, ChildMay = true }).ToList();

            var served = PlanServes.Check(snapshot, plan, chosen, "", "Kerbin");

            Assert.Empty(served.Problems);
            Assert.All(served.Record, r => Assert.Equal(1, r.Leg));
        }

        [Fact]
        public void TheHoldWaitsOnlyForAnOrbitInsideTheWindowAndOtherwiseGivesTheGap()
        {
            var snapshot = Uncrewed(Fixture("SatelliteContract.json", "c-sat"));
            var check = Check(snapshot, true);
            var orbit = check.Choose(check.TickedGuids).Requirements.Single(r => r.Orbit != null);
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Kerbin";
            snapshot.Orbit = new SnapshotOrbit { Body = "Kerbin", Apoapsis = 563000, Periapsis = 537000, Inclination = 88 };

            Assert.True(ContractWatch.ConditionsHold(snapshot, orbit, "Kerbin"));

            snapshot.Orbit.Inclination = 75.6;
            Assert.False(ContractWatch.ConditionsHold(snapshot, orbit, "Kerbin"));
            Assert.Equal("inclination 3.8° outside the 5% window", ContractWatch.Gap(snapshot, orbit));
        }

        // ---- Stations ----

        [Fact]
        public void AStationIsTheCraftInOrbitAndPricedAsThatOrbit()
        {
            var check = Check(Outfitted(8, Fixture("StationContract.json", "c-station")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Kerbin orbit", row.Phase);
            Assert.Equal(OrbitOf("Kerbin", false), check.RouteDeltaV, 3);
        }

        [Fact]
        public void AStationMissingADockingPortOrSeatsIsThisCraftCant()
        {
            var noPort = Check(ContractCheckTests.With(Fixture("StationContract.json", "c-station")));
            var fewSeats = Check(Outfitted(4, Fixture("StationContract.json", "c-station")));

            Assert.Equal("this craft can't: no docking port aboard", Assert.Single(noPort.NotThisFlight).Reason);
            var left = Assert.Single(fewSeats.NotThisFlight);
            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: seats 4 kerbals, the station needs 8", left.Reason);
        }

        [Fact]
        public void AStationNeedingAPartDockedOnIsNotYet()
        {
            var docked = Fixture("StationContract.json", "c-dock");
            Set(docked, "VesselSystemsParameter", "requireNew", "False");

            var left = Assert.Single(Check(Outfitted(8, docked)).NotThisFlight);

            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal(StructureKinds.DockedStationNotYet, left.Reason);
        }

        [Fact]
        public void AStationsMissingNeedIsNeverACraftCheckBlocker()
        {
            var snapshot = ContractCheckTests.With(Fixture("StationContract.json", "c-station"));

            var check = Check(snapshot);

            Assert.True(check.WordsFit);
            Assert.Equal(LeftOutKind.CraftCant, Assert.Single(check.NotThisFlight).Kind);
        }

        // ---- Bases ----

        [Fact]
        public void ABaseIsALandingAtTheSiteHeldThereAndItsOptionalCrewTraitIsSkippedWithANote()
        {
            var landing = Check(ContractCheckTests.With(ContractCheckTests.Exploration("c-land", 10000, "Mun:LANDING"))).RouteDeltaV;

            var check = Check(Outfitted(5, Fixture("BaseContract.json", "c-base")));

            var row = Assert.Single(check.Fitting);
            Assert.Equal("Mun", row.Phase);
            Assert.Equal(landing, check.RouteDeltaV, 3);
            Assert.Contains("carries 0 Pilots, the base needs 2", row.Note);
            var choice = check.Choose(check.TickedGuids);
            Assert.Contains(choice.Requirements, r => r.Words == "hold the base landed on the Mun for 10 s" && r.Reach == ContractReach.Land);
        }

        [Fact]
        public void AMobileBaseNeedsWheelsAboard()
        {
            var mobile = Fixture("BaseContract.json", "c-rover");
            Set(mobile, "VesselSystemsParameter", "checkModuleTypes", "Antenna|Power|Wheel");
            var snapshot = Outfitted(5, mobile);

            Assert.Equal("this craft can't: no wheel aboard", Assert.Single(Check(snapshot).NotThisFlight).Reason);

            snapshot.Craft.Parts[1].Tags.Add("wheel");
            Assert.Equal("c-rover", Assert.Single(Check(snapshot).Fitting).Guid);
        }

        [Fact]
        public void TheHoldForABaseIsLandedOnItsBody()
        {
            var snapshot = Outfitted(5, Fixture("BaseContract.json", "c-base"));
            var check = Check(snapshot);
            var hold = check.Choose(check.TickedGuids).Requirements.Single(r => r.Words.StartsWith("hold"));
            snapshot.Body = "Mun";
            snapshot.Situation = "LANDED";

            Assert.True(ContractWatch.ConditionsHold(snapshot, hold, "Kerbin"));
        }
    }
}
