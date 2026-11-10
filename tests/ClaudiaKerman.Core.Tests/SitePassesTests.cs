// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#974's fix round: which of find_landing_site's recorded passes <c>deorbit</c> follows for a target.</summary>
    public class SitePassesTests
    {
        private const double Duna = 320000;

        private static LandingSite Site(double lat, double lng, double landingUt, double sun = 60)
        {
            return new LandingSite { Latitude = lat, Longitude = lng, PassUt = landingUt - 1200, LandingUt = landingUt, SunElevationDeg = sun };
        }

        [Fact]
        public void A_target_typed_to_two_places_is_the_site_printed_to_three()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", new[] { Site(12.345, -45.678, 5000) });
            var pass = passes.Of("v1", "Duna", Duna, 12.35, -45.68, 5);
            Assert.NotNull(pass);
            Assert.Equal(5000, pass.LandingUt);
        }

        [Fact]
        public void A_target_further_than_accuracy_km_from_every_site_is_none()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", new[] { Site(12.345, -45.678, 5000) });
            // 1 degree of latitude on Duna is about 5.6 km.
            Assert.Null(passes.Of("v1", "Duna", Duna, 13.345, -45.678, 5));
            Assert.NotNull(passes.Of("v1", "Duna", Duna, 13.345, -45.678, 6));
            Assert.Null(passes.Of("v1", "Kerbin", 600000, 12.345, -45.678, 5));
        }

        [Fact]
        public void A_site_from_an_earlier_call_still_matches_after_a_later_one()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", new[] { Site(10, 20, 5000) });
            passes.Record("v1", "Duna", new[] { Site(-30, 100, 9000) });
            Assert.Equal(5000, passes.Of("v1", "Duna", Duna, 10, 20, 5).LandingUt);
            Assert.Equal(9000, passes.Of("v1", "Duna", Duna, -30, 100, 5).LandingUt);
        }

        [Fact]
        public void Of_two_calls_giving_the_same_site_the_latest_wins()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", new[] { Site(10, 20, 5000) });
            passes.Record("v1", "Duna", new[] { Site(10.001, 20, 7000) });
            Assert.Equal(7000, passes.Of("v1", "Duna", Duna, 10, 20, 5).LandingUt);
        }

        [Fact]
        public void A_lander_decoupled_from_the_vessel_the_tool_ran_for_still_matches_the_vessels_own_answer_first()
        {
            var passes = new SitePasses();
            passes.Record("mothership", "Duna", new[] { Site(10, 20, 5000) });
            Assert.Equal(5000, passes.Of("lander", "Duna", Duna, 10, 20, 5).LandingUt);

            passes.Record("lander", "Duna", new[] { Site(10, 20, 6000) });
            passes.Record("other", "Duna", new[] { Site(10, 20, 8000) });
            Assert.Equal(6000, passes.Of("lander", "Duna", Duna, 10, 20, 5).LandingUt);
        }

        [Fact]
        public void Only_the_latest_calls_are_kept_and_a_game_load_forgets_them_all()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", new[] { Site(10, 20, 5000) });
            for (var i = 0; i < SitePasses.MaxCalls; i++) passes.Record("v1", "Duna", new[] { Site(-40, i * 10, 9000 + i) });
            Assert.Null(passes.Of("v1", "Duna", Duna, 10, 20, 5));
            Assert.NotNull(passes.Of("v1", "Duna", Duna, -40, 0, 5));

            passes.Clear();
            Assert.Null(passes.Of("v1", "Duna", Duna, -40, 0, 5));
        }
    }
}
