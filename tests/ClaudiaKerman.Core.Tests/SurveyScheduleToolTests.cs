// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #1308: the Survey Schedule Planning Tool. Its answer is the one the script call (#1307) gives on the same orbit and waypoints; it is offered
    /// only when a Chosen Contract is an orbital survey, so a Mission with none gets a request without it; Claudia's docs tell her what to quote.
    /// </summary>
    public sealed class SurveyScheduleToolTests : IDisposable
    {
        private const double MunRadius = 200000;
        private const double MunGm = 65138398000.0;
        private const double MunRotation = 138984.37657447575;
        private const double Sma = 210000;
        private const double Inclination = 20;
        private const string Goal = "Survey the Mun from orbit.";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>The game's frame on a body that turns: z north, x on longitude 0, y on 90° E.</summary>
        private sealed class Game : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 45; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                latitude = Math.Asin(z / Math.Sqrt(x * x + y * y + z * z)) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        private static SurveyWaypoint Waypoint(string key, double latitude, double longitude, double? min = null, double? max = null)
        {
            return new SurveyWaypoint { Key = key, Body = "Mun", Latitude = latitude, Longitude = longitude, MinAltitude = min, MaxAltitude = max };
        }

        private static GameSnapshot Snapshot(params SurveyWaypoint[] waypoints)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Bodies.First(b => b.Name == "Mun").RotationPeriod = MunRotation;
            snapshot.Body = "Mun";
            snapshot.Situation = "ORBITING";
            snapshot.Orbit = new SnapshotOrbit { Body = "Mun", SemiMajorAxis = Sma, Eccentricity = 0, Inclination = Inclination, MeanAnomalyAtEpoch = 0.4, Epoch = snapshot.Ut };
            snapshot.SurveyWaypoints = waypoints;
            return snapshot;
        }

        /// <summary>The craft's ground track worked out by hand for this circular orbit (lan 0, argument of periapsis 0): what the game's own propagation gives the script call.</summary>
        private static GroundTrack ByHand(GameSnapshot snapshot)
        {
            var mean = Math.Sqrt(MunGm / (Sma * Sma * Sma));
            var now = snapshot.Ut;
            var tilt = Inclination * Math.PI / 180;
            return ut =>
            {
                var angle = 0.4 + mean * (ut - now);
                var x = Math.Cos(angle);
                var y = Math.Sin(angle) * Math.Cos(tilt);
                var z = Math.Sin(angle) * Math.Sin(tilt);
                var lon = Math.Atan2(y, x) * 180 / Math.PI - (ut - now) * 360 / MunRotation;
                return new TrackPoint { Latitude = Math.Asin(z) * 180 / Math.PI, Longitude = ((lon + 540) % 360) - 180, Altitude = Sma - MunRadius };
            };
        }

        private static JsonElement Run(GameSnapshot snapshot)
        {
            var outcome = PlanningTools.Default().Run("survey_schedule", "{}", snapshot, new Game());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        // ---- the answer matches the script call's ----

        [Fact]
        public void TheToolsAnswerMatchesTheScriptCallsOnTheSameOrbitAndWaypoints()
        {
            var waypoints = new[]
            {
                Waypoint("a", 3, 10), Waypoint("b", 5, 100, null, 20000), Waypoint("c", -4, -170, 40000, null), Waypoint("d", 60, 50),
            };
            var snapshot = Snapshot(waypoints);
            var call = SurveySchedule.For(new SurveyBody { Radius = MunRadius, RotationPeriod = MunRotation },
                new SurveyOrbit { Track = ByHand(snapshot), Period = 2 * Math.PI * Math.Sqrt(Sma * Sma * Sma / MunGm), Inclination = Inclination },
                snapshot.Ut, waypoints, SurveySchedule.StockTriggerRange);

            var answer = Run(snapshot);

            var passes = answer.GetProperty("passes").EnumerateArray().ToList();
            Assert.Equal(call.Count, passes.Count);
            Assert.Contains(call, p => p.Ut.HasValue);
            Assert.Contains(call, p => p.Unreachable);
            for (var i = 0; i < call.Count; i++)
            {
                Assert.Equal(call[i].Waypoint.Where(), passes[i].GetProperty("waypoint").GetString());
                Assert.Equal(call[i].Unreachable, passes[i].GetProperty("unreachable").GetBoolean());
                Assert.Equal(call[i].OutOfBand, passes[i].GetProperty("out_of_band").GetString());
                Assert.Equal(call[i].Ut.HasValue, passes[i].TryGetProperty("pass_ut", out _));
                if (call[i].Ut.HasValue)
                {
                    Assert.InRange(passes[i].GetProperty("pass_ut").GetDouble(), call[i].Ut.Value - 1, call[i].Ut.Value + 1);
                    Assert.InRange(passes[i].GetProperty("altitude").GetDouble(), call[i].Altitude.Value - 1, call[i].Altitude.Value + 1);
                }
                if (call[i].ReachingInclination.HasValue)
                    Assert.Equal(Math.Round(call[i].ReachingInclination.Value, 1), passes[i].GetProperty("reaching_inclination").GetDouble());
            }
            var last = call.Where(p => p.Ut.HasValue).Max(p => p.Ut.Value);
            Assert.InRange(answer.GetProperty("total_wait_s").GetDouble(), last - snapshot.Ut - 1, last - snapshot.Ut + 1);
            Assert.Equal(1, answer.GetProperty("unreachable_count").GetInt32());
        }

        [Fact]
        public void AGroundSurveysSitesComeBackInLandingOrderWithNeedsPrecisionMarked()
        {
            var a = Waypoint("a", 0, 0); a.Ground = true; a.Radius = 2000;
            var b = Waypoint("b", 0, 40); b.Ground = true; b.Radius = 9000;
            var c = Waypoint("c", 0, 10); c.Ground = true;
            var snapshot = Snapshot(a, b, c);

            var answer = Run(snapshot);

            var landings = answer.GetProperty("landings").EnumerateArray().ToList();
            Assert.Equal(new[] { "0.0° N 0.0° E", "0.0° N 10.0° E", "0.0° N 40.0° E" }, landings.Select(l => l.GetProperty("waypoint").GetString()).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, landings.Select(l => l.GetProperty("landing").GetInt32()).ToArray());
            Assert.Equal(new[] { true, false, false }, landings.Select(l => l.GetProperty("needs_precision").GetBoolean()).ToArray());
            Assert.Equal(0, answer.GetProperty("passes").GetArrayLength());
        }

        [Fact]
        public void AGroundSurveyAlongsideAnOrbitalOneGivesBothInTheSameAnswer()
        {
            var ground = Waypoint("g", 1, 5); ground.Ground = true;
            var answer = Run(Snapshot(Waypoint("a", 3, 10), ground));

            Assert.Equal(1, answer.GetProperty("passes").GetArrayLength());
            Assert.Equal(1, answer.GetProperty("landings").GetArrayLength());
        }

        [Fact]
        public void TheToolSaysSoWhenNoChosenSurveyHasAWaypointFromOrbitOverTheBody()
        {
            var outcome = PlanningTools.Default().Run("survey_schedule", "{}", Snapshot(), new Game());

            Assert.True(outcome.IsError);
            Assert.Contains("no chosen survey Contract", outcome.Json);
        }

        [Fact]
        public void WithNoPassAtAllTheToolGivesNoTotalWaitRatherThanZero()
        {
            var answer = Run(Snapshot(Waypoint("far", 60, 50)));

            Assert.False(answer.TryGetProperty("total_wait_s", out _));
            Assert.Equal(1, answer.GetProperty("unreachable_count").GetInt32());
        }

        // ---- offered only for a survey ----

        [Fact]
        public void ThePlanningToolSeamOffersTheToolOnlyWhenAChosenContractIsASurvey()
        {
            var tools = PlanningTools.Default();

            var without = tools.Definitions(ToolOffer.Mission).Select(t => t.Name).ToList();
            var with = tools.Definitions(ToolOffer.Mission, true).Select(t => t.Name).ToList();

            Assert.DoesNotContain("survey_schedule", without);
            Assert.Equal(without.Concat(new[] { "survey_schedule" }), with);
            Assert.DoesNotContain("survey_schedule", tools.Definitions(ToolOffer.CraftCheck, true).Select(t => t.Name));
        }

        private MissionControl Control(IHttpTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Snapshot());
            return mission;
        }

        private static ChosenRequirement Requirement(SurveyWaypoint waypoint)
        {
            return new ChosenRequirement { Key = "c-s/0", Title = "Survey the Mun", Waypoint = waypoint };
        }

        private List<string> RequestToolNames(params ChosenRequirement[] chosen)
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var choice = new ContractChoice { Guids = { "c-s" }, Words = "", RouteWords = "orbit the Mun", Section = "Contract Route\n", Summary = "chosen" };
            choice.Requirements.AddRange(chosen);
            mission.Send(new StartMission(Goal, 25m, "claude-sonnet-5-5").Answered(Goal, choice));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.Stage != MissionStage.AwaitingPlanApproval && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(MissionStage.AwaitingPlanApproval, mission.View.Stage);
            using (var doc = JsonDocument.Parse(transport.Requests[0].Body))
                return doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        }

        [Fact]
        public void AMissionRequestOffersTheToolWhenAChosenContractIsAnOrbitalSurvey()
        {
            var names = RequestToolNames(Requirement(Waypoint("w", 3, 10)));

            Assert.Equal(new[] { "survey_schedule", "propose_mission_plan" }, names.Skip(names.Count - 2));
        }

        [Fact]
        public void AMissionWithNoSurveyContractGetsARequestWithoutTheTool()
        {
            Assert.DoesNotContain("survey_schedule", RequestToolNames(new ChosenRequirement { Key = "c-x/0", Title = "Orbit the Mun" }));
        }

        [Fact]
        public void AGroundSurveyGetsARequestWithoutTheTool()
        {
            var ground = Waypoint("g", 3, 10);
            ground.Ground = true;

            Assert.DoesNotContain("survey_schedule", RequestToolNames(Requirement(ground)));
        }

        // ---- the docs ----

        [Fact]
        public void ClaudiasDocsTellHerToQuotePassTimesTotalWaitAndUnreachableWaypointsAndSetTheDurationFromThem()
        {
            var note = MissionPrompts.SurveyScheduleNote;

            Assert.Contains("survey_schedule tool", note);
            Assert.Contains("quote the pass times, the total wait", note);
            Assert.Contains("each unreachable waypoint", note);
            Assert.Contains("set its expected duration from them", note);
        }
    }
}
