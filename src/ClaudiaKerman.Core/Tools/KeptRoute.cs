// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>The high orbit <c>capture</c> keeps for a moon (#866): its apoapsis, and the periapsis <c>transfer_to</c> leaves from.</summary>
    internal sealed class KeptPlan
    {
        public SnapshotBody Moon;
        /// <summary>The ellipse's apoapsis radius, m from the body's centre.</summary>
        public double HighR;
        /// <summary>The departure <see cref="KeptRoute.Departure"/> found from it, and which periapsis after the capture it is at (1: the next).</summary>
        public TransferPlan Departure;
        public int Loop;
        /// <summary>The apoapsis was chosen so that periapsis meets the window; false: the highest safe one, the window a loop or more on.</summary>
        public bool Timed;
    }

    /// <summary>
    /// #866: arriving at a planet with a moon of it next, <c>capture</c> keeps a high ellipse timed to meet the moon, and <c>transfer_to</c> leaves from it at
    /// a periapsis, which saves the low circle (about 590 m/s at Ike in flight 908b). Only for a moon reached at the end of an interplanetary transfer: a
    /// planet round the star, not home. The window is a departure at a periapsis that costs no more than <see cref="LineUpShare"/> over the Hohmann
    /// trip from that periapsis (<see cref="Ideal"/>), solved by <see cref="Transfer"/>; the wait is up to <see cref="MaxLoops"/> loops, every one of them
    /// followed by <see cref="MoonCrossings.Follow"/> so the path meets nothing before it, and never past the Mission Plan's next fixed date.
    /// </summary>
    internal static class KeptRoute
    {
        /// <summary>The longest wait, in loops of the kept ellipse: the moon-crossing check's own followed span.</summary>
        internal const int MaxLoops = MoonCrossings.FollowLoops;
        /// <summary>A window costs at most this share of the Hohmann trip from the periapsis over it, and at least <see cref="LineUpFloor"/> m/s.</summary>
        internal const double LineUpShare = 0.1;
        internal const double LineUpFloor = 25;
        /// <summary>The departure is searched this far either side of the periapsis, as true anomaly (radians): near it, where the burn is cheapest.</summary>
        private const double NearPe = 40 * Math.PI / 180;
        /// <summary>Apoapsis radii scanned for one whose periapsis meets the window, and the most of those found that are solved on <see cref="Transfer"/>.</summary>
        private const int Scans = 240;
        private const int Tries = 8;
        /// <summary>The Delta-v Map's high orbit, as a share of the planet's sphere of influence: <c>capture</c>'s highest (<see cref="CaptureFlight"/>).</summary>
        internal const double PriceShare = 0.5;
        /// <summary>A moon whose apoapsis reaches past <see cref="PriceShare"/> (Gilly's, at 0.57 of Eve's): the ellipse reaches this share past it, up to <see cref="MaxShare"/> of the sphere.</summary>
        internal const double MoonMargin = 0.1;
        internal const double MaxShare = 0.75;

        /// <summary>
        /// The kept ellipse's highest apoapsis round a planet whose sphere of influence is <paramref name="soi"/>, m from its centre: <see cref="PriceShare"/> of it,
        /// or <see cref="MoonMargin"/> past <paramref name="moon"/>'s apoapsis when that is higher; 0 when that would pass <see cref="MaxShare"/>.
        /// </summary>
        internal static double TopR(double soi, SnapshotBody moon)
        {
            var past = moon.Sma * (1 + moon.Eccentricity) * (1 + MoonMargin);
            return past > MaxShare * soi ? 0 : Math.Max(PriceShare * soi, past);
        }

        /// <summary>
        /// The moon the Mission Plan's next trip for this craft goes to, when it is a moon of <paramref name="body"/> and <paramref name="body"/> is a planet
        /// reached by an interplanetary transfer; else null, with <paramref name="why"/> saying why ("" with no Mission Plan). <paramref name="deadline"/> is
        /// the earliest Due Time of the legs after that trip, which the moon must be reached by; null for none.
        /// </summary>
        public static SnapshotBody NextMoon(GameSnapshot snapshot, BodySystem system, SnapshotBody body, out double? deadline, out string why)
        {
            deadline = null;
            why = "";
            var legs = snapshot.LegsAhead;
            if (legs == null || legs.Count == 0) return null;
            why = "the next leg isn't to a moon of " + body.Name;
            if (!Interplanetary(system, body)) return null;
            for (var i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (leg.Manual || !(string.IsNullOrEmpty(leg.Vessel) || string.Equals(leg.Vessel, "craft", StringComparison.OrdinalIgnoreCase))) continue;
                var words = SequenceWords.ActionWords(leg);
                if (SequenceWords.Trip.IsMatch(words) && !SequenceWords.ReturnHome.IsMatch(words))
                {
                    // A name that says only "Transfer": its intent names where to.
                    var target = SequenceWords.TripTarget(snapshot, words, body, CraftCheckFlow.HomeBody) ?? SequenceWords.TripTarget(snapshot, SequenceWords.Of(leg), body, CraftCheckFlow.HomeBody);
                    if (target != null && target != body && string.Equals(target.Parent, body.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        why = "";
                        var moon = system.Find(target.Name);
                        deadline = FixedDate(legs, i, moon);
                        return moon;
                    }
                    // The leg in hand's trip is the one that came here; it may land too.
                    if (i != 0) return null;
                }
                // A landing, ascent or entry first (the leg in hand's too: "Capture and land at Duna") starts from a low orbit.
                if ((SequenceWords.Landing.IsMatch(words) || SequenceWords.Ascent.IsMatch(words) || SequenceWords.Reentry.IsMatch(words)
                    || SequenceWords.Aero.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words))) return null;
            }
            return null;
        }

        /// <summary>
        /// The UT the moon must be reached by (#881): the first leg after the trip at <paramref name="trip"/> with a fixed date, a Due Time (the outline carries no
        /// return window: <see cref="PlannedSequence"/> has no such field), less one low orbit of <paramref name="moon"/> for each leg in between; null with no such leg.
        /// </summary>
        private static double? FixedDate(IList<PlannedSequence> legs, int trip, SnapshotBody moon)
        {
            var orbit = DeltaVMap.LowOrbit(moon);
            var lap = 2 * Math.PI * Math.Sqrt(orbit * orbit * orbit / moon.Gm);
            for (var j = trip + 1; j < legs.Count; j++)
            {
                if (legs[j].DueTimeUt.HasValue) return legs[j].DueTimeUt.Value - (j - trip - 1) * lap;
            }
            return null;
        }

        /// <summary>
        /// The window UT of the ellipse the craft in <paramref name="snapshot"/> keeps for the next leg's moon (<see cref="NextMoon"/>, from its
        /// <see cref="GameSnapshot.LegsAhead"/>): the orbit shaped for it (<see cref="IsKept"/>) with a <see cref="Departure"/> within <see cref="MaxLoops"/>
        /// loops; null otherwise. The Debrief judges the wait up to it only: the moon is met after that burn, as planned.
        /// </summary>
        public static double? KeptWindow(GameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.LegsAhead == null) return null;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return null;
            double? deadline;
            string why;
            var moon = NextMoon(snapshot, setup.System, setup.Body, out deadline, out why);
            if (moon == null || !IsKept(setup.System, setup.Body, setup.Start, moon)) return null;
            int loop;
            var plan = Departure(snapshot, setup.System, setup.Body, setup.Start, moon, DeltaVMap.LowOrbit(moon) - moon.Radius, setup.Now, setup.Now + MaxLoops * setup.Start.Period, deadline, out loop);
            return plan != null ? plan.Ut : (double?)null;
        }

        /// <summary>A planet round the star, not home: what an interplanetary transfer arrives at.</summary>
        public static bool Interplanetary(BodySystem system, SnapshotBody body)
        {
            var parent = system.Parent(body);
            return parent != null && system.Parent(parent) == null && !string.Equals(body.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The orbit is a kept ellipse for <paramref name="moon"/>: round its planet, inside the moon's orbit at periapsis and reaching past it at apoapsis.</summary>
        public static bool IsKept(BodySystem system, SnapshotBody body, Conic orbit, SnapshotBody moon)
        {
            return orbit.Closed && Interplanetary(system, body) && string.Equals(moon.Parent, body.Name, StringComparison.OrdinalIgnoreCase)
                && orbit.PeR < moon.Sma * (1 - moon.Eccentricity) && orbit.ApR >= moon.Sma * (1 + moon.Eccentricity);
        }

        /// <summary>
        /// The window from <paramref name="orbit"/> (round <paramref name="body"/>, as at <paramref name="snapshot"/>'s time) to <paramref name="moon"/>: the first
        /// periapsis from <paramref name="fromUt"/> to <paramref name="untilUt"/> where <see cref="Transfer"/> leaves near it for no more than the Hohmann trip
        /// from it allows, the path meeting nothing before, arriving by <paramref name="deadline"/>; null for none. <paramref name="loop"/>: which periapsis.
        /// </summary>
        public static TransferPlan Departure(GameSnapshot snapshot, BodySystem system, SnapshotBody body, Conic orbit, SnapshotBody moon, double targetPe,
            double fromUt, double untilUt, double? deadline, out int loop)
        {
            loop = 0;
            if (!orbit.Closed) return null;
            var at = snapshot.At(snapshot.Ut);
            at.Orbit = Kos.BlockTestTarget.Elements(orbit, body.Name);
            at.Situation = "ORBITING";
            at.Body = body.Name;
            var near = TimeFromPe(orbit, NearPe);
            var ideal = Ideal(orbit, moon, targetPe);
            var pe = orbit.NextPeriapsis(fromUt - near);
            for (var k = 1; pe.HasValue && pe.Value <= untilUt && k <= MaxLoops + 1; k++, pe = pe.Value + orbit.Period)
            {
                var from = Math.Max(fromUt, pe.Value - near);
                var plan = Transfer.Find(at, new TransferAsk { Target = moon.Name, TargetPe = targetPe, EarliestUt = from, LatestUt = pe.Value + near, BrokenPlane = false });
                if (plan.Error != null || plan.Total(true) > ideal + Math.Max(LineUpFloor, LineUpShare * ideal)) continue;
                if (deadline.HasValue && plan.ArrivalUt > deadline.Value) return null;
                if (!Clear(system, body, orbit, snapshot.Ut, plan.Ut)) return null;
                loop = k;
                return plan;
            }
            return null;
        }

        /// <summary>
        /// capture's plan, at the arrival periapsis <paramref name="peUt"/> (<paramref name="r"/>, the plane's normal <paramref name="normal"/>): the lowest
        /// apoapsis from the moon's orbit up to <paramref name="topR"/> whose next periapsis (else the second, the third) is a window; else the highest
        /// safe one (<paramref name="topR"/> down by halves, as <see cref="CaptureFlight"/>'s shares) with a window within <see cref="MaxLoops"/> loops; else
        /// null, <paramref name="why"/> saying why. The periapsis kept is <paramref name="keptPeR"/>: one off the arrival's is set at the first apoapsis.
        /// </summary>
        public static KeptPlan Plan(GameSnapshot snapshot, BodySystem system, SnapshotBody body, SnapshotBody moon, Vec3 r, Vec3 normal, double peUt,
            double keptPeR, double topR, double targetPe, double? deadline, out string why)
        {
            why = "";
            var mu = body.Gm;
            var lowR = moon.Sma * (1 + moon.Eccentricity);
            if (topR <= lowR)
            {
                why = "no high orbit fits inside " + body.Name + "'s sphere of influence above " + moon.Name;
                return null;
            }
            var moonOrbit = system.OrbitOf(moon);
            var rp = r.Length;
            var across = r.Normalized * -1;
            var side = Vec3.Cross(normal, across);
            // The Hohmann time from the kept periapsis to the moon's orbit, arriving opposite it.
            var hohmann = Math.PI * Math.Sqrt(Math.Pow((keptPeR + moon.Sma) / 2, 3) / mu);
            Func<double, int, double> departUt = (highR, k) => peUt + Period(mu, rp, highR) / 2 + (k - 0.5) * Period(mu, keptPeR, highR);
            Func<double, int, double> miss = (highR, k) =>
            {
                var at = moonOrbit.Position(departUt(highR, k) + hohmann);
                return Math.Atan2(Vec3.Dot(at, side), Vec3.Dot(at, across));
            };

            // The apoapsis radii where the moon is opposite the periapsis when the Hohmann trip from it arrives: lowest first, the next periapsis first.
            var tried = 0;
            for (var k = 1; k <= MaxLoops && tried < Tries; k++)
            {
                var lastR = lowR;
                var last = miss(lowR, k);
                for (var i = 1; i <= Scans && tried < Tries; i++)
                {
                    var highR = lowR * Math.Pow(topR / lowR, (double)i / Scans);
                    var now = miss(highR, k);
                    // A sign change through 0, not the jump at half a turn.
                    if (Math.Sign(now) != Math.Sign(last) && Math.Abs(now - last) < Math.PI)
                    {
                        var root = Bisect(x => miss(x, k), lastR, highR);
                        tried++;
                        var plan = Try(snapshot, system, body, moon, r, normal, peUt, keptPeR, root, targetPe, deadline, k, k);
                        if (plan != null)
                        {
                            plan.Timed = true;
                            return plan;
                        }
                    }
                    lastR = highR;
                    last = now;
                }
            }

            // None lines up: the highest safe apoapsis, and a wait of up to MaxLoops loops for a window.
            for (var highR = topR; highR > lowR; highR /= 2)
            {
                var plan = Try(snapshot, system, body, moon, r, normal, peUt, keptPeR, highR, targetPe, deadline, 1, MaxLoops);
                if (plan != null) return plan;
            }
            why = "no " + moon.Name + " window within " + MaxLoops + " loops of a safe high orbit" + (deadline.HasValue ? " before the next fixed date" : "");
            return null;
        }

        /// <summary>The plan with the ellipse to <paramref name="highR"/> from <paramref name="r"/>: a window at periapsis <paramref name="first"/> to <paramref name="last"/>, the path clear before it; null otherwise.</summary>
        private static KeptPlan Try(GameSnapshot snapshot, BodySystem system, SnapshotBody body, SnapshotBody moon, Vec3 r, Vec3 normal, double peUt,
            double keptPeR, double highR, double targetPe, double? deadline, int first, int last)
        {
            var mu = body.Gm;
            var rp = r.Length;
            var ellipse = Conic.FromState(mu, r, Vec3.Cross(normal, r.Normalized) * Speed(mu, rp, highR), peUt);
            var apUt = peUt + ellipse.Period / 2;
            var kept = ellipse;
            if (Math.Abs(keptPeR - rp) > 1)
            {
                Vec3 ra, va;
                ellipse.State(apUt, out ra, out va);
                kept = Conic.FromState(mu, ra, va.Normalized * Speed(mu, ra.Length, keptPeR), apUt);
            }
            // The first half loop, to the apoapsis, on the ellipse as captured.
            if (!Clear(system, body, ellipse, peUt, apUt)) return null;
            var firstPe = apUt + kept.Period / 2;
            int loop;
            var at = snapshot.At(apUt);
            // From half a loop before periapsis <paramref name="first"/>, so a burn just before it is tried too.
            var plan = Departure(at, system, body, kept, moon, targetPe, firstPe + (first - 1.5) * kept.Period, firstPe + (last - 1) * kept.Period + 1, deadline, out loop);
            if (plan == null) return null;
            return new KeptPlan { Moon = moon, HighR = highR, Departure = plan, Loop = loop + first - 1 };
        }

        /// <summary>The path meets nothing from <paramref name="fromUt"/> to <paramref name="untilUt"/>, followed loop by loop (#867's check), and stays round the body.</summary>
        private static bool Clear(BodySystem system, SnapshotBody body, Conic orbit, double fromUt, double untilUt)
        {
            if (untilUt <= fromUt) return true;
            var path = SingleShooting.Follow(system, body, orbit, fromUt, 1);
            if (path == null || path.Count == 0) return false;
            if (path[0].End != PatchEnd.Stable && path[0].EndUt <= untilUt) return false;
            return MoonCrossings.Follow(system, body, orbit, fromUt, untilUt) == null;
        }

        /// <summary>
        /// The Hohmann trip from <paramref name="orbit"/>'s periapsis to <paramref name="moon"/>'s orbit: the burn at the periapsis onto it, and the capture into a
        /// circle at <paramref name="targetPe"/> from the speed it arrives with, m/s.
        /// </summary>
        internal static double Ideal(Conic orbit, SnapshotBody moon, double targetPe)
        {
            var mu = orbit.Mu;
            var rp = orbit.PeR;
            var rm = moon.Sma;
            var leave = Math.Abs(Speed(mu, rp, orbit.ApR) - Speed(mu, rp, rm));
            var excess = Math.Abs(Math.Sqrt(mu / rm) - Speed(mu, rm, rp));
            var low = moon.Radius + targetPe;
            return leave + Math.Sqrt(excess * excess + 2 * moon.Gm / low) - Math.Sqrt(moon.Gm / low);
        }

        /// <summary>The seconds from periapsis to true anomaly <paramref name="nu"/> on <paramref name="orbit"/>.</summary>
        private static double TimeFromPe(Conic orbit, double nu)
        {
            return orbit.MeanFromTrue(nu) / (2 * Math.PI / orbit.Period);
        }

        private static double Bisect(Func<double, double> f, double lo, double hi)
        {
            var fLo = f(lo);
            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) / 2;
                var fMid = f(mid);
                if (Math.Sign(fMid) == Math.Sign(fLo))
                {
                    lo = mid;
                    fLo = fMid;
                }
                else hi = mid;
            }
            return (lo + hi) / 2;
        }

        private static double Period(double mu, double r1, double r2)
        {
            var a = (r1 + r2) / 2;
            return 2 * Math.PI * Math.Sqrt(a * a * a / mu);
        }

        /// <summary>The speed at radius <paramref name="r"/> of an orbit whose other apsis is <paramref name="other"/>, m/s (vis-viva).</summary>
        internal static double Speed(double mu, double r, double other)
        {
            return Math.Sqrt(mu * (2 / r - 2 / (r + other)));
        }

        /// <summary>"60 x 3,300 km": an orbit's apsides as altitudes over <paramref name="body"/>.</summary>
        internal static string Shape(SnapshotBody body, double peR, double apR)
        {
            return Km(peR - body.Radius) + " x " + Km(apR - body.Radius) + " km";
        }

        private static string Km(double m)
        {
            return Math.Round(m / 1000).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
