// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The reentry predictor against the reentry research's simulation cases (#247): its capsule, atmosphere and burn
    /// (<see cref="ReentrySim"/>), and the landing points it published, downrange of the burn point in the ground frame. The
    /// research rounded to the km, and integrated the vacuum leg where the predictor follows the Kepler orbit, so the
    /// tolerance is a km or two.
    /// </summary>
    public sealed class ReentryPredictorTests
    {
        private static ReentryPrediction Fly(double pe = 30000, ReentrySim.Capsule craft = null, double alt0 = 80000)
        {
            craft = craft ?? new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(alt0, pe), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft,
                ReentrySim.Frame());
            Assert.Null(landed.Error);
            return landed;
        }

        /// <summary>
        /// The predictor's drag-only model flown the old way, written out here as the independent reference: fixed-step RK4 from
        /// the top of the atmosphere (the Kepler entry point), gravity and drag against the turning air, to sea level. Gives the
        /// downrange km and the steps it took.
        /// </summary>
        internal static double Rk4Downrange(double pe, double step, out int steps)
        {
            var orbit = ReentrySim.AfterBurn(pe: pe);
            var t = orbit.TimeAtRadius(ReentrySim.R + ReentrySim.AtmTop, 0, false).Value;
            Vec3 r, v;
            orbit.State(t, out r, out v);
            var air = new ReentrySim.Atmosphere();
            var craft = new ReentrySim.Capsule();
            var spin = ReentrySim.Frame().Spin;
            Func<Vec3, Vec3, Vec3> accel = (p, u) =>
            {
                var length = p.Length;
                var a = p * (-ReentrySim.Mu / (length * length * length));
                double density, sound;
                air.Air(length - ReentrySim.R, 0, out density, out sound);
                if (length - ReentrySim.R >= ReentrySim.AtmTop || density <= 0) return a;
                var flow = u - Vec3.Cross(spin, p);
                var speed = flow.Length;
                return a - flow * (0.5 * density * speed * craft.DragArea(speed / sound, density * speed) / craft.MassKg);
            };
            steps = 0;
            while (true)
            {
                var r0 = r;
                var h0 = r.Length - ReentrySim.R;
                var a1 = accel(r, v);
                var v2 = v + a1 * (step / 2);
                var a2 = accel(r + v * (step / 2), v2);
                var v3 = v + a2 * (step / 2);
                var a3 = accel(r + v2 * (step / 2), v3);
                var v4 = v + a3 * step;
                var a4 = accel(r + v3 * step, v4);
                r = r + (v + 2 * v2 + 2 * v3 + v4) * (step / 6);
                v = v + (a1 + 2 * a2 + 2 * a3 + a4) * (step / 6);
                t += step;
                steps++;
                var h = r.Length - ReentrySim.R;
                if (h > 0) continue;
                var f = h0 / (h0 - h);
                double lat, lng;
                ReentrySim.Frame().Ground(r0 + (r - r0) * f, t - step + f * step, out lat, out lng);
                return ReentrySim.Km(lng);
            }
        }

        private static double Downrange(ReentryPrediction landed)
        {
            return ReentrySim.Km(landed.Longitude);
        }

        [Fact]
        public void The_reference_case_lands_1097_km_downrange_of_the_burn_270_s_to_entry_and_430_s_after()
        {
            var landed = Fly();
            Assert.InRange(Downrange(landed), 1095, 1099);
            Assert.Equal(0, landed.Latitude, 6);
            Assert.InRange(landed.EntryUt, 260, 280);
            Assert.InRange(landed.LandingUt - landed.EntryUt, 420, 440);
            // "Peak about 2.5 g in this atmosphere."
            Assert.InRange(landed.PeakG, 2, 3);
        }

        [Fact]
        public void It_runs_in_milliseconds()
        {
            // The reference reentry, and the steepest and shallowest of the research's periapsis sweep. Measured at about 1.5 ms
            // alone in a Debug build (1.45 ms on BS3, #462); the budget is about 15 times that.
            Timing.Within(25, () =>
            {
                Fly();
                Fly(10000);
                Fly(50000);
            });
        }

        [Fact]
        public void A_full_prediction_with_lift_both_kinds_of_chute_and_the_shield_s_drop_runs_in_milliseconds()
        {
            // Every event the predictor has (#462): measured at about 1.2 ms alone in a Debug build, the figure for #463; the
            // budget is about 15 times that.
            var drogue = ReentrySim.Mk16();
            drogue.Drogue = true;
            var craft = new ReentrySim.Capsule
            {
                Lift = 0.05, Chutes = new List<ReentryChute> { drogue, ReentrySim.Mk16() }, AfterShield = new ReentrySim.Capsule { Scale = 1.2 },
            };
            ReentryPrediction landed = null;
            Timing.Within(18, () =>
            {
                landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            });
            Assert.Null(landed.Error);
            Assert.True(landed.ShieldUt > 0 && landed.ChuteUt > 0);
        }

        [Theory]
        [InlineData(10000, 927)]
        [InlineData(20000, 1002)]
        [InlineData(30000, 1097)]
        [InlineData(40000, 1225)]
        [InlineData(50000, 1414)]
        public void The_periapsis_sweep_lands_where_the_research_has_it(double pe, double km)
        {
            Assert.InRange(Downrange(Fly(pe)), km - 2, km + 2);
        }

        [Fact]
        public void The_landing_is_634_km_short_of_the_vacuum_periapsis_point()
        {
            var orbit = ReentrySim.AfterBurn();
            var peUt = orbit.NextPeriapsis(0).Value;
            double lat, lng;
            ReentrySim.Frame().Ground(orbit.Position(peUt), peUt, out lat, out lng);
            Assert.InRange(ReentrySim.Km(lng) - Downrange(Fly()), 631, 637);
        }

        [Theory]
        [InlineData(0.9, 13)]
        [InlineData(1.1, -12)]
        [InlineData(0.8, 28)]
        [InlineData(1.2, -23)]
        public void A_drag_error_moves_the_landing_as_the_research_found(double scale, double km)
        {
            var shift = Downrange(Fly(craft: new ReentrySim.Capsule { Scale = scale })) - Downrange(Fly());
            Assert.InRange(shift, km - 1.5, km + 1.5);
        }

        [Fact]
        public void A_km_of_periapsis_moves_the_landing_11_km_at_a_30_km_periapsis()
        {
            Assert.InRange(Downrange(Fly(31000)) - Downrange(Fly()), 10, 12);
        }

        [Fact]
        public void One_hypersonic_drag_area_lands_within_2_km_of_the_full_model_and_the_subsonic_one_far_off()
        {
            var full = Downrange(Fly());
            Assert.InRange(Downrange(Fly(craft: new ReentrySim.Capsule { FixedMach = 8 })) - full, 0, 4);
            Assert.InRange(Downrange(Fly(craft: new ReentrySim.Capsule { FixedMach = 0.5 })) - full, 200, 230);
        }

        [Theory]
        [InlineData(10000)]
        [InlineData(30000)]
        [InlineData(50000)]
        public void The_adaptive_step_lands_within_1_km_of_a_half_second_RK4_in_fewer_steps(double pe)
        {
            // #462: the fixed 2 s RK4 (the research's step table: within about 50 m of 0.25 s) gave way to BS3's adaptive step.
            int rk4Steps;
            var rk4 = Rk4Downrange(pe, 0.5, out rk4Steps);
            var landed = Fly(pe);
            Assert.InRange(Downrange(landed) - rk4, -1, 1);
            Assert.True(landed.Steps < rk4Steps, landed.Steps + " adaptive steps against " + rk4Steps + " of RK4");
        }

        [Fact]
        public void The_adaptive_step_is_within_50_m_of_a_quarter_second_RK4()
        {
            // The old 2 s step's own test, kept against the same fine reference: the tolerance buys the same accuracy.
            int steps;
            Assert.InRange(Downrange(Fly()) - Rk4Downrange(30000, 0.25, out steps), -0.05, 0.05);
        }

        [Fact]
        public void A_Mk16_parachute_brings_the_capsule_down_short_of_where_it_falls_without_and_much_later()
        {
            var bare = Fly();
            var chuted = Fly(craft: new ReentrySim.Capsule { Chutes = new List<ReentryChute> { ReentrySim.Mk16() } });
            // The research's capsule never slows under the main's 250 m/s (it reaches the ground at about 232 m/s), so the chute
            // arms only by reentry's last-resort 1500 m and the shortening is what the bare capsule covers downrange from there,
            // falling within a few degrees of vertical: 1500 m at 85 to 89 degrees is 0.03 to 0.13 km. No recorded in-game
            // flight with parachutes exists yet to check the figure against (tests/blocks/reentry_orbit records no landing
            // points); that recording is #462's in-game check.
            var shorter = Downrange(bare) - Downrange(chuted);
            Assert.InRange(shorter, 0.03, 0.13);
            Assert.InRange(bare.LandingSpeed, 200, 260);
            Assert.True(chuted.ChuteUt > chuted.EntryUt && chuted.ChuteUt < chuted.LandingUt);
            Assert.Equal(-1, bare.ChuteUt);
            // Fully open, drag balances weight: v = sqrt(2 m g / (rho A)) = sqrt(2 × 1240 × 9.807 / (1.226 × 337)) = 7.7 m/s.
            Assert.InRange(chuted.LandingSpeed, 7, 8);
            // 1000 m at 7.7 m/s is 130 s, less the 8 s the canopy takes to open and the fall while it does.
            Assert.InRange(chuted.LandingUt - bare.LandingUt, 80, 135);
        }

        [Fact]
        public void The_main_chute_waits_for_the_block_s_safe_speed_and_a_drogue_opens_higher()
        {
            var main = Fly(craft: new ReentrySim.Capsule { Chutes = new List<ReentryChute> { ReentrySim.Mk16() } });
            var drogue = ReentrySim.Mk16();
            drogue.Drogue = true;
            var both = Fly(craft: new ReentrySim.Capsule { Chutes = new List<ReentryChute> { drogue, ReentrySim.Mk16() } });
            // The drogue's rule (500 m/s in 0.02 atm) comes before the main's (250 m/s in 0.04 atm).
            Assert.True(both.ChuteUt < main.ChuteUt, both.ChuteUt + " with a drogue, " + main.ChuteUt + " without");
        }

        [Fact]
        public void A_chute_already_open_when_the_prediction_starts_drags_from_the_start()
        {
            var orbit = ReentrySim.AfterBurn();
            var entry = Fly().EntryUt;
            var open = ReentrySim.Mk16();
            open.State = ReentryChuteState.Deployed;
            var craft = new ReentrySim.Capsule { Chutes = new List<ReentryChute> { open } };
            var landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), orbit, entry, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Null(landed.Error);
            // A 337 m² canopy from the top of the air: it all but stops in the thin air and comes down far short.
            Assert.True(Downrange(Fly()) - Downrange(landed) > 100);
        }

        [Fact]
        public void The_heat_shield_goes_by_the_block_s_rule_and_the_craft_after_it_is_flown_from_there()
        {
            var light = new ReentrySim.Capsule { Scale = 2 };
            var kept = Fly();
            var dropped = Fly(craft: new ReentrySim.Capsule { AfterShield = light });
            Assert.Equal(-1, kept.ShieldUt);
            Assert.True(dropped.ShieldUt > dropped.EntryUt && dropped.ShieldUt < dropped.LandingUt);
            // Twice the drag after the drop (at 400 m/s, low and slow) lands it short, but not by much.
            var shorter = Downrange(kept) - Downrange(dropped);
            Assert.InRange(shorter, 0.01, 5);
        }

        [Fact]
        public void Lift_toward_the_sky_carries_it_farther_and_lift_toward_the_ground_brings_it_short()
        {
            var plain = Downrange(Fly());
            Assert.True(Downrange(Fly(craft: new ReentrySim.Capsule { Lift = 0.2 })) > plain + 5);
            Assert.True(Downrange(Fly(craft: new ReentrySim.Capsule { Lift = -0.2 })) < plain - 5);
        }

        [Fact]
        public void A_prediction_asks_the_air_a_few_hundred_times()
        {
            // #462: was 400 to 1200 (2 s RK4, four calls a step, about 860); BS3 takes about as many steps at three calls each.
            var air = new ReentrySim.Atmosphere();
            var craft = new ReentrySim.Capsule();
            ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 0, craft.MassKg, air, craft, ReentrySim.Frame());
            Assert.InRange(air.Calls, 400, 860);
        }

        [Fact]
        public void Higher_ground_is_reached_sooner_and_short_of_sea_level()
        {
            var craft = new ReentrySim.Capsule();
            var high = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 2000, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            var sea = Fly();
            Assert.True(high.LandingUt < sea.LandingUt);
            Assert.InRange(Downrange(sea) - Downrange(high), 0, 3);
        }

        [Fact]
        public void A_heavier_craft_flies_farther()
        {
            var craft = new ReentrySim.Capsule();
            var heavy = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(), 0, 0, craft.MassKg * 1.1, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            // Mass is the other half of the ballistic coefficient: as drag x0.9 or so.
            Assert.InRange(ReentrySim.Km(heavy.Longitude) - Downrange(Fly()), 8, 16);
        }

        [Fact]
        public void An_orbit_above_the_atmosphere_never_lands()
        {
            var craft = new ReentrySim.Capsule();
            var high = ReentryPredictor.Predict(ReentrySim.Kerbin(), ReentrySim.AfterBurn(pe: 75000), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal("the orbit never reaches Kerbin's atmosphere (periapsis 75000 m)", high.Error);
        }

        [Fact]
        public void An_airless_body_has_no_prediction()
        {
            var craft = new ReentrySim.Capsule();
            var airless = ReentryPredictor.Predict(ReentrySim.Kerbin(0), ReentrySim.AfterBurn(), 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Equal("Kerbin has no atmosphere", airless.Error);
        }

        [Fact]
        public void A_craft_already_in_the_atmosphere_is_flown_from_where_it_is()
        {
            var orbit = ReentrySim.AfterBurn();
            var entry = Fly().EntryUt;
            var craft = new ReentrySim.Capsule();
            var inside = ReentryPredictor.Predict(ReentrySim.Kerbin(), orbit, entry + 1, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, ReentrySim.Frame());
            Assert.Null(inside.Error);
            Assert.Equal(entry + 1, inside.EntryUt, 6);
            Assert.InRange(ReentrySim.Km(inside.Longitude) - Downrange(Fly()), -1, 1);
        }

        [Fact]
        public void A_body_turning_the_other_way_in_the_frame_lands_at_the_same_ground_point()
        {
            // Mirror the frame: the orbit's y and the body's turn flip, so the ground track is the same.
            var mirrored = new BodyFrame { LonOffset = 0, Handed = -1, Ut = 0, RotationPeriod = ReentrySim.RotationPeriod };
            var after = ReentrySim.AfterBurn();
            Vec3 r, v;
            after.State(0, out r, out v);
            var flipped = Conic.FromState(ReentrySim.Mu, new Vec3(r.X, -r.Y, r.Z), new Vec3(v.X, -v.Y, v.Z), 0);
            var craft = new ReentrySim.Capsule();
            var landed = ReentryPredictor.Predict(ReentrySim.Kerbin(), flipped, 0, 0, craft.MassKg, new ReentrySim.Atmosphere(), craft, mirrored);
            Assert.InRange(ReentrySim.Km(landed.Longitude) - Downrange(Fly()), -0.01, 0.01);
        }
    }
}
