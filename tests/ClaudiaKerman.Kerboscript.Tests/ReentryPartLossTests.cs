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
    /// #958: a part lost in the air doesn't end <c>reentry</c>. The block's own hook and <c>claudia_tick</c>'s abort line are lifted out of the real library and run on kOS.Safe
    /// with the game faked, ENTRYLOSS answering with Core's real rule (<c>test_entry_loss</c>); ENTRYFINISH's and CHUTEGROUP's readings are the rig's. Core's rules are in <c>EntryPartLossTests</c>.
    /// </summary>
    public class ReentryPartLossTests
    {
        public ReentryPartLossTests()
        {
            KosHarness.Register(typeof(ReentryPartLossRigFunctions));
        }

        private static string Body()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            return source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
        }

        private static string Lib()
        {
            return CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source.Replace("\r\n", "\n");
        }

        private static int At(string text, string what, int from = 0)
        {
            var i = text.IndexOf(what, from, StringComparison.Ordinal);
            Assert.True(i >= 0, "not found: " + what);
            return i;
        }

        private const string HookStart = "  local ec_watch is ctx:ec_tick.\n";

        /// <summary>The hook as the block sets it, whole; its hold is recorded instead of locked, as kOS.Safe has no steering.</summary>
        private static string Hook()
        {
            var body = Body();
            var start = At(body, HookStart);
            const string End = "\n  }.\n";
            return body.Substring(start, At(body, End, start) + End.Length - start).Replace("lock steering to srfretrograde.", "set steer_locked to true.");
        }

        /// <summary>The block's chute_tick, whole (#963's RCS rule): with both sets called it opens nothing, so no steering is locked or released.</summary>
        private static string ChuteTick()
        {
            var body = Body();
            var start = At(body, "  local chute_tick is {\n");
            const string End = "\n  }.\n";
            return body.Substring(start, At(body, End, start) + End.Length - start);
        }

        private static string AbortLine()
        {
            var lib = Lib();
            var start = At(lib, "  return claudia_abort_reason(", At(lib, "function claudia_tick {"));
            return lib.Substring(start, lib.IndexOf("\n", start, StringComparison.Ordinal) - start) + "\n";
        }

        /// <summary>
        /// The craft: the pod, the engine e1, a drogue d and a main m in stage 1, all stowed; the block's state as a powered finish (<paramref name="powered"/>) or on chutes, both sets called, mid-burn under open chutes.
        /// <paramref name="finish"/> and <paramref name="group"/> are what ENTRYFINISH and CHUTEGROUP read once the parts have gone. <c>tick</c> runs the block's abort line, through the hook.
        /// </summary>
        private static string Rig(bool powered, string finish, string group, double altitude = 16000, string mode = "safe", string states = "list()", bool abortOnLoss = true)
        {
            var on = powered.ToString().ToLowerInvariant();
            return "global status is \"FLYING\".\n"
                + "global notes is list().\nglobal lit is list().\nglobal steer_locked is false.\nglobal asked is 0.\nglobal last_why is \"\".\n"
                + "function chute {\n parameter uid, name.\n"
                + "  local m is lexicon(\"hasevent\", { parameter e. return e = \"deploy chute\". }).\n"
                + "  return lexicon(\"uid\", uid, \"name\", name, \"stage\", 1, \"title\", \"Chute \" + uid, \"hasmodule\", { parameter n. return n = \"ModuleParachute\". }, \"getmodule\", { parameter n. return m. }).\n}\n"
                + "function part_ { parameter uid. return lexicon(\"uid\", uid, \"name\", uid, \"stage\", 0, \"hasmodule\", { parameter n. return false. }). }\n"
                + "local e1 is lexicon(\"uid\", \"e1\", \"name\", \"liquidEngine\", \"stage\", 2, \"ignition\", false, \"hasmodule\", { parameter n. return false. }).\n"
                + "set e1:activate to { lit:add(\"e1@\" + ship:control:pilotmainthrottle). }.\n"
                + "global ship is lexicon(\"parts\", list(part_(\"pod\"), e1, chute(\"d\", \"parachuteDrogue\"), chute(\"m\", \"parachuteSingle\")), \"engines\", list(e1),\n"
                + "  \"altitude\", " + altitude + ", \"availablethrust\", 0, \"geoposition\", lexicon(\"terrainheight\", 1000), \"control\", lexicon(\"pilotmainthrottle\", 1)).\n"
                + "global addons is lexicon(\"claudia\", lexicon(\n"
                + "  \"entryloss\", { parameter q. set asked to asked + 1. set q:chutes to " + states + ". return test_entry_loss(q, " + finish + ", " + group + "). },\n"
                + "  \"blocknote\", { parameter b, n. notes:add(n:phase + \":\" + n:lost + \":\" + n:powered + \":\" + n:chutes). set last_why to n:why. })).\n"
                + "local opt is lexicon(\"min_ec_pct\", 10, \"max_duration\", 100000, \"abort_on_part_loss\", " + abortOnLoss.ToString().ToLowerInvariant() + ", \"chute_mode\", \"" + mode + "\").\n"
                + "local ctx is lexicon(\"block\", \"reentry\", \"opt\", opt, \"start_ut\", 0, \"parts\", 4, \"ec_tick\", { return 55. }, \"rcs_braking\", false).\n"
                + "local body_ is lexicon(\"atm\", lexicon(\"height\", 50000)).\n"
                + "local extra is lexicon(\"parts_lost\", 0).\n"
                + "local entry_ask is lexicon(\"home\", \"pod\", \"chutes\", true).\n"
                + "local ask is lexicon(\"none\", false, \"engine\", " + on + ").\n"
                + "local powered is " + on + ".\n"
                + "local finish is lexicon(\"error\", \"\", \"spare\", list()).\n"
                + "local held is true.\nlocal braking is " + on + ".\n"
                + "local chute_mode is \"" + mode + "\".\n"
                + "local group is lexicon(\"error\", \"\", \"last\", false, \"stages\", list(1), \"uids\", list(\"d\", \"m\"), \"why\", \"\").\n"
                + "local has_drogues is false.\nlocal drogues_out is true.\nlocal mains_out is true.\n"
                + "local shield_uid is \"\".\nlocal shield_gone is false.\n"
                + "local brake_why is \"chutes open\".\n"
                + Hook()
                + "function tick {\n parameter ctx, now.\n" + AbortLine() + "}\n"
                + "function lose { parameter uid. local i is 0. for p in ship:parts { if p:uid = uid { ship:parts:remove(i). return. } set i to i + 1. } }\n";
        }

        private const string ChutesOnly = "lexicon(\"error\", \"\", \"powered\", false, \"engines\", list(), \"spare\", list(), \"why\", \"chutes, 6.1 m/s, no engine with fuel\")";
        private const string OnEngine = "lexicon(\"error\", \"\", \"powered\", true, \"engines\", list(\"e1\"), \"spare\", list(), \"why\", \"engines, keeping everything\")";
        private const string StageOne = "lexicon(\"error\", \"\", \"last\", false, \"stages\", list(1), \"uids\", list(\"d\", \"m\"), \"why\", \"\")";
        private const string Mains = "lexicon(\"error\", \"\", \"last\", false, \"stages\", list(1), \"uids\", list(\"m\"), \"why\", \"\")";
        private const string NoChutes = "lexicon(\"error\", \"\", \"last\", false, \"stages\", list(), \"uids\", list(), \"why\", \"\")";

        private const string Show = "print \"[\" + tick(ctx, 1) + \"]\".\n"
            + "print \"lost=\" + extra:parts_lost + \" parts=\" + ctx:parts + \" powered=\" + powered + \" engine=\" + ask:engine + \" mode=\" + chute_mode.\n"
            + "print \"drogues=\" + has_drogues + \"/\" + drogues_out + \" mains_out=\" + mains_out + \" why=\" + brake_why + \" uids=\" + group:uids:join(\",\").\n"
            + "print \"lit=\" + lit:join(\",\") + \" held=\" + steer_locked + \" shield_gone=\" + shield_gone + \" notes=\" + notes:join(\";\").\n";

        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        [Fact]
        public void A_lost_engine_with_chutes_left_carries_on_to_the_chutes_and_calls_them()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=1 parts=3 powered=False engine=False mode=safe",
                "drogues=False/False mains_out=False why= uids=d,m",
                "lit= held=False shield_gone=True notes=parts_lost:1:False:2",
            }, Run(Rig(true, ChutesOnly, StageOne) + "lose(\"e1\").\n" + Show));
        }

        [Fact]
        public void Lost_chutes_with_an_engine_left_carry_on_to_the_engine_lit_and_held()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=2 parts=2 powered=True engine=True mode=none",
                "drogues=False/True mains_out=True why=chutes open uids=",
                "lit=e1@0 held=True shield_gone=True notes=parts_lost:2:True:0", // lit at zero throttle (#963)
            }, Run(Rig(false, OnEngine, NoChutes) + "lose(\"d\"). lose(\"m\").\n" + Show));
        }

        [Fact]
        public void With_engine_and_chutes_gone_it_still_carries_on_to_the_end()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=3 parts=1 powered=False engine=False mode=none",
                "drogues=False/True mains_out=True why= uids=",
                "lit= held=False shield_gone=True notes=parts_lost:3:False:0",
            }, Run(Rig(true, ChutesOnly, NoChutes) + "lose(\"e1\"). lose(\"d\"). lose(\"m\").\n" + Show));
        }

        /// <summary>The addon reads the group's main as deployed: the finish and a fresh group would count only stowed chutes, so the group stays and the chute path doesn't go over to the engine under it.</summary>
        [Fact]
        public void A_loss_under_an_open_canopy_keeps_the_group_and_the_chutes()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=1 parts=3 powered=False engine=False mode=safe",
                "drogues=False/True mains_out=True why= uids=d,m",
                "lit= held=False shield_gone=True notes=parts_lost:1:False:2",
            }, Run(Rig(false, OnEngine, NoChutes, states: "list(lexicon(\"title\", \"Chute m\", \"state\", \"deployed\"))") + "lose(\"d\").\n" + Show));
        }

        [Fact]
        public void A_lost_drogue_leaves_the_mains_to_be_called()
        {
            var lines = Run(Rig(false, ChutesOnly, Mains) + "lose(\"d\").\n" + Show);
            Assert.Equal("[]", lines[0]);
            Assert.Equal("drogues=False/True mains_out=False why= uids=m", lines[2]);
        }

        // ---- #963: a chute path that loses its mains under its drogues ----

        private const string DrogueOut = "list(lexicon(\"title\", \"Chute d\", \"state\", \"deployed\", \"drogue\", true))";

        /// <summary>A chute path has never braked.</summary>
        private const string OnChutes = "set brake_why to \"\".\n";

        /// <summary>
        /// The drogue is out and the main goes: Core's finish says engines, so the engine is lit at zero throttle and held, and the group stays with the drogue in it, so the powered finish's
        /// BRAKETICK waits for it (Core's <c>EntryPartLossTests</c>: thrust zero until it settles, timed from the switch).
        /// </summary>
        [Fact]
        public void A_chute_path_losing_its_mains_under_its_drogues_goes_to_its_engine_lit_at_zero_throttle_and_keeps_the_drogues_in_its_group()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=1 parts=3 powered=True engine=True mode=safe",
                "drogues=False/True mains_out=True why= uids=d,m",
                "lit=e1@0 held=True shield_gone=True notes=parts_lost:1:True:2",
                "throttle=0 why=mains lost, on to the engines: engines, keeping everything",
            }, Run(Rig(false, OnEngine, NoChutes, states: DrogueOut) + OnChutes + "lose(\"m\").\n" + Show + "print \"throttle=\" + ship:control:pilotmainthrottle + \" why=\" + last_why.\n"));
        }

        /// <summary>
        /// ADR 0020: RCS stays off under a chute. The switch locks the hold while the drogue is out; the warp loop's next stop (fall_tick, so chute_tick) runs before claudia_tick's
        /// RCS rule, and chute_tick, opening nothing as both sets are called, still sets <c>rcs_braking</c>. On the drogues alone, unpowered, it stays as it was.
        /// </summary>
        [Theory]
        [InlineData(true, "held=True rcs_braking=True")]
        [InlineData(false, "held=False rcs_braking=False")]
        public void After_a_switch_under_its_drogues_RCS_stays_off_from_the_next_tick(bool engine, string expected)
        {
            var script = Rig(false, engine ? OnEngine : ChutesOnly, NoChutes, states: DrogueOut) + OnChutes + ChuteTick()
                + "lose(\"m\").\ntick(ctx, 1).\nchute_tick(60, 2500, 0.5).\n"
                + "print \"held=\" + steer_locked + \" rcs_braking=\" + ctx:rcs_braking.\n";
            Assert.Equal(new[] { expected }, Run(script));
        }

        [Fact]
        public void With_no_engine_to_land_it_a_chute_path_losing_its_mains_stays_on_its_drogues_and_notes_why()
        {
            Assert.Equal(new[]
            {
                "[]",
                "lost=1 parts=3 powered=False engine=False mode=safe",
                "drogues=False/True mains_out=True why= uids=d,m",
                "lit= held=False shield_gone=True notes=parts_lost:1:False:2",
                "why=mains lost, staying on the drogues: chutes, 6.1 m/s, no engine with fuel",
            }, Run(Rig(false, ChutesOnly, NoChutes, states: DrogueOut) + OnChutes + "lose(\"m\").\n" + Show + "print \"why=\" + last_why.\n"));
        }

        [Fact]
        public void Each_loss_is_counted_once_and_the_next_tick_with_none_says_nothing()
        {
            Assert.Equal(new[] { "[]", "[]", "[]", "lost=2", "parts_lost:1:False:2;parts_lost:1:False:2" },
                Run(Rig(true, ChutesOnly, StageOne) + "lose(\"e1\"). print \"[\" + tick(ctx, 1) + \"]\". print \"[\" + tick(ctx, 2) + \"]\".\n"
                    + "lose(\"pod\"). print \"[\" + tick(ctx, 3) + \"]\". print \"lost=\" + extra:parts_lost. print notes:join(\";\").\n"));
        }

        [Fact]
        public void Before_the_air_a_part_loss_still_aborts_and_nothing_is_re_read()
        {
            Assert.Equal(new[]
            {
                "[abort abort_on_part_loss: 1 part(s) lost]",
                "lost=0 parts=4 powered=True engine=True mode=safe",
                "drogues=False/True mains_out=True why=chutes open uids=d,m",
                "lit= held=False shield_gone=False notes=",
            }, Run(Rig(true, ChutesOnly, StageOne, altitude: 60000) + "lose(\"e1\").\n" + Show));
        }

        /// <summary>With <c>abort_on_part_loss</c> false a loss above the air ends nothing and the count stays fallen: Core isn't asked (nor the chutes read, nor KSP.log written) on each tick of the coast, only once the craft is in the air.</summary>
        [Fact]
        public void Above_the_air_without_the_abort_Core_is_not_asked_each_tick_until_the_craft_is_in_the_air()
        {
            Assert.Equal(new[] { "[]", "[]", "[]", "asked=0 lost=0", "[]", "asked=1 lost=1" },
                Run(Rig(true, ChutesOnly, StageOne, altitude: 60000, abortOnLoss: false) + "lose(\"e1\").\n"
                    + "print \"[\" + tick(ctx, 1) + \"]\". print \"[\" + tick(ctx, 2) + \"]\". print \"[\" + tick(ctx, 3) + \"]\".\n"
                    + "print \"asked=\" + asked + \" lost=\" + extra:parts_lost.\n"
                    + "set ship:altitude to 16000. print \"[\" + tick(ctx, 4) + \"]\". tick(ctx, 5). print \"asked=\" + asked + \" lost=\" + extra:parts_lost.\n"));
        }

        [Fact]
        public void The_low_charge_watch_still_runs_under_the_hook()
        {
            Assert.Equal(new[] { "abort min_ec_pct: electric charge 5% is below 10%" },
                Run(Rig(true, ChutesOnly, StageOne) + "set ec_watch to { return 5. }.\nprint tick(ctx, 1).\n"));
        }

        [Fact]
        public void Every_other_block_still_aborts_in_the_air_through_the_unchanged_shared_check()
        {
            // Another block's ctx: no hook, the part count fallen, falling through air.
            var script = "global status is \"FLYING\".\n"
                + "global ship is lexicon(\"parts\", list(1, 2), \"altitude\", 16000, \"resources\", list(lexicon(\"name\", \"ElectricCharge\", \"amount\", 90, \"capacity\", 100))).\n"
                + "local opt is lexicon(\"min_ec_pct\", 10, \"max_duration\", 100000, \"abort_on_part_loss\", true).\n"
                + "function tick {\n parameter ctx, now.\n" + AbortLine() + "}\n"
                + "print tick(lexicon(\"opt\", opt, \"start_ut\", 0, \"parts\", 4), 1).\n";
            Assert.Equal(new[] { "abort abort_on_part_loss: 2 part(s) lost" }, Run(script));
            Assert.Contains("  if opt:abort_on_part_loss and parts_lost > 0 {\n    return \"abort abort_on_part_loss: \" + parts_lost + \" part(s) lost\".\n", Lib());
            Assert.Equal(new[] { "reentry" }, CoreLibrary.Files.Where(f => f.Source.Contains("entryloss(")).Select(f => f.Name).ToArray());
        }

        // ---- The block's own drops ----

        /// <summary>The block's shield drop, lifted whole; the drop itself is faked, taking the shield off the craft, and the half-second wait for it to leave is the rig's.</summary>
        private static string ShieldDrop()
        {
            var body = Body();
            var start = At(body, "    if reentry_shield_due(shield_gone, last_speed, agl, brake:start) {\n");
            const string End = "\n    }\n";
            return body.Substring(start, At(body, End, start) + End.Length - start)
                .Replace("reentry_shield_drop(", "fake_shield_drop(").Replace("wait 0.5.", "");
        }

        [Fact]
        public void The_blocks_own_shield_drop_is_not_reported_as_a_loss()
        {
            var script = Rig(true, ChutesOnly, StageOne)
                + "set ctx:parts to 5. ship:parts:add(part_(\"shield\")).\n"
                + "set extra:shield_dropped to false.\nlocal below is lexicon(\"jettison_first\", \"\").\nlocal last_speed is 300. local agl is 2500. local brake is lexicon(\"start\", false).\n"
                + "function fake_shield_drop { parameter uid, parts_of, first. lose(\"shield\"). return 1. }\n"
                + ShieldDrop()
                + Show + "print \"dropped=\" + extra:shield_dropped.\n";
            var lines = Run(script);
            Assert.Equal("[]", lines[0]);
            Assert.Equal("lost=0 parts=4 powered=True engine=True mode=safe", lines[1]);
            Assert.EndsWith("notes=", lines[3]);
            Assert.Equal("dropped=True", lines[4]);
        }

        [Fact]
        public void Every_drop_of_the_blocks_own_takes_the_craft_as_it_now_is_before_a_tick_can_count_it()
        {
            Assert.Contains("set ctx:parts to ship:parts:length.", ShieldDrop());
            // The stack drop goes through claudia_fire_drop, which does the same once the parts have left.
            var lib = Lib();
            var fire = At(lib, "function claudia_fire_drop {");
            var fireBody = lib.Substring(fire, At(lib, "\n}\n", fire) - fire);
            Assert.True(At(fireBody, "wait 0.5.") < At(fireBody, "set ctx:parts to ship:parts:length."));
            Assert.Contains("set drop_error to claudia_fire_drop(ctx, drop, false).", Body());
            // A chute cut leaves its part on the craft: reentry_cut_chutes only fires the cut event.
            var cut = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var cutAt = At(cut, "function reentry_cut_chutes {");
            Assert.DoesNotContain("decouple", cut.Substring(cutAt, At(cut, "\n}\n", cutAt) - cutAt));
        }

        // ---- Where the hook sits ----

        [Fact]
        public void The_hook_is_set_once_its_state_exists_and_before_the_fall()
        {
            var body = Body();
            var hook = At(body, HookStart);
            Assert.True(hook > At(body, "  local gimbals is list().\n"), "after the state it re-reads");
            Assert.True(hook > At(body, "  local brake_why is \"\".\n"));
            Assert.True(hook < At(body, "local fell is claudia_warp_to("), "before the fall");
            Assert.Contains("    return ec_watch:call().\n  }.\n", Hook());
            // claudia_tick asks the hook before it counts the parts.
            var line = AbortLine();
            Assert.True(At(line, "ctx:ec_tick:call()") < At(line, "ctx:parts - ship:parts:length"));
        }

        [Fact]
        public void A_burn_whose_engines_went_still_gives_the_controls_back()
        {
            Assert.Contains("  if powered or braking {\n    claudia_release_controls().", Body());
        }

        [Fact]
        public void A_skip_out_asks_Core_and_flies_the_next_pass_under_its_deadline()
        {
            var body = Body();
            Assert.Contains("local skip is addons:claudia:entryskip(lexicon(\"passes\", passes, \"pe\", ship:periapsis, \"air_top\", body_:atm:height, \"entry_in\", entry_in, \"auto\", auto_duration, \"max_duration\", opt:max_duration, \"elapsed\", time:seconds - ctx:start_ut)).", body);
            Assert.Contains("    set fell to skip:refusal.\n    if fell = \"\" {\n      set opt:max_duration to skip:max_duration.", body);
        }

        // ---- The real answers, through the addon's real conversion ----

        [Fact]
        public void The_real_answers_reach_kOS_as_the_types_the_block_reads()
        {
            var finish = new Dictionary<string, object> { { "error", "" }, { "powered", true }, { "engines", new List<object> { "e1" } }, { "spare", new List<object>() }, { "why", "engines" } };
            var group = new Dictionary<string, object> { { "error", "" }, { "last", false }, { "stages", new List<object> { 1.0 } }, { "uids", new List<object> { "m" } }, { "why", "" } };
            var ask = new Dictionary<string, object> { { "lost", 2.0 }, { "altitude", 16000.0 }, { "air_top", 50000.0 }, { "on_ground", false }, { "powered", false }, { "thrust", 0.0 }, { "chute_mode", "safe" }, { "mode", "safe" } };
            var loss = Assert.IsType<Lexicon>(KosValues.FromPlain(BlockCalls.EntryPartLoss(ask, _ => finish, _ => group)));
            Assert.Equal(BooleanValue.True, loss[new StringValue("carry_on")]);
            Assert.Equal(BooleanValue.True, loss[new StringValue("powered")]);
            Assert.Equal(BooleanValue.True, loss[new StringValue("to_engines")]);
            Assert.IsType<ListValue>(loss[new StringValue("light")]);
            Assert.Equal(new StringValue("safe"), loss[new StringValue("chute_mode")]);
            Assert.IsType<Lexicon>(loss[new StringValue("finish")]);
            Assert.IsType<ListValue>(((Lexicon)loss[new StringValue("group")])[new StringValue("uids")]);
            Assert.Equal(new StringValue("engines"), loss[new StringValue("why")]);

            var failed = Assert.IsType<Lexicon>(KosValues.FromPlain(BlockCalls.EntryPartLoss(ask, null, null)));
            Assert.Equal(BooleanValue.True, failed[new StringValue("carry_on")]);
            Assert.IsType<StringValue>(((Lexicon)failed[new StringValue("finish")])[new StringValue("error")]);
            Assert.IsType<ListValue>(failed[new StringValue("light")]);

            ask["altitude"] = 60000.0;
            var above = Assert.IsType<Lexicon>(KosValues.FromPlain(BlockCalls.EntryPartLoss(ask, _ => finish, _ => group)));
            Assert.Equal(BooleanValue.False, above[new StringValue("carry_on")]);

            var skip = Assert.IsType<Lexicon>(KosValues.FromPlain(BlockCalls.EntrySkip(new Dictionary<string, object> { { "passes", 1.0 }, { "pe", 30000.0 }, { "air_top", 70000.0 }, { "entry_in", 900.0 }, { "auto", true }, { "max_duration", 1900.0 }, { "elapsed", 3.0 } })));
            Assert.Equal(new StringValue(""), skip[new StringValue("refusal")]);
            Assert.IsAssignableFrom<ScalarValue>(skip[new StringValue("max_duration")]);
            var noAnswer = Assert.IsType<Lexicon>(KosValues.FromPlain(BlockCalls.EntrySkip(null)));
            Assert.IsAssignableFrom<ScalarValue>(noAnswer[new StringValue("max_duration")]);
        }
    }

    /// <summary><c>test_entry_loss(ask, finish, group)</c>: Core's ENTRYLOSS answer as the addon gives it, with <c>finish</c> and <c>group</c> as ENTRYFINISH's and CHUTEGROUP's readings. Registered through <see cref="KosHarness.Register"/>.</summary>
    internal static class ReentryPartLossRigFunctions
    {
        [Function("test_entry_loss")]
        public class FunctionLoss : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var group = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                var finish = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                var ask = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosValues.FromPlain(BlockCalls.EntryPartLoss(ask, f => finish, g => group));
            }
        }
    }
}
