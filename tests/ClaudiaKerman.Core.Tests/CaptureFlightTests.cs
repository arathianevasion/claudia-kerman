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
    /// <c>capture</c>'s flight (#788): <see cref="CaptureFlight"/> flown on <see cref="TransferRig"/>, from an arrival hyperbola to a circle at alt in
    /// the arrival plane, the leftover tilt flown the cheaper way or, over max_plane_dv, reported.
    /// </summary>
    public sealed class CaptureFlightTests
    {
        private static string Flown(TransferRig rig)
        {
            return string.Join(", ", rig.Burns.Select(b => b.Key + " " + b.Value.ToString("0.##")));
        }

        private static CaptureAsk Ask(object plane = null, double? alt = null, double? maxPlaneDv = null)
        {
            return new CaptureAsk { Plane = PlaneOption.Read(plane), Alt = alt, MaxPlaneDv = maxPlaneDv, TurnTime = 60 };
        }

        private static double Total(TransferRig rig)
        {
            return rig.Burns.Sum(b => b.Value);
        }

        private static void AssertRound(TransferRig rig, double altitude)
        {
            Assert.True(rig.Orbit.Closed, Flown(rig));
            Assert.InRange(rig.Orbit.E, 0, 0.011);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, altitude - 0.012 * (rig.Body.Radius + altitude), altitude + 0.012 * (rig.Body.Radius + altitude));
            Assert.InRange(rig.Orbit.ApR - rig.Body.Radius, altitude - 0.012 * (rig.Body.Radius + altitude), altitude + 0.012 * (rig.Body.Radius + altitude));
        }

        [Fact]
        public void No_plane_is_the_capture_burn_alone_at_the_periapsis_it_came_in_at()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            AssertRound(rig, 30000);
            Assert.Single(rig.Burns);
            Assert.Equal("plain", step.Report["capture_method"]);
            Assert.Equal(0.0, Convert.ToDouble(step.Report["plane_leftover_deg"]));
        }

        [Fact]
        public void Under_the_low_orbit_the_default_alt_is_the_low_orbit()
        {
            var rig = TransferRig.Arriving("Mun", 8000, 500, 0, 0, 0, 3000);
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            AssertRound(rig, 14000);
        }

        [Theory]
        [InlineData(100000.0)]
        [InlineData(15000.0)]
        public void An_alt_above_or_below_the_periapsis_is_reached(double alt)
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var step = rig.RunCapture(Ask(alt: alt));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, alt);
        }

        // ---- The leftover tilt ----

        [Fact]
        public void A_small_tilt_at_a_node_at_periapsis_is_folded_into_the_capture_burn()
        {
            // An inclination whose plane runs through periapsis: a burn there reaches it at little over the plain capture.
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var choice = Choose(rig, new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 5 });
            Assert.Equal(CaptureMethod.Periapsis, choice.Method);
            Assert.True(choice.PeriapsisCost < choice.ApoapsisCost);
            var step = rig.RunCapture(Ask(5.0));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 30000);
            Assert.InRange(rig.Orbit.Inclination, 4.5, 5.5);
            Assert.Single(rig.Burns);
            Assert.True(Total(rig) < choice.PlainDv + choice.MaxPlaneDv);
        }

        [Fact]
        public void A_big_tilt_is_cheaper_at_a_high_ellipse_s_apoapsis()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var plane = new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 60 };
            var choice = Choose(rig, plane, 500);
            Assert.Equal(CaptureMethod.Apoapsis, choice.Method);
            Assert.True(choice.ApoapsisCost < choice.PeriapsisCost, choice.ApoapsisCost + " vs " + choice.PeriapsisCost);
            var step = rig.RunCapture(Ask(60.0, maxPlaneDv: 500));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.Equal("apoapsis", step.Report["capture_method"]);
            AssertRound(rig, 30000);
            Assert.InRange(rig.Orbit.Inclination, 59.5, 60.5);
            Assert.True(rig.Burned("plane") > 0, Flown(rig));
            Assert.InRange(Total(rig), choice.PlainDv, choice.PlainDv + choice.ApoapsisCost + 5);
        }

        [Fact]
        public void The_flown_total_is_the_cheaper_of_the_two_ways()
        {
            // Across tilts, the way picked costs no more than flying the other: each flown with max_plane_dv out of the way.
            foreach (var inc in new[] { 3.0, 10.0, 25.0, 45.0, 70.0 })
            {
                var picked = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
                var choice = Choose(picked, new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = inc }, 5000);
                Assert.Equal(Math.Min(choice.PeriapsisCost, choice.ApoapsisCost), choice.Method == CaptureMethod.Apoapsis ? choice.ApoapsisCost : choice.PeriapsisCost);
                var step = picked.RunCapture(Ask(inc, maxPlaneDv: 5000));
                Assert.True(step.Error == "", inc + ": " + step.Error + " | " + Flown(picked));
                Assert.InRange(picked.Orbit.Inclination, inc - 0.5, inc + 0.5);
                Assert.InRange(Total(picked), 0, choice.PlainDv + choice.BestCost + 5);
            }
        }

        [Fact]
        public void Over_max_plane_dv_it_captures_and_reports_without_failing()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var step = rig.RunCapture(Ask(60.0, maxPlaneDv: 5));
            Assert.True(step.Error == "", step.Error);
            AssertRound(rig, 30000);
            Assert.Single(rig.Burns);
            Assert.Equal(CaptureMethod.Over, step.Report["capture_method"]);
            Assert.InRange(Convert.ToDouble(step.Report["plane_leftover_deg"]), 59.5, 60.5);
            Assert.True(Convert.ToDouble(step.Report["plane_leftover_dv"]) > 5);
            Assert.InRange(rig.Orbit.Inclination, 0, 0.5);
        }

        [Fact]
        public void The_default_max_plane_dv_is_the_larger_of_50_and_5_percent_of_the_capture()
        {
            var mun = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            var small = Choose(mun, new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 5 });
            Assert.Equal(50, small.MaxPlaneDv);
            var duna = TransferRig.Arriving("Duna", 60000, 3000, 0, 0, 0, 3000);
            var big = Choose(duna, new ArrivalPlane { Arrival = "Duna", Source = "number", Inc = 5 });
            Assert.True(big.PlainDv > 1000);
            Assert.Equal(0.05 * big.PlainDv, big.MaxPlaneDv, 6);
        }

        [Fact]
        public void A_whole_plane_is_reached_through_its_node_a_retrograde_one_in_its_own_direction()
        {
            // A plane that doesn't run through periapsis: the far node of a high ellipse, then down.
            var rig = TransferRig.Arriving("Mun", 30000, 500, 10, 40, 30, 3000);
            var normal = Conic.FromElements(1, 1, 0, 150, 100, 0, 0, 0).W;
            var step = rig.RunCapture(Ask(Lexicon(150, 100), maxPlaneDv: 5000));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 30000);
            Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 0.5, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
            Assert.True(rig.Orbit.Inclination > 90);
        }

        // ---- The Target Vessel, and the record ----

        private static SnapshotTarget Vessel(double inc, double lan, string body, double pe, double ap, double radius)
        {
            return new SnapshotTarget
            {
                Name = "Station", Body = body, Situation = "ORBITING",
                Orbit = new SnapshotOrbit { Inclination = inc, Lan = lan, Periapsis = pe, Apoapsis = ap, SemiMajorAxis = radius + (pe + ap) / 2, Eccentricity = (ap - pe) / (2 * radius + pe + ap), Body = body },
            };
        }

        [Fact]
        public void With_a_Target_Vessel_it_captures_circular_at_its_periapsis_altitude_in_its_plane_and_direction()
        {
            // Inclined and eccentric, at Minmus: circular at the periapsis altitude, whatever target_pe the craft came in at.
            var rig = TransferRig.Arriving("Minmus", 35000, 300, 8, 60, 20, 3000);
            var normal = Conic.FromElements(1, 1, 0, 25, 70, 0, 0, 0).W;
            rig.Stamp = s => s.Target = Vessel(25, 70, "Minmus", 20000, 90000, 60000);
            var step = rig.RunCapture(Ask(maxPlaneDv: 5000));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 20000);
            Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 0.5, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
        }

        [Fact]
        public void A_retrograde_Target_Vessel_is_captured_retrograde()
        {
            var rig = TransferRig.Arriving("Minmus", 35000, 300, 170, 60, 20, 3000);
            rig.Stamp = s => s.Target = Vessel(165, 70, "Minmus", 20000, 90000, 60000);
            var step = rig.RunCapture(Ask(maxPlaneDv: 5000));
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 20000);
            Assert.InRange(rig.Orbit.Inclination, 164.5, 165.5);
        }

        [Fact]
        public void It_reads_the_plane_transfer_to_recorded_for_this_body()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            rig.Stamp = s => s.RecordedPlane = new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 5 }.ToRecord();
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            Assert.InRange(rig.Orbit.Inclination, 4.5, 5.5);
        }

        [Fact]
        public void A_record_for_another_body_is_not_read()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            rig.Stamp = s => s.RecordedPlane = new ArrivalPlane { Arrival = "Minmus", Source = "number", Inc = 5 }.ToRecord();
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error);
            Assert.InRange(rig.Orbit.Inclination, 0, 0.1);
        }

        [Fact]
        public void Claudia_s_value_stands_over_the_record()
        {
            var rig = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, 3000);
            rig.Stamp = s => s.RecordedPlane = new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 5 }.ToRecord();
            var step = rig.RunCapture(Ask("none"));
            Assert.True(step.Error == "", step.Error);
            Assert.InRange(rig.Orbit.Inclination, 0, 0.1);
        }

        // ---- What it refuses, and what it leaves alone ----

        [Fact]
        public void An_orbit_already_round_at_alt_and_in_the_plane_needs_no_burn()
        {
            var rig = TransferRig.Circular("Mun", 30000, 0);
            var step = rig.RunCapture(Ask());
            Assert.Equal("", step.Error);
            Assert.Empty(rig.Burns);
        }

        [Fact]
        public void It_refuses_a_periapsis_in_the_air_past_periapsis_and_before_the_sphere_of_influence()
        {
            var air = TransferRig.Arriving("Duna", 20000, 1000, 0, 0, 0, 3000);
            Assert.Contains("inside the atmosphere", air.RunCapture(Ask()).Error);

            var past = TransferRig.Arriving("Mun", 30000, 500, 0, 0, 0, -3000);
            Assert.Contains("past periapsis", past.RunCapture(Ask()).Error);

            // On the way to the Mun, still round Kerbin: the periapsis is beyond the encounter.
            var early = TransferRig.Circular("Kerbin", 100000, 0);
            var ejection = TransferFlight.Next(early.Snapshot(), new TransferFlightAsk { Target = "Mun", TargetPe = 30000, StartUt = 0, TurnTime = 60 });
            early.Fly(ejection.Kind, ejection.Ut, ejection.Prograde, ejection.Normal, ejection.Radial);
            Assert.Contains("sphere of influence", early.RunCapture(Ask()).Error);

            Assert.Contains("alt", TransferRig.Arriving("Duna", 60000, 1000, 0, 0, 0, 3000).RunCapture(Ask(alt: 30000)).Error);
        }

        // ---- From transfer_to to the capture ----

        [Fact]
        public void Kerbin_to_the_Mun_polar_ends_in_a_polar_circle_for_tens_of_m_s()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var ask = new TransferFlightAsk { Target = "Mun", TargetPe = 30000, StartUt = 0, TurnTime = 60, PlaneOption = PlaneOption.Read(90.0) };
            var flight = rig.Run(ask, 30);
            Assert.True(flight.Error == "", flight.Error + " | " + Flown(rig));
            var approach = rig.Burns.Where(b => b.Key != "ejection").Sum(b => b.Value);
            rig.Stamp = s => s.RecordedPlane = flight.AimedPlane;
            rig.Burns.Clear();
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 30000);
            Assert.InRange(rig.Orbit.Inclination, 89.5, 90.5);
            Assert.True(approach < 80, approach.ToString());
        }

        [Fact]
        public void Kerbin_to_Minmus_with_a_Target_Vessel_ends_in_its_plane_and_direction_at_its_periapsis_altitude()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0, 6);
            var normal = Conic.FromElements(1, 1, 0, 30, 80, 0, 0, 0).W;
            rig.Stamp = s => s.Target = Vessel(30, 80, "Minmus", 20000, 90000, 60000);
            var flight = rig.Run(new TransferFlightAsk { Target = "Minmus", TargetPe = 30000, StartUt = 0, TurnTime = 60 }, 30);
            Assert.True(flight.Error == "", flight.Error + " | " + Flown(rig));
            var before = rig.Stamp;
            rig.Stamp = s => { before(s); s.RecordedPlane = flight.AimedPlane; };
            var step = rig.RunCapture(Ask());
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            AssertRound(rig, 20000);
            var off = Convert.ToDouble(step.Report["plane_leftover_deg"]);
            if (!Equals(step.Report["capture_method"], CaptureMethod.Over))
                Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 0.5, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
            else
                Assert.True(off > 0.5);
        }

        private static IDictionary<string, object> Lexicon(double inc, double lan)
        {
            return new Dictionary<string, object> { { "inc", inc }, { "lan", lan }, { "name", "Other" }, { "body", "Mun" } };
        }

        private static CaptureChoice Choose(TransferRig rig, ArrivalPlane plane, double? max = null)
        {
            var peUt = rig.Orbit.NextPeriapsis(0).Value;
            return CaptureFlight.Choose(rig.System, rig.Body, rig.Orbit, peUt, rig.Orbit.PeR, plane, max);
        }
    }
}
