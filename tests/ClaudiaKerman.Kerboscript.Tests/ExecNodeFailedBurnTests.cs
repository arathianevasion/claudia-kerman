// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #1357: exec_node removes the node it was asked to fly when the burn fails, as it does when the burn completes. From #1316's failed
    /// land_suborbital flight: exec_node returned "out of fuel" without burning and left the test's node planned. exec_node's own lines are
    /// lifted and run on kOS.Safe with the game faked; the block's finish asks the library's real claudia_node_strays what it would remove.
    /// </summary>
    public class ExecNodeFailedBurnTests
    {
        private static string Swap(string source, string from, string to)
        {
            Assert.Contains(from, source);
            return source.Replace(from, to);
        }

        private static string ExecNode()
        {
            var source = CoreLibrary.Blocks.Single(b => b.Name == "exec_node").Source.Replace("\r\n", "\n");
            var start = source.IndexOf("function exec_node {", StringComparison.Ordinal);
            var s = source.Substring(start, source.IndexOf("// How a burn that ends", start, StringComparison.Ordinal) - start);
            s = Swap(s, "function exec_node {", "function test_exec_node {");
            s = Swap(s, "claudia_begin(", "test_begin(");
            s = Swap(s, "claudia_finish(", "test_finish(");
            s = Swap(s, "claudia_make_safe()", "test_safe()");
            s = Swap(s, "claudia_fail(", "test_fail(");
            s = Swap(s, "addons:claudia:turntime(lexicon(\"max_stopping_time\", steeringmanager:maxstoppingtime))", "test_turn()");
            s = Swap(s, "claudia_stage_into_engines(", "test_lit(");
            s = Swap(s, "claudia_warp_to_burn(", "test_warp(");
            return Swap(s, "claudia_drop_empty(", "test_drop_empty(");
        }

        private static string[] Fly(string stageInto)
        {
            var b = new StringBuilder();
            b.Append("global test_node is lexicon(\"time\", 100, \"radialout\", 0, \"normal\", 0, \"prograde\", -59, \"deltav\", lexicon(\"mag\", 59)).\n");
            b.Append("function test_begin { parameter block, opts, block_defaults, md. return lexicon(\"error\", \"\", \"opt\", opts, \"nodes0\", claudia_node_sigs(list(test_node))). }\n");
            // What the real claudia_finish removes: the nodes that weren't there when the block started, less the ones exec_node took as its own.
            b.Append("function test_finish { parameter ctx, success, reason, extra. print reason + \" removes \" + claudia_node_strays(ctx:nodes0, list(test_node)):length. return ctx. }\n");
            b.Append("function test_safe { }\n");
            b.Append("function test_fail { parameter ctx, reason, extra. return test_finish(ctx, false, reason, extra). }\n");
            b.Append("function test_turn { return lexicon(\"error\", \"\", \"turn_time\", 20). }\n");
            b.Append("function test_drop_empty { parameter ctx. }\n");
            b.Append("function test_lit { parameter ctx. return \"" + stageInto + "\". }\n");
            b.Append("function test_warp { parameter ctx, nd, lead, turn_time. return lexicon(\"turn_s\", 0, \"not_aligned\", 0, \"error\", \"stopped at the warp\"). }\n");
            b.Append(ExecNode()).Append('\n');
            b.Append("test_exec_node(lexicon(\"node\", test_node, \"lead\", \"half_burn\", \"stop_at_min\", \"\", \"resolve\", \"\", \"max_duration\", 100, \"stage_limit\", 0)).\n");
            return KosHarness.Run(CoreLibrary.Bundle(b.ToString())).ToArray();
        }

        [Fact]
        public void A_burn_that_fails_before_it_burns_removes_the_node_it_was_to_fly()
        {
            Assert.Equal(new[] { "out of fuel removes 1" }, Fly("out of fuel"));
        }

        [Fact]
        public void A_burn_that_fails_after_the_turn_removes_the_node_too()
        {
            Assert.Equal(new[] { "stopped at the warp removes 1" }, Fly(""));
        }

        [Fact]
        public void Land_hands_the_flight_plans_nodes_to_LANDSTART_before_it_plans_any_of_its_own()
        {
            var land = CoreLibrary.Blocks.Single(b => b.Name == "land").Source;
            Assert.Contains("\"nodes\", claudia_node_sigs(allnodes)", land);
        }
    }
}
