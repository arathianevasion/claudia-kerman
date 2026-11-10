// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>A pass <c>find_landing_site</c> gave for a site on a body with air (#974): when it lands, and the sun at the site then.</summary>
    internal sealed class SitePass
    {
        public double Latitude;
        public double Longitude;
        /// <summary>The pass's deorbit burn, UT (an estimate; <c>deorbit</c> flies its own).</summary>
        public double BurnUt;
        public double LandingUt;
        /// <summary>The sun's elevation at the site at <see cref="LandingUt"/>, degrees, as the tool reported it.</summary>
        public double SunDeg;
    }

    /// <summary>
    /// #974: <c>find_landing_site</c>'s recent answers on bodies with air, so <c>deorbit</c> given one of their sites and a max_wait can fly the pass the
    /// tool worked out, not the earliest lit one. Kept in memory for the game session (the tool and the block run in the same game); an answer given
    /// after the UT a flight resumes at is forgotten (<see cref="ForgetAfter"/>: a quickload or revert went back past it), so a pass from a discarded
    /// timeline is never followed, while a trip to the Space Center or a switch of vessel keeps them. A target within deorbit's accuracy_km of a site on the same
    /// body is that site: a site typed to fewer places, or flown by a lander decoupled from the vessel the tool ran for (a new vessel id), still matches.
    /// Of several, the vessel's own answer wins, then a site within <see cref="SameSiteKm"/> (the site itself) over one further off, then the latest. Whether the pass is still in deorbit's window is the search's to
    /// find (<see cref="DeorbitBurn"/>: a pass out of it is one it can't fly, so the nearest-sun fallback with a note). After a restart there is none,
    /// and deorbit keeps its own rule (the cheapest pass with the sun at 20 degrees or more, the earliest of a tie).
    /// </summary>
    internal sealed class SitePasses
    {
        /// <summary>The game's one record, written by the tool and read by <c>DEORBITBURN</c>.</summary>
        public static readonly SitePasses Shared = new SitePasses();

        /// <summary>How many of the tool's answers are kept, the oldest dropped first.</summary>
        public const int MaxCalls = 8;

        /// <summary>A site this close to the target, km, is the site itself: a site as printed (3 places) or typed to 2 is within about 60 m on Eve.</summary>
        public const double SameSiteKm = 0.1;

        private sealed class Call
        {
            public string VesselId;
            public string Body;
            public double Ut;
            public List<SitePass> Sites;
        }

        private readonly object _lock = new object();
        /// <summary>The latest answer first.</summary>
        private readonly List<Call> _calls = new List<Call>();

        /// <summary>Keeps the tool's answer for <paramref name="vesselId"/> round <paramref name="body"/>, given at <paramref name="ut"/>, the latest of the record.</summary>
        public void Record(string vesselId, string body, double ut, IEnumerable<LandingSite> sites)
        {
            var passes = sites.Select(s => new SitePass { Latitude = s.Latitude, Longitude = s.Longitude, BurnUt = s.PassUt, LandingUt = s.LandingUt, SunDeg = s.SunElevationDeg }).ToList();
            lock (_lock)
            {
                _calls.Insert(0, new Call { VesselId = vesselId ?? "", Body = body ?? "", Ut = ut, Sites = passes });
                if (_calls.Count > MaxCalls) _calls.RemoveRange(MaxCalls, _calls.Count - MaxCalls);
            }
        }

        /// <summary>Forgets the answers given after <paramref name="ut"/>, the UT a flight resumes at: a quickload or revert went back past them, discarding their timeline.</summary>
        public void ForgetAfter(double ut)
        {
            lock (_lock) _calls.RemoveAll(call => call.Ut > ut);
        }

        /// <summary>#975: for the Mission save, the latest answer first; "" for none.</summary>
        public string ToJson()
        {
            lock (_lock)
            {
                if (_calls.Count == 0) return "";
                var json = new JsonWriter().BeginArray();
                foreach (var call in _calls)
                {
                    json.BeginObject().Field("vessel_id", call.VesselId).Field("body", call.Body).Field("ut", call.Ut).Name("sites").BeginArray();
                    foreach (var s in call.Sites)
                        json.BeginObject().Field("lat", s.Latitude).Field("lng", s.Longitude).Field("burn_ut", s.BurnUt).Field("landing_ut", s.LandingUt).Field("sun", s.SunDeg).EndObject();
                    json.EndArray().EndObject();
                }
                return json.EndArray().ToString();
            }
        }

        /// <summary>
        /// #975: replaces the record with what <see cref="ToJson"/> wrote, as a flight resuming at <paramref name="ut"/> finds it: answers given after
        /// <paramref name="ut"/> are dropped (<see cref="ForgetAfter"/>), at most <see cref="MaxCalls"/> kept. "" (an older save) or anything unreadable leaves it empty.
        /// </summary>
        public void Load(string text, double ut)
        {
            var read = new List<Call>();
            JsonNode root = null;
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    root = JsonReader.Parse(text);
                }
                catch (JsonException)
                {
                }
            }
            if (root != null && root.Kind == JsonKind.Array)
                foreach (var node in root.Items.Where(n => n.Kind == JsonKind.Object))
                {
                    var callUt = JsonNode.Double(node["ut"]);
                    var body = JsonNode.Str(node["body"]);
                    var sites = node["sites"];
                    if (!callUt.HasValue || callUt.Value > ut || string.IsNullOrEmpty(body) || sites == null || sites.Kind != JsonKind.Array) continue;
                    var passes = new List<SitePass>();
                    foreach (var site in sites.Items.Where(n => n.Kind == JsonKind.Object))
                    {
                        double? lat = JsonNode.Double(site["lat"]), lng = JsonNode.Double(site["lng"]), burn = JsonNode.Double(site["burn_ut"]), landing = JsonNode.Double(site["landing_ut"]), sun = JsonNode.Double(site["sun"]);
                        if (lat.HasValue && lng.HasValue && burn.HasValue && landing.HasValue && sun.HasValue)
                            passes.Add(new SitePass { Latitude = lat.Value, Longitude = lng.Value, BurnUt = burn.Value, LandingUt = landing.Value, SunDeg = sun.Value });
                    }
                    read.Add(new Call { VesselId = JsonNode.Str(node["vessel_id"]) ?? "", Body = body, Ut = callUt.Value, Sites = passes });
                }
            if (read.Count > MaxCalls) read.RemoveRange(MaxCalls, read.Count - MaxCalls);
            lock (_lock)
            {
                _calls.Clear();
                _calls.AddRange(read);
            }
        }

        /// <summary>
        /// The recorded pass for a target at (<paramref name="lat"/>, <paramref name="lng"/>) round <paramref name="body"/> (radius <paramref name="radiusM"/>):
        /// a site within <paramref name="withinKm"/> of it, this vessel's answer first, then the site itself (<see cref="SameSiteKm"/>), then the latest, then the nearest; null for none.
        /// </summary>
        public SitePass Of(string vesselId, string body, double radiusM, double lat, double lng, double withinKm)
        {
            var kmPerDeg = radiusM * Math.PI / 180 / 1000;
            lock (_lock)
            {
                return _calls.Select((call, age) => new { call, age })
                    .Where(c => string.Equals(c.call.Body, body ?? "", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(c => c.call.Sites.Select(site => new
                    {
                        site,
                        own = string.Equals(c.call.VesselId, vesselId ?? "", StringComparison.Ordinal),
                        c.age,
                        km = LandingSiteSearch.ArcDegrees(site.Latitude, site.Longitude, lat, lng) * kmPerDeg,
                    }))
                    .Where(m => m.km <= withinKm)
                    .OrderBy(m => m.own ? 0 : 1).ThenBy(m => m.km <= SameSiteKm ? 0 : 1).ThenBy(m => m.age).ThenBy(m => m.km)
                    .Select(m => m.site).FirstOrDefault();
            }
        }
    }
}
