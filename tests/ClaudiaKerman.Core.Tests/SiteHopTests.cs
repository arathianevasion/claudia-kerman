// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:SITEHOP</c> (#1310, ADR 0014): a Site Hop's arc, its reach check before liftoff and each tick of its burn.
    /// Mun figures: mu 6.5138398e10, radius 200 km, day 138984.48 s. The arc is checked against a two-body integration, not the code's own sums.
    /// </summary>
    public sealed class SiteHopTests
    {
        private const double MunMu = 6.5138398e10;
        private const double MunRadius = 200000;
        private const double MunDay = 138984.48;

        private static Dictionary<string, object> Landed(double tLat, double tLng, double day = MunDay, double dv = 2000, double accel = 5)
        {
            var east = day > 0 ? 2 * Math.PI * MunRadius / day : 0;
            return new Dictionary<string, object>
            {
                { "mu", MunMu }, { "day", day }, { "lat", 0.0 }, { "lng", 0.0 }, { "r", MunRadius }, { "agl", 2.0 },
                { "vel_up", 0.0 }, { "vel_north", 0.0 }, { "vel_east", east }, { "accel", accel },
                { "target_lat", tLat }, { "target_lng", tLng }, { "target_r", MunRadius }, { "dv", dv },
            };
        }

        private static double[] Unit(double lat, double lng)
        {
            var la = lat * Math.PI / 180;
            var lo = lng * Math.PI / 180;
            return new[] { Math.Cos(la) * Math.Cos(lo), Math.Cos(la) * Math.Sin(lo), Math.Sin(la) };
        }

        /// <summary>Flies the wanted velocity from (0, 0) at the Mun's radius by RK4 until it falls back to that radius; the landing (lat, lng) on the turned body.</summary>
        private static double[] Fly(IDictionary<string, object> plan, double day)
        {
            var v = (double)plan["speed"];
            var hdg = (double)plan["heading"] * Math.PI / 180;
            var pit = (double)plan["pitch"] * Math.PI / 180;
            // At (0, 0): up is x, east is y, north is z.
            double[] s = { MunRadius, 0, 0, v * Math.Sin(pit), v * Math.Cos(pit) * Math.Sin(hdg), v * Math.Cos(pit) * Math.Cos(hdg) };
            const double dt = 0.05;
            double t = 0;
            Func<double[], double[]> f = x =>
            {
                var r = Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
                var k = -MunMu / (r * r * r);
                return new[] { x[3], x[4], x[5], k * x[0], k * x[1], k * x[2] };
            };
            while (true)
            {
                var k1 = f(s);
                var k2 = f(Add(s, k1, dt / 2));
                var k3 = f(Add(s, k2, dt / 2));
                var k4 = f(Add(s, k3, dt));
                var n = new double[6];
                for (var i = 0; i < 6; i++) n[i] = s[i] + dt / 6 * (k1[i] + 2 * k2[i] + 2 * k3[i] + k4[i]);
                t += dt;
                s = n;
                if (t > 5 && Math.Sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]) <= MunRadius) break;
                if (t > 20000) throw new InvalidOperationException("never came down");
            }
            var rr = Math.Sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]);
            var lng = Math.Atan2(s[1], s[0]) * 180 / Math.PI - (day > 0 ? t * 360 / day : 0);
            return new[] { Math.Asin(s[2] / rr) * 180 / Math.PI, lng };
        }

        private static double[] Add(double[] a, double[] b, double h)
        {
            var o = new double[a.Length];
            for (var i = 0; i < a.Length; i++) o[i] = a[i] + h * b[i];
            return o;
        }

        private static double Metres(double[] a, double[] b)
        {
            var u = Unit(a[0], a[1]);
            var w = Unit(b[0], b[1]);
            return MunRadius * Math.Acos(Math.Min(1, u[0] * w[0] + u[1] * w[1] + u[2] * w[2]));
        }

        [Fact]
        public void A20KmHopEastOnAStillBodyIsTheMinimumEnergyArc()
        {
            // 0.1 rad of arc: the least speed is sqrt(mu/r * 2 sin(0.05) / (1 + sin(0.05))) = 176.1 m/s at 45 - 0.1 rad / 4 = 43.57 degrees.
            var plan = SiteHop.Call(Landed(0, 0.1 * 180 / Math.PI, day: 0));
            Assert.Equal("", plan["why"]);
            Assert.Equal(20.0, (double)plan["km"], 1);
            Assert.InRange((double)plan["speed"], 175, 177);
            Assert.InRange((double)plan["pitch"], 43.2, 43.9);
            Assert.InRange((double)plan["heading"], 89.9, 90.1);
        }

        [Theory]
        [InlineData(0.0, 6.0)]
        [InlineData(4.0, 0.0)]
        [InlineData(-3.0, -5.0)]
        public void TheArcComesDownOnTheTargetUnderTheTurningBody(double tLat, double tLng)
        {
            var plan = SiteHop.Call(Landed(tLat, tLng));
            Assert.Equal("", plan["why"]);
            Assert.InRange(Metres(Fly(plan, MunDay), new[] { tLat, tLng }), 0, 100);
        }

        [Fact]
        public void ANorthwardHopHeadsNorth()
        {
            var plan = SiteHop.Call(Landed(3, 0, day: 0));
            Assert.InRange((double)plan["heading"], 0, 0.1);
        }

        [Fact]
        public void TheNeedIsTheBurnWithItsMarginAndLandsDescentFromTheArc()
        {
            // 176.1 m/s to gain from rest, and 176.1 m/s at the ground again: 1.1 * 176.1 + 1.2 * 176.1 + 100 = 505.
            var plan = SiteHop.Call(Landed(0, 0.1 * 180 / Math.PI, day: 0));
            Assert.InRange((double)plan["need"], 502, 508);
        }

        [Fact]
        public void ATargetBeyondTheCraftsDeltaVFailsWithTheNeed()
        {
            var plan = SiteHop.Call(Landed(0, 6, dv: 300));
            var why = (string)plan["why"];
            Assert.StartsWith("the target is 20.9 km away: a Site Hop there needs about ", why);
            Assert.EndsWith(" m/s and the craft has 300 m/s", why);
            Assert.Equal(0.0, (double)plan["throttle"]);
        }

        [Fact]
        public void ACraftThatCanNotLiftOffFails()
        {
            // The Mun's surface gravity is 1.63 m/s^2.
            var why = (string)SiteHop.Call(Landed(0, 6, accel: 1.5))["why"];
            Assert.Equal("the craft can't lift off: its engines give 1.50 m/s^2 against 1.63 m/s^2 of gravity here", why);
        }

        [Fact]
        public void ATargetUnderAKilometreAwayIsTooNearToHop()
        {
            var why = (string)SiteHop.Call(Landed(0, 0.2))["why"];
            Assert.Equal("the target is 0.7 km away: a Site Hop needs at least 1 km", why);
        }

        [Fact]
        public void NearTheGroundItRisesStraightUpAtFullThrottle()
        {
            var tick = SiteHop.Call(Landed(0, 6));
            Assert.Equal(90.0, (double)tick["steer_pitch"]);
            Assert.Equal(1.0, (double)tick["throttle"]);
            Assert.False((bool)tick["done"]);
        }

        [Fact]
        public void ClearOfTheGroundItSteersAlongTheVelocityStillToGain()
        {
            var ask = Landed(0, 6, day: 0);
            ask.Remove("dv");
            ask["agl"] = 100.0;
            var plan = SiteHop.Call(ask);
            // Nothing gained yet: the velocity still to gain is the whole wanted velocity.
            Assert.Equal((double)plan["pitch"], (double)plan["steer_pitch"], 6);
            Assert.Equal((double)plan["heading"], (double)plan["steer_heading"], 6);
            Assert.Equal(1.0, (double)plan["throttle"]);
            Assert.Equal("", plan["why"]);
        }

        [Fact]
        public void ItCutsWhenTheWantedVelocityIsReached()
        {
            var ask = Landed(0, 6, day: 0);
            ask.Remove("dv");
            ask["agl"] = 100.0;
            var plan = SiteHop.Call(ask);
            var v = (double)plan["speed"];
            var p = (double)plan["pitch"] * Math.PI / 180;
            ask["vel_up"] = v * Math.Sin(p) - 0.3;
            ask["vel_east"] = v * Math.Cos(p);
            var tick = SiteHop.Call(ask);
            Assert.True((bool)tick["done"]);
            Assert.Equal(0.0, (double)tick["throttle"]);
        }

        [Fact]
        public void NearTheEndTheThrottleEasesOff()
        {
            var ask = Landed(0, 6, day: 0);
            ask.Remove("dv");
            ask["agl"] = 100.0;
            var plan = SiteHop.Call(ask);
            var v = (double)plan["speed"];
            var p = (double)plan["pitch"] * Math.PI / 180;
            ask["vel_up"] = v * Math.Sin(p) - 1.0;
            ask["vel_east"] = v * Math.Cos(p);
            var tick = SiteHop.Call(ask);
            Assert.False((bool)tick["done"]);
            Assert.InRange((double)tick["throttle"], 0.3, 0.5); // 1 m/s left at 5 m/s^2: 0.4 of full thrust ends it in half a second
        }
    }
}
