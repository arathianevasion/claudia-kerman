// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #817: a small Course Correction flown accurately, and #805: one Core couldn't solve doesn't fail the block. BURNWORTH's answer is Core's
    /// (<see cref="BurnWorth"/>, tested there); here the Kerboscript that flies it, on kOS.Safe, with the game's values given.
    /// </summary>
    public class SmallCorrectionTests
    {
        private static string Library()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClaudiaKerman.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "kerboscript", "library", "claudia_lib.ks"));
        }

        private static string Body(string source, string name)
        {
            var start = source.IndexOf("\nfunction " + name + " {", StringComparison.Ordinal);
            Assert.True(start >= 0, name);
            var end = source.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
            return source.Substring(start, (end < 0 ? source.Length : end) - start);
        }

        private static string N(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// exec_node's burn loop, tick by tick at 0.02 s: the throttle claudia_burn_throttle gives, capped at BURNWORTH's max_throttle, until the node is overshot or
        /// under exec_node's done_dv (BURNWORTH's, under a quarter of its 0.5 m/s max_residual). Prints the first tick's m/s, what is left (negative: overshot) and the ticks.
        /// </summary>
        private static double[] Fly(double dv, double accel, double maxThrottle, double doneDv)
        {
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "local left is " + N(dv) + ". local done_dv is min(" + N(doneDv) + ", 0.5 / 4). local first is -1. local ticks is 0.\n"
                + "until left < done_dv or ticks > 100000 {\n"
                + "  local tset is min(" + N(maxThrottle) + ", claudia_burn_throttle(left, " + N(accel) + ", 0)).\n"
                + "  local tick is tset * " + N(accel) + " * 0.02.\n"
                + "  if first < 0 { set first to tick. }\n"
                + "  set left to left - tick.\n"
                + "  set ticks to ticks + 1.\n"
                + "}\n"
                + "print first. print left. print ticks.")).ToArray();
            return printed.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        }

        private static IDictionary<string, object> Worth(double dv, double accel)
        {
            return BurnWorth.Call(new Dictionary<string, object>
            {
                { "block", "correction" }, { "fix", new Dictionary<string, object> { { "error", "" }, { "dv", dv }, { "error_km", 50.0 } } }, { "accel", accel }, { "max_residual", -1.0 },
            });
        }

        /// <summary>A correction under 1 m/s flies capped, no tick over 5% of it, and ends within 0.05 m/s of its node (and within a tenth of it).</summary>
        [Theory]
        [InlineData(0.9, 19)]
        [InlineData(0.5, 19)]
        [InlineData(0.05, 19)]
        [InlineData(0.05, 60)]
        [InlineData(0.3, 60)]
        public void A_sub_1_m_s_correction_flies_limited_and_within_0_05_m_s_of_its_node(double dv, double accel)
        {
            var worth = Worth(dv, accel);
            Assert.False((bool)worth["skip"]);
            var flown = Fly(dv, accel, (double)worth["max_throttle"], (double)worth["done_dv"]);
            Assert.InRange(flown[0], 0, BurnWorth.TickShare * dv * 1.0001);
            Assert.InRange(Math.Abs(flown[1]), 0, Math.Min(0.05, dv / 10));
        }

        /// <summary>Uncapped, the throttle's 0.5% floor on a strong engine makes one tick 12% of a 0.05 m/s burn: the cap is what holds it to 5%.</summary>
        [Fact]
        public void Without_the_cap_a_tiny_burn_on_a_strong_engine_takes_big_ticks()
        {
            Assert.True(Fly(0.05, 60, 1, BurnWorth.LeastDone)[0] > 2 * BurnWorth.TickShare * 0.05);
        }

        /// <summary>
        /// claudia_fly_correction itself, with BURNWORTH's answer and the craft's acceleration given: Core's answer for an aimed correction it couldn't solve (#805) is no
        /// burn and no error, so nothing flies and there is no reason to fail the block; an error is still the reason.
        /// </summary>
        [Theory]
        [InlineData("", 0.0, true, "")]
        [InlineData("found no correction: off", 0.0, true, "found no correction: off")]
        public void A_correction_Core_could_not_solve_flies_nothing_and_does_not_fail_the_block(string error, double dv, bool skip, string reason)
        {
            var fly = Body(Library(), "claudia_fly_correction")
                .Replace("addons:claudia:burnworth(", "test_worth(")
                .Replace("ship:availablethrust / max(0.001, ship:mass)", "19");
            Assert.Contains("test_worth(lexicon(\"block\", \"correction\", \"fix\", fix,", fly);
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "function test_worth { parameter ask. return lexicon(\"skip\", " + (skip ? "true" : "false") + ", \"max_throttle\", 1, \"max_residual\", -1, \"done_dv\", 0.1). }\n"
                + fly.Replace("function claudia_fly_correction", "function test_fly_correction") + "\n"
                + "local done is test_fly_correction(lexicon(), lexicon(\"error\", \"" + error + "\", \"dv\", " + N(dv) + ", \"error_km\", 300)).\n"
                + "print \"[\" + done:reason + \"]\". print done:dv.")).ToArray();
            Assert.Equal(new[] { "[" + reason + "]", "0" }, printed);
        }

        /// <summary>The Kerboscript side of the rule (ADR 0014): the correction asks Core, and exec_node caps the throttle it flies at.</summary>
        [Fact]
        public void The_correction_asks_Core_and_exec_node_caps_its_throttle()
        {
            var lib = Library();
            var fly = Body(lib, "claudia_fly_correction");
            Assert.Contains("addons:claudia:burnworth(lexicon(\"block\", \"correction\", \"fix\", fix, \"accel\", ship:availablethrust / max(0.001, ship:mass), \"max_residual\", max_residual))", fly);
            Assert.Contains("claudia_burn(ctx, nextnode, worth:max_residual, false, worth)", fly);
            Assert.DoesNotContain("fix:dv < 0.1", fly);
            Assert.Contains("set flight:max_throttle to fine:max_throttle.", Body(lib, "claudia_burn"));
            Assert.Contains("set flight:done_dv to fine:done_dv.", Body(lib, "claudia_burn"));
            var exec = CoreLibrary.Blocks.Single(b => b.Name == "exec_node").Source;
            Assert.Contains("set tset to min(opt:max_throttle, claudia_burn_throttle(", exec);
            Assert.Contains("local done_dv is min(opt:done_dv, opt:max_residual / 4).", exec);
            Assert.Contains("\"max_throttle\", 1, \"done_dv\", 0.1)", exec);
        }
    }
}
