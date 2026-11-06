// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// What the turn-time model needs of a craft on a Coast: the torque it can turn with (kN·m, the weaker of the two directions
    /// on each axis) and its moment of inertia (t·m²) about pitch and yaw. Reaction wheels always count; RCS counts whenever the
    /// craft has MonoPropellant, whatever the RCS switch reads (RCS Use switches it on when the wheels need help). Gimbals and
    /// control surfaces don't: there's no thrust or air on a Coast. The adapter reads these from the game.
    /// </summary>
    public sealed class TurnCraft
    {
        /// <summary>The running reaction wheels' torque (kN·m) about pitch and yaw.</summary>
        public double WheelPitch;
        public double WheelYaw;

        /// <summary>
        /// The enabled RCS thrusters' torque (kN·m) about pitch and yaw, as kOS counts it (<see cref="RcsTorque"/>); counted only
        /// when <see cref="RcsUsable"/>.
        /// </summary>
        public double RcsPitch;
        public double RcsYaw;

        /// <summary>The craft has MonoPropellant aboard, so its thrusters can fire once RCS is switched on.</summary>
        public bool RcsUsable;

        /// <summary>The moment of inertia (t·m²) about pitch and yaw.</summary>
        public double MoiPitch;
        public double MoiYaw;
    }

    /// <summary>A predicted turn: <see cref="Seconds"/> to leave before a burn, or <see cref="Error"/> saying why there's none.</summary>
    public sealed class TurnPrediction
    {
        /// <summary>The time to leave for the turn (s): the turn, the settle time, and at least <see cref="TurnTime.FloorSeconds"/>.</summary>
        public double Seconds;

        /// <summary>The 190° turn alone (s), before the settle time and the floor.</summary>
        public double TurnSeconds;

        /// <summary>The top turn rate (deg/s): kOS's cap, or the peak half the turn reaches if that's lower.</summary>
        public double TopRateDegS;

        /// <summary>The angular acceleration the turn was timed at (deg/s²), the weaker of pitch and yaw.</summary>
        public double AlphaDegS2;

        /// <summary>Null, or why the craft can't turn (<see cref="TurnTime.NoTorque"/>).</summary>
        public string Error;
    }

    /// <summary>
    /// How long a craft takes to turn to a burn on a Coast (spec #314), so a block leaves enough time before it. The turn is a
    /// conservative 190° about the craft's slower transverse axis: full torque speeding up for the first half and slowing down
    /// for the second, capped at kOS steering's top turn rate. A settle time for the alignment check (within 2° and turning under
    /// 0.5°/s) goes on top, and the answer is never under 30 s. The settle time is a starting value, tuned by block-test runs.
    /// </summary>
    public static class TurnTime
    {
        /// <summary>The turn timed: past a half turn, so a burn straight behind the craft is covered whichever way it goes.</summary>
        public const double TurnDegrees = 190;

        /// <summary>Added for the alignment check to settle (s); a starting value.</summary>
        public const double SettleSeconds = 10;

        /// <summary>The least time left for a turn (s), however quick the craft.</summary>
        public const double FloorSeconds = 30;

        /// <summary>kOS's default <c>steeringmanager:maxstoppingtime</c> (s).</summary>
        public const double KosMaxStoppingTime = 2;

        /// <summary>The reason given for a craft with nothing to turn with on a Coast.</summary>
        public const string NoTorque = "no usable torque";

        /// <summary>
        /// The turn <paramref name="craft"/> makes under kOS steering with <paramref name="maxStoppingTime"/> (s, its
        /// <c>maxstoppingtime</c>), which caps the turn rate at that many seconds of the craft's angular acceleration.
        /// </summary>
        public static TurnPrediction Predict(TurnCraft craft, double maxStoppingTime = KosMaxStoppingTime)
        {
            return Predict(craft, craft.RcsUsable, maxStoppingTime);
        }

        /// <summary>
        /// The same turn on the reaction wheels alone, for RCS Use to judge whether the wheels make it in time; an
        /// <see cref="TurnPrediction.Error"/> when the wheels have no torque about pitch or yaw.
        /// </summary>
        public static TurnPrediction PredictWheels(TurnCraft craft, double maxStoppingTime = KosMaxStoppingTime)
        {
            return Predict(craft, false, maxStoppingTime);
        }

        private static TurnPrediction Predict(TurnCraft craft, bool withRcs, double maxStoppingTime)
        {
            var alpha = Alpha(craft, withRcs);
            if (!(alpha > 0) || double.IsInfinity(alpha)) return new TurnPrediction { Error = NoTorque };

            var angle = TurnDegrees * Math.PI / 180;
            var alphaDeg = alpha * 180 / Math.PI;
            // RCS Use flies the burst turn (#692) where RCS adds torque to wheels that can settle it: the turn is TurnPlan's, with
            // no deadline (#730). Without wheels, or with thrusters that add nothing, the capped model on the torque there is stands.
            if (withRcs)
            {
                var plan = TurnPlan.Plan(craft, TurnDegrees, -1);
                if (plan.Error == null && plan.TurnS > 0)
                    return new TurnPrediction
                    {
                        Seconds = Math.Max(FloorSeconds, plan.TurnS + SettleSeconds),
                        TurnSeconds = plan.TurnS,
                        TopRateDegS = plan.CoastRateDegS,
                        AlphaDegS2 = alphaDeg,
                    };
            }
            double peak;
            var turn = CappedTurn(angle, alpha, maxStoppingTime, out peak);
            return new TurnPrediction
            {
                Seconds = Math.Max(FloorSeconds, turn + SettleSeconds),
                TurnSeconds = turn,
                TopRateDegS = peak * 180 / Math.PI,
                AlphaDegS2 = alphaDeg,
            };
        }

        /// <summary>
        /// Seconds to turn <paramref name="angle"/> at <paramref name="alpha"/> (same angle units) under kOS steering with
        /// <paramref name="maxStoppingTime"/> (s; 2 if not over 0), which caps the rate at that many seconds of alpha: full
        /// torque speeding up for the first half and slowing down for the second, cruising at the cap if it's reached.
        /// <paramref name="peak"/> is the top rate.
        /// </summary>
        public static double CappedTurn(double angle, double alpha, double maxStoppingTime, out double peak)
        {
            // kOS's steering asks for no more than it could stop from in maxstoppingtime at full torque.
            var cap = alpha * (maxStoppingTime > 0 ? maxStoppingTime : KosMaxStoppingTime);
            peak = Math.Sqrt(alpha * angle);
            if (peak <= cap) return 2 * Math.Sqrt(angle / alpha);
            // Up to the cap and down from it take cap / alpha between them; the rest of the angle is at the cap.
            peak = cap;
            return angle / cap + cap / alpha;
        }

        /// <summary>The weaker of pitch's and yaw's angular acceleration (rad/s²), on the wheels and, if <paramref name="withRcs"/>, RCS.</summary>
        public static double Alpha(TurnCraft craft, bool withRcs)
        {
            return Math.Min(Acceleration(craft.WheelPitch, craft.RcsPitch, withRcs, craft.MoiPitch),
                Acceleration(craft.WheelYaw, craft.RcsYaw, withRcs, craft.MoiYaw));
        }

        /// <summary>Angular acceleration (rad/s²) on one axis: kN·m over t·m².</summary>
        private static double Acceleration(double wheel, double rcs, bool rcsOn, double moi)
        {
            var torque = Math.Max(0, wheel) + (rcsOn ? Math.Max(0, rcs) : 0);
            return moi > 0 ? torque / moi : 0;
        }
    }
}
