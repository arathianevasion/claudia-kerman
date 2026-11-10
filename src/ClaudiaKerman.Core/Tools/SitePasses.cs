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
    /// #974: <c>find_landing_site</c>'s last answer on a body with air, so <c>deorbit</c> given one of its sites and a max_wait can fly the pass the
    /// tool worked out, not the earliest lit one. Kept in memory for the game session: the tool and the block run in the same game, and the record
    /// is matched by vessel, body, the site to the tool's 3 decimal places, and the pass's landing in deorbit's window. After a restart there is none,
    /// and deorbit keeps its own rule (the earliest pass with the sun at 20 degrees or more).
    /// </summary>
    internal sealed class SitePasses
    {
        /// <summary>The game's one record, written by the tool and read by <c>DEORBITBURN</c>.</summary>
        public static readonly SitePasses Shared = new SitePasses();

        /// <summary>A target this close to a site in latitude and longitude is that site, degrees: the tool prints 3 decimal places.</summary>
        public const double MatchDeg = 0.001;

        private readonly object _lock = new object();
        private string _vesselId = "";
        private string _body = "";
        private List<SitePass> _sites = new List<SitePass>();

        /// <summary>Replaces the record with the tool's answer for <paramref name="vesselId"/> round <paramref name="body"/>.</summary>
        public void Record(string vesselId, string body, IEnumerable<LandingSite> sites)
        {
            var passes = sites.Select(s => new SitePass { Latitude = s.Latitude, Longitude = s.Longitude, BurnUt = s.PassUt, LandingUt = s.LandingUt, SunDeg = s.SunElevationDeg }).ToList();
            lock (_lock)
            {
                _vesselId = vesselId ?? "";
                _body = body ?? "";
                _sites = passes;
            }
        }

        /// <summary>The recorded pass for a target at (<paramref name="lat"/>, <paramref name="lng"/>) of this vessel round this body; null for none.</summary>
        public SitePass Of(string vesselId, string body, double lat, double lng)
        {
            lock (_lock)
            {
                if (!string.Equals(_vesselId, vesselId ?? "", StringComparison.Ordinal) || !string.Equals(_body, body ?? "", StringComparison.OrdinalIgnoreCase)) return null;
                return _sites.FirstOrDefault(s => Math.Abs(s.Latitude - lat) <= MatchDeg && Math.Abs(LandingSiteSearch.Wrap180(s.Longitude - lng)) <= MatchDeg);
            }
        }
    }
}
