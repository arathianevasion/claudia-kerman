// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKerman.Core.Tests
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

        /// <summary>
        /// The fake Mun, counting what it is asked and keeping the time the game's body interface would take for it
        /// (<see cref="IPlanningClock"/>): the tool runs on a worker thread, so each round trip waits for a frame
        /// (<see cref="FrameMs"/>, 40 fps), and a frame serves <see cref="SliceMs"/> of samples (the adapter's
        /// <c>PlanningBody.BudgetMs</c>). A sample's cost is from f623's KSP.log: LANDINGPASS on the main thread took 310 ms for
        /// the 2892 terrain samples and 54 sun samples this fixture counts for it, about 90 ms of it the search's own work (2 to 3
        /// times that under Mono), so about 30 µs a terrain sample; a biome or sun read is cheaper, 10 µs.
        /// </summary>
        internal sealed class GameMun : PlanningBodyBase, IPlanningClock
        {
            public const double SliceMs = 3;
            public const double TerrainMs = 0.03;
            public const double BiomeMs = 0.01;
            public const double SunMs = 0.01;

            public double FrameMs = 25;
            public double LonOffset;
            /// <summary>The biome by latitude and longitude; null for f623's East Crater among Highlands.</summary>
            public Func<double, double, string> BiomeAt;
            public int RoundTrips;
            public int Terrain;
            public int Biomes;
            public int Suns;
            public readonly List<string> Asks = new List<string>();

            public double ElapsedMs { get; private set; }

            private void Frames(double sampleMs)
            {
                RoundTrips++;
                ElapsedMs += Math.Max(1, Math.Ceiling(sampleMs / SliceMs)) * FrameMs;
            }

            public override double TerrainAltitude(string body, double latitude, double longitude)
            {
                // Rolling ground: two waves about 3 km long, 80 m high, so slopes run from flat to about 12 degrees.
                var x = longitude * MPerDeg * Math.Cos(latitude * Math.PI / 180);
                var y = latitude * MPerDeg;
                return 1000 + 80 * Math.Sin(x / 500) + 80 * Math.Sin(y / 500);
            }

            public override string Biome(string body, double latitude, double longitude)
            {
                if (BiomeAt != null) return BiomeAt(latitude, longitude);
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
                Asks.Add(facts + ":" + samples.Count);
                var each = 0.0;
                if ((facts & SiteFacts.Altitude) != 0)
                {
                    Terrain += samples.Count;
                    each += TerrainMs;
                }
                if ((facts & SiteFacts.Biome) != 0)
                {
                    Biomes += samples.Count;
                    each += BiomeMs;
                }
                if ((facts & SiteFacts.Sun) != 0)
                {
                    Suns += samples.Count;
                    each += SunMs;
                }
                Frames(each * samples.Count);
                base.Survey(body, samples, facts);
            }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                Frames(0);
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = LandingSiteSearch.Wrap180(LonOffset + Math.Atan2(y, x) * 180 / Math.PI);
                return true;
            }

            public string Line()
            {
                return ElapsedMs.ToString("0") + " ms in the game, " + RoundTrips + " round trips, " + Terrain + " terrain, " + Biomes + " biome, " + Suns + " sun samples";
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

        private static readonly double TimeoutMs = ToolStep.CallTimeout.TotalMilliseconds;

        private JsonElement Run(string input, GameMun mun)
        {
            var watch = Stopwatch.StartNew();
            var outcome = _tools.Run("find_landing_site", input, Snapshot(), mun);
            _out.WriteLine(input + ": " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms of work here, " + mun.Line());
            _out.WriteLine(string.Join(" ", mun.Asks.GroupBy(a => a).Select(g => g.Key + " x" + g.Count())));
            _out.WriteLine(outcome.Json);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private static List<JsonElement> Sites(JsonElement result)
        {
            return result.GetProperty("sites").EnumerateArray().ToList();
        }

        private static string Note(JsonElement result)
        {
            JsonElement note;
            return result.TryGetProperty("note", out note) ? note.GetString() : "";
        }

        /// <summary>
        /// land's ask of its block call for a biome target, with the tool call's own limits as land_site_query passes them: all but
        /// edge_margin_m, which land leaves at the default.
        /// </summary>
        private static Dictionary<string, object> LandAsk(string input)
        {
            var ask = new Dictionary<string, object> { { "max_correction_dv", 30.0 } };
            using (var doc = JsonDocument.Parse(input))
                foreach (var field in doc.RootElement.EnumerateObject())
                    if (field.Name != "edge_margin_m")
                        ask[field.Name] = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()
                            : field.Value.ValueKind == JsonValueKind.Number ? (object)field.Value.GetDouble() : field.Value.GetBoolean();
            if (!ask.ContainsKey("daylight")) ask["daylight"] = true;
            return ask;
        }

        [Fact]
        public void The_default_call_answers_inside_the_tool_timeout_with_east_crater_sites_from_the_whole_window()
        {
            // Before #536 this call made about 1180 round trips (about 30 s in the game), most of them PlaneChanges' 300 pass
            // scans each reading the frame and the sun; it was cut off at 5 s.
            var mun = Mun();
            var result = Run("{\"body\":\"Mun\",\"biome\":\"East Crater\"}", mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            Assert.False(result.TryGetProperty("searched_hours", out _));
            Assert.DoesNotContain("partial", Note(result));
            Assert.Equal(38.61, result.GetProperty("window_hours").GetDouble());
            Assert.NotEmpty(Sites(result));
            Assert.All(Sites(result), s => Assert.Equal("EastCrater", s.GetProperty("biome").GetString()));
            // land reaches the first site with a 0.1 m/s correction, so there is no plane change to look for.
            Assert.Contains("least any approach could cost", result.GetProperty("plane_change_note").GetString());
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_default_calls_own_work_is_small_beside_the_games()
        {
            // The search's own work runs on the tool's thread while the game serves the reads. Measured at about 120 ms in a Debug
            // build; Mono in the game is 2 to 3 times slower, so the budget leaves the timeout to the game's reads.
            Timing.Within(500, () => _tools.Run("find_landing_site", "{\"body\":\"Mun\",\"biome\":\"East Crater\"}", Snapshot(), Mun()));
        }

        [Theory]
        [MemberData(nameof(F623Calls))]
        public void Every_call_f623_made_answers_inside_the_tool_timeout_and_agrees_with_land(string input)
        {
            var mun = Mun();
            var result = Run(input, mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            // land's block call for the biome, with the same limits and window: the same search, run to the end.
            var land = BlockCalls.LandingSite(LandAsk(input), Snapshot(), Mun());
            _out.WriteLine("land: " + land["error"]);
            Assert.DoesNotContain("partial", Note(result));
            if (Sites(result).Count == 0)
            {
                Assert.StartsWith("no site in the East Crater biome", (string)land["error"]);
                Assert.Contains("land given this biome and these limits and window searches the same ground-track points, so it finds none either", Note(result));
                return;
            }
            Assert.Equal("", land["error"]);
            // With another biome margin than land's the sites may differ; that one exists agrees.
            if (input.Contains("edge_margin_m")) return;
            Assert.Equal(Sites(result)[0].GetProperty("latitude").GetDouble(), (double)land["lat"], 3);
            Assert.Equal(Sites(result)[0].GetProperty("longitude").GetDouble(), (double)land["lng"], 3);
        }

        [Fact]
        public void A_46_h_window_in_daylight_under_10_degrees_gives_the_east_crater_site_land_flies()
        {
            // What Claudia gave land on f623 (land found 1.153, 79.031, slope 5.3, at 723 m/s against 725 predicted).
            var input = "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"daylight\":true,\"max_slope_deg\":10,\"within_hours\":46}";
            var mun = Mun();
            var result = Run(input, mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            var site = Sites(result)[0];
            Assert.Equal("EastCrater", site.GetProperty("biome").GetString());
            Assert.InRange(site.GetProperty("slope_deg").GetDouble(), 0, 10);
            Assert.True(site.GetProperty("sun_elevation_deg").GetDouble() > LandingSiteSearch.MinSunElevationDeg);
            Assert.InRange(site.GetProperty("landing_ut").GetDouble() - Now, 0, 46 * 3600);
            var land = BlockCalls.LandingSite(LandAsk(input), Snapshot(), Mun());
            Assert.Equal("", land["error"]);
            Assert.Equal(site.GetProperty("latitude").GetDouble(), (double)land["lat"], 3);
            Assert.Equal(site.GetProperty("longitude").GetDouble(), (double)land["lng"], 3);
            Assert.Equal(site.GetProperty("predicted_dv").GetDouble(), (double)land["predicted_dv"], 0);
        }

        [Fact]
        public void A_slow_game_gets_a_partial_answer_saying_how_far_the_track_was_searched_not_an_error()
        {
            // 10 frames a second: the reads take four times as long, so the search stops at its deadline and says how far it got.
            var mun = Mun();
            mun.FrameMs = 100;
            var result = Run("{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":48,\"max_slope_deg\":10}", mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            var searched = result.GetProperty("searched_hours").GetDouble();
            Assert.InRange(searched, 0.1, result.GetProperty("window_hours").GetDouble() - 0.1);
            Assert.StartsWith("partial: out of time (3.5 s): the ground track was searched for the first ", Note(result));
            Assert.EndsWith("no site met the limits there; land given this biome and these limits and window searches the whole window, so it may still find a site later in it", Note(result));
            Assert.All(Sites(result), s => Assert.InRange(s.GetProperty("pass_ut").GetDouble() - Now, 0, searched * 3600));
        }

        [Fact]
        public void Lands_block_call_searches_the_whole_window_however_slow_the_game()
        {
            // On the main thread kOS waits for it; it flies what the whole window gives, so it has no deadline.
            var input = "{\"body\":\"Mun\",\"biome\":\"East Crater\",\"within_hours\":48,\"max_slope_deg\":10}";
            var slow = Mun();
            slow.FrameMs = 100;
            var land = BlockCalls.LandingSite(LandAsk(input), Snapshot(), slow);
            var fast = BlockCalls.LandingSite(LandAsk(input), Snapshot(), Mun());
            Assert.Equal("", land["error"]);
            Assert.Equal(fast["lat"], land["lat"]);
            Assert.Equal(fast["lng"], land["lng"]);
            Assert.True(slow.ElapsedMs > TimeoutMs, slow.Line());
        }
    }
}
