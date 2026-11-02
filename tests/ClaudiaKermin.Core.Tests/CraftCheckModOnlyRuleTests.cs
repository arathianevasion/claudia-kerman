// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>Rules only the mod raises stay out of Claude's prompt and schema, and the prompt does not outgrow main's.</summary>
    public class CraftCheckModOnlyRuleTests
    {
        // CraftCheckPrompts.System.Length on main (366a49d), measured with a temporary test.
        private const int MainPromptLength = 6015;

        [Fact]
        public void The_chute_rules_are_mod_only_and_still_found_with_their_titles()
        {
            foreach (var code in new[] { "chutes_short_for_landing", "chutes_thin_for_landing" })
            {
                var rule = CraftCheckRules.Find(code);
                Assert.NotNull(rule);
                Assert.True(rule.ModOnly);
                Assert.False(string.IsNullOrEmpty(rule.Title));
            }
        }

        [Fact]
        public void Mod_only_rules_are_not_in_Describe_or_the_schema_code_list()
        {
            var described = CraftCheckRules.Describe();
            var schema = CraftCheckRules.SchemaJson();
            foreach (var rule in CraftCheckRules.All.Where(r => r.ModOnly))
            {
                Assert.DoesNotContain(rule.Code, described);
                Assert.DoesNotContain(rule.Code, schema);
            }
            Assert.Contains("delta_v_short", described);
            Assert.Contains("delta_v_short", schema);
        }

        [Fact]
        public void The_Craft_Check_prompt_is_no_longer_than_mains()
        {
            Assert.True(CraftCheckPrompts.System.Length <= MainPromptLength,
                "prompt is " + CraftCheckPrompts.System.Length + " chars, main's is " + MainPromptLength);
        }
    }
}
