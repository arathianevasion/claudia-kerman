// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// ADR 0014 guardrail 4 (#706): dock.ks's dock_speed, dock_cone and dock_want against their port to Core,
    /// <see cref="DockGuidance"/>, on DockTests' inputs and more. Deleted with the Kerboscript functions in the same ticket.
    /// </summary>
    public class DockGuidanceParityTests
    {
        private const string Opt = "claudia_options(lexicon(), dock_defaults(), 1800):opt";
        private static readonly DockGuidance.Options Defaults = new DockGuidance.Options { ApproachSpeed = 1, FinalSpeed = 0.3, MaxLateral = 0.2 };

        private static double[] Numbers(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        }

        private static string F(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Speed_and_cone_match()
        {
            var distances = new double[] { 2000, 300, 150, 101, 100, 80, 50, 30, 10.5, 10, 2, 0, -5 };
            var script = "local opt is " + Opt + ".\n" + string.Join("\n", distances.Select(d => "print dock_speed(" + F(d) + ", opt). print dock_cone(" + F(d) + ", opt)."));
            var kos = Numbers(script);
            for (var i = 0; i < distances.Length; i++)
            {
                Assert.Equal(kos[2 * i], DockGuidance.Speed(distances[i], Defaults), 9);
                Assert.Equal(kos[2 * i + 1], DockGuidance.Cone(distances[i], Defaults), 9);
            }
        }

        [Fact]
        public void The_wanted_velocity_matches()
        {
            var states = new double[][]
            {
                new double[] { 80, 0, 0 }, new double[] { 5, 0.1, 0 }, new double[] { 5, 0.5, 0 }, new double[] { 1, 0.5, 0 }, new double[] { 15, 0, 8 }, new double[] { -0.5, 0.5, 0 },
                new double[] { -80, 10, 0 }, new double[] { -80, 60, 0 }, new double[] { 150, 3, 20 }, new double[] { 30, 1.4, 2 }, new double[] { 12, 0.25, 4.9 }, new double[] { 19, 0.1, 6 },
            };
            var script = "local opt is " + Opt + ".\n" + string.Join("\n", states.Select(s =>
            {
                var d = Math.Sqrt(s[0] * s[0] + s[1] * s[1]);
                return "local w is dock_want(lexicon(\"along\", " + F(s[0]) + ", \"lat\", " + F(s[1]) + ", \"distance\", " + F(d) + ", \"align_deg\", " + F(s[2])
                    + "), opt). print w:along. print w:lat.";
            }));
            var kos = Numbers(script);
            for (var i = 0; i < states.Length; i++)
            {
                var s = states[i];
                var want = DockGuidance.Want(s[0], s[1], Math.Sqrt(s[0] * s[0] + s[1] * s[1]), s[2], Defaults);
                Assert.Equal(kos[2 * i], want.Along, 9);
                Assert.Equal(kos[2 * i + 1], want.Lat, 9);
            }
        }
    }
}
