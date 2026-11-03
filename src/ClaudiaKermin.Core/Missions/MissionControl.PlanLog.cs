// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>The per-plan log (#265, #286): see <see cref="PlanLog"/>. Written as a reply is accepted, never when a transcript is replayed on load.</summary>
    public sealed partial class MissionControl
    {
        /// <summary>A Sequence Plan was offered for Approval: its line, with what the turn that made it used.</summary>
        private void LogPlan(Mission mission, Turn turn, SequencePlan plan, IList<string> checkWarnings)
        {
            // The lints the Script Check found, and the same lints read from the script (the badge's source): either way, once each.
            var lints = (plan.Script.Trim().Length > 0 ? ScriptLints.Warnings(plan.Script) : new List<string>())
                .Concat(checkWarnings ?? new List<string>()).Distinct().ToList();
            try
            {
                PlanLog.AppendPlan(mission.Folder, turn.Purpose.ToString(), plan, turn.Stats, turn.Effort,
                    turn.SecondLookReason, turn.CutOffRetry, turn.ScriptRewrite, lints);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The log is best effort; the Mission goes on.
            }
        }

        /// <summary>A Mission Plan turn's line (#514): written when the Mission Plan is accepted, or, with <paramref name="discarded"/>, when its tool loop is dropped.</summary>
        private void LogMissionPlan(Mission mission, Turn turn, bool discarded)
        {
            try
            {
                PlanLog.AppendMissionPlan(mission.Folder, turn.Purpose.ToString(), turn.Stats, turn.ToolSteps, turn.Effort,
                    turn.SecondLookReason, turn.CutOffRetry, discarded);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The log is best effort; the Mission goes on.
            }
        }

        /// <summary>The Mission Plan turns: the first and a Settings change always, a typed Reply or Change while a Mission Plan waits for Approval.</summary>
        private static bool IsMissionPlanTurn(Mission mission, Turn turn)
        {
            switch (turn.Purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    return true;
                case Purpose.Reply:
                case Purpose.Change:
                    return mission.Stage == MissionStage.AwaitingPlanApproval;
                default:
                    return false;
            }
        }

        /// <summary>The Debrief committed with <paramref name="debrief"/>'s outcome (as rated, after any Off-Nominal override): added to the latest plan's line.</summary>
        private void LogDebrief(Mission mission, Turn turn, Debrief debrief)
        {
            try
            {
                // A hand-back Debrief is of a manual stretch: the last run's blocks are not its.
                PlanLog.SetDebrief(mission.Folder, debrief.Outcome, turn.Stats, turn.Effort, turn.SecondLookReason, turn.CutOffRetry,
                    blocks: mission.HandBack ? null : mission.Blocks.Json(null), runDeltaV: mission.RunDeltaVTotal, withRunDeltaV: !mission.HandBack);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The log is best effort; the Mission goes on.
            }
        }
    }
}
