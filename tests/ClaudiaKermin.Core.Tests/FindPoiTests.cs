// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>#713: <c>find_poi</c> through the Planning Tools entry point, and the goal hint that points Claudia at a Point of Interest.</summary>
    public sealed class FindPoiTests
    {
        private sealed class Game : PlanningBodyBase
        {
            public readonly List<SurfaceObject> Objects = new List<SurfaceObject>();
            public int Surveys;

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 321; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 45; }
            public override IList<SurfaceObject> SurfaceObjects(string body) { return Objects.Where(o => o.Body == body).ToList(); }

            public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
            {
                Surveys++;
                base.Survey(body, samples, facts);
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                latitude = longitude = double.NaN;
                return false;
            }
        }

        private readonly PlanningTools _tools = PlanningTools.Default();

        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            // The fixture's table stops at Minmus; the game's has every body.
            snapshot.Bodies.Add(new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = 3.0136321e11, Radius = 320000, AtmosphereHeight = 50000, SeaLevelAtm = 0.0667 });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Moho", Parent = "Sun", Gm = 1.6860938e11, Radius = 250000 });
            return snapshot;
        }

        private static GameSnapshot MunOrbit(double inclination)
        {
            var snapshot = OnPad();
            snapshot.Body = "Mun";
            snapshot.Situation = "ORBITING";
            snapshot.Orbit = new SnapshotOrbit { Body = "Mun", SemiMajorAxis = 230000, Eccentricity = 0.01, Inclination = inclination };
            return snapshot;
        }

        private JsonElement Run(GameSnapshot snapshot, string input, Game game)
        {
            var outcome = _tools.Run("find_poi", input, snapshot, game);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private static Game MunGame()
        {
            var game = new Game();
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "RockArch00", Latitude = -12.47, Longitude = -140.95, Altitude = 2500 });
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "Monolith02", Latitude = -82.2, Longitude = 102.9, Altitude = 1000 });
            game.Objects.Add(new SurfaceObject { Body = "Mun", Name = "Monolith01", Latitude = 57.66, Longitude = 9.14 });
            return game;
        }

        [Fact]
        public void ItListsTheBodysPointsOfInterestLandableFirstThenByNameWithTheSchema()
        {
            var game = MunGame();
            var pois = Run(OnPad(), "{\"body\":\"Mun\"}", game).GetProperty("pois").EnumerateArray().ToList();
            Assert.Equal(new[] { "Monolith TMA-6", "Mun Rock Arch", "Monolith TMA-7" }, pois.Select(p => p.GetProperty("name").GetString()).ToArray());
            var arch = pois[1];
            Assert.Equal(-12.47, arch.GetProperty("latitude").GetDouble());
            Assert.Equal(-140.95, arch.GetProperty("longitude").GetDouble());
            Assert.Equal(2500, arch.GetProperty("terrain_altitude").GetDouble());
            Assert.True(arch.GetProperty("landable").GetBoolean());
            Assert.Equal("land", arch.GetProperty("block").GetString());
            Assert.NotEqual("", arch.GetProperty("note").GetString());
            Assert.False(pois[2].GetProperty("landable").GetBoolean());
            // The height the game didn't give is read once, in one batch.
            Assert.Equal(321, pois[0].GetProperty("terrain_altitude").GetDouble());
            Assert.Equal(1, game.Surveys);
            // Not orbiting the Mun: no distance from the track.
            Assert.False(arch.TryGetProperty("beyond_track_km", out _));
        }

        [Fact]
        public void AnOrbitRoundTheBodyGivesHowFarBeyondTheTracksReachEachLies()
        {
            var pois = Run(MunOrbit(20), "{\"body\":\"Mun\"}", MunGame()).GetProperty("pois").EnumerateArray().ToList();
            var tma6 = pois.Single(p => p.GetProperty("name").GetString() == "Monolith TMA-6");
            Assert.Equal(Math.Round((57.66 - 20) * 200000 * Math.PI / 180 / 1000, 1), tma6.GetProperty("beyond_track_km").GetDouble());
            Assert.Equal(0, pois.Single(p => p.GetProperty("name").GetString() == "Mun Rock Arch").GetProperty("beyond_track_km").GetDouble());
        }

        [Fact]
        public void DunaAndKerbinNameReentry()
        {
            var duna = Run(OnPad(), "{\"body\":\"Duna\"}", new Game()).GetProperty("pois").EnumerateArray().ToList();
            Assert.NotEmpty(duna);
            Assert.All(duna, p => Assert.Equal(FindPoi.Reentry, p.GetProperty("block").GetString()));
            var kerbin = Run(OnPad(), "{\"body\":\"Kerbin\"}", new Game()).GetProperty("pois").EnumerateArray().ToList();
            Assert.Contains(kerbin, p => p.GetProperty("name").GetString() == "Desert Pyramids");
            Assert.All(kerbin, p => Assert.Equal(FindPoi.Reentry, p.GetProperty("block").GetString()));
        }

        [Fact]
        public void ANameNarrowsToOneAndAnUnknownOneIsAnErrorListingThem()
        {
            var one = Assert.Single(Run(OnPad(), "{\"body\":\"Minmus\",\"name\":\"monolith\"}", new Game()).GetProperty("pois").EnumerateArray());
            Assert.Equal("Monolith TMA-8", one.GetProperty("name").GetString());
            var outcome = _tools.Run("find_poi", "{\"body\":\"Mun\",\"name\":\"monolith\"}", OnPad(), MunGame());
            Assert.True(outcome.IsError);
            Assert.Contains("Monolith TMA-6, Mun Rock Arch, Monolith TMA-7", outcome.Json);
        }

        [Fact]
        public void ItIsAPlanningToolOnMissionTurnsWhoseDefinitionStaysSmall()
        {
            Assert.Contains("find_poi", _tools.Definitions(ToolOffer.Mission).Select(t => t.Name));
            Assert.DoesNotContain("find_poi", _tools.Definitions(ToolOffer.CraftCheck).Select(t => t.Name));
            var definition = _tools.Definitions(ToolOffer.Mission).Single(t => t.Name == "find_poi");
            // #713: the definition is part of the cached Mission prefix (MissionPrefixBudgetTests counts characters / 3): 410
            // characters, about 137 tokens, paid for by cuts in find_landing_site's and land's own docs. Growing it is a decision.
            Assert.True(definition.Name.Length + definition.Description.Length + definition.InputSchemaJson.Length <= 410,
                (definition.Name.Length + definition.Description.Length + definition.InputSchemaJson.Length).ToString());
            Assert.Contains("never on it", definition.Description);
            Assert.Contains("find_landing_site's poi", definition.Description);
        }

        // ---- The goal hint ----

        private static string Line(string goal, GameSnapshot snapshot = null)
        {
            snapshot = snapshot ?? OnPad();
            return MissionPrompts.GoalPoiLine(MissionPrompts.GoalPois(snapshot, goal), snapshot);
        }

        [Fact]
        public void TheMunsRockArchGoalCarriesTheTwoArchChoiceLine()
        {
            Assert.Equal("\"rock arch\" names 2 Points of Interest (Mun Rock Arch on Mun, Mun Rock Arch 2 on Mun): choose one, or call find_poi; "
                + "land beside it with find_landing_site's poi and land's poi target, not on it.", Line("Land next to the rock arch on the Mun"));
        }

        [Fact]
        public void MinmussMonolithCarriesTMA8sLine()
        {
            Assert.Equal("Monolith TMA-8 is a Minmus Point of Interest: land beside it with find_landing_site's poi and land's poi target, not on it.",
                Line("Land at Minmus's monolith"));
            Assert.Equal(Line("Land at Minmus's monolith"), Line("land beside Monolith TMA-8 on minmus, then go home"));
            // The trip home names Kerbin, not its monoliths; a goal about Kerbin alone still names them.
            Assert.Equal(Line("Land at Minmus's monolith"), Line("Land next to the monolith on Minmus and return to Kerbin"));
            Assert.StartsWith("\"monolith\" names 5 Points of Interest (KSC Monolith on Kerbin", Line("fly over the monoliths on Kerbin"));
        }

        [Fact]
        public void AGoalWithNoPointOfInterestCarriesNoLine()
        {
            Assert.Equal("", Line("land on the mun and return to kerbin"));
            Assert.Equal("", Line("Prove the kOS link from the launch pad."));
            Assert.Equal("", Line(""));
            Assert.Equal("", Line("land in the twin craters on the mun"));
        }

        [Fact]
        public void AnAirBodysPointOfInterestSaysReentryCantAimAtItAndTMA7IsNotLandable()
        {
            Assert.Equal("Duna Face is a Duna Point of Interest: reentry can't aim at it yet (no precision landing through air).", Line("land by the Duna Face"));
            Assert.StartsWith("Monolith TMA-7 is a Mun Point of Interest: not landable (buried", Line("land at TMA-7 on the mun"));
            Assert.StartsWith("The Mohole is a Moho Point of Interest: land beside it", Line("land by the Mohole"));
        }

        [Fact]
        public void TheLineDropsOnceTheLandingStepIsTicked()
        {
            var poi = Assert.Single(MissionPrompts.GoalPois(OnPad(), "Land next to the monolith on Minmus"));
            Assert.True(MissionControl.PoiToFly(new List<GoalItem>(), poi));
            var steps = new List<GoalItem>
            {
                new GoalItem { Item = "Land beside the monolith on Minmus" },
                new GoalItem { Item = "Return and land on Kerbin" },
            };
            Assert.True(MissionControl.PoiToFly(steps, poi));
            steps[0].Done = true;
            Assert.False(MissionControl.PoiToFly(steps, poi));
        }
    }
}
