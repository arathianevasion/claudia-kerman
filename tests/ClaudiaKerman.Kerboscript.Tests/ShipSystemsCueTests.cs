// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #756: the blocks' Ship Systems wiring. The rule is Core's (ShipSystemsTests); a block only says when, so this pins each cue's
    /// place: after what can still refuse the block, and for lift-off before throttle-up. The harness has no ADDONS to fly them.
    /// </summary>
    public sealed class ShipSystemsCueTests
    {
        private static string Source(string file)
        {
            return CoreLibrary.Files.Single(f => f.Name == file).Source;
        }

        private static void Before(string source, string first, string second)
        {
            var at = source.IndexOf(first, System.StringComparison.Ordinal);
            Assert.True(at >= 0, "missing: " + first);
            Assert.True(at < source.IndexOf(second, at, System.StringComparison.Ordinal), first + " must come before " + second);
        }

        [Theory]
        [InlineData("launch", "lock throttle to tset.")]
        [InlineData("ascend", "lock throttle to tset.")]
        public void The_lift_off_blocks_stow_before_throttle_up(string block, string throttle)
        {
            Before(Source(block), "claudia_stow_ship_systems(ctx)", throttle);
        }

        [Fact]
        public void An_already_flown_ascend_or_one_refused_for_thrust_stows_nothing()
        {
            var ascend = Source("ascend");
            Before(ascend, "if start = \"orbit\" {", "claudia_stow_ship_systems(ctx)");
            Before(ascend, "is too low to lift off", "claudia_stow_ship_systems(ctx)");
        }

        [Fact]
        public void The_stow_wait_is_the_cue_then_the_mods_answer_and_the_cap_goes_in_the_trace()
        {
            var lib = Source("claudia_lib");
            Before(lib, "addons:claudia:shipsystems(\"liftoff\")", "until stow:done {");
            Before(lib, "until stow:done {", "addons:claudia:shipsystems(\"stowing\")");
            Before(lib, "if stow:capped {", "\"note\", stow:note");
        }

        [Fact]
        public void Land_cues_its_terminal_descent()
        {
            Assert.Contains("if phase = \"terminal\" and last_phase <> phase { addons:claudia:shipsystems(\"final_descent\"). }", Source("land"));
        }

        [Fact]
        public void Deorbit_and_reentry_cue_only_once_nothing_refuses_them()
        {
            var deorbit = Source("deorbit");
            Before(deorbit, "return claudia_finish(ctx, false, found:error, extra).", "addons:claudia:shipsystems(\"entry\")");
            Before(deorbit, "addons:claudia:shipsystems(\"entry\")", "local flown is");
            var reentry = Source("reentry");
            Before(reentry, "needs a lit engine (reentry never stages)", "addons:claudia:shipsystems(\"entry\")");
            Before(reentry, "addons:claudia:shipsystems(\"entry\")", "lock steering to srfretrograde.");
        }
    }
}
