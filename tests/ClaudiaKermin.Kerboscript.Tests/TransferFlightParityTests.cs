// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// Parity (ADR 0014 guardrail 4, #459): transfer_to's pure helpers in Kerboscript against <see cref="TransferFlight"/>'s C# on
    /// the same inputs: the miss after the corrections, the correction after a missed ejection, the Course Correction's time and
    /// the earliest departure. Deleted with the Kerboscript it checks.
    /// </summary>
    public class TransferFlightParityTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string N(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        public static IEnumerable<object[]> Misses()
        {
            foreach (var pe in new[] { 38117.0, 29500, 30400, 30000, 12345.6, 1e6 })
                foreach (var tolerance in new[] { 1000.0, 10000, 250 })
                    foreach (var why in new[] { "", "residual 0.3 m/s is over max_residual 0.1 m/s" })
                        yield return new object[] { pe, tolerance, why };
        }

        [Theory]
        [MemberData(nameof(Misses))]
        public void The_miss_is_the_same(double pe, double tolerance, string why)
        {
            var kos = Run("print transfer_to_miss(\"Minmus\", " + N(pe) + ", 30000, " + N(tolerance) + ", \"" + why + "\").").Single();
            Assert.Equal(kos, TransferFlight.Miss("Minmus", pe, 30000, tolerance, why));
        }

        [Theory]
        [InlineData(30000, true, 412.5)]
        [InlineData(20000, false, 412.5)]
        [InlineData(5300, false, 412.5)]
        [InlineData(4400, false, -1)]
        [InlineData(30000, true, -1)]
        [InlineData(30000, true, 0)]
        public void The_correction_after_a_missed_ejection_asks_the_same(double maxDuration, bool auto, double dvLeft)
        {
            var kos = Run("local q is transfer_to_correction_ask(\"Mun\", 30000, 5000, 133, 1000, " + N(maxDuration) + ", " + (auto ? "true" : "false") + ", " + N(dvLeft)
                + "). print q:target. print q:target_pe. print q:earliest_ut. print q:plane_change. print q:haskey(\"latest_ut\"). if q:haskey(\"latest_ut\") { print q:latest_ut. } else { print -1. }"
                + " print q:haskey(\"max_dv\"). if q:haskey(\"max_dv\") { print q:max_dv. } else { print -1. }");
            var cs = TransferFlight.CorrectionAsk("Mun", 30000, 5000, 133, 1000, maxDuration, auto, dvLeft);
            Assert.Equal(new[]
            {
                cs.Target, N(cs.TargetPe), N(cs.EarliestUt), cs.BrokenPlane ? "True" : "False",
                cs.LatestUt.HasValue ? "True" : "False", N(cs.LatestUt ?? -1), cs.MaxDv.HasValue ? "True" : "False", N(cs.MaxDv ?? -1),
            }, kos);
        }

        [Theory]
        [InlineData(16500, null)]
        [InlineData(16500, 20000.0)]
        [InlineData(16500, 10000.0)]
        [InlineData(16500, 2313.0)]
        [InlineData(16500, 2300.0)]
        [InlineData(9000, 8000.0)]
        [InlineData(3100, null)]
        public void The_correction_time_is_the_same(double mid, double? maxDuration)
        {
            var kos = Run("local w is claudia_correction_ut(" + N(mid) + ", 3000, 193, 1000, " + (maxDuration.HasValue ? N(maxDuration.Value) : "\"auto\"") + "). print w:ut. print w:reason.");
            string reason;
            var ut = TransferFlight.CorrectionUt(mid, 3000, 193, 1000, maxDuration, out reason);
            Assert.Equal(new[] { N(ut), reason }, kos);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(2000.0)]
        [InlineData(1100.0)]
        public void The_earliest_departure_is_the_same(double? window)
        {
            var kos = Run("print claudia_earliest(1000, 133, " + (window.HasValue ? N(window.Value) : "\"next\"") + ").").Single();
            Assert.Equal(kos, N(TransferFlight.Earliest(1000, 133, window)));
        }
    }
}
