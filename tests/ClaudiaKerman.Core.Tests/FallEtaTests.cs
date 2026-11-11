// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:FALLETA</c> (#994, ADR 0014): the seconds until a closed orbit or an inbound hyperbolic path falls to a radius, which <c>reentry</c> waits on.
    /// Pinned to the library's <c>claudia_fall_eta</c>: seven closed-orbit answers and one Mun descent printed on kOS.Safe before it was deleted,
    /// and the edges (periapsis at the air's edge, either side of apsides, ecc 0, an 80 Mm apoapsis) worked from its formula in doubles.
    /// The hyperbolic answers are checked against an independent time of flight, not pinned.
    /// </summary>
    public sealed class FallEtaTests
    {
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000;
        private const double KerbinAir = KerbinRadius + 70000;
        private const double KerbinSoi = 84159286;
        private const double MunMu = 6.5138398e10;

        private static double Eta(double ecc, double sma, double mu, double rFall, double ta)
        {
            return BlockCalls.FallEta(new Dictionary<string, object> { { "ecc", ecc }, { "sma", sma }, { "mu", mu }, { "r_fall", rFall }, { "ta", ta } });
        }

        // ---- The Duna return of flight m20261111-172716-a40a: ESCAPING, sma -9,670,147 m, periapsis 29,209 m ----

        private const double FlightSma = -9670147;
        private static readonly double FlightEcc = 1 - (KerbinRadius + 29209) / FlightSma;

        /// <summary>The true anomaly, degrees, at radius <paramref name="r"/> on the flight's path: negative on the way in.</summary>
        private static double FlightTa(double r)
        {
            var p = FlightSma * (1 - FlightEcc * FlightEcc);
            return -Math.Acos((p / r - 1) / FlightEcc) * 180 / Math.PI;
        }

        /// <summary>An independent check: the time from true anomaly <paramref name="fromDeg"/> to <paramref name="toDeg"/> by the time of flight on a hyperbola, from the universal-variable form.</summary>
        private static double HyperbolicFlight(double ecc, double sma, double mu, double fromDeg, double toDeg)
        {
            Func<double, double> f = deg =>
            {
                var nu = deg * Math.PI / 180;
                var cosh = (ecc + Math.Cos(nu)) / (1 + ecc * Math.Cos(nu));
                var h = Math.Log(cosh + Math.Sqrt(cosh * cosh - 1)) * Math.Sign(nu);
                return ecc * Math.Sinh(h) - h;
            };
            return (f(toDeg) - f(fromDeg)) / Math.Sqrt(mu / Math.Pow(-sma, 3));
        }

        [Fact]
        public void The_flights_duna_return_reaches_the_air_from_the_edge_of_kerbins_sphere()
        {
            var ta = FlightTa(KerbinSoi * 0.99);
            var eta = Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, ta);
            Assert.True(eta > 0 && !double.IsInfinity(eta) && !double.IsNaN(eta), "eta " + eta);
            // About a day and a half in from the sphere's edge at that speed.
            Assert.Equal(HyperbolicFlight(FlightEcc, FlightSma, KerbinMu, ta, FlightTa(KerbinAir)), eta, 3);
            Assert.InRange(eta, 30000, 200000);
        }

        [Fact]
        public void An_inbound_hyperbolic_path_a_few_thousand_km_out_reaches_the_air_in_its_own_time()
        {
            var ta = FlightTa(3000000);
            var eta = Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, ta);
            Assert.Equal(HyperbolicFlight(FlightEcc, FlightSma, KerbinMu, ta, FlightTa(KerbinAir)), eta, 3);
            // Kerbin's true anomaly reads 0 to 360 in kOS: the same point given as 360 + ta is the same answer.
            Assert.Equal(eta, Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, 360 + ta), 6);
        }

        [Fact]
        public void A_hyperbolic_path_with_its_periapsis_above_the_air_never_falls_to_it()
        {
            var sma = -9670147.0;
            var ecc = 1 - (KerbinRadius + 80000) / sma;
            Assert.Equal(-1, Eta(ecc, sma, KerbinMu, KerbinAir, -60));
        }

        [Fact]
        public void An_outbound_hyperbolic_path_past_periapsis_never_falls_to_the_air()
        {
            var ta = -FlightTa(3000000);
            Assert.True(ta > 0);
            Assert.Equal(-1, Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, ta));
            Assert.Equal(-1, Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, ta + 360));
        }

        [Fact]
        public void A_hyperbolic_path_already_in_the_air_is_there_now()
        {
            Assert.Equal(0, Eta(FlightEcc, FlightSma, KerbinMu, KerbinAir, FlightTa(650000)));
        }

        [Fact]
        public void A_path_that_isnt_a_conic_never_falls()
        {
            Assert.Equal(-1, Eta(1.2, 700000, KerbinMu, KerbinAir, -30));
            Assert.Equal(-1, Eta(0.5, -700000, KerbinMu, KerbinAir, 180));
            Assert.Equal(-1, Eta(1, -700000, KerbinMu, KerbinAir, -30));
            Assert.Equal(-1, BlockCalls.FallEta(null));
        }

        // ---- Closed orbits: pinned to claudia_fall_eta ----

        private const double RPe = KerbinRadius + 30000;
        private const double RAp = KerbinRadius + 200000;
        private const double Sma = (RPe + RAp) / 2;
        private const double Ecc = (RAp - RPe) / (RAp + RPe);

        /// <summary>claudia_fall_eta's own answers, printed on kOS.Safe, for 30 km by <paramref name="apKm"/> km round Kerbin.</summary>
        [Theory]
        [InlineData(200, 180, 717.2855354851916)]
        [InlineData(200, 120, 1123.3811977702135)]
        [InlineData(200, 90, 1298.9480738749305)]
        [InlineData(200, 240, 311.1898732001698)]
        [InlineData(200, 10, 0)]
        [InlineData(1000, 120, 3023.946329980797)]
        [InlineData(12000, 180, 28340.06562853157)]
        public void A_closed_orbit_gives_claudia_fall_etas_answer(double apKm, double ta, double expected)
        {
            var rAp = KerbinRadius + apKm * 1000;
            Assert.Equal(expected, Eta((rAp - RPe) / (rAp + RPe), (RPe + rAp) / 2, KerbinMu, KerbinAir, ta), 6);
        }

        /// <summary>The edges, pinned to claudia_fall_eta's formula worked again in doubles (it reproduces every pin above to the last digit):
        /// a periapsis exactly at the air's edge, a hair either side of apoapsis and of periapsis, and an 80 Mm apoapsis near apoapsis.</summary>
        [Theory]
        [InlineData(70, 200, 180, 1053.4043830369312)]
        [InlineData(70, 200, 180.0000001, 1053.4043823408933)]
        [InlineData(70, 200, 359.9999999, 0)]
        [InlineData(30, 200, 180.0000001, 717.2855347772309)]
        [InlineData(30, 200, 179.9999999, 717.2855361931524)]
        [InlineData(30, 200, 359.9999999, 0)]
        [InlineData(30, 80000, 179.999, 432663.249041508)]
        [InlineData(30, 80000, 180, 432609.2896395711)]
        [InlineData(30, 80000, 180.001, 432555.3302376345)]
        public void A_closed_orbit_at_the_edges_gives_claudia_fall_etas_answer(double peKm, double apKm, double ta, double expected)
        {
            var rPe = KerbinRadius + peKm * 1000;
            var rAp = KerbinRadius + apKm * 1000;
            Assert.Equal(expected, Eta((rAp - rPe) / (rAp + rPe), (rPe + rAp) / 2, KerbinMu, KerbinAir, ta), 6);
        }

        /// <summary>A circular orbit (ecc 0), as claudia_fall_eta answered: inside the air it is there now; above it, never.</summary>
        [Fact]
        public void A_circular_orbit_gives_claudia_fall_etas_answer()
        {
            Assert.Equal(0, Eta(0, KerbinRadius + 50000, KerbinMu, KerbinAir, 180));
            Assert.Equal(0, Eta(0, KerbinRadius + 70000, KerbinMu, KerbinAir, 0));
            Assert.Equal(-1, Eta(0, KerbinRadius + 100000, KerbinMu, KerbinAir, 180));
        }

        /// <summary>Independent check: advance the mean anomaly by <paramref name="seconds"/>, solve Kepler's equation, read the radius.</summary>
        private static double RadiusAfter(double ecc, double sma, double trueAnomalyDegrees, double seconds)
        {
            var ta = trueAnomalyDegrees * Math.PI / 180;
            var ea = 2 * Math.Atan2(Math.Sqrt(1 - ecc) * Math.Sin(ta / 2), Math.Sqrt(1 + ecc) * Math.Cos(ta / 2));
            var m = ea - ecc * Math.Sin(ea) + seconds * Math.Sqrt(KerbinMu / Math.Pow(sma, 3));
            var e = m;
            for (var i = 0; i < 50; i++) e -= (e - ecc * Math.Sin(e) - m) / (1 - ecc * Math.Cos(e));
            return sma * (1 - ecc * Math.Cos(e));
        }

        [Theory]
        [InlineData(180)]
        [InlineData(120)]
        [InlineData(90)]
        [InlineData(250)]
        public void The_entry_eta_lands_on_the_top_of_the_atmosphere(double ta)
        {
            var eta = Eta(Ecc, Sma, KerbinMu, KerbinAir, ta);
            Assert.True(eta > 0, "eta " + eta);
            Assert.Equal(KerbinAir, RadiusAfter(Ecc, Sma, ta, eta), 0);
        }

        [Fact]
        public void A_closed_orbit_inside_the_air_is_there_now_and_one_that_stays_out_never_falls()
        {
            Assert.Equal(0, Eta(Ecc, Sma, KerbinMu, KerbinAir, 0));
            var outPe = KerbinRadius + 100000;
            var outSma = (outPe + RAp) / 2;
            var outEcc = (RAp - outPe) / (RAp + outPe);
            Assert.Equal(-1, Eta(outEcc, outSma, KerbinMu, KerbinAir, 180));
            Assert.Equal(-1, Eta(0, KerbinRadius + 100000, KerbinMu, KerbinAir, 180));
        }

        /// <summary>#214: land's descent orbit round the Mun, 230 km by 203 km: the ground at 201 km is never reached; 210 km comes before periapsis.</summary>
        [Fact]
        public void The_muns_descent_orbit_gives_claudia_fall_etas_answers()
        {
            Assert.Equal(-1, Eta(0.06235565819861432, 216500, MunMu, 201000, 180));
            Assert.Equal(839.8420252739318, Eta(0.06235565819861432, 216500, MunMu, 210000, 180), 6);
        }
    }
}
