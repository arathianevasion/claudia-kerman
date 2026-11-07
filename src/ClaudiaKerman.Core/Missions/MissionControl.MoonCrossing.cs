// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #867: a craft is never left waiting in an orbit that crosses a moon's sphere of influence (<see cref="MoonCrossings"/>). A Sequence Plan that waits
    /// for its Due Time in one is sent back in the one automatic correction, and still crossing after it, holds a Blocker: the mod's warp to the Due Time
    /// and Auto Mode wait for the player. The Debrief says so of the orbit a run left. With no script running the adapter watches the craft's next patch
    /// while it warps (<see cref="NextPatchWatch"/>) and raises a request when it drops the warp.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The plan offered with a crossing still there after the correction, and its Blocker line; not saved (the warp to the Due Time judges again).</summary>
            public SequencePlan CrossingPlan;
            public string CrossingBlocker = "";
        }

        /// <summary>
        /// The moon the craft's orbit crosses while <paramref name="plan"/> waits: to its Due Time, or the one the mod will give it for a far ejection
        /// (<see cref="TransferWait"/>'s). Null for none, a Manual stretch, another vessel, the mod's own resume at a Coast Checkpoint, or no wait.
        /// </summary>
        private MoonCrossing WaitCrossing(Mission mission, SequencePlan plan, GameSnapshot snapshot)
        {
            if (plan == null || plan.Manual || snapshot == null || !FliesCraft(mission, plan.Vessel) || CoastPlanToken(plan) != null) return null;
            var due = plan.DueTimeUt;
            if (!due.HasValue)
            {
                var stamped = snapshot.At(_ut);
                stamped.StampMission(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config)).TimeVersusDeltaV, _config.CraftCheckMarginPercent, mission.WindowPick);
                due = BurnPoints.DueTime(BurnPoints.First(plan.Script, stamped, _ut, plan.ExpectedDeltaV, _body), _ut);
            }
            if (!due.HasValue || due.Value <= _ut) return null;
            return MoonCrossings.Judge(snapshot.At(_ut), due.Value);
        }

        /// <summary>The Blocker the plan in hand holds, judged now from the craft as it is (the player may have flown the fix); "" for none.</summary>
        private string CrossingBlockerNow(Mission mission)
        {
            var plan = mission.PendingSequence;
            if (plan == null || !ReferenceEquals(mission.CrossingPlan, plan) || mission.CrossingBlocker.Length == 0) return "";
            var crossing = WaitCrossing(mission, plan, MissionVesselSnapshot(mission));
            mission.CrossingBlocker = crossing != null ? MissionPrompts.MoonCrossingBlocker(crossing) : "";
            return mission.CrossingBlocker;
        }

        /// <summary>The plan offered: its Blocker, when its figures still have a crossing (after the one correction, or with no room for it).</summary>
        private static void CrossingOffered(Mission mission, SequencePlan plan, PlanFindings findings)
        {
            mission.CrossingPlan = plan;
            mission.CrossingBlocker = findings != null && findings.MoonCrossing != null ? MissionPrompts.MoonCrossingBlocker(findings.MoonCrossing) : "";
        }

        /// <summary>The plan in hand holds its Blocker (the stored verdict: Auto Mode asks on every input).</summary>
        private static bool CrossingHeld(Mission mission, SequencePlan plan)
        {
            return plan != null && ReferenceEquals(mission.CrossingPlan, plan) && mission.CrossingBlocker.Length > 0;
        }

        /// <summary>The adapter dropped the warp before a moon nobody planned, with no script running: one log line, one event, and a request naming the moon and the time.</summary>
        private void NextPatchDroppedSeen(NextPatchDropped dropped)
        {
            var mission = _mission;
            if (mission == null || MissionOver || IsRunning(mission)) return;
            Note(LogKind.Notice, dropped.Text);
            if (mission.Folder != null) Event(mission, "next_patch_watch", dropped.Text);
            NeedsPlayer(dropped.Text);
        }

        /// <summary>The craft the adapter watches between runs, and the bodies a planned coast meets; "" when no Mission's craft is between runs.</summary>
        private void NextPatchWatchView(Mission mission, MissionView view)
        {
            if (MissionOver || IsRunning(mission) || !FollowsCraft(mission) || mission.VesselId.Length == 0) return;
            view.NextPatchVesselId = mission.VesselId;
            var token = CoastPlanToken(mission.PendingSequence);
            view.NextPatchPlanned = token != null ? token.Bodies : "";
        }

        /// <summary>The Debrief's fact (#867): the orbit the run left crosses a moon; the rating is Claudia's as before.</summary>
        private void CrossingFact(Mission mission)
        {
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot == null) return;
            // The next plan's wait is not known yet: judged as a long one.
            var crossing = MoonCrossings.Judge(snapshot.At(_ut));
            if (crossing != null) mission.RunEvents.Replace(_ut, RunEventKind.MoonCrossing, MissionPrompts.MoonCrossingFact(crossing));
        }
    }
}
