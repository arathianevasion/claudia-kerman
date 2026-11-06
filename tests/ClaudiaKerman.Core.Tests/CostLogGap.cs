// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// For a Mission test whose Spending Cap sits between two request totals: the totals are read from a dry run's cost log, so the
    /// cap keeps its meaning as the tools list (counted in every request's worst case) grows.
    /// </summary>
    internal static class CostLogGap
    {
        /// <summary>The amounts of a cost log's lines of one event, in order ("pending" is a worst case, "paid" the real cost).</summary>
        public static IList<decimal> Amounts(string costLog, string eventName)
        {
            return costLog.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement)
                .Where(r => r.GetProperty("event").GetString() == eventName)
                .Select(r => r.GetProperty("amount").GetDecimal()).ToList();
        }

        /// <summary>A cap in the middle of the gap a test means, the gap itself asserted so it cannot close unnoticed.</summary>
        public static decimal Between(decimal low, decimal high, string gap)
        {
            Assert.True(low < high, "no gap for a cap that " + gap + ": " + low + " to " + high);
            var cap = Math.Round((low + high) / 2, 4);
            Assert.InRange(cap, low, high - 0.0001m);
            return cap;
        }
    }
}
