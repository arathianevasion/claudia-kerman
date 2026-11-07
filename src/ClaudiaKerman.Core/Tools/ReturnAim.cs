// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>Where <c>return</c> is to come down, and the reentry predictor's view of the planet and the craft.</summary>
    internal sealed class ReturnLanding
    {
        public double Latitude;
        public double Longitude;
        /// <summary>The ground's height at the target, m (sea level for water).</summary>
        public double GroundAltitude;
        /// <summary>The engines' Isp, s, for the mass after the burn; 0 takes the mass as it is now.</summary>
        public double Isp;
        /// <summary>Who pays the burn when <see cref="Craft"/> leaves parts out (#814: <see cref="EntryCraft"/>'s masses); null: the craft pays it all.</summary>
        public DropBurn Payer;
        /// <summary>The planet's air, not the moon's the craft is at.</summary>
        public IReentryAir Air;
        public IReentryCraft Craft;
        /// <summary>The planet's frame.</summary>
        public BodyFrame Frame;

        /// <summary>The craft's mass after a burn of <paramref name="deltaV"/> m/s, kg.</summary>
        public double MassAfter(double deltaV)
        {
            return DropGuard.MassAfterBurn(Craft.MassKg, deltaV, Isp, Payer);
        }

        /// <summary>km east and north of the target to where <paramref name="landed"/> comes down on <paramref name="planet"/>.</summary>
        public double[] OffKm(SnapshotBody planet, ReentryPrediction landed)
        {
            var km = planet.Radius / 1000 * Math.PI / 180;
            return new[] { LandingSiteSearch.Wrap180(landed.Longitude - Longitude) * Math.Cos(Latitude * Math.PI / 180) * km, (landed.Latitude - Latitude) * km };
        }

        /// <summary>km from the target to where <paramref name="landed"/> comes down on <paramref name="planet"/>.</summary>
        public double ErrorKm(SnapshotBody planet, ReentryPrediction landed)
        {
            return LandingSiteSearch.ArcDegrees(landed.Latitude, landed.Longitude, Latitude, Longitude) * Math.PI / 180 * planet.Radius / 1000;
        }
    }

    /// <summary>
    /// <c>return</c> aimed at a landing point (#792, spike #482): the cheapest lever on where a return comes down is when it leaves.
    /// A moon keeps its place against the burn point, so the whole path turns with the moon from one window to the next while the
    /// ground turns under it: one least-burn solve and one prediction place every window's landing. The nearest window within
    /// max_wait is taken, its least burn solved, and the secant method with the predictor in the loop moves the burn's time along
    /// the track (within <see cref="ReachS"/>), each try re-solved for target_pe, so the periapsis is held and the correction
    /// halfway has only what the burn left. Across the track there is no cheap lever: the path's plane holds the moon, so an inclined
    /// moon's latitude stays in the landing (Minmus: up to about 60 km, at about 0.65 km per m/s of normal burn), and <see cref="ReturnPlan.ErrorKm"/> says so; the Course Correction takes that out when the delta-v covers it (#790). It only reads.
    /// </summary>
    internal static class ReturnAim
    {
        /// <summary>The periapsis with a target when none is given, m: 30 km, against 35 km without one. The landing moves half as far for the burn's error (0.5 m/s: about 95 km against 180 from the Mun, 450 km against 1,630 from Minmus, where 35 km can skip out).</summary>
        public const double DefaultTargetPe = 30000;
        /// <summary>The KSC as <c>claudia_ksc</c> gives it, for reading a plan's <c>"ksc"</c> before the run.</summary>
        public const double KscLatitude = -0.0972;
        public const double KscLongitude = -74.5577;
        /// <summary>How far the burn may move off its window's least burn, s, at the least: past this from a low orbit the cost climbs fast (±150 s costs 6 to 9 m/s).</summary>
        public const double ReachS = 150;
        /// <summary>The same as a share of the craft's orbit, for a high one: what counts is how far round it the burn moves (150 s of a 38 km Mun orbit's 2,858 s).</summary>
        private const double ReachShare = 150.0 / 2858;
        /// <summary>What an explicit max_duration leaves after the burn, s: to fly it and get under way, as the correction's own limit keeps the rest.</summary>
        public const double AfterBurnS = 600;
        /// <summary>The error <c>exec_node</c> can leave in a burn, m/s, for the spread reported.</summary>
        public const double ResidualMs = 0.5;
        /// <summary>A landing this close along the track is done, km.</summary>
        private const double CloseKm = 1;
        /// <summary>A landing further than this along the track is worth trying the next window for, km.</summary>
        private const double WorthAnotherKm = 5;
        private const int Candidates = 2;
        private const int SecantSteps = 6;
        private const double TimeProbeS = 20;
        /// <summary>
        /// Windows within this share of the step between them count as near, and the soonest of those goes: asked again an hour before
        /// it, at a Due Time (#749), the block is sure to pick the same window, where a strict nearest could wait on for a turn.
        /// </summary>
        private const double NearShare = 0.6;

        /// <summary>
        /// Seconds for the landing's longitude to come round once: the ground turns under a path that turns with the moon, about 7.1 h
        /// at the Mun, 6.1 h at Minmus. A moon locked to its planet's spin never brings it round, so one orbit of the moon at most.
        /// </summary>
        public static double LongitudeTurn(BodySystem system, SnapshotBody planet, SnapshotBody moon, double handed = 1)
        {
            var moonOrbit = system.OrbitOf(moon);
            var rate = Math.Abs(LandingDrift(system, planet, moon, handed));
            return rate > 0 ? Math.Min(360 / rate, moonOrbit.Period) : moonOrbit.Period;
        }

        /// <summary>Degrees of longitude a landing moves per second of waiting.</summary>
        private static double LandingDrift(BodySystem system, SnapshotBody planet, SnapshotBody moon, double handed)
        {
            var moonOrbit = system.OrbitOf(moon);
            var moonRate = 360 / moonOrbit.Period * Sense(moonOrbit, moonOrbit.Epoch);
            return handed * moonRate - (planet.RotationPeriod > 0 ? 360 / planet.RotationPeriod : 0);
        }

        private static double Sense(Conic orbit, double ut)
        {
            Vec3 r, v;
            orbit.State(ut, out r, out v);
            return Vec3.Cross(r, v).Z >= 0 ? 1 : -1;
        }

        /// <summary>Seconds between the burn point's passes: the craft's orbit against the moon's motion, which carries the burn point along.</summary>
        private static double WindowStep(BodySystem system, SnapshotBody moon, Conic orbit, double ut)
        {
            var moonOrbit = system.OrbitOf(moon);
            Vec3 r, v, mr, mv;
            orbit.State(ut, out r, out v);
            moonOrbit.State(ut, out mr, out mv);
            var along = Vec3.Dot(Vec3.Cross(r, v), Vec3.Cross(mr, mv)) >= 0 ? 1 : -1;
            return 1 / (1 / orbit.Period - along / moonOrbit.Period);
        }

        /// <summary>The planet of the moon the Game Snapshot's craft orbits; null round anything else.</summary>
        public static SnapshotBody PlanetOf(GameSnapshot snapshot)
        {
            var system = new BodySystem(snapshot.Bodies);
            var moon = system.Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body);
            var planet = moon == null ? null : system.Parent(moon);
            return planet == null || system.Parent(planet) == null ? null : planet;
        }

        /// <summary>The landing for the Game Snapshot's craft, with the planet's frame; null round no moon (the solve says why), or with <paramref name="problem"/> when the frame can't be read.</summary>
        public static ReturnLanding LandingOn(GameSnapshot snapshot, double latitude, double longitude, double groundAltitude, double isp, IPlanningBody body, IReentryAir air, IReentryCraft craft, out string problem)
        {
            problem = null;
            var planet = PlanetOf(snapshot);
            if (planet == null) return null;
            var frame = BodyFrame.Read(planet, body, snapshot.Ut, out problem);
            if (problem != null) return null;
            return new ReturnLanding { Latitude = latitude, Longitude = longitude, GroundAltitude = groundAltitude, Isp = isp, Air = air, Craft = craft, Frame = frame };
        }

        /// <summary>The aimed burn. A solve the window can't give is as without a landing; with no prediction, the window's least burn stands.</summary>
        public static ReturnPlan Find(BodySystem system, SnapshotBody moon, Conic orbit, double now, ReturnAsk ask)
        {
            var search = Search.Start(system, moon, orbit, now, ask);
            if (search.First.Error != null || search.FirstLanding == null) return search.First;
            ReturnPlan best = null;
            foreach (var window in search.Windows().Take(Candidates))
            {
                var aimed = search.Aim(window);
                if (aimed != null && (best == null || aimed.ErrorKm < best.ErrorKm)) best = aimed;
                if (best != null && search.AlongKm <= WorthAnotherKm) break;
            }
            if (best == null) return search.First;
            best.SpreadKm = search.Spread(best);
            return best;
        }

        /// <summary>
        /// The UT of the window the aim takes (#749's wait, before the run: the pick alone, one solve and one prediction); null when it would
        /// not aim. Before the run the craft and air are estimates, so the block may take another window.
        /// </summary>
        public static double? DepartureUt(GameSnapshot snapshot, ReturnAsk ask)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return null;
            var search = Search.Start(setup.System, setup.Body, setup.Start, setup.Now, ask);
            if (search.First.Error != null || search.FirstLanding == null) return null;
            return search.Windows().First();
        }

        /// <summary>The landing's turn for the Game Snapshot's craft round a moon of <paramref name="planet"/>.</summary>
        public static double LongitudeTurnFor(GameSnapshot snapshot, SnapshotBody planet)
        {
            var system = new BodySystem(snapshot.Bodies);
            return LongitudeTurn(system, planet, system.Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body));
        }

        /// <summary>One aim's facts, and what its tries share.</summary>
        private sealed class Search
        {
            private BodySystem _system;
            private SnapshotBody _moon;
            private SnapshotBody _planet;
            private Conic _orbit;
            private double _now;
            private ReturnAsk _ask;
            private double _earliest;
            private double _latest;
            private double _step;
            public ReturnPlan First;
            public ReentryPrediction FirstLanding;
            /// <summary>km along the track the last <see cref="Aim"/> left.</summary>
            public double AlongKm;

            public static Search Start(BodySystem system, SnapshotBody moon, Conic orbit, double now, ReturnAsk ask)
            {
                var search = new Search { _system = system, _moon = moon, _orbit = orbit, _now = now, _ask = ask };
                search._earliest = Math.Max(now, ask.EarliestUt);
                search.First = ReturnBurn.Find(system, moon, orbit, now, search.Plain(ask.EarliestUt, null));
                if (search.First.Error != null) return search;
                if (ask.MaxWait.HasValue && !(ask.MaxWait.Value > 0))
                {
                    search.First = ReturnBurn.Fail("max_wait must be seconds above 0");
                    return search;
                }
                search._planet = system.Parent(moon);
                search._latest = search._earliest + (ask.MaxWait ?? LongitudeTurn(system, search._planet, moon, ask.Landing.Frame.Handed));
                if (ask.LatestUt.HasValue) search._latest = Math.Min(search._latest, ask.LatestUt.Value);
                search._step = WindowStep(system, moon, orbit, search.First.Ut);
                var landed = search.Predict(search.First);
                if (landed.Error != null) return search;
                search.FirstLanding = landed;
                search.Stamp(search.First, landed);
                return search;
            }

            /// <summary>The ask without its landing, for the burn window given.</summary>
            private ReturnAsk Plain(double earliest, double? latest)
            {
                return new ReturnAsk
                {
                    TargetPe = _ask.TargetPe, TargetInc = _ask.TargetInc, EarliestUt = earliest, LatestUt = latest, PeTolerance = _ask.PeTolerance, IncTolerance = _ask.IncTolerance,
                };
            }

            /// <summary>The windows' UTs within max_wait, best first: the soonest near the target, then the rest by how near each falls.</summary>
            public IEnumerable<double> Windows()
            {
                var landing = _ask.Landing;
                var shift = _step * LandingDrift(_system, _planet, _moon, landing.Frame.Handed);
                var windows = new List<KeyValuePair<double, double>>();
                // The first window stands even past a short max_wait: the block can leave no sooner.
                for (var k = 0; k == 0 || First.Ut + k * _step <= _latest; k++)
                    windows.Add(new KeyValuePair<double, double>(First.Ut + k * _step, Math.Abs(LandingSiteSearch.Wrap180(FirstLanding.Longitude + k * shift - landing.Longitude))));
                var near = Math.Abs(LandingSiteSearch.Wrap180(shift)) * NearShare;
                var soonest = windows.Where(w => w.Value <= near).Select(w => (double?)w.Key).FirstOrDefault();
                return windows.OrderBy(w => soonest.HasValue && w.Key == soonest.Value ? -1 : w.Value).Select(w => w.Key);
            }

            /// <summary>The burn near <paramref name="window"/> that comes down nearest the target; null when none is found there.</summary>
            public ReturnPlan Aim(double window)
            {
                AlongKm = double.PositiveInfinity;
                var low = Math.Max(_earliest, window - _step / 2);
                var high = Math.Min(_latest, window + _step / 2);
                var least = window <= First.Ut ? First : ReturnBurn.Find(_system, _moon, _orbit, _now, Plain(low, high));
                if (least.Error != null) return null;
                var landed = Predict(least);
                if (landed.Error != null) return null;
                Stamp(least, landed);

                var centre = least.Ut;
                var reach = Math.Max(ReachS, _orbit.Period * ReachShare);
                var lowest = Math.Max(_earliest, centre - reach);
                var highest = Math.Max(centre, Math.Min(_latest, centre + reach));
                var best = least;
                var t = centre;
                var at = Off(landed);
                var dt = (centre + TimeProbeS <= highest ? centre + TimeProbeS : Math.Max(lowest, centre - TimeProbeS)) - centre;
                if (dt == 0) return best;
                var probe = Try(centre + dt, ref best);
                if (probe == null) return best;
                double slopeE = (probe[0] - at[0]) / dt, slopeN = (probe[1] - at[1]) / dt;
                for (var i = 0; i < SecantSteps; i++)
                {
                    // Along the track the burn's time moves the landing; what is across it stays.
                    var size = slopeE * slopeE + slopeN * slopeN;
                    if (!(size > 0)) break;
                    var along = at[0] * slopeE + at[1] * slopeN;
                    AlongKm = Math.Abs(along) / Math.Sqrt(size);
                    if (AlongKm <= CloseKm) break;
                    var next = Math.Min(highest, Math.Max(lowest, t - along / size));
                    if (next == t) break;
                    var off = Try(next, ref best);
                    if (off == null) break;
                    slopeE = (off[0] - at[0]) / (next - t);
                    slopeN = (off[1] - at[1]) / (next - t);
                    t = next;
                    at = off;
                }
                best.AimDv = Math.Max(0, best.DeltaV - least.DeltaV);
                return best;
            }

            /// <summary>Tries the burn solved for target_pe at <paramref name="ut"/>: km east and north of the target, keeping the best; null when it has no landing.</summary>
            private double[] Try(double ut, ref ReturnPlan best)
            {
                var plan = ReturnBurn.Find(_system, _moon, _orbit, _now, Plain(ut, ut));
                if (plan.Error != null) return null;
                var landed = Predict(plan);
                if (landed.Error != null) return null;
                Stamp(plan, landed);
                if (plan.ErrorKm < best.ErrorKm) best = plan;
                return Off(landed);
            }

            private double[] Off(ReentryPrediction landed)
            {
                return _ask.Landing.OffKm(_planet, landed);
            }

            private void Stamp(ReturnPlan plan, ReentryPrediction landed)
            {
                plan.Landing = landed;
                plan.ErrorKm = _ask.Landing.ErrorKm(_planet, landed);
            }

            /// <summary>Where <paramref name="plan"/>'s burn comes down, by the predictor.</summary>
            private ReentryPrediction Predict(ReturnPlan plan)
            {
                return Predict(plan.Ut, plan.Prograde, plan.Normal, plan.Radial, plan.DeltaV);
            }

            private ReentryPrediction Predict(double ut, double prograde, double normal, double radial, double deltaV)
            {
                var arrival = SingleShooting.Arrive(_system, _moon, _orbit, ut, SingleShooting.FromNodeFrame(_orbit, ut, prograde, normal, radial), _planet, ReturnBurn.Patches);
                if (arrival == null) return new ReentryPrediction { Error = "the burn does not reach " + _planet.Name };
                var landing = _ask.Landing;
                return ReentryPredictor.Predict(_planet, arrival.Orbit, arrival.StartUt, landing.GroundAltitude, landing.MassAfter(deltaV), landing.Air, landing.Craft, landing.Frame);
            }

            /// <summary>km <see cref="ResidualMs"/> of error along the burn moves the landing, the worse way; -1 when either way does not land, which no figure says.</summary>
            public double Spread(ReturnPlan plan)
            {
                var spread = -1.0;
                foreach (var error in new[] { ResidualMs, -ResidualMs })
                {
                    var landed = Predict(plan.Ut, plan.Prograde + error, plan.Normal, plan.Radial, plan.DeltaV);
                    if (landed.Error != null) return -1;
                    spread = Math.Max(spread, LandingSiteSearch.ArcDegrees(landed.Latitude, landed.Longitude, plan.Landing.Latitude, plan.Landing.Longitude) * Math.PI / 180 * _planet.Radius / 1000);
                }
                return spread;
            }
        }
    }
}
