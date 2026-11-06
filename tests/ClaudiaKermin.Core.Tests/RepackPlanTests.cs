// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    public class RepackPlanTests
    {
        [Theory]
        [InlineData("Bill the Engineer repacks the chutes on Duna.")]
        [InlineData("Land on Duna and come home; an engineer repacks the chutes after the landing.")]
        [InlineData("No landing legs this time; an engineer will repack the parachutes.")]
        public void APlainPlanCounts(string goal)
        {
            Assert.True(RepackPlan.IsPlanned(goal));
        }

        [Theory]
        [InlineData("No engineer will repack the chutes.")]
        [InlineData("Land on Duna without a repack of the chutes; the engineer stays home.")]
        [InlineData("The engineer can't repack the chutes.")]
        [InlineData("The engineer cannot repack the chutes.")]
        [InlineData("The engineer won't repack the chutes.")]
        [InlineData("The engineer will not repack the chutes.")]
        [InlineData("The engineer never repacks the chutes.")]
        [InlineData("Fly without an engineer to repack the chutes.")]
        public void ANegatedPhraseIsNotAPlan(string goal)
        {
            Assert.False(RepackPlan.IsPlanned(goal));
        }
    }
}
