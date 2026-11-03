// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// dock's guidance in Core (#706, ADR 0014): the closing speed, the cone, the wanted velocity (moved from dock.ks, where
    /// DockTests pinned them), the coast band and the start-of-dock monopropellant check. DockTests flies them in the kOS harness.
    /// </summary>
    public class DockGuidanceTests
    {
        private static readonly DockGuidance.Options Defaults = new DockGuidance.Options { ApproachSpeed = 1, FinalSpeed = 0.3, MaxLateral = 0.2 };

        [Theory]
        // 1 m/s beyond 50 m, 0.3 m/s inside 10 m, a taper between, up to 2 m/s far out (#706; it was 5).
        [InlineData(2000, 2)]
        [InlineData(300, 2)]
        [InlineData(150, 1.5)]
        [InlineData(80, 1)]
        [InlineData(50, 1)]
        [InlineData(30, 0.65)]
        [InlineData(10, 0.3)]
        [InlineData(2, 0.3)]
        public void The_closing_speed_goes_by_distance(double distance, double speed)
        {
            Assert.Equal(speed, DockGuidance.Speed(distance, Defaults), 9);
        }

        [Theory]
        [InlineData(5, 0.2)]
        [InlineData(10, 0.2)]
        [InlineData(30, 1.2)]
        public void The_cone_is_max_lateral_inside_10_m_and_widens_by_5_cm_a_metre(double along, double cone)
        {
            Assert.Equal(cone, DockGuidance.Cone(along, Defaults), 9);
        }

        [Theory]
        // On the axis: close at the speed for the distance.
        [InlineData(80, 0, 0, -1, 0)]
        [InlineData(5, 0.1, 0, -0.3, -0.02)]
        // Off the cone (0.2 m inside 10 m): hold the distance and line up.
        [InlineData(5, 0.5, 0, 0, -0.1)]
        // Off the cone within 2 m: back out at final_speed.
        [InlineData(1, 0.5, 0, 0.3, -0.1)]
        // On the cone inside 20 m but pointing 8 degrees off the axis: wait for the turn.
        [InlineData(15, 0, 8, 0, 0)]
        // Half a metre behind the face at a port's edge, as just before capture: no 50 m detour, back out and line up.
        [InlineData(-0.5, 0.5, 0, 0.3, -0.1)]
        // Over a metre behind the port's face: out to 50 m off the axis first, then out past the face.
        [InlineData(-80, 10, 0, 0, 1)]
        [InlineData(-80, 60, 0, 1, 0)]
        public void The_wanted_velocity_lines_up_on_the_axis_before_it_closes(double along, double lat, double align, double wantAlong, double wantLat)
        {
            var want = DockGuidance.Want(along, lat, Math.Sqrt(along * along + lat * lat), align, Defaults);
            Assert.Equal(wantAlong, want.Along, 6);
            Assert.Equal(wantLat, want.Lat, 6);
        }

        private static IDictionary<string, object> Tick(double vAlong, bool firing, string mode = "close", double along = 80, double lat = 0, double vLat = 0, double vSide = 0)
        {
            return DockGuidance.Guide(new Dictionary<string, object>
            {
                { "mode", mode }, { "along", along }, { "lat", lat }, { "distance", Math.Sqrt(along * along + lat * lat) }, { "align_deg", 0.0 },
                { "v_along", vAlong }, { "v_lat", vLat }, { "v_side", vSide }, { "firing", firing },
                { "approach_speed", 1.0 }, { "final_speed", 0.3 }, { "max_lateral", 0.2 },
            });
        }

        [Theory]
        // At 80 m the craft wants -1 m/s along the axis. Inside the band (error to 0.1 m/s) a coasting craft stays coasting, with no command.
        [InlineData(-1.0, false, false)]
        [InlineData(-0.92, false, false)]
        [InlineData(-1.09, false, false)]
        // Out of the band it translates.
        [InlineData(-0.85, false, true)]
        [InlineData(-1.2, false, true)]
        [InlineData(0, false, true)]
        // Translating, it goes on until the error is under 0.05 m/s, then coasts.
        [InlineData(-0.92, true, true)]
        [InlineData(-0.96, true, false)]
        public void The_coast_band_translates_only_once_the_error_leaves_it(double vAlong, bool firing, bool fires)
        {
            var tick = Tick(vAlong, firing);
            Assert.Equal(fires, (bool)tick["fire"]);
            Assert.Equal(Math.Abs(-1 - vAlong), (double)tick["dv_error"], 9);
            var command = Math.Abs((double)tick["cmd_along"]) + Math.Abs((double)tick["cmd_lat"]) + Math.Abs((double)tick["cmd_side"]);
            if (fires) Assert.Equal((-1 - vAlong) / DockGuidance.FullAt, (double)tick["cmd_along"], 9);
            else Assert.Equal(0, command);
        }

        [Theory]
        // A fifth of the wanted speed, within 0.05 and 0.1 m/s.
        [InlineData(2, 0.1)]
        [InlineData(0.65, 0.1)]
        [InlineData(0.3, 0.06)]
        [InlineData(0, 0.05)]
        public void The_band_narrows_with_the_wanted_speed(double wantSpeed, double band)
        {
            Assert.Equal(band, DockGuidance.Band(wantSpeed), 9);
            Assert.False(DockGuidance.Fires(band - 1e-6, wantSpeed, false));
            Assert.True(DockGuidance.Fires(band + 1e-6, wantSpeed, false));
            Assert.True(DockGuidance.Fires(band / 2 + 1e-6, wantSpeed, true));
            Assert.False(DockGuidance.Fires(band / 2 - 1e-6, wantSpeed, true));
        }

        [Fact]
        public void The_band_is_on_the_whole_velocity_error_and_commands_every_axis()
        {
            // 0.08 m/s off in each of the three axes: 0.139 m/s in all, out of the band.
            var tick = Tick(-1.08, false, vLat: 0.08, vSide: -0.08);
            Assert.True((bool)tick["fire"]);
            Assert.Equal(0.4, (double)tick["cmd_along"], 9);
            Assert.Equal(-0.4, (double)tick["cmd_lat"], 9);
            Assert.Equal(0.4, (double)tick["cmd_side"], 9);
        }

        [Fact]
        public void Backing_off_wants_half_a_metre_a_second_out_along_the_line_and_holding_wants_still()
        {
            var back = Tick(0, false, "back_off", along: 6, lat: 8);
            Assert.Equal(0.3, (double)back["along"], 9);
            Assert.Equal(0.4, (double)back["lat"], 9);
            var hold = Tick(0.03, false, "hold");
            Assert.Equal(0, (double)hold["along"]);
            Assert.False((bool)hold["fire"]);
        }

        [Fact]
        public void A_craft_with_enough_monoprop_for_the_distance_starts()
        {
            // tests/blocks/dock's craft: about 6.6 t with 20 units at 240 s, 150 m out: about 28.7 m/s against 7.6.
            var fuel = DockGuidance.Fuel(20, 240, 6.6, 150, 0.1);
            Assert.Equal("", fuel["error"]);
            Assert.Equal(28.7, (double)fuel["dv"], 1);
            Assert.Equal(7.6, (double)fuel["need"], 9);
            // #706's flight: 15 t with 128 units had about 82 m/s, many times what the dock needed.
            Assert.Equal("", DockGuidance.Fuel(128, 240, 15, 150, 1)["error"]);
        }

        [Fact]
        public void Too_little_monoprop_for_the_distance_is_refused_at_the_start()
        {
            var fuel = DockGuidance.Fuel(2, 240, 6.6, 150, 0.1);
            Assert.Equal("too little monopropellant: 2.0 units give 2.9 m/s of RCS, and 150 m to the port needs about 7.6", fuel["error"]);
            // A craft with no RCS Isp read has none.
            Assert.StartsWith("too little monopropellant: 20.0 units give 0.0 m/s", (string)DockGuidance.Fuel(20, 0, 6.6, 150, 0.1)["error"]);
        }

        [Fact]
        public void The_call_averages_the_thrusters_isp_by_thrust_and_reads_distance_and_speed()
        {
            // Two 1 kN thrusters at 240 s and one 2 kN at 300 s: 270 s. A 0 kN one counts for nothing.
            var input = new Dictionary<string, object> { { "distance", 150.0 }, { "speed", 0.1 } };
            var fuel = DockGuidance.FuelCall(input, 20, 6.6, new[] { 1.0, 1.0, 2.0, 0.0 }, new[] { 240.0, 240.0, 300.0, 999.0 });
            Assert.Equal(270, (double)fuel["isp"], 9);
            Assert.Equal(7.6, (double)fuel["need"], 9);
            Assert.Equal(DockGuidance.DeltaV(20, 270, 6.6), (double)fuel["dv"], 9);
            Assert.StartsWith("too little monopropellant: 20.0 units give 0.0 m/s", (string)DockGuidance.FuelCall(input, 20, 6.6, new double[0], new double[0])["error"]);
        }

        [Fact]
        public void The_estimate_is_a_reserve_plus_a_share_a_metre_plus_the_speed_now()
        {
            Assert.Equal(DockGuidance.ReserveDv, DockGuidance.Needed(0, 0), 9);
            Assert.Equal(DockGuidance.ReserveDv + 100 * DockGuidance.DvPerMetre + 2, DockGuidance.Needed(100, 2), 9);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_tick_of_guidance_takes_well_under_a_physics_tick()
        {
            // DOCKGUIDE runs every physics tick (20 ms): 1,000 ticks within 5 ms, 5 microseconds each (ADR 0014 guardrail 6).
            var input = new Dictionary<string, object>
            {
                { "mode", "close" }, { "along", 30.0 }, { "lat", 1.5 }, { "distance", 30.04 }, { "align_deg", 2.0 },
                { "v_along", -0.6 }, { "v_lat", 0.02 }, { "v_side", 0.01 }, { "firing", false },
                { "approach_speed", 1.0 }, { "final_speed", 0.3 }, { "max_lateral", 0.2 },
            };
            Timing.Within(5, () =>
            {
                for (var i = 0; i < 1000; i++) DockGuidance.Guide(input);
            });
        }
    }
}
