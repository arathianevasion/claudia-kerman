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
        // #711: one speed a stretch, no taper: 2 m/s beyond 60 m, approach_speed (1) to 15 m, final_speed (0.3) inside, so
        // the craft is at approach_speed by 50 m and at final_speed by 10 m, as dock's docs give.
        [InlineData(2000, 2)]
        [InlineData(300, 2)]
        [InlineData(150, 2)]
        [InlineData(60.01, 2)]
        [InlineData(60, 1)]
        [InlineData(30, 1)]
        [InlineData(15.01, 1)]
        [InlineData(15, 0.3)]
        [InlineData(2, 0.3)]
        public void The_closing_speed_is_one_figure_a_stretch(double distance, double speed)
        {
            Assert.Equal(speed, DockGuidance.Speed(distance, Defaults), 9);
        }

        [Fact]
        public void A_faster_approach_speed_is_kept_far_out()
        {
            Assert.Equal(3, DockGuidance.Speed(200, new DockGuidance.Options { ApproachSpeed = 3, FinalSpeed = 0.3, MaxLateral = 0.2 }), 9);
        }

        [Fact]
        public void The_wanted_velocity_is_constant_within_a_stretch_and_changes_only_at_its_edges()
        {
            // On the axis, closing from 300 m: the wanted velocity changes at the stretches' edges, 60 m and 15 m, and nowhere else.
            var changes = new List<double>();
            var last = DockGuidance.Want(300, 0, 300, 0, Defaults);
            for (var d = 300.0; d > 0.5; d -= 0.05)
            {
                var want = DockGuidance.Want(d, 0, d, 0, Defaults);
                if (want.Along != last.Along || want.Lat != last.Lat) changes.Add(Math.Round(d, 1));
                last = want;
            }
            Assert.Equal(new[] { 60.0, 15.0 }, changes);
            // Off the axis on the cone, drifting in: the lateral speed is constant too, until the inner zone.
            Assert.Equal(DockGuidance.Want(120, 3, 120, 0, Defaults).Lat, DockGuidance.Want(90, 2, 90, 0, Defaults).Lat, 9);
        }

        [Fact]
        public void It_waits_for_dock_hold_to_settle_the_target_before_it_lines_up()
        {
            // 296 m out, almost all of it sideways (#711's Mission): while dock_hold is starting or still turning the station,
            // the craft only nulls its relative velocity; settled (or passive), it lines up.
            foreach (var hold in new[] { "starting", "active", "near" })
            {
                Assert.True(DockGuidance.Waits(hold));
                var tick = Tick(0.4, false, along: 59, lat: 290, vLat: -0.3, hold: hold);
                Assert.Equal(0, (double)tick["along"]);
                Assert.Equal(0, (double)tick["lat"]);
                Assert.Equal(0.5, (double)tick["dv_error"], 9);
            }
            foreach (var hold in new[] { "settled", "passive", "none" })
            {
                Assert.False(DockGuidance.Waits(hold));
                Assert.Equal(-0.6, (double)Tick(0, false, along: 59, lat: 290, hold: hold)["lat"], 9);
            }
        }

        private static IDictionary<string, object> Wait(string hold, double ut, double since, double timeout = 300)
        {
            return DockGuidance.Guide(new Dictionary<string, object>
            {
                { "mode", "close" }, { "hold", hold }, { "along", 59.0 }, { "lat", 290.0 }, { "distance", 296.0 }, { "align_deg", 0.0 },
                { "v_along", 0.0 }, { "v_lat", 0.0 }, { "v_side", 0.0 }, { "firing", false },
                { "approach_speed", 1.0 }, { "final_speed", 0.3 }, { "max_lateral", 0.2 },
                { "ut", ut }, { "wait_since", since }, { "settle_timeout", timeout }, { "target", "Station" }, { "hold_off_deg", 3.4 }, { "spin_dps", 0.27 },
            });
        }

        private static bool Holds(IDictionary<string, object> tick)
        {
            return (double)tick["lat"] == 0 && (double)tick["along"] == 0;
        }

        [Fact]
        public void An_active_hold_is_waited_for_until_the_settle_timeout_and_the_clock_starts_with_the_wait()
        {
            var first = Wait("active", 1000, -1);
            Assert.True(Holds(first));
            Assert.Equal(1000, (double)first["wait_since"]);
            Assert.Equal(0, (double)first["wait_s"]);
            Assert.Equal("", (string)first["wait_end"]);
            var later = Wait("active", 1299, 1000);
            Assert.True(Holds(later));
            Assert.Equal(299, (double)later["wait_s"], 9);
            Assert.Equal("", (string)later["wait_end"]);
            Assert.Equal("", (string)later["wait_error"]);
        }

        [Fact]
        public void After_the_timeout_a_loose_settle_ends_the_wait_but_before_it_does_not()
        {
            var before = Wait("near", 1100, 1000);
            Assert.True(Holds(before));
            Assert.Equal("", (string)before["wait_end"]);
            var after = Wait("near", 1300, 1000);
            Assert.False(Holds(after));
            Assert.Equal("loosely settled", (string)after["wait_end"]);
            Assert.Equal(300, (double)after["wait_s"], 9);
            Assert.Equal(-1, (double)after["wait_since"]);
            Assert.Equal("", (string)after["wait_error"]);
        }

        [Fact]
        public void A_settled_hold_ends_the_wait_with_how_long_it_took()
        {
            var tick = Wait("settled", 1042, 1000);
            Assert.False(Holds(tick));
            Assert.Equal("settled", (string)tick["wait_end"]);
            Assert.Equal(42, (double)tick["wait_s"], 9);
            Assert.Equal(-1, (double)tick["wait_since"]);
            // Once ended, later ticks report nothing more.
            Assert.Equal("", (string)Wait("settled", 1043, -1)["wait_end"]);
        }

        [Fact]
        public void At_twice_the_timeout_with_no_loose_settle_the_block_gives_up_naming_the_hold()
        {
            Assert.Equal("", (string)Wait("active", 1599, 1000)["wait_error"]);
            var tick = Wait("active", 1600, 1000);
            Assert.Equal("gave up", (string)tick["wait_end"]);
            Assert.Equal("Station's dock_hold never settled: 3.4° off its aim, turning 0.27°/s, after 600 s", (string)tick["wait_error"]);
            // A shorter timeout scales the limit.
            Assert.Equal("gave up", (string)Wait("starting", 1100, 1000, 50)["wait_end"]);
        }

        [Fact]
        public void A_re_aim_restarts_the_clock()
        {
            // Settled at 1000 + 100; a re-aim reads active again with no clock running, so it starts afresh.
            Assert.Equal("settled", (string)Wait("settled", 1100, 1000)["wait_end"]);
            var reaim = Wait("active", 1500, -1);
            Assert.Equal(1500, (double)reaim["wait_since"]);
            Assert.Equal("", (string)reaim["wait_error"]);
            Assert.True(Holds(Wait("active", 1500 + 299, 1500)));
        }

        [Theory]
        [InlineData("passive")]
        [InlineData("none")]
        [InlineData("fault")]
        public void A_passive_target_never_waits_or_starts_a_clock(string hold)
        {
            var tick = Wait(hold, 1000, -1);
            Assert.False(Holds(tick));
            Assert.Equal(-1, (double)tick["wait_since"]);
            Assert.Equal("", (string)tick["wait_end"]);
            Assert.Equal("", (string)tick["wait_error"]);
        }

        [Fact]
        public void The_inner_zone_is_fixed_for_a_stretch()
        {
            Assert.Equal(1.35, DockGuidance.Inner(200, Defaults), 9);
            Assert.Equal(1.35, DockGuidance.Inner(61, Defaults), 9);
            Assert.Equal(0.225, DockGuidance.Inner(60, Defaults), 9);
            Assert.Equal(0.225, DockGuidance.Inner(16, Defaults), 9);
            Assert.Equal(0.1, DockGuidance.Inner(15, Defaults), 9);
            Assert.Equal(0.1, DockGuidance.Inner(5, Defaults), 9);
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
        // On the axis: close at the stretch's speed.
        [InlineData(80, 0, 0, -2, 0)]
        [InlineData(30, 0, 0, -1, 0)]
        // Inside 10 m within the inner zone (half max_lateral): straight in.
        [InlineData(5, 0.08, 0, -0.3, 0)]
        // On the cone but outside the inner zone: close and drift in at 0.3 of the speed.
        [InlineData(5, 0.15, 0, -0.3, -0.09)]
        [InlineData(80, 2, 0, -2, -0.6)]
        // Off the cone (0.2 m inside 10 m): hold the distance and line up at 0.3 of the speed.
        [InlineData(5, 0.5, 0, 0, -0.09)]
        // Off the cone within 2 m: back out at final_speed.
        [InlineData(1, 0.5, 0, 0.3, -0.09)]
        // On the cone inside 20 m but pointing 8 degrees off the axis: wait for the turn.
        [InlineData(15, 0, 8, 0, 0)]
        // Half a metre behind the face at a port's edge, as just before capture: no 50 m detour, back out and line up.
        [InlineData(-0.5, 0.5, 0, 0.3, -0.09)]
        // Over a metre behind the port's face: out to 50 m off the axis first, then out past the face.
        [InlineData(-80, 10, 0, 0, 2)]
        [InlineData(-80, 60, 0, 2, 0)]
        public void The_wanted_velocity_lines_up_on_the_axis_before_it_closes(double along, double lat, double align, double wantAlong, double wantLat)
        {
            var want = DockGuidance.Want(along, lat, Math.Sqrt(along * along + lat * lat), align, Defaults);
            Assert.Equal(wantAlong, want.Along, 6);
            Assert.Equal(wantLat, want.Lat, 6);
        }

        private static IDictionary<string, object> Tick(double vAlong, bool firing, string mode = "close", double along = 30, double lat = 0, double vLat = 0, double vSide = 0,
            string hold = "settled")
        {
            return DockGuidance.Guide(new Dictionary<string, object>
            {
                { "mode", mode }, { "hold", hold }, { "along", along }, { "lat", lat }, { "distance", Math.Sqrt(along * along + lat * lat) }, { "align_deg", 0.0 },
                { "v_along", vAlong }, { "v_lat", vLat }, { "v_side", vSide }, { "firing", firing },
                { "approach_speed", 1.0 }, { "final_speed", 0.3 }, { "max_lateral", 0.2 },
            });
        }

        [Theory]
        // At 30 m the craft wants -1 m/s along the axis. Inside the band (error to 0.25 m/s) a coasting craft stays coasting, with no command.
        [InlineData(-1.0, false, false)]
        [InlineData(-0.8, false, false)]
        [InlineData(-1.24, false, false)]
        // Out of the band it translates.
        [InlineData(-0.7, false, true)]
        [InlineData(-1.3, false, true)]
        [InlineData(0, false, true)]
        // Translating, it goes on until the error is under a quarter of the band (0.0625 m/s), then coasts.
        [InlineData(-0.8, true, true)]
        [InlineData(-0.95, true, false)]
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
        // #711: a quarter of the wanted speed, at least 0.03 m/s (it was a fifth within 0.05 and 0.1).
        [InlineData(2, 0.5)]
        [InlineData(1, 0.25)]
        [InlineData(0.3, 0.075)]
        [InlineData(0.1, 0.03)]
        [InlineData(0, 0.03)]
        public void The_band_is_a_share_of_the_wanted_speed_with_a_floor(double wantSpeed, double band)
        {
            Assert.Equal(band, DockGuidance.Band(wantSpeed), 9);
            Assert.False(DockGuidance.Fires(band - 1e-6, wantSpeed, false));
            Assert.True(DockGuidance.Fires(band + 1e-6, wantSpeed, false));
            Assert.True(DockGuidance.Fires(band / 4 + 1e-6, wantSpeed, true));
            Assert.False(DockGuidance.Fires(band / 4 - 1e-6, wantSpeed, true));
        }

        [Fact]
        public void After_a_burn_the_craft_coasts_until_the_error_leaves_the_band()
        {
            // Wanting -1 m/s at 30 m: a burn ends under 0.0625 m/s off, then the error may grow to 0.25 m/s with RCS idle.
            var firing = true;
            var coasted = 0;
            for (var v = -0.5; v > -1.3; v -= 0.01)
            {
                firing = (bool)Tick(v, firing)["fire"];
                if (!firing) coasted++;
                if (v < -0.95 && v > -1.24) Assert.False(firing, "translating at " + v + " m/s");
            }
            Assert.True(firing, "still coasting past the band");
            Assert.True(coasted > 25, "coasted " + coasted);
        }

        [Fact]
        public void The_band_is_on_the_whole_velocity_error_and_commands_every_axis()
        {
            // 0.2 m/s off in each of the three axes: 0.35 m/s in all, out of the 0.25 m/s band.
            var tick = Tick(-1.2, false, vLat: 0.2, vSide: -0.2);
            Assert.True((bool)tick["fire"]);
            Assert.Equal(1, (double)tick["cmd_along"], 9);
            Assert.Equal(-1, (double)tick["cmd_lat"], 9);
            Assert.Equal(1, (double)tick["cmd_side"], 9);
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
