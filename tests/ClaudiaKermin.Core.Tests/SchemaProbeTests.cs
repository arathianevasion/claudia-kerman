// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.SchemaProbe;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>The #534 probes build the requests they send; these pin what differs between them, with nothing sent.</summary>
    public class SchemaProbeTests
    {
        [Fact]
        public void TheOneMissionSchemaTakesMissionPlanInKindAndHasNoMissionPlanPart()
        {
            var schema = Probes.MissionSchema();
            Assert.Contains("\"enum\":[\"mission_plan\",\"sequence_plan\",\"debrief\",\"answer\",\"alert\"]", schema);
            Assert.DoesNotContain("\"mission_plan\":{", schema);
            Assert.Equal(3, SchemaComplexity.Unions(schema));
            Assert.Equal(0, SchemaComplexity.OptionalParameters(schema));
        }

        /// <summary>
        /// The schema probed on 2026-10-31, with one change since: #581 replaced the Debrief's goal_checklist (an array of objects)
        /// with goal_steps_done (an array of integers), which only shrinks it (size 80 to 78), so it was not probed again.
        /// </summary>
        [Fact]
        public void TheFrozenSchemaFileIsTheOneMissionSchemaButForGoalStepsDone()
        {
            var file = Path.Combine(FindRepoRoot(), "tools", "ClaudiaKermin.SchemaProbe", "schemas", "534-mission.json");
            var probed = File.ReadAllText(file).Trim()
                .Replace("\"goal_checklist\",", "\"goal_steps_done\",")
                .Replace("\"goal_checklist\":{\"type\":\"array\",\"description\":\"The Mission Goal as its steps, each done or not yet.\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,"
                    + "\"required\":[\"item\",\"done\"],\"properties\":{\"item\":{\"type\":\"string\"},\"done\":{\"type\":\"boolean\"}}}}",
                    "\"goal_steps_done\":{\"type\":\"array\",\"description\":\"The numbers of the Mission Plan's goal_steps done so far, 1 for the first.\",\"items\":{\"type\":\"integer\"}}");
            Assert.Equal(Probes.MissionSchema(), probed);
            Assert.Equal(80, SchemaComplexity.Size(File.ReadAllText(file)));
            Assert.Equal(78, SchemaComplexity.Size(Probes.MissionSchema()));
        }

        [Fact]
        public void TheGrammarProbeAndItsControlDifferOnlyInTheProposeTool()
        {
            var with = Probes.GrammarRequest("m", true);
            var without = Probes.GrammarRequest("m", false);
            Assert.Equal(without.Tools.Select(t => t.Name).Concat(new[] { "propose_mission_plan" }), with.Tools.Select(t => t.Name));
            Assert.All(with.Tools, t => Assert.False(t.Strict));
            Assert.Equal(Probes.MissionPlanPartSchema(), with.Tools.Last().InputSchemaJson);
            Assert.Equal(without.OutputSchemaJson, with.OutputSchemaJson);
            Assert.Equal(16, with.MaxTokens);
            Assert.DoesNotContain("propose_mission_plan", without.ToJson());
        }

        [Fact]
        public void TheCacheProbeChangesOneFieldAtATime()
        {
            var steps = Probes.CacheRequests("m", "long").Select(s => s.Value).ToList();
            var baseline = steps[0].ToJson();
            Assert.Equal(baseline, steps[1].ToJson());
            Assert.All(steps, s => Assert.Equal(16, s.MaxTokens));

            Assert.Equal(baseline.Replace("\"effort\":\"medium\"", "\"effort\":\"high\""), steps[2].ToJson());
            Assert.NotEqual(baseline, steps[2].ToJson());
            Assert.Equal(baseline.Replace("\"tool_choice\":{\"type\":\"auto\"}", "\"tool_choice\":{\"type\":\"none\"}"), steps[3].ToJson());
            Assert.NotEqual(baseline, steps[3].ToJson());

            // The long text is history, not system, and the fifth step changes effort the way a mid-conversation notice does.
            Assert.Contains("\"content\":[{\"type\":\"text\",\"text\":\"long\"", baseline);
            Assert.DoesNotContain("\"role\":\"system\"", baseline);
            Assert.Contains("\"role\":\"system\",\"content\":[],\"output_config\":{\"effort\":\"high\"}", steps[4].ToJson());
        }

        [Fact]
        public void TheCacheProbesTextIsOverTheLongestMinimumCacheableLength()
        {
            // Roughly 4 characters a token: well over 4096 tokens.
            Assert.True(Probes.LongText("n").Length > 40000);
            Assert.NotEqual(Probes.LongText("a"), Probes.LongText("b"));
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
