// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// From an orbit over <see cref="InclinedDeg"/> off the moon's plane (#816) none of that holds: the path's plane is the craft's, the
    /// windows differ in cost, and near the cheap alignment the time to the planet shortens about as fast as the ground turns, so the
    /// landings of a band of cheap windows span only about 90° of longitude. Each window is solved and each within
    /// <see cref="CostCapMs"/> of the cheapest predicted (<see cref="Search.FromWindows"/>); a target outside that band is missed by
    /// what the band leaves, for the correction.
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
        /// #816: an orbit more than this off the moon's own orbital plane is inclined. Its windows differ in cost (from a 30 km Mun
        /// orbit at 66°, 266 to about 650 m/s), the cheap one coming round every half orbit of the moon, and its landings don't
        /// step along with the moon, so each window is solved, and each within <see cref="CostCapMs"/> predicted, on its own.
        /// </summary>
        public const double InclinedDeg = 5;
        /// <summary>The windows kept: those costing no more than the cheapest within max_wait plus this, m/s.</summary>
        public const double CostCapMs = 50;
        /// <summary>Landings within this of each other are a tie, km, which goes to the sooner window.</summary>
        public const double TieKm = 5;
        /// <summary>From an inclined orbit, "auto" max_wait runs this past the next cheap alignment, s: a window aligned with the target lies within it (about +35 m/s at 66°).</summary>
        public const double PastAlignmentS = 3.5 * 3600;
        /// <summary>#880: the search for a landing near the target runs this far past the next cheap alignment, s (several alignments, so a band landing near KSC comes round).</summary>
        public const double ReachPastAlignmentS = 60 * 3600;
        /// <summary>#880: a landing this close counts as reaching the target, km.</summary>
        public const double ReachKm = 50;        /// <summary>#880: a window's least burn landing this close is worth aiming, for the time shift (<see cref="Search.Aim(double)"/>) can bring it within <see cref="ReachKm"/>, km.</summary>
        private const double WorthAimingKm = 300;
        /// <summary>#880: the most predictions one search spends before it stops looking further on (an aim counts as <see cref="AimPredictions"/>): about 1.3 s in the game at 10.5 ms each, in one call on the main thread until #463.</summary>
        private const int MostReachPredictions = 120;
        /// <summary>#880: what one aimed window costs in predictions (<see cref="SecantSteps"/> and its probe, at most).</summary>
        private const int AimPredictions = SecantSteps + 2;
        /// <summary>#880: a costlier window's burn straight onto the asymptote may cost this many times what the craft can spend over the cheapest and still be solved (+400 of it is about +100 of the least burn).</summary>
        private const double DirectPerLeastBurn = 4;
        /// <summary>A window whose burn straight onto the asymptote (<see cref="ReturnBurn.Direct"/>) costs this much over the least such is left unsolved, m/s.</summary>
        private const double SkipOverDirectMs = 400;
        /// <summary>The windows the inclined search aims at, nearest landing first.</summary>
        private const int InclinedCandidates = 3;

        /// <summary>
        /// Degrees between the craft's orbital plane and the moon's own, 0 to 90: a retrograde orbit in the moon's plane is in it,
        /// as its cheap burn lines up as well.
        /// </summary>
        public static double PlaneAngle(BodySystem system, SnapshotBody moon, Conic orbit, double ut)
        {
            Vec3 r, v, mr, mv;
            orbit.State(ut, out r, out v);
            system.OrbitOf(moon).State(ut, out mr, out mv);
            var cos = Math.Abs(Vec3.Dot(Vec3.Cross(r, v).Normalized, Vec3.Cross(mr, mv).Normalized));
            return Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
        }

        /// <summary>
        /// The first UT from <paramref name="from"/> when the moon's velocity lies in the craft's plane, so the way home (backwards
        /// along the moon's orbit) does too: the cheap plane alignment, every half orbit of the moon.
        /// </summary>
        public static double NextAlignment(BodySystem system, SnapshotBody moon, Conic orbit, double from)
        {
            var moonOrbit = system.OrbitOf(moon);
            Vec3 r, v;
            orbit.State(from, out r, out v);
            var normal = Vec3.Cross(r, v).Normalized;
            Func<double, double> off = t =>
            {
                Vec3 mr, mv;
                moonOrbit.State(t, out mr, out mv);
                return Vec3.Dot(mv.Normalized, normal);
            };
            const int samples = 180;
            var span = moonOrbit.Period / 2 * 1.05;
            double t0 = from, f0 = off(from);
            if (f0 == 0) return from;
            for (var i = 1; i <= samples; i++)
            {
                double t1 = from + span * i / samples, f1 = off(t1);
                if ((f0 > 0) != (f1 > 0))
                {
                    for (var k = 0; k < 40; k++)
                    {
                        var mid = (t0 + t1) / 2;
                        var fm = off(mid);
                        if ((fm > 0) == (f0 > 0)) { t0 = mid; f0 = fm; } else t1 = mid;
                    }
                    return (t0 + t1) / 2;
                }
                t0 = t1;
                f0 = f1;
            }
            return from;
        }

        /// <summary>
        /// "auto" max_wait from <paramref name="earliest"/>, s: to the next cheap alignment plus <see cref="PastAlignmentS"/> (up to about
        /// 23 h at the Mun) when the plane costs something (<see cref="AlignmentCosts"/>), otherwise one turn of the landing's longitude.
        /// </summary>
        public static double AutoWait(BodySystem system, SnapshotBody planet, SnapshotBody moon, Conic orbit, double now, double earliest, double targetPe, double handed, double pastAlignment)
        {
            double alignment;
            return AlignmentCosts(system, moon, orbit, now, earliest, targetPe, out alignment)
                ? alignment - earliest + pastAlignment
                : LongitudeTurn(system, planet, moon, handed);
        }

        /// <summary>#880: whether <paramref name="planet"/> is home, the only place a return may wait for a landing near the target. Elsewhere #816's rule stands.</summary>
        public static bool IsHome(SnapshotBody planet)
        {
            return string.Equals(planet.Name, CraftCheckFlow.HomeBody, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether waiting for the cheap alignment can save over <see cref="CostCapMs"/>: the orbit is over <see cref="InclinedDeg"/> off
        /// the moon's plane, and the window a quarter of the moon's orbit past the next alignment (<paramref name="alignment"/>), where the
        /// way home is furthest out of the craft's plane, costs over the cap more than the one at it. Two solves. A slight tilt, as an
        /// orbit round Minmus in Kerbin's equator (6°), costs little either way: its windows are predicted each, without the long wait.
        /// </summary>
        public static bool AlignmentCosts(BodySystem system, SnapshotBody moon, Conic orbit, double now, double earliest, double targetPe, out double alignment)
        {
            alignment = earliest;
            var planet = system.Parent(moon);
            if (planet == null || system.Parent(planet) == null || !orbit.Closed || PlaneAngle(system, moon, orbit, earliest) <= InclinedDeg) return false;
            alignment = NextAlignment(system, moon, orbit, earliest);
            var half = orbit.Period / 2;
            var dear = alignment + system.OrbitOf(moon).Period / 4;
            var cheap = ReturnBurn.Find(system, moon, orbit, now, new ReturnAsk { TargetPe = targetPe, EarliestUt = alignment - half, LatestUt = alignment + half });
            var costly = ReturnBurn.Find(system, moon, orbit, now, new ReturnAsk { TargetPe = targetPe, EarliestUt = dear - half, LatestUt = dear + half });
            // A solve that fails says nothing either way: the long wait is kept for a plane known to cost.
            return cheap.Error == null && costly.Error == null && costly.DeltaV - cheap.DeltaV > CostCapMs;
        }

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

        /// <summary>The nearest landing of <paramref name="plans"/>, or the soonest within <see cref="TieKm"/> of it (#816).</summary>
        internal static ReturnPlan Tied(IList<ReturnPlan> plans)
        {
            var nearest = plans.Min(p => p.ErrorKm);
            return plans.Where(p => p.ErrorKm <= nearest + TieKm).OrderBy(p => p.Ut).First();
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
            if (search.Inclined && (ask.Landing != null || search.CostlyPlane)) return search.FromWindows();
            if (search.First.Error != null || ask.Landing == null || search.FirstLanding == null) return search.First;
            ReturnPlan best = null;
            foreach (var window in search.Windows().Take(Candidates))
            {
                var aimed = search.Aim(window);
                if (aimed != null && (best == null || aimed.ErrorKm < best.ErrorKm)) best = aimed;
                if (best != null && search.AlongKm <= WorthAnotherKm) break;
            }
            if (best == null) return search.First;
            best.SpreadKm = search.Spread(best);
            search.ReportStepped(best);
            return best;
        }

        /// <summary>
        /// The UT of the window the aim takes (#749's wait, before the run: the pick alone, one solve and one prediction; from an inclined
        /// orbit, #816, the block's own answer, so both take the same window); null when it would not aim. Before the run the craft and
        /// air are estimates, so the block may take another window. <paramref name="dueBy"/>: the UT a Due Time (#749) is to lead rather
        /// than the window's. Over a costly plane it is the window's or its cheap alignment's, whichever is sooner: the block, asked again
        /// then, searches to that alignment plus <see cref="PastAlignmentS"/> as the plan did, where asked after it it would search on to
        /// the next one, half the moon's orbit later, and might wait for it.
        /// </summary>
        public static double? DepartureUt(GameSnapshot snapshot, ReturnAsk ask, out double? dueBy)
        {
            dueBy = null;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return null;
            var search = Search.Start(setup.System, setup.Body, setup.Start, setup.Now, ask);
            if (search.Inclined)
            {
                var plan = search.FromWindows();
                if (plan.Error != null) return null;
                dueBy = search.DueBy(plan.Ut);
                return plan.Ut;
            }
            if (search.First.Error != null || search.FirstLanding == null) return null;
            return search.Windows().First();
        }

        /// <summary>#888: which way the landing's longitude turns (<see cref="BodyFrame.Handed"/>) read from <paramref name="planet"/>'s frame, rotation and orbit, with no landing; 1 when it can't be read.</summary>
        public static double HandedOn(SnapshotBody planet, double now, IPlanningBody body)
        {
            string problem;
            return HandedOn(planet, now, body, out problem);
        }

        /// <summary><see cref="HandedOn(SnapshotBody, double, IPlanningBody)"/> (#912): <paramref name="problem"/> is why the frame couldn't be read and 1 was taken, else null.</summary>
        public static double HandedOn(SnapshotBody planet, double now, IPlanningBody body, out string problem)
        {
            problem = null;
            if (body == null) { problem = "no planning body"; return 1; }
            var frame = BodyFrame.Read(planet, body, now, out problem);
            if (frame == null) { problem = problem ?? "frame unreadable"; return 1; }
            return frame.Handed;
        }

        /// <summary>
        /// "auto" max_wait (<see cref="AutoWait"/>) for the Game Snapshot's craft round a moon of <paramref name="planet"/>, from
        /// <paramref name="earliest"/>. #802: the landing's turn depends on which way the planet's frame turns, read from
        /// <paramref name="body"/> as the block reads it; without one it is taken to turn with the frame's angle.
        /// </summary>
        public static double AutoWaitFor(GameSnapshot snapshot, SnapshotBody planet, double earliest, double targetPe, IPlanningBody body, double pastAlignment = PastAlignmentS)
        {
            var system = new BodySystem(snapshot.Bodies);
            var moon = system.Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body);
            var handed = HandedOn(planet, snapshot.Ut, body);
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null || setup.Body != moon || !setup.Start.Closed) return LongitudeTurn(system, planet, moon, handed);
            return AutoWait(system, planet, moon, setup.Start, setup.Now, Math.Max(setup.Now, earliest), targetPe, handed, pastAlignment);
        }

        /// <summary>
        /// The UT a return with no target and no window given leaves at (#816), before the run: where the plane costs something
        /// (<see cref="AlignmentCosts"/>), the next pass unless it costs over the cheapest within the auto wait plus
        /// <see cref="CostCapMs"/>, else the cheapest; null when the next pass is taken, or there is none.
        /// </summary>
        public static double? CheapDepartureUt(GameSnapshot snapshot, ReturnAsk ask, out double? dueBy)
        {
            dueBy = null;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return null;
            var search = Search.Start(setup.System, setup.Body, setup.Start, setup.Now, ask);
            if (!search.Inclined || !search.CostlyPlane) return null;
            var plan = search.FromWindows();
            if (plan.Error != null || plan.Ut <= search.First.Ut) return null;
            dueBy = search.DueBy(plan.Ut);
            return plan.Ut;
        }

        /// <summary>One aim's facts, and what its tries share.</summary>
        internal sealed class Search
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
            /// <summary>The orbit is more than <see cref="InclinedDeg"/> off the moon's plane (#816): <see cref="FromWindows"/> picks.</summary>
            public bool Inclined;
            /// <summary>Waiting for the cheap alignment can save over the cap (<see cref="AlignmentCosts"/>); read only when max_wait is "auto".</summary>
            public bool CostlyPlane;
            /// <summary>The landing's longitude turn the windows span (<see cref="LongitudeTurn"/>), s; for tests of #888.</summary>
            internal double Turn;

            public static Search Start(BodySystem system, SnapshotBody moon, Conic orbit, double now, ReturnAsk ask)
            {
                var search = new Search { _system = system, _moon = moon, _orbit = orbit, _now = now, _ask = ask };
                search._earliest = Math.Max(now, ask.EarliestUt);
                search.First = ReturnBurn.Find(system, moon, orbit, now, search.Plain(ask.EarliestUt, null));
                if (ask.MaxWait.HasValue && !(ask.MaxWait.Value > 0))
                {
                    search.First = ReturnBurn.Fail("max_wait must be seconds above 0");
                    return search;
                }
                search._planet = system.Parent(moon);
                // From an inclined orbit the first window failing is no reason to stop: FromWindows tries them all, and its error stands only when none solves.
                search.Inclined = search._planet != null && system.Parent(search._planet) != null && orbit.Closed && PlaneAngle(system, moon, orbit, search._earliest) > InclinedDeg;
                if (search.First.Error != null && !search.Inclined) return search;
                double alignment = 0;
                search.CostlyPlane = search.Inclined && !ask.MaxWait.HasValue && AlignmentCosts(system, moon, orbit, now, search._earliest, ask.TargetPe, out alignment);
                var turn = search.Turn = LongitudeTurn(system, search._planet, moon, ask.Landing == null ? ask.Handed : ask.Landing.Frame.Handed);
                search._latest = search._earliest + (ask.MaxWait ?? (search.CostlyPlane ? alignment - search._earliest + PastAlignmentS : turn));
                if (search.CostlyPlane)
                {
                    // #887: restarted late for the Mission's window, the alignment it was planned for, not the next one half a moon's orbit on.
                    if (ask.PlannedUt.HasValue)
                    {
                        // Start an hour before the band's end, so a window at alignment + PastAlignmentS does not skip to the next alignment.
                        var planned = NextAlignment(system, moon, orbit, ask.PlannedUt.Value - PastAlignmentS - 3600);
                        // Already past that band: #816's normal rule, not the costly-plane burn at once.
                        if (search._earliest <= planned + PastAlignmentS)
                        {
                            alignment = planned;
                            search._latest = Math.Max(search._earliest, alignment + PastAlignmentS);
                        }
                    }
                    search._alignment = alignment;
                    search._turnEnd = search._earliest + turn;
                    search._bandStart = alignment - PastAlignmentS;
                }
                // #880: the search for a landing near the target looks further than #816's rule, unless max_wait is the caller's.
                search._states = ask.Landing != null && ask.DvLeft.HasValue && IsHome(search._planet);
                search._reach = search._states && search.Inclined;
                search._reachLatest = search._reach && search.CostlyPlane && !ask.MaxWait.HasValue ? search._alignment + ReachPastAlignmentS : search._latest;
                if (ask.LatestUt.HasValue)
                {
                    search._latest = Math.Min(search._latest, ask.LatestUt.Value);
                    search._reachLatest = Math.Min(search._reachLatest, ask.LatestUt.Value);
                }
                if (search.Inclined || ask.Landing == null) return search;
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

            /// <summary>Each window's least burn within max_wait, soonest first, one per orbit of the craft from the earliest (the first a whole orbit, however short max_wait); those that found none left out.</summary>
            private List<ReturnPlan> _windows;
            private double _cheapest;

            /// <summary>
            /// With "auto" max_wait over a costly plane, the windows solved are those of one longitude turn from the earliest and those from
            /// <see cref="PastAlignmentS"/> before the alignment on: between them the way home is out of the plane (at the Mun over the cap),
            /// and round Minmus, whose alignment can be 150 h off, solving every window would take seconds.
            /// </summary>
            private double _turnEnd = double.PositiveInfinity;
            private double _bandStart = double.NegativeInfinity;
            /// <summary>Over a costly plane, the cheap alignment the search runs to <see cref="PastAlignmentS"/> past; NaN otherwise.</summary>
            private double _alignment = double.NaN;

            /// <summary>What a Due Time for the window at <paramref name="ut"/> leads (<see cref="ReturnAim.DepartureUt(GameSnapshot, ReturnAsk, out double?)"/>).</summary>
            public double DueBy(double ut)
            {
                return ut > _alignment ? _alignment : ut;
            }

            /// <summary>The most a burn may cost while it is aimed: the cap, from an inclined orbit.</summary>
            private double _dvLimit = double.PositiveInfinity;
            /// <summary>#880: this ask searches for the earliest window landing within <see cref="ReachKm"/>, up to <see cref="_reachLatest"/>.</summary>
            private bool _reach;
            private double _reachLatest;
            /// <summary>#880: this ask's plan states the wait, the delta-v and the landing's distance, from any orbit.</summary>
            private bool _states;
            /// <summary>#880: how far <see cref="Reached"/> looked, UT; short of <see cref="_reachLatest"/> when it ran out of predictions.</summary>
            private double _reachedTo;
            /// <summary><see cref="_windows"/> by their orbit from the earliest, for <see cref="Reached"/> to reuse.</summary>
            private readonly Dictionary<int, ReturnPlan> _solvedAt = new Dictionary<int, ReturnPlan>();

            /// <summary>One span per orbit of the craft from the earliest to <paramref name="latest"/>; the first a whole orbit, however short the wait.</summary>
            private List<double[]> Spans(double latest)
            {
                var period = _orbit.Period;
                var spans = new List<double[]>();
                for (var k = 0; k == 0 || _earliest + k * period < latest; k++)
                    spans.Add(new[] { _earliest + k * period, Math.Min(latest, _earliest + (k + 1) * period) });
                return spans;
            }

            /// <summary>The burn straight onto the asymptote in each span, m/s.</summary>
            private List<double> DirectBurns(IEnumerable<double[]> spans)
            {
                var peR = _planet.Radius + _ask.TargetPe;
                return spans.Select(w => { double ut; return ReturnBurn.Direct(_system, _moon, _planet, _orbit, w[0], w[1], peR, out ut).Length; }).ToList();
            }

            private List<ReturnPlan> Solved()
            {
                if (_windows != null) return _windows;
                _windows = new List<ReturnPlan>();
                var spans = Spans(_latest);
                // A window whose burn straight onto the asymptote costs far over the least of them is far over the cap too (at 66°, +400 m/s of it is about +100 of the least burn): it is not solved.
                var direct = DirectBurns(spans);
                var floor = direct.Where(d => d > 0).DefaultIfEmpty(0).Min();
                for (var k = 0; k < spans.Count; k++)
                {
                    if (k > 0 && floor > 0 && direct[k] > floor + SkipOverDirectMs) continue;
                    if (k > 0 && spans[k][0] >= _turnEnd && spans[k][1] <= _bandStart) continue;
                    var plan = k == 0 ? First : ReturnBurn.Find(_system, _moon, _orbit, _now, Plain(spans[k][0], spans[k][1]));
                    if (plan.Error == null) { _windows.Add(plan); _solvedAt[k] = plan; }
                }
                _cheapest = _windows.Count == 0 ? double.NaN : _windows.Min(w => w.DeltaV);
                return _windows;
            }

            /// <summary>The windows within the cap whose least burn has a landing, nearest first; null when no window solves, empty without a target or a landing.</summary>
            private List<ReturnPlan> Ranked()
            {
                var windows = Solved();
                if (windows.Count == 0) return null;
                var ranked = new List<ReturnPlan>();
                if (_ask.Landing == null) return ranked;
                foreach (var window in windows.Where(w => w.DeltaV <= _cheapest + CostCapMs))
                {
                    var landed = Predict(window);
                    if (landed.Error != null) continue;
                    Stamp(window, landed);
                    ranked.Add(window);
                }
                return ranked.OrderBy(w => w.ErrorKm).ToList();
            }

            /// <summary>
            /// With no target (#816): the soonest window when it costs no more than the cap, else the cheapest. With a
            /// target and no landing to be had, the cheapest, as the window's least burn stands without one.
            /// </summary>
            private ReturnPlan Cheap()
            {
                var soonest = _windows[0];
                return _ask.Landing == null && soonest.DeltaV <= _cheapest + CostCapMs ? soonest : _windows.OrderBy(w => w.DeltaV).First();
            }

            /// <summary>
            /// The inclined orbit's answer (#816): among the windows costing no more than the cheapest plus <see cref="CostCapMs"/>, the
            /// nearest landing (by <see cref="Predict"/>, each), a tie within <see cref="TieKm"/> to the sooner; each of the nearest few
            /// aimed, and the rule again on where they come down. With no target, or no landing to be had, <see cref="Cheap"/>.
            /// </summary>
            public ReturnPlan FromWindows()
            {
                var ranked = Ranked();
                if (ranked == null) return First;
                if (_reach)
                {
                    var reached = Reached(ranked);
                    if (reached != null) return reached;
                }
                ReturnPlan best;
                _dvLimit = _cheapest + CostCapMs;
                if (ranked.Count == 0) best = Cheap();
                else
                {
                    best = Tied(ranked.Take(InclinedCandidates).Select(w => Aim(w) ?? w).ToList());
                    best.SpreadKm = Spread(best);
                }
                best.AimDv = Math.Max(0, best.DeltaV - _cheapest);
                best.WindowNote = string.Format(CultureInfo.InvariantCulture,
                    "; windows {0} over {1:0.0} h, {2:0.0}° off the moon's plane: cheapest {3:0.0} m/s, cap {4:0.0} m/s, chosen UT {5:0} ({6:0.0} m/s, wait {7:0.0} h)",
                    _windows.Count, (_latest - _earliest) / 3600, PlaneAngle(_system, _moon, _orbit, _earliest), _cheapest, _cheapest + CostCapMs, best.Ut, best.DeltaV, (best.Ut - _now) / 3600);
                if (_reach) Report(best, string.Format(CultureInfo.InvariantCulture, "no window it can afford lands within {0:0} km in the {1:0} h searched", ReachKm, (_reachedTo - _earliest) / 3600));
                return best;
            }

            /// <summary>
            /// #880: the most a window's whole burn may cost: <see cref="DeorbitBurn.SpareDv"/> (one spend rule, #907) less <see cref="DeorbitBurn.WindowBuffer"/>, so the Course Correction and the Late Trim keep their
            /// room. The whole burn and not its cost over the cheapest: the ejection itself comes out of the same delta-v. NaN when no prediction says the landing speed.
            /// </summary>
            private double MostDv(IEnumerable<ReturnPlan> ranked)
            {
                var landing = _ask.Landing;
                var powered = _ask.PoweredLandingDv;
                if (!powered.HasValue)
                {
                    var landed = ranked.Select(w => w.Landing).FirstOrDefault() ?? Predict(First);
                    if (landed == null || landed.Error != null) return double.NaN;
                    powered = DeorbitBurn.PoweredLandingDv(landing.Craft, true, landed.LandingSpeed);
                }
                return DeorbitBurn.SpareDv(_ask.DvLeft.Value, powered.Value, _ask.LaterLegsDv, landing.Payer) - DeorbitBurn.WindowBuffer(_ask.DvLeft.Value, landing.Payer);
            }

            /// <summary>
            /// #880: the earliest window landing within <see cref="ReachKm"/> of the target whose burn fits <see cref="MostDv"/>, from the windows of every orbit of the craft up to
            /// <see cref="_reachLatest"/>; null when none does, for #816's rule. A window's least burn close enough is aimed (<see cref="Aim(ReturnPlan)"/>) in time, as #816's are.
            /// </summary>
            private ReturnPlan Reached(IEnumerable<ReturnPlan> ranked)
            {
                _reachedTo = _reachLatest;
                var most = MostDv(ranked);
                if (double.IsNaN(most) || most < _cheapest) return null;
                _dvLimit = most;
                var latest = _latest;
                _latest = _reachLatest;
                try
                {
                    var spans = Spans(_reachLatest);
                    var direct = DirectBurns(spans);
                    var floor = direct.Where(d => d > 0).DefaultIfEmpty(0).Min();
                    var skip = Math.Max(SkipOverDirectMs, DirectPerLeastBurn * (most - _cheapest));
                    var predictions = 0;
                    for (var k = 0; k < spans.Count; k++)
                    {
                        if (predictions >= MostReachPredictions)
                        {
                            _reachedTo = spans[k][0];
                            break;
                        }
                        if (floor > 0 && direct[k] > floor + skip) continue;
                        ReturnPlan window;
                        if (!_solvedAt.TryGetValue(k, out window))
                        {
                            window = ReturnBurn.Find(_system, _moon, _orbit, _now, Plain(spans[k][0], spans[k][1]));
                            if (window.Error != null) continue;
                        }
                        if (window.DeltaV > most) continue;
                        if (window.Landing == null)
                        {
                            predictions++;
                            var landed = Predict(window);
                            if (landed.Error != null) continue;
                            Stamp(window, landed);
                        }
                        if (window.ErrorKm > WorthAimingKm) continue;
                        predictions += AimPredictions;
                        var aimed = window.ErrorKm <= CloseKm ? window : Aim(window);
                        if (aimed.ErrorKm > ReachKm || aimed.DeltaV > most) continue;
                        aimed.SpreadKm = Spread(aimed);
                        aimed.AimDv = Math.Max(0, aimed.DeltaV - _cheapest);
                        aimed.WindowNote = string.Format(CultureInfo.InvariantCulture, "; windows to {0:0.0} h, cheapest {1:0.0} m/s, up to {2:0.0} m/s, chosen UT {3:0} ({4:0.0} m/s, wait {5:0.0} h)",
                            (_reachLatest - _earliest) / 3600, _cheapest, most, aimed.Ut, aimed.DeltaV, (aimed.Ut - _now) / 3600);
                        Report(aimed, null);
                        return aimed;
                    }
                    return null;
                }
                finally
                {
                    _latest = latest;
                }
            }

            /// <summary>#880: the same report from an orbit in the moon's plane, whose windows step with the moon and land near the target within one turn.</summary>
            public void ReportStepped(ReturnPlan plan)
            {
                if (_states) Report(plan, null);
            }

            /// <summary>#880: the wait, the delta-v and the expected landing distance, in words, with what went wrong first.</summary>
            private void Report(ReturnPlan plan, string miss)
            {
                plan.AimNote = (miss == null ? "" : miss + ": ") + string.Format(CultureInfo.InvariantCulture, "window waits {0:0.0} h, {1:0.0} m/s, expected {2:0.0} km from the target", (plan.Ut - _now) / 3600, plan.DeltaV, plan.ErrorKm);
                if (miss != null) plan.WindowNote += "; " + miss;
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
                return Aim(least);
            }

            /// <summary>The burn near <paramref name="least"/>, a window's least burn with its landing stamped, that comes down nearest the target.</summary>
            private ReturnPlan Aim(ReturnPlan least)
            {
                AlongKm = double.PositiveInfinity;
                var landed = least.Landing;
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
                if (plan.ErrorKm < best.ErrorKm && plan.DeltaV <= _dvLimit) best = plan;
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
