// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// reentry's stack (#557): what hangs below the heat shield, how separate drops it and when the block refuses to fly it,
    /// on fake part trees run on kOS.Safe through the real bundle. Mun Shot v2 is pod, HeatShield1, Decoupler.1, then the
    /// service module; 44e9's tree is the same craft with its legs and RCS blocks.
    /// </summary>
    public class ReentryStackTests
    {
        private sealed class Spec
        {
            public string Uid;
            public string Name;
            public int Parent;
            public string[] Modules;
        }

        private static readonly string[] None = new string[0];
        private static readonly string[] Command = { "ModuleCommand" };
        private static readonly string[] Shield = { "ModuleAblator", "ModuleDecouple" };
        private static readonly string[] Decoupler = { "ModuleDecouple" };

        private sealed class Tree
        {
            public readonly List<Spec> Parts = new List<Spec>();

            public int Add(string name, int parent, string[] modules, string uid = null)
            {
                Parts.Add(new Spec { Uid = uid ?? name + "#" + Parts.Count, Name = name, Parent = parent, Modules = modules });
                return Parts.Count - 1;
            }

            /// <summary>The tree as kOS parts the library can read: uid, name, hasparent, parent and hasmodule.</summary>
            public string Script()
            {
                var b = new StringBuilder();
                for (var i = 0; i < Parts.Count; i++)
                {
                    var s = Parts[i];
                    var test = s.Modules.Length == 0 ? "false" : string.Join(" or ", s.Modules.Select(m => "m = \"" + m + "\""));
                    b.Append("local p_" + i + " is lexicon(\"uid\", \"" + s.Uid + "\", \"name\", \"" + s.Name + "\", "
                        + "\"hasparent\", " + (s.Parent >= 0 ? "true" : "false") + ", \"parent\", " + (s.Parent >= 0 ? "p_" + s.Parent : "\"\"") + ", "
                        + "\"hasmodule\", { parameter m. return " + test + ". }).\n");
                }
                b.Append("local parts is list(" + string.Join(", ", Enumerable.Range(0, Parts.Count).Select(i => "p_" + i)) + ").\n");
                return b.ToString();
            }
        }

        /// <summary>Mun Shot v2 home from the Mun, as in b5f3 and 0de6: the pod's side, the shield, Decoupler.1, the service module.</summary>
        private static Tree MunShotV2(bool legsAndRcs = false)
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            t.Add("parachuteSingle", pod, new[] { "ModuleParachute" });
            var kos = t.Add("kOSMachine1m", pod, new[] { "kOSProcessor" });
            var battery = t.Add("batteryBank", kos, None);
            var shield = t.Add("HeatShield1", battery, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var rcsTank = t.Add("RCSFuelTank", dec, None);
            var tank = t.Add("fuelTank", rcsTank, None);
            var small = t.Add("fuelTankSmall", tank, None);
            if (legsAndRcs)
                for (var i = 0; i < 4; i++) t.Add("landingLeg1", small, new[] { "ModuleWheelBase" });
            t.Add("liquidEngine3.v2", small, new[] { "ModuleEnginesFX" });
            if (legsAndRcs)
                for (var i = 0; i < 8; i++) t.Add("RCSblock.01.small", tank, new[] { "ModuleRCSFX" });
            return t;
        }

        private static string[] Run(Tree tree, string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(tree.Script() + script)).ToArray();
        }

        private const string PrintStack =
            "local names is list(). for g in below:gone { names:add(g:name). }\n"
            + "local decs is list(). for d in below:decouplers { decs:add(d:uid). }\n"
            + "print below:shielded. print decs:join(\",\"). print names:join(\",\"). print reentry_names(below:stuck):join(\",\"). print reentry_names(below:parts):join(\",\").\n";

        [Fact]
        public void On_Mun_Shot_v2_the_stack_is_the_service_module_and_it_drops_through_Decoupler_1()
        {
            var printed = Run(MunShotV2(), "local below is reentry_stack(parts, \"pod\").\n" + PrintStack
                + "local plan is reentry_separation(\"auto\", below).\n"
                + "local gone is list(). for g in plan:gone { gone:add(g:name). }\n"
                + "print plan:error. print plan:decouplers[0]:name. print \"\" + gone:contains(\"HeatShield1\") +\",\" + gone:contains(\"mk1pod.v2\") + \",\" + gone:contains(\"Decoupler.1\").\n");
            Assert.Equal(new[]
            {
                "True", "dec1", "RCSFuelTank,fuelTank,fuelTankSmall,liquidEngine3.v2", "", "RCSFuelTank,fuelTank,fuelTankSmall,liquidEngine3.v2",
                // The plan fires Decoupler.1 alone; the shield, the pod and Decoupler.1 itself stay.
                "", "Decoupler.1", "False,False,False",
            }, printed);
        }

        [Fact]
        public void On_44e9s_tree_reentry_drops_the_whole_stack_legs_and_RCS_blocks_included()
        {
            var printed = Run(MunShotV2(legsAndRcs: true), "local below is reentry_stack(parts, \"pod\").\n"
                + "local plan is reentry_separation(\"auto\", below).\n"
                + "local decs is list(). for d in plan:decouplers { decs:add(d:uid). }\n"
                + "print plan:error. print decs:join(\",\"). print plan:gone:length. print reentry_names(plan:gone):join(\",\").\n");
            // RCSFuelTank, fuelTank, fuelTankSmall, liquidEngine3.v2, four landingLeg1, eight RCSblock.01.small.
            Assert.Equal(new[] { "", "dec1", "16", "RCSFuelTank,fuelTank,fuelTankSmall,landingLeg1,liquidEngine3.v2,RCSblock.01.small" }, printed);
        }

        [Fact]
        public void Separate_none_with_the_stack_attached_fails_naming_the_stacks_parts()
        {
            var printed = Run(MunShotV2(legsAndRcs: true), "local below is reentry_stack(parts, \"pod\").\n"
                + "print reentry_separation(\"none\", below):decouplers:length.\n"
                + "print reentry_stack_refusal(below:shielded, reentry_names(below:parts), \"\").\n");
            Assert.Equal(new[]
            {
                "0",
                "stack attached below the heat shield: RCSFuelTank, fuelTank, fuelTankSmall, landingLeg1, liquidEngine3.v2, RCSblock.01.small; drop it first or use separate",
            }, printed);
        }

        [Fact]
        public void A_drop_that_failed_is_said_after_the_names()
        {
            Assert.Equal("stack attached below the heat shield: fuelTank; drop it first or use separate (separate: nothing separated: the decouplers did not fire)",
                Run(new Tree(), "print reentry_stack_refusal(true, list(\"fuelTank\"), \"nothing separated: the decouplers did not fire\").").Single());
        }

        [Fact]
        public void The_drop_and_refusal_come_after_the_entry_check_and_before_the_wait_and_the_hold_and_the_shield_is_found_after_them()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source;
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            var refusal = body.IndexOf("reentry_stack_refusal(", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(body.IndexOf("claudia_fire_drop(ctx, drop)", StringComparison.Ordinal) < refusal);
            // Nothing drops before the orbit is known to reach the atmosphere: a capsule left in orbit has no engine.
            Assert.True(body.IndexOf("the orbit never reaches the atmosphere", StringComparison.Ordinal) < body.IndexOf("claudia_fire_drop(ctx, drop)", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("lock steering to srfretrograde", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("claudia_wait_until(", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("reentry_shield_modules(opt:jettison_stage, ship:parts)", StringComparison.Ordinal));
            // The refusal is only above the atmosphere: a craft already in the air flies on.
            Assert.Contains("if ship:altitude > body_:atm:height {\n      local refused is reentry_stack_refusal(", body);
        }

        [Fact]
        public void A_pod_with_its_shield_and_nothing_below_runs_as_today()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            t.Add("parachuteSingle", pod, new[] { "ModuleParachute" });
            t.Add("HeatShield1", pod, Shield, "shield");
            var printed = Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack
                + "print reentry_stack_refusal(below:shielded, reentry_names(below:parts), \"\") = \"\".\n");
            Assert.Equal(new[] { "True", "", "", "", "", "True" }, printed);
        }

        [Fact]
        public void A_spent_decoupler_left_on_the_shield_is_not_a_stack()
        {
            // Mun Shot v2 after the drop: Decoupler.1 stays with the shield, with nothing below it.
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            t.Add("Decoupler.1", shield, Decoupler, "dec1");
            Assert.Equal(new[] { "True", "", "", "", "" }, Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack));
        }

        [Fact]
        public void A_craft_with_no_heat_shield_runs_as_today()
        {
            // A pod and its chute: no stack, nothing to drop, no refusal.
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            t.Add("parachuteSingle", pod, new[] { "ModuleParachute" });
            Assert.Equal(new[] { "False", "", "", "", "" }, Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack));
            // A stack below a decoupler with no shield anywhere: "auto" drops nothing and "none" is never refused.
            var u = new Tree();
            var pod2 = u.Add("mk1pod.v2", -1, Command, "pod");
            var dec = u.Add("Decoupler.1", pod2, Decoupler, "dec1");
            u.Add("fuelTank", dec, None);
            Assert.Equal(new[] { "False", "dec1", "fuelTank", "", "fuelTank", "0", "True" },
                Run(u, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack
                    + "print reentry_separation(\"auto\", below):decouplers:length.\n"
                    + "print reentry_stack_refusal(below:shielded, reentry_names(below:parts), \"\") = \"\".\n"));
        }

        [Fact]
        public void With_no_heat_shield_the_stack_is_below_the_first_decoupler_under_the_command_part()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var battery = t.Add("batteryBank", pod, None);
            var upper = t.Add("Decoupler.1", battery, Decoupler, "upper");
            var tank = t.Add("fuelTank", upper, None);
            var lower = t.Add("Decoupler.1", tank, Decoupler, "lower");
            t.Add("fuelTankSmall", lower, None);
            // One drop through the upper decoupler takes the lower one and all below it; the battery stays.
            Assert.Equal(new[] { "False", "upper", "fuelTank,Decoupler.1,fuelTankSmall", "", "fuelTank,Decoupler.1,fuelTankSmall" },
                Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack));
        }

        [Fact]
        public void A_part_on_the_shield_with_no_decoupler_between_is_stuck_and_the_shield_is_never_the_one_fired()
        {
            // #516: the shield's own decoupler never drops a stack, so a tank straight on the shield can't be dropped.
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            t.Add("fuelTank", shield, None);
            var printed = Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack
                + "local plan is reentry_separation(\"auto\", below).\n"
                + "print plan:error. print plan:decouplers:length.\n"
                + "print reentry_stack_refusal(below:shielded, reentry_names(below:parts), plan:error).\n");
            Assert.Equal(new[]
            {
                "True", "", "", "fuelTank", "fuelTank",
                "fuelTank: no decoupler between it and the heat shield", "0",
                "stack attached below the heat shield: fuelTank; drop it first or use separate (separate: fuelTank: no decoupler between it and the heat shield)",
            }, printed);
        }

        [Fact]
        public void A_craft_built_from_the_far_side_has_its_stack_stuck_rather_than_dropping_the_pod()
        {
            // The root is the tank: the pod hangs under the shield, so firing the decoupler would take the pod with it.
            var t = new Tree();
            var tank = t.Add("fuelTank", -1, None);
            var dec = t.Add("Decoupler.1", tank, Decoupler, "dec1");
            var shield = t.Add("HeatShield1", dec, Shield, "shield");
            t.Add("mk1pod.v2", shield, Command, "pod");
            Assert.Equal(new[] { "True", "", "", "fuelTank,Decoupler.1", "fuelTank,Decoupler.1" },
                Run(t, "local below is reentry_stack(parts, \"pod\").\n" + PrintStack));
        }

        // ---- A powered touchdown keeps its engines (#628) ----

        /// <summary>A powered pod: pod, shield, Decoupler.1, a tank with legs, its engine, then a decoupler and an empty drop tank below.</summary>
        private static Tree PoweredPod()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var tank = t.Add("fuelTank", dec, None);
            t.Add("landingLeg1", tank, new[] { "ModuleWheelBase" });
            var engine = t.Add("liquidEngine3.v2", tank, new[] { "ModuleEnginesFX" }, "engine");
            var lower = t.Add("Decoupler.2", engine, Decoupler, "dec2");
            t.Add("fuelTankSmall", lower, None);
            return t;
        }

        [Fact]
        public void Separate_auto_with_a_powered_touchdown_keeps_the_engine_and_everything_between_it_and_the_shield()
        {
            var printed = Run(PoweredPod(), "local below is reentry_stack(parts, \"pod\", list(\"engine\")).\n" + PrintStack
                + "local plan is reentry_separation(\"auto\", below).\n"
                + "local decs is list(). for d in plan:decouplers { decs:add(d:uid). }\n"
                + "print plan:error. print decs:join(\",\"). print reentry_names(below:kept):join(\",\").\n");
            Assert.Equal(new[]
            {
                // Only the engineless drop tank is the stack, through the decoupler under the engine.
                "True", "dec2", "fuelTankSmall", "", "fuelTankSmall",
                "", "dec2", "Decoupler.1,fuelTank,landingLeg1,liquidEngine3.v2",
            }, printed);
        }

        [Fact]
        public void A_spent_engine_on_the_drop_tank_is_not_one_the_touchdown_lands_on_so_its_stage_still_goes()
        {
            var t = PoweredPod();
            t.Add("liquidEngine2.v2", t.Parts.Count - 1, new[] { "ModuleEngines" }, "spent");
            var printed = Run(t, "local below is reentry_stack(parts, \"pod\", list(\"engine\")).\n"
                + "local decs is list(). for d in reentry_separation(\"auto\", below):decouplers { decs:add(d:uid). }\n"
                + "print decs:join(\",\"). print reentry_names(below:gone):join(\",\").\n");
            Assert.Equal(new[] { "dec2", "fuelTankSmall,liquidEngine2.v2" }, printed);
        }

        [Fact]
        public void Without_a_powered_touchdown_the_same_craft_drops_everything_below_the_shield()
        {
            var printed = Run(PoweredPod(), "local below is reentry_stack(parts, \"pod\").\n"
                + "local plan is reentry_separation(\"auto\", below).\n"
                + "print plan:decouplers[0]:uid. print reentry_names(plan:gone):join(\",\"). print below:kept:length.\n");
            Assert.Equal(new[] { "dec1", "fuelTank,landingLeg1,liquidEngine3.v2,Decoupler.2,fuelTankSmall", "0" }, printed);
        }

        // ---- The option ----

        [Fact]
        public void Separate_defaults_to_auto_and_takes_none_or_a_stage_number()
        {
            var t = new Tree();
            Assert.Equal("auto", Run(t, "print claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:separate.").Single());
            foreach (var ok in new[] { "\"none\"", "\"auto\"", "0", "2" })
                Assert.Equal("", Run(t, "print reentry_unreadable(claudia_merge_options(lexicon(\"separate\", " + ok + "), reentry_defaults())).").Single());
        }

        [Theory]
        [InlineData("\"soon\"", "separate must be 'auto', 'none' or a stage number, not 'soon'")]
        [InlineData("-1", "separate must be 'auto', 'none' or a stage number, not -1")]
        [InlineData("1.5", "separate must be 'auto', 'none' or a stage number, not 1.5")]
        [InlineData("list(\"fuelTank\")", "separate must be 'auto', 'none' or a stage number, not a List")]
        public void A_separate_it_cannot_read_is_refused(string value, string reason)
        {
            Assert.Equal(reason, Run(new Tree(), "print reentry_unreadable(claudia_merge_options(lexicon(\"separate\", " + value + "), reentry_defaults())).").Single());
        }

        [Fact]
        public void The_docs_name_separate_and_the_failure_and_the_catalog_line_says_the_stack_drops()
        {
            var docs = CoreLibrary.FullDocs(new[] { "reentry" });
            Assert.Contains("separate [\"auto\"]", docs);
            Assert.Contains("stack attached below the heat shield: <names>", docs);
            Assert.Contains("drops the stack below the heat shield", CoreLibrary.Blocks.Single(b => b.Name == "reentry").Docs[0]);
        }
    }
}
