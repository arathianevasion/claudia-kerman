// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#958: a part loss in the air doesn't end <c>reentry</c>; Core's <see cref="BlockCalls.EntryPartLoss"/> says what the craft carries on with. And the skip-out rules, moved to Core with it.</summary>
    public class EntryPartLossTests
    {
        private static Dictionary<string, object> Ask(double altitude = 16000, double lost = 2, bool onGround = false, bool powered = false, double thrust = 0, string chuteMode = "safe", string mode = "safe")
        {
            return new Dictionary<string, object>
            {
                { "lost", lost }, { "altitude", altitude }, { "air_top", 50000.0 }, { "on_ground", onGround }, { "powered", powered }, { "thrust", thrust }, { "chute_mode", chuteMode }, { "mode", mode },
                { "entry", new Dictionary<string, object> { { "home", "h" }, { "chutes", true } } },
                { "group", new Dictionary<string, object> { { "none", false }, { "engine", true } } },
            };
        }

        private static Dictionary<string, object> Finish(bool powered, params object[] engines)
        {
            return new Dictionary<string, object> { { "error", "" }, { "powered", powered }, { "engines", engines.ToList() }, { "spare", new List<object>() }, { "why", powered ? "engines" : "chutes" } };
        }

        private static Dictionary<string, object> Group(params object[] uids)
        {
            return new Dictionary<string, object> { { "error", "" }, { "uids", uids.ToList() } };
        }

        private static Dictionary<string, object> Failed(string why)
        {
            return new Dictionary<string, object> { { "error", why } };
        }

        private static IDictionary<string, object> Loss(Dictionary<string, object> ask, IDictionary<string, object> finish, IDictionary<string, object> group)
        {
            return BlockCalls.EntryPartLoss(ask, _ => finish, _ => group);
        }

        [Theory]
        [InlineData(16000, 2, false, true)]
        [InlineData(50000, 1, false, true)] // at the air top
        [InlineData(50001, 2, false, false)] // above it: the abort stands
        [InlineData(16000, 0, false, false)] // nothing lost
        [InlineData(16000, 2, true, false)] // landed
        public void It_carries_on_only_with_parts_lost_in_the_air_off_the_ground(double altitude, double lost, bool onGround, bool carries)
        {
            var loss = Loss(Ask(altitude, lost, onGround), Finish(false), Group());
            Assert.Equal("", loss["error"]);
            Assert.Equal(carries, loss["carry_on"]);
        }

        [Fact]
        public void Lost_engines_with_chutes_left_finish_on_the_chutes_and_widen_the_group()
        {
            var askedEngine = true;
            var askedDrop = true;
            var loss = BlockCalls.EntryPartLoss(Ask(powered: true, chuteMode: "safe"),
                ask => { askedDrop = (bool)ask["drop"]; return Finish(false); },
                ask => { askedEngine = (bool)ask["engine"]; return Group("a", "b"); });
            Assert.Equal(false, loss["powered"]);
            Assert.False(askedEngine); // so the group can widen to what's left
            Assert.False(askedDrop); // nothing more to drop: the craft as it is now
            Assert.Equal("safe", loss["chute_mode"]);
            Assert.Equal(new List<object>(), loss["light"]);
            Assert.Equal(false, loss["to_engines"]);
        }

        [Fact]
        public void Lost_chutes_with_an_engine_left_go_to_the_engine_and_light_it()
        {
            var loss = Loss(Ask(powered: false), Finish(true, "e1"), Group());
            Assert.Equal(true, loss["powered"]);
            Assert.Equal(true, loss["to_engines"]);
            Assert.Equal(new List<object> { "e1" }, loss["light"]);
            Assert.Equal("none", loss["chute_mode"]); // no chutes left
        }

        private static Dictionary<string, object> Out(Dictionary<string, object> ask, string state)
        {
            ask["chutes"] = new List<object> { new Dictionary<string, object> { { "title", "Mk16" }, { "state", state } } };
            return ask;
        }

        [Theory]
        [InlineData("deployed")]
        [InlineData("semi")]
        public void Under_a_canopy_already_out_a_chute_path_keeps_its_group_and_does_not_go_to_the_engine(string state)
        {
            var groupRead = false;
            var loss = BlockCalls.EntryPartLoss(Out(Ask(powered: false), state), _ => Finish(true, "e1"), _ => { groupRead = true; return Group(); });
            Assert.Equal(true, loss["carry_on"]);
            Assert.Equal(false, loss["powered"]);
            Assert.Equal(false, loss["regroup"]);
            Assert.False(groupRead); // a fresh read counts only stowed chutes: the open one would drop out of the group
            Assert.Equal(new List<object>(), loss["light"]);
            Assert.Equal("safe", loss["chute_mode"]);
        }

        [Theory]
        [InlineData("stowed", true)]
        [InlineData("armed", false)] // called, still packed: a fresh read counts only stowed chutes, so it would drop out
        [InlineData("cut", true)]
        [InlineData("deployed", false)]
        public void Only_a_chute_called_or_out_keeps_the_group(string state, bool regroup)
        {
            Assert.Equal(regroup, BlockCalls.EntryPartLoss(Out(Ask(powered: true, thrust: 50), state), _ => Finish(true, "e1"), _ => Group("m"))["regroup"]);
        }

        [Fact]
        public void A_powered_finish_losing_its_engine_with_its_mains_called_but_packed_keeps_them_and_does_not_widen_the_group()
        {
            // Case A: a fresh group with the engine gone has no stowed chute in this Entry and would widen to a later stage's (#827).
            var groupRead = false;
            var loss = BlockCalls.EntryPartLoss(Out(Ask(powered: true, thrust: 0, chuteMode: "safe"), "armed"), _ => Finish(false), _ => { groupRead = true; return Group("later"); });
            Assert.Equal(false, loss["regroup"]);
            Assert.False(groupRead);
            Assert.Equal("safe", loss["chute_mode"]);
        }

        [Fact]
        public void A_powered_finish_losing_a_tank_with_its_mains_called_but_packed_keeps_them_so_braking_still_waits_for_the_canopy()
        {
            // Case B: a fresh group with only the armed chute aboard comes back empty: chute_mode "none" would send BRAKETICK no uids, and it would burn as the canopy fills.
            var loss = Loss(Out(Ask(powered: true, thrust: 120, chuteMode: "safe"), "armed"), Finish(true, "e1"), Group());
            Assert.Equal(true, loss["powered"]);
            Assert.Equal(false, loss["regroup"]);
            Assert.Equal("safe", loss["chute_mode"]);
        }

        // ---- #963: a chute path that loses its mains under its drogues asks the finish again ----

        /// <summary>A chute path with drogue d <paramref name="drogueState"/> and main m in its group (<c>uids</c>); the addon reads only what is aboard, so a lost main isn't listed.</summary>
        private static Dictionary<string, object> UnderDrogues(string drogueState, string mainState = null)
        {
            var ask = Ask(powered: false);
            ask["uids"] = new List<object> { "d", "m" };
            var chutes = new List<object> { Chute(drogueState, true) };
            if (mainState != null) chutes.Add(Chute(mainState));
            ask["chutes"] = chutes;
            return ask;
        }

        [Theory]
        [InlineData("deployed")]
        [InlineData("semi")]
        [InlineData("armed")]
        public void A_chute_path_that_loses_its_mains_under_its_drogues_goes_to_an_engine_that_can_land_it(string drogueState)
        {
            var groupRead = false;
            var loss = BlockCalls.EntryPartLoss(UnderDrogues(drogueState), _ => Finish(true, "e1"), _ => { groupRead = true; return Group(); });
            Assert.Equal(true, loss["powered"]);
            Assert.Equal(true, loss["to_engines"]);
            Assert.Equal(new List<object> { "e1" }, loss["light"]);
            // The group stays as it was, the drogues in it, so BRAKETICK waits for them; the lost main counts as failed there and holds nothing back.
            Assert.Equal(false, loss["regroup"]);
            Assert.False(groupRead);
            Assert.Equal("safe", loss["chute_mode"]);
            Assert.Equal("mains lost, on to the engines: engines", loss["why"]);
        }

        [Theory]
        [InlineData("chutes, 38.0 m/s, no engine with fuel")] // no engine, or none that will light
        [InlineData("chutes, 38.0 m/s")] // the finish found no engine able to land it
        public void With_no_engine_that_can_land_it_a_chute_path_that_loses_its_mains_stays_on_its_drogues_and_says_so(string why)
        {
            var finish = Finish(false);
            finish["why"] = why;
            var loss = Loss(UnderDrogues("deployed"), finish, Group());
            Assert.Equal(true, loss["carry_on"]);
            Assert.Equal(false, loss["powered"]);
            Assert.Equal(false, loss["to_engines"]);
            Assert.Equal(new List<object>(), loss["light"]);
            Assert.Equal(false, loss["regroup"]);
            Assert.Equal("safe", loss["chute_mode"]);
            Assert.Equal("mains lost, staying on the drogues: " + why, loss["why"]);
        }

        [Theory]
        [InlineData("stowed")]
        [InlineData("armed")]
        [InlineData("deployed")]
        public void A_main_still_aboard_keeps_a_chute_path_on_its_chutes_whatever_else_went(string mainState)
        {
            var loss = Loss(UnderDrogues("deployed", mainState), Finish(true, "e1"), Group());
            Assert.Equal(false, loss["powered"]);
            Assert.Equal(new List<object>(), loss["light"]);
            Assert.Equal("engines", loss["why"]);
        }

        [Fact]
        public void A_main_left_aboard_cut_counts_as_lost_with_the_one_that_went()
        {
            var ask = UnderDrogues("deployed", ReentryBrake.Cut);
            ask["uids"] = new List<object> { "d", "m", "m2" };
            Assert.Equal(true, Loss(ask, Finish(true, "e1"), Group())["to_engines"]);
        }

        [Fact]
        public void At_the_switch_braking_starts_fresh_and_waits_for_the_drogues_already_out_to_settle()
        {
            // The addon's held times go back to these at the switch: a chute path has called BRAKETICK on none of its ticks, and what an earlier flight left must not count.
            var held = BlockCalls.EmptyBrakeHold().Select(p => K(p.Key, p.Value)).Concat(new[] { K("drogues_called", true), K("mains_called", false) }).ToArray();
            var drogueOut = new object[] { Chute(ReentryBrake.Deployed, true) };
            var first = BlockCalls.BrakeTick(WithUids(Tick(100, drogueOut, held), "d", "m"));
            Assert.Equal(ReentryBrake.WhyChuteOpening, first["zero_why"]);
            Assert.Equal(0.0, first["throttle"]);
            Assert.Equal(false, first["start"]);
            Assert.Equal(100.0, first["open_ut"]); // its settle time runs from the switch, never less than the real one
            Assert.Equal(100.0, first["drogue_call_ut"]);
            // Settled four seconds on with the speed steady: the drogues are waited for as #954's are, then it brakes by radar.
            var later = Tick(104.1, drogueOut, held);
            foreach (var key in new[] { "open_ut", "open_count", "drogue_call_ut", "main_call_ut" }) later[key] = first[key];
            later["sample_ut"] = 103.8;
            later["sample_speed"] = 90.0;
            later["decel"] = 0.2;
            var settled = BlockCalls.BrakeTick(WithUids(later, "d", "m"));
            Assert.Equal(true, settled["settled"]);
            Assert.Equal("", settled["zero_why"]);
            Assert.Equal("chutes open", settled["why"]);
            Assert.Equal(true, settled["start"]); // radar 500 within brake_h 600
        }

        [Fact]
        public void At_the_switch_an_armed_drogue_holds_the_thrust_for_its_second_then_the_last_resort_rule_holds()
        {
            var ask = Tick(100, new object[] { Chute(ReentryBrake.Armed, true) }, BlockCalls.EmptyBrakeHold().Select(p => K(p.Key, p.Value)).ToArray());
            ask["drogues_called"] = true;
            ask["mains_called"] = false;
            var first = BlockCalls.BrakeTick(WithUids(ask, "d", "m"));
            Assert.Equal(0.0, first["throttle"]);
            Assert.Equal(false, first["start"]);
            // A second after the switch it has failed: nothing is opening, and the last resort waits for its height (300 m).
            ask["now"] = 101.1;
            ask["drogue_call_ut"] = first["drogue_call_ut"];
            var after = BlockCalls.BrakeTick(WithUids(ask, "d", "m"));
            Assert.Equal(ReentryBrake.WhyLastResort, after["why"]);
            Assert.Equal(false, after["start"]);
            Assert.Equal("", after["zero_why"]);
            ask["radar"] = 290.0;
            Assert.Equal(true, BlockCalls.BrakeTick(WithUids(ask, "d", "m"))["start"]);
        }

        [Fact]
        public void Fresh_brake_times_hold_nothing()
        {
            var fresh = BlockCalls.EmptyBrakeHold();
            foreach (var key in new[] { "open_ut", "kept_ut", "drogue_call_ut", "main_call_ut", "sample_ut", "last_resort_ut", "unread_ut", "trace_ut" }) Assert.Equal(-1.0, fresh[key]);
            foreach (var key in new[] { "open_count", "kept_drogues", "kept_mains", "sample_speed", "decel" }) Assert.Equal(0.0, fresh[key]);
            Assert.Equal(false, fresh["settled"]);
            Assert.Equal(false, fresh["kept_settled"]);
        }

        private static Dictionary<string, object> WithUids(Dictionary<string, object> ask, params object[] uids)
        {
            ask["uids"] = uids.ToList();
            return ask;
        }

        [Fact]
        public void A_powered_finish_losing_its_engines_under_open_chutes_keeps_them_and_their_mode()
        {
            var loss = Loss(Out(Ask(powered: true, thrust: 0, chuteMode: "safe"), "deployed"), Finish(false), Group());
            Assert.Equal(false, loss["powered"]);
            Assert.Equal(false, loss["regroup"]);
            Assert.Equal("safe", loss["chute_mode"]);
        }

        [Fact]
        public void A_powered_finish_that_keeps_an_engine_giving_thrust_stays_on_its_engine_even_if_the_chutes_now_suffice()
        {
            var loss = Loss(Ask(powered: true, thrust: 240), Finish(false), Group("a"));
            Assert.Equal(true, loss["powered"]);
            Assert.Equal(false, loss["to_engines"]);
            Assert.Equal(new List<object>(), loss["light"]);
        }

        [Fact]
        public void A_powered_finish_with_its_engines_gone_and_a_stage_left_lights_that_stage()
        {
            var loss = Loss(Ask(powered: true, thrust: 0), Finish(true, "e2"), Group());
            Assert.Equal(true, loss["powered"]);
            Assert.Equal(false, loss["to_engines"]); // already powered: its own hold stands
            Assert.Equal(new List<object> { "e2" }, loss["light"]);
        }

        [Fact]
        public void With_nothing_left_to_slow_it_the_block_still_carries_on_to_the_end()
        {
            var loss = Loss(Ask(powered: true, thrust: 0), Finish(false), Group());
            Assert.Equal(true, loss["carry_on"]);
            Assert.Equal(false, loss["powered"]);
            Assert.Equal("none", loss["chute_mode"]);
            Assert.Equal(new List<object>(), loss["light"]);
        }

        [Theory]
        [InlineData("none", "safe", true, "none")] // #954's cut, engine still flying: no chute is called after a cut
        [InlineData("none", "safe", false, "safe")] // the engines are gone: the chutes are all that's left
        [InlineData("safe", "safe", false, "safe")]
        [InlineData("none", "none", false, "none")] // the script's chute_mode none stands
        public void The_chute_mode_comes_back_only_to_a_craft_with_no_engine_left(string current, string option, bool thrust, string expected)
        {
            var loss = Loss(Ask(powered: true, thrust: thrust ? 100 : 0, chuteMode: current, mode: option), Finish(false), Group("a"));
            Assert.Equal(expected, loss["chute_mode"]);
        }

        [Fact]
        public void A_finish_it_cannot_read_keeps_what_it_had_and_carries_on()
        {
            var loss = Loss(Ask(powered: true, thrust: 0, chuteMode: "safe"), Failed("no snapshot"), Failed("no snapshot"));
            Assert.Equal(true, loss["carry_on"]);
            Assert.Equal(true, loss["powered"]);
            Assert.Equal("safe", loss["chute_mode"]);
            Assert.Equal("no snapshot", loss["why"]);
            Assert.Equal(new List<object>(), loss["light"]);
        }

        [Fact]
        public void A_failed_read_of_either_answers_with_no_delegates_too()
        {
            var loss = BlockCalls.EntryPartLoss(Ask(powered: true), null, null);
            Assert.Equal(true, loss["carry_on"]);
            Assert.Equal(true, loss["powered"]);
            Assert.False(string.IsNullOrEmpty((string)((IDictionary<string, object>)loss["finish"])["error"]));
        }

        [Fact]
        public void No_ask_is_no_carry_on()
        {
            Assert.Equal(false, BlockCalls.EntryPartLoss(null, null, null)["carry_on"]);
        }

        // ---- #954's held times stay right as chutes go (BRAKETICK's reading comes from the group re-read) ----

        private static Dictionary<string, object> Chute(string state, bool drogue = false)
        {
            return new Dictionary<string, object> { { "title", "chute" }, { "state", state }, { "drogue", drogue } };
        }

        private static Dictionary<string, object> Tick(double now, object[] chutes, params KeyValuePair<string, object>[] held)
        {
            var ask = new Dictionary<string, object>
            {
                { "now", now }, { "radar", 500.0 }, { "speed", 90.0 }, { "brake_h", 600.0 }, { "last_h", 300.0 }, { "shield_speed", 400.0 }, { "shield_agl", 3000.0 }, { "throttle", 0.7 }, { "off_deg", 1.0 }, { "rate_deg", 1.0 },
                { "mains_called", true }, { "chutes", chutes.ToList() }, { "uids", chutes.Select(c => (object)"u").ToList() },
            };
            foreach (var pair in held) ask[pair.Key] = pair.Value;
            return ask;
        }

        private static KeyValuePair<string, object> K(string key, object value)
        {
            return new KeyValuePair<string, object>(key, value);
        }

        [Fact]
        public void Losing_a_called_chute_leaves_the_rest_settling_on_their_own_clock_and_the_thrust_comes_back()
        {
            // Two mains fully open since t=10; one is shot off at t=12. The one left settles at t=18 and the burn may run.
            var heldAt12 = new[] { K("main_call_ut", 8.0), K("open_ut", 10.0), K("open_count", 2.0), K("sample_ut", 11.5), K("sample_speed", 90.0), K("decel", 5.0) };
            var at12 = BlockCalls.BrakeTick(Tick(12, new object[] { Chute(ReentryBrake.Deployed) }, heldAt12));
            Assert.Equal(10.0, at12["open_ut"]); // the time is not lost with the chute
            Assert.Equal(1.0, at12["open_count"]);
            Assert.Equal(ReentryBrake.WhyChuteOpening, at12["zero_why"]); // still filling
            var heldAt18 = new[] { K("main_call_ut", 8.0), K("open_ut", (double)at12["open_ut"]), K("open_count", (double)at12["open_count"]), K("sample_ut", 17.8), K("sample_speed", 90.0), K("decel", 5.0) };
            var at18 = BlockCalls.BrakeTick(Tick(18, new object[] { Chute(ReentryBrake.Deployed) }, heldAt18));
            Assert.Equal(true, at18["settled"]);
            Assert.Equal("", at18["zero_why"]);
            Assert.Equal(0.7, at18["throttle"]);
        }

        [Fact]
        public void The_last_chute_lost_while_called_holds_no_thrust_back()
        {
            var tick = BlockCalls.BrakeTick(Tick(12, new object[0], K("main_call_ut", 11.5), K("open_ut", -1.0)));
            Assert.Equal("", tick["zero_why"]);
            Assert.Equal(0.7, tick["throttle"]);
            Assert.Equal("radar", tick["why"]);
            Assert.Equal(true, tick["start"]);
        }

        [Fact]
        public void A_chute_that_goes_after_the_rest_were_settled_keeps_them_settled()
        {
            var held = new[] { K("main_call_ut", 8.0), K("open_ut", 10.0), K("open_count", 2.0), K("settled", true), K("sample_ut", 11.5), K("sample_speed", 90.0), K("decel", 5.0) };
            var tick = BlockCalls.BrakeTick(Tick(12, new object[] { Chute(ReentryBrake.Deployed) }, held));
            Assert.Equal(true, tick["settled"]);
            Assert.Equal("", tick["zero_why"]);
        }

        // ---- The skip-out rules (#592), moved from reentry.ks ----

        private static IDictionary<string, object> Skip(double passes, double pe, double entryIn = 5000, bool auto = true, double maxDuration = 1900, double elapsed = 3)
        {
            return BlockCalls.EntrySkip(new Dictionary<string, object> { { "passes", passes }, { "pe", pe }, { "air_top", 70000.0 }, { "entry_in", entryIn }, { "auto", auto }, { "max_duration", maxDuration }, { "elapsed", elapsed } });
        }

        [Fact]
        public void A_skip_out_with_the_periapsis_still_inside_waits_for_the_next_pass_and_the_auto_deadline_grows()
        {
            Assert.Equal("", Skip(1, 34800)["refusal"]);
            var two = Skip(2, 34800);
            Assert.Equal("", two["refusal"]);
            Assert.Equal(6803.0, two["max_duration"]); // 3 s run, 5000 s to the next entry, plus 1800
        }

        [Fact]
        public void A_fourth_pass_fails_with_the_skipped_out_reason()
        {
            Assert.Equal("skipped out of the atmosphere 3 times (periapsis 34800 m)", Skip(3, 34800)["refusal"]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void A_skip_out_with_the_periapsis_above_the_atmosphere_fails_at_once(double passes)
        {
            Assert.Equal("skipped out: periapsis 71000 m is above the atmosphere", Skip(passes, 71000)["refusal"]);
        }

        [Fact]
        public void An_explicit_max_duration_stays_a_hard_cap_after_a_skip_out()
        {
            Assert.Equal(1900.0, Skip(1, 34800, auto: false)["max_duration"]);
        }

        [Fact]
        public void A_skip_out_onto_an_orbit_that_never_comes_back_fails()
        {
            Assert.Equal("skipped out onto an orbit that never reaches the atmosphere again (periapsis 34800 m)", Skip(1, 34800, entryIn: -1)["refusal"]);
        }

        [Fact]
        public void A_failed_call_refuses_nothing_and_sets_no_cap_the_wait_could_trip()
        {
            var failed = BlockCalls.EntrySkip(null);
            Assert.Equal("", failed["error"]);
            Assert.Equal("", failed["refusal"]);
            Assert.True((double)failed["max_duration"] > 1e6);
        }
    }
}
