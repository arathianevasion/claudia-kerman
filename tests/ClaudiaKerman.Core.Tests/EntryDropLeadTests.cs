// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// When <c>reentry</c> drops the stack (#815): <see cref="EntryStack.DropLeadS"/> before the atmosphere. The drop kicks the capsule by about
    /// 0.1 m/s in no aimed direction (m20261106-132839-970f's kick, read from its periapsis change); these fly the capsule model with that kick
    /// along each axis at 600, 1,200 and 1,800 s before the air, on a Mun return to Kerbin and a Duna low-orbit descent, and measure how far the
    /// landing moves and how far the capsule is from where the stack would be, unkicked, at the air (the stack takes its own share of the kick the
    /// other way, so the real gap is somewhat more). The figures are printed for the ticket.
    /// </summary>
    public sealed class EntryDropLeadTests
    {
        private const double Kick = 0.1;
        private readonly ITestOutputHelper _out;

        public EntryDropLeadTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private sealed class Case
        {
            public SnapshotBody Body;
            public Conic Orbit;
            public IReentryAir Air;
            public BodyFrame Frame;
        }

        /// <summary>Home from the Mun: equatorial, from the Mun's distance (11,400 km up) to a 29 km periapsis, the flight's.</summary>
        private static Case MunReturn()
        {
            const double ra = ReentrySim.R + 11400000, rp = ReentrySim.R + 28917;
            var va = Math.Sqrt(ReentrySim.Mu * (2 / ra - 2 / (ra + rp)));
            return new Case
            {
                Body = ReentrySim.Kerbin(), Orbit = Conic.FromState(ReentrySim.Mu, new Vec3(ra, 0, 0), new Vec3(0, va, 0), 0),
                Air = new ReentrySim.Atmosphere(), Frame = ReentrySim.Frame(),
            };
        }

        private const double DunaMu = 3.0136321e11, DunaR = 320000, DunaTop = 50000, DunaDay = 65517.859;

        /// <summary>Duna's air, roughly: CO2 at 200 K, 0.0667 atm at sea level falling by e every 5.7 km, none above 50 km.</summary>
        private sealed class DunaAir : IReentryAir
        {
            public void Air(double altitude, double latitude, out double density, out double speedOfSound)
            {
                speedOfSound = Math.Sqrt(1.3 * 8.314462 * 200 / 0.04401);
                density = altitude >= DunaTop ? 0 : PressureAtm(altitude) * 101325 * 0.04401 / (8.314462 * 200);
            }

            public double PressureAtm(double altitude)
            {
                return altitude >= DunaTop ? 0 : 0.0667 * Math.Exp(-Math.Max(0, altitude) / 5700);
            }
        }

        /// <summary>A Duna descent from low orbit: a 60 km apoapsis down to a 15 km periapsis, as deorbit aims.</summary>
        private static Case DunaDescent()
        {
            const double ra = DunaR + 60000, rp = DunaR + 15000;
            var va = Math.Sqrt(DunaMu * (2 / ra - 2 / (ra + rp)));
            return new Case
            {
                Body = new SnapshotBody { Name = "Duna", Parent = "Sun", Gm = DunaMu, Radius = DunaR, AtmosphereHeight = DunaTop, SeaLevelAtm = 0.0667, RotationPeriod = DunaDay },
                Orbit = Conic.FromState(DunaMu, new Vec3(ra, 0, 0), new Vec3(0, va, 0), 0),
                Air = new DunaAir(), Frame = new BodyFrame { LonOffset = 0, Handed = 1, Ut = 0, RotationPeriod = DunaDay },
            };
        }

        private static ReentryPrediction Land(Case c, Conic orbit, double fromUt)
        {
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(c.Body, orbit, fromUt, 0, craft.MassKg, c.Air, craft, c.Frame, false);
            Assert.Null(landed.Error);
            return landed;
        }

        private static double GroundKm(Case c, ReentryPrediction a, ReentryPrediction b)
        {
            Func<double, double> rad = d => d * Math.PI / 180;
            var cos = Math.Sin(rad(a.Latitude)) * Math.Sin(rad(b.Latitude)) + Math.Cos(rad(a.Latitude)) * Math.Cos(rad(b.Latitude)) * Math.Cos(rad(a.Longitude - b.Longitude));
            return Math.Acos(Math.Max(-1, Math.Min(1, cos))) * c.Body.Radius / 1000;
        }

        /// <summary>The most the landing moves (km), and the most gap at the air to the unkicked path (m), over a 0.1 m/s kick along each axis lead s before the air.</summary>
        private static void Measure(Case c, double lead, out double landingKm, out double gapM)
        {
            var top = c.Body.Radius + c.Body.AtmosphereHeight;
            var entry = c.Orbit.TimeAtRadius(top, 0, false).Value;
            var at = entry - lead;
            Vec3 r, v;
            c.Orbit.State(at, out r, out v);
            var prograde = v.Normalized;
            var normal = Vec3.Cross(r, v).Normalized;
            var radial = Vec3.Cross(prograde, normal);
            var straight = Land(c, c.Orbit, at);
            landingKm = 0;
            gapM = 0;
            foreach (var axis in new[] { prograde, prograde * -1, radial, radial * -1, normal, normal * -1 })
            {
                var kicked = Conic.FromState(c.Body.Gm, r, v + axis * Kick, at);
                landingKm = Math.Max(landingKm, GroundKm(c, straight, Land(c, kicked, at)));
                var air = kicked.TimeAtRadius(top, at, false).Value;
                gapM = Math.Max(gapM, (kicked.Position(air) - c.Orbit.Position(air)).Length);
            }
        }

        [Fact]
        public void A_tenth_of_a_metre_a_second_at_the_drop_point_moves_a_Mun_returns_landing_under_5_km()
        {
            double km, gap;
            Measure(MunReturn(), EntryStack.DropLeadS, out km, out gap);
            Assert.True(km < 5, "a 0.1 m/s kick " + EntryStack.DropLeadS + " s before the air moves the landing " + km.ToString("0.00", CultureInfo.InvariantCulture) + " km");
        }

        /// <summary>
        /// Measured: Mun return 0.61 / 1.08 / 1.48 km and 84 / 208 / 351 m at 600 / 1,200 / 1,800 s; Duna descent 1.76 / 0.41 / 0.04 km and
        /// 83 / 377 / 715 m. On the Mun return the kick's effect grows with the time out; on a low Duna orbit (a period of about 2,450 s) it goes
        /// round with the orbit, so a longer lead isn't simply worse, and 600 s is under the 5 km test on both.
        /// </summary>
        [Fact]
        public void The_kick_at_600_s_is_under_5_km_on_both_and_on_a_Mun_return_grows_with_the_time_out_and_the_stack_is_clear_at_the_air()
        {
            foreach (var named in new[] { Tuple.Create("Mun return", MunReturn()), Tuple.Create("Duna descent", DunaDescent()) })
            {
                var moved = new List<double>();
                foreach (var lead in new[] { 600.0, 1200, 1800 })
                {
                    double km, gap;
                    Measure(named.Item2, lead, out km, out gap);
                    _out.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}, kick {1} s before the air: landing moves {2:0.00} km at most, capsule {3:0} m from the unkicked path at the air",
                        named.Item1, lead, km, gap));
                    moved.Add(km);
                    // Clear of the stack but not far: a tenth of a metre a second for the time out, give or take what the orbit makes of it.
                    Assert.InRange(gap, Kick * lead * 0.3, Kick * lead * 10);
                }
                Assert.True(moved[0] < 5, named.Item1 + " at 600 s: " + moved[0].ToString("0.00", CultureInfo.InvariantCulture) + " km");
                if (named.Item1 == "Mun return")
                    Assert.True(moved[0] < moved[1] && moved[1] < moved[2], string.Join(", ", moved.Select(m => m.ToString("0.00", CultureInfo.InvariantCulture))));
            }
        }
    }
}
