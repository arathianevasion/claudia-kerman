// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#1361: the block test runner window's filter, ordering and opening tab, on logs in the format BlockTestRunner.Finish writes.</summary>
    public class BlockTestListTests
    {
        private static readonly string[] Tests = { "land", "launch", "site_hop_landed_mun", "tools/plan_x", "warp" };

        [Fact]
        public void TypingHopListsTheSiteHopTestOnItsTab()
        {
            var results = BlockTestLog.Latest("");
            Assert.Equal(new[] { "site_hop_landed_mun" }, BlockTestList.Shown(Tests, results, BlockTestList.Unvalidated, "hop").ToArray());
        }

        [Fact]
        public void TheFilterIgnoresCaseAndSpacesAndAppliesOnEveryTab()
        {
            var results = BlockTestLog.Latest("2026-11-01 10:00:00Z FAIL launch: x\n2026-11-02 10:00:00Z PASS land\n");
            Assert.Equal(new[] { "launch" }, BlockTestList.Shown(Tests, results, BlockTestList.Failed, " LAUN ").ToArray());
            Assert.Equal(new[] { "land" }, BlockTestList.Shown(Tests, results, BlockTestList.Passed, "AN").ToArray());
            Assert.Empty(BlockTestList.Shown(Tests, results, BlockTestList.Passed, "hop"));
            Assert.Equal(5, BlockTestList.Filter(Tests, "  ").Count);
        }

        [Fact]
        public void TheTestRunLastIsListedFirstOnItsTab()
        {
            var results = BlockTestLog.Latest("2026-11-01 10:00:00Z FAIL warp: x\n2026-11-02 10:00:00Z FAIL land: y\n2026-11-02 10:00:00Z FAIL launch: z\n");
            Assert.Equal("launch", BlockTestList.LastRun(Tests, results));
            Assert.Equal(new[] { "launch", "land", "warp" }, BlockTestList.Shown(Tests, results, BlockTestList.Failed, "").ToArray());
        }

        [Fact]
        public void TheWindowOpensOnTheTabOfTheTestRunLast()
        {
            Assert.Equal(BlockTestList.Unvalidated, BlockTestList.OpeningTab(Tests, BlockTestLog.Latest("")));
            Assert.Equal(BlockTestList.Passed, BlockTestList.OpeningTab(Tests, BlockTestLog.Latest("2026-11-01 10:00:00Z FAIL land: x\n2026-11-02 10:00:00Z PASS warp\n")));
            Assert.Equal(BlockTestList.Failed, BlockTestList.OpeningTab(Tests, BlockTestLog.Latest("2026-11-01 10:00:00Z PASS warp\n2026-11-02 10:00:00Z FAIL land: x\n")));
        }

        [Fact]
        public void ARerunMakesTheTestTheLastRunAgain()
        {
            var results = BlockTestLog.Latest("2026-11-01 10:00:00Z PASS land\n2026-11-02 10:00:00Z PASS warp\n2026-11-03 10:00:00Z PASS land\n");
            Assert.Equal("land", BlockTestList.LastRun(Tests, results));
        }

        [Fact]
        public void ALogNamingNoShippedTestOpensOnUnvalidated()
        {
            var results = BlockTestLog.Latest("2026-11-01 10:00:00Z PASS retired_test\n");
            Assert.Null(BlockTestList.LastRun(Tests, results));
            Assert.Equal(BlockTestList.Unvalidated, BlockTestList.OpeningTab(Tests, results));
        }
    }
}
