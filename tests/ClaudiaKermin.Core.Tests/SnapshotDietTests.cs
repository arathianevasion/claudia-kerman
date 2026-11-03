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
    /// The Game Snapshot a turn sends Claudia (#256): the full snapshot, parts and all, on the first turn and
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
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new string[0], false));
            Assert.Contains("\"parts\":[", turn);
        }

        /// <summary>#680 reverses #256's rule for plan turns: under ADR 0012 a revised plan turn is appended to the cached conversation like any other.</summary>
        [Fact]
        public void AMissionPlanTurnWithTheCraftUnchangedLeavesThePartsOutLikeAnyOther()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Revise the Mission Plan.", MunShotAt(2000));
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false);
            Assert.DoesNotContain("\"parts\"", shaped);
            Assert.EndsWith("\nparts: unchanged since the Game Snapshot at UT 1000 (44 parts)", shaped);
        }

        [Fact]
        public void AMissionPlanTurnAfterTheCraftStagedOrDroppedPartsSendsTheFullSnapshot()
        {
            var full = MunShotAt(1000);
            var earlier = Turn("Continue the Mission.", full);
            var staged = Turn("Revise the Mission Plan.", MunShotAt(2000, s => s.Craft.Stage = 2));
            Assert.Equal(staged, SnapshotDiet.Shape(staged, new[] { earlier }, false));
            var dropped = full.Craft.Parts.Skip(40).Select(p => p.Id).ToList();
            var smaller = Turn("Revise the Mission Plan.", MunShotAt(2000, s => s.Craft.Parts.RemoveAll(p => dropped.Contains(p.Id))));
            Assert.EndsWith("\nparts dropped since the Game Snapshot at UT 1000: [" + string.Join(",", dropped) + "]", SnapshotDiet.Shape(smaller, new[] { earlier }, false));
            var docked = Turn("Revise the Mission Plan.", MunShotAt(2000, s => s.Craft.Parts.Add(new SnapshotPart { Id = 9999 })));
            Assert.Equal(docked, SnapshotDiet.Shape(docked, new[] { earlier }, false));
        }

        [Fact]
        public void APlanTurnOnTheSameLegWithANewTargetVesselSendsTheDynamicSnapshotWithIt()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var plan = Turn("The player changed the Target Vessel.", MunShotAt(1010, s => s.Target = new SnapshotTarget { Name = "Tug", Body = "Kerbin" }));
            var shaped = SnapshotDiet.Shape(plan, new[] { debrief }, true);
            Assert.DoesNotContain(SnapshotDiet.AsAbove, shaped);
            Assert.Contains(SnapshotDiet.DynamicHeader, shaped);
            Assert.Contains("\"target\":{\"name\":\"Tug\"", shaped);
        }

        [Fact]
        public void AMissionPlanTurnAfterACompactionDroppedTheFullSnapshotSendsItInFull()
        {
            // What a compaction leaves behind it: the summary block, which holds no Game Snapshot line.
            var turn = Turn("Revise the Mission Plan.", MunShotAt(2000));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { "Summary of the Mission so far." }, false));
        }

        [Fact]
        public void ALaterTurnWithTheCraftUnchangedLeavesThePartsOutAndSaysSince()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000.5));
            var turn = Turn("Continue the Mission.", MunShotAt(2000));
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false);

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
            var shaped = SnapshotDiet.Shape(turn, new[] { full }, false);
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
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false);

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
            Assert.Equal(child, SnapshotDiet.Shape(child, new[] { earlier }, false));
        }

        [Fact]
        public void APartSwappedForAnotherAtTheSameCountSendsTheFullSnapshot()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000, s => s.Craft.Parts[43].Id = 1));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { earlier }, false));
        }

        [Fact]
        public void AChangedStageSendsTheFullSnapshot()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000, s => s.Craft.Stage = 2));
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { earlier }, false));
        }

        [Fact]
        public void TheLastFullSnapshotIsTheOneComparedNotALaterDynamicOne()
        {
            var full = Turn("Continue the Mission.", MunShotAt(1000));
            var dynamicTurn = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(2000)), new[] { full }, false);
            var turn = Turn("Continue the Mission.", MunShotAt(3000));
            Assert.EndsWith("unchanged since the Game Snapshot at UT 1000 (44 parts)", SnapshotDiet.Shape(turn, new[] { full, "Done.", dynamicTurn }, false));
        }

        [Fact]
        public void TheTurnRightAfterADebriefWithNoRunBetweenPointsBackAtItsSnapshot()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(1030));
            Assert.Equal("Continue the Mission.\nUT: 1030 s.\n" + SnapshotDiet.AsAbove, SnapshotDiet.Shape(turn, new[] { debrief }, true));
        }

        /// <summary>Minmus (#599, m20261031-225746-22cc): 3,320 m up and just starting to fall, periapsis 59.6 km under the surface, at apoapsis.</summary>
        private static GameSnapshot MinmusFallAt(double ut, Action<GameSnapshot> change = null)
        {
            return MunShotAt(ut, s =>
            {
                s.Situation = "SUB_ORBITAL";
                s.Body = "Minmus";
                s.Altitude = 3320;
                s.VerticalSpeed = -0.5;
                s.Bodies = new System.Collections.Generic.List<SnapshotBody> { new SnapshotBody { Name = "Minmus", Gm = 1.7658e9, Radius = 60000 } };
                s.Orbit = new SnapshotOrbit
                {
                    Body = "Minmus", Apoapsis = 3320, Periapsis = -59600, SemiMajorAxis = 31860, Eccentricity = 0.98744507219083,
                    MeanAnomalyAtEpoch = Math.PI, Epoch = ut,
                };
                if (change != null) change(s);
            });
        }

        [Fact]
        public void TheTimeToImpactIsTheOrbitsFallToSeaLevelRadius()
        {
            // Hand-computed from apoapsis 63,320 m from the centre, periapsis 400 m: E = acos((a - R) / (a e)), M = E - e sin E,
            // (pi - M) / n with n = sqrt(mu / a^3) gives 122.484 s.
            Assert.Equal(122.5, MinmusFallAt(826833).ImpactInS.Value, 1);
            Assert.Null(MunShotAt(1000).ImpactInS);
            Assert.Null(MinmusFallAt(826833, s => s.Situation = "LANDED").ImpactInS);
            Assert.Null(MinmusFallAt(826833, s => s.Orbit.Periapsis = 20000).ImpactInS);
        }

        [Fact]
        public void AKerbinReentryUnderParachutesIsNotFalling()
        {
            var kerbin = new SnapshotBody { Name = "Kerbin", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1 };
            var reentry = MinmusFallAt(1000, s =>
            {
                s.Body = "Kerbin";
                s.Bodies = new System.Collections.Generic.List<SnapshotBody> { kerbin };
                s.Orbit.Body = "Kerbin";
                s.Orbit.SemiMajorAxis = 3000000;
                s.Orbit.Eccentricity = 0.9;
                s.Altitude = 5000;
                s.Situation = "FLYING";
            });
            Assert.False(reentry.Falling);
        }

        [Fact]
        public void AFallingCraftGetsAFreshSnapshotWithImpactInSNotAsAbove()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MinmusFallAt(826832.7));
            var plan = Turn("The player asks for a corrective sequence.", MinmusFallAt(826858));
            var shaped = SnapshotDiet.Shape(plan, new[] { debrief }, true);
            Assert.DoesNotContain(SnapshotDiet.AsAbove, shaped);
            Assert.Contains(SnapshotDiet.DynamicHeader, shaped);
            Assert.Contains("\"impact_in_s\":", shaped);
        }

        [Fact]
        public void AnOrbitingCraftOnTheSameLegKeepsAsAbove()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(1030));
            var shaped = SnapshotDiet.Shape(turn, new[] { debrief }, true);
            Assert.EndsWith(SnapshotDiet.AsAbove, shaped);
            Assert.DoesNotContain("impact_in_s", turn);
        }

        [Fact]
        public void OneSnapshotPerLegHoldsOnlyWithin60SecondsOfThePreviousSnapshot()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var later = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(1061)), new[] { debrief }, true);
            Assert.Contains("parts: unchanged since", later);
            // A load that went back in time is not the same leg either.
            var earlier = SnapshotDiet.Shape(Turn("Continue the Mission.", MunShotAt(990)), new[] { debrief }, true);
            Assert.Contains("parts: unchanged since", earlier);
        }

        [Fact]
        public void APlanTurnOnTheSameLegPointsBackButAChangedCraftNever()
        {
            var debrief = Turn("The player approved the Sequence Plan.", MunShotAt(1000));
            var plan = Turn("Revise the Mission Plan.", MunShotAt(1010));
            Assert.Equal("Revise the Mission Plan.\nUT: 1010 s.\n" + SnapshotDiet.AsAbove, SnapshotDiet.Shape(plan, new[] { debrief }, true));
            var staged = Turn("Continue the Mission.", MunShotAt(1010, s => s.Craft.Stage = 2));
            Assert.Equal(staged, SnapshotDiet.Shape(staged, new[] { debrief }, true));
        }

        [Fact]
        public void ATurnWithoutAGameSnapshotIsSentAsItIs()
        {
            var turn = "Continue the Mission.\n" + MissionPrompts.Facts(1000, null);
            Assert.Equal(turn, SnapshotDiet.Shape(turn, new[] { Turn("Continue the Mission.", MunShotAt(900)) }, true));
        }

        [Fact]
        public void WhatFollowsTheSnapshotInTheTurnStaysAfterIt()
        {
            var earlier = Turn("Continue the Mission.", MunShotAt(1000));
            var turn = Turn("Continue the Mission.", MunShotAt(2000)) + "\nChild Vessels: none.";
            var shaped = SnapshotDiet.Shape(turn, new[] { earlier }, false);
            Assert.EndsWith("(44 parts)\nChild Vessels: none.", shaped);
        }
    }
}
