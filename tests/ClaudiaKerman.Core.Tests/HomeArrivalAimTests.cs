// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #915: <c>transfer_to</c>'s Course Correction inside Kerbin's sphere of influence, on a trip home from another planet, aims at the landing target inside <c>pe_tolerance</c> and within the spare
    /// delta-v, through <see cref="BlockCalls.TransferStep"/>. Each answer's node is flown afresh on the rig and through the predictor, not read from the solve.
    /// </summary>
    public sealed class HomeArrivalAimTests
    {
        private const double KmPerDeg = 600 * Math.PI / 180;
        /// <summary>Time before periapsis, s: just inside Kerbin's sphere of influence, as the wait into it leaves the craft.</summary>
        private const double BeforePe = 30000;

        /// <summary>The ground turned by a fixed <paramref name="turned"/> degrees, which sets how far the natural landing is from KSC.</summary>
        private sealed class TurnedBody : PlanningBodyBase
        {
            private readonly double _turned;

            public TurnedBody(double turned)
            {
                _turned = turned;
            }

            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(Math.Atan2(y, x) * 180 / Math.PI - _turned);
                return true;
            }
        }

        private static TransferRig Arriving(double pe)
        {
            return TransferRig.Arriving("Kerbin", pe, 1200, 0, 0, 0, BeforePe);
        }

        private static ReentryPrediction Predict(TransferRig rig, Conic orbit, double from, double turned)
        {
            string problem;
            // The rig starts at UT 0, where TurnedBody's ground is as given; a frame read later would turn it again.
            var frame = BodyFrame.Read(rig.Body, new TurnedBody(turned), 0, out problem);
            Assert.Null(problem);
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(rig.Body, orbit, from, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
            Assert.Null(landed.Error);
            return landed;
        }

        /// <summary>The turn of the ground that puts <paramref name="rig"/>'s natural landing <paramref name="eastKm"/> km east of KSC.</summary>
        private static double TurnedFor(TransferRig rig, double eastKm)
        {
            var natural = Predict(rig, rig.Orbit, rig.Ut, 0);
            return natural.Longitude - ReturnAim.KscLongitude - eastKm / KmPerDeg;
        }

        private static double FromKsc(ReentryPrediction landed)
        {
            return LandingSiteSearch.ArcDegrees(landed.Latitude, landed.Longitude, ReturnAim.KscLatitude, ReturnAim.KscLongitude) * Math.PI / 180 * 600;
        }

        private static GameSnapshot ForKsc(TransferRig rig)
        {
            var snapshot = rig.Snapshot();
            snapshot.LandingTarget = new LandingTarget { Body = "Kerbin", Latitude = ReturnAim.KscLatitude, Longitude = ReturnAim.KscLongitude };
            return snapshot;
        }

        private static Dictionary<string, object> NearInput(double pe, double dvLeft, double tolerance = 1000)
        {
            return new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", pe }, { "correction", true }, { "pe_tolerance", tolerance }, { "start_ut", 0.0 }, { "max_duration", "auto" }, { "auto", true },
                { "turn_time", 60.0 }, { "phase", "near" }, { "dv_left", dvLeft }, { "arrival_ut", BeforePe },
            };
        }

        private static IDictionary<string, object> Aimed(TransferRig rig, GameSnapshot snapshot, Dictionary<string, object> input, double turned)
        {
            return BlockCalls.TransferStep(input, snapshot, new TurnedBody(turned), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
        }

        private static double D(IDictionary<string, object> step, string key)
        {
            return Convert.ToDouble(step[key]);
        }

        /// <summary>The step's node flown exactly on the rig: its m/s.</summary>
        private static double Fly(TransferRig rig, IDictionary<string, object> step)
        {
            return rig.Fly((string)step["kind"], D(step, "ut"), D(step, "prograde"), D(step, "normal"), D(step, "radial"));
        }

        [Theory]
        [InlineData(1000, 40)]
        [InlineData(1000, -40)]
        [InlineData(300, 40)]
        public void A_duna_to_kerbin_arrival_moves_the_predicted_landing_toward_ksc_inside_pe_tolerance_and_the_spare(double tolerance, double eastKm)
        {
            const double dvLeft = 1000;
            var rig = Arriving(30000);
            var turned = TurnedFor(rig, eastKm);
            var natural = FromKsc(Predict(rig, rig.Orbit, rig.Ut, turned));
            Assert.InRange(natural, Math.Abs(eastKm) - 5, Math.Abs(eastKm) + 5);

            var step = Aimed(rig, ForKsc(rig), NearInput(30000, dvLeft, tolerance), turned);

            Assert.Equal("", step["error"]);
            var log = (string)step["plan_log"];
            Assert.True((string)step["do"] == "burn", log);
            Assert.Equal("correction", step["kind"]);
            var spent = Fly(rig, step);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 30000 - tolerance, 30000 + tolerance);
            Assert.InRange(spent, 0, DeorbitBurn.SpareDv(dvLeft, 0, 0, null));
            var left = FromKsc(Predict(rig, rig.Orbit, rig.Ut, turned));
            Assert.True(left < natural / 4, left + " km of " + natural + ": " + log);
            Assert.Contains("km from the target", log);
        }

        /// <summary>With no landing target, or a Mission Goal naming another place, the step is what it was: target_pe only, and nothing logged.</summary>
        [Theory]
        [InlineData(31500)]
        [InlineData(30000)]
        public void With_no_target_the_correction_is_unchanged(double arrivalPe)
        {
            var rig = Arriving(arrivalPe);
            var without = BlockCalls.TransferStep(NearInput(30000, 1000), rig.Snapshot());
            var named = rig.Snapshot();
            named.MissionGoal = "Land in the ocean.";

            var step = Aimed(rig, named, NearInput(30000, 1000), 0);

            Assert.False(step.ContainsKey("plan_log"));
            SameStep(without, step);
        }

        /// <summary>Too little above the reserve: no spend on the landing, the target_pe-only answer, and why in plan_log alone.</summary>
        [Theory]
        [InlineData(31500)]
        [InlineData(30000)]
        public void With_too_little_spare_it_falls_back_to_target_pe_only_and_logs_why(double arrivalPe)
        {
            const double dvLeft = 10;
            var rig = Arriving(arrivalPe);
            var turned = TurnedFor(rig, 40);
            var plain = BlockCalls.TransferStep(NearInput(30000, dvLeft), rig.Snapshot());

            var step = Aimed(rig, ForKsc(rig), NearInput(30000, dvLeft), turned);

            Assert.Equal("", step["error"]);
            var log = (string)step["plan_log"];
            Assert.Contains("target_pe only", log);
            Assert.Contains("reserve", log);
            Assert.DoesNotContain("reserve", string.Join(" ", ((IDictionary<string, object>)step["report"]).Values));
            SameStep(plain, step);
        }

        /// <summary>The later legs' delta-v is held back too (#907's rule): 1,000 m/s left with 1,000 for the legs ahead spends nothing on the landing.</summary>
        [Fact]
        public void The_later_legs_are_held_back_from_the_spare()
        {
            var rig = Arriving(30000);
            var turned = TurnedFor(rig, 40);
            var snapshot = ForKsc(rig);
            snapshot.LaterLegsDv = 1000;

            var step = Aimed(rig, snapshot, NearInput(30000, 1000), turned);

            Assert.Contains("target_pe only", (string)step["plan_log"]);
            SameStep(BlockCalls.TransferStep(NearInput(30000, 1000), rig.Snapshot()), step);
        }

        /// <summary>A miss past what the spare reaches: it spends up to the spare and no more, and comes down nearer, inside pe_tolerance.</summary>
        [Fact]
        public void A_miss_past_the_spare_spends_up_to_the_spare_and_no_more()
        {
            const double dvLeft = 300;
            var rig = Arriving(30000);
            var turned = TurnedFor(rig, 1500);
            var natural = FromKsc(Predict(rig, rig.Orbit, rig.Ut, turned));
            var withChutes = DeorbitBurn.SpareDv(dvLeft, 0, 0, null);

            var step = Aimed(rig, ForKsc(rig), NearInput(30000, dvLeft), turned);

            var log = (string)step["plan_log"];
            Assert.True((string)step["do"] == "burn", log);
            var spent = Fly(rig, step);
            Assert.True(spent <= withChutes + 0.5, spent + " m/s: " + log);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 29000, 31000);
            Assert.True(FromKsc(Predict(rig, rig.Orbit, rig.Ut, turned)) < natural, log);
        }

        /// <summary>A target_pe under the corridor's floor leaves no corridor inside pe_tolerance: target_pe only, and why.</summary>
        [Fact]
        public void With_the_corridor_outside_pe_tolerance_it_falls_back_to_target_pe_only_and_logs_why()
        {
            var rig = Arriving(20000);
            var turned = TurnedFor(rig, 40);
            var plain = BlockCalls.TransferStep(NearInput(20000, 1000), rig.Snapshot());

            var step = Aimed(rig, ForKsc(rig), NearInput(20000, 1000), turned);

            Assert.Contains("target_pe only", (string)step["plan_log"]);
            Assert.Contains("corridor", (string)step["plan_log"]);
            SameStep(plain, step);
        }

        /// <summary>However tight pe_tolerance is, the burn flown leaves the periapsis inside it: aimed if the solve can hold that, target_pe only if not.</summary>
        [Fact]
        public void A_tight_pe_tolerance_is_never_left()
        {
            var rig = Arriving(31500);
            var turned = TurnedFor(rig, 200);

            var step = Aimed(rig, ForKsc(rig), NearInput(30000, 1000, 15), turned);

            Assert.Equal("", step["error"]);
            Assert.Equal("burn", step["do"]);
            Fly(rig, step);
            Assert.InRange(rig.Orbit.PeR - rig.Body.Radius, 30000 - 15, 30000 + 15);
        }

        /// <summary>On the way in, an arrival already within pe_tolerance still waits for the home planet's sphere of influence, where the aim is flown; with no landing it goes on to its end.</summary>
        [Fact]
        public void An_arrival_within_pe_tolerance_waits_for_the_home_sphere_to_aim()
        {
            var inbound = InboundFromTheSun();
            var turned = 0.0;
            var plainInput = NearInput(inbound.Pe, 1000);
            plainInput["phase"] = "fine";
            var plain = BlockCalls.TransferStep(plainInput, inbound.Rig.Snapshot());
            Assert.Equal("done", plain["do"]);

            var aimedInput = NearInput(inbound.Pe, 1000);
            aimedInput["phase"] = "fine";
            var step = Aimed(inbound.Rig, inbound.Rig.Snapshot(), aimedInput, turned);

            Assert.Equal("wait", step["do"]);
            Assert.Equal("near", ((IDictionary<string, object>)step["carry"])["phase"]);

            // Out here KSC is the default target, so a goal naming another place is what stops the aim.
            var named = inbound.Rig.Snapshot();
            named.MissionGoal = "Land in the ocean.";
            aimedInput = NearInput(inbound.Pe, 1000);
            aimedInput["phase"] = "fine";
            Assert.Equal("done", Aimed(inbound.Rig, named, aimedInput, turned)["do"]);
        }

        /// <summary>The finer trim is the last chance to come within pe_tolerance (#672), so it holds target_pe even with a landing to aim at.</summary>
        [Fact]
        public void The_finer_trim_holds_target_pe_only()
        {
            var rig = Arriving(31500);
            var input = NearInput(30000, 1000);
            input["phase"] = "fine";
            var plain = BlockCalls.TransferStep(input, rig.Snapshot());
            Assert.Equal("burn", plain["do"]);

            input = NearInput(30000, 1000);
            input["phase"] = "fine";
            var step = Aimed(rig, ForKsc(rig), input, TurnedFor(rig, 40));

            SameStep(plain, step);
            Assert.False(step.ContainsKey("plan_log"));
        }

        /// <summary>A craft round the star meeting Kerbin, with its periapsis there: the snapshot is outside Kerbin's sphere of influence, so the default landing (KSC) applies.</summary>
        private static (TransferRig Rig, double Pe) InboundFromTheSun()
        {
            // The arrival hyperbola at UT 0, just outside the sphere, put on the star's patch by the planet's own state.
            var rig = TransferRig.Arriving("Kerbin", 30000, 1200, 0, 0, 0, 80000);
            Vec3 r, v, planetR, planetV;
            rig.Orbit.State(0, out r, out v);
            Assert.True(r.Length > rig.Body.SoiRadius, "the premise: the rig starts outside the sphere");
            rig.System.OrbitOf(rig.Body).State(0, out planetR, out planetV);
            var sun = rig.System.Find("Sun");
            var star = TransferRig.Circular("Sun", 1, 0);
            star.Orbit = Conic.FromState(sun.Gm, planetR + r, planetV + v, 0);
            ToolOutcome error;
            var enc = TransferFlight.Encounter(PredictBurn.Setup.Prepare(star.Snapshot(), "Kerbin", out error));
            Assert.NotNull(enc);
            return (star, enc.Pe);
        }

        private static void SameStep(IDictionary<string, object> expected, IDictionary<string, object> actual)
        {
            Assert.Equal(expected["error"], actual["error"]);
            Assert.Equal(expected["do"], actual["do"]);
            Assert.Equal(expected["kind"], actual["kind"]);
            foreach (var key in new[] { "ut", "prograde", "normal", "radial" }) Assert.Equal(D(expected, key), D(actual, key), 6);
        }
    }
}
