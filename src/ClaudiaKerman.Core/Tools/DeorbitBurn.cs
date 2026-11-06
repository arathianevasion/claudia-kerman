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
        /// <summary>km from the target to <see cref="Landing"/>; -1 without one.</summary>
        public double ErrorKm = -1;
    }

    /// <summary>
    /// The deorbit burn search (#282): the burn, within the window, that brings the craft down on the target by the reentry
    /// predictor. The burn is retrograde to a periapsis well inside the atmosphere (a steep entry is the least sensitive to
    /// burn and drag errors, the research found), plus a normal part for a target beside the ground track. One prediction gives
    /// how far round the orbit the craft lands and how long it falls; that, rotated round the orbit, picks the passes worth a
    /// look; Newton's method on the burn time (along the track) and the normal part (across it) then lands each on the target
    /// with the predictor in the loop. The best is kept, an earlier one on a near tie.
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
        private const double TieDv = 1;
        private const double G0 = 9.80665;
        private const int Candidates = 4;
        private const int NewtonSteps = 8;
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
                var first = Try(_ask.EarliestUt, 0);
                if (first.Landing.Error != null) return Fail(first.Landing.Error);

                var best = first;
                foreach (var ut in Passes(first))
                {
                    var refined = Refine(ut);
                    if (refined != null && Better(refined, best, _ask.AccuracyKm)) best = refined;
                }
                if (best.ErrorKm <= _ask.AccuracyKm) best.Error = OverBudget(best);
                else
                    best.Error = "the best burn in the next " + Hours(_latest - _ask.EarliestUt) + " h lands " + Km(best.ErrorKm)
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
                // Without chutes the craft lands on its engines, and that costs its speed at the ground.
                var powered = _craft.Chutes.Count == 0 || !_ask.Chutes ? plan.Landing.LandingSpeed : 0;
                var limit = _ask.MaxDv > 0 ? _ask.MaxDv : AutoMaxDv(_ask.DeltaVLeft, powered);
                if (plan.DeltaV <= limit) return null;
                return "the cheapest burn within accuracy_km needs " + Dv(plan.DeltaV) + " m/s (at UT " + Whole(plan.BurnUt) + "), over max_dv "
                    + Dv(Math.Max(0, limit)) + (_ask.MaxDv > 0 ? "" : " (auto: " + Dv(_ask.DeltaVLeft) + " m/s left, less the reserve and a powered landing)");
            }

            /// <summary>
            /// The burn times worth refining: rotating the first landing round the orbit with the burn, the passes whose landing comes
            /// closest to the target, nearest first.
            /// </summary>
            private IEnumerable<double> Passes(DeorbitPlan first)
            {
                Vec3 rb, vb;
                _orbit.State(first.BurnUt, out rb, out vb);
                var w = _orbit.W;
                var b = rb.Normalized;
                var p = first.Landing.Position.Normalized;
                var angle = Math.Atan2(Vec3.Dot(Vec3.Cross(b, p), w), Vec3.Dot(b, p));
                var fall = first.Landing.LandingUt - first.BurnUt;
                var step = Math.Min(60, _orbit.Period / 120);
                var times = new List<double>();
                var distances = new List<double>();
                for (var ut = _ask.EarliestUt; ut <= _latest + 1e-6; ut += step)
                {
                    var u = _orbit.Position(ut).Normalized;
                    var landing = u * Math.Cos(angle) + Vec3.Cross(w, u) * Math.Sin(angle);
                    double lat, lng;
                    _frame.Ground(landing, ut + fall, out lat, out lng);
                    times.Add(ut);
                    distances.Add(Distance(lat, lng));
                }
                var minima = new List<int>();
                for (var i = 0; i < times.Count; i++)
                {
                    var left = i == 0 || distances[i] <= distances[i - 1];
                    var right = i == times.Count - 1 || distances[i] <= distances[i + 1];
                    if (left && right) minima.Add(i);
                }
                return minima.OrderBy(i => distances[i]).Take(Candidates).Select(i => times[i]).ToList();
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
                    const double dt = 2;
                    const double dn = 1;
                    var byTime = Try(ut + dt, normal);
                    var byNormal = Try(ut, normal + dn);
                    if (byTime.Landing.Error != null || byNormal.Landing.Error != null) break;
                    double e0, n0, e1, n1, e2, n2;
                    Offset(here.Landing, out e0, out n0);
                    Offset(byTime.Landing, out e1, out n1);
                    Offset(byNormal.Landing, out e2, out n2);
                    // The offsets are of the target from the landing: drive them to 0.
                    double a = (e1 - e0) / dt, c = (n1 - n0) / dt, bb = (e2 - e0) / dn, d = (n2 - n0) / dn;
                    var det = a * d - bb * c;
                    if (Math.Abs(det) < 1e-12) break;
                    var moveT = (-e0 * d + bb * n0) / det;
                    var moveN = (-a * n0 + c * e0) / det;
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

            /// <summary>The burn at <paramref name="ut"/> with <paramref name="normal"/> m/s normal and enough retrograde for the entry periapsis, and where it lands.</summary>
            private DeorbitPlan Try(double ut, double normal)
            {
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
                var mass = _craft.MassKg;
                if (_ask.Isp > 0) mass *= Math.Exp(-dv / (_ask.Isp * G0));
                var landing = ReentryPredictor.Predict(_body, after, ut, _ask.GroundAltitude, mass, _air, _craft, _frame, _ask.Chutes);
                return new DeorbitPlan
                {
                    BurnUt = ut, Prograde = -retro, Normal = normal, DeltaV = dv, Landing = landing,
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
