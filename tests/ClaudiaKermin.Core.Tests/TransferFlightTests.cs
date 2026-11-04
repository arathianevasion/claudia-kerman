// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// transfer_to's flight in Core (#459): <see cref="TransferFlight"/> flown step by step on <see cref="TransferRig"/>, to a moon
    /// of the craft's body as before (the Mun and Minmus from Kerbin) and to a body sharing its parent (Duna from Kerbin, Minmus
    /// from the Mun); the window, the ejection and the arrival; and the pure rules moved from the Kerboscript.
    /// </summary>
    public sealed class TransferFlightTests
    {
        /// <summary>A Kerbin day, s.</summary>
        private const double Day = 21600;

        private static TransferFlightAsk Ask(string target, double pe, double? window = null, bool correction = true, double? maxDuration = null)
        {
            return new TransferFlightAsk
            {
                Target = target, TargetPe = pe, Window = window, Correction = correction, StartUt = 0, TurnTime = 60, DvLeft = 5000,
                MaxDuration = maxDuration, Auto = !maxDuration.HasValue,
            };
        }

        private static double D(TransferFlightStep step, string key)
        {
            return Convert.ToDouble(step.Report[key]);
        }

        // ---- Kerbin to Duna ----

        [Fact]
        public void Kerbin_to_Duna_flies_from_the_parking_orbit_to_Duna_s_sphere_of_influence_with_the_periapsis_set()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Duna", 50000));
            Assert.Equal("", step.Error);
            // The first Duna window of a new game is about day 235, at about 1,030 m/s from low Kerbin orbit (TransferWindowsTests).
            Assert.InRange(D(step, "departure_ut") / Day, 220, 245);
            Assert.InRange(rig.Burned("ejection"), 1030 * 0.95, 1030 * 1.07);
            Assert.InRange(D(step, "target_pe_actual"), 49000, 51000);
            Assert.InRange(D(step, "encounter_ut"), D(step, "departure_ut") + 150 * Day, D(step, "departure_ut") + 400 * Day);
            Assert.True(D(step, "arrival_ut") > D(step, "encounter_ut"));
            Assert.InRange(D(step, "v_inf"), 300, 1500);
            Assert.Equal("coast to encounter_ut + 60 before a capture", step.Report["next"]);
            // It ends on the Sun's patch, before the arrival: the capture is circularize's.
            Assert.Equal("Sun", rig.Body.Name);
            Assert.True(rig.Ut < D(step, "encounter_ut"));
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.0, 0.05)]
        [InlineData(0.001, 0.05)]
        [InlineData(-0.001, 0.05)]
        public void The_heliocentric_correction_of_an_ejection_flown_close_to_its_node_is_under_20_m_s(double burnError, double residual)
        {
            // An unperturbed ejection, and ones flown 1 m/s long or short, every burn left a little short as exec_node leaves it.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = burnError;
            rig.Residual = residual;
            var first = TransferFlight.Next(rig.Snapshot(), Ask("Duna", 50000));
            Assert.Equal("ejection", first.Kind);
            var step = rig.Run(Ask("Duna", 50000));
            Assert.True(step.Error == "", step.Error + " | " + string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value)));
            Assert.InRange(rig.Burned("correction"), 0, 20);
            Assert.InRange(D(step, "target_pe_actual"), 49000, 51000);
        }

        [Fact]
        public void A_window_UT_is_re_solved_over_a_narrow_span_round_it()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var planned = TransferWindows.Find(rig.Snapshot(), "Duna", 50000, true, 15).Optimal.DepartureUt;
            var step = TransferFlight.Next(rig.Snapshot(), Ask("Duna", 50000, planned));
            Assert.Equal("", step.Error);
            Assert.Equal("ejection", step.Kind);
            // Within the span (1 % of the synodic period, about 9 days, each way) and a parking orbit's pass of it.
            Assert.InRange(step.Ut, planned - 10 * Day, planned + 10 * Day);
            // A window far past the next one: its own, not the next.
            var later = TransferFlight.Next(rig.Snapshot(), Ask("Duna", 50000, planned + 2.1 * 426 * Day));
            Assert.Equal("", later.Error);
            Assert.True(later.Ut > planned + 400 * Day);
        }

        [Fact]
        public void Kerbin_to_Duna_without_correction_ends_at_the_ejection_s_encounter()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Duna", 50000, correction: false));
            Assert.Equal("", step.Error);
            Assert.Equal(0, rig.Burned("correction"));
            Assert.True(D(step, "encounter_ut") > 0);
        }

        // ---- The Mun to Minmus ----

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.01, 0.05)]
        public void The_Mun_to_Minmus_finds_the_window_ejects_and_arrives_at_the_periapsis(double burnError, double residual)
        {
            var rig = TransferRig.Circular("Mun", 30000, 0);
            rig.BurnError = burnError;
            rig.Residual = residual;
            var step = rig.Run(Ask("Minmus", 30000));
            Assert.True(step.Error == "", step.Error + " | " + string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value)));
            // Window: inside one synodic period of the two moons (about 1.8 days).
            Assert.InRange(D(step, "departure_ut"), 0, Transfer.Synodic(rig.System.OrbitOf(rig.System.Find("Mun")).Period, rig.System.OrbitOf(rig.System.Find("Minmus")).Period));
            // Ejection: a Hohmann from the Mun's orbit to Minmus's leaves at about 140 m/s; from 30 km the Oberth burn is about 250 m/s.
            Assert.InRange(rig.Burned("ejection"), 200, 350);
            Assert.InRange(rig.Burned("correction"), 0, 20);
            // Arrival, with the periapsis set: on Kerbin's patch, or inside Minmus's sphere of influence after a finer correction.
            Assert.InRange(D(step, "target_pe_actual"), 29000, 31000);
            Assert.Contains(rig.Body.Name, new[] { "Kerbin", "Minmus" });
            Assert.True(rig.Ut < D(step, "arrival_ut"));
            Assert.True(D(step, "v_inf") > 0);
        }

        [Fact]
        public void The_Mun_to_Minmus_waits_out_of_the_Mun_s_sphere_of_influence_before_its_correction()
        {
            var rig = TransferRig.Circular("Mun", 30000, 0);
            var ask = Ask("Minmus", 30000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(TransferFlightStep.Burn, eject.Do);
            ask.LastDv = rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            TransferFlight.Carry(ask, eject);
            var wait = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(TransferFlightStep.Wait, wait.Do);
            rig.MoveTo(wait.Ut);
            Assert.Equal("Kerbin", rig.Body.Name);
        }

        // ---- A moon of the craft's body, as before ----

        [Fact]
        public void Kerbin_to_the_Mun_flies_the_ejection_and_ends_before_the_arrival()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Mun", 30000));
            Assert.Equal("", step.Error);
            // transfer_to's block test contract: about a Hohmann transfer's 842 m/s, at most a synodic period's wait.
            Assert.InRange(rig.Burned("ejection"), 800, 900);
            Assert.InRange(D(step, "ejection_wait"), 0, 2300);
            Assert.InRange(D(step, "target_pe_actual"), 29000, 31000);
            Assert.Equal("Kerbin", rig.Body.Name);
            Assert.Equal("coast to encounter_ut + 60 before a capture", step.Report["next"]);
        }

        [Fact]
        public void An_ejection_that_misses_gets_the_correction_that_makes_the_encounter()
        {
            // 3 % short: no Mun encounter, so the correction after a missed ejection (#363) makes one.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = -0.03;
            var step = rig.Run(Ask("Mun", 30000));
            Assert.Equal("", step.Error);
            Assert.True(rig.Burned("correction") > 1);
            Assert.InRange(D(step, "target_pe_actual"), 29000, 31000);
        }

        [Fact]
        public void A_body_that_shares_neither_the_craft_s_body_nor_its_parent_is_refused()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = TransferFlight.Next(rig.Snapshot(), Ask("Ike", 30000));
            Assert.Equal("Ike does not orbit Kerbin, the body the craft orbits, or Sun", step.Error);
            Assert.Equal(TransferFlightStep.Done, step.Do);
        }

        [Fact]
        public void The_block_call_maps_the_step_to_a_lexicon_and_back()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var input = new Dictionary<string, object>
            {
                { "target", "Mun" }, { "target_pe", 30000.0 }, { "window", "next" }, { "correction", true }, { "pe_tolerance", 1000.0 },
                { "start_ut", 0.0 }, { "max_duration", "auto" }, { "auto", true }, { "turn_time", 60.0 }, { "phase", "start" },
            };
            var step = BlockCalls.TransferStep(input, rig.Snapshot());
            Assert.Equal("", step["error"]);
            Assert.Equal("burn", step["do"]);
            Assert.Equal("ejection", step["kind"]);
            var carry = (IDictionary<string, object>)step["carry"];
            Assert.Equal("ejected", carry["phase"]);
            Assert.True((double)step["max_duration"] > 0);
            Assert.True(((IDictionary<string, object>)step["report"]).ContainsKey("ejection_wait"));
            Assert.Equal("found no transfer: target must be a body's name", BlockCalls.TransferStep(new Dictionary<string, object>(), rig.Snapshot())["error"]);
        }

        // ---- The pure rules, from the Kerboscript (TransferFlightParityTests checked them against it) ----

        [Fact]
        public void A_periapsis_still_off_after_the_correction_is_the_reason_the_block_fails()
        {
            Assert.Equal("after the correction, the periapsis at Minmus is 38.1 km, 8.1 km off target_pe 30 km (pe_tolerance 1 km)",
                TransferFlight.Miss("Minmus", 38117, 30000, 1000, ""));
            Assert.Equal("after the correction, the periapsis at Minmus is 29.5 km, 0.5 km off target_pe 30 km (pe_tolerance 1 km): residual 0.3 m/s is over max_residual 0.1 m/s",
                TransferFlight.Miss("Minmus", 29500, 30000, 1000, "residual 0.3 m/s is over max_residual 0.1 m/s"));
            Assert.Equal("", TransferFlight.Miss("Minmus", 30400, 30000, 1000, ""));
            Assert.Equal("", TransferFlight.Miss("Minmus", 38117, 30000, 10000, ""));
        }

        [Fact]
        public void The_correction_after_a_missed_ejection_is_one_burn_the_craft_can_turn_to_and_pay_for()
        {
            // Block started at UT 1000, now UT 5000, a 133 s turn (the #363 craft's TURNTIME).
            var auto = TransferFlight.CorrectionAsk("Mun", 30000, 5000, 133, 1000, 30000, true, 412.5);
            Assert.Equal(5193, auto.EarliestUt);
            Assert.Null(auto.LatestUt);
            Assert.Equal(412.5, auto.MaxDv);
            Assert.False(auto.BrokenPlane);
            // 1000 + 20000 - 120 - (133 + 60): the Course Correction after it needs its 193 s lead and 120 s to burn.
            Assert.Equal(20687, TransferFlight.CorrectionAsk("Mun", 30000, 5000, 133, 1000, 20000, false, 412.5).LatestUt);
            Assert.Null(TransferFlight.CorrectionAsk("Mun", 30000, 5000, 133, 1000, 30000, true, -1).MaxDv);
        }

        [Fact]
        public void The_correction_burns_halfway_or_before_max_duration_ends_or_not_at_all()
        {
            string reason;
            Assert.Equal(16500, TransferFlight.CorrectionUt(16500, 3000, 193, 1000, null, out reason));
            Assert.Equal("", reason);
            Assert.Equal(10880, TransferFlight.CorrectionUt(16500, 3000, 193, 1000, 10000, out reason));
            Assert.Equal("", reason);
            Assert.Equal(3180, TransferFlight.CorrectionUt(16500, 3000, 193, 1000, 2300, out reason));
            Assert.Equal("max_duration (2300 s) ends at UT 3300, too soon for the Course Correction: its burn needs 313 s from UT 3000", reason);
            TransferFlight.CorrectionUt(3100, 3000, 193, 1000, null, out reason);
            Assert.Equal("the arrival comes too soon for the Course Correction", reason);
        }

        [Fact]
        public void The_departure_is_searched_from_once_the_craft_can_have_turned_or_from_the_window()
        {
            Assert.Equal(1133, TransferFlight.Earliest(1000, 133, null));
            Assert.Equal(2000, TransferFlight.Earliest(1000, 133, 2000));
            Assert.Equal(1133, TransferFlight.Earliest(1000, 133, 1100));
        }

        // ---- The sibling predicate ----

        [Theory]
        [InlineData("Kerbin", "Duna", true)]
        [InlineData("Mun", "Minmus", true)]
        [InlineData("Kerbin", "Mun", false)]
        [InlineData("Kerbin", "Ike", false)]
        [InlineData("Kerbin", "Kerbin", false)]
        public void A_sibling_shares_the_parent_of_the_body_the_craft_orbits(string from, string to, bool sibling)
        {
            Assert.Equal(sibling, TransferWindows.IsSiblingTransfer(TransferRig.Circular(from, 30000, 0).Snapshot(), to));
        }

        // ---- Time ----

        /// <summary>
        /// TRANSFERSTEP is asked once a step, not every tick; but the ejection step's window search and aim stall kOS while they
        /// run, so they keep to the window search's own budget (<see cref="TransferWindows.BudgetMs"/>).
        /// </summary>
        [Fact]
        [Trait("Category", "Timing")]
        public void The_ejection_step_to_Duna_keeps_to_the_window_search_s_budget()
        {
            var snapshot = TransferRig.Circular("Kerbin", 100000, 0).Snapshot();
            Timing.Within(TransferWindows.BudgetMs, () => Assert.Equal("", TransferFlight.Next(snapshot, Ask("Duna", 50000)).Error));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_correction_step_on_the_Sun_s_patch_keeps_to_the_window_search_s_budget()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var ask = Ask("Duna", 50000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            ask.LastDv = rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            TransferFlight.Carry(ask, eject);
            ask.MaxDuration = eject.MaxDuration;
            var wait = TransferFlight.Next(rig.Snapshot(), ask);
            rig.MoveTo(wait.Ut);
            TransferFlight.Carry(ask, wait);
            var snapshot = rig.Snapshot();
            Timing.Within(TransferWindows.BudgetMs, () =>
            {
                var copy = new TransferFlightAsk
                {
                    Target = ask.Target, TargetPe = ask.TargetPe, StartUt = 0, TurnTime = 60, MaxDuration = ask.MaxDuration, Auto = true,
                    Phase = ask.Phase, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt,
                };
                Assert.Equal("", TransferFlight.Next(snapshot, copy).Error);
            });
        }
    }
}
