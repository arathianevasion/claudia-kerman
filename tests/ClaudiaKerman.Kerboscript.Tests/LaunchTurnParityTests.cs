// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// #978 (ADR 0014 guardrail 4): launch.ks's launch_pitch and launch_thin_pitch against Core's <see cref="LaunchTurn"/> on the same inputs,
    /// for a craft with no thrust to spare (where the law didn't change). Deleted with the Kerboscript in the same ticket.
    /// </summary>
    public class LaunchTurnParityTests
    {
        private static double Kos(string expression)
        {
            return double.Parse(KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single(), CultureInfo.InvariantCulture);
        }

        private static string Num(double value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        [Theory]
        [InlineData(500, 1000, 70000, 0.45)]
        [InlineData(18250, 1000, 70000, 0.45)]
        [InlineData(40000, 3000, 50000, 0.2)]
        [InlineData(4000, 3100, 50000, 0.05)]
        [InlineData(75000, 1000, 70000, 0.8)]
        public void The_pitch_program_is_the_same(double h, double start, double end, double shape)
        {
            Assert.Equal(Kos("launch_pitch(" + Num(h) + ", " + Num(start) + ", " + Num(end) + ", " + Num(shape) + ")"), LaunchTurn.Program(h, start, end, shape), 9);
        }

        [Theory]
        [InlineData(30, 60, 100)]
        [InlineData(58, 60, 100)]
        [InlineData(70, 60, 100)]
        [InlineData(30, 60, 500)]
        [InlineData(30, 60, 501)]
        [InlineData(30, 60, 20000)]
        public void The_thin_air_rule_is_the_same_with_no_thrust_to_spare(double pitch, double prograde, double q)
        {
            bool thin;
            var core = LaunchTurn.Pitch(pitch, prograde, 100, q, 9, 9.8, out thin);
            Assert.Equal(Kos("launch_thin_pitch(" + Num(pitch) + ", " + Num(prograde) + ", " + Num(q) + ", launch_thin_lead())"), core, 9);
            Assert.Equal(core > pitch, thin);
        }
    }
}
