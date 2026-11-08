// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// A window wait over 6 h is a Due Time (#616, ADR 0004's amendment; <see cref="WindowWaits"/>): the first Maneuver Sequence
    /// of a Mission to another planet gets a Due Time at the Transfer Window less the launch lead, and a player who comes back
    /// after the window has the Due Time moved to the next one when it fits the Craft Check budget, or an Alert to Claudia, who
    /// revises the Mission Plan. Auto Mode never approves at a Due Time (MissionControl.AutoMode.cs), so only the player does.
    /// #774: a craft on another body's surface waits there the same way, the ascent carrying the Due Time.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>The first Maneuver Sequence waits on the pad for a window more than a Kerbin day off: its Due Time is the window less the launch lead. A craft on the surface waits before its ascent instead (#774).</summary>
        private void WindowWait(Mission mission, SequencePlan plan)
        {
            var carriedPlanet = mission.WindowPlanet;
            var carriedUt = mission.WindowUt;
            var carriedDv = mission.WindowDeltaV;
            ClearWindow(mission);
            var surface = SurfaceWait(mission, plan);
            if (surface != null)
            {
                // A manual stretch with a Due Time of Claudia's own waits for that, not a window.
                if (plan.Manual && plan.DueTimeUt.HasValue) return;
                // Back after the departure less the lead, from a manual stretch that noted the window: Approve goes to the missed-window
                // handling, as on the pad. Inside the lead that starts the ascent for this window; after the departure, the next one.
                if (carriedUt.HasValue && carriedPlanet == surface.Planet.Name && _ut > carriedUt.Value - surface.LeadS)
                {
                    mission.WindowPlanet = carriedPlanet;
                    mission.WindowUt = carriedUt;
                    mission.WindowDeltaV = carriedDv;
                    if (!plan.Manual && !plan.DueTimeUt.HasValue) plan.DueTimeUt = carriedUt.Value - surface.LeadS;
                    return;
                }
                FromTheSurface(mission, plan, surface);
                return;
            }
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

        /// <summary>#774: the ascent waits on the surface for its departure's window. A manual stretch has no Due Time and is never cut short: it only notes the window, for the ascent after it.</summary>
        private void FromTheSurface(Mission mission, SequencePlan plan, SurfaceDeparture surface)
        {
            if (surface.Window == null) return;
            if (!plan.Manual)
            {
                if (!surface.DueUt.HasValue) return;
                // A later Due Time of Claudia's own stands, as on the pad.
                if (plan.DueTimeUt.HasValue && plan.DueTimeUt.Value >= surface.DueUt.Value) return;
                plan.DueTimeUt = surface.DueUt.Value;
                Note(LogKind.Notice, "Waiting for " + WindowWaits.WaitsFor(surface.Planet.Name) + ": the ascent is due " + KerbinTime.Date(plan.DueTimeUt.Value)
                    + ", " + KerbinTime.Span(surface.Window.DepartureUt - plan.DueTimeUt.Value) + " before the departure. Fly other Missions meanwhile; a KSP alarm calls you back.");
            }
            mission.WindowPlanet = surface.Planet.Name;
            mission.WindowUt = surface.Window.DepartureUt;
            mission.WindowDeltaV = surface.Window.TotalDv;
        }

        /// <summary>The departure the plan makes from the surface, with its window from now (<see cref="WindowWaits.AfterAscent"/>); null when it makes none.</summary>
        private SurfaceDeparture SurfaceWait(Mission mission, SequencePlan plan)
        {
            if (!FliesCraft(mission, plan.Vessel)) return null;
            var snapshot = MissionVesselSnapshot(mission);
            var surface = WindowWaits.AfterAscent(snapshot, plan, mission.Plan == null ? null : mission.Plan.Sequences, StepOf(mission, plan));
            if (surface == null) return null;
            var settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
            WindowWaits.Solve(snapshot, surface, settings.TimeVersusDeltaV, _config.CraftCheckMarginPercent, _ut, mission.WindowPick);
            return surface;
        }

        /// <summary>The Mission Plan step <paramref name="plan"/> flies, 1-based, as OfferSequencePlan counts it, whether or not this plan is counted yet.</summary>
        private static int StepOf(Mission mission, SequencePlan plan)
        {
            return plan.PlanStep ?? mission.SequencesProposed + (mission.SequenceCounted ? 0 : 1);
        }

        /// <summary>
        /// #749: a transfer_to or return that waits more than a Kerbin day inside its block for its ejection makes the plan a Due Time
        /// at the ejection less the launch lead, as a window wait on the pad does; a shorter wait is <c>expected_duration_s</c>'s
        /// (<see cref="BurnPoints.ShortOf"/>). The craft coasts on rails meanwhile; Auto Mode never approves at a Due Time.
        /// </summary>
        private void TransferWait(Mission mission, SequencePlan plan)
        {
            if (plan.Manual || !FliesCraft(mission, plan.Vessel)) return;
            var snapshot = MissionVesselSnapshot(mission);
            // The block's own wait takes the window the Mission does, the player's pick included (#686).
            if (snapshot != null) snapshot.StampMission(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config)).TimeVersusDeltaV, _config.CraftCheckMarginPercent, mission.WindowPick);
            var wait = BurnPoints.First(plan.Script, snapshot, _ut, plan.ExpectedDeltaV, _body);
            var due = BurnPoints.DueTime(wait, _ut);
            if (!due.HasValue) return;
            if (plan.DueTimeUt.HasValue && plan.DueTimeUt.Value >= due.Value) return;
            plan.DueTimeUt = due.Value;
            // #887: a return's window is kept, so a late Approval is a missed window (WindowWaits.Missed) rather than a search on to the next band.
            var home = wait.Block == "return" && wait.DueByUt.HasValue && (wait.DueByUt.Value < wait.DepartureUt.Value || wait.CostlyWindow) ? ReturnAim.PlanetOf(snapshot.At(_ut)) : null;
            if (home != null)
            {
                mission.WindowPlanet = home.Name;
                mission.WindowUt = wait.DepartureUt.Value;
                mission.WindowDeltaV = Math.Max(0, plan.ExpectedDeltaV);
            }
            Note(LogKind.Notice, "Waiting for the ejection: " + wait.Block + " leaves " + KerbinTime.Date(wait.DepartureUt.Value) + ", so the sequence is due "
                + KerbinTime.Date(due.Value) + ", " + KerbinTime.Span(wait.DepartureUt.Value - due.Value).Trim() + " before. Fly other Missions meanwhile; a KSP alarm calls you back.");
        }

        /// <summary>The plan's first burn is a <c>return</c> (#887), the one whose window <see cref="BurnPoints"/> reads.</summary>
        internal static bool FirstIsReturn(SequencePlan plan)
        {
            if (plan == null || plan.Manual || string.IsNullOrWhiteSpace(plan.Script)) return false;
            var first = BlockOptionCheck.Calls(plan.Script).FirstOrDefault(c => c.Block != "coast");
            return first != null && first.Block == "return";
        }

        private TransferWindow WindowFor(Mission mission, SnapshotBody planet)
        {
            if (planet == null || _snapshot == null) return null;
            var settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
            // The search starts now, not at the snapshot's time, which may be older (the player is back from another Mission).
            return WindowWaits.Window(_snapshot.At(_ut), planet, settings.TimeVersusDeltaV, _config.CraftCheckMarginPercent, mission.WindowPick);
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
            var surface = SurfaceWait(mission, plan);
            // #887: a return has no transfer window to move to, so a missed one goes to Claudia, in a return's own words (#905).
            if (surface == null && FirstIsReturn(plan))
            {
                ReturnWindowAlert(mission, plan, missed);
                return false;
            }
            var next = surface != null ? surface.Window : WindowFor(mission, planet);
            // On the pad that is the whole plan; from a surface in mid-Mission, the legs still to fly (this one too), which the craft's delta-v is for.
            var budget = mission.Plan == null ? 0 : mission.Plan.Sequences.Skip(surface != null ? Math.Max(0, StepOf(mission, plan) - 1) : 0).Sum(s => Math.Max(0, s.DeltaV));
            var head = "The " + mission.WindowPlanet + " window of " + KerbinTime.Date(missed) + " was missed";
            if (next == null || !WindowWaits.Fits(budget, planned, next.TotalDv, CraftDeltaV(_snapshot), _config.CraftCheckMarginPercent))
            {
                WindowAlert(mission, head, next, planned);
                return false;
            }
            var cost = " (" + MetresPerSecond(next.TotalDv) + " against " + MetresPerSecond(planned) + " planned, within the Craft Check budget)";
            mission.WindowUt = next.DepartureUt;
            mission.WindowDeltaV = next.TotalDv;
            var due = surface != null ? surface.DueUt : WindowWaits.DueTime(next.DepartureUt, _ut);
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
            AskForRevisedPlan(mission, MissionPrompts.WindowMissed(head + " and " + why + "."), MissionPrompts.WindowMissedNotice);
        }

        /// <summary>#905: a return's planned window was missed: the plan is dropped and Claudia is asked, with the next cheap window if the block's own search finds one.</summary>
        private void ReturnWindowAlert(Mission mission, SequencePlan plan, double missed)
        {
            var next = NextReturnWindowUt(mission, plan);
            Note(LogKind.Notice, MissionPrompts.ReturnWindowMissedLine(missed, next, _ut));
            AskForRevisedPlan(mission, MissionPrompts.ReturnWindowMissed(next), MissionPrompts.ReturnWindowMissedNotice);
        }

        /// <summary>The UT the return's block would take from now (<see cref="BurnPoints.First"/>, as the Due Time reads it, else the block's next pass); null when the block's search finds none.</summary>
        private double? NextReturnWindowUt(Mission mission, SequencePlan plan)
        {
            var snapshot = MissionVesselSnapshot(mission);
            if (snapshot == null) return null;
            snapshot.StampMission(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config)).TimeVersusDeltaV, _config.CraftCheckMarginPercent, mission.WindowPick);
            var wait = BurnPoints.First(plan.Script, snapshot, _ut, plan.ExpectedDeltaV, _body);
            if (wait != null && wait.DepartureUt.HasValue && wait.DepartureUt.Value > _ut) return wait.DepartureUt;
            // No fixed window (BurnPoints.First gives none for the block's next pass): the UT the block's own search would fly.
            var pass = BurnPoints.ReturnPassUt(plan.Script, snapshot, _ut);
            return pass.HasValue && pass.Value > _ut ? pass : null;
        }

        private void AskForRevisedPlan(Mission mission, string alert, string notice)
        {
            DropPendingSequence(mission);
            ClearWindow(mission);
            mission.ConfirmPrompt = null;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = alert + "\n" + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = notice,
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
