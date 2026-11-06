// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tests;
using ClaudiaKerman.Core.Tools;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// ADR 0014 parity for #793: return.ks's aimed correction (the real claudia_descend and return_aim_cost, on kOS.Safe) against Core's CorrectionBurn on the
    /// same arrivals, both flown through the same patches and predictor. Where the descent leaves the periapsis within 1 km, their landings are within 2 km.
    /// Deleted with the Kerboscript in the ticket.
    /// </summary>
    public sealed class AimedCorrectionParityTests
    {
        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 };
        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
            RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
        };
        private const double Ut = 100000;
        private const double TargetPe = 30000;
        private const double BurnUt = Ut + 300;

        private readonly ITestOutputHelper _out;

        public AimedCorrectionParityTests(ITestOutputHelper output)
        {
            _out = output;
            KosHarness.Register(typeof(ParityFunctions));
        }

        private sealed class FrameBody : PlanningBodyBase
        {
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(Math.Atan2(y, x) * 180 / Math.PI - 360 / 21549.425 * Ut);
                return true;
            }
        }

        private static GameSnapshot Falling(double pe, double toPeriapsis)
        {
            const double apR = 12600000;
            var peR = Kerbin.Radius + pe;
            var sma = (apR + peR) / 2;
            var snapshot = new GameSnapshot { Ut = Ut, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = sma, Eccentricity = (apR - peR) / (apR + peR), Body = "Kerbin", Periapsis = pe, Apoapsis = apR - Kerbin.Radius,
                MeanAnomalyAtEpoch = 2 * Math.PI - toPeriapsis * Math.Sqrt(Kerbin.Gm / (sma * sma * sma)), Epoch = Ut,
            };
            snapshot.Bodies.AddRange(new[] { Sun, Kerbin });
            return snapshot;
        }

        /// <summary>What the rig's aim asks: the arrival, and the target.</summary>
        internal static GameSnapshot Now;
        internal static double TargetLat, TargetLng;

        internal static ReentryPrediction Fly(double radial, double normal, double prograde, out double pe)
        {
            var o = Now.Orbit;
            var orbit = Conic.FromElements(Kerbin.Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
            Vec3 r, v;
            orbit.State(BurnUt, out r, out v);
            var after = Conic.FromState(Kerbin.Gm, r, v + SingleShooting.FromNodeFrame(orbit, BurnUt, prograde, normal, radial), BurnUt);
            pe = after.PeR - Kerbin.Radius;
            string problem;
            var frame = BodyFrame.Read(Kerbin, new FrameBody(), Ut, out problem);
            var craft = new ReentrySim.Capsule { Lift = 0 };
            return ReentryPredictor.Predict(Kerbin, after, BurnUt, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, frame);
        }

        /// <summary>The block's REENTRYIMPACT as its aim reads it: km from the target, -1 with no landing.</summary>
        internal static double AimKm(double radial, double normal, double prograde)
        {
            double pe;
            var landing = Fly(radial, normal, prograde, out pe);
            return landing.Error != null ? -1 : LandingSiteSearch.ArcDegrees(landing.Latitude, landing.Longitude, TargetLat, TargetLng) * Math.PI / 180 * 600;
        }

        /// <summary>
        /// Arrivals a few to tens of km off, from far out and from nearer in. Beyond a few tens of km the descent leaves the periapsis more than 1 km off, which is
        /// the fault #793 mends, and parity is not asked there.
        /// </summary>
        [Theory]
        [InlineData(30000, 30000, 3)]
        [InlineData(30000, 30000, -3)]
        [InlineData(30000, 30000, 8)]
        [InlineData(30000, 30000, -8)]
        [InlineData(30000, 30000, 15)]
        [InlineData(30000, 30000, -15)]
        [InlineData(30000, 30000, 30)]
        [InlineData(30000, 15000, 5)]
        [InlineData(30000, 15000, -12)]
        [InlineData(30500, 30000, 6)]
        [InlineData(29500, 30000, -6)]
        public void The_descent_and_the_solve_land_within_2_km_where_the_periapsis_stays_within_1_km(double pe, double toPeriapsis, double eastKm)
        {
            Now = Falling(pe, toPeriapsis);
            double unused;
            var natural = Fly(0, 0, 0, out unused);
            Assert.Null(natural.Error);
            TargetLat = natural.Latitude;
            TargetLng = natural.Longitude + eastKm / (600 * Math.PI / 180);

            // return_aimed_correction's descent, with nd:orbit as the rig's flown orbit and aim as its predictor call.
            var script = "local found is claudia_descend({ parameter x.\n"
                + "  return return_aim_cost({ parameter o, u. return test_aim_km(x[0], x[1], x[2]). }, lexicon(\"orbit\", lexicon(\"periapsis\", 0), \"soi_ut\", 0), " + TargetPe.ToString(CultureInfo.InvariantCulture) + ").\n"
                + "}, list(0, 0, 0), list(1, 1, 1), 10, return_aim_good_km()).\n"
                + "print found:x[0]. print found:x[1]. print found:x[2].";
            var printed = KosHarness.Run(CoreLibrary.Bundle(script)).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            double descentPe;
            var descent = Fly(printed[0], printed[1], printed[2], out descentPe);
            Assert.Null(descent.Error);

            var node = BlockCalls.CorrectionBurn(new Dictionary<string, object>
            {
                { "target", "Kerbin" }, { "target_pe", TargetPe }, { "earliest_ut", BurnUt }, { "latest_ut", BurnUt }, { "lat", TargetLat }, { "lng", TargetLng },
            }, Now, new FrameBody(), new ReentrySim.Atmosphere(), new ReentrySim.Capsule());
            Assert.Equal("", node["error"]);
            double solvePe;
            var solve = Fly((double)node["radial"], (double)node["normal"], (double)node["prograde"], out solvePe);
            Assert.Null(solve.Error);

            var apart = LandingSiteSearch.ArcDegrees(descent.Latitude, descent.Longitude, solve.Latitude, solve.Longitude) * Math.PI / 180 * 600;
            _out.WriteLine("descent pe " + descentPe + ", " + Math.Sqrt(printed.Sum(x => x * x)) + " m/s; solve pe " + solvePe + ", " + node["dv"] + " m/s; landings " + apart + " km apart");
            Assert.InRange(solvePe, TargetPe - 1000, TargetPe + 1000);
            if (Math.Abs(descentPe - TargetPe) <= 1000) Assert.InRange(apart, 0, 2);
            else _out.WriteLine("the descent left the periapsis over 1 km off: no parity asked");
        }
    }

    internal static class ParityFunctions
    {
        [Function("test_aim_km")]
        public class FunctionAimKm : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var prograde = GetDouble(PopValueAssert(shared));
                var normal = GetDouble(PopValueAssert(shared));
                var radial = GetDouble(PopValueAssert(shared));
                AssertArgBottomAndConsume(shared);
                ReturnValue = ScalarValue.Create(AimedCorrectionParityTests.AimKm(radial, normal, prograde));
            }
        }
    }
}
