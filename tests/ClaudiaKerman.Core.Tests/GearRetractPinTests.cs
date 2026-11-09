// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#946: ascend and launch retract the legs once clear of the ground. Source pins, as the repo tests Kerboscript.</summary>
    public class GearRetractPinTests
    {
        private static string Library(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "kerboscript", "library", file))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "kerboscript", "library", file));
        }

        [Fact]
        public void Ascend_retracts_the_legs_above_100_m_when_it_started_on_the_ground()
        {
            Assert.Contains("if start = \"ground\" and gear and alt:radar > 100 and ship:verticalspeed > 0 { gear off. }", Library("ascend.ks"));
        }

        [Fact]
        public void Launch_retracts_the_legs_above_100_m_and_starts_only_on_the_ground()
        {
            var launch = Library("launch.ks");
            Assert.Contains("if gear and alt:radar > 100 and ship:verticalspeed > 0 { gear off. }", launch);
            Assert.Contains("if not claudia_on_ground() {", launch);
        }
    }
}
