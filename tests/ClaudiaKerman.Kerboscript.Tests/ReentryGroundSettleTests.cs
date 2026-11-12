// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.KOS;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #968: the wait <c>reentry</c> ends with, lifted out of the real library and run on kOS.Safe with the game faked: the status follows a script, a tick is 0.5 s, and GROUNDSETTLE answers with Core's real rule.
    /// A bounce restarts the 2 s; the cap ends a craft that never settles. The rule itself is in <c>GroundSettleTests</c>.
    /// </summary>
    public class ReentryGroundSettleTests
    {
        public ReentryGroundSettleTests()
        {
            KosHarness.Register(typeof(ReentryGroundSettleRigFunctions));
        }

        /// <summary>The block's wait, whole, with its clock and its tick replaced by the rig's.</summary>
        private static string Wait()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var start = source.IndexOf("  local settle is lexicon(", StringComparison.Ordinal);
            Assert.True(start >= 0, "the wait is not in the block");
            const string End = "    wait 0.\n  }\n";
            var end = source.IndexOf(End, start, StringComparison.Ordinal);
            Assert.True(end >= 0, "the wait has no end");
            return source.Substring(start, end + End.Length - start).Replace("time:seconds", "tnow").Replace("wait 0.", "advance().");
        }

        /// <summary>The status each tick (L landed, F flying), from UT 100 in 0.5 s ticks; the last letter holds after the script ends. Prints the UT the wait ends at, and the status then.</summary>
        private static string[] Run(string ticks)
        {
            var script = "global tnow is 100.\nglobal ticks is \"" + ticks + "\".\nglobal ticknum is 0.\nglobal status is \"FLYING\".\n"
                + "function claudia_on_ground { return status = \"LANDED\" or status = \"SPLASHED\". }\n"
                + "function advance { set tnow to tnow + 0.5. set ticknum to ticknum + 1. local i is min(ticknum, ticks:length - 1). set status to choose \"LANDED\" if ticks[i] = \"L\" else \"FLYING\". }\n"
                + "set status to choose \"LANDED\" if ticks[0] = \"L\" else \"FLYING\".\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"groundsettle\", { parameter q. return test_ground_settle(q). })).\n"
                + Wait()
                + "print tnow. print status.\n";
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        [Fact]
        public void A_clean_landing_reports_after_about_two_seconds()
        {
            // First touch at 100, since at 100: done at 102.
            Assert.Equal(new[] { "102.5", "LANDED" }, Run("L"));
        }

        [Fact]
        public void A_bounce_that_comes_back_down_reports_two_seconds_after_it_lands_again()
        {
            // Down at 100, up at 100.5 to 101.5, down again from 102: done at 104, landed.
            Assert.Equal(new[] { "104.5", "LANDED" }, Run("LFFFL"));
        }

        [Fact]
        public void A_craft_that_never_settles_ends_the_block_at_thirty_seconds()
        {
            var ticks = string.Concat(Enumerable.Repeat("LF", 100));
            Assert.Equal(new[] { "130.5" }, Run(ticks).Take(1).ToArray());
        }
    }

    /// <summary><c>test_ground_settle(ask)</c>: Core's GROUNDSETTLE answer as the addon gives it. Registered through <see cref="KosHarness.Register"/>.</summary>
    internal static class ReentryGroundSettleRigFunctions
    {
        [Function("test_ground_settle")]
        public class FunctionSettle : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var ask = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosValues.FromPlain(GroundSettle.Call(ask));
            }
        }
    }
}
