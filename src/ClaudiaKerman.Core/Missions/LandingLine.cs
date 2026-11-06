// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The Debrief's line on a landing aimed at Kerbin (#790): km from KSC predicted, by the <c>return</c> or <c>deorbit</c> that aimed it, and actual, by the <c>reentry</c> that came down, with
    /// which way a miss across the track went. The two may fall in different runs, so the prediction is kept on the Mission as a <see cref="Record"/>. Pure.
    /// </summary>
    public static class LandingLine
    {
        private const char Separator = '|';

        /// <summary>The prediction a return or deorbit's result gives, to keep on the Mission; null when it gave none or it isn't for Kerbin.</summary>
        public static string Record(string block, IDictionary<string, object> result, GameSnapshot snapshot)
        {
            if (block != "return" && block != "deorbit") return null;
            var km = KmFromKsc(Geo(result, "predicted_geo"), Home(snapshot));
            if (!km.HasValue) return null;
            return km.Value.ToString("R", CultureInfo.InvariantCulture) + Separator + Text(result, "aim_note");
        }

        /// <summary>The line for a prediction alone, with the craft still in flight.</summary>
        public static string Predicted(string record)
        {
            double km;
            string note;
            if (!Read(record, out km, out note)) return "";
            return "predicted " + Km(km) + " km from KSC" + (note.Length > 0 ? "; " + note : "");
        }

        /// <summary>The line once a reentry's <paramref name="result"/> has come down on Kerbin, with <paramref name="record"/>'s prediction; "" with no prediction, or no landing there.</summary>
        public static string Landed(string record, IDictionary<string, object> result, GameSnapshot snapshot)
        {
            double predicted;
            string note;
            if (!Read(record, out predicted, out note)) return "";
            var home = Home(snapshot);
            if (home == null || !string.Equals(snapshot.Body, home.Name, StringComparison.OrdinalIgnoreCase)) return "";
            var actual = KmFromKsc(Geo(result, "geo"), home);
            if (!actual.HasValue) return "";
            return "predicted " + Km(predicted) + " km from KSC, came down " + Km(actual.Value) + " km from KSC" + (note.Length > 0 ? "; " + note : "");
        }

        private static bool Read(string record, out double km, out string note)
        {
            km = 0;
            note = "";
            if (string.IsNullOrEmpty(record)) return false;
            var at = record.IndexOf(Separator);
            if (at < 0 || !double.TryParse(record.Substring(0, at), NumberStyles.Float, CultureInfo.InvariantCulture, out km)) return false;
            note = record.Substring(at + 1);
            return true;
        }

        private static SnapshotBody Home(GameSnapshot snapshot)
        {
            return snapshot == null ? null : snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase));
        }

        private static double? KmFromKsc(double[] geo, SnapshotBody home)
        {
            if (geo == null || home == null) return null;
            return LandingSiteSearch.ArcDegrees(geo[0], geo[1], ReturnAim.KscLatitude, ReturnAim.KscLongitude) * Math.PI / 180 * home.Radius / 1000;
        }

        private static double[] Geo(IDictionary<string, object> result, string key)
        {
            object value;
            var geo = result != null && result.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
            object lat, lng;
            if (geo == null || !geo.TryGetValue("lat", out lat) || !geo.TryGetValue("lng", out lng) || lat == null || lng == null) return null;
            return new[] { Convert.ToDouble(lat, CultureInfo.InvariantCulture), Convert.ToDouble(lng, CultureInfo.InvariantCulture) };
        }

        private static string Text(IDictionary<string, object> result, string key)
        {
            object value;
            return result != null && result.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private static string Km(double km)
        {
            return km.ToString(km < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        }
    }
}
