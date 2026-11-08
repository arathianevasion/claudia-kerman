// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #825: where the Mission Plan guard sits in the three callers of claudia_fire_drop. The comparison is Core's (DropGuardTests); a block
    /// needs a ship, so these read the order of the source, as the other drop tests do.
    /// </summary>
    public class DropGuardScriptTests
    {
        private static string Source(string file)
        {
            return CoreLibrary.Files.Single(f => f.Name == file).Source;
        }

        private static string Function(string file, string header)
        {
            var source = Source(file);
            var start = source.IndexOf(header, StringComparison.Ordinal);
            Assert.True(start >= 0, header);
            return source.Substring(start, source.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
        }

        [Fact]
        public void The_comparison_is_cores_and_the_estimate_is_kerboscripts()
        {
            var body = Function("claudia_lib", "function claudia_drop_refusal {");
            Assert.Contains("addons:claudia:dropcheck(", body);
            Assert.Contains("drop_parts_kept(plan)", body);
            Assert.Contains(":reason", body);
        }

        // A fake craft: a kept tank (5 t, 2 t of it propellant) with kept_engine, a tank that goes (5 t, 3 t of it propellant) with gone_engine,
        // gone_engine or kept_engine burning as given; DROPCHECK prints the figures it was given and refuses nothing.
        // isp is what an engine reads while burning (claudia_engine_isp), vac what it reads in a vacuum (the kept craft's mean): they differ per
        // engine in the tests that need to tell the two apart. A flamed-out engine is lit with no thrust.
        private static string Engine(string uid, bool ignition, double thrust, double isp = 300, double vac = 300) =>
            "lexicon(\"uid\", \"" + uid + "\", \"ignition\", " + (ignition ? "true" : "false") + ", \"availablethrust\", " + thrust + ", \"isp\", " + isp + ", "
            + "\"consumedresources\", lexicon(\"Liquid Fuel\", 1, \"Oxidizer\", 1), "
            + "\"maxpossiblethrustat\", { parameter a. return 60. }, \"ispat\", { parameter a. return " + vac + ". })";

        private static string Craft(bool goneBurns, bool keptBurns, string keptEngine = null, string goneEngine = null, string extraEngines = "")
        {
            string Burning(string uid, bool burning) => Engine(uid, burning, burning ? 60 : 0);
            keptEngine = keptEngine ?? Burning("kept_engine", keptBurns);
            goneEngine = goneEngine ?? Burning("gone_engine", goneBurns);
            string Part(string uid, double mass, double fuel) =>
                "lexicon(\"uid\", \"" + uid + "\", \"mass\", " + mass + ", \"resources\", "
                + "list(lexicon(\"name\", \"LiquidFuel\", \"amount\", " + fuel + ", \"density\", 0.005), lexicon(\"name\", \"ElectricCharge\", \"amount\", 100, \"density\", 0)))";
            return "global ship is lexicon(\"mass\", 10, \"engines\", list(" + keptEngine + ", " + goneEngine + extraEngines + "), "
                + "\"parts\", list(" + Part("kept_tank", 5, 400) + ", " + Part("gone_tank", 5, 600) + ")).\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"dropcheck\", { parameter ask. "
                + "for key in list(\"before\", \"left\", \"spent\", \"whole\", \"kept\", \"prop\", \"isp\", \"gone_prop\", \"burners_kept\") { print key + \" \" + round(ask[key], 1). } "
                + "return lexicon(\"reason\", \"refused\"). })).\n"
                + "local plan is lexicon(\"gone\", list(lexicon(\"uid\", \"gone_tank\"), lexicon(\"uid\", \"gone_engine\"))).\n";
        }

        [Fact]
        public void A_stage_burning_itself_out_gives_core_its_propellant_as_the_burns_and_the_kept_craft_as_it_is()
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(Craft(goneBurns: true, keptBurns: false) + "print claudia_drop_refusal(plan, 300).\n"));
            // before: 300 s on the whole 10 t with its 5 t of propellant, so the drop takes 536 m/s and the guard is on. left: 300 s on 5 t with 2 t.
            Assert.Equal(new[] { "before 2039.2", "left 1502.8", "spent 300", "whole 10", "kept 5", "prop 2", "isp 300", "gone_prop 3", "burners_kept 0", "refused" }, printed.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_burning_engine_that_stays_is_counted_for_core_to_have_the_kept_tanks_pay(bool goneBurns)
        {
            // gone_prop is a raw figure, the same propellant in what goes; AfterBurn ignores it once a burner stays (Core's AnyBurningEngineThatStays test).
            var printed = KosHarness.Run(CoreLibrary.Bundle(Craft(goneBurns, keptBurns: true) + "print claudia_drop_refusal(plan, 300).\n"));
            Assert.Contains("burners_kept 1", printed);
            Assert.Contains("gone_prop 3", printed);
            Assert.Contains("prop 2", printed);
        }

        private static double Harmonic(double a, double b) => 2 * a * b / (a + b);

        private static string[] Asked(string craft)
        {
            return KosHarness.Run(CoreLibrary.Bundle(craft + "print claudia_drop_refusal(plan, 300).\n")).ToArray();
        }

        [Fact]
        public void The_burn_is_costed_at_the_isp_of_the_lit_engines_and_the_kept_craft_at_the_isp_of_the_kept_ones()
        {
            // Lit: kept 280 s (330 in vacuum), gone 350 s (400 in vacuum). isp is claudia_engine_isp, the thrust-weighted harmonic mean of the lit: 311.1.
            // Passing the kept mean (330) in its place, or the kept mean's left in `before`, changes these lines.
            var printed = Asked(Craft(true, true, Engine("kept_engine", true, 60, 280, 330), Engine("gone_engine", true, 60, 350, 400)));
            // before: the whole craft's vacuum mean (361.6 s, harmonic) on 10 t with 5 t propellant; left: 330 s on 5 t with 2 t.
            Assert.Equal("before " + System.Math.Round(Harmonic(330, 400) * 9.80665 * System.Math.Log(2), 1), printed[0]);
            Assert.Equal("left " + System.Math.Round(330 * 9.80665 * System.Math.Log(5.0 / 3), 1), printed[1]);
            Assert.Contains("isp " + System.Math.Round(Harmonic(280, 350), 1), printed);
            Assert.Contains("burners_kept 1", printed);
        }

        [Fact]
        public void A_flamed_out_engine_is_not_burning_for_the_isp_or_the_count()
        {
            // Lit with no thrust, at 999 s: the kept engine is lit and burning at 300 s, the flamed-out one stays but is not a burner.
            var flamed = Engine("flameout_engine", true, 0, 999, 999);
            var printed = Asked(Craft(true, false, extraEngines: ", " + flamed));
            Assert.Contains("isp 300", printed);
            Assert.Contains("burners_kept 0", printed);
            Assert.Contains("gone_prop 3", printed);
        }

        [Fact]
        public void With_no_engine_lit_the_check_has_no_burning_isp_and_no_burner_and_no_propellant_to_pay_with()
        {
            var printed = Asked(Craft(false, false));
            Assert.Contains("isp 0", printed);
            Assert.Contains("burners_kept 0", printed);
            Assert.Contains("gone_prop 0", printed);
        }

        [Fact]
        public void With_no_burn_to_come_the_check_asks_with_spent_0()
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(Craft(goneBurns: true, keptBurns: false) + "print claudia_drop_refusal(plan).\n"));
            Assert.Contains("spent 0", printed);
        }

        [Fact]
        public void A_drop_is_refused_before_any_decoupler_fires_and_the_reason_goes_back_as_the_failure()
        {
            var body = Function("claudia_lib", "function claudia_fire_drop {");
            var refusal = body.IndexOf("claudia_drop_refusal(plan)", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(refusal < body.IndexOf("claudia_fire_decoupler(", StringComparison.Ordinal));
            Assert.Contains("return refused.", body);
        }

        [Fact]
        public void Drop_parts_fails_with_the_reason_whatever_allow_no_engine_says()
        {
            var source = Source("drop_parts");
            // The block's only way to fire is claudia_fire_drop, whose failure is the block's, and nothing there looks at allow_no_engine.
            Assert.Contains("claudia_finish(ctx, not_fired = \"\", not_fired, extra)", source);
            Assert.Equal(1, source.Split(new[] { "claudia_fire_drop(" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("allow_no_engine", Function("claudia_lib", "function claudia_drop_refusal {"));
            Assert.DoesNotContain("allow_no_engine", Function("claudia_lib", "function claudia_fire_drop {"));
        }

        [Fact]
        public void Deorbit_refuses_a_separate_before_the_search_and_the_burn()
        {
            var body = Function("deorbit", "function deorbit {");
            var refusal = body.IndexOf("claudia_drop_refusal(separation)", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(refusal < body.IndexOf("addons:claudia:deorbitburn(", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("claudia_burn(ctx, nd)", StringComparison.Ordinal));
            Assert.True(body.IndexOf("claudia_burn(ctx, nd)", StringComparison.Ordinal) < body.IndexOf("claudia_fire_drop(ctx, separation)", StringComparison.Ordinal));
            Assert.Contains("\"separate: \" + refused", body);
        }

        [Fact]
        public void Deorbit_checks_the_separate_again_with_the_burns_delta_v_after_the_search_and_before_the_node()
        {
            var body = Function("deorbit", "function deorbit {");
            var late = body.IndexOf("claudia_drop_refusal(separation, found:dv)", StringComparison.Ordinal);
            Assert.True(late > body.IndexOf("addons:claudia:deorbitburn(", StringComparison.Ordinal));
            Assert.True(late > body.IndexOf("return claudia_finish(ctx, false, found:error, extra)", StringComparison.Ordinal));
            Assert.True(late < body.IndexOf("add nd.", StringComparison.Ordinal));
            // A refusal returns before the node and before burn_ut is set, which stays -1; the check after the burn stays.
            Assert.True(late < body.IndexOf("set extra:burn_ut", StringComparison.Ordinal));
            Assert.Contains("\"separate: \" + late", body);
            Assert.Contains("claudia_fire_drop(ctx, separation)", body);
            Assert.Contains("parameter plan, spent is 0.", Function("claudia_lib", "function claudia_drop_refusal {"));
        }

        [Fact]
        public void A_drop_the_plan_does_not_need_flies_as_before()
        {
            // The early check keeps its one argument and the late one only returns on a reason, so a drop the plan can spare goes on to the node.
            var body = Function("deorbit", "function deorbit {");
            Assert.Contains("local refused is claudia_drop_refusal(separation).", body);
            Assert.Contains("if late <> \"\" {", body);
            Assert.Contains("\"spent\", spent", Function("claudia_lib", "function claudia_drop_refusal {"));
        }

        [Fact]
        public void Reentry_takes_a_refusal_as_a_failed_drop_and_flies_on()
        {
            var body = Function("reentry", "function reentry {");
            var fire = body.IndexOf("set drop_error to claudia_fire_drop(ctx, drop)", StringComparison.Ordinal);
            Assert.True(fire > 0);
            // The refusal is drop_error, which ends the block only above the atmosphere (ENTRYDROP's refusal after the drop, #815); the fall otherwise goes on.
            Assert.True(fire < body.IndexOf("\"drop_error\", drop_error", StringComparison.Ordinal));
            Assert.DoesNotContain("claudia_drop_refusal(", body);
        }
    }
}
