// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// A window wait over 6 h is a Due Time (#616, ADR 0004's amendment; <see cref="WindowWaits"/>): the first Maneuver Sequence
    /// of a Mission to another planet gets a Due Time at the Transfer Window less the launch lead, and a player who comes back
    /// after the window has the Due Time moved to the next one when it fits the Craft Check budget, or an Alert to Claudia, who
    /// revises the Mission Plan. Auto Mode never approves at a Due Time (MissionControl.AutoMode.cs), so only the player does.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>The first Maneuver Sequence waits on the pad for a window more than a Kerbin day off: its Due Time is the window less the launch lead.</summary>
        private void WindowWait(Mission mission, SequencePlan plan)
        {
            ClearWindow(mission);
            // Only before the first run: the craft waits on the pad, and the launch carries the Due Time.
            if (plan.Manual || mission.RunStartUt != 0 || _snapshot == null) return;
            var planet = WindowWaits.Planet(_snapshot, mission.Goal);
            var window = WindowFor(mission, planet);
            if (window == null) return;
            var due = WindowWaits.DueTime(window.DepartureUt, _ut);
            if (!due.HasValue) return;
            // A later Due Time of Claudia's own (a wait for the player) stands, and is not a window's.
            if (plan.DueTimeUt.HasValue && plan.DueTimeUt.Value >= due.Value) return;
            plan.DueTimeUt = due.Value;
            mission.WindowPlanet = planet.Name;
            mission.WindowUt = window.DepartureUt;
            mission.WindowDeltaV = window.TotalDv;
            Note(LogKind.Notice, "Waiting for " + WindowWaits.WaitsFor(planet.Name) + ": the launch is due " + KerbinTime.Date(plan.DueTimeUt.Value)
                + ", an hour before the ejection burn. Fly other Missions meanwhile; a KSP alarm calls you back.");
        }

        private TransferWindow WindowFor(Mission mission, SnapshotBody planet)
        {
            if (planet == null || _snapshot == null) return null;
            var settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
            // The search starts now, not at the snapshot's time, which may be older (the player is back from another Mission).
            return WindowWaits.Window(_snapshot.At(_ut), planet, settings.TimeVersusDeltaV, _config.CraftCheckMarginPercent);
        }

        private static void ClearWindow(Mission mission)
        {
            mission.WindowPlanet = "";
            mission.WindowUt = null;
            mission.WindowDeltaV = null;
        }

        /// <summary>The pending plan's window was missed: Approve asks for the next one.</summary>
        private bool WindowWasMissed(Mission mission)
        {
            return mission.WindowUt.HasValue && WindowWaits.Missed(mission.WindowUt.Value, _ut);
        }

        /// <summary>
        /// The player approved after the window: #458's solver gives the next one. Within the Craft Check budget, the Due Time moves
        /// to it (false: not yet) or, when it is under 6 h off, goes and the script warps there (true: carry on starting); otherwise
        /// an Alert asks Claudia for a revised Mission Plan or a reduced goal (false).
        /// </summary>
        private bool NextWindow(Mission mission, SequencePlan plan)
        {
            var missed = mission.WindowUt.Value;
            var planned = mission.WindowDeltaV ?? 0;
            var planet = _snapshot == null ? null : _snapshot.Bodies.FirstOrDefault(b => b.Name == mission.WindowPlanet);
            var next = WindowFor(mission, planet);
            var budget = mission.Plan == null ? 0 : mission.Plan.Sequences.Sum(s => Math.Max(0, s.DeltaV));
            var head = "The " + mission.WindowPlanet + " window of " + KerbinTime.Date(missed) + " was missed";
            if (next == null || !WindowWaits.Fits(budget, planned, next.TotalDv, CraftDeltaV(_snapshot), _config.CraftCheckMarginPercent))
            {
                WindowAlert(mission, head, next, planned);
                return false;
            }
            var cost = " (" + MetresPerSecond(next.TotalDv) + " against " + MetresPerSecond(planned) + " planned, within the Craft Check budget)";
            mission.WindowUt = next.DepartureUt;
            mission.WindowDeltaV = next.TotalDv;
            var due = WindowWaits.DueTime(next.DepartureUt, _ut);
            if (due.HasValue)
            {
                plan.DueTimeUt = due.Value;
                mission.DueNotified = false;
                _commands.Add(new SetDueTimeAlarm(mission.Id, due.Value, "Claudia: " + plan.Name));
                Note(LogKind.Notice, head + ": the next one leaves " + KerbinTime.Date(next.DepartureUt) + cost + ", so the Due Time moves to "
                    + KerbinTime.Date(due.Value) + ".");
                return false;
            }
            plan.DueTimeUt = null;
            ClearWindow(mission);
            _commands.Add(new ClearDueTimeAlarm(mission.Id));
            Note(LogKind.Notice, head + ": the next one leaves " + KerbinTime.Date(next.DepartureUt) + cost + ", under 6 h off, so the script warps there.");
            return true;
        }

        /// <summary>The next window doesn't fit, or there is none: the plan is dropped and Claudia is asked for a revised Mission Plan or a reduced goal.</summary>
        private void WindowAlert(Mission mission, string head, TransferWindow next, double planned)
        {
            var why = next == null ? "no next window was found" : "the next one, leaving " + KerbinTime.Date(next.DepartureUt) + ", costs "
                + MetresPerSecond(next.TotalDv) + " against " + MetresPerSecond(planned) + " planned, outside the Craft Check budget";
            Note(LogKind.Notice, head + " and " + why + ", so Claudia is revising the Mission Plan.");
            DropPendingSequence(mission);
            ClearWindow(mission);
            mission.ConfirmPrompt = null;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = MissionPrompts.WindowMissed(head + " and " + why + ".") + "\n" + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = MissionPrompts.WindowMissedNotice,
            });
        }

        /// <summary>The craft's vacuum delta-v from the stock simulator; null when it gives none or reads unreliably.</summary>
        private static double? CraftDeltaV(GameSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.DeltaVSimulator || snapshot.Stages.Count == 0 || snapshot.DeltaVUnreliable) return null;
            return snapshot.Stages.Sum(s => s.DeltaVVac);
        }
    }
}
