// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>The window's reader for block-tests.log (#922), on lines in the format BlockTestRunner.Finish writes.</summary>
    public class BlockTestLogTests
    {
        [Fact]
        public void LastRunOfATestWins()
        {
            var log = "2026-11-01 10:00:00Z FAIL launch: orbit.ap < 80000 (got 1)\n"
                + "    detail line\n"
                + "2026-11-02 10:00:00Z PASS launch\n"
                + "2026-11-03 10:00:00Z FAIL launch: timed out after 600 s\n";
            var latest = BlockTestLog.Latest(log);
            Assert.Single(latest);
            Assert.False(latest["launch"].Passed);
            Assert.Equal("2026-11-03", latest["launch"].Date);
            Assert.Equal("timed out after 600 s", latest["launch"].Reason);
        }

        [Fact]
        public void APassAfterAFailMovesToPassed()
        {
            var latest = BlockTestLog.Latest("2026-11-01 10:00:00Z FAIL land: x\n2026-11-05 23:59:59Z PASS land\n");
            Assert.True(latest["land"].Passed);
            Assert.Equal("2026-11-05", latest["land"].Date);
            Assert.Equal("", latest["land"].Reason);
        }

        [Fact]
        public void ToolsNamesAreKept()
        {
            var latest = BlockTestLog.Latest("2026-11-01 10:00:00Z PASS tools/plan_x\n2026-11-01 10:05:00Z FAIL tools/plan_y: no result\r\n");
            Assert.True(latest["tools/plan_x"].Passed);
            Assert.False(latest["tools/plan_y"].Passed);
            Assert.Equal("no result", latest["tools/plan_y"].Reason);
        }

        [Fact]
        public void MalformedLinesAreSkipped()
        {
            var log = "garbage\n\n    2026-11-01 10:00:00Z PASS indented\n"
                + "2026-13-45 99:00:00Z PASS badstamp\n"
                + "2026-11-01 10:00:00Z MAYBE launch\n"
                + "2026-11-01 10:00:00Z PASS\n"
                + "2026-11-01 10:00:00Z PASS ok_test\n";
            var latest = BlockTestLog.Latest(log);
            Assert.Single(latest);
            Assert.True(latest["ok_test"].Passed);
        }

        [Fact]
        public void EmptyOrMissingTextIsNoResults()
        {
            Assert.Empty(BlockTestLog.Latest(""));
            Assert.Empty(BlockTestLog.Latest(null));
        }
    }
}
