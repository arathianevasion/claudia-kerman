// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #532: circularize's burn, as <see cref="AscentSim"/> flies it after launch, also stops once the eccentricity stops falling.
    /// A steep launch leaves a long circularization (the burn longer than the coast to apoapsis, the periapsis far below the
    /// surface, as Mission 9f50's was); it still ends near circular, at the apoapsis launch left.
    /// </summary>
    public sealed class CircularizeLongBurnTests
    {
        private readonly ITestOutputHelper _out;

        public CircularizeLongBurnTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Kerbin's air: exponential, scale height 5600 m, none above 70 km.</summary>
        private sealed class ExpAir : IAscentAir
        {
            public void Air(double altitude, out double atm, out double density, out double speedOfSound)
            {
                atm = altitude < 70000 ? Math.Exp(-altitude / 5600) : 0;
                density = 1.225 * atm;
                speedOfSound = 340 - 0.004 * Math.Min(altitude, 12000);
            }
        }

        private static AscentStage Stage(int number, double vacKn, double aslKn, double isp, double startT, double endT)
        {
            return new AscentStage
            {
                Number = number, ThrustVac = vacKn * 1000, ThrustSeaLevel = aslKn * 1000, Flow = vacKn * 1000 / (isp * AscentSim.G0),
                StartMass = startT * 1000, EndMass = endT * 1000, IspVac = isp, DeltaVVac = isp * AscentSim.G0 * Math.Log(startT / endT),
            };
        }

        /// <summary>
        /// A strong pad stage, then an upper stage at about 1.7 g in vacuum, to an 80 km apoapsis. A turn shape of 2 starting at
        /// 200 m/s climbs steeply: launch leaves a 2,080 m/s circularization of about 87 s, 50 s from apoapsis, periapsis
        /// -597 km. The default shape leaves 1,310 m/s, about 60 s.
        /// </summary>
        private AscentFlight Fly(double turnShape, double turnStartSpeed, bool eccStop)
        {
            var options = new AscentOptions
            {
                Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, Spin = 174.94, StartAltitude = 70,
                TargetAp = 80000, TurnStartSpeed = turnStartSpeed, TurnShape = turnShape, EccStop = eccStop,
            };
            var flight = AscentSim.Fly(options, new ExpAir(), AscentSim.Table(new List<double[]> { new[] { 0.0, 1.0 }, new[] { 1.1, 2.0 }, new[] { 3.0, 1.0 } }),
                new List<AscentStage> { Stage(1, 400, 340, 290, 20, 9), Stage(0, 120, 15, 345, 7, 2) });
            _out.WriteLine("shape " + turnShape + ", ecc stop " + eccStop + ": " + (flight.ReachesOrbit ? "orbit" : flight.Failure) + ", coast_ap " + Math.Round(flight.CoastAp)
                + ", ap " + Math.Round(flight.OrbitAp) + ", pe " + Math.Round(flight.OrbitPe) + ", ecc " + Math.Round(flight.OrbitEcc, 5) + ", stop " + flight.CircularizeStop
                + ", dv_to_orbit " + Math.Round(flight.DeltaVToOrbit));
            return flight;
        }

        [Theory]
        [InlineData(2, 200)]
        [InlineData(0.45, 60)]
        public void A_long_or_short_circularization_ends_near_circular_at_the_apoapsis_launch_left(double turnShape, double turnStartSpeed)
        {
            var flight = Fly(turnShape, turnStartSpeed, true);
            Assert.True(flight.ReachesOrbit, flight.Failure);
            Assert.True(flight.OrbitEcc < 0.01, "ecc " + flight.OrbitEcc);
            Assert.InRange(flight.OrbitAp, flight.CoastAp - 3000, flight.CoastAp + 3000);
            Assert.True(flight.OrbitPe > 70000, "pe " + flight.OrbitPe);
        }

        [Theory]
        [InlineData(2, 200)]
        [InlineData(0.45, 60)]
        public void The_eccentricity_stop_leaves_an_orbit_no_less_circular_than_the_nodes_own_stops(double turnShape, double turnStartSpeed)
        {
            var withStop = Fly(turnShape, turnStartSpeed, true);
            var nodeOnly = Fly(turnShape, turnStartSpeed, false);
            Assert.Equal("node", nodeOnly.CircularizeStop);
            Assert.True(withStop.OrbitEcc <= nodeOnly.OrbitEcc + PastMin.EccBand, withStop.OrbitEcc + " against " + nodeOnly.OrbitEcc);
        }
    }
}
