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

        [Fact]
        public void Drop_parts_is_one_of_the_blocks_and_its_docs_name_its_options()
        {
            Assert.Contains(CoreLibrary.Blocks, b => b.Name == "drop_parts");
            var docs = CoreLibrary.FullDocs(new[] { "drop_parts" });
            Assert.Contains("parts [required]", docs);
            Assert.Contains("dropped", docs);
            Assert.Contains("drop_empty", CoreLibrary.Summaries());
        }
    }
}
