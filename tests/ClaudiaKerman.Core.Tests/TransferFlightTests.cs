// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
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
            // It ends before the arrival (the capture is circularize's): on the Sun's patch with the periapsis already within
            // pe_tolerance, or inside Duna's sphere of influence after the trim there (#747).
            Assert.Contains(rig.Body.Name, new[] { "Sun", "Duna" });
            Assert.True(rig.Ut < D(step, "arrival_ut"));
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

        // ---- #747: the trim near the target, the encounter kept far out, a lost one recovered ----

        [Theory]
        [InlineData(0.001, 0.05)]
        [InlineData(0.001, -0.05)]
        [InlineData(-0.001, 0.05)]
        [InlineData(-0.001, -0.05)]
        public void Kerbin_to_Duna_trims_the_periapsis_inside_Duna_s_sphere_of_influence_within_pe_tolerance(double burnError, double flownError)
        {
            // An ejection 1 m/s off its node, and every correction flown 0.05 m/s long or short (the Duna miss of #747: far out,
            // 0.02 m/s moved the periapsis 57 km, so a trim there can't hold a 1 km pe_tolerance).
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = burnError;
            rig.CorrectionError = flownError;
            var step = rig.Run(Ask("Duna", 50000), 30);
            var flown = string.Join(", ", rig.Burns.Select((b, i) => b.Key + " " + b.Value.ToString("0.###") + " round " + rig.BurnBodies[i]));
            Assert.True(step.Error == "", step.Error + " | " + flown);
            // The trim is the last correction, flown inside Duna's sphere of influence; the far-out ones only kept the encounter.
            Assert.Equal("Duna", rig.BurnBodies[rig.BurnBodies.Count - 1]);
            Assert.Equal("Duna", rig.Body.Name);
            var duna = rig.System.Find("Duna");
            Assert.InRange(rig.Orbit.PeR - duna.Radius, 49000, 51000);
            Assert.InRange(D(step, "target_pe_actual"), 49000, 51000);
            Assert.True(D(step, "encounter_ut") > D(step, "departure_ut"), flown);
            Assert.InRange(rig.Burned("correction"), 0, 20);
        }

        [Fact]
        public void A_Course_Correction_that_loses_the_Duna_encounter_recovers_instead_of_failing()
        {
            // The Duna miss of #747: a Course Correction on the Sun's patch that left no encounter ended the block. Here one is
            // flown 40 m/s wrong; the check after it plans the missed-ejection recovery from the orbit the craft is on.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var ask = Ask("Duna", 50000);
            for (var i = 0; i < 2; i++)
            {
                var s = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.Equal("", s.Error);
                if (s.MaxDuration.HasValue) ask.MaxDuration = s.MaxDuration;
                if (s.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(s.Kind, s.Ut, s.Prograde, s.Normal, s.Radial);
                else rig.MoveTo(s.Ut);
                TransferFlight.Carry(ask, s);
            }
            Assert.Equal("Sun", rig.Body.Name);
            rig.Fly("correction", rig.Ut + 600, -40, 0, 0);
            ask.Phase = "mid_check";

            var recovery = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(recovery.Error == "", recovery.Error);
            Assert.Equal(TransferFlightStep.Burn, recovery.Do);
            Assert.Equal("correction", recovery.Kind);
            Assert.Equal("no encounter with Duna after the correction", recovery.Report["recovered"]);

            var step = rig.Run(ask, 30);
            Assert.True(step.Error == "", step.Error + " | " + string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value)));
            Assert.InRange(D(step, "target_pe_actual"), 49000, 51000);
            Assert.Equal("no encounter with Duna after the correction", step.Report["recovered"]);
        }

        [Fact]
        public void A_second_lost_encounter_fails_rather_than_recovering_again()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var ask = Ask("Mun", 30000);
            ask.Phase = "mid_check";
            ask.Recovered = true;
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.StartsWith("no encounter with Mun after the correction; the next window costs ", step.Error);
        }

        [Fact]
        public void From_the_Sun_s_orbit_Duna_s_periapsis_is_trimmed_inside_its_sphere_of_influence()
        {
            // The corrective transfer_to of #747: a craft round the Sun, Duna a planet of it. Its corrections far out (0.17 and
            // 0.10 m/s) left the periapsis 2.9 km against target_pe 60 km; now the trim waits for Duna's sphere of influence.
            var rig = TransferRig.Circular("Sun", 15000000000.0 - 261600000, 0);
            rig.BurnError = 0.001;
            rig.CorrectionError = 0.05;
            var step = rig.Run(Ask("Duna", 60000), 30);
            var flown = string.Join(", ", rig.Burns.Select((b, i) => b.Key + " " + b.Value.ToString("0.###") + " round " + rig.BurnBodies[i]));
            Assert.True(step.Error == "", step.Error + " | " + flown);
            Assert.True(rig.Body.Name == "Duna" && rig.BurnBodies[rig.BurnBodies.Count - 1] == "Duna", flown);
            Assert.InRange(D(step, "target_pe_actual"), 59000, 61000);
            Assert.InRange(rig.Orbit.PeR - rig.System.Find("Duna").Radius, 59000, 61000);
        }

        /// <summary>
        /// #747's ejection over-burn (node 1,093.6 m/s, used 1,114.8 in 229.8 s from an 80 km Kerbin orbit) is the finite-burn
        /// loss of flying an impulsive node: exec_node steers along the node's remaining vector, the velocity the planned orbit
        /// has at the node's UT less the current orbit's then. Thrust before or after the node UT is carried there turned by
        /// the orbit's motion, so only its share along that vector counts: over a burn sweeping an arc nT of the orbit, about
        /// (nT)^2/24 of the node is lost. Here a 230 s burn sweeps 44 degrees of a 1,873 s orbit. Not a metering error: a burn
        /// ten times shorter loses next to nothing, as the corrective flight's 68 s ejection (425.8 m/s, used 425.7) did.
        /// </summary>
        [Fact]
        public void The_ejection_over_burn_is_the_finite_burn_loss_of_chasing_an_impulsive_node()
        {
            var gm = 3531600000000.0;
            var r0 = 600000 + 79456.0;
            var over = FiniteBurnOver(gm, r0, 1088.07, 101.75, 42.44, 229.8, 1114.8);
            Assert.InRange(over, 15, 30);
            // The measured 21.2 m/s is within the same band and under the fixed-direction estimate dv (nT)^2/24.
            var n = Math.Sqrt(gm / (r0 * r0 * r0));
            var estimate = 1093.6 * Math.Pow(n * 229.8, 2) / 24;
            Assert.InRange(estimate, 25, 30);
            Assert.InRange(1114.8 - 1093.6, 15, estimate);
            // The same node burned ten times faster: under 1 m/s over.
            Assert.InRange(FiniteBurnOver(gm, r0, 1088.07, 101.75, 42.44, 22.98, 1094), 0, 1);
        }

        /// <summary>
        /// exec_node's guidance on a finite burn (a test model of exec_node.ks): a craft in a circular orbit of radius
        /// <paramref name="r0"/>, its node at the orbit's UT 0, an engine of constant mass flow that burns <paramref name="dvUsed"/>
        /// in <paramref name="burnTime"/> s; it starts half the node's delta-v early, steers along the node's remaining vector
        /// (held once under 1 m/s), throttles down in the last second and stops under 0.1 m/s. Gives m/s burned over the node.
        /// </summary>
        private static double FiniteBurnOver(double gm, double r0, double prograde, double normal, double radial, double burnTime, double dvUsed)
        {
            var vc = Math.Sqrt(gm / r0);
            var rNode = new Vec3(r0, 0, 0);
            var vNode = new Vec3(0, vc, 0);
            // Stock's node frame at the node: prograde along v, normal along r x v, radial out.
            var node = new Vec3(radial, prograde, normal);
            var planned = vNode + node;
            var ve = 300 * 9.80665;
            var m0 = 1.0;
            var mdot = (m0 - m0 / Math.Exp(dvUsed / ve)) / burnTime;
            var t = -(m0 - m0 / Math.Exp(node.Length / 2 / ve)) / mdot;
            var orbit = Conic.FromState(gm, rNode, vNode, 0);
            Vec3 r, v, rAt, vAt;
            orbit.State(t, out r, out v);
            var m = m0;
            var spent = 0.0;
            var steer = node.Normalized;
            Vec3? first = null;
            const double dt = 0.25;
            while (true)
            {
                Conic.FromState(gm, r, v, t).State(0, out rAt, out vAt);
                var left = planned - vAt;
                if (!first.HasValue) first = left;
                if (Vec3.Dot(first.Value, left) < 0 || left.Length < 0.1) break;
                if (left.Length > 1) steer = left.Normalized;
                var accel = ve * mdot / m;
                var throttle = Math.Min(1, Math.Max(0.005, left.Length / accel));
                var dv = throttle * accel * dt;
                var after = Conic.FromState(gm, r, v + steer * dv, t);
                after.State(t + dt, out r, out v);
                m -= throttle * mdot * dt;
                t += dt;
                spent += dv;
            }
            return spent - node.Length;
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

        // #732 (folded into #727): launched due east from a 20° site, the craft's plane is 14° from Minmus's. An ejection in that
        // plane toward where Minmus would be, turned into it, misses by Minmus's height out of it; the correction fixes the plane
        // mid-course, aimed through the planned arrival, for tens of m/s, and the arrival is at target_pe.
        [Fact]
        public void The_correction_removes_a_residual_plane_mismatch_mid_course()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var kerbin = rig.System.Find("Kerbin");
            var minmus = rig.System.OrbitOf(rig.System.Find("Minmus"));
            var parking = Conic.FromElements(kerbin.Gm, 700000, 0, 20, 78, 0, 0, 0);
            var tof = Math.PI * Math.Sqrt(Math.Pow((700000 + minmus.A) / 2, 3) / kerbin.Gm);
            // A departure whose arrival finds Minmus well off the line of nodes: at least 8° out of the parking plane.
            double t1 = 0, arrival = 0;
            Vec3 r2 = new Vec3(0, 0, 0), v2;
            for (var t = 10 * Day; t < 60 * Day; t += Day / 4)
            {
                minmus.State(t + tof, out r2, out v2);
                if (Math.Abs(Vec3.Dot(r2.Normalized, parking.W)) < Math.Sin(8 * Math.PI / 180)) continue;
                t1 = t;
                arrival = t + tof;
                break;
            }
            Assert.True(arrival > 0);
            var meet = (r2 - parking.W * Vec3.Dot(parking.W, r2)).Normalized * r2.Length;
            var r1 = meet.Normalized * -700000;
            var arc = Lambert.Solve(kerbin.Gm, r1, meet, tof, parking.W);
            Assert.Null(arc.Error);
            rig.Orbit = Conic.FromState(kerbin.Gm, r1, arc.V1, t1);
            rig.Ut = t1;
            rig.MoveTo(t1 + 600);

            var ask = Ask("Minmus", 30000);
            ask.StartUt = t1 - 3600;
            ask.Phase = "ejected";
            ask.ArrivalUt = arrival;
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(step.Error == "", step.Error);
            Assert.Equal(TransferFlightStep.Burn, step.Do);
            Assert.Equal("plane", step.Kind);
            Assert.InRange(step.Ut, t1 + 600, arrival);
            Assert.True(Math.Abs(step.Normal) > 1, "a plane fix burns normal: " + step.Normal);

            var done = rig.Run(ask);
            Assert.True(done.Error == "", done.Error + " | " + string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value)));
            Assert.InRange(rig.Burned("plane"), 10, 150);
            Assert.InRange(D(done, "target_pe_actual"), 29000, 31000);
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

        // ---- The block call's snapshot carries the Mission's Time versus delta-v (#733) ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Window_next_picks_the_window_plan_transfer_planned_for_the_Mission_s_Time_versus_delta_v(bool inMission)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            var view = new MissionView { Stage = inMission ? MissionStage.Running : MissionStage.None };
            view.Settings.TimeVersusDeltaV = TimeVersusDeltaV.MostEfficient;
            MissionView.Stamp(snapshot, view);
            var setting = inMission ? TimeVersusDeltaV.MostEfficient : TimeVersusDeltaV.Balanced;
            Assert.Equal(setting, snapshot.TimeVersusDeltaV);

            var found = TransferWindows.Find(snapshot, "Duna", 50000, true, snapshot.MarginPercent);
            var expected = TransferWindows.Pick(found, setting);
            // The setting matters here: Most efficient and Fastest pick different windows.
            Assert.True(Math.Abs(TransferWindows.Pick(found, TimeVersusDeltaV.MostEfficient).DepartureUt - TransferWindows.Pick(found, TimeVersusDeltaV.Fastest).DepartureUt) > 10 * Day);
            var plan = PlanningTools.Default().Run("plan_transfer", "{\"target\":\"Duna\",\"target_pe\":50000}", snapshot, null);
            Assert.Contains("leaving at UT " + Math.Round(expected.DepartureUt).ToString(System.Globalization.CultureInfo.InvariantCulture), plan.Summary);

            var input = new Dictionary<string, object>
            {
                { "target", "Duna" }, { "target_pe", 50000.0 }, { "window", "next" }, { "correction", true }, { "pe_tolerance", 1000.0 },
                { "start_ut", 0.0 }, { "max_duration", "auto" }, { "auto", true }, { "turn_time", 60.0 }, { "phase", "start" },
            };
            var step = BlockCalls.TransferStep(input, snapshot);
            Assert.Equal("", step["error"]);
            Assert.InRange(Convert.ToDouble(step["ut"]), expected.DepartureUt - 10 * Day, expected.DepartureUt + 10 * Day);
        }

        // ---- A trip home prices no capture burn (#761, ArrivalRule from #758) ----

        private static Dictionary<string, object> StartInput(string target, double pe)
        {
            return new Dictionary<string, object>
            {
                { "target", target }, { "target_pe", pe }, { "correction", true }, { "pe_tolerance", 1000.0 }, { "start_ut", 0.0 },
                { "max_duration", "auto" }, { "auto", true }, { "turn_time", 60.0 }, { "phase", "start" },
            };
        }

        /// <summary>
        /// #880: from another planet the next Transfer Window goes, with no landing wait; the step names KSC as the landing, so reentry's Late Trim (a Course Correction, #892) aims there
        /// within the spare delta-v (#907). A Mission Goal that names another place leaves the landing out and the window where it was.
        /// </summary>
        [Fact]
        public void An_interplanetary_return_home_takes_the_next_window_and_the_trim_aims_at_ksc_within_the_spare_delta_v()
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            var aimed = rig.Snapshot();
            var step = BlockCalls.TransferStep(StartInput("Kerbin", 30000), aimed);
            Assert.Equal("", step["error"]);
            Assert.Equal(ReturnAim.KscLatitude, Convert.ToDouble(step["lat"]));
            Assert.Equal(ReturnAim.KscLongitude, Convert.ToDouble(step["lng"]));

            var named = rig.Snapshot();
            named.MissionGoal = "Land in the ocean.";
            var plain = BlockCalls.TransferStep(StartInput("Kerbin", 30000), named);
            Assert.False(plain.ContainsKey("lat"), "a goal naming another place has no landing to aim at");
            Assert.Equal(Convert.ToDouble(plain["ut"]), Convert.ToDouble(step["ut"]), 3);

            var coming = AimedCorrectionTests.Falling();
            // What the adapter records from the step is what the trim aims at.
            coming.LandingTarget = BlockCalls.LandingTargetOf(step, "Kerbin");
            var trim = BlockCalls.EntryTrim(new Dictionary<string, object> { { "to_air", 15000.0 }, { "dv_left", 300.0 }, { "isp", 0.0 }, { "has_node", false } },
                coming, new AimedCorrectionTests.FrameBody(coming.Ut), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", trim["error"]);
            Assert.True(Convert.ToDouble(trim["dv"]) > 0, "it flies a trim toward KSC: " + trim["aim_note"]);
            Assert.InRange(Convert.ToDouble(trim["dv"]), 0, DeorbitBurn.SpareDv(300, 0, 0, null));
        }

        [Fact]
        public void Duna_to_Kerbin_departs_when_plan_transfer_does_with_no_capture_priced()
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            var snapshot = rig.Snapshot();
            var capture = ArrivalRule.Capture(snapshot, "Kerbin", null);
            Assert.False(capture);
            var pick = TransferWindows.Pick(TransferWindows.Find(snapshot, "Kerbin", 30000, capture, snapshot.MarginPercent), snapshot.TimeVersusDeltaV);
            // #867: the block first waits for its far ejection, watched by the next-patch watch, then solves it again from there.
            var wait = BlockCalls.TransferStep(StartInput("Kerbin", 30000), snapshot);
            Assert.Equal("wait", wait["do"]);
            rig.MoveTo(Convert.ToDouble(wait["ut"]));
            var eject = StartInput("Kerbin", 30000);
            foreach (var carried in (IDictionary<string, object>)wait["carry"]) eject[carried.Key] = carried.Value;
            var step = BlockCalls.TransferStep(eject, rig.Snapshot());
            Assert.Equal("", step["error"]);
            Assert.Equal("burn", step["do"]);
            // The node flown is the one the wait was planned for.
            Assert.Equal(Convert.ToDouble(((IDictionary<string, object>)wait["report"])["departure_ut"]), Math.Round(Convert.ToDouble(step["ut"])));

            // plan_transfer's departure is the heliocentric one; the ejection burn is flown on the parking orbit, so the two UTs
            // agree to within half its period (the solvers themselves resolve the window to a second: TimeTolerance 1e-6 of the
            // time scale). Measured 202 s of 1,558 s. Priced with a capture (the old default) the departure is 34,120 s later.
            var duna = rig.System.Find("Duna");
            var half = Math.PI * Math.Sqrt(Math.Pow(duna.Radius + 100000, 3) / duna.Gm);
            Assert.InRange(Convert.ToDouble(step["ut"]), pick.DepartureUt - half, pick.DepartureUt + half);

            // And the ejection's dv is plan_transfer's within 1 % (measured 0.5 %).
            var dv = Math.Sqrt(Math.Pow(Convert.ToDouble(step["prograde"]), 2) + Math.Pow(Convert.ToDouble(step["normal"]), 2) + Math.Pow(Convert.ToDouble(step["radial"]), 2));
            Assert.InRange(dv, pick.EjectionDv * 0.99, pick.EjectionDv * 1.01);
        }

        [Fact]
        public void A_trip_home_says_no_capture_is_priced_and_a_trip_elsewhere_still_hints_a_capture()
        {
            var home = TransferRig.Circular("Duna", 100000, 0);
            var homeStep = home.Run(Ask("Kerbin", 30000, correction: false));
            Assert.Equal("", homeStep.Error);
            Assert.Equal("arrival: entry at 21 km, no capture burn", homeStep.Report["arrival_note"]);
            Assert.False(homeStep.Report.ContainsKey("next"));
            var away = TransferRig.Circular("Kerbin", 100000, 0).Run(Ask("Duna", 50000, correction: false));
            Assert.False(away.Report.ContainsKey("arrival_note"));
        }

        /// <summary>A craft ejected from its body and put 40 m/s off by a correction: on the Sun's patch with no encounter, the state a missed-ejection recovery plans from.</summary>
        private static Tuple<TransferRig, TransferFlightAsk> LostOnTheWay(string from, string to, double pe, double miss = -40)
        {
            var rig = TransferRig.Circular(from, 100000, 0);
            var ask = Ask(to, pe);
            for (var i = 0; i < 2; i++)
            {
                var s = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.Equal("", s.Error);
                if (s.MaxDuration.HasValue) ask.MaxDuration = s.MaxDuration;
                if (s.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(s.Kind, s.Ut, s.Prograde, s.Normal, s.Radial);
                else rig.MoveTo(s.Ut);
                TransferFlight.Carry(ask, s);
            }
            Assert.Equal("Sun", rig.Body.Name);
            rig.Fly("correction", rig.Ut + 600, miss, 0, 0);
            ask.Phase = "mid_check";
            return Tuple.Create(rig, ask);
        }

        [Fact]
        public void A_missed_ejection_recovery_on_a_trip_home_is_accepted_when_dv_left_covers_the_ejection_but_not_a_capture()
        {
            // Duna to Kerbin, a correction 100 m/s off loses the encounter. Ranked with a capture, the recovery picked is a 100.5 m/s
            // burn (over dv_left 100, refused before #761); ranked by the burn alone, as the ArrivalRule says a trip home is, 29 m/s.
            var lost = LostOnTheWay("Duna", "Kerbin", 30000, -100);
            var input = StartInput("Kerbin", 30000);
            input["phase"] = "mid_check";
            input["dv_left"] = 100.0;
            var step = BlockCalls.TransferStep(input, lost.Item1.Snapshot());
            Assert.Equal("", step["error"]);
            Assert.Equal("burn", step["do"]);
            Assert.Equal("correction", step["kind"]);
            Assert.Equal("no encounter with Kerbin after the correction", ((IDictionary<string, object>)step["report"])["recovered"]);
            var dv = Math.Sqrt(Math.Pow(Convert.ToDouble(step["prograde"]), 2) + Math.Pow(Convert.ToDouble(step["normal"]), 2) + Math.Pow(Convert.ToDouble(step["radial"]), 2));
            Assert.InRange(dv, 1, 100);
        }

        [Fact]
        public void The_same_dv_left_to_a_body_that_is_not_home_is_still_refused_for_the_capture_it_prices()
        {
            // Kerbin to Duna, a correction 200 m/s off loses the encounter. Duna's capture is priced, so the recovery picked is a
            // 199.9 m/s burn (a 37.7 m/s one exists, ranked without the capture): over dv_left 100, as before.
            var lost = LostOnTheWay("Kerbin", "Duna", 50000, 200);
            var input = StartInput("Duna", 50000);
            input["phase"] = "mid_check";
            input["dv_left"] = 100.0;
            var step = BlockCalls.TransferStep(input, lost.Item1.Snapshot());
            Assert.Contains("no encounter with Duna after the correction, and no correction fits:", (string)step["error"]);
            Assert.Contains("more than the craft's 100 m/s", (string)step["error"]);
        }

        [Fact]
        public void The_correction_after_a_missed_ejection_takes_the_arrival_rules_capture()
        {
            Assert.False(TransferFlight.CorrectionAsk("Kerbin", 30000, 5000, 133, 1000, 30000, true, 412.5, false).Capture);
            Assert.True(TransferFlight.CorrectionAsk("Kerbin", 30000, 5000, 133, 1000, 30000, true, 412.5).Capture);
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
