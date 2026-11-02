// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKermin.Core.Ui;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The Target Vessel (ADR 0013, #643): chosen once, when the Mission starts, by the same choice the start form shows;
    /// the adapter reads it live by its id, and only a Mission with one has its entry in the Game Snapshot.
    /// </summary>
    public sealed partial class MissionControl
    {
        private GameSnapshot _targetFactsFrom;
        private TargetFacts _targetFacts = new TargetFacts();

        /// <summary>What the Target Vessel is chosen from, as of the latest snapshot: one object per snapshot, so the form can cache its choice.</summary>
        private TargetFacts CurrentTargetFacts()
        {
            if (!ReferenceEquals(_targetFactsFrom, _snapshot))
            {
                _targetFactsFrom = _snapshot;
                _targetFacts = TargetFacts.Of(_snapshot);
            }
            return _targetFacts;
        }

        /// <summary>The Target Vessel for this goal and the form's pick (#643): the same choice the start form shows.</summary>
        private TargetChoice ChooseTarget(string goal, TargetPick pick)
        {
            return TargetVessel.Choose((goal ?? "").Trim(), pick, CurrentTargetFacts());
        }

        /// <summary>The Mission takes the target the Craft Check judged, and the log says how it was found or why there is none.</summary>
        private void TakeTarget(Mission mission, TargetChoice choice)
        {
            mission.Target = choice.Target;
            if (choice.LogLine.Length > 0) Note(LogKind.Notice, choice.LogLine);
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
