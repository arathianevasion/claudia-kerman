// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// ADR 0014 guardrail 4 (#712): land.ks's land_path_guard against its port to Core, <see cref="LandGuard"/>, on
    /// LandAscendTests' ridge-then-basin inputs and the m20261104 abort. The reason's wording changes on purpose ("climbed off"
    /// becomes "is above"); whether it trips and the km over must match. Deleted with the Kerboscript in the same ticket.
    /// </summary>
    public class LandGuardParityTests
    {
        private static string F(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        [Fact]
        public void The_guard_trips_where_land_path_guard_does_by_the_same_km()
        {
            // alt now, predicted alt, predicted ground.
            var cases = new[]
            {
                new[] { 3500.0, 3500, 2000 }, new[] { 1600.0, 1500, -1500 }, new[] { 3400.0, 2500, 1000 }, new[] { 2300.0, 800, 0 },
                new[] { 3108.0, 2081, 0 }, new[] { 3000.0, 2000, 0 }, new[] { 3001.0, 2000, 0 }, new[] { 9000.0, 6000, 0 }, new[] { 9001.0, 6000, -2000 },
                new[] { 0.0, 1500, 0 }, new[] { 5000.0, 200, 400 },
            };
            var script = string.Join("\n", cases.Select(c => "print land_path_guard(" + F(c[0]) + ", lexicon(\"alt\", " + F(c[1]) + ", \"ground\", " + F(c[2]) + "))."))
                + "\nprint land_path_guard(99999, \"\").";
            var kos = KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
            for (var i = 0; i < cases.Length; i++)
            {
                var c = cases[i];
                // Far from the site, so the guard that trips aborts rather than coming straight down.
                var tick = LandGuard.Tick(new Dictionary<string, object>
                {
                    { "alt", c[0] }, { "path_alt", c[1] }, { "path_ground", c[2] }, { "s", 50000.0 }, { "a_max", 10.0 }, { "g", 1.6 }, { "dv_left", 5000.0 },
                });
                if (kos[i] == "")
                {
                    Assert.Equal("fly", tick["verdict"]);
                    continue;
                }
                Assert.Equal("abort", tick["verdict"]);
                Assert.Equal(kos[i].Replace("braking climbed off its predicted path", "braking is above its predicted path"), tick["error"]);
            }
            Assert.Equal("", kos[cases.Length]);
            Assert.Equal("fly", LandGuard.Tick(new Dictionary<string, object> { { "alt", 99999.0 }, { "s", 50000.0 } })["verdict"]);
        }
    }
}
