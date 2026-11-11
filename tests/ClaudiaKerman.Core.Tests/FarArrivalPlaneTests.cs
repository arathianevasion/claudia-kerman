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

        /// <summary><see cref="TransferRig.Run"/>, noting the inclination the craft has as it first finds itself round <paramref name="ask"/>'s target.</summary>
        private static Flight Fly(TransferRig rig, TransferFlightAsk ask, double tilt = 0)
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
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                if (!tilted && rig.Body.Name == "Sun")
                {
                    rig.Fly(Tilt, rig.Ut, 0, tilt, 0);
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
    }
}
