// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>find_landing_site</c> on mission f623's orbit (#536): Mun Shot v2 in a 28 km retrograde Mun orbit (inclination 177.9) at
    /// UT 84123.7, from that turn's Game Snapshot, against a fake Mun whose East Crater is where <c>land</c> touched down (1.153,
    /// 79.031), with rolling terrain and a sun that rises there about 14 h on. The fake body counts what the game's body interface
    /// would be asked: its round trips (each one a frame, the tool running on a worker thread) and its samples.
    /// </summary>
    public sealed class FindLandingSiteMunShotTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double MunTurn = 138984.38;
        private const double Now = 84123.713396560735;
        private const double MPerDeg = MunR * Math.PI / 180;
        /// <summary>The Mun's day: its turn against the sun's, Kerbin going round the sun once in 9,203,545 s.</summary>
        private static readonly double SolarDay = 1 / (1 / MunTurn - 1 / 9203545.0);

        private readonly ITestOutputHelper _out;
        private readonly PlanningTools _tools = PlanningTools.Default();

        public FindLandingSiteMunShotTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>The fake Mun, counting what it is asked.</summary>
        internal sealed class GameMun : PlanningBodyBase
        {
            public double LonOffset;
            public int RoundTrips;
            public int Terrain;
            public int Biomes;
            public int Suns;
            public readonly List<string> Asks = new List<string>();

            public override double TerrainAltitude(string body, double latitude, double longitude)
            {
                // Rolling ground: two waves about 3 km long, 80 m high, so slopes run from flat to about 12 degrees.
                var x = longitude * MPerDeg * Math.Cos(latitude * Math.PI / 180);
                var y = latitude * MPerDeg;
                return 1000 + 80 * Math.Sin(x / 500) + 80 * Math.Sin(y / 500);
            }

            public override string Biome(string body, double latitude, double longitude)
            {
                return latitude > -0.5 && latitude < 8 && longitude > 68 && longitude < 90 ? "EastCrater" : "Highlands";
            }

            public override double SunElevation(string body, double latitude, double longitude, double ut)
            {
                // The sub-solar point drifts west a turn a day; it is 70 degrees east of the site (the sun 20 degrees up) 14 h on.
                var sub = 79 + 70 + 360 * 14 * 3600 / SolarDay - 360 * (ut - Now) / SolarDay;
                return 90 - LandingSiteSearch.ArcDegrees(latitude, longitude, 0, LandingSiteSearch.Wrap180(sub));
            }

            public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
            {
                RoundTrips++;
                Asks.Add(facts + ":" + samples.Count);
                if ((facts & SiteFacts.Altitude) != 0) Terrain += samples.Count;
                if ((facts & SiteFacts.Biome) != 0) Biomes += samples.Count;
                if ((facts & SiteFacts.Sun) != 0) Suns += samples.Count;
                base.Survey(body, samples, facts);
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                RoundTrips++;
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(LonOffset + Math.Atan2(y, x) * 180 / Math.PI);
                return true;
            }

            public string Line()
            {
                return RoundTrips + " round trips, " + Terrain + " terrain, " + Biomes + " biome, " + Suns + " sun samples";
            }
        }

        internal static Conic Orbit()
        {
            return Conic.FromElements(MunGm, 228351.13100643214, 0.00044413505266554409, 177.87604694362062, 73.9933151117832, 352.118150791042,
                2.8730076778318625, 84123.713396560735);
        }

        /// <summary>The fake Mun with its frame turned so land's touchdown (UT 134906.9, after a burn at 133663.2) is over (1.153, 79.031).</summary>
        internal static GameMun Mun()
        {
            var orbit = Orbit();
            var arrival = orbit.Position(133663.2).Normalized * -1;
            var lonOffset = 79.031 - Math.Atan2(arrival.Y, arrival.X) * 180 / Math.PI + 360 / MunTurn * (134906.9 - Now);
            return new GameMun { LonOffset = LandingSiteSearch.Wrap180(lonOffset) };
        }

        internal static GameSnapshot Snapshot()
        {
            var snapshot = new GameSnapshot
            {
                Ut = Now, Situation = "ORBITING", Body = "Mun", DeltaVSimulator = true, DaylightLandingDefault = true, TimeCapHours = 6,
            };
            snapshot.Craft.Name = "Mun Shot v2";
            snapshot.Craft.MassTonnes = 5.5828304290771484;
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = 1995.3963623046875, DeltaVAsl = 1995.3963623046875, ThrustVacKn = 59.999996185302734, StartMassT = 5.5828304290771484, EndMassT = 3.0953993797302246 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, StartMassT = 1.4150002002716064, EndMassT = 1.4150002002716064 });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, StartMassT = 1.4150000810623169, EndMassT = 1.4150000810623169 });
            snapshot.Orbit = new SnapshotOrbit
            {
                Apoapsis = 28452.54974802793, Periapsis = 28249.712264836387, Inclination = 177.87604694362062, Eccentricity = 0.00044413505266554409,
                Period = 2686.3715765306033, SemiMajorAxis = 228351.13100643214, Lan = 73.9933151117832, ArgumentOfPeriapsis = 352.118150791042,
                MeanAnomalyAtEpoch = 2.8730076778318625, Epoch = 84123.713396560735, Body = "Mun",
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256,
            });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = MunTurn, Sma = 12000000 });
            return snapshot;
        }

        public static IEnumerable<object[]> F623Calls()
        {
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\"}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":12,\"max_slope_deg\":8}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":38.6,\"max_slope_deg\":8}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":24,\"max_slope_deg\":8}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":12,\"max_slope_deg\":8,\"daylight\":false}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":24,\"max_slope_deg\":15,\"edge_margin_m\":0}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":36,\"max_slope_deg\":20,\"edge_margin_m\":0}" };
            yield return new object[] { "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":46,\"max_slope_deg\":10}" };
        }

        [Fact]
        public void ProfilePass()
        {
            var mun = Mun();
            var watch = Stopwatch.StartNew();
            var pass = BlockCalls.LandingPass(new Dictionary<string, object>
            {
                { "lat", 1.153 }, { "lng", 79.031 }, { "r_pe", MunR + 1000 + 3000.0 }, { "daylight", true }, { "within_hours", 46.0 },
                { "thrust", 60.0 }, { "mass", 5.58 }, { "isp", 345.0 }, { "turn_lag", 2.0 }, { "max_correction_dv", 30.0 },
            }, Snapshot(), mun);
            _out.WriteLine("pass: " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms CPU, " + mun.Line() + " error " + pass["error"] + " arrive " + pass["arrive_ut"]);
            _out.WriteLine(string.Join(" ", mun.Asks.GroupBy(a => a).Select(g => g.Key + "x" + g.Count())));
        }

        [Theory]
        [MemberData(nameof(F623Calls))]
        public void Profile(string input)
        {
            var mun = Mun();
            var watch = Stopwatch.StartNew();
            var outcome = _tools.Run("find_landing_site", input, Snapshot(), mun);
            _out.WriteLine(input + ": " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms CPU, " + mun.Line());
            _out.WriteLine(string.Join(" ", mun.Asks.GroupBy(a => a).Select(g => g.Key + "x" + g.Count())));
            _out.WriteLine(outcome.Json.Length > 1500 ? outcome.Json.Substring(0, 1500) : outcome.Json);
        }
    }
}
