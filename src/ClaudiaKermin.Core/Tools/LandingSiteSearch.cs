// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>What to look for: <c>find_landing_site</c>'s inputs once defaulted, and what a Building Block will pass later.</summary>
    internal sealed class LandingSiteQuery
    {
        /// <summary>The biome wanted; null or empty for any.</summary>
        public string Biome;
        /// <summary>The sun must be up at the pass.</summary>
        public bool Daylight = true;
        public double MaxSlopeDeg = 5;
        /// <summary>How far ahead the ground track is searched, hours.</summary>
        public double WithinHours = 6;
        public bool HasNear;
        public double NearLatitude;
        public double NearLongitude;
        public int MaxSites = 3;
        /// <summary>With a biome: a site is inside it only if the biome runs at least this far every way round it, m (about the landing accuracy); 0 for no margin.</summary>
        public double EdgeMarginM = LandingSiteSearch.DefaultEdgeMarginM;
        /// <summary>A site is one <c>land</c> can fly only with a pass whose correction burn costs at most this, m/s (<see cref="LandingPass"/>).</summary>
        public double MaxCorrectionDv = LandingPass.MostCorrectionDv;
        /// <summary>The craft that will land (#511): a site is kept only if some Descent Shape clears its pass's approach. Null: no terrain filter.</summary>
        public BrakeCraft Craft;
        /// <summary>When the search stops and answers with what it has (#536); null to run to the end.</summary>
        public SearchDeadline Deadline;
    }

    internal sealed class LandingSite
    {
        public double Latitude;
        public double Longitude;
        public string Biome = "";
        /// <summary>Terrain height above sea level, m.</summary>
        public double Altitude;
        public double SlopeDeg;
        /// <summary>The UT the craft's current ground track passes over the site.</summary>
        public double PassUt;
        /// <summary>The sun's elevation at <see cref="PassUt"/>, degrees.</summary>
        public double SunElevationDeg;
        /// <summary>Degrees of arc from the <c>near</c> point; NaN without one.</summary>
        public double FromNearDeg = double.NaN;
        /// <summary>The biome edge is within the query's margin of this site: nothing deeper in the biome was reachable.</summary>
        public bool AtEdge;
        /// <summary>The touchdown UT of the pass <c>land</c> would fly to this site from the orbit as it is (<see cref="LandingPass"/>).</summary>
        public double LandingUt;
        /// <summary>That pass's correction burn, m/s.</summary>
        public double CorrectionDv;
        /// <summary>The landing's predicted delta-v (deorbit, correction and descent) and the Descent Shape it flies (#511); NaN without a craft.</summary>
        public double PredictedDv = double.NaN;
        public double DescentShape = double.NaN;
    }

    internal sealed class LandingSiteResult
    {
        public string Error;
        public readonly List<LandingSite> Sites = new List<LandingSite>();
        /// <summary>Ground-track points looked at, and how many were left after the biome, daylight and slope tests, for saying why a search came back empty.</summary>
        public int Candidates;
        public int AfterBiome;
        public int AfterDaylight;
        public int AfterSlope;
        /// <summary>Sites that passed every test but have no pass <c>land</c> can fly in the window.</summary>
        public int Unflyable;
        /// <summary>The window actually searched, hours (the ask, held to what the search can cover).</summary>
        public double WindowHours;
        /// <summary>
        /// The query's deadline passed before the search was done (#536): <see cref="Sites"/> are those found by then, and with the
        /// track in pass order every point before <see cref="SearchedHours"/> (from now) was tried; with a <c>near</c> point,
        /// <see cref="Tried"/> of the <see cref="NearPoints"/> track points closest to it were.
        /// </summary>
        public bool OutOfTime;
        /// <summary>Hours from now the track was searched to, when <see cref="OutOfTime"/>.</summary>
        public double SearchedHours;
        /// <summary>Candidate points tried (in the search's order), and how many there were, when <see cref="OutOfTime"/>.</summary>
        public int Tried;
        public int NearPoints;
    }

    /// <summary>
    /// The landing-site search (#278), in Core so the <c>find_landing_site</c> tool and the land-by-biome block share it. The craft's
    /// current orbit gives the ground track: the orbit is propagated here and turned into latitude and longitude with the body's
    /// rotation, after the body interface says where the orbit's frame sits on the body (three reads, which also catch a frame this
    /// code did not expect). Each point of the track within the window is a candidate site, tried in order of pass time (or
    /// closeness to a <c>near</c> point) against the biome, the sun and the slope, so a site is always one the craft will fly over,
    /// and kept only if <c>land</c>'s pass search (<see cref="LandingPass"/>) has a pass to it in the window (#510). The body
    /// interface is asked in batches, and the track in rounds in pass order; it is asked nothing about a body with an atmosphere, which is refused.
    /// </summary>
    internal static class LandingSiteSearch
    {
        /// <summary>Daylight (GLOSSARY.md): the sun must be more than this high at the pass for a daylight site, degrees. The same as land_daylight_sun_deg in land.ks.</summary>
        public const double MinSunElevationDeg = 20;
        public const double MaxWindowHours = 48;
        /// <summary>How far inside its biome a site is wanted by default, m: about the landing accuracy.</summary>
        public const double DefaultEdgeMarginM = 5000;
        /// <summary>The distance either side of a site the slope is measured over, m.</summary>
        public const double SlopeSpacing = 25;
        /// <summary>Sites in one result are at least this far apart, degrees of arc.</summary>
        public const double MinSeparationDeg = 2;
        private const int TrackStepsPerOrbit = 180;
        /// <summary>Enough for 48 h of a 30 km Mun orbit at full resolution (#510: the window is at least a turn of the body).</summary>
        private const int MaxTrackSamples = 16000;
        /// <summary>The most track points surveyed for the biome and the sun in one round (with a <c>near</c> point, the only round), and the most whose slope is measured, per search.</summary>
        private const int MaxCandidates = 240;
        private const int MaxSlopeChecks = 60;
        private const int SlopeBatch = 12;
        /// <summary>With a <c>near</c> point, only track within this many degrees of it is a candidate.</summary>
        private const double NearRadiusDeg = 15;
        /// <summary>The spacing of the whole-body biome survey for a site off the track (#545), degrees of arc, and the most of its points whose slope is measured.</summary>
        private const double BiomeGridDeg = 4;
        private const int MaxOffTrackChecks = 2 * SlopeBatch;

        public static LandingSiteResult Find(SnapshotBody body, Conic orbit, double now, LandingSiteQuery query, IPlanningBody api)
        {
            var result = new LandingSiteResult();
            if (body.AtmosphereHeight > 0)
            {
                result.Error = body.Name + " has an atmosphere, and find_landing_site is airless-only for now";
                return result;
            }
            if (!orbit.Closed || orbit.Period <= 0)
            {
                result.Error = "the craft's orbit round " + body.Name + " is not closed, so it has no repeating ground track to search";
                return result;
            }

            double lonOffset, handed;
            var frame = Frame(body.Name, api, out lonOffset, out handed);
            if (frame != null)
            {
                result.Error = frame;
                return result;
            }

            var window = Math.Max(0.05, Math.Min(MaxWindowHours, query.WithinHours)) * 3600;
            result.WindowHours = window / 3600;
            var track = Track(body, orbit, now, window, lonOffset, handed);
            result.Candidates = track.Count;

            var all = Candidates(track, query);
            var wantBiome = !string.IsNullOrEmpty(query.Biome);
            var deadline = query.Deadline ?? SearchDeadline.None;
            var sites = new List<LandingSite>();
            var edgeSites = new List<LandingSite>();
            var checkedSlope = 0;
            var unreadable = 0;
            var passedSlope = 0;
            var stoppedAt = -1;
            // In rounds of the track in pass order, so a long window keeps the track's resolution and a soon site comes first.
            for (var from = 0; from < all.Count && sites.Count < query.MaxSites && checkedSlope < MaxSlopeChecks; from += MaxCandidates)
            {
                if (deadline.Passed)
                {
                    stoppedAt = from;
                    break;
                }
                var round = all.Skip(from).Take(MaxCandidates).ToList();
                var candidates = round;
                // The biome first, then the sun only where the biome is right (#536: through the game a read is time a frame can't
                // spare, and the sun at every track point was most of them).
                if (wantBiome)
                {
                    api.Survey(body.Name, candidates.Select(c => c.Sample).ToList(), SiteFacts.Biome);
                    candidates = candidates.Where(c => SameBiome(c.Sample.Biome, query.Biome)).ToList();
                }
                result.AfterBiome += candidates.Count;
                if (query.Daylight && candidates.Count > 0)
                {
                    api.Survey(body.Name, candidates.Select(c => c.Sample).ToList(), SiteFacts.Sun);
                    candidates = candidates.Where(c => c.Sample.SunElevation > MinSunElevationDeg).ToList();
                }
                result.AfterDaylight += candidates.Count;
                var stopped = TestRound(body, orbit, now, query, api, candidates, sites, edgeSites, result, ref checkedSlope, ref unreadable, ref passedSlope);
                if (stopped != null)
                {
                    stoppedAt = all.IndexOf(stopped);
                    break;
                }
            }
            if (stoppedAt >= 0)
            {
                result.OutOfTime = true;
                result.Tried = stoppedAt;
                result.NearPoints = all.Count;
                result.SearchedHours = (all[stoppedAt].Sample.Ut - now) / 3600;
            }
            if (sites.Count == 0) sites = edgeSites;
            result.AfterSlope = passedSlope;
            if (all.Count > 0 && checkedSlope > 0 && unreadable == checkedSlope)
            {
                result.Error = "the terrain on " + body.Name + " could not be read";
                return result;
            }
            result.Sites.AddRange(sites);
            return result;
        }

        /// <summary>
        /// One round's candidates (already in the biome and the sun) through the slope test in batches, the biome margin and
        /// <c>land</c>'s pass search, adding sites to <paramref name="sites"/> (or <paramref name="edgeSites"/>, at the biome edge).
        /// Returns the first candidate not tried when the query's deadline passed (#536), or null when the round was done.
        /// </summary>
        private static Candidate TestRound(SnapshotBody body, Conic orbit, double now, LandingSiteQuery query, IPlanningBody api, List<Candidate> candidates,
            List<LandingSite> sites, List<LandingSite> edgeSites, LandingSiteResult result, ref int checkedSlope, ref int unreadable, ref int passedSlope)
        {
            var wantBiome = !string.IsNullOrEmpty(query.Biome);
            var deadline = query.Deadline ?? SearchDeadline.None;
            for (var at = 0; at < candidates.Count && sites.Count < query.MaxSites && checkedSlope < MaxSlopeChecks; at += SlopeBatch)
            {
                if (deadline.Passed) return candidates[at];
                var batch = candidates.Skip(at).Take(Math.Min(SlopeBatch, MaxSlopeChecks - checkedSlope)).ToList();
                checkedSlope += batch.Count;
                var points = new List<SiteSample>();
                foreach (var c in batch) points.AddRange(Cross(body, c.Sample));
                api.Survey(body.Name, points, SiteFacts.Altitude | SiteFacts.Biome | SiteFacts.Sun);
                var gentle = new List<int>();
                var slopes = new double[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    var five = points.Skip(i * 5).Take(5).ToList();
                    if (five.Any(p => double.IsNaN(p.Altitude)))
                    {
                        unreadable++;
                        continue;
                    }
                    slopes[i] = Slope(five);
                    if (slopes[i] <= query.MaxSlopeDeg) gentle.Add(i);
                }
                var deep = wantBiome && query.EdgeMarginM > 0 ? DeepInBiome(body, batch, gentle, query, api) : null;
                foreach (var i in gentle)
                {
                    passedSlope++;
                    var c = batch[i];
                    var five = points.Skip(i * 5).Take(5).ToList();
                    var atEdge = deep != null && !deep.Contains(i);
                    var list = atEdge ? edgeSites : sites;
                    if (list.Count >= query.MaxSites) continue;
                    if (list.Any(s => ArcDegrees(s.Latitude, s.Longitude, c.Sample.Latitude, c.Sample.Longitude) < MinSeparationDeg)) continue;
                    var site = new LandingSite
                    {
                        Latitude = c.Sample.Latitude, Longitude = c.Sample.Longitude, Biome = five[0].Biome, Altitude = five[0].Altitude,
                        SlopeDeg = slopes[i], PassUt = c.Sample.Ut, SunElevationDeg = five[0].SunElevation, FromNearDeg = c.FromNear, AtEdge = atEdge,
                    };
                    var pass = Fly(body, orbit, now, query, site, api);
                    if (pass.OutOfTime) return c;
                    if (pass.Error != null)
                    {
                        result.Unflyable++;
                        continue;
                    }
                    list.Add(site);
                }
            }
            return null;
        }

        /// <summary>
        /// Whether <c>land</c> can fly to the site from the orbit as it is: its pass search (<see cref="LandingPass"/>) over the window
        /// <c>land</c> searches, from the first burn it may plan. Sets the site's <see cref="LandingSite.LandingUt"/> and
        /// <see cref="LandingSite.CorrectionDv"/>. The site is under the track at its own pass, so the search first looks only to just
        /// past that, where the earliest pass <c>land</c> takes must be, and over the rest of the window only if that finds none.
        /// The pass found (its error null when the site is flyable), or one <see cref="LandingPassResult.OutOfTime"/>.
        /// </summary>
        private static LandingPassResult Fly(SnapshotBody body, Conic orbit, double now, LandingSiteQuery query, LandingSite site, IPlanningBody api)
        {
            var ask = new LandingPassAsk
            {
                Latitude = site.Latitude, Longitude = site.Longitude, PeRadius = body.Radius + Math.Max(0, site.Altitude) + LandingPass.ApproachM,
                EarliestUt = now + LandingPass.LeadS, Daylight = query.Daylight, MaxCorrectionDv = query.MaxCorrectionDv, Craft = query.Craft,
                Deadline = query.Deadline,
            };
            var latest = now + LandingPass.ReachSeconds(Math.Max(0.05, Math.Min(MaxWindowHours, query.WithinHours)), query.Daylight, body.RotationPeriod);
            ask.LatestUt = Math.Min(latest, site.PassUt + 1.5 * orbit.Period);
            var pass = LandingPass.Find(body, orbit, now, ask, api);
            if (pass.Error != null && !pass.OutOfTime && ask.LatestUt < latest)
            {
                ask.LatestUt = latest;
                pass = LandingPass.Find(body, orbit, now, ask, api);
            }
            if (pass.Error != null) return pass;
            site.LandingUt = pass.ArriveUt;
            site.CorrectionDv = pass.CorrectionDv;
            site.PredictedDv = pass.LandingDv;
            if (pass.Descent != null) site.DescentShape = pass.Descent.Shape;
            return pass;
        }

        /// <summary>
        /// The query's biome's best site off the ground track (#545), for the plane changes when no pass reaches the biome: a survey
        /// of the whole body every <see cref="BiomeGridDeg"/> degrees finds the biome, and its points are tried, those whose grid
        /// neighbours are in it too first, then by how far their latitude lies beyond the orbit's inclination
        /// (<paramref name="inclination"/>, degrees), so the plane change onto the site is the smallest. The first gentle enough
        /// and the margin inside the biome is the site; without one, the first gentle enough, marked <see cref="LandingSite.AtEdge"/>.
        /// Its pass fields are unset: no pass reaches it. Null when the survey finds the biome nowhere, or no point tried is gentle enough.
        /// </summary>
        public static LandingSite BiomeSite(SnapshotBody body, double inclination, double now, LandingSiteQuery query, IPlanningBody api)
        {
            if (string.IsNullOrEmpty(query.Biome)) return null;
            var rows = new List<List<SiteSample>>();
            var grid = new List<SiteSample>();
            for (var lat = -90 + BiomeGridDeg / 2; lat < 90; lat += BiomeGridDeg)
            {
                var count = Math.Max(1, (int)Math.Round(360 * Math.Cos(lat * Math.PI / 180) / BiomeGridDeg));
                var row = new List<SiteSample>(count);
                for (var k = 0; k < count; k++) row.Add(new SiteSample { Latitude = lat, Longitude = Wrap180(-180 + (k + 0.5) * 360 / count), Ut = now });
                rows.Add(row);
                grid.AddRange(row);
            }
            api.Survey(body.Name, grid, SiteFacts.Biome);

            var own = inclination > 90 ? 180 - inclination : inclination;
            var ranked = new List<Candidate>();
            var interior = new HashSet<SiteSample>();
            for (var i = 0; i < rows.Count; i++)
                for (var k = 0; k < rows[i].Count; k++)
                {
                    var p = rows[i][k];
                    if (!SameBiome(p.Biome, query.Biome)) continue;
                    ranked.Add(new Candidate { Sample = p });
                    var n = rows[i].Count;
                    if (In(rows[i][(k + 1) % n], query) && In(rows[i][(k + n - 1) % n], query)
                        && (i == 0 || In(Nearest(rows[i - 1], p.Longitude), query)) && (i == rows.Count - 1 || In(Nearest(rows[i + 1], p.Longitude), query)))
                        interior.Add(p);
                }
            ranked = ranked.OrderBy(c => interior.Contains(c.Sample) ? 0 : 1).ThenBy(c => Math.Max(0, Math.Abs(c.Sample.Latitude) - own))
                .Take(MaxOffTrackChecks).ToList();

            LandingSite edgeSite = null;
            for (var at = 0; at < ranked.Count; at += SlopeBatch)
            {
                var batch = ranked.Skip(at).Take(SlopeBatch).ToList();
                var points = new List<SiteSample>();
                foreach (var c in batch) points.AddRange(Cross(body, c.Sample));
                api.Survey(body.Name, points, SiteFacts.Altitude);
                var gentle = new List<int>();
                var slopes = new double[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    var five = points.Skip(i * 5).Take(5).ToList();
                    if (five.Any(p => double.IsNaN(p.Altitude))) continue;
                    slopes[i] = Slope(five);
                    if (slopes[i] <= query.MaxSlopeDeg) gentle.Add(i);
                }
                var deep = query.EdgeMarginM > 0 ? DeepInBiome(body, batch, gentle, query, api) : null;
                foreach (var i in gentle)
                {
                    var site = new LandingSite
                    {
                        Latitude = batch[i].Sample.Latitude, Longitude = batch[i].Sample.Longitude, Biome = batch[i].Sample.Biome,
                        Altitude = points[i * 5].Altitude, SlopeDeg = slopes[i], AtEdge = deep != null && !deep.Contains(i),
                    };
                    if (!site.AtEdge) return site;
                    if (edgeSite == null) edgeSite = site;
                }
            }
            return edgeSite;
        }

        private static bool In(SiteSample sample, LandingSiteQuery query)
        {
            return SameBiome(sample.Biome, query.Biome);
        }

        /// <summary>The point of a survey row nearest a longitude.</summary>
        private static SiteSample Nearest(List<SiteSample> row, double longitude)
        {
            var k = (int)Math.Floor((Wrap180(longitude) + 180) / 360 * row.Count);
            return row[Math.Max(0, Math.Min(row.Count - 1, k))];
        }

        private sealed class Candidate
        {
            public SiteSample Sample;
            public double FromNear = double.NaN;
        }

        private sealed class TrackPoint
        {
            public double Latitude;
            public double Longitude;
            public double Ut;
        }

        /// <summary>How the orbit's frame sits on the body: the longitude of its x axis and whether longitude grows with its angle (1) or against it (-1); an error text when the three reads don't fit a frame this code knows.</summary>
        internal static string Frame(string body, IPlanningBody api, out double lonOffset, out double handed)
        {
            lonOffset = 0;
            handed = 1;
            double lat0, lon0, lat1, lon1, lat2, lon2;
            if (!api.SurfaceUnder(body, 1, 0, 0, out lat0, out lon0) || !api.SurfaceUnder(body, 0, 1, 0, out lat1, out lon1)
                || !api.SurfaceUnder(body, 0, 0, 1, out lat2, out lon2)
                || double.IsNaN(lat0 + lon0 + lat1 + lon1 + lat2))
                return "the game could not say where the orbit's frame sits on " + body;
            var quarter = Wrap180(lon1 - lon0);
            if (Math.Abs(lat0) > 1 || Math.Abs(lat1) > 1 || lat2 < 89 || Math.Abs(Math.Abs(quarter) - 90) > 1)
                return "the orbit's frame does not sit on " + body + " the way the ground track assumes (z north, x and y on the equator)";
            lonOffset = lon0;
            handed = quarter > 0 ? 1 : -1;
            return null;
        }

        /// <summary>The craft's ground track over the window: latitude, and the longitude fixed to the body, at evenly spaced UTs.</summary>
        private static List<TrackPoint> Track(SnapshotBody body, Conic orbit, double now, double window, double lonOffset, double handed)
        {
            var step = orbit.Period / TrackStepsPerOrbit;
            var count = (int)Math.Min(MaxTrackSamples, Math.Ceiling(window / step) + 1);
            step = count > 1 ? window / (count - 1) : window;
            var turn = body.RotationPeriod > 0 ? 360 / body.RotationPeriod : 0;
            var track = new List<TrackPoint>();
            for (var i = 0; i < count; i++)
            {
                var ut = now + i * step;
                var r = orbit.Position(ut);
                var length = r.Length;
                if (length <= 0) continue;
                var lat = Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI;
                var lon = lonOffset + handed * Math.Atan2(r.Y, r.X) * 180 / Math.PI - turn * (ut - now);
                track.Add(new TrackPoint { Latitude = lat, Longitude = Wrap180(lon), Ut = ut });
            }
            return track;
        }

        /// <summary>The track points worth testing: all in pass order, or with a <c>near</c> point the ones close to it, nearest first.</summary>
        private static List<Candidate> Candidates(List<TrackPoint> track, LandingSiteQuery query)
        {
            IEnumerable<Candidate> all = track.Select(t => new Candidate
            {
                Sample = new SiteSample { Latitude = t.Latitude, Longitude = t.Longitude, Ut = t.Ut },
                FromNear = query.HasNear ? ArcDegrees(t.Latitude, t.Longitude, query.NearLatitude, query.NearLongitude) : double.NaN,
            });
            if (query.HasNear)
                return all.Where(c => c.FromNear <= NearRadiusDeg).OrderBy(c => c.FromNear).Take(MaxCandidates).ToList();
            return all.ToList();
        }

        /// <summary>The site and the four points a slope is measured from (east, west, north, south, each <see cref="SlopeSpacing"/> away).</summary>
        private static IEnumerable<SiteSample> Cross(SnapshotBody body, SiteSample site)
        {
            var dLat = SlopeSpacing / body.Radius * 180 / Math.PI;
            var cos = Math.Max(0.05, Math.Cos(site.Latitude * Math.PI / 180));
            var dLon = dLat / cos;
            yield return new SiteSample { Latitude = site.Latitude, Longitude = site.Longitude, Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude, Longitude = Wrap180(site.Longitude + dLon), Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude, Longitude = Wrap180(site.Longitude - dLon), Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude + dLat, Longitude = site.Longitude, Ut = site.Ut };
            yield return new SiteSample { Latitude = site.Latitude - dLat, Longitude = site.Longitude, Ut = site.Ut };
        }

        /// <summary>The batch's candidates (by index in <paramref name="gentle"/>) whose biome runs the margin out every way round them: eight points that far off, all in the query's biome.</summary>
        private static HashSet<int> DeepInBiome(SnapshotBody body, List<Candidate> batch, List<int> gentle, LandingSiteQuery query, IPlanningBody api)
        {
            const int Ways = 8;
            var d = query.EdgeMarginM / body.Radius * 180 / Math.PI;
            var ring = new List<SiteSample>();
            foreach (var i in gentle)
            {
                var site = batch[i].Sample;
                var cos = Math.Max(0.05, Math.Cos(site.Latitude * Math.PI / 180));
                for (var w = 0; w < Ways; w++)
                {
                    var angle = w * 2 * Math.PI / Ways;
                    ring.Add(new SiteSample
                    {
                        Latitude = Math.Max(-90, Math.Min(90, site.Latitude + d * Math.Cos(angle))),
                        Longitude = Wrap180(site.Longitude + d * Math.Sin(angle) / cos),
                        Ut = site.Ut,
                    });
                }
            }
            var deep = new HashSet<int>();
            if (ring.Count == 0) return deep;
            api.Survey(body.Name, ring, SiteFacts.Biome);
            for (var at = 0; at < gentle.Count; at++)
                if (ring.Skip(at * Ways).Take(Ways).All(p => SameBiome(p.Biome, query.Biome))) deep.Add(gentle[at]);
            return deep;
        }

        /// <summary>The ground's steepness from the centre and the four points round it, degrees.</summary>
        private static double Slope(List<SiteSample> five)
        {
            var east = (five[1].Altitude - five[2].Altitude) / (2 * SlopeSpacing);
            var north = (five[3].Altitude - five[4].Altitude) / (2 * SlopeSpacing);
            return Math.Atan(Math.Sqrt(east * east + north * north)) * 180 / Math.PI;
        }

        /// <summary>Biome names match ignoring case, spaces and punctuation ("Northern Basin" is stock's "NorthernBasin").</summary>
        public static bool SameBiome(string a, string b)
        {
            return string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Squash(string text)
        {
            return new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
        }

        public static double Wrap180(double degrees)
        {
            var wrapped = degrees % 360;
            if (wrapped > 180) wrapped -= 360;
            if (wrapped <= -180) wrapped += 360;
            return wrapped;
        }

        /// <summary>Great-circle distance between two points, degrees of arc.</summary>
        public static double ArcDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double rad = Math.PI / 180;
            var a = Math.Pow(Math.Sin((lat2 - lat1) * rad / 2), 2) + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin((lon2 - lon1) * rad / 2), 2);
            return 2 * Math.Asin(Math.Min(1, Math.Sqrt(a))) / rad;
        }

        public static string Count(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
