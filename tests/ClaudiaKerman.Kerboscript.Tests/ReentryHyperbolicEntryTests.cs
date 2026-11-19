// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.KOS;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #994: <c>reentry</c> started above the air on a path home from another planet. The block's own lines, from the time to the air to its refusal, are lifted out of the real library and
    /// run on kOS.Safe with the game faked, FALLETA answering with Core's real rule (<c>test_fall_eta</c>). Core's rule is in <c>FallEtaTests</c>.
    /// </summary>
    public class ReentryHyperbolicEntryTests
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000;
        /// <summary>Flight m20261111-172716-a40a's Duna return at Kerbin: ESCAPING, sma -9,670,147 m.</summary>
        private const double Sma = -9670147;

        public ReentryHyperbolicEntryTests()
        {
            KosHarness.Register(typeof(ReentryHyperbolicRigFunctions));
        }

        private static string D(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>The block's lines from <c>local entry_in is 0.</c> to the close of the refusal's if.</summary>
        private static string Slice()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source.Replace("\r\n", "\n");
            var body = source.Substring(source.IndexOf("function reentry {", StringComparison.Ordinal));
            var start = body.IndexOf("  local entry_in is 0.\n", StringComparison.Ordinal);
            Assert.True(start > 0);
            var refusal = body.IndexOf("the orbit never reaches the atmosphere", start, StringComparison.Ordinal);
            var end = body.IndexOf("\n  }\n", refusal, StringComparison.Ordinal) + 5;
            return body.Substring(start, end - start);
        }

        /// <summary>Runs the slice with the craft at <paramref name="altitude"/> m on a hyperbola of periapsis <paramref name="pe"/> m, inbound or out; prints the time to the air and the outcome.</summary>
        private static string[] Start(double pe, double altitude, bool inbound)
        {
            var ecc = 1 - (KerbinRadius + pe) / Sma;
            var p = Sma * (1 - ecc * ecc);
            var nu = Math.Acos((p / (KerbinRadius + altitude) - 1) / ecc) * 180 / Math.PI;
            // kOS reads the true anomaly 0 to 360: on the way in it is over 180.
            var ta = inbound ? 360 - nu : nu;
            var script = "global ship is lexicon(\"altitude\", " + D(altitude) + ", \"periapsis\", " + D(pe) + ", \"orbit\", lexicon(\"eccentricity\", " + D(ecc) + ", \"semimajoraxis\", " + D(Sma) + ", \"trueanomaly\", " + D(ta) + ")).\n"
                + "global body_ is lexicon(\"mu\", " + D(KerbinMu) + ", \"radius\", " + D(KerbinRadius) + ", \"atm\", lexicon(\"height\", 70000)).\n"
                + "global ctx is lexicon().\nglobal extra is lexicon().\n"
                + "global addons is lexicon(\"claudia\", lexicon(\"falleta\", { parameter q. return test_fall_eta(q). })).\n"
                + "function claudia_on_ground { return false. }\n"
                + "function claudia_finish { parameter c, ok, reason, ex. return \"finished: \" + reason. }\n"
                + "function starts {\n" + Slice() + "  print round(entry_in).\n  return \"went on\".\n}\n"
                + "print starts().\n";
            return KosHarness.Run(script).ToArray();
        }

        [Fact]
        public void The_flights_duna_return_with_its_periapsis_at_29_km_waits_for_the_air_and_doesnt_refuse()
        {
            var printed = Start(29209, 3000000, true);
            Assert.Equal("went on", printed.Last());
            Assert.True(double.Parse(printed[0], CultureInfo.InvariantCulture) > 0, string.Join(" | ", printed));
        }

        [Fact]
        public void From_the_edge_of_kerbins_sphere_it_waits_a_day_and_more()
        {
            var printed = Start(29209, 84000000, true);
            Assert.Equal("went on", printed.Last());
            Assert.InRange(double.Parse(printed[0], CultureInfo.InvariantCulture), 30000, 200000);
        }

        [Fact]
        public void A_periapsis_above_the_air_still_refuses_with_the_reason_it_had()
        {
            Assert.Equal(new[] { "finished: the orbit never reaches the atmosphere (periapsis 80000 m)" }, Start(80000, 3000000, true));
        }

        [Fact]
        public void Outbound_past_periapsis_still_refuses()
        {
            Assert.Equal(new[] { "finished: the orbit never reaches the atmosphere (periapsis 29209 m)" }, Start(29209, 3000000, false));
        }
    }

    /// <summary><c>test_fall_eta(ask)</c>: Core's FALLETA answer as the addon gives it. Registered through <see cref="KosHarness.Register"/>.</summary>
    internal static class ReentryHyperbolicRigFunctions
    {
        [Function("test_fall_eta")]
        public class FunctionFallEta : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var ask = KosValues.ToDictionary((Lexicon)PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                ReturnValue = ScalarValue.Create(BlockCalls.FallEta(ask));
            }
        }
    }
}
