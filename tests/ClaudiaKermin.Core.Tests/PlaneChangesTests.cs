// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;
using Xunit.Abstractions;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <c>find_landing_site</c>'s plane changes (#521, ADR 0011): ready-made <c>change_orbit</c> set_inc calls, ranked by the plane
    /// change plus the landing <c>land</c> plans on the orbit after it, on the Mun with a fake body whose terrain is a function of
    /// where the track is.
    /// </summary>
    public sealed class PlaneChangesTests
    {
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;
        private const double MunTurn = 138984.38;
        private const double MPerDeg = MunR * Math.PI / 180;
        private const double Now = 1000;

        private static readonly SnapshotBody Mun = new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, RotationPeriod = MunTurn, SoiRadius = 2e6 };

        private readonly ITestOutputHelper _out;

        public PlaneChangesTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static BrakeCraft Lander()
        {
            return new BrakeCraft { ThrustN = 60000, MassKg = 5400, Isp = 345, LagS = 1 };
        }

        private sealed class Terrain : PlanningBodyBase
        {
            public Func<double, double, double> At = (lat, lng) => 0;

            public override double TerrainAltitude(string body, double latitude, double longitude) { return At(latitude, longitude); }
            public override string Biome(string body, double latitude, double longitude) { return "Midlands"; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 45; }

            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                var r = Math.Sqrt(x * x + y * y + z * z);
                latitude = Math.Asin(z / r) * 180 / Math.PI;
                longitude = Math.Atan2(y, x) * 180 / Math.PI;
                return true;
            }
        }

        /// <summary>land's ask over a site from a 30 km Mun orbit, over a full window (a turn of the Mun, 38.6 h).</summary>
        private static LandingPassAsk Ask(double lat, double lng)
        {
            return new LandingPassAsk
            {
                Latitude = lat, Longitude = lng, PeRadius = MunR + LandingPass.ApproachM, EarliestUt = Now + LandingPass.LeadS,
                LatestUt = Now + MunTurn, Daylight = false, MaxCorrectionDv = 30, Craft = Lander(), MoveSite = true,
            };
        }

        private static Conic Orbit(double inc)
        {
            return Conic.FromElements(MunGm, MunR + 30000, 0, inc, 0, 0, 0, Now);
        }

        private void Show(PlaneChangeResult found)
        {
            _out.WriteLine(found.Planes + " planes, " + found.Ms.ToString("0") + " ms, note " + found.Note);
            foreach (var o in found.Options) _out.WriteLine(o.Line(Now) + " heading " + o.Heading.ToString("0") + " shape " + o.Shape);
        }

        [Fact]
        public void A_site_above_the_orbits_inclination_gets_cheapest_and_soonest_change_orbit_calls_that_reach_it()
        {
            var ask = Ask(20, 30);
            var orbit = Orbit(10);
            Assert.NotNull(LandingPass.Find(Mun, orbit, Now, ask, new Terrain()).Error);
            var found = PlaneChanges.Find(Mun, orbit, Now, ask, new Terrain(), double.NaN, false);
            Show(found);
            var cheapest = found.Options.Single(o => o.Option == "cheapest");
            var soonest = found.Options.Single(o => o.Option == "soonest");
            foreach (var option in new[] { cheapest, soonest })
            {
                Assert.True(option.PlaneDv > 0 && option.LandingDv > 0);
                Assert.Equal(option.PlaneDv + option.LandingDv, option.TotalDv, 6);
                Assert.StartsWith("change_orbit(lexicon(\"goal\", \"set_inc\", \"value\", ", option.Call);
                // The call, solved as change_orbit solves it, gives an orbit that passes over the site in the window.
                var burn = BlockCalls.OrbitBurn(new Dictionary<string, object> { { "goal", "set_inc" }, { "value", option.Inclination }, { "at", option.BurnUt } }, Snapshot(10));
                Assert.Equal("", burn["error"]);
                Assert.Equal(option.PlaneDv, (double)burn["dv"], 0);
                var pass = LandingPass.Find(Mun, option.After, Now, Without(ask, option.BurnUt), new Terrain());
                Assert.Null(pass.Error);
                Assert.True(pass.ArriveUt <= ask.LatestUt);
                Assert.Equal(option.Inclination, option.After.Inclination, 1);
            }
            Assert.True(cheapest.TotalDv <= soonest.TotalDv);
            Assert.True(soonest.ArriveUt <= cheapest.ArriveUt);
        }

        /// <summary>
        /// A wall <paramref name="height"/> m high, 4 to 25 km from the site, on bearings within 3 degrees of <paramref name="bearing"/>
        /// from it: an approach on heading <paramref name="bearing"/> - 180 crosses it; one 4 degrees off misses it.
        /// </summary>
        private static Func<double, double, double> Wall(double siteLat, double siteLng, double bearing, double height)
        {
            return (lat, lng) =>
            {
                var d = LandingSiteSearch.ArcDegrees(lat, lng, siteLat, siteLng) * MPerDeg;
                if (d < 4000 || d > 25000) return 0;
                var from = Math.Atan2((lng - siteLng) * Math.Cos(siteLat * Math.PI / 180), lat - siteLat) * 180 / Math.PI;
                return Math.Abs(LandingSiteSearch.Wrap180(from - bearing)) < 3 ? height : 0;
            };
        }

        [Theory]
        [InlineData(8000, true)]
        [InlineData(1000, false)]
        public void A_reachable_site_whose_approach_crosses_a_wall_gets_a_saving_plane_change_only_when_it_saves_20_m_s_net(double height, bool offered)
        {
            // An equatorial orbit approaches (0, 30) from the west, across the wall. 8 km of wall costs the descent about 150 m/s
            // over flat ground; a 4 degree plane change (about 37 m/s) misses it. 1 km costs about 30 m/s, less than any plane
            // change that misses it.
            var terrain = new Terrain { At = Wall(0, 30, 270, height) };
            var ask = Ask(0, 30);
            ask.MoveSite = false;
            var orbit = Orbit(0);
            var current = LandingPass.Find(Mun, orbit, Now, ask, terrain);
            Assert.Null(current.Error);
            var found = PlaneChanges.Find(Mun, orbit, Now, ask, terrain, current.LandingDv, current.Moved);
            Show(found);
            _out.WriteLine("current " + current.LandingDv.ToString("0"));
            if (!offered)
            {
                Assert.Empty(found.Options);
                return;
            }
            var saving = found.Options.Single();
            Assert.Equal("saving", saving.Option);
            Assert.True(current.LandingDv - saving.TotalDv >= PlaneChanges.SavingDv, saving.TotalDv + " against " + current.LandingDv);
            Assert.True(Math.Abs(LandingSiteSearch.Wrap180(saving.Heading - 90)) > 3, "heading " + saving.Heading);
        }

        [Fact]
        public void No_plane_change_offered_has_an_approach_that_fails_the_clearance_check()
        {
            // The cheapest plane change's approach to (20, 30) from the 10 degree orbit runs about 85 degrees: a wall no shape
            // clears on it, so the plane changes offered come in on other headings, and each clears when land plans it again.
            var plain = PlaneChanges.Find(Mun, Orbit(10), Now, Ask(20, 30), new Terrain(), double.NaN, false);
            var blocked = plain.Options.First().Heading;
            var terrain = new Terrain { At = Wall(20, 30, blocked + 180, 12000) };
            var ask = Ask(20, 30);
            ask.MoveSite = false;
            var found = PlaneChanges.Find(Mun, Orbit(10), Now, ask, terrain, double.NaN, false);
            Show(found);
            Assert.NotEmpty(found.Options);
            foreach (var option in found.Options)
            {
                var land = Without(ask, option.BurnUt);
                land.Craft = Lander();
                land.LatestUt = option.ArriveUt + 60;
                var again = LandingPass.Find(Mun, option.After, Now, land, terrain);
                Assert.Null(again.Error);
                Assert.True(again.Descent.Result.Clears);
                Assert.True(again.Descent.Result.LeastSpare >= 0);
                Assert.True(Math.Abs(LandingSiteSearch.Wrap180(option.Heading - blocked)) > 3, option.Heading + " against " + blocked);
            }
        }

        [Fact]
        public void The_search_stays_within_about_2_s_on_the_Mun_with_a_full_window()
        {
            // A full window: a turn of the Mun, 38.6 h, rough ground, a site above the orbit's inclination.
            var terrain = new Terrain { At = (lat, lng) => 600 * Math.Sin(lat * 7) * Math.Cos(lng * 5) };
            Timing.Within(PlaneChanges.BudgetMs, () => Assert.NotEmpty(PlaneChanges.Find(Mun, Orbit(10), Now, Ask(20, 30), terrain, double.NaN, false).Options));
        }

        private static LandingPassAsk Without(LandingPassAsk ask, double burn)
        {
            return new LandingPassAsk
            {
                Latitude = ask.Latitude, Longitude = ask.Longitude, PeRadius = ask.PeRadius, EarliestUt = burn + LandingPass.LeadS, LatestUt = ask.LatestUt,
                Daylight = false, MaxCorrectionDv = ask.MaxCorrectionDv,
            };
        }

        private static GameSnapshot Snapshot(double inc)
        {
            var snapshot = new GameSnapshot { Ut = Now, Situation = "ORBITING", Body = "Mun" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = MunR + 30000, Eccentricity = 0, Inclination = inc, Lan = 0, ArgumentOfPeriapsis = 0,
                MeanAnomalyAtEpoch = 0, Epoch = Now, Body = "Mun", Periapsis = 30000, Apoapsis = 30000,
            };
            snapshot.Bodies.Add(Mun);
            return snapshot;
        }
    }
}
