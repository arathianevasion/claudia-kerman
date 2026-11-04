// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#566: Haiku 5.5 as a selectable model, its two price tiers and the per-model cache-read price.</summary>
    public class PriceTierTests
    {
        private const string Haiku = "claude-haiku-5-5";
        private const string Sonnet = "claude-sonnet-5-5";

        private static MessagesRequest Request(string model, int systemChars)
        {
            var request = new MessagesRequest { Model = model, MaxTokens = 1000 };
            request.System.Add(new string('x', systemChars));
            return request;
        }

        [Fact]
        public void TheShippedModelsAreHaikuThenSonnetAndEachHasAPriceLine()
        {
            var config = new PlayerConfig();
            Assert.Equal(new[] { Haiku, Sonnet }, config.Models);
            foreach (var model in config.Models) Assert.True(config.Prices.Knows(model), model);
            // Opus left the shipped list but its price stays: a fallback can still route a refusal to it.
            Assert.True(config.Prices.Knows("claude-opus-5-5"));
        }

        [Fact]
        public void TheShippedConfigFileOffersHaikuThenSonnetAndItsPriceLinesLoadBack()
        {
            using (var dir = new TempDir())
            {
                var loaded = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(new[] { Haiku, Sonnet }, loaded.Models);
                Assert.Contains("models = claude-haiku-5-5, claude-sonnet-5-5\n", File.ReadAllText(Path(dir)));
                Assert.Equal(0.10m, loaded.Prices.PriceOf(Haiku).InputPerMTok);
                Assert.Equal(0.01m, loaded.Prices.PriceOf(Haiku).CacheReadPerMTok);
                Assert.Equal(2.50m, loaded.Prices.PriceOf(Haiku, 100001).OutputPerMTok);
                Assert.Equal(0.20m, loaded.Prices.PriceOf("claude-opus-5-5").CacheReadPerMTok);
            }
        }

        private static string Path(TempDir dir)
        {
            return System.IO.Path.Combine(dir.Sub, PlayerConfig.FileName);
        }

        [Fact]
        public void TheCacheReadPriceIsPerModelAndDefaultsToATenthOfInput()
        {
            var prices = new PlayerConfig().Prices;
            Assert.Equal(0.10m, prices.PriceOf(Sonnet).CacheReadPerMTok);
            Assert.Equal(0.20m, prices.PriceOf("claude-opus-5-5").CacheReadPerMTok);
            Assert.Equal(0.01m, prices.PriceOf(Haiku).CacheReadPerMTok);
            Assert.Equal(0.05m, prices.PriceOf(Haiku, 100001).CacheReadPerMTok);
            Assert.Equal(0.20m, prices.PriceOf("claude-sonnet-5").CacheReadPerMTok);
            Assert.Equal(0.5m, new ModelPrice(5m, 25m).CacheReadPerMTok);

            var usage = new Usage { CacheReadInputTokens = 100000 };
            Assert.Equal(0.01m, prices.CostOf(usage, Sonnet));
            Assert.Equal(0.02m, prices.CostOf(usage, "claude-opus-5-5"));
            Assert.Equal(0.001m, prices.CostOf(usage, Haiku));
        }

        [Fact]
        public void AHaikuPromptIsPaidAtTheShortTierUpTo100kTokensAndTheLongTierOverIt()
        {
            var prices = new PlayerConfig().Prices;
            // 40k fresh + 30k written + 30k read = exactly 100,000: short tier.
            var atLimit = new Usage { InputTokens = 40000, CacheCreationInputTokens = 30000, CacheReadInputTokens = 30000, OutputTokens = 1000 };
            Assert.Equal((40000 * 0.10m + 30000 * 0.20m + 30000 * 0.01m + 1000 * 0.50m) / 1000000m, prices.CostOf(atLimit, Haiku));
            // One more token and the whole request is at the long tier.
            var over = new Usage { InputTokens = 40001, CacheCreationInputTokens = 30000, CacheReadInputTokens = 30000, OutputTokens = 1000 };
            Assert.Equal((40001 * 0.50m + 30000 * 1.00m + 30000 * 0.05m + 1000 * 2.50m) / 1000000m, prices.CostOf(over, Haiku));
            // Sonnet has one tier.
            Assert.Equal((40001 * 2m + 30000 * 4m + 30000 * 0.10m + 1000 * 10m) / 1000000m, prices.CostOf(over, Sonnet));
        }

        [Fact]
        public void EachIterationPicksItsOwnTier()
        {
            var prices = new PlayerConfig().Prices;
            var usage = new Usage();
            usage.Iterations.Add(new UsageIteration { Type = "compaction", InputTokens = 150000, OutputTokens = 1000 });
            usage.Iterations.Add(new UsageIteration { Type = "message", CacheReadInputTokens = 5000, OutputTokens = 1000 });
            Assert.Equal((150000 * 0.50m + 1000 * 2.50m + 5000 * 0.01m + 1000 * 0.50m) / 1000000m, prices.CostOf(usage, Haiku));
        }

        [Fact]
        public void TheWorstCaseUsesTheLongTierFromTheProbedRequestSize()
        {
            var prices = new PlayerConfig().Prices;
            // Every estimated input token as a cache write.
            Assert.Equal((100000 * 0.20m + 1000 * 0.50m) / 1000000m, prices.WorstCase(Haiku, 100000, 1000));
            Assert.Equal((100001 * 1.00m + 1000 * 2.50m) / 1000000m, prices.WorstCase(Haiku, 100001, 1000));

            var small = Request(Haiku, 3000);
            var large = Request(Haiku, 320000);
            Assert.Equal(prices.WorstCase(Haiku, (small.ToJson().Length + 2) / 3, 1000), RequestCharge.WorstCase(prices, Haiku, small));
            var estimated = (large.ToJson().Length + 2) / 3;
            Assert.True(estimated > 100000);
            Assert.Equal(estimated * 1.00m / 1000000m + 1000 * 2.50m / 1000000m, RequestCharge.WorstCase(prices, Haiku, large));
        }

        [Fact]
        public void TheInputPartOfALongTierWorstCaseLeavesOutTheLongTierOutputPrice()
        {
            var prices = new PlayerConfig().Prices;
            var large = Request(Haiku, 320000);
            var worst = RequestCharge.WorstCase(prices, Haiku, large);
            var estimated = (large.ToJson().Length + 2) / 3;
            Assert.Equal(estimated * 1.00m / 1000000m, RequestCharge.InputCost(prices, Haiku, large, worst));
        }

        [Fact]
        public void AnUnknownModelIsChargedAtTheHighestPriceOfEachKindAtThatPromptSize()
        {
            var prices = new PlayerConfig().Prices;
            var shortTier = prices.PriceOf("some-future-model", 1000);
            Assert.Equal(prices.Models.Max(m => prices.PriceOf(m).CacheReadPerMTok), shortTier.CacheReadPerMTok);
            // At 100k+ the Haiku long tier (0.50 in) is under Fable (10 in), but the cache read is its own maximum.
            var longTier = prices.PriceOf("some-future-model", 100001);
            Assert.Equal(prices.Models.Max(m => prices.PriceOf(m, 100001).InputPerMTok), longTier.InputPerMTok);
            Assert.Equal(prices.Models.Max(m => prices.PriceOf(m, 100001).CacheReadPerMTok), longTier.CacheReadPerMTok);
        }

        [Fact]
        public void TheConfigParsesTheNewPriceSyntaxAndTheOldTwoValueLinesUnchanged()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                // The long line comes before its base line: order must not matter.
                File.AppendAllText(Path(dir),
                    "price.my-model.long = 3, 12, 0.4\n"
                    + "price.my-model = 1, 4, 0.05\n"
                    + "price.old-model = 1.5, 7\n"
                    + "price.bad-model = 1, 4, nope\n"
                    + "price.bad-two = 1, 4, 0.1, 9\n"
                    + "price.claude-haiku-5-5 = 0.2, 0.6, 0.02\n");
                var prices = PlayerConfig.LoadOrCreate(dir.Sub).Prices;

                Assert.Equal(1m, prices.PriceOf("my-model").InputPerMTok);
                Assert.Equal(0.05m, prices.PriceOf("my-model").CacheReadPerMTok);
                Assert.Equal(3m, prices.PriceOf("my-model", 100001).InputPerMTok);
                Assert.Equal(0.4m, prices.PriceOf("my-model", 100001).CacheReadPerMTok);

                Assert.Equal(1.5m, prices.PriceOf("old-model").InputPerMTok);
                Assert.Equal(7m, prices.PriceOf("old-model").OutputPerMTok);
                Assert.Equal(0.15m, prices.PriceOf("old-model").CacheReadPerMTok);
                Assert.Equal(1.5m, prices.PriceOf("old-model", 500000).InputPerMTok);

                Assert.False(prices.Knows("bad-model"));
                Assert.False(prices.Knows("bad-two"));

                // A new base line for Haiku keeps the shipped long tier.
                Assert.Equal(0.2m, prices.PriceOf(Haiku).InputPerMTok);
                Assert.Equal(0.5m, prices.PriceOf(Haiku, 100001).InputPerMTok);
            }
        }

        [Fact]
        public void TheStartFormEstimateForHaikuIsAboutAQuarterOfADollar()
        {
            var prices = new PlayerConfig().Prices;
            Assert.Equal(0.24m, prices.EstimateMission(Haiku));
            Assert.Equal(2.40m, prices.EstimateMission(Sonnet));
        }

        [Fact]
        public void ASonnetRequestsJsonIsTheBytesItWasBeforeHaikuWasOffered()
        {
            var request = new MessagesRequest { Model = Sonnet, MaxTokens = 16000, Effort = "high" };
            request.System.Add("You are Claudia.");
            request.Messages.Add(Message.UserText("Hello."));
            Assert.Equal(PinnedSonnetJson, request.ToJson());
            // The fallbacks field goes on every model: Haiku 5.5 accepted it on structured-output requests (probed 2026-10-31).
            request.Model = Haiku;
            Assert.Equal(PinnedSonnetJson.Replace(Sonnet, Haiku), request.ToJson());
        }

        // Recorded from MessagesRequest before this change (it is untouched by #566).
        private const string PinnedSonnetJson =
            "{\"model\":\"claude-sonnet-5-5\",\"max_tokens\":16000,\"stream\":true,\"fallbacks\":\"default\",\"thinking\":{\"type\":\"adaptive\"},"
            + "\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"},\"system\":[{\"type\":\"text\",\"text\":\"You are Claudia.\","
            + "\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"1h\"}}],\"output_config\":{\"effort\":\"high\"},"
            + "\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Hello.\"}]}]}";
    }
}
