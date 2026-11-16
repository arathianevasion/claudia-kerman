// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1033, seam 1: Target Vessel Contracts (orbital rescue, repair, construction, comet sample). Claudia flies to the vessel the game
    /// spawned for the Contract (ADR 0013's amendment: the Contract's vessel is the Mission's one Target Vessel) and the player does the
    /// EVA in a Manual stretch; the route is priced by the rendezvous with it, and a set holds at most one such Contract.
    /// </summary>
    public sealed class TargetVesselContractTests
    {
        private static SnapshotContract Fixture(string file, string id)
        {
            var contract = ContractCheckTests.Fixture(file);
            contract.Guid = id;
            for (var i = 0; i < contract.Requirements.Count; i++) contract.Requirements[i].Key = ContractRequirement.KeyOf(id, new[] { i });
            return contract;
        }

        /// <summary>The vessel the game spawned for <paramref name="contract"/>, in a circular orbit <paramref name="km"/> above Kerbin.</summary>
        private static SnapshotVessel Spawned(SnapshotContract contract, uint id, string name, double km, string situation = "ORBITING", string body = "Kerbin")
        {
            return new SnapshotVessel
            {
                Id = id, Name = name, Body = body, Situation = situation, PeriapsisM = km * 1000, ApoapsisM = km * 1000, Inclination = 0, ContractGuid = contract.Guid,
            };
        }

        /// <summary>The pad craft with a second seat, Claudia in the first: one seat is empty.</summary>
        private static GameSnapshot WithSeat(params SnapshotContract[] contracts)
        {
            var snapshot = ContractCheckTests.With(contracts);
            snapshot.Craft.Seats = 2;
            return snapshot;
        }

        private static ContractCheck Check(GameSnapshot snapshot, string words = "", TargetVessel target = null)
        {
            return ContractCheck.Work(snapshot, words, false, ContractCheckTests.Margin, target);
        }

        [Fact]
        public void AnOrbitalRescueIsPlannedAsTheRendezvousAManualStretchForTheEvaAndTheTripHome()
        {
            var rescue = Fixture("RecoverAsset.json", "c-rescue");
            var snapshot = WithSeat(rescue);
            snapshot.ContractVessels.Add(Spawned(rescue, 4401, "Gusdun's Capsule", 120));

            var check = Check(snapshot);

            var row = Assert.Single(check.Fitting);
            Assert.True(row.Ticked);
            Assert.True(row.YouFly);
            Assert.Equal("Kerbin orbit, Return", row.Phase);
            var choice = check.Choose(check.TickedGuids);
            var eva = choice.Requirements.Single(r => r.Manual.Length > 0);
            Assert.Equal("Kerbin", eva.Body);
            Assert.Equal(ContractReach.Orbit, eva.Reach);
            Assert.Contains("Gusdun Kerman", eva.Manual);
            Assert.Contains(choice.Requirements, r => r.Return);
            Assert.Equal((uint)4401, choice.Target.Id);
            Assert.Contains("Gusdun's Capsule", choice.Section);
        }

        [Fact]
        public void TheRouteIsPricedByTheRendezvousSoAHigherTargetCostsMore()
        {
            var low = Fixture("RecoverAsset.json", "c-low");
            var lowSnapshot = WithSeat(low);
            lowSnapshot.ContractVessels.Add(Spawned(low, 4401, "Gusdun's Capsule", 80));
            var high = Fixture("RecoverAsset.json", "c-high");
            var highSnapshot = WithSeat(high);
            highSnapshot.ContractVessels.Add(Spawned(high, 4401, "Gusdun's Capsule", 1200));

            var lowRoute = Check(lowSnapshot).RouteDeltaV;
            var highRoute = Check(highSnapshot).RouteDeltaV;

            // A Hohmann climb from 80 km to 1,200 km round Kerbin and back down is about 2 x 470 m/s; the rendezvous pays the climb and the circularisation there.
            Assert.InRange(highRoute - lowRoute, 500, 1500);
        }

        [Fact]
        public void ARescueWithNoEmptySeatIsThisCraftCant()
        {
            var rescue = Fixture("RecoverAsset.json", "c-rescue");
            var snapshot = ContractCheckTests.With(rescue);
            snapshot.ContractVessels.Add(Spawned(rescue, 4401, "Gusdun's Capsule", 120));

            var left = Assert.Single(Check(snapshot).NotThisFlight);

            Assert.Equal(LeftOutKind.CraftCant, left.Kind);
            Assert.Equal("this craft can't: no empty seat for Gusdun Kerman", left.Reason);
        }

        [Fact]
        public void AGroundRescueAndAPartRecoveryAreNotYet()
        {
            var ground = Fixture("RecoverAsset.json", "c-ground");
            var part = Fixture("RecoverAsset.json", "c-part");
            part.Settings = part.Settings.Select(s => s.Name == "recoveryType" ? new ContractSetting("recoveryType", "2") : s).ToList();
            var snapshot = WithSeat(ground, part);
            snapshot.ContractVessels.Add(Spawned(ground, 4401, "Gusdun's Capsule", 0, "LANDED", "Mun"));
            snapshot.ContractVessels.Add(Spawned(part, 4402, "Mk1 Pod Wreck", 120));

            var check = Check(snapshot);

            Assert.Equal("not yet: ground rescues aren't planned yet", check.NotThisFlight.Single(l => l.Guid == "c-ground").Reason);
            Assert.Equal("not yet: part recovery isn't planned yet", check.NotThisFlight.Single(l => l.Guid == "c-part").Reason);
            Assert.All(check.NotThisFlight, l => Assert.Equal(LeftOutKind.NotYet, l.Kind));
        }

        /// <summary>The pad craft with an Engineer aboard in a second seat.</summary>
        private static GameSnapshot WithEngineer(params SnapshotContract[] contracts)
        {
            var snapshot = WithSeat(contracts);
            snapshot.Crew.Add(new SnapshotCrewMember { Name = "Bill Kerman", Trait = "Engineer" });
            return snapshot;
        }

        [Fact]
        public void ARepairNeedsAnEngineerAndARepairKitAboard()
        {
            var repair = Fixture("VesselRepairContract.json", "c-repair");
            var noEngineer = WithSeat(repair);
            noEngineer.ContractVessels.Add(Spawned(repair, 4410, "Relay Sat Q-7", 250));
            var noKit = WithEngineer(repair);
            noKit.ContractVessels.Add(Spawned(repair, 4410, "Relay Sat Q-7", 250));
            var kitted = WithEngineer(repair);
            kitted.ContractVessels.Add(Spawned(repair, 4410, "Relay Sat Q-7", 250));
            kitted.Craft.Parts[0].Stowed.Add("evaRepairKit");

            Assert.Equal("this craft can't: no Engineer aboard", Assert.Single(Check(noEngineer).NotThisFlight).Reason);
            Assert.Equal("this craft can't: no EVA repair kit aboard", Assert.Single(Check(noKit).NotThisFlight).Reason);
            var row = Assert.Single(Check(kitted).Fitting);
            Assert.True(row.YouFly);
            Assert.Equal("Kerbin orbit", row.Phase);
        }

        [Fact]
        public void AConstructionNeedsThePartToAttachAboard()
        {
            var build = Fixture("OrbitalConstructionContract.json", "c-build");
            var without = WithEngineer(build);
            without.ContractVessels.Add(Spawned(build, 4411, "Comms Relay K-3", 250));
            var with = WithEngineer(build);
            with.ContractVessels.Add(Spawned(build, 4411, "Comms Relay K-3", 250));
            with.Craft.Parts.Add(new SnapshotPart { Id = 990002, Name = "batteryBankLarge", ParentId = with.Craft.Parts[0].Id });

            Assert.Equal("this craft can't: no batteryBankLarge aboard to attach", Assert.Single(Check(without).NotThisFlight).Reason);
            var choice = Check(with).Choose(new[] { "c-build" });
            Assert.Contains(choice.Requirements, r => r.Manual == "attach batteryBankLarge to Comms Relay K-3 on EVA");
        }

        [Fact]
        public void ARoverRepairIsALandingBesideItWithTheDriveThePlayers()
        {
            var repair = Fixture("VesselRepairContract.json", "c-rover");
            var snapshot = WithEngineer(repair);
            snapshot.Craft.Parts[0].Stowed.Add("evaRepairKit");
            snapshot.ContractVessels.Add(Spawned(repair, 4412, "Mun Rover", 0, "LANDED", "Mun"));

            var choice = Check(snapshot).Choose(new[] { "c-rover" });

            var eva = choice.Requirements.Single(r => r.Manual.Length > 0);
            Assert.Equal(ContractReach.Land, eva.Reach);
            Assert.Equal("Mun", eva.Body);
            Assert.StartsWith("drive to Mun Rover and ", eva.Manual);
        }

        /// <summary>A comet-sample Contract for the comet at <paramref name="body"/>, as the game spawned it.</summary>
        private static GameSnapshot CometAt(string body, double km)
        {
            var comet = new SnapshotContract
            {
                Guid = "c-comet", Kind = "CometSampleContract", State = "Active", Funds = 90000,
                Requirements = { new ContractRequirement { Key = ContractRequirement.KeyOf("c-comet", new[] { 0 }), Kind = "CometSampleParameter", State = "Incomplete" } },
            };
            var snapshot = WithSeat(comet);
            snapshot.ContractVessels.Add(Spawned(comet, 4420, "Comet KRB-41", km, "ORBITING", body));
            return snapshot;
        }

        [Fact]
        public void ACometSampleIsPlannedWhenTheInterceptIsPricedAndNotYetInSolarOrbit()
        {
            var nearby = Check(CometAt("Kerbin", 900));
            var solar = Check(CometAt("Sun", 13000000));

            var row = Assert.Single(nearby.Fitting);
            Assert.True(row.YouFly);
            var left = Assert.Single(solar.NotThisFlight);
            Assert.Equal(LeftOutKind.NotYet, left.Kind);
            Assert.Equal("not yet: this comet's intercept is beyond what the planner prices", left.Reason);
        }

        [Fact]
        public void ASetHoldsAtMostOneTargetVesselContractAndAnyOtherIsNotThisFlight()
        {
            var first = Fixture("RecoverAsset.json", "c-one");
            first.Funds = 50000;
            var second = Fixture("RecoverAsset.json", "c-two");
            second.Funds = 20000;
            var snapshot = WithSeat(first, second);
            snapshot.Craft.Seats = 3;
            snapshot.ContractVessels.Add(Spawned(first, 4401, "Gusdun's Capsule", 120));
            snapshot.ContractVessels.Add(Spawned(second, 4402, "Jebfrey's Capsule", 130));

            var check = Check(snapshot);

            Assert.Equal(new[] { "c-one" }, check.TickedGuids);
            var left = Assert.Single(check.NotThisFlight);
            Assert.Equal(LeftOutKind.OneTarget, left.Kind);
            Assert.Equal("not this flight: one Target Vessel per Mission", left.Reason);
            check.Tick("c-two", true);
            Assert.False(check.SetFits);
            Assert.Equal("not this flight: one Target Vessel per Mission", check.Problem);
        }

        [Fact]
        public void ATargetFromTheGoalOrThePickWinsAndTheContractIsNotThisFlight()
        {
            var rescue = Fixture("RecoverAsset.json", "c-rescue");
            var snapshot = WithSeat(rescue);
            snapshot.ContractVessels.Add(Spawned(rescue, 4401, "Gusdun's Capsule", 120));

            var other = Check(snapshot, "dock with Kerbin Station", new TargetVessel(5000, "Kerbin Station"));
            var same = Check(snapshot, "", new TargetVessel(4401, "Gusdun's Capsule"));

            var left = Assert.Single(other.NotThisFlight);
            Assert.Equal(LeftOutKind.OneTarget, left.Kind);
            Assert.False(left.Tickable);
            Assert.Equal("not this flight: one Target Vessel per Mission (this Mission's is Kerbin Station)", left.Reason);
            Assert.Single(same.Fitting);
        }

        [Fact]
        public void TheAdapterTagsAVesselByWhatItsContractNamesAKerbalAboardForARescueOrTheVesselsName()
        {
            var contracts = new SnapshotContracts();
            contracts.Accepted.Add(Fixture("RecoverAsset.json", "c-rescue"));
            contracts.Accepted.Add(Fixture("VesselRepairContract.json", "c-repair"));

            Assert.Equal("c-rescue", ContractVessels.ContractFor(contracts, "Gusdun's Capsule", new[] { "Gusdun Kerman" }));
            Assert.Equal("c-repair", ContractVessels.ContractFor(contracts, "relay sat q-7", new string[0]));
            Assert.Equal("", ContractVessels.ContractFor(contracts, "Kerbin Station", new[] { "Bill Kerman" }));
        }

        [Fact]
        public void ACometTheGameHasntSpawnedIsNotFoundRatherThanUnpriced()
        {
            var snapshot = CometAt("Kerbin", 900);
            snapshot.ContractVessels.Clear();

            Assert.Equal("not yet: the Contract's vessel isn't in the game", Assert.Single(Check(snapshot).NotThisFlight).Reason);
        }

        [Fact]
        public void TheCrewCheckIsForTheTraitTheContractNames()
        {
            var repair = Fixture("VesselRepairContract.json", "c-repair");
            var trait = repair.Requirements[0];
            trait.Settings = trait.Settings.Select(s => s.Name == "targetTrait" ? new ContractSetting("targetTrait", "Scientist") : s).ToList();
            var snapshot = WithEngineer(repair);
            snapshot.Craft.Parts[0].Stowed.Add("evaRepairKit");
            snapshot.ContractVessels.Add(Spawned(repair, 4410, "Relay Sat Q-7", 250));

            Assert.Equal("this craft can't: no Scientist aboard", Assert.Single(Check(snapshot).NotThisFlight).Reason);
        }
    }
}
