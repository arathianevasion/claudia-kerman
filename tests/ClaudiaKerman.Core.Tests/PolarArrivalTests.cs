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

        [Fact]
        public void Duna_s_row_covers_the_highest_periapsis_it_was_fitted_to()
        {
            // From 90 km: on this date a 100 km parking orbit's ejection meets the Mun, so that flight has no solve to compare.
            Assert.True(Polar("Duna").DeltaV >= Added("Kerbin", 90000, "Duna", 180000, 0));
        }

        [Theory]
        [InlineData("Fly to the Mun, land at the north pole, return to Kerbin", "Land on the Mun")]
        [InlineData("Launch from Kerbin to a polar orbit of the Mun", "Capture at the Mun")]
        [InlineData("Land at the Mun's north-pole", "Land on the Mun")]
        [InlineData("Land at the north pole", "Land")]
        public void Natural_goals_at_a_pole_are_read(string goal, string leg)
        {
            Assert.Equal(90, new Scene(goal, "Transfer to the Mun", leg).Pick().Inc);
        }

        [Fact]
        public void A_biome_only_goal_is_read_from_the_map_for_the_arrival_body()
        {
            Assert.Equal(90, new Scene("Land in Ice Caps", "Transfer to the Mun", "Land").WithBiomeMap().Pick().Inc);
            Assert.InRange(new Scene("Land in Twin Craters", "Transfer to the Mun", "Land").WithBiomeMap().Pick().Inc, 27, 33);
        }

        [Fact]
        public void The_map_overrules_a_polar_name_on_a_leg_with_no_landing_too()
        {
            var scene = new Scene("", "Transfer to the Mun", "Capture over Polar Lowlands");
            scene.Snapshot.BiomeLatitudesOf = body => BiomeLatitudes.Survey((lat, lng) => Math.Abs(lat) >= 20 && Math.Abs(lat) <= 40 ? "Polar Lowlands" : "Midlands");
            Assert.Null(scene.Pick());
        }

        // ---- plane "auto" at a pole or a biome ----

        private sealed class Scene
        {
            public readonly TransferRig Rig = TransferRig.Circular("Kerbin", 100000, 0);
            public readonly GameSnapshot Snapshot;

            public Scene(string goal = "", params string[] legs)
            {
                Snapshot = Rig.Snapshot();
                Snapshot.MissionGoal = goal;
                Snapshot.LegsAhead = legs.Select(n => new PlannedSequence { Name = n }).ToList();
                Snapshot.Bodies.Single(b => b.Name == "Mun").BiomeNames.AddRange(new[] { "Poles", "Polar Lowlands", "Midlands", "Twin Craters", "Ice Caps", "Lowlands" });
                Snapshot.Bodies.Single(b => b.Name == "Minmus").BiomeNames.AddRange(new[] { "Poles", "Flats" });
            }

            /// <summary>The Mun's biome map, as the game answers for a point: a polar one named only in the data, a band each side of the equator, and one across it.</summary>
            public Scene WithBiomeMap()
            {
                Snapshot.BiomeLatitudesOf = body => BiomeLatitudes.Survey((lat, lng) =>
                {
                    var north = Math.Abs(lat);
                    return north >= 65 ? "Ice Caps" : north >= 20 && north <= 40 ? "Twin Craters" : "Lowlands";
                });
                return this;
            }

            public ArrivalPlane Pick(string arrival = "Mun", object option = null)
            {
                string error;
                var plane = ArrivalPlanes.Resolve(Snapshot, Rig.System, Rig.System.Find(arrival), PlaneOption.Read(option), out error);
                Assert.Equal("", error);
                return plane;
            }
        }

        [Theory]
        [InlineData("Land at the Mun's pole and return to Kerbin", "Transfer to the Mun", "Land on the Mun", "Return to Kerbin")]
        [InlineData("Plant a flag", "Transfer to the Mun", "Land at the Mun's north pole")]
        [InlineData("Plant a flag", "Transfer to the Mun", "Land in the Poles biome")]
        [InlineData("Land in the Polar Lowlands of the Mun", "Transfer to the Mun", "Land")]
        [InlineData("Polar orbit of the Mun, then come home", "Transfer to the Mun", "Capture at the Mun", "Return to Kerbin")]
        [InlineData("Survey the Mun", "Transfer to the Mun", "Capture into a polar orbit")]
        public void Auto_flies_a_goal_or_leg_at_a_pole_in_at_90_prograde(string goal, params string[] legs)
        {
            var plane = new Scene(goal, legs).Pick();
            Assert.Equal(90, plane.Inc);
            Assert.Equal("site", plane.Source);
            Assert.Null(plane.Normal);
        }

        [Fact]
        public void The_game_s_biome_map_says_a_biome_is_polar_whatever_its_name()
        {
            Assert.Null(new Scene("", "Transfer to the Mun", "Land in Ice Caps").Pick());
            Assert.Equal(90, new Scene("", "Transfer to the Mun", "Land in Ice Caps").WithBiomeMap().Pick().Inc);
            Assert.Equal(90, new Scene("Land in the Mun's Ice Caps", "Transfer to the Mun", "Land").WithBiomeMap().Pick().Inc);
        }

        [Fact]
        public void A_biome_off_the_equator_sets_its_latitude_and_one_across_it_sets_none()
        {
            var plane = new Scene("", "Transfer to the Mun", "Land in Twin Craters").WithBiomeMap().Pick();
            Assert.InRange(plane.Inc, 27, 33);
            Assert.Null(new Scene("", "Transfer to the Mun", "Land in the Lowlands").WithBiomeMap().Pick());
        }

        [Fact]
        public void A_biome_the_game_says_is_not_polar_is_not_flown_as_polar_for_its_name()
        {
            var scene = new Scene("", "Transfer to the Mun", "Land in Polar Lowlands");
            scene.Snapshot.BiomeLatitudesOf = body => BiomeLatitudes.Survey((lat, lng) => Math.Abs(lat) <= 40 ? "Polar Lowlands" : "Midlands");
            Assert.Null(scene.Pick());
        }

        [Theory]
        [InlineData("Land in Twin Craters on the Mun")]
        [InlineData("Land on the Mun")]
        public void A_non_polar_biome_with_no_readable_latitude_sets_no_plane_as_today(string leg)
        {
            Assert.Null(new Scene("", "Transfer to the Mun", leg).Pick());
        }

        [Fact]
        public void A_biome_the_survey_misses_falls_back_on_its_name()
        {
            var scene = new Scene("", "Transfer to the Mun", "Land in Polar Lowlands");
            scene.Snapshot.BiomeLatitudesOf = body => BiomeLatitudes.Survey((lat, lng) => "Midlands");
            Assert.Equal(90, scene.Pick().Inc);
        }

        [Theory]
        [InlineData("Land at the pole", 90.0, 90.0)]
        [InlineData("Land at the pole", "none", null)]
        [InlineData("Land at the pole", 12.0, 12.0)]
        public void Claudia_s_value_wins_over_a_pole(string leg, object option, double? inc)
        {
            var plane = new Scene("Land at the Mun's pole", "Transfer to the Mun", leg).Pick("Mun", option);
            Assert.Equal(inc, plane == null ? (double?)null : plane.Inc);
            if (plane != null) Assert.Equal("number", plane.Source);
        }

        [Fact]
        public void A_landing_site_s_own_latitude_comes_before_the_words_about_a_pole()
        {
            var site = new Scene("Land at the Mun's pole", "Transfer to the Mun", "Land near the pole at (85.2, 10)");
            Assert.Equal(85.7, site.Pick().Inc, 3);
            var picked = new Scene("Land at the Mun's pole", "Transfer to the Mun", "Land");
            picked.Snapshot.PickedSite = new PickedSite { Body = "Mun", Latitude = -70 };
            Assert.Equal(70.5, picked.Pick().Inc, 3);
            // An equatorial site is still a site: the goal's words don't overrule it.
            Assert.Null(new Scene("Land at the Mun's pole", "Transfer to the Mun", "Land at (0.2, 10)").Pick());
        }

        [Theory]
        [InlineData("Polar orbit of Kerbin, then land on the Mun", "Transfer to the Mun", "Land on the Mun")]
        [InlineData("Land at Minmus's pole after a Mun flyby", "Transfer to the Mun", "Transfer to Minmus", "Land on Minmus")]
        [InlineData("A non-polar orbit of the Mun", "Transfer to the Mun", "Capture at the Mun")]
        [InlineData("Land at the Mun", "Transfer to the Mun", "Land with a pole-mounted camera")]
        public void Words_about_a_pole_elsewhere_set_no_plane_here(string goal, params string[] legs)
        {
            Assert.Null(new Scene(goal, legs).Pick());
        }

        [Fact]
        public void A_return_home_that_ends_in_reentry_still_has_no_plane_on_auto()
        {
            Assert.Null(new Scene("Land at Kerbin's north pole", "Return to Kerbin").Pick("Kerbin"));
        }

        [Fact]
        public void The_Mission_s_goal_reaches_the_flight_and_a_pole_is_flown_there_for_tens_of_m_s()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var view = new MissionView { Stage = MissionStage.Running, Goal = "Land at the Mun's pole and return to Kerbin", PlanLegs = new List<PlannedSequence> { new PlannedSequence { Name = "Transfer to the Mun" }, new PlannedSequence { Name = "Land on the Mun" } } };
            rig.Stamp = s => MissionView.Stamp(s, view);
            var step = rig.Run(new TransferFlightAsk { Target = "Mun", TargetPe = 30000, StartUt = 0, TurnTime = 60 }, 30);
            Assert.True(step.Error == "", step.Error);
            Assert.Equal(90, ArrivalPlane.FromRecord(step.AimedPlane).Inc);
            Assert.InRange(rig.Orbit.Inclination, 89.5, 90.5);
            Assert.True(rig.Burns.Where(b => b.Key != "ejection").Sum(b => b.Value) <= Polar("Mun").DeltaV);
        }

        // ---- The survey ----

        [Fact]
        public void The_survey_reads_a_stray_patch_as_noise_and_keeps_what_it_never_found_out()
        {
            var read = BiomeLatitudes.Survey((lat, lng) => Math.Abs(lat) >= 70 || (lat == 2 && lng < -170) ? "Poles" : Math.Abs(lat) < 5 ? "Band" : "");
            Assert.Equal(90, read["Poles"]);
            Assert.Equal(0, read["Band"]);
            Assert.False(read.ContainsKey(""));
            Assert.Empty(BiomeLatitudes.Survey((lat, lng) => ""));
        }

        /// <summary>The whole m/s on a leg's line of the Craft Check's Delta-v Map.</summary>
        private static double Figure(string[] facts, string id)
        {
            var line = facts.Single(l => l.StartsWith("- " + id + ":", StringComparison.Ordinal));
            return double.Parse(Regex.Match(line, @": ([\d,]+) m/s").Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }
    }
}
