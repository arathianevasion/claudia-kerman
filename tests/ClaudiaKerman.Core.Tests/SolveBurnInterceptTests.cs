// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// <c>solve_burn</c>'s <c>intercept_at</c> goal (#451) through the Planning Tools entry point, on hand-built Game Snapshots with
    /// the stock Kerbin system's constants. Each arc is checked two ways that don't use the solver: the closed form where there is
    /// one (the Hohmann transfer), and by flying the burn it returns, a Kepler propagation from the craft's state to see how far
    /// from the target it arrives.
    /// </summary>
    public sealed class SolveBurnInterceptTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double MunSma = 12000000;
        private const double ParkingSma = KerbinR + 100000;
        private const double BurnUt = 10;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>A craft on a circular parking orbit at UT 0, with the Mun placed by its mean anomaly at epoch (radians).</summary>
        private static GameSnapshot Snapshot(double munPhase, double inclination = 0, double munInclination = 0)
        {
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = "Kerbin" };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = ParkingSma, Eccentricity = 0, Inclination = inclination, MeanAnomalyAtEpoch = 0, Epoch = 0, Body = "Kerbin",
                Periapsis = 100000, Apoapsis = 100000,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = 21549.425, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = 6.5138398e10, Radius = 200000, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = MunSma,
                Inclination = munInclination, MeanAnomalyAtEpoch = munPhase,
            });
            return snapshot;
        }

        private static double MeanMotion(double sma) { return Math.Sqrt(KerbinGm / (sma * sma * sma)); }

        private static double HohmannFlight()
        {
            var a = (ParkingSma + MunSma) / 2;
            return Math.PI * Math.Sqrt(a * a * a / KerbinGm);
        }

        /// <summary>A Mun phase that puts it <paramref name="ahead"/> radians past the craft's position when the craft arrives <paramref name="flight"/> s after the burn.</summary>
        private static double PhaseFor(double flight, double ahead)
        {
            return MeanMotion(ParkingSma) * BurnUt + ahead - MeanMotion(MunSma) * (BurnUt + flight);
        }

        private static string R(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

        private JsonElement Run(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.False(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.Clone();
        }

        private string Error(GameSnapshot snapshot, string input)
        {
            var outcome = _tools.Run("solve_burn", input, snapshot, new NoPlanningBody());
            Assert.True(outcome.IsError, outcome.Json);
            using (var doc = JsonDocument.Parse(outcome.Json)) return doc.RootElement.GetProperty("error").GetString();
        }

        /// <summary>
        /// How far an arrival may miss from the result's components being rounded to 0.01 m/s: a velocity error grows to several
        /// times its size in distance over the flight (0.05 m/s a second of flight), still a few km in a 12,000 km orbit.
        /// </summary>
        private static double Rounding(double flight) { return 0.05 * flight; }

        private static double Component(JsonElement result, string name) { return result.GetProperty("dv").GetProperty(name).GetDouble(); }

        /// <summary>
        /// How far from the Mun's centre the burn in <paramref name="result"/> arrives <paramref name="flight"/> s later: the node's
        /// components turned into a velocity change (prograde along v, normal along r x v, radial out) and flown as a Kepler orbit.
        /// </summary>
        private static double Miss(GameSnapshot snapshot, JsonElement result, double flight)
        {
            var craft = Conic.FromElements(KerbinGm, ParkingSma, 0, snapshot.Orbit.Inclination, 0, 0, 0, 0);
            Vec3 r, v;
            craft.State(BurnUt, out r, out v);
            var along = v.Normalized;
            var normal = Vec3.Cross(r, v).Normalized;
            var radial = Vec3.Cross(along, normal);
            var after = v + along * Component(result, "prograde") + normal * Component(result, "normal") + radial * Component(result, "radial");
            var arrive = Conic.FromState(KerbinGm, r, after, BurnUt).Position(BurnUt + flight);
            var b = snapshot.Bodies[2];
            var mun = Conic.FromElements(KerbinGm, b.Sma, b.Eccentricity, b.Inclination, b.Lan, b.ArgumentOfPeriapsis, b.MeanAnomalyAtEpoch, 0);
            return (arrive - mun.Position(BurnUt + flight)).Length;
        }

        [Fact]
        public void A_Hohmann_departure_is_recovered_with_the_Hohmann_flight_time()
        {
            var flight = HohmannFlight();
            var snapshot = Snapshot(PhaseFor(flight, Math.PI));
            var result = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + "}");

            var a = (ParkingSma + MunSma) / 2;
            var circular = Math.Sqrt(KerbinGm / ParkingSma);
            Assert.Equal(Math.Sqrt(KerbinGm * (2 / ParkingSma - 1 / a)) - circular, Component(result, "prograde"), 0);
            Assert.Equal(0, Component(result, "normal"), 1);
            Assert.Equal(0, Component(result, "radial"), 1);
            Assert.Equal("intercept_at", result.GetProperty("goal").GetString());
            Assert.InRange(result.GetProperty("achieved").GetProperty("sma").GetDouble(), a - 10, a + 10);
            Assert.True(Miss(snapshot, result, flight) < Rounding(flight), "misses the Mun by " + Miss(snapshot, result, flight) + " m");

            // At the far end the craft is at the apoapsis of the transfer; the Mun moves on a circle, so the burn to match it is the
            // difference of the speeds.
            var arrival = result.GetProperty("arrival");
            var match = Math.Sqrt(KerbinGm / MunSma) - Math.Sqrt(KerbinGm * (2 / MunSma - 1 / a));
            Assert.Equal(match, arrival.GetProperty("relative_speed").GetDouble(), 1);
            Assert.Equal(match, arrival.GetProperty("match_dv").GetProperty("magnitude").GetDouble(), 1);
            Assert.Equal(match, arrival.GetProperty("match_dv").GetProperty("prograde").GetDouble(), 1);
            Assert.Equal(BurnUt + flight, arrival.GetProperty("at_ut").GetDouble(), 0);
            Assert.Equal(flight, arrival.GetProperty("flight_time").GetDouble(), 0);
        }

        [Fact]
        public void An_arrive_ut_is_the_same_arc_as_its_flight_time()
        {
            var flight = HohmannFlight();
            var snapshot = Snapshot(PhaseFor(flight, Math.PI));
            var byTof = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + "}");
            var byUt = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"arrive_ut\":" + R(BurnUt + flight) + "}");
            Assert.Equal(Component(byTof, "prograde"), Component(byUt, "prograde"), 6);
            Assert.Equal(byTof.GetProperty("magnitude").GetDouble(), byUt.GetProperty("magnitude").GetDouble(), 6);
        }

        [Fact]
        public void A_retrograde_arc_goes_round_against_the_craft_and_reaches_the_target()
        {
            var flight = 60000.0;
            var snapshot = Snapshot(PhaseFor(flight, 1.0));
            var result = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + ",\"retrograde\":true}");
            // Reversing the craft's motion takes more than its whole speed.
            Assert.True(Component(result, "prograde") < -Math.Sqrt(KerbinGm / ParkingSma), "prograde " + Component(result, "prograde"));
            Assert.True(Miss(snapshot, result, flight) < Rounding(flight), "misses the Mun by " + Miss(snapshot, result, flight) + " m");

            var forward = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + "}");
            Assert.True(Component(forward, "prograde") > -Math.Sqrt(KerbinGm / ParkingSma), "prograde " + Component(forward, "prograde"));
            Assert.True(Miss(snapshot, forward, flight) < Rounding(flight));
        }

        [Fact]
        public void A_one_revolution_arc_reaches_the_target_and_is_a_different_burn()
        {
            var flight = 90000.0;
            var snapshot = Snapshot(PhaseFor(flight, 0.7));
            var one = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + ",\"revolutions\":1}");
            var none = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + ",\"revolutions\":0}");
            Assert.True(Miss(snapshot, one, flight) < Rounding(flight), "misses the Mun by " + Miss(snapshot, one, flight) + " m");
            Assert.True(Miss(snapshot, none, flight) < Rounding(flight));
            Assert.NotEqual(Math.Round(one.GetProperty("magnitude").GetDouble()), Math.Round(none.GetProperty("magnitude").GetDouble()));
        }

        [Fact]
        public void An_arc_to_an_inclined_target_has_a_normal_component()
        {
            var flight = HohmannFlight();
            var snapshot = Snapshot(PhaseFor(flight, 2.5), munInclination: 10);
            var result = Run(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + "}");
            Assert.NotEqual(0, Math.Round(Component(result, "normal")));
            Assert.True(Miss(snapshot, result, flight) < Rounding(flight), "misses the Mun by " + Miss(snapshot, result, flight) + " m");
        }

        [Fact]
        public void A_flight_time_too_short_for_the_revolutions_is_an_error_with_the_quickest_time()
        {
            var snapshot = Snapshot(1);
            var error = Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in 10\",\"tof\":2000,\"revolutions\":1}");
            Assert.Contains("no path makes 1 revolution in 2000 s", error);
            Assert.Contains("the quickest takes", error);
        }

        [Fact]
        public void A_target_nearly_straight_across_and_off_the_craft_plane_is_a_180_degree_error()
        {
            // The craft is inclined and at its ascending node at the burn, so the equatorial Mun is in the equator with it; a hair past
            // straight across, the transfer's plane would be set by that hair.
            var flight = HohmannFlight();
            var snapshot = Snapshot(0, inclination: 10);
            snapshot.Orbit.MeanAnomalyAtEpoch = -MeanMotion(ParkingSma) * BurnUt;
            snapshot.Bodies[2].MeanAnomalyAtEpoch = Math.PI + 0.0002 - MeanMotion(MunSma) * (BurnUt + flight);
            var error = Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + "}");
            Assert.Contains("nearly 180 degrees", error);
        }

        [Fact]
        public void Bad_fields_are_errors_that_say_why()
        {
            var snapshot = Snapshot(1);
            Assert.Contains("needs target", Error(snapshot, "{\"goal\":\"intercept_at\",\"tof\":1000}"));
            Assert.Contains("arrive_ut", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\"}"));
            Assert.Contains("one of them", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1000,\"arrive_ut\":2000}"));
            Assert.Contains("above 0", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":-5}"));
            Assert.Contains("above 0", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":0}"));
            Assert.Contains("finite", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1e999}"));
            Assert.Contains("finite", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"arrive_ut\":1e999}"));
            Assert.Contains("not after the burn", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in 100\",\"arrive_ut\":50}"));
            Assert.Contains("whole number", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1000,\"revolutions\":1.5}"));
            Assert.Contains("revolutions is from 0", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1000,\"revolutions\":-1}"));
            Assert.Contains("true or false", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1000,\"retrograde\":\"yes\"}"));
            Assert.Contains("takes no value", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"tof\":1000,\"value\":3}"));
            Assert.Contains("not in orbit round Kerbin", Error(snapshot, "{\"goal\":\"intercept_at\",\"target\":\"Sun\",\"tof\":1000}"));
        }

        [Fact]
        public void The_new_fields_belong_to_intercept_at_alone()
        {
            var snapshot = Snapshot(1);
            Assert.Contains("for intercept_at only", Error(snapshot, "{\"goal\":\"circularize\",\"at\":\"pe\",\"tof\":1000}"));
            Assert.Contains("for intercept_at only", Error(snapshot, "{\"goal\":\"set_ap\",\"value\":200000,\"retrograde\":false}"));
            Assert.Contains("for intercept_at only", Error(snapshot, "{\"goal\":\"set_pe\",\"value\":100000,\"revolutions\":0}"));
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void It_solves_in_milliseconds()
        {
            var flight = HohmannFlight();
            var snapshot = Snapshot(PhaseFor(flight, 1.2));
            var input = "{\"goal\":\"intercept_at\",\"target\":\"Mun\",\"at\":\"in " + BurnUt + "\",\"tof\":" + R(flight) + ",\"revolutions\":0}";
            // One Lambert solve and predict_burn's propagation of the answer; the budget is the 20 ms the other solve_burn goals keep to.
            Timing.Within(20, () => Assert.False(_tools.Run("solve_burn", input, snapshot, new NoPlanningBody()).IsError));
        }
    }
}
