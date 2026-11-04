// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>reentry's predicted-against-actual landing distance (#478): the great-circle km on the body.</summary>
    public class PredictionMissTests
    {
        private const double Kerbin = 600000;

        private static double Km(double lat, double lng, double actualLat, double actualLng, double radius = Kerbin)
        {
            var answer = PredictionMiss.Call(new Dictionary<string, object>
            {
                { "lat", lat }, { "lng", lng }, { "actual_lat", actualLat }, { "actual_lng", actualLng }, { "radius", radius },
            });
            return (double)answer["km"];
        }

        [Fact]
        public void The_same_point_is_no_miss()
        {
            Assert.Equal(0, Km(-0.0972, -74.5577, -0.0972, -74.5577), 6);
        }

        [Fact]
        public void A_degree_of_longitude_on_the_equator_is_a_degree_of_arc()
        {
            Assert.Equal(Math.PI / 180 * Kerbin / 1000, Km(0, -74, 0, -75), 6);
        }

        [Fact]
        public void A_pole_to_pole_miss_is_half_the_circumference_and_the_body_radius_scales_it()
        {
            Assert.Equal(Math.PI * Kerbin / 1000, Km(90, 0, -90, 0), 6);
            Assert.Equal(Math.PI * 261600 / 1000, Km(90, 0, -90, 0, 261600), 6);
        }

        [Fact]
        public void Longitude_wraps_across_the_date_line()
        {
            Assert.Equal(2 * Math.PI / 180 * Kerbin / 1000, Km(0, 179, 0, -179), 6);
        }
    }
}
