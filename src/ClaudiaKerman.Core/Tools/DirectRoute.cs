// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>A route's price from body data alone (<see cref="DirectRoute.Price"/>), m/s, without a capture at the far end.</summary>
    internal sealed class DirectRoutePrice
    {
        /// <summary>The burn from the moon's orbit onto the fall to the low periapsis at its planet: both routes pay it.</summary>
        public double ReturnDv;
        /// <summary>The two-step route's capture from the fall into a low circular orbit.</summary>
        public double PlanetCaptureDv;
        /// <summary>The two-step route's ejection from that low orbit.</summary>
        public double TwoStepEjectionDv;
        /// <summary>The direct route's ejection, at the fall's periapsis.</summary>
        public double EjectionDv;

        public double Direct
        {
            get { return ReturnDv + EjectionDv; }
        }

        public double TwoStep
        {
            get { return ReturnDv + PlanetCaptureDv + TwoStepEjectionDv; }
        }

        /// <summary>The Oberth saving: what the two-step route pays to stop at the periapsis and again to regain that speed.</summary>
        public double Saving
        {
            get { return TwoStep - Direct; }
        }
    }

    /// <summary>One route of a trip from a moon's orbit to another planet: its burns and when they are, as flown.</summary>
    internal sealed class RouteRow
    {
        /// <summary>When the burn out of the moon's orbit is, and the UT to give return's window for that burn.</summary>
        public double DepartUt;
        public double ReturnWindow;
        public double ReturnDv;
        /// <summary>The capture at the planet: the two-step route's only; 0 on the direct route.</summary>
        public double PlanetCaptureDv;
        /// <summary>When the ejection from the planet is, and the UT to give transfer_to's window for it.</summary>
        public double EjectUt;
        public double EjectWindow;
        public double EjectionDv;
        /// <summary>The capture at the target, 0 without capture.</summary>
        public double CaptureDv;
        public double ArrivalUt;
        public double ArrivalVInf;
        public double TotalDv;
    }

    /// <summary>The two routes from a moon's orbit to another planet, or why there are none (<see cref="Error"/>).</summary>
    internal sealed class DirectRoutePlan
    {
        public string Error;
        public string Target;
        public string Planet;
        /// <summary>The periapsis both routes fall to at the planet (its low orbit's height) and the one given to transfer_to at the target, m.</summary>
        public double ReturnPe;
        public double TargetPe;
        /// <summary>The window both routes take: the setting's pick, named, and the speed leaving the planet there.</summary>
        public string WindowName;
        public double VInf;
        public RouteRow Direct;
        public RouteRow TwoStep;
        /// <summary>Why there is no direct row, when only it failed.</summary>
        public string DirectError;
        public RouteRow Pick;
        public double? CraftDv;
        public double? LaterLegsDv;
    }

    /// <summary>
    /// A trip from a moon's orbit to another planet of its star as one leg (#760). Two routes in the same Transfer Window: the
    /// <b>two-step</b>, a return to the planet's low orbit, a capture there and the ejection from it; and the <b>direct</b>, a fall to
    /// the same low periapsis (<see cref="ReturnBurn"/>, #759) at the time the moon's place puts that periapsis where the ejection
    /// wants it, and the ejection at the periapsis (the Oberth burn of <see cref="SiblingEjection"/>, as transfer_to flies it). The
    /// saving is twice the capture the direct route doesn't fly. The direct route is flown by the existing blocks, return then
    /// transfer_to, each with its window from the row: a craft falling from a moon is in a closed orbit round the planet, which
    /// is what transfer_to's parking orbit asks. <see cref="Price"/> is the same figure from body data, for the Delta-v Map.
    /// </summary>
    internal static class DirectRoute
    {
        /// <summary>Whether <paramref name="target"/> is a planet of the star of the planet whose moon the craft is in orbit round, other than that planet.</summary>
        public static bool Applies(GameSnapshot snapshot, string target)
        {
            if (snapshot == null || string.IsNullOrEmpty(target) || snapshot.IsGrounded || snapshot.Orbit == null || !(snapshot.Orbit.Eccentricity < 1) || !(snapshot.Orbit.SemiMajorAxis > 0)) return false;
            var system = new BodySystem(snapshot.Bodies);
            var moon = system.Find(string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Body : snapshot.Orbit.Body);
            var body = system.Find(target);
            if (moon == null || body == null) return false;
            var planet = system.Parent(moon);
            var star = planet == null ? null : system.Parent(planet);
            return star != null && string.IsNullOrEmpty(star.Parent) && body != planet && string.Equals(body.Parent, star.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The two routes' legs from body data, the one pricing of this leg for whatever has no craft or window to hand (the Delta-v
        /// Map): from a circular orbit <paramref name="moonOrbitR"/> m from <paramref name="moon"/>'s centre (its low orbit by default), the
        /// fall to <paramref name="planet"/>'s low orbit height, leaving the planet at <paramref name="vInf"/> m/s. No plane change: the fall
        /// and the ejection are taken in the plane of the moon's orbit.
        /// </summary>
        public static DirectRoutePrice Price(SnapshotBody moon, SnapshotBody planet, double vInf, double? moonOrbitR = null)
        {
            var peR = DeltaVMap.LowOrbit(planet);
            var r = moonOrbitR ?? DeltaVMap.LowOrbit(moon);
            var a = (peR + moon.Sma) / 2;
            var excess = Math.Abs(Math.Sqrt(planet.Gm / moon.Sma) - Math.Sqrt(planet.Gm * (2 / moon.Sma - 1 / a)));
            var price = new DirectRoutePrice { ReturnDv = Math.Sqrt(excess * excess + 2 * moon.Gm / r) - Math.Sqrt(moon.Gm / r) };
            Legs(planet.Gm, peR, Math.Sqrt(planet.Gm * (2 / peR - 1 / a)), vInf, price);
            return price;
        }

        /// <summary>The legs at the planet, from a periapsis <paramref name="peR"/> reached at <paramref name="vAtPe"/>: the shared sums of both routes.</summary>
        private static void Legs(double mu, double peR, double vAtPe, double vInf, DirectRoutePrice price)
        {
            var circular = Math.Sqrt(mu / peR);
            var leaving = Math.Sqrt(vInf * vInf + 2 * mu / peR);
            price.PlanetCaptureDv = vAtPe - circular;
            price.TwoStepEjectionDv = leaving - circular;
            price.EjectionDv = leaving - vAtPe;
        }

        /// <summary>The route the setting takes, by <see cref="TransferWindows.Pick"/>'s rule: Fastest the one arriving first, Most efficient the cheaper, Balanced the one arriving first unless the other saves <see cref="TransferWindows.BalancedSaving"/>.</summary>
        public static RouteRow Pick(RouteRow a, RouteRow b, TimeVersusDeltaV setting)
        {
            var sooner = a.ArrivalUt <= b.ArrivalUt ? a : b;
            var other = sooner == a ? b : a;
            switch (setting)
            {
                case TimeVersusDeltaV.Fastest: return sooner;
                case TimeVersusDeltaV.MostEfficient: return a.TotalDv <= b.TotalDv ? a : b;
                default: return sooner.TotalDv - other.TotalDv >= TransferWindows.BalancedSaving * sooner.TotalDv ? other : sooner;
            }
        }

        /// <summary>The periapsis (m) a trip to <paramref name="body"/> that ends in reentry aims at when plan_transfer isn't given one: the entry periapsis.</summary>
        public static double EntryPe(SnapshotBody body)
        {
            return Math.Round(DeorbitBurn.EntryPeriapsisShare * body.AtmosphereHeight);
        }

        /// <summary>Both routes for the Game Snapshot's craft, in the window the setting picks; call only where <see cref="Applies"/>.</summary>
        public static DirectRoutePlan Find(GameSnapshot snapshot, string target, double targetPe, bool capture)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
            var system = setup.System;
            var moon = setup.Body;
            var planet = system.Parent(moon);
            var now = setup.Now;
            var craft = setup.Start;
            var peAltitude = Math.Round(DeltaVMap.LowOrbit(planet) - planet.Radius);

            var home = ReturnBurn.Find(system, moon, craft, now, new ReturnAsk { TargetPe = peAltitude, EarliestUt = now });
            if (home.Error != null) return Fail(home.Error);
            var fall = Arrival(system, moon, planet, craft, home);
            if (fall == null) return Fail("the return does not reach " + planet.Name);
            var peUt = fall.Orbit.NextPeriapsis(fall.StartUt) ?? fall.StartUt;

            // The windows as seen from the planet's low orbit, from when the craft can be there.
            string problemError;
            var view = FromLowOrbit(snapshot, planet, peUt);
            var problem = TransferWindows.Problem.Of(view, target, targetPe, capture, out problemError);
            if (problem == null) return Fail(problemError);
            var found = TransferWindows.Search(problem, snapshot.MarginPercent);
            if (found.Error != null) return Fail(found.Error);
            var window = TransferWindows.Pick(found, snapshot.TimeVersusDeltaV);
            var leaving = problem.Leaving(window.DepartureUt, window.ArrivalUt - window.DepartureUt);
            if (!leaving.HasValue) return Fail("the window's arc has no Lambert path");

            var price = new DirectRoutePrice();
            Legs(planet.Gm, fall.Orbit.PeR, SpeedAtPe(fall.Orbit), leaving.Value.Length, price);
            // Its ejection as transfer_to's node too, from the low orbit in the fall's plane, so the two rows pay the asymptote's tilt alike.
            var lowR = DeltaVMap.LowOrbit(planet);
            var lowOrbit = Conic.FromState(planet.Gm, fall.Orbit.P * lowR, fall.Orbit.Q * Math.Sqrt(planet.Gm / lowR), window.DepartureUt);
            double twoStepEject;
            var twoStepBurn = SiblingEjection.Oberth(lowOrbit, planet.Gm, leaving.Value, window.DepartureUt, peUt, out twoStepEject);
            var twoStep = new RouteRow
            {
                DepartUt = home.Ut, ReturnWindow = now, ReturnDv = home.DeltaV, PlanetCaptureDv = price.PlanetCaptureDv, EjectUt = twoStepEject, EjectWindow = window.DepartureUt,
                EjectionDv = twoStepBurn.Length, CaptureDv = window.CaptureDv, ArrivalUt = twoStepEject + window.ArrivalUt - window.DepartureUt, ArrivalVInf = window.ArrivalVInf,
            };
            twoStep.TotalDv = twoStep.ReturnDv + twoStep.PlanetCaptureDv + twoStep.EjectionDv + twoStep.CaptureDv;

            string directError;
            var direct = Direct(system, moon, planet, craft, now, peAltitude, problem, window, home, fall, out directError);
            var plan = new DirectRoutePlan
            {
                Target = found.Target, Planet = planet.Name, ReturnPe = peAltitude, TargetPe = targetPe,
                WindowName = window == found.Optimal && window != found.Soonest ? "optimal" : "soonest", VInf = leaving.Value.Length,
                TwoStep = twoStep, Direct = direct, DirectError = directError, CraftDv = found.CraftDv, LaterLegsDv = found.LaterLegsDv,
            };
            plan.Pick = direct == null ? twoStep : Pick(direct, twoStep, snapshot.TimeVersusDeltaV);
            return plan;
        }

        /// <summary>
        /// The direct route: the moon's place at the burn sets where the fall's periapsis is, turned by a fixed angle (<paramref name="home"/>'s
        /// return measures it), and the window's asymptote sets where the ejection wants it; the burn is at the moon's pass that lines
        /// them up nearest the window. Then the return solved there, and the ejection from the fall it gives, as transfer_to flies it.
        /// </summary>
        private static RouteRow Direct(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic craft, double now, double peAltitude,
            TransferWindows.Problem problem, TransferWindow window, ReturnPlan home, Patch fall, out string error)
        {
            error = null;
            var moonOrbit = system.OrbitOf(moon);
            var tof = window.ArrivalUt - window.DepartureUt;
            var plan = home;
            var flown = fall;
            var burnUt = double.NaN;
            var earliest = now;
            // A second pass from the first's own solve: the burn's place in the craft's orbit moves the fall a little.
            for (var pass = 0; pass < AlignPasses; pass++)
            {
                var orbit = flown.Orbit;
                Func<Vec3, double> angle = x => Math.Atan2(Vec3.Dot(x, orbit.Q), Vec3.Dot(x, orbit.P));
                // The fall's periapsis is at angle 0: turned from the moon's place at the burn by -angle(moon then).
                var turned = -angle(moonOrbit.Position(plan.Ut));
                var falling = (orbit.NextPeriapsis(flown.StartUt) ?? flown.StartUt) - plan.Ut;
                var sense = Vec3.Dot(moonOrbit.W, orbit.W) < 0 ? -1 : 1;
                if (pass == 0) burnUt = window.DepartureUt - falling;
                // Where the ejection wants the periapsis, with the asymptote of that burn's own ejection time.
                for (var i = 0; i < AlignSteps; i++)
                {
                    var moved = TransferWindows.CheapestAt(problem, burnUt + falling, tof);
                    var towards = problem.Leaving(moved.DepartureUt, moved.ArrivalUt - moved.DepartureUt);
                    if (!towards.HasValue) break;
                    var wanted = angle(SiblingEjection.BurnPoint(orbit.W, towards.Value, orbit.PeR, planet.Gm));
                    burnUt += Wrap(wanted - turned - angle(moonOrbit.Position(burnUt))) / (sense * moonOrbit.N);
                }
                // The window from now is at or after the fall from now, but the moon's pass may fall before now: its next one.
                while (burnUt < now) burnUt += moonOrbit.Period;

                earliest = Math.Max(now, burnUt - craft.Period / 2);
                plan = ReturnBurn.Find(system, moon, craft, now, new ReturnAsk { TargetPe = peAltitude, EarliestUt = earliest });
                if (plan.Error != null)
                {
                    error = plan.Error;
                    return null;
                }
                flown = Arrival(system, moon, planet, craft, plan);
                if (flown == null)
                {
                    error = "the return at UT " + Num(plan.Ut) + " does not reach " + planet.Name;
                    return null;
                }
            }

            // The window re-costed at the fall's periapsis, the ejection from it as transfer_to's node.
            var peUt = flown.Orbit.NextPeriapsis(flown.StartUt) ?? flown.StartUt;
            var row = TransferWindows.CheapestAt(problem, peUt, tof);
            var away = problem.Leaving(row.DepartureUt, row.ArrivalUt - row.DepartureUt);
            if (!away.HasValue)
            {
                error = "the window's arc at UT " + Num(peUt) + " has no Lambert path";
                return null;
            }
            double ejectUt;
            var burn = SiblingEjection.Oberth(flown.Orbit, planet.Gm, away.Value, peUt, flown.StartUt, out ejectUt);
            var result = new RouteRow
            {
                DepartUt = plan.Ut, ReturnWindow = earliest, ReturnDv = plan.DeltaV, EjectUt = ejectUt, EjectWindow = ejectUt - flown.Orbit.Period / 2, EjectionDv = burn.Length,
                CaptureDv = row.CaptureDv, ArrivalUt = ejectUt + row.ArrivalUt - row.DepartureUt, ArrivalVInf = row.ArrivalVInf,
            };
            result.TotalDv = result.ReturnDv + result.EjectionDv + result.CaptureDv;
            return result;
        }

        /// <summary>Lining the fall's periapsis up: the steps of a pass (the moon's elliptical motion is close to linear in the burn's time, the asymptote slow) and the solves of the return it takes.</summary>
        private const int AlignSteps = 4;
        private const int AlignPasses = 2;

        /// <summary>The first patch round the planet after <paramref name="plan"/>'s burn.</summary>
        private static Patch Arrival(BodySystem system, SnapshotBody moon, SnapshotBody planet, Conic craft, ReturnPlan plan)
        {
            return SingleShooting.Arrive(system, moon, craft, plan.Ut, SingleShooting.FromNodeFrame(craft, plan.Ut, plan.Prograde, plan.Normal, plan.Radial), planet, 2);
        }

        private static double SpeedAtPe(Conic orbit)
        {
            return Math.Sqrt(orbit.Mu * (2 / orbit.PeR - 1 / orbit.A));
        }

        /// <summary>The Game Snapshot as if the craft were in <paramref name="planet"/>'s low circular orbit at <paramref name="ut"/>: the windows' view of the two-step route.</summary>
        private static GameSnapshot FromLowOrbit(GameSnapshot snapshot, SnapshotBody planet, double ut)
        {
            var low = DeltaVMap.LowOrbit(planet);
            var view = new GameSnapshot
            {
                Ut = ut, Situation = "ORBITING", Body = planet.Name, TimeVersusDeltaV = snapshot.TimeVersusDeltaV, MarginPercent = snapshot.MarginPercent,
                LaterLegsDv = snapshot.LaterLegsDv, DeltaVSimulator = snapshot.DeltaVSimulator, Bodies = snapshot.Bodies, Stages = snapshot.Stages,
                Orbit = new SnapshotOrbit { SemiMajorAxis = low, Eccentricity = 0, Body = planet.Name, Periapsis = low - planet.Radius, Apoapsis = low - planet.Radius, Epoch = ut },
            };
            return view;
        }

        private static double Wrap(double a)
        {
            a = (a + Math.PI) % (2 * Math.PI);
            if (a < 0) a += 2 * Math.PI;
            return a - Math.PI;
        }

        private static DirectRoutePlan Fail(string why)
        {
            return new DirectRoutePlan { Error = why };
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
