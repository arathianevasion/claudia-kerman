// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
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
        [InlineData("Fly home, in on the target")]
        [InlineData("come home-base")]
        [InlineData("come home-stretch")]
        [InlineData("Dock at the home stretch")]
        [InlineData("Land on the Mun")]
        public void AWordThatOnlyLooksLikeAReturnDoesNotMatch(string words)
        {
            Assert.DoesNotMatch(SequenceWords.ReturnHome, words);
        }

        private static GameSnapshot InKerbinOrbit()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
        }

        [Theory]
        [InlineData("Come home from the Mun and land")]
        [InlineData("Return home from the Mun and land")]
        [InlineData("Fly back home from Minmus")]
        public void AReturnHomeLegIsAtKerbinWhateverBodyItNames(string words)
        {
            var snapshot = InKerbinOrbit();

            Assert.Equal("Kerbin", SequenceWords.LegBody(snapshot, words, SequenceWords.Body(snapshot, "Minmus")).Name);
        }

        [Fact]
        public void ALegThatNamesABodyIsAtThatBodyAndOneThatNamesNoneKeepsThePreviousOne()
        {
            var snapshot = InKerbinOrbit();
            var mun = SequenceWords.Body(snapshot, "Mun");
            var minmus = SequenceWords.Body(snapshot, "Minmus");

            Assert.Equal("Mun", SequenceWords.LegBody(snapshot, "Land on the Mun", minmus).Name);
            Assert.Equal("Minmus", SequenceWords.LegBody(snapshot, "Ascend and circularize", minmus).Name);
            Assert.Same(mun, SequenceWords.LegBody(snapshot, "Ascend and circularize", mun));
        }
    }
}
