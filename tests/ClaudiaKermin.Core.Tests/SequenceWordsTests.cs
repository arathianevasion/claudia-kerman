// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The return to the home planet (#689): the words that read as a return match, and the look-alikes don't.</summary>
    public class SequenceWordsTests
    {
        [Theory]
        [InlineData("Land on the Mun and come home")]
        [InlineData("Orbit Minmus then head home")]
        [InlineData("go home")]
        [InlineData("Back home after the landing")]
        [InlineData("Fly home and splash down")]
        [InlineData("Return home")]
        [InlineData("Return to Kerbin")]
        [InlineData("Coming HOME after the Mun")]
        [InlineData("heads home")]
        public void AReturnToTheHomePlanetMatches(string words)
        {
            Assert.Matches(SequenceWords.ReturnHome, words);
        }

        [Theory]
        [InlineData("homestead")]
        [InlineData("Set up a homestead on the Mun")]
        [InlineData("home in on the target")]
        [InlineData("Fly home in on the target")]
        [InlineData("Dock at the home stretch")]
        [InlineData("Land on the Mun")]
        public void AWordThatOnlyLooksLikeAReturnDoesNotMatch(string words)
        {
            Assert.DoesNotMatch(SequenceWords.ReturnHome, words);
        }
    }
}
