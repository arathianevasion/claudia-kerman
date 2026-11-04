// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// #475's parity test: the KSLib LAZcalc that launch flew until #475 (the patched lib_lazcalc, its arithmetic
    /// LAZcalc_azimuth, fed the list LAZcalc_init builds from launch_laz_inc's inclination) against Core's heading
    /// (<c>ADDONS:CLAUDIA:LAUNCHHEADING</c>), at KSC (latitude -0.0972) for inclinations 0, 6, 45 and 90 to an 80 km orbit.
    /// Deleted with the Kerboscript heading, as ADR 0014 guardrail 4 asks; its figures are in the #475 commit message.
    /// </summary>
    public class LaunchHeadingParityTests
    {
        private const double KscLatitude = -0.0972;
        private const double Gm = 3.5316e12;
        private const double Radius = 600000;
        private const double Period = 21549.425;
        private const double TargetAp = 80000;

        private readonly ITestOutputHelper output;

        public LaunchHeadingParityTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(6)]
        [InlineData(45)]
        [InlineData(90)]
        public void LAZcalc_and_the_Core_heading_agree_at_KSC(double inc)
        {
            var equatorialVel = (2 * Math.PI * Radius) / Period;
            var orbitVel = Math.Sqrt(Gm / (Radius + TargetAp));
            var lat = KscLatitude.ToString("R", CultureInfo.InvariantCulture);
            // Launch's own clamp, then LAZcalc_init's list: |inc|, latitude, equatorial speed, orbital speed, node, epsilon.
            var printed = KosHarness.Run(CoreLibrary.Bundle(
                "local c is launch_laz_inc(" + inc + ", " + lat + ").\n"
                + "print round(LAZcalc_azimuth(list(abs(c), " + lat + ", " + equatorialVel.ToString("R", CultureInfo.InvariantCulture)
                + ", " + orbitVel.ToString("R", CultureInfo.InvariantCulture) + ", \"Ascending\", 10), " + lat + ", 0), 4).")).Single();
            var laz = double.Parse(printed, CultureInfo.InvariantCulture);

            var core = (double)BlockCalls.LaunchHeading(new Dictionary<string, object>
            {
                { "inc", (double)inc }, { "lat", KscLatitude }, { "target_ap", TargetAp }, { "gm", Gm }, { "radius", Radius }, { "rotation", Period },
            })["heading"];

            output.WriteLine("parity inc " + inc + ": LAZcalc " + laz.ToString("0.0000", CultureInfo.InvariantCulture) + ", HeadingInto "
                + core.ToString("0.0000", CultureInfo.InvariantCulture) + ", difference " + (laz - core).ToString("0.0000", CultureInfo.InvariantCulture));
            Assert.InRange(laz - core, -1.0, 1.0);
        }
    }
}
