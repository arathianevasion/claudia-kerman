// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Mission's cached prefix, its system prompt and tool definitions, stays under a budget (#550). Every Mission request
    /// repeats the prefix, and every revised Mission Plan rewrites it to the cache (#534), so growth shows up in every leg's
    /// cost. It grew 5% between two Mun flights (25,569 tokens in b5f3, 26,865 in 3dc5, measured from the API's cache reads) before
    /// anyone noticed. Raising the budget is a deliberate change in a PR, not drift.
    /// </summary>
    public sealed class MissionPrefixBudgetTests : IDisposable
    {
        // Measured on a03bf44: 20,452 tokens by the mod's estimate (JSON characters / 3, rounded up, as RequestCharge.WorstCase
        // counts input) for the system prompt and tool definitions of the first Mission request in this test's Mission. The estimate
        // runs low against the API's own count (25,569 in b5f3, 26,865 in 3dc5, whose Mission layer also carried a real body table
        // and goal), so compare this figure only with itself.
        private const int TodaysEstimate = 20452;

        /// <summary>About 5% above today's figure (1.05 x 20,452 = 21,475), rounded up to the next hundred.</summary>
        private const int BudgetTokens = 21500;

        // The first Mission request's messages for a Mun goal from Kerbin orbit (same estimate): 5,096 tokens before #575, plus 321 for the
        // plan_transfer row and its lead line = 5,417. The row replaces a tool step, a whole request of about $0.22 in b5f3, 3dc5 and
        // 6be2, for about $0.001 of cache write (and a cache read on each later request).
        private const int FirstTurnEstimate = 5417;

        /// <summary>About 5% above the first turn's figure (1.05 x 5,417 = 5,688), rounded up to the next hundred.</summary>
        private const int FirstTurnBudgetTokens = 5700;

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>The first Mission request, built and sent by MissionControl as in play (BuildRequest), captured at the transport.</summary>
        private string FirstMissionRequestBody()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Requests.Count == 0 && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.NotEmpty(transport.Requests);
            return transport.Requests[0].Body;
        }

        /// <summary>The mod's own estimate of a request's input (RequestCharge.WorstCase): JSON characters / 3, rounded up.</summary>
        private static int EstimatedTokens(string json)
        {
            return (json.Length + 2) / 3;
        }

        /// <summary>The first Mission request for a Mun goal from Kerbin orbit, whose messages carry the stage table and the plan_transfer row.</summary>
        private string FirstMunRequestBody()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan-priced.sse"));
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Land on the Mun and return to Kerbin.", 10m, "claude-sonnet-5-5"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Requests.Count == 0 && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.NotEmpty(transport.Requests);
            return transport.Requests[0].Body;
        }

        [Fact]
        public void TheMessagesOfTheFirstMissionRequestStayUnderTheBudget()
        {
            using (var body = JsonDocument.Parse(FirstMunRequestBody()))
            {
                var messages = body.RootElement.GetProperty("messages").GetRawText();
                var estimate = EstimatedTokens(messages);
                Assert.True(estimate <= FirstTurnBudgetTokens,
                    "The first Mission request's messages are now " + estimate + " tokens (estimated), over the budget of " + FirstTurnBudgetTokens
                    + " (" + FirstTurnEstimate + " when the budget was set): raise it only as a deliberate change, with the cost reason in the PR.");
            }
        }

        [Fact]
        public void TheSystemPromptAndToolDefinitionsOfAMissionRequestStayUnderTheBudget()
        {
            using (var body = JsonDocument.Parse(FirstMissionRequestBody()))
            {
                var prefix = body.RootElement.GetProperty("system").GetRawText() + body.RootElement.GetProperty("tools").GetRawText();
                var estimate = EstimatedTokens(prefix);
                Assert.True(estimate <= BudgetTokens,
                    "The Mission prefix is now " + estimate + " tokens (estimated), over the budget of " + BudgetTokens
                    + " (" + TodaysEstimate + " when the budget was set). Every request repeats it and every revised Mission Plan rewrites it to the cache: "
                    + "raise the budget only as a deliberate change, with the cost reason in the PR.");
            }
        }
    }
}
