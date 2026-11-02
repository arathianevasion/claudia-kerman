// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Ui;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Target Vessel (ADR 0013): chosen once, when the Mission starts, from KSP's target or the vessel the Mission Goal names;
    /// the adapter reads it live by its id, and only a Mission with one has its entry in the Game Snapshot.
    /// </summary>
    public sealed partial class MissionControl
    {
        private void ChooseTarget(Mission mission, string goal)
        {
            string how;
            mission.Target = TargetVessel.Choose(goal, _snapshot, out how);
            if (mission.Target != null) Note(LogKind.Notice, "Target Vessel: " + mission.Target.Name + " (" + how + ").");
            else if (how.Length > 0) Note(LogKind.Notice, "No Target Vessel: " + how + ".");
            if (_snapshot != null) FitTarget(_snapshot);
        }

        /// <summary>The snapshot keeps a target entry only for the Mission in play's Target Vessel, so a Mission without one pays nothing.</summary>
        private void FitTarget(GameSnapshot snapshot)
        {
            var target = MissionOver ? null : _mission.Target;
            if (target == null) snapshot.Target = null;
            else if (snapshot.Target == null || snapshot.Target.Id != target.Id)
                snapshot.Target = snapshot.KspTarget != null && snapshot.KspTarget.Id == target.Id ? snapshot.KspTarget : null;
        }

        /// <summary>
        /// The Mission has a Target Vessel the snapshot names among the game's vessels but hasn't read yet (a snapshot taken before
        /// the Mission started): the Mission Plan waits a snapshot for it.
        /// </summary>
        private static bool TargetUnread(Mission mission, GameSnapshot snapshot)
        {
            var target = mission.Target;
            return target != null && snapshot.Target == null && snapshot.Vessels.Any(v => v.Id == target.Id);
        }
    }
}
