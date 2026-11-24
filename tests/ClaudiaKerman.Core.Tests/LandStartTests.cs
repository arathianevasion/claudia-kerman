// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:LANDSTART</c> (#1309, ADR 0014): where <c>land</c> starts from. A low, near-circular orbit flies the
    /// deorbit as before (the rule was land's inline eccentricity check, pinned here with its words); a suborbital arc that ends
    /// near the site flies its powered descent from the arc, aimed at the site, or for "any" at where the arc ends.
    /// Mun figures: mu 6.5138398e10, radius 200 km, day 138984.48 s.
    /// </summary>
    public sealed class LandStartTests
    {
        private const double MunMu = 6.5138398e10;
        private const double MunRadius = 200000;
        private const double MunDay = 138984.48;

        private static IDictionary<string, object> Arc(double endLat, double endLng, double fallS, object site)
        {
            var ask = new Dictionary<string, object>
            {
                { "situation", "SUB_ORBITAL" }, { "ecc", 0.6 }, { "sma", 120000.0 }, { "mu", MunMu }, { "r_fall", MunRadius },
                { "radius", MunRadius }, { "day", MunDay }, { "fall_s", fallS }, { "end_lat", endLat }, { "end_lng", endLng },
            };
            var at = site as double[];
            if (at != null)
            {
                ask["site_lat"] = at[0];
                ask["site_lng"] = at[1];
            }
            else if (site is string) ask["target"] = site;
            return ask;
        }

        private static IDictionary<string, object> Orbit(string situation, double ecc)
        {
            return new Dictionary<string, object> { { "situation", situation }, { "ecc", ecc }, { "sma", 230000.0 }, { "mu", MunMu }, { "fall_s", -1.0 } };
        }

        [Fact]
        public void ANearCircularOrbitStartsAsBefore()
        {
            Assert.Equal("orbit", BlockCalls.LandStart(Orbit("ORBITING", 0.1))["start"]);
            Assert.Equal("orbit", BlockCalls.LandStart(Orbit("ORBITING", 0.002))["start"]);
        }

        [Fact]
        public void AnEccentricOrbitIsRefusedInLandsOldWords()
        {
            var said = BlockCalls.LandStart(Orbit("ORBITING", 0.25));
            Assert.Equal("fail", said["start"]);
            Assert.StartsWith("land needs a near-circular orbit (eccentricity under 0.1)", (string)said["why"]);
            Assert.Contains("situation ORBITING, eccentricity 0.25", (string)said["why"]);
        }

        [Fact]
        public void AnArcEndingNearTheSiteStartsFromTheArcAimedAtTheSite()
        {
            // The arc ends 0.9 degrees east as kOS reads it now; in the 300 s fall the Mun turns 0.777 degrees east under it,
            // so the ground it meets is 0.123 degrees east of (0, 20): 429 m along the equator, 1 km from the site at (0, 20.4).
            var said = BlockCalls.LandStart(Arc(0, 20.9, 300, new[] { 0.0, 20.4 }));
            Assert.Equal("arc", said["start"]);
            Assert.Equal(0.0, (double)said["lat"], 6);
            Assert.Equal(20.4, (double)said["lng"], 6);
            Assert.InRange((double)said["end_m"], 950, 1000);
        }

        [Fact]
        public void AnArcStartGivesTheSpeedWhereItMeetsTheGroundAndTheDeltaVTheDescentNeeds()
        {
            // Vis-viva at 200 km on a 120 km semi-major axis: sqrt(mu (2/200000 - 1/120000)) = 329.5 m/s; needed is 1.2 x that + 100.
            var said = BlockCalls.LandStart(Arc(0, 20.9, 300, new[] { 0.0, 20.4 }));
            Assert.Equal(329.5, (double)said["v_end"], 1);
            Assert.Equal(495.4, (double)said["needed"], 1);
        }

        [Fact]
        public void AnArcEndingFarFromTheSiteIsRefusedSayingHowFar()
        {
            // 3 degrees of the Mun's equator is 10.5 km.
            var said = BlockCalls.LandStart(Arc(0, 23.777, 300, new[] { 0.0, 20.0 }));
            Assert.Equal("fail", said["start"]);
            Assert.Equal("land's suborbital arc ends 10.5 km from the site; it starts from an arc only when that is within 5 km (otherwise from a low, near-circular orbit)", said["why"]);
        }

        [Fact]
        public void AnyAimsWhereTheArcMeetsTheGround()
        {
            var said = BlockCalls.LandStart(Arc(1.5, 20.9, 300, "any"));
            Assert.Equal("arc", said["start"]);
            Assert.Equal(1.5, (double)said["lat"], 6);
            Assert.Equal(20.123, (double)said["lng"], 3);
        }

        [Fact]
        public void ABiomeTargetFromAnArcIsRefusedAskingForItsSite()
        {
            var said = BlockCalls.LandStart(Arc(0, 20.9, 300, "biome"));
            Assert.Equal("fail", said["start"]);
            Assert.Equal("from a suborbital arc land needs a site, not a biome: pass the site find_landing_site gave as the target", said["why"]);
        }

        [Fact]
        public void APathThatNeverMeetsTheGroundIsNoArc()
        {
            var said = BlockCalls.LandStart(Arc(0, 20.9, -1, new[] { 0.0, 20.4 }));
            Assert.Equal("fail", said["start"]);
            Assert.StartsWith("land needs a near-circular orbit (eccentricity under 0.1) or a suborbital arc", (string)said["why"]);
        }
    }
}
