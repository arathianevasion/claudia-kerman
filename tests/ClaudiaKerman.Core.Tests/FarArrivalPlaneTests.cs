// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #986: a far target's arrival plane is set far out, at the Course Correction on the star's patch, as a moon's is at the halfway burn (#788); the trim inside
    /// the sphere of influence is then a trim. Mission m20261110-203402-45d1 came into Duna at 53.9° with plane_dv 0 ("Land at Duna equator" set no plane), then
    /// flew an 806 m/s plane change to the equator; its return reached Kerbin at 57.9°.
    /// </summary>
    public sealed class FarArrivalPlaneTests
    {
        private readonly ITestOutputHelper _out;

        public FarArrivalPlaneTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The flight's plane change at Duna, m/s, and its whole capture (plans.jsonl, events.jsonl).</summary>
        private const double FlownPlaneChange = 806;

        /// <summary>
        /// The rig's planets share a plane, so a flight there comes in nearly in it. A real one comes in out of it: the arc's own tilt off the target's orbit puts the
        /// miss above or below it, and the flight came into Duna at 53.9°. This normal burn on the star's patch, flown as the ejection ends, does the same.
        /// </summary>
        private const string Tilt = "tilt";
        /// <summary>m/s of <see cref="Tilt"/>: the rig's plain arrival comes in at 85° at Duna and 46° at Kerbin.</summary>
        private const double ArcTilt = 3;

        private static TransferFlightAsk Ask(string target, double pe, object plane = null)
        {
            return new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = 0, TurnTime = 60, PlaneOption = PlaneOption.Read(plane) };
        }

        private static List<PlannedSequence> Legs(params string[] names)
        {
            return names.Select(n => new PlannedSequence { Name = n }).ToList();
        }

        /// <summary>What a flight did: the arrival's inclination as it entered the target's sphere of influence, and its burns.</summary>
        private sealed class Flight
        {
            public TransferRig Rig;
            public TransferFlightStep Last;
            public double IncAtEntry = double.NaN;
            /// <summary>The first far-out correction as asked, and the encounter's periapsis altitude (m) once it was flown.</summary>
            public TransferFlightStep FarOutStep;
            public double PeAfterFarOut = double.NaN;

            /// <summary>m/s burned out on the star's patch after the ejection: the far-out corrections.</summary>
            public double FarOut
            {
                get { return Rig.Burns.Where((b, i) => b.Key != "ejection" && b.Key != Tilt && Rig.BurnBodies[i] == "Sun").Sum(b => b.Value); }
            }

            /// <summary>m/s burned inside the target's sphere of influence.</summary>
            public double Inside(string target)
            {
                return Rig.Burns.Where((b, i) => Rig.BurnBodies[i] == target).Sum(b => b.Value);
            }

            public string Burns
            {
                get { return string.Join(", ", Rig.Burns.Select((b, i) => b.Key + " " + b.Value.ToString("0.##") + " round " + Rig.BurnBodies[i])); }
            }
        }

        /// <summary>
        /// exec_node's stop for a step, m/s: it ends the burn once what is left is under min(done_dv 0.1, max_residual / 4), max_residual exec_node's 0.5 when the step
        /// gives none (exec_node.ks; transfer_to passes only the step's max_residual).
        /// </summary>
        private static double ExecNodeStop(TransferFlightStep step)
        {
            return Math.Min(0.1, (step.MaxResidual > 0 ? step.MaxResidual : 0.5) / 4);
        }

        /// <summary>
        /// <see cref="TransferRig.Run"/>, noting the inclination the craft has as it first finds itself round <paramref name="ask"/>'s target. <paramref name="farOut"/>
        /// gives the m/s the first far-out correction is flown to, from the step (the rig flies every other burn exactly).
        /// </summary>
        private static Flight Fly(TransferRig rig, TransferFlightAsk ask, double tilt = 0, Func<TransferFlightStep, double> farOut = null, double tiltAlong = 0)
        {
            var flight = new Flight { Rig = rig };
            var tilted = tilt == 0;
            for (var i = 0; i < 30; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                if (step.Error != "" || step.Do == TransferFlightStep.Done)
                {
                    flight.Last = step;
                    // A flight with nothing to trim ends on the star's patch: coast it in to read where it arrives.
                    if (double.IsNaN(flight.IncAtEntry) && step.Error == "")
                    {
                        var enc = TransferFlight.Encounter(PredictBurn.Setup.Prepare(rig.Snapshot(), ask.Target, out _));
                        if (enc != null)
                        {
                            rig.MoveTo(enc.SoiUt + 60);
                            flight.IncAtEntry = rig.Orbit.Inclination;
                        }
                    }
                    return flight;
                }
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Burn)
                {
                    rig.MoveTo(step.Ut);
                    var first = step.Kind == "correction" && step.Phase == "mid_check" && rig.Body.Name != ask.Target && flight.FarOutStep == null;
                    var dv = Dv(step);
                    var share = first && farOut != null && dv > 0 ? Math.Max(0, farOut(step)) / dv : 1;
                    ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde * share, step.Normal * share, step.Radial * share);
                    if (first)
                    {
                        flight.FarOutStep = step;
                        var enc = TransferFlight.Encounter(PredictBurn.Setup.Prepare(rig.Snapshot(), ask.Target, out _));
                        flight.PeAfterFarOut = enc == null ? double.NaN : enc.Pe;
                    }
                }
                else rig.MoveTo(step.Ut);
                if (!tilted && rig.Body.Name == "Sun")
                {
                    rig.Fly(Tilt, rig.Ut, tiltAlong, tilt, 0);
                    tilted = true;
                }
                if (double.IsNaN(flight.IncAtEntry) && rig.Body.Name == ask.Target) flight.IncAtEntry = rig.Orbit.Inclination;
                TransferFlight.Carry(ask, step);
            }
            throw new InvalidOperationException("transfer_to took over 30 steps");
        }

        [Fact]
        public void A_Duna_transfer_at_plane_0_sets_the_plane_far_out_and_arrives_within_a_few_degrees_of_it()
        {
            var plain = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, "none"), ArcTilt);
            var flight = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, 0.0), ArcTilt);
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            Assert.Equal("Duna", flight.Rig.Body.Name);
            var added = flight.FarOut - plain.FarOut;
            _out.WriteLine("no plane: in at {0:0.0}°, far out {1:0.00} m/s, inside {2:0.00} m/s | {3}", plain.IncAtEntry, plain.FarOut, plain.Inside("Duna"), plain.Burns);
            _out.WriteLine("plane 0:  in at {0:0.0}°, far out {1:0.00} m/s (+{2:0.00}), inside {3:0.00} m/s, ends at {4:0.00}° | {5}", flight.IncAtEntry, flight.FarOut, added, flight.Inside("Duna"), flight.Rig.Orbit.Inclination, flight.Burns);
            // The flown flight came in tens of degrees off: the test means nothing unless this one would too.
            Assert.True(plain.IncAtEntry > 10, "the rig's plain arrival is " + plain.IncAtEntry + "°");
            // Set before the sphere of influence, and held to the end.
            Assert.InRange(flight.IncAtEntry, 0, 3);
            Assert.InRange(flight.Rig.Orbit.Inclination, 0, 3);
            // Tens of m/s at most far out, against the 806 the flight spent after capture.
            Assert.InRange(added, 0, 50);
            Assert.True(flight.FarOut + flight.Inside("Duna") < FlownPlaneChange / 10, flight.Burns);
        }

        [Fact]
        public void Auto_with_a_next_leg_to_land_at_Duna_s_equator_arrives_within_a_few_degrees_of_0()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            // The flight's own legs, words as Claudia wrote them (events.jsonl, propose_mission_plan): the leg in hand names its capture, so the landing after it is no direct entry (#762).
            rig.Stamp = s => s.LegsAhead = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Transfer to Duna, optimal window", Intent = "Depart in the optimal Kerbin-Duna window (UT about 162382126), coast to Duna, and capture into a 60 km orbit." },
                new PlannedSequence { Name = "Land at Duna equator", Intent = "Deorbit toward a daylight site on the Duna equator (found with find_landing_site) and fly the powered descent to touchdown." },
                new PlannedSequence { Name = "Crew report at Duna", Intent = "Run the crewReport experiment from the landed pod and transmit it." },
            };
            var flight = Fly(rig, Ask("Duna", 60000), ArcTilt);
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            var aimed = ArrivalPlane.FromRecord(flight.Last.AimedPlane);
            Assert.NotNull(aimed);
            Assert.Equal(0, aimed.Inc);
            _out.WriteLine("auto, equator: in at {0:0.0}°, ends at {1:0.00}° | {2}", flight.IncAtEntry, flight.Rig.Orbit.Inclination, flight.Burns);
            Assert.InRange(flight.IncAtEntry, 0, 3);
            Assert.InRange(flight.Rig.Orbit.Inclination, 0, 3);
        }

        [Theory]
        [InlineData("Land at Duna equator", 0.0)]
        [InlineData("Land on Duna's equator", 0.0)]
        [InlineData("Land the off-road rover on the equator, not near the canyon", 0.0)]
        [InlineData("Equatorial landing on Duna", 0.0)]
        [InlineData("Land at Duna's north pole", 90.0)]
        [InlineData("Polar landing on Duna", 90.0)]
        public void A_next_landing_leg_s_band_gives_its_plane(string leg, double inc)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            snapshot.LegsAhead = Legs("Transfer to Duna and capture", leg);
            string error;
            var plane = ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error);
            Assert.Equal("", error);
            Assert.NotNull(plane);
            Assert.Equal("site", plane.Source);
            Assert.Equal(inc, plane.Inc, 1);
        }

        [Fact]
        public void A_picked_site_still_gives_its_latitude_and_an_equatorial_pick_with_no_band_sets_none()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            snapshot.LegsAhead = Legs("Transfer to Duna and capture", "Land at Duna equator");
            snapshot.PickedSite = new PickedSite { Body = "Duna", Latitude = 20 };
            string error;
            Assert.Equal(20.5, ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error).Inc, 3);
            // The flight's own pick, 0.025° N, under the equator's band word: the equator.
            snapshot.PickedSite = new PickedSite { Body = "Duna", Latitude = 0.025 };
            Assert.Equal(0, ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error).Inc);
            snapshot.LegsAhead = Legs("Transfer to Duna and capture", "Land at the site");
            Assert.Null(ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error));
        }

        [Fact]
        public void A_landing_with_no_capture_before_it_is_still_a_direct_entry_with_no_plane_on_auto()
        {
            // #762: "Transfer to Duna" then "Land at Duna equator", no capture named: the craft comes straight down, and only Claudia's own value applies.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            snapshot.LegsAhead = Legs("Transfer to Duna", "Land at Duna equator");
            string error;
            Assert.Null(ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error));
        }

        [Fact]
        public void A_trip_home_to_Kerbin_on_auto_aims_far_out_for_KSC_s_plane()
        {
            var plain = Fly(TransferRig.Circular("Duna", 100000, 0), Ask("Kerbin", 30000, "none"), ArcTilt);
            var flight = Fly(TransferRig.Circular("Duna", 100000, 0), Ask("Kerbin", 30000), ArcTilt);
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            _out.WriteLine("home, no plane: in at {0:0.0}°, far out {1:0.00} m/s | {2}", plain.IncAtEntry, plain.FarOut, plain.Burns);
            _out.WriteLine("home, auto:     in at {0:0.0}°, far out {1:0.00} m/s (+{2:0.00}), ends at {3:0.00}° | {4}", flight.IncAtEntry, flight.FarOut, flight.FarOut - plain.FarOut, flight.Rig.Orbit.Inclination, flight.Burns);
            Assert.True(plain.IncAtEntry > 10, "the rig's plain arrival is " + plain.IncAtEntry + "°");
            // KSC is at 0.1° S: the equator, prograde.
            Assert.InRange(flight.IncAtEntry, 0, 3);
            Assert.InRange(flight.FarOut - plain.FarOut, 0, 50);
            // The plane isn't recorded for a capture: a trip home ends in reentry, and the landing aim inside Kerbin's sphere of influence (#915) is unchanged.
            Assert.Equal("", flight.Last.AimedPlane);
        }

        [Fact]
        public void A_trip_home_with_a_goal_naming_another_place_aims_no_plane()
        {
            var rig = TransferRig.Circular("Duna", 100000, 0);
            rig.Stamp = s => s.MissionGoal = "Fly to Duna and back, and land in the ocean.";
            var plain = Fly(TransferRig.Circular("Duna", 100000, 0), Ask("Kerbin", 30000, "none"), ArcTilt);
            var flight = Fly(rig, Ask("Kerbin", 30000), ArcTilt);
            Assert.Equal(plain.IncAtEntry, flight.IncAtEntry, 3);
        }

        /// <summary>The halfway correction flown as exec_node stops it: short by <see cref="ExecNodeStop"/>.</summary>
        private static double Stopped(TransferFlightStep step)
        {
            return Dv(step) - ExecNodeStop(step);
        }

        private static double Dv(TransferFlightStep step)
        {
            return Math.Sqrt(step.Prograde * step.Prograde + step.Normal * step.Normal + step.Radial * step.Radial);
        }

        /// <summary>m/s burned round <paramref name="body"/> but the ejection: the far-out corrections between moons.</summary>
        private static double Round(Flight flight, string body)
        {
            return flight.Rig.Burns.Where((b, i) => b.Key != "ejection" && b.Key != Tilt && flight.Rig.BurnBodies[i] == body).Sum(b => b.Value);
        }

        private static double PeAtEnd(Flight flight)
        {
            return flight.Rig.Orbit.PeR - flight.Rig.Body.Radius;
        }

        /// <summary>
        /// Review of #986 item 1: far out a plane burn swings the arrival sideways, and exec_node's own stop (0.1 m/s short of a 0.32 m/s node) put Duna's periapsis
        /// 122 km lower, 62 km into the planet. The far-out plane burn is stopped within 0.005 m/s (max_residual 0.02): flown that short, the periapsis stays within
        /// 10 km of its aim, and the trim inside is a fraction of a m/s.
        /// </summary>
        [Theory]
        [InlineData("Duna", 60000.0, false)]
        [InlineData("Kerbin", 30000.0, true)]
        public void A_far_out_plane_burn_stops_within_0_005_m_s_and_flown_that_short_keeps_the_periapsis_near_its_aim(string target, double pe, bool home)
        {
            var flight = Fly(TransferRig.Circular(home ? "Duna" : "Kerbin", 100000, 0), Ask(target, pe, home ? null : (object)0.0), ArcTilt, Stopped);
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            _out.WriteLine("{0}: plane burn {1:0.000} m/s, max_residual {2}, stopped {3} m/s short; pe after it {4:0} m; in at {5:0.0}°; inside {6:0.00} m/s | {7}", target, Dv(flight.FarOutStep),
                flight.FarOutStep.MaxResidual, ExecNodeStop(flight.FarOutStep), flight.PeAfterFarOut, flight.IncAtEntry, flight.Inside(target), flight.Burns);
            Assert.InRange(ExecNodeStop(flight.FarOutStep), 0, 0.005);
            Assert.InRange(flight.PeAfterFarOut, pe - 10000, pe + 10000);
            Assert.InRange(flight.IncAtEntry, 0, 3);
            Assert.InRange(flight.Inside(target), 0, 1);
            Assert.InRange(PeAtEnd(flight), pe - 1000, pe + 1000);
        }

        /// <summary>
        /// Review of #986 item 2: a plane burn that ends far short (an engine that cuts out, not exec_node's stop) leaves the periapsis inside the planet, and the
        /// next step restores it: the trim inside the sphere of influence, which every far target with a plane flies (PeFineCorrection waits for it). At Duna it
        /// sets the plane too; a trip home has no plane trim inside, so it comes in off its plane, with its periapsis restored for the landing aim.
        /// </summary>
        [Theory]
        [InlineData("Duna", 60000.0, false, 0.7)]
        [InlineData("Duna", 60000.0, false, 0.5)]
        [InlineData("Kerbin", 30000.0, true, 0.7)]
        [InlineData("Kerbin", 30000.0, true, 0.5)]
        public void A_far_out_plane_burn_flown_short_has_its_periapsis_restored_inside(string target, double pe, bool home, double share)
        {
            var flight = Fly(TransferRig.Circular(home ? "Duna" : "Kerbin", 100000, 0), Ask(target, pe, home ? null : (object)0.0), ArcTilt, s => share * Dv(s));
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            _out.WriteLine("{0} at {1:0%}: pe after the plane burn {2:0} m; in at {3:0.0}°; inside {4:0.00} m/s; ends pe {5:0} m at {6:0.0}° | {7}", target, share, flight.PeAfterFarOut,
                flight.IncAtEntry, flight.Inside(target), PeAtEnd(flight), flight.Rig.Orbit.Inclination, flight.Burns);
            Assert.True(flight.PeAfterFarOut < 0, "the rig's short burn should put the periapsis inside " + target + ": " + flight.PeAfterFarOut);
            Assert.InRange(PeAtEnd(flight), pe - 1000, pe + 1000);
            Assert.InRange(flight.Inside(target), 0, 10);
            if (!home) Assert.InRange(flight.Rig.Orbit.Inclination, 0, 3);
        }

        /// <summary>
        /// Review of #986 item 5: an arrival coming in retrograde (155°) aimed for the equator is turned far out, by way of the far side of the planet: 0.34 m/s, where
        /// the trim inside alone pays 23.9 m/s. Stopped within 0.005 m/s, its periapsis stays above the surface, within 20 km of its aim; flown at 70%, the trim inside
        /// restores it. So there is no angle past which far out is declined.
        /// </summary>
        [Fact]
        public void A_retrograde_arrival_aimed_for_the_equator_is_turned_far_out_and_its_periapsis_kept()
        {
            var plain = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, "none"), 0.5, null, 0.5);
            var stopped = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, 0.0), 0.5, Stopped, 0.5);
            var shortOf = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, 0.0), 0.5, s => 0.7 * Dv(s), 0.5);
            var insideOnly = Fly(TransferRig.Circular("Kerbin", 100000, 0), Ask("Duna", 60000, 0.0), 0.5, s => 0, 0.5);
            _out.WriteLine("plain in at {0:0.0}°; stopped: plane burn {1:0.00} m/s, pe after {2:0} m, in at {3:0.0}°, inside {4:0.00} m/s; at 70%: pe after {5:0} m, inside {6:0.00} m/s; inside only: {7:0.00} m/s",
                plain.IncAtEntry, Dv(stopped.FarOutStep), stopped.PeAfterFarOut, stopped.IncAtEntry, stopped.Inside("Duna"), shortOf.PeAfterFarOut, shortOf.Inside("Duna"), insideOnly.Inside("Duna"));
            Assert.True(plain.IncAtEntry > 150, "the rig's plain arrival is " + plain.IncAtEntry + "°");
            Assert.True(stopped.Last.Error == "", stopped.Last.Error + " | " + stopped.Burns);
            Assert.InRange(ExecNodeStop(stopped.FarOutStep), 0, 0.005);
            Assert.InRange(stopped.PeAfterFarOut, 60000 - 20000, 60000 + 20000);
            Assert.InRange(stopped.IncAtEntry, 0, 3);
            Assert.InRange(stopped.Inside("Duna"), 0, 1);
            Assert.True(shortOf.Last.Error == "", shortOf.Last.Error + " | " + shortOf.Burns);
            Assert.InRange(PeAtEnd(shortOf), 59000, 61000);
            Assert.InRange(shortOf.Rig.Orbit.Inclination, 0, 3);
            // Far out is the cheaper place by far, even for a turn this size.
            Assert.True(Dv(stopped.FarOutStep) + stopped.Inside("Duna") < insideOnly.Inside("Duna") / 10, stopped.Burns + " against " + insideOnly.Burns);
        }

        /// <summary>
        /// Review of #986 item 4: a moon to its sibling (the Mun to Minmus) is a far target, and its halfway burn round Kerbin now aims a number, site or home plane.
        /// That is the cheaper place: 1.7 m/s there for a polar arrival, against 10.8 m/s for the trim inside Minmus's small sphere of influence alone, and stopped
        /// within 0.005 m/s the periapsis stays within 1 km of its aim.
        /// </summary>
        [Fact]
        public void A_sibling_moon_s_plane_is_set_at_the_halfway_burn_for_less_than_the_trim_inside_would_cost()
        {
            var flight = Fly(TransferRig.Circular("Mun", 30000, 0), Ask("Minmus", 30000, 90.0), 0, Stopped);
            var insideOnly = Fly(TransferRig.Circular("Mun", 30000, 0), Ask("Minmus", 30000, 90.0), 0, s => 0);
            _out.WriteLine("Minmus polar: round Kerbin {0:0.00} m/s, pe after {1:0} m, in at {2:0.0}°, inside {3:0.00} m/s; inside only: in at {4:0.0}°, {5:0.00} m/s | {6}",
                Round(flight, "Kerbin"), flight.PeAfterFarOut, flight.IncAtEntry, flight.Inside("Minmus"), insideOnly.IncAtEntry, insideOnly.Inside("Minmus"), flight.Burns);
            Assert.True(flight.Last.Error == "", flight.Last.Error + " | " + flight.Burns);
            Assert.InRange(ExecNodeStop(flight.FarOutStep), 0, 0.005);
            Assert.InRange(flight.PeAfterFarOut, 29000, 31000);
            Assert.InRange(flight.IncAtEntry, 89, 91);
            Assert.True(Round(flight, "Kerbin") + flight.Inside("Minmus") < insideOnly.Inside("Minmus") / 3, flight.Burns + " against " + insideOnly.Burns);
        }

        /// <summary>Review of #986 item 3: a capture the leg in hand turns down is none, so a landing after it is still a direct entry (#762).</summary>
        [Theory]
        [InlineData("Transfer to Duna, skip capture, straight down")]
        [InlineData("Transfer to Duna with no capture: direct descent")]
        [InlineData("Transfer to Duna without capturing")]
        public void A_leg_in_hand_that_turns_its_capture_down_is_still_a_direct_entry_with_no_plane_on_auto(string leg)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            snapshot.LegsAhead = Legs(leg, "Land at Duna equator");
            string error;
            Assert.Null(ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error));
            // A leg naming its capture gives the equator, whatever else its clause turns down.
            foreach (var capture in new[] { "Transfer to Duna, capture into orbit", "Transfer to Duna without delay and capture into orbit" })
            {
                snapshot.LegsAhead = Legs(capture, "Land at Duna equator");
                Assert.Equal(0, ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error).Inc);
            }
        }

        /// <summary>Review of #986 item 6: the equator named as where the landing isn't sets no plane.</summary>
        [Theory]
        [InlineData("Land at Duna, far from the equator")]
        [InlineData("Land at Duna away from the equator")]
        [InlineData("Land at Duna, not on the equator")]
        [InlineData("Land at Duna, far from Duna's equator")]
        [InlineData("Land at Duna, 30 km south of the equator")]
        public void A_landing_leg_that_names_the_equator_as_where_it_isn_t_gives_no_plane(string leg)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var snapshot = rig.Snapshot();
            snapshot.LegsAhead = Legs("Transfer to Duna and capture", leg);
            string error;
            Assert.Null(ArrivalPlanes.Resolve(snapshot, rig.System, rig.System.Find("Duna"), PlaneOption.Auto, out error));
        }
    }
}
