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
