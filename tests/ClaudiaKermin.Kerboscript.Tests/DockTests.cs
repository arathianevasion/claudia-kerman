// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using kOS.Safe;
using kOS.Safe.Encapsulation;
using kOS.Safe.Function;
using Xunit;

namespace ClaudiaKermin.Kerboscript.Tests
{
    /// <summary>
    /// The dock Building Block (#456) flown in the kOS harness: dock_run, the block's own flight, against <see cref="DockRig"/>, a
    /// craft's port moving in the target port's frame (distance out along its axis and off it) under RCS of limited authority,
    /// with the magnetic capture modelled as a distance-and-alignment threshold, and dock_hold's state as the mod reports it.
    /// Flying it in KSP is tests/blocks/dock (#457).
    /// </summary>
    public class DockTests
    {
        static DockTests()
        {
            DockRigFunctions.Register();
        }

        private static string[] Run(string missionScript)
        {
            return KosHarness.Run(CoreLibrary.Bundle(missionScript)).ToArray();
        }

        private static string One(string expression)
        {
            return Run("print " + expression + ".").Single();
        }

        private static double Number(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        private const string Opt = "claudia_options(lexicon(), dock_defaults(), 1800):opt";

        /// <summary>dock_run on the rig: the reason, docked, the last monoprop read undocked and the distance at the end.</summary>
        private static string[] Fly(string options = "")
        {
            return Run("local opt is claudia_options(lexicon(" + options + "), dock_defaults(), 1800):opt.\n"
                + "local done is dock_run(opt, \"Station\", { return test_dk_state(). }, { parameter a, l. test_dk_push(a, l). }).\n"
                + "print done:reason. print done:docked. print done:monoprop. print done:state:distance.");
        }

        // ---- Options and the pure helpers ----

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("auto|auto|1|0.3|0.2|1800", One("dock_defaults():values:join(\"|\") + \"|\" + " + Opt + ":max_duration"));
            Assert.Equal("unknown option roll", One("claudia_options(lexicon(\"roll\", 90), dock_defaults(), 1800):error"));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("\"port\", \"nose\", \"target_port\", \"size1\", \"approach_speed\", 2", "")]
        [InlineData("\"port\", 3", "port must be a port's kOS tag, a size such as size1, or 'auto'")]
        [InlineData("\"target_port\", \"\"", "target_port must be a port's kOS tag, a size such as size1, or 'auto'")]
        [InlineData("\"final_speed\", 0", "final_speed must be above 0, not 0")]
        [InlineData("\"max_lateral\", -1", "max_lateral must be above 0, not -1")]
        [InlineData("\"final_speed\", 2", "final_speed must not be over approach_speed")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("dock_unreadable(claudia_options(lexicon(" + options + "), dock_defaults(), 1800):opt)"));
        }

        [Theory]
        // 1 m/s beyond 50 m, 0.3 m/s inside 10 m, a taper between, up to 5 m/s far out.
        [InlineData(2000, 5)]
        [InlineData(300, 3)]
        [InlineData(80, 1)]
        [InlineData(50, 1)]
        [InlineData(30, 0.65)]
        [InlineData(10, 0.3)]
        [InlineData(2, 0.3)]
        public void The_closing_speed_goes_by_distance(double distance, double speed)
        {
            Assert.Equal(speed, Number(One("dock_speed(" + distance.ToString(CultureInfo.InvariantCulture) + ", " + Opt + ")")), 9);
        }

        private static string State(double along, double lat, double align = 0)
        {
            var distance = Math.Sqrt(along * along + lat * lat);
            return string.Format(CultureInfo.InvariantCulture, "lexicon(\"along\", {0}, \"lat\", {1}, \"distance\", {2}, \"align_deg\", {3})", along, lat, distance, align);
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
        // Behind the port's face: out to 50 m off the axis first, then out past the face.
        [InlineData(-80, 10, 0, 0, 1)]
        [InlineData(-80, 60, 0, 1, 0)]
        public void The_wanted_velocity_lines_up_on_the_axis_before_it_closes(double along, double lat, double align, double wantAlong, double wantLat)
        {
            var printed = Run("local w is dock_want(" + State(along, lat, align) + ", " + Opt + "). print w:along. print w:lat.");
            Assert.Equal(wantAlong, Number(printed[0]), 6);
            Assert.Equal(wantLat, Number(printed[1]), 6);
        }

        // ---- The flight on the rig ----

        [Fact]
        public void A_target_80_m_away_docks_and_fills_the_contract()
        {
            DockRig.Reset(along: 70, lateral: 40, alongSpeed: 0.5);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("True", printed[1]);
            Assert.True(DockRig.Docked);
            // The capture threshold: the ports met inside 0.3 m, within 5 degrees, closing under 0.6 m/s.
            Assert.True(DockRig.CaptureDistance < DockRig.CaptureRange);
            Assert.True(DockRig.CaptureClosing < 0.6, "closing at " + DockRig.CaptureClosing);
            // It came in along the axis: inside 10 m of the port it stayed within max_lateral of it.
            Assert.True(DockRig.WorstLateralInside10 <= 0.2 + 1e-6, "lateral " + DockRig.WorstLateralInside10 + " m inside 10 m");
            Assert.True(DockRig.FastestInside10 <= 0.3 + 0.07, "closing at " + DockRig.FastestInside10 + " m/s inside 10 m");
            var used = DockRig.MonopropAtStart - Number(printed[2]);
            Assert.True(used > 0 && used < DockRig.MonopropAtStart, "monoprop used " + used);
        }

        [Fact]
        public void The_whole_block_fills_the_result_contract()
        {
            // dock()'s extra results, as it builds them from dock_run's: docked, port, target_port, monoprop_used.
            DockRig.Reset(along: 80, lateral: 0, alongSpeed: 0);
            var printed = Run("local opt is " + Opt + ".\n"
                + "local done is dock_run(opt, \"Station\", { return test_dk_state(). }, { parameter a, l. test_dk_push(a, l). }).\n"
                + "local extra is lexicon(\"docked\", done:docked, \"port\", dock_port_name(\"nose\", \"size0\"), \"target_port\", dock_port_name(\"\", \"size0\"), "
                + "\"monoprop_used\", round(max(0, " + DockRig.MonopropAtStart.ToString(CultureInfo.InvariantCulture) + " - done:monoprop), 1)).\n"
                + "print extra:docked. print extra:port. print extra:target_port. print extra:monoprop_used > 0. print done:reason = \"\".");
            Assert.Equal(new[] { "True", "nose", "size0", "True", "True" }, printed);
        }

        [Fact]
        public void A_target_behind_the_craft_s_back_is_passed_on_the_side()
        {
            // The craft starts behind the target port's face: it moves out 50 m off the axis, round, and in.
            DockRig.Reset(along: -40, lateral: 5, alongSpeed: 0);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.True(DockRig.Docked);
            Assert.True(DockRig.WidestBehind >= 49, "widest behind the face " + DockRig.WidestBehind + " m");
        }

        [Fact]
        public void A_dock_hold_fault_backs_the_craft_off_and_fails()
        {
            DockRig.Reset(along: 30, lateral: 0, alongSpeed: 0);
            DockRig.FaultAt = 20;
            var printed = Fly();
            Assert.StartsWith("dock_hold on Station stopped: blocked: DOEVENT on another vessel; backed off to ", printed[0]);
            Assert.Equal("False", printed[1]);
            Assert.True(DockRig.BackedOff >= 10 - 0.5, "backed off " + DockRig.BackedOff + " m");
            Assert.True(Math.Abs(DockRig.AlongSpeed) < 0.06, "still moving at " + DockRig.AlongSpeed + " m/s");
            Assert.False(DockRig.Docked);
        }

        [Fact]
        public void A_passive_target_turning_over_1_deg_s_is_refused()
        {
            DockRig.Reset(along: 80, lateral: 0, alongSpeed: 0, hold: "passive", holdReason: "the target has no kOS processor", spin: 3);
            var printed = Fly();
            Assert.Equal("Station turns at 3 deg/s, over 1 deg/s, with no dock_hold (the target has no kOS processor)", printed[0]);
            Assert.Equal(0, DockRig.Pushes);
        }

        [Fact]
        public void A_passive_target_turning_slowly_is_met_at_its_port_s_facing()
        {
            DockRig.Reset(along: 80, lateral: 3, alongSpeed: 0, hold: "passive", holdReason: "the target has no electric charge", spin: 0.5);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.True(DockRig.Docked);
        }

        [Fact]
        public void A_passive_target_that_starts_turning_close_in_is_backed_off_from()
        {
            DockRig.Reset(along: 30, lateral: 0, alongSpeed: 0, hold: "passive", holdReason: "dock_hold didn't start: busy", spin: 0);
            DockRig.SpinAt = 15;
            var printed = Fly();
            Assert.StartsWith("Station turns at 2 deg/s, over 1 deg/s, with no dock_hold (dock_hold didn't start: busy); backed off to ", printed[0]);
        }

        [Fact]
        public void Drifting_50_m_past_the_start_fails()
        {
            // No RCS authority: the craft keeps the 1 m/s it started with, away from the port.
            DockRig.Reset(along: 60, lateral: 0, alongSpeed: 1, authority: 0);
            var printed = Fly();
            Assert.Equal("drifted to 110 m from Station's port, 50 m past the start's 60 m", printed[0]);
        }

        [Fact]
        public void Running_out_of_monopropellant_fails()
        {
            DockRig.Reset(along: 80, lateral: 0, alongSpeed: 0, monoprop: 0.3);
            var printed = Fly();
            Assert.StartsWith("out of monopropellant ", printed[0]);
            Assert.EndsWith(" m from Station's port", printed[0]);
            Assert.Equal("False", printed[1]);
        }

        [Fact]
        public void An_abort_stops_the_flight_with_its_reason()
        {
            DockRig.Reset(along: 80, lateral: 0, alongSpeed: 0);
            DockRig.AbortAt = 30;
            DockRig.AbortReason = "abort max_duration: ran 1801 s, over 1800 s";
            var printed = Fly();
            Assert.Equal("abort max_duration: ran 1801 s, over 1800 s", printed[0]);
            Assert.Equal("False", printed[1]);
        }

        [Fact]
        public void An_abort_during_the_back_off_stops_it_too()
        {
            DockRig.Reset(along: 30, lateral: 0, alongSpeed: 0);
            DockRig.FaultAt = 10;
            DockRig.AbortAt = 15;
            DockRig.AbortReason = "abort min_ec_pct: electric charge 9% is below 10%";
            var printed = Fly();
            Assert.Equal("abort min_ec_pct: electric charge 9% is below 10%", printed[0]);
        }
    }

    /// <summary>
    /// The craft's port in the target port's frame: <c>along</c> out along the port's axis and a signed lateral offset off it,
    /// each with a velocity that the block's push steers toward its wanted value at the RCS's acceleration, in 0.2 s ticks.
    /// Capture is a threshold: the ports within <see cref="CaptureRange"/>, the craft's port within 5 degrees of the axis and
    /// closing under 0.6 m/s. The craft's port starts 20 degrees off and turns at 4 deg/s. Monoprop goes at 0.5 units per m/s.
    /// </summary>
    internal static class DockRig
    {
        public const double Dt = 0.2;
        public const double CaptureRange = 0.3;

        public static double Along, Lateral, AlongSpeed, LateralSpeed, Align, Spin, Monoprop, MonopropAtStart, Ut, Authority;
        public static string Hold = "", HoldReason = "", AbortReason = "";
        public static double FaultAt, AbortAt, SpinAt;
        public static bool Docked;
        public static int Pushes;
        public static double CaptureDistance, CaptureClosing, WorstLateralInside10, FastestInside10, WidestBehind, BackedOff;
        private static double _faultDistance;

        public static void Reset(double along, double lateral, double alongSpeed, string hold = "active", string holdReason = "", double spin = 0,
            double monoprop = 40, double authority = 0.3)
        {
            Along = along;
            Lateral = lateral;
            AlongSpeed = alongSpeed;
            LateralSpeed = 0;
            Align = 20;
            Spin = spin;
            Monoprop = MonopropAtStart = monoprop;
            Ut = 0;
            Authority = authority;
            Hold = hold;
            HoldReason = holdReason;
            AbortReason = "";
            FaultAt = AbortAt = SpinAt = double.MaxValue;
            Docked = false;
            Pushes = 0;
            CaptureDistance = CaptureClosing = double.MaxValue;
            WorstLateralInside10 = FastestInside10 = WidestBehind = BackedOff = 0;
            _faultDistance = -1;
        }

        private static double Distance { get { return Math.Sqrt(Along * Along + Lateral * Lateral); } }

        public static IDictionary<string, object> State()
        {
            var abort = Ut >= AbortAt ? AbortReason : "";
            var hold = Hold;
            var reason = HoldReason;
            if (Ut >= FaultAt && hold == "active")
            {
                hold = "fault";
                reason = "blocked: DOEVENT on another vessel";
                if (_faultDistance < 0) _faultDistance = Distance;
            }
            if (_faultDistance >= 0) BackedOff = Math.Max(BackedOff, Distance - _faultDistance);
            var spin = Ut >= SpinAt ? 2 : Spin;
            return new Dictionary<string, object>
            {
                { "abort", abort }, { "docked", Docked }, { "distance", Distance }, { "along", Along }, { "lat", Math.Abs(Lateral) },
                { "speed", Math.Sqrt(AlongSpeed * AlongSpeed + LateralSpeed * LateralSpeed) }, { "align_deg", Align }, { "spin_dps", spin },
                { "monoprop", Monoprop }, { "hold", hold }, { "hold_reason", reason }, { "ut", Ut },
            };
        }

        /// <summary>One tick toward the wanted relative velocity: along the axis, and off it (away from the axis, positive).</summary>
        public static void Push(double wantAlong, double wantLat)
        {
            Pushes++;
            var side = Lateral >= 0 ? 1 : -1;
            var step = Authority * Dt;
            var dAlong = Math.Max(-step, Math.Min(step, wantAlong - AlongSpeed));
            var dLateral = Math.Max(-step, Math.Min(step, wantLat * side - LateralSpeed));
            if (Monoprop > 0)
            {
                AlongSpeed += dAlong;
                LateralSpeed += dLateral;
                Monoprop = Math.Max(0, Monoprop - 0.5 * (Math.Abs(dAlong) + Math.Abs(dLateral)));
            }
            Along += AlongSpeed * Dt;
            Lateral += LateralSpeed * Dt;
            Align = Math.Max(0, Align - 4 * Dt);
            Ut += Dt;
            if (Along < 0) WidestBehind = Math.Max(WidestBehind, Math.Abs(Lateral));
            if (Distance < 10 && Along > 0)
            {
                WorstLateralInside10 = Math.Max(WorstLateralInside10, Math.Abs(Lateral));
                FastestInside10 = Math.Max(FastestInside10, -AlongSpeed);
            }
            if (Distance < CaptureRange && Align < 5 && -AlongSpeed < 0.6)
            {
                Docked = true;
                CaptureDistance = Distance;
                CaptureClosing = -AlongSpeed;
            }
        }
    }

    internal static class DockRigFunctions
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            foreach (var type in typeof(DockRigFunctions).GetNestedTypes())
                foreach (FunctionAttribute attribute in type.GetCustomAttributes(typeof(FunctionAttribute), false))
                    FunctionManager.RegisterMethod(attribute, type);
            _registered = true;
        }

        [Function("test_dk_state")]
        public class FunctionState : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                AssertArgBottomAndConsume(shared);
                ReturnValue = KosPlain.From(DockRig.State());
            }
        }

        [Function("test_dk_push")]
        public class FunctionPush : SafeFunctionBase
        {
            public override void Execute(SafeSharedObjects shared)
            {
                var lat = GetDouble(PopStructureAssertEncapsulated(shared, true));
                var along = GetDouble(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                DockRig.Push(along, lat);
                ReturnValue = new BooleanValue(true);
            }
        }
    }
}
