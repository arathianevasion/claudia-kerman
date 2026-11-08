// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#872: the ejection node a watched wait of <c>transfer_to</c> carries, as one string with its format and parsing in one place.</summary>
    public sealed class EjectionTokenTests
    {
        [Fact]
        public void ATokenRoundTripsExactly()
        {
            var token = new EjectionToken { Ut = 169104601.123456, Prograde = 1234.5678901234, Normal = -0.1, Radial = 1e-7, Phase = "ejected" };
            var back = EjectionToken.Parse(token.ToString());
            Assert.NotNull(back);
            Assert.Equal(token.Ut, back.Ut);
            Assert.Equal(token.Prograde, back.Prograde);
            Assert.Equal(token.Normal, back.Normal);
            Assert.Equal(token.Radial, back.Radial);
            Assert.Equal("ejected", back.Phase);
            Assert.Equal(token.ToString(), back.ToString());
        }

        [Fact]
        public void TheStringIsTheFiveFieldFormatTheBlockPassesThrough()
        {
            Assert.Equal("100;1.5;0;-2.25;escaping", new EjectionToken { Ut = 100, Prograde = 1.5, Normal = 0, Radial = -2.25, Phase = "escaping" }.ToString());
        }

        [Fact]
        public void ATokenIsTakenFromAFlownStep()
        {
            var token = EjectionToken.Of(new TransferFlightStep { Ut = 50, Prograde = 3, Normal = 2, Radial = 1, Phase = "ejected" });
            Assert.Equal("50;3;2;1;ejected", token.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("1;2;3;4")]
        [InlineData("1;2;3;4;ejected;5")]
        [InlineData("x;2;3;4;ejected")]
        [InlineData("1;2;;4;ejected")]
        public void AnythingElseDoesNotParse(string text)
        {
            Assert.Null(EjectionToken.Parse(text));
        }
    }
}
