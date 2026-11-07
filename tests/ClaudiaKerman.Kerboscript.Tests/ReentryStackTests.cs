// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// reentry's firing of what its Entry drops (#557, #823): fake part trees run on kOS.Safe through the real bundle, given what Core's
    /// <c>ENTRYDROP</c> call really answers for that tree and plan. Which shield and which parts is Core's (EntryDropTests); these say
    /// what the block then fires and when it refuses.
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

            /// <summary>The tree as kOS parts the library can read and fire: a fired decoupler's uid goes on the list <c>fired</c>.</summary>
            public string Script()
            {
                var b = new StringBuilder("local fired is list().\n");
                for (var i = 0; i < Parts.Count; i++)
                {
                    var s = Parts[i];
                    var test = s.Modules.Length == 0 ? "false" : string.Join(" or ", s.Modules.Select(m => "m = \"" + m + "\""));
                    b.Append("local p_" + i + " is lexicon(\"uid\", \"" + s.Uid + "\", \"name\", \"" + s.Name + "\", \"children\", list(), "
                        + "\"hasparent\", " + (s.Parent >= 0 ? "true" : "false") + ", \"parent\", " + (s.Parent >= 0 ? "p_" + s.Parent : "\"\"") + ", "
                        + "\"hasmodule\", { parameter m. return " + test + ". }, "
                        + "\"getmodule\", { parameter m. return lexicon(\"alleventnames\", list(\"decouple\"), \"part\", lexicon(\"uid\", \"" + s.Uid + "\"), \"doevent\", { parameter e. fired:add(\"" + s.Uid + "\"). }). }).\n");
                    if (s.Parent >= 0) b.Append("p_" + s.Parent + ":children:add(p_" + i + ").\n");
                }
                b.Append("local parts is list(" + string.Join(", ", Enumerable.Range(0, Parts.Count).Select(i => "p_" + i)) + ").\n");
                return b.ToString();
            }

            private static SnapshotPart Part(Spec s, int i)
            {
                return new SnapshotPart
                {
                    Id = (uint)(i + 1), ParentId = s.Parent >= 0 ? (uint)(s.Parent + 1) : (uint?)null, Name = s.Name,
                    Tags = s.Modules.Select(m => m == "ModuleAblator" ? "heat_shield" : m == "ModuleDecouple" ? "decoupler" : "other").ToList(),
                };
            }

            /// <summary><c>local below</c>: what ENTRYDROP answers for this tree at <paramref name="at"/>.</summary>
            public string Below(string at, IList<PlannedSequence> legs, params string[] keep)
            {
                var snapshot = new GameSnapshot
                {
                    Body = at,
                    Orbit = new SnapshotOrbit { Body = at },
                    Craft = new SnapshotCraft { Parts = Parts.Select(Part).ToList() },
                    Bodies = new List<SnapshotBody>
                    {
                        new SnapshotBody { Name = "Kerbin", AtmosphereHeight = 70000, SeaLevelAtm = 1 },
                        new SnapshotBody { Name = "Duna", AtmosphereHeight = 50000, SeaLevelAtm = 0.07 },
                    },
                };
                var answer = BlockCalls.EntryDropCall(new Dictionary<string, object> { { "home", "pod" }, { "keep_engines", keep.Cast<object>().ToList() } },
                    snapshot, legs, 0, "", id => Parts[(int)id - 1].Uid);
                Func<string, string> list = key => "list(" + string.Join(", ", ((IEnumerable<object>)answer[key]).Select(u => "\"" + u + "\"")) + ")";
                return "local below is lexicon(\"error\", \"\", \"shield\", \"" + answer["shield"] + "\", \"decouplers\", " + list("decouplers") + ", \"gone\", " + list("gone")
                    + ", \"kept\", " + list("kept") + ", \"names\", " + list("names") + ", \"stuck\", " + list("stuck") + ").\n";
            }
        }

        private static readonly IList<PlannedSequence> Return = new[] { new PlannedSequence { Name = "Return to Kerbin", DeltaV = 600 } };
        private static readonly IList<PlannedSequence> DunaThenKerbin = new[]
        {
            new PlannedSequence { Name = "Land on Duna", DeltaV = 100 }, new PlannedSequence { Name = "Ascend from Duna", DeltaV = 1336 }, new PlannedSequence { Name = "Return to Kerbin", DeltaV = 623 },
        };

        /// <summary>Mun Shot v2 home from the Mun, as in b5f3 and 0de6: the pod's side, the shield, Decoupler.1, the service module.</summary>
        private static Tree MunShotV2()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var tank = t.Add("fuelTank", dec, None);
            t.Add("liquidEngine3.v2", tank, new[] { "ModuleEnginesFX" }, "engine");
            return t;
        }

        /// <summary>The flight's layout (m20261106-203509-34a6), cut down: the core stage, its radial tanks on their own decouplers, its engine.</summary>
        private static Tree TheFlight()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            var dec = t.Add("Decoupler.1", shield, Decoupler, "dec1");
            var core = t.Add("fuelTank", dec, None);
            for (var i = 0; i < 4; i++) t.Add("radialTank", t.Add("radialDecoupler", core, Decoupler), None);
            t.Add("toroidalAerospike", core, new[] { "ModuleEnginesFX" }, "engine");
            return t;
        }

        /// <summary>Pod, shield 1, the ascent stage, shield 2, the transfer stage.</summary>
        private static Tree TwoShields()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var s1 = t.Add("shield1", pod, Shield, "s1");
            var d1 = t.Add("dec1", s1, Decoupler, "d1");
            var ascent = t.Add("ascentTank", d1, None);
            var s2 = t.Add("shield2", ascent, Shield, "s2");
            var d2 = t.Add("dec2", s2, Decoupler, "d2");
            t.Add("transferTank", d2, None);
            return t;
        }

        private static string[] Run(Tree tree, string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(tree.Script() + script)).ToArray();
        }

        private const string Options = "local opt is claudia_merge_options(lexicon(), reentry_defaults()).\n";

        /// <summary>Prints what "auto" fires (decoupler uids), the refusal, and the shield's uid and modules.</summary>
        private const string PrintFires = Options
            + "local plan is reentry_separation(\"auto\", below, parts).\n"
            + "local decs is list(). for d in plan:decouplers { decs:add(d:uid). }\n"
            + "print decs:join(\",\"). print reentry_stack_refusal(\"auto\", below:stuck, plan:error).\n"
            + "local jettison is reentry_jettison_uid(opt, below). print jettison. print reentry_shield_modules(jettison, parts):length.\n";

        [Fact]
        public void On_the_flights_layout_a_non_last_entry_fires_nothing_with_chutes()
        {
            Assert.Equal(new[] { "", "", "", "0" }, Run(TheFlight(), TheFlight().Below("Duna", DunaThenKerbin) + PrintFires));
        }

        [Fact]
        public void On_the_flights_layout_a_non_last_entry_fires_nothing_when_powered()
        {
            Assert.Equal(new[] { "", "", "", "0" }, Run(TheFlight(), TheFlight().Below("Duna", DunaThenKerbin, "engine") + PrintFires));
        }

        [Fact]
        public void On_the_flights_layout_the_Last_Entry_fires_the_decoupler_under_the_shield_and_jettisons_it()
        {
            var printed = Run(TheFlight(), TheFlight().Below("Kerbin", Return) + PrintFires);
            // The shield's own decoupler and Decoupler.1 beside it; the block drops the stack first, which takes nothing else.
            Assert.Equal(new[] { "dec1", "", "shield", "2" }, printed);
        }

        [Fact]
        public void A_shield_that_isnt_the_Entry_Shield_is_never_fired()
        {
            var tree = TwoShields();
            var printed = Run(tree, tree.Below("Duna", DunaThenKerbin) + PrintFires
                + "print reentry_shield_drop(jettison, { return parts. }). print fired:join(\",\").\n");
            // Duna uses shield 2: dec2 drops the transfer stage; only shield 2 and the decoupler beside it are jettisoned.
            Assert.Equal(new[] { "d2", "", "s2", "2", "2", "s2,d2" }, printed);
        }

        [Fact]
        public void The_Entry_Shield_still_goes_by_reentry_shield_due()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            Assert.Contains("local shield is reentry_shield_modules(shield_uid, ship:parts).\n  local shield_gone is shield:length = 0.", body);
            var due = body.IndexOf("if reentry_shield_due(shield_gone, last_speed, agl) {", StringComparison.Ordinal);
            Assert.True(due > 0);
            Assert.True(due < body.IndexOf("reentry_shield_drop(shield_uid, { return ship:parts. })", StringComparison.Ordinal));
        }

        [Fact]
        public void A_part_with_no_decoupler_below_the_Entry_Shield_refuses_and_below_another_shield_it_doesnt()
        {
            var t = new Tree();
            var pod = t.Add("mk1pod.v2", -1, Command, "pod");
            var shield = t.Add("HeatShield1", pod, Shield, "shield");
            t.Add("fuelTank", shield, None);
            const string print = "local plan is reentry_separation(\"auto\", below, parts).\n"
                + "print plan:error. print plan:decouplers:length. print reentry_stack_refusal(\"auto\", below:names, plan:error).\n";
            Assert.Equal(new[]
            {
                "fuelTank: no decoupler between it and the heat shield", "0",
                "stack attached below the heat shield: fuelTank; drop it first or use separate (separate: fuelTank: no decoupler between it and the heat shield)",
            }, Run(t, t.Below("Kerbin", Return) + print));
            Assert.Equal(new[] { "", "0", "" }, Run(t, t.Below("Duna", DunaThenKerbin) + print));
        }

        [Fact]
        public void Separate_none_keeps_the_stack_with_no_refusal_and_keeps_the_shield_that_would_take_it()
        {
            var tree = MunShotV2();
            var printed = Run(tree, tree.Below("Kerbin", Return) + "local opt is claudia_merge_options(lexicon(\"separate\", \"none\"), reentry_defaults()).\n"
                + "print reentry_separation(\"none\", below, parts):decouplers:length.\n"
                + "print reentry_stack_refusal(\"none\", below:names, \"\").\n"
                + "print reentry_jettison_uid(opt, below).\n");
            Assert.Equal(new[] { "0", "", "" }, printed);
        }

        [Fact]
        public void Jettison_none_keeps_the_shield_and_the_stack_still_drops()
        {
            var tree = MunShotV2();
            var printed = Run(tree, tree.Below("Kerbin", Return) + "local opt is claudia_merge_options(lexicon(\"jettison_stage\", \"none\"), reentry_defaults()).\n"
                + "print reentry_separation(\"auto\", below, parts):decouplers:length. print reentry_jettison_uid(opt, below).\n");
            Assert.Equal(new[] { "1", "" }, printed);
        }

        [Fact]
        public void A_powered_touchdown_that_keeps_its_engines_keeps_the_shield_whose_drop_would_take_them()
        {
            var tree = MunShotV2();
            Assert.Equal(new[] { "" }, Run(tree, tree.Below("Kerbin", Return, "engine") + Options + "print reentry_jettison_uid(opt, below).\n"));
        }

        [Fact]
        public void A_drop_that_failed_is_said_after_the_names()
        {
            Assert.Equal("stack attached below the heat shield: fuelTank; drop it first or use separate (separate: nothing separated: the decouplers did not fire)",
                Run(new Tree(), "print reentry_stack_refusal(\"auto\", list(\"fuelTank\"), \"nothing separated: the decouplers did not fire\").").Single());
        }

        [Fact]
        public void The_drop_and_refusal_come_after_the_entry_check_and_before_the_wait_and_the_hold_and_the_shield_is_found_after_them()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            var refusal = body.IndexOf("reentry_stack_refusal(", StringComparison.Ordinal);
            Assert.True(refusal > 0);
            Assert.True(body.IndexOf("claudia_fire_drop(ctx, drop)", StringComparison.Ordinal) < refusal);
            // Nothing drops before the orbit is known to reach the atmosphere: a capsule left in orbit has no engine.
            Assert.True(body.IndexOf("the orbit never reaches the atmosphere", StringComparison.Ordinal) < body.IndexOf("claudia_fire_drop(ctx, drop)", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("lock steering to srfretrograde", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("claudia_wait_until(", StringComparison.Ordinal));
            Assert.True(refusal < body.IndexOf("reentry_shield_modules(shield_uid, ship:parts)", StringComparison.Ordinal));
            // Read fresh at block start, and again for the craft that's left.
            Assert.True(body.IndexOf("addons:claudia:entrydrop(entry_ask)", StringComparison.Ordinal) < body.IndexOf("claudia_fire_drop(ctx, drop)", StringComparison.Ordinal));
            // The refusal is only above the atmosphere: a craft already in the air flies on.
            Assert.Contains("if ship:altitude > body_:atm:height {\n      local refused is reentry_stack_refusal(", body);
        }

        // ---- The options ----

        [Fact]
        public void Separate_and_jettison_stage_default_to_auto_and_take_none()
        {
            var t = new Tree();
            Assert.Equal("auto", Run(t, "print claudia_options(lexicon(), reentry_defaults(), \"auto\"):opt:separate.").Single());
            foreach (var key in new[] { "separate", "jettison_stage" })
                foreach (var ok in new[] { "\"none\"", "\"auto\"" })
                    Assert.Equal("", Run(t, "print reentry_unreadable(claudia_merge_options(lexicon(\"" + key + "\", " + ok + "), reentry_defaults())).").Single());
        }

        [Theory]
        [InlineData("separate", "2", "separate must be 'auto' or 'none', not 2")]
        [InlineData("separate", "\"soon\"", "separate must be 'auto' or 'none', not soon")]
        [InlineData("jettison_stage", "1", "jettison_stage must be 'auto' or 'none', not 1")]
        [InlineData("jettison_stage", "\"soon\"", "jettison_stage must be 'auto' or 'none', not soon")]
        public void A_stage_number_or_other_value_is_refused_saying_whats_allowed(string key, string value, string reason)
        {
            Assert.Equal(reason, Run(new Tree(), "print reentry_unreadable(claudia_merge_options(lexicon(\"" + key + "\", " + value + "), reentry_defaults())).").Single());
        }

        [Fact]
        public void The_docs_name_separate_and_the_failure_and_the_catalog_line_says_the_stack_drops()
        {
            var docs = CoreLibrary.FullDocs(new[] { "reentry" });
            Assert.Contains("separate [\"auto\"]", docs);
            Assert.Contains("stack attached below the heat shield: <names>", docs);
            Assert.Contains("drops the stack below this entry's heat shield", CoreLibrary.Blocks.Single(b => b.Name == "reentry").Docs[0]);
        }
    }
}
