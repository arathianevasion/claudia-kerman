// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#968: <c>reentry</c> reports only after the craft has stayed on the ground for about 2 s, so a bounce doesn't read as FLYING. The rule is walked tick by tick, as the block's wait does.</summary>
    public class GroundSettleTests
    {
        /// <summary>Walks the rule over <paramref name="onGround"/> at <paramref name="step"/> s a tick from UT 100, first touch at 100; the UT it says done, or -1.</summary>
        private static double DoneAt(bool[] onGround, double step = 0.5)
        {
            double since = -1;
            for (var i = 0; i < onGround.Length; i++)
            {
                var now = 100 + i * step;
                var answer = GroundSettle.Call(new Dictionary<string, object> { { "on_ground", onGround[i] }, { "now", now }, { "since", since }, { "first", 100.0 } });
                since = (double)answer["since"];
                if ((bool)answer["done"]) return now;
            }
            return -1;
        }

        private static bool[] Run(bool value, int ticks)
        {
            var run = new bool[ticks];
            for (var i = 0; i < ticks; i++) run[i] = value;
            return run;
        }

        private static bool[] Join(params bool[][] parts)
        {
            var all = new List<bool>();
            foreach (var part in parts) all.AddRange(part);
            return all.ToArray();
        }

        [Fact]
        public void A_clean_landing_is_done_after_two_seconds_on_the_ground()
        {
            Assert.Equal(102, DoneAt(Run(true, 20)));
        }

        [Fact]
        public void Not_done_before_two_seconds()
        {
            Assert.Equal(-1, DoneAt(Run(true, 4)));
        }

        [Fact]
        public void A_bounce_starts_the_two_seconds_again()
        {
            // Down to 101, up at 101.5 and 102, down again at 102.5: done 2 s after that, not 2 s after first touch.
            var walk = Join(Run(true, 3), Run(false, 2), Run(true, 20));
            Assert.Equal(104.5, DoneAt(walk));
        }

        [Fact]
        public void A_craft_that_bounces_for_ever_is_done_at_the_cap()
        {
            var walk = new bool[100];
            for (var i = 0; i < walk.Length; i++) walk[i] = i % 3 != 0;
            Assert.Equal(130, DoneAt(walk, 1.0));
        }

        [Fact]
        public void The_cap_ends_it_even_in_the_air()
        {
            Assert.Equal(130, DoneAt(Run(false, 40), 1.0));
        }

        [Fact]
        public void A_missing_first_is_taken_as_now_and_echoed_so_the_cap_still_fires()
        {
            var state = GroundSettle.Call(new Dictionary<string, object> { { "on_ground", false }, { "now", 100.0 } });
            Assert.Equal(100.0, state["first"]);
            Assert.Equal(false, state["done"]);
            var later = GroundSettle.Call(new Dictionary<string, object> { { "on_ground", false }, { "now", 130.0 }, { "since", state["since"] }, { "first", state["first"] } });
            Assert.Equal(true, later["done"]);
        }
    }
}
