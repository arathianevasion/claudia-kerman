// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>What a variable of an <see cref="SqpProblem"/> measures, which sets its finite-difference step through the problem's <see cref="Scale"/>.</summary>
    internal enum SqpVariable
    {
        /// <summary>A burn component, m/s.</summary>
        Velocity,
        /// <summary>A burn time, s.</summary>
        Time,
    }

    /// <summary>Why an SQP solve ended.</summary>
    internal enum SqpStatus
    {
        /// <summary>Every constraint is within its tolerance and the step has shrunk to nothing: <see cref="SqpResult.X"/> is the answer.</summary>
        Converged,
        /// <summary>It ran out of iterations; <see cref="SqpResult.X"/> is the best point so far.</summary>
        IterationCap,
        /// <summary>No step could be taken (no defined trial point lowered the merit, or the derivatives or the step system failed); <see cref="SqpResult.X"/> is the best point so far.</summary>
        Stalled,
        /// <summary>The problem was not usable, or its start is undefined; nothing was solved.</summary>
        Invalid,
    }

    /// <summary>
    /// A small equality-constrained minimisation for <see cref="Sqp"/>: minimise <see cref="Objective"/> over a few variables,
    /// subject to <see cref="Constraints"/> equality constraints (each zero within its tolerance) and box bounds on the variables.
    /// Variables are in SI units (m/s, s); the solver scales them by <see cref="Scale"/> itself.
    /// </summary>
    internal sealed class SqpProblem
    {
        /// <summary>The scale of the body the burn is round; it sets the finite-difference steps and conditions the solve.</summary>
        public Scale Scale;
        /// <summary>What each variable measures; its length is the number of variables.</summary>
        public SqpVariable[] Variables;
        /// <summary>The starting point. It must be defined (<see cref="Evaluate"/> true) and within the bounds.</summary>
        public double[] Start;
        /// <summary>The lower bounds, or null for none; an entry may be infinite.</summary>
        public double[] Lower;
        /// <summary>The upper bounds, or null for none; an entry may be infinite, and one equal to its lower bound fixes the variable.</summary>
        public double[] Upper;
        /// <summary>The objective; it must be finite wherever <see cref="Evaluate"/> is defined.</summary>
        public Func<double[], double> Objective;
        /// <summary>
        /// The objective's Hessian in SI units when the objective is quadratic (|Δv|² has 2I over the burn components and 0 for a
        /// burn time), or null for none; symmetric and positive semi-definite. The solve's Hessian starts from it.
        /// </summary>
        public double[,] ObjectiveHessian;
        /// <summary>How many equality constraints there are.</summary>
        public int Constraints;
        /// <summary>Fills the constraint residuals at a point, in the caller's units; false (or a non-finite residual) means the point is undefined (no encounter, no escape).</summary>
        public Func<double[], double[], bool> Evaluate;
        /// <summary>Each constraint is met within its tolerance, in the residual's units.</summary>
        public double[] Tolerances;
        /// <summary>The cap on SQP iterations (each a derivative evaluation and a line search), at least 1.</summary>
        public int MaxIterations;
    }

    /// <summary>What an SQP solve found. A non-converged solve still carries its best point, so the caller decides what is close enough.</summary>
    internal sealed class SqpResult
    {
        public SqpStatus Status { get; internal set; }
        /// <summary>Why it did not converge, in words; null when it did.</summary>
        public string Reason { get; internal set; }
        /// <summary>The answer, or the best point reached (null for <see cref="SqpStatus.Invalid"/>).</summary>
        public double[] X { get; internal set; }
        /// <summary>The objective at <see cref="X"/>.</summary>
        public double Objective { get; internal set; }
        /// <summary>The constraint residuals at <see cref="X"/>, in the caller's units.</summary>
        public double[] Residuals { get; internal set; }
        /// <summary>How many times <see cref="SqpProblem.Evaluate"/> was called, finite-difference points included.</summary>
        public int Evaluations { get; internal set; }
        public int Iterations { get; internal set; }

        public bool Converged
        {
            get { return Status == SqpStatus.Converged; }
        }
    }

    /// <summary>
    /// Sequential quadratic programming for the small equality-constrained problems of Course Corrections and moon returns
    /// (ADR 0010): 3 or 4 variables, 1 to 3 constraints, solved by single shooting through <c>Trajectory</c>. Each iteration takes
    /// central finite differences at fixed steps in <see cref="Scale"/> units, solves a dense KKT system for the step (an active set
    /// holds the box bounds), and backtracks on an L1 merit function. The Hessian is the objective's exact part plus damped BFGS
    /// updates for the constraints' curvature. A trial point that is undefined halves the step; a start that is undefined is
    /// <see cref="SqpStatus.Invalid"/>. It throws nothing of its own: a solve that does not converge returns its best point with a
    /// status. An exception from the caller's objective or constraints is a bug in them and is not caught.
    /// </summary>
    internal static class Sqp
    {
        /// <summary>
        /// The finite-difference steps, scaled: about 1e-3 m/s and 1e-2 s at Kerbin's surface scale. Smaller velocity steps fall
        /// into the periapsis staircase (2 to 3 cm) the bisected SOI entry time leaves.
        /// </summary>
        private const double VelocityStep = 4e-7;
        private const double TimeStep = 4e-5;
        /// <summary>A feasible point whose step is under this (scaled; about 2 mm/s at Kerbin) has converged.</summary>
        private const double StepTolerance = 1e-6;
        private const int MaxVariables = 8;
        private const int Backtracks = 40;
        /// <summary>Armijo's sufficient decrease, and the penalty's margin over the multipliers.</summary>
        private const double Armijo = 1e-4;
        private const double PenaltyMargin = 1.1;
        /// <summary>The KKT system's regularisation (its constraint rows equilibrated, the objective Hessian scaled to 1) when it is singular.</summary>
        private const double Regularisation = 1e-8;
        /// <summary>The first Hessian's diagonal for a variable the objective has no curvature in, against an objective Hessian scaled to 1.</summary>
        private const double CurvatureStart = 1e-3;
        /// <summary>Inside this of 0° or 180°, an inclination target is taken as equatorial and constrained by the orbit normal's equatorial components.</summary>
        public const double EquatorialBand = 1.0;

        /// <summary>The number of constraints <see cref="Inclination"/> fills for a target inclination: 2 within <see cref="EquatorialBand"/> of the equator, else 1.</summary>
        public static int InclinationConstraints(double targetIncDeg)
        {
            return targetIncDeg < EquatorialBand || targetIncDeg > 180 - EquatorialBand ? 2 : 1;
        }

        /// <summary>
        /// The inclination residuals of the orbit with angular momentum <paramref name="h"/> into <paramref name="into"/> from
        /// <paramref name="at"/>: h_z/|h| − cos(target), or within <see cref="EquatorialBand"/> of the equator, where that has
        /// zero slope at the answer, the unit normal's x and y, which must both be zero. A zero <paramref name="h"/> (a radial
        /// trajectory) gives NaN residuals, which the solve takes as an undefined point.
        /// </summary>
        public static void Inclination(Vec3 h, double targetIncDeg, double[] into, int at)
        {
            if (into == null || at < 0 || at + InclinationConstraints(targetIncDeg) > into.Length)
                throw new ArgumentException("the residuals array has no room at " + at + " for the inclination's constraints", "into");
            if (double.IsNaN(targetIncDeg) || targetIncDeg < 0 || targetIncDeg > 180)
                throw new ArgumentException("the target inclination must be from 0 to 180 degrees", "targetIncDeg");
            var n = h / h.Length;
            if (InclinationConstraints(targetIncDeg) == 2)
            {
                into[at] = n.X;
                into[at + 1] = n.Y;
            }
            else into[at] = n.Z - Math.Cos(targetIncDeg * Math.PI / 180);
        }

        public static SqpResult Solve(SqpProblem problem)
        {
            var why = Problem(problem);
            if (why != null) return Invalid(why);
            return new Run(problem).Solve();
        }

        private static SqpResult Invalid(string reason)
        {
            return new SqpResult { Status = SqpStatus.Invalid, Reason = "the SQP solve: " + reason };
        }

        /// <summary>The first thing wrong with the problem, or null.</summary>
        private static string Problem(SqpProblem p)
        {
            if (p == null) return "no problem given";
            if (!SearchInput.Finite(p.Scale.LengthScale) || p.Scale.LengthScale <= 0 || !SearchInput.Finite(p.Scale.VelocityScale) || p.Scale.VelocityScale <= 0)
                return "the scale is not set";
            if (p.Variables == null || p.Variables.Length < 1 || p.Variables.Length > MaxVariables) return "it takes 1 to " + MaxVariables + " variables";
            var n = p.Variables.Length;
            if (p.Constraints < 1 || p.Constraints > n) return "it takes 1 to " + n + " constraints for " + n + " variables";
            if (p.Objective == null || p.Evaluate == null) return "the objective and the constraints must both be given";
            if (p.Start == null || p.Start.Length != n) return "the start must have one value per variable";
            if ((p.Lower != null && p.Lower.Length != n) || (p.Upper != null && p.Upper.Length != n)) return "each bound must have one value per variable";
            if (p.Tolerances == null || p.Tolerances.Length != p.Constraints) return "there must be one tolerance per constraint";
            foreach (var t in p.Tolerances)
                if (!SearchInput.Finite(t) || t <= 0) return "each tolerance must be a positive number";
            if (p.MaxIterations < 1) return "the iteration cap must be at least 1";
            if (p.ObjectiveHessian != null && !SemiDefinite(p.ObjectiveHessian, n))
                return "the objective's Hessian must be " + n + " by " + n + ", finite, symmetric and positive semi-definite";
            for (var i = 0; i < n; i++)
            {
                var lo = p.Lower != null ? p.Lower[i] : double.NegativeInfinity;
                var hi = p.Upper != null ? p.Upper[i] : double.PositiveInfinity;
                if (double.IsNaN(lo) || double.IsNaN(hi) || !(lo <= hi)) return "variable " + i + "'s lower bound must not be above its upper bound";
                if (!SearchInput.Finite(p.Start[i])) return "the start must be finite";
                if (p.Start[i] < lo || p.Start[i] > hi) return "the start is outside variable " + i + "'s bounds";
            }
            return null;
        }

        /// <summary>Whether <paramref name="h"/> is n by n, finite, symmetric and positive semi-definite (a Cholesky factorisation with a relative floor on the pivots).</summary>
        private static bool SemiDefinite(double[,] h, int n)
        {
            if (h.GetLength(0) != n || h.GetLength(1) != n) return false;
            var largest = 0.0;
            foreach (var v in h)
            {
                if (!SearchInput.Finite(v)) return false;
                largest = Math.Max(largest, Math.Abs(v));
            }
            var floor = 1e-12 * largest;
            var l = new double[n, n];
            for (var i = 0; i < n; i++)
            {
                for (var j = 0; j < n; j++)
                    if (Math.Abs(h[i, j] - h[j, i]) > floor) return false;
                for (var j = 0; j <= i; j++)
                {
                    var sum = h[i, j] + (i == j ? floor : 0);
                    for (var k = 0; k < j; k++) sum -= l[i, k] * l[j, k];
                    if (i == j)
                    {
                        if (sum < 0) return false;
                        l[i, i] = Math.Sqrt(sum);
                    }
                    else l[i, j] = l[j, j] > 0 ? sum / l[j, j] : 0;
                }
            }
            return true;
        }

        /// <summary>One solve's state, in scaled variables z (x over each variable's unit) and constraints over their tolerances.</summary>
        private sealed class Run
        {
            private readonly SqpProblem _p;
            private readonly int _n, _m;
            private readonly double[] _unit, _step, _lo, _hi;
            private double _fScale = 1;
            private int _evaluations;

            // The current point and what is known at it.
            private double[] _z;
            private double _f;
            private double[] _c;
            private double[] _g;
            private double[,] _a;

            // The best point so far: feasible beats infeasible, then the lower objective or the smaller violation.
            private double[] _bestZ;
            private double _bestF, _bestViolation;
            private double[] _bestC;

            public Run(SqpProblem p)
            {
                _p = p;
                _n = p.Variables.Length;
                _m = p.Constraints;
                _unit = new double[_n];
                _step = new double[_n];
                _lo = new double[_n];
                _hi = new double[_n];
                for (var i = 0; i < _n; i++)
                {
                    var time = p.Variables[i] == SqpVariable.Time;
                    _unit[i] = time ? p.Scale.TimeScale : p.Scale.VelocityScale;
                    _step[i] = time ? TimeStep : VelocityStep;
                    _lo[i] = p.Lower != null ? p.Lower[i] / _unit[i] : double.NegativeInfinity;
                    _hi[i] = p.Upper != null ? p.Upper[i] / _unit[i] : double.PositiveInfinity;
                }
            }

            public SqpResult Solve()
            {
                _z = new double[_n];
                for (var i = 0; i < _n; i++) _z[i] = Clamp(i, _p.Start[i] / _unit[i]);
                _c = new double[_m];
                if (!Eval(_z, out _f, _c)) return Result(SqpStatus.Invalid, "the start is undefined (the trajectory there does not reach what the constraints measure)", 0);
                Keep(_z, _f, _c);

                if (!Derivatives(_z, _f, _c, out _g, out _a))
                    return Result(SqpStatus.Stalled, "the derivatives at the start are undefined (every point a step away is)", 0);
                var w = FirstHessian();
                for (var i = 0; i < _n; i++) _g[i] /= _fScale;
                _f /= _fScale;

                var mu = 0.0;
                for (var iter = 0; iter < _p.MaxIterations; iter++)
                {
                    double[] p, lambda;
                    if (!Step(w, out p, out lambda))
                        return Result(SqpStatus.Stalled, "the step's KKT system is singular (the constraints' derivatives are not independent)", iter);
                    if (Feasible(_c) && NormInf(p) <= StepTolerance) return Result(SqpStatus.Converged, null, iter);

                    // The penalty by Powell's rule: above the multipliers, which makes the step a descent direction for the merit
                    // (Nocedal and Wright, theorem 18.2), and let fall back halfway when they shrink, so one large multiplier
                    // far from the answer doesn't leave a penalty that rejects every step along a curved constraint.
                    var gp = Dot(_g, p);
                    var linear = new double[_m];
                    for (var k = 0; k < _m; k++)
                    {
                        linear[k] = _c[k];
                        for (var j = 0; j < _n; j++) linear[k] += _a[k, j] * p[j];
                    }
                    var drop = Norm1(_c) - Norm1(linear);
                    var needed = PenaltyMargin * NormInf(lambda);
                    mu = Math.Max(needed, 0.5 * (mu + needed));
                    var merit = _f + mu * Norm1(_c);
                    var slope = gp - mu * drop;

                    double[] zNew, cNew;
                    double fNew;
                    if (!Search(p, merit, slope, mu, out zNew, out fNew, out cNew))
                        return Result(SqpStatus.Stalled, "no defined point along the step lowers the merit", iter + 1);

                    // The new multipliers' Lagrangian gradient at both ends gives the curvature the BFGS update learns. Where the
                    // constraints add none along the step, y is the exact Hessian's W s and the update leaves W as it is; where they
                    // do, it blends their curvature in, so W is the exact part only until then.
                    double[] gNew;
                    double[,] aNew;
                    if (!Derivatives(zNew, fNew * _fScale, cNew, out gNew, out aNew))
                        return Result(SqpStatus.Stalled, "the derivatives are undefined at the point reached", iter + 1);
                    for (var i = 0; i < _n; i++) gNew[i] /= _fScale;
                    var s = new double[_n];
                    var y = new double[_n];
                    for (var i = 0; i < _n; i++)
                    {
                        s[i] = zNew[i] - _z[i];
                        y[i] = gNew[i] - _g[i];
                        for (var k = 0; k < _m; k++) y[i] += (aNew[k, i] - _a[k, i]) * lambda[k];
                    }
                    // With no exact part, the identity it starts from is rescaled to the curvature the first step saw (Nocedal and Wright 6.20).
                    if (iter == 0 && _p.ObjectiveHessian == null)
                    {
                        var sy = Dot(s, y);
                        if (sy > 0)
                            for (var i = 0; i < _n; i++) w[i, i] = Dot(y, y) / sy;
                    }
                    DampedBfgs(w, s, y);
                    _z = zNew;
                    _f = fNew;
                    _c = cNew;
                    _g = gNew;
                    _a = aNew;
                }
                return Result(SqpStatus.IterationCap, _p.MaxIterations + " iterations without converging", _p.MaxIterations);
            }

            /// <summary>
            /// Backtracks from the full step on the L1 merit, halving on an undefined point, with one second-order correction to the
            /// full step (back onto the constraints) when it is defined but rejected, so curved constraints don't stall it (Maratos).
            /// </summary>
            private bool Search(double[] p, double merit, double slope, double mu, out double[] zNew, out double fNew, out double[] cNew)
            {
                cNew = new double[_m];
                var alpha = 1.0;
                for (var k = 0; k < Backtracks; k++)
                {
                    zNew = Along(_z, p, alpha);
                    var defined = Eval(zNew, out fNew, cNew);
                    if (defined)
                    {
                        Keep(zNew, fNew, cNew);
                        fNew /= _fScale;
                        if (Accept(fNew + mu * Norm1(cNew), merit, alpha, slope)) return true;
                        if (k == 0)
                        {
                            var corrected = Correction(zNew, cNew);
                            if (corrected != null)
                            {
                                var cSoc = new double[_m];
                                double fSoc;
                                if (Eval(corrected, out fSoc, cSoc))
                                {
                                    Keep(corrected, fSoc, cSoc);
                                    fSoc /= _fScale;
                                    if (Accept(fSoc + mu * Norm1(cSoc), merit, 1, slope))
                                    {
                                        zNew = corrected;
                                        fNew = fSoc;
                                        cNew = cSoc;
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                    alpha *= 0.5;
                }
                zNew = null;
                fNew = double.NaN;
                return false;
            }

            private static bool Accept(double trial, double merit, double alpha, double slope)
            {
                return slope < 0 ? trial <= merit + Armijo * alpha * slope : trial < merit;
            }

            /// <summary>The second-order correction from a rejected full step: the least-norm move, over the free variables, that zeroes the constraints' linearisation there.</summary>
            private double[] Correction(double[] zTrial, double[] cTrial)
            {
                var free = new bool[_n];
                for (var i = 0; i < _n; i++) free[i] = zTrial[i] > _lo[i] && zTrial[i] < _hi[i];
                var aat = new double[_m, _m];
                for (var r = 0; r < _m; r++)
                    for (var q = 0; q < _m; q++)
                        for (var j = 0; j < _n; j++)
                            if (free[j]) aat[r, q] += _a[r, j] * _a[q, j];
                var rhs = new double[_m];
                for (var r = 0; r < _m; r++) rhs[r] = -cTrial[r];
                var y = Linear.Solve(aat, rhs);
                if (y == null) return null;
                var z = new double[_n];
                for (var j = 0; j < _n; j++)
                {
                    var d = 0.0;
                    if (free[j])
                        for (var r = 0; r < _m; r++) d += _a[r, j] * y[r];
                    z[j] = Clamp(j, zTrial[j] + d);
                }
                return z;
            }

            /// <summary>
            /// The QP step: minimise g·p + ½ pᵀWp subject to c + A p = 0 and the bounds. An active set fixes each bound a step
            /// would cross and frees one whose multiplier says it holds the step back, until neither happens.
            /// </summary>
            private bool Step(double[,] w, out double[] p, out double[] lambda)
            {
                // -1 held at the lower bound, +1 at the upper, 0 free.
                var held = new int[_n];
                for (var i = 0; i < _n; i++) held[i] = _z[i] <= _lo[i] ? -1 : _z[i] >= _hi[i] ? 1 : 0;
                p = null;
                lambda = null;
                for (var round = 0; round < 4 * _n + 4; round++)
                {
                    if (!Kkt(w, held, out p, out lambda)) return false;
                    var crossed = false;
                    for (var i = 0; i < _n; i++)
                    {
                        if (held[i] != 0) continue;
                        if (_z[i] + p[i] < _lo[i]) { held[i] = -1; crossed = true; }
                        else if (_z[i] + p[i] > _hi[i]) { held[i] = 1; crossed = true; }
                    }
                    if (crossed) continue;
                    var release = -1;
                    var worst = 0.0;
                    for (var i = 0; i < _n; i++)
                    {
                        if (held[i] == 0) continue;
                        // The QP Lagrangian's slope in p_i: negative at a lower bound (or positive at an upper) wants the bound let go.
                        var r = _g[i];
                        for (var j = 0; j < _n; j++) r += w[i, j] * p[j];
                        for (var k = 0; k < _m; k++) r += _a[k, i] * lambda[k];
                        var pull = held[i] < 0 ? -r : r;
                        if (pull > worst) { worst = pull; release = i; }
                    }
                    if (release < 0) return true;
                    held[release] = 0;
                }
                // The set cycled: take the last step, held inside the bounds.
                for (var i = 0; i < _n; i++) p[i] = Clamp(i, _z[i] + p[i]) - _z[i];
                return true;
            }

            /// <summary>The KKT system over the free variables, with the held ones at their bounds; each constraint row is equilibrated, and a singular system is retried regularised.</summary>
            private bool Kkt(double[,] w, int[] held, out double[] p, out double[] lambda)
            {
                p = new double[_n];
                lambda = new double[_m];
                var free = new int[_n];
                var nf = 0;
                for (var i = 0; i < _n; i++)
                {
                    if (held[i] < 0) p[i] = _lo[i] - _z[i];
                    else if (held[i] > 0) p[i] = _hi[i] - _z[i];
                    else free[nf++] = i;
                }
                // Every variable held: the bounds alone set the step.
                if (nf == 0) return true;
                var rowScale = new double[_m];
                for (var k = 0; k < _m; k++)
                {
                    var norm = 0.0;
                    for (var j = 0; j < nf; j++) norm = Math.Max(norm, Math.Abs(_a[k, free[j]]));
                    rowScale[k] = norm > 0 ? 1 / norm : 1;
                }
                var size = nf + _m;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var kkt = new double[size, size];
                    var rhs = new double[size];
                    for (var r = 0; r < nf; r++)
                    {
                        var i = free[r];
                        rhs[r] = -_g[i];
                        for (var j = 0; j < _n; j++)
                            if (held[j] != 0) rhs[r] -= w[i, j] * p[j];
                        for (var q = 0; q < nf; q++) kkt[r, q] = w[i, free[q]];
                        for (var k = 0; k < _m; k++) kkt[r, nf + k] = _a[k, i] * rowScale[k];
                    }
                    for (var k = 0; k < _m; k++)
                    {
                        rhs[nf + k] = -_c[k];
                        for (var j = 0; j < _n; j++)
                            if (held[j] != 0) rhs[nf + k] -= _a[k, j] * p[j];
                        rhs[nf + k] *= rowScale[k];
                        for (var q = 0; q < nf; q++) kkt[nf + k, q] = _a[k, free[q]] * rowScale[k];
                        if (attempt == 1) kkt[nf + k, nf + k] = -Regularisation;
                    }
                    var x = Linear.Solve(kkt, rhs);
                    if (x == null) continue;
                    for (var r = 0; r < nf; r++) p[free[r]] = x[r];
                    // Regularised, ε λ_k is what the step leaves of row k's linearisation. Where the step meets it (a redundant
                    // row, as one of the equatorial pair is at a node) λ_k stands; where it can't (a row with no slope but a
                    // residual) λ_k is the residual over ε, no estimate at all, which would inflate the penalty: it is taken as 0.
                    for (var k = 0; k < _m; k++)
                    {
                        var left = Regularisation * Math.Abs(x[nf + k]);
                        lambda[k] = attempt == 0 || left <= 1e-6 * (1 + Math.Abs(rhs[nf + k])) ? x[nf + k] * rowScale[k] : 0;
                    }
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Central differences of the objective and constraints at fixed scaled steps, one-sided where a step would cross a bound
            /// or land on an undefined point (a fixed variable, lower bound equal to upper, has none). False when every side
            /// tried for some variable is undefined.
            /// </summary>
            private bool Derivatives(double[] z, double f, double[] c, out double[] g, out double[,] a)
            {
                g = new double[_n];
                a = new double[_m, _n];
                var cp = new double[_m];
                var cm = new double[_m];
                for (var j = 0; j < _n; j++)
                {
                    var h = _step[j];
                    var up = z[j] + h <= _hi[j];
                    var down = z[j] - h >= _lo[j];
                    if (!up && !down)
                    {
                        // A window narrower than a step each side: one side, as far as the wider room goes; none for a fixed variable.
                        var room = Math.Max(_hi[j] - z[j], z[j] - _lo[j]);
                        if (!(room > 0)) continue;
                        up = _hi[j] - z[j] >= z[j] - _lo[j];
                        down = !up;
                        h = room;
                    }
                    double fp = 0, fm = 0;
                    if (up) up = Eval(Moved(z, j, h), out fp, cp);
                    if (down) down = Eval(Moved(z, j, -h), out fm, cm);
                    if (up && down)
                    {
                        g[j] = (fp - fm) / (2 * h);
                        for (var k = 0; k < _m; k++) a[k, j] = (cp[k] - cm[k]) / (2 * h);
                    }
                    else if (up)
                    {
                        g[j] = (fp - f) / h;
                        for (var k = 0; k < _m; k++) a[k, j] = (cp[k] - c[k]) / h;
                    }
                    else if (down)
                    {
                        g[j] = (f - fm) / h;
                        for (var k = 0; k < _m; k++) a[k, j] = (c[k] - cm[k]) / h;
                    }
                    else return false;
                }
                return true;
            }

            /// <summary>
            /// The first Hessian, scaled: the objective's exact one, with <see cref="CurvatureStart"/> on each variable it has no
            /// curvature in (a burn time), or the identity when there is none. It also sets the objective's scale so that the
            /// exact Hessian's largest diagonal is 1 (else the start's largest gradient entry).
            /// </summary>
            private double[,] FirstHessian()
            {
                var hx = _p.ObjectiveHessian;
                var largest = 0.0;
                if (hx != null)
                    for (var i = 0; i < _n; i++) largest = Math.Max(largest, hx[i, i] * _unit[i] * _unit[i]);
                if (largest <= 0) largest = NormInf(_g);
                _fScale = largest > 0 && SearchInput.Finite(largest) ? largest : 1;
                var w = new double[_n, _n];
                for (var i = 0; i < _n; i++)
                {
                    if (hx == null)
                    {
                        w[i, i] = 1;
                        continue;
                    }
                    for (var j = 0; j < _n; j++) w[i, j] = hx[i, j] * _unit[i] * _unit[j] / _fScale;
                    if (w[i, i] <= 0) w[i, i] = CurvatureStart;
                }
                return w;
            }

            /// <summary>Powell's damped BFGS update (Nocedal and Wright, procedure 18.2), which keeps <paramref name="b"/> positive definite.</summary>
            private void DampedBfgs(double[,] b, double[] s, double[] y)
            {
                var bs = new double[_n];
                for (var i = 0; i < _n; i++)
                    for (var j = 0; j < _n; j++) bs[i] += b[i, j] * s[j];
                var sbs = Dot(s, bs);
                if (!(sbs > 1e-300)) return;
                var sy = Dot(s, y);
                var theta = sy >= 0.2 * sbs ? 1 : 0.8 * sbs / (sbs - sy);
                var r = new double[_n];
                for (var i = 0; i < _n; i++) r[i] = theta * y[i] + (1 - theta) * bs[i];
                var sr = Dot(s, r);
                if (!(sr > 0) || !SearchInput.Finite(sr)) return;
                for (var i = 0; i < _n; i++)
                    for (var j = 0; j < _n; j++) b[i, j] += -bs[i] * bs[j] / sbs + r[i] * r[j] / sr;
            }

            /// <summary>Evaluates at a scaled point: the objective (unscaled) and the constraints over their tolerances. False when undefined.</summary>
            private bool Eval(double[] z, out double f, double[] c)
            {
                var x = X(z);
                _evaluations++;
                var raw = new double[_m];
                f = double.NaN;
                if (!_p.Evaluate(x, raw)) return false;
                for (var k = 0; k < _m; k++)
                {
                    if (!SearchInput.Finite(raw[k])) return false;
                    c[k] = raw[k] / _p.Tolerances[k];
                }
                f = _p.Objective(x);
                return SearchInput.Finite(f);
            }

            /// <summary>Remembers a defined point if it beats the best so far (<paramref name="f"/> unscaled, as it is kept).</summary>
            private void Keep(double[] z, double f, double[] c)
            {
                var violation = 0.0;
                foreach (var ck in c) violation = Math.Max(violation, Math.Abs(ck));
                var feasible = violation <= 1;
                if (_bestZ != null)
                {
                    var bestFeasible = _bestViolation <= 1;
                    if (bestFeasible && !feasible) return;
                    if (bestFeasible == feasible && (feasible ? f >= _bestF : violation >= _bestViolation)) return;
                }
                _bestZ = (double[])z.Clone();
                _bestF = f;
                _bestViolation = violation;
                _bestC = (double[])c.Clone();
            }

            private SqpResult Result(SqpStatus status, string reason, int iterations)
            {
                var result = new SqpResult
                {
                    Status = status, Reason = reason == null ? null : "the SQP solve: " + reason, Evaluations = _evaluations, Iterations = iterations,
                };
                if (status == SqpStatus.Invalid) return result;
                // On convergence the answer is the current point; otherwise the best one seen.
                var z = status == SqpStatus.Converged ? _z : _bestZ;
                var c = status == SqpStatus.Converged ? _c : _bestC;
                result.X = X(z);
                result.Objective = _p.Objective(result.X);
                result.Residuals = new double[_m];
                for (var k = 0; k < _m; k++) result.Residuals[k] = c[k] * _p.Tolerances[k];
                return result;
            }

            private bool Feasible(double[] c)
            {
                foreach (var ck in c)
                    if (Math.Abs(ck) > 1) return false;
                return true;
            }

            /// <summary>The point in SI units; a variable at a bound is the caller's bound exactly, not its scaled image (which rounds a ulp outside).</summary>
            private double[] X(double[] z)
            {
                var x = new double[_n];
                for (var i = 0; i < _n; i++)
                    x[i] = z[i] <= _lo[i] ? _p.Lower[i] : z[i] >= _hi[i] ? _p.Upper[i] : z[i] * _unit[i];
                return x;
            }

            private double Clamp(int i, double v)
            {
                return Math.Min(_hi[i], Math.Max(_lo[i], v));
            }

            private double[] Along(double[] z, double[] p, double alpha)
            {
                var r = new double[_n];
                for (var i = 0; i < _n; i++) r[i] = Clamp(i, z[i] + alpha * p[i]);
                return r;
            }

            private static double[] Moved(double[] z, int j, double h)
            {
                var r = (double[])z.Clone();
                r[j] += h;
                return r;
            }
        }

        private static double Dot(double[] a, double[] b)
        {
            var sum = 0.0;
            for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        private static double Norm1(double[] a)
        {
            var sum = 0.0;
            foreach (var x in a) sum += Math.Abs(x);
            return sum;
        }

        private static double NormInf(double[] a)
        {
            var max = 0.0;
            foreach (var x in a) max = Math.Max(max, Math.Abs(x));
            return max;
        }

        /// <summary>A small dense linear solve: Ruiz equilibration, then Gaussian elimination with complete pivoting.</summary>
        private static class Linear
        {
            private const int EquilibrationRounds = 4;

            /// <summary>
            /// The solution of <paramref name="a"/> x = <paramref name="b"/>, or null when, with every row and column scaled to a
            /// largest entry near 1, a pivot falls below 1e-13 (singular). The scaling keeps one stiff entry (a BFGS curvature of
            /// 1e10) from making every small one, the regularisation included, look like zero.
            /// </summary>
            public static double[] Solve(double[,] a, double[] b)
            {
                var n = b.Length;
                a = (double[,])a.Clone();
                b = (double[])b.Clone();
                foreach (var v in a)
                    if (!SearchInput.Finite(v)) return null;
                var colScale = new double[n];
                for (var j = 0; j < n; j++) colScale[j] = 1;
                for (var round = 0; round < EquilibrationRounds; round++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var max = 0.0;
                        for (var j = 0; j < n; j++) max = Math.Max(max, Math.Abs(a[i, j]));
                        if (max <= 0) return null;
                        var s = 1 / Math.Sqrt(max);
                        for (var j = 0; j < n; j++) a[i, j] *= s;
                        b[i] *= s;
                    }
                    for (var j = 0; j < n; j++)
                    {
                        var max = 0.0;
                        for (var i = 0; i < n; i++) max = Math.Max(max, Math.Abs(a[i, j]));
                        if (max <= 0) return null;
                        var s = 1 / Math.Sqrt(max);
                        for (var i = 0; i < n; i++) a[i, j] *= s;
                        colScale[j] *= s;
                    }
                }
                var scaled = Eliminate(a, b);
                if (scaled == null) return null;
                for (var j = 0; j < n; j++) scaled[j] *= colScale[j];
                foreach (var v in scaled)
                    if (!SearchInput.Finite(v)) return null;
                return scaled;
            }

            private static double[] Eliminate(double[,] a, double[] b)
            {
                var n = b.Length;
                var col = new int[n];
                for (var i = 0; i < n; i++) col[i] = i;
                var largest = 0.0;
                foreach (var v in a) largest = Math.Max(largest, Math.Abs(v));
                if (!(largest > 0) || !SearchInput.Finite(largest)) return null;
                for (var k = 0; k < n; k++)
                {
                    int pr = k, pc = k;
                    var max = 0.0;
                    for (var i = k; i < n; i++)
                        for (var j = k; j < n; j++)
                            if (Math.Abs(a[i, j]) > max) { max = Math.Abs(a[i, j]); pr = i; pc = j; }
                    if (max <= 1e-13 * largest) return null;
                    if (pr != k)
                    {
                        for (var j = 0; j < n; j++) { var t = a[k, j]; a[k, j] = a[pr, j]; a[pr, j] = t; }
                        var tb = b[k]; b[k] = b[pr]; b[pr] = tb;
                    }
                    if (pc != k)
                    {
                        for (var i = 0; i < n; i++) { var t = a[i, k]; a[i, k] = a[i, pc]; a[i, pc] = t; }
                        var tc = col[k]; col[k] = col[pc]; col[pc] = tc;
                    }
                    for (var i = k + 1; i < n; i++)
                    {
                        var factor = a[i, k] / a[k, k];
                        if (factor == 0) continue;
                        for (var j = k; j < n; j++) a[i, j] -= factor * a[k, j];
                        b[i] -= factor * b[k];
                    }
                }
                var y = new double[n];
                for (var i = n - 1; i >= 0; i--)
                {
                    var sum = b[i];
                    for (var j = i + 1; j < n; j++) sum -= a[i, j] * y[j];
                    y[i] = sum / a[i, i];
                }
                var x = new double[n];
                for (var i = 0; i < n; i++) x[col[i]] = y[i];
                foreach (var v in x)
                    if (!SearchInput.Finite(v)) return null;
                return x;
            }
        }
    }
}
