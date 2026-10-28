/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>The right-hand side of y' = f(y, t): writes the derivative at <c>y</c> and <c>t</c> into <c>dy</c>.</summary>
    internal delegate void OdeFunction(double[] y, double t, double[] dy);

    /// <summary>Why a <see cref="Bs3"/> solve ended.</summary>
    internal enum OdeStatus
    {
        /// <summary>It reached the end time.</summary>
        Finished,
        /// <summary>An event fired: <see cref="OdeResult.Event"/> is which, and the state is at it.</summary>
        EventTerminated,
        /// <summary>It took the step cap without reaching the end time.</summary>
        MaxStepsExceeded,
        /// <summary>The error control asked for a step below the smallest it may take.</summary>
        StepTooSmall,
        /// <summary>The function gave NaN or infinity.</summary>
        NonFinite,
        /// <summary>A start state, time, tolerance or limit was not usable; the function was never called.</summary>
        InvalidInput,
    }

    /// <summary>
    /// What a <see cref="Bs3"/> solve ended with. It never throws on its input or its function's values: anything but
    /// <see cref="OdeStatus.Finished"/> and <see cref="OdeStatus.EventTerminated"/> carries a <see cref="Reason"/> in words.
    /// </summary>
    internal sealed class OdeResult
    {
        public OdeStatus Status;
        public string Reason;
        /// <summary>The time it ended at: the end time, the event's time, or where it stopped.</summary>
        public double T;
        /// <summary>The state at <see cref="T"/> (a copy the caller may keep).</summary>
        public double[] Y;
        /// <summary>The event that ended it, or null.</summary>
        public OdeEvent Event;
        /// <summary>Accepted steps, and calls of the function (rejected steps' included).</summary>
        public int Steps;
        public int Evaluations;
    }

    /// <summary>
    /// The Bogacki-Shampine 3(2) embedded Runge-Kutta integrator with adaptive steps and terminal events, ported from MechJebLib's
    /// <c>ODE/BS3.cs</c>, with the parts of <c>ODE/AbstractRungeKutta.cs</c> (the PI step control and the initial step from
    /// Hairer, Norsett and Wanner, as scipy has it) and <c>ODE/AbstractIVP.cs</c> (the solve loop and the events) that it uses
    /// (MechJeb2 a958383). It is first-same-as-last, so a step costs three calls of the function. An event is found by bisection
    /// on the step's cubic Hermite interpolant, as MechJeb's, and ends the solve there with the state interpolated to it.
    /// Unlike MechJeb's: the state is a plain array (no pooled vectors, interpolant or cancellation token); its input is checked
    /// in every build and every failure comes back as an <see cref="OdeResult"/> with a reason, never an exception; a
    /// non-finite derivative stops it rather than feeding NaN to the step control; and the error estimate is scaled by the step,
    /// as in scipy (MechJeb's leaves the step out, so its tolerances were on the derivative's error, not the state's).
    /// <para>
    /// Bogacki P, Shampine LF. A 3(2) pair of Runge-Kutta formulas. Applied Mathematics Letters. 1989;2(4):321–325.
    /// doi:10.1016/0893-9659(89)90079-7. MechJeb's note: BS3 needs tighter tolerances (about 1e-8) and more steps than DP5.
    /// </para>
    /// </summary>
    internal sealed class Bs3
    {
        private const string Name = "Bogacki-Shampine";
        private const double MaxFactor = 10;
        private const double MinFactor = 0.2;
        private const double Safety = 0.9;
        private const int ErrorEstimatorOrder = 2;
        private const int Order = 3;
        // MechJeb's PI controller: beta 0.2 / (q + 1), alpha 1 / (q + 1) - 0.75 beta, with q the error estimator's order.
        private const double Beta = 0.2 / (ErrorEstimatorOrder + 1.0);
        private const double Alpha = 1.0 / (ErrorEstimatorOrder + 1.0) - 0.75 * Beta;
        private const double Eps = 2.220446049250313e-16;

        private const double A21 = 0.5;
        private const double A32 = 0.75;
        private const double A41 = 2.0 / 9.0;
        private const double A42 = 1.0 / 3.0;
        private const double A43 = 4.0 / 9.0;
        private const double C2 = 0.5;
        private const double C3 = 0.75;
        private const double E1 = -5.0 / 72.0;
        private const double E2 = 1.0 / 12.0;
        private const double E3 = 1.0 / 9.0;
        private const double E4 = -0.125;

        /// <summary>The smallest step, s (it may be undercut by the last step to the end time).</summary>
        public double Hmin = 0;
        /// <summary>The largest step.</summary>
        public double Hmax = double.PositiveInfinity;
        /// <summary>The cap on accepted steps.</summary>
        public int MaxSteps = 2000;
        /// <summary>The relative and absolute tolerances on each component of the state, per step.</summary>
        public double Rtol = 1e-9;
        public double Atol = 1e-9;
        /// <summary>The first step; 0 picks one from the function.</summary>
        public double Hstart = 0;

        private int _n;
        private int _evaluations;
        private double[] _y, _dy, _ynew, _dynew, _k2, _k3, _k4;
        private double _t, _tnew, _habs, _lastErrorNorm;
        private int _direction;
        private bool _snapping;

        /// <summary>
        /// Integrates y' = <paramref name="f"/>(y, t) from <paramref name="y0"/> at <paramref name="t0"/> to <paramref name="tf"/>
        /// (either way), ending at the first of <paramref name="events"/> to fire. <paramref name="y0"/> is not changed.
        /// </summary>
        public OdeResult Solve(OdeFunction f, double[] y0, double t0, double tf, IReadOnlyList<OdeEvent> events = null)
        {
            var problem = Problem(f, y0, t0, tf, events);
            if (problem != null) return new OdeResult { Status = OdeStatus.InvalidInput, Reason = Name + ": " + problem, T = t0, Y = y0 == null ? null : (double[])y0.Clone() };

            _n = y0.Length;
            _y = (double[])y0.Clone();
            _dy = new double[_n];
            _ynew = new double[_n];
            _dynew = new double[_n];
            _k2 = new double[_n];
            _k3 = new double[_n];
            _k4 = new double[_n];
            _evaluations = 0;
            _lastErrorNorm = 1e-4;
            _direction = t0 != tf ? Math.Sign(tf - t0) : 1;
            _t = t0;
            var steps = 0;
            var maxStep = Hmax;

            if (!Call(f, _y, _t, _dy)) return End(OdeStatus.NonFinite, "the function is not finite at the start (t = " + R(_t) + ")", null, steps);
            _habs = Hstart > 0 ? Hstart : InitialStep(f);
            if (double.IsNaN(_habs)) return End(OdeStatus.NonFinite, "the function is not finite near the start (t = " + R(_t) + ")", null, steps);
            _habs = Math.Max(Hmin, Math.Min(Hmax, _habs));

            var count = events == null ? 0 : events.Count;
            var last = new double[count];
            var now = new double[count];
            for (var i = 0; i < count; i++) last[i] = events[i].F(_y, _t);

            while ((_direction > 0 && _t < tf) || (_direction < 0 && _t > tf))
            {
                if (steps >= MaxSteps)
                    return End(OdeStatus.MaxStepsExceeded, MaxSteps + " steps without reaching t = " + R(tf) + " (at " + R(_t) + ")", null, steps);

                var tnext = _t + _habs * _direction;
                if (_direction * (tnext - tf) > 0)
                {
                    _snapping = true;
                    maxStep = _habs = Math.Abs(tf - _t);
                }
                else
                {
                    _snapping = false;
                    maxStep = Hmax;
                    _habs = Math.Abs(tnext - _t); // deliberate for bit-stability
                }

                double next;
                var failure = Step(f, maxStep, out next);
                if (failure != OdeStatus.Finished)
                    return End(failure, failure == OdeStatus.NonFinite
                        ? "the function is not finite near t = " + R(_t)
                        : "the step needed at t = " + R(_t) + " is below the smallest allowed", null, steps);
                steps++;
                _tnew = _snapping && maxStep == _habs ? tf : _t + _habs * _direction;

                // Events: MechJeb's assumption, one crossing per event per step; the earliest one ends the solve.
                OdeEvent fired = null;
                var firedAt = 0.0;
                for (var i = 0; i < count; i++)
                {
                    now[i] = events[i].F(_ynew, _tnew);
                    if (!events[i].Crossed(last[i], now[i])) continue;
                    var at = EventTime(events[i], last[i]);
                    if (fired == null || _direction * (at - firedAt) < 0)
                    {
                        fired = events[i];
                        firedAt = at;
                    }
                }
                if (fired != null)
                {
                    var y = new double[_n];
                    Interpolate(firedAt, y);
                    _t = firedAt;
                    _y = y;
                    return End(OdeStatus.EventTerminated, null, fired, steps);
                }
                for (var i = 0; i < count; i++) last[i] = now[i];

                Array.Copy(_ynew, _y, _n);
                Array.Copy(_dynew, _dy, _n);
                _t = _tnew;
                _habs = next;
            }
            return End(OdeStatus.Finished, null, null, steps);
        }

        private string Problem(OdeFunction f, double[] y0, double t0, double tf, IReadOnlyList<OdeEvent> events)
        {
            if (f == null) return "the function is missing";
            if (events != null)
                foreach (var e in events)
                    if (e == null || e.F == null || e.Direction < -1 || e.Direction > 1) return "each event needs a function and a direction of -1, 0 or 1";
            if (y0 == null || y0.Length == 0) return "the start state must have at least one component";
            foreach (var y in y0)
                if (!SearchInput.Finite(y)) return "the start state must be finite";
            if (!SearchInput.Finite(t0) || !SearchInput.Finite(tf)) return "the start and end times must be finite";
            if (!SearchInput.Finite(Rtol) || !SearchInput.Finite(Atol) || Rtol <= 0 || Atol <= 0) return "the tolerances must be positive numbers";
            if (double.IsNaN(Hmin) || double.IsNaN(Hmax) || Hmin < 0 || Hmax < Hmin || Hmax <= 0) return "the step limits must be 0 <= Hmin <= Hmax";
            if (!SearchInput.Finite(Hstart) || Hstart < 0) return "the first step must be 0 (automatic) or a positive number";
            if (MaxSteps < 1) return "the step cap must be at least 1";
            return null;
        }

        private OdeResult End(OdeStatus status, string reason, OdeEvent fired, int steps)
        {
            return new OdeResult
            {
                Status = status, Reason = reason == null ? null : Name + ": " + reason, T = _t, Y = (double[])_y.Clone(), Event = fired,
                Steps = steps, Evaluations = _evaluations,
            };
        }

        private bool Call(OdeFunction f, double[] y, double t, double[] dy)
        {
            _evaluations++;
            f(y, t, dy);
            for (var i = 0; i < _n; i++)
                if (!SearchInput.Finite(dy[i])) return false;
            return true;
        }

        /// <summary>
        /// AbstractRungeKutta.Step: tries steps from <see cref="_habs"/> until one is within the tolerance, shrinking on each
        /// rejection; leaves the accepted step in <see cref="_habs"/>, its end in <see cref="_ynew"/> and <see cref="_dynew"/>, and
        /// the next step's size in <paramref name="next"/>. Finished means it took one.
        /// </summary>
        private OdeStatus Step(OdeFunction f, double maxStep, out double next)
        {
            next = 0;
            var previouslyRejected = false;
            while (true)
            {
                var minStep = 10 * Math.Abs(NextAfter(_t, _direction) - _t);
                minStep = Math.Max(minStep, Hmin);
                if (_habs > maxStep) _habs = maxStep;
                // MechJeb's ThrowOnMinStep (its default): a step forced below the smallest is a failure, except the last one to the end time.
                if (_habs < minStep && !_snapping) return OdeStatus.StepTooSmall;

                if (!RkStep(f)) return OdeStatus.NonFinite;

                if (Hmin == Hmax)
                {
                    next = _habs;
                    return OdeStatus.Finished;
                }

                var errorNorm = ScaledErrorNorm();
                if (double.IsNaN(errorNorm)) return OdeStatus.NonFinite;
                if (errorNorm < 1)
                {
                    var factor = errorNorm == 0
                        ? MaxFactor
                        : Math.Min(MaxFactor, Safety * Math.Pow(errorNorm, -Alpha) * Math.Pow(_lastErrorNorm, Beta));
                    if (previouslyRejected) factor = Math.Min(1.0, factor);
                    _lastErrorNorm = Math.Max(errorNorm, 1e-4);
                    next = _habs * factor;
                    return OdeStatus.Finished;
                }
                _habs *= Math.Max(MinFactor, Safety * Math.Pow(errorNorm, -Alpha));
                previouslyRejected = true;
            }
        }

        private bool RkStep(OdeFunction f)
        {
            var h = _habs * _direction;
            for (var i = 0; i < _n; i++) _ynew[i] = _y[i] + h * A21 * _dy[i];
            if (!Call(f, _ynew, _t + C2 * h, _k2)) return false;
            for (var i = 0; i < _n; i++) _ynew[i] = _y[i] + h * A32 * _k2[i];
            if (!Call(f, _ynew, _t + C3 * h, _k3)) return false;
            for (var i = 0; i < _n; i++) _ynew[i] = _y[i] + h * (A41 * _dy[i] + A42 * _k2[i] + A43 * _k3[i]);
            if (!Call(f, _ynew, _t + h, _k4)) return false;
            Array.Copy(_k4, _dynew, _n);
            return true;
        }

        private double ScaledErrorNorm()
        {
            // MechJeb's error vector is a difference of derivatives, the step left out; scipy's, which its step control follows,
            // is that times the step, the error in the state the tolerances are on. This takes scipy's.
            var h = _habs;
            var error = 0.0;
            for (var i = 0; i < _n; i++)
            {
                var err = h * (E1 * _dy[i] + E2 * _k2[i] + E3 * _k3[i] + E4 * _k4[i]);
                var scale = Atol + Rtol * Math.Max(Math.Abs(_y[i]), Math.Abs(_ynew[i]));
                error += err / scale * (err / scale);
            }
            return Math.Sqrt(error / _n);
        }

        /// <summary>AbstractRungeKutta.SelectInitialStep (Hairer, Norsett and Wanner II.4, as scipy): NaN if the function goes non-finite.</summary>
        private double InitialStep(OdeFunction f)
        {
            if (Hmax == Hmin) return Hmin;
            double ny = 0, nf = 0, ns = 0;
            var scale = new double[_n];
            for (var i = 0; i < _n; i++)
            {
                scale[i] = Math.Abs(_y[i]) * Rtol + Atol;
                ny += _y[i] * _y[i];
                nf += _dy[i] * _dy[i];
                ns += scale[i] * scale[i];
            }
            // MechJeb divides norms (|y| / |scale|), not the scipy elementwise ratio; kept.
            var d0 = Math.Sqrt(ny) / Math.Sqrt(ns);
            var d1 = Math.Sqrt(nf) / Math.Sqrt(ns);
            var h0 = d0 < 1e-5 || d1 < 1e-5 ? 1e-6 : 0.01 * d0 / d1;
            var y1 = new double[_n];
            var f1 = new double[_n];
            for (var i = 0; i < _n; i++) y1[i] = _y[i] + h0 * _direction * _dy[i];
            if (!Call(f, y1, _t + h0 * _direction, f1)) return double.NaN;
            var d2 = 0.0;
            for (var i = 0; i < _n; i++)
            {
                var q = (f1[i] - _dy[i]) / scale[i];
                d2 += q * q;
            }
            d2 = Math.Sqrt(d2) / h0;
            var h1 = d1 <= 1e-15 && d2 <= 1e-15
                ? Math.Max(1e-6, h0 * 1e-3)
                : Math.Pow(0.01 / Math.Max(d1, d2), 1.0 / (Order + 1));
            return Math.Min(100 * h0, h1);
        }

        /// <summary>
        /// The event's time in the step just taken: bisection on the cubic Hermite interpolant, to the last bit, as MechJeb's
        /// Bisection.Solve to EPS. It gives the end past the crossing, so the event's condition holds at the state there.
        /// </summary>
        private double EventTime(OdeEvent e, double atStart)
        {
            var a = _t;
            var b = _tnew;
            var fa = atStart;
            var y = new double[_n];
            for (var i = 0; i < 200 && Math.Abs(b - a) > 4 * Eps * Math.Max(Math.Abs(a), Math.Abs(b)); i++)
            {
                var m = a + (b - a) / 2;
                if (m == a || m == b) break;
                Interpolate(m, y);
                var fm = e.F(y, m);
                if (fa != 0 && fm != 0 && (fm > 0) == (fa > 0))
                {
                    a = m;
                    fa = fm;
                }
                else
                    b = m;
            }
            return b;
        }

        /// <summary>The cubic Hermite interpolant between the step's ends, at <paramref name="x"/>.</summary>
        private void Interpolate(double x, double[] yout)
        {
            var h = _tnew - _t;
            var s = (x - _t) / h;
            var s2 = s * s;
            var s3 = s2 * s;
            var h00 = 2 * s3 - 3 * s2 + 1;
            var h10 = s3 - 2 * s2 + s;
            var h01 = -2 * s3 + 3 * s2;
            var h11 = s3 - s2;
            for (var i = 0; i < _n; i++) yout[i] = h00 * _y[i] + h10 * h * _dy[i] + h01 * _ynew[i] + h11 * h * _dynew[i];
        }

        private static double NextAfter(double x, int direction)
        {
            if (x == 0) return direction > 0 ? double.Epsilon : -double.Epsilon;
            var bits = BitConverter.DoubleToInt64Bits(x);
            bits += (x > 0) == (direction > 0) ? 1 : -1;
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static string R(double value)
        {
            return value.ToString("G6", CultureInfo.InvariantCulture);
        }
    }
}
