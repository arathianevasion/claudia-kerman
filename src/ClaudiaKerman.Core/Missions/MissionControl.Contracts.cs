// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// #1022: Start Mission with accepted Contracts. Before any request, Mission Control's Contract check card is posted (the mod's own,
    /// ADR 0021): what fits, pre-ticked, and what isn't on this flight and why. "Plan these" folds it to one line and carries on with the
    /// start, the chosen Contracts' route added to the player's words for the Craft Check and their Contract Route section added to the
    /// Mission's first request; "Plan without Contracts" carries on with the words alone. With no accepted Contracts, or when the typed words
    /// alone don't fit the craft (the Craft Check then gives its Blocker), Start Mission is as it was.
    /// </summary>
    public sealed partial class MissionControl
    {
        private ContractCheck _contractCheck;
        /// <summary>The Start Mission the card answers.</summary>
        private StartMission _contractStart;
        /// <summary>"Plan these" was pressed: the card's one line, shown until the Mission starts; null otherwise.</summary>
        private ContractChoice _folded;

        private sealed partial class Mission
        {
            /// <summary>#1022: the player's own words of a Mission started from the Contract check card; "" when it wasn't.</summary>
            public string ContractWords = "";
            /// <summary>#1022: the Contract Route section of its first request; "" with no Contracts chosen.</summary>
            public string ContractRoute = "";
        }

        /// <summary>How many Contracts the latest Game Snapshot has accepted.</summary>
        private int AcceptedContracts
        {
            get { return _snapshot == null || _snapshot.Contracts == null ? 0 : _snapshot.Contracts.Accepted.Count; }
        }

        /// <summary>
        /// The card is posted for this start and it waits for the player's answer: there are accepted Contracts, the craft's delta-v is known
        /// and the typed words alone fit. A start already answered from the card goes on. Pressed again, Start Mission posts a new card in
        /// place of the old (new words, a changed craft). Why the start can't go on at all, or "": the craft's delta-v isn't known, so the
        /// Contracts can't be priced (never dropped silently), or nothing typed and the craft can't reach orbit.
        /// </summary>
        private bool HeldForContracts(StartMission start, out string refusal)
        {
            refusal = "";
            if (start.ContractsAnswered) return false;
            _folded = null;
            _contractCheck = null;
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.Contracts == null || snapshot.Contracts.Accepted.Count == 0) return false;
            var check = ContractCheck.Work(snapshot, start.Goal, start.Remote, _config.CraftCheckMarginPercent);
            if (!check.Priceable)
            {
                refusal = "The craft's delta-v isn't known (turn on the stock delta-v readout), so your Contracts can't be priced. Turn it on, then start again.";
                return false;
            }
            if (!check.WordsFit)
            {
                if (check.Words.Length == 0) refusal = "This craft can't reach orbit with the Craft Check's reserve, so no Contract fits.";
                return false;
            }
            _contractCheck = check;
            _contractStart = start;
            return true;
        }

        /// <summary>The set the player has ticked: the suggestion until they edit it (#1023).</summary>
        private IList<string> Ticked()
        {
            return _contractCheck == null ? new List<string>() : _contractCheck.TickedGuids;
        }

        private void ContractsPlanned()
        {
            var ticked = Ticked();
            // A set that doesn't fit is never sent: "Plan these" is greyed for it.
            if (_contractCheck == null || ticked.Count == 0 || !_contractCheck.SetFits) return;
            var choice = _contractCheck.Choose(ticked);
            var start = _contractStart.Answered(choice.Goal, choice);
            _contractCheck = null;
            _folded = choice;
            Start(start);
        }

        private void PlannedWithoutContracts()
        {
            if (_contractCheck == null || _contractCheck.Words.Length == 0) return;
            var start = _contractStart.Answered(_contractCheck.Words, null);
            _contractCheck = null;
            Start(start);
        }

        /// <summary>A Mission starting from the card: its words, its Contract Route and the card's line in its transcript.</summary>
        private void TakeContracts(Mission mission, StartMission start)
        {
            if (start.Contracts == null || start.Contracts.Guids.Count == 0) return;
            mission.ContractWords = start.Contracts.Words;
            mission.ContractRoute = start.Contracts.Section;
            Note(LogKind.Notice, start.Contracts.Summary);
            _folded = null;
        }

        /// <summary>The Mission Plan request's goal: the player's words as ever, or with Contracts chosen, the words and the Contract Route.</summary>
        private static string GoalText(Mission mission)
        {
            if (mission.ContractRoute.Length == 0) return "Mission Goal, in the player's words (quoted data): " + MissionPrompts.Quoted(mission.Goal) + "\n";
            return (mission.ContractWords.Length > 0
                ? "The player's own words for this Mission (quoted data): " + MissionPrompts.Quoted(mission.ContractWords) + "\n"
                : "No words typed: the chosen Contracts below are the Mission Goal.\n") + mission.ContractRoute;
        }

        private ContractCheckView ContractCheckView()
        {
            if (_folded != null && MissionOver) return new ContractCheckView { Folded = true, Summary = _folded.Summary };
            var check = _contractCheck;
            if (check == null || !MissionOver) return null;
            var ticked = Ticked();
            return new ContractCheckView
            {
                Accepted = check.Accepted, Words = check.Words,
                Rows = check.Fitting.ToList(),
                Headline = check.Headline(ticked.Count),
                NotThisFlight = check.NotThisFlight.ToList(),
                CraftDeltaV = check.CraftDeltaV, RouteDeltaV = check.RouteDeltaV, ReservePercent = check.ReservePercent, Reserve = check.Reserve, Spare = check.Spare,
                PlanLabel = ticked.Count > 0 ? "Plan these " + ticked.Count : "Pick a Contract",
                CanPlan = ticked.Count > 0 && check.SetFits,
                Shortfall = check.Problem,
                CanPlanWithout = check.Words.Length > 0,
            };
        }
    }
}
