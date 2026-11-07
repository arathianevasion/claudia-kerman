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
        private static string Craft(bool goneBurns, bool keptBurns)
        {
            string Engine(string uid, bool burning) =>
                "lexicon(\"uid\", \"" + uid + "\", \"ignition\", " + (burning ? "true" : "false") + ", \"availablethrust\", " + (burning ? 60 : 0) + ", \"isp\", 300, "
                + "\"consumedresources\", lexicon(\"Liquid Fuel\", 1, \"Oxidizer\", 1), "
                + "\"maxpossiblethrustat\", { parameter a. return 60. }, \"ispat\", { parameter a. return 300. })";
            string Part(string uid, double mass, double fuel) =>
                "lexicon(\"uid\", \"" + uid + "\", \"mass\", " + mass + ", \"resources\", "
                + "list(lexicon(\"name\", \"LiquidFuel\", \"amount\", " + fuel + ", \"density\", 0.005), lexicon(\"name\", \"ElectricCharge\", \"amount\", 100, \"density\", 0)))";
            return "global ship is lexicon(\"mass\", 10, \"engines\", list(" + Engine("kept_engine", keptBurns) + ", " + Engine("gone_engine", goneBurns) + "), "
                + "\"parts\", list(" + Part("kept_tank", 5, 400) + ", " + Part("gone_tank", 5, 600) + ")).\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"dropcheck\", { parameter ask. "
                + "for key in list(\"left\", \"spent\", \"whole\", \"kept\", \"prop\", \"isp\", \"gone_prop\", \"burners_kept\") { print key + \" \" + round(ask[key], 1). } "
                + "return lexicon(\"reason\", \"refused\"). })).\n"
                + "local plan is lexicon(\"gone\", list(lexicon(\"uid\", \"gone_tank\"), lexicon(\"uid\", \"gone_engine\"))).\n";
        }

        [Fact]
        public void A_stage_burning_itself_out_gives_core_its_propellant_as_the_burns_and_the_kept_craft_as_it_is()
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(Craft(goneBurns: true, keptBurns: false) + "print claudia_drop_refusal(plan, 300).\n"));
            // left: 300 s on 5 t with 2 t of propellant.
            Assert.Equal(new[] { "left 1502.8", "spent 300", "whole 10", "kept 5", "prop 2", "isp 300", "gone_prop 3", "burners_kept 0", "refused" }, printed.ToArray());
        }

        [Theory]
        [InlineData(false, 0)] // the kept engine alone burns, so nothing burning draws on what goes
        [InlineData(true, 3)]
        public void A_burning_engine_that_stays_is_counted_for_core_to_have_the_kept_tanks_pay(bool goneBurns, int goneProp)
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(Craft(goneBurns, keptBurns: true) + "print claudia_drop_refusal(plan, 300).\n"));
            Assert.Contains("burners_kept 1", printed);
            Assert.Contains("gone_prop " + goneProp, printed);
            Assert.Contains("prop 2", printed);
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
            // The refusal is drop_error, which ends the block only above the atmosphere (reentry_stack_refusal); the fall otherwise goes on.
            Assert.True(fire < body.IndexOf("local refused is reentry_stack_refusal(", StringComparison.Ordinal));
            Assert.DoesNotContain("claudia_drop_refusal(", body);
        }
    }
}
