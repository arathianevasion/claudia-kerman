// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Chute Allocation (#626) on the recorded Mun Shot v2 in Kerbin orbit (Mk1 pod with a Mk16 above its heat shield; a
    /// lander stage below on a TD-12; the snapshot carries Kerbin's and Duna's air), its chutes given stock's Mk16 numbers
    /// (<see cref="ReentrySim.Mk16"/>) and, where a test wants them, more Mk16s on the lander's tank.
    /// </summary>
    public class ChuteAllocationTests
    {
        private const uint PodChute = 1117472593;
        /// <summary>The FL-T200 on the lander stage (stage 1), which the TD-12 at stage 1 takes away before the return.</summary>
        private const uint LanderTank = 1454380882;

        private static GameSnapshot Craft(int landerChutes = 0, bool podChute = true)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            var pod = snapshot.Craft.Parts.Single(p => p.Id == PodChute);
            if (podChute) pod.Chute = ReentrySim.Mk16();
            else snapshot.Craft.Parts.Remove(pod);
            for (var i = 0; i < landerChutes; i++)
            {
                snapshot.Craft.Parts.Add(new SnapshotPart
                {
                    Id = (uint)(900 + i), ParentId = LanderTank, Name = "parachuteSingle", Title = "Lander Mk16 " + (i + 1), Stage = 1, MassT = 0.1,
                    Tags = new List<string> { "parachute" }, Chute = ReentrySim.Mk16(),
                });
            }
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

        private static ChuteAllocation Allocate(GameSnapshot snapshot, IList<PlannedSequence> plan, int firstStep = 0)
        {
            return ChuteAllocation.Of(snapshot, plan, firstStep, false, null);
        }

        private static IList<string> Titles(EntryChutes entry)
        {
            return entry.Chutes.Select(c => c.Title).ToList();
        }

        [Fact]
        public void AMunMissionHasNoEntriesSoTheOneReentryKeepsEveryChute()
        {
            var plan = new List<PlannedSequence>
            {
                Leg("Mun transfer and capture", "Transfer to the Mun with a 30 km periapsis, then circularize into low Mun orbit.", 1200),
                Leg("Land East Crater", "Land in the Mun's East Crater biome in daylight.", 650),
            };

            var allocation = Allocate(Craft(), plan);

            Assert.Empty(allocation.Entries);
            Assert.False(allocation.ReturnUncovered);
            Assert.Single(allocation.Chutes);
            Assert.Null(allocation.For(1, "Mun"));
        }

        [Fact]
        public void AKerbinReturnAloneKeepsEveryChuteAboard()
        {
            var plan = new List<PlannedSequence> { Leg("Deorbit and splash down", "Reenter Kerbin's atmosphere and splash down under parachute.", 100) };

            var allocation = Allocate(Craft(landerChutes: 2), plan);

            var home = Assert.Single(allocation.Entries);
            Assert.True(home.Return);
            Assert.Equal("Kerbin", home.Body);
            Assert.Equal(new[] { "Lander Mk16 1", "Lander Mk16 2", "Mk16 Parachute" }, Titles(home).OrderBy(t => t, StringComparer.Ordinal));
            Assert.False(home.PoweredFinish);
            Assert.InRange(home.TouchdownSpeed, 3, ChuteAllocation.TouchdownLimit);
            Assert.False(allocation.ReturnUncovered);
        }

        [Fact]
        public void ADunaLandingUsesTheLandersChutesAndThePodsAreKeptForTheReturn()
        {
            var allocation = Allocate(Craft(landerChutes: 4), DunaLandingAndReturn());

            Assert.Equal(2, allocation.Entries.Count);
            var duna = allocation.Entries[0];
            var home = allocation.Entries[1];
            Assert.Equal(1, duna.Step);
            Assert.Equal("Duna", duna.Body);
            Assert.False(duna.Return);
            Assert.Equal(2, home.Step);
            Assert.True(home.Return);

            Assert.True(duna.Chutes.Count > 0, allocation.Describe());
            Assert.All(duna.Chutes, c => Assert.False(c.Pod));
            Assert.False(duna.PoweredFinish);
            Assert.InRange(duna.TouchdownSpeed, 0, ChuteAllocation.TouchdownLimit);
            Assert.Contains("Mk16 Parachute", Titles(home));
            Assert.False(home.PoweredFinish);
            Assert.False(allocation.ReturnUncovered);
            // No chute is given twice.
            Assert.Equal(allocation.Chutes.Count, allocation.Entries.Sum(e => e.Chutes.Count));
        }

        [Fact]
        public void TheDunaLandingTakesOnlyTheLanderChutesItNeedsAndTheRestStayWithTheReturn()
        {
            var few = Allocate(Craft(landerChutes: 4), DunaLandingAndReturn());
            var many = Allocate(Craft(landerChutes: 8), DunaLandingAndReturn());

            Assert.Equal(few.Entries[0].Chutes.Count, many.Entries[0].Chutes.Count);
            Assert.Equal(9 - many.Entries[0].Chutes.Count, many.Entries[1].Chutes.Count);
        }

        [Fact]
        public void WithOneMk16TheDunaLandingIsAPoweredFinishAndTheMk16IsKeptForTheReturn()
        {
            var allocation = Allocate(Craft(), DunaLandingAndReturn());

            var duna = allocation.Entries[0];
            var home = allocation.Entries[1];
            Assert.Empty(duna.Chutes);
            Assert.True(duna.PoweredFinish);
            // The speed its chutes (none) leave it at, for the delta-v its powered finish needs.
            Assert.True(duna.TouchdownSpeed > 100, "Duna touchdown " + duna.TouchdownSpeed);
            Assert.Equal(new[] { "Mk16 Parachute" }, Titles(home));
            Assert.False(home.PoweredFinish);
            Assert.False(allocation.ReturnUncovered);
        }

        [Fact]
        public void AReturnThePodsChutesCantCoverIsFlagged()
        {
            // The only chute is on the lander, which leaves before the return.
            var allocation = Allocate(Craft(landerChutes: 1, podChute: false), DunaLandingAndReturn());

            var home = allocation.Return;
            Assert.True(home.PoweredFinish);
            Assert.True(home.TouchdownSpeed > ChuteAllocation.TouchdownLimit);
            Assert.True(allocation.ReturnUncovered);
            Assert.Contains("The return can't be covered.", allocation.Describe());
        }

        [Fact]
        public void AChuteUsedOrLostSinceIsLeftOutWhenTheAllocationIsWorkedOutAgain()
        {
            var plan = DunaLandingAndReturn();
            var before = Allocate(Craft(landerChutes: 4), plan);

            // After the Duna landing's Debrief: the lander's chutes opened (one torn off), and the return is what is left.
            var after = Craft(landerChutes: 4);
            foreach (var chute in after.Craft.Parts.Where(p => p.Id >= 900 && p.Id < 904)) chute.Chute.State = ReentryChuteState.Deployed;
            after.Craft.Parts.RemoveAll(p => p.Id == 903);
            var again = Allocate(after, plan, 2);

            Assert.Equal(5, before.Chutes.Count);
            Assert.Equal(new[] { "Mk16 Parachute" }, again.Chutes.Select(c => c.Title));
            var home = Assert.Single(again.Entries);
            Assert.True(home.Return);
            Assert.Equal(new[] { "Mk16 Parachute" }, Titles(home));

            // The pod's chute used too: the return is no longer covered.
            after.Craft.Parts.Single(p => p.Id == PodChute).Chute.State = ReentryChuteState.Deployed;
            Assert.True(Allocate(after, plan, 2).ReturnUncovered);
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

            var allocation = Allocate(Craft(landerChutes: 4), plan);

            var duna = Assert.Single(allocation.Entries);
            Assert.Equal(3, duna.Step);
            Assert.Equal("Touchdown", duna.Sequence);
            // The deorbit step's reentry is the landing's entry.
            Assert.Same(duna, allocation.For(2, "Duna"));
            Assert.Null(allocation.For(2, "Kerbin"));
        }

        [Fact]
        public void TheAddonReadGivesTheEntrysChutesByKosUidOrEveryChuteAboardWithNoEntry()
        {
            var snapshot = Craft(landerChutes: 4);
            snapshot.Orbit.Body = "Duna";
            var allocation = Allocate(snapshot, DunaLandingAndReturn());
            Func<uint, string> uid = id => id == 903 ? null : "uid" + id;

            var set = BlockCalls.ChuteAllocationCall(allocation, 1, snapshot, uid);

            Assert.Equal("", set["error"]);
            Assert.Equal(false, set["all"]);
            Assert.Equal("Land on Duna", set["entry"]);
            Assert.Equal(false, set["return"]);
            Assert.Equal(allocation.Entries[0].Chutes.Where(c => c.PartId != 903).Select(c => (object)("uid" + c.PartId)), (List<object>)set["uids"]);
            Assert.Equal(((List<object>)set["uids"]).Count, ((List<object>)set["titles"]).Count);
            Assert.Equal(false, set["powered_finish"]);
            Assert.Equal(allocation.Entries[0].TouchdownSpeed, (double)set["touchdown_speed"]);

            // No Mission (or no Approval yet): every usable chute aboard.
            var all = BlockCalls.ChuteAllocationCall(null, -1, snapshot, uid);
            Assert.Equal(true, all["all"]);
            Assert.Equal("", all["entry"]);
            Assert.Equal(4, ((List<object>)all["uids"]).Count);
            Assert.Equal(-1.0, all["touchdown_speed"]);
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
