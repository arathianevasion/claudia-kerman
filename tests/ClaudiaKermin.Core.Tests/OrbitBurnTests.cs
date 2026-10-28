// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>ADDONS:CLAUDIA:ORBITBURN</c>, change_orbit's solve (#444), through <see cref="BlockCalls"/>: the same answer as
    /// <c>solve_burn</c> for every goal, unrounded, with the figure each goal aims at; each node flown as an impulse on a
    /// <see cref="Conic"/> reaches it.
    /// </summary>
    public sealed class OrbitBurnTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double KerbinDay = 21549.425;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The craft round Kerbin between the two altitudes (SolveBurnGoalsTests' bodies, Minmus inclined 6°).</summary>
        private static GameSnapshot Snapshot(double pe, double ap, double inclination, double lan, double argPe, double phase)
        {
            double rp = KerbinR + pe, ra = KerbinR + ap;
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = inclination, Lan = lan, ArgumentOfPeriapsis = argPe,
                MeanAnomalyAtEpoch = phase, Epoch = 0, Body = "Kerbin", Periapsis = pe, Apoapsis = ap,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = KerbinDay, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
            });
            return snapshot;
        }

        /// <summary>SolveBurnGoalsTests' contract orbit: 100 km by 3500 km inclined 20°, so every goal has a node, a crossing and room.</summary>
        private static GameSnapshot Every()
        {
            return Snapshot(100000, 3500000, 20, 15, 30, 0);
        }

        /// <summary>Each goal as solve_burn's JSON input and as ORBITBURN's lexicon, with the elements it should say it sets.</summary>
        public static TheoryData<string, string, string> Goals()
        {
            return new TheoryData<string, string, string>
            {
                { "{\"goal\":\"set_sma\",\"value\":1000000}", "goal=set_sma;value=1000000", "sma=1000000" },
                { "{\"goal\":\"set_ecc\",\"value\":0.1}", "goal=set_ecc;value=0.1", "ecc=0.1" },
                { "{\"goal\":\"ellipticize\",\"pe\":80000,\"ap\":300000,\"at\":\"altitude 150000\"}", "goal=ellipticize;pe=80000;ap=300000;at=altitude 150000", "pe=80000,ap=300000" },
                { "{\"goal\":\"set_inc\",\"value\":5}", "goal=set_inc;value=5", "inc=5" },
                { "{\"goal\":\"match_plane\",\"target\":\"Minmus\"}", "goal=match_plane;target=Minmus", "rel_inc=0" },
                { "{\"goal\":\"shift_lan\",\"value\":60}", "goal=shift_lan;value=60", "lan=60,inc=20" },
                { "{\"goal\":\"resonant\",\"value\":2,\"of\":3}", "goal=resonant;value=2;of=3", "period=resonant" },
                { "{\"goal\":\"stationary\"}", "goal=stationary", "period=" + KerbinDay.ToString("R", CultureInfo.InvariantCulture) },
                { "{\"goal\":\"set_pe\",\"value\":90000}", "goal=set_pe;value=90000", "pe=90000" },
                { "{\"goal\":\"circularize\",\"at\":\"ap\"}", "goal=circularize;at=ap", "ecc=0" },
            };
        }

        /// <summary>"key=value;..." as a block's lexicon would give it: numbers as doubles, the rest as strings.</summary>
        private static Dictionary<string, object> Ask(string pairs)
        {
            var ask = new Dictionary<string, object>();
            foreach (var pair in pairs.Split(';'))
            {
                var parts = pair.Split('=');
                double number;
                ask[parts[0]] = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? (object)number : parts[1];
            }
            return ask;
        }

        private static double D(IDictionary<string, object> result, string key)
        {
            return Convert.ToDouble(result[key], CultureInfo.InvariantCulture);
        }

        [Theory]
        [MemberData(nameof(Goals))]
        public void Each_goal_gives_solve_burns_node_unrounded_and_what_it_aims_at(string json, string ask, string wanted)
        {
            var snapshot = Every();
            var block = BlockCalls.OrbitBurn(Ask(ask), snapshot);
            Assert.Equal("", block["error"]);
            var tool = _tools.Run("solve_burn", json, snapshot, new NoPlanningBody());
            Assert.False(tool.IsError, tool.Json);
            using (var doc = JsonDocument.Parse(tool.Json))
            {
                var result = doc.RootElement;
                // The tool rounds the time to 0.1 s and the components to 0.01 m/s.
                Assert.Equal(result.GetProperty("at_ut").GetDouble(), D(block, "ut"), 0);
                Assert.InRange(D(block, "ut") - result.GetProperty("at_ut").GetDouble(), -0.05, 0.05);
                foreach (var axis in new[] { "prograde", "normal", "radial" })
                    Assert.InRange(D(block, axis) - result.GetProperty("dv").GetProperty(axis).GetDouble(), -0.005, 0.005);
                Assert.InRange(D(block, "dv") - result.GetProperty("magnitude").GetDouble(), -0.005, 0.005);
            }
            // No craft stages in the snapshot: no burn time.
            Assert.Equal(-1.0, D(block, "burn_time"));
            var aims = (IDictionary<string, object>)block["wanted"];
            var expected = wanted.Split(',').Select(p => p.Split('=')).ToList();
            Assert.Equal(expected.Select(p => p[0]), aims.Keys);
            foreach (var p in expected.Where(p => p[1] != "resonant"))
                Assert.Equal(double.Parse(p[1], CultureInfo.InvariantCulture), Convert.ToDouble(aims[p[0]], CultureInfo.InvariantCulture), 6);
        }

        /// <summary>Each goal's ORBITBURN lexicon alone.</summary>
        public static TheoryData<string> Asks()
        {
            var asks = new TheoryData<string>();
            foreach (var row in Goals()) asks.Add((string)row[1]);
            return asks;
        }

        [Theory]
        [MemberData(nameof(Asks))]
        public void Each_node_flown_as_an_impulse_reaches_what_it_aims_at(string ask)
        {
            var snapshot = Every();
            var block = BlockCalls.OrbitBurn(Ask(ask), snapshot);
            Assert.Equal("", block["error"]);
            var after = Flown(snapshot, block);
            foreach (var aim in (IDictionary<string, object>)block["wanted"])
            {
                var figure = Convert.ToDouble(aim.Value, CultureInfo.InvariantCulture);
                var actual = Element(after, aim.Key, snapshot);
                switch (aim.Key)
                {
                    case "inc":
                    case "rel_inc":
                    case "lan":
                        Assert.InRange(actual - figure, -0.01, 0.01);
                        break;
                    case "ecc":
                        Assert.InRange(actual - figure, -1e-4, 1e-4);
                        break;
                    case "period":
                        Assert.InRange(actual / figure, 0.999, 1.001);
                        break;
                    default:
                        Assert.InRange(actual - figure, -10, 10);
                        break;
                }
            }
        }

        [Fact]
        public void Resonant_aims_at_the_period_that_makes_value_orbits_while_this_orbit_makes_of()
        {
            var snapshot = Every();
            var block = BlockCalls.OrbitBurn(Ask("goal=resonant;value=2;of=3"), snapshot);
            var period = Convert.ToDouble(((IDictionary<string, object>)block["wanted"])["period"], CultureInfo.InvariantCulture);
            Assert.Equal(Orbit(snapshot).Period * 3 / 2, period, 3);
        }

        [Fact]
        public void The_node_is_the_orbits_at_the_call_so_a_later_snapshot_gives_a_later_node()
        {
            // The same set_inc asked an hour apart: a block solves from the orbit when it starts, not the plan's.
            var early = Every();
            var late = Every();
            late.Ut = 3600;
            var first = BlockCalls.OrbitBurn(Ask("goal=set_inc;value=40"), early);
            var second = BlockCalls.OrbitBurn(Ask("goal=set_inc;value=40"), late);
            Assert.True(D(first, "ut") < 3600, first["ut"].ToString());
            Assert.True(D(second, "ut") >= 3600, second["ut"].ToString());
        }

        [Theory]
        [InlineData(null, "goal must be one of circularize, set_ap, set_pe, next_patch_pe, set_sma, set_ecc, ellipticize, set_inc, match_plane, shift_lan, resonant, stationary, intercept_at")]
        [InlineData("goal=launch", "goal must be one of circularize, set_ap, set_pe, next_patch_pe, set_sma, set_ecc, ellipticize, set_inc, match_plane, shift_lan, resonant, stationary, intercept_at")]
        [InlineData("goal=intercept_at;target=Mun", "ORBITBURN does not solve intercept_at")]
        [InlineData("goal=intercept_at;target=Mun;tof=3600", "ORBITBURN does not solve intercept_at")]
        [InlineData("goal=set_inc", "set_inc needs a value, the inclination in degrees")]
        [InlineData("goal=set_inc;value=200", "set_inc's value is an inclination from 0 to 180 degrees")]
        [InlineData("goal=set_inc;value=high", "value must be a finite number, not high")]
        [InlineData("goal=set_inc;value=90;colour=red", "unknown input colour: ORBITBURN takes solve_burn's goal, value, at, target, pe, ap, of and direction")]
        [InlineData("goal=match_plane", "match_plane needs target, the body whose orbit plane to match")]
        [InlineData("goal=match_plane;target=Pluto", "there is no body named \"Pluto\"")]
        public void An_ask_it_cannot_solve_gives_only_the_reason(string ask, string reason)
        {
            var result = BlockCalls.OrbitBurn(ask == null ? new Dictionary<string, object>() : Ask(ask), Every());
            Assert.Equal(new[] { "error" }, result.Keys);
            Assert.StartsWith(reason, (string)result["error"]);
        }

        [Fact]
        public void A_number_where_a_name_goes_or_a_non_finite_number_is_refused()
        {
            Assert.Equal("goal must be a string, not 5", BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", 5.0 } }, Every())["error"]);
            Assert.Equal("value must be a finite number, not NaN",
                BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", "set_inc" }, { "value", double.NaN } }, Every())["error"]);
            Assert.Equal("no Game Snapshot", BlockCalls.OrbitBurn(Ask("goal=set_inc;value=90"), null)["error"]);
        }

        [Fact]
        public void At_takes_a_ut_as_well_as_a_word()
        {
            var result = BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", "set_inc" }, { "value", 30.0 }, { "at", 600.0 } }, Every());
            Assert.Equal("", result["error"]);
            Assert.Equal(600, D(result, "ut"), 6);
        }

        [Theory]
        [MemberData(nameof(Asks))]
        public void Each_goal_solves_in_milliseconds(string ask)
        {
            // solve_burn's own budget (SolveBurnGoalsTests): the block call adds only the lexicon round trip.
            var snapshot = Every();
            var input = Ask(ask);
            Timing.Within(20, () => Assert.Equal("", BlockCalls.OrbitBurn(input, snapshot)["error"]));
        }

        private static Conic Orbit(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            return Conic.FromElements(KerbinGm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        /// <summary>The orbit after <paramref name="node"/>'s burn as an impulse at its UT.</summary>
        private static Conic Flown(GameSnapshot snapshot, IDictionary<string, object> node)
        {
            var ut = D(node, "ut");
            Vec3 r, v;
            Orbit(snapshot).State(ut, out r, out v);
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var burn = along * D(node, "prograde") + up * D(node, "normal") + Vec3.Cross(along, up) * D(node, "radial");
            return Conic.FromState(KerbinGm, r, v + burn, ut);
        }

        /// <summary>An element of <paramref name="orbit"/> by ORBITBURN's names; rel_inc against Minmus.</summary>
        private static double Element(Conic orbit, string element, GameSnapshot snapshot)
        {
            switch (element)
            {
                case "inc": return orbit.Inclination;
                case "rel_inc":
                    var w = new BodySystem(snapshot.Bodies).OrbitOf(snapshot.Bodies.Single(b => b.Name == "Minmus")).W;
                    return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(orbit.W, w)))) * 180 / Math.PI;
                case "lan":
                    var node = Vec3.Cross(new Vec3(0, 0, 1), orbit.W);
                    var lan = Math.Atan2(node.Y, node.X) * 180 / Math.PI;
                    return lan < 0 ? lan + 360 : lan;
                case "period": return orbit.Period;
                case "sma": return orbit.A;
                case "ecc": return orbit.E;
                case "pe": return orbit.PeR - KerbinR;
                case "ap": return orbit.ApR - KerbinR;
                default: throw new ArgumentException(element);
            }
        }
    }
}
