// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// A moon's sphere of influence an orbit crosses over a wait (#867): the moon, when it is likely met, and the cheapest fix.
    /// <see cref="MoonCrossings"/> finds one; the plan check, the Debrief, the next-patch watch and <c>transfer_to</c>'s refusal word it.
    /// </summary>
    internal sealed class MoonCrossing
    {
        /// <summary>The body the craft orbits, and the moon whose sphere of influence the orbit reaches.</summary>
        public SnapshotBody Body;
        public SnapshotBody Moon;
        /// <summary>The UT the moon is likely met (the first pass found by following the path); null when none was found in the loops followed.</summary>
        public double? Ut;
        /// <summary>The orbit runs inside the moon's orbit (its periapsis below it): the fix lowers the apoapsis; outside, it raises the periapsis.</summary>
        public bool Inside = true;
        /// <summary>The edge of the moon's sphere of influence the orbit reaches, m from the body's centre: the lowest inside, the highest outside.</summary>
        public double EdgeR;
        /// <summary>The fix: <c>change_orbit</c>'s <see cref="FixOption"/> to this altitude above the body's sea level, m, and its cost, m/s; null when no such burn clears the moon.</summary>
        public double? FixAltitude;
        public double FixDv;
        /// <summary>For comparison, the circle at the periapsis (inside) or apoapsis (outside), m/s.</summary>
        public double CircleDv;

        /// <summary>"set_ap" inside the moon's orbit, "set_pe" outside.</summary>
        public string FixOption
        {
            get { return Inside ? "set_ap" : "set_pe"; }
        }

        /// <summary>The edge as an altitude above the body's sea level, m.</summary>
        public double EdgeAltitude
        {
            get { return EdgeR - Body.Radius; }
        }

        /// <summary>"Ike's sphere of influence (from 1,734 km up) about UT 169,104,601" (the time left out when none was found).</summary>
        public string What()
        {
            return Moon.Name + "'s sphere of influence (from " + Km(EdgeAltitude) + " km " + (Inside ? "up" : "down") + ")" + (Ut.HasValue ? " about UT " + Num(Ut.Value) : "");
        }

        /// <summary>"change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s)", or the circle alone when no such burn clears it.</summary>
        public string Fix()
        {
            if (!FixAltitude.HasValue) return "an orbit clear of " + Moon.Name + " (a circle costs about " + Num(CircleDv) + " m/s)";
            return "change_orbit " + FixOption + " " + Km(FixAltitude.Value) + " km (about " + Num(FixDv) + " m/s; a circle costs about " + Num(CircleDv) + " m/s)";
        }

        /// <summary>
        /// The plan check's and the Debrief's line: "the orbit round Duna crosses Ike's sphere of influence (from 1,734 km up) about
        /// UT 169,104,601: fly change_orbit set_ap 1,520 km (about 26 m/s; a circle costs about 260 m/s) before waiting".
        /// </summary>
        public string Line()
        {
            return "the orbit round " + Body.Name + " crosses " + What() + ": fly " + Fix() + " before waiting";
        }

        /// <summary>
        /// <c>transfer_to</c>'s clause after an ejection that meets the moon: "and the orbit you're in crosses Ike's SOI: lower its apoapsis
        /// below 1,734 km (about 26 m/s) before waiting".
        /// </summary>
        public string RefusalClause(string orbit)
        {
            var fix = !FixAltitude.HasValue ? "leave it"
                : (Inside ? "lower its apoapsis below " : "raise its periapsis above ") + Km(EdgeAltitude) + " km (about " + Num(FixDv) + " m/s)";
            return "and " + orbit + " crosses " + Moon.Name + "'s SOI: " + fix + " before waiting";
        }

        internal static string Num(double x)
        {
            return Math.Round(x).ToString("#,0", CultureInfo.InvariantCulture);
        }

        private static string Km(double m)
        {
            return Math.Round(m / 1000).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Whether an orbit round a body with moons crosses one of their spheres of influence over a wait (#867): one check for the plan
    /// (a Due Time), the Debrief, the next-patch watch (<see cref="NextPatchWatch"/>), <c>transfer_to</c>'s refusal and #866's waiting ellipse.
    /// A wait of up to <see cref="FollowLoops"/> loops is judged by following the path loop by loop (<see cref="SingleShooting.Follow"/>); a longer one by
    /// geometry, since over many loops the moon comes round to wherever the orbit reaches: the orbit crosses when its apoapsis reaches the moon's lowest
    /// sphere-of-influence edge (its periapsis less the radius) and its periapsis the highest (its apoapsis plus the radius). A moon the caller planned to
    /// meet is never a crossing. The fix is the cheapest single burn that clears the edge by <see cref="FixMargin"/>.
    /// </summary>
    internal static class MoonCrossings
    {
        /// <summary>A wait of up to this many loops is followed; a longer one is judged by geometry.</summary>
        internal const int FollowLoops = 3;
        /// <summary>The loops followed to find when a crossing by geometry is likely met: 30, a week at Duna's 19,800 s (908b met Ike on its 13th).</summary>
        internal const int LikelyLoops = 30;
        /// <summary>
        /// The fix clears the edge by this share of the moon's sphere of influence: 20 %, 210 km at Ike, so a small error in the burn or the moon's
        /// own eccentricity can't put it back. 908b's 100 × 2,144 km Duna orbit: set_ap 1,520 km, about 26 m/s, against about 260 m/s for the circle.
        /// </summary>
        internal const double FixMargin = 0.2;

        /// <summary>
        /// The check: the moon <paramref name="orbit"/> round <paramref name="body"/> crosses between <paramref name="fromUt"/> and <paramref name="untilUt"/>,
        /// or null when it crosses none. <paramref name="planned"/>: the bodies a planned encounter meets, which never count.
        /// </summary>
        public static MoonCrossing Judge(BodySystem system, SnapshotBody body, Conic orbit, double fromUt, double untilUt, ICollection<string> planned = null)
        {
            if (!orbit.Closed || untilUt <= fromUt) return null;
            if (untilUt - fromUt <= FollowLoops * orbit.Period) return Follow(system, body, orbit, fromUt, untilUt, planned);
            SnapshotBody first = null;
            var lowest = double.PositiveInfinity;
            var inside = true;
            double edge = 0;
            foreach (var moon in system.Children(body))
            {
                if (Planned(moon, planned)) continue;
                var soi = moon.SoiRadius;
                var low = moon.Sma * (1 - moon.Eccentricity) - soi;
                var high = moon.Sma * (1 + moon.Eccentricity) + soi;
                if (orbit.ApR < low || orbit.PeR > high) continue;
                // The moon met first going out from the periapsis: the lowest edge inside, the highest outside.
                var isInside = orbit.PeR < moon.Sma;
                var key = isInside ? low : -high;
                if (first != null && key >= lowest) continue;
                first = moon;
                lowest = key;
                inside = isInside;
                edge = isInside ? low : high;
            }
            if (first == null) return null;
            var crossing = Fixed(body, first, orbit, inside, edge);
            var likely = Follow(system, body, orbit, fromUt, Math.Min(untilUt, fromUt + LikelyLoops * orbit.Period), planned);
            if (likely != null && likely.Moon == first) crossing.Ut = likely.Ut;
            return crossing;
        }

        /// <summary>The craft's own orbit in <paramref name="snapshot"/> over the wait until <paramref name="untilUt"/>; null when it crosses none or the snapshot has no orbit.</summary>
        public static MoonCrossing Judge(GameSnapshot snapshot, double untilUt, ICollection<string> planned = null)
        {
            if (snapshot == null) return null;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            return setup == null ? null : Judge(setup.System, setup.Body, setup.Start, setup.Now, untilUt, planned);
        }

        /// <summary>The craft's own orbit over a wait not known yet (the Debrief's fact): long, so judged by geometry, the likely time from <see cref="LikelyLoops"/> followed.</summary>
        public static MoonCrossing Judge(GameSnapshot snapshot)
        {
            if (snapshot == null) return null;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null || !setup.Start.Closed) return null;
            return Judge(setup.System, setup.Body, setup.Start, setup.Now, setup.Now + LikelyLoops * setup.Start.Period);
        }

        /// <summary>
        /// The first moon the path meets between <paramref name="fromUt"/> and <paramref name="untilUt"/>, followed loop by loop, with the UT it is met;
        /// null when it meets none first, or first meets a planned one, leaves the body, or comes down.
        /// </summary>
        public static MoonCrossing Follow(BodySystem system, SnapshotBody body, Conic orbit, double fromUt, double untilUt, ICollection<string> planned = null)
        {
            var loop = orbit.Closed ? orbit.Period : double.PositiveInfinity;
            for (var start = fromUt; start < untilUt; start += loop)
            {
                var path = SingleShooting.Follow(system, body, orbit, start, 1);
                if (path == null || path.Count == 0) return null;
                var patch = path[0];
                if (patch.End == PatchEnd.Encounter)
                {
                    if (patch.EndUt > untilUt || Planned(patch.Next, planned)) return null;
                    var inside = orbit.PeR < patch.Next.Sma;
                    var crossing = Fixed(body, patch.Next, orbit, inside,
                        inside ? patch.Next.Sma * (1 - patch.Next.Eccentricity) - patch.Next.SoiRadius : patch.Next.Sma * (1 + patch.Next.Eccentricity) + patch.Next.SoiRadius);
                    crossing.Ut = patch.EndUt;
                    return crossing;
                }
                if (patch.End != PatchEnd.Stable || patch.Error != null) return null;
            }
            return null;
        }

        private static bool Planned(SnapshotBody moon, ICollection<string> planned)
        {
            return planned != null && planned.Any(p => string.Equals(p, moon.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The crossing with its fix: inside, the apoapsis lowered at the periapsis; outside, the periapsis raised at the apoapsis; each to <see cref="FixMargin"/> past the edge, rounded to 10 km.</summary>
        private static MoonCrossing Fixed(SnapshotBody body, SnapshotBody moon, Conic orbit, bool inside, double edge)
        {
            var mu = orbit.Mu;
            var rp = orbit.PeR;
            var ra = orbit.ApR;
            var crossing = new MoonCrossing { Body = body, Moon = moon, Inside = inside, EdgeR = edge };
            var clear = inside ? edge - FixMargin * moon.SoiRadius : edge + FixMargin * moon.SoiRadius;
            var altitude = inside ? Math.Floor((clear - body.Radius) / 10000) * 10000 : Math.Ceiling((clear - body.Radius) / 10000) * 10000;
            var to = body.Radius + altitude;
            if (inside)
            {
                crossing.CircleDv = Speed(mu, rp, ra) - Math.Sqrt(mu / rp);
                if (to > rp && altitude > body.AtmosphereHeight)
                {
                    crossing.FixAltitude = altitude;
                    crossing.FixDv = Speed(mu, rp, ra) - Speed(mu, rp, to);
                }
            }
            else
            {
                crossing.CircleDv = Math.Sqrt(mu / ra) - Speed(mu, ra, rp);
                if (to < ra && (body.SoiRadius <= 0 || to < body.SoiRadius))
                {
                    crossing.FixAltitude = altitude;
                    crossing.FixDv = Speed(mu, ra, to) - Speed(mu, ra, rp);
                }
            }
            return crossing;
        }

        /// <summary>The speed at radius <paramref name="r"/> of an orbit whose other apsis is at <paramref name="other"/>, m/s (vis-viva).</summary>
        private static double Speed(double mu, double r, double other)
        {
            return Math.Sqrt(mu * (2 / r - 2 / (r + other)));
        }
    }
}
