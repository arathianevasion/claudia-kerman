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
            Assert.True(body.IndexOf("reentry_gimbals(ship:engines, true, gimbals)", StringComparison.Ordinal) < fell);
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

        /// <summary>reentry_gimbals itself, lifted: the exit rig fakes the rest of the block.</summary>
        private static string GimbalsFunction()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var start = source.IndexOf("function reentry_gimbals {", StringComparison.Ordinal);
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
            var script = ExitRig + GimbalsFunction() + "function exit_ {\n parameter fell.\n" + ExitSlice() + " return \"went on\".\n}\n"
                + "local out is exit_(\"" + fell + "\").\nprint notes:join(\";\"). print out.\n";
            Assert.Equal(new[] { "abort,locked=False;finish,locked=False," + fell, fell }, KosHarness.Run(script).ToArray());
        }

        [Fact]
        public void A_landing_gives_the_gimbals_back_before_it_goes_on_to_the_end()
        {
            var script = ExitRig + GimbalsFunction() + "function exit_ {\n parameter fell.\n" + ExitSlice() + " return \"landed,locked=\" + a:gimbal:lock.\n}\n"
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
            Assert.Contains("if powered and held {\n        set ctx:rcs_braking to true.\n        if mains_out { lock steering to reentry_chute_aim(-ship:velocity:surface, ship:up:vector). }\n      } else if not braking {\n        unlock steering.", body);
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
            Assert.Contains("\"rate_deg\", ship:angularvel:mag * constant:radtodeg", ask);
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
            Assert.Contains("if braking and time:seconds >= trace_ut {", body);
            Assert.Contains("\"rate_deg\", round(b:rate_deg, 1), \"zero_why\", b:zero_why", body);
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
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("drogue_call_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("main_call_ut")]);
            Assert.IsAssignableFrom<ScalarValue>(tick[new StringValue("rate_deg")]);
            Assert.Equal(new StringValue("off aim"), tick[new StringValue("zero_why")]);
            Assert.Equal(ScalarValue.Create(0), tick[new StringValue("throttle")]);

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
