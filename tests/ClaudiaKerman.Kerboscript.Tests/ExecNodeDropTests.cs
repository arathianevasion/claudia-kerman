// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #985: exec_node drops the empty drop tanks the "auto" rule lets go (#979) before its burn, so every block that burns through it
    /// (capture, transfer_to, circularize, change_orbit, tune_pe, deorbit, return) starts without them. From m20261110-203402-45d1: the Kerbin
    /// capture carried 2.8 t of empty radials through a 950 m/s burn. exec_node's own lines and claudia_drop_empty are lifted and run on
    /// kOS.Safe with the game faked: mock parts as in AutoDropFreeTests, a fired decoupler takes what hangs under it, and the warp to the burn
    /// prints the craft the burn would start with, then ends the block.
    /// </summary>
    public class ExecNodeDropTests
    {
        private sealed class Spec
        {
            public string Uid;
            public string Parent;
            public string[] Modules;
            public double Amount = -1;
        }

        private static readonly string[] None = new string[0];
        private static readonly string[] Shield = { "ModuleDecouple", "ModuleAblator" };
        private static readonly string[] Radial = { "ModuleAnchoredDecoupler" };

        private static Spec P(string uid, string parent, string[] modules, double amount = -1)
        {
            return new Spec { Uid = uid, Parent = parent, Modules = modules, Amount = amount };
        }

        /// <summary>pod, a core tank holding fuel and its engine, and an empty radial set on the tank: R, its empty tank Rt, shield Rs and nose cone Rn.</summary>
        private static List<Spec> EmptyRadialCraft()
        {
            return new List<Spec>
            {
                P("pod", null, new[] { "ModuleCommand" }),
                P("T", "pod", None, 300),
                P("E", "T", new[] { "ModuleEngines" }),
                P("R", "T", Radial),
                P("Rt", "R", None, 0),
                P("Rs", "Rt", Shield),
                P("Rn", "Rt", None),
            };
        }

        private static string Swap(string source, string from, string to)
        {
            Assert.Contains(from, source);
            return source.Replace(from, to);
        }

        private static string Lift(string source, string from, string to)
        {
            source = source.Replace("\r\n", "\n");
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "no " + from);
            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end >= 0, "no " + to);
            return source.Substring(start, end - start);
        }

        private static string ExecNode()
        {
            var s = Lift(CoreLibrary.Blocks.Single(b => b.Name == "exec_node").Source, "function exec_node {", "// How a burn that ends");
            s = Swap(s, "function exec_node {", "function test_exec_node {");
            s = Swap(s, "claudia_begin(", "test_begin(");
            s = Swap(s, "claudia_finish(", "test_finish(");
            s = Swap(s, "claudia_make_safe()", "test_safe()");
            s = Swap(s, "addons:claudia:turntime(lexicon(\"max_stopping_time\", steeringmanager:maxstoppingtime))", "test_turn()");
            s = Swap(s, "claudia_stage_into_engines(", "test_lit(");
            s = Swap(s, "claudia_warp_to_burn(", "test_warp(");
            // Not swapped with Swap: without #985 exec_node doesn't call it, and the test is to fail, not throw.
            return s.Replace("claudia_drop_empty(", "test_drop_empty(");
        }

        private static string DropEmpty()
        {
            var s = Lift(CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source, "function claudia_drop_empty {", "// ---- Waits and warp");
            s = Swap(s, "function claudia_drop_empty {", "function test_drop_empty {");
            s = Swap(s, "addons:claudia:autodropfree():free", "test_free");
            s = Swap(s, "ship:crew", "list()");
            s = Swap(s, "ship:parts", "test_parts");
            s = Swap(s, "stage:number", "2");
            s = Swap(s, "time:seconds", "tnow");
            s = Swap(s, "claudia_drop_holds_core(gone)", "false");
            s = Swap(s, "claudia_fire_decoupler(d)", "test_fire(d)");
            s = Swap(s, "wait 0.5.", "set tnow to tnow + 0.5.");
            s = Swap(s, "claudia_restart_clock(ctx).", "");
            return Swap(s, "addons:claudia:blocknote(", "test_note(");
        }

        private const string Fakes =
            "global tnow is 1000.\n"
            + "function test_begin { parameter block, opts, block_defaults, md.\n"
            + "  return lexicon(\"block\", block, \"error\", \"\", \"opt\", opts, \"parts\", test_parts:length, \"dropped\", list(), \"drop_tried\", list(),\n"
            + "    \"drop_tries\", lexicon(), \"drop_ut\", 0, \"drop_key\", \"\", \"drop_scan\", 0, \"drop_mode\", \"full\"). }\n"
            + "function test_finish { parameter ctx, success, reason, extra. print \"dropped \" + ctx:dropped:join(\",\"). return ctx. }\n"
            + "function test_safe { }\n"
            + "function test_note { parameter block, note. }\n"
            + "function test_turn { return lexicon(\"error\", \"\", \"turn_time\", 20). }\n"
            + "function test_lit { parameter ctx. return \"\". }\n"
            + "function test_uids { local ids is list(). for p in test_parts { ids:add(p:uid). } return ids:join(\",\"). }\n"
            + "function test_warp { parameter ctx, nd, lead, turn_time.\n"
            + "  print \"burn starts with \" + test_uids().\n"
            + "  return lexicon(\"turn_s\", 0, \"not_aligned\", 0, \"error\", \"stopped at the warp\"). }\n"
            // A fired decoupler takes itself and everything under it off the craft.
            + "function test_under { parameter p, d. local q is p. until false { if q:uid = d:uid { return true. } if not q:hasparent { return false. } set q to q:parent. } }\n"
            + "function test_fire { parameter d. local kept is list(). for p in test_parts { if not test_under(p, d) { kept:add(p). } } set test_parts to kept. return true. }\n";

        private static bool IsDecoupler(Spec p) => p.Modules.Contains("ModuleDecouple") || p.Modules.Contains("ModuleAnchoredDecoupler");

        // The kOS "decoupler" of a part: the nearest ancestor with a decoupler module (heat shields included), "" if none.
        private static string DecouplerOf(List<Spec> s, Spec p)
        {
            for (var q = s.Find(x => x.Uid == p.Parent); q != null; q = s.Find(x => x.Uid == q.Parent))
                if (IsDecoupler(q)) return "p_" + q.Uid;
            return "\"\"";
        }

        /// <summary>Flies exec_node to its warp on the craft with the kinds Core lets go: what the burn starts with, and what was dropped.</summary>
        private static string[] Fly(List<Spec> s, string free, string dropEmpty = "\"auto\"")
        {
            var b = new StringBuilder();
            foreach (var p in s)
            {
                var res = p.Amount < 0 ? "" : "lexicon(\"name\", \"LiquidFuel\", \"amount\", " + p.Amount + ", \"capacity\", 400)";
                var test = p.Modules.Length == 0 ? "false" : string.Join(" or ", p.Modules.Select(m => "m = \"" + m + "\""));
                b.Append("global p_" + p.Uid + " is lexicon(\"uid\", \"" + p.Uid + "\", \"name\", \"" + p.Uid + "\", \"tag\", \"\", "
                    + "\"hasparent\", " + (p.Parent != null ? "true" : "false") + ", \"parent\", " + (p.Parent != null ? "p_" + p.Parent : "\"\"") + ", "
                    + "\"decoupler\", " + DecouplerOf(s, p) + ", \"resources\", list(" + res + "), "
                    + "\"hasmodule\", { parameter m. return " + test + ". }).\n");
            }
            b.Append("global test_parts is list(" + string.Join(", ", s.Select(p => "p_" + p.Uid)) + ").\n");
            b.Append("global test_free is " + free + ".\n");
            b.Append(Fakes).Append(ExecNode()).Append('\n').Append(DropEmpty()).Append('\n');
            b.Append("test_exec_node(lexicon(\"node\", lexicon(\"deltav\", lexicon(\"mag\", 950)), \"lead\", \"half_burn\", \"stop_at_min\", \"\", \"resolve\", \"\", "
                + "\"max_duration\", 100, \"stage_limit\", 0, \"drop_empty\", " + dropEmpty + ")).\n");
            return KosHarness.Run(CoreLibrary.Bundle(b.ToString())).ToArray();
        }

        [Fact]
        public void An_empty_radial_set_with_a_shield_and_a_nose_cone_is_dropped_before_the_burn()
        {
            Assert.Equal(new[] { "burn starts with pod,T,E", "dropped Rt,Rs,Rn" }, Fly(EmptyRadialCraft(), "list(\"shield\")"));
        }

        [Fact]
        public void A_radial_tank_with_propellant_left_stays_while_the_empty_one_goes()
        {
            var s = EmptyRadialCraft();
            s.AddRange(new[] { P("Q", "T", Radial), P("Qt", "Q", None, 50), P("Qs", "Qt", Shield), P("Qn", "Qt", None) });
            Assert.Equal(new[] { "burn starts with pod,T,E,Q,Qt,Qs,Qn", "dropped Rt,Rs,Rn" }, Fly(s, "list(\"shield\")"));
        }

        [Fact]
        public void A_stack_Entry_Shield_over_an_empty_tank_stays_while_the_empty_radial_set_goes()
        {
            // #979's guard: only a radial decoupler lets a shield go, so the stack decoupler D above the shield S and its empty tank never fires.
            var s = EmptyRadialCraft();
            s.AddRange(new[] { P("D", "E", new[] { "ModuleDecouple" }), P("S", "D", Shield), P("St", "S", None, 0) });
            Assert.Equal(new[] { "burn starts with pod,T,E,D,S,St", "dropped Rt,Rs,Rn" }, Fly(s, "list(\"shield\", \"leg\")"));
        }

        [Fact]
        public void With_drop_empty_off_nothing_goes()
        {
            Assert.Equal(new[] { "burn starts with pod,T,E,R,Rt,Rs,Rn", "dropped " }, Fly(EmptyRadialCraft(), "list(\"shield\")", "list()"));
        }
    }
}
