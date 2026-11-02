// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The run's blocks trail (#609): every block's result, compact, whatever the script reports at the end.</summary>
    public sealed class BlockTrailTests
    {
        private static Dictionary<string, object> Result(bool success, string reason, params (string Key, object Value)[] more)
        {
            var result = new Dictionary<string, object>
            {
                { "success", success }, { "reason", reason }, { "dv_used", 812.4 }, { "duration", 63.0 }, { "body", "Kerbin" },
            };
            foreach (var pair in more) result[pair.Key] = pair.Value;
            return result;
        }

        private static List<JsonElement> Entries(string json)
        {
            return JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }

        [Fact]
        public void AScriptThatReportsItsOwnLexiconAfterTransferToStillListsTheEncounter()
        {
            // 20ac's Sequence Plan 3: the script replaced transfer_to's result with a lexicon of its own.
            var trail = new BlockTrail();
            trail.Add("transfer_to", Result(true, "done", ("encounter_ut", 1203456.0), ("target_pe_actual", 41200.0), ("ejection_wait", 300.0)));
            var report = new Dictionary<string, object> { { "success", false }, { "reason", "transfer ended outside the Mun SOI" } };

            var line = trail.Line(report);

            Assert.StartsWith(BlockTrail.LinePrefix, line);
            var entry = Assert.Single(Entries(trail.Json(report)));
            Assert.Equal("transfer_to", entry.GetProperty("block").GetString());
            Assert.True(entry.GetProperty("success").GetBoolean());
            Assert.Equal("done", entry.GetProperty("reason").GetString());
            Assert.Equal(1203456, entry.GetProperty("encounter_ut").GetDouble());
            Assert.Equal(41200, entry.GetProperty("target_pe_actual").GetDouble());
            // Only the key results: the rest of the block's result stays out.
            Assert.False(entry.TryGetProperty("ejection_wait", out _));
            Assert.False(entry.TryGetProperty("dv_used", out _));
        }

        [Fact]
        public void AnyOtherBlockHasOnlyTheThreeCommonFields()
        {
            var trail = new BlockTrail();
            trail.Add("exec_node", Result(true, "done", ("ecc", 0.01)));

            var entry = Assert.Single(Entries(trail.Json(null)));
            Assert.Equal(new[] { "block", "success", "reason" }, entry.EnumerateObject().Select(p => p.Name));
        }

        [Fact]
        public void EachEntryIsSmall()
        {
            // About 20-40 tokens a block for Haiku: some 3 characters a token.
            var trail = new BlockTrail();
            trail.Add("return", Result(true, "done", ("planet_pe", 32000.0), ("correction_dv", 12.4)));
            Assert.True(trail.Json(null).Length < 120, trail.Json(null));
        }

        [Fact]
        public void TheBlockTheScriptReportedIsNotListedTwice()
        {
            var trail = new BlockTrail();
            trail.Add("launch", Result(true, "done"));
            var circularize = Result(true, "done", ("ecc", 0.002));
            trail.Add("circularize", circularize);

            // Reported as it was, or with more added: the report already says it.
            var asItWas = new Dictionary<string, object>(circularize);
            var withMore = new Dictionary<string, object>(circularize) { { "note", "parking orbit" } };
            Assert.Equal(new[] { "launch" }, Entries(trail.Json(asItWas)).Select(e => e.GetProperty("block").GetString()));
            Assert.Equal(new[] { "launch" }, Entries(trail.Json(withMore)).Select(e => e.GetProperty("block").GetString()));
            // A report that changed any of it is the script's own: the block stays listed.
            var changed = new Dictionary<string, object>(circularize) { ["reason"] = "orbit too low" };
            Assert.Equal(new[] { "launch", "circularize" }, Entries(trail.Json(changed)).Select(e => e.GetProperty("block").GetString()));
        }

        [Fact]
        public void ARunWhoseOnlyBlockIsTheReportHasNoLine()
        {
            var trail = new BlockTrail();
            var result = Result(true, "done");
            trail.Add("launch", result);

            Assert.Equal("", trail.Line(result));
            Assert.Equal("", new BlockTrail().Line(null));
        }

        [Fact]
        public void PastTwelveEntriesTheOldestSuccessfulOnesCollapseIntoACount()
        {
            var trail = new BlockTrail();
            trail.Add("launch", Result(false, "abort min_ec_pct"));
            for (var i = 0; i < 15; i++) trail.Add("exec_node", Result(true, "done " + i));

            var items = Entries(trail.Json(null));

            Assert.Equal(BlockTrail.MaxEntries + 1, items.Count);
            // The failure is kept; the oldest successes went.
            // The collapse line first: it counts the oldest blocks.
            Assert.Equal("4 more blocks succeeded", items[0].GetString());
            Assert.Equal("launch", items[1].GetProperty("block").GetString());
            Assert.Equal("done 4", items[2].GetProperty("reason").GetString());
            Assert.Equal("done 14", items[12].GetProperty("reason").GetString());
        }

        [Fact]
        public void TheCollapseCountsFailuresWhenThereAreMoreThanTwelve()
        {
            var trail = new BlockTrail();
            for (var i = 0; i < 14; i++) trail.Add("exec_node", Result(false, "failed " + i));
            trail.Add("coast", Result(true, "done"));

            var items = Entries(trail.Json(null));

            Assert.Equal(BlockTrail.MaxEntries + 1, items.Count);
            Assert.Equal("3 more blocks: 1 succeeded, 2 failed", items[0].GetString());
            Assert.Equal("failed 2", items[1].GetProperty("reason").GetString());
        }

        [Fact]
        public void ThirteenBlocksWithTheLastReportedListTwelveAndNoCollapseLine()
        {
            var trail = new BlockTrail();
            for (var i = 0; i < 12; i++) trail.Add("exec_node", Result(true, "done " + i));
            var last = Result(true, "last");
            trail.Add("coast", last);

            var items = Entries(trail.Json(last));

            Assert.Equal(12, items.Count);
            Assert.All(items, e => Assert.Equal(JsonValueKind.Object, e.ValueKind));
        }

        [Fact]
        public void ALongReasonIsCut()
        {
            var trail = new BlockTrail();
            trail.Add("land", Result(false, new string('x', 500)));

            var reason = Entries(trail.Json(null))[0].GetProperty("reason").GetString();
            Assert.Equal(BlockTrail.MaxReason + 1, reason.Length);
        }

        [Fact]
        public void TheTrailRoundTripsThroughASave()
        {
            var trail = new BlockTrail();
            for (var i = 0; i < 14; i++) trail.Add("exec_node", Result(true, "done " + i));
            trail.Add("transfer_to", Result(false, "no encounter", ("encounter_ut", -1.0), ("target_pe_actual", -1.0)));

            var again = new BlockTrail();
            again.Restore(trail.ToJson());

            Assert.Equal(trail.Json(null), again.Json(null));
            Assert.Equal("", new BlockTrail().ToJson());
            var broken = new BlockTrail();
            broken.Restore("{not json");
            Assert.Equal(0, broken.Count);
        }
    }
}
