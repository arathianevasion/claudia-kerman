// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>ADR 0014 guardrail 4, #1297: science_decision, science_transmit_choice and science_held_choice against Core's ScienceChoice on the same inputs. Deleted with the Kerboscript.</summary>
    public class ScienceChoiceParityTests
    {
        private static readonly bool[] Bools = { false, true };

        private static string B(bool b)
        {
            return b ? "true" : "false";
        }

        public static IEnumerable<object[]> Decisions()
        {
            foreach (var ex in Bools) foreach (var re in Bools) foreach (var has in Bools) foreach (var inop in Bools) foreach (var av in Bools) foreach (var tr in Bools)
                foreach (var left in new[] { 0, 0.05, 3 })
                    yield return new object[] { ex, re, has, inop, av, tr, left };
        }

        [Fact]
        public void Decision_matches_on_every_input()
        {
            var cases = Decisions().ToList();
            var script = string.Join("\n", cases.Select(c => "print science_decision(" + string.Join(", ", c.Take(6).Select(x => B((bool)x))) + ", " + ((double)c[6]).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")."));
            var kos = KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                Assert.Equal(kos[i], ScienceChoice.Decision((bool)c[0], (bool)c[1], (bool)c[2], (bool)c[3], (bool)c[4], (bool)c[5], (double)c[6]));
            }
        }

        [Fact]
        public void Transmit_and_held_choices_match_on_every_input()
        {
            var cases = new List<object[]>();
            foreach (var mode in new[] { "keep", "transmit", "auto" }) foreach (var ret in Bools) foreach (var loses in Bools) foreach (var send in Bools) foreach (var has in Bools) foreach (var inop in Bools)
                foreach (var ec in new[] { 5.0, 10, 90 })
                    cases.Add(new object[] { mode, ret, loses, send, has, inop, ec });
            var script = string.Join("\n", cases.Select(c =>
                "print science_transmit_choice(\"" + c[0] + "\", " + B((bool)c[1]) + ", " + B((bool)c[2]) + ", " + B((bool)c[3]) + ", " + c[6] + ", 10).\n"
                + "print \"[\" + science_held_choice(" + B((bool)c[4]) + ", " + B((bool)c[5]) + ", \"" + c[0] + "\", " + B((bool)c[1]) + ", " + B((bool)c[2]) + ", " + B((bool)c[3]) + ", " + c[6] + ", 10) + \"]\"."));
            var kos = KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                Assert.Equal(kos[2 * i], ScienceChoice.TransmitChoice((string)c[0], (bool)c[1], (bool)c[2], (bool)c[3], (double)c[6], 10));
                Assert.Equal(kos[2 * i + 1], "[" + ScienceChoice.HeldChoice((bool)c[4], (bool)c[5], (string)c[0], (bool)c[1], (bool)c[2], (bool)c[3], (double)c[6], 10) + "]");
            }
        }
    }
}
