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
    /// <c>find_landing_site</c> for a biome off the ground track (#545), on mission 3dc5's orbit: Mun Shot v2 in a 30 km Mun orbit
    /// at inclination 1.31 at UT 196754.95, from that turn's Game Snapshot, asked for Twin Craters (about 23 degrees south), which no
    /// pass reaches. The fake Mun is f623's (<see cref="FindLandingSiteMunShotTests.GameMun"/>: rolling terrain, a sun, the game's
    /// round trips counted) with Twin Craters where 3dc5's later sites were (-23.5, 136.3 and -17.2, 134.5).
    /// </summary>
    public sealed class FindLandingSiteOffTrackTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunTurn = 138984.38;
        private const double Now = 196754.95489835105;
        private const string TwinCraters = "{\"body\":\"Mun\",\"biome\":\"Twin Craters\"}";

        private static readonly double TimeoutMs = ToolStep.CallTimeout.TotalMilliseconds;

        private readonly ITestOutputHelper _out;
        private readonly PlanningTools _tools = PlanningTools.Default();

        public FindLandingSiteOffTrackTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string Biome(double latitude, double longitude)
        {
            return latitude > -29 && latitude < -17 && longitude > 126 && longitude < 146 ? "TwinCraters" : "Highlands";
        }

        /// <summary>The fake Mun with its frame as at <paramref name="ut"/>: the body turned on from the snapshot's.</summary>
        private static FindLandingSiteMunShotTests.GameMun Mun(double ut = Now)
        {
            return new FindLandingSiteMunShotTests.GameMun { BiomeAt = Biome, LonOffset = LandingSiteSearch.Wrap180(-360 / MunTurn * (ut - Now)) };
        }

        private static Conic Orbit()
        {
            return Conic.FromElements(MunGm, 230174.91108860678, 0.0035664398854182884, 1.3113898817980065, 1.5467283768627453, 208.44225448839833,
                4.6112452693837236, Now);
        }

        private static GameSnapshot Snapshot()
        {
            var snapshot = new GameSnapshot
            {
                Ut = Now, Situation = "ORBITING", Body = "Mun", DeltaVSimulator = true, DaylightLandingDefault = true, TimeCapHours = 6,
            };
            snapshot.Craft.Name = "Mun Shot v2";
            snapshot.Craft.MassTonnes = 11.348;
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 611.9, DeltaVAsl = 611.9, ThrustVacKn = 215, StartMassT = 11.348, EndMassT = 9.337 });
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = 2296.8, DeltaVAsl = 2296.8, ThrustVacKn = 60, StartMassT = 6.087, EndMassT = 3.087 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, StartMassT = 1.415, EndMassT = 1.415 });
            snapshot.Stages.Add(new SnapshotStage { Number = 0, StartMassT = 1.415, EndMassT = 1.415 });
            snapshot.Orbit = new SnapshotOrbit
            {
                Apoapsis = 30995.816072135785, Periapsis = 29354.006105077773, Inclination = 1.3113898817980065, Eccentricity = 0.0035664398854182884,
                Period = 2718.6187593575623, SemiMajorAxis = 230174.91108860678, Lan = 1.5467283768627453, ArgumentOfPeriapsis = 208.44225448839833,
                MeanAnomalyAtEpoch = 4.6112452693837236, Epoch = Now, Body = "Mun",
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = 3.5316e12, Radius = 600000, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256,
            });
            snapshot.Bodies.Add(MunBody);
            return snapshot;
        }

        private static readonly SnapshotBody MunBody = new SnapshotBody
        {
            Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = MunTurn, Sma = 12000000,
        };

        private JsonElement Run(string input, FindLandingSiteMunShotTests.GameMun mun)
        {
            var watch = Stopwatch.StartNew();
            var outcome = _tools.Run("find_landing_site", input, Snapshot(), mun);
            _out.WriteLine(input + ": " + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms of work here, " + mun.Line());
            _out.WriteLine(string.Join(" ", mun.Asks.GroupBy(a => a).Select(g => g.Key + " x" + g.Count())));
            _out.WriteLine(outcome.Summary);
            _out.WriteLine(outcome.Json);
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        /// <summary>#543: the same Mission time cap gives the tool's default window and land's auto window (the pass search's) one length.</summary>
        [Theory]
        [InlineData(6.0, 38.61)]
        [InlineData(40.0, 40.0)]
        [InlineData(60.0, 48.0)]
        public void The_tools_default_window_and_lands_auto_window_agree_for_a_short_and_a_long_time_cap(double cap, double expected)
        {
            var snapshot = Snapshot();
            snapshot.TimeCapHours = cap;
            var tool = _tools.Run("find_landing_site", TwinCraters, snapshot, Mun());
            Assert.False(tool.IsError, tool.Json);
            double toolWindow;
            using (var doc = JsonDocument.Parse(tool.Json)) toolWindow = doc.RootElement.GetProperty("window_hours").GetDouble();

            var ask = new Dictionary<string, object> { { "lat", 0.0 }, { "lng", 0.0 }, { "r_pe", 200000.0 + LandingPass.ApproachM }, { "daylight", false } };
            var land = BlockCalls.LandingPass(ask, snapshot, Mun());
            var landWindow = Convert.ToDouble(land["window_hours"]);

            Assert.Equal(expected, toolWindow, 2);
            Assert.Equal(toolWindow, landWindow, 2);
        }

        [Fact]
        public void Twin_craters_off_the_track_gets_cheapest_and_soonest_plane_changes_inside_the_tool_timeout()
        {
            var mun = Mun();
            var result = Run(TwinCraters, mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            Assert.Empty(result.GetProperty("sites").EnumerateArray());
            Assert.Contains("plane_changes are for a site in the biome off the track", result.GetProperty("note").GetString());
            Assert.False(result.TryGetProperty("plane_change_note", out _), "no note: neither out of time nor none found");

            var site = result.GetProperty("plane_change_site");
            var lat = site.GetProperty("latitude").GetDouble();
            Assert.Equal("TwinCraters", Biome(lat, site.GetProperty("longitude").GetDouble()));

            var changes = result.GetProperty("plane_changes").EnumerateArray().ToList();
            var options = changes.Select(c => c.GetProperty("option").GetString()).ToList();
            Assert.True(options.SequenceEqual(new[] { "cheapest", "soonest" }) || options.SequenceEqual(new[] { "cheapest and soonest" }), string.Join(", ", options));
            foreach (var change in changes)
            {
                Assert.Equal("set_inc", change.GetProperty("goal").GetString());
                Assert.StartsWith("change_orbit(lexicon(\"goal\", \"set_inc\", \"value\", ", change.GetProperty("call").GetString());
                Assert.Equal(change.GetProperty("plane_change_dv").GetDouble() + change.GetProperty("landing_dv").GetDouble(), change.GetProperty("total_dv").GetDouble(), 0);
                // About the site's latitude, not 3dc5's 45 degrees (394.6 m/s).
                Assert.InRange(change.GetProperty("value").GetDouble(), Math.Abs(lat) - 2, 45);
                Assert.True(change.GetProperty("plane_change_dv").GetDouble() < 394, change.ToString());
                Assert.InRange(change.GetProperty("landing_ut").GetDouble() - Now, 0, LandingPass.ReachSeconds(38.61, true, MunTurn));
            }
            Assert.True(changes.First().GetProperty("total_dv").GetDouble() <= changes.Last().GetProperty("total_dv").GetDouble());
            Assert.True(changes.Last().GetProperty("landing_ut").GetDouble() <= changes.First().GetProperty("landing_ut").GetDouble());
        }

        [Fact]
        public void Land_finds_no_twin_craters_site_on_the_orbit_as_it_is_and_finds_one_after_each_plane_change()
        {
            var result = Run(TwinCraters, Mun());
            Assert.Contains("so it finds none either", result.GetProperty("note").GetString());
            var land = BlockCalls.LandingSite(new Dictionary<string, object> { { "biome", "Twin Craters" }, { "daylight", true }, { "max_correction_dv", 30.0 } }, Snapshot(), Mun());
            _out.WriteLine("land: " + land["error"]);
            Assert.StartsWith("no site in the Twin Craters biome", (string)land["error"]);

            var site = result.GetProperty("plane_change_site");
            var lat = site.GetProperty("latitude").GetDouble();
            var lng = site.GetProperty("longitude").GetDouble();
            Assert.NotEmpty(result.GetProperty("plane_changes").EnumerateArray());
            foreach (var change in result.GetProperty("plane_changes").EnumerateArray())
            {
                // The orbit after the call, as change_orbit solves it, and land's biome search on it with the site as near, from a
                // little after the burn: what land(target biome Twin Craters, near the site) does once the plane change is flown.
                var burnUt = change.GetProperty("burn_ut").GetDouble();
                var burn = BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", "set_inc" }, { "value", change.GetProperty("value").GetDouble() }, { "at", burnUt } }, Snapshot());
                Assert.Equal("", burn["error"]);
                Vec3 r, v;
                Orbit().State((double)burn["ut"], out r, out v);
                string why;
                var dv = OrbitChange.ChangeInclination(r, v, change.GetProperty("value").GetDouble(), out why);
                Assert.True(dv.HasValue, why);
                var after = Conic.FromState(MunGm, r, v + dv.Value, (double)burn["ut"]);
                var from = (double)burn["ut"] + 60;
                var query = new LandingSiteQuery
                {
                    Biome = "Twin Craters", Daylight = true, MaxSlopeDeg = 5, WithinHours = LandingPass.WindowHours(null, 6, MunTurn), MaxCorrectionDv = 30,
                    Craft = DescentApproach.FromSnapshot(Snapshot()), HasNear = true, NearLatitude = lat, NearLongitude = lng,
                };
                var found = LandingSiteSearch.Find(MunBody, after, from, query, Mun(from));
                Assert.Null(found.Error);
                Assert.NotEmpty(found.Sites);
                Assert.Equal("TwinCraters", found.Sites[0].Biome);
                _out.WriteLine(change.GetProperty("option").GetString() + ": land finds " + found.Sites[0].Latitude.ToString("0.00") + ", " + found.Sites[0].Longitude.ToString("0.00")
                    + " for " + found.Sites[0].PredictedDv.ToString("0") + " m/s against " + change.GetProperty("landing_dv").GetDouble() + " proposed");
            }
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void The_calls_own_work_is_small_beside_the_games()
        {
            // As f623's default call (FindLandingSiteMunShotTests): the search's own work, the plane changes' most of it, leaves the
            // timeout to the game's reads. Before #545 the plane changes' scan alone took about 580 ms here, mostly on planes that
            // never come within reach of the site's latitude.
            Timing.Within(500, () => _tools.Run("find_landing_site", TwinCraters, Snapshot(), Mun()));
        }

        [Theory]
        [InlineData(33, false)]
        [InlineData(40, true)]
        public void A_slow_game_still_gets_plane_changes_said_to_be_partial_not_none(double frameMs, bool coarse)
        {
            // 30 frames a second: the deadline passes while the plane changes are planned again on every shape, so the answer is
            // those done by then. 25: it passes before any is, so the coarse plans stand in (3dc5 gave none: "out of time before a
            // plane change was worked out").
            var mun = Mun();
            mun.FrameMs = frameMs;
            var result = Run(TwinCraters, mun);
            Assert.True(mun.ElapsedMs <= TimeoutMs, mun.Line());
            Assert.NotEmpty(result.GetProperty("plane_changes").EnumerateArray());
            var note = result.GetProperty("plane_change_note").GetString();
            Assert.StartsWith("partial: out of time, so these are the best of the ", note);
            Assert.Equal(coarse, note.Contains(", landing_dv from a coarse plan of the descent; ask again with near set to the site to plan it fully"));
        }

        [Fact]
        public void A_biome_nowhere_on_the_body_says_so_instead_of_proposing_plane_changes()
        {
            var result = Run("{\"body\":\"Mun\",\"biome\":\"Farside Basin\"}", Mun());
            Assert.Empty(result.GetProperty("sites").EnumerateArray());
            Assert.Contains("a survey of Mun every 4 degrees found no point in the biome gentle enough to plan plane changes for (give near for a smaller biome)", result.GetProperty("note").GetString());
            Assert.False(result.TryGetProperty("plane_changes", out _));
        }
    }
}
