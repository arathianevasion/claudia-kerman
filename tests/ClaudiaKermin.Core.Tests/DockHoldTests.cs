// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// dock_hold (#456): the bundled program the mod runs on the Target Vessel while dock flies, which targets get it, and the
    /// mod's coordination of the two runs (<see cref="DockHolds"/>) for every way they end.
    /// </summary>
    public class DockHoldTests
    {
        private const uint Craft = 11;
        private const uint TargetProcessor = 22;
        private const int Run = -5;

        [Fact]
        public void The_program_takes_the_two_port_uids_and_nothing_else()
        {
            var program = DockHold.Program("4012345", "987");
            Assert.Contains("local dh_port_uid is \"4012345\".", program);
            Assert.Contains("local dh_toward_uid is \"987\".", program);
            Assert.DoesNotContain("%PORT%", program);
            Assert.Throws<ArgumentException>(() => DockHold.Program("12\". sendmessage", "1"));
            Assert.Throws<ArgumentException>(() => DockHold.Program("1", ""));
        }

        [Fact]
        public void The_program_never_messages_or_touches_files()
        {
            var program = DockHold.Program("1", "2").ToLowerInvariant();
            foreach (var word in new[] { "sendmessage", "connection", "copypath", "deletepath", "writejson", "kuniverse", "doevent", "doaction" })
                Assert.DoesNotContain(word, program);
        }

        [Fact]
        public void The_program_holds_a_fixed_aim_and_re_aims_only_past_15_degrees()
        {
            // #706: steering to the craft's port each tick chased the craft's own turns into a limit cycle. The harness has no
            // vectors, so the rule is pinned by source: steering locks to a held direction, set at the start and again only
            // when the craft is over 15 degrees off it with the ports over 10 m apart.
            var program = DockHold.Program("1", "2").Replace("\r\n", "\n");
            Assert.Contains("local dh_aim is dh_toward:nodeposition - dh_port:nodeposition.\n", program);
            Assert.Contains("lock steering to lookdirup(dh_aim, dh_up).\n", program);
            Assert.Contains("if vang(dh_now, dh_aim) > 15 and dh_now:mag > 10 {\n      set dh_aim to dh_now.\n", program);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, "set dh_aim to "));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"\block steering to"));
        }

        [Theory]
        [InlineData(true, true, true, true, "")]
        [InlineData(false, true, true, true, "the target is not loaded")]
        [InlineData(true, false, true, true, "the target has no kOS processor")]
        [InlineData(true, true, false, true, "the target has no electric charge")]
        [InlineData(true, true, true, false, "the target has no link to Claudia")]
        public void Only_a_loaded_powered_linked_target_with_a_processor_gets_a_hold(bool loaded, bool processor, bool charged, bool linked, string why)
        {
            Assert.Equal(why, DockHold.PassiveReason(loaded, processor, charged, linked));
        }

        private static DockHolds Running()
        {
            var holds = new DockHolds();
            uint oldPart;
            int oldRun;
            Assert.False(holds.Starting(Craft, TargetProcessor, Run, out oldPart, out oldRun));
            Assert.Equal(DockHoldState.Starting, holds.State(Craft, out _));
            holds.Started(TargetProcessor, Run);
            Assert.Equal(DockHoldState.Active, holds.State(Craft, out _));
            return holds;
        }

        private static RunOutcome Outcome(RunOutcomeKind kind, string reason = "", string report = null, bool aborted = false, string error = "")
        {
            var lexicon = report == null ? null : new Dictionary<string, object> { { "success", report == "docked" }, { "reason", report } };
            return new RunOutcome(kind, lexicon, error, reason, aborted);
        }

        [Fact]
        public void Dock_returning_stops_the_hold()
        {
            var holds = Running();
            uint part;
            int run;
            Assert.True(holds.Stop(Craft, out part, out run));
            Assert.Equal(TargetProcessor, part);
            Assert.Equal(Run, run);
            Assert.Equal(DockHoldState.None, holds.State(Craft, out _));
            // The stopped run's outcome comes after: it no longer counts.
            holds.Ended(TargetProcessor, Run, Outcome(RunOutcomeKind.Aborted, "dock ended", aborted: true));
            Assert.Equal(DockHoldState.None, holds.State(Craft, out _));
        }

        [Fact]
        public void The_craft_s_run_ending_without_a_dock_result_stops_the_hold_too()
        {
            // The mod calls Stop for the craft's processor when its run ends, whatever ended it.
            var holds = Running();
            uint part;
            int run;
            Assert.True(holds.Stop(Craft, out part, out run));
            Assert.False(holds.Stop(Craft, out part, out run));
        }

        [Fact]
        public void The_program_asks_DockGuidance_for_its_level_and_reports_each_change()
        {
            // #722: the level rule (bands, 2 s to rise, 1 s to fall) is DockGuidance.HoldLevel's; the program reads, steers and reports.
            var program = DockHold.Program("1", "2").Replace("\r\n", "\n");
            Assert.Contains("set dh_s to addons:claudia:dockholdlevel(dh_s).\n", program);
            Assert.Contains("if dh_s[\"changed\"] {\n      addons:claudia:dockholdsettled(dh_s[\"level\"]).", program);
            Assert.Contains("set dh_s[\"reaim\"] to true.", program);
            Assert.DoesNotContain("dh_since", program);
        }

        [Fact]
        public void A_running_hold_takes_a_loose_settle_and_then_a_strict_one()
        {
            var holds = Running();
            holds.Settled(TargetProcessor, Run, "near");
            Assert.Equal(DockHoldState.Near, holds.State(Craft, out _));
            Assert.Single(holds.Live());
            holds.Settled(TargetProcessor, Run, "settled");
            Assert.Equal(DockHoldState.Settled, holds.State(Craft, out _));
            holds.Settled(TargetProcessor, Run, "");
            Assert.Equal(DockHoldState.Active, holds.State(Craft, out _));
        }

        [Fact]
        public void A_running_hold_moves_between_active_and_settled_as_it_reports()
        {
            var holds = Running();
            holds.Settled(TargetProcessor, Run, "settled");
            Assert.Equal(DockHoldState.Settled, holds.State(Craft, out _));
            Assert.Single(holds.Live());
            holds.Settled(TargetProcessor, Run, "");
            Assert.Equal(DockHoldState.Active, holds.State(Craft, out _));
            holds.Settled(TargetProcessor, Run, "settled");
            // A settled hold still ends docked or faults like an active one.
            holds.Ended(TargetProcessor, Run, Outcome(RunOutcomeKind.Finished, report: "docked"));
            Assert.Equal(DockHoldState.Docked, holds.State(Craft, out _));
            // A report from another run, or after the hold ended, changes nothing.
            holds.Settled(TargetProcessor, Run, "");
            Assert.Equal(DockHoldState.Docked, holds.State(Craft, out _));
            var other = Running();
            Assert.False(other.Settled(TargetProcessor, Run - 1, "settled"));
            Assert.False(other.Settled(Craft, Run, "settled"));
            Assert.Equal(DockHoldState.Active, other.State(Craft, out _));
            other.Settled(TargetProcessor, Run, "settled");
            other.Faulted(TargetProcessor, Run, "its kOS processor lost power");
            Assert.Equal(DockHoldState.Fault, other.State(Craft, out _));
        }

        [Fact]
        public void A_hold_that_saw_its_port_caught_is_docked_not_a_fault()
        {
            var holds = Running();
            holds.Ended(TargetProcessor, Run, Outcome(RunOutcomeKind.Finished, report: "docked"));
            Assert.Equal(DockHoldState.Docked, holds.State(Craft, out _));
            uint part;
            int run;
            Assert.False(holds.Stop(Craft, out part, out run));
        }

        [Fact]
        public void A_gate_trip_is_a_fault_whatever_the_outcome_says_after()
        {
            var holds = Running();
            holds.Faulted(TargetProcessor, Run, "blocked: DOEVENT on another vessel");
            holds.Ended(TargetProcessor, Run, Outcome(RunOutcomeKind.Aborted, "blocked: DOEVENT on another vessel", aborted: true));
            string why;
            Assert.Equal(DockHoldState.Fault, holds.State(Craft, out why));
            Assert.Equal("blocked: DOEVENT on another vessel", why);
        }

        [Fact]
        public void Lost_power_is_a_fault_and_the_run_is_still_stopped()
        {
            var holds = Running();
            holds.Faulted(TargetProcessor, Run, "its processor lost power");
            Assert.Equal(DockHoldState.Fault, holds.State(Craft, out _));
            // No longer checked for power each frame, but still stopped when dock returns.
            Assert.Empty(holds.Live());
            uint part;
            int run;
            Assert.True(holds.Stop(Craft, out part, out run));
        }

        [Theory]
        [InlineData(RunOutcomeKind.NoReport, "", null, "it failed: Undefined Variable Name 'x'")]
        [InlineData(RunOutcomeKind.Interrupted, "the kOS processor was lost", null, "the kOS processor was lost")]
        [InlineData(RunOutcomeKind.StoppedByPlayer, "", null, "the player stopped it")]
        [InlineData(RunOutcomeKind.Finished, "", "its port is gone", "it ended: its port is gone")]
        public void A_hold_ending_on_its_own_while_dock_flies_is_a_fault(RunOutcomeKind kind, string reason, string report, string why)
        {
            var holds = Running();
            holds.Ended(TargetProcessor, Run, Outcome(kind, reason, report, error: kind == RunOutcomeKind.NoReport ? "dock_hold\nUndefined Variable Name 'x'\n" : ""));
            string got;
            Assert.Equal(DockHoldState.Fault, holds.State(Craft, out got));
            Assert.Equal(why, got);
        }

        [Fact]
        public void A_refused_start_leaves_the_target_passive()
        {
            var holds = new DockHolds();
            uint part;
            int run;
            holds.Starting(Craft, TargetProcessor, Run, out part, out run);
            holds.Refused(TargetProcessor, Run, "The kOS processor is busy running another program");
            string why;
            Assert.Equal(DockHoldState.Passive, holds.State(Craft, out why));
            Assert.Equal("dock_hold didn't start: The kOS processor is busy running another program", why);
            Assert.False(holds.Stop(Craft, out part, out run));
        }

        [Fact]
        public void A_passive_target_has_no_run_to_stop()
        {
            var holds = new DockHolds();
            uint part;
            int run;
            holds.Passive(Craft, "the target has no kOS processor", out part, out run);
            string why;
            Assert.Equal(DockHoldState.Passive, holds.State(Craft, out why));
            Assert.Equal("the target has no kOS processor", why);
            Assert.Empty(holds.Live());
            Assert.False(holds.Stop(Craft, out part, out run));
        }

        [Fact]
        public void A_second_dock_stops_the_first_hold()
        {
            var holds = Running();
            uint part;
            int run;
            Assert.True(holds.Starting(Craft, TargetProcessor, Run - 1, out part, out run));
            Assert.Equal(Run, run);
            // The old run's end doesn't touch the new hold.
            holds.Ended(TargetProcessor, Run, Outcome(RunOutcomeKind.Aborted, "replaced", aborted: true));
            Assert.Equal(DockHoldState.Starting, holds.State(Craft, out _));
            Assert.True(holds.IsHold(TargetProcessor, Run - 1));
            Assert.False(holds.IsHold(TargetProcessor, Run));
        }
    }
}
