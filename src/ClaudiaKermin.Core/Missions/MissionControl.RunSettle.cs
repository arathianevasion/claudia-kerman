// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Debrief that follows a run is built from a Game Snapshot read after the run ended (#671), not the periodic one that
    /// may be up to a second older and still mid-burn. After a run that ends landed, splashed or still coming down, it also waits
    /// until the craft is LANDED or SPLASHED and still, up to <see cref="RunSettleWait"/> of real time. The wait is not a request
    /// and spends nothing; the Mission stays Running until it is over. Not saved: a load turns a Running Mission into an owed Debrief.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>Real time the Debrief waits for the craft to settle before it goes with the latest snapshot.</summary>
        public static readonly TimeSpan RunSettleWait = TimeSpan.FromSeconds(10);

        /// <summary>Surface speed, vertical included, under which a landed craft is still, m/s.</summary>
        public const double SettledSpeed = 0.5;

        /// <summary>The real-time clock the wait runs on; a test sets its own.</summary>
        internal Func<DateTime> Clock = () => DateTime.UtcNow;

        private sealed class RunSettle
        {
            public RunOutcome Outcome;
            public double EndUt;
            public DateTime Deadline;
        }

        private sealed partial class Mission
        {
            /// <summary>The ended run whose Debrief waits for a snapshot read after it; null when none does.</summary>
            public RunSettle Settle;
        }

        /// <summary>The craft is on the ground or coming down to it, so its Debrief waits for it to be still.</summary>
        internal static bool NeedsSettling(GameSnapshot snapshot)
        {
            switch (snapshot.Situation)
            {
                case "LANDED":
                case "SPLASHED":
                    return true;
                case "FLYING":
                case "SUB_ORBITAL":
                    return snapshot.VerticalSpeed < 0;
                default:
                    return false;
            }
        }

        internal static bool IsSettled(GameSnapshot snapshot)
        {
            // Narrower than IsGrounded on purpose: a craft on the pad has not flown, so it has nothing to settle from.
            return (snapshot.Situation == "LANDED" || snapshot.Situation == "SPLASHED")
                && snapshot.SurfaceSpeed < SettledSpeed && Math.Abs(snapshot.VerticalSpeed) < SettledSpeed;
        }

        /// <summary>The run just ended: the Debrief goes now if the snapshot in hand is of the craft after it, else it waits for one.</summary>
        private void AwaitPostRunSnapshot(Mission mission, RunOutcome outcome, double endUt)
        {
            mission.Settle = new RunSettle { Outcome = outcome, EndUt = endUt > 0 ? endUt : _ut, Deadline = Clock() + RunSettleWait };
            // No snapshot of the craft at all (its scene is gone): nothing to wait for.
            if (_snapshot == null) mission.Settle.Deadline = DateTime.MinValue;
            ContinueRunEnd(mission);
        }

        /// <summary>Called as snapshots and the clock arrive: begins the Debrief once the wait is over.</summary>
        private void ContinueRunEnd()
        {
            var mission = _mission;
            if (mission != null && mission.Settle != null) ContinueRunEnd(mission);
        }

        private void ContinueRunEnd(Mission mission)
        {
            var wait = mission.Settle;
            if (wait == null) return;
            // Lost, ended, Manual Control, a new run or a load took the Mission out of the run: that handling wins, and the wait is dropped.
            if (!IsRunning(mission) || mission.RunId.HasValue)
            {
                mission.Settle = null;
                return;
            }
            var snapshot = _snapshot;
            var after = snapshot != null && snapshot.Ut >= wait.EndUt;
            if (Clock() < wait.Deadline && !(after && (!NeedsSettling(snapshot) || IsSettled(snapshot)))) return;
            mission.Settle = null;
            BeginRunDebrief(mission, wait.Outcome);
        }
    }
}
