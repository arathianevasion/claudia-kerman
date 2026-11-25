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
    /// near the site flies its powered descent from the arc, aimed at the site, or for "any" (target "") at where the arc ends.
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
            ask["target"] = at != null ? new Dictionary<string, object> { { "lat", at[0] }, { "lng", at[1] } } : site;
            ask["now"] = 1000.0;
            ask["descent_shape"] = "auto";
            return ask;
        }

        private static IDictionary<string, object> Orbit(string situation, double ecc)
        {
            return new Dictionary<string, object> { { "situation", situation }, { "ecc", ecc }, { "sma", 230000.0 }, { "mu", MunMu }, { "fall_s", -1.0 } };
        }

        [Fact]
        public void ANodeLandDidNotPlanRefusesTheStartFromAnOrbitAndFromAnArc()
        {
            // #1357: land plans its own burns after this check, so any node planned now is another's (#1316: a stale -59 m/s node that exec_node flew as land's deorbit).
            var stray = new List<object> { "100|0|0|-59" };
            var orbit = Orbit("ORBITING", 0.002);
            orbit["nodes"] = stray;
            var arc = Arc(0, 20.9, 300, new[] { 0.0, 20.4 });
            arc["nodes"] = stray;
            foreach (var ask in new[] { orbit, arc })
            {
                var said = BlockCalls.LandStart(ask);
                Assert.Equal("fail", said["start"]);
                Assert.Equal("land won't start with a maneuver node it didn't plan: one at UT 100 for 59 m/s; remove it first", said["why"]);
            }
        }

        [Fact]
        public void SeveralStrayNodesAreCountedAndTheFirstNamed()
        {
            var orbit = Orbit("ORBITING", 0.002);
            orbit["nodes"] = new List<object> { "100|0|0|-59", "500|3|4|0" };
            Assert.Equal("land won't start with 2 maneuver nodes it didn't plan: the first at UT 100 for 59 m/s; remove them first", BlockCalls.LandStart(orbit)["why"]);
        }

        [Fact]
        public void NoNodesStartsAsBefore()
        {
            var orbit = Orbit("ORBITING", 0.002);
            orbit["nodes"] = new List<object>();
            Assert.Equal("orbit", BlockCalls.LandStart(orbit)["start"]);
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
            var site = (IDictionary<string, object>)((IDictionary<string, object>)said["plan"])["site"];
            Assert.Equal(0.0, (double)site["lat"], 6);
            Assert.Equal(20.4, (double)site["lng"], 6);
            Assert.InRange((double)said["end_m"], 950, 1000);
        }

        [Fact]
        public void AnArcStartGivesTheSpeedWhereItMeetsTheGroundAndTheDeltaVTheDescentNeeds()
        {
            // Vis-viva at 200 km on a 120 km semi-major axis: sqrt(mu (2/200000 - 1/120000)) = 329.5 m/s; needed is 1.2 x that + 100.
            var figures = (IDictionary<string, object>)((IDictionary<string, object>)BlockCalls.LandStart(Arc(0, 20.9, 300, new[] { 0.0, 20.4 }))["plan"])["figures"];
            Assert.Equal(0.0, figures["deorbit_dv"]);
            Assert.Equal(329.5, (double)figures["v_pe"], 1);
            Assert.Equal(495.4, (double)figures["needed"], 1);
        }

        [Fact]
        public void AnArcsPlanHasNoPassAndBrakesFromTheRealState()
        {
            // No deorbit burn: it "burns" now and arrives when the arc meets the ground; no path, so braking predicts its own;
            // braking at the ground's radius; "auto" leaves the shape to the descent (-1), a number is flown as given.
            var plan = (IDictionary<string, object>)BlockCalls.LandStart(Arc(0, 20.9, 300, new[] { 0.0, 20.4 }))["plan"];
            Assert.Equal("", plan["error"]);
            Assert.Equal(1000.0, plan["burn_ut"]);
            Assert.Equal(1300.0, plan["arrive_ut"]);
            Assert.Equal(-1.0, plan["next_pass_ut"]);
            Assert.Equal(MunRadius, plan["r_pe"]);
            Assert.Equal(-1.0, plan["descent_shape"]);
            Assert.Empty((IList<object>)plan["path"]);
            var given = Arc(0, 20.9, 300, new[] { 0.0, 20.4 });
            given["descent_shape"] = 0.5;
            Assert.Equal(0.5, ((IDictionary<string, object>)BlockCalls.LandStart(given)["plan"])["descent_shape"]);
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
            var said = BlockCalls.LandStart(Arc(1.5, 20.9, 300, ""));
            Assert.Equal("arc", said["start"]);
            var site = (IDictionary<string, object>)((IDictionary<string, object>)said["plan"])["site"];
            Assert.Equal(1.5, (double)site["lat"], 6);
            Assert.Equal(20.123, (double)site["lng"], 3);
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
