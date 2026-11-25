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
            // #1351: where a node is handed over the passes are the Survey Plane's, from the orbit after the burn.
            var body = new SurveyBody { Radius = MunRadius, RotationPeriod = MunRotation, Gm = MunGm };
            SurfaceUnder under = (double x, double y, double z, out double lat, out double lon) => new Game().SurfaceUnder("Mun", x, y, z, out lat, out lon);
            var plane = SurveySchedule.Plane(body, snapshot.Orbit, snapshot.Ut, under, waypoints, SurveySchedule.StockTriggerRange);
            if (plane.Node != null) call = SurveySchedule.PassesAfterNode(body, snapshot.Orbit, plane, snapshot.Ut, under, waypoints, SurveySchedule.StockTriggerRange);

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

        // ---- #1341: the Survey Plane and its node ----

        private static bool UnderGame(double x, double y, double z, out double latitude, out double longitude)
        {
            return new Game().SurfaceUnder("Mun", x, y, z, out latitude, out longitude);
        }

        [Fact]
        public void TheToolGivesTheSamePlaneAndNodeAsTheScriptCallOnTheSameOrbitAndWaypoints()
        {
            var waypoints = new[] { Waypoint("a", 10.0, 58.2), Waypoint("b", 12.5, 12.5), Waypoint("c", 7.1, 6.0), Waypoint("d", 13.2, 4.2), Waypoint("e", 19.2, 0.7) };
            var snapshot = Snapshot(waypoints);
            var mun = snapshot.Bodies.First(b => b.Name == "Mun");
            // The script call runs this on the game's own elements and frame reads.
            var call = SurveySchedule.Plane(new SurveyBody { Radius = mun.Radius, RotationPeriod = mun.RotationPeriod, Gm = mun.Gm }, snapshot.Orbit, snapshot.Ut,
                UnderGame, waypoints, snapshot.SurveyTriggerRange);

            var answer = Run(snapshot);

            Assert.NotNull(call.Node);
            var plane = answer.GetProperty("survey_plane");
            Assert.Equal(Math.Round(call.Inclination, 1), plane.GetProperty("inclination").GetDouble());
            Assert.Equal(Math.Round(call.Lan, 1), plane.GetProperty("lan").GetDouble());
            Assert.Equal(call.Passed.Select(w => w.Where()), plane.GetProperty("passes_over").EnumerateArray().Select(w => w.GetString()));
            var node = answer.GetProperty("node");
            Assert.Equal(Math.Round(call.Node.Ut, 1), node.GetProperty("burn_ut").GetDouble());
            Assert.Equal(Math.Round(call.Node.Prograde, 2), node.GetProperty("prograde").GetDouble());
            Assert.Equal(Math.Round(call.Node.Normal, 2), node.GetProperty("normal").GetDouble());
            Assert.Equal(Math.Round(call.Node.Radial, 2), node.GetProperty("radial").GetDouble());
            Assert.Equal(Math.Round(call.Node.DeltaV, 1), node.GetProperty("delta_v").GetDouble());
        }

        [Fact]
        public void WhenTheToolGivesANodeItsPassesAreForTheOrbitAfterTheBurnAndMarkedSo()
        {
            var waypoints = new[] { Waypoint("a", 10.0, 58.2), Waypoint("b", 12.5, 12.5), Waypoint("c", 7.1, 6.0), Waypoint("d", 13.2, 4.2), Waypoint("e", 19.2, 0.7) };
            var snapshot = Snapshot(waypoints);

            var answer = Run(snapshot);

            Assert.True(answer.TryGetProperty("node", out var node));
            Assert.True(answer.GetProperty("passes_after_node").GetBoolean());
            var burn = node.GetProperty("burn_ut").GetDouble();
            var timed = answer.GetProperty("passes").EnumerateArray().Where(p => p.TryGetProperty("pass_ut", out _)).ToList();
            Assert.Equal(answer.GetProperty("survey_plane").GetProperty("passes_over").GetArrayLength(), timed.Count);
            Assert.All(timed, p => Assert.InRange(p.GetProperty("pass_ut").GetDouble(), burn, snapshot.Ut + SurveySchedule.PlaneWindow));
        }

        [Fact]
        public void WhenTheCraftsOwnPlaneServesTheToolGivesThePlaneAndNoNode()
        {
            // One waypoint under the craft's own track 40° on from its node (it starts 23° on): no plane passes over more.
            var snapshot = Snapshot(Waypoint("a", 12.7, 38.0));

            var answer = Run(snapshot);

            Assert.Equal(20, answer.GetProperty("survey_plane").GetProperty("inclination").GetDouble());
            Assert.False(answer.TryGetProperty("node", out _));
            Assert.False(answer.TryGetProperty("passes_after_node", out _));
        }

        [Fact]
        public void AGroundSurveysAnswerHasNoSurveyPlane()
        {
            var site = Waypoint("g", 0, 10); site.Ground = true;

            var answer = Run(Snapshot(site));

            Assert.False(answer.TryGetProperty("survey_plane", out _));
            Assert.False(answer.TryGetProperty("node", out _));
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
            Assert.Equal(new[] { false, true, false }, landings.Select(l => l.GetProperty("radius_unknown").GetBoolean()).ToArray());
            Assert.Equal(0, answer.GetProperty("passes").GetArrayLength());
        }

        [Fact]
        public void TheToolsLandingOrderWithNoClosedOrbitIsTheOneTheScriptCallGives()
        {
            // #1327: the script call (SurveySchedule.Entries, what ADDONS:CLAUDIA:SURVEYSCHEDULE runs) with no closed orbit, against the tool on the same sites.
            var sites = new List<SurveyWaypoint>();
            foreach (var at in new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 40.0 }, new[] { 0.0, 10.0 } })
            {
                var site = Waypoint("g" + at[1], at[0], at[1]); site.Ground = true; sites.Add(site);
            }
            var snapshot = Snapshot(sites.ToArray());
            snapshot.Orbit.Eccentricity = 1.5;

            var answer = Run(snapshot);
            var script = SurveySchedule.Entries(new SurveyBody { Radius = MunRadius }, null, snapshot.Ut, new List<SurveyWaypoint>(), sites, 15000);

            Assert.Equal(script.Select(x => x.Waypoint.Key), answer.GetProperty("landings").EnumerateArray().Select(x => x.GetProperty("waypoint").GetString()).Select(w => sites.First(y => y.Where() == w).Key));
            Assert.Equal(3, script.Count);
        }

        [Fact]
        public void AMixedSurveyStillGivesItsLandingsWhenTheCraftHasNoOrbit()
        {
            var ground = Waypoint("g", 1, 5); ground.Ground = true;
            var snapshot = Snapshot(Waypoint("a", 3, 10), ground);
            snapshot.Orbit = null;

            var answer = Run(snapshot);

            Assert.Equal(1, answer.GetProperty("landings").GetArrayLength());
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
            // #1351: an orbital waypoint always has a pass on the Survey Plane the tool hands over; a survey of landings alone has none.
            var site = Waypoint("g", 0, 10); site.Ground = true;

            var answer = Run(Snapshot(site));

            Assert.False(answer.TryGetProperty("total_wait_s", out _));
            Assert.Empty(answer.GetProperty("passes").EnumerateArray());
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
        public void AGroundSurveyGetsARequestWithTheTool()
        {
            var ground = Waypoint("g", 3, 10);
            ground.Ground = true;

            Assert.Contains("survey_schedule", RequestToolNames(Requirement(ground)));
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

        [Fact]
        public void ClaudiasDocsTellHerToCoastStraightToEachPassAndReportEachWaypointLeftForALaterPlane()
        {
            var tool = PlanningTools.Default().Definitions(ToolOffer.Mission, true).First(t => t.Name == "survey_schedule").Description;
            var note = MissionPrompts.SurveyScheduleNote;

            Assert.Contains("coast straight to that ut", note);
            Assert.Contains("never one orbit at a time", note);
            Assert.Contains("run science", note);
            Assert.Contains("left for a later plane", note);
            Assert.Contains("one window shared by every waypoint", note);
            Assert.Contains("the total wait", note);
            Assert.Contains("left_for_next_plane", tool);
            Assert.Contains("43,200 s", tool);
        }

        [Fact]
        public void ClaudiasDocsTellHerToFlyTheSurveyPlanesNodeWithExecNodeAndQuoteItInTheSequencePlan()
        {
            var tool = PlanningTools.Default().Definitions(ToolOffer.Mission, true).First(t => t.Name == "survey_schedule").Description;
            foreach (var docs in new[] { MissionPrompts.SurveyScheduleNote, tool })
            {
                Assert.Contains("Survey Plane", docs);
                Assert.Contains("exec_node", docs);
                Assert.Contains("when one is given", docs);
                Assert.Contains("quote the Survey Plane and its burn", docs);
            }
            Assert.Contains("ADDONS:CLAUDIA:SURVEYPLANE()", MissionPrompts.SurveyScheduleNote);
        }

        [Fact]
        public void ClaudiasDocsTellHerToLandAtTheFirstSiteThenSiteHopToEachNextInOrderWithScienceAtEach()
        {
            var note = MissionPrompts.SurveyLandingNote;

            Assert.Contains("land at the first site with land", note);
            Assert.Contains("site_hop", note);
            Assert.Contains("in landing order", note);
            Assert.Contains("science", note);
            Assert.Contains("needs_precision", note);
        }

        [Fact]
        public void ClaudiasDocsSayWhatRadiusUnknownMeansAndToTreatItAsNeedingPrecision()
        {
            var note = MissionPrompts.SurveyLandingNote;

            Assert.Contains("radius_unknown", note);
            Assert.Contains("couldn't read", note);
            Assert.Contains("treat it as needing precision", note);
        }

        // ---- #1350: only the waypoints the game still shows unmet ----

        [Fact]
        public void TheWaypointsTheGameShowsMetAreLeftOutOfTheToolsAnswer()
        {
            var chosen = new[] { "a", "b", "c" }.Select(k => new ChosenRequirement { Key = "g/" + k, Waypoint = Waypoint(k, 3, 10) }).ToList();
            var readings = new List<RequirementReading>
            {
                new RequirementReading { Key = "g/a", State = "met" },
                new RequirementReading { Key = "g/b", State = "completed" },
                new RequirementReading { Key = "g/c", State = "not met" },
            };

            var open = SurveyScheduleTool.Unmet(chosen, readings);

            Assert.Equal(new[] { "c" }, open.Select(w => w.Key).ToArray());
        }

        [Fact]
        public void With19North0East7OpenFromTheMun14DegreeOrbitTheSurveyPlanePassesOverItAndItsNodeReachesIt()
        {
            // #1344: the four met sites left out, only 19.2° N 0.7° E open, a 10 km, 14° orbit with lan 121°.
            var open = Waypoint("open", 19.2, 0.7);
            var snapshot = Snapshot(open);
            snapshot.Orbit = new SnapshotOrbit { Body = "Mun", SemiMajorAxis = MunRadius + 10000, Eccentricity = 0, Inclination = 14, Lan = 121, MeanAnomalyAtEpoch = 0.4, Epoch = snapshot.Ut };

            var answer = Run(snapshot);

            var plane = answer.GetProperty("survey_plane");
            Assert.Equal(new[] { open.Where() }, plane.GetProperty("passes_over").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.True(answer.TryGetProperty("node", out _));
            // The craft's own 14° plane can't reach 19.2° N; the node turns it to the Survey Plane, which does.
            // #1351: its pass is on the Survey Plane, after the burn, not "unreachable" from the orbit it leaves.
            var pass = answer.GetProperty("passes").EnumerateArray().Single();
            Assert.False(pass.GetProperty("unreachable").GetBoolean());
            Assert.True(pass.GetProperty("pass_ut").GetDouble() > answer.GetProperty("node").GetProperty("burn_ut").GetDouble());
            Assert.InRange(plane.GetProperty("inclination").GetDouble(), 14.5, 20);
            Assert.True(answer.GetProperty("node").GetProperty("delta_v").GetDouble() > 0);
            // The script call's own function, given the same waypoints, agrees with the tool.
            var body = new SurveyBody { Radius = MunRadius, RotationPeriod = MunRotation, Gm = MunGm };
            var call = SurveySchedule.Plane(body, snapshot.Orbit, snapshot.Ut, (double x, double y, double z, out double lat, out double lon) => new Game().SurfaceUnder("Mun", x, y, z, out lat, out lon),
                new List<SurveyWaypoint> { open }, snapshot.SurveyTriggerRange);
            Assert.Equal(Math.Round(call.Inclination, 1), plane.GetProperty("inclination").GetDouble());
            Assert.Equal(Math.Round(call.Lan, 1), plane.GetProperty("lan").GetDouble());
            Assert.Equal(call.Node != null, answer.TryGetProperty("node", out _));
        }
    }
}
