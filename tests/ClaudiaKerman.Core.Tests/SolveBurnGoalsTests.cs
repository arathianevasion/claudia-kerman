// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The orbit-change goals of <c>solve_burn</c> (#443) through the Planning Tools entry point, on hand-built Game Snapshots
    /// with the stock Kerbin system's constants: each against first principles (vis-viva, the plane-change formula
    /// 2 v sin(Δi/2), Kepler's third law), with the element it set read back from the propagated orbit.
    /// </summary>
    public sealed class SolveBurnGoalsTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double KerbinDay = 21549.425;
        private const double MunGm = 6.5138398e10;
        private const double MunR = 200000;

        /// <summary>
        /// A mean anomaly just past the periapsis. A craft exactly at it finds the next one a whole orbit on, which floating
        /// point can put a hair past the end of the predicted stable patch (one orbit long; #442's notes).
        /// </summary>
        private const double JustPast = 0.01;

        private readonly PlanningTools _tools = PlanningTools.Default();

        /// <summary>The craft round <paramref name="body"/> between the two altitudes, at its periapsis at UT 0 unless <paramref name="phase"/> says otherwise.</summary>
        private static GameSnapshot Snapshot(double pe = 100000, double? ap = null, double inclination = 0, double lan = 0, double argPe = 0,
            double phase = 0, string body = "Kerbin")
        {
            var radius = body == "Mun" ? MunR : KerbinR;
            double rp = radius + pe, ra = radius + (ap ?? pe);
            var snapshot = new GameSnapshot { Ut = 0, Situation = "ORBITING", Body = body };
            snapshot.Orbit = new SnapshotOrbit
            {
                SemiMajorAxis = (rp + ra) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = inclination, Lan = lan, ArgumentOfPeriapsis = argPe,
                MeanAnomalyAtEpoch = phase, Epoch = 0, Body = body, Periapsis = pe, Apoapsis = ap ?? pe,
            };
            snapshot.Bodies.Add(new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000, RotationPeriod = 432000 });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SeaLevelAtm = 1, SoiRadius = 84159286.48,
                RotationPeriod = KerbinDay, Sma = 13599840256, MeanAnomalyAtEpoch = 3.14,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = MunR, SoiRadius = 2429559.1, RotationPeriod = 138984.38, Sma = 12000000, MeanAnomalyAtEpoch = 1,
            });
            snapshot.Bodies.Add(new SnapshotBody
            {
                Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, RotationPeriod = 40400, Sma = 47000000,
                Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
            });
            return snapshot;
        }

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

        private static double Prograde(JsonElement result) { return result.GetProperty("dv").GetProperty("prograde").GetDouble(); }
        private static double Magnitude(JsonElement result) { return result.GetProperty("magnitude").GetDouble(); }
        private static double Achieved(JsonElement result, string name) { return result.GetProperty("achieved").GetProperty(name).GetDouble(); }
        private static JsonElement Patch(JsonElement result) { return result.GetProperty("predict_burn").GetProperty("patches")[0]; }

        /// <summary>The craft's state at <paramref name="ut"/> on the snapshot's orbit, as the tools build it.</summary>
        private static Conic Orbit(GameSnapshot snapshot)
        {
            var o = snapshot.Orbit;
            var gm = snapshot.Bodies.Single(b => b.Name == o.Body).Gm;
            return Conic.FromElements(gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static double VisViva(double mu, double r, double a)
        {
            return Math.Sqrt(mu * (2 / r - 1 / a));
        }

        private static string R(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        // ---- set_sma ----

        [Fact]
        public void Set_sma_on_a_circular_Kerbin_orbit_burns_the_vis_viva_difference_at_the_periapsis()
        {
            var rp = KerbinR + 100000;
            var result = Run(Snapshot(), "{\"goal\":\"set_sma\",\"value\":1000000}");
            Assert.Equal(VisViva(KerbinGm, rp, 1000000) - Math.Sqrt(KerbinGm / rp), Prograde(result), 2);
            Assert.Equal(0, result.GetProperty("dv").GetProperty("normal").GetDouble());
            Assert.Equal(0, result.GetProperty("dv").GetProperty("radial").GetDouble());
            Assert.Equal(1000000, Achieved(result, "sma"), 0);
            Assert.Equal(2 * 1000000 - rp - KerbinR, Patch(result).GetProperty("ap").GetDouble(), 0);
        }

        [Fact]
        public void Set_sma_on_an_eccentric_orbit_at_the_apoapsis_lowers_it_by_vis_viva()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 500000;
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"set_sma\",\"value\":800000,\"at\":\"ap\"}");
            Assert.Equal(VisViva(KerbinGm, ra, 800000) - VisViva(KerbinGm, ra, (rp + ra) / 2), Prograde(result), 2);
            Assert.True(Prograde(result) < 0);
            Assert.Equal(800000, Achieved(result, "sma"), 0);
            Assert.Equal(500000, Patch(result).GetProperty("ap").GetDouble(), 0);
        }

        [Fact]
        public void Set_sma_round_the_Mun_uses_the_Mun()
        {
            var rp = MunR + 30000;
            var result = Run(Snapshot(30000, phase: JustPast, body: "Mun"), "{\"goal\":\"set_sma\",\"value\":400000}");
            Assert.Equal(VisViva(MunGm, rp, 400000) - Math.Sqrt(MunGm / rp), Prograde(result), 2);
            Assert.Equal(400000, Achieved(result, "sma"), 0);
            Assert.Equal("Mun", Patch(result).GetProperty("body").GetString());
        }

        [Fact]
        public void An_sma_under_half_the_radius_is_out_of_reach()
        {
            Assert.Contains("under half the burn's radius", Error(Snapshot(), "{\"goal\":\"set_sma\",\"value\":300000}"));
            Assert.Contains("above 0", Error(Snapshot(), "{\"goal\":\"set_sma\",\"value\":-5}"));
        }

        // ---- set_ecc ----

        [Fact]
        public void Set_ecc_at_the_periapsis_of_a_circle_burns_to_the_speed_for_that_eccentricity()
        {
            // At an apsis the burn keeps it one: v² = μ(1 + e)/r makes r the periapsis of an orbit of eccentricity e.
            var rp = KerbinR + 100000;
            var result = Run(Snapshot(), "{\"goal\":\"set_ecc\",\"value\":0.1}");
            Assert.Equal(Math.Sqrt(KerbinGm * 1.1 / rp) - Math.Sqrt(KerbinGm / rp), Prograde(result), 2);
            Assert.Equal(0.1, Achieved(result, "eccentricity"), 6);
        }

        [Fact]
        public void Set_ecc_to_zero_at_the_apoapsis_is_circularizing_there()
        {
            double rp = KerbinR + 100000, ra = KerbinR + 500000;
            var result = Run(Snapshot(100000, 500000), "{\"goal\":\"set_ecc\",\"value\":0,\"at\":\"ap\"}");
            Assert.Equal(Math.Sqrt(KerbinGm / ra) - VisViva(KerbinGm, ra, (rp + ra) / 2), Prograde(result), 2);
            Assert.Equal(0, Achieved(result, "eccentricity"), 5);
        }

        [Fact]
        public void Set_ecc_round_the_Mun_between_the_apsides_reaches_the_eccentricity_with_a_prograde_burn()
        {
            var snapshot = Snapshot(30000, 300000, body: "Mun");
            var result = Run(snapshot, "{\"goal\":\"set_ecc\",\"value\":0.5,\"at\":\"in 600\"}");
            Assert.Equal(0.5, Achieved(result, "eccentricity"), 6);
            Assert.Equal(Math.Abs(Prograde(result)), Magnitude(result), 2);
            Assert.Equal(0.5, Patch(result).GetProperty("eccentricity").GetDouble(), 4);
        }

        [Fact]
        public void Set_ecc_below_what_the_flight_path_angle_allows_says_why()
        {
            // Climbing at angle γ, a burn along the velocity can only bring the eccentricity down to |sin γ|.
            Assert.Contains("lowest eccentricity", Error(Snapshot(100000, 2000000), "{\"goal\":\"set_ecc\",\"value\":0,\"at\":\"in 600\"}"));
            Assert.Contains("0 or more", Error(Snapshot(), "{\"goal\":\"set_ecc\",\"value\":-0.1}"));
        }

        // ---- ellipticize ----

        [Fact]
        public void Ellipticize_reaches_the_pe_and_ap_through_the_burn_point_with_the_vis_viva_velocity()
        {
            double r = KerbinR + 100000, rp = KerbinR + 80000, ra = KerbinR + 300000, a = (rp + ra) / 2;
            var result = Run(Snapshot(), "{\"goal\":\"ellipticize\",\"pe\":80000,\"ap\":300000,\"at\":\"in 60\"}");
            // The transverse speed is h/r with h² = μp; the rest of the vis-viva speed is radial.
            var h = Math.Sqrt(KerbinGm * 2 * rp * ra / (rp + ra));
            double vt = h / r, speed = VisViva(KerbinGm, r, a), vr = Math.Sqrt(speed * speed - vt * vt), v0 = Math.Sqrt(KerbinGm / r);
            Assert.Equal(Math.Sqrt((vt - v0) * (vt - v0) + vr * vr), Magnitude(result), 2);
            Assert.Equal(80000, Achieved(result, "pe"), 0);
            Assert.Equal(300000, Achieved(result, "ap"), 0);
            Assert.NotEqual(0, result.GetProperty("dv").GetProperty("radial").GetDouble());
        }

        [Fact]
        public void Ellipticize_round_the_Mun_on_an_eccentric_orbit()
        {
            var result = Run(Snapshot(30000, 200000, body: "Mun"), "{\"goal\":\"ellipticize\",\"pe\":20000,\"ap\":500000,\"at\":\"in 900\"}");
            Assert.Equal(20000, Achieved(result, "pe"), 0);
            Assert.Equal(500000, Achieved(result, "ap"), 0);
        }

        [Fact]
        public void Ellipticize_needs_the_burn_altitude_between_its_pe_and_ap()
        {
            Assert.Contains("outside the Pe", Error(Snapshot(), "{\"goal\":\"ellipticize\",\"pe\":200000,\"ap\":300000}"));
            Assert.Contains("needs pe and ap", Error(Snapshot(), "{\"goal\":\"ellipticize\",\"pe\":80000}"));
            Assert.Contains("above its ap", Error(Snapshot(), "{\"goal\":\"ellipticize\",\"pe\":300000,\"ap\":80000}"));
            Assert.Contains("ellipticize only", Error(Snapshot(), "{\"goal\":\"set_ap\",\"value\":300000,\"pe\":80000}"));
        }

        // ---- set_inc ----

        [Fact]
        public void Set_inc_from_an_equatorial_circle_costs_two_v_sin_half_the_change()
        {
            var r = KerbinR + 100000;
            var result = Run(Snapshot(), "{\"goal\":\"set_inc\",\"value\":10}");
            Assert.Equal(2 * Math.Sqrt(KerbinGm / r) * Math.Sin(5 * Math.PI / 180), Magnitude(result), 2);
            Assert.Equal(10, Achieved(result, "inclination"), 3);
            Assert.Equal(10, Patch(result).GetProperty("inclination").GetDouble(), 2);
        }

        [Fact]
        public void Set_inc_on_an_inclined_eccentric_orbit_burns_at_the_higher_equatorial_node()
        {
            // Pe 30° past the ascending node: the AN is at true anomaly -30°, the DN at 150°, nearer the Ap and slower.
            double rp = KerbinR + 100000, ra = KerbinR + 500000, a = (rp + ra) / 2, e = (ra - rp) / (ra + rp);
            var snapshot = Snapshot(100000, 500000, inclination: 20, argPe: 30);
            var result = Run(snapshot, "{\"goal\":\"set_inc\",\"value\":5}");
            var nu = 150 * Math.PI / 180;
            var p = a * (1 - e * e);
            // The horizontal speed is h/r = sqrt(μp)/r; the turn keeps the vertical speed.
            var horizontal = Math.Sqrt(KerbinGm / p) * (1 + e * Math.Cos(nu));
            Assert.Equal(2 * horizontal * Math.Sin(7.5 * Math.PI / 180), Magnitude(result), 2);
            Assert.Equal(5, Achieved(result, "inclination"), 3);
            Assert.InRange(Orbit(snapshot).Radius(result.GetProperty("at_ut").GetDouble()) - p / (1 + e * Math.Cos(nu)), -100, 100);
        }

        private double AtUt(GameSnapshot snapshot, string input) { return Run(snapshot, input).GetProperty("at_ut").GetDouble(); }

        [Fact]
        public void Set_inc_default_on_a_near_circular_orbit_takes_the_nearer_node_not_the_higher()
        {
            // 100 x 100.2 km (e about 0.00017) at 2°, Pe 30° past the AN: the DN, at 150°, is nearer; the AN is higher by about 100 m.
            var snapshot = Snapshot(100000, 100200, inclination: 2, argPe: 30, phase: JustPast);
            var dn = AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":0,\"at\":\"eq_dn\"}");
            var an = AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":0,\"at\":\"eq_an\"}");
            Assert.True(dn < an);
            Assert.Equal(dn, AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":0}"), 3);
            Assert.Equal(dn, AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":0,\"at\":\"eq_nearest\"}"), 3);
            // The named references mean what they did: eq_highest is still the slower node, nearer or not.
            Assert.Contains(AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":0,\"at\":\"eq_highest\"}"), new[] { an, dn });
        }

        [Fact]
        public void Set_inc_default_on_an_eccentric_orbit_still_takes_the_cheaper_farther_node()
        {
            // 100 x 3500 km at 20°, Pe 30° before the AN: the AN is near the Pe and fast, the DN near the Ap and slow.
            var snapshot = Snapshot(100000, 3500000, inclination: 20, argPe: 330, phase: JustPast);
            var an = AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"eq_an\"}");
            var dn = AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"eq_dn\"}");
            Assert.True(an < dn);
            Assert.Equal(dn, AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":10}"), 3);
            Assert.Equal(dn, AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"eq_highest\"}"), 3);
            Assert.Equal(an, AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"eq_nearest\"}"), 3);
        }

        [Fact]
        public void Set_inc_default_takes_the_farther_node_only_at_the_saving_threshold()
        {
            Assert.Equal(10, SolveBurn.PickNode(10, 50, 20, 50 - SolveBurn.FartherNodeSaving + 0.001), 6);
            Assert.Equal(20, SolveBurn.PickNode(10, 50, 20, 50 - SolveBurn.FartherNodeSaving), 6);
            Assert.Equal(20, SolveBurn.PickNode(20, 50 - SolveBurn.FartherNodeSaving - 0.001, 10, 50), 6);
            Assert.Equal(10, SolveBurn.PickNode(20, 60, 10, 50), 6);
        }

        [Fact]
        public void Set_inc_default_on_an_equatorial_orbit_still_burns_at_the_apoapsis()
        {
            var snapshot = Snapshot(100000, 300000, phase: JustPast);
            Assert.Equal(AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":5,\"at\":\"ap\"}"), AtUt(snapshot, "{\"goal\":\"set_inc\",\"value\":5}"), 3);
        }

        [Fact]
        public void Set_inc_round_the_Mun_flattens_an_inclined_orbit()
        {
            var r = MunR + 30000;
            var result = Run(Snapshot(30000, inclination: 12, body: "Mun"), "{\"goal\":\"set_inc\",\"value\":0}");
            Assert.Equal(2 * Math.Sqrt(MunGm / r) * Math.Sin(6 * Math.PI / 180), Magnitude(result), 2);
            Assert.Equal(0, Achieved(result, "inclination"), 3);
        }

        [Fact]
        public void Set_inc_on_an_escape_or_at_too_high_a_latitude_says_why()
        {
            var escape = Snapshot();
            escape.Orbit.SemiMajorAxis = -2000000;
            escape.Orbit.Eccentricity = 1.35;
            Assert.Contains("escape", Error(escape, "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"in 10\"}"));
            // A 60° circle reaches latitude 60° a quarter of an orbit after its ascending node; no 10° orbit passes there.
            var r = KerbinR + 100000;
            var quarter = Math.PI / 2 * Math.Sqrt(r * r * r / KerbinGm);
            Assert.Contains("latitude 60", Error(Snapshot(inclination: 60), "{\"goal\":\"set_inc\",\"value\":10,\"at\":\"in " + R(quarter) + "\"}"));
            Assert.Contains("0 to 180", Error(Snapshot(), "{\"goal\":\"set_inc\",\"value\":190}"));
        }

        // ---- match_plane ----

        [Fact]
        public void Match_plane_to_Minmus_from_an_equatorial_100_km_orbit_costs_the_textbook_figure_on_a_relative_node()
        {
            var r = KerbinR + 100000;
            var snapshot = Snapshot();
            var result = Run(snapshot, "{\"goal\":\"match_plane\",\"target\":\"Minmus\"}");
            var textbook = 2 * Math.Sqrt(KerbinGm / r) * Math.Sin(3 * Math.PI / 180);
            Assert.InRange(Magnitude(result), textbook * 0.98, textbook * 1.02);
            Assert.InRange(Achieved(result, "relative_inclination"), 0, 0.01);
            // On a relative node: the craft is in Minmus's orbit plane then.
            var minmus = Conic.FromElements(KerbinGm, 47000000, 0, 6, 78, 38, 0.9, 0);
            var at = Orbit(snapshot).Position(result.GetProperty("at_ut").GetDouble());
            Assert.InRange(Math.Abs(Vec3.Dot(at.Normalized, minmus.W)), 0, 1e-3);
            // Turning the velocity through 6° keeps its size: the prograde part is -v(1 - cos 6°).
            Assert.Equal(-Math.Sqrt(KerbinGm / r) * (1 - Math.Cos(6 * Math.PI / 180)), Prograde(result), 1);
        }

        [Fact]
        public void Match_plane_picks_the_slower_node_by_default_and_takes_either_when_asked()
        {
            var snapshot = Snapshot(100000, 1500000, argPe: 40);
            var an = Run(snapshot, "{\"goal\":\"match_plane\",\"target\":\"Minmus\",\"at\":\"rel_an\"}");
            var dn = Run(snapshot, "{\"goal\":\"match_plane\",\"target\":\"Minmus\",\"at\":\"rel_dn\"}");
            var cheaper = Run(snapshot, "{\"goal\":\"match_plane\",\"target\":\"Minmus\"}");
            Assert.Equal(Math.Min(Magnitude(an), Magnitude(dn)), Magnitude(cheaper), 2);
            Assert.NotEqual(Magnitude(an), Magnitude(dn), 0);
            foreach (var result in new[] { an, dn, cheaper }) Assert.InRange(Achieved(result, "relative_inclination"), 0, 0.01);
        }

        [Fact]
        public void Match_plane_needs_a_target_in_the_same_sphere_of_influence_and_a_relative_node()
        {
            Assert.Contains("needs target", Error(Snapshot(), "{\"goal\":\"match_plane\"}"));
            Assert.Contains("not in orbit round Mun", Error(Snapshot(30000, body: "Mun"), "{\"goal\":\"match_plane\",\"target\":\"Minmus\"}"));
            Assert.Contains("rel_an or rel_dn", Error(Snapshot(), "{\"goal\":\"match_plane\",\"target\":\"Minmus\",\"at\":\"pe\"}"));
        }

        // ---- shift_lan ----

        [Fact]
        public void Shift_lan_burns_where_the_planes_cross_and_keeps_the_inclination()
        {
            // Two planes of inclination i whose LANs differ by ΔΩ are θ apart, cos θ = cos² i + sin² i cos ΔΩ; the turn costs 2 v sin(θ/2).
            var r = KerbinR + 100000;
            var result = Run(Snapshot(inclination: 10, lan: 0), "{\"goal\":\"shift_lan\",\"value\":30}");
            double i = 10 * Math.PI / 180, dOmega = 30 * Math.PI / 180;
            var theta = Math.Acos(Math.Cos(i) * Math.Cos(i) + Math.Sin(i) * Math.Sin(i) * Math.Cos(dOmega));
            Assert.Equal(2 * Math.Sqrt(KerbinGm / r) * Math.Sin(theta / 2), Magnitude(result), 2);
            Assert.Equal(30, Achieved(result, "lan"), 3);
            Assert.Equal(10, Achieved(result, "inclination"), 3);
        }

        [Fact]
        public void Shift_lan_round_the_Mun_costs_the_same_plane_turn()
        {
            var r = MunR + 30000;
            var result = Run(Snapshot(30000, inclination: 45, lan: 100, body: "Mun"), "{\"goal\":\"shift_lan\",\"value\":80}");
            double i = 45 * Math.PI / 180, dOmega = 20 * Math.PI / 180;
            var theta = Math.Acos(Math.Cos(i) * Math.Cos(i) + Math.Sin(i) * Math.Sin(i) * Math.Cos(dOmega));
            Assert.Equal(2 * Math.Sqrt(MunGm / r) * Math.Sin(theta / 2), Magnitude(result), 2);
            Assert.Equal(80, Achieved(result, "lan"), 3);
            Assert.Equal(45, Achieved(result, "inclination"), 3);
        }

        [Fact]
        public void Shift_lan_at_a_given_time_sets_the_lan_from_there()
        {
            // MechJeb's form: the plane through the burn point and the new node, so the LAN is exact and the inclination moves.
            var result = Run(Snapshot(100000, 400000, inclination: 30, lan: 10, argPe: 70, body: "Kerbin"), "{\"goal\":\"shift_lan\",\"value\":40,\"at\":\"pe\"}");
            Assert.Equal(40, Achieved(result, "lan"), 3);
        }

        [Fact]
        public void Shift_lan_from_an_equatorial_orbit_or_on_the_equator_says_why()
        {
            Assert.Contains("equatorial", Error(Snapshot(), "{\"goal\":\"shift_lan\",\"value\":30}"));
            Assert.Contains("on the equator", Error(Snapshot(inclination: 10), "{\"goal\":\"shift_lan\",\"value\":30,\"at\":\"eq_an\"}"));
        }

        // ---- resonant ----

        [Fact]
        public void Resonant_with_this_orbit_sets_the_period_by_keplers_third_law()
        {
            // 2 orbits per 3 of today's: the period is 1.5 times longer, so the semi-major axis is 1.5^(2/3) times larger.
            var r = KerbinR + 100000;
            var period = 2 * Math.PI * Math.Sqrt(r * r * r / KerbinGm);
            var result = Run(Snapshot(), "{\"goal\":\"resonant\",\"value\":2,\"of\":3}");
            var a = r * Math.Pow(1.5, 2.0 / 3);
            Assert.Equal(VisViva(KerbinGm, r, a) - Math.Sqrt(KerbinGm / r), Prograde(result), 2);
            Assert.Equal(1.5 * period, Achieved(result, "period"), 0);
        }

        [Fact]
        public void Resonant_with_the_Mun_has_half_its_period_for_two_orbits_per_one()
        {
            var munPeriod = 2 * Math.PI * Math.Sqrt(Math.Pow(12000000, 3) / KerbinGm);
            double rp = KerbinR + 100000, ra = KerbinR + 300000;
            var result = Run(Snapshot(100000, 300000, phase: JustPast), "{\"goal\":\"resonant\",\"value\":2,\"of\":1,\"target\":\"Mun\"}");
            Assert.Equal(munPeriod / 2, Achieved(result, "period"), 0);
            // A prograde burn at the Pe to the semi-major axis Kepler's third law gives that period.
            var a = Math.Pow(KerbinGm * Math.Pow(munPeriod / 2 / (2 * Math.PI), 2), 1.0 / 3);
            Assert.Equal(VisViva(KerbinGm, rp, a) - VisViva(KerbinGm, rp, (rp + ra) / 2), Prograde(result), 2);
        }

        [Fact]
        public void Resonant_round_the_Mun_with_this_orbit()
        {
            var r = MunR + 30000;
            var period = 2 * Math.PI * Math.Sqrt(r * r * r / MunGm);
            var result = Run(Snapshot(30000, phase: JustPast, body: "Mun"), "{\"goal\":\"resonant\",\"value\":3,\"of\":4}");
            Assert.Equal(period * 4 / 3, Achieved(result, "period"), 0);
        }

        [Fact]
        public void A_resonant_orbit_under_the_surface_or_against_a_body_elsewhere_says_why()
        {
            // 3 orbits per 2 from 100 km: the semi-major axis shrinks to 534 km, so the Pe would be far underground.
            Assert.Contains("below Kerbin's surface", Error(Snapshot(phase: JustPast), "{\"goal\":\"resonant\",\"value\":3,\"of\":2}"));
            Assert.Contains("not in orbit round Mun", Error(Snapshot(30000, phase: JustPast, body: "Mun"), "{\"goal\":\"resonant\",\"value\":1,\"target\":\"Minmus\"}"));
            Assert.Contains("of is for resonant only", Error(Snapshot(), "{\"goal\":\"set_sma\",\"value\":1000000,\"of\":2}"));
        }

        // ---- stationary ----

        [Fact]
        public void Stationary_circularizes_at_the_synchronous_radius_where_the_orbit_crosses_it()
        {
            var synchronous = Math.Pow(KerbinGm * Math.Pow(KerbinDay / (2 * Math.PI), 2), 1.0 / 3);
            var snapshot = Snapshot(100000, 3500000);
            var result = Run(snapshot, "{\"goal\":\"stationary\"}");
            Assert.Equal(synchronous, Achieved(result, "sma"), 0);
            Assert.InRange(Achieved(result, "eccentricity"), 0, 1e-6);
            Assert.Equal(KerbinDay, Achieved(result, "period"), 0);
            // at_ut is rounded to 0.1 s, a few tens of metres of climb.
            Assert.InRange(Orbit(snapshot).Radius(result.GetProperty("at_ut").GetDouble()) - synchronous, -100, 100);
            Assert.NotEqual(0, result.GetProperty("dv").GetProperty("radial").GetDouble());
        }

        [Fact]
        public void Stationary_from_an_ap_at_the_synchronous_altitude_burns_there()
        {
            var synchronous = Math.Pow(KerbinGm * Math.Pow(KerbinDay / (2 * Math.PI), 2), 1.0 / 3);
            double rp = KerbinR + 100000, ra = synchronous;
            var result = Run(Snapshot(100000, synchronous - KerbinR), "{\"goal\":\"stationary\"}");
            Assert.Equal(Math.Sqrt(KerbinGm / ra) - VisViva(KerbinGm, ra, (rp + ra) / 2), Prograde(result), 2);
            Assert.Equal(KerbinDay, Achieved(result, "period"), 0);
        }

        [Fact]
        public void Stationary_says_why_when_there_is_no_such_orbit_or_the_orbit_never_reaches_it()
        {
            // The Mun turns once in 38.6 h: its synchronous radius is past its sphere of influence.
            Assert.Contains("outside its sphere of influence", Error(Snapshot(30000, body: "Mun"), "{\"goal\":\"stationary\"}"));
            var fast = Snapshot(30000, body: "Mun");
            fast.Bodies.Single(b => b.Name == "Mun").RotationPeriod = 100;
            Assert.Contains("below its surface", Error(fast, "{\"goal\":\"stationary\"}"));
            Assert.Contains("set_ap to it first", Error(Snapshot(), "{\"goal\":\"stationary\"}"));
            Assert.Contains("leave at out", Error(Snapshot(), "{\"goal\":\"stationary\",\"at\":\"ap\"}"));
        }

        // ---- The contract ----

        public static TheoryData<string, string> Goals()
        {
            return new TheoryData<string, string>
            {
                { "set_sma", "{\"goal\":\"set_sma\",\"value\":1000000}" },
                { "set_ecc", "{\"goal\":\"set_ecc\",\"value\":0.1}" },
                { "ellipticize", "{\"goal\":\"ellipticize\",\"pe\":80000,\"ap\":300000}" },
                { "set_inc", "{\"goal\":\"set_inc\",\"value\":5}" },
                { "match_plane", "{\"goal\":\"match_plane\",\"target\":\"Minmus\"}" },
                { "shift_lan", "{\"goal\":\"shift_lan\",\"value\":60}" },
                { "resonant", "{\"goal\":\"resonant\",\"value\":2,\"of\":3}" },
                { "stationary", "{\"goal\":\"stationary\"}" },
            };
        }

        /// <summary>A 100 km by 3500 km orbit inclined 20°: every goal has a node, a crossing and room to work on it.</summary>
        private static GameSnapshot Every()
        {
            return Snapshot(100000, 3500000, inclination: 20, lan: 15, argPe: 30);
        }

        [Theory]
        [MemberData(nameof(Goals))]
        public void Every_goal_returns_the_standard_contract_and_predict_burn_agrees(string goal, string input)
        {
            var snapshot = Every();
            snapshot.DeltaVSimulator = true;
            var result = Run(snapshot, input);
            Assert.Equal(goal, result.GetProperty("goal").GetString());
            var dv = result.GetProperty("dv");
            double p = dv.GetProperty("prograde").GetDouble(), n = dv.GetProperty("normal").GetDouble(), r = dv.GetProperty("radial").GetDouble();
            Assert.Equal(Math.Sqrt(p * p + n * n + r * r), Magnitude(result), 1);
            Assert.True(result.GetProperty("at_ut").GetDouble() >= 0);
            Assert.True(result.TryGetProperty("burn_time", out _));
            Assert.NotEmpty(result.GetProperty("achieved").EnumerateObject());
            // predict_burn on the returned components and time leaves the same orbit the result reports.
            var again = _tools.Run("predict_burn", "{\"burns\":[{\"at\":" + R(result.GetProperty("at_ut").GetDouble()) + ",\"dv\":{\"prograde\":" + R(p)
                + ",\"normal\":" + R(n) + ",\"radial\":" + R(r) + "}}]}", snapshot, new NoPlanningBody());
            Assert.False(again.IsError, again.Json);
            using (var doc = JsonDocument.Parse(again.Json))
            {
                var patch = doc.RootElement.GetProperty("patches")[0];
                var expected = Patch(result);
                // at_ut and the components are rounded (0.1 s, 0.01 m/s), which moves the apsides by a few hundred metres at most.
                Assert.InRange(patch.GetProperty("pe").GetDouble() - expected.GetProperty("pe").GetDouble(), -500, 500);
                Assert.InRange(patch.GetProperty("inclination").GetDouble() - expected.GetProperty("inclination").GetDouble(), -0.01, 0.01);
            }
        }

        [Theory]
        [Trait("Category", "Timing")]
        [MemberData(nameof(Goals))]
        public void Each_goal_solves_in_milliseconds(string goal, string input)
        {
            // Closed forms and a short BrentRoot: measured well under 1 ms each in a Debug build, nearly all of it predict_burn's
            // propagation of the answer; the budget is 20 ms, the share of a 50 ms frame a Planning Tool call may take and more.
            var snapshot = Every();
            Timing.Within(20, () => Assert.False(_tools.Run("solve_burn", input, snapshot, new NoPlanningBody()).IsError, goal));
        }

        [Fact]
        public void The_schema_grows_only_by_target_pe_ap_of_the_intercept_fields_and_the_goals()
        {
            using (var doc = JsonDocument.Parse(new SolveBurn().InputSchemaJson))
            {
                var properties = doc.RootElement.GetProperty("properties");
                Assert.Equal(new[] { "goal", "at", "target", "value", "pe", "ap", "of", "arrive_ut", "tof", "retrograde", "revolutions", "direction" }, properties.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Equal(new[] { "circularize", "set_ap", "set_pe", "next_patch_pe", "set_sma", "set_ecc", "ellipticize", "set_inc", "match_plane", "shift_lan", "resonant", "stationary", "intercept_at", "match_velocities", "course_correction" },
                    properties.GetProperty("goal").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
            }
        }

        [Fact]
        public void The_description_has_one_clause_per_goal_and_stays_short()
        {
            var description = new SolveBurn().Description;
            foreach (var goal in new[] { "intercept_at:", "set_sma:", "set_ecc:", "ellipticize:", "set_inc:", "match_plane:", "shift_lan:", "resonant:", "stationary:", "match_velocities:", "course_correction:" })
                Assert.Single(description.Split(new[] { goal }, StringSplitOptions.None).Skip(1));
            // Prompt cost (#212): 1,100 characters with #443's eight goal clauses, cut from 1,451 so the Mission requests stay under
            // the Spending Cap tests' sizes (MissionDebriefTests' $0.45 cap failed at 1,451); a new goal or option adds at most one clause.
            Assert.InRange(description.Length, 1, 1150);
        }
    }
}
