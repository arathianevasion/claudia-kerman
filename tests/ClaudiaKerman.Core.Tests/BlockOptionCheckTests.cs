// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Script Check's Building Block options (#606): an inline lexicon missing a required option, or naming one the block
    /// doesn't take, fails before anything flies. The options are read from the bundled library's <c>_defaults()</c>.
    /// </summary>
    public sealed class BlockOptionCheckTests
    {
        // Mun flight m20261101-132742-2d0c: the transfer leg's script, which the block refused at once in flight.
        private const string Script2d0c = "local res is transfer_to(lexicon(\"target\", \"Mun\", \"stage_limit\", 3)).\nprint res.\n";

        private static IList<string> Problems(string script)
        {
            return BlockOptionCheck.Problems(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheMissingTargetPeOf2d0cFailsNamingTheBlockTheOptionAndTheOptionList()
        {
            var problem = Assert.Single(Problems(Script2d0c));
            Assert.Equal("line 1: transfer_to: missing required option target_pe; options: target*, target_pe*, window, correction, pe_tolerance (* required), plus the common options", problem);
        }

        [Fact]
        public void TheRetryWithTargetPePasses()
        {
            Assert.Empty(Problems("local res is transfer_to(lexicon(\"target\", \"Mun\", \"target_pe\", 30000, \"stage_limit\", 3)).\n"));
        }

        [Fact]
        public void AnUnknownKeyFailsNamingTheKey()
        {
            var problem = Assert.Single(Problems("transfer_to(lexicon(\"target\",\"Mun\",\"target_pe\",30000,\"pe\",30000))."));
            Assert.StartsWith("line 1: transfer_to: unknown option pe; options: ", problem);
        }

        [Fact]
        public void MissingAndUnknownOnOneCallAreOneProblem()
        {
            var problem = Assert.Single(Problems("coast(lexicon(\"untill\", \"soi\"))."));
            Assert.StartsWith("line 1: coast: missing required option until; unknown option untill; options: until*, lead (* required)", problem);
        }

        [Fact]
        public void TheCommonOptionsPassOnEveryBlock()
        {
            var common = "\"min_ec_pct\", 5, \"max_duration\", 600, \"abort_on_part_loss\", false, \"stage_limit\", 1, \"drop_empty\", list(), "
                + "\"rcs_tolerance\", lexicon(\"on_deg\", 5)";
            foreach (var block in BlockOptionCheck.Blocks)
            {
                var required = string.Concat(block.Value.Where(o => o.Value).Select(o => ", \"" + o.Key + "\", 1").ToArray());
                var script = "local res is " + block.Key + "(lexicon(" + common + required + ")).\n";
                Assert.True(Problems(script).Count == 0, script + "\n" + string.Join("\n", Problems(script).ToArray()));
            }
        }

        [Fact]
        public void KeysAreCaseInsensitiveAsInKos()
        {
            Assert.Empty(Problems("coast(lexicon(\"UNTIL\", \"soi\", \"Lead\", 10))."));
        }

        [Theory]
        [InlineData("local o is lexicon(\"target\", \"Mun\").\ntransfer_to(o).\n")]
        [InlineData("local o is lexicon(\"target\", \"Mun\").\nset o[\"target_pe\"] to 30000.\ntransfer_to(o).\n")]
        [InlineData("local k is \"target_pe\".\ntransfer_to(lexicon(\"target\", \"Mun\", k, 30000)).\n")]
        [InlineData("transfer_to(lexicon(\"target\", \"Mun\", \"target\" + \"_pe\", 30000)).\n")]
        [InlineData("transfer_to().\n")]
        [InlineData("// transfer_to(lexicon(\"target\", \"Mun\")).\nprint \"transfer_to(lexicon())\".\n")]
        [InlineData("print ship:coast(lexicon()).\n")]
        public void FormsItCannotJudgeAreSkipped(string script)
        {
            Assert.Empty(Problems(script));
        }

        [Fact]
        public void NestedCallsInValuesAreReadThrough()
        {
            Assert.Empty(Problems("deorbit(lexicon(\"target\", latlng(1, 2), \"separate\", list(\"a\", \"b,c\")))."));
            var problem = Assert.Single(Problems("land(lexicon(\"target\", latlng(1, 2), \"slope\", 3))."));
            Assert.StartsWith("line 1: land: unknown option slope;", problem);
        }

        [Fact]
        public void TheLineIsTheCallsLineInTheMissionScript()
        {
            var problem = Assert.Single(Problems("print \"go\".\n\ndrop_parts(\n  lexicon()\n).\n"));
            Assert.StartsWith("line 3: drop_parts: missing required option parts;", problem);
        }

        [Fact]
        public void TheBundledLibrarysOwnCallsAreNeverJudged()
        {
            // The library calls blocks with options built in variables and internal keys; only the Mission Script is read.
            Assert.Empty(Problems("print \"hi\".\n"));
            Assert.Empty(Problems(""));
        }

        /// <summary>A guard: the reader finds exactly the required options the shipped library marks "required".</summary>
        [Fact]
        public void EveryBlocksRequiredOptionsReadFromTheShippedLibrary()
        {
            var required = BlockOptionCheck.Blocks
                .Where(b => b.Value.Any(o => o.Value))
                .ToDictionary(b => b.Key, b => string.Join(",", b.Value.Where(o => o.Value).Select(o => o.Key).ToArray()));
            Assert.Equal(new Dictionary<string, string>
            {
                { "change_orbit", "goal" },
                { "coast", "until" },
                { "deorbit", "target" },
                { "drop_parts", "parts" },
                { "transfer_to", "target,target_pe" },
                { "tune_pe", "value" },
            }, required);
            Assert.Equal(CoreLibrary.Blocks.Select(b => b.Name).OrderBy(n => n), BlockOptionCheck.Blocks.Keys.OrderBy(n => n));
            Assert.Equal(new[] { "min_ec_pct", "max_duration", "abort_on_part_loss", "stage_limit", "drop_empty", "rcs_tolerance" }, BlockOptionCheck.CommonOptions);
        }
    }
}
