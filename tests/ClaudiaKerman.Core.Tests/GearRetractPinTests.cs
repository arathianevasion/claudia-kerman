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
            Assert.Contains("if start = \"ground\" and gear and alt:radar > 100 and ship:verticalspeed > 0 {\n      gear off.", Library("ascend.ks").Replace("\r\n", "\n"));
        }

        [Fact]
        public void Land_compares_biome_names_ignoring_spaces_on_both_sides()
        {
            Assert.Contains("extra:biome:replace(\" \", \"\") <> aim_name:replace(\" \", \"\")", Library("land.ks"));
        }

        [Fact]
        public void Reentry_lowers_the_gear_under_1000_m_in_fall_tick_and_still_in_the_braking_branch()
        {
            var reentry = Library("reentry.ks").Replace("\r\n", "\n");
            Assert.Contains("chute_tick(last_speed, agl, pressure).\n    if agl < 1000 and not gear { gear on. }\n    if powered and reentry_brake_due", reentry);
            Assert.Contains("lock throttle to tset.\n    gear on.\n", reentry);
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
