// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// reentry logs its own predicted landing against the actual one (#478). The prediction is log-only: a predictor error is
    /// logged and reentry flies on unchanged. The miss distance is Core's (PredictionMissTests).
    /// </summary>
    public class ReentryPredictionTests
    {
        private static string[] Run(string script)
        {
            return KosHarness.Run(CoreLibrary.Bundle(script)).ToArray();
        }

        [Fact]
        public void A_predictor_error_is_logged_with_its_reason_and_says_reentry_flies_on()
        {
            Assert.Equal(new[] { "predicted_landing", "the orbit never reaches the atmosphere", "False", "flying on without a prediction" },
                Run("local n is reentry_prediction_note(lexicon(\"error\", \"the orbit never reaches the atmosphere\")).\n"
                    + "print n:phase. print n:error. print n:haskey(\"predicted_geo\"). print n:note.\n"));
        }

        [Fact]
        public void An_answer_with_no_landing_point_and_no_reason_still_says_why()
        {
            Assert.Equal(new[] { "no landing point" }, Run("print reentry_prediction_note(lexicon()):error.\n"));
        }

        [Fact]
        public void A_prediction_is_logged_as_its_geo_and_landing_time()
        {
            Assert.Equal(new[] { "predicted_landing", "-0.0972", "-74.5577", "5056" },
                Run("local n is reentry_prediction_note(lexicon(\"error\", \"\", \"lat\", -0.09716, \"lng\", -74.55774, \"landing_ut\", 5056.3, \"error_km\", -1)).\n"
                    + "print n:phase. print n:predicted_geo:lat. print n:predicted_geo:lng. print n:landing_ut.\n"));
        }

        [Fact]
        public void A_landing_reports_the_suns_elevation_and_its_docs_say_so()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source;
            var report = source.IndexOf("set extra:sun_elevation_deg to round(90 - vang(ship:up:vector, body(\"Sun\"):position), 1).", System.StringComparison.Ordinal);
            Assert.True(report > source.IndexOf("if fell <> \"\" {", System.StringComparison.Ordinal), "the field is set once the craft is down, past the abort");
            Assert.True(report < source.IndexOf("return claudia_finish(ctx, true, \"\", extra).", System.StringComparison.Ordinal), "and before the block ends");
            Assert.Contains("//| next, else \"skipped out ...\"), shield_dropped, chutes_used, chutes_kept (titles), powered_dv (m/s), sun_elevation_deg, parts_lost.", source);
        }

        [Fact]
        public void The_prediction_only_goes_to_the_log_and_never_into_how_reentry_flies()
        {
            var source = CoreLibrary.Files.Single(f => f.Name == "reentry").Source;
            Assert.Equal(1, source.Split("reentry_prediction_note(predicted)").Length - 1);
            Assert.Equal(1, source.Split("reentry_note_landing(ctx, predicted, last_speed)").Length - 1);
            Assert.DoesNotContain("if predicted", source);
        }
    }
}
