// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// Chute Groups (ADR 0019) on the recorded Mun Shot v2 in Kerbin orbit (Mk1 pod with a stage-0 Mk16 above its heat shield; a
    /// lander stage below on a TD-12; the snapshot carries Kerbin's and Duna's air), its chutes given stock's Mk16 numbers
    /// (<see cref="ReentrySim.Mk16"/>), and on a small craft with no shield for the worked examples.
    /// </summary>
    public class ChuteGroupsTests
    {
        private const uint PodChute = 1117472593;
        /// <summary>The FL-T200 on the lander stage, under the shield and the TD-12.</summary>
        private const uint LanderTank = 1454380882;

        /// <summary>The fixture craft, with <paramref name="landerChutes"/> more Mk16s in <paramref name="landerStage"/> on the lander's tank.</summary>
        private static GameSnapshot Craft(int landerChutes = 0, bool podChute = true, int landerStage = 1)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            var pod = snapshot.Craft.Parts.Single(p => p.Id == PodChute);
            if (podChute) pod.Chute = ReentrySim.Mk16();
            else snapshot.Craft.Parts.Remove(pod);
            for (var i = 0; i < landerChutes; i++) snapshot.Craft.Parts.Add(Chute((uint)(900 + i), LanderTank, landerStage));
            return snapshot;
        }

        private static SnapshotPart Chute(uint id, uint parent, int stage, string title = null)
        {
            return new SnapshotPart
            {
                Id = id, ParentId = parent, Name = "parachuteSingle", Title = title ?? "Mk16 " + id, Stage = stage, MassT = 0.1,
                Tags = new List<string> { "parachute" }, Chute = ReentrySim.Mk16(),
            };
        }

        /// <summary>A pod with no shield, so no Entry drops anything, and chutes of the stages given.</summary>
        private static GameSnapshot Simple(params int[] stages)
        {
            var snapshot = Craft(podChute: false);
            snapshot.Craft.Parts.Clear();
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1, Name = "mk1pod", MassT = 1, CrewCapacity = 1, Tags = new List<string> { "command", "crew_seat" } });
            for (var i = 0; i < stages.Length; i++) snapshot.Craft.Parts.Add(Chute((uint)(10 + i), 1, stages[i]));
            snapshot.Craft.MassTonnes = 1.5;
            return snapshot;
        }

        private static PlannedSequence Leg(string name, string intent, double deltaV)
        {
            return new PlannedSequence { Name = name, Intent = intent, DeltaV = deltaV };
        }

        /// <summary>The Duna benchmark's shape: out to Duna, land under chutes, then home.</summary>
        private static List<PlannedSequence> DunaLandingAndReturn()
        {
            return new List<PlannedSequence>
            {
                Leg("Duna transfer", "Transfer to Duna and capture into low Duna orbit.", 3500),
                Leg("Land on Duna", "Deorbit and land on Duna under parachute.", 100),
                Leg("Ascend and return", "Ascend to low Duna orbit, fly the return to Kerbin, then reenter and land under parachute.", 1500),
            };
        }

        private static List<PlannedSequence> KerbinOnly()
        {
            return new List<PlannedSequence> { Leg("Deorbit and splash down", "Reenter Kerbin's atmosphere and splash down under parachute.", 100) };
        }

        private static ChuteGroups Group(GameSnapshot snapshot, IList<PlannedSequence> plan, int firstStep = 0)
        {
            return ChuteGroups.Of(snapshot, plan, firstStep, false, null);
        }

        private static int[] Stages(EntryChutes entry)
        {
            return entry.Chutes.Select(c => c.Stage).OrderByDescending(s => s).ToArray();
        }

        // ---- The return's mass is the craft EntryCraft says enters (#925) ----

        private const uint PodPart = 1, Shield = 3, Decoupler = 4, StackTank = 5;

        /// <summary>
        /// A craft built from the stack end (its tank is the root): tank, a decoupler, a heat shield, the pod and its chute. The shield is not itself a staged
        /// decoupler here, so the decoupler is the one that drops the stack, and it sits on the pod's side of the tree, where ChildVessels.Staging keeps it.
        /// <paramref name="goesWithStack"/> says whether it lets go of the shield (#924: it leaves with the stack) or of the tank (it stays).
        /// </summary>
        private static GameSnapshot WithStack(bool goesWithStack)
        {
            var snapshot = Simple();
            var parts = snapshot.Craft.Parts;
            parts.Single(p => p.Id == PodPart).ParentId = Shield;
            parts.Add(Chute(2, PodPart, 0));
            parts.Add(new SnapshotPart { Id = StackTank, ParentId = null, Name = "fuelTank", MassT = 1.25, Stage = 1 });
            var decoupler = new SnapshotPart { Id = Decoupler, ParentId = StackTank, Name = "Decoupler.1", MassT = 0.05, Stage = 1, Tags = new List<string> { "decoupler" } };
            decoupler.Releases.Add(goesWithStack ? Shield : StackTank);
            parts.Add(decoupler);
            parts.Add(new SnapshotPart { Id = Shield, ParentId = Decoupler, Name = "HeatShield1", MassT = 0.4, Tags = new List<string> { "heat_shield" } });
            snapshot.Craft.MassTonnes = parts.Sum(p => p.MassT);
            return snapshot;
        }

        private static double EntersKg(GameSnapshot snapshot)
        {
            var craft = EntryCraft.Of(snapshot, null, PodPart, 1, true, true);
            return (craft.KeptT - (craft.ShieldDropped != null ? craft.ShieldDropped.MassT : 0)) * 1000;
        }

        private static double ReturnKg(GameSnapshot snapshot)
        {
            return Group(snapshot, KerbinOnly()).Return.MassKg;
        }

        [Fact]
        public void AReturnsMassLeavesOutADecouplerThatLeavesWithTheStack()
        {
            var snapshot = WithStack(goesWithStack: true);
            Assert.Contains(Decoupler, EntryCraft.Of(snapshot, null, PodPart, 1, true, true).LeftOut.Select(p => p.Id));
            Assert.Equal(EntersKg(snapshot), ReturnKg(snapshot), 6);
        }

        [Fact]
        public void AReturnsMassStillCountsADecouplerThatStays()
        {
            var snapshot = WithStack(goesWithStack: false);
            Assert.DoesNotContain(Decoupler, EntryCraft.Of(snapshot, null, PodPart, 1, true, true).LeftOut.Select(p => p.Id));
            Assert.Equal(EntersKg(snapshot), ReturnKg(snapshot), 6);
            // Only the stack that stays keeps its decoupler, and its shield then jettisons (EntryCraft.ShieldDropped): 0.4 t off, 0.05 t on.
            Assert.Equal(ReturnKg(WithStack(goesWithStack: true)) + 50 - 400, ReturnKg(snapshot), 6);
        }

        // ---- A Return's mass is EntryCraft's, whichever part is the root (#930) ----

        /// <summary>The craft rebuilt with <paramref name="root"/> as its root: the parent links along the way from the old root run the other way.</summary>
        private static void RootAt(GameSnapshot snapshot, uint root)
        {
            var byId = snapshot.Craft.Parts.ToDictionary(p => p.Id);
            var path = new List<SnapshotPart>();
            for (var part = byId[root]; part != null; part = part.ParentId.HasValue && byId.ContainsKey(part.ParentId.Value) ? byId[part.ParentId.Value] : null) path.Add(part);
            for (var i = path.Count - 1; i > 0; i--) path[i].ParentId = path[i - 1].Id;
            path[0].ParentId = null;
            snapshot.Craft.Parts.Remove(path[0]);
            snapshot.Craft.Parts.Insert(0, path[0]);
        }

        /// <summary>The fixture with the TD-12 letting go of the heat shield on its top node, as the adapter reads <see cref="SnapshotPart.Releases"/> live (#924); the recording's JSON doesn't carry it.</summary>
        private static GameSnapshot MunShot()
        {
            var snapshot = Craft();
            snapshot.Craft.Parts.Single(p => p.Id == 1607102063).Releases.Add(558691162);
            return snapshot;
        }

        [Fact]
        public void TheMunShotReturnReadsTheSameMassRootedAtThePodAndAtTheLanderTank()
        {
            var pod = ReturnKg(MunShot());
            var tank = MunShot();
            RootAt(tank, LanderTank);
            Assert.Equal(1115, pod, 0);
            Assert.Equal(pod, ReturnKg(tank), 6);
        }

        [Fact]
        public void AShieldlessReturnReadsWhatStagingKeeps_NotTheLaunchStack()
        {
            var snapshot = Craft();
            snapshot.Craft.Parts.Single(p => p.Id == 558691162).Tags.Clear(); // the shield's tags: heat_shield, decoupler, aero_stabiliser
            var home = Assert.Single(Group(snapshot, KerbinOnly()).Entries);
            Assert.Equal(1415, home.MassKg, 0);
            Assert.Equal(8.5, home.TouchdownSpeed, 1);
            Assert.False(Group(snapshot, KerbinOnly()).ReturnUncovered);
        }

        [Fact]
        public void AKeptShieldReadsWithItsMass()
        {
            // The decoupler goes with the stack, so nothing is left beside the shield to jettison it: it stays, and its 0.4 t is in the figure.
            var snapshot = WithStack(goesWithStack: true);
            var craft = EntryCraft.Of(snapshot, null, PodPart, 1, true, true);
            Assert.Null(craft.ShieldDropped);
            Assert.Equal(craft.KeptT * 1000, ReturnKg(snapshot), 6);
            Assert.Equal(1000 + 100 + 0.4 * 1000, ReturnKg(snapshot), 6);
        }

        [Fact]
        public void AStuckStackReadsAsEntryCraftKeepsIt()
        {
            var snapshot = WithStack(goesWithStack: true);
            snapshot.Craft.Parts.Single(p => p.Id == Decoupler).Releases.Clear();
            var craft = EntryCraft.Of(snapshot, null, PodPart, 1, true, true);
            Assert.Equal((craft.KeptT - (craft.ShieldDropped != null ? craft.ShieldDropped.MassT : 0)) * 1000, ReturnKg(snapshot), 6);
        }

        // ---- The worked examples ----

        [Fact]
        public void Stage2FourAndStage0OneOverDunaThenKerbin_DunaOpensStage2AndKerbinStage0()
        {
            var groups = Group(Simple(2, 2, 2, 2, 0), DunaLandingAndReturn());

            Assert.Equal(new[] { 2, 2, 2, 2 }, Stages(groups.Entries[0]));
            Assert.False(groups.Entries[0].Last);
            Assert.Equal(new[] { 0 }, Stages(groups.Entries[1]));
            Assert.True(groups.Entries[1].Last);
            Assert.True(groups.Entries[1].Return);
        }

        [Fact]
        public void Stage2RepackedAtDuna_KerbinIsTheLastEntryAndEveryStowedChuteOpens()
        {
            var groups = Group(Simple(2, 2, 2, 2, 0), DunaLandingAndReturn(), 2);

            var home = Assert.Single(groups.Entries);
            Assert.True(home.Last);
            Assert.Equal(new[] { 2, 2, 2, 2, 0 }, Stages(home));
        }

        [Fact]
        public void Stage2AndStage0OverAKerbinReturnOnly_TheLastEntryTakesAllAboard()
        {
            var home = Assert.Single(Group(Simple(2, 0), KerbinOnly()).Entries);

            Assert.True(home.Last);
            Assert.Equal(new[] { 2, 0 }, Stages(home));
        }

        [Fact]
        public void Stage0OnlyOverDunaThenKerbin_DunaHasItAndKerbinHasNone()
        {
            var groups = Group(Simple(0), DunaLandingAndReturn());

            Assert.Equal(new[] { 0 }, Stages(groups.Entries[0]));
            Assert.Empty(groups.Entries[1].Chutes);
        }

        // ---- What counts, read fresh ----

        [Fact]
        public void AChuteWithStagingDisabledIsInTheLastGroup()
        {
            var groups = Group(Simple(0, -1), DunaLandingAndReturn());

            Assert.Equal(new[] { 0 }, Stages(groups.Entries[0]));
            Assert.Equal(new[] { -1 }, Stages(groups.Entries[1]));
            Assert.Equal(new[] { 2 }, ChuteGroups.Choose(Group(Simple(2, -1), KerbinOnly()).Chutes, false).Select(c => c.Stage));
        }

        [Fact]
        public void AChuteRestagedMovesGroup()
        {
            var snapshot = Simple(2, 0, 0);
            Assert.Single(Group(snapshot, DunaLandingAndReturn()).Entries[0].Chutes);

            snapshot.Craft.Parts.Single(p => p.Id == 11).Stage = 2;

            var groups = Group(snapshot, DunaLandingAndReturn());
            Assert.Equal(new uint[] { 10, 11 }, groups.Entries[0].Chutes.Select(c => c.PartId));
            Assert.Equal(new uint[] { 12 }, groups.Entries[1].Chutes.Select(c => c.PartId));
        }

        [Theory]
        [InlineData(ReentryChuteState.SemiDeployed)]
        [InlineData(ReentryChuteState.Deployed)]
        public void AnOpenChuteDoesntCountUntilRepacked(ReentryChuteState open)
        {
            var snapshot = Simple(2, 0);
            snapshot.Craft.Parts.Single(p => p.Id == 10).Chute.State = open;

            Assert.Equal(new uint[] { 11 }, ChuteGroups.ForEntryNow(snapshot, false).Chutes.Select(c => c.PartId));

            // A repack reads as stowed, and it is the highest group again.
            snapshot.Craft.Parts.Single(p => p.Id == 10).Chute.State = ReentryChuteState.Stowed;
            Assert.Equal(new uint[] { 10 }, ChuteGroups.ForEntryNow(snapshot, false).Chutes.Select(c => c.PartId));
        }

        [Fact]
        public void ACutChuteDoesntCountAndAGoneOneIsNotThere()
        {
            var snapshot = Simple(2, 1, 0);
            snapshot.Craft.Parts.Single(p => p.Id == 10).Chute = null;
            snapshot.Craft.Parts.RemoveAll(p => p.Id == 11);

            Assert.Equal(new uint[] { 12 }, ChuteGroups.ForEntryNow(snapshot, false).Chutes.Select(c => c.PartId));
        }

        [Fact]
        public void AnEntryLosesWhatItsOwnDropTakesBeforeItChooses()
        {
            // The Kerbin return is shield number 1: the stack below it goes, the lander's chutes with it.
            var home = Assert.Single(Group(Craft(landerChutes: 2), KerbinOnly()).Entries);

            Assert.Equal(new[] { PodChute }, home.Chutes.Select(c => c.PartId));
            Assert.False(home.PoweredFinish);
            Assert.InRange(home.TouchdownSpeed, 3, ChuteGroups.TouchdownLimit);
        }

        [Fact]
        public void ADunaLandingUsesTheLandersGroupAndTheReturnThePods()
        {
            var groups = Group(Craft(landerChutes: 4), DunaLandingAndReturn());

            Assert.Equal(2, groups.Entries.Count);
            var duna = groups.Entries[0];
            var home = groups.Entries[1];
            Assert.Equal(1, duna.Step);
            Assert.Equal("Duna", duna.Body);
            Assert.False(duna.Return);
            Assert.Equal(2, home.Step);
            Assert.True(home.Return);
            Assert.Equal(new[] { 1, 1, 1, 1 }, Stages(duna));
            Assert.False(duna.PoweredFinish);
            Assert.InRange(duna.TouchdownSpeed, 0, ChuteGroups.TouchdownLimit);
            Assert.Equal(new[] { PodChute }, home.Chutes.Select(c => c.PartId));
            Assert.False(groups.ReturnUncovered);
        }

        [Fact]
        public void AWeakGroupStaysAPoweredFinishWithItsTouchdownSpeed()
        {
            var duna = Group(Craft(landerChutes: 1), DunaLandingAndReturn()).Entries[0];

            Assert.True(duna.PoweredFinish);
            Assert.True(duna.TouchdownSpeed > ChuteGroups.TouchdownLimit, "Duna touchdown " + duna.TouchdownSpeed);
        }

        [Fact]
        public void AReturnItsChutesCantCoverIsFlagged()
        {
            // The only chute is on the lander, and its stack drops at the return.
            var groups = Group(Craft(landerChutes: 1, podChute: false), KerbinOnly());

            Assert.True(groups.Return.PoweredFinish);
            Assert.True(groups.Return.TouchdownSpeed > ChuteGroups.TouchdownLimit);
            Assert.True(groups.ReturnUncovered);
        }

        [Fact]
        public void BurnsAfterTheFinalEntryMeanItIsNotTheLastEntry()
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Land on Duna", "Deorbit and land on Duna under parachute.", 100),
                Leg("Ascend and dock", "Ascend to low Duna orbit and dock with the station.", 1400),
            };

            var duna = Assert.Single(Group(Simple(2, 0), plan).Entries);

            Assert.False(duna.Last);
            Assert.Equal(new[] { 2 }, Stages(duna));
        }

        [Fact]
        public void AChildVesselsBurnsAfterTheFinalEntryDontStopItBeingTheLastEntry()
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Land on Duna", "Deorbit and land on Duna under parachute.", 100),
                new PlannedSequence { Name = "Probe burn", Intent = "Raise the probe's orbit.", DeltaV = 200, Vessel = "Probe" },
            };

            var duna = Assert.Single(Group(Simple(2, 0), plan).Entries);

            Assert.True(duna.Last);
            Assert.Equal(new[] { 2, 0 }, Stages(duna));
        }

        [Fact]
        public void TheDescriptionNamesEachEntrysGroupAndTouchdown()
        {
            var text = Group(Simple(2, 0), DunaLandingAndReturn()).Describe();

            Assert.Contains("Land on Duna (Duna): stage 2 (1 chute)", text);
            Assert.Contains("Ascend and return (Kerbin): every chute aboard (1 chute), ", text);
            Assert.Contains(" m/s", text);
            Assert.Equal("", Group(Simple(2, 0), new List<PlannedSequence>()).Describe());
        }

        // ---- The reader of Entries (ADR 0018) ----

        [Fact]
        public void AMunMissionHasNoEntries()
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land East Crater", "Land in the Mun's East Crater biome in daylight.", 650),
            };

            var groups = Group(Craft(), plan);

            Assert.Empty(groups.Entries);
            Assert.False(groups.ReturnUncovered);
            Assert.Single(groups.Chutes);
        }

        [Fact]
        public void AComeHomeLegAfterAMunLandingIsTheKerbinReturnEntry()
        {
            var plan = new List<PlannedSequence> { Leg("Mun landing and return", "Land on the Mun and come home.", 500) };

            var home = Assert.Single(Group(Craft(landerChutes: 1), plan).Entries);

            Assert.True(home.Return);
            Assert.Equal("Kerbin", home.Body);
        }

        [Fact]
        public void AerobrakingIsNotAnEntryAndADeorbitLegBeforeItsLandingIsTheSameEntry()
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Duna transfer", "Transfer to Duna.", 1000),
                Leg("Aerobrake", "Aerobrake at Duna into a low orbit.", 0),
                Leg("Deorbit", "Deorbit over the target and reenter Duna's atmosphere.", 50),
                Leg("Touchdown", "Land on Duna under parachute.", 50),
            };

            var duna = Assert.Single(Group(Craft(landerChutes: 4), plan).Entries);

            Assert.Equal(3, duna.Step);
            Assert.Equal("Touchdown", duna.Sequence);
        }

        // ---- The predictor's ends ----

        [Fact]
        public void ADunaHighlandEntryTouchesDownHarderThanTheSameEntryAtSeaLevel()
        {
            var plan = DunaLandingAndReturn();
            var low = Group(Craft(landerChutes: 1), plan).Entries.Single(e => e.Body == "Duna");
            var snapshot = Craft(landerChutes: 1);
            snapshot.Bodies.Single(b => b.Name == "Duna").TerrainHeight = 6000;
            var high = Group(snapshot, plan).Entries.Single(e => e.Body == "Duna");

            Assert.Equal(6000, high.EndAltitude);
            Assert.True(high.TouchdownSpeed > low.TouchdownSpeed, high.TouchdownSpeed + " vs " + low.TouchdownSpeed);
        }

        [Fact]
        public void AnEntryWithNoSiteEndsAtTheBodysConservativeHeightAndKerbinAtSeaLevel()
        {
            var snapshot = Craft(landerChutes: 1);
            var duna = snapshot.Bodies.Single(b => b.Name == "Duna");
            var groups = Group(snapshot, DunaLandingAndReturn());

            Assert.Equal(duna.Radius * ChuteGroups.TerrainShareOfRadius, groups.Entries.Single(e => e.Body == "Duna").EndAltitude, 6);
            Assert.True(groups.Entries.Single(e => e.Body == "Duna").EndAltitude > 0);
            Assert.Equal(0, groups.Return.EndAltitude);
        }

        // ---- The block's call ----

        private static string Uid(uint id)
        {
            return id == 903 ? null : "uid" + id;
        }

        [Fact]
        public void TheAddonCallGivesTheHighestGroupByKosUidOnANonLastEntry()
        {
            var snapshot = Craft(landerChutes: 4);
            snapshot.Orbit.Body = "Duna";
            var plan = DunaLandingAndReturn();

            var set = BlockCalls.ChuteGroupCall(snapshot, plan, 1, "", Uid);

            Assert.Equal("", set["error"]);
            Assert.Equal(false, set["last"]);
            Assert.Equal(1.0, Convert.ToDouble(set["stage"]));
            // 903 isn't aboard: left out.
            Assert.Equal(new object[] { "uid900", "uid901", "uid902" }, (List<object>)set["uids"]);
            Assert.Equal(3, ((List<object>)set["titles"]).Count);
        }

        [Fact]
        public void TheAddonCallGivesEveryChuteOnTheLastEntryAndWithNoPlan()
        {
            var snapshot = Craft(landerChutes: 2);

            var noPlan = BlockCalls.ChuteGroupCall(snapshot, null, -1, "", Uid);
            Assert.Equal(true, noPlan["last"]);
            Assert.Equal(3, ((List<object>)noPlan["uids"]).Count);

            var home = BlockCalls.ChuteGroupCall(snapshot, DunaLandingAndReturn(), 2, "", Uid);
            Assert.Equal(true, home["last"]);
        }

        [Fact]
        public void TheAddonCallKeepsTheTouchdownForTheLog()
        {
            var snapshot = Craft(landerChutes: 1);
            snapshot.Orbit.Body = "Duna";

            var set = BlockCalls.ChuteGroupCall(snapshot, DunaLandingAndReturn(), 1, "", Uid);

            Assert.True((double)set["touchdown_speed"] > ChuteGroups.TouchdownLimit);
            Assert.Equal("there is no Game Snapshot of the craft", BlockCalls.ChuteGroupCall(null, null, 0, "", Uid)["error"]);
        }

        [Fact]
        public void TheChutesNumbersGoInTheRecordingButNotInClaudiasFacts()
        {
            var snapshot = Craft();

            var parsed = GameSnapshot.Parse(snapshot.ToJson()).Craft.Parts.Single(p => p.Id == PodChute).Chute;

            Assert.NotNull(parsed);
            Assert.Equal(ReentrySim.Mk16().FullArea, parsed.FullArea, 6);
            Assert.Equal(1000, parsed.DeployAltitude);
            Assert.Equal(ReentryChuteState.Stowed, parsed.State);
            Assert.DoesNotContain("\"chute\"", snapshot.ToJson(false));
        }
    }
}
