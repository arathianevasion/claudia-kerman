// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// One drop_empty scan walks the craft once and costs a fixed number of kOS instructions (#533). claudia_tick runs a
    /// scan every 0.5 s inside every Burning block's loop, so a slow one stalls the block (a stale launch aim, a late burn cut).
    /// The craft is a 76-part, 7-decoupler fixture built as mock parts; the harness counts steps, so cost = steps × IPU.
    /// </summary>
    public class DropScanBudgetTests
    {
        // The budget for one scan on the fixture: 40,000 instructions, under the 50k a second holds at IPU 1000.
        // Measured 36,500 here, where each hasmodule is a kOS delegate (about 17 instructions a call); a real part's is a
        // native suffix, so a scan in the game costs less.
        // The old path, per the ticket: about 1,600 kind checks at about 390 instructions is about 624,000 per scan, so this
        // budget is more than ten times under it (the ticket's estimate; no test measures the old path).
        private const int ScanBudgetInstructions = 40000;
        private const int Ipu = 100;

        private sealed class Spec
        {
            public string Uid;
            public int Parent;
            public string[] Modules;
            public double Amount = -1;
            public double Capacity = 400;
        }

        private static readonly string[] None = new string[0];
        private static readonly string[] Dec = { "ModuleDecouple" };

        /// <summary>
        /// pod, crew cabin, battery, heat shield; D1 (a tank, engine); D2 (empty tank + engine) with four radial boosters:
        /// B1 (empty tank + nose) fires, B2 (empty tank + nose, holding B2b: empty tank + nose) fires with B2b, B3 holds fuel,
        /// B4's nose holds a kerbal; trim to 76 parts. 7 decouplers (the shield is not one): D1, D2, B1, B2, B2b, B3, B4.
        /// </summary>
        private static List<Spec> Fixture()
        {
            var s = new List<Spec>();
            Spec Add(string uid, int parent, string[] modules, double amount = -1, double capacity = 400)
            {
                s.Add(new Spec { Uid = uid, Parent = parent, Modules = modules, Amount = amount, Capacity = capacity });
                return s.Last();
            }
            Add("pod", -1, new[] { "ModuleCommand" });                       // 0
            Add("cabin", 0, new[] { "ModuleScienceContainer" });             // 1
            Add("battery", 0, None, 50, 100);                                // 2 (charge-like; counted as a tank of 100 holding 50)
            Add("shield", 1, new[] { "ModuleDecouple", "ModuleAblator" });   // 3
            Add("D1", 3, Dec);                                               // 4
            Add("T1", 4, None, 400);                                         // 5
            Add("E1", 5, new[] { "ModuleEnginesFX" });                       // 6
            Add("D2", 6, Dec);                                               // 7
            Add("T2", 7, None, 0);                                           // 8
            Add("E2", 8, new[] { "ModuleEngines" });                         // 9
            var t2 = 8;
            Add("B1", t2, Dec); Add("B1t", s.Count - 1, None, 0); Add("B1n", s.Count - 1, None);
            Add("B2", t2, Dec); var b2t = s.Count; Add("B2t", s.Count - 1, None, 0); Add("B2n", b2t, None);
            Add("B2b", b2t, Dec); Add("B2bt", s.Count - 1, None, 0); Add("B2bn", s.Count - 1, None);
            Add("B3", t2, Dec); Add("B3t", s.Count - 1, None, 100); Add("B3n", s.Count - 1, None);
            Add("B4", t2, Dec); Add("B4t", s.Count - 1, None, 0); Add("B4n", s.Count - 1, None);
            for (var i = 0; s.Count < 76; i++)
                Add("trim" + i, i % 2 == 0 ? 0 : 5, i % 7 == 0 ? new[] { "ModuleRCSFX" } : None);
            return s;
        }

        // The kOS "decoupler" of a part: the nearest ancestor with a decoupler module (heat shields included), "" if none.
        private static string DecouplerOf(List<Spec> s, int i)
        {
            for (var p = s[i].Parent; p >= 0; p = s[p].Parent)
                if (s[p].Modules.Contains("ModuleDecouple")) return "p_" + p;
            return "\"\"";
        }

        // A part's hasmodule. A real one is a native suffix; a kOS delegate costs far more per call, so the budget run
        // uses the cheapest delegate that answers right and the counting run (which pins how often it is asked) the other.
        private static string HasModule(string[] modules, bool counting)
        {
            var test = modules.Length == 0 ? "false" : string.Join(" or ", modules.Select(m => "m = \"" + m + "\""));
            return counting
                ? "{ parameter m. set hm_calls to hm_calls + 1. if m = \"ModuleEngines\" { set engine_calls to engine_calls + 1. } return " + test + ". }"
                : "{ parameter m. return " + test + ". }";
        }

        private static string FixtureScript(bool counting = false)
        {
            var s = Fixture();
            var b = new StringBuilder("global hm_calls is 0.\nglobal engine_calls is 0.\n");
            for (var i = 0; i < s.Count; i++)
            {
                var res = s[i].Amount < 0 ? "" : "lexicon(\"name\", \"LiquidFuel\", \"amount\", " + s[i].Amount + ", \"capacity\", " + s[i].Capacity + ")";
                b.Append("local p_" + i + " is lexicon(\"uid\", \"" + s[i].Uid + "\", \"name\", \"" + s[i].Uid + "\", \"tag\", \"\", "
                    + "\"hasparent\", " + (s[i].Parent >= 0 ? "true" : "false") + ", \"parent\", " + (s[i].Parent >= 0 ? "p_" + s[i].Parent : "\"\"") + ", "
                    + "\"decoupler\", " + (s[i].Parent >= 0 ? DecouplerOf(s, i) : "\"\"") + ", \"resources\", list(" + res + "), "
                    + "\"hasmodule\", " + HasModule(s[i].Modules, counting) + ").\n");
            }
            b.Append("local parts is list(" + string.Join(", ", Enumerable.Range(0, s.Count).Select(i => "p_" + i)) + ").\n");
            b.Append("local crewed is lexicon(\"B4n\", true).\n");
            return b.ToString();
        }

        private const string PrintPlan =
            "for step in plan { local ids is list(). for g in step:gone { ids:add(g:uid). } print step:uid + \":\" + ids:join(\",\"). }\n";

        [Fact]
        public void The_fixture_is_76_parts_with_7_decouplers_four_deep()
        {
            var s = Fixture();
            Assert.Equal(76, s.Count);
            Assert.Equal(7, s.Count(p => p.Modules.Contains("ModuleDecouple") && !p.Modules.Contains("ModuleAblator")));
        }

        [Fact]
        public void A_scan_fires_the_empty_booster_decouplers_and_not_the_rest()
        {
            var script = FixtureScript()
                + "local scan is claudia_scan_craft(parts, crewed).\n"
                + "local plan is claudia_scan_plan(scan, true, \"auto\", list()).\n" + PrintPlan;
            // B1 and B2 (with B2b's parts, which B2 also drops) fire; B2b alone too. D1/D2 hold engines, B3 fuel, B4 a kerbal.
            Assert.Equal(new[] { "B1:B1t,B1n", "B2:B2t,B2n,B2b,B2bt,B2bn", "B2b:B2bt,B2bn" },
                KosHarness.Run(CoreLibrary.Bundle(script)).ToArray());
        }

        [Fact]
        public void A_scan_with_the_kerbal_not_known_to_be_aboard_fires_B4_too_and_a_tried_decoupler_is_skipped()
        {
            var script = FixtureScript().Replace("lexicon(\"B4n\", true)", "lexicon()")
                + "local scan is claudia_scan_craft(parts, crewed).\n"
                + "local plan is claudia_scan_plan(scan, true, \"auto\", list(\"B2\")).\n" + PrintPlan;
            Assert.Equal(new[] { "B1:B1t,B1n", "B2b:B2bt,B2bn", "B4:B4t,B4n" }, KosHarness.Run(CoreLibrary.Bundle(script)).ToArray());
        }

        [Fact]
        public void A_list_names_the_parts_whose_decoupler_is_judged_and_a_shield_is_never_one()
        {
            var script = FixtureScript()
                + "local scan is claudia_scan_craft(parts, crewed).\n"
                + "local plan is claudia_scan_plan(scan, false, list(\"B1n\", \"shield\", \"B4t\"), list()).\n" + PrintPlan;
            // list mode skips the kinds check: B4's kerbal does not hold it back.
            Assert.Equal(new[] { "B1:B1t,B1n", "B4:B4t,B4n" }, KosHarness.Run(CoreLibrary.Bundle(script)).ToArray());
        }

        [Fact]
        public void The_craft_walk_asks_each_part_for_its_kind_once()
        {
            var script = FixtureScript(true)
                + "local scan is claudia_scan_craft(parts, crewed).\n"
                + "local plan is claudia_scan_plan(scan, true, \"auto\", list()).\n"
                + "print engine_calls.\nprint hm_calls.\n";
            var printed = KosHarness.Run(CoreLibrary.Bundle(script));
            // ModuleEngines is the first kind check: at most one per part (a part nothing can use the kind of skips it),
            // and none while judging.
            var kindChecks = int.Parse(printed[0]);
            Assert.Equal(47, kindChecks);
            // 3 decoupler checks on each of 76 parts, 17 kind checks on the 47 whose kind the scan can use: the most it can ask.
            Assert.True(int.Parse(printed[1]) <= (76 * 3 + 47 * 17), "hasmodule asked " + printed[1] + " times");
        }

        [Fact]
        public void One_scan_on_the_76_part_fixture_fits_40000_instructions()
        {
            var script = FixtureScript()
                + "local scan is claudia_scan_craft(parts, crewed).\n"
                + "local plan is claudia_scan_plan(scan, true, \"auto\", list()).\n" + PrintPlan;
            int steps, baseline;
            KosHarness.Run(CoreLibrary.Bundle(FixtureScript()), Ipu, 5000, out baseline);
            KosHarness.Run(CoreLibrary.Bundle(script), Ipu, 5000, out steps);
            // Minus the run that only builds the mock parts; the printing of the plan stays in, a few hundred instructions.
            var instructions = (steps - baseline) * Ipu;
            Assert.True(instructions <= ScanBudgetInstructions, "one scan took up to " + instructions + " instructions (fixture alone " + baseline * Ipu + ")");
        }

        [Fact]
        public void The_old_kind_check_cost_a_list_build_and_the_new_one_does_not()
        {
            // Plain part: matches no check. The old kind check built a 17-entry list of lists on every call.
            const string plain = "local part is lexicon(\"hasmodule\", { parameter m. return false. }).\n";
            int calls, baseline;
            KosHarness.Run(CoreLibrary.Bundle(plain + "local k is 0.\n"), Ipu, 5000, out baseline);
            KosHarness.Run(CoreLibrary.Bundle(plain + "local k is claudia_part_kind(part).\n"), Ipu, 5000, out calls);
            // One call, mock delegates included: well under the ~390 the old list build made it.
            Assert.True((calls - baseline) * Ipu <= 250, "one kind check took about " + (calls - baseline) * Ipu + " instructions");
        }

        [Fact]
        public void The_library_has_no_per_scan_kind_or_module_checks_outside_the_one_walk()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source;
            string Between(string from, string to)
            {
                var a = source.IndexOf(from, System.StringComparison.Ordinal);
                return source.Substring(a, source.IndexOf(to, a, System.StringComparison.Ordinal) - a);
            }
            foreach (var judging in new[]
            {
                Between("function claudia_scan_gone {", "function claudia_scan_plan {"),
                Between("function claudia_scan_plan {", "// Fires decouplers whose tanks"),
                Between("function claudia_drop_empty {", "// ---- Waits and warp"),
            })
            {
                Assert.DoesNotContain("claudia_part_kind", judging);
                Assert.DoesNotContain("hasmodule", judging);
                Assert.DoesNotContain("claudia_decoupler_of", judging);
                Assert.DoesNotContain("claudia_dropped_with", judging);
            }
        }
    }
}
