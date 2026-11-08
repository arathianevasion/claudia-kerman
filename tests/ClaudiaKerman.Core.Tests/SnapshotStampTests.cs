// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The one stamp of a Game Snapshot with the Mission's Time versus delta-v and margin (#739). Planning, Planning Tool and
    /// block-call snapshots all go through it, so a block call's window pick matches plan_transfer's (#733).
    /// </summary>
    public sealed class SnapshotStampTests
    {
        [Fact]
        public void A_Most_efficient_Mission_with_a_non_default_margin_stamps_both_settings()
        {
            var view = new MissionView { Stage = MissionStage.Running, MarginPercent = 25 };
            view.Settings.TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
            var snapshot = new GameSnapshot();

            MissionView.Stamp(snapshot, view);

            Assert.Equal(TimeVersusDeltaV.MostEfficient, snapshot.TimeVersusDeltaV);
            Assert.Equal(25, snapshot.MarginPercent);
        }

        /// <summary>
        /// #832: a block call's snapshot gets the later legs' delta-v as the planning tools' does: the legs after the one in hand (CurrentSequence, the leg counted as begun), the craft's only.
        /// Neither the leg in hand nor one before it counts, nor a Child Vessel's.
        /// </summary>
        [Theory]
        [InlineData(0, 1200)]
        [InlineData(1, 400)]
        [InlineData(2, 400)]
        [InlineData(3, 0)]
        [InlineData(-1, 1200)]
        public void The_later_legs_delta_v_is_the_craft_s_legs_after_the_one_in_hand(int current, double expected)
        {
            var view = new MissionView
            {
                Stage = MissionStage.Running, CurrentSequence = current, CraftName = "Kerbal X",
                PlanLegs = new System.Collections.Generic.List<PlannedSequence>
                {
                    new PlannedSequence { Name = "Launch", DeltaV = 3400 },
                    new PlannedSequence { Name = "Transfer", DeltaV = 800, Vessel = "Kerbal X" },
                    new PlannedSequence { Name = "Probe", DeltaV = 600, Vessel = "Probe" },
                    new PlannedSequence { Name = "Land", DeltaV = 400, Vessel = "craft" },
                },
            };
            // Legs 0 to 3: after leg 0, 800 + 400 (the probe's left out); after leg 1, 400; after leg 2 (the probe), 400; after 3, none.
            var snapshot = MissionView.Stamp(new GameSnapshot(), view);
            Assert.Equal(expected, snapshot.LaterLegsDv);
            Assert.Null(MissionView.Stamp(new GameSnapshot(), new MissionView { Stage = MissionStage.Running }).LaterLegsDv);
        }

        [Fact]
        public void While_a_child_vessel_runs_the_craft_s_later_legs_are_not_taken_from_its_delta_v()
        {
            var view = new MissionView
            {
                Stage = MissionStage.Running, CurrentSequence = 0, CraftName = "Kerbal X", RunningChild = "Capsule",
                PlanLegs = new System.Collections.Generic.List<PlannedSequence>
                {
                    new PlannedSequence { Name = "Deorbit", DeltaV = 150, Vessel = "Capsule" },
                    new PlannedSequence { Name = "Station", DeltaV = 300, Vessel = "Kerbal X" },
                },
            };
            Assert.Equal(0, MissionView.Stamp(new GameSnapshot(), view).LaterLegsDv);
        }

        /// <summary>#898: the planning tool path and Stamp share one rule; a rewrite of the leg in hand (SequenceCounted) counts the next leg, a fresh plan is unchanged.</summary>
        [Theory]
        [InlineData(1, false, 400)] // fresh plan: one leg begun, the leg planned is index 1, later is 400
        [InlineData(1, true, 1200)] // rewrite of leg 0 in hand: leg 1's 800 stays a later leg
        [InlineData(2, true, 400)]
        [InlineData(0, false, 1200)]
        [InlineData(0, true, 1200)]
        public void A_rewrite_of_the_leg_in_hand_counts_the_next_leg_as_later(int stepsBegun, bool counted, double expected)
        {
            var legs = new System.Collections.Generic.List<PlannedSequence>
            {
                new PlannedSequence { Name = "Launch", DeltaV = 3400 },
                new PlannedSequence { Name = "Transfer", DeltaV = 800, Vessel = "Kerbal X" },
                new PlannedSequence { Name = "Probe", DeltaV = 600, Vessel = "Probe" },
                new PlannedSequence { Name = "Land", DeltaV = 400, Vessel = "craft" },
            };
            Assert.Equal(expected, MissionCards.LaterLegsDv(legs, stepsBegun, counted, "Kerbal X", ""));
            Assert.Equal(0, MissionCards.LaterLegsDv(legs, stepsBegun, counted, "Kerbal X", "Probe"));
            // Stamp is the same rule with the leg in hand counted.
            var view = new MissionView { Stage = MissionStage.Running, CurrentSequence = stepsBegun - 1, CraftName = "Kerbal X", PlanLegs = legs };
            Assert.Equal(MissionCards.LaterLegsDv(legs, stepsBegun, true, "Kerbal X", ""), MissionView.Stamp(new GameSnapshot(), view).LaterLegsDv);
        }

        [Fact]
        public void With_no_Mission_the_snapshot_keeps_the_defaults()
        {
            var defaults = new PlayerConfig();
            var snapshot = new GameSnapshot();

            MissionView.Stamp(snapshot, null);
            Assert.Equal(TimeVersusDeltaV.Balanced, snapshot.TimeVersusDeltaV);
            Assert.Equal(defaults.CraftCheckMarginPercent, snapshot.MarginPercent);

            // A view with no Mission (Stage None) is no Mission either: it leaves the defaults alone.
            var idle = new MissionView { Stage = MissionStage.None, MarginPercent = 25 };
            idle.Settings.TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
            MissionView.Stamp(snapshot, idle);
            Assert.Equal(TimeVersusDeltaV.Balanced, snapshot.TimeVersusDeltaV);
            Assert.Equal(defaults.CraftCheckMarginPercent, snapshot.MarginPercent);
        }
    }
}
