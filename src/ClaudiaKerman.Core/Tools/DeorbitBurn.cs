// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What the <c>deorbit</c> block asks for: where to land, and the window to burn in.</summary>
    internal sealed class DeorbitAsk
    {
        public double TargetLatitude;
        public double TargetLongitude;
        /// <summary>The first UT the burn may be at (the block leaves itself time to turn and lead the burn).</summary>
        public double EarliestUt;
        /// <summary>How long after <see cref="EarliestUt"/> the burn may wait, s.</summary>
        public double MaxWait;
        /// <summary>The ground's height above sea level at the target, m.</summary>
        public double GroundAltitude;
        public double AccuracyKm = 5;
        /// <summary>The engines' Isp, s, for the mass after the burn; 0 takes the mass as it is now.</summary>
        public double Isp;
        /// <summary>Who pays the burn when the craft leaves parts out (#814, #854: <see cref="EntryCraft"/>'s masses); null: the craft pays it all.</summary>
        public DropBurn Payer;
        /// <summary>Whether the predictor opens the craft's stowed chutes (#477); false keeps them stowed.</summary>
        public bool Chutes = true;
        /// <summary>m/s the burn may cost (#794); 0 or less is "auto": <see cref="DeorbitBurn.AutoMaxDv"/> of <see cref="DeltaVLeft"/>.</summary>
        public double MaxDv;
        /// <summary>The delta-v stock reports left, m/s; 0 or less when it can't say.</summary>
        public double DeltaVLeft;
    }

    /// <summary>The burn <see cref="DeorbitBurn.Find"/> chose, in maneuver-node components, and where it lands.</summary>
    internal sealed class DeorbitPlan
    {
        /// <summary>Null when the burn lands within the accuracy; otherwise why the block must not burn.</summary>
        public string Error;
        public double BurnUt = -1;
        /// <summary>m/s; prograde is negative (a retrograde burn).</summary>
        public double Prograde;
        public double Normal;
        public double DeltaV;
        /// <summary>The landing of the best burn found; null when none could be predicted.</summary>
        public ReentryPrediction Landing;
        /// <summary>The orbit after the burn, as the predictor flew it (#791).</summary>
        public Conic After;
        /// <summary>km from the target to <see cref="Landing"/>; -1 without one.</summary>
        public double ErrorKm = -1;
    }

    /// <summary>
    /// The deorbit burn search (#282): the burn, within the window, that brings the craft down on the target by the reentry
    /// predictor. The burn is retrograde to a periapsis well inside the atmosphere (a steep entry is the least sensitive to
    /// burn and drag errors, the research found), plus a normal part for a target beside the ground track. A scan of the window
    /// with the predictor picks the passes worth a look (on an eccentric orbit one prediction says little about the next); Newton's
    /// method on the burn time (along the track) and the normal part (across it) lands each on the target; each that lands within
    /// accuracy_km then walks toward less delta-v inside the band. The cheapest is kept, an earlier one on a near tie (#794).
    /// </summary>
    internal static class DeorbitBurn
    {
        /// <summary>The periapsis aimed for, as a share of the atmosphere's height (21 km at Kerbin, 27 km at Eve, 15 km at Duna and Laythe).</summary>
        public const double EntryPeriapsisShare = 0.3;
        /// <summary>The most normal delta-v the search spends on a target beside the ground track, m/s.</summary>
        public const double MaxNormal = 50;
        /// <summary>The auto <c>max_dv</c> keeps back the larger of this, m/s, or <see cref="ReserveShare"/> of the delta-v left (#794).</summary>
        public const double ReserveMin = 50;
        public const double ReserveShare = 0.1;
        /// <summary>Burns within accuracy whose cost differs by less than this are a tie, and the earlier wins, m/s.</summary>
        internal const double TieDv = 1;
        private const double G0 = 9.80665;
        private const int NewtonSteps = 8;
        /// <summary>Scan samples are at most this far apart in true anomaly, deg (even in the seconds near a periapsis)...</summary>
        private const double ScanTrueAnomalyDeg = 30;
        /// <summary>...and at most this share of a turn of the body (even in the hours near an apoapsis).</summary>
        private const double ScanRotationShare = 1.0 / 24;
        /// <summary>
        /// Predictions the search may spend, so a long max_wait or a far target can't stall the main thread: the scan's (at 12 a
        /// pass, 20 passes: more than a low orbit's whole turn of the body; a longer window is searched that far and the failure
        /// says so), the scan and refining together, and everything with the walks.
        /// </summary>
        private const int MaxScanTries = 240;
        private const int MaxRefineTries = 600;
        private const int MaxTries = 800;
        /// <summary>Passes whose scan cost rounds to the same multiple of this are refined nearest the target first, m/s.</summary>
        private const double PassCostStepDv = 5;
        /// <summary>A walk ends inside this share of accuracy_km: at the very edge the burn's own error would fail the block's check after the burn.</summary>
        private const double WalkBandShare = 0.8;
        private const int WalkRounds = 4;
        private const int WalkHalvings = 3;
        /// <summary>A walk step forecast to save less than this isn't flown, m/s; a walk that saves less than <see cref="TieDv"/> in all isn't kept.</summary>
        private const double WalkFloorDv = 0.1;
        /// <summary>A refined landing this close to the target is done, km.</summary>
        private const double CloseEnoughKm = 0.05;
        /// <summary>Burns this close in error are a tie, and the earlier wins, km.</summary>
        private const double TieKm = 0.5;
        /// <summary>The retrograde part's search: BrentRoot's tolerance relative to max(|burn|, 1) m/s, and its iteration cap.</summary>
        private const double RetroTolerance = 1e-12;
        private const int RetroIterations = 100;

        public static DeorbitPlan Find(SnapshotBody body, Conic orbit, DeorbitAsk ask, IReentryAir air, IReentryCraft craft, BodyFrame frame)
        {
            if (body.AtmosphereHeight <= 0) return Fail("body has no atmosphere");
            if (!orbit.Closed) return Fail("the orbit escapes " + body.Name + ": deorbit burns from a closed orbit, so capture first");
            var top = body.Radius + body.AtmosphereHeight;
            if (orbit.PeR <= top)
                return Fail("the periapsis (" + Whole(orbit.PeR - body.Radius) + " m) is already inside " + body.Name
                    + "'s atmosphere: fly reentry");
            if (ask.MaxDv <= 0 && ask.DeltaVLeft <= 0) return Fail("stock can't say the delta-v left: give max_dv");
            var search = new Search(body, orbit, ask, air, craft, frame);
            return search.Run();
        }

        /// <summary>"" or why the delta-v left (m/s; 0 or less when stock can't say) won't fly a burn of <paramref name="dv"/> m/s.</summary>
        public static string ShortOfDv(double dv, double dvLeft)
        {
            if (dvLeft > 0 && dvLeft < dv)
                return "not enough delta-v: the burn needs " + Dv(dv) + " m/s, " + Dv(dvLeft) + " m/s left";
            return "";
        }

        /// <summary>The auto <c>max_dv</c>: the delta-v left less the reserve (the larger of 50 m/s or 10% of it), less a powered landing's need.</summary>
        public static double AutoMaxDv(double dvLeft, double poweredLandingDv)
        {
            return dvLeft - Math.Max(ReserveMin, ReserveShare * dvLeft) - Math.Max(0, poweredLandingDv);
        }

        /// <summary>The least burn from <paramref name="body"/>'s low circular orbit to the entry periapsis, m/s: <c>deorbit</c>'s from there, for planning (#790).</summary>
        public static double FromLowOrbit(SnapshotBody body)
        {
            var low = DeltaVMap.LowOrbit(body);
            var pe = body.Radius + EntryPeriapsisShare * body.AtmosphereHeight;
            return Math.Sqrt(body.Gm / low) - Math.Sqrt(body.Gm * (2 / low - 2 / (low + pe)));
        }

        /// <summary>What a powered landing keeps back (#790, shared with <c>return</c>'s fix across the track): without chutes, or with them left stowed, the craft lands on its engines, and that costs its speed at the ground.</summary>
        public static double PoweredLandingDv(IReentryCraft craft, bool chutes, double landingSpeed)
        {
            return craft.Chutes == null || craft.Chutes.Count == 0 || !chutes ? landingSpeed : 0;
        }

        /// <summary>
        /// Whether burn <paramref name="a"/> beats <paramref name="b"/> (#794): of burns within <paramref name="accuracyKm"/> the cheaper (the
        /// earlier on a near tie in delta-v), and any such burn beats one outside it; of burns outside it the smaller error (the earlier on a near tie).
        /// </summary>
        internal static bool Better(DeorbitPlan a, DeorbitPlan b, double accuracyKm)
        {
            var aIn = a.ErrorKm <= accuracyKm;
            var bIn = b.ErrorKm <= accuracyKm;
            if (aIn != bIn) return aIn;
            if (aIn)
            {
                if (a.DeltaV < b.DeltaV - TieDv) return true;
                if (a.DeltaV > b.DeltaV + TieDv) return false;
                return a.BurnUt < b.BurnUt;
            }
            if (a.ErrorKm < b.ErrorKm - TieKm) return true;
            if (a.ErrorKm > b.ErrorKm + TieKm) return false;
            return a.BurnUt < b.BurnUt;
        }

        /// <summary>
        /// The burn to fly, with <see cref="DeorbitPlan.Error"/> from <paramref name="overBudget"/> when it is within accuracy. Near ties are
        /// measured from the cheapest, so a chain of them can't walk the choice away from it; when the earlier of a near tie is over budget,
        /// the cheapest answers instead: it may fit, and a refusal must quote the cheapest.
        /// </summary>
        internal static DeorbitPlan Choose(List<DeorbitPlan> burns, double accuracyKm, Func<DeorbitPlan, string> overBudget)
        {
            var cheapest = burns.Where(burn => burn.ErrorKm <= accuracyKm).OrderBy(burn => burn.DeltaV).FirstOrDefault();
            var best = cheapest ?? burns[0];
            foreach (var burn in burns)
                if ((cheapest == null || burn.DeltaV <= cheapest.DeltaV + TieDv) && Better(burn, best, accuracyKm)) best = burn;
            if (cheapest == null) return best;
            best.Error = overBudget(best);
            if (best.Error == null) return best;
            cheapest.Error = overBudget(cheapest);
            return cheapest;
        }

        private static string Dv(double dv)
        {
            return Math.Round(dv, 1).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static DeorbitPlan Fail(string reason)
        {
            return new DeorbitPlan { Error = reason };
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        private sealed class Search
        {
            private readonly SnapshotBody _body;
            private readonly Conic _orbit;
            private readonly DeorbitAsk _ask;
            private readonly IReentryAir _air;
            private readonly IReentryCraft _craft;
            private readonly BodyFrame _frame;
            private readonly double _peRadius;
            private readonly double _latest;
            private int _tries;

            public Search(SnapshotBody body, Conic orbit, DeorbitAsk ask, IReentryAir air, IReentryCraft craft, BodyFrame frame)
            {
                _body = body;
                _orbit = orbit;
                _ask = ask;
                _air = air;
                _craft = craft;
                _frame = frame;
                _peRadius = body.Radius + EntryPeriapsisShare * body.AtmosphereHeight;
                _latest = ask.EarliestUt + Math.Max(0, ask.MaxWait);
            }

            public DeorbitPlan Run()
            {
                var scan = Scan();
                var first = scan.FirstOrDefault(sample => sample.Landing.Error == null);
                if (first == null) return Fail(scan[0].Landing.Error);

                // Scan samples only find the passes: one far inside a wide band must not win a cost tie against a burn on the target by being earlier.
                var burns = new List<DeorbitPlan>();
                foreach (var ut in Passes(scan))
                {
                    if (_tries >= MaxRefineTries) break;
                    var refined = Refine(ut);
                    if (refined != null) burns.Add(refined);
                }
                foreach (var plan in burns.Where(burn => burn.ErrorKm <= _ask.AccuracyKm).OrderBy(burn => burn.DeltaV).ThenBy(burn => burn.BurnUt).ToList())
                {
                    if (_tries >= MaxTries) break;
                    var walked = Walk(plan);
                    if (walked != plan) burns.Add(walked);
                }
                if (burns.Count == 0) burns.Add(first);

                var best = Choose(burns, _ask.AccuracyKm, OverBudget);
                if (best.ErrorKm > _ask.AccuracyKm)
                    best.Error = "the best burn in the next " + Hours(scan[scan.Count - 1].BurnUt - _ask.EarliestUt) + " h lands " + Km(best.ErrorKm)
                        + " km from the target, over accuracy_km " + Km(_ask.AccuracyKm)
                        + (best.Normal != 0 && Math.Abs(best.Normal) >= MaxNormal - 1e-6
                            ? " (the target is too far off the ground track; change the orbit's plane, or allow a longer max_wait)"
                            : "; allow a longer max_wait or a wider accuracy_km");
                return best;
            }

            /// <summary>Null, or why the burn must not fly: more than the delta-v left, or than max_dv (the auto one when none was given).</summary>
            private string OverBudget(DeorbitPlan plan)
            {
                var lacking = ShortOfDv(plan.DeltaV, _ask.DeltaVLeft);
                if (lacking != "") return lacking;
                var powered = PoweredLandingDv(_craft, _ask.Chutes, plan.Landing.LandingSpeed);
                var limit = _ask.MaxDv > 0 ? _ask.MaxDv : AutoMaxDv(_ask.DeltaVLeft, powered);
                if (plan.DeltaV <= limit) return null;
                return "the cheapest burn within accuracy_km needs " + Dv(plan.DeltaV) + " m/s (at UT " + Whole(plan.BurnUt) + "), over max_dv "
                    + Dv(Math.Max(0, limit)) + (_ask.MaxDv > 0 ? "" : " (auto: " + Dv(_ask.DeltaVLeft) + " m/s left, less the reserve and a powered landing)");
            }

            /// <summary>
            /// A burn with no normal part at samples across the window, the predictor in the loop. The samples follow the orbit and the
            /// turning body, so they are close enough to catch every pass: near a periapsis the landing moves fast, near an apoapsis slowly.
            /// </summary>
            private List<DeorbitPlan> Scan()
            {
                var longest = _frame.RotationPeriod > 0 ? _frame.RotationPeriod * ScanRotationShare : double.PositiveInfinity;
                var samples = new List<DeorbitPlan>();
                var ut = _ask.EarliestUt;
                while (true)
                {
                    samples.Add(Try(ut, 0));
                    if (ut >= _latest || _tries >= MaxScanTries) return samples;
                    var next = _orbit.TimeAtTrue(_orbit.TrueAnomaly(ut) + ScanTrueAnomalyDeg * Math.PI / 180, ut).Value;
                    ut = Math.Min(_latest, Math.Min(next, ut + longest));
                }
            }

            /// <summary>The burn times worth refining, the scan's local nearest to the target: the cheapest first so a spent budget drops the dearest, and of those about the same cost (a near-circular orbit) the nearest.</summary>
            private static IEnumerable<double> Passes(List<DeorbitPlan> scan)
            {
                var minima = new List<DeorbitPlan>();
                for (var i = 0; i < scan.Count; i++)
                {
                    var left = i == 0 || scan[i].ErrorKm <= scan[i - 1].ErrorKm;
                    var right = i == scan.Count - 1 || scan[i].ErrorKm <= scan[i + 1].ErrorKm;
                    if (left && right && !double.IsInfinity(scan[i].ErrorKm)) minima.Add(scan[i]);
                }
                return minima.OrderBy(sample => Math.Round(sample.DeltaV / PassCostStepDv)).ThenBy(sample => sample.ErrorKm).Select(sample => sample.BurnUt).ToList();
            }

            /// <summary>Newton's method from a pass: the burn time moves the landing along the track, the normal part across it.</summary>
            private DeorbitPlan Refine(double ut)
            {
                double normal = 0;
                DeorbitPlan best = null;
                for (var step = 0; step < NewtonSteps; step++)
                {
                    var here = Try(ut, normal);
                    if (here.Landing.Error != null) break;
                    if (best == null || here.ErrorKm < best.ErrorKm) best = here;
                    if (here.ErrorKm < CloseEnoughKm) break;
                    var slopes = SlopesAt(here);
                    if (slopes == null) break;
                    // The offsets are of the target from the landing: drive them to 0.
                    double moveT, moveN;
                    slopes.Move(-slopes.East, -slopes.North, out moveT, out moveN);
                    var limit = _orbit.Period / 8;
                    moveT = Math.Max(-limit, Math.Min(limit, moveT));
                    var nextUt = Math.Max(_ask.EarliestUt, Math.Min(_latest, ut + moveT));
                    var nextNormal = Math.Max(-MaxNormal, Math.Min(MaxNormal, normal + moveN));
                    if (Math.Abs(nextUt - ut) < 1e-3 && Math.Abs(nextNormal - normal) < 1e-4) break;
                    ut = nextUt;
                    normal = nextNormal;
                }
                return best;
            }

            /// <summary>
            /// From a burn within accuracy_km, toward the cheapest in the band. With the landing's and the cost's slopes in the burn time and
            /// the normal part, the cost falls fastest toward one point on the band's edge: Newton aims there, the step halved when it
            /// lands outside the band or costs more. Returns <paramref name="start"/> unless the walk saved a real amount, so a burn
            /// that costs the same anywhere (a circular orbit) stays on the target.
            /// </summary>
            private DeorbitPlan Walk(DeorbitPlan start)
            {
                var band = WalkBandShare * _ask.AccuracyKm;
                var here = start;
                for (var round = 0; round < WalkRounds && _tries < MaxTries; round++)
                {
                    var slopes = SlopesAt(here);
                    if (slopes == null) break;
                    double costEast, costNorth;
                    slopes.CostSlope(out costEast, out costNorth);
                    var slope = Math.Sqrt(costEast * costEast + costNorth * costNorth);
                    if (slope <= 0) break;
                    var moveEast = -band * costEast / slope - slopes.East;
                    var moveNorth = -band * costNorth / slope - slopes.North;
                    if (-(costEast * moveEast + costNorth * moveNorth) < WalkFloorDv) break;
                    double moveT, moveN;
                    slopes.Move(moveEast, moveNorth, out moveT, out moveN);
                    var limit = _orbit.Period / 8;
                    moveT = Math.Max(-limit, Math.Min(limit, moveT));
                    DeorbitPlan better = null;
                    var share = 1.0;
                    for (var halving = 0; halving <= WalkHalvings && better == null; halving++, share /= 2)
                    {
                        var normal = Math.Max(-MaxNormal, Math.Min(MaxNormal, here.Normal + share * moveN));
                        // The cost is least near no normal part: a step past it would only pay the same again on the other side.
                        if (normal * here.Normal < 0) normal = 0;
                        var next = Try(Math.Max(_ask.EarliestUt, Math.Min(_latest, here.BurnUt + share * moveT)), normal);
                        if (next.ErrorKm <= band && next.DeltaV < here.DeltaV) better = next;
                    }
                    if (better == null) break;
                    here = better;
                }
                return start.DeltaV - here.DeltaV >= TieDv ? here : start;
            }

            /// <summary>How the target's offset from the landing, km east and north, and the cost, m/s, change with the burn time and the normal part.</summary>
            private sealed class Slopes
            {
                public double East, North, EastByTime, NorthByTime, EastByNormal, NorthByNormal, Det, CostByTime, CostByNormal;

                /// <summary>The change of burn time and normal part that moves the offset by the given km.</summary>
                public void Move(double east, double north, out double moveT, out double moveN)
                {
                    moveT = (east * NorthByNormal - EastByNormal * north) / Det;
                    moveN = (EastByTime * north - NorthByTime * east) / Det;
                }

                /// <summary>The cost's slope in the offset, m/s per km (the inverse transpose of the offset's slopes).</summary>
                public void CostSlope(out double east, out double north)
                {
                    east = (NorthByNormal * CostByTime - NorthByTime * CostByNormal) / Det;
                    north = (EastByTime * CostByNormal - EastByNormal * CostByTime) / Det;
                }
            }

            /// <summary>The slopes at a burn, from two more predictions; null when one fails or the offset's slopes are singular.</summary>
            private Slopes SlopesAt(DeorbitPlan here)
            {
                const double dt = 2;
                const double dn = 1;
                var byTime = Try(here.BurnUt + dt, here.Normal);
                var byNormal = Try(here.BurnUt, here.Normal + dn);
                if (byTime.Landing.Error != null || byNormal.Landing.Error != null) return null;
                double e0, n0, e1, n1, e2, n2;
                Offset(here.Landing, out e0, out n0);
                Offset(byTime.Landing, out e1, out n1);
                Offset(byNormal.Landing, out e2, out n2);
                var slopes = new Slopes
                {
                    East = e0, North = n0, EastByTime = (e1 - e0) / dt, NorthByTime = (n1 - n0) / dt, EastByNormal = (e2 - e0) / dn, NorthByNormal = (n2 - n0) / dn,
                    CostByTime = (byTime.DeltaV - here.DeltaV) / dt, CostByNormal = (byNormal.DeltaV - here.DeltaV) / dn,
                };
                slopes.Det = slopes.EastByTime * slopes.NorthByNormal - slopes.EastByNormal * slopes.NorthByTime;
                return Math.Abs(slopes.Det) < 1e-12 ? null : slopes;
            }

            /// <summary>The burn at <paramref name="ut"/> with <paramref name="normal"/> m/s normal and enough retrograde for the entry periapsis, and where it lands.</summary>
            private DeorbitPlan Try(double ut, double normal)
            {
                _tries++;
                Vec3 r, v;
                _orbit.State(ut, out r, out v);
                var along = v.Normalized;
                // The node's normal is the orbit's real angular momentum: in a mirrored frame (Handed -1) the cross product points the other way.
                var up = Vec3.Cross(r, v).Normalized * _frame.Handed;
                // The periapsis falls as the retrograde part grows: BrentRoot for the one aimed at. When no retrograde part
                // reaches it (or every one passes under it), the end nearer it, as a bisection would settle.
                Func<double, double> under = s => PeR(r, v - along * s + up * normal, ut) - _peRadius;
                var found = BrentRoot.Solve(under, 0, v.Length, RetroTolerance, RetroIterations);
                var usable = found.Converged || found.Status == SearchStatus.IterationCap;
                var retro = usable ? found.X : under(0) <= 0 ? 0 : v.Length;
                var after = Conic.FromState(_body.Gm, r, v - along * retro + up * normal, ut);
                var dv = Math.Sqrt(retro * retro + normal * normal);
                var mass = DropGuard.MassAfterBurn(_craft.MassKg, dv, _ask.Isp, _ask.Payer);
                var landing = ReentryPredictor.Predict(_body, after, ut, _ask.GroundAltitude, mass, _air, _craft, _frame, _ask.Chutes);
                return new DeorbitPlan
                {
                    BurnUt = ut, Prograde = -retro, Normal = normal, DeltaV = dv, Landing = landing, After = after,
                    ErrorKm = landing.Error == null ? Distance(landing.Latitude, landing.Longitude) : double.PositiveInfinity,
                };
            }

            /// <summary>The periapsis radius after the burn; 0 for a burn that leaves the craft falling straight down (no orbit plane).</summary>
            private double PeR(Vec3 r, Vec3 v, double ut)
            {
                return Vec3.Cross(r, v).Length > 0 ? Conic.FromState(_body.Gm, r, v, ut).PeR : 0;
            }

            /// <summary>The target's offset from a landing, km east and north on the ground there.</summary>
            private void Offset(ReentryPrediction landing, out double east, out double north)
            {
                var km = _body.Radius / 1000 * Math.PI / 180;
                east = LandingSiteSearch.Wrap180(_ask.TargetLongitude - landing.Longitude) * Math.Cos(landing.Latitude * Math.PI / 180) * km;
                north = (_ask.TargetLatitude - landing.Latitude) * km;
            }

            private double Distance(double lat, double lng)
            {
                return LandingSiteSearch.ArcDegrees(lat, lng, _ask.TargetLatitude, _ask.TargetLongitude) * Math.PI / 180 * _body.Radius / 1000;
            }
        }

        public static string Km(double km)
        {
            return km.ToString(km < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
        }

        private static string Hours(double seconds)
        {
            return (seconds / 3600).ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
