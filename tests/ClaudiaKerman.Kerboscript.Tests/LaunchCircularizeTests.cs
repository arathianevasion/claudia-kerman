// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Kos;
using Xunit;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>The pure helpers of the launch and circularize Building Blocks, run on kOS.Safe through the real bundle.</summary>
    public class LaunchCircularizeTests
    {
        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        // ---- launch: options ----

        [Fact]
        public void Launch_options_take_the_spec_defaults()
        {
            Assert.Equal(new[] { "now", "auto", "0", "into_inc", "60", "0.45", "5", "none", "body", "body", "body", "auto", "" },
                Run("local c is claudia_options(lexicon(), launch_defaults(), \"auto\"). local o is c:opt.\n"
                    + "print o:launch_at. print o:target_ap. print o:target_inc. print o:heading. print o:turn_start_speed. print o:turn_shape.\n"
                    + "print o:max_aoa. print o:throttle_max_q. print o:g. print o:atm_height. print o:surface_pressure. print o:max_duration. print c:error."));
        }

        [Theory]
        [InlineData("lexicon()", "")]
        [InlineData("lexicon(\"launch_at\", 1000, \"target_ap\", 80000, \"heading\", 90, \"throttle_max_q\", 20000, \"g\", 9.81, \"atm_height\", 70000, \"surface_pressure\", 1)", "")]
        // A string launch_at other than "now" is a body's name, looked up when launch plans the wait (launch_when), not refused here.
        [InlineData("lexicon(\"launch_at\", \"Minmus\")", "")]
        [InlineData("lexicon(\"target_ap\", \"high\")", "target_ap must be 'auto' or m, not 'high'")]
        [InlineData("lexicon(\"heading\", \"lib_lazcalc\")", "")]
        [InlineData("lexicon(\"heading\", \"into_inc\")", "")]
        [InlineData("lexicon(\"heading\", \"east\")", "heading must be 'into_inc' or degrees, not 'east'")]
        [InlineData("lexicon(\"throttle_max_q\", \"off\")", "throttle_max_q must be 'none' or Pa, not 'off'")]
        [InlineData("lexicon(\"g\", \"kerbin\")", "g must be 'body' or a number, not 'kerbin'")]
        public void A_launch_option_string_it_does_not_know_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("launch_unreadable(claudia_options(" + options + ", launch_defaults(), \"auto\"):opt)"));
        }

        // ---- launch: the pitch program ----

        [Theory]
        [InlineData(500, "90")]
        [InlineData(1000, "90")]
        [InlineData(18250, "41.77")]
        [InlineData(70000, "0")]
        [InlineData(75000, "0")]
        public void The_pitch_falls_from_vertical_at_the_turn_start_to_flat_at_the_turn_end(double altitude, string pitch)
        {
            Assert.Equal(pitch, One("round(launch_pitch(" + altitude + ", 1000, 70000, 0.45), 2)"));
        }

        [Theory]
        [InlineData(11000, 12000, 0.5, "True")]
        [InlineData(10900, 12000, 0.5, "True")]
        [InlineData(10700, 12000, 0.5, "False")]
        [InlineData(14000, 12000, 0.05, "False")]
        [InlineData(30000, -1, 0.5, "False")]
        public void The_turn_waits_while_throttle_max_q_eases_the_throttle_in_thick_air(double q, double maxQ, double pressureRatio, string held)
        {
            Assert.Equal(held, One("launch_turn_held(" + q + ", " + maxQ + ", " + pressureRatio + ")"));
        }

        // ---- launch: the throttle ----

        [Theory]
        [InlineData(50000, 0, -1, 0, "1")]
        [InlineData(79000, 0, -1, 0, "0.5")]
        [InlineData(79000, 0, -1, 0.7, "0.7")]
        [InlineData(80000, 0, -1, 0.7, "0")]
        [InlineData(81000, 0, -1, 0.7, "0")]
        [InlineData(50000, 15000, 20000, 0, "1")]
        [InlineData(50000, 20000, 20000, 0, "0.5")]
        [InlineData(50000, 22000, 20000, 0, "0.05")]
        [InlineData(50000, 22000, 20000, 0.6, "0.6")]
        [InlineData(50000, 90000, -1, 0, "1")]
        public void The_throttle_eases_to_the_target_ap_and_under_max_q_but_only_the_target_ap_cuts_it_off(double ap, double q, double maxQ, double floor, string throttle)
        {
            Assert.Equal(throttle, One("round(launch_throttle(" + ap + ", 80000, 2000, " + q + ", " + maxQ + ", " + floor + "), 3)"));
        }

        // #234: no trickle just under the target; the cutoff band (half the top-up band) is inside it.
        [Theory]
        [InlineData(79999, 0, -1, 0, "0")]
        [InlineData(79900, 0, -1, 0, "0")]
        [InlineData(79800, 0, -1, 0, "0")]
        [InlineData(79700, 0, -1, 0, "0.15")]
        [InlineData(79000, 20000, 20000, 0, "0.5")]
        public void The_throttle_cuts_off_within_the_cutoff_band_of_the_target_ap_instead_of_trickling(double ap, double q, double maxQ, double floor, string throttle)
        {
            Assert.Equal(throttle, One("round(launch_throttle(" + ap + ", 80000, 2000, " + q + ", " + maxQ + ", " + floor + "), 3)"));
        }

        [Theory]
        [InlineData(80000, "200")]
        [InlineData(10000, "50")]
        public void The_cutoff_band_is_half_the_top_up_band(double targetAp, string cutoff)
        {
            Assert.Equal(cutoff, One("launch_cutoff_tol(" + targetAp + ")"));
            Assert.Equal("True", One("launch_cutoff_tol(" + targetAp + ") < launch_topup_tol(" + targetAp + ")"));
        }

        [Theory]
        [InlineData(0, "True")]
        [InlineData(0.02, "False")]
        [InlineData(0.05, "True")]
        [InlineData(1, "True")]
        public void Stock_delta_v_is_trusted_at_zero_throttle_or_from_five_percent(double throttle, string trusted)
        {
            Assert.Equal(trusted, One("claudia_dv_trusted(" + throttle + ")"));
        }

        [Theory]
        [InlineData(0.9, 6.4, 9.81, 167.97, "0.374")]
        [InlineData(0.9, 20, 9.81, 100, "1")]
        [InlineData(0.05, 6.4, 9.81, 167.97, "0")]
        [InlineData(0.9, 6.4, 9.81, 0, "0")]
        public void In_thick_air_the_throttle_floor_keeps_thrust_at_least_equal_to_weight(double pressureRatio, double mass, double g, double thrust, string floor)
        {
            Assert.Equal(floor, One("round(launch_throttle_floor(" + pressureRatio + ", " + mass + ", " + g + ", " + thrust + "), 3)"));
        }

        // ---- launch: the ascent, flown offline (launch_ascent_sim.ks) ----

        // launch_no_flip's ascent (target_ap 60 km, throttle_max_q 12 kPa) in the offline sim, with the
        // turn hold on or off: the result lines by their first word, and the whole printout.
        private static (System.Collections.Generic.Dictionary<string, string> Result, string Track) Ascent(bool hold, double dragArea, double maxQ = 12000, double targetAp = 60000, bool thin = true, double thrustScale = 1, double dry = 2.4)
        {
            var sim = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "launch_ascent_sim.ks"));
            var lines = Run(sim + "\nsim_ascent(lexicon(\"target_ap\", " + targetAp + ", \"max_q\", " + maxQ
                + ", \"turn_start_speed\", 60, \"turn_shape\", 0.45, \"max_aoa\", 5, \"drag_area\", " + dragArea + ", \"hold\", " + (hold ? "true" : "false") + ", \"thin\", " + (thin ? "true" : "false") + ", \"thrust_scale\", " + thrustScale + ", \"dry\", " + dry + ")).");
            var result = lines.Where(l => !l.StartsWith("track ")).ToDictionary(l => l.Split(' ')[0], l => l.Substring(l.IndexOf(' ') + 1));
            return (result, string.Join("\n", lines));
        }

        // #235: the pitch program turned a q-limited craft over by altitude while its thrust, held to about
        // its weight, couldn't hold the path up; the path fell over and the craft stalled near 10 km.
        [Theory]
        [InlineData(0.5)]
        [InlineData(1.0)]
        public void Without_the_turn_hold_a_q_limited_craft_stalls_low_in_the_climb(double dragArea)
        {
            var (result, track) = Ascent(false, dragArea);
            Assert.True(result["stalled"].StartsWith("True"), track);
            var at = double.Parse(result["stalled"].Split(' ').Last());
            Assert.InRange(at, 8000, 14000);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(1.0)]
        [InlineData(1.5)]
        public void With_the_turn_hold_a_q_limited_craft_climbs_to_its_cutoff_without_leaving_the_airflow(double dragArea)
        {
            var (result, track) = Ascent(true, dragArea);
            Assert.True(result["stalled"].StartsWith("False"), track);
            Assert.True(double.Parse(result["coast_ap"]) >= 59700, track);
            Assert.True(double.Parse(result["max_q"]) < 15000, track);
            Assert.True(double.Parse(result["min_twr"]) > 0.95, track);
            // The nose never runs ahead of (below) the prograde by more than max_aoa.
            Assert.True(double.Parse(result["max_lead"]) <= 5, track);
        }

        [Fact]
        public void Without_throttle_max_q_the_turn_hold_changes_nothing()
        {
            Assert.Equal(Ascent(false, 0.5, -1, 80000).Track, Ascent(true, 0.5, -1, 80000).Track);
        }

        // #236: in thin air the pitch is held no lower than 3 degrees under the surface-prograde pitch.
        [Theory]
        [InlineData(30, 60, 100, 3, "57")]
        [InlineData(58, 60, 100, 3, "58")]
        [InlineData(70, 60, 100, 3, "70")]
        [InlineData(30, 60, 500, 3, "57")]
        [InlineData(30, 60, 501, 3, "30")]
        [InlineData(30, 60, 20000, 3, "30")]
        public void In_thin_air_the_pitch_is_held_within_a_few_degrees_of_the_prograde(double pitch, double prograde, double q, double lead, string held)
        {
            Assert.Equal(held, One("launch_thin_pitch(" + pitch + ", " + prograde + ", " + q + ", " + lead + ")"));
        }

        // #531: the climb's trace names its phase as plan_ascent's sim does; coast wins, then the turn not started, the hold, the thin-air rule.
        [Theory]
        [InlineData("true", "true", "true", "true", "coast")]
        [InlineData("false", "false", "false", "false", "vertical")]
        [InlineData("false", "true", "true", "true", "turn_held")]
        [InlineData("false", "true", "false", "true", "thin_air")]
        [InlineData("false", "true", "false", "false", "turn")]
        public void The_trace_names_the_phase_of_the_climb(string coasting, string started, string held, string thin, string phase)
        {
            Assert.Equal(phase, One("launch_phase(" + coasting + ", " + started + ", " + held + ", " + thin + ")"));
        }

        // #531: a line every 2 s, and at a change of phase or stage, but no more than one in 0.25 s for a change.
        [Theory]
        [InlineData("\"turn\", \"vertical\", 3, 3, 100, 99.9", "")]
        [InlineData("\"turn\", \"turn\", 3, 3, 102, 100", "tick")]
        [InlineData("\"turn\", \"vertical\", 3, 3, 100.3, 100", "phase")]
        [InlineData("\"turn\", \"turn\", 2, 3, 100.3, 100", "stage")]
        [InlineData("\"thin_air\", \"turn\", 3, 3, 100.1, 100", "")]
        public void The_trace_is_due_every_two_seconds_and_at_a_change(string arguments, string why)
        {
            Assert.Equal(why, One("launch_trace_due(" + arguments + ")"));
        }

        // A slow (70% thrust) craft to a 400 km apoapsis is still burning above 40 km, in the thin air. It has more to burn
        // (2 t dry) since the sim's Coriolis (#370) costs it what it had spare.
        [Fact]
        public void Without_the_thin_air_cap_the_nose_runs_well_below_the_prograde_up_high_and_with_it_stays_within_a_few_degrees()
        {
            var (free, freeTrack) = Ascent(true, 0.5, 12000, 400000, false, 0.7, 2.0);
            Assert.True(double.Parse(free["max_thin_lead"]) > 20, freeTrack);
            var (capped, track) = Ascent(true, 0.5, 12000, 400000, true, 0.7, 2.0);
            Assert.True(double.Parse(capped["max_thin_lead"]) <= 3.1, track);
            Assert.True(capped["stalled"].StartsWith("False"), track);
            Assert.True(double.Parse(capped["coast_ap"]) >= 397000, track);
        }

        // ---- launch: heading and the plane ----

        // The heading is Core's (ADDONS:CLAUDIA:LAUNCHHEADING, #475). launch flies it in game (launch_orbit).
        [Fact]
        public void The_heading_comes_from_Core()
        {
            Assert.Contains("addons:claudia:launchheading(", CoreLibrary.Files.Single(f => f.Name == "launch").Source);
            Assert.DoesNotContain("LAZcalc", CoreLibrary.Bundle(""));
            Assert.DoesNotContain("LAZcalc", CoreLibrary.FullDocs() + CoreLibrary.Summaries());
        }

        // The window itself is ADDONS:CLAUDIA:LAUNCHWINDOW's (C#, LaunchWindowTests); launch_apply_window takes its lexicon into the plan.
        [Theory]
        [InlineData("lexicon(\"error\", \"\", \"wait\", 3591.6, \"inc\", -6)", "false", "3591.6 -6 ")]
        [InlineData("lexicon(\"error\", \"\", \"wait\", 3591.6, \"inc\", -6)", "true", "3591.6 0 ")]
        [InlineData("lexicon(\"error\", \"the pad never passes under a plane of inclination 6\", \"wait\", 0, \"inc\", 0)", "false", "0 0 launch_at Minmus: the pad never passes under a plane of inclination 6")]
        public void The_window_sets_the_wait_and_unless_target_inc_was_given_the_inclination(string window, string incGiven, string expected)
        {
            var printed = Run("local plan is lexicon(\"error\", \"\", \"wait\", 0, \"inc\", 0). set plan to launch_apply_window(plan, " + window + ", \"Minmus\", " + incGiven
                + "). print plan:wait + \" \" + plan:inc + \" \" + plan:error.");
            Assert.Equal(expected, printed[0]);
        }

        // ---- launch: angle off the airflow ----

        [Theory]
        [InlineData(5, 100, 8000, "5")]
        [InlineData(5, 8000, 8000, "5")]
        [InlineData(5, 16000, 8000, "2.5")]
        [InlineData(5, 32000, 8000, "1.25")]
        [InlineData(5, 400000, 8000, "0.5")]
        [InlineData(0, 16000, 8000, "0")]
        public void The_allowed_angle_off_the_airflow_falls_with_dynamic_pressure_above_the_reference(double maxAoa, double q, double qRef, string allowed)
        {
            Assert.Equal(allowed, One("round(launch_allowed_aoa(" + maxAoa + ", " + q + ", " + qRef + "), 3)"));
        }

        // #676: while burning, the limit toward the airflow keeps the aim in the heading's vertical plane. Turning
        // straight from the airflow (launch_limit_aoa) kept the airflow's own sideways drift; a fast turn holds the
        // limit from the turn start, where a metre a second sideways is a degree off the plane, and the drift grew
        // with the climb (in a 3D point-mass sim of efe0's craft at turn_shape 0.2, 1 m/s north at the turn start
        // ended at 1.4 degrees of inclination; kept in the plane, 0.1). The aim's pitch in the plane is the nearest
        // to want_pitch that is max_aoa off an airflow whose projection on the plane is at flow_pitch and which is
        // off_plane degrees out of it: within flow_pitch +- arccos(cos(max_aoa) / cos(off_plane)).
        [Theory]
        [InlineData(85, 88, 0, 5, "85")]
        [InlineData(60, 89, 0, 5, "84")]
        [InlineData(60, 89, 3, 5, "85")]
        [InlineData(60, 89, 4.9, 5, "88")]
        [InlineData(80, 70, 0, 5, "75")]
        [InlineData(30, 95, 1, 2.5, "92.71")]
        public void The_aim_held_toward_the_airflow_stays_in_the_heading_plane(double wantPitch, double flowPitch, double offPlane, double maxAoa, string pitch)
        {
            Assert.Equal(pitch, One("round(launch_plane_pitch(" + wantPitch + ", " + flowPitch + ", " + offPlane + ", " + maxAoa + "), 2)"));
        }

        // The guidance pass uses it while burning, from the airflow's pitch in the plane and its angle out of it, and
        // keeps launch_limit_aoa for the coast; an airflow further out of the plane than the angle allowed is turned
        // toward the plane. kOS.Safe has no vectors, so the call sites are pinned by source (as KeepRollTests does).
        [Fact]
        public void The_climb_limits_the_aim_in_the_heading_plane_and_the_coast_toward_the_airflow()
        {
            var src = System.Text.RegularExpressions.Regex.Replace(CoreLibrary.Files.Single(f => f.Name == "launch").Source, "\\s+", " ");
            Assert.Contains("launch_plane_pitch(pitch, arctan2(vdot(flow, ship:up:vector), vdot(flow, along)), off_plane, allowed)", src);
            Assert.Contains("if coasting or burn_through { set want to launch_limit_aoa(want, flow, allowed). }", src);
            Assert.Contains("set want to launch_limit_aoa(vxcl(normal, flow), flow, allowed).", src);
        }

        // #932: while burning through, Core is asked again each tick (its stage figures at most a second old) until it says the coast works: coast_ap latches
        // that end. The default max_duration grows with the burn-through's own time; one given by the caller doesn't.
        [Fact]
        public void A_burn_through_is_re_checked_each_tick_and_its_time_is_added_to_the_default_limit()
        {
            var src = System.Text.RegularExpressions.Regex.Replace(CoreLibrary.Files.Single(f => f.Name == "launch").Source, "\\s+", " ");
            Assert.Contains("if tset = 0 or burn_through { //", src);
            Assert.Contains("if extra:coast_ap = 0 { if time:seconds >= burn_figures:due {", src);
            Assert.Contains("lexicon(\"burning\", burn_through, \"vspeed\", ship:verticalspeed, \"pe\", ship:periapsis, \"ap\", ap,", src);
            // #936: Core ends the burn-through (the periapsis clears the air, or it fell back into it, with a reason).
            Assert.Contains("set reason to bt:fail.", src);
            Assert.Contains("set done to bt:done. if done { return. }", src);
            Assert.Contains("local auto_limit is opt:max_duration:istype(\"String\"). if auto_limit { set opt:max_duration to go:wait + 900. }", src);
            Assert.Contains("if burn_through and auto_limit { set opt:max_duration to opt:max_duration + pass_gap. }", src);
        }

        // ---- launch: losing control ----

        [Theory]
        [InlineData("true", 0.374, 12345.6, 153.2, 150.4, 9642.7,
            "lost control: 153 degrees off the ascent path at 9643 m, burning at 37% throttle, q 12346 Pa, 150 degrees off the airflow")]
        [InlineData("false", 0, 31000, 40.6, 39.5, 4860.2,
            "lost control: 41 degrees off the ascent path at 4860 m, coasting at 0% throttle, q 31000 Pa, 40 degrees off the airflow")]
        [InlineData("false", 1, 20000, 57, 52, 9303,
            "lost control: 57 degrees off the ascent path at 9303 m, coasting at 100% throttle, q 20000 Pa, 52 degrees off the airflow")]
        public void The_lost_control_reason_says_whether_it_was_burning_or_coasting_with_throttle_q_and_the_angle_off_the_airflow(
            string burning, double throttle, double q, double offAim, double offFlow, double altitude, string reason)
        {
            Assert.Equal(reason, One("launch_lost_control(" + burning + ", " + throttle + ", " + q + ", " + offAim + ", " + offFlow + ", " + altitude + ")"));
        }

        // ---- circularize ----

        // A 70 x 100 km Kerbin orbit: sma 685 km, ecc 15/685.
        private const string Kerbin = "3.5316e12";
        private const string Sma = "685000";
        private const string Ecc = "(15000 / 685000)";

        [Theory]
        [InlineData("700000", "true", "24.7", "0")]
        [InlineData("670000", "true", "-25", "0")]
        [InlineData("685000", "true", "-0.5", "-49.7")]
        [InlineData("685000", "false", "-0.5", "49.7")]
        public void The_burn_turns_the_velocity_at_that_point_into_circular_speed_along_the_horizon(string r, string climbing, string prograde, string radial)
        {
            var printed = Run("local b is circularize_burn(" + Kerbin + ", " + r + ", " + Sma + ", " + Ecc + ", " + climbing + "). print round(b:prograde, 1). print round(b:radial, 1).");
            Assert.Equal(new[] { prograde, radial }, printed);
        }

        [Theory]
        [InlineData("670000", "0")]
        [InlineData("685000", "91.25")]
        [InlineData("700000", "180")]
        [InlineData("710000", "-1")]
        [InlineData("660000", "-1")]
        public void The_orbit_passes_an_altitude_at_one_true_anomaly_each_way_or_never(string r, string anomaly)
        {
            Assert.Equal(anomaly, One("round(circularize_anomaly(" + r + ", " + Sma + ", " + Ecc + "), 2)"));
        }

        [Theory]
        [InlineData(0, 90, 0, "1000")]
        [InlineData(90, 0, 0, "3000")]
        [InlineData(0, 90, 0.1, "872.9")]
        public void The_time_to_a_true_anomaly_follows_keplers_equation(double now, double target, double ecc, string seconds)
        {
            Assert.Equal(seconds, One("round(circularize_time_to(" + now + ", " + target + ", " + ecc + ", 4000), 1)"));
        }

        [Fact]
        public void Circularize_takes_ap_pe_or_an_altitude()
        {
            Assert.Equal("", One("circularize_unreadable(\"ap\")"));
            Assert.Equal("", One("circularize_unreadable(\"pe\")"));
            Assert.Equal("", One("circularize_unreadable(90000)"));
            Assert.Equal("at must be 'ap', 'pe' or an altitude in m, not 'apo'", One("circularize_unreadable(\"apo\")"));
        }

        [Fact]
        public void Circularize_options_take_the_spec_defaults()
        {
            Assert.Equal(new[] { "ap", "0.01", "auto", "" },
                Run("local c is claudia_options(lexicon(), circularize_defaults(), \"auto\"). print c:opt:at. print c:opt:tolerance. print c:opt:max_duration. print c:error."));
        }

        // #532: ecc as before, plus which stop ended the burn and the node's residual; #577: and whether it finished short.
        [Fact]
        public void Circularize_results_are_ecc_stop_residual_and_finished_short_and_skipped()
        {
            Assert.Equal(new[] { "ecc=0.12346", "stop=", "residual=-1", "finished_short=False", "skipped=False", "5" },
                Run("local x is circularize_extra(0.123456). for k in list(\"ecc\", \"stop\", \"residual\", \"finished_short\", \"skipped\") { print k + \"=\" + x[k]. } print x:keys:length."));
        }

        [Fact]
        public void Circularize_finishes_a_short_burn_within_five_percent_by_default()
        {
            Assert.Equal("0.05", One("claudia_options(lexicon(), circularize_defaults(), \"auto\"):opt:finish_fraction"));
        }

        // #577: an orbit over tolerance still finishes the circularization when both apsides are within the fraction of the
        // burn point's radius (from Kerbin's centre, 600 km) and the periapsis clears the 70 km atmosphere.
        [Theory]
        // The ticket's circularize failures, ecc 0.0189 at an 840 km radius: apsides about 16 km either side.
        [InlineData(255900, 224100, 840000, 0.05, "True")]
        // On the fraction's edge (42 km of 840 km) both ways, and just past it.
        [InlineData(282000, 198000, 840000, 0.05, "True")]
        [InlineData(282001, 240000, 840000, 0.05, "False")]
        [InlineData(240000, 197999, 840000, 0.05, "False")]
        // Within the fraction but the periapsis is in the atmosphere: not finished.
        [InlineData(100000, 69000, 680000, 0.05, "False")]
        // An escape orbit's apoapsis is negative.
        [InlineData(-900000, 240000, 840000, 0.05, "False")]
        // finish_fraction 0 never finishes short.
        [InlineData(240001, 240000, 840000, 0, "False")]
        public void A_circularization_over_tolerance_is_finished_when_both_apsides_are_within_the_fraction_of_the_target(
            double ap, double pe, double rad, double fraction, string expected)
        {
            Assert.Equal(expected, One("circularize_near(" + ap + ", " + pe + ", 600000, " + rad + ", " + fraction + ", 70000)"));
        }

        // ---- exec_node: finishing a short burn (#577) ----

        [Fact]
        public void Exec_node_finishes_a_short_burn_within_five_percent_by_default()
        {
            Assert.Equal("0.05", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt:finish_fraction"));
        }

        [Theory]
        // Within max_residual: done.
        [InlineData(0.4, 120, 0.5, 0.05, "done")]
        [InlineData(0.5, 120, 0.5, 0.05, "done")]
        // Over max_residual, within 5% of the node (6 m/s of 120): short, a trim finishes it.
        [InlineData(3, 120, 0.5, 0.05, "short")]
        [InlineData(6, 120, 0.5, 0.05, "short")]
        // f623's ejection: 24.7 m/s left of an 854 m/s node is 2.9%, within 42.7 m/s.
        [InlineData(24.7, 854, 0.5, 0.05, "short")]
        // Over the fraction: a failure, as before.
        [InlineData(6.01, 120, 0.5, 0.05, "over")]
        [InlineData(20, 120, 0.5, 0.05, "over")]
        // finish_fraction 0 keeps the old rule.
        [InlineData(0.6, 120, 0.5, 0, "over")]
        public void A_burn_ending_over_max_residual_but_within_the_fraction_of_its_node_is_short_and_past_it_over(
            double residual, double nodeDv, double maxResidual, double fraction, string expected)
        {
            Assert.Equal(expected, One("exec_node_finish(" + residual + ", " + nodeDv + ", " + maxResidual + ", " + fraction + ")"));
        }

        [Theory]
        // About 5 s at full acceleration: 3 m/s at 40 m/s^2 is 1.5%.
        [InlineData(3, 40, 1, 2, "0.015")]
        // Never under 0.5%, never over full.
        [InlineData(0.05, 40, 0, 2, "0.005")]
        [InlineData(100, 5, 0, 2, "1")]
        // Full at no thrust, so the tick stages into the next engine.
        [InlineData(3, 0, 0, 2, "1")]
        // Off by more than the pointing limit: not lit.
        [InlineData(3, 40, 2.5, 2, "0")]
        public void A_trim_burns_at_a_low_throttle_once_pointed_within_the_limit(double dvLeft, double maxAccel, double angleOff, double maxAngle, string expected)
        {
            Assert.Equal(expected, One("round(claudia_trim_throttle(" + dvLeft + ", " + maxAccel + ", " + angleOff + ", " + maxAngle + "), 4)"));
        }

        // #532: exec_node's options for a caller that doesn't ask for the eccentricity stop are as before.
        [Fact]
        public void Exec_node_keeps_only_the_nodes_stops_unless_asked_and_refuses_a_stop_it_does_not_know()
        {
            Assert.Equal("", One("claudia_options(lexicon(), exec_node_defaults(), \"auto\"):opt:stop_at_min"));
            Assert.Equal("", One("exec_node_unreadable(claudia_options(lexicon(\"stop_at_min\", \"ecc\"), exec_node_defaults(), \"auto\"):opt)"));
            Assert.Equal("stop_at_min must be '' or 'ecc', not 'pe'",
                One("exec_node_unreadable(claudia_options(lexicon(\"stop_at_min\", \"pe\"), exec_node_defaults(), \"auto\"):opt)"));
        }

        // #532: circularize's burn stops once the eccentricity stops falling: the index of the reading it stops on, or -1.
        [Theory]
        // Falling to circular, then rising past the 1e-4 band two readings running: it stops on the second rise.
        [InlineData("0.5, 0.3, 0.1, 0.02, 0.005, 0.0052, 0.012, 0.03", "6")]
        [InlineData("0.5, 0.3, 0.1, 0.02, 0.005, 0.012, 0.03, 0.05", "6")]
        // Noise inside the band never stops it, nor does a flat stretch (a turn, a staging gap).
        [InlineData("0.3, 0.2, 0.2, 0.2, 0.10005, 0.1, 0.10008, 0.10009, 0.1", "-1")]
        // A single noisy reading past the band, or a kick (staging) that the burn then brings down, doesn't stop it.
        [InlineData("0.3, 0.2, 0.25, 0.15, 0.1", "-1")]
        [InlineData("0.3, 0.2, 0.21, 0.21, 0.209, 0.15", "-1")]
        // Rising from the first reading: the lowest is the first.
        [InlineData("0.01, 0.02, 0.03", "2")]
        public void The_burn_stops_once_the_eccentricity_has_risen_two_readings_running_past_a_noise_band(string readings, string expected)
        {
            Assert.Equal(expected, Run("local seen is exec_node_min_seen(). local at is -1. local i is 0.\n"
                + "for e in list(" + readings + ") { if at < 0 and exec_node_past_min(seen, e, 0.0001) { set at to i. } set i to i + 1. }\n"
                + "print at.").Single());
        }

        /// <summary>printed, rounded to as many decimals as expected has.</summary>
        private static string RoundedLike(string printed, string expected)
        {
            var decimals = expected.Contains(".") ? expected.Split('.')[1].Length : 0;
            return System.Math.Round(double.Parse(printed, System.Globalization.CultureInfo.InvariantCulture), decimals).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
