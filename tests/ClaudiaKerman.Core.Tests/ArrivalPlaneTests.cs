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
    /// The arrival plane on the approach (#788): who picks it (<see cref="ArrivalPlanes"/>), and <see cref="TransferFlight"/> flown on
    /// <see cref="TransferRig"/> to reach it inside the target's sphere of influence, direction of travel included.
    /// </summary>
    public sealed class ArrivalPlaneTests
    {
        private static TransferFlightAsk Ask(string target, double pe, object plane = null)
        {
            return new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = 0, TurnTime = 60, PlaneOption = PlaneOption.Read(plane) };
        }

        private static List<PlannedSequence> Legs(params string[] names)
        {
            return names.Select(n => new PlannedSequence { Name = n }).ToList();
        }

        private static string Flown(TransferRig rig)
        {
            return string.Join(", ", rig.Burns.Select((b, i) => b.Key + " " + b.Value.ToString("0.##") + " round " + rig.BurnBodies[i]));
        }

        /// <summary>m/s of every burn after the ejection: what the trip cost beyond it.</summary>
        private static double AfterEjection(TransferRig rig)
        {
            return rig.Burns.Where(b => b.Key != "ejection").Sum(b => b.Value);
        }

        /// <summary>A flight with no plane, to learn where the approach comes in from: the plane through it is the one a flight should reach for free.</summary>
        private static Vec3 ApproachOf(string body, double altitude, string target, double pe)
        {
            var rig = TransferRig.Circular(body, altitude, 0);
            var step = rig.Run(Ask(target, pe), 30);
            Assert.True(step.Error == "", step.Error);
            rig.MoveTo(Math.Max(rig.Ut, D(step, "encounter_ut") + 60));
            return ArrivalPlanes.WayIn(rig.Orbit, rig.Ut);
        }

        /// <summary>
        /// Where that flight enters the sphere of influence. A far target's trim burns there, so a plane through it is reached exactly; one through the
        /// asymptote is off it by the miss distance (under a degree at Duna), which <c>capture</c> takes up.
        /// </summary>
        private static Vec3 EntryOf(string body, double altitude, string target, double pe)
        {
            var rig = TransferRig.Circular(body, altitude, 0);
            var step = rig.Run(Ask(target, pe), 30);
            Assert.True(step.Error == "", step.Error);
            rig.MoveTo(Math.Max(rig.Ut, D(step, "encounter_ut") + 200));
            return rig.Orbit.Position(rig.Ut);
        }

        /// <summary>An orbit's inclination and LAN whose normal is <paramref name="n"/>.</summary>
        private static void Elements(Vec3 n, out double inc, out double lan)
        {
            inc = Math.Acos(n.Z) * 180 / Math.PI;
            lan = Math.Atan2(n.X, -n.Y) * 180 / Math.PI;
        }

        private static double D(TransferFlightStep step, string key)
        {
            return Convert.ToDouble(step.Report[key]);
        }

        private static SnapshotTarget Vessel(double inc, double lan, string body, double pe = 40000, double ap = 60000, double radius = 200000)
        {
            return new SnapshotTarget
            {
                Name = "Station", Body = body, Situation = "ORBITING",
                Orbit = new SnapshotOrbit { Inclination = inc, Lan = lan, Periapsis = pe, Apoapsis = ap, SemiMajorAxis = radius + (pe + ap) / 2, Eccentricity = (ap - pe) / (2 * radius + pe + ap), Body = body },
            };
        }

        // ---- The approach trim reaches each kind of target ----

        [Theory]
        [InlineData(90.0)]
        [InlineData(150.0)]
        [InlineData(20.0)]
        public void A_number_is_reached_at_the_Mun_direction_included(double inc)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Mun", 30000, inc), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.Equal("Mun", rig.Body.Name);
            Assert.InRange(rig.Orbit.Inclination, inc - 0.5, inc + 0.5);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 29000, 31000);
            // Far under what the same turn costs in a low Mun orbit (about 760 m/s for 90 degrees).
            Assert.True(AfterEjection(rig) < 80, Flown(rig));
        }

        [Fact]
        public void A_polar_number_at_Duna_costs_a_few_m_s_on_the_approach()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Duna", 60000, 90.0), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.Equal("Duna", rig.Body.Name);
            Assert.InRange(rig.Orbit.Inclination, 89.5, 90.5);
            Assert.True(rig.Burned("plane") < 25, Flown(rig));
        }

        [Fact]
        public void A_Target_Vessel_s_plane_through_the_approach_is_reached_at_its_periapsis_altitude()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var approach = ApproachOf("Kerbin", 100000, "Mun", 30000).Normalized;
            var normal = Vec3.Cross(approach, new Vec3(0.3, -0.5, 0.8)).Normalized;
            double inc, lan;
            Elements(normal, out inc, out lan);
            rig.Stamp = s => s.Target = Vessel(inc, lan, "Mun");
            var step = rig.Run(Ask("Mun", 30000), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            // The halfway burn swings the Mun's slow approach, so the trim ends some degrees off a whole plane; capture takes that up (CaptureFlightTests).
            Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 9, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
            // The vessel's periapsis altitude, not target_pe.
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 39000, 41000);
            Assert.True(AfterEjection(rig) < 80, Flown(rig));
            Assert.Equal("target_vessel", ArrivalPlane.FromRecord(step.AimedPlane).Source);
        }

        [Fact]
        public void A_retrograde_Target_Vessel_gets_a_retrograde_arrival()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var approach = ApproachOf("Kerbin", 100000, "Mun", 30000).Normalized;
            var normal = Vec3.Cross(approach, new Vec3(0.3, -0.5, 0.8)).Normalized;
            if (normal.Z > 0) normal = normal * -1;
            double inc, lan;
            Elements(normal, out inc, out lan);
            Assert.True(inc > 90);
            rig.Stamp = s => s.Target = Vessel(inc, lan, "Mun");
            var step = rig.Run(Ask("Mun", 30000), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 9, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
            Assert.True(rig.Orbit.Inclination > 90);
        }

        [Fact]
        public void A_moon_s_plane_is_matched_in_direction_never_its_mirror()
        {
            // Minmus's plane at the Mun: 6 degrees, not 174, whatever way round the approach came in.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Mun", 30000, "Minmus"), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.True(rig.Orbit.Inclination < 90, rig.Orbit.Inclination + " | " + Flown(rig));
            var wanted = rig.System.OrbitOf(rig.System.Find("Minmus")).W;
            // Round the plane through the approach: the rest is capture's.
            var off = ArrivalPlane.Angle(rig.Orbit.W, wanted);
            Assert.True(off <= 15, off + " | " + Flown(rig));
        }

        [Fact]
        public void A_moon_s_whole_plane_through_the_approach_is_reached_inclination_and_LAN()
        {
            // Ike's plane, moved so it holds this approach: at Duna the trim then reaches it within half a degree.
            var approach = EntryOf("Kerbin", 100000, "Duna", 60000).Normalized;
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var normal = Vec3.Cross(approach, new Vec3(0.2, 0.1, 0.9)).Normalized;
            double inc, lan;
            Elements(normal, out inc, out lan);
            var step = rig.Run(Ask("Duna", 60000, new Dictionary<string, object> { { "inc", inc }, { "lan", lan }, { "name", "Ike's stand-in" }, { "body", "Duna" } }), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.True(ArrivalPlane.Angle(rig.Orbit.W, normal) <= 0.5, ArrivalPlane.Angle(rig.Orbit.W, normal) + " | " + Flown(rig));
            Assert.True(rig.Burned("plane") < 25, Flown(rig));
        }

        [Fact]
        public void A_landing_site_s_latitude_sets_a_prograde_inclination_half_a_degree_past_it()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.Stamp = s =>
            {
                s.LegsAhead = Legs("Transfer to the Mun", "Land at (-30.2, 40.1)");
            };
            var step = rig.Run(Ask("Mun", 30000), 30);
            Assert.True(step.Error == "", step.Error + " | " + Flown(rig));
            Assert.InRange(rig.Orbit.Inclination, 30.2, 31.2);
            Assert.True(AfterEjection(rig) < 40, Flown(rig));
        }

        [Fact]
        public void Auto_with_nothing_applicable_flies_as_it_did()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var step = rig.Run(Ask("Mun", 30000), 30);
            Assert.True(step.Error == "", step.Error);
            Assert.Equal("", step.AimedPlane);
            Assert.Equal("Kerbin", rig.Body.Name);
            Assert.Equal(0, rig.Burned("plane"));
            Assert.False(step.Report.ContainsKey("plane_off_deg"));
        }

        [Fact]
        public void Correction_false_flies_no_plane_trim_but_still_records_the_plane()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            var ask = Ask("Mun", 30000, 90.0);
            ask.Correction = false;
            var step = rig.Run(ask, 30);
            Assert.True(step.Error == "", step.Error);
            Assert.Equal(0, rig.Burned("plane"));
            Assert.Equal("Kerbin", rig.Body.Name);
            Assert.Equal(90, ArrivalPlane.FromRecord(step.AimedPlane).Inc);
        }

        [Fact]
        public void A_bad_plane_fails_at_the_first_step()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            Assert.Contains("plane must be", TransferFlight.Next(rig.Snapshot(), Ask("Mun", 30000, 200.0)).Error);
            Assert.Contains("plane must be", TransferFlight.Next(rig.Snapshot(), Ask("Mun", 30000, "Nowhere")).Error);
            Assert.Contains("orbits nothing", TransferFlight.Next(rig.Snapshot(), Ask("Mun", 30000, "Sun")).Error);
        }

        // ---- Who picks: the priority order of item 3 ----

        private sealed class Scene
        {
            public readonly TransferRig Rig = TransferRig.Circular("Kerbin", 100000, 0);
            public GameSnapshot Snapshot;

            public Scene()
            {
                Snapshot = Rig.Snapshot();
            }

            public ArrivalPlane Pick(string arrival, object option = null)
            {
                string error;
                var plane = ArrivalPlanes.Resolve(Snapshot, Rig.System, Rig.System.Find(arrival), PlaneOption.Read(option), out error);
                Assert.Equal("", error);
                return plane;
            }
        }

        [Fact]
        public void Claudia_s_value_wins_over_everything()
        {
            var scene = new Scene();
            scene.Snapshot.Target = Vessel(45, 10, "Mun");
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Land at (10, 10)");
            scene.Snapshot.PickedSite = new PickedSite { Body = "Mun", Latitude = 20 };
            Assert.Equal("number", scene.Pick("Mun", 90.0).Source);
            Assert.Null(scene.Pick("Mun", "none"));
            Assert.Equal("body", scene.Pick("Mun", "Minmus").Source);
        }

        [Fact]
        public void A_Target_Vessel_orbiting_the_arrival_body_comes_next()
        {
            var scene = new Scene();
            scene.Snapshot.Target = Vessel(45, 10, "Mun", 41000);
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Land at (10, 10)");
            var plane = scene.Pick("Mun");
            Assert.Equal("target_vessel", plane.Source);
            Assert.Equal(41000, plane.VesselPe);
            Assert.InRange(plane.Inc, 44.9, 45.1);
        }

        [Fact]
        public void A_Target_Vessel_round_another_body_is_not_this_arrival_s()
        {
            var scene = new Scene();
            scene.Snapshot.Target = Vessel(45, 10, "Minmus");
            Assert.Null(scene.Pick("Mun"));
        }

        [Fact]
        public void The_next_leg_s_landing_site_comes_next_the_pick_first()
        {
            var scene = new Scene();
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Capture at the Mun", "Land at (10, 10)");
            Assert.Equal(10.5, scene.Pick("Mun").Inc, 3);
            scene.Snapshot.PickedSite = new PickedSite { Body = "Mun", Latitude = -20 };
            var plane = scene.Pick("Mun");
            Assert.Equal("site", plane.Source);
            Assert.Null(plane.Normal);
            Assert.Equal(20.5, plane.Inc, 3);
            // A pick on another body says nothing of this one.
            scene.Snapshot.PickedSite = new PickedSite { Body = "Minmus", Latitude = -20 };
            Assert.Equal(10.5, scene.Pick("Mun").Inc, 3);
        }

        [Fact]
        public void A_landed_Target_Vessel_is_the_next_leg_s_site()
        {
            var scene = new Scene();
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Land beside the base");
            scene.Snapshot.Target = new SnapshotTarget { Name = "Base", Body = "Mun", Situation = "LANDED", Position = new SnapshotPosition { Latitude = 15 } };
            Assert.Equal(15.5, scene.Pick("Mun").Inc, 3);
        }

        [Fact]
        public void A_Point_of_Interest_in_the_next_leg_s_words_is_its_site()
        {
            var scene = new Scene();
            var poi = PointsOfInterest.Merge("Mun", new SurfaceObject[0]).First(p => Math.Abs(p.Latitude) > 1);
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Land beside " + poi.Name);
            Assert.Equal(Math.Min(90, Math.Abs(poi.Latitude) + 0.5), scene.Pick("Mun").Inc, 3);
        }

        [Fact]
        public void An_equatorial_site_sets_no_target()
        {
            var scene = new Scene();
            scene.Snapshot.LegsAhead = Legs("Transfer to the Mun", "Land at (0.2, 10)");
            Assert.Null(scene.Pick("Mun"));
        }

        [Fact]
        public void The_next_leg_s_moon_comes_after_a_site_whole_plane_and_direction()
        {
            var scene = new Scene();
            scene.Snapshot.LegsAhead = Legs("Transfer to Duna", "Capture at Duna", "Transfer to Ike");
            var plane = scene.Pick("Duna");
            Assert.Equal("moon", plane.Source);
            Assert.True(plane.Normal.HasValue);
            Assert.True(ArrivalPlane.Angle(plane.Normal.Value, scene.Rig.System.OrbitOf(scene.Rig.System.Find("Ike")).W) < 0.01);
        }

        [Fact]
        public void A_leg_elsewhere_stops_the_search()
        {
            var scene = new Scene();
            scene.Snapshot.LegsAhead = Legs("Transfer to Duna", "Capture at Duna", "Return to Kerbin", "Transfer to Ike");
            Assert.Null(scene.Pick("Duna"));
        }

        [Fact]
        public void A_return_home_that_ends_in_reentry_has_no_plane_even_asked()
        {
            var scene = new Scene();
            Assert.Null(scene.Pick("Kerbin", 90.0));
        }

        // ---- The record ----

        [Fact]
        public void The_record_keeps_the_plane()
        {
            var plane = new ArrivalPlane { Arrival = "Minmus", Source = "target_vessel", Inc = 12.25, Normal = new Vec3(0.1, -0.2, 0.97), VesselPe = 41000 };
            var back = ArrivalPlane.FromRecord(plane.ToRecord());
            Assert.Equal("Minmus", back.Arrival);
            Assert.Equal(12.25, back.Inc);
            Assert.Equal(-0.2, back.Normal.Value.Y);
            Assert.Equal(41000, back.VesselPe);
            var number = ArrivalPlane.FromRecord(new ArrivalPlane { Arrival = "Mun", Source = "number", Inc = 90 }.ToRecord());
            Assert.Null(number.Normal);
            Assert.Null(number.VesselPe);
            Assert.Null(ArrivalPlane.FromRecord(""));
            Assert.Null(ArrivalPlane.FromRecord("nonsense"));
        }
    }
}
