// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>How a request's pending charge came out: paid at its real usage, kept at its worst case, or released.</summary>
    public struct RequestSettlement
    {
        /// <summary>"paid", "kept" or "released".</summary>
        public string Kind;
        /// <summary>The amount the log line carries: the real cost when paid, else the pending charge.</summary>
        public decimal Amount;
        /// <summary>Added to Spent: the real cost when paid.</summary>
        public decimal Spent;
        /// <summary>Added to Held: the input cost kept for a cancelled or lost request that got no response, or its whole worst case when it streamed any output or the input cost is not known.</summary>
        public decimal Held;
        /// <summary>"rejected", "cancelled" or "lost" when not paid; null when paid (the caller says how the reply ended).</summary>
        public string Outcome;
    }

    /// <summary>
    /// What one Craft Check request cost, held by <see cref="CraftCheckFlow"/> until a Mission starts from that check
    /// (it joins the Mission's Spent and cost log) or the check is replaced (it goes to KSP.log only).
    /// </summary>
    internal sealed class CraftCheckCharge
    {
        public string Model = "";
        /// <summary>The worst case recorded before sending.</summary>
        public decimal Pending;
        /// <summary>The input part of <see cref="Pending"/>: all a request that got no response at all keeps.</summary>
        public decimal Input;
        /// <summary>Set when the request ended.</summary>
        public RequestSettlement Settlement;
        /// <summary>The check's earlier tool-loop steps, each its own request, oldest first; this charge is its last.</summary>
        public readonly System.Collections.Generic.List<CraftCheckCharge> Earlier = new System.Collections.Generic.List<CraftCheckCharge>();
    }

    /// <summary>The Spending Cap's pricing and settling rules, shared by a Mission's requests and the Craft Check.</summary>
    public static class RequestCharge
    {
        /// <summary>The most the request could cost: about 3 characters a token, rounded up, so the estimate leans high, plus a full <c>max_tokens</c> of output.</summary>
        public static decimal WorstCase(PriceTable prices, string model, MessagesRequest request)
        {
            return prices.WorstCase(model, EstimatedInputTokens(request), request.MaxTokens);
        }

        private static long EstimatedInputTokens(MessagesRequest request)
        {
            return (request.ToJson().Length + 2) / 3;
        }

        /// <summary>
        /// The part of a request's worst case that is its input: what a request that never answered can still have cost,
        /// the output a full <c>max_tokens</c> would add being released. Never more than <paramref name="worst"/>.
        /// </summary>
        public static decimal InputCost(PriceTable prices, string model, MessagesRequest request, decimal worst)
        {
            return System.Math.Max(0m, worst - prices.OutputCost(model, request.MaxTokens, EstimatedInputTokens(request)));
        }

        /// <summary>
        /// Replaces a request's pending charge with what it really cost (refused, cut-off and off-schema
        /// replies are paid too). A cancelled or lost request that got no response at all keeps only its
        /// input cost (<paramref name="pendingInput"/>; the whole worst case when that is null): the output it could
        /// have billed is released. One that streamed any output (<see cref="StreamResult.OutputStreamed"/>) keeps its full worst case. Only a request that was turned away before any reply began (the key
        /// rejected, no credit, no network, an HTTP error status) is released entirely, because nothing was billed.
        /// </summary>
        public static RequestSettlement Settle(PriceTable prices, decimal pending, StreamResult result, string model, decimal? pendingInput = null)
        {
            if (result != null && result.Status == StreamStatus.Completed && result.Message != null)
            {
                var usage = result.Message.Usage;
                var paid = prices.CostOf(usage, usage.Iterations.Count > 0 ? model : result.Message.Model ?? model);
                return new RequestSettlement { Kind = "paid", Amount = paid, Spent = paid };
            }
            if (result != null && result.Status == StreamStatus.Failed && !MayHaveBilled(result.Failure))
                return new RequestSettlement { Kind = "released", Amount = pending, Outcome = "rejected" };
            // Any streamed output may have been billed, so that request keeps its full worst case (errs safe, #539).
            var streamed = result != null && result.OutputStreamed;
            var kept = pendingInput.HasValue && !streamed ? System.Math.Min(pending, pendingInput.Value) : pending;
            return new RequestSettlement
            {
                Kind = "kept", Amount = kept, Held = kept,
                Outcome = result != null && result.Status == StreamStatus.Cancelled ? "cancelled" : "lost",
            };
        }

        private static bool MayHaveBilled(StreamFailure failure)
        {
            switch (failure)
            {
                case StreamFailure.Unauthorized:
                case StreamFailure.LowCredit:
                case StreamFailure.ModelNotFound:
                case StreamFailure.NoNetwork:
                case StreamFailure.HttpStatus:
                    return false;
                default:
                    return true;
            }
        }
    }
}
