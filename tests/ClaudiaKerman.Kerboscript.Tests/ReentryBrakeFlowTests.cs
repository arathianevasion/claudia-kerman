// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.KOS;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #954: chutes first, engines last, on a powered finish: the gimbals are locked only while the chutes are fully open and given back on every exit, no chute opens
    /// under a burn that began with them all out, and the aim under open chutes. The block's own lines are lifted and run on kOS.Safe with the game faked; Core's rules are in
    /// <c>ReentryBrakeTests</c>.
    /// </summary>
    public class ReentryBrakeFlowTests
    {
        public ReentryBrakeFlowTests()
        {
            KosHarness.Register(typeof(ReentryBrakeRigFunctions));
        }

        private static string Body()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            return source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
        }

        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        // ---- Gimbals ----

        /// <summary>Fake engines: uid, whether it has a gimbal, whether that is locked already. Prints each engine's lock.</summary>
        private const string Engines =
            "function eng {\n parameter uid, has, locked_.\n"
            + "  return lexicon(\"uid\", uid, \"hasgimbal\", has, \"gimbal\", lexicon(\"lock\", locked_)).\n}\n"
            + "local a is eng(\"a\", true, false).\n"
            + "local dart is eng(\"dart\", false, false).\n"
            + "local manual is eng(\"manual\", true, true).\n"
            + "local engs is list(a, dart, manual).\n"
            + "local locked is list().\n"
            + "function show { print a:gimbal:lock + \",\" + manual:gimbal:lock + \",\" + locked:join(\"/\"). }\n";

        [Fact]
        public void The_gimbals_lock_and_only_those_it_locked_come_back()
        {
            Assert.Equal(new[] { "True,True,a", "False,True,a" },
                Run(Engines + "reentry_gimbals(engs, true, locked). show().\nreentry_gimbals(engs, false, locked). show().\n"));
        }

        [Fact]
        public void An_engine_gone_is_skipped_and_a_lock_with_none_recorded_frees_nothing()
        {
            Assert.Equal(new[] { "True,True,a", "True,True,a" },
                Run(Engines + "reentry_gimbals(engs, true, locked). show().\nengs:remove(0). reentry_gimbals(engs, false, locked). show().\n"));
            Assert.Equal(new[] { "False,True," }, Run(Engines + "reentry_gimbals(engs, false, locked). show().\n"));
        }

        // ---- Every exit gives the gimbals back ----

        private const string Restore = "  reentry_gimbals(ship:engines, false, gimbals).\n";

        /// <summary>
        /// One line gives the gimbals back, reached by every way out of the fall: the braking loop and the wait both end before it, no <c>return</c> stands between them and it, and the
        /// landing, the hard touchdown, the abort and every other failure (<c>fell</c>) all come after it.
        /// </summary>
        [Fact]
        public void One_line_gives_the_gimbals_back_before_every_exit()
        {
            var body = Body();
            Assert.Equal(1, body.Split("reentry_gimbals(ship:engines, false").Length - 1);
            var fell = body.IndexOf("local fell is claudia_warp_to(", StringComparison.Ordinal);
            var loop = body.IndexOf("if fell = \"\" and braking and not claudia_on_ground() {", StringComparison.Ordinal);
            var restore = body.IndexOf(Restore, StringComparison.Ordinal);
            var abort = body.IndexOf("if fell <> \"\" {\n    for t in reentry_abort(", StringComparison.Ordinal);
            var landing = body.IndexOf("return claudia_finish(ctx, true, \"\", extra).", StringComparison.Ordinal);
            var hard = body.IndexOf("return claudia_finish(ctx, false, \"touchdown at \"", StringComparison.Ordinal);
            var failure = body.IndexOf("return claudia_finish(ctx, false, fell, extra).", StringComparison.Ordinal);
            Assert.True(fell > 0 && loop > fell && restore > loop && abort > restore && failure > abort && hard > failure && landing > hard, "order of the exits");
            Assert.DoesNotContain("return ", body.Substring(fell, restore - fell));
            // Locking is only in the two loops' tick: the closure, set up before the fall.
            Assert.True(body.IndexOf("reentry_gimbals(ship:engines, b:open, gimbals)", StringComparison.Ordinal) < fell);
        }

        /// <summary>The block's own lines from the restore to the end of the abort branch, run: <c>out</c> says which branch; the gimbals are free when the abort (and <c>claudia_finish</c>) run.</summary>
        private const string ExitRig =
            "global notes is list().\n"
            + "global a is lexicon(\"uid\", \"a\", \"hasgimbal\", true, \"gimbal\", lexicon(\"lock\", true)).\n"
            + "global ship is lexicon(\"engines\", list(a), \"parts\", list()).\n"
            + "global gimbals is list(\"a\").\n"
            + "global chute_mode is \"safe\".\n"
            + "global group is lexicon().\n"
            + "global ask is lexicon().\n"
            + "global extra is lexicon(\"chutes_used\", list(), \"max_temp_pct\", 0, \"max_g\", 0).\n"
            + "global ctx is lexicon(\"block\", \"reentry\").\n"
            + "global addons is lexicon(\"claudia\", lexicon(\"hottestpart\", 0)).\n"
            + "function reentry_fresh_group { parameter g, a_, u. return g. }\n"
            + "function reentry_abort { parameter m, g. notes:add(\"abort,locked=\" + a:gimbal:lock). return list(). }\n"
            + "function reentry_stowed_titles { parameter p. return list(). }\n"
            + "function reentry_geo { return lexicon(). }\n"
            + "function claudia_finish { parameter c, ok, reason, ex. notes:add(\"finish,locked=\" + a:gimbal:lock + \",\" + reason). return reason. }\n";

        /// <summary>One of the library's functions, lifted: the rigs fake the rest of the block.</summary>
        private static string Function(string name)
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var start = source.IndexOf("function " + name + " {", StringComparison.Ordinal);
            return source.Substring(start, source.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3 - start);
        }

        private static string ExitSlice()
        {
            var body = Body();
            var start = body.IndexOf(Restore, StringComparison.Ordinal);
            const string End = "    return claudia_finish(ctx, false, fell, extra).\n  }\n";
            var end = body.IndexOf(End, start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > 0, "the exit slice");
            return body.Substring(start, end + End.Length - start);
        }

        [Theory]
        [InlineData("abort min_ec_pct: electric charge 1% is below 10%")]
        [InlineData("skipped out of the atmosphere 3 times")]
        public void A_failure_or_an_abort_gives_the_gimbals_back_before_the_block_ends(string fell)
        {
            var script = ExitRig + Function("reentry_gimbals") + "function exit_ {\n parameter fell.\n" + ExitSlice() + " return \"went on\".\n}\n"
                + "local out is exit_(\"" + fell + "\").\nprint notes:join(\";\"). print out.\n";
            Assert.Equal(new[] { "abort,locked=False;finish,locked=False," + fell, fell }, KosHarness.Run(script).ToArray());
        }

        [Fact]
        public void A_landing_gives_the_gimbals_back_before_it_goes_on_to_the_end()
        {
            var script = ExitRig + Function("reentry_gimbals") + "function exit_ {\n parameter fell.\n" + ExitSlice() + " return \"landed,locked=\" + a:gimbal:lock.\n}\n"
                + "print exit_(\"\").\n";
            Assert.Equal(new[] { "landed,locked=False" }, KosHarness.Run(script).ToArray());
        }

        // ---- No chute opens under a burn that began with them all out ----

        private static string ChuteLine()
        {
            var body = Body();
            var loop = body.IndexOf("until claudia_on_ground() {", StringComparison.Ordinal);
            const string Guard = "if brake_why <> \"chutes open\" { chute_tick(";
            var start = body.IndexOf(Guard, loop, StringComparison.Ordinal);
            Assert.True(start > 0, "the guarded chute_tick in the braking loop");
            return body.Substring(start, body.IndexOf('\n', start) - start) + "\n";
        }

        [Theory]
        [InlineData("chutes open", 0)]
        [InlineData("last resort", 1)]
        [InlineData("radar", 1)]
        public void On_the_normal_path_chute_tick_is_not_called_while_braking(string why, int calls)
        {
            var body = Body();
            var loop = body.IndexOf("until claudia_on_ground() {", StringComparison.Ordinal);
            var end = body.IndexOf("    set tset to 0.\n    set extra:powered_dv", StringComparison.Ordinal);
            Assert.Equal(1, body.Substring(loop, end - loop).Split("chute_tick(").Length - 1);
            var script = "global last_speed is 10. global agl is 30.\n"
                + "global body_ is lexicon(\"atm\", lexicon(\"altitudepressure\", { parameter a. return 0.05. })).\n"
                + "global ship is lexicon(\"altitude\", 30).\nglobal calls is 0.\nlocal chute_tick is { parameter s, a, p. set calls to calls + 1. }.\n"
                + "local brake_why is \"" + why + "\".\n" + ChuteLine() + "print calls.\n";
            Assert.Equal(new[] { calls.ToString() }, KosHarness.Run(script).ToArray());
        }

        // ---- The aim under open chutes ----

        private const string Aim =
            "function vec { parameter x, y, z. return lexicon(\"x\", x, \"y\", y, \"z\", z, \"mag\", sqrt(x^2 + y^2 + z^2)). }\n"
            + "function vdot { parameter a, b. return a:x*b:x + a:y*b:y + a:z*b:z. }\n"
            + "local up_v is vec(0, 0, 1).\n"
            + "function aim_of { parameter retro. local a is reentry_chute_aim(retro, up_v). print a:x + \",\" + a:y + \",\" + a:z. }\n";

        [Theory]
        // retro is the velocity reversed: falling, its up component is positive.
        [InlineData(0, 3, 12, "0,3,12")] // falling at 12: retrograde, drift included
        [InlineData(4, 0, 1, "4,0,1")] // over 3 m/s: still retrograde however flat
        [InlineData(0, 2, 2, "0,0,1")] // under 3 m/s: straight up
        [InlineData(0, 0, -5, "0,0,1")] // climbing: retrograde would point down, so up
        public void Under_open_chutes_it_holds_retrograde_until_slow_then_straight_up_with_no_drift_term(double x, double y, double z, string expected)
        {
            Assert.Equal(new[] { expected }, Run(Aim + "aim_of(vec(" + x + ", " + y + ", " + z + ")).\n"));
        }

        [Fact]
        public void A_powered_finish_keeps_its_hold_under_chutes_on_the_wheels_and_a_chute_only_finish_still_lets_go()
        {
            var body = Body();
            Assert.Contains("if powered and held {\n        if mains_out { lock steering to reentry_chute_aim(-ship:velocity:surface, ship:up:vector). }\n      } else if not braking {\n        unlock steering.", body);
            Assert.Contains("    if powered and held and (drogues_out or mains_out) { set ctx:rcs_braking to true. }", body); // #963: also after a switch to the engines
            // #942's release still wins: it clears held, so no later mains-out takes the steering back.
            Assert.Contains("low:carry_on and not braking { set held to false. unlock steering.", body);
        }

        [Fact]
        public void The_throttle_goes_through_Cores_guard_every_tick_of_every_powered_finish()
        {
            var body = Body();
            Assert.Contains("set tset to brake_ask(reentry_brake_throttle(", body);
            Assert.Contains("):throttle.", body);
            // Only the guard's answer is flown.
            Assert.Equal(2, body.Split("set tset to").Length - 1);
        }

        [Fact]
        public void The_block_hands_Core_the_speed_the_calls_and_the_rate_or_its_rules_go_blind()
        {
            var body = Body();
            var ask = body.Substring(body.IndexOf("addons:claudia:braketick(", StringComparison.Ordinal));
            ask = ask.Substring(0, ask.IndexOf('\n'));
            Assert.Contains("\"speed\", ship:velocity:surface:mag", ask);
            // Core samples the slowing from the speed: the block's own figure stops at the burn.
            Assert.DoesNotContain("\"decel\"", ask);
            Assert.Contains("\"drogues_called\", drogues_out", ask);
            Assert.Contains("\"mains_called\", mains_out", ask);
            Assert.Contains("\"rate_deg\", vxcl(ship:facing:forevector, ship:angularvel):mag * constant:radtodeg", ask);
            Assert.Contains("\"brake_why\", brake_why", ask);
            // #969: the final height, worked out in Core every tick with no drag, and how far off upright the craft is for the cut.
            Assert.Contains("\"up_deg\", vang(ship:facing:forevector, ship:up:vector)", ask);
            Assert.Contains("\"v_down\", -ship:verticalspeed", ask);
            Assert.Contains("\"a_up\", reentry_up_accel(ship:facing:forevector)", ask);
            Assert.Contains("\"soft\", reentry_powered_soft(), \"end\", reentry_powered_end()", ask);
            Assert.Contains("\"why\", b:cut_why", CutLines());
        }

        [Fact]
        public void A_chute_called_in_the_braking_loop_is_called_before_Cores_word_so_the_thrust_is_zero_that_tick()
        {
            var body = Body();
            var loop = body.IndexOf("until claudia_on_ground() {", StringComparison.Ordinal);
            var call = body.IndexOf("if brake_why <> \"chutes open\" { chute_tick(", loop, StringComparison.Ordinal);
            var word = body.IndexOf("set tset to brake_ask(", loop, StringComparison.Ordinal);
            Assert.True(call > 0 && word > call, "chute_tick before brake_ask");
        }

        [Fact]
        public void The_trace_logs_the_rate_and_why_the_thrust_is_zero_once_a_second_while_braking()
        {
            var body = Body();
            Assert.Contains("if b:trace { addons:claudia:blocktrace(", body);
            Assert.Contains("\"rate_deg\", round(b:rate_deg, 1), \"zero_why\", b:zero_why", body);
        }

        // ---- The spin guard is tipping only: roll about the thrust axis isn't counted (player, 2026-11-09) ----

        /// <summary>The block's own rate measure, lifted from its ask, on a fake craft: Core's throttle and why.</summary>
        private static string[] ThrustOn(double wx, double wy, double wz)
        {
            var body = Body();
            var ask = body.Substring(body.IndexOf("addons:claudia:braketick(", StringComparison.Ordinal));
            const string Key = "\"rate_deg\", ";
            var start = ask.IndexOf(Key, StringComparison.Ordinal) + Key.Length;
            var measure = ask.Substring(start, ask.IndexOf(", \"drogues_called\"", StringComparison.Ordinal) - start).Replace("constant:radtodeg", "radtodeg");
            var script = "function vec { parameter x, y, z. return lexicon(\"x\", x, \"y\", y, \"z\", z, \"mag\", sqrt(x^2 + y^2 + z^2)). }\n"
                + "function vxcl { parameter on, v. local d is on:x*v:x + on:y*v:y + on:z*v:z. return vec(v:x - on:x*d, v:y - on:y*d, v:z - on:z*d). }\n"
                + "global radtodeg is 57.29577951.\n"
                + "global ship is lexicon(\"facing\", lexicon(\"forevector\", vec(0, 0, 1)), \"angularvel\", vec(" + wx + ", " + wy + ", " + wz + ")).\n"
                + "local tick is test_brake_tick(lexicon(\"now\", 100, \"throttle\", 0.8, \"off_deg\", 1, \"rate_deg\", " + measure + ")).\n"
                + "print tick:throttle + \",\" + tick:zero_why.\n";
            return KosHarness.Run(script).ToArray();
        }

        private const double Rad25 = 25 / 57.29577951;

        [Fact]
        public void A_pointed_craft_rolling_at_25_degrees_a_second_gets_thrust()
        {
            Assert.Equal(new[] { "0.8," }, ThrustOn(0, 0, Rad25));
        }

        [Theory]
        [InlineData(Rad25, 0)]
        [InlineData(0, Rad25)]
        public void A_craft_tipping_at_25_degrees_a_second_gets_none(double wx, double wy)
        {
            Assert.Equal(new[] { "0,spinning" }, ThrustOn(wx, wy, 0));
        }

        // ---- Chute states the addon can't read: cut what is out and land on the engine ----

        private static string CutLines()
        {
            var body = Body();
            var start = body.IndexOf("    if b:cut {\n", StringComparison.Ordinal);
            Assert.True(start > 0, "the block's cut");
            const string End = "\n    }\n";
            return body.Substring(start, body.IndexOf(End, start, StringComparison.Ordinal) + End.Length - start);
        }

        /// <summary>The block's cut, run with Core's real answers over a read that keeps failing, then the block's real test of whether a chute is due.</summary>
        private static string CutRig(string ticks)
        {
            return "global cuts is 0.\nglobal notes is list().\nlocal chute_mode is \"safe\".\n"
                + "global ship is lexicon(\"parts\", list()).\nglobal ctx is lexicon(\"block\", \"reentry\").\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"blocknote\", { parameter a_, n. notes:add(n:phase + \":\" + n:why). })).\n"
                + "function reentry_cut_chutes { parameter parts, last. set cuts to cuts + 1. return list(\"Mk16 last=\" + last). }\n"
                + Function("reentry_chute_stage")
                + "local unread_ut is -1. local sample_ut is -1.\n"
                + "local tick_at is {\n parameter now.\n"
                + " local b is test_brake_tick(lexicon(\"now\", now, \"chutes_unread\", true, \"uids\", choose list(\"a\") if chute_mode <> \"none\" else list(), \"unread_ut\", unread_ut, \"sample_ut\", sample_ut, \"speed\", 30, \"radar\", 100, \"brake_h\", 200, \"last_h\", 50, \"shield_speed\", 400, \"shield_agl\", 3000, \"throttle\", 0.8, \"off_deg\", 1, \"rate_deg\", 1)).\n"
                + " set unread_ut to b:unread_ut. set sample_ut to b:sample_ut.\n"
                + CutLines()
                + " return b.\n}.\n"
                + ticks;
        }

        [Fact]
        public void Failing_on_for_a_second_the_cut_fires_once_no_chute_is_called_after_and_the_engine_proceeds()
        {
            var script = CutRig("for t in list(100, 100.5, 101, 101.02, 101.04) { tick_at(t). }\n"
                + "local last_ is tick_at(101.1).\n"
                + "print cuts. print notes:join(\";\"). print chute_mode.\n"
                + "print reentry_chute_stage(chute_mode, true, false, false, 100, 100, 0.5).\n"
                + "print last_:start + \",\" + last_:throttle.\n");
            Assert.Equal(new[] { "1", "chutes_cut:unreadable", "none", "wait", "True,0.8" }, KosHarness.Run(script).ToArray());
        }

        /// <summary>#969: the same lines on Core's real answer at the final height after a last resort, upright under chutes fully open and settled: one cut, noted as the final burn, and no chute called after it.</summary>
        [Fact]
        public void At_the_final_height_the_block_cuts_the_chutes_for_the_final_burn_and_calls_none_after()
        {
            var script = "global cuts is 0.\nglobal notes is list().\nlocal chute_mode is \"safe\".\n"
                + "global ship is lexicon(\"parts\", list()).\nglobal ctx is lexicon(\"block\", \"reentry\").\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"blocknote\", { parameter a_, n. notes:add(n:phase + \":\" + n:why). })).\n"
                + "function reentry_cut_chutes { parameter parts, last. set cuts to cuts + 1. return list(\"Mk16 last=\" + last). }\n"
                + Function("reentry_chute_stage")
                + "local b is test_brake_tick(lexicon(\"now\", 100, \"radar\", 20, \"speed\", 8, \"v_down\", 8, \"a_up\", 12, \"g\", 2.94, \"soft\", 2, \"end\", 10, \"up_deg\", 5, \"throttle\", 0.8, \"off_deg\", 1, \"rate_deg\", 1,"
                + " \"brake_why\", \"last resort\", \"last_resort_ut\", 90, \"open_ut\", 85, \"open_count\", 1, \"settled\", true, \"all_open_ut\", 80, \"uids\", list(\"a\"), \"drogues_called\", true, \"drogue_call_ut\", 80,"
                + " \"chutes\", list(lexicon(\"title\", \"Mk16\", \"state\", \"deployed\", \"drogue\", true)))).\n"
                + CutLines()
                + "print cuts. print notes:join(\";\"). print chute_mode. print b:throttle.\n"
                + "print reentry_chute_stage(chute_mode, true, false, false, 100, 100, 0.5).\n";
            Assert.Equal(new[] { "1", "chutes_cut:final burn", "none", "0.8", "wait" }, KosHarness.Run(script).ToArray());
        }

        [Fact]
        public void Failing_for_under_a_second_the_block_cuts_nothing_and_still_calls_chutes()
        {
            var script = CutRig("tick_at(100). tick_at(100.9).\nprint cuts. print chute_mode.\n");
            Assert.Equal(new[] { "0", "safe" }, KosHarness.Run(script).ToArray());
        }

        [Fact]
        public void The_mid_flight_cut_cuts_whatever_is_out_even_on_the_last_entry()
        {
            Assert.Contains("reentry_cut_chutes(ship:parts, false)", CutLines());
            Assert.Contains("set chute_mode to \"none\".", CutLines());
        }

        /// <summary>A chute-only descent never asks Core (so can't be told to cut): it opens by speed, height and pressure, reading no chute state at all. This line is its only way in.</summary>
        [Fact]
        public void A_chute_only_descent_never_asks_so_nothing_is_cut()
        {
            var body = Body();
            Assert.Equal(1, body.Split("addons:claudia:braketick(").Length - 1);
            const string Line = "local brake is choose brake_ask() if powered else lexicon(\"start\", false).\n";
            Assert.Contains(Line, body);
            var fell = body.IndexOf("local fell is claudia_warp_to(", StringComparison.Ordinal);
            // The other ask is in the burn alone, reached only when braking started, and braking starts only on Core's word.
            var loop = body.IndexOf("if fell = \"\" and braking and not claudia_on_ground() {", StringComparison.Ordinal);
            Assert.Equal(2, body.Split("brake_ask(").Length - 1);
            Assert.True(body.IndexOf("brake_ask(reentry_brake_throttle(", StringComparison.Ordinal) > loop && loop > fell);
            var script = "global asked is 0.\nlocal brake_ask is { set asked to asked + 1. return lexicon(\"start\", false, \"cut\", true). }.\nlocal powered is false.\n" + Line + "print asked. print brake:haskey(\"cut\").\n";
            Assert.Equal(new[] { "0", "False" }, KosHarness.Run(script).ToArray());
        }

        /// <summary>The block's own line, run: gimbals lock under open chutes and are freed at a cut, as the canopies they were locked for are gone.</summary>
        [Theory]
        [InlineData(true, false, "True,True,a")]
        [InlineData(false, true, "False,True,a")] // locked earlier under open chutes, then cut
        [InlineData(false, false, "True,True,a")] // a bad tick: left as they are
        public void The_gimbals_lock_while_fully_open_and_are_freed_at_a_cut(bool open, bool cut, string expected)
        {
            var body = Body();
            const string Start = "if b:open or b:cut { reentry_gimbals(";
            var at = body.IndexOf(Start, StringComparison.Ordinal);
            Assert.True(at > 0, "the block's gimbal line");
            var script = Engines + Function("reentry_gimbals") + "global ship is lexicon(\"engines\", engs).\n"
                + "local gimbals is locked.\nreentry_gimbals(engs, true, gimbals).\n"
                + "local b is lexicon(\"open\", " + open.ToString().ToLowerInvariant() + ", \"cut\", " + cut.ToString().ToLowerInvariant() + ").\n"
                + body.Substring(at, body.IndexOf('\n', at) - at) + "\nshow().\n";
            Assert.Equal(new[] { expected }, KosHarness.Run(script).ToArray());
        }

        [Fact]
        public void After_the_cut_it_lands_like_any_powered_finish()
        {
            var body = Body();
            // mains_out stays true after a cut: the hold under chutes must not outlive them.
            Assert.Contains("if mains_out and chute_mode <> \"none\" {\n        set steer to reentry_chute_aim(-vel, up_v).", body);
            // The abort reads the same mode, so it opens nothing after the cut.
            Assert.Contains("for t in reentry_abort(chute_mode, reentry_fresh_group(group, ask, extra:chutes_used)) {", body);
        }

        // ---- The real answers, through the addon's real conversion ----

        [Fact]
        public void The_real_answers_reach_kOS_as_the_types_the_block_reads()
        {
            var heights = (Lexicon)KosValues.FromPlain(BlockCalls.BrakeHeights(new Dictionary<string, object> { { "v_down", 12.0 }, { "a_up", 12.0 }, { "g", 2.94 }, { "ks", new List<object> { 0.0, 0.0 } }, { "radar", 400.0 }, { "soft", 2.0 }, { "end", 10.0 } }));
            Assert.IsAssignableFrom<ScalarValue>(heights[new StringValue("brake_h")]);
            Assert.IsAssignableFrom<ScalarValue>(heights[new StringValue("last_h")]);

            var tick = (Lexicon)KosValues.FromPlain(BlockCalls.BrakeTick(new Dictionary<string, object>
            {
                { "radar", 30.0 }, { "speed", 12.0 }, { "brake_h", 32.0 }, { "last_h", 8.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "throttle", 1.0 }, { "off_deg", 45.0 }, { "now", 20.0 }, { "open_ut", 10.0 },
                { "chutes", new List<object> { new Dictionary<string, object> { { "title", "Mk16 Parachute" }, { "state", ReentryBrake.Deployed } } } },
            }));
            Assert.Equal(BooleanValue.True, tick[new StringValue("start")]);
            Assert.Equal(BooleanValue.True, tick[new StringValue("open")]);
            Assert.Equal(new StringValue("chutes open"), tick[new StringValue("why")]);
            Assert.Equal(new StringValue("Mk16 Parachute deployed"), tick[new StringValue("chutes")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("open_ut")]);
            Assert.Equal(BooleanValue.True, tick[new StringValue("settled")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("open_count")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("drogue_call_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("main_call_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("rate_deg")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("last_resort_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("unread_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("trace_ut")]);
            Assert.Equal(BooleanValue.False, tick[new StringValue("cut")]);
            Assert.Equal(BooleanValue.False, tick[new StringValue("trace")]);
            Assert.Equal(new StringValue("off aim"), tick[new StringValue("zero_why")]);
            Assert.Equal(ScalarValue.Create(0), tick[new StringValue("throttle")]);
            // #969: the final wait's values; the block reads cut_why in its cut note.
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("all_open_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("final_h")]);
            Assert.Equal(BooleanValue.False, tick[new StringValue("final")]);
            Assert.Equal(new StringValue(""), tick[new StringValue("cut_why")]);
            Assert.IsAssignableFrom<Lexicon>(tick[new StringValue("note")]);
            var cut = (Lexicon)KosValues.FromPlain(BlockCalls.BrakeTick(new Dictionary<string, object>
            {
                { "radar", 20.0 }, { "speed", 8.0 }, { "v_down", 8.0 }, { "a_up", 12.0 }, { "g", 2.94 }, { "soft", 2.0 }, { "end", 10.0 }, { "up_deg", 5.0 }, { "throttle", 0.8 }, { "now", 100.0 },
                { "brake_why", "last resort" }, { "last_resort_ut", 90.0 }, { "open_ut", 85.0 }, { "open_count", 1.0 }, { "settled", true }, { "all_open_ut", 80.0 }, { "drogues_called", true }, { "drogue_call_ut", 80.0 },
                { "chutes", new List<object> { new Dictionary<string, object> { { "title", "Mk25 Drogue" }, { "state", ReentryBrake.Deployed }, { "drogue", true } } } },
            }));
            Assert.Equal(BooleanValue.True, cut[new StringValue("cut")]);
            Assert.Equal(new StringValue("final burn"), cut[new StringValue("cut_why")]);
            Assert.Equal(BooleanValue.True, cut[new StringValue("final")]);
            // #969's follow-up: the cut's note, with the seconds left to stop the turn.
            var cutNote = (Lexicon)cut[new StringValue("note")];
            Assert.Equal(new StringValue("final_cut"), cutNote[new StringValue("phase")]);
            Assert.IsAssignableFrom<ScalarValue>(cutNote[new StringValue("turn_s")]);
            Assert.IsAssignableFrom<ScalarValue>(cutNote[new StringValue("rate_deg")]);
            Assert.IsAssignableFrom<ScalarValue>(cutNote[new StringValue("up_deg")]);
            var stop = (Lexicon)((Lexicon)KosValues.FromPlain(BlockCalls.BrakeTick(new Dictionary<string, object>
            {
                { "radar", 300.0 }, { "speed", 8.0 }, { "v_down", 8.0 }, { "a_up", 12.0 }, { "g", 2.94 }, { "soft", 2.0 }, { "end", 10.0 }, { "now", 100.0 }, { "brake_why", "last resort" }, { "all_open_ut", 80.0 },
                { "chutes", new List<object> { new Dictionary<string, object> { { "title", "Mk25 Drogue" }, { "state", ReentryBrake.Deployed }, { "drogue", true } } } },
            })))[new StringValue("note")];
            Assert.Equal(new StringValue("thrust_stopped"), stop[new StringValue("phase")]);
            Assert.IsAssignableFrom<ScalarValue>(stop[new StringValue("final_h")]);

            var unread = (Lexicon)KosValues.FromPlain(BlockCalls.BrakeTick(new Dictionary<string, object>
            {
                { "now", 20.0 }, { "chutes_unread", true }, { "uids", new List<object> { "a" } }, { "unread_ut", 19.0 }, { "sample_ut", 19.8 }, { "sample_speed", 30.0 }, { "speed", 30.0 }, { "throttle", 1.0 }, { "open_ut", 10.0 }, { "open_count", 1.0 }, { "settled", true }, { "drogue_call_ut", 5.0 }, { "main_call_ut", 8.0 },
            }));
            Assert.Equal(BooleanValue.True, unread[new StringValue("cut")]);
            Assert.Equal(new StringValue("unreadable"), unread[new StringValue("cut_why")]);
            Assert.IsAssignableFrom<Lexicon>(unread[new StringValue("note")]);
            Assert.Equal(BooleanValue.False, unread[new StringValue("start")]);
            Assert.Equal(new StringValue("chutes unreadable"), unread[new StringValue("zero_why")]);
            Assert.Equal(new StringValue("unreadable"), unread[new StringValue("chutes")]);
            Assert.Equal(ScalarValue.Create(0), unread[new StringValue("throttle")]);
            Assert.Equal(BooleanValue.True, unread[new StringValue("settled")]);
            Assert.IsAssignableFrom<ScalarValue>(unread[new StringValue("open_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(unread[new StringValue("unread_ut")]);

            var failed = (Lexicon)KosValues.FromPlain(BlockCalls.BrakeTick(null));
            Assert.Equal(BooleanValue.False, failed[new StringValue("start")]);
            Assert.Equal(ScalarValue.Create(0), failed[new StringValue("throttle")]);
        }
    }

    /// <summary><c>test_brake_heights(input)</c> and <c>test_brake_tick(input)</c>: Core's answers as the addon gives them, for the descent simulations on kOS. Registered through <see cref="KosHarness.Register"/>.</summary>
    internal static class ReentryBrakeRigFunctions
    {
        [Function("test_brake_heights")]
        public class FunctionHeights : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosValues.FromPlain(BlockCalls.BrakeHeights(input));
            }
        }

        [Function("test_brake_tick")]
        public class FunctionTick : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var input = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosValues.FromPlain(BlockCalls.BrakeTick(input));
            }
        }
    }
}
