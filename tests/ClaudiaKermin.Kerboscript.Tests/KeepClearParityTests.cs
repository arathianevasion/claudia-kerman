// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// #713 (ADR 0014): land's keep-clear decision, land_clear_of and land_clear_shift, against Core's <see cref="KeepClear.Shift"/>
    /// on the same inputs, before the Kerboscript is deleted (with this test) in the same ticket.
    /// </summary>
    public class KeepClearParityTests
    {
        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        [Theory]
        [InlineData(80, 300)]
        [InlineData(50, 300)]
        [InlineData(49.9, 300)]
        [InlineData(20, 300)]
        [InlineData(0, 300)]
        [InlineData(0, 10)]
        [InlineData(30, 0)]
        public void TheKerboscriptAndCoreMoveTheSiteTheSame(double dist, double maxDivert)
        {
            var kos = double.Parse(One(string.Format(CultureInfo.InvariantCulture, "round(land_clear_shift({0}, {1}), 6)", dist, maxDivert)), CultureInfo.InvariantCulture);
            Assert.Equal(kos, System.Math.Round(KeepClear.Shift(dist, maxDivert), 6));
            Assert.Equal(One(string.Format(CultureInfo.InvariantCulture, "land_clear_of({0})", dist)) == "True", KeepClear.Shift(dist, 1e9) == 0);
        }
    }
}
