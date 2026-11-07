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
        [InlineData("local r is deorbit(lexicon(\"target\", \"Auto\")).")]
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

        /// <summary>#852: the next-patch time held in a variable is a trip as the direct form is; the coast's text doesn't say "nextpatch" any more.</summary>
        [Theory]
        [InlineData("local t is time:seconds + ship:orbit:nextpatcheta.\nlocal a is coast(lexicon(\"until\", t + 60)).\n")]
        [InlineData("local patch_wait is ship:orbit:nextpatcheta.\nlocal a is coast(lexicon(\"until\", time:seconds + patch_wait + 60)).\n")]
        // Unread by the plan checks, so nothing says where it ends: assigned twice, from something else, or no until to read at all.
        [InlineData("local t is 0.\nset t to time:seconds + ship:orbit:nextpatcheta.\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("local t is pick().\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("local a is coast().\n")]
        // A written time as far as the name goes, but not the only value it has, nor one that reaches the coast.
        [InlineData("local t is time:seconds + 100.\nset t to t + ship:orbit:nextpatcheta.\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("if b { local t is time:seconds + 100. }\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("if b set t to time:seconds + 100.\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("local a is coast(lexicon(\"until\", t)).\nlocal t is time:seconds + 100.\n")]
        [InlineData("lock t to time:seconds + 100.\nlocal a is coast(lexicon(\"until\", t)).\n")]
        [InlineData("local a is coast(lexicon(\"until\", t + 60)).\n")]
        [InlineData("local a is coast(lexicon(\"until\", time:seconds + e)).\n")]
        [InlineData("local s is \"a\nb\". set t to pick().\nlocal t is time:seconds + 100.\nlocal a is coast(lexicon(\"until\", t)).\n")]
        public void A_deorbit_after_a_coast_it_can_not_place_stays_unchecked(string coast)
        {
            Assert.Null(DeorbitTargets.Missing(coast + Bare, At(Duna)));
        }

        /// <summary>An until with anything in it that isn't a number, the clock or this orbit's own time: what it comes to, nothing here can say.</summary>
        [Theory]
        [InlineData("time:seconds + ship:orbit:nextpatcheta")]
        [InlineData("ship:orbit:nextpatcheta + time:seconds")]
        [InlineData("time:seconds + ship:orbit:nextpatcheta + 3 * ship:orbit:period")]
        [InlineData("time:seconds + ship:orbit:nextpatch:period")]
        [InlineData("time:seconds + ship:orbit:period:x")]
        [InlineData("time:seconds + target:orbit:period")]
        [InlineData("time:seconds + x:ship:orbit:period")]
        [InlineData("time:seconds + ship:orbit:periods")]
        [InlineData("res:encounter_ut + 60")]
        [InlineData("time:seconds + max(1, eta:apoapsis - 120)")]
        [InlineData("time:seconds + 1e3x")]
        [InlineData("time:seconds + 11111111111111111111111111111111111111111111x")]
        [InlineData("\"soon\"")]
        public void A_deorbit_after_a_coast_not_read_as_staying_on_this_orbit_stays_unchecked(string until)
        {
            Assert.Null(DeorbitTargets.Missing("local a is coast(lexicon(\"until\", " + until + ")).\n" + Bare, At(Duna)));
        }

        [Theory]
        [InlineData("time:seconds + 100")]
        [InlineData("1000000")]
        [InlineData(" time : seconds + 100.5 ")]
        // coast.ks's own doc example.
        [InlineData("time:seconds + 3 * ship:orbit:period")]
        [InlineData("100 + time:seconds")]
        [InlineData("time:seconds + 1e3")]
        [InlineData("TIME:SECONDS + 1.5E+3 - (2 * SHIP:ORBIT:PERIOD) / 3")]
        // Recorded: m20261029-231825-28d7, m20261026-043443-4024, m20261027-141128-3c1d.
        [InlineData("time:seconds + ship:orbit:period")]
        [InlineData("time:seconds + eta:periapsis - 600")]
        [InlineData("568693.9 - 6000")]
        public void A_deorbit_after_a_coast_to_a_written_time_is_still_judged(string until)
        {
            Assert.Equal("Duna", DeorbitTargets.Missing("local a is coast(lexicon(\"until\", " + until + ")).\n" + Bare, At(Duna)));
        }

        /// <summary>The same times held in a variable: the one assignment holds a time on this orbit, so the coast stays here.</summary>
        [Theory]
        [InlineData("local t is time:seconds + 3 * ship:orbit:period.", "t")]
        [InlineData("local t is 100 + time:seconds.", "t + 60")]
        [InlineData("set t to time:seconds + 1e3.", "t")]
        [InlineData("local e is 3 * ship:orbit:period.", "time:seconds + e")]
        [InlineData("local e is 100.5.", "time:seconds + e + 60")]
        public void A_deorbit_after_a_coast_to_a_written_time_in_a_variable_is_still_judged(string assignment, string until)
        {
            Assert.Equal("Duna", DeorbitTargets.Missing(assignment + "\nlocal a is coast(lexicon(\"until\", " + until + ")).\n" + Bare, At(Duna)));
            Assert.Equal("Duna", DeorbitTargets.Missing(assignment + "\nlocal a is coast(lexicon(\"until\", " + until + ")).\n" + Auto, At(Duna)));
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
