// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// reentry's powered touchdown (#628, #824) and its chutes option, on kOS.Safe through the real bundle: the braking burn
    /// flown on a point mass falling through a Duna-like atmosphere, lighting engines, and which chutes the block may open on fake parts.
    /// </summary>
    public class ReentryPoweredTests
    {
        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        // ---- Options ----

        [Fact]
        public void Chutes_default_to_auto_and_15_m_s_is_the_limit_on_chutes_and_engines_alike()
        {
            Assert.Equal(new[] { "auto", "", "True", "False" },
                Run("local d is reentry_defaults(). print d:chutes. print reentry_unreadable(d).\n"
                    + "print reentry_touchdown_ok(14.9). print reentry_touchdown_ok(15.1).\n"));
        }

        [Theory]
        [InlineData("powered")]
        [InlineData("chutes")]
        public void Touchdown_given_fails_saying_it_is_automatic(string value)
        {
            var opts = "lexicon(\"touchdown\", \"" + value + "\")";
            Assert.Equal(new[] { "unknown option touchdown", "touchdown is no longer an option: reentry decides for itself whether to land on its engines" },
                Run("local checked is claudia_options(" + opts + ", reentry_defaults(), \"auto\"). print checked:error. print reentry_option_error(" + opts + ", checked:error).\n"));
            Assert.Contains("reentry decides for itself whether to land on its engines", BlockOptionCheck.Problems("local r is reentry(" + opts + ").").Single());
        }

        [Fact]
        public void Another_unknown_option_keeps_its_plain_reason()
        {
            Assert.Equal("unknown option hover", One("reentry_option_error(lexicon(\"hover\", 1), \"unknown option hover\")"));
            Assert.Empty(BlockOptionCheck.Problems("local r is reentry(lexicon(\"chutes\", \"none\"))."));
        }

        [Fact]
        public void The_docs_no_longer_offer_touchdown()
        {
            Assert.DoesNotContain("touchdown [", CoreLibrary.FullDocs(new[] { "reentry" }));
        }

        // ---- Lighting the engines ----

        /// <summary>Fake engines, uid and whether lit: lit logs each one activated, and a stage firing would need the stage command, which the block's source never has.</summary>
        private const string Engines =
            "local activated is list().\n"
            + "function eng {\n parameter uid, ignition.\n"
            + "  return lexicon(\"uid\", uid, \"ignition\", ignition, \"activate\", { activated:add(uid). }).\n}\n"
            + "local engs is list(eng(\"low\", false), eng(\"high\", false), eng(\"high2\", false), eng(\"lit\", true)).\n";

        [Fact]
        public void Only_the_engines_Core_chose_are_lit_part_by_part_and_a_lit_one_is_left()
        {
            Assert.Equal(new[] { "high,high2", "high,high2" },
                Run(Engines + "reentry_light(engs, list(\"high\", \"high2\", \"lit\")). print activated:join(\",\").\n"
                    + "for e in engs { if e:uid:startswith(\"high\") { set e:ignition to true. } }\n"
                    + "reentry_light(engs, list(\"high\", \"high2\")). print activated:join(\",\").\n"));
        }

        [Fact]
        public void The_block_never_stages_and_lights_the_chosen_engines_then_the_spare_on_a_flameout()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            Assert.DoesNotContain("stage.", body.Replace("_stage.", "").Replace(":stage.", ""));
            Assert.DoesNotContain("claudia_autostage", source);
            Assert.DoesNotContain("claudia_stage_into_engines", source);
            var ask = body.IndexOf("addons:claudia:entryfinish(", StringComparison.Ordinal);
            var light = body.IndexOf("reentry_light(ship:engines, finish:engines).", StringComparison.Ordinal);
            Assert.True(ask > 0 && ask < light);
            Assert.True(light < body.IndexOf("reentry_separation(", StringComparison.Ordinal));
            Assert.Contains("if ship:availablethrust <= 0 { reentry_light(ship:engines, finish:spare). }", body);
            // Braking ticks don't autostage, and running dry isn't an abort: burning false, and only the touchdown's speed fails it.
            Assert.Contains("claudia_tick(ctx, tset, false)", body);
            Assert.Contains("if not reentry_touchdown_ok(last_speed) {", body);
        }

        [Fact]
        public void Chutes_with_a_list_all_or_a_tag_fails_saying_what_is_allowed()
        {
            foreach (var value in new[] { "list(\"main\")", "\"all\"", "\"main\"", "3" })
            {
                Assert.StartsWith("chutes must be 'auto' or 'none', not ",
                    Run("local d is reentry_defaults(). set d:chutes to " + value + ". print reentry_unreadable(d).\n").Single());
            }
            Assert.Equal(new[] { "", "" },
                Run("local d is reentry_defaults(). print reentry_unreadable(d). set d:chutes to \"none\". print reentry_unreadable(d).\n"));
        }

        // ---- Chutes (ADR 0019) ----

        /// <summary>
        /// The Duna land-and-return's chutes, on fake parts: "d" (a drogue) and "m" (a main) in stage 2, "p" (a main) in stage 0, and a fuel
        /// tank. armed logs each chute opened or cut; a chute reads as stowed until opened, and open until cut. parts is the craft.
        /// </summary>
        private const string Chutes =
            "local armed is list().\n"
            + "local cuts is list().\n"
            + "function chute {\n parameter uid, name, stage_.\n"
            + "  local m is lexicon(\"hasevent\", { parameter e. if e = \"deploy chute\" { return not armed:contains(uid). } return e = \"cut chute\" and armed:contains(uid) and not cuts:contains(uid). }).\n"
            + "  set m:doevent to { parameter e. if e = \"deploy chute\" { armed:add(uid). } else { cuts:add(uid). } }.\n"
            + "  return lexicon(\"uid\", uid, \"name\", name, \"stage\", stage_, \"title\", \"Chute \" + uid, \"hasmodule\", { parameter n. return n = \"ModuleParachute\". }, \"getmodule\", { parameter n. return m. }).\n}\n"
            + "local tank is lexicon(\"uid\", \"t\", \"name\", \"fuelTank\", \"stage\", 2, \"hasmodule\", { parameter n. return false. }).\n"
            + "local d is chute(\"d\", \"parachuteDrogue\", 2).\n"
            + "local m is chute(\"m\", \"parachuteSingle\", 2).\n"
            + "local p is chute(\"p\", \"parachuteSingle\", 0).\n"
            + "local parts is list(d, tank, m, p).\n"
            + "local group is lexicon(\"error\", \"\", \"last\", false, \"stage\", 2, \"uids\", list(\"d\", \"m\")).\n";

        [Fact]
        public void Within_a_group_drogues_open_before_mains_and_nothing_outside_it_is_armed()
        {
            Assert.Equal(new[] { "True", "drogues", "Chute d", "d", "mains", "Chute m", "d,m" },
                Run(Chutes + "print reentry_has_drogues(parts, group).\n"
                    + "print reentry_chute_stage(\"safe\", true, false, false, 400, 20000, 0.03).\n"
                    + "print reentry_open_chutes(parts, true, group):join(\",\"). print armed:join(\",\").\n"
                    + "print reentry_chute_stage(\"safe\", true, true, false, 200, 5000, 0.05).\n"
                    + "print reentry_open_chutes(parts, false, group):join(\",\"). print armed:join(\",\").\n"));
        }

        [Fact]
        public void A_last_entry_opens_every_stowed_chute_aboard()
        {
            Assert.Equal(new[] { "Chute m,Chute p" },
                Run(Chutes + "set group:last to true. print reentry_open_chutes(parts, false, group):join(\",\").\n"));
        }

        [Fact]
        public void A_restaged_chute_is_read_at_the_opening_so_it_joins_its_new_group()
        {
            Assert.Equal(new[] { "Chute m,Chute p" },
                Run(Chutes + "set p:stage to 2. print reentry_open_chutes(parts, false, group):join(\",\").\n"));
            // And one restaged out of the group stays shut.
            Assert.Equal(new[] { "", "" },
                Run(Chutes + "set m:stage to 0. print reentry_open_chutes(parts, false, group):join(\",\"). print armed:join(\",\").\n"));
        }

        [Fact]
        public void A_repacked_chute_reads_as_stowed_and_opens_again()
        {
            Assert.Equal(new[] { "Chute m", "", "Chute m" },
                Run(Chutes + "print reentry_open_chutes(parts, false, group):join(\",\").\n"
                    + "print reentry_open_chutes(parts, false, group):join(\",\").\n"
                    + "armed:remove(armed:find(\"m\")). print reentry_open_chutes(parts, false, group):join(\",\").\n"));
        }

        [Fact]
        public void Chutes_dropped_after_the_choice_are_read_as_gone_and_the_block_works_from_what_is_aboard()
        {
            // The stage-2 chutes leave with a drop: the group has gone, nothing has been opened, so the block asks again and opens the pod's.
            Assert.Equal(new[] { "False", "True", "Chute p", "p" },
                Run(Chutes + "print reentry_group_gone(group, parts, list()).\n"
                    + "set parts to list(p, tank).\n"
                    + "print reentry_group_gone(group, parts, list()).\n"
                    + "print reentry_open_chutes(parts, false, reentry_group_of(\"auto\", lexicon(\"error\", \"\", \"last\", false, \"stage\", 0, \"uids\", list(\"p\")), parts)):join(\",\").\n"
                    + "print armed:join(\",\").\n"));
        }

        [Fact]
        public void A_skip_out_opens_no_new_group()
        {
            // The first pass opened all of stage 2. The next pass reads none of it stowed, but has opened some, so it keeps to that group: the pod's stays shut.
            Assert.Equal(new[] { "False", "False", "", "d,m" },
                Run(Chutes + "local used is list().\n"
                    + "for t in reentry_open_chutes(parts, true, group) { used:add(t). }\n"
                    + "for t in reentry_open_chutes(parts, false, group) { used:add(t). }\n"
                    + "print reentry_group_gone(group, parts, used).\n"
                    + "print reentry_has_drogues(parts, group).\n"
                    + "print reentry_open_chutes(parts, false, group):join(\",\").\n"
                    + "print armed:join(\",\").\n"));
        }

        [Fact]
        public void Open_chutes_are_cut_after_landing_on_a_non_last_entry_only()
        {
            Assert.Equal(new[] { "", "", "Chute d,Chute m", "d,m", "" },
                Run(Chutes + "reentry_open_chutes(parts, true, group). reentry_open_chutes(parts, false, group).\n"
                    + "print reentry_cut_chutes(parts, true):join(\",\"). print cuts:join(\",\").\n"
                    + "print reentry_cut_chutes(parts, false):join(\",\"). print cuts:join(\",\").\n"
                    // One stock has already cut, or one never opened, offers no event: the cut does nothing.
                    + "print reentry_cut_chutes(parts, false):join(\",\").\n"));
        }

        [Fact]
        public void An_answer_in_error_opens_every_chute_aboard_and_choice_none_opens_nothing()
        {
            Assert.Equal(new[] { "d,m,p", "True", "0", "False" },
                Run(Chutes + "local failed is reentry_group_of(\"auto\", lexicon(\"error\", \"no snapshot\"), parts).\n"
                    + "print failed:uids:join(\",\"). print failed:last.\n"
                    + "local none is reentry_group_of(\"none\", group, parts). print none:uids:length. print none:last.\n"));
        }

        // ---- The stopping distance ----

        [Fact]
        public void With_no_drag_the_stopping_distance_is_the_kinematic_one()
        {
            // (100^2 - 2^2) / (2 x 10) = 499.8 m, over twenty 50 m steps.
            Assert.Equal("499.8", One("round(reentry_stop_distance(100, 10, list(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0), 50, 2), 2)"));
        }

        [Fact]
        public void With_even_drag_the_stopping_distance_is_the_closed_form_one()
        {
            // net 10 m/s^2, k 1e-3 /m from 100 m/s to 0: ln(1 + k v^2 / net) / (2 k) = 500 ln 2 = 346.57 m.
            Assert.Equal("346.57", One("round(reentry_stop_distance(100, 10, list(1e-3,1e-3,1e-3,1e-3,1e-3,1e-3,1e-3,1e-3,1e-3,1e-3), 100, 0), 2)"));
        }

        [Fact]
        public void A_fall_the_engines_cant_stop_in_the_height_left_is_minus_one_so_braking_starts_once_the_air_has_done_its_work()
        {
            Assert.Equal(new[] { "-1", "-1", "True", "False", "True", "True", "False" },
                Run("print reentry_stop_distance(100, -1, list(0,0,0,0), 50, 2).\n"
                    + "print reentry_brake_height(300, 12, 2.94, list(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0), 1000).\n"
                    + "print reentry_brake_due(1000, -1, 300).\n"
                    // High on a shallow path at orbital speed: not yet. Then under the shield's speed, or under its height, or simply within the stopping distance.
                    + "print reentry_brake_due(40000, -1, 1200).\n"
                    + "print reentry_brake_due(40000, -1, 390).\n"
                    + "print reentry_brake_due(900, 1000, 1200).\n"
                    + "print reentry_brake_due(1100, 1000, 300).\n"));
        }

        [Fact]
        public void Drag_steps_scale_half_the_drag_now_by_the_pressure_below()
        {
            // 10 m/s^2 at 100 m/s: k = 0.5 x 10 / 100^2 = 5e-4 /m now, times the pressure at each 100 m step's middle over the
            // pressure now: 1.05 at 50 m down, 1.95 at 950 m.
            Assert.Equal(new[] { "20", "0.00053", "0.00098", "0" },
                Run("local ks is reentry_drag_steps(10, 100, 0.05, 2000, { parameter d. return 0.05 + 0.05 * d / 1000. }).\n"
                    + "print ks:length. print round(ks[0], 5). print round(ks[9], 5).\n"
                    + "print reentry_drag_steps(0, 100, 0.05, 2000, { parameter d. return 1. })[5].\n"));
        }

        // ---- The braking burn, simulated ----

        // A point mass falling straight down on Duna (g 2.94 m/s^2; air pressure 0.0667 atm at the ground falling by e every
        // 5 km), centre of mass 3 m above its feet. Drag is k v^2, k at the ground ground_k (1/m) and scaled with pressure, times
        // cd_low below 250 m/s (drag falling off as the craft goes subsonic, which the block's measurement can't see coming).
        // Each 0.05 s tick it flies reentry's powered touchdown: the brake height from the drag felt, sampled every 0.25 s
        // until braking begins, then reentry_brake_throttle on a_max m/s^2 of thrust (no mass lost: the thrust only grows).
        // Prints the speed at touchdown, the radar altitude braking began at and the delta-v burned. fuel: the delta-v (m/s) the craft has.
        private const string Simulation = @"
function sim {
  parameter y0, v0, a_max, ground_k, cd_low, fuel is 1e9.
  local e is 2.718281828459045. local h_scale is 5000. local p0 is 0.0667. local g is 2.94. local com is 3.
  local y is y0. local v is v0.
  local t is 0. local dt is 0.05. local next_sample is 0.
  local braking is false. local brake_at is -1. local brake_h is 0. local used is 0. local tset is 0. local last is v.
  until y <= 0 or t > 3000 {
    local p is p0 * e ^ (-y / h_scale).
    local k is ground_k * p / p0.
    if v < 250 { set k to k * cd_low. }
    local drag is k * v * abs(v).
    local radar is y + com.
    if not braking {
      if t >= next_sample {
        local here is y.
        local ks is reentry_drag_steps(abs(drag), abs(v), p, radar, { parameter d. return p0 * e ^ (-(here + com - d) / h_scale). }).
        set brake_h to reentry_brake_height(v, a_max, g, ks, radar).
        set next_sample to t + 0.25.
      }
      if reentry_brake_due(radar, brake_h, v) { set braking to true. set brake_at to radar. }
    }
    if braking { set tset to reentry_brake_throttle(radar, v, a_max, g). }
    if used >= fuel { set tset to 0. }
    set last to v.
    set v to v + (g - drag - tset * a_max) * dt.
    set y to y - v * dt.
    set used to used + tset * a_max * dt.
    set t to t + dt.
  }
  print round(last, 2). print round(brake_at). print round(used). print reentry_touchdown_ok(last).
}
";

        private sealed class Landing
        {
            public double TouchdownSpeed, BrakeAt, DvUsed;
            public bool Ok;
        }

        private static Landing Fly(double y0, double v0, double aMax, double groundK, double cdLow, double fuel = 1e9)
        {
            var printed = Run(Simulation + string.Format(CultureInfo.InvariantCulture, "sim({0}, {1}, {2}, {3}, {4}, {5}).", y0, v0, aMax, groundK, cdLow, fuel));
            double D(int i) => double.Parse(printed[i], CultureInfo.InvariantCulture);
            return new Landing { TouchdownSpeed = D(0), BrakeAt = D(1), DvUsed = D(2), Ok = printed[3] == "True" };
        }

        [Theory]
        // No chutes: terminal about 120 m/s at the ground (k 2.04e-4), drag dropping to 60% subsonic; 4 g-Duna of thrust, then
        // a weak lander (1.7, from near its terminal speed: at 250 m/s it can't stop in 10 km) and a stiff one (8).
        [InlineData(10000, 250, 12, 2.04e-4, 0.6)]
        [InlineData(10000, 150, 5, 2.04e-4, 0.6)]
        [InlineData(10000, 250, 24, 2.04e-4, 0.6)]
        // Drag exactly as measured all the way down.
        [InlineData(10000, 250, 12, 2.04e-4, 1)]
        // No air worth the name: the kinematic case.
        [InlineData(5000, 150, 8, 0, 1)]
        public void Duna_with_no_chutes_a_powered_touchdown_lands_under_the_limit(double y0, double v0, double aMax, double groundK, double cdLow)
        {
            var landing = Fly(y0, v0, aMax, groundK, cdLow);
            Assert.True(landing.BrakeAt > 0, "braking never began");
            Assert.True(landing.TouchdownSpeed > 0 && landing.TouchdownSpeed < 6, "touchdown at " + landing.TouchdownSpeed + " m/s (braking from " + landing.BrakeAt + " m)");
        }

        [Fact]
        public void Short_of_fuel_it_burns_to_empty_and_only_a_touchdown_over_the_limit_fails()
        {
            // Under chutes that leave it at 12 m/s (k 0.0204): 5 m/s of fuel is all burned and it still lands under the limit, so the block passes.
            var close = Fly(4000, 60, 12, 0.0204, 1, 5);
            Assert.True(close.DvUsed >= 5, "burned " + close.DvUsed);
            Assert.True(close.TouchdownSpeed > 2 && close.Ok, "touchdown at " + close.TouchdownSpeed + " m/s");
            // With no chute and the same fuel, the fall is too fast: that alone fails it.
            var fast = Fly(4000, 60, 12, 2.04e-4, 1, 5);
            Assert.True(fast.DvUsed >= 5, "burned " + fast.DvUsed);
            Assert.False(fast.Ok, "touchdown at " + fast.TouchdownSpeed + " m/s");
        }

        [Fact]
        public void Duna_under_one_chute_brakes_late_and_burns_little()
        {
            // One chute: terminal about 25 m/s at the ground (k 4.7e-3).
            var underChute = Fly(4000, 60, 12, 4.7e-3, 1);
            var noChute = Fly(4000, 60, 12, 0, 1);
            Assert.True(underChute.TouchdownSpeed > 0 && underChute.TouchdownSpeed < 6, "touchdown at " + underChute.TouchdownSpeed + " m/s");
            Assert.True(underChute.BrakeAt < noChute.BrakeAt, "the chute's drag should let braking start lower: " + underChute.BrakeAt + " m against " + noChute.BrakeAt + " m");
            Assert.True(underChute.DvUsed < noChute.DvUsed, "the chute should save delta-v: " + underChute.DvUsed + " against " + noChute.DvUsed + " m/s");
        }
    }
}
