// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Game Snapshot a turn sends Claudia (#256): the full snapshot, parts and all, on the first turn, every Mission Plan turn and
    /// any turn whose part count or craft.stage differs from the last full one sent; otherwise everything but craft.parts with a line
    /// saying the parts are unchanged; and one snapshot per leg, so the turn right after a Debrief points back at it.
    /// </summary>
    public sealed class SnapshotDietTests
    {
        /// <summary>The recorded Mun Shot v2 in Kerbin orbit: 44 parts, stage 3.</summary>
        private static GameSnapshot MunShot()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
        }

        private static GameSnapshot MunShotAt(double ut, Action<GameSnapshot> change = null)
        {
            var snapshot = MunShot();
            snapshot.Ut = ut;
            if (change != null) change(snapshot);
            return snapshot;
        }

        private static string Turn(string ask, GameSnapshot snapshot)
        {
            return ask + "\n" + MissionPrompts.Facts(snapshot.Ut, snapshot);
        }

        /// <summary>The mod's own estimate of input (RequestCharge.WorstCase, as MissionPrefixBudgetTests counts): JSON characters / 3, rounded up.</summary>
        private static int EstimatedTokens(string text)
        {
            return (new JsonWriter().String(text).ToString().Length + 2) / 3;
        }

        [Fact]
        public void TheFirstTurnSendsTheFullSnapshot()
        {
            var turn = Turn("Continue the Mission.", MunShotAt(1000));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new string[0], false, false));
            Assert.Contains("\"parts\":[", turn);
        }

        [Fact]
        public void AMissionPlanTurnSendsTheFullSnapshotEvenWithTheCraftUnchanged()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Revise the Mission Plan.", MunShotAt(2000));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { earlier }, true, false));
        }

        [Fact]
        public void ALaterTurnWithTheCraftUnchangedLeavesThePartsOutAndSaysSince()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000.5));
            var turn = Turn("Continue the Mission.", MunShotAt(2000));
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false, false);

            Assert.DoesNotContain("\"parts\"", shaped);
            Assert.StartsWith("Continue the Mission.\nUT: 2000 s.\n", shaped);
            foreach (var kept in new[] { "\"name\":\"Mun Shot v2\"", "\"mass_t\":", "\"stage\":3", "\"seats\":", "\"vessel_id\":", "\"orbit\":", "\"resources\":", "\"stages\":" })
                Assert.Contains(kept, shaped);
            Assert.EndsWith("\nparts: unchanged since the Game Snapshot at UT 1000.5 (44 parts)", shaped);
            // The JSON in it is still a Game Snapshot, at this turn's UT.
            var json = shaped.Split('\n').Single(l => l.StartsWith("{", StringComparison.Ordinal));
            Assert.Equal(2000, GameSnapshot.Parse(json).Ut);
        }

        [Fact]
        public void TheDynamicSnapshotOfARecorded44PartCraftIsUnder1500Tokens()
        {
            var full = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000));
            var shaped = SnapshotDiet.Shape(turn, new[] { full }, false, false);
            Assert.True(EstimatedTokens(shaped) < 1500, "the dynamic snapshot is " + EstimatedTokens(shaped) + " tokens (estimated)");
            // 725 when written; what it saves: the full one is 4,190 by the same estimate.
            Assert.True(EstimatedTokens(turn) > 4000, "the full snapshot is " + EstimatedTokens(turn) + " tokens (estimated)");
        }

        [Fact]
        public void AChangedPartCountSendsTheFullSnapshotNamingThePartsDropped()
        {
            var full = MunShotAt(1000);
            var dropped = full.Craft.Parts.Skip(40).Select(p => p.Id).ToList();
            var earlier = Turn("Continue the Mission.", full);
            var turn = Turn("The player approved the Sequence Plan.", MunShotAt(2000, s => s.Craft.Parts.RemoveAll(p => dropped.Contains(p.Id))));
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false, false);

            Assert.StartsWith(turn, shaped);
            Assert.EndsWith("\nparts dropped since the Game Snapshot at UT 1000: [" + string.Join(",", dropped) + "]", shaped);
        }

        [Fact]
        public void AnotherVesselWithFewerOfTheSamePartsIsNotSaidToHaveDroppedAny()
        {
            var full = MunShotAt(1000);
            var kept = full.Craft.Parts.Take(10).Select(p => p.Id).ToList();
            var earlier = Turn("Continue the Mission.", full);
            var child = Turn("Continue the Mission.", MunShotAt(2000, s =>
            {
                s.Craft.Parts.RemoveAll(p => !kept.Contains(p.Id));
                s.Craft.VesselId = "another-vessel";
            }));
            Assert.Equal(child, SnapshotDiet.Shape(child, new[] { earlier }, false, false));
        }

        [Fact]
        public void APartSwappedForAnotherAtTheSameCountSendsTheFullSnapshot()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000, s => s.Craft.Parts[43].Id = 1));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { earlier }, false, false));
        }

        [Fact]
        public void AChangedStageSendsTheFullSnapshot()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000, s => s.Craft.Stage = 2));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { earlier }, false, false));
        }

        [Fact]
        public void TheLastFullSnapshotIsTheOneComparedNotALaterDynamicOne()
        {
            var full = Turn("Continue the Mission.", MunShotAt(1000));
            var dynamicTurn = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(2000)), new[] { full }, false, false);
            var turn = Turn("Continue the Mission.", MunShotAt(3000));
            Assert.EndsWith("unchanged since the Game Snapshot at UT 1000 (44 parts)", SnapshotDiet.Shape(turn, new[] { full, "Done.", dynamicTurn }, false, false));
        }

        [Fact]
        public void TheTurnRightAfterADebriefWithNoRunBetweenPointsBackAtItsSnapshot()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(1030));
            Assert.Equal("Continue the Mission.\nUT: 1030 s.\n" + SnapshotDiet.AsAbove, SnapshotDiet.Shape(turn, new[] { debrief }, false, true));
        }

        [Fact]
        public void OneSnapshotPerLegHoldsOnlyWithin60SecondsOfThePreviousSnapshot()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var later = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(1061)), new[] { debrief }, false, true);
            Assert.Contains("parts: unchanged since", later);
            // A load that went back in time is not the same leg either.
            var earlier = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(990)), new[] { debrief }, false, true);
            Assert.Contains("parts: unchanged since", earlier);
        }

        [Fact]
        public void APlanTurnOrAChangedCraftIsNeverPointedBack()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var plan = Turn("Revise the Mission Plan.", MunShotAt(1010));
            Assert.Equal(plan, SnapshotDiet.Shape(plan, new[] { debrief }, true, true));
            var staged = Turn("Continue the Mission.", MunShotAt(1010, s => s.Craft.Stage = 2));
            Assert.Equal(staged, SnapshotDiet.Shape(staged, new[] { debrief }, false, true));
        }

        [Fact]
        public void ATurnWithoutAGameSnapshotIsSentAsItIs()
        {
            var turn = "Continue the Mission.\n" + MissionPrompts.Facts(1000, null);
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { Turn("Continue the Mission.", MunShotAt(900)) }, false, true));
        }

        [Fact]
        public void WhatFollowsTheSnapshotInTheTurnStaysAfterIt()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000)) + "\nChild Vessels: none.";
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false, false);
            Assert.EndsWith("(44 parts)\nChild Vessels: none.", shaped);
        }
    }
}
