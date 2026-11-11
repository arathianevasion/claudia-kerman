// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #979: drop_empty's "auto" drop with the kinds Core lets go (ADDONS:CLAUDIA:AUTODROPFREE, AutoDrop.FreeKinds). From flight
    /// m20261110-203402-45d1: a radial pod is a decoupler, an empty FL-T400, a heat shield and a nose cone, after the player's redesign
    /// landing legs too. Judged by claudia_scan_craft and claudia_scan_plan on mock parts, as DropScanBudgetTests does.
    /// </summary>
    public class AutoDropFreeTests
    {
        private sealed class Spec
        {
            public string Uid;
            public int Parent;
            public string[] Modules;
            public double Amount = -1;
        }

        private static readonly string[] None = new string[0];
        private const string Shields = "list(\"shield\")";
        private const string ShieldsAndLegs = "list(\"shield\", \"leg\")";

        /// <summary>pod (a kerbal), core stack tank and engine, and one radial: R, its empty tank Rt, shield Rs and nose Rn on the tank, and extras on Rt.</summary>
        private static List<Spec> Radial(params (string uid, string parent, string[] modules)[] extras)
        {
            var s = new List<Spec>
            {
                new Spec { Uid = "pod", Parent = -1, Modules = new[] { "ModuleCommand" } },
                new Spec { Uid = "T", Parent = 0, Modules = None, Amount = 300 },
                new Spec { Uid = "E", Parent = 1, Modules = new[] { "ModuleEngines" } },
                new Spec { Uid = "R", Parent = 1, Modules = new[] { "ModuleAnchoredDecoupler" } },
                new Spec { Uid = "Rt", Parent = 3, Modules = None, Amount = 0 },
                new Spec { Uid = "Rs", Parent = 4, Modules = new[] { "ModuleDecouple", "ModuleAblator" } },
                new Spec { Uid = "Rn", Parent = 4, Modules = None },
            };
            foreach (var (uid, parent, modules) in extras)
                s.Add(new Spec { Uid = uid, Parent = s.FindIndex(p => p.Uid == parent), Modules = modules });
            return s;
        }

        private static bool IsDecoupler(Spec p)
        {
            return p.Modules.Contains("ModuleDecouple") || p.Modules.Contains("ModuleAnchoredDecoupler");
        }

        // The kOS "decoupler" of a part: the nearest ancestor with a decoupler module (heat shields included), "" if none.
        private static string DecouplerOf(List<Spec> s, int i)
        {
            for (var p = s[i].Parent; p >= 0; p = s[p].Parent)
                if (IsDecoupler(s[p])) return "p_" + p;
            return "\"\"";
        }

        private static string[] Plan(List<Spec> s, string free, string wanted = null)
        {
            var b = new StringBuilder();
            for (var i = 0; i < s.Count; i++)
            {
                var res = s[i].Amount < 0 ? "" : "lexicon(\"name\", \"LiquidFuel\", \"amount\", " + s[i].Amount + ", \"capacity\", 400)";
                var test = s[i].Modules.Length == 0 ? "false" : string.Join(" or ", s[i].Modules.Select(m => "m = \"" + m + "\""));
                b.Append("local p_" + i + " is lexicon(\"uid\", \"" + s[i].Uid + "\", \"name\", \"" + s[i].Uid + "\", \"tag\", \"\", "
                    + "\"hasparent\", " + (s[i].Parent >= 0 ? "true" : "false") + ", \"parent\", " + (s[i].Parent >= 0 ? "p_" + s[i].Parent : "\"\"") + ", "
                    + "\"decoupler\", " + DecouplerOf(s, i) + ", \"resources\", list(" + res + "), "
                    + "\"hasmodule\", { parameter m. return " + test + ". }).\n");
            }
            b.Append("local parts is list(" + string.Join(", ", Enumerable.Range(0, s.Count).Select(i => "p_" + i)) + ").\n");
            b.Append("local scan is claudia_scan_craft(parts, lexicon(\"pod\", true)).\n");
            b.Append("local plan is claudia_scan_plan(scan, " + (wanted == null ? "true, \"auto\"" : "false, " + wanted) + ", list(), " + free + ").\n");
            b.Append("for step in plan { local ids is list(). for g in step:gone { ids:add(g:uid). } print step:uid + \":\" + ids:join(\",\"). }\n");
            return KosHarness.Run(CoreLibrary.Bundle(b.ToString())).ToArray();
        }

        [Fact]
        public void AnEmptyRadialWithAShieldAndANoseConeFires()
        {
            Assert.Equal(new[] { "R:Rt,Rs,Rn" }, Plan(Radial(), Shields));
        }

        [Fact]
        public void WithLegsItFiresOnlyWhenLegsAreFree()
        {
            var legged = Radial(("Rl1", "Rt", new[] { "ModuleWheelBase" }), ("Rl2", "Rt", new[] { "ModuleWheelBase" }));
            Assert.Equal(new[] { "R:Rt,Rs,Rn,Rl1,Rl2" }, Plan(legged, ShieldsAndLegs));
            Assert.Empty(Plan(legged, Shields));
        }

        [Fact]
        public void ParachutesAndEnginesStillBlockWithEverythingFree()
        {
            Assert.Empty(Plan(Radial(("Rc", "Rt", new[] { "ModuleParachute" })), ShieldsAndLegs));
            Assert.Empty(Plan(Radial(("Re", "Rt", new[] { "ModuleEngines" })), ShieldsAndLegs));
        }

        [Fact]
        public void APartUnderTheShieldLeavesWithTheRadialAndIsJudged()
        {
            // The shield's own decoupler module is no decoupler (#516), so what hangs under it leaves with R: an engine there blocks, trim goes.
            Assert.Empty(Plan(Radial(("Re", "Rs", new[] { "ModuleEngines" })), ShieldsAndLegs));
            Assert.Equal(new[] { "R:Rt,Rs,Rn,Rx" }, Plan(Radial(("Rx", "Rs", None)), Shields));
        }

        /// <summary>pod (a kerbal), a stack decoupler D, then the given parts in a stack under it, each the child of the one before.</summary>
        private static List<Spec> Stack(params Spec[] under)
        {
            var s = new List<Spec>
            {
                new Spec { Uid = "pod", Parent = -1, Modules = new[] { "ModuleCommand" } },
                new Spec { Uid = "D", Parent = 0, Modules = new[] { "ModuleDecouple" } },
            };
            foreach (var p in under)
            {
                p.Parent = s.Count - 1;
                s.Add(p);
            }
            return s;
        }

        private static Spec Shield() => new Spec { Uid = "S", Modules = new[] { "ModuleDecouple", "ModuleAblator" } };
        private static Spec EmptyTank() => new Spec { Uid = "T", Modules = None, Amount = 0 };

        // The fix round's probe (#940/#948: pod, decoupler, shield is a real design): only a radial decoupler lets a shield go (#843).
        [Fact]
        public void AStackDecouplerAboveAShieldAndAnEmptyTankDoesNotFire()
        {
            Assert.Empty(Plan(Stack(Shield(), EmptyTank()), ShieldsAndLegs));
        }

        [Fact]
        public void AStackDecouplerAboveAnEmptyTankAndAShieldDoesNotFire()
        {
            Assert.Empty(Plan(Stack(EmptyTank(), Shield()), ShieldsAndLegs));
        }

        [Fact]
        public void ANamedTankUnderAStackShieldDoesNotTakeTheShield()
        {
            // The walk past a shield stops at a stack decoupler: the named tank has no decoupler of its own, as before #979.
            Assert.Empty(Plan(Stack(Shield(), EmptyTank()), ShieldsAndLegs, "list(\"T\")"));
        }

        [Fact]
        public void WithNothingFreeAShieldStillBlocks()
        {
            Assert.Empty(Plan(Radial(), "list()"));
        }
    }
}
