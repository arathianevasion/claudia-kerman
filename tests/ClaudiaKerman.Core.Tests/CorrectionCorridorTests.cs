// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The aimed correction spends spare delta-v (#817): the periapsis corridor at Kerbin, the reserve with the later legs (#832), a reason for every burn it stops at, and
    /// a correction it can't solve that is no failure (#805). Each answer's landing is flown afresh through the patches and the predictor.
    /// </summary>
    public sealed class CorrectionCorridorTests
    {
        private const double Pe = 30000;
        private const double KmPerDeg = 600 * Math.PI / 180;
        private readonly ITestOutputHelper _out;

        public CorrectionCorridorTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The correction halfway home from the Mun, as return times it: about 15,000 s before the periapsis.</summary>
        private const double Halfway = 15000;

        /// <summary>A capsule with one chute: it needn't land on its engines, so nothing is kept back for that.</summary>
        private sealed class ChutedCapsule : IReentryCraft
        {
            private readonly ReentrySim.Capsule _capsule = new ReentrySim.Capsule();
            public double MassKg { get { return _capsule.MassKg; } }
            public double DragArea(double mach, double densitySpeed) { return _capsule.DragArea(mach, densitySpeed); }
            public double LiftArea(double mach, double angleOfAttack) { return _capsule.LiftArea(mach, angleOfAttack); }
            public IList<ReentryChute> Chutes { get { return new[] { new ReentryChute() }; } }
            public IReentryCraft AfterShield { get { return null; } }
        }

        private static SnapshotBody Kerbin(GameSnapshot snapshot)
        {
            return snapshot.Bodies.Find(b => b.Name == "Kerbin");
        }

        private static IDictionary<string, object> Aim(GameSnapshot snapshot, double lat, double lng, double dvLeft, double burnIn = 300, double pe = Pe, double peTolerance = 1000)
        {
            return BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", pe }, { "earliest_ut", snapshot.Ut + burnIn }, { "latest_ut", snapshot.Ut + burnIn }, { "lat", lat }, { "lng", lng },
                { "dv_left", dvLeft }, { "pe_tolerance", peTolerance },
            }, snapshot, new AimedCorrectionTests.FrameBody(snapshot.Ut), new ReentrySim.Atmosphere(), new ChutedCapsule());
        }

        private static double D(IDictionary<string, object> node, string key)
        {
            return Convert.ToDouble(node[key]);
        }

        private static Conic OrbitOf(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            return Conic.FromElements(Kerbin(snapshot).Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        /// <summary>The node flown afresh: its periapsis (m above sea level) and landing.</summary>
        private static ReentryPrediction Flown(GameSnapshot snapshot, IDictionary<string, object> node, out double pe)
        {
            var kerbin = Kerbin(snapshot);
            var orbit = OrbitOf(snapshot);
            var ut = D(node, "ut");
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            var after = Conic.FromState(kerbin.Gm, r, v + SingleShooting.FromNodeFrame(orbit, ut, D(node, "prograde"), D(node, "normal"), D(node, "radial")), ut);
            pe = after.PeR - kerbin.Radius;
            string problem;
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var craft = new ChutedCapsule();
            var landed = ReentryPredictor.Predict(kerbin, after, ut, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            return landed;
        }

        private static double Km(double fromLat, double fromLng, double toLat, double toLng)
        {
            return LandingSiteSearch.ArcDegrees(fromLat, fromLng, toLat, toLng) * Math.PI / 180 * 600;
        }

        private static void InCorridor(double pe)
        {
            Assert.InRange(pe, Pe - CorrectionBurn.PeCorridor - 100, Pe + CorrectionBurn.PeCorridor + 100);
        }

        /// <summary>The grilled criterion: about 100 km off with 80 m/s left (30 above the 50 m/s reserve) closes to within 5 km in prediction, the entry inside the corridor.</summary>
        [Theory]
        [InlineData(100)]
        [InlineData(-100)]
        public void A_100_km_miss_with_80_m_s_left_closes_within_5_km_with_the_entry_in_the_corridor(double eastKm)
        {
            var snapshot = AimedCorrectionTests.Falling(Pe, 0, Halfway);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var target = lng + eastKm / KmPerDeg;
            var node = Aim(snapshot, lat, target, 80);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            _out.WriteLine("{0} km: {1:0.00} m/s, Pe {2:0} m, {3:0.00} km left, peak {4:0.0} g; {5}", eastKm, D(node, "dv"), pe, Km(flown.Latitude, flown.Longitude, lat, target), flown.PeakG, node["aim_note"]);
            InCorridor(pe);
            Assert.InRange(Km(flown.Latitude, flown.Longitude, lat, target), 0, 5);
            Assert.InRange(D(node, "error_km"), 0, 5);
            Assert.InRange(D(node, "dv"), 0, 30);
            Assert.InRange(flown.PeakG, 0, CorrectionBurn.MostEntryG);
        }

        /// <summary>415 km (the flight's) is out of reach with 80 m/s left: it closes as far as the corridor and the 30 m/s above the reserve go, and reports the rest. No failure.</summary>
        [Theory]
        [InlineData(415)]
        [InlineData(-415)]
        public void A_415_km_miss_closes_as_far_as_it_reaches_and_reports_the_rest(double eastKm)
        {
            var snapshot = AimedCorrectionTests.Falling(Pe, 0, Halfway);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var target = lng + eastKm / KmPerDeg;
            var node = Aim(snapshot, lat, target, 80);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            var left = Km(flown.Latitude, flown.Longitude, lat, target);
            _out.WriteLine("{0} km: {1:0.00} m/s, Pe {2:0} m, {3:0.0} km left; {4}", eastKm, D(node, "dv"), pe, left, node["aim_note"]);
            InCorridor(pe);
            Assert.InRange(D(node, "dv"), 25, 30.5);
            Assert.InRange(left, 100, Math.Abs(eastKm) - 100);
            Assert.InRange(D(node, "error_km"), left - 0.01, left + 0.01);
            Assert.Contains("spent the 30 m/s it may above the reserve", (string)node["aim_note"]);
            Assert.Contains("80 m/s left", (string)node["aim_note"]);
        }

        /// <summary>A craft at the reserve burns nothing for the landing, not even the corridor's fraction of a m/s, and says what is left.</summary>
        [Fact]
        public void A_craft_at_the_reserve_burns_nothing_and_reports_the_remainder()
        {
            var snapshot = AimedCorrectionTests.Falling();
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + 100 / KmPerDeg, 50);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0, 0.1);
            Assert.InRange(D(node, "pe_predicted"), Pe - 1000, Pe + 1000);
            Assert.InRange(D(node, "error_km"), 95, 105);
            Assert.StartsWith("plain burn: ", (string)node["aim_note"]);
            Assert.Contains("km from the target, 50 m/s left", (string)node["aim_note"]);
        }

        /// <summary>#832: the Mission Plan's later legs come off what the landing may spend, as the reserve does: 80 left with 30 for later legs is a craft at the reserve.</summary>
        [Fact]
        public void The_later_legs_come_off_what_the_landing_may_spend()
        {
            var snapshot = AimedCorrectionTests.Falling();
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            snapshot.LaterLegsDv = 30;
            var node = Aim(snapshot, lat, lng + 100 / KmPerDeg, 80);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0, 0.1);
            Assert.Contains("0 m/s above the reserve", (string)node["aim_note"]);
            Assert.Equal(450 - 100, DeorbitBurn.AutoMaxDv(500, 0, 100), 9);
            Assert.Equal(450 - 120, DeorbitBurn.AutoMaxDv(500, 120, -5), 9);
        }

        // ---- a reason for each burn it stops at ----

        [Fact]
        public void With_no_landing_to_read_the_plain_burn_says_so()
        {
            var snapshot = AimedCorrectionTests.Falling(90000);
            var node = Aim(snapshot, 0, 0, 80, 300, 90000);
            Assert.Equal("", node["error"]);
            Assert.StartsWith("plain burn: no landing to read", (string)node["aim_note"]);
            Assert.EndsWith("80 m/s left", (string)node["aim_note"]);
        }

        [Fact]
        public void Too_late_to_move_the_landing_the_plain_burn_says_so()
        {
            var snapshot = AimedCorrectionTests.Falling(Pe, 0, 3000);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + 200 / KmPerDeg, 80, 120);
            Assert.Equal("", node["error"]);
            Assert.StartsWith("plain burn: too late to move the landing", (string)node["aim_note"]);
            Assert.Contains("km from the target, 80 m/s left", (string)node["aim_note"]);
        }

        /// <summary>Already there: no burn to report and nothing said.</summary>
        [Fact]
        public void An_arrival_already_there_says_nothing()
        {
            var snapshot = AimedCorrectionTests.Falling();
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng, 80);
            Assert.Equal("", node["aim_note"]);
            Assert.InRange(D(node, "dv"), 0, 0.1);
        }

        /// <summary>
        /// #805: a plain burn that converged but can't hold target_pe to pe_tolerance (here 1 mm) still flies when aimed at a landing: its periapsis is in the corridor. No aiming from it;
        /// the note says why, where it comes down and what is left.
        /// </summary>
        [Fact]
        public void A_plain_burn_just_off_pe_tolerance_still_flies_and_says_why()
        {
            var snapshot = AimedCorrectionTests.Falling(33000);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + 50 / KmPerDeg, 80, 300, Pe, 0.001);
            Assert.Equal("", node["error"]);
            Assert.InRange(D(node, "dv"), 0.1, 10);
            Assert.InRange(D(node, "pe_predicted"), Pe - 100, Pe + 100);
            Assert.StartsWith("plain burn: found no correction", (string)node["aim_note"]);
            Assert.Contains("km from the target, 80 m/s left", (string)node["aim_note"]);
            Assert.InRange(D(node, "error_km"), 10, 200);
        }

        /// <summary>The corridor's rule (#817): 5 km either way of target_pe at Kerbin, never under 25 km, and held at a target_pe under that; elsewhere held.</summary>
        [Theory]
        [InlineData("Kerbin", 30000, 25000, 35000)]
        [InlineData("Kerbin", 28000, 25000, 33000)]
        [InlineData("Kerbin", 25000, 25000, 30000)]
        [InlineData("Kerbin", 22000, 22000, 22000)]
        [InlineData("Sun", 30000, 30000, 30000)]
        public void The_corridor_has_an_absolute_floor_and_holds_a_target_pe_under_it(string body, double targetPe, double low, double high)
        {
            var snapshot = AimedCorrectionTests.Falling();
            double l, h;
            CorrectionBurn.CorridorOf(snapshot.Bodies.Find(b => b.Name == body), new CorrectionAsk { TargetPe = targetPe }, out l, out h);
            Assert.Equal(low, l);
            Assert.Equal(high, h);
        }

        /// <summary>The floor in flight: aimed with target_pe 28 km, a miss the deep end would close leaves the periapsis no lower than 25 km; with target_pe 22 km it is held.</summary>
        [Theory]
        [InlineData(28000, 100, 25000 - 10, 33000 + 10)]
        [InlineData(28000, -100, 25000 - 10, 33000 + 10)]
        [InlineData(22000, 100, 22000 - 1000, 22000 + 1000)]
        public void An_aimed_burn_keeps_its_periapsis_inside_the_floored_corridor(double targetPe, double eastKm, double lowest, double highest)
        {
            var snapshot = AimedCorrectionTests.Falling(targetPe, 0, Halfway);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var node = Aim(snapshot, lat, lng + eastKm / KmPerDeg, 80, 300, targetPe);
            Assert.Equal("", node["error"]);
            double pe;
            var flown = Flown(snapshot, node, out pe);
            _out.WriteLine("{0} m, {1} km: {2:0.00} m/s, Pe {3:0} m, peak {4:0.0} g; {5}", targetPe, eastKm, D(node, "dv"), pe, flown.PeakG, node["aim_note"]);
            Assert.InRange(pe, lowest, highest);
            Assert.InRange(flown.PeakG, 0, CorrectionBurn.MostEntryG);
        }

        /// <summary>#805's rule for a plain burn off pe_tolerance: it flies inside the corridor, or within twice pe_tolerance of target_pe.</summary>
        [Theory]
        [InlineData(30000, 1000, 31300, true)]
        [InlineData(30000, 1000, 24900, false)]
        [InlineData(30000, 1000, 35100, false)]
        [InlineData(22000, 1000, 23900, true)]
        [InlineData(22000, 1000, 24100, false)]
        public void A_periapsis_the_landing_may_be_left_on(double targetPe, double tolerance, double pe, bool acceptable)
        {
            var kerbin = Kerbin(AimedCorrectionTests.Falling());
            Assert.Equal(acceptable, CorrectionBurn.PeAcceptable(kerbin, new CorrectionAsk { TargetPe = targetPe, PeTolerance = tolerance }, pe));
        }

        /// <summary>The path as it is, to Kerbin from the snapshot's orbit, for <see cref="CorrectionBurn.Unsolved"/>.</summary>
        private static CorrectionPlan Uncorrected(GameSnapshot snapshot, double targetPe, double lat, double lng)
        {
            var kerbin = Kerbin(snapshot);
            var system = new BodySystem(snapshot.Bodies);
            var orbit = OrbitOf(snapshot);
            string problem;
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var ask = new CorrectionAsk
            {
                Target = "Kerbin", TargetPe = targetPe, DvLeft = 80,
                Landing = new ReturnLanding { Latitude = lat, Longitude = lng, Air = new ReentrySim.Atmosphere(), Craft = new ChutedCapsule(), Frame = frame },
            };
            return CorrectionBurn.Unsolved((when, dv) => SingleShooting.Arrive(system, kerbin, orbit, when, dv, kerbin, 1), snapshot.Ut + 300, kerbin, ask, "the solver gave up");
        }

        /// <summary>#805: with no plain burn to fly, none flies on a path whose periapsis is in the corridor; the return carries on, and the note says why, where it comes down and what is left.</summary>
        [Fact]
        public void With_no_burn_solved_none_flies_on_a_path_in_the_corridor_and_says_why()
        {
            var snapshot = AimedCorrectionTests.Falling(33000);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var plan = Uncorrected(snapshot, Pe, lat, lng + 50 / KmPerDeg);
            Assert.Null(plan.Error);
            Assert.Equal(0.0, plan.DeltaV);
            Assert.InRange(plan.PePredicted, 32000, 34000);
            Assert.StartsWith("no correction flown: the solver gave up; ", plan.AimNote);
            Assert.EndsWith("km from the target, 80 m/s left", plan.AimNote);
            Assert.InRange(plan.ErrorKm, 10, 200);
        }

        /// <summary>
        /// #805 is no licence to come home on no entry: with no burn solved, a path that skips the air (60 km) or dives too deep (15 km), or holds a target_pe under the floor too far off,
        /// is the block's error, as before #805, with the periapsis and the corridor in it.
        /// </summary>
        [Theory]
        [InlineData(60000, Pe, "without it the periapsis is 60 km, outside 25 to 35 km")]
        [InlineData(15000, Pe, "without it the periapsis is 15 km, outside 25 to 35 km")]
        [InlineData(23000, 20000, "without it the periapsis is 23 km, outside 20 km")]
        public void With_no_burn_solved_a_path_outside_the_corridor_fails_the_block(double pe, double targetPe, string why)
        {
            var snapshot = AimedCorrectionTests.Falling(pe);
            var plan = Uncorrected(snapshot, targetPe, 0, 0);
            Assert.Equal("found no correction: the solver gave up; " + why, plan.Error);
            Assert.Equal(0.0, plan.DeltaV);
        }

        /// <summary>Without a landing an unsolved correction is still the block's error, as before.</summary>
        [Fact]
        public void Without_a_landing_an_unsolved_correction_is_still_an_error()
        {
            var snapshot = AimedCorrectionTests.Falling(33000);
            var node = BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", Pe }, { "earliest_ut", snapshot.Ut + 300 }, { "pe_tolerance", 0.001 },
            }, snapshot);
            Assert.StartsWith("found no correction", (string)node["error"]);
        }

        // ---- #880: what closing a miss costs ----

        /// <summary>
        /// Measured for #880, no behaviour hangs on it: the burn that closes 415 km and 1,300 km (the worst miss after an inclined Mun return ejection, #816) with the
        /// limit lifted, halfway home and nearer the Mun. The figures go in the ticket's notes.
        /// </summary>
        [Theory]
        [InlineData(415, 0, 30000)]
        [InlineData(-415, 0, 30000)]
        [InlineData(1300, 0, 30000)]
        [InlineData(-1300, 0, 30000)]
        [InlineData(415, 0, 15000)]
        [InlineData(1300, 0, 15000)]
        [InlineData(-1300, 0, 15000)]
        [InlineData(-415, 0, 15000)]
        public void What_closing_a_miss_costs(double eastKm, double inc, double toPeriapsis)
        {
            var snapshot = AimedCorrectionTests.Falling(Pe, inc, toPeriapsis);
            double lat, lng;
            AimedCorrectionTests.Natural(snapshot, out lat, out lng);
            var kerbin = Kerbin(snapshot);
            var system = new BodySystem(snapshot.Bodies);
            string problem;
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var orbit = OrbitOf(snapshot);
            // East: halfway home the landing moves along the track mostly by the time of arrival against the turning ground, so about east whatever the inclination.
            var targetLat = lat;
            var targetLng = lng + eastKm / (KmPerDeg * Math.Cos(lat * Math.PI / 180));
            var ask = new CorrectionAsk
            {
                Target = "Kerbin", TargetPe = Pe, EarliestUt = snapshot.Ut + 300, LatestUt = snapshot.Ut + 300, DvLeft = 5000, MostLandingDv = 3000,
                Landing = new ReturnLanding { Latitude = targetLat, Longitude = targetLng, Air = new ReentrySim.Atmosphere(), Craft = new ChutedCapsule(), Frame = frame },
            };
            var plan = CorrectionBurn.Find(system, kerbin, orbit, snapshot.Ut, ask);
            Assert.Null(plan.Error);
            _out.WriteLine("{0} km, inc {1}, {2} s out: {3:0.0} m/s, Pe {4:0} m, {5:0.0} km left, peak {6:0.0} g; {7}", eastKm, inc, toPeriapsis, plan.DeltaV, plan.PePredicted, plan.ErrorKm,
                plan.Landing == null ? 0 : plan.Landing.PeakG, plan.AimNote);
            Assert.True(plan.ErrorKm < Math.Abs(eastKm) / 2, "it moves the landing most of the way");
        }

        /// <summary>The corridor's figures: peak g on the capsule at each end of it and at target_pe, for the docs (#817).</summary>
        [Theory]
        [InlineData(25000)]
        [InlineData(30000)]
        [InlineData(35000)]
        public void The_corridor_entries_pull_under_the_g_limit(double pe)
        {
            var snapshot = AimedCorrectionTests.Falling(pe);
            var kerbin = Kerbin(snapshot);
            string problem;
            var frame = BodyFrame.Read(kerbin, new AimedCorrectionTests.FrameBody(snapshot.Ut), snapshot.Ut, out problem);
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(kerbin, OrbitOf(snapshot), snapshot.Ut, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            _out.WriteLine("Pe {0} m: peak {1:0.00} g, landing {2:0.0} m/s", pe, landed.PeakG, landed.LandingSpeed);
            Assert.InRange(landed.PeakG, 0, CorrectionBurn.MostEntryG);
        }
    }
}
