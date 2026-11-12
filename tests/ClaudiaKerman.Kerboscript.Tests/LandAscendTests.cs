// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// The land and ascend blocks' pure helpers on kOS.Safe, through the real bundle. Expected
    /// values are worked examples (Mun figures: mu 6.5138398e10, radius 200 km, day 138984.48 s).
    /// </summary>
    public class LandAscendTests
    {
        static LandAscendTests()
        {
            KosHarness.Register(typeof(LandSteerRigFunctions));
        }

        private const string MunMu = "6.5138398e10";

        private static string One(string expression)
        {
            return KosHarness.Run(CoreLibrary.Bundle("print " + expression + ".")).Single();
        }

        [Fact]
        public void LandRefusesABodyWithAnAtmosphereNamingReentry()
        {
            Assert.Equal("land flies airless bodies only; Duna has an atmosphere; use reentry", One("land_atmosphere_reason(\"Duna\")"));
        }

        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        // ---- land: options ----

        [Fact]
        public void Land_options_take_the_spec_defaults()
        {
            var printed = Run(
                "local o is claudia_options(lexicon(), land_defaults(), \"auto\"):opt.\n"
                + "print o:target. print o:daylight. print o:max_slope. print o:max_divert.\n"
                + "print o:touchdown_speed. print o:gear_alt. print o:burn_margin. print o:max_duration.\n"
                + "print o:within_hours. print o:max_correction_dv. print o:vessel_offset.");
            Assert.Equal(new[] { "any", "True", "8", "300", "2", "1000", "1.1", "auto", "auto", "auto", "200" }, printed);
        }

        [Theory]
        [InlineData("\"within_hours\", 20", "")]
        [InlineData("\"within_hours\", 0", "within_hours must be \"auto\" or hours from 0.05 to 48")]
        [InlineData("\"within_hours\", 60", "within_hours must be \"auto\" or hours from 0.05 to 48")]
        [InlineData("\"within_hours\", \"long\"", "within_hours must be \"auto\" or hours from 0.05 to 48")]
        [InlineData("\"max_correction_dv\", 45", "")]
        [InlineData("\"max_correction_dv\", -1", "max_correction_dv must be \"auto\" or m/s from 0 to 1000")]
        public void Land_reads_within_hours_and_max_correction_dv_as_auto_or_numbers(string option, string expected)
        {
            Assert.Equal(expected, One("land_unreadable(claudia_options(lexicon(" + option + "), land_defaults(), \"auto\"):opt)"));
        }

        // ---- land: the pass search's reach and window (#510) ----

        [Theory]
        [InlineData("\"auto\"", 1000, "30")]
        [InlineData("\"auto\"", 400, "20")]
        [InlineData("\"auto\"", 0, "30")]
        [InlineData("45", 400, "45")]
        public void Max_correction_dv_auto_is_the_smaller_of_30_and_5_percent_of_the_delta_v_as_find_landing_site_works_it_out(string option, double dv, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_reach_dv({0}, {1})", option, dv)));
            if (option == "\"auto\"") Assert.Equal(double.Parse(expected, CultureInfo.InvariantCulture), Core.Tools.LandingPass.DefaultMaxCorrectionDv(dv), 6);
        }

        [Fact]
        public void The_pass_query_asks_for_the_site_its_periapsis_the_reach_and_the_window_only_when_given()
        {
            var printed = Run(
                "local q is land_pass_query(lexicon(\"lat\", -17.9, \"lng\", 118.9), 63000, 5000, true, 30, \"auto\").\n"
                + "print q:lat. print q:lng. print q:r_pe. print q:earliest_ut. print q:daylight. print q:max_correction_dv. print q:haskey(\"within_hours\").\n"
                + "print land_pass_query(lexicon(\"lat\", 0, \"lng\", 0), 63000, 5000, false, 30, 20):within_hours.");
            Assert.Equal(new[] { "-17.9", "118.9", "63000", "5000", "True", "30", "False", "20" }, printed);
        }

        // ---- land: a missed pass (#510) ----

        // Solving the pass again before each burn: the planned burn, the first pass found now, and the
        // passes missed so far, on a 3700 s orbit that stays circular. Each miss is a warp that overshot
        // the planned burn, so the search finds the pass an orbit and a half on.
        private const string Misses = @"
local period is 3700.
local missed is 0.
local planned is 10000.
local verdict is """".
until false {
  local found is planned + 1.5 * period.
  set verdict to land_pass_verdict(planned, found, true, period, missed, 0).
  print verdict.
  if verdict <> ""replan"" { break. }
  set missed to missed + 1.
  set planned to found.
}
print land_missed_reason(true, """", planned + 1.5 * period + 1800, missed, 0).
";

        [Fact]
        public void A_missed_pass_replans_to_the_next_at_most_twice_then_fails_naming_the_next_pass()
        {
            Assert.Equal(new[] { "replan", "replan", "fail", "missed its pass over the site a third time; the next is at UT 28450 (next_pass_ut)" }, Run(Misses));
        }

        [Fact]
        public void A_pass_solved_again_within_half_an_orbit_of_the_plan_is_the_same_pass()
        {
            Assert.Equal("fly", One("land_pass_verdict(10000, 10004, true, 3700, 0, 0.01)"));
            Assert.Equal("fly", One("land_pass_verdict(10000, 9990, true, 3700, 2, 0.01)"));
        }

        [Fact]
        public void A_missed_pass_fails_when_the_orbit_is_no_longer_near_circular_or_no_pass_is_left()
        {
            Assert.Equal("fail", One("land_pass_verdict(10000, 15550, true, 3700, 0, 0.12)"));
            Assert.Equal("missed its pass over the site and the orbit is no longer near-circular (eccentricity 0.12); the next is at UT 17400 (next_pass_ut)",
                One("land_missed_reason(true, \"\", 17400, 0, 0.12)"));
            Assert.Equal("fail", One("land_pass_verdict(10000, -1, false, 3700, 0, 0)"));
            Assert.Equal("missed its pass over the site and has no other: no pass over the site in the next 11.2 h",
                One("land_missed_reason(false, \"no pass over the site in the next 11.2 h\", -1, 0, 0)"));
        }

        [Fact]
        public void A_missed_pass_has_its_own_results_starting_at_none()
        {
            Assert.Equal(new[] { "0", "-1" }, Run("local x is land_extra().\nprint x:passes_missed. print x:next_pass_ut."));
        }

        [Fact]
        public void The_pass_is_solved_again_600_s_before_the_deorbit_burn()
        {
            Assert.Equal("9400", One("land_resolve_ut(10000)"));
        }

        // ---- land: the default max_duration (#510) ----

        [Fact]
        public void The_default_max_duration_covers_a_planned_descent_longer_than_2400_s()
        {
            // Spike 2a0e: a 3,089 s descent from the deorbit burn on Minmus, a minute's wait, aborted at "ran 2564 s, over
            // 2460 s" under the old wait plus 2400 s. Now the wait, the fall (3,000 s) and the braking (89 s), plus 1200 s.
            Assert.Equal("4349", One("land_max_duration(60, 3000, 89)"));
            Assert.Equal("True", One("land_max_duration(60, 3000, 89) > 60 + 3089"));
            Assert.Equal("4289", One("land_max_duration(-5, 3000, 89)"));
        }

        [Fact]
        public void The_kerboscript_daylight_threshold_agrees_with_find_landing_sites_at_20()
        {
            Assert.Equal(20, Core.Tools.LandingSiteSearch.MinSunElevationDeg);
            Assert.Equal(Core.Tools.LandingSiteSearch.MinSunElevationDeg.ToString(CultureInfo.InvariantCulture), One("land_daylight_sun_deg()"));
        }

        [Fact]
        public void Land_rejects_an_unknown_option_by_name()
        {
            Assert.Equal("unknown option max_slop", One("claudia_options(lexicon(\"max_slop\", 5), land_defaults(), \"auto\"):error"));
        }

        [Fact]
        public void Land_reads_a_target_as_any_or_a_lexicon_of_lat_and_lng()
        {
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"target\", \"any\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"lat\", 1, \"lng\", 2)), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("target must be \"any\", \"vessel\", LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y), lexicon(\"biome\", name) or lexicon(\"poi\", name), not 'somewhere'",
                One("land_unreadable(claudia_options(lexicon(\"target\", \"somewhere\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("target must be \"any\", \"vessel\", LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y), lexicon(\"biome\", name) or lexicon(\"poi\", name), not a lexicon without lat and lng",
                One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"lat\", 1)), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_slope must be a number of degrees from 0 to 45",
                One("land_unreadable(claudia_options(lexicon(\"max_slope\", 60), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("touchdown_speed must be a speed above 0 m/s",
                One("land_unreadable(claudia_options(lexicon(\"touchdown_speed\", 0), land_defaults(), \"auto\"):opt)"));
        }

        // ---- land: beside a landed Target Vessel (#662) ----

        [Theory]
        [InlineData("\"target\", \"vessel\"", "")]
        [InlineData("\"target\", \"vessel\", \"vessel_offset\", 50", "")]
        [InlineData("\"target\", \"vessel\", \"vessel_offset\", 20", "vessel_offset must be metres from 50 to 2000: never on top of the Target Vessel")]
        [InlineData("\"vessel_offset\", \"far\"", "vessel_offset must be metres from 50 to 2000: never on top of the Target Vessel")]
        public void Land_reads_a_vessel_target_and_its_offset_never_under_50_m(string options, string expected)
        {
            Assert.Equal(expected, One("land_unreadable(claudia_options(lexicon(" + options + "), land_defaults(), \"auto\"):opt)"));
        }

        [Theory]
        // 200 m beside it: a site moved 90 m for a slope is still 50 m across the track (0.7 x 200 - 90), so an overshoot misses it.
        [InlineData(300, 200, "90")]
        [InlineData(40, 200, "40")]
        [InlineData(300, 50, "0")]
        public void A_vessel_target_s_divert_keeps_the_site_50_m_across_the_track(double maxDivert, double offset, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "round(land_vessel_divert({0}, {1}), 3)", maxDivert, offset)));
        }

        [Theory]
        // An equatorial orbit's track runs east at the equator: the site is due north, across it.
        [InlineData(0, 22, 0, "0.05730", "22.00000")]
        // A polar orbit's runs north: due east.
        [InlineData(0, 22, 90, "0.00000", "22.05730")]
        // 30 degrees inclined at 10 degrees north: the track runs about 61 degrees from north, within 45 of east, so north.
        [InlineData(10, -179.99, 30, "10.05730", "-179.99000")]
        // 80 degrees inclined: about 10 degrees from north, so east, wrapping the longitude.
        [InlineData(0, 179.99, 80, "0.00000", "-179.95270")]
        public void A_vessel_target_s_site_is_the_offset_across_the_ground_track_from_the_vessel(double lat, double lng, double inc, string siteLat, string siteLng)
        {
            var printed = Run(string.Format(CultureInfo.InvariantCulture,
                "local s is land_beside({0}, {1}, 200, {2}, 200000).\n"
                + "print round(s:lat, 5). print round(s:lng, 5). print round(land_surface_distance({0}, {1}, s:lat, s:lng, 200000), 1).", lat, lng, inc));
            Assert.Equal(double.Parse(siteLat, CultureInfo.InvariantCulture), double.Parse(printed[0], CultureInfo.InvariantCulture), 5);
            Assert.Equal(double.Parse(siteLng, CultureInfo.InvariantCulture), double.Parse(printed[1], CultureInfo.InvariantCulture), 4);
            Assert.Equal("200", printed[2]);
        }

        // ---- land: a biome target (#281) ----

        private static string Unreadable(string target)
        {
            return One("land_unreadable(claudia_options(lexicon(\"target\", " + target + "), land_defaults(), \"auto\"):opt)");
        }

        [Theory]
        [InlineData("lexicon(\"biome\", \"Midlands\")")]
        [InlineData("lexicon(\"BIOME\", \"Northern Basin\")")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 2, \"lng\", -30))")]
        public void Land_reads_a_biome_target_with_an_optional_near_point(string target)
        {
            Assert.Equal("", Unreadable(target));
            Assert.Equal("True", One("land_is_biome(" + target + ")"));
        }

        [Theory]
        [InlineData("lexicon(\"biome\", \"\")", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", \"  \")", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", 3)", "target's biome must be a biome's name, e.g. \"Midlands\"")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"lat\", 1)", "a biome target takes only biome and near, not lat")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 1))", "target's near must be LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y) with numbers")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", lexicon(\"lat\", 1, \"lng\", \"west\"))", "target's near must be LATLNG(lat, lng) or lexicon(\"lat\", x, \"lng\", y) with numbers")]
        [InlineData("lexicon(\"biome\", \"Midlands\", \"near\", \"ksc\")", "target's near must be LATLNG(lat, lng) or a lat/lng lexicon, not a String")]
        public void Land_says_why_a_biome_target_is_unreadable(string target, string expected)
        {
            Assert.Equal(expected, Unreadable(target));
        }

        [Fact]
        public void A_biome_targets_other_options_are_still_read()
        {
            Assert.Equal("max_slope must be a number of degrees from 0 to 45",
                One("land_unreadable(claudia_options(lexicon(\"target\", lexicon(\"biome\", \"Midlands\"), \"max_slope\", 60), land_defaults(), \"auto\"):opt)"));
        }

        [Theory]
        [InlineData("\"any\"")]
        [InlineData("lexicon(\"lat\", 1, \"lng\", 2)")]
        public void A_site_or_any_is_not_a_biome_target(string target)
        {
            Assert.Equal("False", One("land_is_biome(" + target + ")"));
        }

        [Fact]
        public void A_biome_target_asks_the_site_search_for_its_biome_daylight_and_slope_and_leaves_the_window_to_its_default()
        {
            // No within_hours: the search looks as far as find_landing_site does by default (#510: the larger of the time cap
            // and a turn of the body), not 3 orbits.
            var printed = Run(
                "local q is land_site_query(lexicon(\"biome\", \"Midlands\"), true, 8).\n"
                + "print q:biome. print q:daylight. print q:max_slope_deg. print q:haskey(\"within_hours\"). print q:haskey(\"near\"). print q:max_correction_dv.");
            Assert.Equal(new[] { "Midlands", "True", "8", "False", "False", "30" }, printed);
        }

        [Fact]
        public void A_biome_target_passes_on_the_window_and_reach_it_was_given()
        {
            var printed = Run("local q is land_site_query(lexicon(\"biome\", \"Flats\"), true, 8, 20, 12.5).\nprint q:within_hours. print q:max_correction_dv.");
            Assert.Equal(new[] { "20", "12.5" }, printed);
        }

        // ---- land: a Point of Interest target (#713) ----

        [Fact]
        public void Land_reads_a_poi_target_and_the_site_search_finds_its_ring_site()
        {
            Assert.Equal("", Unreadable("lexicon(\"poi\", \"Monolith TMA-8\")"));
            Assert.Equal("True", One("land_is_biome(lexicon(\"poi\", \"Monolith TMA-8\"))"));
            Assert.Equal("poi", One("land_site_kind(lexicon(\"poi\", \"Monolith TMA-8\"))"));
            Assert.Equal("biome", One("land_site_kind(lexicon(\"biome\", \"Flats\"))"));
            var printed = Run("local q is land_site_query(lexicon(\"poi\", \"Monolith TMA-8\"), false, 8).\nprint q:poi. print q:haskey(\"biome\"). print q:daylight.");
            Assert.Equal(new[] { "Monolith TMA-8", "False", "False" }, printed);
        }

        [Theory]
        [InlineData("lexicon(\"poi\", \"\")", "target's poi must be a find_poi name")]
        [InlineData("lexicon(\"poi\", 3)", "target's poi must be a find_poi name")]
        [InlineData("lexicon(\"poi\", \"Monolith TMA-8\", \"near\", lexicon(\"lat\", 1, \"lng\", 2))", "a poi target takes only poi, not near")]
        public void Land_says_why_a_poi_target_is_unreadable(string target, string expected)
        {
            Assert.Equal(expected, Unreadable(target));
        }

        [Fact]
        public void A_poi_the_body_lacks_or_cant_land_beside_is_refused_before_staging()
        {
            Assert.Equal("Monolith TMA-7 is not a landable Point of Interest: buried",
                One("land_unknown_biome(lexicon(\"error\", \"Monolith TMA-7 is not a landable Point of Interest: buried\"))"));
            Assert.Equal("\"x\" names no one Point of Interest on Mun",
                One("land_unknown_biome(lexicon(\"error\", char(34) + \"x\" + char(34) + \" names no one Point of Interest on Mun\"))"));
        }

        [Fact]
        public void The_report_has_settle_s_0_until_a_settle_holds()
        {
            // #865: seconds the final descent held height until Aligned below 10 m; land_descend sets it at touchdown.
            Assert.Equal("0", One("land_extra():settle_s"));
        }

        [Fact]
        public void The_poi_report_fields_start_empty()
        {
            Assert.Equal(new[] { "", "-1" }, Run("local e is land_extra(). print e:poi. print e:distance_to_poi."));
        }

        // ---- land: impact_ut over the terrain on the way down (#383) ----

        [Fact]
        public void The_impact_time_allows_for_terrain_higher_than_the_site()
        {
            // A fall from 6000 m, 30 m down a second, over ground rising from the site's 0 m to 3000 m: it
            // meets the ground at 3000 m (100 s), not at the site's height (200 s).
            const string Fall = "local samples is list().\n"
                + "for t in range(0, 200, 5) { samples:add(lexicon(\"ut\", t, \"alt\", 6000 - 30 * t, \"ground\", min(3000, 40 * t))). }\n";
            Assert.Equal(new[] { "100" }, Run(Fall + "print round(land_impact_ut(samples), 1)."));
            // Ground that stays under the fall: -1.
            Assert.Equal(new[] { "-1" }, Run("print land_impact_ut(list(lexicon(\"ut\", 0, \"alt\", 5000, \"ground\", 0), lexicon(\"ut\", 5, \"alt\", 4000, \"ground\", 100)))."));
        }

        [Fact]
        public void A_biome_targets_near_point_goes_to_the_site_search_as_latitude_and_longitude()
        {
            var printed = Run(
                "local q is land_site_query(lexicon(\"biome\", \"Flats\", \"near\", lexicon(\"lat\", 2.5, \"lng\", -30)), false, 5).\n"
                + "print q:near:latitude. print q:near:longitude. print q:daylight.");
            Assert.Equal(new[] { "2.5", "-30", "False" }, printed);
        }

        [Fact]
        public void A_max_slope_of_0_asks_the_site_search_for_the_flattest_it_takes()
        {
            // land allows max_slope 0; the search wants more than 0.
            Assert.Equal("0.1", One("land_site_query(lexicon(\"biome\", \"Flats\"), false, 0):max_slope_deg"));
        }

        [Fact]
        public void A_Mission_Script_function_named_like_a_library_function_replaces_it()
        {
            // Why the Script Check refuses such a function (ADR 0007): the Mission Script's wins, inside the blocks too.
            Assert.Equal(new[] { "999" }, Run("function land_wrap_lng { parameter x. return 999. }\nprint land_wrap_lng(10)."));
        }

        [Theory]
        [InlineData("Midlands", "midlands", "True")]
        [InlineData("NorthernBasin", "Northern Basin", "True")]
        [InlineData("Highlands", "Midlands", "False")]
        [InlineData("", "Midlands", "False")]
        public void Biome_names_match_ignoring_case_and_spaces(string landed, string asked, string expected)
        {
            Assert.Equal(expected, One("\"" + landed + "\":replace(\" \", \"\") = \"" + asked + "\":replace(\" \", \"\")"));
        }

        // ---- land: a biome's next site after a terrain refusal (#528) ----

        private const string TerrainRefusal = "terrain on the approach: no Descent Shape clears it on any pass in the next 6 h (the ground rises 900 m over the margin)";

        [Theory]
        // A biome site refused for terrain gives way to the next, up to 5 sites.
        [InlineData(true, TerrainRefusal, 1, "True")]
        [InlineData(true, TerrainRefusal, 4, "True")]
        [InlineData(true, TerrainRefusal, 5, "False")]
        // A site given as coordinates moved for terrain already (up to 5 km); it doesn't retry.
        [InlineData(false, TerrainRefusal, 1, "False")]
        // A plan, and the other failures: plane changes (#545), no thrust, no pass in reach. Unknown biomes (#591) fail at the
        // site search, before any pass search.
        [InlineData(true, "", 1, "False")]
        [InlineData(true, "no pass over the site in the next 6 h; plane changes that reach it (fly one as its own step, then land again): change_orbit inc 4", 1, "False")]
        [InlineData(true, "thrust and mass are required", 1, "False")]
        [InlineData(true, "descent_shape 3 doesn't clear the terrain on the approach", 1, "False")]
        public void A_biome_target_takes_its_next_site_only_after_a_terrain_on_the_approach_refusal(bool byBiome, string error, int tried, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_next_site({0}, \"{1}\", {2})", byBiome ? "true" : "false", error, tried)));
        }

        [Fact]
        public void With_every_site_refused_the_failure_says_how_many_of_the_biomes_sites_were_tried()
        {
            Assert.Equal(
                "terrain on the approach at 5 East Crater sites: no Descent Shape clears it on any pass in the next 6 h (the ground rises 900 m over the margin)",
                One("land_sites_refused(\"" + TerrainRefusal + "\", 5, \"East Crater\")"));
            // One site tried (the search had no other) says so; another failure is the reason as it was.
            Assert.StartsWith("terrain on the approach at 1 East Crater site: no Descent Shape clears it",
                One("land_sites_refused(\"" + TerrainRefusal + "\", 1, \"East Crater\")"));
            Assert.Equal("no pass over the site in the next 6 h", One("land_sites_refused(\"no pass over the site in the next 6 h\", 3, \"East Crater\")"));
        }

        [Fact]
        public void A_biome_whose_first_site_is_refused_for_terrain_plans_its_second()
        {
            // land's loop over the biome's sites, with the pass search's verdicts given: the first site refused, the second planned.
            var printed = Run(
                "local verdicts is list(\"" + TerrainRefusal + "\", \"\", \"\").\n"
                + "local tried is list().\n"
                + "local error is \"\".\n"
                + "until false {\n"
                + "  tried:add(tried:length + 1).\n"
                + "  set error to verdicts[tried:length - 1].\n"
                + "  if not land_next_site(true, error, tried:length) { set error to land_sites_refused(error, tried:length, \"Twin Craters\"). break. }\n"
                + "}\n"
                + "print tried:length. print error = \"\".\n"
                + "set tried to list().\n"
                + "until false {\n"
                + "  tried:add(tried:length + 1).\n"
                + "  if not land_next_site(true, \"" + TerrainRefusal + "\", tried:length) { set error to land_sites_refused(\"" + TerrainRefusal + "\", tried:length, \"Twin Craters\"). break. }\n"
                + "}\n"
                + "print tried:length. print error:startswith(\"terrain on the approach at 5 Twin Craters sites: \").");
            Assert.Equal(new[] { "2", "True", "5", "True" }, printed);
        }

        // ---- land: an unknown biome is refused before staging (#622) ----

        [Fact]
        public void An_unknown_biome_is_refused_before_land_stages_and_other_errors_wait_for_the_search()
        {
            // land fails with this text before claudia_stage_into_engines, so the stage count is unchanged.
            const string Unknown = "Crater is not a biome on Mun; its biomes are: East Crater, Midlands";
            Assert.Equal(Unknown, One("land_unknown_biome(lexicon(\"error\", \"" + Unknown + "\"))"));
            Assert.Equal("", One("land_unknown_biome(lexicon(\"error\", \"no site in the Midlands biome on Mun under the next 6 h of ground track\"))"));
            Assert.Equal("", One("land_unknown_biome(lexicon(\"error\", \"\"))"));
        }

        [Fact]
        public void A_biome_with_no_site_says_when_land_has_already_staged()
        {
            const string Advice = "raise max_slope, try another biome or near point, or change the orbit's plane";
            Assert.Equal(Advice + ", or land with daylight false", One("land_no_site_advice(true, false)"));
            Assert.Equal(Advice, One("land_no_site_advice(false, false)"));
            Assert.Equal(Advice + " (land has already staged into its engines, so the stack is not as it was)", One("land_no_site_advice(false, true)"));
        }

        // ---- land: geometry and orbits ----

        [Fact]
        public void Land_measures_distance_along_the_surface()
        {
            Assert.Equal("314159.265", One("round(land_surface_distance(0, 0, 0, 90, 200000), 3)"));
            Assert.Equal("314159.265", One("round(land_surface_distance(-45, 10, 45, 10, 200000), 3)"));
        }

        [Fact]
        public void Land_knows_where_a_site_will_have_turned_to()
        {
            Assert.Equal("19.3248", One("round(land_site_lng(10, 3600, 138984.48), 4)"));
            Assert.Equal("-171.6752", One("round(land_site_lng(179, 3600, 138984.48), 4)"));
            Assert.Equal("170", One("land_wrap_lng(-190)"));
        }

        [Fact]
        public void Land_plans_the_deorbit_burn_by_vis_viva()
        {
            // From a 30 km circular Mun orbit to a 3 km periapsis.
            Assert.Equal("-16.859", One("round(land_deorbit_dv(" + MunMu + ", 230000, sqrt(" + MunMu + " / 230000), 203000), 3)"));
            Assert.Equal("1239.99", One("round(land_transfer_time(" + MunMu + ", 230000, 203000), 2)"));
            Assert.Equal("583.856", One("round(land_periapsis_speed(" + MunMu + ", 230000, 203000), 3)"));
        }

        // #214: land reports when the orbit after its deorbit burn meets the ground, so the block test
        // can check braking starts before it. From the apoapsis of the 230 km by 203 km descent orbit:
        // the ground at 201 km is never reached; 210 km comes before periapsis, half the 1240 s orbit away.
        [Fact]
        public void The_fall_time_to_a_radius_is_shared_and_is_none_for_ground_under_periapsis()
        {
            const string Descent = "0.06235565819861432, 216500, " + MunMu;
            Assert.Equal("-1", One("claudia_fall_eta(" + Descent + ", 201000, 180)"));
            var eta = double.Parse(One("claudia_fall_eta(" + Descent + ", 210000, 180)"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(eta, 800, 1239);
        }

        [Fact]
        public void Land_documents_its_burn_start_and_impact_times()
        {
            var land = CoreLibrary.FullDocs(new[] { "land" });
            Assert.Contains("burn_start_ut (when braking was planned to start", land);
            Assert.Contains("braking_began_ut (when braking began", land);
            Assert.Contains("impact_ut", land);
        }

        // ---- land: descent guidance ----

        [Fact]
        public void Land_starts_braking_at_the_stopping_distance_with_margin()
        {
            Assert.Equal("27500", One("round(land_stop_distance(500, 5, 1.1), 3)"));
        }

        [Theory]
        // Mun Shot v2: stage 3 had 554 m/s against about 600+ m/s of braking need.
        [InlineData(554, 600, "True")]
        // Covers the need but not with the margin (600 * 1.1 = 660).
        [InlineData(650, 600, "True")]
        [InlineData(700, 600, "False")]
        public void Land_stages_a_stage_that_cannot_cover_the_braking_with_margin(double stageDv, double need, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_stage_short({0}, {1}, 1.1)", stageDv, need)));
        }

        [Theory]
        // #546, Mun Shot v2 over Twin Craters: the Swivel stage short of the 957 m/s braking need, 2484 m/s in all, and staging
        // it off drops the whole Underhang: the lander alone brakes, so the search plans with it.
        [InlineData(800, 2484, 957, "true", "True")]
        // The stage covers the braking: it brakes, Underhang and all.
        [InlineData(1100, 2484, 957, "true", "False")]
        // Staging it off would leave part of the Underhang, or is blocked: it stays.
        [InlineData(800, 2484, 957, "false", "False")]
        // Stock reads no delta-v, or the stages below couldn't cover the braking either.
        [InlineData(800, 0, 957, "true", "False")]
        [InlineData(800, 1500, 957, "true", "False")]
        public void Land_plans_with_the_lander_alone_when_the_stage_with_the_Underhang_is_staged_off_before_the_fall(double stageDv, double totalDv, double need, string mayStage, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_plans_staged({0}, {1}, {2}, {3})", stageDv, totalDv, need, mayStage)));
        }

        [Theory]
        // #596, 22cc: Mun Shot v2 in Minmus orbit, the Swivel stage (648 m/s) hanging below the Terrier lander (3,103 m/s), a
        // 419 m/s need (times burn_margin 1.1 = 460.9): the lander alone has it, so the stage goes before the deorbit burn.
        [InlineData("early", "true", 3103, 419, "True")]
        // A lander short of the need, with the margin (450 < 460.9), or well short: it keeps the stack.
        [InlineData("early", "true", 450, 419, "False")]
        [InlineData("early", "true", 0, 419, "False")]
        // Staging off would leave part of the Underhang, or is blocked.
        [InlineData("early", "false", 3103, 419, "False")]
        // "late" keeps today's: the stack brakes and the Underhang drops near the ground.
        [InlineData("late", "true", 3103, 419, "False")]
        public void Land_stages_the_Underhang_off_before_the_deorbit_when_the_lander_alone_has_the_landing(string drop, string mayStage, double landerDv, double need, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture,
                "land_drops_before_deorbit(\"{0}\", {1}, {2}, land_dv_needed({3}, 1.1, -1))", drop, mayStage, landerDv, need)));
        }

        [Fact]
        public void The_stage_that_covers_the_braking_kept_the_stack_before_596()
        {
            // 22cc's figures under #546's rule alone: 648 m/s covers 460.9, so it never staged before the fall.
            Assert.Equal("False", One("land_plans_staged(648, 3751, land_dv_needed(419, 1.1, -1), true)"));
        }

        [Fact]
        public void Drop_underhang_defaults_to_early_and_is_checked()
        {
            Assert.Equal("early", One("claudia_options(lexicon(), land_defaults(), \"auto\"):opt:drop_underhang"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"drop_underhang\", \"late\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("drop_underhang must be \"early\" or \"late\"",
                One("land_unreadable(claudia_options(lexicon(\"drop_underhang\", \"soon\"), land_defaults(), \"auto\"):opt)"));
        }

        [Fact]
        public void Land_decides_the_early_drop_on_the_plans_need_with_burn_margin_and_flies_the_lander()
        {
            var land = CoreLibrary.Files.Single(f => f.Name == "land").Source;
            Assert.Contains("local lander_need is land_dv_needed(plan:predicted_dv, opt:burn_margin, land_deorbit_figures(plan):needed).", land);
            Assert.Contains("if land_drops_before_deorbit(opt:drop_underhang, may_stage_first, under:lander_dv, lander_need) {", land);
            Assert.Contains("set craft to land_lander_craft(land_craft(under, turn, opt), under).", land);
        }

        [Fact]
        public void The_lander_alone_keeps_the_turn_lag_and_margin_and_has_nothing_to_drop()
        {
            var printed = Run(
                "local craft is lexicon(\"thrust\", 215, \"mass\", 12, \"isp\", 320, \"turn_lag\", 7.2, \"burn_margin\", 1.1, \"drop_agl\", 2000,\n"
                + "  \"lander_thrust\", 60, \"lander_mass\", 6.1, \"lander_isp\", 345).\n"
                + "local lander is land_lander_craft(craft, lexicon(\"lander_thrust\", 60, \"lander_mass\", 6.1, \"lander_isp\", 345, \"lander_dv\", 3083)).\n"
                + "print lander:thrust. print lander:mass. print lander:isp. print lander:turn_lag. print lander:burn_margin.\n"
                + "print lander:haskey(\"drop_agl\"). print lander:haskey(\"lander_thrust\"). print craft:thrust.\n"
                // Mun Shot v2's lander, 3083 m/s at Isp 345: 6.1 t / e^(3083 / (345 * 9.80665)) = 2.452 t dry.
                + "print round(lander:dry_mass, 3).\n"
                + "print land_lander_craft(craft, lexicon(\"lander_thrust\", 60, \"lander_mass\", 6.1, \"lander_isp\", 345, \"lander_dv\", 0)):haskey(\"dry_mass\").");
            Assert.Equal(new[] { "60", "6.1", "345", "7.2", "1.1", "False", "False", "215", "2.452", "False" }, printed);
        }

        [Theory]
        // Stage 3, next stage 2 holds engines: fires.
        [InlineData(3, 0, "list(true)", "list(false)", "")]
        // A decoupler-only stage 2 then engines in 1: runs on.
        [InlineData(3, 0, "list(false, true)", "list(false, false)", "")]
        // Next stage fires parachutes or a heat shield.
        [InlineData(3, 0, "list(true)", "list(true)", "unsafe")]
        // The next stage is at stage_limit.
        [InlineData(3, 3, "list()", "list()", "stage_limit")]
        [InlineData(3, 2, "list(true)", "list(false)", "")]
        // No engines below at all.
        [InlineData(2, 0, "list(false, false)", "list(false, false)", "no engines")]
        public void Land_stages_early_only_into_engines_within_the_limit_and_nothing_unsafe(int stageNumber, int limit, string engines, string unsafeBelow, string expected)
        {
            Assert.Equal(expected, string.Join("", Run(string.Format(CultureInfo.InvariantCulture,
                "print land_stage_early_block({0}, {1}, {2}, {3}) + \"|\".", stageNumber, limit, engines, unsafeBelow))).TrimEnd('|'));
        }

        [Fact]
        public void Land_works_out_the_time_to_impact_from_height_and_downward_speed()
        {
            // Dropped from 100 m at 1.62 m/s^2: sqrt(2 * 100 / 1.62).
            Assert.Equal("11.111", One("round(land_fall_eta(100, 0, 1.62), 3)"));
            // Already moving down: 100 = 20 t + 0.81 t^2.
            Assert.Equal("4.264", One("round(land_fall_eta(100, 20, 1.62), 3)"));
            // Climbing away still comes back down, later.
            Assert.Equal("True", One("land_fall_eta(100, -10, 1.62) > land_fall_eta(100, 0, 1.62)"));
            Assert.Equal("0", One("land_fall_eta(0, 5, 1.62)"));
            Assert.Equal("0", One("land_fall_eta(-5, 5, 1.62)"));
        }

        [Theory]
        // Nominal coast: shallow descent, ground far off.
        [InlineData(80, 20, "False")]
        // Over-burned deorbit: 200 m/s down needs 55 s to stop (with margin), ground in 25 s.
        [InlineData(25, 200, "True")]
        // Same fall, but the ground is further than the braking time plus the pad.
        [InlineData(120, 200, "False")]
        // Rising: nothing to stop, but the pad still applies.
        [InlineData(5, -10, "True")]
        [InlineData(0, 0, "True")]
        public void Land_ends_the_coast_when_the_ground_comes_inside_the_braking_time(double eta, double down, string expected)
        {
            Assert.Equal(expected, One(string.Format(CultureInfo.InvariantCulture, "land_brake_now({0}, {1}, 4, 1.1)", eta, down)));
        }

        [Theory]
        // Braking is due at a time to impact of 100 / 4 * 1.1 + 30 = 57.5 s (v_down 100, accel 4, margin 1.1):
        // 4x until 60 s before that, 2x from there, 1x from 30 s before.
        [InlineData(300, 4)]
        [InlineData(120, 4)]
        [InlineData(117, 2)]
        [InlineData(90, 2)]
        [InlineData(87, 1)]
        [InlineData(57, 1)]
        public void Land_warps_at_4x_then_2x_from_60_s_and_1x_from_30_s_before_braking(double eta, int expected)
        {
            Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), One(string.Format(CultureInfo.InvariantCulture, "land_warp_cap({0}, 100, 4, 1.1)", eta)));
        }

        [Fact]
        public void Land_is_at_1x_once_braking_is_due()
        {
            Assert.Equal("1", One("land_warp_cap(10, 100, 4, 1.1)"));
        }

        [Fact]
        public void Land_fails_after_the_deorbit_burn_when_periapsis_ends_well_under_the_plan()
        {
            // Planned 3200 m; 1500 m of slack.
            Assert.Equal("True", One("land_periapsis_ok(3100, 3200)"));
            Assert.Equal("True", One("land_periapsis_ok(1700, 3200)"));
            Assert.Equal("False", One("land_periapsis_ok(1600, 3200)"));
            Assert.Equal("False", One("land_periapsis_ok(-4000, 3200)"));
            // Higher than planned is fine for this check.
            Assert.Equal("True", One("land_periapsis_ok(9000, 3200)"));
        }

        [Fact]
        public void Land_closes_on_the_site_along_a_stopping_curve_then_gently()
        {
            // sqrt(2 a s + (2 a)^2) - 2 a, a = 4 / margin: the stopping curve, eased onto the site.
            Assert.Equal("81.8", One("round(land_vh_target(1000, 4, 1), 3)"));
            Assert.Equal("59.372", One("round(land_vh_target(1000, 4, 2), 3)"));
            Assert.Equal("4", One("round(land_vh_target(10, 4, 1), 3)"));
            // About half the distance per second at the last few metres.
            Assert.Equal("0.485", One("round(land_vh_target(1, 4, 1), 3)"));
            Assert.Equal("0", One("land_vh_target(0, 4, 1)"));
        }

        [Fact]
        public void Land_descends_no_faster_than_it_can_stop_and_touches_down_at_touchdown_speed()
        {
            // Far above the approach height: limited by what it can stop from (2 m/s^2 over 1000 m, by land_h_end over the
            // ground after 2 s of reaction: land_sink_limit, #547; it was the 63.246 that stops at the ground).
            Assert.Equal("-52.71", One("round(land_vv_target(1000, 0, 2, 2), 3)"));
            // At the ground: the touchdown speed.
            Assert.Equal("-2", One("land_vv_target(1, 0, 2, 2)"));
            Assert.Equal("-2", One("land_vv_target(100, 90, 2, 2)"));
            // Below the approach height (terrain rising): climbs, at most 20 m/s.
            Assert.Equal("20", One("land_vv_target(500, 1000, 2, 2)"));
            Assert.Equal("5", One("land_vv_target(950, 1000, 2, 2)"));
            // However high and however hard it can stop: never over 100 m/s down (#365's ran to 240).
            Assert.Equal("-100", One("land_vv_target(20000, 0, 5, 2)"));
        }

        [Fact]
        public void Lands_sink_limit_stops_the_descent_by_h_end_over_the_ground_after_2_s_of_reaction()
        {
            // #547: 1000 m over the ground at 2 m/s^2: 2 s at the speed, then stopping, uses the 800 m down to land_h_end.
            Assert.Equal("52.71", One("round(land_sink_limit(1000, 2), 3)"));
            Assert.Equal("800", One("round(2 * land_sink_limit(1000, 2) + land_sink_limit(1000, 2) ^ 2 / (2 * 2), 6)"));
            // Under twice h_end it stops by half the height, so the terminal descent still reaches the ground; nothing at it.
            Assert.Equal("150", One("round(2 * land_sink_limit(300, 2) + land_sink_limit(300, 2) ^ 2 / (2 * 2), 6)"));
            Assert.Equal("0", One("land_sink_limit(0, 2)"));
            Assert.Equal("0", One("land_sink_limit(-50, 2)"));
            // A slower turn reacts later: Mun Shot v2's lander on the stack's 7.3 s turn lag, the 800 m used the same way; never
            // under the 2 s.
            Assert.Equal("800", One("round(7.3 * land_sink_limit(1000, 2, 7.3) + land_sink_limit(1000, 2, 7.3) ^ 2 / (2 * 2), 6)"));
            Assert.Equal("True", One("land_sink_limit(1000, 2, 7.3) < land_sink_limit(1000, 2) and land_sink_limit(1000, 2, 1) = land_sink_limit(1000, 2)"));
            // More height, or more to stop with, allows more.
            Assert.Equal("True", One("land_sink_limit(2000, 2) > land_sink_limit(1000, 2) and land_sink_limit(1000, 4) > land_sink_limit(1000, 2)"));
            // The C# port BRAKEPATH predicts with.
            foreach (var c in new[] { new[] { 1000, 2.0, 2 }, new[] { 300, 2.0, 2 }, new[] { 2697, 3.37, 2.6 }, new[] { 50, 6.0, 1 }, new[] { 0, 2.0, 2 }, new[] { 1000, 2.0, 7.3 } })
                Assert.Equal(double.Parse(One(string.Format(CultureInfo.InvariantCulture, "land_sink_limit({0}, {1}, {2})", c[0], c[1], c[2])), CultureInfo.InvariantCulture),
                    Core.Tools.BrakeGuidance.SinkLimit(c[0], c[1], c[2]), 9);
        }

        [Fact]
        public void Braking_far_over_its_descent_shape_near_the_site_is_seen_and_braking_on_its_shape_isnt()
        {
            // #547, m20261031-022433-3dc5: 321 m out at 63.6 m/s, 2697 m up with h_ref 216 and vv_want -100. At 100 m/s it loses
            // 1 km in the 10 s it has left across the ground, not the 2.5 km it is over its shape.
            Assert.Equal("True", One("land_over_shape(2697, 216, 321, 63.6, 100)"));
            // 2.8 km out at 218 m/s, 3 km over its shape: 26 s left, 2.6 km at 100 m/s.
            Assert.Equal("True", One("land_over_shape(3476, 490, 2842, 218.1, 100)"));
            // 10.6 km out at 426 m/s, 2.3 km over: 50 s left, time enough.
            Assert.Equal("False", One("land_over_shape(3860, 1593, 10584, 425.6, 100)"));
            // Where braking began, under its shape; on it, nearer in; and the terminal descent, which has no shape left.
            Assert.Equal("False", One("land_over_shape(3206, 3310, 21870, 599.5, 100)"));
            Assert.Equal("False", One("land_over_shape(450, 400, 1500, 90, 40)"));
            Assert.Equal("False", One("land_over_shape(2000, 0, 10, 0.5, 100)"));
        }

        [Fact]
        public void The_descent_shape_runs_from_h_start_at_the_starting_speed_to_h_end_stopped_and_a_smaller_shape_holds_higher()
        {
            // #511: h_ref = h_end + (h_start - h_end) (v_h / v0)^shape.
            Assert.Equal("3000", One("land_shape_height(575, 575, 3000, 200, 0.5)"));
            Assert.Equal("3000", One("land_shape_height(575, 575, 3000, 200, 3)"));
            Assert.Equal("200", One("land_shape_height(0, 575, 3000, 200, 0.5)"));
            Assert.Equal("200", One("land_shape_height(0, 575, 3000, 200, 3)"));
            Assert.Equal("1600", One("land_shape_height(287.5, 575, 3000, 200, 1)"));
            foreach (var v in new[] { 1.0, 50, 200, 400, 574 })
            {
                const string Higher = "land_shape_height({0}, 575, 3000, 200, {1}) > land_shape_height({0}, 575, 3000, 200, {2})";
                Assert.Equal("True", One(string.Format(CultureInfo.InvariantCulture, Higher, v, 0.4, 1.2)));
                Assert.Equal("True", One(string.Format(CultureInfo.InvariantCulture, Higher, v, 1.2, 3)));
                Assert.Equal(double.Parse(One(string.Format(CultureInfo.InvariantCulture, "land_shape_height({0}, 575, 3000, 200, 0.7)", v)), CultureInfo.InvariantCulture),
                    Core.Tools.BrakeGuidance.ShapeHeight(v, 575, 3000, 200, 0.7), 6);
            }
            // The descent holds it to the site, ending 200 m up, and comes down only within 30 m of it.
            Assert.Equal("200", One("land_shape_ref(600, 0, 575, 3000, 1)"));
            Assert.Equal("0", One("land_shape_ref(20, 0, 575, 3000, 1)"));
            Assert.Equal("200", One("land_h_end()"));
        }

        // ---- land: the Descent Shape's plan, report and recovery (#511) ----

        [Fact]
        public void Land_takes_descent_shape_auto_or_a_number_and_reports_the_descent()
        {
            Assert.Equal("auto", One("claudia_options(lexicon(), land_defaults(), \"auto\"):opt:descent_shape"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"descent_shape\", 0.4), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("descent_shape must be \"auto\" or a Descent Shape from 0.05 to 10 (smaller is steeper)",
                One("land_unreadable(claudia_options(lexicon(\"descent_shape\", \"steep\"), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("descent_shape must be \"auto\" or a Descent Shape from 0.05 to 10 (smaller is steeper)",
                One("land_unreadable(claudia_options(lexicon(\"descent_shape\", 0), land_defaults(), \"auto\"):opt)"));
            Assert.Equal(new[] { "-1", "-1", "-1", "-1", "-1", "False", "", "False" }, Run("local x is land_extra().\n"
                + "print x:descent_shape. print x:descent_pe. print x:predicted_dv. print x:actual_dv. print x:predicted_clearance. print x:moved_for_terrain. print x:shape_range."
                + " print x:vertical_fallback."));
            var land = CoreLibrary.FullDocs(new[] { "land" });
            foreach (var field in new[] { "descent_shape [\"auto\"]", "descent_pe", "predicted_dv", "actual_dv", "predicted_clearance", "moved_for_terrain", "shape_range",
                "retry with a smaller shape", "terrain on the approach", "above its predicted path", "vertical_fallback" })
                Assert.Contains(field, land);
        }

        [Fact]
        public void The_pass_query_carries_the_craft_the_shape_and_whether_the_site_may_move()
        {
            var printed = Run("local q is land_pass_query(lexicon(\"lat\", 1, \"lng\", 2), 203000, 5000, false, 30, \"auto\", lexicon(\"thrust\", 60, \"mass\", 5.4), 0.5, true).\n"
                + "print q:thrust. print q:mass. print q:shape. print q:move_site.\n"
                + "local plain is land_pass_query(lexicon(\"lat\", 1, \"lng\", 2), 203000, 5000, false, 30, \"auto\").\n"
                + "print plain:shape. print plain:move_site. print plain:haskey(\"thrust\").");
            Assert.Equal(new[] { "60", "5.4", "0.5", "True", "auto", "False", "False" }, printed);
        }

        [Fact]
        public void A_site_out_of_reach_fails_before_any_burn_naming_the_plane_changes_and_land_never_flies_one()
        {
            // #521: the site's pass search asks LANDINGPASS for the plane changes, whose error the block fails with before any burn;
            // the docs say to fly the change_orbit first, and the block never calls it.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ClaudiaKerman.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "kerboscript", "library", "land.ks"));
            Assert.Contains("site_query:add(\"plane_changes\", true).", source);
            Assert.DoesNotContain("change_orbit(", string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//"))));
            var docs = CoreLibrary.FullDocs(new[] { "land" });
            Assert.Contains("fly a change_orbit from find_landing_site's plane_changes first, as its own step, and budget its total_dv", docs);
            Assert.Contains("the change_orbit plane changes that reach the site, with their delta-v", docs);
        }

        [Fact]
        public void The_delta_v_check_and_the_staging_use_the_simulated_figures_times_burn_margin()
        {
            Assert.Equal("825", One("round(land_dv_needed(750, 1.1, 1500))"));
            Assert.Equal("1500", One("land_dv_needed(-1, 1.1, 1500)"));
            Assert.Equal("770", One("round(land_dv_needed(700, 1.1, 1400))"));
            Assert.Equal("1400", One("land_dv_needed(-1, 1.1, 1400)"));
            Assert.Equal("0.2 to 1.164", One("land_shape_range(0.2, 1.1643)"));
            Assert.Equal("", One("land_shape_range(-1, -1)"));
        }

        [Fact]
        public void The_prediction_as_braking_begins_flies_switches_to_a_steeper_shape_or_doesnt_brake()
        {
            Assert.Equal("fly", One("land_repredict_verdict(true, -1, true)"));
            // The planned shape no longer clears: the cheapest that does (BRAKEPATH's "auto").
            Assert.Equal("switch", One("land_repredict_verdict(false, 0.38, true)"));
            // None clears and periapsis is over the terrain on the track: no braking, the orbit kept.
            Assert.Equal("hold", One("land_repredict_verdict(false, -1, true)"));
            // None clears but the orbit meets the terrain: braking on the steepest shape rather than falling into it.
            Assert.Equal("steepest", One("land_repredict_verdict(false, -1, false)"));
        }

        [Fact]
        public void An_abort_under_the_terrain_with_the_delta_v_and_a_pass_left_circularizes_and_otherwise_leaves_the_orbit()
        {
            Assert.Equal("circularize", One("land_recover(true, 900, 120, true)"));
            Assert.Equal("leave", One("land_recover(false, 900, 120, true)"));
            Assert.Equal("leave", One("land_recover(true, 100, 120, true)"));
            Assert.Equal("leave", One("land_recover(true, 900, 120, false)"));
            Assert.Equal("leave", One("land_recover(true, -1, 120, true)"));
            // 3 km up at the Mun at 400 m/s and 13 m/s up: the circular 566.5 m/s there is 166.5 m/s on, and the climb stopped.
            Assert.Equal("167", One("round(land_circular_dv(" + MunMu + ", 203000, 400, 13), 1)"));
            Assert.Equal("0", One("round(land_circular_dv(" + MunMu + ", 203000, sqrt(" + MunMu + " / 203000), 0), 6)"));
        }

        [Fact]
        public void The_orbit_after_an_abort_clears_the_bodys_highest_terrain_climbing_to_it_if_need_be()
        {
            // #530: on Minmus Flats the abort circularized at 2650 m, over the 1.6 km of ground on the approach but under Minmus's
            // 5.7 km peaks; the next block flew into them. Now it recovers to the highest terrain plus the path's margin.
            Assert.Equal("6025", One("land_recover_alt(2650, 5725)"));
            Assert.Equal("8000", One("land_recover_alt(8000, 5725)"));
            // It climbs at a tenth of the height still to gain a second, at most 20 m/s, and not at all once there.
            Assert.Equal("20", One("land_recover_climb(3375)"));
            Assert.Equal("5", One("land_recover_climb(50)"));
            Assert.Equal("0", One("land_recover_climb(-10)"));
            // From 50.1 m/s at 2650 m on Minmus: circular at 6025 m (163.6 m/s) and the 20 m/s climb gained and lost, 153 m/s
            // against the 467 m/s it had left. Already over it: the circular burn alone, as before.
            Assert.Equal("153", One("round(land_recover_dv(1.7658e9, 60000, 2650, 6025, 50.1, 0))"));
            Assert.Equal(One("round(land_circular_dv(" + MunMu + ", 208000, 400, 13), 3)"), One("round(land_recover_dv(" + MunMu + ", 200000, 8000, 8000, 400, 13), 3)"));
        }

        [Fact]
        public void The_turn_lag_the_simulation_flies_is_the_time_to_turn_20_degrees_and_at_least_1_s()
        {
            Assert.Equal("1", One("land_turn_lag(0, 0)"));
            Assert.Equal(Core.Tools.BrakePath.TurnLag(1.9, 102.5).ToString("0.###", CultureInfo.InvariantCulture), One("round(land_turn_lag(1.9, 102.5), 3)"));
        }

        [Fact]
        public void The_path_keeps_300_m_or_2_s_of_descent_over_the_terrain()
        {
            Assert.Equal("300", One("land_clear_margin(40)"));
            Assert.Equal("300", One("land_clear_margin(-13)"));
            Assert.Equal("400", One("land_clear_margin(200)"));
        }

        [Fact]
        public void Land_gives_vertical_acceleration_first()
        {
            Assert.Equal(new[] { "6", "8" }, Run("local s is land_split_accel(10, 6, 10). print s:v. print round(s:h, 3)."));
            Assert.Equal(new[] { "10", "0" }, Run("local s is land_split_accel(10, 12, 5). print s:v. print round(s:h, 3)."));
            Assert.Equal(new[] { "0", "4" }, Run("local s is land_split_accel(10, -3, 4). print s:v. print round(s:h, 3)."));
        }

        // ---- land: braking once (#233) ----

        [Fact]
        public void Land_takes_an_overshoot_tolerance_in_metres()
        {
            Assert.Equal("500", One("claudia_options(lexicon(), land_defaults(), \"auto\"):opt:max_overshoot"));
            Assert.Equal("", One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", 50), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_overshoot must be metres from 0 to 10000",
                One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", -1), land_defaults(), \"auto\"):opt)"));
            Assert.Equal("max_overshoot must be metres from 0 to 10000",
                One("land_unreadable(claudia_options(lexicon(\"max_overshoot\", \"near\"), land_defaults(), \"auto\"):opt)"));
            var land = CoreLibrary.FullDocs(new[] { "land" });
            foreach (var field in new[] { "max_overshoot [500]", "reversals", "touchdown_h_speed", "lean" })
            {
                Assert.Contains(field, land);
            }
        }

        [Fact]
        public void Land_brakes_along_its_stopping_curve_and_eases_onto_the_site()
        {
            // On land_vh_target's curve (4 m/s^2 at margin 1, 1000 m out: 81.8 m/s): the curve's own
            // deceleration, 4 v / (v + 8), never over the 4 m/s^2 it plans for.
            Assert.Equal("-3.644", One("round(land_axis_accel(1000, land_vh_target(1000, 4, 1), 4, 1), 3)"));
            // Easing off near the site: 10 m out at 4 m/s.
            Assert.Equal("-1.333", One("round(land_axis_accel(10, 4, 4, 1), 3)"));
            // Too fast to stop on the site: everything it has; it lands long rather than turning back.
            Assert.Equal("-4", One("round(land_axis_accel(1000, 200, 4, 1), 3)"));
            // Stopped short: closes on the site.
            Assert.Equal("4", One("round(land_axis_accel(1000, 0, 4, 1), 3)"));
        }

        [Fact]
        public void Land_only_stops_once_past_the_site()
        {
            // Past the site and still moving on: brakes, never pushes back towards the site.
            Assert.Equal("-4", One("round(land_axis_accel(-50, 20, 4, 1), 3)"));
            // Slowly (over 4 s) as it nears a stop, so a lagging turn doesn't carry it through zero.
            Assert.Equal("-0.25", One("round(land_axis_accel(-50, 1, 4, 1), 3)"));
            Assert.Equal("0", One("land_axis_accel(-50, 0, 4, 1)"));
            // Drifting back towards it: pushed the way it was going until stopped.
            Assert.Equal("0.125", One("round(land_axis_accel(-50, -0.5, 4, 1), 3)"));
        }

        [Fact]
        public void Land_closes_across_its_path_from_either_side()
        {
            Assert.Equal(One("land_axis_accel(300, 0, 2, 1.1)"), One("land_cross_accel(300, 0, 2, 1.1)"));
            Assert.Equal(One("-land_axis_accel(300, 0, 2, 1.1)"), One("land_cross_accel(-300, 0, 2, 1.1)"));
            // Moving away from the site across the path: turns that motion round.
            Assert.Equal("True", One("land_cross_accel(-300, 5, 2, 1.1) < 0"));
        }

        [Fact]
        public void Land_counts_a_reversal_only_past_1_m_per_s_of_jitter()
        {
            Assert.Equal("False", One("land_is_reversed(-0.9, false)"));
            Assert.Equal("True", One("land_is_reversed(-1.1, false)"));
            // Once reversed it stays so until moving ahead again, so one swing back counts once.
            Assert.Equal("True", One("land_is_reversed(-0.5, true)"));
            Assert.Equal("False", One("land_is_reversed(0, true)"));
        }

        // ---- land: the site along the surface, and the braking guard (#431) ----

        // land_site_figures from a craft on the Mun's equator at lng 0 heading east: dist, along, across (m).
        private static string[] Figures(double siteLat, double siteLng, double hdg = 90)
        {
            return Run(string.Format(CultureInfo.InvariantCulture,
                "local f is land_site_figures(0, 0, {0}, {1}, {2}, 200000). print round(f:dist, 1). print round(f:along, 1). print round(f:across, 1).",
                siteLat, siteLng, hdg));
        }

        [Fact]
        public void Land_measures_the_site_along_and_across_its_track_on_the_surface()
        {
            // 1 degree of the Mun's surface is 3490.7 m. North of an eastward track is to the left (negative).
            Assert.Equal(new[] { "3508.1", "3490.7", "-349.1" }, Figures(0.1, 1));
            Assert.Equal(new[] { "3490.7", "-3490.7", "0" }, Figures(0, -1));
            // A quarter of the way round, 10 degrees south of the track: along the track 90 degrees, 10 off it.
            Assert.Equal(new[] { "314159.3", "314159.3", "34906.6" }, Figures(-10, 90));
            // The far side, 175 degrees round and 1 degree north: the straight line flattened onto the
            // craft's horizon would say about 17 km. (Off the track converges toward the antipode: cos dist =
            // cos along x cos across.)
            Assert.Equal(new[] { "610520.5", "610865.2", "-3490.7" }, Figures(1, 175));
            Assert.Equal("-610865.2", Figures(0, -175)[1]);
            // Heading north, a site due east is across, to the right.
            Assert.Equal(new[] { "3490.7", "0", "3490.7" }, Figures(0, 1, 0));
        }

        [Fact]
        public void Land_bears_from_north_clockwise()
        {
            Assert.Equal("90", One("round(land_bearing(0, 0, 0, 10), 3)"));
            Assert.Equal("0", One("round(land_bearing(0, 0, 10, 0), 3)"));
            Assert.Equal("270", One("round(land_bearing(0, 0, 0, -10), 3)"));
            Assert.Equal("180", One("round(land_bearing(10, 30, -10, 30), 3)"));
        }

        [Fact]
        public void With_no_correction_burn_braking_is_not_due_at_the_deorbit_point_but_near_the_site()
        {
            // Samples from the deorbit burn on the Mun's equator, 26.9 km up and 507 m/s, falling half an orbit
            // to periapsis 3 km up over a site 175 degrees ahead (and 0.5 north) at 1240 s, as in #431's mission.
            var printed = Run(@"
local a_brake is land_brake_accel(60000 / 5400, 1.63).
local samples is list().
local t is 0.
until t > 1300 {
  local f is land_site_figures(0, 175 * t / 1240, 0.5, 175, 90, 200000).
  samples:add(lexicon(""ut"", t, ""dist"", f:dist, ""ahead"", f:along, ""v_h"", 507 + 63 * t / 1240,
    ""v_down"", 11950 * sin(180 * t / 1240) * 3.141592653589793 / 1240, ""alt"", 3000 + 11950 * (1 + cos(180 * t / 1240)), ""ground"", 0)).
  set t to t + 5.
}
local p is samples[0].
print land_brake_due(p:dist, p:ahead, p:v_h, p:v_down, p:alt - p:ground, a_brake, 1.1, 0).
print land_brake_estimate(samples, a_brake, 1.1).");
            Assert.Equal("False", printed[0]);
            // Braking comes due some 30 s before the site, not at the first sample.
            Assert.InRange(double.Parse(printed[1], CultureInfo.InvariantCulture), 1150, 1240);
        }

        [Fact]
        public void Braking_that_raises_periapsis_trips_the_guard()
        {
            // Speed is not an input (#434): a fall alone gains speed with periapsis steady, so nothing trips.
            Assert.Equal("", One("land_brake_guard(4900, 4900)"));
            // Periapsis falling: no trip.
            Assert.Equal("", One("land_brake_guard(4900, -20000)"));
            Assert.Equal("", One("land_brake_guard(4900, 4950)"));
            Assert.Equal("braking raised periapsis: 6.2 km, 4.9 when braking began", One("land_brake_guard(4900, 6200)"));
            Assert.Equal("", One("land_brake_guard(4900, 5899)"));
            Assert.Equal("braking raised periapsis: 5.9 km, 4.9 when braking began", One("land_brake_guard(4900, 5900)"));
            // Below sea level reads negative.
            Assert.Equal("", One("land_brake_guard(-3000, -3500)"));
            Assert.Equal("", One("land_brake_guard(-3000, -2001)"));
            Assert.Equal("braking raised periapsis: -2 km, -3 when braking began", One("land_brake_guard(-3000, -2000)"));
            Assert.Equal("braking raised periapsis: 0.5 km, -1.5 when braking began", One("land_brake_guard(-1500, 500)"));
        }

        // A predicted path (land_path_at's: s m to the site, alt and ground m above sea level) over a 2 km ridge 30 km out, then a
        // basin 1.5 km under the plain, then the site on the plain: the path climbs 1.5 km over the ridge and drops into the basin.
        private const string RidgeThenBasin = "local pred is list("
            + "lexicon(\"s\", 40000, \"alt\", 3000, \"ground\", 0), lexicon(\"s\", 30000, \"alt\", 3500, \"ground\", 2000), "
            + "lexicon(\"s\", 20000, \"alt\", 1500, \"ground\", -1500), lexicon(\"s\", 10000, \"alt\", 800, \"ground\", 0), "
            + "lexicon(\"s\", 0, \"alt\", 200, \"ground\", 0)).\n";

        [Fact]
        public void The_predicted_path_is_read_where_the_craft_is_along_the_track()
        {
            // #511: the path guard (Core's LandGuard since #712, its cases in LandGuardTests) reads the prediction there.
            var printed = Run(RidgeThenBasin
                + "print round(land_path_at(pred, 30000):alt). print round(land_path_at(pred, 20000):ground).\n"
                + "print round(land_path_at(pred, 25000):alt). print round(land_path_at(pred, 45000):alt). print round(land_path_at(pred, -500):alt).\n"
                + "print land_path_at(list(), 100).");
            Assert.Equal(new[] { "3500", "-1500", "2500", "3000", "200", "" }, printed);
        }

        [Fact]
        public void The_descent_steers_by_the_highest_ground_on_the_rest_of_the_predicted_path()
        {
            Assert.Equal(new[] { "2000", "0", "-1000000000" }, Run(RidgeThenBasin
                + "print land_path_ground_ahead(pred, 35000). print land_path_ground_ahead(pred, 15000). print land_path_ground_ahead(pred, 0)."));
        }

        [Fact]
        public void The_descent_stages_before_guidance_only_when_braking_dry_with_no_Underhang_pending()
        {
            Assert.Equal(new[] { "True", "False", "False", "False" }, Run(
                "print land_stage_before_guidance(true, false, 0). print land_stage_before_guidance(true, false, 40).\n"
                + "print land_stage_before_guidance(false, false, 0). print land_stage_before_guidance(true, true, 0)."));
        }

        [Fact]
        public void Climbing_while_fast_brakes_off_the_speed_over_what_gravity_holds_down_or_what_stops_on_the_site_not_the_curves_rate()
        {
            // #530: Minmus, 62.1 km from the centre (g 0.458 m/s^2, so gravity holds 168.6 m/s down); the stopping curve's rate 9.34.
            const string G = "1.7658e9 / 62100 ^ 2";
            // 21.9 km short at 180.4 m/s, over the held speed: its excess over 2 s, not the 9.34 that took it to 50 m/s 22 km short.
            Assert.Equal("5.9", One("round(land_climb_braking(21874, 180.4, " + G + ", 62100, 9.34), 1)"));
            // Under it, far out: what stops it on the site.
            Assert.Equal("0.56", One("round(land_climb_braking(20000, 150, " + G + ", 62100, 9.34), 2)"));
            // At the stopping distance: the curve's rate, as #511 had it for Twin Craters.
            Assert.Equal("9.34", One("land_climb_braking(1000, 150, " + G + ", 62100, 9.34)"));
            foreach (var c in new[] { new[] { 21874, 180.4 }, new[] { 20000, 150.0 }, new[] { 1000, 150.0 }, new[] { 0, 60.0 } })
                Assert.Equal(double.Parse(One(string.Format(CultureInfo.InvariantCulture, "land_climb_braking({0}, {1}, " + G + ", 62100, 9.34)", c[0], c[1])), CultureInfo.InvariantCulture),
                    Core.Tools.BrakeGuidance.ClimbBraking(c[0], c[1], 1.7658e9 / (62100.0 * 62100), 62100, 9.34), 9);
        }

        [Fact]
        public void Braking_never_speeds_back_up_past_the_slowest_it_has_flown_but_keeps_20_m_s()
        {
            // #530: v_top held the starting 191.7 m/s, so at 45 m/s, 20 km short, the guidance pushed back up for it.
            Assert.Equal("45", One("land_v_top(191.7, 45)"));
            Assert.Equal("191.7", One("land_v_top(191.7, 200)"));
            Assert.Equal("20", One("land_v_top(50, 3)"));
            foreach (var c in new[] { new[] { 191.7, 45 }, new[] { 191.7, 200 }, new[] { 50, 3.0 } })
                Assert.Equal(double.Parse(One(string.Format(CultureInfo.InvariantCulture, "land_v_top({0}, {1})", c[0], c[1])), CultureInfo.InvariantCulture),
                    Core.Tools.BrakeGuidance.VTop(c[0], c[1]), 9);
        }

        [Fact]
        public void Land_turns_back_only_when_stopped_beyond_max_overshoot()
        {
            Assert.Equal("False", One("land_turn_back(-400, 0.5, 500)"));
            Assert.Equal("False", One("land_turn_back(-600, 5, 500)"));
            Assert.Equal("True", One("land_turn_back(-600, 0.5, 500)"));
            Assert.Equal("False", One("land_turn_back(600, 0, 500)"));
        }

        [Fact]
        public void Land_guidance_comes_straight_down_once_stopped_and_stays_upright_near_the_ground()
        {
            // Stopped 200 m long of the site (inside max_overshoot): no horizontal push, descending.
            var stopped = Run(Guide("-200, 0, 0, 0, -5, 80"));
            Assert.Equal("0", stopped[0]);
            Assert.Equal("0", stopped[1]);
            // 3 m up, still drifting 1 m/s sideways: at most 11 degrees of lean to null it, as Core's LandSteer limits it (#865).
            var low = Run(Guide("0, 0, 1, 0, -2, 3") + " local k is test_land_steer(lexicon(\"want\", w, \"last\", lexicon(\"al\", 0, \"cr\", 0, \"up\", 1), "
                + "\"v_al\", 1, \"v_cr\", 0, \"v_up\", -2, \"a_max\", 60000 / 5400)). print round(arctan2(sqrt(k:al ^ 2 + k:cr ^ 2), k:up), 3) <= 11.");
            Assert.Equal("True", low[3]);
        }

        // land_guide for one state (s, c, v_al, v_cr, v_up, h) of the reference lander (60 kN, 5.4 t) at
        // the Mun, braking having begun 3000 m up; prints al, cr and up rounded, and leaves it in w.
        private static string Guide(string state)
        {
            var p = state.Split(',');
            return "local w is land_guide(lexicon(\"s\", " + p[0] + ", \"c\", " + p[1] + ", \"v_al\", " + p[2] + ", \"v_cr\", " + p[3]
                + ", \"v_up\", " + p[4] + ", \"h\", " + p[5] + ", \"h_start\", 3000, \"v0\", 575, \"shape\", 2, \"v_top\", 575, \"a_max\", 60000 / 5400, \"g\", 1.63, \"radius\", 200000), 2, 1.1).\n"
                + "print round(w:al, 3). print round(w:cr, 3). print round(w:up, 3).";
        }

        [Fact]
        public void On_the_Mun_landing_of_7bc7_the_final_descent_steers_as_before_865_and_throttles_as_before_unless_over_15_degrees_off()
        {
            // m20261107-051747-7bc7's terminal trace (Mun Shot v2: 60 kN, about 5.2 t): h, v_h, v_down, m to go, every 2 s to 8 m. Each
            // state's demand from land_guide goes through Core's tick as the final descent and as before #865 (h_ref not 0: the old
            // land_max_tilt, land_steer and cos(error) throttle, which LandSteerParityTests matched to land.ks before they were deleted).
            // The direction and its m/s² are the same in every state: the 20 degree cap binds in none (at 322 and 284 m, where land laid
            // the craft flat, it is 5.9 and 2.3 m/s across, outside the cap's zone). At 240 and 112 m the nose is over 15 degrees off,
            // so the throttle gives only the vertical need: 0.129 to 0.124 and 0.321 to 0.308.
            var states = new[]
            {
                new[] { 357, 11.2, 16.7, 30 }, new[] { 322, 5.9, 17.3, 13 }, new[] { 284, 2.3, 20.4, 5 }, new[] { 240, 0.9, 23.7, 2 },
                new[] { 192, 1.1, 25.3, 2 }, new[] { 147, 0.3, 19.8, 4 }, new[] { 112, 1.4, 15.3, 2 }, new[] { 85, 0.3, 11.7, 0 },
                new[] { 65, 0.2, 8.9, 0 }, new[] { 49, 0.1, 6.8, 0 }, new[] { 38, 0.1, 5.1, 0 }, new double[] { 29, 0, 3.9, 0 },
                new double[] { 22, 0, 3, 0 }, new double[] { 17, 0, 2.3, 0 }, new double[] { 12, 0, 2.1, 0 }, new double[] { 8, 0, 2, 0 },
            };
            var source = new System.Text.StringBuilder("local a_max is 60000 / 5200.\n");
            foreach (var st in states)
                source.Append(string.Format(CultureInfo.InvariantCulture, @"
local w is land_guide(lexicon(""s"", {3}, ""c"", 0.5, ""v_al"", {1}, ""v_cr"", 0.1, ""v_up"", -{2}, ""h"", {0}, ""h_start"", 3000, ""v0"", 575,
  ""shape"", 2, ""v_top"", 575, ""a_max"", a_max, ""g"", 1.63, ""radius"", 200000 + {0}), 2, 1.1).
local input is lexicon(""want"", w, ""last"", lexicon(""al"", 0.06, ""cr"", -0.02, ""up"", 0.998), ""v_al"", {1}, ""v_cr"", 0.1, ""v_up"", -{2},
  ""a_max"", a_max, ""g"", 1.63, ""facing"", lexicon(""al"", 0.11, ""cr"", -0.05, ""up"", 1.018), ""rate"", 1, ""top_rate"", 0, ""dv_left"", 900, ""ut"", 100).
local k is test_land_steer(input).
print w:h_ref. print k:al. print k:cr. print k:up. print k:mag. print k:throttle.
local before is w:copy. set before[""h_ref""] to 1. set input[""want""] to before.
set k to test_land_steer(input).
print k:al. print k:cr. print k:up. print k:mag. print k:throttle.", st[0], st[1], st[2], st[3]));
            var printed = Run(source.ToString()).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(states.Length * 11, printed.Length);
            var changed = new List<string>();
            var throttled = new List<double>();
            for (var i = 0; i < states.Length; i++)
            {
                var t = printed.Skip(i * 11).ToArray();
                if (Math.Abs(t[5] - t[10]) > 1e-9) throttled.Add(states[i][0]);
                if (Enumerable.Range(1, 4).Any(j => Math.Abs(t[j] - t[j + 5]) > 1e-9))
                    changed.Add(string.Format(CultureInfo.InvariantCulture, "{0} m (h_ref {1}): lean {2:0.0} to {3:0.0}, mag {4:0.00} to {5:0.00}",
                        states[i][0], t[0], Math.Atan2(Math.Sqrt(t[6] * t[6] + t[7] * t[7]), t[8]) * 180 / Math.PI, Math.Atan2(Math.Sqrt(t[1] * t[1] + t[2] * t[2]), t[3]) * 180 / Math.PI,
                        t[9], t[4]));
            }
            Assert.True(changed.Count == 0, string.Join(" | ", changed));
            Assert.Equal(new double[] { 240, 112 }, throttled);
        }

        // ---- land: the powered descent, simulated (#233) ----

        // land_descend's loop on a point mass over flat Mun ground, from where braking starts: start_frac
        // times the distance at which land_vh_target falls to its speed, start_c m to one side, 3000 m up,
        // 575 m/s across the ground and 5 m/s down (about 3 m/s on the 30 by 3 km descent orbit there).
        // Each 0.05 s tick, land_guide's acceleration reaches the craft through a first-order lag (lag s,
        // standing in for the turn and the throttle cut while turning), capped at the engine's thrust
        // (thrust N, 345 s Isp, 5.4 t to start: the reference lander has 60 kN). Reversals and turning
        // back are counted as land_descend counts them. Prints reversals, sideways and downward speed at
        // touchdown, m past the site along the path and across it, delta-v used and seconds flown.
        private const string Simulation = @"
function sim {
  parameter start_frac, start_c, lag, thrust, max_overshoot.
  local mu is 6.5138398e10. local rad is 200000. local margin is 1.1. local m is 5400.
  local x is 0. local y is 3000. local z is 0. local vx is 575. local vy is -5. local vz is 0.
  local ax is 0. local ay is 0. local az is 0.
  local a_brake is sqrt(max(0.01, (0.95 * thrust / m) ^ 2 - (mu / (rad + y) ^ 2) ^ 2)).
  // Where land_vh_target(site_x, a_brake, margin) = vx, so land_descend starts braking.
  local a_ok is a_brake / margin.
  local site_x is start_frac * (vx ^ 2 / (2 * a_ok) + 2 * vx).
  local site_z is start_c.
  local h_start is max(500, y).
  local dt is 0.05. local t is 0. local used is 0.
  local sign is 1. local reversals is 0. local reversed is false.
  local last_h is 0. local last_down is 0.
  until y <= 0 or t > 900 {
    local g is mu / (rad + y) ^ 2.
    local a_max is thrust / m.
    if land_turn_back((site_x - x) * sign, sqrt(vx ^ 2 + vz ^ 2), max_overshoot) {
      set sign to -sign.
      set reversals to reversals + 1.
      set reversed to false.
    }
    local was is reversed.
    set reversed to land_is_reversed(vx * sign, was).
    if reversed and not was {
      set reversals to reversals + 1.
    }
    local w is land_guide(lexicon(""s"", (site_x - x) * sign, ""c"", (site_z - z) * sign, ""v_al"", vx * sign, ""v_cr"", vz * sign,
      ""v_up"", vy, ""h"", y, ""h_start"", h_start, ""v0"", 575, ""shape"", 2, ""v_top"", 575, ""a_max"", a_max, ""g"", g, ""radius"", rad + y), 2, margin).
    local k is min(1, dt / lag).
    set ax to ax + (w:al * sign - ax) * k.
    set az to az + (w:cr * sign - az) * k.
    set ay to ay + (w:up - ay) * k.
    local a_mag is sqrt(ax ^ 2 + ay ^ 2 + az ^ 2).
    if a_mag > a_max {
      set ax to ax * a_max / a_mag. set ay to ay * a_max / a_mag. set az to az * a_max / a_mag.
      set a_mag to a_max.
    }
    set last_h to sqrt(vx ^ 2 + vz ^ 2).
    set last_down to -vy.
    set vx to vx + ax * dt. set vz to vz + az * dt.
    set vy to vy + (ay - g + (vx ^ 2 + vz ^ 2) / (rad + y)) * dt.
    set x to x + vx * dt. set y to y + vy * dt. set z to z + vz * dt.
    set m to m - m * a_mag / (345 * 9.80665) * dt.
    set used to used + a_mag * dt.
    set t to t + dt.
  }
  print reversals. print round(last_h, 3). print round(last_down, 3).
  print round(x - site_x, 1). print round(z - site_z, 1). print round(used). print round(t).
}
";

        private sealed class Flight
        {
            public int Reversals;
            public double SidewaysAtTouchdown, DownAtTouchdown, Long, Across, DvUsed, Seconds;
        }

        private static Flight Fly(double startFrac, double startC = 0, double lag = 1, double thrust = 60000, double maxOvershoot = 500)
        {
            var printed = Run(Simulation + string.Format(CultureInfo.InvariantCulture, "sim({0}, {1}, {2}, {3}, {4}).", startFrac, startC, lag, thrust, maxOvershoot));
            double D(int i) => double.Parse(printed[i], CultureInfo.InvariantCulture);
            return new Flight
            {
                Reversals = int.Parse(printed[0], CultureInfo.InvariantCulture),
                SidewaysAtTouchdown = D(1), DownAtTouchdown = D(2), Long = D(3), Across = D(4), DvUsed = D(5), Seconds = D(6),
            };
        }

        private static void AssertSettlesOnTheSite(Flight f)
        {
            Assert.Equal(0, f.Reversals);
            // The block tests' touchdown_h_speed bound.
            Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
            Assert.InRange(f.DownAtTouchdown, 1, 3);
            Assert.InRange(f.Long, -10, 10);
            Assert.InRange(f.Across, -10, 10);
        }

        [Fact]
        public void The_descent_brakes_once_onto_the_site_and_touches_down_upright()
        {
            // Before #233 the same flight swung back over the site 4 times, touched down at 0.7 m/s
            // sideways and used 1012 m/s.
            var f = Fly(1);
            AssertSettlesOnTheSite(f);
            // The descent's share of the block tests' 900 m/s dv_used bound: the 575 m/s across the
            // ground plus holding the craft up and bringing it down; about 710 m/s.
            Assert.InRange(f.DvUsed, 575, 800);
        }

        [Fact]
        public void The_descent_brakes_once_with_a_slow_turn_or_a_weak_engine()
        {
            AssertSettlesOnTheSite(Fly(1, lag: 2));
            // Thrust-to-weight 3.4 at the Mun, half the reference lander's.
            AssertSettlesOnTheSite(Fly(1, thrust: 30000));
        }

        [Fact]
        public void The_descent_takes_in_a_site_moved_to_one_side()
        {
            // FLATSPOT may move the site up to max_divert (300 m) as braking starts.
            AssertSettlesOnTheSite(Fly(1, startC: 300));
            AssertSettlesOnTheSite(Fly(1, startC: -300, lag: 2, thrust: 30000));
        }

        [Fact]
        public void A_late_descent_lands_long_rather_than_turning_back()
        {
            // Braking starts at 82% of the distance it should: it stops about 200 m past the site,
            // inside max_overshoot, and comes straight down there.
            var f = Fly(0.82);
            Assert.Equal(0, f.Reversals);
            Assert.InRange(f.Long, 10, 500);
            Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
        }

        [Fact]
        public void A_descent_stopped_beyond_max_overshoot_turns_back_once()
        {
            foreach (var f in new[] { Fly(0.82, maxOvershoot: 50), Fly(0.8) })
            {
                Assert.Equal(1, f.Reversals);
                Assert.InRange(f.Long, -50, 50);
                Assert.InRange(f.SidewaysAtTouchdown, 0, 0.5);
            }
        }

        // ---- land: the powered descent over terrain (#365) ----

        // land_descend's loop on a point mass along one line over a terrain profile (a list of
        // list(x, height) corners, flat beyond the ends), braking from x = 0 at y m above sea level,
        // 575 m/s along and 5 m/s down, with the reference lander (60 kN, 5.4 t, lag 1 s), the site at
        // site_x, on the Descent Shape shape (#511). It steers by land_clearance over the terrain at land_lookahead's
        // distances and the highest ground on to where it stops (the rest of its path), as land_descend does, holds to
        // the speed braking began at, or the slowest since (land_v_top), and points as Core's LandSteer says (test_land_steer). BrakePath.Simulate is
        // this loop in C#. Another body and craft (mu, rad; m kg, thrust N, a lag s) may be given after t_end.
        // Prints whether it hit the ground hard (over 5 m/s), the fastest descent, the m past the site and the seconds
        // flown, then the altitude (m) each time it passed another 5 km along.
        private const string TerrainSimulation = @"
function terr {
  parameter prof, x.
  if x <= prof[0][0] { return prof[0][1]. }
  for i in range(1, prof:length) {
    if x <= prof[i][0] {
      return prof[i - 1][1] + (x - prof[i - 1][0]) / (prof[i][0] - prof[i - 1][0]) * (prof[i][1] - prof[i - 1][1]).
    }
  }
  return prof[prof:length - 1][1].
}
function terr_max {
  parameter prof, a, b.
  local most is max(terr(prof, a), terr(prof, b)).
  for c in prof {
    if c[0] > a and c[0] < b { set most to max(most, c[1]). }
  }
  return most.
}
function tsim {
  parameter prof, site_x, y, shape, v_start is 575, vy_start is -5, t_end is 900, mu is 6.5138398e10, rad is 200000, m is 5400, thrust is 60000, lag is 1.
  local margin is 1.1.
  local x is 0. local vx is v_start. local vy is vy_start. local ax is 0. local ay is 0. local idle is 0.
  local h_start is -1. local v0 is vx. local v_top is max(50, vx). local to_go is max(0, site_x).
  local dir is lexicon(""al"", -1, ""cr"", 0, ""up"", 0).
  local dt is 0.05. local t is 0. local fastest_down is 0. local radar is y - terr(prof, x).
  local alts is list(). local next_x is 0.
  until radar <= 0 or t > t_end {
    local g is mu / (rad + y) ^ 2.
    local a_max is thrust / m.
    local heights is list().
    for d in land_lookahead(abs(vx), to_go) {
      heights:add(terr(prof, x + d)).
    }
    heights:add(terr_max(prof, x, x + to_go)).
    local h is land_clearance(radar, y, heights).
    if h_start < 0 { set h_start to max(land_h_end(), h). }
    set v_top to land_v_top(v_top, abs(vx)).
    local w is land_guide(lexicon(""s"", site_x - x, ""c"", 0, ""v_al"", vx, ""v_cr"", 0, ""v_up"", vy, ""h"", h, ""h_start"", h_start,
      ""v0"", v0, ""shape"", shape, ""v_top"", v_top, ""a_max"", a_max, ""g"", g, ""radius"", rad + y, ""lag"", lag), 2, margin).
    set to_go to w:to_go.
    set dir to test_land_steer(lexicon(""want"", w, ""last"", dir, ""v_al"", vx, ""v_cr"", 0, ""v_up"", vy, ""a_max"", a_max, ""g"", g)).
    local mag is min(a_max, dir:mag).
    // The zero-thrust coast (#511): climbing while the demand is under 5% of what the engines give.
    if vy > 0 and mag < 0.05 * a_max { set idle to idle + 1. }
    local k is min(1, dt / lag).
    set ax to ax + (dir:al * mag - ax) * k.
    set ay to ay + (dir:up * mag - ay) * k.
    local a_mag is sqrt(ax ^ 2 + ay ^ 2).
    if a_mag > a_max {
      set ax to ax * a_max / a_mag. set ay to ay * a_max / a_mag. set a_mag to a_max.
    }
    if x >= next_x { alts:add(round(y)). set next_x to next_x + 5000. }
    set vx to vx + ax * dt.
    set vy to vy + (ay - g + vx ^ 2 / (rad + y)) * dt.
    set x to x + vx * dt. set y to y + vy * dt.
    set m to m - m * a_mag / (345 * 9.80665) * dt.
    set fastest_down to max(fastest_down, -vy).
    set t to t + dt.
    set radar to y - terr(prof, x).
  }
  print sqrt(vx ^ 2 + vy ^ 2) > 5. print round(fastest_down). print round(x - site_x). print round(t). print idle.
  for a in alts { print a. }
}
";

        private static string[] FlyOver(string profile, double siteX, double startY, double shape = 2)
        {
            return Run(TerrainSimulation + string.Format(CultureInfo.InvariantCulture, "tsim({0}, {1}, {2}, {3}).", profile, siteX, startY, shape));
        }

        /// <summary>
        /// <see cref="FlyOver"/> for the Minmus Flats approach of m20261030-013514-be46 (#530): braking 22.8 km short at 191.7 m/s and 10.4 m/s
        /// down, 2097 m up, shape 0.2, Mun Shot v2 (60 kN, 5 t, the turn lag of TURNTIME's 9.8 s at up to 59.9 deg/s).
        /// </summary>
        private static string[] FlyOverFlats()
        {
            return Run(TerrainSimulation + string.Format(CultureInfo.InvariantCulture, "tsim({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}).",
                FlatsProfile, 22808, 2097, 0.2, 191.7, -10.4, 900, MinmusMu, 60000, 5000, 60000, Core.Tools.BrakePath.TurnLag(59.9, 9.8)));
        }

        private const double MinmusMu = 1.7658e9;
        /// <summary>The Flats approach by x from braking start (BrakePathTests' Flats): plain at 0, the ridge 1650 m high 1.8 to 3.2 km on, the Flats at 1100 m from 4.2 km.</summary>
        private const string FlatsProfile = "list(list(0, 0), list(1808, 0), list(2408, 1650), list(3208, 1650), list(4208, 1100), list(30000, 1100))";

        /// <summary>The same flight as <see cref="FlyOver"/> by BrakePath.Simulate, the C# port: m past the site, seconds, and the altitude at each 5 km.</summary>
        private static Core.Tools.BrakeResult FlyOverInCSharp(double[][] profile, double siteX, double startY, double shape, out List<double> alts,
            double vStart = 575, double vUpStart = -5, double mu = 6.5138398e10, double radius = 200000, double massKg = 5400, double thrustN = 60000, double lag = 1)
        {
            Func<double, double> terr = x =>
            {
                if (x <= profile[0][0]) return profile[0][1];
                for (var i = 1; i < profile.Length; i++)
                    if (x <= profile[i][0]) return profile[i - 1][1] + (x - profile[i - 1][0]) / (profile[i][0] - profile[i - 1][0]) * (profile[i][1] - profile[i - 1][1]);
                return profile[profile.Length - 1][1];
            };
            var world = new Core.Tools.BrakeWorld
            {
                Mu = mu, Radius = radius, Dt = 0.05, PathSpacing = 5000,
                Ground = Core.Tools.TerrainProfile.Sample(s => terr(siteX - s), -5000, siteX + 5000, 50),
            };
            var craft = new Core.Tools.BrakeCraft { ThrustN = thrustN, MassKg = massKg, Isp = 345, LagS = lag };
            var flown = Core.Tools.BrakePath.Simulate(craft, world, new Core.Tools.BrakeStart { S = siteX, Alt = startY, VH = vStart, VUp = vUpStart, Braking = true }, shape);
            // The points every 5 km: not where braking stopped (#712) or came down.
            alts = flown.Path.Take(flown.Path.Count - 1).Where(p => p.S != flown.BrakeEndS).Select(p => p.Alt).ToList();
            return flown;
        }

        // The FlyOver profiles: #433's rim far out, a crater with its far rim past the site, flat ground and a ridge then a basin.
        public static readonly object[][] Profiles =
        {
            new object[] { "list(list(0, 0), list(40000, 0), list(48000, 3000), list(50000, 0))", 60000.0, 1400.0, 2.0 },
            new object[] { "list(list(0, 1500), list(3000, -1500), list(18500, -1500), list(21000, 500))", 18000.0, 4500.0, 2.0 },
            new object[] { "list(list(0, 0), list(40000, 0))", 20000.0, 3000.0, 2.0 },
            new object[] { "list(list(0, 0), list(40000, 0))", 20000.0, 3000.0, 0.3 },
            new object[] { "list(list(0, 0), list(4000, 2000), list(6000, -1500), list(12000, -1500), list(14000, 0), list(30000, 0))", 20000.0, 3500.0, 1.0 },
        };

        [Theory]
        [MemberData(nameof(Profiles))]
        public void Brakepaths_simulation_flies_the_kOS_guidance_laws_to_the_same_stop_and_altitudes(string profile, double siteX, double startY, double shape)
        {
            // #511: the C# port BRAKEPATH predicts with, against land_guide, Core's LandSteer and the look-ahead on kOS, the same loop.
            var kos = FlyOver(profile, siteX, startY, shape);
            List<double> alts;
            var flown = FlyOverInCSharp(Corners(profile), siteX, startY, shape, out alts);
            AssertTheSameFlight(kos, flown, alts);
        }

        private static double[][] Corners(string profile)
        {
            return profile.Replace("list(list(", "").Replace("))", "").Split(new[] { "), list(" }, StringSplitOptions.None)
                .Select(c => c.Split(',').Select(n => double.Parse(n.Trim(), CultureInfo.InvariantCulture)).ToArray()).ToArray();
        }

        private static void AssertTheSameFlight(string[] kos, Core.Tools.BrakeResult flown, List<double> alts)
        {
            Assert.Null(flown.Failure);
            Assert.Equal("False", kos[0]);
            // The same stop point, to 5 m.
            Assert.InRange(-flown.StopS - double.Parse(kos[2], CultureInfo.InvariantCulture), -5, 5);
            // The same altitudes every 5 km, to 10 m.
            var kosAlts = kos.Skip(5).Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(kosAlts.Count, alts.Count);
            for (var i = 0; i < alts.Count; i++) Assert.InRange(alts[i] - kosAlts[i], -10, 10);
        }

        [Fact]
        public void On_the_minmus_flats_approach_braking_begun_22_km_short_crosses_and_lands_on_the_site_the_same_in_kOS_and_brakepath()
        {
            // #530: from the trace's braking start the kOS laws braked to 50 m/s in 16 s, then flip-flopped there for 430 s,
            // climbing; BRAKEPATH predicted 687 m/s. Now both fly it across at its speed and down onto the site.
            var kos = FlyOverFlats();
            List<double> alts;
            var flown = FlyOverInCSharp(Corners(FlatsProfile), 22808, 2097, 0.2, out alts, 191.7, -10.4, MinmusMu, 60000, 5000, 60000,
                Core.Tools.BrakePath.TurnLag(59.9, 9.8));
            // The same approach: soft, and the same altitudes every 5 km. #865: the final descent's 20 degree cap holds only below 50 m
            // or under 2 m/s across (the player's call), but here it slows under 2 m/s about 260 m up while land_guide still asks
            // to brake sideways with nothing up, and the cap's lift held its height there for about 40 s (#886: now bounded by land_guide's wanted descent rate, so it
            // doesn't). BRAKEPATH calls the same LandSteer rule, so its stop is within 5 m of kOS's.
            Assert.Null(flown.Failure);
            Assert.Equal("False", kos[0]);
            var kosAlts = kos.Skip(5).Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToList();
            Assert.Equal(kosAlts.Count, alts.Count);
            for (var i = 0; i < alts.Count; i++) Assert.InRange(alts[i] - kosAlts[i], -10, 10);
            Assert.InRange(double.Parse(kos[2], CultureInfo.InvariantCulture) + flown.StopS, -5, 5);
            Assert.InRange(double.Parse(kos[2], CultureInfo.InvariantCulture), -25, 25);
            Assert.InRange(double.Parse(kos[3], CultureInfo.InvariantCulture), 0, 250);
            Assert.InRange(flown.DvUsed, 150, 300);
            // Never back over its starting height plus the arrest of its fall above circular speed (BrakePathTests), let alone 2.4 km.
            Assert.True(alts.Max() < 2097 + 300, string.Join(", ", alts));
        }

        /// <summary>The Twin Craters approach from m20261031-022433-3dc5's braking start (BrakePathTests' recorded ground): 965 m there, the site's hollow at 400 m.</summary>
        private const string TwinCratersProfile = "list(list(-563, 980), list(21870, 400), list(30000, 400))";

        [Fact]
        public void From_the_78_km_twin_craters_braking_start_kOS_and_brakepath_fly_the_same_drop_and_land_on_the_site()
        {
            // #547: braking 21.9 km out at 599.5 m/s and 9 m/s down, 4309 m up (3344 m over the ground), shape 2.35, Mun Shot v2's
            // lander (60 kN, 5 t, TURNTIME's 9.9 s at up to 59.5 deg/s). It holds about 3.4 km to within a km of the site, then comes
            // down no faster than land_sink_limit lets it, and touches down softly on the site, the same in both.
            var lag = Core.Tools.BrakePath.TurnLag(59.5, 9.9);
            var kos = Run(TerrainSimulation + string.Format(CultureInfo.InvariantCulture, "tsim({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}).",
                TwinCratersProfile, 21870, 4309, 2.345, 599.5, -9, 900, 6.5138398e10, 200000, 5000, 60000, lag));
            List<double> alts;
            var flown = FlyOverInCSharp(Corners(TwinCratersProfile), 21870, 4309, 2.345, out alts, 599.5, -9, 6.5138398e10, 200000, 5000, 60000, lag);
            AssertTheSameFlight(kos, flown, alts);
            Assert.InRange(double.Parse(kos[2], CultureInfo.InvariantCulture), -300, 300);
            Assert.InRange(double.Parse(kos[1], CultureInfo.InvariantCulture), 0, 100);
            Assert.True(flown.LeastStopRoom >= Core.Tools.BrakeGuidance.HEnd / 2, "least room to stop " + Math.Round(flown.LeastStopRoom) + " m");
        }

        [Fact]
        public void Land_steers_by_its_height_above_the_highest_ground_ahead()
        {
            // 1000 m up over ground at 0, a 600 m hill ahead: 400 m clear.
            Assert.Equal("400", One("land_clearance(1000, 1000, list(0, 200, 600, 100))"));
            // Ground ahead lower than under it: the radar height.
            Assert.Equal("1000", One("land_clearance(1000, 1500, list(-500, 100))"));
            // A minute ahead at 100 m/s, but no farther than where it stops.
            Assert.Equal(new[] { "23", "50", "6000", "700" }, Run(
                "local d is land_lookahead(100, 1e9). print d:length. print d[0]. print d[d:length - 1]. print land_lookahead(100, 700)[22]."));
        }

        [Fact]
        public void The_terminal_descent_steers_by_radar_not_the_predicted_paths_ground()
        {
            // #562, 6be2 over East Crater: stopped over the site, the predicted path's ground put h at -1 while radar read 26.
            // Steering by h it climbed 0.1 m/s and hovered 450 s; by radar it comes down.
            var over = Run(Guide("0, 0, 0, 0, 0, -1").Replace("\"h_start\"", "\"radar\", 26, \"h_start\"") + " print w:h_ref. print w:vv <= -2. print round(w:h).");
            Assert.Equal(new[] { "0", "True", "26" }, over.Skip(3).ToArray());
            // Still on its Descent Shape (h_ref over 0), the path's ground still counts.
            var shaped = Run(Guide("2000, 0, 100, 0, -10, 500").Replace("\"h_start\"", "\"radar\", 900, \"h_start\"") + " print round(w:h).");
            Assert.Equal("500", shaped[3]);
            // No radar given (BRAKEPATH's sim): h as before.
            Assert.Equal("-1", Run(Guide("0, 0, 0, 0, 0, -1") + " print round(w:h).")[3]);
        }

        [Fact]
        public void A_terminal_hover_is_caught_after_ten_seconds()
        {
            // #562: land_hover_watch(since, phase, v_down, touchdown, thrott, ut) gives the UT the hover began (-1 for none).
            Assert.Equal("-1", One("land_hover_watch(-1, \"terminal\", 2, 2, 0.1, 100)"));
            Assert.Equal("100", One("land_hover_watch(-1, \"terminal\", 0.1, 2, 0.1, 100)"));
            Assert.Equal("100", One("land_hover_watch(100, \"terminal\", 0, 2, 0.1, 105)"));
            Assert.Equal("-1", One("land_hover_watch(100, \"terminal\", 1.5, 2, 0.1, 105)"));
            Assert.Equal("-1", One("land_hover_watch(100, \"approach\", 0, 2, 0.1, 105)"));
            // Throttle 0 (resting on a slope, say) isn't a hover.
            Assert.Equal("-1", One("land_hover_watch(100, \"terminal\", 0, 2, 0, 105)"));
            Assert.Equal("False", One("land_hovering(100, 109)"));
            Assert.Equal("True", One("land_hovering(100, 110)"));
            Assert.Equal("False", One("land_hovering(-1, 1000)"));
            // Caught: the descent comes down at touchdown speed whatever h says.
            Assert.Equal("-2", One("land_vv_target(-1, 0, 2, 2, 2, true)"));
            Assert.Equal("-2", One("land_vv_target(300, 0, 2, 2, 2, true)"));
        }

        [Fact]
        public void The_descent_burns_in_every_phase_but_the_fall()
        {
            Assert.Equal("False", One("land_burning(\"fall\")"));
            Assert.Equal("True", One("land_burning(\"braking\")"));
            Assert.Equal("True", One("land_burning(\"approach\")"));
            Assert.Equal("True", One("land_burning(\"terminal\")"));
            // A stage run dry while braking: throttle 0, but still burning.
            Assert.Equal("True", One("land_burning(land_phase(true, 43, 2231))"));
            Assert.Equal("False", One("land_burning(land_phase(false, 575, -1))"));
        }

        // ---- land: braking for a craft that turns slowly (#380) ----

        [Fact]
        public void Land_times_a_turn_by_turntimes_model_and_counts_the_last_5_degrees_as_pointed()
        {
            // Mun Shot v2's TURNTIME: 190 degrees in 102.5 s at up to 1.9 deg/s, so 2.5 s to reach it.
            Assert.Equal("102.5", One("round(land_turn_seconds(195, 1.9, 102.5), 3)"));
            Assert.Equal("5.132", One("round(land_turn_seconds(10, 1.9, 102.5), 3)"));
            // 2 degrees to go never reaches the top rate: 2 sqrt(2 / 0.76).
            Assert.Equal("3.244", One("round(land_turn_seconds(7, 1.9, 102.5), 3)"));
            // A turn that peaks under kOS's cap (1 deg/s^2: 190 degrees in 27.568 s, peaking at 13.784 deg/s).
            Assert.Equal("27.568", One("round(land_turn_seconds(195, 13.784, 27.568), 3)"));
            Assert.Equal("0", One("land_turn_seconds(5, 1.9, 102.5)"));
            Assert.Equal("0", One("land_turn_seconds(0, 1.9, 102.5)"));
            // No TURNTIME (no torque): no allowance.
            Assert.Equal("0", One("land_turn_seconds(90, 0, 0)"));
        }

        [Fact]
        public void The_braking_start_distance_grows_with_the_turn_left_and_is_todays_when_pointed()
        {
            // With no turn left it is where land_vh_target falls to the speed, as before #380.
            Assert.Equal("568", One("round(land_vh_target(land_brake_distance(568, 10, 1.1, 0), 10, 1.1), 3)"));
            Assert.Equal("True", One("land_brake_distance(568, 10, 1.1, land_turn_seconds(0, 1.9, 102.5)) = land_brake_distance(568, 10, 1.1, 0)"));
            // 20 s still to turn at 568 m/s: 11.36 km farther out; more turn, farther still.
            Assert.Equal("11360", One("round(land_brake_distance(568, 10, 1.1, 20) - land_brake_distance(568, 10, 1.1, 0), 3)"));
            Assert.Equal("True", One("land_brake_distance(568, 10, 1.1, land_turn_seconds(90, 1.9, 102.5)) > land_brake_distance(568, 10, 1.1, land_turn_seconds(30, 1.9, 102.5))"));
            // The rule: just outside the distance it waits, just inside it brakes; a turn left brakes it sooner.
            var d = "land_brake_distance(568, 10, 1.1, 0)";
            Assert.Equal("False", One("land_brake_due(" + d + " + 1, 1e5, 568, 10, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(" + d + " - 1, 1e5, 568, 10, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(" + d + " + 1, 1e5, 568, 10, 3000, 10, 1.1, 1)"));
            // The site behind brakes it whatever the distance; the ground coming up only as a safety net, inside the path's
            // margin (#511: 300 m, or 2 s of the descent).
            Assert.Equal("True", One("land_brake_due(1e6, -1, 568, 10, 3000, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(1e6, 1e6, 568, 10, 290, 10, 1.1, 0)"));
            Assert.Equal("True", One("land_brake_due(1e6, 1e6, 568, 200, 390, 10, 1.1, 0)"));
            Assert.Equal("False", One("land_brake_due(1e6, 1e6, 568, 80, 3000, 10, 1.1, 0)"));
        }

        [Fact]
        public void A_low_pass_over_high_ground_far_out_doesnt_start_braking_at_orbital_speed()
        {
            // Twin Craters (m20261029-181119-0584): periapsis about 5 km, 567 m/s, the terrain ahead bringing clearance under
            // 1,500 m about 80 km from the site. Braking began there and asked for nearly nothing; now it waits for the stopping distance.
            Assert.Equal("False", One("land_brake_due(80000, 80000, 567, -13, 1400, land_brake_accel(60000 / 5400, 1.63), 1.1, 0)"));
            // The stopping distance there is about 18.1 km.
            Assert.Equal("True", One("land_brake_due(18000, 18000, 567, 5, 1400, land_brake_accel(60000 / 5400, 1.63), 1.1, 0)"));
        }

        // For the reference lander (60 kN, 5.4 t) at the Mun, 575 m/s along, v_up m/s up, 3000 m up, the
        // site s m along and 100 m across: land_fall_guide, and land_guide's first command from the same
        // state where land_brake_distance starts braking, with v_top (the speed braking began at) v_top.
        // Prints the fall's al and up, the degrees between the two, and land_guide's al.
        private static double[] FallAndFirst(string s, double vUp, string vTop = "575")
        {
            var state = "\"c\", 100, \"v_al\", 575, \"v_cr\", 0, \"v_up\", " + vUp.ToString(CultureInfo.InvariantCulture)
                + ", \"h\", 3000, \"a_max\", 60000 / 5400, \"g\", 1.63, \"radius\", 200000";
            return Run("local start is land_brake_distance(575, land_brake_accel(60000 / 5400, 1.63), 1.1, 0).\n"
                + "local f is land_fall_guide(lexicon(\"s\", " + s + ", " + state + "), 2, 1.1).\n"
                + "local w is land_guide(lexicon(\"s\", start, \"h_start\", 3000, \"v0\", 575, \"shape\", 2, \"v_top\", " + vTop + ", " + state + "), 2, 1.1).\n"
                + "local dot is (f:al * w:al + f:cr * w:cr + f:up * w:up) / sqrt((f:al ^ 2 + f:cr ^ 2 + f:up ^ 2) * (w:al ^ 2 + w:cr ^ 2 + w:up ^ 2)).\n"
                + "print f:al. print f:up. print arccos(min(1, dot)). print w:al.")
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        [Fact]
        public void The_fall_steers_along_the_first_braking_command()
        {
            // Falling 10 m/s (braking starts near periapsis; from a faster fall the Descent Shape, at h_start, first arrests it) at the braking start: what land_guide first asks for from the same state.
            var at = FallAndFirst("start", -10);
            Assert.InRange(at[2], 0, 0.01);
            // Braking: back along the path and held up, not surface retrograde.
            Assert.True(at[0] < -5);
            Assert.True(at[1] > 1);
            // Farther out, the same: the command braking will begin with, not one for a site it isn't
            // braking for yet.
            var far = FallAndFirst("start * 3", -10);
            Assert.InRange(far[2], 0, 0.01);
            Assert.Equal(at[0], far[0], 6);
            // Falling 5 m/s, land_guide's first command holds the speed braking began at (v_top) until its
            // stopping curve falls under it, a moment on (0 along): the fall points along the braking that
            // comes then, land_guide's from the same state on its curve.
            Assert.Equal(0, FallAndFirst("start", -5)[3], 3);
            var slow = FallAndFirst("start", -5, "1e9");
            Assert.InRange(slow[2], 0, 0.01);
            Assert.True(slow[0] < -5);
        }

        [Fact]
        public void Braking_that_starts_early_for_the_turn_flies_by_the_site_that_much_nearer()
        {
            Assert.Equal("8000", One("land_turning_s(10000, 500, 4)"));
            Assert.Equal("10000", One("land_turning_s(10000, 500, 0)"));
            Assert.Equal("1", One("land_turning_s(100, 500, 4)"));
            Assert.Equal("0.5", One("land_turning_s(0.5, 500, 4)"));
            Assert.Equal("-50", One("land_turning_s(-50, 500, 4)"));
        }

        [Fact]
        public void The_coast_ends_the_turn_and_30_s_before_braking_and_never_under_90_s()
        {
            // Mun Shot v2: a 112.5 s turn.
            Assert.Equal("857.5", One("land_coast_end(1000, 112.5)"));
            Assert.Equal("True", One("land_coast_end(1000, 112.5) <= 1000 - 112.5 - 30"));
            // A quick turn: the 90 s as before #380.
            Assert.Equal("910", One("land_coast_end(1000, 30)"));
        }

        // A fall at 575 m/s over a terrain profile (as for tsim) to a site at 60 km, periapsis 3000 m up
        // over it at 104 s: y = 3000 + 0.2 (104 - t)^2. Prints the braking start land_brake_estimate gives
        // from samples every 5 s, the UT land_brake_due fires checking every 0.1 s by land_descend's
        // own clearance (land_lookahead, land_clearance), and the start the coast used before #380
        // (periapsis less the stopping time at periapsis).
        private const string FallEstimate = @"
function fall_at {
  parameter prof, t.
  local x is 575 * t.
  return lexicon(""ut"", t, ""x"", x, ""dist"", abs(60000 - x), ""ahead"", 60000 - x, ""v_h"", 575, ""v_down"", 0.4 * (104 - t),
    ""alt"", 3000 + 0.2 * (104 - t) ^ 2, ""ground"", terr(prof, x)).
}
function estimate {
  parameter prof.
  local a_brake is land_brake_accel(60000 / 5400, 1.63).
  local samples is list().
  local t is 0.
  until t > 180 { samples:add(fall_at(prof, t)). set t to t + 5. }
  print round(land_brake_estimate(samples, a_brake, 1.1), 1).
  set t to 0.
  until t > 180 {
    local p is fall_at(prof, t).
    local heights is list().
    for d in land_lookahead(p:v_h, p:dist) { heights:add(terr(prof, p:x + d)). }
    local h is land_clearance(p:alt - p:ground, p:alt, heights).
    if land_brake_due(p:dist, p:ahead, p:v_h, p:v_down, h, a_brake, 1.1, 0) { break. }
    set t to t + 0.1.
  }
  print round(t, 1).
  print round(104 - 575 * 1.1 / (2 * a_brake), 1).
}
";

        private static double[] Estimate(string profile)
        {
            return Run(TerrainSimulation + FallEstimate + "estimate(" + profile + ").")
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        [Fact]
        public void The_coast_plans_on_braking_no_later_than_it_starts_over_high_ground()
        {
            // A 3600 m plateau from 30 km on, over the craft's 3000 m periapsis: the ground comes inside
            // the margin (#511: the ground-close safety net) some 35 s before the periapsis estimate, which was too late.
            var high = Estimate("list(list(0, 0), list(20000, 0), list(30000, 3600), list(90000, 3600))");
            Assert.InRange(high[0], high[1] - 5.1, high[1]);
            Assert.True(high[2] > high[1] + 30);
            // Flat ground: it brakes for the site, and the estimate is still no later.
            var flat = Estimate("list(list(0, 0), list(90000, 0))");
            Assert.InRange(flat[0], flat[1] - 5.1, flat[1]);
        }

        [Fact]
        public void The_descent_traces_a_line_every_2_s_and_at_each_phase_change()
        {
            Assert.Equal("fall", One("land_phase(false, 575, -1)"));
            Assert.Equal("braking", One("land_phase(true, 200, 450)"));
            Assert.Equal("approach", One("land_phase(true, 20, 100)"));
            Assert.Equal("terminal", One("land_phase(true, 0.5, 0)"));
            Assert.Equal("False", One("land_trace_due(\"braking\", \"braking\", 101.9, 100)"));
            Assert.Equal("True", One("land_trace_due(\"braking\", \"braking\", 102, 100)"));
            Assert.Equal("True", One("land_trace_due(\"approach\", \"braking\", 100.1, 100)"));
        }

        [Fact]
        public void Braking_far_out_holds_its_speed_rather_than_speeding_up_to_the_stopping_curve()
        {
            // 60 km out the curve allows about 1070 m/s; at 575 it would push ahead, held to 575 it doesn't.
            Assert.Equal("True", One("land_axis_accel(60000, 575, 10, 1.1) > 1"));
            Assert.Equal("0", One("round(land_axis_accel(60000, 575, 10, 1.1, 575), 3)"));
            // Slower than v_top it still closes on the site.
            Assert.Equal("True", One("land_axis_accel(60000, 0, 10, 1.1, 50) > 1"));
        }

        [Fact]
        public void A_descent_braking_far_out_climbs_over_a_rim_ahead_rather_than_flying_into_it()
        {
            // Braking began 60 km short, 1400 m up (land_descend's h < 1500), with a 3 km rim rising over
            // 8 km between it and the site: it cruises no faster than it was going and clears the rim.
            var f = FlyOver("list(list(0, 0), list(40000, 0), list(48000, 3000), list(50000, 0))", 60000, 1400);
            Assert.Equal("False", f[0]);
            Assert.InRange(double.Parse(f[2], CultureInfo.InvariantCulture), -20, 500);
        }

        [Fact]
        public void Braking_at_near_orbital_speed_far_from_the_site_never_sits_at_near_zero_throttle_while_climbing()
        {
            // Twin Craters try 2 (#511): braking began 80 km out at 567 m/s, past periapsis and climbing 13 m/s, 1.5 km over the
            // ground. Along the track v_top held the speed and the stopping curve allowed more; vertically the demand was clamped at
            // 0: throttle 0.01, and clearance rose 2 km with no thrust. Whatever shape it flies, it now brakes while it climbs.
            foreach (var shape in new[] { 0.3, 1.0, 3.0 })
            {
                var f = Run(TerrainSimulation + string.Format(CultureInfo.InvariantCulture,
                    "tsim(list(list(0, 0), list(100000, 0)), 80000, 1500, {0}, 567, 13, 60).", shape));
                // The first minute: no tick climbing with under 5% of the engines' acceleration asked for.
                Assert.Equal("0", f[4]);
            }
        }

        [Fact]
        public void A_descent_into_a_crater_holds_its_height_above_the_far_rim_and_never_falls_fast()
        {
            // Over the near rim, the floor 3 km down, the site on the floor just short of a 2 km far rim.
            var f = FlyOver("list(list(0, 1500), list(3000, -1500), list(18500, -1500), list(21000, 500))", 18000, 4500);
            Assert.Equal("False", f[0]);
            Assert.InRange(double.Parse(f[1], CultureInfo.InvariantCulture), 0, 100);
            Assert.InRange(double.Parse(f[2], CultureInfo.InvariantCulture), -20, 500);
        }

        // ---- ascend ----

        [Fact]
        public void Ascend_options_take_the_spec_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), ascend_defaults(), 900):opt.\nprint o:target_alt. print o:target_inc. print o:max_duration.");
            Assert.Equal(new[] { "auto", "0", "900" }, printed);
            Assert.Equal("unknown option target_ap", One("claudia_options(lexicon(\"target_ap\", 5), ascend_defaults(), 900):error"));
            Assert.Equal("", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", 90), ascend_defaults(), 900):opt)"));
            Assert.Equal("target_inc must be degrees from 0 to 180", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", 200), ascend_defaults(), 900):opt)"));
            Assert.Equal("target_inc must be degrees from 0 to 180", One("ascend_unreadable(claudia_options(lexicon(\"target_inc\", \"polar\"), ascend_defaults(), 900):opt)"));
        }

        [Fact]
        public void Ascend_aims_5_km_above_the_highest_terrain_unless_told_and_never_below_it()
        {
            Assert.Equal("12061", One("ascend_target_alt(\"auto\", 7061)"));
            Assert.Equal("20000", One("ascend_target_alt(20000, 7061)"));
            Assert.Equal("", One("ascend_check_alt(12061, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt 5000 m is not above the highest terrain, 7061 m", One("ascend_check_alt(5000, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt 3000000 m is outside the Mun's sphere of influence", One("ascend_check_alt(3000000, 7061, 2429559, \"Mun\")"));
            Assert.Equal("target_alt must be \"auto\" or metres, not 'high'", One("ascend_check_alt(\"high\", 7061, 2429559, \"Mun\")"));
        }

        [Fact]
        public void Ascend_heads_for_the_target_inclination()
        {
            Assert.Equal("90", One("round(ascend_azimuth(0, 0, 550, 0), 3)"));
            Assert.Equal("0", One("round(ascend_azimuth(90, 0, 550, 0), 3)"));
            Assert.Equal("45", One("round(ascend_azimuth(45, 0, 550, 0), 3)"));
            Assert.Equal("270", One("round(ascend_azimuth(180, 0, 550, 0), 3)"));
            // The ground already moves east at 10 m/s, so less of the burn goes east.
            Assert.Equal("44.254", One("round(ascend_azimuth(45, 0, 550, 10), 3)"));
            // An inclination below the site's latitude can't be reached: due east, the nearest.
            Assert.Equal("90", One("round(ascend_azimuth(10, 30, 550, 0), 3)"));
        }

        [Fact]
        public void Ascend_climbs_at_half_the_speed_that_would_coast_it_up_to_the_target()
        {
            // Coasting up 10 km at 1.6 m/s^2 takes 178.885 m/s; it climbs at half that.
            Assert.Equal("89.443", One("round(ascend_vv_target(2000, 12000, 1.6), 3)"));
            Assert.Equal("0", One("ascend_vv_target(13000, 12000, 1.6)"));
        }

        [Fact]
        public void Ascend_pitches_to_hold_the_climb_rate()
        {
            Assert.Equal("90", One("round(ascend_pitch(10, 1.6, 0, 100), 3)"));
            Assert.Equal("9.207", One("round(ascend_pitch(10, 1.6, 100, 100), 3)"));
            Assert.Equal("0", One("round(ascend_pitch(10, 1.6, 150, 100), 3)"));
        }

        [Fact]
        public void Ascend_circularizes_at_apoapsis_by_vis_viva()
        {
            Assert.Equal("15.381", One("round(ascend_circularize_dv(" + MunMu + ", 212000, 201000), 3)"));
        }

        // #597: ascend also starts from flight, the abort to orbit for a falling craft. 7061 m is the Mun's highest terrain.
        [Fact]
        public void Ascend_starts_from_the_ground_or_from_flight_over_an_airless_body()
        {
            Assert.Equal("ground", One("ascend_start(\"LANDED\", false, -190000, 7061, \"Mun\")"));
            Assert.Equal("ground", One("ascend_start(\"SPLASHED\", false, -190000, 7061, \"Mun\")"));
            Assert.Equal("ground", One("ascend_start(\"PRELAUNCH\", false, -190000, 7061, \"Mun\")"));
            Assert.Equal("flight", One("ascend_start(\"SUB_ORBITAL\", false, -59600, 7061, \"Mun\")"));
            Assert.Equal("flight", One("ascend_start(\"FLYING\", false, -59600, 7061, \"Mun\")"));
            // In orbit but the periapsis under the highest terrain: still falling into it.
            Assert.Equal("flight", One("ascend_start(\"ORBITING\", false, 4000, 7061, \"Mun\")"));
            // Past escape speed there's no apoapsis to climb to, even on a path into the terrain.
            Assert.Equal("ascend starts landed, flying or sub-orbital, not ESCAPING", One("ascend_start(\"ESCAPING\", false, 4000, 7061, \"Mun\")"));
            // Already safe in orbit: nothing to fly.
            Assert.Equal("orbit", One("ascend_start(\"ORBITING\", false, 12000, 7061, \"Mun\")"));
            Assert.Equal("ascend starts landed, flying or sub-orbital, not ESCAPING", One("ascend_start(\"ESCAPING\", false, 12000, 7061, \"Mun\")"));
            Assert.Equal("ascend starts landed, flying or sub-orbital, not DOCKED", One("ascend_start(\"DOCKED\", false, 12000, 7061, \"Mun\")"));
            Assert.Equal("ascend flies from airless bodies only; Duna has an atmosphere (use launch)", One("ascend_start(\"LANDED\", true, -300000, 8264, \"Duna\")"));
            Assert.Equal("ascend flies from airless bodies only; Duna has an atmosphere (use launch)", One("ascend_start(\"SUB_ORBITAL\", true, -300000, 8264, \"Duna\")"));
        }

        [Fact]
        public void Ascend_from_a_fall_over_Minmus_first_commands_a_climb()
        {
            // 22cc's state, roughly: 2 km over Minmus (radius 60 km), 30 m/s down and 20 m/s across, to "auto" 10,725 m.
            const string Fall = "1.7658e9, 62000, 20, -30, 2000, 10725, ";
            // A Terrier lander (6 m/s^2) puts all of it into stopping the fall and climbing.
            Assert.Equal("90", One("round(ascend_climb_pitch(" + Fall + "6), 3)"));
            // With more thrust, the rest goes into horizontal speed, still pointing up.
            var pitch = double.Parse(One("ascend_climb_pitch(" + Fall + "25)"), CultureInfo.InvariantCulture);
            Assert.InRange(pitch, 30, 89);
            Assert.True(double.Parse(One("sin(ascend_climb_pitch(" + Fall + "25))"), CultureInfo.InvariantCulture) > 0.5);
            // No thrust (the stage dry before staging): straight up, never into the ground.
            Assert.Equal("90", One("ascend_climb_pitch(" + Fall + "0)"));
        }

        [Fact]
        public void Ascend_from_flight_keeps_its_direction_of_travel_unless_hovering()
        {
            Assert.Equal("200", One("ascend_flight_az(90, 200, 150)"));
            Assert.Equal("90", One("ascend_flight_az(90, 200, 9)"));
            // A hover drifting a little past the ground's own 9 m/s of turn still flies into target_inc.
            Assert.Equal("90", One("ascend_flight_az(90, 200, 15)"));
        }

        [Fact]
        public void Ascend_from_flight_climbs_at_least_1_km_above_the_craft_so_apoapsis_is_ahead()
        {
            Assert.Equal("10725", One("ascend_climb_to(\"flight\", 10725, 2000)"));
            Assert.Equal("16000", One("ascend_climb_to(\"flight\", 10725, 15000)"));
            Assert.Equal("10725", One("ascend_climb_to(\"ground\", 10725, 15000)"));
        }

        [Fact]
        public void Ascend_from_flight_fails_cleanly_when_it_cannot_reach_orbit_before_impact()
        {
            // 30 m/s down at 2 km over Minmus, 20 m/s across, 160 m/s for the orbit.
            Assert.Equal("", One("ascend_flight_short(-30, 20, 160, 6, 0.45, 2000, 600)"));
            // Stock can't say (0): the delta-v isn't judged.
            Assert.Equal("", One("ascend_flight_short(-30, 20, 160, 6, 0.45, 2000, 0)"));
            Assert.Equal("too little delta-v to reach orbit before impact: needs at least 170 m/s, 120 m/s left", One("ascend_flight_short(-30, 20, 160, 6, 0.45, 2000, 120)"));
            // 1.6 m/s^2 against 0.45 stops 150 m/s down in 9,783 m: the ground comes first.
            Assert.Equal("too little thrust to reach orbit before impact: stopping the fall takes 9783 m, 2000 m above the ground", One("ascend_flight_short(-150, 20, 160, 1.6, 0.45, 2000, 600)"));
            Assert.Equal("too little thrust to reach orbit before impact: thrust-to-weight 0.8 while falling", One("ascend_flight_short(-30, 20, 160, 0.36, 0.45, 2000, 600)"));
            // Climbing already: nothing to stop.
            Assert.Equal("", One("ascend_flight_short(5, 20, 160, 0.36, 0.45, 2000, 600)"));
        }
    }
}
