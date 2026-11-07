// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#831: a deorbit with no target where the block's auto gives none goes back at plan time, by the one rule the block asks (<see cref="HomeLanding.DefaultTarget"/>).</summary>
    public sealed class DeorbitTargetsTests
    {
        private static readonly SnapshotBody Kerbin = new SnapshotBody { Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, RotationPeriod = 21549.425 };
        private static readonly SnapshotBody Duna = new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667, RotationPeriod = 65517.859 };

        private const string Bare = "local r is deorbit().";
        private const string Auto = "local r is deorbit(lexicon(\"target\", \"auto\")).";

        private static GameSnapshot At(SnapshotBody body, string goal = "")
        {
            var snapshot = new GameSnapshot { Ut = 1000, Situation = "ORBITING", Body = body.Name, MissionGoal = goal };
            snapshot.Bodies.AddRange(new[] { Kerbin, Duna });
            return snapshot;
        }

        [Theory]
        [InlineData(Bare)]
        [InlineData(Auto)]
        public void A_deorbit_at_duna_with_no_target_goes_back(string script)
        {
            Assert.Equal("Duna", DeorbitTargets.Missing(script, At(Duna)));
        }

        [Fact]
        public void A_deorbit_at_kerbin_with_a_goal_auto_can_serve_passes()
        {
            Assert.Null(DeorbitTargets.Missing(Bare, At(Kerbin, "Orbit Kerbin and come home.")));
            Assert.Null(DeorbitTargets.Missing(Auto, At(Kerbin)));
        }

        [Theory]
        [InlineData("Splash down in the ocean.", true)]
        [InlineData("Land at (12.5, -30.1).", true)]
        [InlineData("Land within 50 km of KSC.", false)]
        public void A_goal_naming_a_place_is_what_the_blocks_answer_says(string goal, bool refused)
        {
            var snapshot = At(Kerbin, goal);
            // The block asks LANDINGTARGET with the planet alone; the check must agree with that answer.
            var blockSays = HomeLanding.DefaultTarget(snapshot, new LandingTargetAsk { Planet = "Kerbin" });
            Assert.Equal(refused, blockSays == HomeLanding.None);
            Assert.Equal(refused ? "Kerbin" : null, DeorbitTargets.Missing(Bare, snapshot));
        }

        [Theory]
        [InlineData("local r is deorbit(lexicon(\"target\", \"ksc\")).")]
        [InlineData("local r is deorbit(lexicon(\"target\", latlng(10, 20))).")]
        public void A_deorbit_with_a_target_passes_at_any_body(string script)
        {
            Assert.Null(DeorbitTargets.Missing(script, At(Duna)));
            Assert.Null(DeorbitTargets.Missing(script, At(Kerbin, "Splash down.")));
        }

        [Fact]
        public void A_deorbit_after_a_trip_block_stays_unchecked()
        {
            Assert.Null(DeorbitTargets.Missing("local a is transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 50000)).\n" + Bare, At(Kerbin, "Splash down.")));
            Assert.Null(DeorbitTargets.Missing("local a is return(lexicon()).\n" + Bare, At(Duna)));
            Assert.Null(DeorbitTargets.Missing("local a is coast(lexicon(\"until\", time:seconds + ship:orbit:nextpatcheta)).\n" + Bare, At(Duna)));
        }

        [Fact]
        public void No_snapshot_or_no_script_checks_nothing()
        {
            Assert.Null(DeorbitTargets.Missing(Bare, null));
            Assert.Null(DeorbitTargets.Missing("", At(Duna)));
        }

        [Fact]
        public void A_finding_reads_in_the_correction_and_a_clean_plan_adds_nothing()
        {
            var clean = new PlanFindings();
            Assert.False(clean.FiguresWrong);
            Assert.Equal("", MissionPrompts.PlanCheckFailed(clean));
            var found = new PlanFindings { DeorbitNoTarget = "Duna" };
            Assert.True(found.FiguresWrong);
            Assert.Contains("Deorbit has no target at Duna", MissionPrompts.PlanCheckFailed(found));
            Assert.Contains("deorbit", MissionPrompts.PlanFixNotice(found));
            Assert.Single(found.Warnings());
        }
    }
}
