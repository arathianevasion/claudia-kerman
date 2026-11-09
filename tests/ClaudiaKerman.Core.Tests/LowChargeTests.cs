// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #942: <c>ADDONS:CLAUDIA:LOWCHARGE</c>, the rule for what low charge does to <c>reentry</c> once the craft is falling into the
    /// air: it no longer ends the block, the steering goes under <c>min_ec_pct</c>, and the chutes are armed under 2%.
    /// </summary>
    public sealed class LowChargeTests
    {
        private const double AirTop = 50000;

        private static IDictionary<string, object> Ask(double pct, double floor, double pe)
        {
            return BlockCalls.LowCharge(new Dictionary<string, object> { { "ec_pct", pct }, { "min_ec_pct", floor }, { "pe", pe }, { "air_top", AirTop } });
        }

        [Theory]
        [InlineData(50000.1)] // just above the air top
        [InlineData(80000)]
        public void With_the_periapsis_above_the_air_top_nothing_changes_so_min_ec_pct_still_aborts(double pe)
        {
            var low = Ask(5, 10, pe);
            Assert.Equal("", low["error"]);
            Assert.Equal(false, low["carry_on"]);
            Assert.Equal(0, low["step"]);
        }

        [Fact]
        public void Nearly_empty_above_the_air_top_nothing_is_armed()
        {
            var low = Ask(1, 10, 80000);
            Assert.Equal(false, low["carry_on"]);
            Assert.Equal(0, low["step"]);
        }

        [Theory]
        [InlineData(10)] // at the floor is not under it
        [InlineData(55)]
        public void Over_the_floor_in_the_air_nothing_is_due(double pct)
        {
            var low = Ask(pct, 10, 15000);
            Assert.Equal(false, low["carry_on"]);
            Assert.Equal(0, low["step"]);
        }

        [Theory]
        [InlineData(50000)] // at the air top counts
        [InlineData(15000)]
        [InlineData(-200000)]
        public void Under_the_floor_with_the_periapsis_in_the_air_the_block_carries_on_and_lets_go_of_the_steering(double pe)
        {
            var low = Ask(9.9, 10, pe);
            Assert.Equal(true, low["carry_on"]);
            Assert.Equal(1, low["step"]);
        }

        [Theory]
        [InlineData(1.9, 10, true)]  // under 2%, under the floor
        [InlineData(1.9, 0, false)]  // under 2% with the floor turned off: the chutes are still armed, but there is no abort to stop
        public void Under_2_percent_in_the_air_the_chutes_are_armed(double pct, double floor, bool carryOn)
        {
            var low = Ask(pct, floor, 15000);
            Assert.Equal(2, low["step"]);
            Assert.Equal(carryOn, low["carry_on"]);
        }

        [Fact]
        public void At_2_percent_exactly_the_chutes_wait()
        {
            Assert.Equal(1, Ask(2, 10, 15000)["step"]);
        }

        [Fact]
        public void An_empty_ask_changes_nothing()
        {
            var low = BlockCalls.LowCharge(null);
            Assert.Equal("", low["error"]);
            Assert.Equal(false, low["carry_on"]);
            Assert.Equal(0, low["step"]);
        }

        /// <summary>ADR 0014 guardrail 6: every <c>reentry</c> tick asks; 1,000 ticks (20 s at 50 Hz) in 2 ms.</summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_ticks_take_under_2_ms()
        {
            var ask = new Dictionary<string, object> { { "ec_pct", 55.0 }, { "min_ec_pct", 10.0 }, { "pe", 15000.0 }, { "air_top", AirTop } };
            Timing.Within(2, () =>
            {
                for (var i = 0; i < 1000; i++) BlockCalls.LowCharge(ask);
            });
        }
    }
}
