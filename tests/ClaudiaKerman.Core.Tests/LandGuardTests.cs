// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>land</c>'s path guard and its vertical fallback (#712, ADR 0014), as <c>ADDONS:CLAUDIA:LANDGUARD</c> answers each braking
    /// tick (<see cref="LandGuard.Tick"/>). The guard's cases moved from LandAscendTests; the fallback replays
    /// m20261104-013323-9903's abort over Minmus Flats.
    /// </summary>
    public sealed class LandGuardTests
    {
        private const double MinmusGm = 1.7658e9;
        private const double MinmusR = 60000;
        /// <summary>Mun Shot v2's lander on Minmus: 60 kN, about 6.1 t.</summary>
        private const double AMax = 60000 / 6100.0;

        /// <summary>The state at the abort: 190 m short of the site, 3108 m up over the Flats at sea level, 43.4 m/s across and 5.4 down, 2127 m/s left, the path predicted at 2081 m.</summary>
        private static Dictionary<string, object> AtTheAbort()
        {
            return new Dictionary<string, object>
            {
                { "alt", 3108.0 }, { "path", Path(2081, 0) }, { "s", 190.0 }, { "c", 0.0 }, { "v_al", 43.4 }, { "v_cr", 0.0 },
                { "v_up", -5.4 }, { "radar", 3108.0 }, { "a_max", AMax }, { "g", G(3108) }, { "radius", MinmusR + 3108 }, { "dv_left", 2127.0 },
                { "touchdown", 2.0 }, { "margin", 1.1 }, { "lag", 2.0 }, { "hover", false }, { "vertical", false },
            };
        }

        /// <summary>land_path_at's answer: the predicted path's alt and ground there, m above sea level.</summary>
        private static Dictionary<string, object> Path(double alt, double ground)
        {
            return new Dictionary<string, object> { { "alt", alt }, { "ground", ground } };
        }

        private static double G(double alt)
        {
            return MinmusGm / Math.Pow(MinmusR + alt, 2);
        }

        // ---- The guard (moved from LandAscendTests, #511) ----

        [Fact]
        public void A_basin_after_a_ridge_doesnt_trip_the_guard_and_a_real_climb_off_the_predicted_path_does()
        {
            // #511: altitude against the prediction at that point along the track, not clearance against its start. The path
            // climbs 1.5 km over a 2 km ridge 30 km out (3500 m), drops into a basin 1.5 km under the plain (1500 m, 20 km out)
            // and comes down to the plain (800 m, 10 km out).
            Assert.Equal("fly", Far(3500, 3500, 2000)["verdict"]);
            Assert.Equal("fly", Far(1600, 1500, -1500)["verdict"]);
            // 0.9 km over a predicted 2500 m: under the 1 km floor.
            Assert.Equal("fly", Far(3400, 2500, 1000)["verdict"]);
            var climbed = Far(2300, 800, 0);
            Assert.Equal("abort", climbed["verdict"]);
            Assert.Equal("braking is above its predicted path: 1.5 km over it", climbed["error"]);
            // A quarter of the predicted height over the ground, when that is over 1 km.
            Assert.Equal(1500, LandGuard.Margin(6000, 0));
            Assert.Equal("fly", Far(7500, 6000, 0)["verdict"]);
            Assert.Equal("abort", Far(7501, 6000, 0)["verdict"]);
            // No path (land_path_at's ""), no guard.
            Assert.Equal("fly", LandGuard.Tick(new Dictionary<string, object> { { "alt", 99999.0 }, { "path", "" }, { "s", 50000.0 } })["verdict"]);
        }

        /// <summary>A tick 50 km short of the site, so a guard that trips can't come straight down.</summary>
        private static IDictionary<string, object> Far(double alt, double pathAlt, double pathGround)
        {
            var tick = AtTheAbort();
            tick["alt"] = alt;
            tick["path"] = Path(pathAlt, pathGround);
            tick["s"] = 50000.0;
            return LandGuard.Tick(tick);
        }

        // ---- The vertical fallback ----

        [Fact]
        public void At_the_m20261104_abort_it_comes_straight_down_onto_the_site_and_touches_down()
        {
            var first = LandGuard.Tick(AtTheAbort());
            Assert.Equal("vertical", first["verdict"]);
            Assert.Equal("", first["error"]);
            Assert.Equal("braking is above its predicted path: 1 km over it, 190 m from the site at 43.4 m/s", first["why"]);
            var flown = Fly(AtTheAbort());
            Assert.True(flown.Landed, "still " + Math.Round(flown.Alt) + " m up after " + Math.Round(flown.T) + " s");
            Assert.InRange(flown.TouchdownSpeed, 0, 3);
            Assert.InRange(flown.S, -LandGuard.PastM, 150);
            // Well inside the delta-v it was allowed for, and that a small part of what was left.
            var need = LandGuard.FallbackDv(43.4, 5.4, 3108, AMax, G(3108), 1.1);
            Assert.InRange(flown.Dv, 0.5 * need, LandGuard.DvMargin * need);
            Assert.InRange(need, 50, 300);
        }

        [Fact]
        public void Short_of_the_site_or_of_delta_v_it_still_aborts()
        {
            // 3 km short, as when braking starts: the shape went wrong, so land_recover as before.
            var shortOfSite = AtTheAbort();
            shortOfSite["s"] = 3000.0;
            shortOfSite["v_al"] = 150.0;
            var tick = LandGuard.Tick(shortOfSite);
            Assert.Equal("abort", tick["verdict"]);
            Assert.Equal("braking is above its predicted path: 1 km over it", tick["error"]);
            Assert.Equal("3000 m from the site, over 1000", tick["why"]);
            // Too fast to stop by 100 m past it.
            var tooFast = AtTheAbort();
            tooFast["v_al"] = 90.0;
            Assert.StartsWith("stops ", (string)LandGuard.Tick(tooFast)["why"]);
            // Not the delta-v for the descent, or none known.
            foreach (var left in new[] { 100.0, 0.0, -1.0 })
            {
                var poor = AtTheAbort();
                poor["dv_left"] = left;
                tick = LandGuard.Tick(poor);
                Assert.Equal("abort", tick["verdict"]);
                Assert.Contains(" m/s left, under ", (string)tick["why"]);
            }
            // A lander that can't hold itself up.
            var weak = AtTheAbort();
            weak["a_max"] = 0.5;
            Assert.Equal("too little thrust to hold against gravity", LandGuard.Tick(weak)["why"]);
        }

        [Fact]
        public void Once_vertical_it_stays_vertical_holding_its_height_while_it_stops_and_descending_once_stopped()
        {
            var tick = AtTheAbort();
            tick["vertical"] = true;
            tick["path"] = Path(9000, 0);
            var holding = LandGuard.Tick(tick);
            Assert.Equal("vertical", holding["verdict"]);
            Assert.Equal(0.0, (double)holding["vv"]);
            Assert.True((double)holding["al"] < 0);
            // The hold isn't land_phase's "terminal", so the hover watch doesn't catch it (h_ref is the height it holds).
            Assert.Equal(3108.0, (double)holding["h_ref"]);
            tick["v_al"] = 0.0;
            tick["s"] = 5.0;
            var down = LandGuard.Tick(tick);
            // The terminal descent's sink limit, 100 m/s at most, from 3 km up.
            Assert.Equal(-100.0, (double)down["vv"], 6);
            Assert.Equal(0.0, (double)down["h_ref"]);
            tick["hover"] = true;
            Assert.Equal(-2.0, (double)LandGuard.Tick(tick)["vv"], 6);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_thousand_ticks_take_under_5_ms()
        {
            // ADR 0014 guardrail 6: LANDGUARD is called every braking tick; 1,000 ticks (about 20 s of braking at 50 Hz) in 5 ms.
            var guarding = AtTheAbort();
            guarding["s"] = 50000.0;
            guarding["alt"] = 2081.0;
            var vertical = AtTheAbort();
            vertical["vertical"] = true;
            Timing.Within(5, () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    LandGuard.Tick(guarding);
                    LandGuard.Tick(vertical);
                }
            });
        }

        private sealed class Flight
        {
            public double S, Alt, T, Dv, TouchdownSpeed;
            public bool Landed;
        }

        /// <summary>
        /// The fallback flown from <paramref name="state"/> on a point mass along the track over ground at sea level, as land_descend
        /// flies it: LandGuard each 0.1 s tick, the thrust lagging the demand by 1 s (the turn), at most <see cref="AMax"/>.
        /// </summary>
        private static Flight Fly(Dictionary<string, object> state)
        {
            const double dt = 0.1;
            double s = (double)state["s"], y = (double)state["alt"], vx = (double)state["v_al"], vy = (double)state["v_up"];
            double ax = 0, ay = 0;
            var flight = new Flight();
            while (y > 0 && flight.T < 900)
            {
                var g = G(y);
                state["s"] = s;
                state["alt"] = y;
                state["radar"] = y;
                state["v_al"] = vx;
                state["v_up"] = vy;
                state["g"] = g;
                state["radius"] = MinmusR + y;
                var tick = LandGuard.Tick(state);
                Assert.Equal("vertical", tick["verdict"]);
                state["vertical"] = true;
                ax += ((double)tick["al"] - ax) * dt;
                ay += ((double)tick["up"] - ay) * dt;
                var a = Math.Sqrt(ax * ax + ay * ay);
                if (a > AMax)
                {
                    ax *= AMax / a;
                    ay *= AMax / a;
                    a = AMax;
                }
                flight.TouchdownSpeed = Math.Sqrt(vx * vx + vy * vy);
                vx += ax * dt;
                vy += (ay - g + vx * vx / (MinmusR + y)) * dt;
                s -= vx * dt;
                y += vy * dt;
                flight.Dv += a * dt;
                flight.T += dt;
            }
            flight.S = s;
            flight.Alt = y;
            flight.Landed = y <= 0;
            return flight;
        }
    }
}
