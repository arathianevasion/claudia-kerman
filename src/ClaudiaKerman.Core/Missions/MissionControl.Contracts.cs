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
        private readonly HashSet<string> _unticked = new HashSet<string>();
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
        /// and the typed words alone fit. A start already answered from the card goes on.
        /// </summary>
        private bool HeldForContracts(StartMission start)
        {
            if (start.ContractsAnswered) return false;
            _folded = null;
            _contractCheck = null;
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.Contracts == null || snapshot.Contracts.Accepted.Count == 0) return false;
            var check = ContractCheck.Work(snapshot, start.Goal, start.Remote, _config.CraftCheckMarginPercent);
            if (!check.Priceable || !check.WordsFit) return false;
            _contractCheck = check;
            _contractStart = start;
            _unticked.Clear();
            return true;
        }

        /// <summary>Why a start with nothing typed can't go on; "" when there are no accepted Contracts (the form needs words then).</summary>
        private string NoWordsRefusal()
        {
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.Contracts == null || snapshot.Contracts.Accepted.Count == 0) return "";
            return "The craft's delta-v isn't known (turn on the stock delta-v readout), so your Contracts can't be priced. Type what this Mission should do, or turn it on and start again.";
        }

        private IList<string> Ticked()
        {
            return _contractCheck == null ? new List<string>() : _contractCheck.Fitting.Where(r => !_unticked.Contains(r.Guid)).Select(r => r.Guid).ToList();
        }

        private void ContractTicked(TickContract tick)
        {
            if (_contractCheck == null || _contractCheck.Fitting.All(r => r.Guid != tick.Guid)) return;
            if (tick.Ticked) _unticked.Remove(tick.Guid);
            else _unticked.Add(tick.Guid);
        }

        private void ContractsPlanned()
        {
            var ticked = Ticked();
            if (_contractCheck == null || ticked.Count == 0) return;
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
                Rows = check.Fitting.Select(r => new ContractCheckRow
                {
                    Guid = r.Guid, Title = r.Title, Phase = r.Phase, AddedDeltaV = r.AddedDeltaV, DeadlineUt = r.DeadlineUt, Note = r.Note, Ticked = ticked.Contains(r.Guid),
                }).ToList(),
                NotThisFlight = check.NotThisFlight.ToList(),
                CraftDeltaV = check.CraftDeltaV, RouteDeltaV = check.RouteDeltaV, ReservePercent = check.ReservePercent, Reserve = check.Reserve, Spare = check.Spare,
                PlanLabel = ticked.Count > 0 ? "Plan these " + ticked.Count : "Pick a Contract",
                CanPlan = ticked.Count > 0,
                CanPlanWithout = check.Words.Length > 0,
            };
        }
    }
}
