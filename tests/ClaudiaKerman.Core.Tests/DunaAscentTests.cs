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
    /// #978: the Duna ascent of m20261110-203402-45d1 (Duna round trip, 2026-11-10). The craft lifts off at 2,941 m with thrust-to-weight 3.5
    /// (180 kN, Isp 340, 17.7 t), stages as <c>plan_ascent</c> answered there (stage 5 804 m/s, 4 1,287 m/s, 3 1,837 m/s), into Duna's air from
    /// that flight's Game Snapshot. Before #978 it climbed within a few degrees of the prograde all the way (the thin-air lead held its kick to 3°,
    /// max_aoa the rest), cut off 73° above the horizon at 16 km, and plan_ascent's sim needed 1,463 m/s to a 60 km orbit with only 31 m/s
    /// between turn shapes 0.2 and 0.8.
    /// </summary>
    public sealed class DunaAscentTests
    {
        private const double Isp = 340;
        private const double ThrustKn = 180;
        private const double Gm = 301363211975.0977;
        private const double Radius = 320000;
        private readonly ITestOutputHelper _out;

        public DunaAscentTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Duna's air from the flight's Game Snapshot: altitude m, atmospheres, kg/m³, speed of sound m/s.</summary>
        private static readonly double[][] DunaAirRows =
        {
            new[] { 0, 0.0666667, 0.133339, 246.56 }, new[] { 1000, 0.0599658, 0.120029, 246.47 }, new[] { 2000, 0.0536775, 0.107853, 246 },
            new[] { 3000, 0.0477976, 0.0967082, 245.14 }, new[] { 4000, 0.0423222, 0.0864787, 243.94 }, new[] { 5000, 0.0372472, 0.0770738, 242.41 },
            new[] { 6000, 0.0325685, 0.0684223, 240.57 }, new[] { 7000, 0.0282821, 0.0604704, 238.47 }, new[] { 8000, 0.024384, 0.0531792, 236.12 },
            new[] { 9000, 0.02087, 0.0465234, 233.55 }, new[] { 10000, 0.0177363, 0.0404906, 230.78 }, new[] { 11000, 0.0149787, 0.0350803, 227.85 },
            new[] { 12000, 0.0125931, 0.0303036, 224.79 }, new[] { 13000, 0.0105148, 0.0260321, 221.61 }, new[] { 14000, 0.00867734, 0.0221269, 218.36 },
            new[] { 15000, 0.00707489, 0.0185969, 215.07 }, new[] { 16000, 0.00570146, 0.0154568, 211.78 }, new[] { 17000, 0.00455107, 0.0127274, 208.51 },
            new[] { 18000, 0.00361775, 0.0104343, 205.32 }, new[] { 19000, 0.00289555, 0.00860748, 202.24 }, new[] { 20000, 0.00237849, 0.00727899, 199.33 },
            new[] { 21000, 0.00198898, 0.00625601, 196.61 }, new[] { 22000, 0.00165081, 0.00532451, 194.16 }, new[] { 23000, 0.00136008, 0.00448546, 192.01 },
            new[] { 24000, 0.00111287, 0.00373949, 190.22 }, new[] { 25000, 0.000905269, 0.00308641, 188.85 }, new[] { 26000, 0.000733377, 0.00252644, 187.87 },
            new[] { 27000, 0.00059328, 0.00205826, 187.21 }, new[] { 28000, 0.000481069, 0.00167612, 186.81 }, new[] { 29000, 0.000392836, 0.00137159, 186.61 },
            new[] { 30000, 0.00032467, 0.00113423, 186.56 }, new[] { 32000, 0.000232906, 0.000813655, 186.56 }, new[] { 34000, 0.000174503, 0.000609624, 186.56 },
            new[] { 36000, 0.000122191, 0.000426874, 186.56 }, new[] { 38000, 7.98304e-05, 0.000278887, 186.56 }, new[] { 40000, 4.85331e-05, 0.00016955, 186.56 },
            new[] { 42000, 2.66421e-05, 9.30739e-05, 186.56 }, new[] { 44000, 1.25005e-05, 4.36704e-05, 186.56 }, new[] { 46000, 4.45103e-06, 1.54779e-05, 186.99 },
            new[] { 48000, 8.36577e-07, 2.48012e-06, 202.52 }, new[] { 49000, 1.74656e-07, 4.94183e-07, 207.3 }, new[] { 50000, 0, 0, 0.0 },
        };

        private static SnapshotBody Duna()
        {
            var body = new SnapshotBody { Name = "Duna", Gm = Gm, Radius = Radius, AtmosphereHeight = 50000, SeaLevelAtm = 0.0666666677961107, RotationPeriod = 65517.859375 };
            body.Air.AddRange(DunaAirRows);
            return body;
        }

        /// <summary>
        /// A stage of the 180 kN Isp 340 engine from its start mass (t) and plan_ascent's vacuum delta-v; at Duna's sea level its thrust is 1% down
        /// (the engine loses 15% of its thrust at 1 atm, from the stock simulator's figures on Kerbin's pad).
        /// </summary>
        private static AscentStage Stage(int number, double startT, double dv)
        {
            var endT = startT / Math.Exp(dv / (Isp * AscentSim.G0));
            return new AscentStage
            {
                Number = number, ThrustVac = ThrustKn * 1000, ThrustSeaLevel = ThrustKn * 1000 * 0.99, Flow = ThrustKn * 1000 / (Isp * AscentSim.G0),
                StartMass = startT * 1000, EndMass = endT * 1000, IspVac = Isp, DeltaVVac = dv, BurnTime = (startT - endT) * 1000 / (ThrustKn * 1000 / (Isp * AscentSim.G0)),
            };
        }

        /// <summary>The stages plan_ascent answered at Duna: 17.7 t lighting at 3.5 g; stage 4's start from its thrust-to-weight 6.88 at 59,317 m.</summary>
        private static List<AscentStage> Stages()
        {
            return new List<AscentStage> { Stage(5, 17.7, 804), Stage(4, 12.491, 1287), Stage(3, 7.08, 1837) };
        }

        /// <summary>A 1.25 m stack's nose-first drag area, m² (the flight's own table was not recorded with the snapshot).</summary>
        private static Func<double, double> Drag()
        {
            return AscentSim.Table(new List<double[]> { new[] { 0.0, 0.8 }, new[] { 0.85, 0.8 }, new[] { 1.1, 1.6 }, new[] { 1.5, 1.1 }, new[] { 3.0, 0.8 } });
        }

        private AscentFlight Fly(double shape)
        {
            var options = new AscentOptions
            {
                Gm = Gm, Radius = Radius, AtmosphereHeight = 50000, SeaLevelAtm = 0.0666666677961107, Spin = 2 * Math.PI * Radius / 65517.859375,
                StartAltitude = 2941, TargetAp = 60000, TurnShape = shape, Track = true,
            };
            var flight = AscentSim.Fly(options, new BodyAir(Duna()), Drag(), Stages());
            var cut = Cutoff(flight);
            _out.WriteLine("shape " + shape + ": " + (flight.ReachesOrbit ? "orbit" : flight.Failure) + ", dv to orbit " + Math.Round(flight.DeltaVToOrbit)
                + ", left " + Math.Round(flight.DeltaVLeft) + ", coast_ap " + Math.Round(flight.CoastAp)
                + (cut == null ? "" : ", last burning row " + Math.Round(cut[0]) + " m, nose " + Math.Round(90 - cut[1], 1) + " deg above the horizon, prograde " + Math.Round(90 - cut[2], 1)
                    + ", h " + Math.Round(cut[9]) + " v " + Math.Round(cut[10]) + " m/s"));
            return flight;
        }

        /// <summary>The last Track row of the climb before the first cutoff (the rows are every 1000 m while burning).</summary>
        private static double[] Cutoff(AscentFlight flight)
        {
            return flight.Track.LastOrDefault(r => r[4] > 0);
        }

        /// <summary>The shape plan_ascent picks, as its search does: the most delta-v left among its shapes that reach orbit.</summary>
        private AscentFlight Best()
        {
            return PlanAscent.Shapes().Select(s => new { Shape = s, Flight = Fly(s) }).Where(t => t.Flight.ReachesOrbit)
                .OrderByDescending(t => Math.Round(t.Flight.DeltaVLeft)).ThenByDescending(t => t.Shape).First().Flight;
        }

        // Before #978 this fixture's best was 1,386 m/s (the flight's own plan_ascent said 1,463, with a drag table the flight didn't record);
        // now 1,263 at turn_shape 0.15 (1,176 at 0.10 with the turn start's kick unlimited, more than kOS steering follows: LaunchTurn.ThickLead).
        [Fact]
        public void The_shape_plan_ascent_picks_reaches_a_60_km_orbit_for_clearly_less_than_1463_m_s()
        {
            var best = Best();
            Assert.True(best.DeltaVToOrbit < 1300, "dv to orbit " + best.DeltaVToOrbit);
        }

        // Before #978 the shapes 0.20 to 0.80 left 2,507 to 2,539 m/s; now 0.05 to 0.80 leave 2,507 to 2,663.
        [Fact]
        public void The_turn_shapes_plan_ascent_tries_differ_by_well_over_31_m_s()
        {
            var left = PlanAscent.Shapes().Select(Fly).Where(f => f.ReachesOrbit).Select(f => f.DeltaVLeft).ToList();
            Assert.True(left.Max() - left.Min() > 120, "spread " + (left.Max() - left.Min()));
        }

        // The flight cut off 73 degrees above the horizon at 16 km (this sim before #978: 68 at 20 km). Now the nose is 43 degrees above it at the
        // burn's last 1000 m row, at 21 km: the turn start's kick is held to what kOS steering follows (LaunchTurn.ThickLead), and above 500 Pa
        // max_aoa keeps the climb within 5 degrees of the prograde, so the brief's "well under 20" isn't reached by the turn start alone.
        [Fact]
        public void The_launch_phase_cuts_off_well_below_the_old_law()
        {
            var cut = Cutoff(Best());
            Assert.True(90 - cut[1] < 50, "nose " + (90 - cut[1]) + " degrees above the horizon at " + cut[0] + " m");
            Assert.True(90 - cut[2] < 55, "prograde " + (90 - cut[2]) + " degrees above the horizon at " + cut[0] + " m");
        }

        // #978 review: 1 km above the pad the path is still 70 degrees above the horizon (an unlimited kick had it near 30 at shape 0.05).
        [Fact]
        public void A_kilometre_above_the_pad_the_climb_is_still_steep()
        {
            var row = Fly(0.05).Track.First(r => r[0] >= 2941 + 1000);
            Assert.True(90 - row[2] > 60, "prograde " + (90 - row[2]) + " degrees above the horizon at " + row[0] + " m");
        }
    }
}
