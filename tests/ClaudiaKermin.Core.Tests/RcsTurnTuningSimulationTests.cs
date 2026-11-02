using System;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// #678: a stepped model of kOS steering turning a craft under the RCS turn-rate override. The rate asked for is kp x error, no more
    /// than alpha x T (alpha from TURNTIME). The craft only manages 1.7 deg/s2 of the 2.6 deg/s2 modelled (the e17d flight's figures)
    /// and the thrusters lag the command by a time constant. Model time, not wall clock.
    /// </summary>
    public class RcsTurnTuningSimulationTests
    {
        private const double OnDeg = 10;      // rcs_tolerance on_deg: an overshoot under this does not switch RCS back on
        private const double RealAlpha = 1.7; // deg/s2 the craft achieved

        private static double ModelledAlpha()
        {
            // Torque / MOI = 2.6 deg/s2 on both axes, half from the wheels and half from RCS.
            var t = 100 * 2.6 * Math.PI / 180 / 2;
            var craft = new TurnCraft { WheelPitch = t, WheelYaw = t, RcsPitch = t, RcsYaw = t, RcsUsable = true, MoiPitch = 100, MoiYaw = 100 };
            return TurnTime.Predict(craft).AlphaDegS2;
        }

        // Time to Aligned (error under 2 deg, rate under 0.5 deg/s), the times the error crossed zero and the largest overshoot.
        private static (double Seconds, int Crossings, double Overshoot) Fly(double angle, double t, double kp, double lag)
        {
            const double dt = 0.02;
            var cap = ModelledAlpha() * t;
            double error = angle, rate = 0, drive = 0, time = 0, over = 0;
            var crossings = 0;
            var sign = 1;
            while (time < 120)
            {
                var want = Math.Max(-cap, Math.Min(cap, kp * error));
                var command = Math.Max(-1, Math.Min(1, (want - rate) / (RealAlpha * lag)));
                drive += (command - drive) * dt / lag * 3;
                rate += drive * RealAlpha * dt;
                error -= rate * dt;
                time += dt;
                var s = error > 0 ? 1 : -1;
                if (s != sign && Math.Abs(error) > 0.01) { crossings++; sign = s; }
                if (error < 0) over = Math.Max(over, -error);
                if (Math.Abs(error) < 2 && Math.Abs(rate) < 0.5) break;
            }
            return (time, crossings, over);
        }

        [Fact]
        public void Alpha_comes_from_TURNTIME()
        {
            Assert.Equal(2.6, ModelledAlpha(), 3);
        }

        [Theory]
        [InlineData(142, 0.5)]
        [InlineData(142, 1)]
        [InlineData(142, 2)]
        [InlineData(178, 0.5)]
        [InlineData(178, 1)]
        [InlineData(178, 2)]
        public void The_new_tuning_settles_with_at_most_one_small_overshoot_in_under_30_s(double angle, double lag)
        {
            var t = TurnTime.OverrideStoppingTime(angle, ModelledAlpha());
            var (seconds, crossings, over) = Fly(angle, t, TurnTime.OverrideKpGain / t, lag);
            Assert.True(crossings <= 1, "crossings " + crossings);
            Assert.True(over < OnDeg, "overshoot " + over);
            Assert.True(seconds < 30, "seconds " + seconds);
        }

        [Theory]
        [InlineData(142, 2)]
        [InlineData(178, 2)]
        public void The_old_tuning_overshoots_by_more_than_the_on_gate(double angle, double lag)
        {
            var t = Math.Min(10, Math.Max(2, Math.Sqrt(angle / ModelledAlpha())));
            var (_, _, over) = Fly(angle, t, 1.6 / t, lag);
            Assert.True(over >= OnDeg, "overshoot " + over);
        }

        [Theory]
        [InlineData(36, 1)]
        [InlineData(142, 2.6)]
        [InlineData(178, 2.6)]
        [InlineData(5000, 1)]
        public void The_gain_is_within_the_no_overshoot_bound_for_a_craft_braking_at_two_thirds_of_alpha(double angle, double alpha)
        {
            var t = TurnTime.OverrideStoppingTime(angle, alpha);
            // Linear rate law, slowing from the cap alpha x T at (2/3) alpha: no overshoot while kp <= 2 x (2/3) alpha / (alpha x T).
            Assert.True(TurnTime.OverrideKpGain / t <= 4.0 / 3 / t);
        }
    }
}
