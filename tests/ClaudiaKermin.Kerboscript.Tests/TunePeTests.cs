// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The pure helpers of the tune_pe Building Block, run on kOS.Safe through the real bundle. Flying it needs KSP: those are tests/blocks/tune_pe_*.</summary>
    public class TunePeTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        private static double Number(string expression)
        {
            return double.Parse(One(expression), CultureInfo.InvariantCulture);
        }

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("required|current|1000|50|auto", One("tune_pe_defaults():values:join(\"|\") + \"|\" + claudia_options(lexicon(), tune_pe_defaults(), \"auto\"):opt:max_duration"));
        }

        [Theory]
        [InlineData("lexicon(\"value\", 50000)", "")]
        [InlineData("lexicon(\"value\", 50000, \"patch\", \"next\", \"tolerance\", 500, \"max_dv\", 10)", "")]
        [InlineData("lexicon()", "value is required")]
        [InlineData("lexicon(\"value\", -5)", "value must be metres above 0, not -5")]
        [InlineData("lexicon(\"value\", \"high\")", "value must be metres above 0, not high")]
        [InlineData("lexicon(\"value\", 50000, \"patch\", \"later\")", "patch must be 'current' or 'next', not later")]
        [InlineData("lexicon(\"value\", 50000, \"tolerance\", 0)", "tolerance must be metres above 0, not 0")]
        [InlineData("lexicon(\"value\", 50000, \"max_dv\", \"lots\")", "max_dv must be m/s above 0, not lots")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("tune_pe_unreadable(claudia_options(" + options + ", tune_pe_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void Drop_empty_is_taken_and_passed_on_to_the_burn()
        {
            // #291: tune_pe takes drop_empty like every burning block, and exec_node gets it with the node.
            const string options = "claudia_options(lexicon(\"value\", 50000, \"drop_empty\", \"side tanks\"), tune_pe_defaults(), \"auto\")";
            Assert.Equal("||side tanks", One(options + ":error + \"|\" + tune_pe_unreadable(" + options + ":opt) + \"|\" + claudia_common_opts(" + options + ":opt):drop_empty:join(\",\")"));
            Assert.Equal("drop_empty must be a list of part names or tags", One("claudia_options(lexicon(\"value\", 50000, \"drop_empty\", 5), tune_pe_defaults(), \"auto\"):error"));
        }

        // ---- the next-patch burn before the sphere-of-influence change (#291) ----

        private static string Fit(double now, double nodeUt, double soiUt, double burnTime, double lead)
        {
            var call = "tune_pe_fit(" + string.Join(", ", new[] { now, nodeUt, soiUt, burnTime, lead }.Select(x => x.ToString(CultureInfo.InvariantCulture))) + ")";
            return One(call + ":error + \"|\" + " + call + ":ut + \"|\" + " + call + ":moved");
        }

        [Fact]
        public void A_next_patch_burn_that_ends_before_the_change_stays_halfway()
        {
            // Change in 2000 s, node at 1000 s, a 60 s burn led by 30 s: it ends at 1030 s.
            Assert.Equal("|1000|False", Fit(0, 1000, 2000, 60, 30));
        }

        [Fact]
        public void A_next_patch_burn_that_would_run_into_the_change_moves_earlier()
        {
            // Change in 1200 s, node at 600 s. A 900 s burn led by 400 s ends at 1100 s, 100 s before the change: it stays.
            Assert.Equal("|600|False", Fit(0, 600, 1200, 900, 400));
            // A 1000 s burn led by 450 s would end at 1150 s, only 50 s before: the node moves to 1200 - 60 - 550 = 590 s,
            // the burn starting at 140 s, time enough to turn and lead.
            Assert.Equal("|590|True", Fit(0, 600, 1200, 1000, 450));
        }

        [Fact]
        public void A_next_patch_burn_that_cannot_end_before_the_change_is_refused_with_why()
        {
            // Change in 1200 s, a 1300 s burn led by 600 s: ending 60 s before the change puts the node at 440 s and the start
            // at -160 s, under the 120 s a turn and lead need.
            Assert.Equal("the burn (1300 s from 600 s before its node) does not fit before the sphere-of-influence change in 1200 s|440|True",
                Fit(0, 600, 1200, 1300, 600));
        }

        [Fact]
        public void A_next_patch_burn_of_unknown_length_is_left_where_it_is()
        {
            Assert.Equal("|600|False", Fit(0, 600, 1200, -1, 0));
        }

        [Fact]
        public void An_unknown_option_is_refused_by_the_shared_check()
        {
            Assert.Equal("unknown option colour", One("claudia_options(lexicon(\"colour\", 1), tune_pe_defaults(), \"auto\"):error"));
        }

        // ---- the burn point ----

        [Fact]
        public void The_current_patch_burns_at_the_apoapsis()
        {
            Assert.Equal("|3000", One("tune_pe_burn_point(\"current\", 0.3, 3000, 500, 7000, false, false, 0):error + \"|\" + tune_pe_burn_point(\"current\", 0.3, 3000, 500, 7000, false, false, 0):in"));
        }

        [Fact]
        public void An_apoapsis_under_two_minutes_off_waits_for_the_next_one()
        {
            Assert.Equal("7060", One("tune_pe_burn_point(\"current\", 0.3, 60, 500, 7000, false, false, 0):in"));
        }

        [Fact]
        public void The_current_patch_refuses_an_escape_orbit()
        {
            Assert.Equal("the orbit escapes (ecc 1.2): no apoapsis to burn at", One("tune_pe_burn_point(\"current\", 1.2, 0, 0, 0, false, true, 5000):error"));
        }

        [Fact]
        public void The_current_patch_refuses_an_apoapsis_after_the_sphere_of_influence_change()
        {
            Assert.Equal("the apoapsis comes after the sphere-of-influence change (in 4000 s): use patch next", One("tune_pe_burn_point(\"current\", 0.9, 9000, 500, 20000, false, true, 4000):error"));
            Assert.Equal("", One("tune_pe_burn_point(\"current\", 0.9, 3000, 500, 20000, false, true, 4000):error"));
        }

        [Fact]
        public void The_next_patch_burns_halfway_to_the_sphere_of_influence_change()
        {
            Assert.Equal("|2500", One("tune_pe_burn_point(\"next\", 0.9, 9000, 500, 20000, false, true, 5000):error + \"|\" + tune_pe_burn_point(\"next\", 0.9, 9000, 500, 20000, false, true, 5000):in"));
        }

        [Theory]
        [InlineData(16000, "300,8000")]
        [InlineData(1200, "300,600")]
        [InlineData(1000, "500")]
        [InlineData(400, "200")]
        public void The_next_patch_tries_a_burn_near_the_start_as_well_as_halfway(double soiEta, string times)
        {
            // #538 (f623): halfway out the retrograde fix for a Mun encounter costs over 100 m/s; near the start it costs about 25.
            Assert.Equal(times, One("tune_pe_next_times(" + soiEta.ToString(CultureInfo.InvariantCulture) + "):join(\",\")"));
        }

        [Fact]
        public void The_next_patch_fails_without_one()
        {
            Assert.Equal("no next patch: the orbit stays in this sphere of influence", One("tune_pe_burn_point(\"next\", 0.1, 3000, 500, 7000, false, false, 0):error"));
        }

        [Fact]
        public void The_next_patch_fails_when_the_change_is_too_close_to_fly_a_burn()
        {
            Assert.Equal("the sphere-of-influence change is too close (200 s) to plan and fly a burn", One("tune_pe_burn_point(\"next\", 0.9, 9000, 500, 20000, false, true, 200):error"));
        }

        [Fact]
        public void An_unsafe_periapsis_before_the_apoapsis_burns_as_soon_as_a_burn_can_be_led()
        {
            // Inbound: periapsis in 1500 s, the next apoapsis 9000 s off. Burn 120 s on, not at the apoapsis.
            const string point = "tune_pe_burn_point(\"current\", 0.9, 9000, 1500, 20000, true, false, 0)";
            Assert.Equal("|120|True", One(point + ":error + \"|\" + " + point + ":in + \"|\" + " + point + ":early"));
        }

        [Fact]
        public void An_unsafe_periapsis_after_the_apoapsis_keeps_the_apoapsis_burn()
        {
            const string point = "tune_pe_burn_point(\"current\", 0.9, 3000, 9000, 20000, true, false, 0)";
            Assert.Equal("|3000|False", One(point + ":error + \"|\" + " + point + ":in + \"|\" + " + point + ":early"));
        }

        [Fact]
        public void A_safe_periapsis_keeps_the_apoapsis_burn_even_when_it_comes_first()
        {
            const string point = "tune_pe_burn_point(\"current\", 0.9, 9000, 1500, 20000, false, false, 0)";
            Assert.Equal("9000|False", One(point + ":in + \"|\" + " + point + ":early"));
        }

        [Fact]
        public void An_unsafe_periapsis_too_soon_to_burn_before_fails_with_the_reason()
        {
            Assert.Equal("the periapsis is inside the atmosphere or the surface and comes in 250 s, too soon to plan and fly a burn before it",
                One("tune_pe_burn_point(\"current\", 0.9, 9000, 250, 20000, true, false, 0):error"));
        }

        [Fact]
        public void An_early_burn_after_the_sphere_of_influence_change_is_refused()
        {
            Assert.Equal("the apoapsis comes after the sphere-of-influence change (in 60 s): use patch next",
                One("tune_pe_burn_point(\"current\", 0.9, 9000, 1500, 20000, true, true, 60):error"));
        }

        // ---- the burn ----

        [Fact]
        public void A_burn_off_the_apoapsis_matches_the_apoapsis_burn_where_the_two_meet()
        {
            var at = Number("tune_pe_burn_at(3.5316e12, 1600000, sqrt(3.5316e12 * (2 / 1600000 - 1 / 1200000)), 0, 700000):prograde");
            var ap = Number("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 700000):prograde");
            Assert.Equal(ap, at, 3);
        }

        [Fact]
        public void A_burn_on_the_way_in_lifts_the_periapsis_to_the_value()
        {
            // A 35 km x 8005 km Kerbin orbit (radius 600 km), inbound at 2000 km from the centre; rebuild the orbit after the burn.
            const double mu = 3.5316e12, rp = 635000, ra = 8605000, r = 2000000, want = 645000;
            var a = (rp + ra) / 2;
            var v = System.Math.Sqrt(mu * (2 / r - 1 / a));
            var h = System.Math.Sqrt(mu * a * (1 - System.Math.Pow((ra - rp) / (ra + rp), 2)));
            var vr = -System.Math.Sqrt(v * v - System.Math.Pow(h / r, 2));
            var dv = Number("tune_pe_burn_at(3.5316e12, 2000000, " + v.ToString("R", CultureInfo.InvariantCulture) + ", " + vr.ToString("R", CultureInfo.InvariantCulture) + ", 645000):prograde");
            Assert.True(dv > 0);
            var w = v + dv;
            var h2 = h * w / v;
            var e2 = w * w / 2 - mu / r;
            var a2 = -mu / (2 * e2);
            var ecc = System.Math.Sqrt(1 + 2 * e2 * h2 * h2 / (mu * mu));
            Assert.Equal(want, a2 * (1 - ecc), 0);
        }

        [Fact]
        public void A_value_not_below_the_burn_point_is_refused()
        {
            Assert.StartsWith("value is not below the burn point", One("tune_pe_burn_at(3.5316e12, 1600000, 500, 0, 1700000):error"));
        }

        [Fact]
        public void Lowering_the_periapsis_from_the_apoapsis_is_a_retrograde_burn_that_lands_on_the_new_orbit()
        {
            // 200 km x 1000 km Kerbin orbit, periapsis lowered to 100 km.
            var dv = Number("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 700000):prograde");
            Assert.InRange(dv, -60, -50);
            // The vis-viva check: speed at the apoapsis on the new orbit (sma 1.15e6) minus speed on the old (sma 1.2e6).
            var expected = System.Math.Sqrt(3.5316e12 * (2 / 1.6e6 - 1 / 1.15e6)) - System.Math.Sqrt(3.5316e12 * (2 / 1.6e6 - 1 / 1.2e6));
            Assert.Equal(expected, dv, 3);
        }

        [Fact]
        public void Raising_the_periapsis_is_a_prograde_burn_and_the_same_periapsis_costs_nothing()
        {
            Assert.True(Number("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 900000):prograde") > 0);
            Assert.Equal(0, Number("round(tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 800000):prograde, 6)"), 6);
        }

        [Fact]
        public void A_periapsis_above_the_apoapsis_is_refused()
        {
            Assert.Equal("value is above the apoapsis (1600000 m from the centre): a burn there can't lift the periapsis past it",
                One("tune_pe_apoapsis_burn(3.5316e12, 1600000, 1200000, 1700000):error"));
        }

        // ---- the budget ----

        [Theory]
        [InlineData(-35.2, 50, "")]
        [InlineData(50, 50, "")]
        [InlineData(-61.04, 50, "needs 61 m/s, over max_dv 50")]
        [InlineData(12, 10, "needs 12 m/s, over max_dv 10")]
        public void A_burn_over_max_dv_fails_with_what_it_needs(double dv, double maxDv, string reason)
        {
            Assert.Equal(reason, One("tune_pe_over_budget(" + dv.ToString(CultureInfo.InvariantCulture) + ", " + maxDv + ")"));
        }
    }
}
