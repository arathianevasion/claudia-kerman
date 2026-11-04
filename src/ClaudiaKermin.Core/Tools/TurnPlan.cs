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
        /// <c>corrections</c> (missing for a turn's first plan; else those taken). Gives <c>error</c> ("" or "no usable torque"), <c>phase</c> ("": not flown yet),
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
                { "phase", "" },
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

        /// <summary>The wheels settle at most this long (s) after the last stop burst before RCS Use's rule takes over again.</summary>
        public const double SettleMaxSeconds = 30;

        /// <summary>
        /// One tick of a burst turn, through the block-internal <c>ADDONS:CLAUDIA:BURSTTURN</c>: <paramref name="turn"/> is
        /// <see cref="Call"/>'s plan the first time and this call's answer after; <paramref name="now"/> gives <c>ut</c>,
        /// <c>error</c> (deg), <c>rate</c> (deg/s), <c>left_s</c> (-1: no deadline), <c>aligned</c>, <c>monoprop</c> (units),
        /// and <c>energy</c> (J/kg) and <c>speed</c> (m/s) of the orbit. The spin-up ends at the coast rate (or twice its time),
        /// the coast at <c>stop_deg</c>, the stop burst after its time; then the correction rule (<see cref="Plan"/> on
        /// <paramref name="craft"/>) flies one more burst pair, or the turn reports and the wheels settle until Aligned or
        /// <see cref="SettleMaxSeconds"/>. Gives the turn back with <c>phase</c>, <c>rcs_on</c> (what RCS should be: on in spin
        /// and stop only) and <c>done</c>; <c>tuning</c> (kOS's <c>t</c>, <c>kp</c>, <c>ki</c>) when a phase begins; and
        /// <c>report</c>, the turn's one RCS Use line (#692's figures), as the wheels take over.
        /// </summary>
        public static IDictionary<string, object> Step(IDictionary<string, object> turn, IDictionary<string, object> now, Func<TurnCraft> craft)
        {
            var t = new Dictionary<string, object>(turn ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            t.Remove("tuning");
            t.Remove("report");
            t["done"] = false;
            var ut = Number(now, "ut", 0);
            var error = Number(now, "error", 0);
            var rate = Number(now, "rate", 0);
            var monoprop = Number(now, "monoprop", 0);
            var phase = Text(t, "phase");
            if (phase == "")
            {
                foreach (var key in new[] { "spin_total", "stop_total", "corrections", "flown" }) t[key] = 0.0;
                t["mono"] = monoprop;
                t["energy0"] = Number(now, "energy", 0);
                t["plan_dps"] = Number(t, "coast_rate", 0);
                return Enter(t, "spin", ut);
            }
            var took = ut - Number(t, "ut", ut);
            if (phase == "spin" && (rate >= Number(t, "coast_rate", 0) || took >= 2 * Number(t, "spin_s", 0)))
            {
                t["spin_total"] = Number(t, "spin_total", 0) + took;
                return Enter(t, "coast", ut);
            }
            if (phase == "coast")
            {
                t["flown"] = Math.Max(Number(t, "flown", 0), rate);
                return error <= Number(t, "stop_deg", 0) ? Enter(t, "stop", ut) : t;
            }
            if (phase == "stop" && took >= Number(t, "stop_s", 0))
            {
                t["stop_total"] = Number(t, "stop_total", 0) + took;
                var corrections = Number(t, "corrections", 0);
                var next = Call(new Dictionary<string, object>
                {
                    { "angle", error }, { "left_s", Number(now, "left_s", -1) }, { "corrections", corrections },
                }, craft());
                if ((bool)next["rcs"])
                {
                    foreach (var entry in next) if (entry.Key != "phase") t[entry.Key] = entry.Value;
                    t["corrections"] = corrections + 1;
                    return Enter(t, "spin", ut);
                }
                var speed = Number(now, "speed", 0);
                t["report"] = new Dictionary<string, object>
                {
                    { "coast_dps", Number(t, "plan_dps", 0) },
                    { "flown_dps", Number(t, "flown", 0) },
                    { "spin_s", Number(t, "spin_total", 0) },
                    { "stop_s", Number(t, "stop_total", 0) },
                    { "monoprop_used", Number(t, "mono", 0) - monoprop },
                    { "left_deg", error },
                    { "corrections", corrections },
                    { "dv_mps", speed > 0 ? Math.Abs(Number(now, "energy", 0) - Number(t, "energy0", 0)) / speed : 0.0 },
                };
                t["phase"] = "settle";
                t["ut"] = ut;
                t["rcs_on"] = false;
                return t;
            }
            if (phase == "settle" && (took > SettleMaxSeconds || Bool(now, "aligned"))) t["done"] = true;
            return t;
        }

        /// <summary>Puts turn <paramref name="t"/> in <paramref name="phase"/> at <paramref name="ut"/>, with RCS on but in the coast and the phase's tuning.</summary>
        private static IDictionary<string, object> Enter(Dictionary<string, object> t, string phase, double ut)
        {
            t["phase"] = phase;
            t["ut"] = ut;
            t["rcs_on"] = phase != "coast";
            object tuning;
            t["tuning"] = t.TryGetValue(phase, out tuning) ? tuning : Lexicon(new SteeringTuning { T = TurnTime.KosMaxStoppingTime, Kp = 1, Ki = 0.1 });
            return t;
        }

        private static string Text(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static bool Bool(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value is bool && (bool)value;
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
