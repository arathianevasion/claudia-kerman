// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Config
{
    /// <summary>USD per million tokens.</summary>
    public struct ModelPrice
    {
        public decimal InputPerMTok;
        public decimal OutputPerMTok;
        /// <summary>What a cache read costs; a tenth of the input price when not given.</summary>
        public decimal CacheReadPerMTok;

        public ModelPrice(decimal input, decimal output)
            : this(input, output, input * PriceTable.CacheReadMultiplier)
        {
        }

        public ModelPrice(decimal input, decimal output, decimal cacheRead)
        {
            InputPerMTok = input;
            OutputPerMTok = output;
            CacheReadPerMTok = cacheRead;
        }
    }

    /// <summary>
    /// What each model costs, from the player config. A reply's cost is the sum over the attempts in its
    /// <c>usage.iterations</c> (compaction, the message, a fallback model's message), each priced at its own
    /// model's rate; a model that is not in the table is charged at the table's highest price, so cost is
    /// never underestimated. The mod writes every cache breakpoint with the 1-hour TTL, so a cache write
    /// costs twice the input price; a cache read costs the price line's third value, a tenth of the input
    /// price when the line has none. A model may have a second, long-context price for a prompt (uncached
    /// input, cache writes and cache reads together) over <see cref="LongContextTokens"/> tokens.
    /// </summary>
    public sealed class PriceTable
    {
        public const decimal CacheWriteMultiplier = 2m;
        public const decimal CacheReadMultiplier = 0.1m;

        /// <summary>A prompt over this many tokens is charged at the model's long-context price, when it has one.</summary>
        public const long LongContextTokens = 100000;

        /// <summary>A Mission's typical load, for the start form's estimate: about $2.40 on Sonnet 5.5, about $5 on Opus 5.5, about $0.25 on Haiku 5.5 (a Mun landing and return).</summary>
        public const long ReferenceMissionInputTokens = 700000;
        public const long ReferenceMissionOutputTokens = 100000;
        /// <summary>A Mission sends a quarter of the reference load in requests past <see cref="LongContextTokens"/>: a flight crosses it around leg 4 or 5.</summary>
        public const long ReferenceLongShareDivisor = 4;

        private readonly Dictionary<string, ModelPrice> _prices = new Dictionary<string, ModelPrice>();
        private readonly Dictionary<string, ModelPrice> _longPrices = new Dictionary<string, ModelPrice>();

        /// <summary>The shipped table: Sonnet 5.5, Haiku 5.5, Opus 5.5 and the fallback models.</summary>
        public static PriceTable Default()
        {
            var table = new PriceTable();
            table.Set("claude-sonnet-5-5", new ModelPrice(2m, 10m, 0.10m));
            table.Set("claude-haiku-5-5", new ModelPrice(0.10m, 0.50m, 0.01m));
            table.SetLong("claude-haiku-5-5", new ModelPrice(0.50m, 2.50m, 0.05m));
            table.Set("claude-opus-5-5", new ModelPrice(4m, 20m, 0.20m));
            table.Set("claude-sonnet-5", new ModelPrice(2m, 10m, 0.20m));
            table.Set("claude-opus-5", new ModelPrice(5m, 25m));
            table.Set("claude-opus-4-8", new ModelPrice(5m, 25m));
            table.Set("claude-fable-5-1", new ModelPrice(10m, 50m));
            return table;
        }

        public void Set(string model, ModelPrice price)
        {
            _prices[model] = price;
        }

        /// <summary>Sets the price of a prompt over <see cref="LongContextTokens"/> tokens for a model.</summary>
        public void SetLong(string model, ModelPrice price)
        {
            _longPrices[model] = price;
        }

        public IEnumerable<string> Models
        {
            get { return _prices.Keys; }
        }

        public bool Knows(string model)
        {
            return model != null && _prices.ContainsKey(model);
        }

        /// <summary>The model's price for a prompt up to <see cref="LongContextTokens"/>; the table's highest input, output and cache-read price for an unknown model.</summary>
        public ModelPrice PriceOf(string model)
        {
            return PriceOf(model, 0);
        }

        /// <summary>
        /// The model's price for a prompt of this many tokens: its long-context price when it has one and the prompt is over
        /// <see cref="LongContextTokens"/>. An unknown model gets the highest of each price among the table's models at that size.
        /// </summary>
        public ModelPrice PriceOf(string model, long promptTokens)
        {
            if (model != null && _prices.ContainsKey(model)) return Tier(model, promptTokens);
            var all = _prices.Keys.Select(m => Tier(m, promptTokens)).ToList();
            return new ModelPrice(all.Max(p => p.InputPerMTok), all.Max(p => p.OutputPerMTok), all.Max(p => p.CacheReadPerMTok));
        }

        private ModelPrice Tier(string model, long promptTokens)
        {
            ModelPrice longPrice;
            if (promptTokens > LongContextTokens && _longPrices.TryGetValue(model, out longPrice)) return longPrice;
            return _prices[model];
        }

        /// <summary>
        /// The real cost of a reply: each iteration at its own model's rate (the request's model when the API
        /// names none); with no iterations, the reply's top-level totals at the model that produced it.
        /// </summary>
        public decimal CostOf(Usage usage, string model)
        {
            if (usage.Iterations.Count == 0)
                return Cost(model, usage.InputTokens, usage.CacheCreationInputTokens, usage.CacheReadInputTokens, usage.OutputTokens);
            decimal total = 0m;
            foreach (var iteration in usage.Iterations)
                total += Cost(iteration.Model ?? model, iteration.InputTokens, iteration.CacheCreationInputTokens, iteration.CacheReadInputTokens, iteration.OutputTokens);
            return total;
        }

        /// <summary>The most a request could cost: every estimated input token as a cache write, and a full <paramref name="maxTokens"/> of output, at the long-context price when the estimate is over <see cref="LongContextTokens"/>.</summary>
        public decimal WorstCase(string model, long estimatedInputTokens, int maxTokens)
        {
            return Cost(model, 0, estimatedInputTokens, 0, maxTokens);
        }

        /// <summary>What a full <paramref name="maxTokens"/> of output would cost: the part of <see cref="WorstCase"/> a request that never answered cannot have used.</summary>
        public decimal OutputCost(string model, int maxTokens, long promptTokens = 0)
        {
            return maxTokens * PriceOf(model, promptTokens).OutputPerMTok / 1000000m;
        }

        /// <summary>The start form's estimate of a whole Mission on this model: a quarter of the load in requests past <see cref="LongContextTokens"/>.</summary>
        public decimal EstimateMission(string model)
        {
            var longInput = ReferenceMissionInputTokens / ReferenceLongShareDivisor;
            var longOutput = ReferenceMissionOutputTokens / ReferenceLongShareDivisor;
            return Cost(model, ReferenceMissionInputTokens - longInput, 0, 0, ReferenceMissionOutputTokens - longOutput, 0)
                + Cost(model, longInput, 0, 0, longOutput, LongContextTokens + 1);
        }

        private decimal Cost(string model, long input, long cacheWrite, long cacheRead, long output, long promptTokens = -1)
        {
            var price = PriceOf(model, promptTokens >= 0 ? promptTokens : input + cacheWrite + cacheRead);
            return (input * price.InputPerMTok
                + cacheWrite * price.InputPerMTok * CacheWriteMultiplier
                + cacheRead * price.CacheReadPerMTok
                + output * price.OutputPerMTok) / 1000000m;
        }
    }
}
