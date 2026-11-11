// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #980: after the Duna ascent of m20261110-203402-45d1, stock's stage 4 still read its pre-ascent figures (start 12,490 kg
    /// against a 11,583 kg craft, 1,287 m/s), so the mod read 3,125 m/s left; after a warp stock read 603 m/s, 2,441 left, at the
    /// same mass. Figures copied by hand from that Mission's Game Snapshots.
    /// </summary>
    public sealed class StaleStageReadingTests
    {
        private const double Density = 0.005;

        /// <summary>The craft in Duna orbit right after the ascent, as the mod read it then.</summary>
        private static GameSnapshot AfterAscent()
        {
            var snapshot = new GameSnapshot { Body = "Duna", Situation = "ORBITING", DeltaVSimulator = true };
            snapshot.Craft.MassTonnes = 11.5830717086792;
            snapshot.Stages.Add(new SnapshotStage { Number = 5, DeltaVVac = 0, ThrustVacKn = 180, StartMassT = 11.583124160766602, EndMassT = 11.583124160766602 });
            snapshot.Stages.Add(new SnapshotStage { Number = 4, DeltaVVac = 1287.1795654296875, DeltaVAsl = 1268.8953857421875, ThrustVacKn = 180, StartMassT = 12.489814758300781, EndMassT = 8.4898147583007813 });
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 1837.8304443359375, DeltaVAsl = 1811.7239990234375, ThrustVacKn = 180, StartMassT = 7.0798201560974121, EndMassT = 4.07982063293457 });
            snapshot.Stages.Add(new SnapshotStage { Number = 2, ThrustVacKn = 180, StartMassT = 4.07982063293457, EndMassT = 4.07982063293457 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, StartMassT = 1.8649985790252686, EndMassT = 1.8649985790252686 });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, StartMassT = 1.8649990558624268, EndMassT = 1.8649990558624268 });
            // The engine burns LiquidFuel and Oxidizer; what is left, 421.49 and 515.16 units, split over two tanks here.
            var engine = new SnapshotPart { Id = 1, Stage = 4 };
            engine.EnginePropellants.Add("LiquidFuel");
            engine.EnginePropellants.Add("Oxidizer");
            snapshot.Craft.Parts.Add(engine);
            var tank = new SnapshotPart { Id = 2 };
            tank.Resources.Add(new SnapshotPartResource { Name = "LiquidFuel", Amount = 300, Density = Density });
            tank.Resources.Add(new SnapshotPartResource { Name = "Oxidizer", Amount = 366.6, Density = Density });
            tank.Resources.Add(new SnapshotPartResource { Name = "MonoPropellant", Amount = 104.96, Density = 0.004 });
            snapshot.Craft.Parts.Add(tank);
            var radial = new SnapshotPart { Id = 3 };
            radial.Resources.Add(new SnapshotPartResource { Name = "LiquidFuel", Amount = 121.49354074155235, Density = Density });
            radial.Resources.Add(new SnapshotPartResource { Name = "Oxidizer", Amount = 148.5587385502155, Density = Density });
            radial.Resources.Add(new SnapshotPartResource { Name = "Ablator", Amount = 999.99, Density = 0.001 });
            snapshot.Craft.Parts.Add(radial);
            return snapshot;
        }

        private static double Left(GameSnapshot snapshot)
        {
            return LiveReadings.DeltaVLeftOf(snapshot).Value;
        }

        [Fact]
        public void AStageStartingHeavierThanTheCraftIsStale()
        {
            var snapshot = AfterAscent();
            Assert.Equal(new[] { 4 }, StaleStageReading.StaleStages(snapshot).Select(s => s.Number).ToArray());
        }

        [Fact]
        public void TheStaleStageIsWorkedOutFromThePropellantLeftAndItsOwnIsp()
        {
            var snapshot = AfterAscent();
            Assert.Equal(3125, Left(snapshot), 0);

            StaleStageReading.Freshen(snapshot);

            var stage = snapshot.Stages.Single(s => s.Number == 4);
            // Stock's own reading after the warp: start 10.173 t, 603.09 m/s vacuum, 594.53 at sea level; 2,441 m/s left.
            Assert.InRange(stage.StartMassT, 10.163, 10.183);
            Assert.InRange(stage.DeltaVVac, 598, 608);
            Assert.InRange(stage.DeltaVAsl, 589, 600);
            Assert.InRange(Left(snapshot), 2436, 2446);
            Assert.True(snapshot.StageReadingFreshened);
            // The end mass and the stages stock had right are left alone.
            Assert.Equal(8.4898147583007813, stage.EndMassT);
            Assert.Equal(1837.8304443359375, snapshot.Stages.Single(s => s.Number == 3).DeltaVVac);
        }

        [Fact]
        public void AFreshReadingIsLeftAsItIs()
        {
            var snapshot = AfterAscent();
            var four = snapshot.Stages.Single(s => s.Number == 4);
            four.StartMassT = 10.173076629638672;
            four.DeltaVVac = 603.09295654296875;
            Assert.Empty(StaleStageReading.StaleStages(snapshot));

            StaleStageReading.Freshen(snapshot);

            Assert.Equal(603.09295654296875, four.DeltaVVac);
            Assert.Equal(10.173076629638672, four.StartMassT);
            Assert.False(snapshot.StageReadingFreshened);
        }

        [Fact]
        public void WithNoPartsToWeighTheStaleStageStartsAtMostAtTheCraftsMass()
        {
            var snapshot = AfterAscent();
            snapshot.Craft.Parts.Clear();

            StaleStageReading.Freshen(snapshot);

            var stage = snapshot.Stages.Single(s => s.Number == 4);
            Assert.Equal(snapshot.Craft.MassTonnes, stage.StartMassT, 6);
            Assert.True(stage.DeltaVVac < 1287, "an upper bound below the stale reading");
        }

        [Fact]
        public void WithNoPartsToWeighALowerStaleStageStartsAtMostAtTheEndMassOfTheStageAbove()
        {
            var snapshot = AfterAscent();
            snapshot.Craft.Parts.Clear();
            var three = snapshot.Stages.Single(s => s.Number == 3);
            three.StartMassT = 12.2;

            StaleStageReading.Freshen(snapshot);

            // Stage 4 burns first and is let go at 8.49 t: stage 3 can't start heavier than that.
            Assert.Equal(8.4898147583007813, three.StartMassT, 6);
            Assert.Equal(snapshot.Craft.MassTonnes, snapshot.Stages.Single(s => s.Number == 4).StartMassT, 6);
        }

        [Fact]
        public void PropellantThatDoesNotCoverTheGoodStagesFallsBackToTheCraftsMass()
        {
            var snapshot = AfterAscent();
            snapshot.Craft.Parts.RemoveAt(1);

            StaleStageReading.Freshen(snapshot);

            Assert.Equal(snapshot.Craft.MassTonnes, snapshot.Stages.Single(s => s.Number == 4).StartMassT, 6);
        }

        [Fact]
        public void TheRunsDeltaVUsedIsTheAscentsRealCost()
        {
            // Landed before the ascent, stock's three stages: 803.88 + 1286.97 + 1837.26 m/s.
            var start = 803.879638671875 + 1286.9722900390625 + 1837.2607421875;
            var now = AfterAscent();
            StaleStageReading.Freshen(now);
            Assert.InRange(MissionPrompts.RunDeltaVUsed(start, now).Value, 1480, 1495);
        }
    }
}
