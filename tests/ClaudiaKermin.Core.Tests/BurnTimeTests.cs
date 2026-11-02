// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Text.Json;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The burn-time references (#442), checked against the orbit's geometry rather than against a second copy of the formulas:
    /// the craft's latitude is 0 at an equatorial node, it is in the target's plane at a relative one, and its radius is the
    /// altitude's at a crossing, moving the way the reference says.
    /// </summary>
    public sealed class BurnTimeTests
    {
        private const double KerbinGm = 3.5316e12;
        private const double KerbinR = 600000;
        private const double MunGm = 6.5138398e10;
        private const double MunSma = 12000000;

        private static readonly SnapshotBody Kerbin = new SnapshotBody
        {
            Name = "Kerbin", Parent = "Sun", Gm = KerbinGm, Radius = KerbinR, AtmosphereHeight = 70000, SoiRadius = 84159286.48, Sma = 13599840256,
        };
        private static readonly SnapshotBody Mun = new SnapshotBody
        {
            Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = 200000, SoiRadius = 2429559.1, Sma = MunSma, MeanAnomalyAtEpoch = 1,
        };
        private static readonly SnapshotBody Minmus = new SnapshotBody
        {
            Name = "Minmus", Parent = "Kerbin", Gm = 1.7658e9, Radius = 60000, SoiRadius = 2247428.4, Sma = 47000000,
            Inclination = 6, Lan = 78, ArgumentOfPeriapsis = 38, MeanAnomalyAtEpoch = 0.9,
        };
        private static readonly SnapshotBody Sun = new SnapshotBody { Name = "Sun", Gm = 1.1723328e18, Radius = 261600000 };

        private static BodySystem System()
        {
            return new BodySystem(new[] { Sun, Kerbin, Mun, Minmus });
        }

        /// <summary>An orbit round Kerbin from its Pe and Ap altitudes (m) and angles in degrees; the craft is at mean anomaly <paramref name="m0"/> at UT 0.</summary>
        private static Conic Orbit(double pe, double ap, double inc = 0, double lan = 0, double argPe = 0, double m0 = 0)
        {
            double rp = KerbinR + pe, ra = KerbinR + ap;
            return Conic.FromElements(KerbinGm, (rp + ra) / 2, (ra - rp) / (ra + rp), inc, lan, argPe, m0, 0);
        }

        private static Patch PatchOf(Conic orbit, double endUt = double.PositiveInfinity)
        {
            return new Patch { Body = Kerbin, Orbit = orbit, StartUt = 0, EndUt = endUt, End = PatchEnd.Stable };
        }

        private static double Resolve(string at, Conic orbit, SnapshotBody target = null, double after = 0)
        {
            string problem;
            var ut = BurnTime.Resolve(JsonReader.Parse(JsonSerializer.Serialize(at)), new List<Patch> { PatchOf(orbit) }, after, after, "the burn", System(), target, out problem);
            Assert.Null(problem);
            Assert.NotNull(ut);
            return ut.Value;
        }

        private static string Problem(string at, Conic orbit, SnapshotBody target = null, double after = 0)
        {
            string problem;
            var ut = BurnTime.Resolve(JsonReader.Parse(JsonSerializer.Serialize(at)), new List<Patch> { PatchOf(orbit) }, after, after, "the burn", System(), target, out problem);
            Assert.Null(ut);
            Assert.NotNull(problem);
            return problem;
        }

        private static Conic[] Orbits()
        {
            return new[]
            {
                Orbit(100000, 100000, 28.5, 40, 0, 1),
                Orbit(100000, 100000, 98, 250, 0, 4),
                Orbit(100000, 2000000, 35, 120, 77, 2.5),
                Orbit(200000, 40000000, 62, 300, 210, 5.5),
                Orbit(100000, 700000, 150, 15, 300, 0.2),
            };
        }

        private static void StateOf(Conic orbit, double ut, out Vec3 r, out Vec3 v)
        {
            orbit.State(ut, out r, out v);
        }

        // ---- the equatorial nodes ----

        [Fact]
        public void The_equatorial_ascending_node_is_at_latitude_0_going_north_on_circular_inclined_and_eccentric_orbits()
        {
            foreach (var orbit in Orbits())
            {
                var ut = Resolve("eq_an", orbit);
                Vec3 r, v;
                StateOf(orbit, ut, out r, out v);
                Assert.True(Math.Abs(r.Z) / r.Length < 1e-9, "latitude " + r.Z / r.Length);
                Assert.True(v.Z > 0);
                Assert.True(ut >= 0 && ut <= orbit.Period + 1);
            }
        }

        [Fact]
        public void The_equatorial_descending_node_is_at_latitude_0_going_the_other_way_and_half_an_orbit_of_angle_from_the_ascending()
        {
            foreach (var orbit in Orbits())
            {
                var ut = Resolve("eq_dn", orbit);
                Vec3 r, v, ra, va;
                StateOf(orbit, ut, out r, out v);
                Assert.True(Math.Abs(r.Z) / r.Length < 1e-9);
                Assert.True(v.Z < 0);
                StateOf(orbit, Resolve("eq_an", orbit), out ra, out va);
                Assert.True(Vec3.Dot(r.Normalized, ra.Normalized) < -1 + 1e-9);
            }
        }

        [Fact]
        public void The_nearest_equatorial_node_is_the_sooner_and_the_highest_is_the_one_farther_from_the_body()
        {
            foreach (var orbit in Orbits())
            {
                double an = Resolve("eq_an", orbit), dn = Resolve("eq_dn", orbit);
                Assert.Equal(Math.Min(an, dn), Resolve("eq_nearest", orbit), 6);
                var high = Resolve("eq_highest", orbit);
                Assert.True(high == an || high == dn);
                var other = high == an ? dn : an;
                Assert.True(orbit.Radius(high) >= orbit.Radius(other) - 1e-6);
            }
        }

        [Fact]
        public void The_highest_node_of_an_eccentric_orbit_is_the_one_near_the_apoapsis()
        {
            // Argument of periapsis 90 puts the Ap over the north pole's side: the descending node (after the Ap) and the ascending one are equal-radius,
            // so slide the Pe off the line of nodes instead.
            var orbit = Orbit(100000, 2000000, 30, 0, 20, 0);
            var high = Resolve("eq_highest", orbit);
            double an = Resolve("eq_an", orbit), dn = Resolve("eq_dn", orbit);
            Assert.Equal(orbit.Radius(an) > orbit.Radius(dn) ? an : dn, high, 6);
        }

        [Fact]
        public void An_equatorial_orbit_has_no_equatorial_nodes_and_says_so()
        {
            var orbit = Orbit(100000, 100000);
            foreach (var at in new[] { "eq_an", "eq_dn", "eq_nearest", "eq_highest" })
                Assert.Contains("equatorial, so it has no equatorial nodes", Problem(at, orbit));
        }

        [Fact]
        public void A_retrograde_equatorial_orbit_has_no_equatorial_nodes_either()
        {
            Assert.Contains("equatorial", Problem("eq_an", Orbit(100000, 100000, 180)));
        }

        [Fact]
        public void A_node_inside_a_hyperbolas_opening_is_an_error_not_a_guess()
        {
            // A fast hyperbola with its periapsis on the line of nodes goes through one node; the other is in the opening.
            var orbit = Conic.FromElements(KerbinGm, -1.5e6, 3, 30, 0, 0, -0.1, 0);
            var an = Resolve("eq_an", orbit, after: -1);
            var dn = Problem("eq_dn", orbit, after: -1);
            Assert.Contains("opening of the hyperbola", dn);
            Assert.Equal(an, Resolve("eq_nearest", orbit, after: -1), 6);
            Assert.Equal(an, Resolve("eq_highest", orbit, after: -1), 6);
        }

        [Fact]
        public void A_hyperbola_whose_nodes_are_both_behind_it_has_none_ahead()
        {
            var orbit = Conic.FromElements(KerbinGm, -1.5e6, 3, 30, 0, 0, -0.1, 0);
            Assert.Contains("no equatorial node is ahead", Problem("eq_nearest", orbit, after: 1e6));
        }

        // ---- the relative nodes ----

        [Fact]
        public void The_relative_ascending_node_is_where_the_craft_crosses_the_targets_plane_going_above_it()
        {
            var system = System();
            var plane = system.OrbitOf(Minmus).W;
            foreach (var orbit in Orbits())
            {
                var ut = Resolve("rel_an", orbit, Minmus);
                Vec3 r, v;
                StateOf(orbit, ut, out r, out v);
                Assert.True(Math.Abs(Vec3.Dot(r, plane)) / r.Length < 1e-9);
                Assert.True(Vec3.Dot(v, plane) > 0);
            }
        }

        [Fact]
        public void The_relative_descending_node_is_where_it_crosses_going_below_it_and_is_the_other_side_of_the_orbit()
        {
            var system = System();
            var plane = system.OrbitOf(Minmus).W;
            foreach (var orbit in Orbits())
            {
                var ut = Resolve("rel_dn", orbit, Minmus);
                Vec3 r, v, ra, va;
                StateOf(orbit, ut, out r, out v);
                Assert.True(Math.Abs(Vec3.Dot(r, plane)) / r.Length < 1e-9);
                Assert.True(Vec3.Dot(v, plane) < 0);
                StateOf(orbit, Resolve("rel_an", orbit, Minmus), out ra, out va);
                Assert.True(Vec3.Dot(r.Normalized, ra.Normalized) < -1 + 1e-9);
            }
        }

        [Fact]
        public void Relative_nodes_are_not_the_equatorial_ones()
        {
            // TimeSelector in MechJeb falls through for the REL_* modes and leaves the time unchanged; here they are their own nodes.
            var orbit = Orbit(100000, 100000, 28.5, 40, 0, 1);
            Assert.True(Math.Abs(Resolve("rel_an", orbit, Minmus) - Resolve("eq_an", orbit)) > 1);
            Assert.True(Resolve("rel_an", orbit, Minmus) > 0);
        }

        [Fact]
        public void Relative_nodes_to_the_equatorial_mun_are_the_equatorial_nodes()
        {
            var orbit = Orbit(100000, 100000, 28.5, 40, 0, 1);
            Assert.Equal(Resolve("eq_an", orbit), Resolve("rel_an", orbit, Mun), 6);
            Assert.Equal(Resolve("eq_dn", orbit), Resolve("rel_dn", orbit, Mun), 6);
        }

        [Fact]
        public void Relative_nodes_need_a_target_a_different_plane_and_a_body_in_the_craft_s_sphere_of_influence()
        {
            var orbit = Orbit(100000, 100000, 28.5, 40, 0, 1);
            Assert.Contains("needs target", Problem("rel_an", orbit));
            Assert.Contains("needs target", Problem("closest_approach", orbit));
            // Kerbin is not in orbit round Kerbin, and the Sun is not in orbit round it.
            Assert.Contains("is not in orbit round Kerbin", Problem("rel_dn", orbit, Kerbin));
            Assert.Contains("is not in orbit round Kerbin", Problem("closest_approach", orbit, Sun));
            var munPlane = Orbit(100000, 100000, 0);
            Assert.Contains("same plane", Problem("rel_an", munPlane, Mun));
        }

        [Fact]
        public void A_relative_node_for_a_body_outside_the_craft_s_current_sphere_of_influence_is_an_error()
        {
            // The craft is round the Mun; Minmus orbits Kerbin.
            var round = new Patch { Body = Mun, Orbit = Conic.FromElements(MunGm, 300000, 0.01, 30, 0, 0, 0, 0), StartUt = 0, EndUt = double.PositiveInfinity, End = PatchEnd.Stable };
            string problem;
            var ut = BurnTime.Resolve(JsonReader.Parse("\"rel_an\""), new List<Patch> { round }, 0, 0, "the burn", System(), Minmus, out problem);
            Assert.Null(ut);
            Assert.Contains("Minmus is not in orbit round Mun", problem);
        }

        // ---- altitude ----

        [Fact]
        public void An_altitude_crossing_is_at_that_radius_and_the_direction_picks_the_way_through_it()
        {
            foreach (var orbit in new[] { Orbit(100000, 2000000, 20, 0, 40, 1), Orbit(100000, 2000000, 20, 0, 40, 4), Orbit(200000, 40000000, 62, 300, 210, 5.5) })
            {
                var alt = 800000.0;
                double up = Resolve("altitude 800000 ascending", orbit), down = Resolve("altitude 800000 descending", orbit);
                Assert.Equal(KerbinR + alt, orbit.Radius(up), 3);
                Assert.Equal(KerbinR + alt, orbit.Radius(down), 3);
                Vec3 r, v;
                StateOf(orbit, up, out r, out v);
                Assert.True(Vec3.Dot(r, v) > 0);
                StateOf(orbit, down, out r, out v);
                Assert.True(Vec3.Dot(r, v) < 0);
                Assert.Equal(Math.Min(up, down), Resolve("altitude 800000", orbit), 6);
            }
        }

        [Fact]
        public void An_altitude_the_orbit_does_not_cross_is_an_error_naming_the_apsides()
        {
            var orbit = Orbit(100000, 700000, 10);
            Assert.Contains("does not cross 50000 m (Pe 100000 m, Ap 700000 m)", Problem("altitude 50000", orbit));
            Assert.Contains("does not cross 900000 m", Problem("altitude 900000", orbit));
            // Touching an apsis is not a crossing.
            Assert.Contains("does not cross", Problem("altitude 700000", orbit));
        }

        [Fact]
        public void A_circular_orbit_never_crosses_an_altitude()
        {
            Assert.Contains("circular", Problem("altitude 100000", Orbit(100000, 100000, 10)));
        }

        [Fact]
        public void An_altitude_reference_that_is_malformed_is_an_error()
        {
            var orbit = Orbit(100000, 700000, 10);
            Assert.Contains("altitude <m>", Problem("altitude lots", orbit));
            Assert.Contains("\"ascending\" or \"descending\"", Problem("altitude 300000 sideways", orbit));
            Assert.Contains("altitude <m>", Problem("altitude NaN", orbit));
        }

        [Fact]
        public void An_altitude_already_passed_on_an_escape_is_an_error()
        {
            var orbit = Conic.FromElements(KerbinGm, -1.5e6, 3, 30, 0, 0, -0.1, 0);
            Assert.Contains("already passed", Problem("altitude 3000000 descending", orbit));
        }

        // ---- ap, pe, UT and in <s>, as before ----

        [Fact]
        public void Ap_pe_a_ut_and_in_s_resolve_as_predict_burn_always_did()
        {
            var orbit = Orbit(100000, 700000, 10, 0, 0, 1);
            Assert.Equal(orbit.NextApoapsis(1e-3).Value, Resolve("ap", orbit), 6);
            Assert.Equal(orbit.NextPeriapsis(1e-3).Value, Resolve("pe", orbit), 6);
            Assert.Equal(1234.0, Resolve("1234", orbit), 6);
            Assert.Equal(60.0, Resolve("in 60", orbit), 6);
            Assert.Contains("must be", Problem("sometime", orbit));
        }

        // ---- the closest approach ----

        [Fact]
        public void The_closest_approach_to_the_mun_from_a_transfer_orbit_is_the_encounter_Trajectory_finds()
        {
            double r1 = KerbinR + 100000, at = (r1 + MunSma) / 2;
            var dv = Math.Sqrt(KerbinGm * (2 / r1 - 1 / at)) - Math.Sqrt(KerbinGm / r1);
            var transfer = Math.PI * Math.Sqrt(at * at * at / KerbinGm);
            var craftRate = Math.Sqrt(KerbinGm / (r1 * r1 * r1));
            var munRate = Math.Sqrt(KerbinGm / (MunSma * MunSma * MunSma));
            var mun = new SnapshotBody { Name = "Mun", Parent = "Kerbin", Gm = MunGm, Radius = 200000, SoiRadius = 2429559.1, Sma = MunSma, MeanAnomalyAtEpoch = Math.PI - munRate * transfer - munRate * 60 + 0.02 };
            var system = new BodySystem(new[] { Sun, Kerbin, mun });
            var parked = Orbit(100000, 100000, 0, 0, 0, -craftRate * 60);
            Vec3 r, v;
            parked.State(60, out r, out v);
            var burned = Conic.FromState(KerbinGm, r, v + v.Normalized * dv, 60);
            var path = system.Follow(Kerbin, burned, 60, 3);
            Assert.Equal(PatchEnd.Encounter, path[0].End);

            string problem;
            var ut = BurnTime.Resolve(JsonReader.Parse("\"closest_approach\""), path, 60, 60, "the burn", system, mun, out problem);
            Assert.Null(problem);
            // The nearest the Kerbin patch gets to the Mun's centre is where it ends, entering the Mun's sphere of influence.
            Assert.Equal(path[0].EndUt, ut.Value, 1);
        }

        [Fact]
        public void The_closest_approach_of_a_low_orbit_is_the_point_nearest_the_mun_in_the_next_few_orbits()
        {
            var system = System();
            var orbit = Orbit(100000, 100000, 0, 0, 0, 0);
            var ut = Resolve("closest_approach", orbit, Mun);
            Assert.True(ut >= 0 && ut <= 3 * orbit.Period);
            var munOrbit = system.OrbitOf(Mun);
            var best = (orbit.Position(ut) - munOrbit.Position(ut)).Length;
            for (var t = 0.0; t <= 3 * orbit.Period; t += 5)
                Assert.True((orbit.Position(t) - munOrbit.Position(t)).Length >= best - 1);
        }

        // ---- the speed ----

        [Fact]
        [Trait("Category", "Timing")]
        public void Every_reference_resolves_within_a_budget()
        {
            var orbits = Orbits();
            Timing.Within(500, () =>
            {
                foreach (var orbit in orbits)
                    foreach (var at in new[] { "ap", "pe", "eq_an", "eq_dn", "eq_nearest", "eq_highest", "rel_an", "rel_dn", "closest_approach" })
                    {
                        string problem;
                        BurnTime.Resolve(JsonReader.Parse("\"" + at + "\""), new List<Patch> { PatchOf(orbit) }, 0, 0, "the burn", System(), Minmus, out problem);
                    }
            });
        }
    }
}
