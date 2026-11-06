// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// reentry's touchdown "powered" and its chutes option (#628), on kOS.Safe through the real bundle: the braking burn
    /// flown on a point mass falling through a Duna-like atmosphere, and which chutes the block may open on fake parts.
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
        public void Touchdown_defaults_to_chutes_and_chutes_to_auto_so_a_Kerbin_entry_flies_as_before()
        {
            Assert.Equal(new[] { "chutes", "auto", "", "True", "False" },
                Run("local d is reentry_defaults(). print d:touchdown. print d:chutes. print reentry_unreadable(d).\n"
                    + "print reentry_touchdown_ok(14.9). print reentry_touchdown_ok(15.1).\n"));
        }

        [Fact]
        public void A_powered_touchdown_fails_over_its_own_soft_limit()
        {
            Assert.Equal(new[] { "True", "False", "6" },
                Run("print reentry_touchdown_ok(5.9, true). print reentry_touchdown_ok(6.1, true). print reentry_touchdown_max(true).\n"));
        }

        [Fact]
        public void Unknown_touchdown_and_chutes_values_are_refused_and_a_bare_tag_is_a_list_of_one()
        {
            Assert.Equal(new[]
            {
                "touchdown must be 'chutes' or 'powered', not 'hover'",
                "chutes must be 'auto', 'none', 'all' or a list of part tags",
                "chutes must be 'auto', 'none', 'all' or a list of part tags",
                "", "List", "main",
            }, Run("local d is reentry_defaults(). set d:touchdown to \"hover\". print reentry_unreadable(d).\n"
                + "set d to reentry_defaults(). set d:chutes to 3. print reentry_unreadable(d).\n"
                + "set d to reentry_defaults(). set d:chutes to list(3). print reentry_unreadable(d).\n"
                + "set d to reentry_defaults(). set d:chutes to \"main\". print reentry_unreadable(d). print d:chutes:typename. print d:chutes[0].\n"));
        }

        // ---- Chutes ----

        /// <summary>
        /// Two chutes, as on a Duna lander short of chutes: "a" (tag main) and "b" (tag spare), each logging the events fired on it
        /// into armed, and a fuel tank. parts is the craft, chutes its parachutes.
        /// </summary>
        private const string Chutes =
            "local armed is list().\n"
            + "function chute {\n parameter uid, name, tag.\n"
            + "  local m is lexicon(\"hasevent\", { parameter e. return e = \"deploy chute\" and not armed:contains(uid). }).\n"
            + "  set m:doevent to { parameter e. armed:add(uid). }.\n"
            + "  return lexicon(\"uid\", uid, \"name\", name, \"tag\", tag, \"title\", \"Chute \" + uid, \"hasmodule\", { parameter n. return n = \"ModuleParachute\". }, \"getmodule\", { parameter n. return m. }).\n}\n"
            + "local tank is lexicon(\"uid\", \"t\", \"name\", \"fuelTank\", \"tag\", \"\", \"hasmodule\", { parameter n. return false. }).\n"
            + "local parts is list(chute(\"a\", \"parachuteSingle\", \"main\"), tank, chute(\"b\", \"parachuteRadial\", \"spare\")).\n"
            + "local chutes is reentry_chutes_in(parts).\n"
            + "local everything is lexicon(\"error\", \"\", \"all\", true, \"uids\", list()).\n";

        [Fact]
        public void With_one_of_two_chutes_allowed_the_other_is_never_armed()
        {
            // The Chute Allocation keeps b for a later entry: auto opens a alone, mains and drogues alike.
            Assert.Equal(new[] { "a", "Chute a", "", "a", "False" },
                Run(Chutes + "local set is reentry_chute_set(\"auto\", chutes, lexicon(\"error\", \"\", \"all\", false, \"uids\", list(\"a\"))).\n"
                    + "print set:uids:join(\",\"). print reentry_open_chutes(parts, false, set:uids):join(\",\").\n"
                    + "print reentry_open_chutes(parts, true, set:uids):join(\",\"). print armed:join(\",\"). print reentry_has_drogues(parts, set:uids).\n"));
        }

        [Fact]
        public void Auto_with_no_allocation_keeps_every_chute_as_reentry_did_before()
        {
            Assert.Equal(new[] { "a,b", "a,b", "a,b" },
                Run(Chutes + "print reentry_chute_set(\"auto\", chutes, everything):uids:join(\",\").\n"
                    + "print reentry_chute_set(\"auto\", chutes, lexicon(\"error\", \"no snapshot\")):uids:join(\",\").\n"
                    + "reentry_open_chutes(parts, false, reentry_chute_set(\"auto\", chutes, everything):uids). print armed:join(\",\").\n"));
        }

        [Fact]
        public void The_plans_list_none_or_all_overrides_the_allocation()
        {
            Assert.Equal(new[] { "b", "", "a,b", "chutes: no parachute is tagged 'drogue'", "" },
                Run(Chutes + "local only_a is lexicon(\"error\", \"\", \"all\", false, \"uids\", list(\"a\")).\n"
                    + "print reentry_chute_set(list(\"spare\"), chutes, only_a):uids:join(\",\").\n"
                    + "print reentry_chute_set(\"none\", chutes, only_a):uids:join(\",\").\n"
                    + "print reentry_chute_set(\"all\", chutes, only_a):uids:join(\",\").\n"
                    + "print reentry_chute_set(list(\"drogue\"), chutes, only_a):error.\n"
                    + "reentry_open_chutes(parts, false, reentry_chute_set(\"none\", chutes, only_a):uids). print armed:join(\",\").\n"));
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
        public void A_fall_the_engines_cant_stop_in_the_height_left_is_minus_one_so_braking_starts_now()
        {
            Assert.Equal(new[] { "-1", "-1", "True" },
                Run("print reentry_stop_distance(100, -1, list(0,0,0,0), 50, 2).\n"
                    + "print reentry_brake_height(300, 12, 2.94, list(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0), 1000).\n"
                    + "print reentry_brake_due(1000, -1).\n"));
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
        // Prints the speed at touchdown, the radar altitude braking began at and the delta-v burned.
        private const string Simulation = @"
function sim {
  parameter y0, v0, a_max, ground_k, cd_low.
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
      if reentry_brake_due(radar, brake_h) { set braking to true. set brake_at to radar. }
    }
    if braking { set tset to reentry_brake_throttle(radar, v, a_max, g). }
    set last to v.
    set v to v + (g - drag - tset * a_max) * dt.
    set y to y - v * dt.
    set used to used + tset * a_max * dt.
    set t to t + dt.
  }
  print round(last, 2). print round(brake_at). print round(used).
}
";

        private sealed class Landing
        {
            public double TouchdownSpeed, BrakeAt, DvUsed;
        }

        private static Landing Fly(double y0, double v0, double aMax, double groundK, double cdLow)
        {
            var printed = Run(Simulation + string.Format(CultureInfo.InvariantCulture, "sim({0}, {1}, {2}, {3}, {4}).", y0, v0, aMax, groundK, cdLow));
            double D(int i) => double.Parse(printed[i], CultureInfo.InvariantCulture);
            return new Landing { TouchdownSpeed = D(0), BrakeAt = D(1), DvUsed = D(2) };
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
