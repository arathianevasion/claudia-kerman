// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools.Numerics;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>What <c>land</c>'s pass search looks for over a fixed site (#510).</summary>
    internal sealed class LandingPassAsk
    {
        public double Latitude;
        public double Longitude;
        /// <summary>The descent's periapsis, m from the body's centre: <see cref="LandingPass.ApproachM"/> over the site's terrain.</summary>
        public double PeRadius;
        /// <summary>The first UT a deorbit burn may be planned for.</summary>
        public double EarliestUt;
        /// <summary>The last UT a touchdown may be planned for: the window's end (<see cref="LandingPass.ReachSeconds"/>).</summary>
        public double LatestUt;
        public bool Daylight = true;
        /// <summary>The most the correction burn that puts the ground track over the site may cost, m/s.</summary>
        public double MaxCorrectionDv = LandingPass.MostCorrectionDv;
        /// <summary>
        /// The craft that will brake (#511): with one that can fly, each pass's descent is planned over the terrain under its
        /// approach (<see cref="DescentApproach.Plan"/>), a pass no Descent Shape clears is passed over, and the soonest pass within
        /// 10% of the cheapest landing is taken. Null plans no descent (the pass search alone).
        /// </summary>
        public BrakeCraft Craft;
        /// <summary>The Descent Shape asked for (<c>descent_shape</c>); null for "auto".</summary>
        public double? Shape;
        /// <summary>braking's margin (<c>burn_margin</c>).</summary>
        public double Margin = 1.1;
        /// <summary>A site no shape clears on a pass may move to the nearest that clears within 5 km (a site given as coordinates).</summary>
        public bool MoveSite;
        /// <summary>
        /// For the plane-change search's coarse pass (#521): the shapes searched (null for all of <see cref="BrakePath.Shapes"/>),
        /// a descent plan kept across searches (null: one per search), and how finely it tells headings apart, degrees.
        /// </summary>
        public IList<double> Shapes;
        public Dictionary<string, DescentChoice> Planned;
        public double HeadingStepDeg = 1;
        /// <summary>m between the terrain samples under the fall from the deorbit burn (#545: the coarse pass reads it sparser).</summary>
        public double FallStepM = DescentApproach.FallProfileStep;
        /// <summary>
        /// The sun's elevation at the site by UT, degrees, for a search that read it once over the window (the plane-change search,
        /// #536); null to survey it at each search's passes.
        /// </summary>
        public Func<double, double> SunAt;
        /// <summary>When to stop planning descents and answer <see cref="LandingPassResult.OutOfTime"/> (#536); null for never.</summary>
        public SearchDeadline Deadline;
        /// <summary>
        /// Without a craft, stop at the first pass taken (#545: the plane-change search's scan of each plane wants only it); with
        /// daylight, only where <see cref="SunAt"/> gives the sun there.
        /// </summary>
        public bool FirstOnly;
    }

    /// <summary>One pass over the site the search looked at, for <c>land</c>'s trace.</summary>
    internal sealed class LandingPassLine
    {
        public double BurnUt;
        public double ArriveUt;
        /// <summary>The site's distance off the orbit's plane at the touchdown, m.</summary>
        public double CrossM;
        /// <summary>The correction burn that puts the ground track over the site, m/s (<see cref="LandingPass.CorrectionDv"/>).</summary>
        public double CorrectionDv;
        /// <summary>The sun's elevation at the site at the touchdown, degrees; NaN where it wasn't asked (out of reach, or daylight not wanted).</summary>
        public double SunDeg = double.NaN;
        /// <summary>"chosen", "next", or why <c>land</c> won't plan for it.</summary>
        public string Verdict = "";
        /// <summary>The descent planned over this pass's approach (#511); null if none was planned or none clears.</summary>
        public DescentTry Descent;
        /// <summary>The approach's heading across the ground at the site, degrees; NaN where no descent was planned.</summary>
        public double Heading = double.NaN;
        /// <summary>The site the descent flies to, moved for terrain if it had to be.</summary>
        public double SiteLat;
        public double SiteLng;
        public bool Moved;
        /// <summary>The landing's delta-v: deorbit, correction and descent, m/s; NaN without a descent.</summary>
        public double LandingDv = double.NaN;
    }

    internal sealed class LandingPassResult
    {
        /// <summary>Null when a pass was found; otherwise why there is none.</summary>
        public string Error;
        /// <summary>The search ran over the passes (false when it could not start: no closed orbit, too low, no frame).</summary>
        public bool Ran;
        public double BurnUt = -1;
        public double ArriveUt = -1;
        public double CorrectionDv;
        public double CrossM;
        public double SunDeg = double.NaN;
        /// <summary>The touchdown UT of the next pass <c>land</c> may plan for after the chosen one, in the window; -1 for none.</summary>
        public double NextPassUt = -1;
        /// <summary>Seconds from the deorbit burn to periapsis over the site.</summary>
        public double FallS;
        /// <summary>The chosen pass's descent (#511), its site and landing delta-v; null without a craft.</summary>
        public DescentTry Descent;
        public double SiteLat;
        public double SiteLng;
        public bool Moved;
        public double LandingDv = double.NaN;
        /// <summary>The periapsis flown, m from the body's centre: the ask's, or the descent's.</summary>
        public double PeRadius;
        /// <summary>The shapes that clear (a refused <c>descent_shape</c>'s report); NaN when not worked out.</summary>
        public double ShapeLo = double.NaN;
        public double ShapeHi = double.NaN;
        /// <summary>Passes passed over because no shape cleared their approach (they count as no missed pass).</summary>
        public int TerrainPasses;
        /// <summary>The ask's deadline passed before every pass had its descent planned: no answer either way (#536).</summary>
        public bool OutOfTime;
        public readonly List<LandingPassLine> Passes = new List<LandingPassLine>();
    }

    /// <summary>
    /// <c>land</c>'s pass search over a fixed site (#510), in Core so <c>find_landing_site</c> shows only sites <c>land</c> can fly,
    /// and both choose the same pass. A pass is a deorbit burn whose arrival point (opposite the burn, a transfer's fall later)
    /// comes closest to the site; it is in reach when the correction burn that turns the orbit's plane over the site costs at most
    /// the ask's <see cref="LandingPassAsk.MaxCorrectionDv"/>, and taken when, with daylight wanted, the sun is up there at the
    /// touchdown too. The search covers the whole window, not a few orbits: a fixed site on a slowly turning body (Minmus turns once
    /// in 40,400 s against a 3,700 s orbit) meets an inclined ground track only a few times a turn.
    /// </summary>
    internal static class LandingPass
    {
        /// <summary>m above the site's terrain the descent's periapsis is aimed at; land.ks's approach.</summary>
        public const double ApproachM = 3000;
        /// <summary>A burn less than this far ahead is too soon to plan, s; land.ks plans from now plus this.</summary>
        public const double LeadS = 120;
        /// <summary><c>max_correction_dv</c>'s default is the smaller of this, m/s, and <see cref="CorrectionDvShare"/> of the lander's delta-v.</summary>
        public const double MostCorrectionDv = 30;
        public const double CorrectionDvShare = 0.05;
        /// <summary>Daylight's own reach: a little over one turn of the body, which holds a whole day and night.</summary>
        public const double DaylightTurns = 1.2;
        /// <summary>The scan's step, a fraction of the orbit; each pass found is then pinned to the second.</summary>
        private const int StepsPerOrbit = 36;

        /// <summary>
        /// The window <c>land</c> and <c>find_landing_site</c> share, hours: <paramref name="withinHours"/> if given (held to the
        /// search's 0.05 to 48), else the larger of the time cap and one turn of the body orbited, up to 48.
        /// </summary>
        public static double WindowHours(double? withinHours, double timeCapHours, double rotationPeriod)
        {
            if (withinHours.HasValue) return Math.Max(0.05, Math.Min(LandingSiteSearch.MaxWindowHours, withinHours.Value));
            return Math.Min(LandingSiteSearch.MaxWindowHours, Math.Max(timeCapHours, Math.Abs(rotationPeriod) / 3600));
        }

        /// <summary>How far ahead the pass search looks, s: the window, or with daylight a day of the body where that is longer.</summary>
        public static double ReachSeconds(double windowHours, bool daylight, double rotationPeriod)
        {
            var window = windowHours * 3600;
            return daylight ? Math.Max(window, DaylightTurns * Math.Abs(rotationPeriod)) : window;
        }

        /// <summary><c>max_correction_dv</c>'s default from the lander's delta-v (m/s; 0 or less for unknown): the smaller of 30 m/s and 5% of it.</summary>
        public static double DefaultMaxCorrectionDv(double landerDv)
        {
            return landerDv > 0 ? Math.Min(MostCorrectionDv, CorrectionDvShare * landerDv) : MostCorrectionDv;
        }

        /// <summary>The transfer's fall from the deorbit burn at radius <paramref name="r0"/> to periapsis at <paramref name="rPe"/>, s: half its orbit.</summary>
        public static double FallSeconds(double mu, double r0, double rPe)
        {
            var a = (r0 + rPe) / 2;
            return Math.PI * Math.Sqrt(a * a * a / mu);
        }

        /// <summary>
        /// The correction burn <c>land</c> flies halfway through the fall, from the deorbit burn at radius <paramref name="r0"/> to
        /// periapsis at <paramref name="rPe"/>: it turns the plane about the craft's position so the site, <paramref name="crossM"/>
        /// m off the plane at periapsis on a body of radius <paramref name="radius"/>, lies in it, keeping the horizontal speed. m/s.
        /// </summary>
        public static double CorrectionDv(double mu, double r0, double rPe, double crossM, double radius)
        {
            var a = (r0 + rPe) / 2;
            var e = (r0 - rPe) / (r0 + rPe);
            // Halfway through the fall: a quarter of the transfer's orbit, by time, before periapsis.
            var mean = -Math.PI / 2;
            var ecc = mean;
            for (var i = 0; i < 20; i++) ecc -= (ecc - e * Math.Sin(ecc) - mean) / (1 - e * Math.Cos(ecc));
            var nu = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(ecc / 2), Math.Sqrt(1 - e) * Math.Cos(ecc / 2));
            var r = a * (1 - e * Math.Cos(ecc));
            var vh = Math.Sqrt(mu * a * (1 - e * e)) / r;
            // Turning the plane by turn about a point sep before the site moves the site off it by asin(sin(turn) sin(sep)).
            var sep = Math.Abs(nu);
            var turn = Math.Asin(Math.Min(1, Math.Sin(Math.Abs(crossM) / radius) / Math.Max(1e-9, Math.Sin(sep))));
            return 2 * vh * Math.Sin(turn / 2);
        }

        public static LandingPassResult Find(SnapshotBody body, Conic orbit, double now, LandingPassAsk ask, IPlanningBody api)
        {
            var result = new LandingPassResult();
            if (!orbit.Closed || orbit.Period <= 0)
            {
                result.Error = "the craft's orbit round " + body.Name + " is not closed, so it has no passes over the site";
                return result;
            }
            if (ask.PeRadius > orbit.A - 1000)
            {
                result.Error = "orbit too low to land from: it must be at least " + Whole(ask.PeRadius + 1000 - body.Radius) + " m up, 1 km over the descent's start";
                return result;
            }
            double lonOffset, handed;
            var frame = LandingSiteSearch.Frame(body.Name, api, out lonOffset, out handed);
            if (frame != null)
            {
                result.Error = frame;
                return result;
            }

            var fall = FallSeconds(orbit.Mu, orbit.A, ask.PeRadius);
            result.FallS = fall;
            result.Ran = true;
            var turnRate = body.RotationPeriod != 0 ? 360 / body.RotationPeriod : 0;
            var lat = ask.Latitude * Math.PI / 180;
            Func<double, Vec3> siteAt = ut =>
            {
                var phi = handed * (ask.Longitude - lonOffset + turnRate * (ut - now)) * Math.PI / 180;
                return new Vec3(Math.Cos(lat) * Math.Cos(phi), Math.Cos(lat) * Math.Sin(phi), Math.Sin(lat));
            };
            // The angle between the arrival point (opposite the burn) and the site then, radians.
            Func<double, double> apart = burn =>
            {
                var arrival = orbit.Position(burn).Normalized * -1;
                return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(arrival, siteAt(burn + fall)))));
            };

            var step = orbit.Period / StepsPerOrbit;
            var lastBurn = ask.LatestUt - fall;
            var steps = (int)Math.Ceiling((lastBurn - ask.EarliestUt) / step) + 2;
            var prev2 = double.NaN;
            var prev = double.NaN;
            var firstOnly = ask.FirstOnly && ask.Craft == null && (!ask.Daylight || ask.SunAt != null);
            for (var i = 0; i <= steps; i++)
            {
                var t = ask.EarliestUt + (i - 1) * step;
                var here = apart(t);
                if (i >= 2 && prev < prev2 && prev <= here)
                {
                    var pinned = BrentMin.Solve(apart, t - 2 * step, t, 0.5, 60);
                    var burn = pinned.Converged ? pinned.X : t - step;
                    if (burn >= ask.EarliestUt && burn <= lastBurn)
                    {
                        var arrive = burn + fall;
                        var cross = Math.Abs(Math.Asin(Math.Max(-1, Math.Min(1, Vec3.Dot(orbit.W, siteAt(arrive)))))) * body.Radius;
                        var line = new LandingPassLine
                        {
                            BurnUt = burn, ArriveUt = arrive, CrossM = cross,
                            CorrectionDv = CorrectionDv(orbit.Mu, orbit.A, ask.PeRadius, cross, body.Radius),
                        };
                        result.Passes.Add(line);
                        if (firstOnly && line.CorrectionDv <= ask.MaxCorrectionDv && (!ask.Daylight || ask.SunAt(arrive) > LandingSiteSearch.MinSunElevationDeg))
                            break;
                    }
                }
                prev2 = prev;
                prev = here;
            }

            var inReach = result.Passes.Where(p => p.CorrectionDv <= ask.MaxCorrectionDv).ToList();
            if (ask.Daylight && inReach.Count > 0 && ask.SunAt != null)
                foreach (var pass in inReach) pass.SunDeg = ask.SunAt(pass.ArriveUt);
            else if (ask.Daylight && inReach.Count > 0)
            {
                var samples = inReach.Select(p => new SiteSample { Latitude = ask.Latitude, Longitude = ask.Longitude, Ut = p.ArriveUt }).ToList();
                api.Survey(body.Name, samples, SiteFacts.Sun);
                for (var i = 0; i < inReach.Count; i++) inReach[i].SunDeg = samples[i].SunElevation;
            }

            result.PeRadius = ask.PeRadius;
            var eligible = new List<LandingPassLine>();
            foreach (var pass in result.Passes)
            {
                pass.Verdict = Rejected(pass, ask);
                pass.SiteLat = ask.Latitude;
                pass.SiteLng = ask.Longitude;
                if (pass.Verdict == "") eligible.Add(pass);
            }

            // Each pass's descent over the terrain under its approach (#511): northbound and southbound passes cross different
            // ground, so it is planned per approach (the same heading over the same site is planned once).
            var planning = ask.Craft != null && ask.Craft.CanFly;
            var clearing = eligible;
            string terrain = null;
            if (planning)
            {
                var planned = ask.Planned ?? new Dictionary<string, DescentChoice>();
                clearing = new List<LandingPassLine>();
                foreach (var pass in eligible)
                {
                    if (ask.Deadline != null && ask.Deadline.Passed)
                    {
                        result.OutOfTime = true;
                        result.Error = "out of time before every pass over the site had its descent planned";
                        return result;
                    }
                    var choice = PlanPass(body, orbit, now, ask, api, lonOffset, handed, pass, ask.Latitude, ask.Longitude, planned);
                    if (choice.Best == null && ask.MoveSite && !ask.Shape.HasValue)
                    {
                        // The nearest site within 5 km that some shape clears on this pass.
                        foreach (var off in DescentApproach.Neighbours)
                        {
                            double nLat, nLng;
                            DescentApproach.Destination(ask.Latitude, ask.Longitude, off[1], off[0], body.Radius, out nLat, out nLng);
                            var moved = PlanPass(body, orbit, now, ask, api, lonOffset, handed, pass, nLat, nLng, planned);
                            if (moved.Best == null) continue;
                            choice = moved;
                            pass.SiteLat = nLat;
                            pass.SiteLng = nLng;
                            pass.Moved = true;
                            break;
                        }
                    }
                    if (choice.Best == null)
                    {
                        pass.Verdict = choice.Error;
                        terrain = terrain ?? choice.Error;
                        if (double.IsNaN(result.ShapeLo)) result.ShapeLo = choice.ShapeLo;
                        if (double.IsNaN(result.ShapeHi)) result.ShapeHi = choice.ShapeHi;
                        result.TerrainPasses++;
                        continue;
                    }
                    pass.Descent = choice.Best;
                    pass.LandingDv = choice.Best.Dv + pass.CorrectionDv;
                    clearing.Add(pass);
                }
            }

            if (clearing.Count > 0)
            {
                var chosen = clearing[planning ? ChoosePass(clearing.Select(p => p.LandingDv).ToList()) : 0];
                var after = clearing.FirstOrDefault(p => p.ArriveUt > chosen.ArriveUt);
                var cheapest = planning ? clearing.Min(p => p.LandingDv) : 0;
                foreach (var pass in clearing)
                    if (pass != chosen && pass != after)
                        pass.Verdict = "clears, " + Whole(pass.LandingDv) + " m/s: over 10% more than the cheapest pass, " + Whole(cheapest) + " m/s";
                chosen.Verdict = "chosen";
                if (after != null) after.Verdict = "next";
                // The trace runs to the next pass, past which nothing was decided.
                if (after != null) result.Passes.RemoveRange(result.Passes.IndexOf(after) + 1, result.Passes.Count - result.Passes.IndexOf(after) - 1);
                result.BurnUt = chosen.BurnUt;
                result.ArriveUt = chosen.ArriveUt;
                result.CorrectionDv = chosen.CorrectionDv;
                result.CrossM = chosen.CrossM;
                result.SunDeg = chosen.SunDeg;
                result.SiteLat = chosen.SiteLat;
                result.SiteLng = chosen.SiteLng;
                result.Moved = chosen.Moved;
                if (after != null) result.NextPassUt = after.ArriveUt;
                if (chosen.Descent != null)
                {
                    // The descent's own periapsis: the burn stays put, the fall to it lengthens or shortens by seconds.
                    result.Descent = chosen.Descent;
                    result.LandingDv = chosen.LandingDv;
                    result.PeRadius = body.Radius + chosen.Descent.PeAlt;
                    result.FallS = FallSeconds(orbit.Mu, orbit.A, result.PeRadius);
                    result.ArriveUt = chosen.BurnUt + result.FallS;
                }
                return result;
            }

            var hours = Tenths((ask.LatestUt - now) / 3600);
            if (terrain != null)
                result.Error = ask.Shape.HasValue ? terrain
                    : "terrain on the approach: no Descent Shape clears it on any pass in the next " + hours + " h (" + terrain.Replace("terrain on the approach: ", "") + ")";
            else if (result.Passes.Count == 0)
                result.Error = "no pass over the site in the next " + hours + " h";
            else if (inReach.Count == 0)
            {
                var nearest = result.Passes.OrderBy(p => p.CorrectionDv).First();
                result.Error = "the site is " + Tenths(nearest.CrossM / 1000) + " km off the ground track at its nearest pass in the next " + hours
                    + " h, a " + Whole(nearest.CorrectionDv) + " m/s correction, over max_correction_dv " + Tenths(ask.MaxCorrectionDv)
                    + " m/s: plan a plane change first, or raise max_correction_dv";
            }
            else
                result.Error = "no daylight pass over the site in the next " + hours + " h on this orbit: the sun never gets over "
                    + Whole(LandingSiteSearch.MinSunElevationDeg) + " degrees there at a pass in reach; pick a lower-latitude site, change the orbit's plane, or land with daylight false";
            return result;
        }

        /// <summary>
        /// The pass <c>land</c> takes among those whose descent clears (#511), by their landing delta-v in pass order: the soonest
        /// within 10% of the cheapest. An index into <paramref name="landingDvs"/>.
        /// </summary>
        public static int ChoosePass(IList<double> landingDvs)
        {
            var cheapest = landingDvs.Min();
            for (var i = 0; i < landingDvs.Count; i++)
                if (landingDvs[i] <= 1.1 * cheapest) return i;
            return 0;
        }

        /// <summary>The descent over a pass's approach to (<paramref name="lat"/>, <paramref name="lng"/>), planned once per site and heading.</summary>
        private static DescentChoice PlanPass(SnapshotBody body, Conic orbit, double now, LandingPassAsk ask, IPlanningBody api, double lonOffset, double handed,
            LandingPassLine pass, double lat, double lng, Dictionary<string, DescentChoice> planned)
        {
            var turnRate = body.RotationPeriod != 0 ? 360 / body.RotationPeriod : 0;
            var phi = handed * (lng - lonOffset + turnRate * (pass.ArriveUt - now)) * Math.PI / 180;
            var latR = lat * Math.PI / 180;
            var site = new Vec3(Math.Cos(latR) * Math.Cos(phi), Math.Cos(latR) * Math.Sin(phi), Math.Sin(latR));
            var speed = Math.Sqrt(orbit.Mu * (2 / ask.PeRadius - 2 / (orbit.A + ask.PeRadius)));
            double heading, vRot;
            DescentApproach.Heading(orbit.W, site, speed, body.Radius, body.RotationPeriod, handed, out heading, out vRot);
            pass.Heading = heading;
            var step = ask.HeadingStepDeg;
            var key = string.Format(CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000},{2:0}", lat, lng, Math.Round(heading / step) * step % 360);
            DescentChoice choice;
            if (!planned.TryGetValue(key, out choice))
            {
                choice = DescentApproach.Plan(ask.Craft, body, api, orbit.A, lat, lng, heading, vRot, ask.Shape, ask.Margin, ask.Shapes, ask.FallStepM);
                if (choice.Best != null) choice.Best.Heading = heading;
                planned[key] = choice;
            }
            return choice;
        }

        /// <summary>Why <c>land</c> won't plan for a pass, or "": a correction over the most allowed, or dark with daylight wanted.</summary>
        private static string Rejected(LandingPassLine pass, LandingPassAsk ask)
        {
            if (pass.CorrectionDv > ask.MaxCorrectionDv)
                return "a " + Whole(pass.CorrectionDv) + " m/s correction, over max_correction_dv " + Tenths(ask.MaxCorrectionDv)
                    + " (" + Tenths(pass.CrossM / 1000) + " km off the track)";
            if (ask.Daylight && !(pass.SunDeg > LandingSiteSearch.MinSunElevationDeg))
                return "dark: the sun " + Tenths(pass.SunDeg) + " degrees up, not over " + Whole(LandingSiteSearch.MinSunElevationDeg);
            return "";
        }

        private static string Tenths(double x)
        {
            return Math.Round(x, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Whole(double x)
        {
            return Math.Round(x, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
