// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Runs the mod stops itself (ADR 0003): a runtime gate trip, which the kOS bridge reports with
    /// <see cref="KosRunBlocked"/> after it has stopped the program and made the craft safe, and the runaway
    /// hard stop (<see cref="RunawayStop"/>), which this side times in UT and carries out with an
    /// <see cref="AbortScript"/>. Either way the run's Debrief gives the reason.
    /// </summary>
    public sealed partial class MissionControl
    {
        private const string TimedOutEvent = "timed out: the mod stopped the script";

        private void StartRunawayTimer(Mission mission, SequencePlan plan)
        {
            mission.StopReason = null;
            mission.WarpTargetUt = null;
            mission.LastWarpingUt = null;
            mission.Warping = false;
            mission.RunDeadlineUt = RunawayStop.Deadline(_ut, plan.ExpectedDurationSeconds, _config.RunawayMultiplier);
        }

        private void WarpRequestSeen(KosWarpRequested requested)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != requested.RunId) return;
            mission.WarpTargetUt = requested.TargetUt;
        }

        /// <summary>#834: the warp state arrives with the UT it was read at, so both go stale together: no UtAdvanced, no clock, no stop.</summary>
        private void WarpSeen(bool warping)
        {
            var mission = _mission;
            if (mission == null) return;
            mission.Warping = warping;
            if (warping) mission.LastWarpingUt = _ut;
        }

        /// <summary>After every input: a run still going at its deadline is aborted (with make-safe) and Debriefed as "timed out".</summary>
        private void CheckRunaway()
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || !mission.RunId.HasValue || !mission.RunningProcessor.HasValue) return;
            if (!mission.RunDeadlineUt.HasValue || _ut < mission.RunDeadlineUt.Value || mission.StopReason != null) return;
            // #830: the warp's target is still ahead (or only just passed): wait for it, a script not warping or past its warps stops now.
            // #834: and only while warp is running, or ran within the allowance (a warp that never ran is held 60 s past the deadline).
            if (mission.WarpTargetUt.HasValue && _ut < mission.WarpTargetUt.Value + RunawayStop.WarpAllowanceSeconds
                && (mission.Warping || _ut < Math.Max(mission.RunDeadlineUt.Value, mission.LastWarpingUt ?? double.MinValue) + RunawayStop.WarpAllowanceSeconds)) return;
            mission.StopReason = RunawayStop.Reason;
            // As the deadline counted it: a plan of under a minute is held to one.
            var planned = Math.Max(mission.PendingSequence != null ? mission.PendingSequence.ExpectedDurationSeconds : 0, RunawayStop.MinimumPlannedSeconds);
            var text = "The script was still running at " + Percent(_config.RunawayMultiplier) + " of its planned "
                + planned.ToString("0", CultureInfo.InvariantCulture) + " s: the mod stopped it and made the craft safe.";
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
            Event(mission, "timed_out", text);
            mission.RunEvents.Add(_ut, RunEventKind.Stopped, TimedOutEvent);
            _commands.Add(new AbortScript(mission.RunningProcessor.Value, RunawayStop.Reason, mission.RunId.Value));
        }

        /// <summary>
        /// #696: a run the hard stop ended mid-warp gets an event saying how far it was from the warp's target (the warp trace's
        /// last request) and the duration that would have covered it, for the Debrief and Claudia's retry. Call before the deadline is cleared.
        /// </summary>
        private void TimedOutMidWarp(Mission mission, RunOutcome outcome)
        {
            if (mission.StopReason != RunawayStop.Reason || !mission.RunDeadlineUt.HasValue || outcome == null) return;
            // When the stop fired: in warp that can be well past the deadline. The deadline if its event was crowded out.
            var fired = mission.RunEvents.Events.LastOrDefault(e => e.Kind == RunEventKind.Stopped && e.Text == TimedOutEvent);
            var stoppedUt = fired != null ? fired.Ut : mission.RunDeadlineUt.Value;
            var cut = BurnPoints.WarpCutShort(outcome.Trace, mission.RunStartUt, stoppedUt);
            if (cut != null) mission.RunEvents.Add(stoppedUt, RunEventKind.Stopped, MissionPrompts.TimedOutMidWarp(cut));
        }

        private void RunBlocked(KosRunBlocked blocked)
        {
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != blocked.RunId || mission.StopReason != null) return;
            mission.StopReason = "blocked: " + blocked.What;
            var text = "A runtime gate stopped the script (" + mission.StopReason + ") and the craft was made safe.";
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
            Event(mission, "blocked", blocked.What);
            mission.RunEvents.Add(_ut, RunEventKind.Stopped, mission.StopReason);
        }

        /// <summary>The run's outcome as the Debrief sees it: a run the mod stopped itself is Aborted, with the reason.</summary>
        private static RunOutcome StoppedOutcome(Mission mission, RunOutcome outcome)
        {
            if (mission.StopReason == null || outcome == null || outcome.Reason == mission.StopReason) return outcome;
            var kind = outcome.Kind == RunOutcomeKind.Finished ? RunOutcomeKind.Finished : RunOutcomeKind.Aborted;
            return new RunOutcome(kind, outcome.Report, outcome.ErrorText, mission.StopReason, true);
        }

        /// <summary>For the Debrief card's title, then forgotten: " (blocked: …)", " (timed out)" or "".</summary>
        private static string TakeStopReason(Mission mission)
        {
            var reason = mission.StopReason;
            mission.StopReason = null;
            return reason == null ? "" : " (" + reason + ")";
        }

        private static string Percent(double multiplier)
        {
            return (multiplier * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }
}
