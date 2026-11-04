// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// #721: every site that restarts the tick clock goes through claudia_restart_clock, which sets ctx:last_ut to now and
    /// ctx:last_tset to the throttle in effect, so the first interval after a restart is credited at that throttle (the
    /// accounting claudia_tick does with claudia_dv_step). The game's time and ship are faked as globals the library reads.
    /// </summary>
    public class RestartClockTests
    {
        private const string Fakes =
            "global time is lexicon(\"seconds\", 10).\n"
            + "global ship is lexicon(\"control\", lexicon(\"mainthrottle\", 1), \"availablethrust\", 20, \"mass\", 10).\n"
            + "local ctx is lexicon(\"last_ut\", 10, \"last_tset\", 1, \"dv_used\", 0).\n";

        [Fact]
        public void A_restart_mid_burn_credits_the_next_interval_at_the_throttle_in_effect()
        {
            // The block commanded 1; a sub-block left the engines at 0.5 when it returned at 10.02 s. The next tick, at
            // 10.04 s, credits 0.02 s at 0.5: 0.5 * 20 kN / 10 t * 0.02 s = 0.02 m/s (20 mm/s). Crediting it at the
            // commanded 1 would give 0.04 m/s (40 mm/s), the bug.
            var script = Fakes
                + "set time:seconds to 10.02.\n"
                + "set ship:control:mainthrottle to 0.5.\n"
                + "claudia_restart_clock(ctx).\n"
                + "print round(ctx:last_tset * 1000).\n"
                + "print round(ctx:last_ut * 1000).\n"
                + "set time:seconds to 10.04.\n"
                + "print round(claudia_dv_step(ctx:last_tset, ship:availablethrust, ship:mass, time:seconds - ctx:last_ut) * 1000).\n";
            Assert.Equal(new[] { "500", "10020", "20" }, KosHarness.Run(CoreLibrary.Bundle(script)).ToArray());
        }

        [Fact]
        public void No_site_sets_last_ut_to_the_clock_outside_the_helper()
        {
            // Every restart goes through claudia_restart_clock, so the one direct reset left is in the helper itself.
            // claudia_tick's max(now, ctx:last_ut) is not a restart and is not matched.
            var direct = CoreLibrary.Files
                .Where(f => f.Source.Contains("set ctx:last_ut to time:seconds."))
                .Select(f => f.Name)
                .ToArray();
            Assert.Equal(new[] { "claudia_lib" }, direct);
            Assert.Equal(1, CoreLibrary.Files.Single(f => f.Name == "claudia_lib").Source.Split("set ctx:last_ut to time:seconds.").Length - 1);
        }
    }
}
