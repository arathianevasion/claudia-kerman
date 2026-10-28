// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using ClaudiaKermin.Core.Tools.Numerics;
using Xunit;

namespace ClaudiaKermin.Core.Tests.Numerics
{
    /// <summary>
    /// Seeded random sweep of <see cref="Sqp"/> on Course Corrections (#447, the #440 pattern): <see cref="EjectionBurn"/>'s Mun
    /// transfer from EjectionBurnTests' circular 100 km orbit, its ejection perturbed by 0.5 to 5 m/s in a random direction, and
    /// then the least burn at a random point of the coast that brings periapsis at the Mun to a random target_pe, posed by single
    /// shooting through <see cref="BodySystem.Follow"/> as #449 will pose it. Each must converge within 1 km of target_pe, in
    /// under 100 ms, to a burn along the periapsis's gradient (the least burn meeting one constraint lies along it). Two more
    /// sweeps add an arrival inclination: a random one from 20° to 160° through the cosine form, and an equatorial one through
    /// the two-constraint form. In patched conics the Mun-relative orbit is equatorial only if the Kerbin conic is, so the
    /// equatorial sweep perturbs the ejection in its plane alone; the pair of normal components is then redundant (both move
    /// with the one out-of-plane burn component), which a real equatorial Course Correction also poses.
    /// </summary>
    public sealed class CourseCorrectionSweepTests
    {
        /// <summary>What a sweep asks of the arrival besides target_pe.</summary>
        internal enum Arrival
        {
            AnyInclination,
            Inclined,
            Equatorial,
            /// <summary>Any inclination, with the burn time a fourth variable bounded to a window.</summary>
            FreeTime,
        }

        private const int Cases = 4;
        private const double KerbinR = 600000;
        private const double MunR = 200000;
        private const double EjectionPe = 30000;
        /// <summary>The periapsis tolerance the solve is given (EjectionBurn's PeClose), and the acceptance's bound on the answer.</summary>
        private const double PeTolerance = 10;
        private const double PeBound = 1000;
        /// <summary>The inclination residuals' tolerance (of a cosine, or of a unit normal's component), and the bound on the arrival, degrees.</summary>
        private const double IncTolerance = 1e-6;
        private const double IncBound = 0.01;
        private const double BudgetMs = 100;
        /// <summary>How much more than the fixed-time burn a free-time one may cost, m/s: the two solves' step tolerance (about 2 mm/s each).</summary>
        private const double FreeTimeSlack = 0.005;
        /// <summary>The angle the least burn may make with the periapsis's gradient, degrees: the solver's derivatives are finite differences on a periapsis staircase.</summary>
        private const double AlongGradient = 1;

        /// <summary>Which planned ejection a sweep perturbs: the same Mun transfer as two planners find it.</summary>
        public enum Plan
        {
            /// <summary><see cref="EjectionBurn"/>'s, found when the sweep first needs it.</summary>
            EjectionBurn,
            /// <summary><see cref="TransferFindFigures"/>: the transfer as #448's <c>Transfer.Find</c> plans it.</summary>
            TransferFind,
        }

        /// <summary>
        /// The ejection as #448's <c>Transfer.Find</c> plans it from <see cref="Circular"/>, about 1 ms and 0.5 mm/s from
        /// <see cref="EjectionBurn"/>'s. Sqp once stalled on two seeds from it (#481), which a Course Correction must not: a real
        /// one starts from whatever the last plan left. When Transfer.Find is on main, point this at it.
        /// </summary>
        internal static readonly EjectionPlan TransferFindFigures = new EjectionPlan
        {
            Ut = 1912.6872892554063, Prograde = 837.6841505772729, Normal = 0, Radial = 0.000538,
        };

        /// <summary>Every seed from every plan.</summary>
        public static IEnumerable<object[]> Seeds
        {
            get
            {
                foreach (Plan plan in Enum.GetValues(typeof(Plan)))
                    foreach (var seed in Sweep.SeedValues)
                        yield return new object[] { plan, seed };
            }
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void A_perturbed_Mun_transfer_is_corrected_to_target_pe_in_under_100_ms(Plan plan, int seed)
        {
            Corrects(seed, Arrival.AnyInclination, plan, true);
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void A_perturbed_Mun_transfer_is_corrected_to_target_pe_and_an_inclined_arrival(Plan plan, int seed)
        {
            Corrects(seed, Arrival.Inclined, plan, true);
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void A_perturbed_Mun_transfer_is_corrected_to_target_pe_and_an_equatorial_arrival(Plan plan, int seed)
        {
            Corrects(seed, Arrival.Equatorial, plan, true);
        }

        [Theory]
        [MemberData(nameof(Seeds))]
        public void A_perturbed_Mun_transfer_is_corrected_to_target_pe_with_a_free_burn_time(Plan plan, int seed)
        {
            Corrects(seed, Arrival.FreeTime, plan, true);
        }

        /// <summary>EjectionBurnTests' start: a circular, equatorial 100 km orbit at UT 0, the Mun at mean anomaly 1.7.</summary>
        private static GameSnapshot Circular()
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = KerbinR + 100000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = Sweep.KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000,
                MeanAnomalyAtEpoch = 1.7,
            });
            return snapshot;
        }

        /// <summary>The transfer, found once: the setup, and the ejection's UT, state and burn (inertial, m/s).</summary>
        private sealed class Transfer
        {
            public PredictBurn.Setup Setup;
            public double Ut;
            public Vec3 R, V, Burn;
        }

        private static readonly Lazy<Transfer> ByEjectionBurn = new Lazy<Transfer>(() =>
        {
            var plan = EjectionBurn.Find(Circular(), new EjectionAsk { Target = "Mun", TargetPe = EjectionPe, EarliestUt = 0 });
            Assert.Null(plan.Error);
            return Planned(plan);
        });

        private static readonly Lazy<Transfer> ByTransferFind = new Lazy<Transfer>(() => Planned(TransferFindFigures));

        /// <summary>The transfer a plan from <see cref="Circular"/> flies.</summary>
        private static Transfer Planned(EjectionPlan plan)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(Circular(), "Mun", out error);
            Assert.NotNull(setup);
            Vec3 r, v;
            setup.Start.State(plan.Ut, out r, out v);
            // Stock's maneuver-node frame, as PredictBurn applies it.
            var along = v.Normalized;
            var up = Vec3.Cross(r, v).Normalized;
            var outward = Vec3.Cross(along, up);
            return new Transfer { Setup = setup, Ut = plan.Ut, R = r, V = v, Burn = along * plan.Prograde + up * plan.Normal + outward * plan.Radial };
        }

        /// <summary>The Mun patch after a burn of <paramref name="dv"/> at (<paramref name="r"/>, <paramref name="v"/>) at <paramref name="ut"/>, or null when the trajectory has no Mun encounter.</summary>
        /// <remarks>
        /// The Kerbin patch is followed alone and the switch to the Mun made here as <see cref="BodySystem.Follow"/> makes it:
        /// asked for two patches, Follow takes the state at the end of the second, which throws when a trial burn escapes Kerbin
        /// onto a hyperbola round the Sun (its end is at infinity).
        /// </remarks>
        private static Patch MunPatch(BodySystem system, Vec3 r, Vec3 v, Vec3 dv, double ut)
        {
            var kerbin = system.Find("Kerbin");
            var coast = system.Follow(kerbin, Conic.FromState(kerbin.Gm, r, v + dv, ut), ut, 1)[0];
            if (coast.Error != null || coast.End != PatchEnd.Encounter || coast.Next.Name != "Mun") return null;
            Vec3 er, ev, mr, mv;
            coast.Orbit.State(coast.EndUt, out er, out ev);
            system.OrbitOf(coast.Next).State(coast.EndUt, out mr, out mv);
            var arrival = Conic.FromState(coast.Next.Gm, er - mr, ev - mv, coast.EndUt);
            return new Patch { Body = coast.Next, Orbit = arrival, StartUt = coast.EndUt };
        }

        private static Vec3 Normal(Patch patch)
        {
            Vec3 r, v;
            patch.Orbit.State(patch.StartUt, out r, out v);
            return Vec3.Cross(r, v);
        }

        internal static void Corrects(int seed, Arrival ask, Plan plan, bool timed)
        {
            var name = "Course Correction" + (ask == Arrival.AnyInclination ? "" : " (" + ask + ")") + " from " + plan + "'s plan";
            var transfer = (plan == Plan.TransferFind ? ByTransferFind : ByEjectionBurn).Value;
            var system = transfer.Setup.System;
            var rng = new Random(seed);
            for (var i = 0; i < Cases; i++)
            {
                // A random direction, uniform on the sphere (on the equator's circle for an equatorial arrival), and a size from 0.5 to 5 m/s.
                var z = ask == Arrival.Equatorial ? 0 : rng.Uniform(-1, 1);
                var phi = rng.Uniform(0, 2 * Math.PI);
                var size = rng.Uniform(0.5, 5);
                var error = new Vec3(Math.Sqrt(1 - z * z) * Math.Cos(phi), Math.Sqrt(1 - z * z) * Math.Sin(phi), z) * size;
                var share = rng.Uniform(0.1, 0.6);
                var targetPe = rng.Uniform(20000, 60000);
                var inclined = rng.Uniform(20, 160);
                var inputs = "ejection error=" + Sweep.R(error) + " m/s at UT " + Sweep.R(transfer.Ut) + ", correction at " + Sweep.R(share)
                    + " of the coast, target_pe=" + Sweep.R(targetPe);

                var kerbin = system.Find("Kerbin");
                var ejected = Conic.FromState(kerbin.Gm, transfer.R, transfer.V + transfer.Burn + error, transfer.Ut);
                var coast = system.Follow(kerbin, ejected, transfer.Ut, 1)[0];
                if (coast.End != PatchEnd.Encounter)
                    Sweep.Fail(name, seed, i, inputs, "the perturbed transfer has no Mun encounter, so the correction has no defined start");
                var ut = transfer.Ut + share * (coast.EndUt - transfer.Ut);
                Vec3 r, v;
                ejected.State(ut, out r, out v);
                var start = MunPatch(system, r, v, default(Vec3), ut);
                if (start == null) Sweep.Fail(name, seed, i, inputs, "the coast loses its Mun encounter from the correction's UT on");
                // Equatorial round the Mun on the side the transfer arrives: 0° prograde, 180° retrograde.
                var targetInc = ask == Arrival.Inclined ? inclined : Normal(start).Z > 0 ? 0.0 : 180.0;
                var withInc = ask == Arrival.Inclined || ask == Arrival.Equatorial;
                inputs += withInc ? " target_inc=" + Sweep.R(targetInc) : "";

                var m = withInc ? 1 + Sqp.InclinationConstraints(targetInc) : 1;
                var tolerances = new double[m];
                tolerances[0] = PeTolerance;
                for (var k = 1; k < m; k++) tolerances[k] = IncTolerance;
                // A free burn time is a fourth variable, bounded to a window round the correction's UT a tenth of the coast each way.
                var free = ask == Arrival.FreeTime;
                var window = 0.1 * (coast.EndUt - transfer.Ut);
                if (free) inputs += " burn time free in [" + Sweep.R(ut - window) + ", " + Sweep.R(ut + window) + "]";
                Func<bool, SqpProblem> pose = timeFree => new SqpProblem
                {
                    Scale = Scale.Create(kerbin.Gm, kerbin.Radius),
                    Variables = timeFree
                        ? new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Time }
                        : new[] { SqpVariable.Velocity, SqpVariable.Velocity, SqpVariable.Velocity },
                    Start = timeFree ? new[] { 0, 0, 0, ut } : new double[3],
                    Lower = timeFree ? new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, ut - window } : null,
                    Upper = timeFree ? new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, ut + window } : null,
                    Objective = x => x[0] * x[0] + x[1] * x[1] + x[2] * x[2],
                    ObjectiveHessian = timeFree
                        ? new double[,] { { 2, 0, 0, 0 }, { 0, 2, 0, 0 }, { 0, 0, 2, 0 }, { 0, 0, 0, 0 } }
                        : new double[,] { { 2, 0, 0 }, { 0, 2, 0 }, { 0, 0, 2 } },
                    Constraints = m,
                    Evaluate = (x, c) =>
                    {
                        var at = timeFree ? x[3] : ut;
                        Vec3 rt = r, vt = v;
                        if (timeFree) ejected.State(at, out rt, out vt);
                        var patch = MunPatch(system, rt, vt, new Vec3(x[0], x[1], x[2]), at);
                        if (patch == null) return false;
                        c[0] = patch.Orbit.PeR - MunR - targetPe;
                        if (withInc) Sqp.Inclination(Normal(patch), targetInc, c, 1);
                        return true;
                    },
                    Tolerances = tolerances,
                    MaxIterations = 50,
                };
                var problem = pose(free);
                SqpResult result = null;
                if (timed) Timing.Within(BudgetMs, () => result = Sqp.Solve(problem));
                else result = Sqp.Solve(problem);

                if (!result.Converged)
                    Sweep.Fail(name, seed, i, inputs, result.Status + " after " + result.Iterations + " iterations and " + result.Evaluations
                        + " evaluations: " + result.Reason + "; residuals " + string.Join(", ", result.Residuals ?? new double[0]));
                var burn = new Vec3(result.X[0], result.X[1], result.X[2]);
                if (free)
                {
                    // Any time in the window may be chosen, the fixed one included, so the free answer costs no more than it.
                    var when = result.X[3];
                    if (when < ut - window || when > ut + window)
                        Sweep.Fail(name, seed, i, inputs, "the burn time " + Sweep.R(when) + " is outside its window");
                    var fixedAt = Sqp.Solve(pose(false));
                    if (!fixedAt.Converged) Sweep.Fail(name, seed, i, inputs, "the fixed-time solve did not converge: " + fixedAt.Reason);
                    var fixedDv = Math.Sqrt(fixedAt.Objective);
                    if (burn.Length > fixedDv + FreeTimeSlack)
                        Sweep.Fail(name, seed, i, inputs, "a free burn time costs " + Sweep.R(burn.Length) + " m/s, more than " + Sweep.R(fixedDv) + " m/s at the fixed time");
                    ejected.State(when, out r, out v);
                    ut = when;
                }
                var arrival = MunPatch(system, r, v, burn, ut);
                if (arrival == null) Sweep.Fail(name, seed, i, inputs, "the answer " + Sweep.R(burn) + " has no Mun encounter");
                var pe = arrival.Orbit.PeR - MunR;
                if (Math.Abs(pe - targetPe) > PeBound)
                    Sweep.Fail(name, seed, i, inputs, "periapsis " + Sweep.R(pe) + " m, " + Sweep.R(pe - targetPe) + " m from target_pe");
                if (withInc)
                {
                    var inc = Math.Acos(Math.Max(-1, Math.Min(1, Normal(arrival).Normalized.Z))) * 180 / Math.PI;
                    if (Math.Abs(inc - targetInc) > IncBound)
                        Sweep.Fail(name, seed, i, inputs, "arrives at " + Sweep.R(inc) + "°, not " + Sweep.R(targetInc) + "°");
                }
                else if (!free)
                {
                    // The least burn meeting one constraint lies along that constraint's gradient: check it by finer differences.
                    const double h = 0.01;
                    var gradient = new double[3];
                    for (var j = 0; j < 3; j++)
                    {
                        var step = new Vec3(j == 0 ? h : 0, j == 1 ? h : 0, j == 2 ? h : 0);
                        var up = MunPatch(system, r, v, burn + step, ut);
                        var down = MunPatch(system, r, v, burn - step, ut);
                        if (up == null || down == null) Sweep.Fail(name, seed, i, inputs, "the answer is within " + h + " m/s of losing the encounter");
                        gradient[j] = (up.Orbit.PeR - down.Orbit.PeR) / (2 * h);
                    }
                    var g = new Vec3(gradient[0], gradient[1], gradient[2]);
                    var angle = Math.Acos(Math.Min(1, Math.Abs(Vec3.Dot(g, burn)) / (g.Length * burn.Length))) * 180 / Math.PI;
                    if (angle > AlongGradient)
                        Sweep.Fail(name, seed, i, inputs, "the burn " + Sweep.R(burn) + " is " + Sweep.R(angle) + "° off the periapsis's gradient, so it is not the least");
                }
            }
        }
    }
}
