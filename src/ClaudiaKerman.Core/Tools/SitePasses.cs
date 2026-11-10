// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

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
    /// tool worked out, not the earliest lit one. Kept in memory for the game session (the tool and the block run in the same game) and forgotten on a
    /// game load (<see cref="Clear"/>), so a pass from a discarded timeline is never followed. A target within deorbit's accuracy_km of a site on the same
    /// body is that site: a site typed to fewer places, or flown by a lander decoupled from the vessel the tool ran for (a new vessel id), still matches.
    /// Of several, the vessel's own answer wins, then the latest, then the nearest site. Whether the pass is still in deorbit's window is the search's to
    /// find (<see cref="DeorbitBurn"/>: a pass out of it is one it can't fly, so the nearest-sun fallback with a note). After a restart there is none,
    /// and deorbit keeps its own rule (the earliest pass with the sun at 20 degrees or more).
    /// </summary>
    internal sealed class SitePasses
    {
        /// <summary>The game's one record, written by the tool and read by <c>DEORBITBURN</c>.</summary>
        public static readonly SitePasses Shared = new SitePasses();

        /// <summary>How many of the tool's answers are kept, the oldest dropped first.</summary>
        public const int MaxCalls = 8;

        private sealed class Call
        {
            public string VesselId;
            public string Body;
            public List<SitePass> Sites;
        }

        private readonly object _lock = new object();
        /// <summary>The latest answer first.</summary>
        private readonly List<Call> _calls = new List<Call>();

        /// <summary>Keeps the tool's answer for <paramref name="vesselId"/> round <paramref name="body"/>, the latest of the record.</summary>
        public void Record(string vesselId, string body, IEnumerable<LandingSite> sites)
        {
            var passes = sites.Select(s => new SitePass { Latitude = s.Latitude, Longitude = s.Longitude, BurnUt = s.PassUt, LandingUt = s.LandingUt, SunDeg = s.SunElevationDeg }).ToList();
            lock (_lock)
            {
                _calls.Insert(0, new Call { VesselId = vesselId ?? "", Body = body ?? "", Sites = passes });
                if (_calls.Count > MaxCalls) _calls.RemoveRange(MaxCalls, _calls.Count - MaxCalls);
            }
        }

        /// <summary>Forgets every answer: a game load (quickload, revert) discards the timeline their passes were worked out in.</summary>
        public void Clear()
        {
            lock (_lock) _calls.Clear();
        }

        /// <summary>
        /// The recorded pass for a target at (<paramref name="lat"/>, <paramref name="lng"/>) round <paramref name="body"/> (radius <paramref name="radiusM"/>):
        /// a site within <paramref name="withinKm"/> of it, this vessel's answer first, then the latest, then the nearest; null for none.
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
                    .OrderBy(m => m.own ? 0 : 1).ThenBy(m => m.age).ThenBy(m => m.km)
                    .Select(m => m.site).FirstOrDefault();
            }
        }
    }
}
