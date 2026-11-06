// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A polar arrival, priced and flown (#806): the Delta-v Map's <c>polar:</c> row against <see cref="TransferFlight"/>'s own solves, and
    /// <c>plane: "auto"</c> at a pole or a biome, so the figure the Craft Check passes is the one the flight spends.
    /// </summary>
    public sealed class PolarArrivalTests
    {
        private static DeltaVLeg Polar(string body)
        {
            return DeltaVMap.Compute(TransferRig.Circular("Kerbin", 100000, 0).Bodies, "Kerbin").Leg("polar:" + body);
        }

        /// <summary>m/s the plane adds to the flight, every burn after the ejection against the same flight with no plane.</summary>
        private static double Added(string from, double altitude, string target, double pe, double ut)
        {
            Func<object, double> flown = plane =>
            {
                var rig = TransferRig.Circular(from, altitude, ut);
                var step = rig.Run(new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = ut, TurnTime = 60, PlaneOption = PlaneOption.Read(plane) }, 30);
                Assert.True(step.Error == "", step.Error);
                return rig.Burns.Where(b => b.Key != "ejection").Sum(b => b.Value);
            };
            return flown(90.0) - flown(null);
        }

        // ---- The row ----

        [Fact]
        public void The_Mun_s_polar_row_is_near_80_and_Duna_s_under_25()
        {
            Assert.InRange(Polar("Mun").DeltaV, 75, 85);
            Assert.InRange(Polar("Duna").DeltaV, 15, 25);
            Assert.Equal(Polar("Mun").DeltaV, Polar("Mun").PlaneChange);
        }

        [Theory]
        [InlineData("Kerbin", 100000, "Mun", 14000)]
        [InlineData("Kerbin", 100000, "Mun", 30000)]
        [InlineData("Kerbin", 100000, "Mun", 114000)]
        [InlineData("Kerbin", 100000, "Minmus", 4200)]
        [InlineData("Kerbin", 100000, "Minmus", 30000)]
        [InlineData("Kerbin", 100000, "Duna", 60000)]
        [InlineData("Kerbin", 100000, "Duna", 160000)]
        [InlineData("Duna", 70000, "Ike", 9100)]
        [InlineData("Duna", 70000, "Ike", 30000)]
        public void The_row_is_never_under_a_polar_arrival_the_flight_solves(string from, double altitude, string target, double pe)
        {
            foreach (var ut in new[] { 0.0, 2000000.0 })
            {
                var added = Added(from, altitude, target, pe, ut);
                Assert.True(Polar(target).DeltaV >= added, target + " row " + Polar(target).DeltaV + " under " + added + " flown from UT " + ut);
            }
        }

        [Fact]
        public void A_row_for_a_body_with_no_sphere_of_influence_read_still_prices_the_approach()
        {
            var bodies = TransferRig.Circular("Kerbin", 100000, 0).Bodies;
            bodies.Single(b => b.Name == "Mun").SoiRadius = 0;
            Assert.InRange(DeltaVMap.Compute(bodies, "Kerbin").Leg("polar:Mun").DeltaV, 75, 85);
        }

        [Fact]
        public void The_row_s_words_no_longer_describe_a_turn_and_have_not_grown()
        {
            var words = Polar("Mun").Description;
            Assert.Contains("on the approach", words);
            Assert.Contains("return:Mun does not include it", words);
            Assert.DoesNotContain("highest point", words);
            const string Old = "Extra, added to its capture, for a polar orbit of Mun instead of an equatorial one (plane change costed at the arrival speed relative to Mun, an upper bound on a turn at the capture orbit's highest point; return:Mun does not include it)";
            Assert.True(words.Length <= Old.Length, words);
        }

        [Fact]
        public void The_other_rows_and_the_capture_are_unchanged()
        {
            var map = DeltaVMap.Compute(TransferRig.Circular("Kerbin", 100000, 0).Bodies, "Kerbin");
            Assert.Equal(map.Leg("transfer:Mun").Arrival, map.Leg("capture:Mun").DeltaV);
            Assert.InRange(map.Leg("transfer:Mun").DeltaV, 1100, 1250);
            Assert.InRange(map.Leg("land:Mun").DeltaV, 585, 595);
        }

        // ---- The card that found it ----

        [Fact]
        public void Mun_Shot_v2_s_pole_landing_comes_to_about_6390_m_s_and_passes_the_margin()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-editor.json")));
            const string goal = "Land at the Mun's pole and return to Kerbin";
            var facts = CraftCheckPrompts.Facts(goal, snapshot, 15, null).Split('\n');
            // The figures the card added up, as the request gives them: launch, the trip with its capture, polar, the Mun both ways, the return and the deorbit.
            var figures = new[] { "launch:Kerbin", "transfer:Mun", "polar:Mun", "land:Mun", "ascend:Mun", "return:Mun", "land:Kerbin" }.ToDictionary(id => id, id => Figure(facts, id));
            var route = figures.Values.Sum();
            Assert.InRange(figures["polar:Mun"], 75, 85);
            Assert.InRange(route, 6340, 6440);
            // The craft's 7,393 m/s passes the 15% margin, by about 45 m/s; at the old 516 it was short by about 450.
            Assert.InRange(7393 - route * 1.15, 0, 100);
        }

        /// <summary>The whole m/s on a leg's line of the Craft Check's Delta-v Map.</summary>
        private static double Figure(string[] facts, string id)
        {
            var line = facts.Single(l => l.StartsWith("- " + id + ":", StringComparison.Ordinal));
            return double.Parse(Regex.Match(line, @": ([\d,]+) m/s").Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }
    }
}
