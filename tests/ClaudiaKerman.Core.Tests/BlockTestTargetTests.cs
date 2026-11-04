// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The block test runner's second craft (#455, reused by #456): a target placed relative to the spawned craft, so many metres
    /// along its track and moving at a relative velocity in its node frame, as <c>test.cfg</c>'s <c>target_*</c> keys give it.
    /// </summary>
    public class BlockTestTargetTests
    {
        private const double Gm = 3.5316e12;
        private const double R = 700000;
        private const double Ut = 1000;

        /// <summary>The craft as the runner spawns it: circular at 100 km round Kerbin, at inclination inc.</summary>
        private static SnapshotOrbit Craft(double inc = 0)
        {
            return new SnapshotOrbit { SemiMajorAxis = R, Eccentricity = 0, Inclination = inc, Lan = 0, ArgumentOfPeriapsis = 0, MeanAnomalyAtEpoch = 0, Epoch = Ut };
        }

        private static Conic Conic(SnapshotOrbit o)
        {
            return Tools.Conic.FromElements(Gm, o.SemiMajorAxis, o.Eccentricity, o.Inclination, o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch);
        }

        private static void States(SnapshotOrbit target, double inc, out Vec3 r, out Vec3 v, out Vec3 rt, out Vec3 vt)
        {
            Conic(Craft(inc)).State(Ut, out r, out v);
            Conic(target).State(Ut, out rt, out vt);
        }

        [Fact]
        public void A_target_with_no_offset_is_where_the_craft_is()
        {
            Vec3 r, v, rt, vt;
            States(BlockTestTarget.Place(Gm, Craft(), Ut, 0, 0, 0, 0), 0, out r, out v, out rt, out vt);
            Assert.InRange((rt - r).Length, 0, 0.01);
            Assert.InRange((vt - v).Length, 0, 1e-6);
        }

        [Theory]
        [InlineData(0, 1500)]
        [InlineData(0, -1500)]
        [InlineData(28, 200000)]
        public void A_target_along_the_track_is_that_far_round_the_orbit_on_the_same_orbit(double inc, double along)
        {
            Vec3 r, v, rt, vt;
            var target = BlockTestTarget.Place(Gm, Craft(inc), Ut, along, 0, 0, 0);
            States(target, inc, out r, out v, out rt, out vt);
            // The arc along the circle, measured as the angle between the two radii.
            var arc = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(r, rt) / (r.Length * rt.Length)))) * R;
            Assert.InRange(arc, Math.Abs(along) - 0.5, Math.Abs(along) + 0.5);
            // Ahead is the way the craft moves; behind, the other way.
            Assert.Equal(Math.Sign(along), Math.Sign(Vec3.Dot(rt - r, v)));
            Assert.InRange(target.SemiMajorAxis, R - 0.01, R + 0.01);
            Assert.InRange(target.Eccentricity, 0, 1e-9);
            Assert.InRange(target.Inclination, inc - 1e-9, inc + 1e-9);
        }

        [Fact]
        public void A_relative_velocity_is_in_the_craft_s_node_frame()
        {
            Vec3 r, v, rt, vt;
            States(BlockTestTarget.Place(Gm, Craft(), Ut, 0, 30, 0, 0), 0, out r, out v, out rt, out vt);
            var relative = vt - v;
            Assert.InRange(relative.Length, 29.999, 30.001);
            Assert.InRange(Vec3.Dot(relative, v.Normalized), 29.999, 30.001);

            States(BlockTestTarget.Place(Gm, Craft(), Ut, 0, 0, 0, 10), 0, out r, out v, out rt, out vt);
            Assert.InRange(Vec3.Dot(vt - v, r.Normalized), 9.999, 10.001);
        }

        [Fact]
        public void Thirty_metres_a_second_prograde_raises_the_far_side_by_vis_viva()
        {
            var target = BlockTestTarget.Place(Gm, Craft(), Ut, 0, 30, 0, 0);
            var speed = Math.Sqrt(Gm / R) + 30;
            var sma = 1 / (2 / R - speed * speed / Gm);
            Assert.InRange(target.SemiMajorAxis, sma - 0.5, sma + 0.5);
            // The apoapsis half an orbit on: 2a - r, about 38.6 km above the 100 km circle.
            Assert.InRange(target.SemiMajorAxis * (1 + target.Eccentricity) - R, 38000, 39000);
        }

        [Fact]
        public void A_normal_velocity_tilts_the_target_s_plane_by_its_angle_to_the_orbital_speed()
        {
            var target = BlockTestTarget.Place(Gm, Craft(), Ut, 0, 0, 6, 0);
            var expected = Math.Atan2(6, Math.Sqrt(Gm / R)) * 180 / Math.PI;
            Assert.InRange(target.Inclination, expected - 1e-6, expected + 1e-6);
        }

        [Fact]
        public void A_spec_places_a_second_craft_with_the_target_keys()
        {
            var spec = BlockTestSpec.Parse("rendezvous", "block = rendezvous\ntarget_craft = reference.craft\ntarget_orbit_along = -200000\n"
                + "target_orbit_prograde = 30\ntarget_orbit_normal = 6\ntarget_orbit_radial = -1\n");
            Assert.Equal("reference.craft", spec.TargetCraft);
            Assert.Equal(-200000.0, spec.TargetOrbitAlong);
            Assert.Equal(30.0, spec.TargetOrbitPrograde);
            Assert.Equal(6.0, spec.TargetOrbitNormal);
            Assert.Equal(-1.0, spec.TargetOrbitRadial);
        }

        [Fact]
        public void A_spec_without_target_craft_spawns_one_craft_and_refuses_target_keys_alone()
        {
            var spec = BlockTestSpec.Parse("exec_node", "block = exec_node\n");
            Assert.Null(spec.TargetCraft);
            Assert.Equal(0.0, spec.TargetOrbitAlong);
            var refused = Assert.Throws<FormatException>(() => BlockTestSpec.Parse("t", "block = rendezvous\ntarget_orbit_along = 500\n"));
            Assert.Contains("target_craft", refused.Message);
            Assert.Throws<FormatException>(() => BlockTestSpec.Parse("t", "block = rendezvous\ntarget_craft = reference.craft\nstart = launchpad\n"));
        }

        [Fact]
        public void A_spec_lands_the_second_craft_on_the_body_ahead_of_the_craft_with_the_landed_keys()
        {
            var spec = BlockTestSpec.Parse("land_vessel", "block = land\norbit_body = Mun\ntarget_craft = target.craft\ntarget_landed_lat = 0.1\ntarget_landed_ahead = 270\n");
            Assert.True(spec.TargetLanded);
            Assert.Equal(0.1, spec.TargetLandedLat);
            Assert.Equal(270.0, spec.TargetLandedAhead);
            Assert.False(BlockTestSpec.Parse("rendezvous", "block = rendezvous\ntarget_craft = reference.craft\n").TargetLanded);
            Assert.Contains("target_craft", Assert.Throws<FormatException>(() => BlockTestSpec.Parse("t", "block = land\ntarget_landed_ahead = 90\n")).Message);
            var both = Assert.Throws<FormatException>(() => BlockTestSpec.Parse("t", "block = land\ntarget_craft = t.craft\ntarget_landed_ahead = 90\ntarget_orbit_along = 5\n"));
            Assert.Contains("landed or in orbit", both.Message);
        }
    }
}
