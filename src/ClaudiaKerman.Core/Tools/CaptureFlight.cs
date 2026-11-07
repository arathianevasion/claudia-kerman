// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What <c>capture</c> tells <see cref="CaptureFlight.Next"/>: its options, and how far it has got.</summary>
    internal sealed class CaptureAsk
    {
        /// <summary>The circular orbit's altitude, m; null for "auto".</summary>
        public double? Alt;
        public PlaneOption Plane = PlaneOption.Auto;
        /// <summary>The most a tilt may add to the capture, m/s; null for "auto": the larger of 50 and 5% of the capture burn.</summary>
        public double? MaxPlaneDv;
        /// <summary>TURNTIME's turn_time, s.</summary>
        public double TurnTime;
        /// <summary>Where the flight is: <see cref="CaptureFlight.Start"/> first, then the phase the last step named.</summary>
        public string Phase = CaptureFlight.Start;
        /// <summary>The way the leftover tilt is flown, carried from the start (<see cref="CaptureMethod"/>).</summary>
        public string Method = "";
        /// <summary>The burns flown so far.</summary>
        public int Burns;
    }

    /// <summary>The ways <c>capture</c> flies a leftover tilt (#788 item 4).</summary>
    internal static class CaptureMethod
    {
        /// <summary>No tilt to fly: the capture burn alone.</summary>
        public const string Plain = "plain";
        /// <summary>Folded into the capture burn at periapsis, what a burn there can't reach flown at a node of the low orbit.</summary>
        public const string Periapsis = "periapsis";
        /// <summary>Capture into a high ellipse, change plane at its apoapsis (or the node nearest it), then lower.</summary>
        public const string Apoapsis = "apoapsis";
        /// <summary>The tilt is over max_plane_dv: the capture burn alone, the leftover reported.</summary>
        public const string Over = "over_max_plane_dv";
    }

    /// <summary>The choice between the two ways of flying a leftover tilt, and what each costs (m/s over the plain capture).</summary>
    internal sealed class CaptureChoice
    {
        public string Method = CaptureMethod.Plain;
        /// <summary>The capture burn with no tilt, m/s.</summary>
        public double PlainDv;
        /// <summary>What each way adds to it; infinity where it can't be flown.</summary>
        public double PeriapsisCost;
        public double ApoapsisCost = double.PositiveInfinity;
        public double MaxPlaneDv;
        /// <summary>Degrees between the arrival orbit and the plane wanted.</summary>
        public double TiltDeg;
        /// <summary>For <see cref="CaptureMethod.Apoapsis"/>: the ellipse's apoapsis radius, m.</summary>
        public double HighR;

        /// <summary>What the cheaper way would cost, m/s.</summary>
        public double BestCost
        {
            get { return Math.Min(PeriapsisCost, ApoapsisCost); }
        }
    }

    /// <summary>
    /// <c>capture</c>'s flight (#788, ADR 0014): one step at a time, so the block only flies nodes with exec_node and reports. From an
    /// arrival inside the body's sphere of influence, it burns at periapsis into a circular orbit at <c>alt</c>, in the arrival plane
    /// <c>transfer_to</c> recorded. A tilt the approach couldn't take out is flown the cheaper way (<see cref="Choose"/>), or, over
    /// max_plane_dv, not at all: the capture stands and the block reports the leftover without failing, for Claudia at the Debrief.
    /// Every burn is worked from the orbit as flown, so a short or long burn is taken up by the next one.
    /// </summary>
    internal static class CaptureFlight
    {
        public const string Start = "start";
        /// <summary>In the high ellipse, before its plane change.</summary>
        private const string High = "high";
        /// <summary>Shaping the orbit to a circle at alt.</summary>
        private const string Shape = "shape";
        /// <summary>A circle at alt; the tilt's burn at its node flown or not.</summary>
        private const string Tilted = "tilted";
        /// <summary>#866: in the high ellipse kept for the next leg's moon, before its tilt; then before its periapsis is set to alt; then checking its window.</summary>
        private const string Keep = "keep";
        private const string KeepPe = "keep_pe";
        private const string Kept = "kept";
        /// <summary>The report's key for what was kept, or why the circle was flown instead (#866).</summary>
        internal const string KeptKey = "kept";

        /// <summary>Degrees off the plane that count as in it: the arrival plane's tolerance.</summary>
        internal const double InPlaneDeg = 0.5;
        /// <summary>The shares of the sphere of influence the high ellipse may reach: the highest whose path meets nothing.</summary>
        private static readonly double[] HighShares = { 0.5, 0.25, 0.1 };
        /// <summary>The floor of the default max_plane_dv, m/s, and its share of the capture burn.</summary>
        internal const double MaxPlaneFloor = 50;
        internal const double MaxPlaneShare = 0.05;
        /// <summary>How far an apsis may be from alt, as a share of its radius, with the orbit counted as round there.</summary>
        private const double ShapeTolerance = 0.005;
        /// <summary>The most burns a capture takes: capture, plane, lower, a tilt's, and two to round off a short burn.</summary>
        private const int MaxBurns = 8;
        /// <summary>The seconds past the turn a burn is planned at, as transfer_to's.</summary>
        private const double Lead = 60;
        /// <summary>exec_node's max_residual for a burn judged by how round it leaves the orbit, not by the node.</summary>
        internal const double RoundResidual = 1000000;
        /// <summary>Its max_residual for any other burn here: a long burn's node swings (#532), and the next step works from the orbit as flown, so no residual fails the block.</summary>
        private const double ByOrbit = 100000;

        public static TransferFlightStep Next(GameSnapshot snapshot, CaptureAsk ask)
        {
            var step = Decide(snapshot, ask);
            step.Drops = ask.Burns + (step.Do == TransferFlightStep.Burn ? 1 : 0);
            return step;
        }

        private static TransferFlightStep Decide(GameSnapshot snapshot, CaptureAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            string unread;
            var plane = PlaneFor(snapshot, setup, ask, out unread);
            if (unread != "") return Fail(unread);
            var altitude = Altitude(setup, ask, plane);
            var floor = setup.Body.Radius + Math.Max(0, setup.Body.AtmosphereHeight);
            if (!(setup.Body.Radius + altitude > floor)) return Fail("alt " + Num(altitude) + " m is inside the atmosphere or below the surface");
            var targetR = setup.Body.Radius + altitude;
            if (setup.Body.SoiRadius > 0 && targetR >= setup.Body.SoiRadius) return Fail("alt " + Num(altitude) + " m is outside " + setup.Body.Name + "'s sphere of influence");
            if (ask.Burns > MaxBurns) return Done(setup, plane, ask, targetR, "the orbit is not round at " + Num(altitude) + " m after " + ask.Burns + " burns");
            switch (ask.Phase)
            {
                case Start: return Begin(snapshot, setup, ask, plane, targetR, floor);
                case High: return InHigh(setup, ask, plane, targetR);
                case Keep:
                case KeepPe:
                case Kept: return InKeep(snapshot, setup, ask, plane, targetR);
                case Shape:
                case Tilted: return Round(setup, ask, plane, targetR);
                default: return Fail("no capture phase named " + ask.Phase);
            }
        }

        /// <summary>The plane: Claudia's explicit value, else what <c>transfer_to</c> recorded for this body, else the same rule it used; null for none.</summary>
        private static ArrivalPlane PlaneFor(GameSnapshot snapshot, PredictBurn.Setup setup, CaptureAsk ask, out string unread)
        {
            unread = "";
            var option = ask.Plane ?? PlaneOption.Auto;
            if (option.IsAuto)
            {
                var recorded = ArrivalPlane.FromRecord(snapshot.RecordedPlane);
                if (recorded != null && string.Equals(recorded.Arrival, setup.Body.Name, StringComparison.OrdinalIgnoreCase)) return recorded.Source == ArrivalPlane.NoneSource ? null : recorded;
            }
            return ArrivalPlanes.Resolve(snapshot, setup.System, setup.Body, option, out unread);
        }

        /// <summary>
        /// The altitude, m: <c>alt</c>; a Target Vessel's periapsis altitude round this body; else the periapsis the craft arrived at, or the body's
        /// low orbit if that is under it. It keeps a higher periapsis because <c>target_pe</c> chose it and the Map's low orbit is a budgeting figure
        /// (Minmus's, 4.2 km, is under its peaks).
        /// </summary>
        private static double Altitude(PredictBurn.Setup setup, CaptureAsk ask, ArrivalPlane plane)
        {
            if (ask.Alt.HasValue) return ask.Alt.Value;
            if (plane != null && plane.VesselPe.HasValue) return plane.VesselPe.Value;
            return Math.Max(setup.Start.PeR, DeltaVMap.LowOrbit(setup.Body)) - setup.Body.Radius;
        }

        /// <summary>The first burn: refused where it can't be flown, none needed when the orbit is already there, else by the way <see cref="Choose"/> picks.</summary>
        private static TransferFlightStep Begin(GameSnapshot snapshot, PredictBurn.Setup setup, CaptureAsk ask, ArrivalPlane plane, double targetR, double floor)
        {
            var orbit = setup.Start;
            var now = setup.Now;
            if (orbit.PeR <= floor) return Fail("the periapsis at " + Num(orbit.PeR - setup.Body.Radius) + " m is inside the atmosphere or below the surface: raise it first");
            var after = orbit.Closed ? now + ask.TurnTime + Lead : now;
            var peUt = orbit.NextPeriapsis(after);
            if (!peUt.HasValue) return Fail("the craft is past periapsis, leaving " + setup.Body.Name + ": there is no capture to burn");
            var path = SingleShooting.Follow(setup.System, setup.Body, orbit, now, 1);
            if (path != null && path.Count > 0 && path[0].End != PatchEnd.Stable && path[0].EndUt < peUt.Value)
                return Fail("the craft leaves " + setup.Body.Name + "'s sphere of influence before its periapsis: coast into the sphere of influence of the body it is arriving at first");
            var inPlane = plane == null || plane.OffBy(orbit.W) <= InPlaneDeg;
            if (orbit.Closed && inPlane && IsRound(orbit, targetR)) return Done(setup, plane, ask, targetR, "");

            var choice = Choose(setup.System, setup.Body, orbit, peUt.Value, targetR, plane, ask.MaxPlaneDv);
            Vec3 r, v;
            orbit.State(peUt.Value, out r, out v);
            var circle = "";
            if (!orbit.Closed)
            {
                var kept = BeginKeep(snapshot, setup, plane, choice, peUt.Value, r, v, targetR, out circle);
                if (kept != null) return kept;
            }
            var keep = orbit.W;
            var otherR = Math.Max(orbit.PeR, targetR);
            if (choice.Method == CaptureMethod.Periapsis) keep = plane.Through(r, orbit.W).Normal;
            if (choice.Method == CaptureMethod.Apoapsis) otherR = choice.HighR;
            // From a closed orbit a folded tilt's eccentricity rises mid-burn, so it can't stop at the roundest.
            var isRound = choice.Method != CaptureMethod.Apoapsis && Math.Abs(otherR - orbit.PeR) <= ShapeTolerance * otherR && !(orbit.Closed && choice.Method == CaptureMethod.Periapsis);
            var step = Node(orbit, peUt.Value, Tangential(r, v, otherR, keep, orbit.Mu), "capture", choice.Method == CaptureMethod.Apoapsis ? High : Shape, choice.Method, isRound);
            step.Report["capture_method"] = choice.Method;
            if (choice.Method == CaptureMethod.Over) step.Report["plane_leftover_dv"] = Math.Round(choice.BestCost, 1);
            if (circle != "") step.Report[KeptKey] = circle;
            return step;
        }

        /// <summary>
        /// #866: with a moon of this planet next (<see cref="KeptRoute.NextMoon"/>), the capture burn into the high ellipse timed to it (<see cref="KeptRoute.Plan"/>),
        /// in the arrival plane: a tilt is flown at its node high up, as the apoapsis method's. Null for the circle, <paramref name="circle"/> saying why ("" with no Mission Plan).
        /// </summary>
        private static TransferFlightStep BeginKeep(GameSnapshot snapshot, PredictBurn.Setup setup, ArrivalPlane plane, CaptureChoice choice, double peUt, Vec3 r, Vec3 v,
            double targetR, out string circle)
        {
            double? deadline;
            var body = setup.Body;
            var moon = KeptRoute.NextMoon(snapshot, setup.System, body, out deadline, out circle);
            if (moon == null)
            {
                if (circle != "") circle = "circle: " + circle;
                return null;
            }
            var orbit = setup.Start;
            var plan = KeptRoute.Plan(snapshot, setup.System, body, moon, r, orbit.W, peUt, targetR, HighShares[0] * body.SoiRadius, MoonPe(moon), deadline, out circle);
            if (plan == null)
            {
                circle = "circle: " + circle;
                return null;
            }
            var method = CaptureMethod.Plain;
            if (choice.TiltDeg > InPlaneDeg)
            {
                var ellipse = Conic.FromState(orbit.Mu, r, Vec3.Cross(orbit.W, r.Normalized) * KeptRoute.Speed(orbit.Mu, r.Length, plan.HighR), peUt);
                var turn = TiltPoint(ellipse, plane, peUt);
                method = turn != null && turn.Burn.Length <= choice.MaxPlaneDv ? CaptureMethod.Apoapsis : CaptureMethod.Over;
                if (method == CaptureMethod.Over && turn != null) choice.ApoapsisCost = turn.Burn.Length;
            }
            var step = Node(orbit, peUt, Tangential(r, v, plan.HighR, orbit.W, orbit.Mu), "capture", Keep, method, false);
            step.Report["capture_method"] = method;
            if (method == CaptureMethod.Over) step.Report["plane_leftover_dv"] = Math.Round(choice.ApoapsisCost, 1);
            step.Report[KeptKey] = KeptLine(body, moon, targetR, plan.HighR, plan.Departure.Ut, plan.Loop);
            return step;
        }

        /// <summary>
        /// In the kept ellipse: the tilt at its node, then the periapsis set to alt at the apoapsis, then the window checked on the orbit as flown. A window
        /// within <see cref="KeptRoute.MaxLoops"/> loops ends the capture there; none (a burn off, a Due Time moved): the circle at alt, saying why.
        /// </summary>
        private static TransferFlightStep InKeep(GameSnapshot snapshot, PredictBurn.Setup setup, CaptureAsk ask, ArrivalPlane plane, double targetR)
        {
            var orbit = setup.Start;
            if (!orbit.Closed) return Round(setup, ask, plane, targetR);
            var after = setup.Now + ask.TurnTime + Lead;
            if (ask.Phase == Keep && ask.Method == CaptureMethod.Apoapsis && plane != null && plane.OffBy(orbit.W) > InPlaneDeg)
            {
                var turn = TiltPoint(orbit, plane, after);
                if (turn != null) return Node(orbit, turn.Ut, turn.Burn, "plane", KeepPe, ask.Method, false);
            }
            if (ask.Phase != Kept && Math.Abs(orbit.PeR - targetR) > ShapeTolerance * targetR)
                return ApsisBurn(orbit, orbit.NextApoapsis(after), targetR, ask, Kept, false);
            double? deadline;
            string why;
            var body = setup.Body;
            var moon = KeptRoute.NextMoon(snapshot, setup.System, body, out deadline, out why);
            if (moon != null && KeptRoute.IsKept(setup.System, body, orbit, moon))
            {
                int loop;
                var window = KeptRoute.Departure(snapshot, setup.System, body, orbit, moon, MoonPe(moon), after, after + KeptRoute.MaxLoops * orbit.Period, deadline, out loop);
                if (window != null)
                {
                    var done = Done(setup, plane, ask, targetR, "");
                    done.Report[KeptKey] = KeptLine(body, moon, orbit.PeR, orbit.ApR, window.Ut, loop);
                    return done;
                }
                why = "no " + moon.Name + " window within " + KeptRoute.MaxLoops + " loops of this orbit" + (deadline.HasValue ? " before the next fixed date" : "");
            }
            var step = Round(setup, ask, plane, targetR);
            step.Report[KeptKey] = "circle: " + (why != "" ? why : "the orbit kept no longer reaches " + (moon != null ? moon.Name : "the moon"));
            return step;
        }

        /// <summary>The moon's periapsis a window is judged at: the Delta-v Map's low orbit there (transfer_to's own target_pe is asked when it flies).</summary>
        private static double MoonPe(SnapshotBody moon)
        {
            return DeltaVMap.LowOrbit(moon) - moon.Radius;
        }

        /// <summary>"kept 60 x 3,300 km for Ike: window at periapsis 1, UT 169,104,601".</summary>
        private static string KeptLine(SnapshotBody body, SnapshotBody moon, double peR, double apR, double windowUt, int loop)
        {
            return "kept " + KeptRoute.Shape(body, peR, apR) + " for " + moon.Name + ": window at periapsis " + loop + ", UT " + MoonCrossing.Num(windowUt);
        }

        /// <summary>
        /// Which way to fly the tilt (#788 item 4). (a) folded into the capture at periapsis, through the plane closest to the one wanted that a burn
        /// there can reach, what that leaves flown at a node of the low orbit; (b) into a high ellipse, the plane changed where it is slow, at its
        /// apoapsis or the node of the two planes farthest from periapsis, then lowered. The cheaper, unless it adds more than max_plane_dv.
        /// </summary>
        internal static CaptureChoice Choose(BodySystem system, SnapshotBody body, Conic orbit, double peUt, double targetR, ArrivalPlane plane, double? maxPlaneDv)
        {
            Vec3 r, v;
            orbit.State(peUt, out r, out v);
            var mu = orbit.Mu;
            var rp = orbit.PeR;
            var vp = v.Length;
            var otherR = Math.Max(rp, targetR);
            var after = Math.Sqrt(mu * (2 / rp - 2 / (rp + otherR)));
            var plain = Math.Abs(vp - after);
            var choice = new CaptureChoice { PlainDv = plain, PeriapsisCost = 0, MaxPlaneDv = maxPlaneDv ?? Math.Max(MaxPlaneFloor, MaxPlaneShare * plain) };
            if (plane == null) return choice;
            choice.TiltDeg = plane.OffBy(orbit.W);
            if (choice.TiltDeg <= InPlaneDeg) return choice;

            var reach = plane.Through(r, orbit.W);
            var folded = ArrivalPlane.Angle(orbit.W, reach.Normal) * Math.PI / 180;
            var firstBurn = Math.Sqrt(vp * vp + after * after - 2 * vp * after * Math.Cos(folded));
            var rest = reach.LeftoverDeg > InPlaneDeg ? 2 * Math.Sqrt(mu / targetR) * Math.Sin(reach.LeftoverDeg * Math.PI / 360) : 0;
            choice.PeriapsisCost = firstBurn + rest - plain;

            if (body.SoiRadius > 0)
            {
                foreach (var share in HighShares)
                {
                    var highR = share * body.SoiRadius;
                    if (highR <= 1.5 * otherR) break;
                    var ellipse = Conic.FromState(mu, r, Vec3.Cross(orbit.W, r.Normalized) * Math.Sqrt(mu * (2 / rp - 2 / (rp + highR))), peUt);
                    var path = SingleShooting.Follow(system, body, ellipse, peUt, 1);
                    if (path == null || path.Count == 0 || path[0].End != PatchEnd.Stable) continue;
                    var turn = TiltPoint(ellipse, plane, peUt);
                    if (turn == null) continue;
                    // The ellipse's two burns are the plain capture only from an arrival faster than the ellipse; from a closed orbit they are a climb and a fall.
                    var ellipseV = Math.Sqrt(mu * (2 / rp - 2 / (rp + highR)));
                    choice.ApoapsisCost = Math.Abs(vp - ellipseV) + turn.Burn.Length + Math.Abs(ellipseV - after) - plain;
                    choice.HighR = highR;
                    break;
                }
            }
            if (choice.BestCost > choice.MaxPlaneDv) choice.Method = CaptureMethod.Over;
            else choice.Method = choice.ApoapsisCost < choice.PeriapsisCost ? CaptureMethod.Apoapsis : CaptureMethod.Periapsis;
            return choice;
        }

        /// <summary>Where and how a closed orbit turns into the plane: the burn, its UT, and the plane it reaches.</summary>
        private sealed class Tilt
        {
            public double Ut;
            public Vec3 Burn;
        }

        /// <summary>
        /// The plane change on <paramref name="orbit"/> after <paramref name="after"/>: at a node of its plane and the plane wanted, the one farthest from
        /// periapsis. For an inclination alone, the apoapsis when a plane of that inclination runs through it, else where the orbit crosses the equator. Null when no point reaches it.
        /// </summary>
        private static Tilt TiltPoint(Conic orbit, ArrivalPlane plane, double after)
        {
            var points = new List<KeyValuePair<Vec3, Vec3>>();
            if (plane.Normal.HasValue)
            {
                var line = Vec3.Cross(orbit.W, plane.Normal.Value);
                if (line.Length < 1e-9) line = orbit.P; // Reversed in place: any point turns it.
                points.Add(new KeyValuePair<Vec3, Vec3>(line, plane.Normal.Value));
                points.Add(new KeyValuePair<Vec3, Vec3>(line * -1, plane.Normal.Value));
            }
            else
            {
                var high = orbit.P * -1;
                var reach = plane.Through(high, orbit.W);
                if (reach.LeftoverDeg <= InPlaneDeg) points.Add(new KeyValuePair<Vec3, Vec3>(high, reach.Normal));
                else
                {
                    var equator = Vec3.Cross(new Vec3(0, 0, 1), orbit.W);
                    if (equator.Length < 1e-9) equator = orbit.P;
                    foreach (var at in new[] { equator, equator * -1 }) points.Add(new KeyValuePair<Vec3, Vec3>(at, plane.Through(at, orbit.W).Normal));
                }
            }
            Tilt best = null;
            var bestR = double.NegativeInfinity;
            foreach (var point in points)
            {
                var nu = Math.Atan2(Vec3.Dot(point.Key, orbit.Q), Vec3.Dot(point.Key, orbit.P));
                var ut = orbit.TimeAtTrue(nu, after);
                if (!ut.HasValue) continue;
                Vec3 r, v;
                orbit.State(ut.Value, out r, out v);
                if (r.Length <= bestR) continue;
                var up = r.Normalized;
                var outward = up * Vec3.Dot(v, up);
                var along = (v - outward).Length;
                best = new Tilt { Ut = ut.Value, Burn = outward + Vec3.Cross(point.Value, up) * along - v };
                bestR = r.Length;
            }
            return best;
        }

        /// <summary>In the high ellipse: the plane change, or straight on to the shaping when it is already in the plane.</summary>
        private static TransferFlightStep InHigh(PredictBurn.Setup setup, CaptureAsk ask, ArrivalPlane plane, double targetR)
        {
            var orbit = setup.Start;
            if (plane == null || plane.OffBy(orbit.W) <= InPlaneDeg) return Round(setup, ask, plane, targetR);
            var turn = TiltPoint(orbit, plane, setup.Now + ask.TurnTime + Lead);
            if (turn == null) return Round(setup, ask, plane, targetR);
            return Node(orbit, turn.Ut, turn.Burn, "plane", Shape, ask.Method, false);
        }

        /// <summary>
        /// Takes a closed orbit to a circle at <paramref name="targetR"/>, one burn at a time from the orbit as flown: raising or lowering the far side
        /// from an apsis, then rounding off at the other; then a folded capture's leftover at its node; then the end.
        /// </summary>
        private static TransferFlightStep Round(PredictBurn.Setup setup, CaptureAsk ask, ArrivalPlane plane, double targetR)
        {
            var orbit = setup.Start;
            if (!orbit.Closed) return Done(setup, plane, ask, targetR, "the capture burn left the craft on an escape orbit (ecc " + orbit.E.ToString("0.###", CultureInfo.InvariantCulture) + ")");
            var after = setup.Now + ask.TurnTime + Lead;
            var tolerance = ShapeTolerance * targetR;
            var low = orbit.PeR;
            var high = orbit.ApR;
            var phase = ask.Phase == Tilted ? Tilted : Shape;
            if (low > targetR + tolerance) return ApsisBurn(orbit, orbit.NextApoapsis(after), targetR, ask, phase, false);
            if (high < targetR - tolerance) return ApsisBurn(orbit, orbit.NextPeriapsis(after), targetR, ask, phase, false);
            if (high > targetR + tolerance) return ApsisBurn(orbit, orbit.NextPeriapsis(after), targetR, ask, phase, true);
            if (low < targetR - tolerance) return ApsisBurn(orbit, orbit.NextApoapsis(after), targetR, ask, phase, true);
            if (ask.Phase != Tilted && ask.Method == CaptureMethod.Periapsis && plane != null && plane.OffBy(orbit.W) > InPlaneDeg)
            {
                var turn = TiltPoint(orbit, plane, after);
                if (turn != null) return Node(orbit, turn.Ut, turn.Burn, "plane", Tilted, ask.Method, false);
            }
            return Done(setup, plane, ask, targetR, "");
        }

        /// <summary>A burn at an apsis to the orbit from there to <paramref name="targetR"/>.</summary>
        private static TransferFlightStep ApsisBurn(Conic orbit, double? ut, double targetR, CaptureAsk ask, string phase, bool round)
        {
            if (!ut.HasValue) return Fail("the orbit has no apsis to burn at");
            Vec3 r, v;
            orbit.State(ut.Value, out r, out v);
            return Node(orbit, ut.Value, Tangential(r, v, targetR, orbit.W, orbit.Mu), "capture", phase, ask.Method, round);
        }

        /// <summary>The burn at <paramref name="r"/> (an apsis of the orbit it leaves on) to the orbit in the plane of <paramref name="normal"/> whose other apsis is <paramref name="otherR"/>.</summary>
        private static Vec3 Tangential(Vec3 r, Vec3 v, double otherR, Vec3 normal, double mu)
        {
            var radius = r.Length;
            var speed = Math.Sqrt(mu * (2 / radius - 2 / (radius + otherR)));
            return Vec3.Cross(normal, r / radius) * speed - v;
        }

        private static bool IsRound(Conic orbit, double targetR)
        {
            return Math.Abs(orbit.PeR - targetR) <= ShapeTolerance * targetR && Math.Abs(orbit.ApR - targetR) <= ShapeTolerance * targetR;
        }

        private static TransferFlightStep Node(Conic orbit, double ut, Vec3 burn, string kind, string then, string method, bool round)
        {
            double prograde, normal, radial;
            SingleShooting.NodeFrame(orbit, ut, burn, out prograde, out normal, out radial);
            return new TransferFlightStep
            {
                Do = TransferFlightStep.Burn, Kind = kind, Ut = ut, Prograde = prograde, Normal = normal, Radial = radial, Phase = then, Why = method,
                MaxResidual = round ? RoundResidual : ByOrbit,
            };
        }

        /// <summary>The end: what the orbit is, and the tilt left, in degrees; <paramref name="why"/> "" or why the block fails.</summary>
        private static TransferFlightStep Done(PredictBurn.Setup setup, ArrivalPlane plane, CaptureAsk ask, double targetR, string why)
        {
            var step = new TransferFlightStep { Do = TransferFlightStep.Done, Error = why };
            var orbit = setup.Start;
            step.Report["ecc"] = Math.Round(orbit.E, 5);
            step.Report["inc"] = Math.Round(orbit.Inclination, 2);
            step.Report["plane_leftover_deg"] = plane != null ? Math.Round(plane.OffBy(orbit.W), 2) : 0;
            if (ask.Method != "") step.Report["capture_method"] = ask.Method;
            return step;
        }

        private static TransferFlightStep Fail(string why)
        {
            return new TransferFlightStep { Do = TransferFlightStep.Done, Error = why ?? "" };
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
