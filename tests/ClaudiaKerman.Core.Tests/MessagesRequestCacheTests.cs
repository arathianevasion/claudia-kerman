// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Text.Json;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>The compaction request writes nothing to the cache (#549); every other request keeps its breakpoints.</summary>
    public sealed class MessagesRequestCacheTests
    {
        private const string Sonnet = "claude-sonnet-5-5";

        private static MessagesRequest Request(bool cache)
        {
            var request = new MessagesRequest { Model = Sonnet, MaxTokens = 4000, Compaction = !cache, Cache = cache };
            request.System.Add("You are Claudia.");
            request.System.Add("Mission layer.");
            request.Messages.Add(Message.UserText("Go."));
            return request;
        }

        [Fact]
        public void ACompactionRequestCarriesNoCacheControlAnywhere()
        {
            Assert.DoesNotContain("cache_control", Request(false).ToJson());
        }

        [Fact]
        public void AnOrdinaryRequestKeepsTheTailAndEverySystemLayerAtOneHour()
        {
            var json = Request(true).ToJson();
            using (var body = JsonDocument.Parse(json))
            {
                var root = body.RootElement;
                Assert.Equal("1h", root.GetProperty("cache_control").GetProperty("ttl").GetString());
                Assert.Equal(2, root.GetProperty("system").GetArrayLength());
                foreach (var layer in root.GetProperty("system").EnumerateArray())
                {
                    Assert.Equal("ephemeral", layer.GetProperty("cache_control").GetProperty("type").GetString());
                    Assert.Equal("1h", layer.GetProperty("cache_control").GetProperty("ttl").GetString());
                }
            }
            Assert.Contains("\"thinking\":{\"type\":\"adaptive\"},\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"},\"system\":[{\"type\":\"text\",\"text\":\"You are Claudia.\",\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"}}", json);
        }

        [Fact]
        public void ACompactionsWorstCaseIsAtLeastItsRealCostWhenAllItsInputComesBackPlain()
        {
            var prices = new PlayerConfig().Prices;
            var request = Request(false);
            var inputTokens = (request.ToJson().Length + 2) / 3;
            var usage = new Usage { InputTokens = inputTokens, OutputTokens = request.MaxTokens };
            usage.Iterations.Add(new UsageIteration { Type = "compaction", InputTokens = inputTokens, OutputTokens = request.MaxTokens });
            Assert.True(RequestCharge.WorstCase(prices, Sonnet, request) >= prices.CostOf(usage, Sonnet));
        }
    }
}
