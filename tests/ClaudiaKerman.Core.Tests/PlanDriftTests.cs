// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public sealed class PlanDriftTests
    {
        private static GameSnapshot Orbit()
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "ORBITING", DeltaVSimulator = true };
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 1 });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 2 });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, DeltaVVac = 1000 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, DeltaVVac = 3000 });
            snapshot.Orbit = new SnapshotOrbit { Apoapsis = 100000, Periapsis = 90000 };
            return snapshot;
        }

        private static string Drift(GameSnapshot before, GameSnapshot after)
        {
            return PlanDrift.Exceeded(before, after, 15);
        }

        [Fact]
        public void NothingHasChangedWhenTheSnapshotsAgreeOrThereIsNothingToCompare()
        {
            Assert.Equal("", Drift(Orbit(), Orbit()));
            Assert.Equal("", Drift(null, Orbit()));
            Assert.Equal("", Drift(Orbit(), null));
        }

        [Fact]
        public void ChangesInsideTheToleranceAreNotDrift()
        {
            var now = Orbit();
            now.Stages[1].DeltaVVac = 2700;
            now.Orbit.Apoapsis = 108000;
            Assert.Equal("", Drift(Orbit(), now));
        }

        [Fact]
        public void AddedOrLostPartsABodyOrTakingOffAreDrift()
        {
            var lost = Orbit();
            lost.Craft.Parts.RemoveAt(1);
            Assert.Contains("parts", Drift(Orbit(), lost));

            var added = Orbit();
            added.Craft.Parts.Add(new SnapshotPart { Id = 3 });
            Assert.Contains("parts", Drift(Orbit(), added));

            var elsewhere = Orbit();
            elsewhere.Body = "Mun";
            Assert.Contains("Mun", Drift(Orbit(), elsewhere));

            var landed = Orbit();
            landed.Situation = "LANDED";
            Assert.Contains("LANDED", Drift(Orbit(), landed));
            Assert.Equal("", Drift(landed, Pad(landed)));
        }

        private static GameSnapshot Pad(GameSnapshot like)
        {
            var pad = Orbit();
            pad.Situation = "PRELAUNCH";
            return pad;
        }

        [Fact]
        public void DeltaVBeyondTheToleranceIsDriftAndThePlayersToleranceDecidesHowMuch()
        {
            var now = Orbit();
            now.Stages[1].DeltaVVac = 2000;
            Assert.Contains("delta-v", Drift(Orbit(), now));
            Assert.Equal("", PlanDrift.Exceeded(Orbit(), now, 40));

            // Without the simulator on either side there are no figures to compare.
            var before = Orbit();
            before.DeltaVSimulator = false;
            Assert.Equal("", Drift(before, now));
        }

        /// <summary>#980, in Duna orbit at 11.583 t: stock's stage 4 read 1,287 m/s stale, 603 after a warp with no burn.</summary>
        private static GameSnapshot Duna(double stageFour, bool freshened)
        {
            var snapshot = Orbit();
            snapshot.Craft.MassTonnes = 11.5830717086792;
            snapshot.Stages[0].DeltaVVac = 1837.83;
            snapshot.Stages[1].DeltaVVac = stageFour;
            snapshot.StageReadingFreshened = freshened;
            return snapshot;
        }

        [Fact]
        public void AFreshenedStageReadingOnEitherSideIsNotComparedSoStockCatchingUpIsNotDrift()
        {
            // The mod's own figure for the stale stage (here its craft-mass bound, 1,150 m/s) against stock's 603 later.
            Assert.Equal("", Drift(Duna(1150, true), Duna(603.09, false)));
            Assert.Equal("", Drift(Duna(603.09, false), Duna(1150, true)));
        }

        [Fact]
        public void ADeltaVDropAtTheSameMassWithNothingFreshenedIsDrift()
        {
            // An engine shut down or a tank locked with the plan pending: the mass stays, the delta-v goes.
            Assert.Equal("delta-v changed from 3125 to 1838 m/s", Drift(Duna(1287.18, false), Duna(0, false)));
        }

        [Fact]
        public void ADeltaVDropWithMassGoneIsDrift()
        {
            var after = Duna(603.09, false);
            after.Craft.MassTonnes = 9.9;
            Assert.Equal("delta-v changed from 3125 to 2441 m/s", Drift(Duna(1287.18, false), after));
        }

        [Fact]
        public void AnOrbitThatMovedBeyondTheToleranceIsDriftOnlyWhileStable()
        {
            var raised = Orbit();
            raised.Orbit.Periapsis = 150000;
            Assert.Contains("periapsis", Drift(Orbit(), raised));

            var higher = Orbit();
            higher.Orbit.Apoapsis = 200000;
            Assert.Contains("apoapsis", Drift(Orbit(), higher));

            // A craft still climbing through the air has no stable orbit to compare.
            var before = Orbit();
            before.Situation = "SUB_ORBITAL";
            var after = Orbit();
            after.Situation = "SUB_ORBITAL";
            after.Orbit.Apoapsis = 400000;
            Assert.Equal("", Drift(before, after));
        }

        [Fact]
        public void TheToleranceAndTheManualHoldComeFromThePlayerConfig()
        {
            var config = new PlayerConfig();
            Assert.Equal(15, config.ReplanTolerancePercent);
            Assert.Equal(1.0, config.ManualHoldSeconds);

            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck-drift-" + Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, PlayerConfig.FileName), "replan_tolerance = 30%\nmanual_hold_s = 2.5\n");
                var loaded = PlayerConfig.LoadOrCreate(directory);
                Assert.Equal(30, loaded.ReplanTolerancePercent);
                Assert.Equal(2.5, loaded.ManualHoldSeconds);
            }
            finally
            {
                System.IO.Directory.Delete(directory, true);
            }
        }
    }
}
