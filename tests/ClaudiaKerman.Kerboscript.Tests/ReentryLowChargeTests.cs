// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #942: low charge doesn't end <c>reentry</c> once the craft is falling into the air. The block's own delegate and <c>claudia_tick</c>'s
    /// abort line are lifted out of the real library and run on kOS.Safe with the game faked; Core's rule is in <c>LowChargeTests</c>.
    /// </summary>
    public class ReentryLowChargeTests
    {
        private static string Body()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            return source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
        }

        private static string Lib()
        {
            return CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source.Replace("\r\n", "\n");
        }

        private static int At(string body, string text)
        {
            var i = body.IndexOf(text, StringComparison.Ordinal);
            Assert.True(i > 0, "not found: " + text);
            return i;
        }

        /// <summary>The delegate as the block sets it, whole.</summary>
        private static string Watch()
        {
            var body = Body();
            var start = At(body, "  set ctx:ec_tick to {\n");
            const string End = "\n  }.\n";
            return body.Substring(start, body.IndexOf(End, start, StringComparison.Ordinal) + End.Length - start);
        }

        /// <summary>The block's state and the game, faked; answers[n] is LOWCHARGE's answer on the nth tick, and the stage-2 group holds d and m, the pod's p stays out of it.</summary>
        private const string Rig =
            "local armed is list().\n"
            + "local notes is list().\n"
            + "local fresh is 0.\n"
            + "local turn is 0.\n"
            + "local answers is list().\n"
            + "function chute {\n parameter uid, name, stage_.\n"
            + "  local m is lexicon(\"hasevent\", { parameter e. return e = \"deploy chute\" and not armed:contains(uid). }).\n"
            + "  set m:doevent to { parameter e. armed:add(uid). }.\n"
            + "  return lexicon(\"uid\", uid, \"name\", name, \"stage\", stage_, \"title\", \"Chute \" + uid, \"hasmodule\", { parameter n. return n = \"ModuleParachute\". }, \"getmodule\", { parameter n. return m. }).\n}\n"
            + "local chosen is lexicon(\"error\", \"\", \"last\", false, \"stages\", list(2), \"uids\", list(\"d\", \"m\"), \"why\", \"\").\n"
            + "global ship is lexicon(\"periapsis\", 15000, \"parts\", list(chute(\"d\", \"parachuteDrogue\", 2), chute(\"m\", \"parachuteSingle\", 2), chute(\"p\", \"parachuteSingle\", 0)),\n"
            + "  \"resources\", list(lexicon(\"name\", \"ElectricCharge\", \"amount\", 1, \"capacity\", 100))).\n"
            + "global rcs is false.\n"
            + "global sas is false.\n"
            + "global addons is lexicon(\"claudia\", lexicon(\n"
            + "  \"lowcharge\", { parameter q. set turn to turn + 1. return answers[turn - 1]. },\n"
            + "  \"chutegroup\", { parameter q. set fresh to fresh + 1. return chosen. },\n"
            + "  \"blocknote\", { parameter b, n. notes:add(n:chutes_used + \"/\" + n:steering_released). })).\n"
            + "local ctx is lexicon(\"block\", \"reentry\").\n"
            + "local opt is lexicon(\"min_ec_pct\", 10).\n"
            + "local body_ is lexicon(\"atm\", lexicon(\"height\", 50000)).\n"
            + "local extra is lexicon(\"chutes_used\", list()).\n"
            + "local ask is lexicon(\"none\", false, \"engine\", false).\n"
            + "local held is true.\n"
            + "local chute_mode is \"safe\".\n"
            + "local braking is false.\n"
            + "local group is lexicon(\"last\", false, \"stages\", list(), \"uids\", list(), \"why\", \"\").\n"
            + "local ec_step is 0.\n";

        private const string Nothing = "lexicon(\"carry_on\", false, \"step\", 0, \"abort_pct\", 55)";
        private const string Release = "lexicon(\"carry_on\", true, \"step\", 1, \"abort_pct\", 100)";
        private const string Arm = "lexicon(\"carry_on\", true, \"step\", 2, \"abort_pct\", 100)";

        private static string[] Ticks(string extraRig, string script, params string[] answers)
        {
            return KosHarness.Run(CoreLibrary.Bundle(Rig + extraRig + "set answers to list(" + string.Join(", ", answers) + ").\n" + Watch() + script)).ToArray();
        }

        [Fact]
        public void Under_the_floor_it_carries_on_lets_go_of_the_steering_and_notes_it_once()
        {
            Assert.Equal(new[] { "100", "100", "False", "/True", "" },
                Ticks("",
                    "print ctx:ec_tick:call(). print ctx:ec_tick:call().\n"
                    + "print held. print notes:join(\";\"). print armed:join(\",\").\n", Release, Release));
        }

        [Fact]
        public void Over_the_floor_nothing_changes_and_nothing_is_noted()
        {
            Assert.Equal(new[] { "55", "True", "0" },
                Ticks("", "print ctx:ec_tick:call(). print held. print notes:length.\n", Nothing));
        }

        [Fact]
        public void Under_2_percent_each_stowed_chute_in_the_group_gets_the_event_once()
        {
            Assert.Equal(new[] { "100", "d,m", "Chute d,Chute m", "1", "d,m" },
                Ticks("",
                    "print ctx:ec_tick:call().\n"
                    + "print armed:join(\",\"). print extra:chutes_used:join(\",\"). print notes:length.\n"
                    + "ctx:ec_tick:call(). print armed:join(\",\").\n", Arm, Arm));
        }

        [Fact]
        public void A_chute_already_armed_or_open_is_not_stowed_so_it_gets_no_event()
        {
            // The drogue was already out: only the main gets the event, once.
            Assert.Equal(new[] { "d,m", "Chute m" },
                Ticks("armed:add(\"d\").\n", "ctx:ec_tick:call(). print armed:join(\",\"). print extra:chutes_used:join(\",\").\n", Arm));
        }

        [Fact]
        public void Before_the_group_is_chosen_the_arming_reads_it_fresh_and_keeps_to_it()
        {
            Assert.Equal(new[] { "1", "d,m" },
                Ticks("", "ctx:ec_tick:call(). print fresh. print armed:join(\",\").\n", Arm));
        }

        [Fact]
        public void Chute_mode_none_arms_nothing()
        {
            Assert.Equal(new[] { "100", "", "" },
                Ticks("set chute_mode to \"none\".\n", "print ctx:ec_tick:call(). print armed:join(\",\"). print extra:chutes_used:join(\",\").\n", Arm));
        }

        [Fact]
        public void A_group_with_no_chutes_arms_nothing()
        {
            // reentry_group_of's fallback for chutes "none" is last true with no uids.
            Assert.Equal(new[] { "" },
                Ticks("set chosen to lexicon(\"error\", \"\", \"last\", true, \"stages\", list(), \"uids\", list(), \"why\", \"\").\n", "ctx:ec_tick:call(). print armed:join(\",\").\n", Arm));
        }

        [Fact]
        public void The_braking_burn_keeps_its_steering_and_the_chutes_are_still_armed()
        {
            Assert.Equal(new[] { "100", "True", "d,m", "Chute d, Chute m/False" },
                Ticks("set braking to true.\n", "print ctx:ec_tick:call(). print held. print armed:join(\",\"). print notes:join(\";\").\n", Arm));
        }

        [Fact]
        public void Charge_coming_back_neither_notes_again_nor_arms_again()
        {
            Assert.Equal(new[] { "d,m", "1" },
                Ticks("", "ctx:ec_tick:call(). ctx:ec_tick:call(). ctx:ec_tick:call(). print armed:join(\",\"). print notes:length.\n", Arm, Release, Arm));
        }

        [Fact]
        public void Armed_before_the_drop_the_group_chosen_after_it_is_armed_too_and_nothing_twice()
        {
            // The drop took d and m's group; the block's choice after the drop (the reset below) is the pod's p.
            Assert.Equal(new[] { "d,m", "d,m,p" },
                Ticks("",
                    "ctx:ec_tick:call(). print armed:join(\",\").\n"
                    + "set group to lexicon(\"error\", \"\", \"last\", false, \"stages\", list(0), \"uids\", list(\"p\"), \"why\", \"\").\n"
                    + "set ec_step to min(ec_step, 1).\n"
                    + "ctx:ec_tick:call(). print armed:join(\",\").\n", Arm, Arm));
            var body = Body();
            Assert.Equal(At(body, "  set group to reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts).\n") + "  set group to reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts).\n".Length,
                At(body, "  set ec_step to min(ec_step, 1)."));
        }

        // ---- Where it is asked ----

        [Fact]
        public void Only_reentry_sets_the_watch_and_after_its_trim_so_a_trim_burn_keeps_its_steering()
        {
            var setters = CoreLibrary.Files.Where(f => f.Source.Contains("set ctx:ec_tick")).Select(f => f.Name).ToArray();
            Assert.Equal(new[] { "reentry" }, setters);
            var body = Body();
            var watch = At(body, "set ctx:ec_tick to {");
            Assert.True(At(body, "claudia_fly_correction(ctx, addons:claudia:entrytrim(") < watch);
            Assert.True(watch < At(body, "claudia_wait_until(ctx, time:seconds + entry_in - below:lead)"), "set before the first wait");
        }

        [Fact]
        public void The_hold_is_not_taken_up_again_after_the_steering_is_released()
        {
            var body = Body();
            // The lock is only for a craft still held; the wait for the air stands either way, so a release during the first wait doesn't start the fall early.
            Assert.Contains("if waited = \"\" { if held { lock steering to srfretrograde. } set waited to choose claudia_warp_to(ctx, air_ut, air_ut - time:seconds)", body);
            Assert.Contains("low:carry_on and not braking { set held to false. unlock steering. sas off. claudia_rcs_off(\"low charge\"). }", body);
            Assert.Equal(2, body.Split("lock steering to").Length - 1);
            // The only other lock is the braking burn's own, which keeps pointing the engine.
            Assert.True(At(body, "if fell = \"\" and braking and not claudia_on_ground() {") < At(body, "lock steering to steer."));
        }

        // ---- claudia_tick's abort line ----

        private const string TickRig =
            "global ship is lexicon(\"parts\", list(), \"resources\", list(lexicon(\"name\", \"ElectricCharge\", \"amount\", 5, \"capacity\", 100))).\n"
            + "local opt is lexicon(\"min_ec_pct\", 10, \"max_duration\", 1000, \"abort_on_part_loss\", true).\n";

        private static string AbortLine()
        {
            var lib = Lib();
            var tick = At(lib, "function claudia_tick {");
            var start = lib.IndexOf("  return claudia_abort_reason(", tick, StringComparison.Ordinal);
            return lib.Substring(start, lib.IndexOf("\n", start, StringComparison.Ordinal) - start) + "\n";
        }

        private static string[] Abort(string ctx)
        {
            return KosHarness.Run(CoreLibrary.Bundle(TickRig
                + "function tick_abort {\n parameter ctx, now.\n" + AbortLine() + "}\n"
                + "print tick_abort(" + ctx + ", 0).\n")).ToArray();
        }

        private const string Ctx = "lexicon(\"opt\", opt, \"start_ut\", 0, \"parts\", {0}{1})";

        [Fact]
        public void Without_a_watch_every_other_block_still_aborts_on_low_charge()
        {
            Assert.Equal(new[] { "abort min_ec_pct: electric charge 5% is below 10%" }, Abort(string.Format(Ctx, 0, "")));
        }

        [Fact]
        public void The_abort_judges_the_reading_the_hook_gives_so_a_real_one_still_ends_the_block()
        {
            Assert.Equal(new[] { "abort min_ec_pct: electric charge 7% is below 10%" }, Abort(string.Format(Ctx, 0, ", \"ec_tick\", { return 7. }")));
        }

        [Fact]
        public void A_hook_that_carries_on_stops_only_the_min_ec_pct_abort()
        {
            Assert.Equal(new[] { "" }, Abort(string.Format(Ctx, 0, ", \"ec_tick\", { return 100. }")));
            Assert.Equal(new[] { "abort abort_on_part_loss: 2 part(s) lost" }, Abort(string.Format(Ctx, 2, ", \"ec_tick\", { return 100. }")));
        }
    }
}
