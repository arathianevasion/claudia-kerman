// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #699: a biome the Mission Goal names (Mission m20261103-031325-bac8, "land in the twin craters on the mun"). The matcher,
    /// the request line, the landing check's rule, then the one correction through <see cref="MissionControl"/>.
    /// </summary>
    public sealed class GoalBiomesTests : IDisposable
    {
        private const string TwinCratersGoal = "land in the twin craters on the mun, send a crew report and return to kerbin";
        private const string Sonnet = "claude-sonnet-5-5";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static readonly string[] MunBiomes =
        {
            "Poles", "Lowlands", "Midlands", "Highlands", "Midland Craters", "Highland Craters", "Canyons", "East Crater",
            "East Farside Crater", "Farside Crater", "Northwest Crater", "Southwest Crater", "Twin Craters", "Polar Crater",
            "Polar Lowlands", "Northern Basin",
        };

        private static readonly string[] MinmusBiomes = { "Poles", "Lowlands", "Midlands", "Highlands", "Slopes", "Flats", "Lesser Flats", "Great Flats", "Greater Flats" };

        /// <summary>The on-pad snapshot with the Mun's and Minmus's biomes, as the Adapter reads them.</summary>
        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Bodies.Single(b => b.Name == "Mun").BiomeNames.AddRange(MunBiomes);
            snapshot.Bodies.Single(b => b.Name == "Minmus").BiomeNames.AddRange(MinmusBiomes);
            return snapshot;
        }

        // ---- The matcher ----

        [Fact]
        public void TheTwinCratersGoalNamesTheMunsTwinCraters()
        {
            var found = Assert.Single(MissionPrompts.GoalBiomes(OnPad(), TwinCratersGoal));
            Assert.Equal("Mun", found.Body);
            Assert.Equal("Twin Craters", found.Biome);
        }

        [Fact]
        public void MidlandCratersIsNotReadAsMidlands()
        {
            var found = Assert.Single(MissionPrompts.GoalBiomes(OnPad(), "land in the midland craters on the Mun"));
            Assert.Equal("Midland Craters", found.Biome);
            Assert.Equal("Farside Crater", Assert.Single(MissionPrompts.GoalBiomes(OnPad(), "land at the farside crater of the mun")).Biome);
            Assert.Equal("East Farside Crater", Assert.Single(MissionPrompts.GoalBiomes(OnPad(), "land at the east farside crater of the mun")).Biome);
        }

        [Fact]
        public void AGoalNamingNoBiomeGivesNothing()
        {
            Assert.Empty(MissionPrompts.GoalBiomes(OnPad(), "land on the mun and return to kerbin"));
            Assert.Empty(MissionPrompts.GoalBiomes(OnPad(), "Prove the kOS link from the launch pad."));
            Assert.Empty(MissionPrompts.GoalBiomes(OnPad(), ""));
        }

        [Fact]
        public void AMinmusBiomeInAMunGoalGivesNothing()
        {
            Assert.Empty(MissionPrompts.GoalBiomes(OnPad(), "land on the great flats of the mun"));
        }

        [Fact]
        public void ABiomeTwoNamedBodiesShareGoesToTheBodyNamedNearestIt()
        {
            var found = Assert.Single(MissionPrompts.GoalBiomes(OnPad(), "fly by the Mun, then land on Minmus highlands"));
            Assert.Equal("Minmus", found.Body);
            Assert.Equal("Highlands", found.Biome);
        }

        [Fact]
        public void AGoalNamingNoBodyLooksAtTheCraftsBody()
        {
            var snapshot = OnPad();
            snapshot.Body = "Mun";
            Assert.Equal("Twin Craters", Assert.Single(MissionPrompts.GoalBiomes(snapshot, "land in the twin craters")).Biome);
        }

        // ---- The line ----

        [Fact]
        public void TheLineNamesTheBiomeAndHowToUseItAndIsEmptyForNone()
        {
            Assert.Equal("Twin Craters is a Mun biome: pass it as find_landing_site's biome and as land's target biome.",
                MissionPrompts.GoalBiomeLine(MissionPrompts.GoalBiomes(OnPad(), TwinCratersGoal)));
            Assert.Equal("", MissionPrompts.GoalBiomeLine(new List<GoalBiome>()));
        }

        [Fact]
        public void TheSnapshotJsonCarriesNoBiomeNames()
        {
            var snapshot = OnPad();
            Assert.DoesNotContain("Twin Craters", snapshot.ToJson(false));
            Assert.DoesNotContain("Twin Craters", snapshot.ToJson(true));
        }

        // ---- The landing check's rule ----

        private static readonly Func<string, double, double, string> NoBiomeAt = (body, lat, lng) => "";

        private static GoalBiomeMiss Miss(string script, string craftBody = "Mun", Func<string, double, double, string> biomeAt = null)
        {
            return LandingBiomes.Miss(script, MissionPrompts.GoalBiomes(OnPad(), TwinCratersGoal), OnPad(), craftBody, biomeAt ?? NoBiomeAt);
        }

        [Fact]
        public void ALandingAimedAtAnotherBiomeOfTheGoalsBodyIsAMiss()
        {
            var miss = Miss("local res is land(lexicon(\"target\", lexicon(\"biome\", \"Midland Craters\", \"near\", latlng(3.1, 22)))).");
            Assert.NotNull(miss);
            Assert.Equal("Twin Craters", miss.Goal.Biome);
            Assert.Equal("Midland Craters", miss.Targeted);
            Assert.Equal("the Mission Goal names Twin Craters (a Mun biome); this landing targets Midland Craters", MissionPrompts.GoalBiomeMissed(miss));
        }

        [Fact]
        public void ALandingAimedAtTheGoalsBiomeAnyCaseIsNoMiss()
        {
            Assert.Null(Miss("local res is land(lexicon(\"target\", lexicon(\"biome\", \"twin craters\")))."));
        }

        [Fact]
        public void ABiomeTargetIsJudgedFromAnotherBodyWhenOnlyTheGoalsBodyHasIt()
        {
            Assert.NotNull(Miss("transfer_to(lexicon(\"target\", \"Mun\", \"target_pe\", 30000)). land(lexicon(\"target\", lexicon(\"biome\", \"Midland Craters\"))).", "Kerbin"));
            Assert.Null(Miss("land(lexicon(\"target\", lexicon(\"biome\", \"Slopes\"))).", "Kerbin"));
        }

        [Fact]
        public void APointTargetIsJudgedByTheGamesBiomeThereOnTheGoalsBody()
        {
            Func<string, double, double, string> at = (body, lat, lng) => body == "Mun" && lat == -0.5 ? "Midland Craters" : "Twin Craters";
            Assert.Equal("Midland Craters", Miss("land(lexicon(\"target\", latlng(-0.5, 22))).", "Mun", at).Targeted);
            Assert.Equal("Midland Craters", Miss("land(lexicon(\"target\", lexicon(\"lat\", -0.5, \"lng\", 22))).", "Mun", at).Targeted);
            Assert.Null(Miss("land(lexicon(\"target\", latlng(8, 22))).", "Mun", at));
            // Off the goal's body, or where the game can't say, a point isn't judged.
            Assert.Null(Miss("land(lexicon(\"target\", latlng(-0.5, 22))).", "Kerbin", at));
            Assert.Null(Miss("land(lexicon(\"target\", latlng(-0.5, 22))).", "Mun"));
        }

        [Fact]
        public void AnyVesselAndComputedTargetsAreNotJudged()
        {
            Assert.Null(Miss("land(lexicon(\"daylight\", true))."));
            Assert.Null(Miss("land(lexicon(\"target\", \"any\"))."));
            Assert.Null(Miss("land(lexicon(\"target\", \"vessel\"))."));
            Assert.Null(Miss("local site is latlng(1, 2). land(lexicon(\"target\", site))."));
            Assert.Null(Miss("local b is \"Midlands\". land(lexicon(\"target\", lexicon(\"biome\", b)))."));
        }

        [Fact]
        public void ABiomeOfAnotherGoalBodyIsJudgedOnThatBodyNotTheCrafts()
        {
            var goal = MissionPrompts.GoalBiomes(OnPad(), "land in twin craters on the mun, then on the greater flats of minmus");
            Assert.Equal(2, goal.Count);
            Assert.Null(LandingBiomes.Miss("transfer_to(lexicon(\"target\", \"Minmus\", \"target_pe\", 30000)). land(lexicon(\"target\", lexicon(\"biome\", \"Greater Flats\"))).",
                goal, OnPad(), "Mun", NoBiomeAt));
            var miss = LandingBiomes.Miss("land(lexicon(\"target\", lexicon(\"biome\", \"Flats\"))).", goal, OnPad(), "Mun", NoBiomeAt);
            Assert.Equal("Greater Flats", miss.Goal.Biome);
        }

        [Fact]
        public void APointAfterATransferOrForAnotherVesselIsNotJudged()
        {
            Func<string, double, double, string> at = (body, lat, lng) => "Midland Craters";
            Assert.Null(Miss("transfer_to(lexicon(\"target\", \"Mun\", \"target_pe\", 30000)). land(lexicon(\"target\", latlng(-0.5, 22))).", "Mun", at));
            Assert.Null(Miss("land(lexicon(\"target\", latlng(-0.5, 22))).", "", at));
        }

        [Fact]
        public void ALandingIsStillToFlyUntilItsStepIsTicked()
        {
            var biome = new GoalBiome { Body = "Mun", Biome = "Twin Craters" };
            Assert.True(MissionControl.LandingToFly(new List<GoalItem>(), biome));
            var steps = new List<GoalItem>
            {
                new GoalItem { Item = "Land in Twin Craters on the Mun" },
                new GoalItem { Item = "Return and land on Kerbin" },
            };
            Assert.True(MissionControl.LandingToFly(steps, biome));
            steps[0].Done = true;
            Assert.False(MissionControl.LandingToFly(steps, biome));
            Assert.True(MissionControl.LandingToFly(new List<GoalItem> { new GoalItem { Item = "Landing on the Mun" } }, biome));
            Assert.False(MissionControl.LandingToFly(new List<GoalItem> { new GoalItem { Item = "Send a crew report from the landed Mun lander" } }, biome));
        }

        [Fact]
        public void AGoalNamingNoBodyNeverFallsBackToKerbin()
        {
            var snapshot = OnPad();
            snapshot.Bodies.Single(b => b.Name == "Kerbin").BiomeNames.AddRange(new[] { "Water", "Shores" });
            Assert.Empty(MissionPrompts.GoalBiomes(snapshot, "splash down in the water"));
        }

        [Fact]
        public void AGoalNamingNoBiomeJudgesNothing()
        {
            Assert.Null(LandingBiomes.Miss("land(lexicon(\"target\", lexicon(\"biome\", \"Midland Craters\"))).",
                MissionPrompts.GoalBiomes(OnPad(), "land on the mun"), OnPad(), "Mun", NoBiomeAt));
        }

        // ---- Through the Mission ----

        private MissionControl Control(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            return mission;
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }

        private void ToSequencePlan(MissionControl mission, string goal)
        {
            mission.Send(new StartMission(goal, 25m, Sonnet));
            DrainUntil(mission, v => v.CraftCheck.State != CraftCheckState.Running);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
        }

        private static ScriptedResponse Reply(string json)
        {
            var text = JsonSerializer.Serialize(json);
            var sse = new StringBuilder();
            sse.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_t\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],"
                + "\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":3900,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}}\n\n");
            sse.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
            sse.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + text + "}}\n\n");
            sse.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
            sse.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},"
                + "\"usage\":{\"output_tokens\":90,\"iterations\":[{\"type\":\"message\",\"input_tokens\":3900,\"output_tokens\":90}]}}\n\n");
            sse.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
            return new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sse.ToString()) };
        }

        private static ScriptedResponse PlanReply()
        {
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\",\"sequence_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"mission_plan\":{\"why\":\"Two legs.\",\"sequences\":["
                + "{\"name\":\"Land\",\"intent\":\"Land.\",\"delta_v\":0,\"timing\":\"now\",\"due_time_ut\":-1,\"manual\":false},"
                + "{\"name\":\"Home\",\"intent\":\"Go home.\",\"delta_v\":1000,\"timing\":\"after\",\"due_time_ut\":-1,\"manual\":false}],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"short\",\"mission_time_s\":-1}}");
        }

        private static ScriptedResponse Landing(string biome)
        {
            var script = "local res is land(lexicon(\"target\", lexicon(\"biome\", \"" + biome + "\", \"near\", latlng(3.1, 22)))). addons:claudia:report(res).";
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Plan.\",\"reply_intent\":\"none\",\"unsure_because\":\"\","
                + "\"mission_plan\":null,\"debrief\":null,\"alert\":null,"
                + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Land\",\"intent\":\"Land.\",\"why\":\"Because.\",\"target\":\"\",\"tolerances\":\"\",\"expected_delta_v\":600,\"expected_duration_s\":900,"
                + "\"abort_conditions\":[],\"risks\":[],\"telemetry_checks\":[],"
                + "\"script\":" + JsonSerializer.Serialize(script) + ",\"custom_script_reason\":\"\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"\"}}");
        }

        private List<JsonElement> PlanLines(MissionControl mission)
        {
            var path = Path.Combine(_save.Path, "ClaudiaKerman", "missions", mission.View.MissionId, "plans.jsonl");
            return File.ReadAllText(path).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).Where(l => l.GetProperty("event").GetString() == "plan").ToList();
        }

        private const string Line = "Twin Craters is a Mun biome: pass it as find_landing_site";

        [Fact]
        public void ALandingAimedAtAnotherBiomeGoesBackOnceAndTheRequestsCarryTheLine()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Landing("Midland Craters"), Landing("Twin Craters"));
            var mission = Control(transport);
            ToSequencePlan(mission, TwinCratersGoal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(3, transport.Requests.Count);
            Assert.Contains(Line, transport.Requests[0].Body);
            Assert.Contains(Line, transport.Requests[1].Body);
            Assert.Contains("The Mission Goal names Twin Craters (a Mun biome); this landing targets Midland Craters", transport.Requests[2].Body);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("this landing targets Midland Craters"));
            var line = Assert.Single(PlanLines(mission));
            Assert.True(line.GetProperty("script_rewrite").GetBoolean());
            Assert.DoesNotContain(line.GetProperty("lints").EnumerateArray(), l => l.GetString().Contains("landing biome"));
        }

        [Fact]
        public void AKeptOtherBiomeIsOfferedAfterTheOneCorrectionWithTheWarningLogged()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Landing("Midland Craters"), Landing("Midland Craters"));
            var mission = Control(transport);
            ToSequencePlan(mission, TwinCratersGoal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(3, transport.Requests.Count);
            var line = Assert.Single(PlanLines(mission));
            Assert.Contains(line.GetProperty("lints").EnumerateArray(), l => l.GetString().StartsWith("landing biome: the Mission Goal names Twin Craters"));
        }

        [Fact]
        public void ALandingAimedAtTheGoalsBiomeIsOfferedWithNoRewrite()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Landing("Twin Craters"));
            var mission = Control(transport);
            ToSequencePlan(mission, TwinCratersGoal);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.False(Assert.Single(PlanLines(mission)).GetProperty("script_rewrite").GetBoolean());
        }

        [Fact]
        public void AGoalNamingNoBiomeAddsNoLineAndSendsNothingBack()
        {
            var transport = new StreamingFakeTransport(PlanReply(), Landing("Midland Craters"));
            var mission = Control(transport);
            ToSequencePlan(mission, "land on the mun, send a crew report and return to kerbin");
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            Assert.Equal(2, transport.Requests.Count);
            Assert.DoesNotContain(" biome: pass ", transport.Requests[0].Body);
            Assert.DoesNotContain(" biome: pass ", transport.Requests[1].Body);
        }
    }
}
