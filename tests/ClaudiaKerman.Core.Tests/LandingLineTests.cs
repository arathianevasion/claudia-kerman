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
            snapshot.Bodies.Add(new SnapshotBody { Name = "Mun", Parent = "Kerbin", Radius = 200000 });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Minmus", Parent = "Kerbin", Radius = 60000 });
            snapshot.Bodies.Add(new SnapshotBody { Name = "Duna", Parent = "Sun", Radius = 320000 });
            return snapshot;
        }

        private static IDictionary<string, object> Geo(double lat, double lng)
        {
            return new Dictionary<string, object> { { "lat", lat }, { "lng", lng } };
        }

        private static IDictionary<string, object> Down(double lat, double lng)
        {
            return new Dictionary<string, object> { { "geo", Geo(lat, lng) }, { "touchdown_speed", 6.0 } };
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
            var line = LandingLine.Landed(record, Down(ReturnAim.KscLatitude, ReturnAim.KscLongitude + 2), On("Kerbin"));
            Assert.Equal("predicted 10 km from KSC, came down 21 km from KSC", line);
        }

        private static IDictionary<string, object> DownAt(double sun)
        {
            return new Dictionary<string, object> { { "geo", Geo(0, 0) }, { "touchdown_speed", 6.0 }, { "sun_elevation_deg", sun } };
        }

        [Fact]
        public void A_dark_landing_with_daylight_in_the_Mission_Settings_gets_a_line_with_the_figure()
        {
            Assert.Equal("the sun was -12.3 degrees up at the landing, not over 20, and the Mission Settings asked for daylight", LandingLine.Dark(DownAt(-12.3), true, "Land on Duna"));
        }

        [Fact]
        public void A_dark_landing_with_daylight_in_the_Mission_Goal_gets_a_line_with_settings_off()
        {
            Assert.Contains("the Mission Goal asked for daylight", LandingLine.Dark(DownAt(5), false, "Fly to Duna and land near the equator in Daylight"));
        }

        [Fact]
        public void A_landing_gets_no_dark_line_if_daylight_was_not_asked_for()
        {
            Assert.Equal("", LandingLine.Dark(DownAt(-12.3), false, "Land on Duna"));
            Assert.Equal("", LandingLine.Dark(DownAt(-12.3), false, null));
        }

        [Fact]
        public void A_landing_in_Daylight_gets_no_dark_line_and_20_degrees_is_not_Daylight()
        {
            Assert.Equal("", LandingLine.Dark(DownAt(20.1), true, "in daylight"));
            Assert.NotEqual("", LandingLine.Dark(DownAt(20), true, "in daylight"));
        }

        [Fact]
        public void A_reentry_that_did_not_come_down_or_reported_no_sun_gets_no_dark_line()
        {
            var aborted = new Dictionary<string, object> { { "touchdown_speed", -1.0 }, { "sun_elevation_deg", -30.0 } };
            Assert.Equal("", LandingLine.Dark(aborted, true, "in daylight"));
            Assert.Equal("", LandingLine.Dark(Down(0, 0), true, "in daylight"));
            Assert.Equal("", LandingLine.Dark(null, true, "in daylight"));
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
            var landed = LandingLine.Landed(record, Down(0, -74), On("Kerbin"));
            Assert.EndsWith("; " + note, landed);
            Assert.Contains("came down", landed);
        }

        [Fact]
        public void No_prediction_no_line()
        {
            Assert.Null(LandingLine.Record("return", new Dictionary<string, object> { { "predicted_geo", new Dictionary<string, object>() } }, On("Mun")));
            Assert.Null(LandingLine.Record("land", Aimed(1, 1), On("Mun")));
            Assert.Equal("", LandingLine.Landed("", Down(0, 0), On("Kerbin")));
        }

        [Fact]
        public void A_landing_elsewhere_or_with_no_place_read_gives_none()
        {
            var record = LandingLine.Record("return", Aimed(0, -74), On("Mun"));
            Assert.Equal("", LandingLine.Landed(record, Down(0, 0), On("Duna")));
            Assert.Equal("", LandingLine.Landed(record, new Dictionary<string, object> { { "geo", new Dictionary<string, object>() }, { "touchdown_speed", 6.0 } }, On("Kerbin")));
        }

        /// <summary>A deorbit at another planet aims there: no km from KSC. A reentry that never touched down reads as no landing, though its geo is set.</summary>
        [Fact]
        public void Elsewhere_or_not_down_gives_none()
        {
            Assert.Null(LandingLine.Record("deorbit", Aimed(0, -74), On("Duna")));
            var record = LandingLine.Record("return", Aimed(0, -74), On("Mun"));
            Assert.Equal("", LandingLine.Landed(record, new Dictionary<string, object> { { "geo", Geo(0, -74) }, { "touchdown_speed", -1.0 } }, On("Kerbin")));
        }

        /// <summary>The prediction lives in the Mission's save for a reentry in a later run.</summary>
        [Fact]
        public void The_prediction_survives_a_save()
        {
            Assert.Equal("3|fix flown", MissionSaveState.FromValues(new MissionSaveState { Id = "m1", LandingAim = "3|fix flown" }.ToValues()).LandingAim);
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
