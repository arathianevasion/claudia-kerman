// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// The transfer and return blocks' pure helpers, run on kOS.Safe
    /// through the real bundle. Flying them needs KSP: those are the block tests in tests/blocks.
    /// </summary>
    public class TransferAndReturnTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        private static double Number(string expression)
        {
            return double.Parse(One(expression), CultureInfo.InvariantCulture);
        }

        // ---- transfer options ----

        private static string Unreadable(string options)
        {
            return One("transfer_to_unreadable(claudia_options(lexicon(" + options + "), transfer_to_defaults(), \"auto\"):opt)");
        }

        [Fact]
        public void Transfer_takes_the_spec_options_and_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), transfer_to_defaults(), \"auto\"). print o:error. print o:opt:window. print o:opt:correction. print o:opt:max_duration.");
            Assert.Equal(new[] { "", "next", "True", "auto" }, printed);
        }

        [Fact]
        public void Transfer_needs_a_target_and_a_target_periapsis()
        {
            Assert.Equal("target is required", Unreadable(""));
            Assert.Equal("target_pe is required", Unreadable("\"target\", \"Mun\""));
            Assert.Equal("", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000"));
        }

        [Fact]
        public void Transfer_refuses_option_values_it_cannot_read()
        {
            Assert.Equal("target must be a body or its name", Unreadable("\"target\", 5, \"target_pe\", 30000"));
            Assert.Equal("target_pe must be metres above 0, not -5", Unreadable("\"target\", \"Mun\", \"target_pe\", -5"));
            Assert.Equal("window must be 'next' or a time (UT seconds), not 'later'", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"window\", \"later\""));
            Assert.Equal("correction must be true or false", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"correction\", \"yes\""));
            Assert.Equal("target_pe must be metres above 0, not 30km", Unreadable("\"target\", \"Mun\", \"target_pe\", \"30km\""));
            Assert.Equal("", Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"window\", 5000, \"correction\", false"));
        }

        // ---- The arrival periapsis after the correction (#672) ----

        [Fact]
        public void Transfer_to_judges_the_arrival_periapsis_within_pe_tolerance_1000_by_default()
        {
            Assert.Equal("1000", One("claudia_options(lexicon(), transfer_to_defaults(), \"auto\"):opt:pe_tolerance"));
        }

        [Theory]
        [InlineData("\"x\"", "pe_tolerance must be metres above 0, not x")]
        [InlineData("0", "pe_tolerance must be metres above 0, not 0")]
        [InlineData("-5", "pe_tolerance must be metres above 0, not -5")]
        [InlineData("2500", "")]
        public void Transfer_to_refuses_a_pe_tolerance_that_is_not_metres_above_0(string tolerance, string expected)
        {
            Assert.Equal(expected, Unreadable("\"target\", \"Mun\", \"target_pe\", 30000, \"pe_tolerance\", " + tolerance));
        }

        [Fact]
        public void A_max_residual_given_goes_to_exec_node_and_return_s_correction_takes_its_default()
        {
            const string ctx = "local c is lexicon(\"opt\", claudia_options(lexicon(), transfer_to_defaults(), \"auto\"):opt, \"start_ut\", 0). ";
            Assert.Equal(new[] { "True", "0.1" }, Run(ctx + "local b is claudia_burn_options(c, \"n\", 0.1). print b:haskey(\"max_residual\"). print b:max_residual."));
            Assert.Equal("False", Run(ctx + "print claudia_burn_options(c, \"n\"):haskey(\"max_residual\").").Single());
            var ret = CoreLibrary.Blocks.Single(b => b.Name == "return").Source;
            Assert.Contains("set fixed to claudia_fly_correction(ctx, addons:claudia:correctionburn(\n          return_with_inc(claudia_correction_ask(planet:name, opt:target_pe, when:ut), opt:target_inc))).", ret.Replace("\r\n", "\n"));
        }

        // ---- The Course Correction's burn time (#368): return's; transfer_to's is Core's TransferFlight (#459) ----

        // return asks Core's TransferFlight.CorrectionUt and Earliest through TRANSFERSTEP (#731).
        private static System.Collections.Generic.IDictionary<string, object> Timing(params object[] pairs)
        {
            var ask = new System.Collections.Generic.Dictionary<string, object>();
            for (var i = 0; i < pairs.Length; i += 2) ask[(string)pairs[i]] = pairs[i + 1];
            return ClaudiaKerman.Core.Tools.BlockCalls.TransferStep(ask, null);
        }

        private static string[] CorrectionUt(double mid, object maxDuration)
        {
            // Block started at UT 1000; the ejection is flown and it is now UT 3000; the craft needs 193 s to turn to and lead a
            // burn (transfer_to's 133 s TURNTIME plus a minute).
            var w = Timing("timing", "correction", "mid", mid, "now", 3000.0, "lead", 193.0, "start_ut", 1000.0, "max_duration", maxDuration);
            return new[] { ((double)w["ut"]).ToString(CultureInfo.InvariantCulture), (string)w["why"] };
        }

        [Fact]
        public void The_correction_burns_halfway_when_max_duration_is_auto_or_ends_after_it()
        {
            // Halfway to the encounter is UT 16500.
            Assert.Equal(new[] { "16500", "" }, CorrectionUt(16500, "auto"));
            Assert.Equal(new[] { "16500", "" }, CorrectionUt(16500, 20000.0));
        }

        [Fact]
        public void A_max_duration_that_admits_the_ejection_admits_its_correction_too_burning_it_sooner()
        {
            // max_duration ends at UT 11000, before halfway: the correction moves to 120 s before the end, not past it into the abort.
            Assert.Equal(new[] { "10880", "" }, CorrectionUt(16500, 10000.0));
            // Just enough: 120 s to burn after the 193 s lead.
            Assert.Equal(new[] { "3193", "" }, CorrectionUt(16500, 2313.0));
        }

        [Fact]
        public void A_max_duration_with_no_time_left_for_the_correction_fails_before_the_burn_with_a_reason()
        {
            Assert.Equal(new[] { "3180", "max_duration (2300 s) ends at UT 3300, too soon for the Course Correction: its burn needs 313 s from UT 3000" },
                CorrectionUt(16500, 2300.0));
        }

        [Fact]
        public void The_correction_asks_the_solver_for_a_burn_at_its_time()
        {
            Assert.Equal(new[] { "target,target_pe,earliest_ut,latest_ut", "Mun", "30000", "16500", "16500" },
                Run("local q is claudia_correction_ask(\"Mun\", 30000, 16500). print q:keys:join(\",\"). print q:target. print q:target_pe. print q:earliest_ut. print q:latest_ut."));
        }

        [Fact]
        public void A_broken_plane_s_plane_change_burns_at_its_planned_time_not_halfway()
        {
            // The correction time given plane_ut (UT 9000) in place of halfway: it stays there with max_duration auto or later,
            // and moves sooner only as the Course Correction does when max_duration ends first.
            Assert.Equal(new[] { "9000", "" }, CorrectionUt(9000, "auto"));
            Assert.Equal(new[] { "8880", "" }, CorrectionUt(9000, 8000.0));
        }

        // ---- Earliest departure ----

        /// <summary>return's rule (#346, #357), as Core's TransferFlight.Earliest is transfer_to's: the block's start plus the turn time, or a later window.</summary>
        [Theory]
        [InlineData("next", "1133")]
        [InlineData("2000", "2000")]
        [InlineData("1100", "1133")]
        public void The_departure_is_searched_from_once_the_craft_can_have_turned_or_from_the_window(string window, string earliest)
        {
            object w = window == "next" ? (object)"next" : double.Parse(window, CultureInfo.InvariantCulture);
            var ut = (double)Timing("timing", "earliest", "start_ut", 1000.0, "turn_time", 133.0, "window", w)["ut"];
            Assert.Equal(earliest, ut.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Return_asks_Core_for_both_timings_and_no_Kerboscript_twin_is_left()
        {
            var ret = CoreLibrary.Blocks.Single(b => b.Name == "return").Source;
            Assert.Contains("\"timing\", \"earliest\", \"start_ut\", ctx:start_ut", ret);
            Assert.Contains("\"timing\", \"correction\"", ret);
            var bundle = CoreLibrary.Bundle("");
            Assert.DoesNotContain("claudia_earliest", bundle);
            Assert.DoesNotContain("claudia_correction_ut", bundle);
            var transfer = CoreLibrary.Blocks.Single(b => b.Name == "transfer_to").Source;
            Assert.Contains("\"start_ut\", ctx:start_ut", transfer);
            Assert.Contains("\"turn_time\", turn:turn_time", transfer);
            Assert.Contains("\"window\", opt:window", transfer);
        }

        // ---- return ----

        // return's periapsis time was RSVP's time_at_periapsis; mean anomaly is zero at periapsis.
        [Theory]
        [InlineData(700000, 90, "0.75")]
        [InlineData(700000, 270, "0.25")]
        [InlineData(-9000000, -10, null)]
        public void The_next_periapsis_comes_when_the_mean_anomaly_reaches_zero(double a, double meanAnomaly, string orbits)
        {
            var orb = string.Format(CultureInfo.InvariantCulture,
                "lexicon(\"body\", lexicon(\"mu\", 3.5316e12), \"semimajoraxis\", {0}, \"meananomalyatepoch\", {1}, \"epoch\", 1000)", a, meanAnomaly);
            // A hyperbola's mean anomaly runs on from negative: 10 degrees short is 10 / 360 of a "period" at its mean motion.
            var fraction = orbits ?? (10 / 360.0).ToString(CultureInfo.InvariantCulture);
            var expected = string.Format(CultureInfo.InvariantCulture, "round(return_pe_ut({0}) - 1000 - {1} * 2 * 3.141592653589793 * sqrt(abs({2}) ^ 3 / 3.5316e12), 3)", orb, fraction, a);
            Assert.Equal("0", One(expected));
        }

        [Fact]
        public void Return_options_take_the_spec_defaults()
        {
            var printed = Run("local o is claudia_options(lexicon(), return_defaults(), \"auto\"). print o:error. print o:opt:target_pe. print o:opt:window. print o:opt:correction.");
            Assert.Equal(new[] { "", "35000", "next", "True" }, printed);
            Assert.Equal("", One("return_unreadable(claudia_options(lexicon(), return_defaults(), \"auto\"):opt)"));
            Assert.Equal("target_pe must be metres above 0, not 0", One("return_unreadable(claudia_options(lexicon(\"target_pe\", 0), return_defaults(), \"auto\"):opt)"));
        }

        private static string ReturnUnreadable(string options)
        {
            return One("return_unreadable(claudia_options(" + options + ", return_defaults(), \"auto\"):opt)");
        }

        /// <summary>#792: the block only passes the target and max_wait; Core picks the window and times the burn (ReturnBurnTests).</summary>
        [Fact]
        public void Return_passes_its_target_and_max_wait_to_the_ejection()
        {
            var source = CoreLibrary.Blocks.Single(b => b.Name == "return").Source;
            Assert.Contains("set burn_ask to lexicon(\"lat\", site:lat, \"lng\", site:lng, \"ground_alt\", max(0, ground_alt), \"max_wait\", opt:max_wait, \"max_duration\", opt:max_duration, \"start_ut\", ctx:start_ut, \"isp\", claudia_engine_isp(), \"craft\", craft).", source);
            Assert.Contains("addons:claudia:returnburn(return_with_inc(burn_ask, opt:target_inc))", source);
            Assert.Equal("auto", One("claudia_options(lexicon(), return_defaults(), \"auto\"):opt:max_wait"));
            Assert.Equal("35000", One("claudia_options(lexicon(), return_defaults(), \"auto\"):opt:target_pe"));
        }

        [Fact]
        public void An_explicit_target_stands_and_auto_alone_goes_to_the_mod()
        {
            Assert.Equal("ksc", One("claudia_landing_target(\"ksc\", lexicon())"));
            Assert.Equal("none", One("claudia_landing_target(\"none\", lexicon())"));
            Assert.Equal("3", One("claudia_landing_target(lexicon(\"lat\", 3, \"lng\", 4), lexicon()):lat"));
        }

        [Fact]
        public void Return_reads_its_target_as_auto_by_default()
        {
            Assert.Equal("auto|False", One("claudia_options(lexicon(), return_defaults(), \"auto\"):opt:target + \"|\" + return_aims(claudia_options(lexicon(), return_defaults(), \"auto\"):opt)"));
        }

        [Theory]
        [InlineData("lexicon(\"target\", \"ksc\")")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", -0.1, \"lng\", -74.5))")]
        [InlineData("lexicon(\"target\", \"ksc\", \"target_pe\", 40000, \"correction\", false)")]
        [InlineData("lexicon(\"target\", \"none\")")]
        public void Return_takes_the_targets_deorbit_takes(string options)
        {
            Assert.Equal("", ReturnUnreadable(options));
        }

        [Theory]
        [InlineData("lexicon(\"target\", \"kerbin\")", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not 'kerbin'")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 1))", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a lexicon without lat and lng")]
        [InlineData("lexicon(\"target\", lexicon(\"lat\", 95, \"lng\", 0))", "target's lat must be from -90 to 90, not 95")]
        [InlineData("lexicon(\"target\", 3)", "target must be LATLNG(lat, lng), lexicon(\"lat\", x, \"lng\", y) or \"ksc\", not a Scalar")]
        public void Return_refuses_a_target_it_cannot_read(string options, string reason)
        {
            Assert.Equal(reason, ReturnUnreadable(options));
        }

        [Fact]
        public void Return_asks_the_predictor_for_the_path_it_means_with_its_elements_and_the_target()
        {
            var stub = "local kerbin is lexicon(\"name\", \"Kerbin\"). local orb is lexicon(\"semimajoraxis\", 9e6, \"eccentricity\", 0.9, \"inclination\", 2, \"lan\", 3, \"argumentofperiapsis\", 4, \"meananomalyatepoch\", 5, \"epoch\", 6, \"body\", kerbin). ";
            var asked = Run(stub + "local q is return_impact_query(orb, 777, lexicon(\"lat\", -0.1, \"lng\", -74.5), -20, lexicon(\"why\", \"capsule\")). print q:keys:length. print q:from_ut. print q:lat. print q:lng. print q:ground_alt. print q:orbit_sma. print q:orbit_ecc. print q:orbit_inc. print q:orbit_lan. print q:orbit_argpe. print q:orbit_mna. print q:orbit_epoch. print q:orbit_body. print q:craft:why.");
            Assert.Equal(new[] { "13", "777", "-0.1", "-74.5", "0", "9000000", "0.9", "2", "3", "4", "5", "6", "Kerbin", "capsule" }, asked);
        }

        /// <summary>
        /// #814: the craft reentry will fly is read once, with the target, before any prediction: the ejection, the correction (through burn_ask) and
        /// both checks of the path (through the predict closure) carry that one answer, never a read of their own.
        /// </summary>
        [Fact]
        public void The_craft_reentry_flies_is_read_once_and_every_prediction_carries_it()
        {
            var source = CoreLibrary.Blocks.Single(b => b.Name == "return").Source.Replace("\r\n", "\n");
            var read = "local craft is addons:claudia:entrycraft(lexicon(\"core\", core:part:uid, \"stage_limit\", opt:stage_limit, \"body\", planet:name)).";
            Assert.Equal(1, source.Split("addons:claudia:entrycraft(").Length - 1);
            Assert.Contains(read, source);
            Assert.True(source.IndexOf(read, StringComparison.Ordinal) < source.IndexOf("set predict to {", StringComparison.Ordinal));
            Assert.Contains("\"isp\", claudia_engine_isp(), \"craft\", craft).", source);
            Assert.Contains("return addons:claudia:reentryimpact(return_impact_query(orb, from_ut, site, ground_alt, craft)).", source);
        }

        /// <summary>#793: Core aims the correction (AimedCorrectionTests); the block hands it the ejection's landing, the craft's Isp and its delta-v left, and no Kerboscript twin is left.</summary>
        [Fact]
        public void An_aimed_correction_asks_Core_for_the_landing_and_no_Kerboscript_twin_is_left()
        {
            Assert.Equal(new[] { "target,target_pe,earliest_ut,latest_ut,lat,lng,ground_alt,craft,isp,dv_left", "-0.1", "-74.5", "20", "345", "88", "capsule" },
                Run("local q is return_aimed_ask(claudia_correction_ask(\"Kerbin\", 30000, 5), lexicon(\"lat\", -0.1, \"lng\", -74.5, \"ground_alt\", 20, \"max_wait\", \"auto\", \"craft\", lexicon(\"why\", \"capsule\")), 345, 88). "
                    + "print q:keys:join(\",\"). print q:lat. print q:lng. print q:ground_alt. print q:isp. print q:dv_left. print q:craft:why."));
            var bundle = CoreLibrary.Bundle("");
            Assert.DoesNotContain("claudia_descend", bundle);
            Assert.DoesNotContain("return_aim_cost", bundle);
            Assert.DoesNotContain("return_aimed_correction", bundle);
        }

        // ---- return from any moon (#450) ----

        [Fact]
        public void Return_names_no_body()
        {
            Assert.DoesNotContain("Kerbin", CoreLibrary.Blocks.Single(b => b.Name == "return").Source);
        }

        [Fact]
        public void Return_takes_any_arrival_inclination_by_default()
        {
            Assert.Equal("any", One("claudia_options(lexicon(), return_defaults(), \"auto\"):opt:target_inc"));
            Assert.Equal("", ReturnUnreadable("lexicon(\"target_inc\", 30)"));
            Assert.Equal("", ReturnUnreadable("lexicon(\"target_inc\", 180)"));
        }

        [Theory]
        [InlineData("lexicon(\"target_inc\", 200)", "target_inc must be 'any' or degrees from 0 to 180, not 200")]
        [InlineData("lexicon(\"target_inc\", -1)", "target_inc must be 'any' or degrees from 0 to 180, not -1")]
        [InlineData("lexicon(\"target_inc\", \"polar\")", "target_inc must be 'any' or degrees from 0 to 180, not 'polar'")]
        [InlineData("lexicon(\"target_inc\", list())", "target_inc must be 'any' or degrees from 0 to 180, not a List")]
        public void Return_refuses_a_target_inc_it_cannot_read(string options, string reason)
        {
            Assert.Equal(reason, ReturnUnreadable(options));
        }

        [Fact]
        public void A_target_inc_given_goes_into_the_solvers_ask_and_any_leaves_it_out()
        {
            Assert.Equal(new[] { "target_pe,earliest_ut,target_inc", "30" },
                Run("local q is return_with_inc(lexicon(\"target_pe\", 35000, \"earliest_ut\", 1133), 30). print q:keys:join(\",\"). print q:target_inc."));
            Assert.Equal("target_pe,earliest_ut", One("return_with_inc(lexicon(\"target_pe\", 35000, \"earliest_ut\", 1133), \"any\"):keys:join(\",\")"));
        }

        /// <summary>
        /// #759: Core decides whether the path flown is the return asked (ReturnBurnTests pins the figures); here, that the block asks
        /// it last, with planet_pe already in the report, and fails on the answer. No path at all needs the correction without asking.
        /// </summary>
        [Fact]
        public void A_return_that_misses_what_was_asked_fails_with_planet_pe_in_its_script_result()
        {
            var source = CoreLibrary.Blocks.Single(b => b.Name == "return").Source;
            var pe = source.IndexOf("set extra:planet_pe to round(home:orbit:periapsis).", StringComparison.Ordinal);
            var asked = source.IndexOf("local miss is return_miss(home, opt).", StringComparison.Ordinal);
            var failed = source.IndexOf("return claudia_finish(ctx, false, miss, extra).", StringComparison.Ordinal);
            var succeeded = source.LastIndexOf("return claudia_finish(ctx, true, \"\", extra).", StringComparison.Ordinal);
            Assert.True(pe >= 0 && pe < asked && asked < failed && failed < succeeded, "planet_pe, then the check, then its failure, then success");
            Assert.Equal("no return to the planet", One("return_miss(\"none\", lexicon())"));
        }

        [Fact]
        public void The_time_from_a_radius_down_to_periapsis_follows_keplers_equation()
        {
            // Ellipse a = 10,000 km, e = 0.5, at r = a: E = 90 degrees, M = pi/2 - e.
            Assert.InRange(Number("return_time_to_pe(10000000, 0.5, 3.5316e12, 10000000)"), 18016, 18021);
            // Hyperbola a = -10,000 km, e = 1.5, at r = 12,500 km: cosh H = 1.5.
            Assert.InRange(Number("return_time_to_pe(-10000000, 1.5, 3.5316e12, 12500000)"), 12023, 12028);
        }

        [Fact]
        public void A_radius_the_orbit_never_comes_down_to_takes_no_time()
        {
            // Periapsis is at 5,000 km; the atmosphere top at 4,000 km is never reached.
            Assert.Equal("0", One("return_time_to_pe(10000000, 0.5, 3.5316e12, 4000000)"));
        }
    }
}
