// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The pre-burn refusal rule (#579) on Script Results recorded from real play: 3dc5's three terrain refusals by <c>land</c>
    /// and f623's <c>tune_pe</c> "found no burn" qualify; f623's 31 m/s residual ejection, and its 0.9 m/s exec_node that
    /// left the orbit changed, do not. Each run's start is the report of the run before it, as the transcript has them.
    /// </summary>
    public sealed class PreBurnRefusalTests
    {
        // ---- m20261031-022433-3dc5 (Mun Shot v2, Twin Craters) ----

        private const string LandTail = "\"slope\":-1,\"touchdown_speed_actual\":-1,\"touchdown_h_speed\":-1,\"lean\":-1,\"reversals\":0,\"distance_to_target\":-1,"
            + "\"best_effort\":false,\"burn_start_ut\":-1,\"braking_began_ut\":-1,\"impact_ut\":-1,\"biome\":\"\",\"staged_before_braking\":false,"
            + "\"stage_left\":-1,\"dropped_underhang\":[],\"underhang_kept\":[],\"underhang_drop_agl\":-1,\"passes_missed\":0,\"next_pass_ut\":-1,"
            + "\"descent_shape\":-1,\"descent_pe\":-1,\"predicted_dv\":-1,\"actual_dv\":-1,\"predicted_clearance\":-1,\"moved_for_terrain\":false,\"shape_range\":\"\"}";

        /// <summary>The plane change before the first refusal, at 45 degrees.</summary>
        private const string PlaneChange3dc5 = "{\"success\":true,\"reason\":\"\",\"dv_used\":387.9,\"dv_remaining\":2518.2,\"duration\":1820.2,\"body\":\"Mun\","
            + "\"situation\":\"ORBITING\",\"stage\":3,\"orbit\":{\"ap\":31126,\"pe\":28891,\"inc\":44.99},\"burn_ut\":198621.4,\"inc_actual\":44.9922}";

        private const string TerrainRefusal1 = "{\"success\":false,\"reason\":\"terrain on the approach: no Descent Shape clears it on any pass in the next 48 h "
            + "(no Descent Shape clears the terrain on the approach by 300 m from a periapsis of 1500 to 10313 m; the fall from the deorbit burn passes too low "
            + "over the ground up to 166 km before the site)\",\"dv_used\":0,\"dv_remaining\":2518.2,\"duration\":5.9,\"body\":\"Mun\",\"situation\":\"ORBITING\","
            + "\"stage\":3,\"orbit\":{\"ap\":31125,\"pe\":28891,\"inc\":44.99}," + LandTail;

        private const string TerrainRefusal2 = "{\"success\":false,\"reason\":\"terrain on the approach: no Descent Shape clears it on any pass in the next 48 h "
            + "(no Descent Shape clears the terrain on the approach by 300 m from a periapsis of 1000 to 10388 m; the fall from the deorbit burn passes too low "
            + "over the ground up to 175 km before the site)\",\"dv_used\":0,\"dv_remaining\":2518.2,\"duration\":5.9,\"body\":\"Mun\",\"situation\":\"ORBITING\","
            + "\"stage\":3,\"orbit\":{\"ap\":31125,\"pe\":28891,\"inc\":44.99}," + LandTail;

        /// <summary>The raise to a 78 km orbit before the third refusal.</summary>
        private const string Raise3dc5 = "{\"success\":true,\"reason\":\"\",\"dv_used\":17,\"dv_remaining\":2484,\"duration\":1561.6,\"body\":\"Mun\","
            + "\"situation\":\"ORBITING\",\"stage\":3,\"orbit\":{\"ap\":78797,\"pe\":77986,\"inc\":44.99},\"ecc\":0.00146}";

        private const string TerrainRefusal3 = "{\"success\":false,\"reason\":\"terrain on the approach: no Descent Shape clears it on any pass in the next 24 h "
            + "(no Descent Shape clears the terrain on the approach by 300 m from a periapsis of 3500 to 10957 m; the fall from the deorbit burn passes too low "
            + "over the ground up to 64 km before the site)\",\"dv_used\":0,\"dv_remaining\":2484,\"duration\":5.9,\"body\":\"Mun\",\"situation\":\"ORBITING\","
            + "\"stage\":3,\"orbit\":{\"ap\":78797,\"pe\":77986,\"inc\":44.99}," + LandTail;

        // ---- m20261030-173711-f623 (Mun Shot v2) ----

        /// <summary>The parking orbit's circularisation before the ejection.</summary>
        private const string Circularise623 = "{\"success\":false,\"reason\":\"eccentricity 0.0189 is over tolerance 0.01\",\"dv_used\":1797.7,\"dv_remaining\":3328.7,"
            + "\"duration\":137.5,\"body\":\"Kerbin\",\"situation\":\"ORBITING\",\"stage\":3,\"orbit\":{\"ap\":101216,\"pe\":75215,\"inc\":0.12},\"ecc\":0.01889}";

        private const string ResidualEjection = "{\"success\":false,\"reason\":\"residual 31.61 m/s is over max_residual 0.5 m/s\",\"dv_used\":817,\"dv_remaining\":2465.5,"
            + "\"duration\":368.7,\"body\":\"Kerbin\",\"situation\":\"ORBITING\",\"stage\":3,\"orbit\":{\"ap\":17172958,\"pe\":75530,\"inc\":0.6},\"encounter_ut\":-1,"
            + "\"target_pe_actual\":-1,\"correction_dv\":0,\"ejection_wait\":346,\"ejection_node_dv\":835.837,\"ejection_dv_used\":817,\"ejection_burn_time\":46.6}";

        private const string NoBurnFound = "{\"success\":false,\"reason\":\"found no burn that reaches 30000 m (best is 839183 m off)\",\"dv_used\":0,\"dv_remaining\":2464.8,"
            + "\"duration\":1.1,\"body\":\"Kerbin\",\"situation\":\"ORBITING\",\"stage\":3,\"orbit\":{\"ap\":17173248,\"pe\":75527,\"inc\":0.6},\"pe_actual\":1690010,\"burn_ut\":-1}";

        private const string ResidualNode = "{\"success\":false,\"reason\":\"residual 29.42 m/s is over max_residual 1 m/s\",\"dv_used\":0.9,\"dv_remaining\":2410.9,"
            + "\"duration\":274.2,\"body\":\"Kerbin\",\"situation\":\"SUB_ORBITAL\",\"stage\":3,\"orbit\":{\"ap\":9244284,\"pe\":61484,\"inc\":0.6},\"node_dv\":30.2,"
            + "\"residual\":29.422,\"burn_time\":2.7,\"late_start_s\":0,\"turn_s\":29.2,\"turn_time\":37.6,\"not_aligned\":0}";

        [Theory]
        [InlineData(PlaneChange3dc5, TerrainRefusal1)]
        [InlineData(TerrainRefusal1, TerrainRefusal2)]
        [InlineData(Raise3dc5, TerrainRefusal3)]
        public void Each_of_3dc5s_terrain_refusals_is_one(string before, string run)
        {
            var reason = Judge(before, run);

            Assert.NotNull(reason);
            Assert.StartsWith("terrain on the approach: no Descent Shape clears it", reason);
        }

        [Fact]
        public void F623s_found_no_burn_is_one()
        {
            Assert.Equal("found no burn that reaches 30000 m (best is 839183 m off)", Judge(ResidualEjection, NoBurnFound));
        }

        [Fact]
        public void F623s_31_m_s_residual_ejection_is_not_one()
        {
            Assert.Null(Judge(Circularise623, ResidualEjection));
        }

        [Fact]
        public void F623s_exec_node_under_1_m_s_that_left_the_orbit_changed_is_not_one()
        {
            Assert.Null(Judge(NoBurnFound, ResidualNode));
        }

        [Fact]
        public void A_run_that_succeeded_was_aborted_or_did_not_report_is_not_one()
        {
            var before = OrbitFix.OfReport(Report(PlaneChange3dc5));
            var after = OrbitFix.OfReport(Report(TerrainRefusal1));
            Assert.Null(PreBurnRefusal.Reason(new RunOutcome(RunOutcomeKind.Finished, Report(PlaneChange3dc5), "", "", false), before, before, 2518.2, 2518.2, false));
            Assert.Null(PreBurnRefusal.Reason(new RunOutcome(RunOutcomeKind.Finished, Report(TerrainRefusal1), "", "", true), before, after, 2518.2, 2518.2, false));
            Assert.Null(PreBurnRefusal.Reason(new RunOutcome(RunOutcomeKind.NoReport, Report(TerrainRefusal1), "", "", false), before, after, 2518.2, 2518.2, false));
        }

        [Fact]
        public void A_crossed_Telemetry_Check_rules_it_out_even_recovered()
        {
            var outcome = new RunOutcome(RunOutcomeKind.Finished, Report(TerrainRefusal1), "", "", false);
            var before = OrbitFix.OfReport(Report(PlaneChange3dc5));
            var after = OrbitFix.OfReport(Report(TerrainRefusal1));

            Assert.NotNull(PreBurnRefusal.Reason(outcome, before, after, 2518.2, 2518.2, false));
            Assert.Null(PreBurnRefusal.Reason(outcome, before, after, 2518.2, 2518.2, true));
        }

        [Fact]
        public void Without_dv_used_the_stock_delta_v_decides_within_2_m_s()
        {
            var report = Report(TerrainRefusal1);
            report.Remove("dv_used");
            var outcome = new RunOutcome(RunOutcomeKind.Finished, report, "", "", false);
            var before = OrbitFix.OfReport(Report(PlaneChange3dc5));
            var after = OrbitFix.OfReport(report);

            Assert.NotNull(PreBurnRefusal.Reason(outcome, before, after, 2518.2, 2516.4, false));
            Assert.Null(PreBurnRefusal.Reason(outcome, before, after, 2518.2, 2515.9, false));
            Assert.Null(PreBurnRefusal.Reason(outcome, before, after, null, 2518.2, false));
        }

        [Fact]
        public void The_fact_reads_back_what_it_wrote()
        {
            var fact = PreBurnRefusal.Fact("Land at \"Twin Craters\"", "terrain on the approach");
            string sequence, reason;

            Assert.True(PreBurnRefusal.ReadFact(fact, out sequence, out reason));
            Assert.Equal("Land at \"Twin Craters\"", sequence);
            Assert.Equal("terrain on the approach", reason);
            Assert.False(PreBurnRefusal.ReadFact("The player replied (quoted data): \"go\"", out sequence, out reason));
        }

        /// <summary>The rule on one recorded run: the run before it gives the start (its orbit, and its dv_remaining as the stock delta-v).</summary>
        private static string Judge(string before, string run)
        {
            var start = Report(before);
            var end = Report(run);
            var outcome = new RunOutcome(RunOutcomeKind.Finished, end, "", "", false);
            return PreBurnRefusal.Reason(outcome, OrbitFix.OfReport(start), OrbitFix.OfReport(end),
                PreBurnRefusal.Number(start, "dv_remaining"), PreBurnRefusal.Number(end, "dv_remaining"), false);
        }

        /// <summary>A Script Result as the kOS bridge hands it on: a lexicon of strings, numbers, booleans, lists and lexicons.</summary>
        internal static Dictionary<string, object> Report(string json)
        {
            using (var document = JsonDocument.Parse(json))
                return (Dictionary<string, object>)Value(document.RootElement);
        }

        private static object Value(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    return element.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value));
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(Value).ToList();
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
    }
}
