// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#974's fix round: which of find_landing_site's recorded passes <c>deorbit</c> follows for a target.</summary>
    public class SitePassesTests
    {
        private const double DunaRadiusM = 320000;

        private static LandingSite Site(double lat, double lng, double landingUt, double sun = 60)
        {
            return new LandingSite { Latitude = lat, Longitude = lng, PassUt = landingUt - 1200, LandingUt = landingUt, SunElevationDeg = sun };
        }

        [Fact]
        public void A_target_typed_to_two_places_is_the_site_printed_to_three()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(12.345, -45.678, 5000) });
            var pass = passes.Of("v1", "Duna", DunaRadiusM, 12.35, -45.68, 5);
            Assert.NotNull(pass);
            Assert.Equal(5000, pass.LandingUt);
        }

        [Fact]
        public void A_target_further_than_accuracy_km_from_every_site_is_none()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(12.345, -45.678, 5000) });
            // 1 degree of latitude on Duna is about 5.6 km.
            Assert.Null(passes.Of("v1", "Duna", DunaRadiusM, 13.345, -45.678, 5));
            Assert.NotNull(passes.Of("v1", "Duna", DunaRadiusM, 13.345, -45.678, 6));
            Assert.Null(passes.Of("v1", "Kerbin", 600000, 12.345, -45.678, 5));
        }

        [Fact]
        public void A_site_from_an_earlier_call_still_matches_after_a_later_one()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(10, 20, 5000) });
            passes.Record("v1", "Duna", 200, new[] { Site(-30, 100, 9000) });
            Assert.Equal(5000, passes.Of("v1", "Duna", DunaRadiusM, 10, 20, 5).LandingUt);
            Assert.Equal(9000, passes.Of("v1", "Duna", DunaRadiusM, -30, 100, 5).LandingUt);
        }

        [Fact]
        public void The_site_itself_wins_over_a_later_calls_site_a_few_km_off_and_of_the_same_site_the_latest_wins()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(10, 20, 5000) });
            // About 3 km north, inside the default accuracy_km.
            passes.Record("v1", "Duna", 200, new[] { Site(10.54, 20, 7000) });
            Assert.Equal(5000, passes.Of("v1", "Duna", DunaRadiusM, 10, 20, 5).LandingUt);

            passes.Record("v1", "Duna", 300, new[] { Site(10.001, 20, 8000) });
            Assert.Equal(8000, passes.Of("v1", "Duna", DunaRadiusM, 10, 20, 5).LandingUt);
        }

        [Fact]
        public void A_lander_decoupled_from_the_vessel_the_tool_ran_for_still_matches_the_vessels_own_answer_first()
        {
            var passes = new SitePasses();
            passes.Record("mothership", "Duna", 100, new[] { Site(10, 20, 5000) });
            Assert.Equal(5000, passes.Of("lander", "Duna", DunaRadiusM, 10, 20, 5).LandingUt);

            passes.Record("lander", "Duna", 200, new[] { Site(10, 20, 6000) });
            passes.Record("other", "Duna", 300, new[] { Site(10, 20, 8000) });
            Assert.Equal(6000, passes.Of("lander", "Duna", DunaRadiusM, 10, 20, 5).LandingUt);
        }

        [Fact]
        public void Only_the_latest_calls_are_kept()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(10, 20, 5000) });
            for (var i = 0; i < SitePasses.MaxCalls; i++) passes.Record("v1", "Duna", 200 + i, new[] { Site(-40, i * 10, 9000 + i) });
            Assert.Null(passes.Of("v1", "Duna", DunaRadiusM, 10, 20, 5));
            Assert.NotNull(passes.Of("v1", "Duna", DunaRadiusM, -40, 0, 5));
        }

        [Fact]
        public void A_flight_resuming_before_an_answer_forgets_it_and_keeps_the_answers_before()
        {
            var passes = new SitePasses();
            passes.Record("v1", "Duna", 100, new[] { Site(10, 20, 5000) });
            passes.Record("v1", "Duna", 300, new[] { Site(-30, 100, 9000) });

            // A trip to the Space Center and back: the flight resumes later, both kept.
            passes.ForgetAfter(400);
            Assert.NotNull(passes.Of("v1", "Duna", DunaRadiusM, -30, 100, 5));

            // A quickload to a save made between the two answers.
            passes.ForgetAfter(200);
            Assert.Null(passes.Of("v1", "Duna", DunaRadiusM, -30, 100, 5));
            Assert.NotNull(passes.Of("v1", "Duna", DunaRadiusM, 10, 20, 5));
        }
    }
}
