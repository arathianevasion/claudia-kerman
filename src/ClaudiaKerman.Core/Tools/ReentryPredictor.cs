// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools.Numerics;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The air of a body for the reentry predictor: the adapter reads KSP's own pressure and temperature curves; tests give a
    /// table. It is asked hundreds of times per prediction, so it must be cheap.
    /// </summary>
    public interface IReentryAir
    {
        /// <summary>
        /// Density (kg/m³) and speed of sound (m/s) at <paramref name="altitude"/> m above sea level over <paramref name="latitude"/>
        /// degrees; density 0 above the atmosphere.
        /// </summary>
        void Air(double altitude, double latitude, out double density, out double speedOfSound);

        /// <summary>Static pressure, atm, at <paramref name="altitude"/> m above sea level (0 above the atmosphere): when parachutes open.</summary>
        double PressureAtm(double altitude);
    }

    /// <summary>
    /// The craft for the reentry predictor, held surface retrograde (as <c>reentry</c> flies it): the adapter reads KSP's drag
    /// cubes for that attitude; tests give the research's capsule.
    /// </summary>
    public interface IReentryCraft
    {
        /// <summary>The craft's mass now, kg.</summary>
        double MassKg { get; }

        /// <summary>
        /// The drag area, m², held retrograde at <paramref name="mach"/>, so that drag (N) = ½ρv² × this; <paramref name="densitySpeed"/>
        /// (ρv, kg/m²/s) is for KSP's pseudo-Reynolds curve. Stowed parachutes are in it; open ones are <see cref="Chutes"/>'.
        /// </summary>
        double DragArea(double mach, double densitySpeed);

        /// <summary>
        /// The lift area, m², at <paramref name="mach"/> with the airflow <paramref name="angleOfAttack"/> degrees off retrograde
        /// (0 held retrograde, which is all the predictor flies): lift (N) = ½ρv² × this, square to the airflow, toward the
        /// craft's top (the sky, as kOS holds <c>srfretrograde</c>); negative pushes it down. 0 for a craft without body lift.
        /// </summary>
        double LiftArea(double mach, double angleOfAttack);

        /// <summary>The craft's parachutes, in no order; empty for none, never null.</summary>
        IList<ReentryChute> Chutes { get; }

        /// <summary>
        /// The craft once its heat shield has gone (its mass, drag and lift; its <see cref="Chutes"/> are not read, the chutes being
        /// this craft's), or null when the heat shield stays (none, or the block keeps it).
        /// </summary>
        IReentryCraft AfterShield { get; }
    }

    /// <summary>Where a parachute is in KSP's deployment: stowed, armed (waiting for its opening pressure), semi- or fully deployed.</summary>
    public enum ReentryChuteState
    {
        Stowed,
        Armed,
        SemiDeployed,
        Deployed,
    }

    /// <summary>
    /// One parachute for the reentry predictor, as KSP's ModuleParachute has it: once armed (by the block's rules,
    /// <see cref="ReentryRules"/>) it semi-deploys when the air reaches <see cref="MinPressureAtm"/>, and deploys fully at
    /// <see cref="DeployAltitude"/> above the ground, each opening over its time.
    /// </summary>
    public sealed class ReentryChute
    {
        /// <summary>A drogue chute: <c>reentry</c> opens drogues first, by their own rules, and the mains only once they are out.</summary>
        public bool Drogue;
        public ReentryChuteState State = ReentryChuteState.Stowed;
        /// <summary>KSP's minAirPressureToOpen, atm: an armed chute semi-deploys at this pressure or more.</summary>
        public double MinPressureAtm;
        /// <summary>KSP's deployAltitude, m above the ground: a semi-deployed chute deploys fully below it.</summary>
        public double DeployAltitude;
        /// <summary>The drag area, m², the chute adds semi-deployed and fully deployed (over the stowed chute's, in the craft's drag).</summary>
        public double SemiArea;
        public double FullArea;
        /// <summary>How long, s, the semi and full deployments take to open (KSP's semiDeploymentSpeed and deploymentSpeed), and the curve of the opening (deploymentCurve: area share = time share ^ this).</summary>
        public double SemiSeconds;
        public double FullSeconds;
        public double Curve = 1;
    }

    /// <summary>
    /// When the <c>reentry</c> block arms its parachutes and drops its heat shield, as reentry.ks has them
    /// (<c>reentry_chute_*</c>, <c>reentry_drogue_*</c>, <c>reentry_shield_*</c>), its "safe" chute mode; keep the two in step
    /// (ReentryScienceTests checks them). Speeds are air speeds, heights above the ground.
    /// </summary>
    public static class ReentryRules
    {
        /// <summary>Mains arm at or under this speed (m/s) in air of this pressure (atm) or more, or by this height (m) whatever the speed.</summary>
        public const double ChuteMaxSpeed = 250;
        public const double ChuteMinPressureAtm = 0.04;
        public const double ChuteLastAgl = 1500;
        /// <summary>Drogues the same, by their own numbers.</summary>
        public const double DrogueMaxSpeed = 500;
        public const double DrogueMinPressureAtm = 0.02;
        public const double DrogueLastAgl = 3000;
        /// <summary>The heat shield goes at or under this speed (m/s), or by this height (m).</summary>
        public const double ShieldSpeed = 400;
        public const double ShieldLastAgl = 3000;
    }

    /// <summary>
    /// How the frame orbit elements use sits on a turning body: the longitude under its x axis at <see cref="Ut"/>, and whether
    /// longitude grows with the frame's angle (1) or against it (-1), as <see cref="LandingSiteSearch.Frame"/> reads it.
    /// </summary>
    internal sealed class BodyFrame
    {
        public double LonOffset;
        public double Handed = 1;
        public double Ut;
        public double RotationPeriod;

        /// <summary>The frame of <paramref name="body"/> as it is at <paramref name="now"/>, or null with <paramref name="error"/> saying why it can't be read.</summary>
        public static BodyFrame Read(SnapshotBody body, IPlanningBody api, double now, out string error)
        {
            double lonOffset, handed;
            error = LandingSiteSearch.Frame(body.Name, api, out lonOffset, out handed);
            if (error != null) return null;
            return new BodyFrame { LonOffset = lonOffset, Handed = handed, Ut = now, RotationPeriod = body.RotationPeriod };
        }

        /// <summary>The body's angular velocity in the frame, rad/s: the air turns with it.</summary>
        public Vec3 Spin
        {
            get { return new Vec3(0, 0, RotationPeriod > 0 ? Handed * 2 * Math.PI / RotationPeriod : 0); }
        }

        /// <summary>The latitude and longitude, degrees, of the ground under <paramref name="r"/> at <paramref name="ut"/>.</summary>
        public void Ground(Vec3 r, double ut, out double latitude, out double longitude)
        {
            var length = r.Length;
            latitude = Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI;
            var turn = RotationPeriod > 0 ? 360 / RotationPeriod : 0;
            longitude = LandingSiteSearch.Wrap180(LonOffset + Handed * Math.Atan2(r.Y, r.X) * 180 / Math.PI - turn * (ut - Ut));
        }
    }

    /// <summary>Where a craft comes down, or why the predictor can't say.</summary>
    internal sealed class ReentryPrediction
    {
        /// <summary>Null when it landed; otherwise why there is no landing point.</summary>
        public string Error;
        public double Latitude;
        public double Longitude;
        /// <summary>When it first reaches the top of the atmosphere, and when it reaches the ground.</summary>
        public double EntryUt;
        public double LandingUt;
        /// <summary>Where it reaches the ground, in the body's inertial frame, m.</summary>
        public Vec3 Position;
        /// <summary>The most the air pulls on the craft on the way down, in g, its open parachutes left out (#921): what its entry pulls, which the periapsis decides and <see cref="CorrectionBurn.MostEntryG"/> judges. A chute's opening is a separate load no burn can change, and read at the integrator's trial states it spiked to over 100 g.</summary>
        public double PeakG;
        /// <summary>The air speed it reaches the ground at, m/s.</summary>
        public double LandingSpeed;
        /// <summary>When the first parachute starts to open (semi-deploys), and when the heat shield goes; -1 for never.</summary>
        public double ChuteUt = -1;
        public double ShieldUt = -1;
        /// <summary>The integrator's accepted steps, and the craft's drag asked for (each one also asks the air once).</summary>
        public int Steps;
        public int Evaluations;
    }

    /// <summary>
    /// The reentry predictor (#282, from the reentry research of #247; adaptive, with lift, parachutes and the heat shield's drop
    /// since #462): where a craft held surface retrograde lands. The vacuum leg follows the Kepler orbit (<see cref="Conic"/>,
    /// as predict_burn) to the top of the atmosphere; from there gravity, drag and lift are integrated against the turning
    /// atmosphere with <see cref="Bs3"/>'s adaptive step, as MechJeb's ReentrySimulation does. Its events end a stretch where the
    /// model changes (a parachute arms, semi-deploys or deploys fully; the heat shield goes, by <see cref="ReentryRules"/>) or
    /// the flight does (the ground; back out of the atmosphere), and the next stretch starts from there. The air and the craft
    /// come through <see cref="IReentryAir"/> and <see cref="IReentryCraft"/>. Shared by the deorbit burn search and the
    /// block-internal calls; it only reads.
    /// </summary>
    internal sealed class ReentryPredictor
    {
        private const double G0 = 9.80665;
        /// <summary>
        /// The integrator's relative tolerance per step on the state (position and velocity in the body's frame): within about
        /// 15 m of a 0.25 s RK4 step through Kerbin's atmosphere in about 215 steps, as many as the 2 s RK4 took and a quarter of
        /// a 0.5 s one's (1e-5 halves the steps but drifts 110 m; 1e-7 doubles them for 1 m). Under an open parachute the drag
        /// is stiff and the step stays near a second, so a fall under one takes about twice the steps.
        /// </summary>
        public const double Rtol = 1e-6;
        /// <summary>The largest step, s: high in the air, where the drag is all but none, the step would otherwise grow past what the events can see.</summary>
        public const double MaxStep = 20;
        /// <summary>A craft still flying after this long in the atmosphere is given up on.</summary>
        public const double MaxAtmosphereSeconds = 3600;
        /// <summary>Times it may skip back out of the atmosphere and fall in again.</summary>
        private const int MaxSkips = 3;
        /// <summary>The integrator's step cap per stretch.</summary>
        private const int MaxSteps = 5000;

        private readonly SnapshotBody _body;
        private readonly IReentryAir _air;
        private readonly Vec3 _spin;
        private readonly double _ground;
        private readonly List<Chute> _chutes = new List<Chute>();
        private IReentryCraft _craft;
        private double _mass;
        private double _peak;
        private int _evaluations;
        private bool _shieldGone;
        /// <summary>The unit normal of the airflow's plane as the craft entered the air: lift is square to it.</summary>
        private Vec3 _plane;

        /// <summary>A parachute's state over the flight, and when its current deployment began.</summary>
        private sealed class Chute
        {
            public ReentryChute Of;
            public ReentryChuteState State;
            public double OpenedUt = double.NegativeInfinity;
        }

        private ReentryPredictor(SnapshotBody body, IReentryAir air, IReentryCraft craft, BodyFrame frame, double ground, bool chutes)
        {
            _body = body;
            _air = air;
            _craft = craft;
            _spin = frame.Spin;
            _ground = ground;
            foreach (var chute in craft.Chutes ?? new ReentryChute[0])
                if (chute != null && (chutes || chute.State == ReentryChuteState.SemiDeployed || chute.State == ReentryChuteState.Deployed))
                    _chutes.Add(new Chute { Of = chute, State = chute.State });
        }

        /// <summary>
        /// Where the craft on <paramref name="orbit"/> round <paramref name="body"/> lands, falling from <paramref name="fromUt"/>,
        /// on ground <paramref name="groundAltitude"/> m above sea level, with mass <paramref name="massKg"/>, opening its chutes and
        /// dropping its heat shield by <see cref="ReentryRules"/>. With <paramref name="chutes"/> false (#477) no chute is armed or
        /// opened: each stays stowed, its drag as the craft has it stowed; one already open stays open.
        /// </summary>
        public static ReentryPrediction Predict(SnapshotBody body, Conic orbit, double fromUt, double groundAltitude, double massKg,
            IReentryAir air, IReentryCraft craft, BodyFrame frame, bool chutes = true)
        {
            if (body == null || orbit == null || air == null || craft == null || frame == null)
                return Fail("the body, orbit, air, craft and frame must all be given");
            if (body.AtmosphereHeight <= 0) return Fail(body.Name + " has no atmosphere");
            if (!SearchInput.Finite(fromUt) || !SearchInput.Finite(groundAltitude)) return Fail("the start time and the ground's height must be finite");
            if (!SearchInput.Finite(massKg) || massKg <= 0) return Fail("the craft's mass must be a positive number of kg");
            var predictor = new ReentryPredictor(body, air, craft, frame, groundAltitude, chutes) { _mass = massKg };
            return predictor.Fly(orbit, fromUt, frame);
        }

        private ReentryPrediction Fly(Conic orbit, double t, BodyFrame frame)
        {
            var radius = _body.Radius;
            var top = radius + _body.AtmosphereHeight;
            var prediction = new ReentryPrediction();
            if (orbit.Radius(t) <= radius + _ground) return Fail("the craft is already on the ground");
            var solver = new Bs3 { Rtol = Rtol, Atol = 1e-6, Hmax = MaxStep, MaxSteps = MaxSteps };
            OdeFunction f = Derivative;
            var groundEvent = new OdeEvent((y, ut) => Height(y) - _ground, -1);
            var outEvent = new OdeEvent((y, ut) => Height(y) - _body.AtmosphereHeight, 1);
            for (var skip = 0; skip <= MaxSkips; skip++)
            {
                if (orbit.Radius(t) > top)
                {
                    var entry = orbit.TimeAtRadius(top, t, false);
                    if (!entry.HasValue)
                        return Fail(skip == 0
                            ? "the orbit never reaches " + _body.Name + "'s atmosphere (periapsis " + Metres(orbit.PeR - radius) + " m)"
                            : "the craft skips out of " + _body.Name + "'s atmosphere and does not fall back in");
                    t = entry.Value;
                }
                if (skip == 0) prediction.EntryUt = t;
                Vec3 r, v;
                orbit.State(t, out r, out v);
                var y = new[] { r.X, r.Y, r.Z, v.X, v.Y, v.Z };
                var normal = Vec3.Cross(r, v - Vec3.Cross(_spin, r));
                _plane = normal.Length > 0 ? normal.Normalized : new Vec3(0, 0, 1);
                var end = t + MaxAtmosphereSeconds;
                var skipped = false;
                while (true)
                {
                    // Anything due already (a craft predicted from under its chutes' heights) happens before the stretch.
                    while (Happen(y, t, prediction)) { }
                    var events = new List<OdeEvent> { groundEvent, outEvent };
                    var changes = Changes(prediction);
                    events.AddRange(changes.Keys);
                    var leg = solver.Solve(f, y, t, end, events);
                    prediction.Steps += leg.Steps;
                    prediction.Evaluations = _evaluations;
                    if (leg.Status == OdeStatus.Finished) break;
                    if (leg.Status != OdeStatus.EventTerminated) return Fail("the fall could not be integrated: " + leg.Reason);
                    y = leg.Y;
                    t = leg.T;
                    if (leg.Event == groundEvent)
                    {
                        var at = new Vec3(y[0], y[1], y[2]);
                        double lat, lng;
                        frame.Ground(at, t, out lat, out lng);
                        prediction.Latitude = lat;
                        prediction.Longitude = lng;
                        prediction.LandingUt = t;
                        prediction.Position = at;
                        prediction.PeakG = _peak;
                        prediction.LandingSpeed = AirSpeed(y);
                        return prediction;
                    }
                    if (leg.Event == outEvent)
                    {
                        orbit = Conic.FromState(_body.Gm, new Vec3(y[0], y[1], y[2]), new Vec3(y[3], y[4], y[5]), t);
                        skipped = true;
                        break;
                    }
                    changes[leg.Event](t);
                }
                if (!skipped) return Fail("the craft is still flying after " + Metres(MaxAtmosphereSeconds) + " s in " + _body.Name + "'s atmosphere");
            }
            return Fail("the craft skips out of " + _body.Name + "'s atmosphere more than " + MaxSkips + " times");
        }

        /// <summary>
        /// The changes the next stretch may end at, each with its event: a function that crosses 0 upward where the change's
        /// condition comes true (its conditions' shares of their limits, combined by min for "and", max for "or").
        /// </summary>
        private Dictionary<OdeEvent, Action<double>> Changes(ReentryPrediction prediction)
        {
            var changes = new Dictionary<OdeEvent, Action<double>>();
            foreach (var chute in _chutes)
            {
                var c = chute;
                if (Pending(c)) changes.Add(new OdeEvent((y, ut) => Due(c, y), 1), ut => Advance(c, ut, prediction));
            }
            if (!ShieldStays) changes.Add(new OdeEvent((y, ut) => ShieldDue(y), 1), ut => DropShield(ut, prediction));
            return changes;
        }

        /// <summary>
        /// Makes the one change due at <paramref name="y"/> now, if any, without waiting for an event: a craft predicted from low
        /// down, or a chute armed in air already thick enough (KSP checks the pressure the moment it is armed). False for none.
        /// </summary>
        private bool Happen(double[] y, double t, ReentryPrediction prediction)
        {
            foreach (var chute in _chutes)
            {
                if (!Pending(chute) || Due(chute, y) < 0) continue;
                Advance(chute, t, prediction);
                return true;
            }
            if (ShieldStays || ShieldDue(y) < 0) return false;
            DropShield(t, prediction);
            return true;
        }

        /// <summary>Whether the chute has a next state to reach: the mains are not armed while a drogue is still stowed.</summary>
        private bool Pending(Chute chute)
        {
            if (chute.State == ReentryChuteState.Deployed) return false;
            return chute.State != ReentryChuteState.Stowed || chute.Of.Drogue || !DroguesPending();
        }

        /// <summary>The event function for the chute's next state: arming by the block's rule, semi-deploying by its pressure, deploying fully by its height.</summary>
        private double Due(Chute chute, double[] y)
        {
            switch (chute.State)
            {
                case ReentryChuteState.Stowed: return Arms(chute, y);
                case ReentryChuteState.Armed: return PressureShare(y, chute.Of.MinPressureAtm);
                default: return Below(y, chute.Of.DeployAltitude);
            }
        }

        private void Advance(Chute chute, double t, ReentryPrediction prediction)
        {
            chute.State++;
            if (chute.State < ReentryChuteState.SemiDeployed) return;
            chute.OpenedUt = t;
            if (prediction.ChuteUt < 0) prediction.ChuteUt = t;
        }

        private bool DroguesPending()
        {
            foreach (var chute in _chutes)
                if (chute.Of.Drogue && chute.State == ReentryChuteState.Stowed) return true;
            return false;
        }

        /// <summary>The heat shield goes: the craft is the one after it, lighter by the difference in their masses.</summary>
        private void DropShield(double t, ReentryPrediction prediction)
        {
            var after = _craft.AfterShield;
            _mass = Math.Max(1e-3, _mass - (_craft.MassKg - after.MassKg));
            _craft = after;
            _shieldGone = true;
            prediction.ShieldUt = t;
        }

        /// <summary>Whether no heat-shield drop is to come: it has gone, or the craft keeps it.</summary>
        private bool ShieldStays
        {
            get { return _shieldGone || _craft.AfterShield == null; }
        }

        private double Height(double[] y)
        {
            return Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]) - _body.Radius;
        }

        private double AirSpeed(double[] y)
        {
            var r = new Vec3(y[0], y[1], y[2]);
            return (new Vec3(y[3], y[4], y[5]) - Vec3.Cross(_spin, r)).Length;
        }

        /// <summary>The share by which the craft is under <paramref name="agl"/> m above the ground: 0 at it, positive below.</summary>
        private double Below(double[] y, double agl)
        {
            return (agl - (Height(y) - _ground)) / Math.Max(agl, 1);
        }

        /// <summary>The share by which the pressure is over <paramref name="atm"/>: 0 at it, positive above.</summary>
        private double PressureShare(double[] y, double atm)
        {
            var min = Math.Max(atm, 1e-9);
            return (_air.PressureAtm(Height(y)) - min) / min;
        }

        /// <summary>The share by which the air speed is under <paramref name="speed"/>: 0 at it, positive slower.</summary>
        private double Slower(double[] y, double speed)
        {
            return (speed - AirSpeed(y)) / Math.Max(speed, 1);
        }

        /// <summary>reentry's arming rule for the chute: slow enough in thick enough air, or low enough whatever the speed.</summary>
        private double Arms(Chute chute, double[] y)
        {
            var drogue = chute.Of.Drogue;
            var speed = Slower(y, drogue ? ReentryRules.DrogueMaxSpeed : ReentryRules.ChuteMaxSpeed);
            var pressure = PressureShare(y, drogue ? ReentryRules.DrogueMinPressureAtm : ReentryRules.ChuteMinPressureAtm);
            return Math.Max(Math.Min(speed, pressure), Below(y, drogue ? ReentryRules.DrogueLastAgl : ReentryRules.ChuteLastAgl));
        }

        private double ShieldDue(double[] y)
        {
            return Math.Max(Slower(y, ReentryRules.ShieldSpeed), Below(y, ReentryRules.ShieldLastAgl));
        }

        private void Derivative(double[] y, double t, double[] dy)
        {
            var a = Accel(new Vec3(y[0], y[1], y[2]), new Vec3(y[3], y[4], y[5]), t);
            dy[0] = y[3];
            dy[1] = y[4];
            dy[2] = y[5];
            dy[3] = a.X;
            dy[4] = a.Y;
            dy[5] = a.Z;
        }

        private Vec3 Accel(Vec3 r, Vec3 v, double t)
        {
            var length = r.Length;
            var a = r * (-_body.Gm / (length * length * length));
            var h = length - _body.Radius;
            if (h >= _body.AtmosphereHeight) return a;
            double density, sound;
            _air.Air(h, Math.Asin(Math.Max(-1, Math.Min(1, r.Z / length))) * 180 / Math.PI, out density, out sound);
            if (density <= 0) return a;
            var air = v - Vec3.Cross(_spin, r);
            var speed = air.Length;
            if (speed <= 0) return a;
            _evaluations++;
            var mach = sound > 0 ? speed / sound : 0;
            var bare = _craft.DragArea(mach, density * speed);
            var q = 0.5 * density * speed / _mass;
            var k = q * (bare + ChuteArea(t));
            _peak = Math.Max(_peak, q * bare * speed / G0);
            a = a - air * k;
            var liftArea = _craft.LiftArea(mach, 0);
            if (liftArea == 0) return a;
            // Lift is square to the airflow and to the orbit's plane as it entered: toward the sky while the craft flies along
            // its track, and still the same way, not flipping, once the fall turns vertical (the craft keeps its roll).
            var square = Vec3.Cross(air, _plane);
            var size = square.Length;
            if (size < 1e-9 * speed) return a;
            return a + square * (q * speed * liftArea / size);
        }

        /// <summary>The drag area the open chutes add at <paramref name="t"/>, each opening over its time.</summary>
        private double ChuteArea(double t)
        {
            double area = 0;
            foreach (var chute in _chutes)
            {
                if (chute.State == ReentryChuteState.SemiDeployed)
                    area += Opening(t, chute, chute.Of.SemiSeconds) * chute.Of.SemiArea;
                else if (chute.State == ReentryChuteState.Deployed)
                {
                    var share = Opening(t, chute, chute.Of.FullSeconds);
                    area += (1 - share) * chute.Of.SemiArea + share * chute.Of.FullArea;
                }
            }
            return area;
        }

        private static double Opening(double t, Chute chute, double seconds)
        {
            if (seconds <= 0 || t - chute.OpenedUt >= seconds) return 1;
            var share = Math.Max(0, (t - chute.OpenedUt) / seconds);
            return Math.Pow(share, chute.Of.Curve > 0 ? chute.Of.Curve : 1);
        }

        private static ReentryPrediction Fail(string reason)
        {
            return new ReentryPrediction { Error = reason };
        }

        private static string Metres(double value)
        {
            return Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
