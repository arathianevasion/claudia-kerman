// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #770, from Mission m20261105-174251-8f20: transfer_to's Encounter Watch, its correction clear of the sphere of influence's
    /// edge and its restore before a recovery. The replay is what KSP.log shows: the correction flown about 190 s past Kerbin's
    /// edge; as the next warp packed the craft, KSP put it back on a Kerbin patch at 83.93 Mm (the edge is 84.16 Mm), moving out at
    /// 1,014 m/s, and held it there for 51,097 s of warp before the next Sun patch.
    /// </summary>
    public sealed class EncounterWatchTests
    {
        /// <summary>From KSP.log: the distance from Kerbin at the re-entry, m, and the time KSP then held the craft round Kerbin, s.</summary>
        private const double ReentryDistance = 83.93e6;
        private const double HeldOnKerbin = 51097;

        private static TransferFlightAsk Ask(string target, double pe)
        {
            return new TransferFlightAsk { Target = target, TargetPe = pe, StartUt = 0, TurnTime = 60, DvLeft = 5000, Auto = true };
        }

        /// <summary>Flies the rig's steps to the first wait into the target's sphere of influence, and gives that wait, not yet waited.</summary>
        private static TransferFlightStep FlyToTheLongCoast(TransferRig rig, TransferFlightAsk ask)
        {
            for (var i = 0; i < 20; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.True(step.Error == "", step.Error);
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Wait && step.Phase == "near") return step;
                Assert.NotEqual(TransferFlightStep.Done, step.Do);
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            throw new InvalidOperationException("no long coast in 20 steps");
        }

        /// <summary>What KSP did as the warp began: the craft, at the same velocity, on a Kerbin patch at <see cref="ReentryDistance"/>.</summary>
        private static void RePatchIntoKerbin(TransferRig rig)
        {
            var kerbin = rig.System.Find("Kerbin");
            Vec3 r, v, kr, kv;
            rig.Orbit.State(rig.Ut, out r, out v);
            rig.System.OrbitOf(kerbin).State(rig.Ut, out kr, out kv);
            rig.Body = kerbin;
            rig.Orbit = Conic.FromState(kerbin.Gm, (r - kr).Normalized * ReentryDistance, v - kv, rig.Ut);
        }

        /// <summary>
        /// What KSP did next: <see cref="HeldOnKerbin"/> seconds on that patch, past the edge (KSP's on-rails transition came that
        /// late), then the Sun patch from wherever that left the craft.
        /// </summary>
        private static void RePatchBackOntoTheSun(TransferRig rig)
        {
            var kerbin = rig.System.Find("Kerbin");
            var sun = rig.System.Find("Sun");
            var ut = rig.Ut + HeldOnKerbin;
            Vec3 r, v, kr, kv;
            rig.Orbit.State(ut, out r, out v);
            rig.System.OrbitOf(kerbin).State(ut, out kr, out kv);
            rig.Body = sun;
            rig.Orbit = Conic.FromState(sun.Gm, r + kr, v + kv, ut);
            rig.Ut = ut;
        }

        // ---- Why the re-patch went unnoticed (the pin, written before the fix) ----

        [Fact]
        public void The_step_that_plans_the_long_coast_sees_an_encounter_the_warp_then_loses()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var ask = Ask("Duna", 50000);

            // Core's only reading of the coast is this one: the craft in physics, on the Sun patch, before the warp packs it.
            var wait = FlyToTheLongCoast(rig, ask);
            Assert.Equal("Sun", rig.Body.Name);
            var setup = Setup(rig);
            Assert.NotNull(TransferFlight.Encounter(setup));

            // KSP re-patches only as the warp begins; nothing in the flight reads the orbit again until wait.Ut, months on.
            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Assert.True(wait.Ut > rig.Ut + 100 * 21600);
            Assert.Null(TransferFlight.Encounter(Setup(rig)));
        }

        private static PredictBurn.Setup Setup(TransferRig rig)
        {
            ToolOutcome error;
            return PredictBurn.Setup.Prepare(rig.Snapshot(), "Duna", out error);
        }

        // ---- The post-escape correction, clear of the edge ----

        [Fact]
        public void The_post_escape_correction_is_held_until_the_margin_past_the_sphere_of_influence()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            var ask = Ask("Duna", 50000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            ask.LastDv = rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            TransferFlight.Carry(ask, eject);

            var wait = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(TransferFlightStep.Wait, wait.Do);
            Assert.Equal("escaped", wait.Phase);
            rig.MoveTo(wait.Ut);
            Assert.Equal("Sun", rig.Body.Name);

            var kerbin = rig.System.Find("Kerbin");
            var gap = (rig.Orbit.Position(rig.Ut) - rig.System.OrbitOf(kerbin).Position(rig.Ut)).Length;
            Assert.True(gap >= kerbin.SoiRadius * 1.05 - 1000, gap + " m from Kerbin at the wait's end");
        }

        [Fact]
        public void A_correction_step_still_round_the_body_left_goes_back_to_the_way_out()
        {
            // The wait's end found KSP still reporting Kerbin: no correction is planned on that patch.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            var ask = Ask("Duna", 50000);
            var eject = TransferFlight.Next(rig.Snapshot(), ask);
            ask.LastDv = rig.Fly(eject.Kind, eject.Ut, eject.Prograde, eject.Normal, eject.Radial);
            TransferFlight.Carry(ask, eject);
            ask.Phase = "escaped";
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.Equal(TransferFlightStep.Wait, step.Do);
            Assert.Equal("escaped", step.Phase);
        }

        // ---- The watch ----

        /// <summary>The watch a wait carries, as the adapter builds it from TRANSFERSTEP's lexicon.</summary>
        private static EncounterWatch WatchOf(TransferFlightStep wait)
        {
            Assert.NotNull(wait.Watch);
            return new EncounterWatch(wait.Watch.ToLexicon());
        }

        /// <summary>Polls as the block does: a check when one is due; the first line it gives, or "".</summary>
        private static string Poll(EncounterWatch watch, TransferRig rig)
        {
            return watch.Due(rig.Ut) ? watch.Check(rig.Snapshot()) : "";
        }

        private static TransferRig Coasting(out TransferFlightAsk ask, out TransferFlightStep wait)
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            ask = Ask("Duna", 50000);
            wait = FlyToTheLongCoast(rig, ask);
            return rig;
        }

        /// <summary>Carries a wait's phase into the ask as the block does, then the drop.</summary>
        private static void Woken(TransferFlightAsk ask, TransferFlightStep wait)
        {
            if (wait.MaxDuration.HasValue) ask.MaxDuration = wait.MaxDuration;
            TransferFlight.Carry(ask, wait);
            ask.WatchDropped = true;
        }

        [Fact]
        public void A_watch_over_a_coast_where_the_encounter_holds_never_asks_for_a_drop()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            var soi = (double)wait.Watch.SoiUt;
            var checks = 0;
            // Warp ticks of 100,000x (2,000 s) with a start and an end at lower warp, as KSP's own limits make them.
            while (rig.Ut < soi - 10)
            {
                if (watch.Due(rig.Ut))
                {
                    checks++;
                    Assert.Equal("", watch.Check(rig.Snapshot()));
                }
                rig.MoveTo(Math.Min(soi - 10, rig.Ut + (rig.Ut - wait.Watch.SinceUt < 1000 || soi - rig.Ut < 1000 ? 50 : 2000)));
            }
            Assert.InRange(checks, 100, 2000);
        }

        [Fact]
        public void The_checks_are_tighter_near_the_coast_s_two_ends()
        {
            var watch = new EncounterWatch(new EncounterWatchPlan { Target = "Duna", SinceUt = 1000, SoiUt = 1000 + 300 * 21600 }.ToLexicon());
            Assert.Equal(EncounterWatch.MinGap, watch.Gap(1001));
            Assert.Equal(EncounterWatch.MaxGap, watch.Gap(1000 + 150 * 21600));
            Assert.Equal(EncounterWatch.MinGap, watch.Gap(1000 + 300 * 21600 - 1));
            Assert.Equal(0.05 * 10000, watch.Gap(1000 + 10000));
        }

        [Fact]
        public void A_wait_with_nothing_to_watch_is_never_due()
        {
            Assert.False(new EncounterWatch(new Dictionary<string, object>()).Due(1e12));
        }

        private static double Dv(TransferFlightStep step)
        {
            return Math.Sqrt(step.Prograde * step.Prograde + step.Normal * step.Normal + step.Radial * step.Radial);
        }

        [Fact]
        public void An_encounter_lost_mid_coast_asks_for_a_drop_and_the_next_step_is_a_restore_of_about_what_lost_it()
        {
            // Measured on this rig: Duna's periapsis moves 6 to 7 Mm per m/s along the track early in the coast, so a 50 km
            // encounter takes about 8 m/s to lose outright (the 48 Mm sphere of influence), and costs about that to restore.
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            Assert.Equal("", Poll(watch, rig));
            rig.MoveTo(rig.Ut + 2 * 21600);
            Assert.Equal("", Poll(watch, rig));

            rig.Fly("by_hand", rig.Ut, -8, 0, 0);
            Assert.Null(TransferFlight.Encounter(Setup(rig)));
            rig.MoveTo(rig.Ut + EncounterWatch.MaxGap);
            // One reading that looks wrong isn't a finding; the next tick's is.
            Assert.Equal("", Poll(watch, rig));
            rig.MoveTo(rig.Ut + 100);
            var line = Poll(watch, rig);
            Assert.StartsWith("Encounter Watch: Duna encounter lost at UT ", line);
            Assert.EndsWith("; warp dropped to restore it", line);

            Woken(ask, wait);
            var restore = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(restore.Error == "", restore.Error);
            Assert.Equal(TransferFlightStep.Burn, restore.Do);
            Assert.Equal("correction", restore.Kind);
            Assert.True(restore.Restored);
            Assert.Equal("no encounter with Duna after the correction", restore.Report["recovered"]);
            Assert.InRange(Dv(restore), 0.1, 10);

            // It restores this window's encounter, and the flight goes on from there to the asked periapsis.
            ask.LastDv = rig.Fly(restore.Kind, restore.Ut, restore.Prograde, restore.Normal, restore.Radial);
            TransferFlight.Carry(ask, restore);
            Assert.NotNull(TransferFlight.Encounter(Setup(rig)));
            var after = rig.Run(ask, 30);
            Assert.True(after.Error == "", after.Error);
            Assert.InRange(Convert.ToDouble(after.Report["target_pe_actual"]), 49000, 51000);
            Assert.True(rig.Ut < wait.Ut + 30 * 21600, "arrived at UT " + rig.Ut);
        }

        [Fact]
        public void A_small_perturbation_that_loses_a_high_encounter_is_restored_under_5_m_s()
        {
            // An encounter high in the sphere of influence is the one a small perturbation can lose: the flight's coast at a
            // periapsis of about 30 Mm, asked for, then 2.5 m/s more along the track takes it out of Duna's 48 Mm sphere of influence.
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            rig.MoveTo(rig.Ut + 2 * 21600);
            rig.Fly("first", rig.Ut, -3.5, 0, 0);
            var high = TransferFlight.Encounter(Setup(rig));
            Assert.InRange(high.Pe, 25e6, 45e6);
            ask.TargetPe = high.Pe;
            var plan = new EncounterWatchPlan { Target = "Duna", Pe = high.Pe, PeLimit = 4.8e6, SoiUt = high.SoiUt, SinceUt = rig.Ut, Bodies = high.Bodies };
            var watch = new EncounterWatch(plan.ToLexicon());
            Assert.Equal("", Poll(watch, rig));

            rig.Fly("by_hand", rig.Ut, -2.5, 0, 0);
            Assert.Null(TransferFlight.Encounter(Setup(rig)));
            rig.MoveTo(rig.Ut + EncounterWatch.MaxGap);
            Assert.Equal("", Poll(watch, rig));
            rig.MoveTo(rig.Ut + 100);
            Assert.Contains("Duna encounter lost", Poll(watch, rig));

            Woken(ask, wait);
            var restore = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(restore.Error == "", restore.Error);
            Assert.Equal(TransferFlightStep.Burn, restore.Do);
            Assert.InRange(Dv(restore), 0.1, 5);
            ask.LastDv = rig.Fly(restore.Kind, restore.Ut, restore.Prograde, restore.Normal, restore.Radial);
            Assert.NotNull(TransferFlight.Encounter(Setup(rig)));
        }

        [Fact]
        public void A_drifted_encounter_that_holds_gets_a_burn_back_to_target_pe_and_counts_as_no_restore()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            Assert.Equal("", Poll(watch, rig));
            // About 2 m/s: 14 Mm off, well past the 4.8 Mm limit.
            rig.Fly("by_hand", rig.Ut + 60, -2, 0, 0);
            Assert.NotNull(TransferFlight.Encounter(Setup(rig)));
            rig.MoveTo(rig.Ut + EncounterWatch.MaxGap);
            Assert.Equal("", Poll(watch, rig));
            rig.MoveTo(rig.Ut + 100);
            Assert.Contains("Duna periapsis off by ", Poll(watch, rig));

            Woken(ask, wait);
            var burn = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(burn.Error == "", burn.Error);
            Assert.Equal(TransferFlightStep.Burn, burn.Do);
            Assert.Equal("restore_check", burn.Phase);
            Assert.InRange(Dv(burn), 0.5, 5);
            Assert.False(burn.Restored);
            Assert.False(burn.Report.ContainsKey("recovered"));
        }

        [Fact]
        public void Drift_past_the_threshold_asks_for_a_drop_and_within_it_does_not()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var ask = Ask("Duna", 50000);
            var wait = FlyToTheLongCoast(rig, ask);
            var duna = rig.System.Find("Duna");
            var enc = TransferFlight.Encounter(Setup(rig));

            // The plan's periapsis 1 % of the SOI off what it reads now: within the limit, no finding on two ticks.
            var near = new EncounterWatchPlan { Target = "Duna", Pe = enc.Pe + 0.01 * duna.SoiRadius, PeLimit = 0.1 * duna.SoiRadius, SoiUt = enc.SoiUt, SinceUt = rig.Ut, Bodies = enc.Bodies };
            var holds = new EncounterWatch(near.ToLexicon());
            Assert.Equal("", holds.Check(rig.Snapshot()));
            Assert.Equal("", holds.Check(rig.Snapshot()));

            // Past it: asked for at the second reading, with the figure.
            var far = new EncounterWatchPlan { Target = "Duna", Pe = enc.Pe + 0.2 * duna.SoiRadius, PeLimit = 0.1 * duna.SoiRadius, SoiUt = enc.SoiUt, SinceUt = rig.Ut, Bodies = enc.Bodies };
            var drifted = new EncounterWatch(far.ToLexicon());
            Assert.Equal("", drifted.Check(rig.Snapshot()));
            Assert.Contains("Duna periapsis off by ", drifted.Check(rig.Snapshot()));
            Assert.Equal(EncounterWatch.DriftShare * duna.SoiRadius, wait.Watch.PeLimit);
        }

        [Fact]
        public void The_mid_course_coast_to_a_far_target_s_node_is_a_watched_wait_and_the_node_is_planned_at_its_end()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.5;
            var ask = Ask("Duna", 50000);
            for (var i = 0; i < 12; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.True(step.Error == "", step.Error);
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Wait && step.Phase == "mid")
                {
                    Assert.NotNull(step.Watch);
                    Assert.True(step.MidUt > step.Ut);
                    rig.MoveTo(step.Ut);
                    TransferFlight.Carry(ask, step);
                    var burn = TransferFlight.Next(rig.Snapshot(), ask);
                    if (burn.Do == TransferFlightStep.Burn) Assert.Equal(step.MidUt, burn.Ut, 3);
                    return;
                }
                Assert.False(step.Do == TransferFlightStep.Wait && step.Phase == "near", "reached the long coast with no mid-course burn to wait for");
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            Assert.Fail("no watched mid-course wait");
        }

        // ---- The replay of the Mission ----

        [Fact]
        public void Replay_the_watch_catches_the_Kerbin_re_entry_within_the_first_warp_ticks()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            Assert.Equal("", Poll(watch, rig));

            // 47 ms after the step, as the warp packs the craft: on a Kerbin patch. The first tick reads it, the next confirms it.
            RePatchIntoKerbin(rig);
            rig.Ut += 5;
            Assert.True(watch.Due(rig.Ut + EncounterWatch.MinGap));
            rig.Ut += EncounterWatch.MinGap;
            Assert.Equal("", Poll(watch, rig));
            rig.Ut += 5;
            Assert.Contains("Duna encounter lost at UT", Poll(watch, rig));

            // The next step takes the way out again, clear of the edge: no burn on the Kerbin patch.
            Woken(ask, wait);
            var next = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(next.Error == "", next.Error);
            Assert.Equal(TransferFlightStep.Wait, next.Do);
            Assert.Equal("escaped", next.Phase);
            Assert.Equal(1, next.Drops);
        }

        [Fact]
        public void Replay_after_KSP_s_late_Sun_patch_the_watch_catches_the_loss_and_a_restore_saves_the_window()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            Assert.Equal("", Poll(watch, rig));

            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Assert.Equal("", Poll(watch, rig));
            rig.Ut += 80;
            Assert.Contains("Duna encounter lost at UT", Poll(watch, rig));

            Woken(ask, wait);
            var restore = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(restore.Error == "", restore.Error);
            Assert.Equal(TransferFlightStep.Burn, restore.Do);
            // Not the 340 m/s burn about six years out the Mission took: about 15 m/s on this rig, now (Kerbin's pull over the 51,097 s).
            Assert.InRange(Dv(restore), 5, 25);
            Assert.True(restore.Ut < rig.Ut + 1000);
        }

        // ---- Hand back ----

        [Fact]
        public void A_lost_encounter_with_no_affordable_restore_fails_with_the_figures_not_a_multi_year_recovery()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Assert.Null(TransferFlight.Encounter(Setup(rig)));
            Woken(ask, wait);
            ask.DvLeft = 0.5;

            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.StartsWith("no encounter with Duna after the correction, and no correction fits: ", step.Error);
            Assert.Contains("a restore costs ", step.Error);
            Assert.Contains("the next window costs ", step.Error);
            Assert.Contains(" s from now", step.Error);
            Assert.Equal(TransferFlightStep.Done, step.Do);
        }

        [Fact]
        public void A_next_window_recovery_in_auto_is_held_to_the_stated_limit()
        {
            // The restore already had: the recovery, if there is one, burns inside 30 Kerbin days, never years out.
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Woken(ask, wait);
            ask.Restored = true;
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(step.Error == "", step.Error);
            Assert.Equal(TransferFlightStep.Burn, step.Do);
            Assert.True(step.Recovered);
            Assert.InRange(Dv(step), 0.1, 30);
            Assert.True(step.Ut <= rig.Ut + ask.TurnTime + 60 + TransferFlight.RecoveryWait, "burn at UT " + step.Ut + " from UT " + rig.Ut);
        }

        [Fact]
        public void A_lost_encounter_inside_an_explicit_max_duration_with_no_affordable_restore_fails_with_the_figures()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            RePatchIntoKerbin(rig);
            RePatchBackOntoTheSun(rig);
            Woken(ask, wait);
            ask.Auto = false;
            ask.MaxDuration = rig.Ut + 2 * 21600;
            ask.DvLeft = 0.5;

            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.StartsWith("no encounter with Duna after the correction, and no correction fits: ", step.Error);
            Assert.Contains("a restore costs ", step.Error);
            Assert.Contains("the next window costs ", step.Error);
        }

        [Fact]
        public void A_drift_s_burn_the_craft_can_t_pay_for_is_not_flown()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            rig.Fly("by_hand", rig.Ut + 60, -2, 0, 0);
            Assert.NotNull(TransferFlight.Encounter(Setup(rig)));
            Woken(ask, wait);
            ask.DvLeft = 0.5;
            var step = TransferFlight.Next(rig.Snapshot(), ask);
            Assert.True(step.Error == "", step.Error);
            Assert.NotEqual(TransferFlightStep.Burn, step.Do);
        }

        [Fact]
        public void A_short_max_duration_s_watched_mid_course_wait_still_flies_its_burn()
        {
            // "A shorter one moves the correction sooner": the burn at the end of max_duration, its wait ending in time for it.
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.5;
            var ask = Ask("Duna", 50000);
            for (var i = 0; i < 12; i++)
            {
                var step = TransferFlight.Next(rig.Snapshot(), ask);
                Assert.True(step.Error == "", step.Error);
                if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
                if (step.Do == TransferFlightStep.Burn && step.Phase == "mid" )
                {
                    ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                    TransferFlight.Carry(ask, step);
                    // From here max_duration ends 10 days on, well before halfway.
                    ask.Auto = false;
                    ask.MaxDuration = rig.Ut + 10 * 21600;
                    var wait = TransferFlight.Next(rig.Snapshot(), ask);
                    Assert.True(wait.Error == "", wait.Error);
                    Assert.Equal(TransferFlightStep.Wait, wait.Do);
                    Assert.Equal(ask.MaxDuration.Value - 120, wait.MidUt, 3);
                    // A wake a few seconds past the wait's end, as a warp's overshoot gives.
                    rig.MoveTo(wait.Ut + 5);
                    TransferFlight.Carry(ask, wait);
                    var burn = TransferFlight.Next(rig.Snapshot(), ask);
                    Assert.True(burn.Error == "", burn.Error);
                    Assert.Equal(TransferFlightStep.Burn, burn.Do);
                    Assert.Equal(wait.MidUt, burn.Ut, 3);
                    return;
                }
                if (step.Do == TransferFlightStep.Burn) ask.LastDv = rig.Fly(step.Kind, step.Ut, step.Prograde, step.Normal, step.Radial);
                else rig.MoveTo(step.Ut);
                TransferFlight.Carry(ask, step);
            }
            Assert.Fail("no correction on the Sun's patch");
        }

        [Fact]
        public void A_flight_stops_watching_after_its_drops_so_a_watch_can_t_loop()
        {
            var rig = TransferRig.Circular("Kerbin", 100000, 0);
            rig.BurnError = 0.005;
            rig.CorrectionError = 0.05;
            var ask = Ask("Duna", 50000);
            ask.Drops = TransferFlight.MaxDrops;
            var unwatched = FlyToTheLongCoast(rig, ask);
            Assert.Null(unwatched.Watch);
        }

        [Fact]
        public void The_block_call_gives_a_wait_s_watch_and_carries_a_drop_to_the_restore()
        {
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var input = new Dictionary<string, object>
            {
                { "target", "Duna" }, { "target_pe", 50000.0 }, { "correction", true }, { "pe_tolerance", 1000.0 }, { "start_ut", 0.0 }, { "max_duration", "auto" },
                { "auto", true }, { "turn_time", 60.0 }, { "dv_left", 5000.0 }, { "phase", ask.Phase }, { "arrival_ut", ask.ArrivalUt }, { "plane_ut", ask.PlaneUt }, { "sibling", ask.Sibling },
            };
            var step = BlockCalls.TransferStep(input, rig.Snapshot());
            Assert.Equal("wait", step["do"]);
            var watch = (IDictionary<string, object>)step["watch"];
            Assert.Equal("Duna", watch["target"]);
            Assert.Equal("Sun,Duna", watch["bodies"]);
            var carry = (IDictionary<string, object>)step["carry"];
            Assert.Equal(false, carry["watch_dropped"]);
            Assert.Equal(0.0, carry["drops"]);

            rig.Fly("by_hand", rig.Ut, -8, 0, 0);
            input["watch_dropped"] = true;
            input["phase"] = "near";
            var restore = BlockCalls.TransferStep(input, rig.Snapshot());
            Assert.Equal("burn", restore["do"]);
            carry = (IDictionary<string, object>)restore["carry"];
            Assert.Equal("restore_check", carry["phase"]);
            Assert.Equal(1.0, carry["drops"]);
            Assert.Equal(true, carry["restored"]);
            Assert.Empty((IDictionary<string, object>)restore["watch"]);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_check_takes_under_its_budget()
        {
            // Guardrail 6: a check can run every physics tick near the coast's two ends. A tick at 50 Hz is 20 ms; the budget is a tenth.
            TransferFlightAsk ask;
            TransferFlightStep wait;
            var rig = Coasting(out ask, out wait);
            var watch = WatchOf(wait);
            var snapshot = rig.Snapshot();
            Timing.Within(2, () => Assert.Equal("", watch.Check(snapshot)));
        }
    }
}
