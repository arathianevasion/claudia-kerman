// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>#715: circularize and tune_pe skip a burn not worth its turn when Claudia gave no tolerance (<see cref="BurnWorth"/>).</summary>
    public sealed class BurnWorthTests
    {
        private static IDictionary<string, object> Circularize(bool given, double dv, double ecc, double pe, string body = "Mun")
        {
            return BurnWorth.Call(new Dictionary<string, object>
            {
                { "block", "circularize" }, { "given", given }, { "dv", dv }, { "ecc", ecc }, { "pe", pe },
                { "body", body }, { "has_atm", false }, { "atm_height", 0.0 },
            });
        }

        private static IDictionary<string, object> TunePe(bool given, double value, double pe, string patch = "current", string body = "Mun", bool hasAtm = false, double atm = 0)
        {
            return BurnWorth.Call(new Dictionary<string, object>
            {
                { "block", "tune_pe" }, { "given", given }, { "value", value }, { "pe", pe }, { "patch", patch },
                { "body", body }, { "has_atm", hasAtm }, { "atm_height", atm },
            });
        }

        [Fact]
        public void CircularizeSkipsAThreeMetrePerSecondBurnAtTheMunOnASafePeriapsis()
        {
            var answer = Circularize(false, 3.1, 0.012, 30000);
            Assert.True((bool)answer["skip"]);
            Assert.Equal("within its default tolerance: a 3.1 m/s burn isn't worth the turn", answer["note"]);
        }

        [Theory]
        [InlineData(3.0, 0.04, 30000)]   // eccentricity over 0.03
        [InlineData(3.0, 0.03, 30000)]   // at 0.03
        [InlineData(5.0, 0.012, 30000)]  // burn at 5 m/s
        [InlineData(3.0, 0.012, 9000)]   // under the Mun's 7,061 m peak plus 2 km margin
        [InlineData(3.0, 0.012, 9061)]   // at the margin is not above it
        public void CircularizeBurnsWhenTheBurnIsBigTheOrbitIsEccentricOrThePeriapsisIsUnsafe(double dv, double ecc, double pe)
        {
            Assert.False((bool)Circularize(false, dv, ecc, pe)["skip"]);
        }

        [Fact]
        public void CircularizeNeverSkipsOnABodyWhosePeaksAreUnknown()
        {
            Assert.False((bool)Circularize(false, 1, 0.01, 500000, "Wal")["skip"]);
        }

        [Fact]
        public void CircularizeWithAnExplicitToleranceNeverSkipsHere()
        {
            Assert.False((bool)Circularize(true, 3.1, 0.012, 30000)["skip"]);
        }

        [Fact]
        public void TunePeDefaultToleranceIsTheLargerOfOneKilometreAndFivePercentOfValue()
        {
            Assert.Equal(1000, BurnWorth.TunePeTolerance(15000));
            Assert.Equal(2500, BurnWorth.TunePeTolerance(50000));
            Assert.Equal(2500, (double)TunePe(false, 50000, 90000)["tolerance"]);
            Assert.Equal(1000, (double)TunePe(false, 15000, 90000)["tolerance"]);
        }

        [Theory]
        [InlineData(50000, 52400)]
        [InlineData(50000, 47500)]
        [InlineData(15000, 15900)]
        public void TunePeSkipsAPeriapsisWithinTheDefaultTolerance(double value, double pe)
        {
            var answer = TunePe(false, value, pe);
            Assert.True((bool)answer["skip"]);
            Assert.StartsWith("within its default tolerance: the periapsis is ", (string)answer["note"]);
        }

        [Fact]
        public void TunePeBurnsOutsideTheDefaultToleranceAndWithAnExplicitOne()
        {
            Assert.False((bool)TunePe(false, 50000, 52600)["skip"]);
            Assert.False((bool)TunePe(true, 50000, 50100)["skip"]);
            Assert.Equal(0, (double)TunePe(true, 50000, 50100)["tolerance"]);
        }

        [Fact]
        public void TunePeNeverSkipsOnPatchNextAndKeepsOneKilometre()
        {
            var answer = TunePe(false, 50000, 50400, "next");
            Assert.False((bool)answer["skip"]);
            Assert.Equal(1000, (double)answer["tolerance"]);
        }

        [Fact]
        public void TunePeNeverSkipsWithAnUnsafePeriapsisAndKeepsOneKilometre()
        {
            // Inside Kerbin's 70 km atmosphere, and under the Mun's peaks plus margin.
            foreach (var answer in new[] { TunePe(false, 60000, 61000, "current", "Kerbin", true, 70000), TunePe(false, 8000, 8500) })
            {
                Assert.False((bool)answer["skip"]);
                Assert.Equal(1000, (double)answer["tolerance"]);
            }
        }

        // The safe height and hint were Kerboscript's tune_pe_safe_height and tune_pe_safe_hint (#610) until #715; their cases.
        [Fact]
        public void TheSafeHeightIsTheAtmosphereOrTheHighestPeakPlusAMargin()
        {
            Assert.Equal(70000, BurnWorth.SafeHeight("Kerbin", true, 70000));
            Assert.Equal(9061, BurnWorth.SafeHeight("Mun", false, 0));
            Assert.Equal(-1, BurnWorth.SafeHeight("Wal", false, 0));
        }

        [Fact]
        public void APeriapsisOverToleranceOnASafeEncounterSaysACaptureCanFollow()
        {
            // 20ac: 35.3 km at the Mun against 30 +- 5 km, well clear of its 7 km peaks.
            Assert.Equal("; encounter safe: pe 35.3 km at Mun; a capture can follow", BurnWorth.SafeHint(true, 35300, "Mun", BurnWorth.SafeHeight("Mun", false, 0)));
        }

        [Theory]
        [InlineData(true, 5000, "Mun", false, 0.0)]
        [InlineData(true, 40000, "Laythe", true, 50000.0)]
        [InlineData(true, 35300, "Wal", false, 0.0)]
        [InlineData(false, 35300, "Kerbin", true, 70000.0)]
        public void AnUnsafeOrUnknownPeriapsisOrNoEncounterHasNoHint(bool toMoon, double pe, string body, bool hasAtm, double atm)
        {
            Assert.Equal("", BurnWorth.SafeHint(toMoon, pe, body, BurnWorth.SafeHeight(body, hasAtm, atm)));
            var answer = BurnWorth.Call(new Dictionary<string, object>
            {
                { "block", "hint" }, { "to_moon", toMoon }, { "pe", pe }, { "body", body }, { "has_atm", hasAtm }, { "atm_height", atm },
            });
            Assert.Equal("", answer["note"]);
        }
    }
}
