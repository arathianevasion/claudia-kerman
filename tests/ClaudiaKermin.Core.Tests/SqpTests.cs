// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// <see cref="Sqp"/> (#447, ADR 0010) on textbook problems with known answers: a constrained quadratic, Rosenbrock on the
    /// unit circle, a Hohmann burn and an equatorial plane change round Kerbin, a bound active at the answer, a start next to
    /// an undefined region, and the ways a solve can fail. The Course Correction sweep is in Numerics/CourseCorrectionSweepTests.
    /// </summary>
    public sealed class SqpTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;

        /// <summary>Unit scale, so the solver's scaled variables are the problem's own.</summary>
        private static readonly Scale Unit = Scale.Create(1, 1);

        private static SqpProblem Problem(int n, Func<double[], double> f, Func<double[], double[], bool> c, double[] start, double tol = 1e-9)
        {
            var kinds = new SqpVariable[n];
            return new SqpProblem
            {
                Scale = Unit, Variables = kinds, Start = start, Objective = f, Constraints = 1, Evaluate = c, Tolerances = new[] { tol },
                MaxIterations = 100,
            };
        }

        private static double[,] Diagonal(params double[] d)
        {
            var h = new double[d.Length, d.Length];
            for (var i = 0; i < d.Length; i++) h[i, i] = d[i];
            return h;
        }

        // ---- textbook problems ----

        /// <summary>min x² + 2y² + 3z² on x + y + z = 1: Lagrange gives 2x = 4y = 6z, so (6, 3, 2)/11.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_constrained_quadratic_reaches_its_lagrange_answer(bool exactHessian)
        {
            var problem = Problem(3, x => x[0] * x[0] + 2 * x[1] * x[1] + 3 * x[2] * x[2], (x, c) =>
            {
                c[0] = x[0] + x[1] + x[2] - 1;
                return true;
            }, new double[] { 0, 0, 0 });
            if (exactHessian) problem.ObjectiveHessian = Diagonal(2, 4, 6);
            var result = Sqp.Solve(problem);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(6.0 / 11, result.X[0], 6);
            Assert.Equal(3.0 / 11, result.X[1], 6);
            Assert.Equal(2.0 / 11, result.X[2], 6);
            Assert.InRange(Math.Abs(result.Residuals[0]), 0, 1e-9);
        }

        /// <summary>Rosenbrock on the unit circle (fmincon's example, there as x² + y² ≤ 1, which is active): checked against BrentMin over the circle's angle.</summary>
        [Fact]
        public void Rosenbrock_on_the_unit_circle_reaches_the_constrained_minimum()
        {
            Func<double, double, double> rosenbrock = (x, y) => (1 - x) * (1 - x) + 100 * (y - x * x) * (y - x * x);
            var reference = BrentMin.Solve(t => rosenbrock(Math.Cos(t), Math.Sin(t)), 0, Math.PI / 2, 1e-10, 200);
            Assert.True(reference.Converged);
            Assert.Equal(0.7864, Math.Cos(reference.X), 4);

            var result = Sqp.Solve(Problem(2, x => rosenbrock(x[0], x[1]), (x, c) =>
            {
                c[0] = x[0] * x[0] + x[1] * x[1] - 1;
                return true;
            }, new double[] { 0, 0 }));
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(Math.Cos(reference.X), result.X[0], 5);
            Assert.Equal(Math.Sin(reference.X), result.X[1], 5);
        }

        /// <summary>
        /// The least burn from a circular 100 km orbit that puts apoapsis at the Mun's 12,000 km is Hohmann's prograde burn,
        /// 842 m/s (as EjectionBurnTests works it), in Kerbin's scale with the burn in m/s.
        /// </summary>
        [Fact]
        public void The_least_burn_to_a_given_apoapsis_is_the_Hohmann_burn()
        {
            const double r1 = KerbinR + 100000, r2 = 12000000;
            var hohmann = Math.Sqrt(KerbinGm * 2 * r2 / (r1 * (r1 + r2))) - Math.Sqrt(KerbinGm / r1);
            var orbit = Conic.FromElements(KerbinGm, r1, 0, 0, 0, 0, 0, 0);
            Vec3 r, v;
            orbit.State(0, out r, out v);
            var result = Sqp.Solve(Burn(x =>
            {
                var after = Conic.FromState(KerbinGm, r, v + new Vec3(x[0], x[1], x[2]), 0);
                return new[] { after.Closed ? after.ApR - r2 : double.NaN };
            }, 1, new[] { 1.0 }));
            Assert.True(result.Converged, result.Reason);
            var burn = new Vec3(result.X[0], result.X[1], result.X[2]);
            Assert.Equal(hohmann, burn.Length, 2);
            Assert.InRange(Vec3.Dot(burn, v.Normalized), hohmann - 0.01, hohmann + 0.01);
            Assert.InRange(Math.Abs(result.Residuals[0]), 0, 1);
        }

        /// <summary>
        /// At the ascending node of a 5° orbit the least burn to an equatorial orbit removes the velocity's z part (any v with
        /// v_z = 0 makes r × v point along z there). At 0° the cosine form has zero slope, so this runs through the two-constraint form.
        /// </summary>
        [Fact]
        public void An_equatorial_target_converges_through_the_two_constraint_form()
        {
            Assert.Equal(2, Sqp.InclinationConstraints(0));
            var orbit = Conic.FromElements(KerbinGm, KerbinR + 100000, 0, 5, 0, 0, 0, 0);
            Vec3 r, v;
            orbit.State(0, out r, out v);
            var result = Sqp.Solve(Burn(x =>
            {
                var c = new double[2];
                Sqp.Inclination(Vec3.Cross(r, v + new Vec3(x[0], x[1], x[2])), 0, c, 0);
                return c;
            }, 2, new[] { 1e-7, 1e-7 }));
            Assert.True(result.Converged, result.Reason);
            Assert.InRange(result.X[0], -0.01, 0.01);
            Assert.InRange(result.X[1], -0.01, 0.01);
            Assert.Equal(-v.Z, result.X[2], 2);
            var after = Conic.FromState(KerbinGm, r, v + new Vec3(result.X[0], result.X[1], result.X[2]), 0);
            Assert.InRange(after.Inclination, 0, 1e-4);
        }

        /// <summary>min (x − 3)² + (y − 1)² on x + y = 2 is (2, 0) unbounded; with x ≤ 1.5 the bound holds at the answer (1.5, 0.5).</summary>
        [Fact]
        public void A_bound_active_at_the_answer_holds_it()
        {
            var problem = Problem(2, x => (x[0] - 3) * (x[0] - 3) + (x[1] - 1) * (x[1] - 1), (x, c) =>
            {
                c[0] = x[0] + x[1] - 2;
                return true;
            }, new double[] { 0, 0 });
            problem.Variables = new[] { SqpVariable.Time, SqpVariable.Velocity };
            problem.Lower = new[] { -10.0, double.NegativeInfinity };
            problem.Upper = new[] { 1.5, double.PositiveInfinity };
            problem.ObjectiveHessian = Diagonal(2, 2);
            var result = Sqp.Solve(problem);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(1.5, result.X[0], 9);
            Assert.Equal(0.5, result.X[1], 6);

            // And a bound at the start that the answer leaves: x ≥ 1.8 starting there reaches the unbounded answer (2, 0).
            problem.Start = new[] { 1.8, 0.0 };
            problem.Lower = new[] { 1.8, double.NegativeInfinity };
            problem.Upper = null;
            result = Sqp.Solve(problem);
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(2, result.X[0], 6);
            Assert.Equal(0, result.X[1], 6);
        }

        /// <summary>
        /// min 4(x² + y²) on x + y = 2 from (0.1, −0.1) with no exact Hessian: the first step (from W = I, against a gradient
        /// scaled to 1) lands on (0.1, 1.9), inside a region (y &gt; 1.5) where the constraint is undefined, as a burn past which
        /// the encounter is lost. It must back off and still converge to (1, 1).
        /// </summary>
        [Fact]
        public void A_start_next_to_an_undefined_region_converges_after_backtracking()
        {
            var undefined = 0;
            var result = Sqp.Solve(Problem(2, x => 4 * (x[0] * x[0] + x[1] * x[1]), (x, c) =>
            {
                if (x[1] > 1.5)
                {
                    undefined++;
                    return false;
                }
                c[0] = x[0] + x[1] - 2;
                return true;
            }, new double[] { 0.1, -0.1 }));
            Assert.True(undefined > 0, "the first step never reached the undefined region, so this tests nothing");
            Assert.True(result.Converged, result.Reason);
            Assert.Equal(1, result.X[0], 6);
            Assert.Equal(1, result.X[1], 6);
        }

        // ---- failures ----

        [Fact]
        public void An_undefined_start_is_invalid()
        {
            var calls = 0;
            var result = Sqp.Solve(Problem(2, x => x[0] * x[0], (x, c) =>
            {
                calls++;
                return false;
            }, new double[] { 0, 0 }));
            Assert.Equal(SqpStatus.Invalid, result.Status);
            Assert.Contains("start is undefined", result.Reason);
            Assert.Equal(1, calls);
            Assert.Null(result.X);
        }

        [Fact]
        public void A_non_finite_residual_is_undefined()
        {
            var result = Sqp.Solve(Problem(1, x => x[0] * x[0], (x, c) =>
            {
                c[0] = double.NaN;
                return true;
            }, new double[] { 0 }));
            Assert.Equal(SqpStatus.Invalid, result.Status);
        }

        [Fact]
        public void Unusable_inputs_are_invalid_without_a_call()
        {
            var calls = 0;
            Func<SqpProblem> make = () => Problem(2, x => x[0] * x[0], (x, c) =>
            {
                calls++;
                c[0] = x[0] + x[1] - 1;
                return true;
            }, new double[] { 0, 0 });
            var broken = new Action<SqpProblem>[]
            {
                p => p.Start = new[] { double.NaN, 0 },
                p => p.Start = new[] { 0.0 },
                p => p.Tolerances = new[] { 0.0 },
                p => p.Tolerances = new[] { 1.0, 1.0 },
                p => p.MaxIterations = 0,
                p => p.Constraints = 3,
                p => p.Constraints = 0,
                p => p.Objective = null,
                p => p.Evaluate = null,
                p => p.Variables = new SqpVariable[0],
                p => p.Scale = default(Scale),
                p => { p.Lower = new[] { 1.0, 0 }; },
                p => { p.Lower = new[] { 0.0, 0 }; p.Upper = new[] { -1.0, 0 }; },
                p => p.Upper = new[] { 0.0 },
            };
            foreach (var breakIt in broken)
            {
                var problem = make();
                breakIt(problem);
                var result = Sqp.Solve(problem);
                Assert.Equal(SqpStatus.Invalid, result.Status);
                Assert.NotNull(result.Reason);
            }
            Assert.Equal(SqpStatus.Invalid, Sqp.Solve(null).Status);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void The_iteration_cap_returns_the_best_point_with_its_status()
        {
            Func<double, double, double> rosenbrock = (x, y) => (1 - x) * (1 - x) + 100 * (y - x * x) * (y - x * x);
            var problem = Problem(2, x => rosenbrock(x[0], x[1]), (x, c) =>
            {
                c[0] = x[0] * x[0] + x[1] * x[1] - 1;
                return true;
            }, new double[] { 0, 0 });
            problem.MaxIterations = 2;
            var result = Sqp.Solve(problem);
            Assert.Equal(SqpStatus.IterationCap, result.Status);
            Assert.Contains("2 iterations", result.Reason);
            Assert.NotNull(result.X);
            Assert.Equal(rosenbrock(result.X[0], result.X[1]), result.Objective, 12);
            Assert.Equal(result.X[0] * result.X[0] + result.X[1] * result.X[1] - 1, result.Residuals[0], 12);
            Assert.True(result.Evaluations > 0);
        }

        [Fact]
        public void Derivatives_undefined_all_round_the_start_stall_at_the_start()
        {
            var result = Sqp.Solve(Problem(2, x => x[0] * x[0], (x, c) =>
            {
                c[0] = x[0] + x[1] - 1;
                return x[0] == 0.25 && x[1] == 0.5;
            }, new[] { 0.25, 0.5 }));
            Assert.Equal(SqpStatus.Stalled, result.Status);
            Assert.Equal(new[] { 0.25, 0.5 }, result.X);
            Assert.Equal(-0.25, result.Residuals[0], 12);
        }

        // ---- the inclination helper ----

        [Fact]
        public void The_inclination_helper_uses_the_cosine_away_from_the_equator_and_the_normal_near_it()
        {
            Assert.Equal(1, Sqp.InclinationConstraints(1.5));
            Assert.Equal(1, Sqp.InclinationConstraints(90));
            Assert.Equal(1, Sqp.InclinationConstraints(178.5));
            Assert.Equal(2, Sqp.InclinationConstraints(0.5));
            Assert.Equal(2, Sqp.InclinationConstraints(179.5));
            Assert.Equal(2, Sqp.InclinationConstraints(180));

            // An orbit inclined 30° with its ascending node on +x: the normal is (0, −sin 30°, cos 30°).
            var h = new Vec3(0, -Math.Sin(Math.PI / 6), Math.Cos(Math.PI / 6)) * 7.5e9;
            var c = new double[3];
            Sqp.Inclination(h, 30, c, 1);
            Assert.Equal(0, c[1], 12);
            Sqp.Inclination(h, 60, c, 1);
            Assert.Equal(Math.Cos(Math.PI / 6) - 0.5, c[1], 12);
            Sqp.Inclination(h, 0, c, 1);
            Assert.Equal(0, c[1], 12);
            Assert.Equal(-0.5, c[2], 12);
            Sqp.Inclination(new Vec3(0, 0, -3), 180, c, 0);
            Assert.Equal(0, c[0], 12);
            Assert.Equal(0, c[1], 12);
        }

        /// <summary>A burn problem round Kerbin: minimise |Δv|² over its three components (m/s) from zero, subject to <paramref name="residuals"/>.</summary>
        private static SqpProblem Burn(Func<double[], double[]> residuals, int m, double[] tolerances)
        {
            return new SqpProblem
            {
                Scale = Scale.Create(KerbinGm, KerbinR),
                Variables = new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                Start = new double[3],
                Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                ObjectiveHessian = Diagonal(2, 2, 2),
                Constraints = m,
                Evaluate = (x, c) =>
                {
                    var r = residuals(x);
                    Array.Copy(r, c, m);
                    return true;
                },
                Tolerances = tolerances,
                MaxIterations = 100,
            };
        }
    }
}
