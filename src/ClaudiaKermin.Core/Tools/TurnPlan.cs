// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>kOS steering settings for one phase of a burst turn: <c>maxstoppingtime</c> (s) and the rate controllers' kp and ki.</summary>
    public sealed class SteeringTuning
    {
        public double T, Kp, Ki;
    }

    /// <summary>A burst turn through an angle (<see cref="TurnPlan"/>); every angle is in degrees and every time in seconds.</summary>
    public sealed class BurstTurn
    {
        /// <summary>Whether RCS should fly this turn: the wheels alone can't make it in time (after a stop burst, the correction rule).</summary>
        public bool Rcs;

        /// <summary>The turn on the reaction wheels alone, with the Aligned hold on top; -1 when they have no torque about pitch or yaw.</summary>
        public double WheelsS;

        /// <summary>The coast rate (deg/s); 0 when there is no burst turn to fly (no RCS torque, or no wheels to settle it).</summary>
        public double CoastRateDegS;

        /// <summary>The spin-up burst and the stop burst, the same length; and the coast between them.</summary>
        public double SpinS, StopS, CoastS;

        /// <summary>The error at which the stop burst starts, and the error it leaves for the wheels to settle (where they take over).</summary>
        public double StopDeg, WheelsDeg;

        /// <summary>The whole turn as planned: the bursts, the coast and the wheels' settle.</summary>
        public double TurnS;

        /// <summary>The angular acceleration (deg/s²) on the wheels and RCS together, the weaker of pitch and yaw.</summary>
        public double AlphaDegS2;

        /// <summary>kOS's steering in each phase: up to the coast rate on RCS, holding it on the wheels, braking on RCS.</summary>
        public SteeringTuning Spin = new SteeringTuning(), Coast = new SteeringTuning(), Stop = new SteeringTuning();

        /// <summary>Null, or why the craft can't turn (<see cref="TurnTime.NoTorque"/>).</summary>
        public string Error;
    }

    /// <summary>
    /// The burst turn (#692, ADR 0014), through the block-internal <c>ADDONS:CLAUDIA:TURNPLAN</c> (<see cref="Call"/>), called once
    /// per turn when RCS Use's on-gate is checked and once after each stop burst. RCS turns the craft as a pilot would: one burst
    /// spins it up to a coast rate, it coasts with RCS off, one burst stops it a few degrees short, and the wheels settle the rest.
    /// The coast rate is the slowest that finishes within 80% of the time left (to <c>turn_by</c>, and never over the 120 s a turn
    /// to a burn waits), since a slower turn spends less monopropellant. kOS still switches RCS and steers; this only plans.
    /// </summary>
    public static class TurnPlan
    {
        /// <summary>A turn plans to be done within this share of the time left.</summary>
        public const double MarginShare = 0.8;

        /// <summary>With no deadline, a turn plans to take this share of the wheels' own time.</summary>
        public const double NoDeadlineShare = 0.5;

        /// <summary>The longest a turn plans for (s): claudia_turn_to_burn gives up after 120 s.</summary>
        public const double MaxSeconds = 120;

        /// <summary>The wheels' time to settle once they hold the target (s): Aligned's 3 s hold.</summary>
        public const double SettleSeconds = 3;

        /// <summary>The stop burst leaves this much (deg), or a quarter of a smaller turn, for the wheels to settle.</summary>
        public const double HandOverDeg = 3;

        /// <summary>The coast is never slower than this (deg/s), unless the turn's fastest is.</summary>
        public const double MinCoastDegS = 0.5;

        /// <summary>The stop burst's <c>maxstoppingtime</c> (s): a low cap, so kOS brakes on full torque.</summary>
        public const double StopStoppingTime = 0.5;

        /// <summary>A leftover over this (deg) after a stop burst may take a correction ...</summary>
        public const double CorrectionDeg = 10;

        /// <summary>... and a turn takes at most this many.</summary>
        public const int MaxCorrections = 2;

        private const double Deg = 180 / Math.PI;

        /// <summary>
        /// The burst turn <paramref name="craft"/> flies through <paramref name="angleDeg"/> with <paramref name="leftS"/> s to its
        /// deadline (negative: none). <paramref name="corrections"/> is negative for a turn's first plan; after a stop burst it is
        /// the corrections already taken, and <see cref="BurstTurn.Rcs"/> is the correction rule: the leftover is over
        /// <see cref="CorrectionDeg"/>, the wheels can't close it by the deadline, and fewer than <see cref="MaxCorrections"/>
        /// were taken.
        /// </summary>
        public static BurstTurn Plan(TurnCraft craft, double angleDeg, double leftS, int corrections = -1)
        {
            var angle = Math.Max(0, angleDeg);
            var alphaW = TurnTime.Alpha(craft, false) * Deg;
            var alpha = TurnTime.Alpha(craft, craft.RcsUsable) * Deg;
            var plan = new BurstTurn { WheelsS = -1, AlphaDegS2 = Finite(alpha) };
            if (!(alpha > 0) || double.IsInfinity(alpha)) { plan.Error = TurnTime.NoTorque; return plan; }
            double peak;
            var wheels = alphaW > 0 && !double.IsInfinity(alphaW);
            if (wheels) plan.WheelsS = TurnTime.CappedTurn(angle, alphaW, TurnTime.KosMaxStoppingTime, out peak) + SettleSeconds;
            // A burst needs RCS torque to add, and wheels to hold the coast and settle the end (with none, RCS Use holds RCS on).
            if (!wheels || !(alpha > alphaW) || !(angle > 0)) return plan;

            var budget = leftS >= 0 ? Math.Min(leftS, MaxSeconds) * MarginShare : Math.Min(plan.WheelsS, MaxSeconds) * NoDeadlineShare;
            plan.WheelsDeg = Math.Min(HandOverDeg, angle / 4);
            var settle = TurnTime.CappedTurn(plan.WheelsDeg, alphaW, TurnTime.KosMaxStoppingTime, out peak) + SettleSeconds;
            var rcsDeg = angle - plan.WheelsDeg;
            var time = budget - settle;
            // Bursts of rate / alpha each and a coast at rate: rcsDeg / rate + rate / alpha = time; the slower root spends least.
            var fastest = Math.Sqrt(alpha * rcsDeg);
            var rate = time > 0 && alpha * time * time >= 4 * rcsDeg
                ? (alpha * time - Math.Sqrt(alpha * alpha * time * time - 4 * alpha * rcsDeg)) / 2
                : fastest;
            rate = Math.Max(rate, Math.Min(MinCoastDegS, fastest));
            var burstDeg = rate * rate / (2 * alpha);
            plan.CoastRateDegS = rate;
            plan.SpinS = plan.StopS = rate / alpha;
            plan.CoastS = Math.Max(0, (rcsDeg - 2 * burstDeg) / rate);
            plan.StopDeg = plan.WheelsDeg + burstDeg;
            plan.TurnS = plan.SpinS + plan.CoastS + plan.StopS + settle;
            // kOS caps the rate at maxstoppingtime x its torque over the MOI (RCS counted only while on), and asks for kp x error:
            // a cap at the coast rate on each phase's torque, and a kp that keeps kp x error over it until the stop burst.
            var kp = 2 * rate / plan.StopDeg;
            plan.Spin = new SteeringTuning { T = rate / alpha, Kp = kp };
            plan.Coast = new SteeringTuning { T = rate / alphaW, Kp = kp };
            plan.Stop = new SteeringTuning { T = StopStoppingTime, Kp = kp };

            var late = leftS >= 0 && plan.WheelsS > leftS;
            plan.Rcs = late && (corrections < 0 || (corrections < MaxCorrections && angle > CorrectionDeg));
            return plan;
        }

        /// <summary>
        /// The answer to a block's ask: <c>angle</c> (deg), <c>left_s</c> (s to the deadline; -1 or missing for none) and
        /// <c>corrections</c> (missing for a turn's first plan; else those taken). Gives <c>error</c> ("" or "no usable torque"),
        /// <c>rcs</c>, <c>wheels_s</c>, <c>coast_rate</c> (deg/s), <c>spin_s</c>, <c>coast_s</c>, <c>stop_s</c>,
        /// <c>stop_deg</c>, <c>wheels_deg</c>, <c>turn_s</c>, <c>alpha</c> (deg/s²) and lexicons <c>spin</c>, <c>coast</c>
        /// and <c>stop</c> of kOS's <c>t</c>, <c>kp</c> and <c>ki</c> for each phase.
        /// </summary>
        public static IDictionary<string, object> Call(IDictionary<string, object> input, TurnCraft craft)
        {
            var plan = Plan(craft, Number(input, "angle", 0), Number(input, "left_s", -1), (int)Math.Round(Number(input, "corrections", -1)));
            return new Dictionary<string, object>
            {
                { "error", plan.Error ?? "" },
                { "rcs", plan.Rcs },
                { "wheels_s", plan.WheelsS },
                { "coast_rate", plan.CoastRateDegS },
                { "spin_s", plan.SpinS },
                { "coast_s", plan.CoastS },
                { "stop_s", plan.StopS },
                { "stop_deg", plan.StopDeg },
                { "wheels_deg", plan.WheelsDeg },
                { "turn_s", plan.TurnS },
                { "alpha", plan.AlphaDegS2 },
                { "spin", Lexicon(plan.Spin) },
                { "coast", Lexicon(plan.Coast) },
                { "stop", Lexicon(plan.Stop) },
            };
        }

        private static IDictionary<string, object> Lexicon(SteeringTuning tuning)
        {
            return new Dictionary<string, object> { { "t", tuning.T }, { "kp", tuning.Kp }, { "ki", tuning.Ki } };
        }

        private static double Finite(double x)
        {
            return double.IsNaN(x) || double.IsInfinity(x) ? 0 : x;
        }

        private static double Number(IDictionary<string, object> input, string key, double fallback)
        {
            object value;
            if (input == null || !input.TryGetValue(key, out value) || value == null || value is string || value is bool) return fallback;
            var x = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(x) ? fallback : x;
        }
    }
}
