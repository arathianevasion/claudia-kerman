// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;
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
            /// <summary>The snapshot the held Blocker was last judged on: a new one (the player may have flown the fix) judges it again.</summary>
            public GameSnapshot CrossingJudgedOn;
            /// <summary>The plan in hand was offered with no snapshot of the craft (outside Flight, another vessel active): the first one judges it.</summary>
            public bool CrossingUnjudged;
            /// <summary>The bodies the current or last run's <c>transfer_to</c> aimed at, comma-separated: planned, not crossed, until the next run. Saved.</summary>
            public string RunTransfers = "";
            /// <summary>#871: the current or last run's Sequence Plan name and intent: a moon it names is planned. Saved with <see cref="RunTransfers"/>.</summary>
            public string RunWords = "";
        }

        /// <summary>The saved orbit of the Mission craft outside Flight (#877); not saved, the adapter reads it again.</summary>
        private GameSnapshot _savedOrbit;

        /// <summary>
        /// What the crossing check judges: the Flight snapshot of the craft or, outside Flight, its saved orbit (a landed craft's has none, so no Blocker).
        /// Null with neither (another vessel active, or the craft isn't in the save): a held Blocker stands, an offered plan waits to be judged.
        /// </summary>
        private GameSnapshot CrossingSnapshot(Mission mission)
        {
            var live = MissionVesselSnapshot(mission);
            if (live != null || _scene == GameScene.Flight || _savedOrbit == null) return live;
            return mission.VesselId.Length > 0 && _savedOrbit.Craft.VesselId != mission.VesselId ? null : _savedOrbit;
        }

        /// <summary>The adapter read the Mission craft's saved orbit outside Flight: a plan offered with no snapshot of the craft is judged now.</summary>
        private void SavedOrbitSeen(GameSnapshot saved)
        {
            _savedOrbit = _scene == GameScene.Flight ? null : saved;
            if (_mission != null && _savedOrbit != null) CrossingFirstSnapshot(_mission);
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
            // The last run's transfer_to target is met on the coast it left: planned, not crossed.
            return MoonCrossings.Judge(snapshot.At(_ut), due.Value, RunTransfers(mission));
        }

        private static IList<string> RunTransfers(Mission mission)
        {
            return mission.RunTransfers.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// The Blocker the plan in hand holds, judged now from the craft as it is (the player may have flown the fix); "" for none. With no snapshot of
        /// the craft (outside Flight, another vessel active) a held Blocker stands: only a snapshot that shows no crossing lifts it.
        /// </summary>
        private string CrossingBlockerNow(Mission mission)
        {
            var plan = mission.PendingSequence;
            if (plan == null || !ReferenceEquals(mission.CrossingPlan, plan) || (mission.CrossingBlocker.Length == 0 && !mission.CrossingUnjudged)) return "";
            var snapshot = CrossingSnapshot(mission);
            if (snapshot != null) CrossingJudge(mission, plan, snapshot);
            return mission.CrossingBlocker;
        }

        private void CrossingJudge(Mission mission, SequencePlan plan, GameSnapshot snapshot)
        {
            var crossing = WaitCrossing(mission, plan, snapshot);
            mission.CrossingBlocker = crossing != null ? MissionPrompts.MoonCrossingBlocker(crossing) : "";
            mission.CrossingJudgedOn = snapshot;
            mission.CrossingUnjudged = false;
        }

        /// <summary>The plan offered: its Blocker, when its figures still have a crossing (after the one correction, or with no room for it); unjudged with no snapshot of the craft.</summary>
        private void CrossingOffered(Mission mission, SequencePlan plan, PlanFindings findings)
        {
            mission.CrossingPlan = plan;
            mission.CrossingBlocker = findings != null && findings.MoonCrossing != null ? MissionPrompts.MoonCrossingBlocker(findings.MoonCrossing) : "";
            mission.CrossingUnjudged = mission.CrossingBlocker.Length == 0 && !plan.Manual && FliesCraft(mission, plan.Vessel) && MissionVesselSnapshot(mission) == null;
            // #877: with no snapshot of the craft, its saved orbit judges the plan now.
            var saved = mission.CrossingUnjudged ? CrossingSnapshot(mission) : null;
            if (saved != null) CrossingJudge(mission, plan, saved);
        }

        /// <summary>
        /// The plan as it is offered, its Due Time final: one with a Due Time the plan's figures didn't see (the mod's window wait sets it after them),
        /// or a restored one, is judged here, and a crossing is its Blocker.
        /// </summary>
        private void CrossingAtOffer(Mission mission, SequencePlan plan)
        {
            if (!plan.DueTimeUt.HasValue || CrossingHeld(mission, plan)) return;
            var snapshot = CrossingSnapshot(mission);
            if (snapshot == null)
            {
                // Offered outside Flight (or restored there): the first snapshot of the craft judges it.
                if (!plan.Manual && FliesCraft(mission, plan.Vessel))
                {
                    mission.CrossingPlan = plan;
                    mission.CrossingUnjudged = true;
                }
                return;
            }
            var crossing = WaitCrossing(mission, plan, snapshot);
            if (crossing == null) return;
            mission.CrossingPlan = plan;
            mission.CrossingBlocker = MissionPrompts.MoonCrossingBlocker(crossing);
            mission.CrossingUnjudged = false;
        }

        /// <summary>A snapshot has come: a plan offered with none of the craft is judged now, and a crossing is its Blocker, on its card and in the log.</summary>
        private void CrossingFirstSnapshot(Mission mission)
        {
            var plan = mission.PendingSequence;
            if (!mission.CrossingUnjudged || plan == null || !ReferenceEquals(mission.CrossingPlan, plan) || mission.Stage != MissionStage.AwaitingSequenceApproval) return;
            var snapshot = CrossingSnapshot(mission);
            if (snapshot == null) return;
            CrossingJudge(mission, plan, snapshot);
            if (mission.CrossingBlocker.Length == 0) return;
            Note(LogKind.Notice, mission.CrossingBlocker);
            var card = mission.Log.LastOrDefault(e => e.Kind == LogKind.SequencePlan);
            if (card != null) card.Text = PlanCardText(mission, plan);
        }

        /// <summary>The plan in hand holds its Blocker: judged again only on a new snapshot of the craft, since Auto Mode asks on every input.</summary>
        private bool CrossingHeld(Mission mission, SequencePlan plan)
        {
            if (plan == null || !ReferenceEquals(mission.CrossingPlan, plan) || (mission.CrossingBlocker.Length == 0 && !mission.CrossingUnjudged)) return false;
            var snapshot = CrossingSnapshot(mission);
            if (snapshot != null && !ReferenceEquals(snapshot, mission.CrossingJudgedOn) && ReferenceEquals(mission.PendingSequence, plan)) CrossingJudge(mission, plan, snapshot);
            return mission.CrossingBlocker.Length > 0;
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

        /// <summary>The craft the adapter watches between runs, and the bodies a planned coast meets (a Coast Checkpoint's, the last run's transfer_to's); "" when no Mission's craft is between runs.</summary>
        private void NextPatchWatchView(Mission mission, MissionView view)
        {
            if (MissionOver || IsRunning(mission) || !FollowsCraft(mission) || mission.VesselId.Length == 0) return;
            view.NextPatchVesselId = mission.VesselId;
            var token = CoastPlanToken(mission.PendingSequence);
            var planned = (token != null ? token.Bodies : "").Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).Concat(RunTransfers(mission));
            view.NextPatchPlanned = string.Join(",", planned.Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray());
        }

        /// <summary>The bodies a plan's <c>transfer_to</c> calls aim at, as written; empty for none.</summary>
        private static IList<string> TransferTargets(SequencePlan plan)
        {
            if (plan == null || string.IsNullOrEmpty(plan.Script)) return new List<string>();
            return BlockOptionCheck.Calls(plan.Script).Where(c => c.Block == "transfer_to").Select(c => (c.Option("target") ?? "").Trim().Trim('"'))
                .Where(t => t.Length > 0).ToList();
        }

        /// <summary>The Debrief's fact (#867): the orbit the run left crosses a moon; the rating is Claudia's as before.</summary>
        private void CrossingFact(Mission mission)
        {
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot == null) return;
            // A transfer_to that never ejected (refused, failed) planned nothing: only a target the path now meets, or is round, stays planned.
            mission.RunTransfers = string.Join(",", RunTransfers(mission).Where(t => MoonCrossings.Meets(snapshot.At(_ut), t)).ToArray());
            // The next plan's wait is not known yet: judged as a long one. A moon the run's own transfer_to aims at is planned, not crossed.
            // An ellipse capture kept for the next leg's moon (#866) waits only to its window: that wait is followed, the moon met after the burn.
            var planned = RunTransfers(mission).ToList();
            var at = snapshot.At(_ut);
            if (mission.Plan != null) at.LegsAhead = mission.Plan.Sequences.Skip(System.Math.Max(0, StepsBegun(mission) - 1)).ToList();
            // #871: a moon the Sequence Plan or the Mission Plan's current or later legs name is planned too (a capture met without transfer_to, a gravity assist),
            // when the craft is on its way to meet it; a parking orbit that merely reaches it is still a crossing.
            // Named as a destination only: a moon a leg departs or returns from is where the craft was, not where it goes.
            var texts = new List<string> { mission.RunWords };
            if (at.LegsAhead != null) texts.AddRange(at.LegsAhead.Select(SequenceWords.Of));
            planned.AddRange(texts.SelectMany(t => SequenceWords.Destinations(snapshot, t)).Select(b => b.Name).Distinct(System.StringComparer.OrdinalIgnoreCase)
                .Where(n => MoonCrossings.Meets(at, n)));
            var window = KeptRoute.KeptWindow(at);
            var crossing = window.HasValue ? MoonCrossings.Judge(snapshot.At(_ut), window.Value, planned) : MoonCrossings.Judge(snapshot.At(_ut), planned);
            if (crossing != null) mission.RunEvents.Replace(_ut, RunEventKind.MoonCrossing, MissionPrompts.MoonCrossingFact(crossing, MoonCrossings.Meets(at, crossing.Moon.Name)));
        }
    }
}
