// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class PartLossRuleTests
    {
        [Fact]
        public void A_part_still_on_the_craft_is_not_lost()
        {
            Assert.False(PartLossRule.BrokeOff(true, false));
        }

        [Fact]
        public void A_part_that_left_with_no_staging_broke_off()
        {
            Assert.True(PartLossRule.BrokeOff(false, false));
        }

        [Fact]
        public void A_part_that_left_at_a_staging_or_decoupler_firing_is_not_lost()
        {
            Assert.False(PartLossRule.BrokeOff(false, true));
        }

        [Fact]
        public void A_part_still_aboard_at_a_staging_is_not_lost()
        {
            Assert.False(PartLossRule.BrokeOff(true, true));
        }

        [Theory]
        [InlineData(10.0, 10.0, true)]
        [InlineData(9.0, 10.0, true)]
        [InlineData(11.0, 10.0, true)]
        [InlineData(7.9, 10.0, false)]
        [InlineData(0.0, 10.0, false)]
        public void A_firing_explains_a_break_only_when_it_is_just_before_or_after(double explainedAt, double brokeAt, bool expected)
        {
            Assert.Equal(expected, PartLossRule.Explains(explainedAt, brokeAt));
        }
    }
}
