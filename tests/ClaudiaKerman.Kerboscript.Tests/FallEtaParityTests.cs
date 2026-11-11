// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>#994 parity (ADR 0014 guardrail 4): claudia_fall_eta against Core's FallEta on closed orbits. Deleted with the Kerboscript.</summary>
    public class FallEtaParityTests
    {
        private readonly ITestOutputHelper _out;

        public FallEtaParityTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string D(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Closed_orbits_give_the_same_answers()
        {
            var cases = new System.Collections.Generic.List<double[]>();
            const double mu = 3.5316e12, r = 600000;
            foreach (var pe in new[] { 30000.0, 69000, 100000 })
                foreach (var ap in new[] { 200000.0, 1000000, 12000000 })
                    for (var ta = 0.0; ta < 360; ta += 15)
                    {
                        var rPe = r + pe; var rAp = r + ap;
                        cases.Add(new[] { (rAp - rPe) / (rAp + rPe), (rPe + rAp) / 2, mu, r + 70000, ta });
                    }
            cases.Add(new[] { 0.06235565819861432, 216500, 6.5138398e10, 201000, 180 });
            cases.Add(new[] { 0.06235565819861432, 216500, 6.5138398e10, 210000, 180 });
            cases.Add(new[] { 0.0, 700000, mu, 670000, 90 });
            var script = new StringBuilder();
            foreach (var c in cases) script.Append("print claudia_fall_eta(" + string.Join(", ", c.Select(D)) + ").\n");
            var printed = KosHarness.Run(CoreLibrary.Bundle(script.ToString()));
            Assert.Equal(cases.Count, printed.Count);
            var worst = 0.0;
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var ks = double.Parse(printed[i], CultureInfo.InvariantCulture);
                var cs = BlockCalls.FallEta(c[0], c[1], c[2], c[3], c[4]);
                _out.WriteLine("{0} -> ks {1} cs {2}", string.Join(", ", c.Select(D)), D(ks), D(cs));
                Assert.Equal(ks < 0, cs < 0);
                worst = Math.Max(worst, Math.Abs(ks - cs));
                Assert.True(Math.Abs(ks - cs) <= 1e-6 * Math.Max(1, Math.Abs(ks)), "case " + i + ": ks " + ks + " cs " + cs);
            }
            _out.WriteLine("worst {0}", worst);
        }
    }
}
