// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>ADR 0014 guardrail 4, #954: reentry_brake_due, reentry_stop_distance and reentry_brake_height against Core's on the same inputs. Deleted with the Kerboscript.</summary>
    public class ReentryBrakeParityTests
    {
        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData(1000, -1, 300)]
        [InlineData(40000, -1, 1200)]
        [InlineData(40000, -1, 390)]
        [InlineData(2500, -1, 1200)]
        [InlineData(3000, -1, 401)]
        [InlineData(3001, -1, 401)]
        [InlineData(900, 1000, 1200)]
        [InlineData(1000, 1000, 1200)]
        [InlineData(1100, 1000, 300)]
        public void Brake_due_matches(double radar, double brakeH, double speed)
        {
            var kos = Run("print reentry_brake_due(" + Num(radar) + ", " + Num(brakeH) + ", " + Num(speed) + ").").Single();
            Assert.Equal(kos == "True", ReentryBrake.DueByRadar(radar, brakeH, speed, 400, 3000));
        }

        public static IEnumerable<object[]> Falls()
        {
            foreach (var v in new[] { 1.0, 12, 60, 150, 300 })
                foreach (var a in new[] { 3.4, 5, 12, 24 })
                    foreach (var k in new[] { 0, 2e-4, 5e-3 })
                        foreach (var radar in new[] { 30.0, 300, 2000, 9000 })
                            yield return new object[] { v, a, k, radar };
        }

        [Theory]
        [MemberData(nameof(Falls))]
        public void Stop_distance_and_brake_height_match(double vDown, double aUp, double k, double radar)
        {
            const double G = 2.94;
            var ks = Enumerable.Repeat(k, 20).ToArray();
            var kosKs = "list(" + string.Join(",", ks.Select(Num)) + ")";
            var kos = Run("print reentry_stop_distance(" + Num(vDown) + ", " + Num(aUp - G) + ", " + kosKs + ", " + Num(radar / 20) + ", 2).\n"
                + "print reentry_brake_height(" + Num(vDown) + ", " + Num(aUp) + ", " + Num(G) + ", " + kosKs + ", " + Num(radar) + ").");
            Assert.Equal(double.Parse(kos[0], CultureInfo.InvariantCulture), ReentryBrake.StopDistance(vDown, aUp - G, ks, radar / 20, 2), 6);
            Assert.Equal(double.Parse(kos[1], CultureInfo.InvariantCulture), ReentryBrake.BrakeHeight(vDown, aUp, G, ks, radar, 2, 10), 6);
        }
    }
}
