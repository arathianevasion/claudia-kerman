// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#790: the Debrief's one line on a landing aimed at Kerbin, predicted and actual km from KSC, with the way a miss across the track went.</summary>
    public sealed class LandingLineTests
    {
        private static GameSnapshot On(string body)
        {
            var snapshot = new GameSnapshot { Ut = 1, Body = body };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Kerbin", Radius = 600000 });
            return snapshot;
        }

        private static IDictionary<string, object> Geo(double lat, double lng)
        {
            return new Dictionary<string, object> { { "lat", lat }, { "lng", lng } };
        }

        private static IDictionary<string, object> Aimed(double lat, double lng, string note = "")
        {
            return new Dictionary<string, object> { { "predicted_geo", Geo(lat, lng) }, { "aim_note", note } };
        }

        /// <summary>1 degree of Kerbin is 10.47 km.</summary>
        [Fact]
        public void The_line_gives_predicted_and_actual_km_from_ksc()
        {
            var record = LandingLine.Record("return", Aimed(ReturnAim.KscLatitude + 1, ReturnAim.KscLongitude), On("Mun"));
            var line = LandingLine.Landed(record, new Dictionary<string, object> { { "geo", Geo(ReturnAim.KscLatitude, ReturnAim.KscLongitude + 2) } }, On("Kerbin"));
            Assert.Equal("predicted 10 km from KSC, came down 21 km from KSC", line);
        }

        [Fact]
        public void A_prediction_alone_says_so_before_the_craft_is_down()
        {
            var record = LandingLine.Record("deorbit", Aimed(ReturnAim.KscLatitude, ReturnAim.KscLongitude), On("Kerbin"));
            Assert.Equal("predicted 0.0 km from KSC", LandingLine.Predicted(record));
        }

        [Theory]
        [InlineData("fix flown: 40.2 m/s, 62 km down to 0.0 km")]
        [InlineData("miss accepted: 62 km from the target; the fix costs 40.2 m/s, over the 12 m/s left above the reserve")]
        public void The_line_says_which_way_a_miss_across_the_track_went_with_the_m_s(string note)
        {
            var record = LandingLine.Record("return", Aimed(ReturnAim.KscLatitude, ReturnAim.KscLongitude, note), On("Minmus"));
            Assert.EndsWith("; " + note, LandingLine.Predicted(record));
            var landed = LandingLine.Landed(record, new Dictionary<string, object> { { "geo", Geo(0, -74) } }, On("Kerbin"));
            Assert.EndsWith("; " + note, landed);
            Assert.Contains("came down", landed);
        }

        [Fact]
        public void No_prediction_no_line()
        {
            Assert.Null(LandingLine.Record("return", new Dictionary<string, object> { { "predicted_geo", new Dictionary<string, object>() } }, On("Mun")));
            Assert.Null(LandingLine.Record("land", Aimed(1, 1), On("Mun")));
            Assert.Equal("", LandingLine.Landed("", new Dictionary<string, object> { { "geo", Geo(0, 0) } }, On("Kerbin")));
        }

        [Fact]
        public void A_landing_elsewhere_or_with_no_place_read_gives_none()
        {
            var record = LandingLine.Record("return", Aimed(0, -74), On("Mun"));
            Assert.Equal("", LandingLine.Landed(record, new Dictionary<string, object> { { "geo", Geo(0, 0) } }, On("Duna")));
            Assert.Equal("", LandingLine.Landed(record, new Dictionary<string, object> { { "geo", new Dictionary<string, object>() } }, On("Kerbin")));
        }

        [Fact]
        public void The_run_keeps_one_landing_line()
        {
            var log = new RunEventLog();
            log.Replace(1, RunEventKind.Landing, "predicted 3 km from KSC");
            log.Add(2, RunEventKind.Staging, "stage 1 fired");
            log.Replace(3, RunEventKind.Landing, "predicted 3 km from KSC, came down 5 km from KSC");
            var lines = log.EventLines(0);
            Assert.Equal(2, lines.Count);
            Assert.Contains("+3 s landing: predicted 3 km from KSC, came down 5 km from KSC", lines);
        }
    }
}
