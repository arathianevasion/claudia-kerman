// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>The drop_empty option's and drop_parts' pure helpers, run on kOS.Safe through the real bundle.</summary>
    public class DropPartsTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        [Fact]
        public void Every_block_takes_drop_empty_and_it_defaults_to_auto()
        {
            Assert.Equal("", One("claudia_options(lexicon(\"drop_empty\", list(\"fuelTank\")), exec_node_defaults(), \"auto\"):error"));
            Assert.Equal("", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):error"));
            Assert.Equal("auto", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt:drop_empty"));
            Assert.Equal("auto", One("claudia_common_defaults(\"auto\"):drop_empty"));
            Assert.Equal("auto", One("claudia_common_opts(claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt):drop_empty"));
        }

        [Fact]
        public void A_list_still_works_and_an_empty_list_turns_dropping_off()
        {
            Assert.Equal("2", One("claudia_options(lexicon(\"drop_empty\", list(\"a\", \"b\")), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("0", One("claudia_options(lexicon(\"drop_empty\", list()), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("", One("claudia_options(lexicon(\"drop_empty\", list()), exec_node_defaults(), \"auto\"):error"));
        }

        [Theory]
        [InlineData("list(\"\", \"\", \"\")", "list(0)", "list(400)", "True")]        // empty tank, nose cones, fuel duct
        [InlineData("list(\"\", \"\")", "list(80)", "list(400)", "False")]             // the tank holds fuel
        [InlineData("list(\"\", \"rcs\")", "list(0)", "list(400)", "False")]           // empty tank + RCS block
        [InlineData("list(\"\", \"engine\")", "list(0)", "list(400)", "False")]        // empty tank + engine
        [InlineData("list(\"\", \"\")", "list()", "list()", "False")]                  // trim only, no tank
        [InlineData("list(\"\", \"parachute\")", "list(0)", "list(400)", "False")]     // empty tank + parachute
        [InlineData("list(\"\", \"shield\")", "list(0)", "list(400)", "False")]        // empty tank + heat shield
        [InlineData("list(\"\", \"\")", "list(0, 0)", "list(400, 400)", "True")]       // two empty tanks
        [InlineData("list(\"\", \"\")", "list(0, 9)", "list(400, 400)", "False")]      // one still holds fuel
        public void Auto_fires_only_for_empty_tanks_and_trim(string kinds, string amounts, string capacities, string expected)
        {
            Assert.Equal(expected, One("claudia_auto_drop_fires(" + kinds + ", " + amounts + ", " + capacities + ")"));
        }

        [Fact]
        public void A_bare_name_is_a_one_item_list_and_a_non_list_is_refused()
        {
            Assert.Equal("boosters", One("claudia_options(lexicon(\"drop_empty\", \"boosters\"), exec_node_defaults(), \"auto\"):opt:drop_empty[0]"));
            Assert.Equal("1", One("claudia_options(lexicon(\"drop_empty\", \"boosters\"), exec_node_defaults(), \"auto\"):opt:drop_empty:length"));
            Assert.Equal("drop_empty must be a list of part names or tags", One("claudia_options(lexicon(\"drop_empty\", 3), exec_node_defaults(), \"auto\"):error"));
        }

        [Theory]
        [InlineData("fuelTank", "", "list(\"fuelTank\")", "True")]
        [InlineData("fuelTank", "", "list(\"FUELTANK\")", "True")]     // any case
        [InlineData("fuelTank", "boosters", "list(\"x\", \"Boosters\")", "True")]
        [InlineData("fuelTank", "", "list(\"boosters\")", "False")]
        [InlineData("fuelTank", "", "list(\"\")", "False")]            // an empty name would match every untagged part: never
        [InlineData("fuelTank", "", "list()", "False")]
        public void A_part_matches_by_name_or_tag_ignoring_case(string name, string tag, string wanted, string expected)
        {
            Assert.Equal(expected, One("claudia_drop_matches(\"" + name + "\", \"" + tag + "\", " + wanted + ")"));
        }

        [Theory]
        [InlineData("list(0, 0.004)", "list(400, 400)", "True")]        // dregs under the 0.01 unit floor
        [InlineData("list(0, 5)", "list(400, 400)", "False")]           // one tank still holds fuel
        [InlineData("list(0.005)", "list(1)", "True")]                  // tiny tank: 0.01 units counts as empty
        [InlineData("list()", "list()", "False")]                       // no tank: not empty, so never dropped for it
        public void A_set_is_empty_only_when_every_tank_in_it_is(string amounts, string capacities, string expected)
        {
            Assert.Equal(expected, One("claudia_tanks_empty(" + amounts + ", " + capacities + ")"));
        }

        [Fact]
        public void Charge_air_and_ablator_are_not_fuel()
        {
            Assert.Equal("False", One("claudia_is_propellant(\"ElectricCharge\")"));
            Assert.Equal("False", One("claudia_is_propellant(\"IntakeAir\")"));
            Assert.Equal("False", One("claudia_is_propellant(\"Ablator\")"));
            Assert.Equal("True", One("claudia_is_propellant(\"LiquidFuel\")"));
            Assert.Equal("True", One("claudia_is_propellant(\"SolidFuel\")"));
        }

        // A plain part as kOS gives it: it answers hasmodule and nothing the library may not ask of every part
        // (no crewcapacity, so reading one throws).
        private const string PlainPart = "lexicon(\"hasmodule\", { parameter m. return false. })";

        [Theory]
        [InlineData("false", 1, "left")]
        [InlineData("true", 1, "retry")]
        [InlineData("true", 2, "retry")]
        [InlineData("true", 3, "gave_up")]
        public void A_fired_decoupler_whose_parts_stay_is_tried_again_until_the_bound_then_given_up(string stayed, int tries, string expected)
        {
            Assert.Equal("3", One("claudia_drop_max_tries()"));
            Assert.Equal(expected, One("claudia_drop_outcome(" + stayed + ", " + tries + ")"));
        }

        [Fact]
        public void A_decoupler_whose_parts_stay_is_counted_to_the_bound_then_left_in_drop_tried_with_no_kOS_error()
        {
            var script = "local ctx is lexicon(\"drop_tried\", list(), \"drop_tries\", lexicon()).\n"
                + "print claudia_drop_settle(ctx, \"u1\", true) + ctx:drop_tries[\"u1\"] + ctx:drop_tried:length.\n"
                + "print claudia_drop_settle(ctx, \"u1\", true) + ctx:drop_tries[\"u1\"] + ctx:drop_tried:length.\n"
                + "print claudia_drop_settle(ctx, \"u1\", true) + ctx:drop_tries[\"u1\"] + ctx:drop_tried:length.\n"
                + "print ctx:drop_tried[0].\n"
                + "print claudia_drop_settle(ctx, \"u2\", false) + ctx:drop_tries[\"u2\"] + ctx:drop_tried:length.\n";
            Assert.Equal(new[] { "retry10", "retry20", "gave_up31", "u1", "left12" },
                KosHarness.Run(CoreLibrary.Bundle(script)).ToArray());
        }

        [Fact]
        public void Drop_empty_ignores_stage_limit_and_the_bound_ends_in_a_blocknote()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source;
            var start = source.IndexOf("function claudia_drop_empty {", System.StringComparison.Ordinal);
            var body = source.Substring(start, source.IndexOf("// ---- Waits and warp", start, System.StringComparison.Ordinal) - start);
            Assert.DoesNotContain("stage_limit", body);
            Assert.Contains("claudia_drop_settle(ctx, fired[i], stayed)", body);
            Assert.Contains("blocknote", body);
        }

        [Fact]
        public void A_plain_part_has_no_kind_and_the_kind_reads_nothing_but_modules()
        {
            Assert.Equal("", One("claudia_part_kind(" + PlainPart + ")"));
            Assert.Equal("command", One("claudia_part_kind(lexicon(\"hasmodule\", { parameter m. return m = \"ModuleCommand\". }))"));
            Assert.DoesNotContain("crewcapacity", CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source);
        }

        [Fact]
        public void Auto_drops_a_stack_of_an_RCS_tank_and_a_plain_part_without_reading_a_suffix_a_plain_part_lacks()
        {
            // The tank and the structural part both have no job, so both kinds are "" and an empty tank lets it fire;
            // a plain part with a command pod in the stack does not.
            Assert.Equal("True", One("claudia_auto_drop_fires(list(claudia_part_kind(" + PlainPart + "), claudia_part_kind(" + PlainPart + ")), list(0), list(40))"));
            Assert.Equal("False", One("claudia_auto_drop_fires(list(claudia_part_kind(" + PlainPart + "), claudia_part_kind(lexicon(\"hasmodule\", { parameter m. return m = \"ModuleCommand\". }))), list(0), list(40))"));
        }

        [Theory]
        [InlineData("ModuleScienceContainer")]
        [InlineData("KerbalSeat")]
        public void A_crew_cabin_known_only_by_its_container_or_seat_module_has_a_kind_and_blocks_auto_drop(string module)
        {
            var cabin = "lexicon(\"hasmodule\", { parameter m. return m = \"" + module + "\". })";
            Assert.Equal("crew", One("claudia_part_kind(" + cabin + ")"));
            Assert.Equal("False", One("claudia_auto_drop_fires(list(claudia_part_kind(" + PlainPart + "), claudia_part_kind(" + cabin + ")), list(0), list(40))"));
        }

        // A part as kOS gives it, by the modules it has; "above" is what its :decoupler answers.
        private static string Part(string modules, string above)
        {
            return "lexicon(\"name\", \"p\", \"decoupler\", " + above + ", \"hasmodule\", { parameter m. return list(" + modules + "):contains(m). })";
        }

        private const string Shield = "\"ModuleDecouple\", \"ModuleAblator\"";

        [Fact]
        public void A_heat_shield_is_never_the_decoupler_of_the_part_below_it()
        {
            // pod -> battery -> heat shield -> TR-18A -> tank: the TR-18A's decoupler, as kOS gives it, is the shield.
            var shield = Part(Shield, "\"\"");
            Assert.Equal("", One("claudia_decoupler_of(" + Part("", shield) + ")"));
            Assert.Equal("", One("claudia_decoupler_of(" + Part("\"ModuleDecouple\"", shield) + ")"));
            var trunk = Part("\"ModuleDecouple\"", "\"\"");
            Assert.Equal("True", One("claudia_decoupler_of(" + Part("", trunk) + "):istype(\"Lexicon\")"));
        }

        [Fact]
        public void Naming_a_decoupler_fires_that_decoupler_and_not_the_one_above_it()
        {
            var shield = Part(Shield, "\"\"");
            var tr18 = Part("\"ModuleDecouple\"", shield);
            Assert.Equal("True", One("claudia_drop_decoupler(" + tr18 + "):hasmodule(\"ModuleDecouple\")"));
            Assert.Equal("False", One("claudia_drop_decoupler(" + tr18 + "):hasmodule(\"ModuleAblator\")"));
        }

        [Fact]
        public void A_heat_shield_is_no_drop_target_even_when_named_or_under_a_tank()
        {
            var shield = Part(Shield, "\"\"");
            Assert.Equal("", One("claudia_drop_decoupler(" + shield + ")"));
            // An empty tank directly under the shield (no decoupler between): nothing to fire.
            Assert.Equal("", One("claudia_drop_decoupler(" + Part("", shield) + ")"));
        }

        [Fact]
        public void The_drop_plan_refuses_a_named_heat_shield_and_the_stage_plan_skips_it()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source;
            Assert.Contains("is a heat shield; reentry drops it", source);
            Assert.Contains("claudia_part_kind(p) <> \"shield\" and p:stage = stage_num", source);
        }

        [Fact]
        public void The_stage_drop_plan_is_in_claudia_lib_and_the_deorbit_name_is_gone_from_the_library()
        {
            var library = CoreLibrary.Files.Where(f => f.Name != "claudia_lib").Select(f => f.Source);
            foreach (var source in library)
            {
                Assert.DoesNotContain("deorbit_stage_drop_plan", source);
            }
            Assert.Contains("function claudia_stage_drop_plan {", CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source);
        }

        [Fact]
        public void The_names_of_what_left_come_from_the_uids_and_names_taken_while_aboard()
        {
            Assert.Equal("b,c", One("claudia_names_left(list(1, 2, 3), list(\"a\", \"b\", \"c\"), list(1)):join(\",\")"));
            Assert.Equal("", One("claudia_names_left(list(1, 2), list(\"a\", \"b\"), list(1, 2)):join(\",\")"));
        }

        [Fact]
        public void No_library_code_reads_the_name_of_a_part_after_the_uid_check_that_it_left()
        {
            foreach (var f in CoreLibrary.Files)
                Assert.DoesNotMatch(@"if not (left|now_on|now_uids):contains\((g|p):uid\) \{\s*\S+:add\(\w:name\)", f.Source);
        }

        [Fact]
        public void The_drop_parts_block_needs_a_list_of_names()
        {
            Assert.Equal("parts is required", One("drop_parts_names(\"required\")"));
            Assert.Equal("parts must be a list of part names or tags", One("drop_parts_names(list())"));
            Assert.Equal("parts must be a list of part names or tags", One("drop_parts_names(4)"));
            Assert.Equal("1", One("drop_parts_names(\"boosters\"):length"));
            Assert.Equal("2", One("drop_parts_names(list(\"a\", \"b\")):length"));
        }

        // ---- #608: a drop that strands the craft in space is refused ----

        // a107: the drop took the Terrier stack and all 1,784 m/s with Kerbin's periapsis at 601 km (atmosphere 70 km).
        [Theory]
        [InlineData("\"ORBITING\"", 601000, 70000, 1784, 0, "false", "True")]   // only engine and all fuel go, pe above the air: refused
        [InlineData("\"ORBITING\"", 601000, 70000, 1784, 0, "true", "False")]   // allow_no_engine lets it go
        [InlineData("\"ORBITING\"", 35000, 70000, 1784, 0, "false", "False")]   // periapsis inside the atmosphere: it comes down
        [InlineData("\"SUB_ORBITAL\"", -200000, 70000, 1784, 0, "false", "False")] // falling into the air
        [InlineData("\"LANDED\"", -600000, 70000, 1784, 0, "false", "False")]
        [InlineData("\"SPLASHED\"", -600000, 70000, 1784, 0, "false", "False")]
        [InlineData("\"LANDED\"", -200000, -1, 900, 0, "false", "False")]      // landed on the Mun
        [InlineData("\"ORBITING\"", 12000, -1, 900, 0, "false", "True")]        // Mun orbit: no atmosphere to come down in
        [InlineData("\"ESCAPING\"", 601000, 70000, 1784, 0, "false", "True")]
        [InlineData("\"ORBITING\"", 601000, 70000, 1784, 950, "false", "False")] // an empty tank or a spent stage, the engine stays
        [InlineData("\"ORBITING\"", 601000, 70000, 0, 0, "false", "False")]     // no delta-v before it: the drop takes none
        public void A_drop_is_refused_only_in_space_on_an_orbit_missing_the_air_when_it_takes_the_last_delta_v(
            string status, int pe, int atm, int dvNow, int dvLeft, string allow, string expected)
        {
            Assert.Equal(expected, One("drop_parts_strands(" + status + ", " + pe + ", " + atm + ", " + dvNow + ", " + dvLeft + ", " + allow + ")"));
        }

        [Fact]
        public void The_refusal_names_the_delta_v_the_drop_would_take_and_the_way_out()
        {
            var reason = One("drop_parts_strand_reason(1784.3, 601234, 70000)");
            Assert.Contains("all 1784 m/s", reason);
            Assert.Contains("periapsis 601 km is above the atmosphere", reason);
            Assert.Contains("reentry's separate", reason);
            Assert.Contains("allow_no_engine", reason);
            Assert.Contains("no atmosphere", One("drop_parts_strand_reason(900, 12000, -1)"));
            Assert.Contains("all the delta-v", One("drop_parts_strand_reason(-1, 601234, 70000)"));
        }

        [Theory]
        [InlineData(340, 4.0, 2.0, 2311)]  // Terrier-like: wet 4 t, 2 t of fuel: 340 * g0 * ln 2
        [InlineData(0, 4.0, 2.0, 0)]       // no engine stays (no isp)
        [InlineData(340, 4.0, 0, 0)]       // the engine stays, its fuel goes
        [InlineData(340, 2.0, 2.0, 0)]     // nonsense masses never divide by zero
        public void The_delta_v_left_is_the_rocket_equation_of_what_stays(int isp, double wet, double prop, int expected)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(expected.ToString(inv), One("round(drop_parts_rocket_dv(" + isp + ", " + wet.ToString(inv) + ", " + prop.ToString(inv) + "))"));
        }

        [Theory]
        [InlineData(1784, 0, 2100, 0, "1784")]     // a107 refused: stock's figure, all of it
        [InlineData(1784, 950, 2100, 1000, "834")] // stock recounted after the drop
        [InlineData(0, 0, 2100, 0, "2100")]        // stock not yet computed: the proxy's
        [InlineData(-1, -1, 2100, 0, "2100")]      // a trickle throttle: the proxy's
        [InlineData(1784, 1784, 2100, 1100, "1000")] // stock not recounted yet: the proxy's
        [InlineData(950, 990, 1000, 1040, "0")]    // an empty tank dropped gains delta-v: 0
        public void The_delta_v_a_drop_took_is_stocks_unless_stock_is_unknown_or_stale(int before, int after, int proxyNow, int proxyLeft, string expected)
        {
            Assert.Equal(expected, One("drop_parts_dv_taken(" + before + ", " + after + ", " + proxyNow + ", " + proxyLeft + ")"));
        }

        [Theory]
        [InlineData("Liquid Fuel", "LiquidFuel")]
        [InlineData("MonoPropellant", "Monopropellant")]
        [InlineData("Xenon Gas", "XenonGas")]
        public void A_part_resource_and_an_engines_consumed_resource_share_a_key(string a, string b)
        {
            Assert.Equal("True", One("drop_parts_res_key(\"" + a + "\") = drop_parts_res_key(\"" + b + "\")"));
        }

        [Fact]
        public void Drop_parts_checks_the_drop_before_firing_and_reports_dropped_dv()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "drop_parts").Source;
            var refuse = source.IndexOf("drop_parts_strands(status", System.StringComparison.Ordinal);
            Assert.True(refuse > 0);
            Assert.True(refuse < source.IndexOf("claudia_fire_drop(ctx, plan)", System.StringComparison.Ordinal));
            Assert.Contains("\"allow_no_engine\", false", source);
            Assert.Contains("set extra:dropped_dv to", source);
        }

        [Fact]
        public void Drop_parts_is_one_of_the_blocks_and_its_docs_name_its_options()
        {
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "drop_parts");
            var docs = CoreLibrary.FullDocs(new[] { "drop_parts" });
            Assert.Contains("parts [required]", docs);
            Assert.Contains("dropped", docs);
            Assert.Contains("allow_no_engine (false)", docs);
            Assert.Contains("dropped_dv", docs);
            Assert.Contains("drop_empty", CoreLibrary.Summaries());
        }
    }
}
