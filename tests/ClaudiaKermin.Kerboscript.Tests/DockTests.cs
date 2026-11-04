// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Tools;
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
    /// The rig's push runs Core's <see cref="DockGuidance"/> as DOCKGUIDE does (#706); its own rules are DockGuidanceTests'.
    /// Flying it in KSP is tests/blocks/dock (#457).
    /// </summary>
    public class DockTests
    {
        static DockTests()
        {
            KosHarness.Register(typeof(DockRigFunctions));
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
                + "local done is dock_run(\"Station\", { return test_dk_state(). }, { parameter s, mode. test_dk_push(s, opt, mode). }).\n"
                + "print done:reason. print done:docked. print done:monoprop. print done:state:distance.");
        }

        // ---- Options and the pure helpers ----

        [Fact]
        public void The_options_take_the_spec_defaults()
        {
            Assert.Equal("auto|auto|1|0.3|0.2|300|1800", One("dock_defaults():values:join(\"|\") + \"|\" + " + Opt + ":max_duration"));
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
        [InlineData("\"settle_timeout\", 0", "settle_timeout must be above 0, not 0")]
        public void An_option_value_it_cannot_use_is_refused(string options, string reason)
        {
            Assert.Equal(reason, One("dock_unreadable(claudia_options(lexicon(" + options + "), dock_defaults(), 1800):opt)"));
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
                + "local done is dock_run(\"Station\", { return test_dk_state(). }, { parameter s, mode. test_dk_push(s, opt, mode). }).\n"
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
        public void From_150_m_the_craft_coasts_for_most_of_the_approach()
        {
            // #706: RCS translated every tick and drained the tank. With the coast band it fires only when the velocity error
            // leaves 0.05 to 0.1 m/s.
            DockRig.Reset(along: 150, lateral: 3, alongSpeed: 0);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.True(DockRig.Docked);
            Assert.True(DockRig.FiredTicks < 0.2 * DockRig.Ticks, "RCS translated on " + DockRig.FiredTicks + " of " + DockRig.Ticks + " ticks");
            var used = DockRig.MonopropAtStart - Number(printed[2]);
            Assert.True(used < 0.15 * DockRig.MonopropAtStart, "monoprop used " + used + " of " + DockRig.MonopropAtStart);
        }

        [Fact]
        public void From_300_m_mostly_sideways_it_waits_for_the_station_to_turn_then_coasts_in()
        {
            // #711's Mission: rendezvous handed off 296 m out (along 60 m, lateral 290 m), and dock_hold took minutes to turn the
            // station's port to the craft. Chasing the turning axis translated on 484 of 522 trace lines. Now the craft only
            // nulls its relative velocity until the hold settles, and then flies one speed a stretch, coasting between burns.
            DockRig.Reset(along: 60, lateral: 290, alongSpeed: 0.2, monoprop: 120);
            DockRig.Turning(Math.Atan2(290, 60) * 180 / Math.PI, 120);
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("True", printed[1]);
            Assert.True(DockRig.Docked);
            Assert.True(DockRig.FiredTicks <= 0.3 * DockRig.Ticks, "RCS translated on " + DockRig.FiredTicks + " of " + DockRig.Ticks + " ticks");
            Assert.True(DockRig.WorstLateralInside10 <= 0.2 + 1e-6, "lateral " + DockRig.WorstLateralInside10 + " m inside 10 m");
        }

        [Fact]
        public void Captured_while_the_merged_vessel_turns_over_1_deg_s_is_success_with_no_back_off()
        {
            // #711: dock_hold reported docked and ended, the craft's port wasn't read Docked yet, and the merged vessel turned at
            // 1 deg/s: dock_run read a passive target spinning, backed off and failed. Any docked state comes first now.
            DockRig.Reset(along: 20, lateral: 0, alongSpeed: 0);
            DockRig.CaptureLag = 1000;
            DockRig.CaptureSpin = 1.5;
            var printed = Fly();
            Assert.Equal("", printed[0]);
            Assert.Equal("True", printed[1]);
            Assert.Equal(0, DockRig.PushesAfterCapture);
            Assert.Equal(0, DockRig.BackedOff);
        }

        [Fact]
        public void At_15_percent_of_the_starting_monoprop_it_backs_off_and_fails()
        {
            // Too little to close from 30 m: at 15% left it stops closing, backs off and fails, rather than run dry at the port.
            DockRig.Reset(along: 30, lateral: 0, alongSpeed: 0, monoprop: 0.4);
            var printed = Fly();
            Assert.Matches("^monopropellant down to 0(\\.[0-9]+)? units, 15% of the start's, [0-9.]+ m from Station's port; backed off to [0-9]+ m$", printed[0]);
            Assert.Equal("False", printed[1]);
            Assert.True(Number(printed[2]) <= 0.15 * 0.4 + 1e-9, "monoprop " + printed[2]);
            Assert.False(DockRig.Docked);
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
        public static bool Docked, Firing;
        public static int Pushes, Ticks, FiredTicks, PushesAfterCapture;
        public static double CaptureDistance, CaptureClosing, WorstLateralInside10, FastestInside10, WidestBehind, BackedOff;
        /// <summary>
        /// The target port's axis turning toward the craft (dock_hold, #711): degrees still to turn and the rate (deg/s). The rig
        /// rotates the craft's position and velocity in the port's frame by it each tick, and reports the hold settled
        /// <see cref="SettleAfter"/> s after the turn ends.
        /// </summary>
        public static double TurnLeft, TurnRate, SettleAfter, TurnEndedAt;
        /// <summary>
        /// Ticks after capture during which the craft's own port isn't yet read as docked, while dock_hold reports docked and the
        /// merged vessel turns at <see cref="CaptureSpin"/> deg/s (#711's failure).
        /// </summary>
        public static int CaptureLag;
        public static double CaptureSpin;
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
            Docked = Firing = false;
            Pushes = Ticks = FiredTicks = PushesAfterCapture = 0;
            CaptureDistance = CaptureClosing = double.MaxValue;
            WorstLateralInside10 = FastestInside10 = WidestBehind = BackedOff = 0;
            TurnLeft = TurnRate = SettleAfter = 0;
            TurnEndedAt = double.MaxValue;
            CaptureLag = 0;
            CaptureSpin = 0;
            _faultDistance = -1;
        }

        /// <summary>dock_hold turns the target's port by <paramref name="degrees"/> toward the craft over <paramref name="seconds"/>, then settles.</summary>
        public static void Turning(double degrees, double seconds, double settleAfter = 2)
        {
            TurnLeft = degrees;
            TurnRate = degrees / seconds;
            SettleAfter = settleAfter;
            Spin = TurnRate;
        }

        /// <summary>
        /// The hold the rig reports: an active one is settled once its turn is over and the settle time has passed (at once with
        /// no turn), and active while it turns.
        /// </summary>
        private static string HoldNow()
        {
            if (Hold != "active") return Hold;
            return TurnRate <= 0 || Ut >= TurnEndedAt + SettleAfter ? "settled" : "active";
        }

        private static double Distance { get { return Math.Sqrt(Along * Along + Lateral * Lateral); } }

        public static IDictionary<string, object> State()
        {
            var abort = Ut >= AbortAt ? AbortReason : "";
            var hold = HoldNow();
            var reason = HoldReason;
            var docked = Docked;
            var spin = Ut >= SpinAt ? 2 : Spin;
            if (Docked)
            {
                // Captured: dock_hold has ended "docked" and the merged vessel turns, while the craft's port reads docked only after the lag.
                hold = "docked";
                spin = CaptureSpin;
                docked = PushesAfterCapture >= CaptureLag;
            }
            if (Ut >= FaultAt && (hold == "active" || hold == "settled"))
            {
                hold = "fault";
                reason = "blocked: DOEVENT on another vessel";
                if (_faultDistance < 0) _faultDistance = Distance;
            }
            if (_faultDistance >= 0) BackedOff = Math.Max(BackedOff, Distance - _faultDistance);
            return new Dictionary<string, object>
            {
                { "abort", abort }, { "docked", docked }, { "distance", Distance }, { "along", Along }, { "lat", Math.Abs(Lateral) },
                { "speed", Math.Sqrt(AlongSpeed * AlongSpeed + LateralSpeed * LateralSpeed) },
                { "v_along", AlongSpeed }, { "v_lat", Lateral >= 0 ? LateralSpeed : -LateralSpeed }, { "v_side", 0.0 }, { "align_deg", Align }, { "spin_dps", spin },
                { "monoprop", Monoprop }, { "hold", hold }, { "hold_reason", reason }, { "ut", Ut },
            };
        }

        /// <summary>
        /// One tick of dock's push: <see cref="DockGuidance.Guide"/> on the state dock_run read, as DOCKGUIDE answers it, and the
        /// RCS command it gives (clipped to 1 an axis, of <see cref="Authority"/> m/s²) on the ticks it translates.
        /// </summary>
        public static void Push(IDictionary<string, object> state, IDictionary<string, object> opt, string mode)
        {
            Pushes++;
            var input = new Dictionary<string, object>(state);
            input["mode"] = mode;
            input["firing"] = Firing;
            foreach (var key in new[] { "approach_speed", "final_speed", "max_lateral" }) input[key] = opt[key];
            var tick = DockGuidance.Guide(input);
            Firing = (bool)tick["fire"];
            Ticks++;
            var side = Lateral >= 0 ? 1 : -1;
            if (Firing && Monoprop > 0)
            {
                FiredTicks++;
                var dAlong = Math.Max(-1, Math.Min(1, (double)tick["cmd_along"])) * Authority * Dt;
                var dLateral = Math.Max(-1, Math.Min(1, (double)tick["cmd_lat"])) * Authority * Dt;
                AlongSpeed += dAlong;
                LateralSpeed += dLateral * side;
                Monoprop = Math.Max(0, Monoprop - 0.5 * (Math.Abs(dAlong) + Math.Abs(dLateral)));
            }
            if (Docked) PushesAfterCapture++;
            Along += AlongSpeed * Dt;
            Lateral += LateralSpeed * Dt;
            Align = Math.Max(0, Align - 4 * Dt);
            if (TurnLeft > 0)
            {
                // The port's axis turns toward the craft: the craft's position and velocity, read in the port's frame, turn the other way.
                var step = Math.Min(TurnLeft, TurnRate * Dt);
                TurnLeft -= step;
                var a = -step * Math.PI / 180 * (Lateral >= 0 ? 1 : -1);
                double cos = Math.Cos(a), sin = Math.Sin(a);
                double along = Along, lateral = Lateral, vAlong = AlongSpeed, vLateral = LateralSpeed;
                Along = along * cos - lateral * sin;
                Lateral = along * sin + lateral * cos;
                AlongSpeed = vAlong * cos - vLateral * sin;
                LateralSpeed = vAlong * sin + vLateral * cos;
                if (TurnLeft <= 0)
                {
                    TurnEndedAt = Ut + Dt;
                    Spin = 0;
                }
            }
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
                var mode = PopStructureAssertEncapsulated(shared, true).ToString();
                var opt = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                var state = (IDictionary<string, object>)KosPlain.To(PopStructureAssertEncapsulated(shared, true));
                AssertArgBottomAndConsume(shared);
                DockRig.Push(state, opt, mode);
                ReturnValue = new BooleanValue(true);
            }
        }
    }
}
