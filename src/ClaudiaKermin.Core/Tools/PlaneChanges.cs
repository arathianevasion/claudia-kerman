// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// One plane change the search offers for a landing site (#521): a <c>change_orbit</c> set_inc burn Claudia flies before
    /// <c>land</c>, and the landing it sets up on the orbit after it.
    /// </summary>
    internal sealed class PlaneChange
    {
        /// <summary>"cheapest", "soonest", "cheapest and soonest" or "saving" (the optional third, over the current orbit's landing).</summary>
        public string Option = "";
        /// <summary>set_inc's value, degrees, and its at: the burn's UT.</summary>
        public double Inclination;
        public double BurnUt;
        public double PlaneDv;
        /// <summary>The landing on the orbit after the burn, as <c>land</c> plans it (<see cref="LandingPass"/>): deorbit, correction and descent, m/s.</summary>
        public double LandingDv;
        public double DeorbitUt;
        public double ArriveUt;
        /// <summary>The approach's heading across the ground at the site, degrees, and the Descent Shape it flies.</summary>
        public double Heading;
        public double Shape;
        public double SiteLat;
        public double SiteLng;
        public bool Moved;
        /// <summary>The orbit after the burn.</summary>
        public Conic After;

        public double TotalDv
        {
            get { return PlaneDv + LandingDv; }
        }

        /// <summary>"northbound" or "southbound": which way the approach crosses the site.</summary>
        public string Approach
        {
            get { return Heading <= 90 || Heading >= 270 ? "northbound" : "southbound"; }
        }

        /// <summary>The call, as a Mission Script makes it.</summary>
        public string Call
        {
            get
            {
                return "change_orbit(lexicon(\"goal\", \"set_inc\", \"value\", " + Inclination.ToString("0.###", CultureInfo.InvariantCulture)
                    + ", \"at\", " + Math.Round(BurnUt, 1).ToString("0.0", CultureInfo.InvariantCulture) + "))";
            }
        }

        /// <summary>The call and its figures in a line, for <c>land</c>'s report.</summary>
        public string Line(double now)
        {
            return Option + ": " + Call + ", " + Whole(PlaneDv) + " m/s, then land for " + Whole(LandingDv) + " m/s (" + Whole(TotalDv)
                + " m/s in all), touching down at UT " + Whole(ArriveUt) + ", in " + Tenths((ArriveUt - now) / 3600) + " h";
        }

        internal static string Whole(double x)
        {
            return Math.Round(x, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        internal static string Tenths(double x)
        {
            return Math.Round(x, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class PlaneChangeResult
    {
        public readonly List<PlaneChange> Options = new List<PlaneChange>();
        /// <summary>Why none came back where that isn't plain (no craft to land, nothing clears); null otherwise.</summary>
        public string Note;
        /// <summary>The planes tried, and the search's time, ms.</summary>
        public int Planes;
        public double Ms;
    }

    /// <summary>
    /// <c>find_landing_site</c>'s plane changes for a site (#521, ADR 0011): ready-made <c>change_orbit</c> set_inc calls, ranked by
    /// the plane change plus the landing it sets up, that landing being <c>land</c>'s own plan on the orbit after the burn
    /// (<see cref="LandingPass.Find"/>, which plans each pass's descent over the terrain under its approach with
    /// <see cref="BrakePath.Search"/>). Burns are tried round the next orbit at inclinations near the site's latitude and near the
    /// orbit's own; each orbit's first pass in reach is planned on a coarse shape grid, with one descent per few degrees of
    /// heading, and the best few again on every shape over the pass <c>land</c> would take. Only planes whose approach clears the
    /// terrain come back. Unreachable on the orbit as it is, it offers the cheapest (least in all, whatever the wait) and the
    /// soonest (least in all among those landing within an orbit of the earliest landing any plane gives); reachable, a plane
    /// change only where it saves at least <see cref="SavingDv"/> net, or where the orbit as it is would make <c>land</c> move the
    /// site. It never flies anything: the plane change is its own step, before <c>land</c>.
    /// </summary>
    internal static class PlaneChanges
    {
        /// <summary>About the whole search's budget, ms (ADR 0011); the work is bounded by count, so the answer doesn't hang on the clock.</summary>
        public const double BudgetMs = 2000;
        /// <summary>A plane change on a site <c>land</c> can already reach is offered only for at least this net saving, m/s.</summary>
        public const double SavingDv = 20;
        /// <summary>Burn times tried round the next orbit.</summary>
        private const int BurnsPerOrbit = 16;
        /// <summary>The coarse pass's heading step, degrees, and how many planes are planned again on every shape.</summary>
        private const double CoarseHeadingDeg = 3;
        private const int Refined = 3;
        /// <summary>The most planes whose first pass gets a coarse descent.</summary>
        private const int Coarse = 48;
        /// <summary>Inclinations tried, degrees: over the site's latitude, and either side of the orbit's own.</summary>
        private static readonly double[] OverLatitude = { -2, -1, 0, 0.5, 1, 2, 3, 5, 8, 12, 18, 25, 35 };
        private static readonly double[] RoundOrbit = { -8, -4, -2, -1, -0.5, 0.5, 1, 2, 4, 8 };

        /// <summary>The coarse shape grid: four of <see cref="BrakePath.Shapes"/>, from the steepest to the shallowest.</summary>
        private static IList<double> CoarseShapes
        {
            get { return new[] { BrakePath.Shapes[0], BrakePath.Shapes[4], BrakePath.Shapes[8], BrakePath.Shapes[BrakePath.Shapes.Length - 1] }; }
        }

        /// <summary>
        /// The plane changes for the site <paramref name="ask"/> names (its window, daylight, reach, craft and margin), from
        /// <paramref name="orbit"/> at <paramref name="now"/>. <paramref name="currentDv"/> is the landing <c>land</c> plans on the
        /// orbit as it is (NaN: it can't reach the site), and <paramref name="currentMoved"/> whether that landing moves the site.
        /// </summary>
        public static PlaneChangeResult Find(SnapshotBody body, Conic orbit, double now, LandingPassAsk ask, IPlanningBody api, double currentDv, bool currentMoved)
        {
            var watch = Stopwatch.StartNew();
            var result = new PlaneChangeResult();
            if (ask.Craft == null || !ask.Craft.CanFly)
            {
                result.Note = "no plane change worked out: the craft has no engine to land with";
                return result;
            }
            if (!orbit.Closed || orbit.Period <= 0) return result;
            var reachable = !double.IsNaN(currentDv);

            // Each plane and its first pass in reach (land's pass search without a craft: reach and daylight only).
            var planes = new List<PlaneChange>();
            foreach (var burn in BurnTimes(orbit, ask.EarliestUt))
            {
                Vec3 r, v;
                orbit.State(burn, out r, out v);
                foreach (var inc in Inclinations(orbit.Inclination, ask.Latitude))
                {
                    string why;
                    var dv = OrbitChange.ChangeInclination(r, v, inc, out why);
                    if (!dv.HasValue || dv.Value.Length < 1) continue;
                    var after = Conic.FromState(orbit.Mu, r, v + dv.Value, burn);
                    var scan = LandingPass.Find(body, after, now, Copy(ask, burn, null), api);
                    result.Planes++;
                    if (scan.Error != null) continue;
                    planes.Add(new PlaneChange { Inclination = inc, BurnUt = burn, PlaneDv = dv.Value.Length, ArriveUt = scan.ArriveUt, After = after });
                }
            }
            if (planes.Count == 0)
            {
                result.Note = "no plane change tried reaches the site in the window";
                return Done(result, watch);
            }

            // The coarse descent over each plane's first pass, one per few degrees of heading: the cheapest plane changes and the
            // soonest landings in turn, up to Coarse planes, so neither option is left out however many planes reach the site.
            var coarse = new Dictionary<string, DescentChoice>();
            var planned = new List<PlaneChange>();
            foreach (var plane in Alternate(planes.OrderBy(p => p.PlaneDv), planes.OrderBy(p => p.ArriveUt)).Take(Coarse))
            {
                var pass = Land(body, plane, now, ask, api, plane.ArriveUt + 60, CoarseShapes, coarse);
                if (pass != null) planned.Add(pass);
            }
            if (planned.Count == 0)
            {
                result.Note = "no plane change tried has an approach to the site that clears the terrain";
                return Done(result, watch);
            }

            // The best few planned again on every shape, over the pass land would take on that plane.
            var earliest = planned.Min(p => p.ArriveUt);
            var soon = earliest + orbit.Period;
            var picks = planned.OrderBy(p => p.TotalDv).Take(Refined).ToList();
            if (!reachable) picks = picks.Concat(planned.Where(p => p.ArriveUt <= soon).OrderBy(p => p.TotalDv).Take(Refined - 1)).Distinct().ToList();
            var refined = new List<PlaneChange>();
            foreach (var pick in picks)
            {
                var pass = Land(body, pick, now, ask, api, pick.ArriveUt + 60, null, null);
                if (pass != null) refined.Add(pass);
            }
            if (refined.Count == 0)
            {
                result.Note = "no plane change tried has an approach to the site that clears the terrain";
                return Done(result, watch);
            }

            var cheapest = refined.OrderBy(p => p.TotalDv).First();
            if (!reachable)
            {
                var group = refined.Where(p => p.ArriveUt <= soon).ToList();
                var soonest = group.Count > 0 ? group.OrderBy(p => p.TotalDv).First() : refined.OrderBy(p => p.ArriveUt).First();
                if (soonest == cheapest)
                {
                    cheapest.Option = "cheapest and soonest";
                    result.Options.Add(cheapest);
                }
                else
                {
                    cheapest.Option = "cheapest";
                    soonest.Option = "soonest";
                    result.Options.Add(cheapest);
                    result.Options.Add(soonest);
                }
            }
            else
            {
                // The optional third: never applied by itself, only offered.
                var offer = currentMoved ? refined.Where(p => !p.Moved).OrderBy(p => p.TotalDv).FirstOrDefault() : cheapest;
                if (offer != null && (currentMoved || currentDv - offer.TotalDv >= SavingDv))
                {
                    offer.Option = "saving";
                    result.Options.Add(offer);
                }
            }
            return Done(result, watch);
        }

        /// <summary>
        /// <c>land</c>'s plan on the plane's orbit, deorbit burns from <see cref="LandingPass.LeadS"/> after the plane change up to
        /// <paramref name="latest"/>: the plane with its landing, or null when no approach clears.
        /// </summary>
        private static PlaneChange Land(SnapshotBody body, PlaneChange plane, double now, LandingPassAsk ask, IPlanningBody api, double latest,
            IList<double> shapes, Dictionary<string, DescentChoice> planned)
        {
            var landAsk = Copy(ask, plane.BurnUt, ask.Craft);
            landAsk.LatestUt = Math.Min(ask.LatestUt, latest);
            landAsk.Shapes = shapes;
            landAsk.Planned = planned;
            landAsk.HeadingStepDeg = planned != null ? CoarseHeadingDeg : 1;
            var found = LandingPass.Find(body, plane.After, now, landAsk, api);
            if (found.Error != null || found.Descent == null) return null;
            return new PlaneChange
            {
                Inclination = plane.Inclination, BurnUt = plane.BurnUt, PlaneDv = plane.PlaneDv, After = plane.After,
                LandingDv = found.LandingDv, DeorbitUt = found.BurnUt, ArriveUt = found.ArriveUt, Heading = found.Descent.Heading,
                Shape = found.Descent.Shape, SiteLat = found.SiteLat, SiteLng = found.SiteLng, Moved = found.Moved,
            };
        }

        /// <summary><paramref name="ask"/> for the orbit after a plane change at <paramref name="burn"/>: deorbit burns from <see cref="LandingPass.LeadS"/> after it.</summary>
        private static LandingPassAsk Copy(LandingPassAsk ask, double burn, BrakeCraft craft)
        {
            return new LandingPassAsk
            {
                Latitude = ask.Latitude, Longitude = ask.Longitude, PeRadius = ask.PeRadius, EarliestUt = Math.Max(ask.EarliestUt, burn + LandingPass.LeadS),
                LatestUt = ask.LatestUt, Daylight = ask.Daylight, MaxCorrectionDv = ask.MaxCorrectionDv, Craft = craft, Margin = ask.Margin, MoveSite = ask.MoveSite,
            };
        }

        /// <summary>The burn times tried: <see cref="BurnsPerOrbit"/> round the next orbit from <paramref name="earliest"/>.</summary>
        private static IEnumerable<double> BurnTimes(Conic orbit, double earliest)
        {
            for (var k = 0; k < BurnsPerOrbit; k++) yield return earliest + k * orbit.Period / BurnsPerOrbit;
        }

        /// <summary>The inclinations tried, degrees: over the site's latitude and either side of the orbit's own, on the orbit's side of 90.</summary>
        private static IEnumerable<double> Inclinations(double current, double siteLat)
        {
            var retrograde = current > 90;
            var own = retrograde ? 180 - current : current;
            var lat = Math.Abs(siteLat);
            var all = OverLatitude.Select(d => lat + d).Concat(RoundOrbit.Select(d => own + d))
                .Where(i => i >= 0 && i <= 90).Select(i => Math.Round(i, 1)).Distinct();
            return all.Select(i => retrograde ? 180 - i : i);
        }

        /// <summary>The items of <paramref name="one"/> and <paramref name="two"/> in turn, each once.</summary>
        private static IEnumerable<PlaneChange> Alternate(IEnumerable<PlaneChange> one, IEnumerable<PlaneChange> two)
        {
            var seen = new HashSet<PlaneChange>();
            using (var a = one.GetEnumerator())
            using (var b = two.GetEnumerator())
            {
                bool moreA = true, moreB = true;
                while (moreA || moreB)
                {
                    if (moreA && (moreA = a.MoveNext()) && seen.Add(a.Current)) yield return a.Current;
                    if (moreB && (moreB = b.MoveNext()) && seen.Add(b.Current)) yield return b.Current;
                }
            }
        }

        private static PlaneChangeResult Done(PlaneChangeResult result, Stopwatch watch)
        {
            result.Ms = watch.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
