// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>#1284: the card up was reopened by Change Contracts, so its ticks are the player's Chosen Contracts, not the suggestion.</summary>
        private bool _reopened;
        /// <summary>#1292: why the last Change Contracts press did nothing, said on the folded line; "" when it wasn't refused, or once it succeeded or the Contracts are fixed.</summary>
        private string _changeRefusal = "";

        private sealed partial class Mission
        {
            /// <summary>#1022: the player's own words of a Mission started from the Contract check card; "" when it wasn't.</summary>
            public string ContractWords = "";
            /// <summary>#1285: the Chosen Contracts the Mission started on, kept until its first Mission Plan is approved so Change Contracts can reopen them; null when none were chosen (not saved).</summary>
            public ContractChoice Chosen;
            /// <summary>#1022: the Contract Route section of its first request; "" with no Contracts chosen.</summary>
            public string ContractRoute = "";
            /// <summary>#1024: the Contract Requirements the player chose; the plan proposal is checked against them. Empty with none chosen.</summary>
            public List<ChosenRequirement> ChosenRequirements = new List<ChosenRequirement>();
            /// <summary>#1024: the Contract Route's record of the leg serving each chosen Requirement, from the plan on offer.</summary>
            public List<ServedRequirement> ServedBy = new List<ServedRequirement>();
            /// <summary>#1024: the Serves tags of each leg of the plan on offer, from the mod; empty with none chosen.</summary>
            public IList<string[]> ServesTags = new List<string[]>();
            /// <summary>#1025: what the player typed during the Mission; a revised plan may add a Contract only at their asking.</summary>
            public readonly List<string> PlayerAsked = new List<string>();
            /// <summary>#1025: the plan on offer's drops (GUID, the line with the reason) and the Requirements it adds; applied at its Approval.</summary>
            public Dictionary<string, string> PlanDrops = new Dictionary<string, string>();
            public List<ChosenRequirement> PlanAdded = new List<ChosenRequirement>();
        }

        /// <summary>#1025: copies of what the revised plan's Contract check needs, taken on the main thread for the tool's worker.</summary>
        private sealed class ContractInputs
        {
            public List<ChosenRequirement> Chosen;
            public string Words;
            public Dictionary<string, string> Droppable;
            public HashSet<string> Completed;
            public HashSet<string> Ended;
            public List<string> Asked;
            public int FirstUnflown;
        }

        private static ContractInputs ContractInputsOf(Mission mission)
        {
            return new ContractInputs
            {
                Chosen = mission.ChosenRequirements.ToList(), Words = mission.ContractWords,
                Droppable = new Dictionary<string, string>(mission.Contracts.Droppable), Completed = new HashSet<string>(mission.Contracts.Completed),
                Asked = mission.PlayerAsked.ToList(), Ended = new HashSet<string>(mission.Contracts.Ended),
                FirstUnflown = mission.Plan == null ? 0 : MissionCards.LegInHand(StepsBegun(mission), mission.SequenceCounted),
            };
        }

        private static ContractReplan Replan(GameSnapshot snapshot, MissionPlan plan, ContractInputs inputs)
        {
            return ContractReplan.Check(snapshot, plan, inputs.Chosen, inputs.Words, CraftCheckFlow.HomeBody, inputs.Droppable, inputs.Completed, inputs.Asked, inputs.Ended, inputs.FirstUnflown);
        }

        /// <summary>
        /// #1024: the chosen Contract Requirements a proposed Mission Plan leaves unserved or can't meet, as findings for Claudia; the Serves
        /// tags and the Contract Route's record are made when the plan is offered (<see cref="OfferMissionPlan"/>).
        /// </summary>
        private static IList<string> ContractProblems(GameSnapshot snapshot, MissionPlan plan, ContractInputs inputs)
        {
            if (snapshot == null || (inputs.Chosen.Count == 0 && inputs.Asked.Count == 0)) return new string[0];
            return Replan(snapshot, plan, inputs).Problems;
        }

        /// <summary>#1024: the plan's Serves tags and the Contract Route's record, set on the Mission as the plan is offered.</summary>
        private void ServePlan(Mission mission, MissionPlan plan)
        {
            mission.ServedBy = new List<ServedRequirement>();
            mission.ServesTags = new List<string[]>();
            mission.PlanDrops = new Dictionary<string, string>();
            mission.PlanAdded = new List<ChosenRequirement>();
            if (_snapshot == null || (mission.ChosenRequirements.Count == 0 && mission.PlayerAsked.Count == 0) || plan.Sequences.Count == 0) return;
            var served = Replan(_snapshot, plan, ContractInputsOf(mission));
            mission.ServedBy = served.Record;
            mission.ServesTags = served.Legs;
            mission.PlanDrops = served.Drops;
            mission.PlanAdded = served.Added;
        }

        /// <summary>#1025: the plan on offer's Contract drops and additions, for its card; "" with none.</summary>
        private static string ContractPlanLines(Mission mission)
        {
            var text = "";
            if (mission.PlanDrops.Count > 0) text += "\nDrops: " + string.Join("; ", mission.PlanDrops.Values.ToArray());
            if (mission.PlanAdded.Count > 0) text += "\nAdds, as you asked: " + string.Join("; ", mission.PlanAdded.Select(r => r.Title).Distinct().ToArray());
            return text;
        }

        /// <summary>#1025: the approved plan's drops and additions become the Mission's chosen Contracts.</summary>
        private void ApplyContractChanges(Mission mission)
        {
            if (mission.PlanDrops.Count == 0 && mission.PlanAdded.Count == 0) return;
            mission.ChosenRequirements = mission.ChosenRequirements.Where(r => !mission.PlanDrops.ContainsKey(ContractWatch.GuidOf(r.Key)))
                .Concat(mission.PlanAdded.Where(a => mission.ChosenRequirements.All(r => r.Key != a.Key))).ToList();
            foreach (var drop in mission.PlanDrops.Values) Notice("Contract dropped from the Mission: " + drop + ".");
            mission.PlanDrops = new Dictionary<string, string>();
            mission.PlanAdded = new List<ChosenRequirement>();
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
        /// Contracts can't be priced (never dropped silently), or the craft can't reach orbit with nothing typed (no Contract fits). Words typed that don't fit go on to the Craft Check (#1241).
        /// </summary>
        private bool HeldForContracts(StartMission start, out string refusal)
        {
            refusal = "";
            _craftCheck.HopContractsWithoutWords = "";
            if (start.ContractsAnswered) return false;
            _folded = null;
            _contractCheck = null;
            _changeRefusal = "";
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.Contracts == null || snapshot.Contracts.Accepted.Count == 0)
            {
                LogToKsp("Contract check card skipped: " + (snapshot == null ? "no Game Snapshot yet" : snapshot.Contracts == null ? "no Contracts read (not Career, or the Contract system isn't loaded)" : "no accepted Contracts") + ".");
                return false;
            }
            WriteContractsRead(snapshot);
            // #1033: a target the goal or the player's pick gives wins over a Target Vessel Contract's vessel (ADR 0013's amendment).
            var check = ContractCheck.Work(snapshot, start.Goal, start.Remote, _config.CraftCheckMarginPercent, GivenTarget(start.Goal, start.Target));
            if (!check.Priceable)
            {
                refusal = "The craft's delta-v isn't known (turn on the stock delta-v readout), so your Contracts can't be priced. Turn it on, then start again.";
                LogToKsp("Contract check card refused: " + refusal);
                return false;
            }
            if (!check.WordsFit)
            {
                if (check.Words.Length == 0) refusal = "This craft can't reach orbit with the Craft Check's reserve, so no Contract fits.";
                // #1241: words that don't fit go on to the Craft Check, whose Blocker names the hop Contracts the craft could fly without them.
                else _craftCheck.HopContractsWithoutWords = check.HopsWithoutWords();
                LogToKsp(refusal.Length > 0 ? "Contract check card refused: " + refusal : "Contract check card skipped: the typed words don't fit the craft; the Craft Check goes on without Contracts.");
                return false;
            }
            _contractCheck = check;
            _contractStart = start;
            _reopened = false;
            LogToKsp(ContractCardLog.Posted(check, Ticked()));
            return true;
        }

        /// <summary>#1262: a line for KSP.log, under the mod's prefix.</summary>
        private void LogToKsp(string text)
        {
            _commands.Add(new LogToGame(text));
        }

        /// <summary>#1262: each accepted Contract's read tree, saved to the save's Claudia contracts folder as Start Mission goes, as the capture button does.</summary>
        private void WriteContractsRead(GameSnapshot snapshot)
        {
            if (_saveDirectory == null) return;
            try
            {
                var written = MissionFolder.WriteContractSnapshots(_saveDirectory, snapshot);
                if (written.Count > 0) LogToKsp("Contracts read at Start Mission saved (" + written.Count + ") to " + Path.GetDirectoryName(written[0]));
            }
            catch (Exception e) when (IsFileError(e))
            {
                LogToKsp("Couldn't save the Contracts read at Start Mission: " + e.Message);
            }
        }

        /// <summary>A tick or swap just applied: logs whether the Contract is now on (the result, not the request) and whether Plan these would go.</summary>
        private void LogEdit(string what, string guid)
        {
            if (_contractCheck == null) return;
            var ticked = Ticked();
            var canPlan = ticked.Count > 0 && _contractCheck.SetFits && !(_form != null && TargetWins(_form));
            LogToKsp(ContractCardLog.Edited(what, guid, ticked.Contains(guid), ticked, canPlan, _contractCheck.Problem));
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
            if (_contractCheck == null || ticked.Count == 0 || !_contractCheck.SetFits)
            {
                LogToKsp("Plan these pressed and ignored: " + (_contractCheck == null ? "no card is up" : ticked.Count == 0 ? "nothing is ticked" : "the ticked set doesn't fit the craft") + ".");
                return;
            }
            // #1033: a Target Vessel Contract ticked again after Clear, or against another target picked, goes out rather than fly without its vessel.
            if (_form != null && TargetWins(_form))
            {
                LogToKsp("Plan these pressed and ignored: the start form's own target wins over a Target Vessel Contract.");
                return;
            }
            var choice = _contractCheck.Choose(ticked);
            LogToKsp("Plan these: " + string.Join(", ", choice.Guids.ToArray()) + ".");
            var start = _contractStart.Answered(choice.Goal, choice);
            _contractCheck = null;
            _folded = choice;
            Start(start);
        }

        private void PlannedWithoutContracts()
        {
            if (_contractCheck == null || _contractCheck.Words.Length == 0)
            {
                LogToKsp("Plan without Contracts pressed and ignored: " + (_contractCheck == null ? "no card is up" : "no words were typed") + ".");
                return;
            }
            LogToKsp("Plan without Contracts: the player's words alone.");
            var start = _contractStart.Answered(_contractCheck.Words, null);
            _contractCheck = null;
            Start(start);
        }

        /// <summary>#1285: the Mission hasn't had its first Approval (the player's, or any Auto Mode gives): its Contracts can still change.</summary>
        private static bool BeforeFirstApproval(Mission mission)
        {
            return !mission.MissionPlanApproved && mission.SequencesProposed == 0
                && (mission.Stage == MissionStage.WaitingForFlight || mission.Stage == MissionStage.Planning || mission.Stage == MissionStage.AwaitingPlanApproval);
        }

        /// <summary>#1285: what the folded line says once the Mission's first Mission Plan is approved.</summary>
        private const string FixedLine = "The Contracts are fixed for this Mission; start a new Mission to change them.";

        /// <summary>#1284: why "Change Contracts" is greyed while the Craft Check's request is out.</summary>
        private const string ChangeWaits = "The Craft Check's request is out; Change Contracts waits for its verdict.";

        /// <summary>
        /// #1284: "Change Contracts" on the folded card. The Contract Check is worked again from the latest Game Snapshot with the words and
        /// target given at Start Mission, ticked on the Chosen Contracts still accepted (a Contract accepted since is unticked); the folded
        /// choice is set aside. No request: the Craft Check's result stays, and Plan these uses it only while it is current for the new set's goal.
        /// </summary>
        private void ContractsChanged()
        {
            var mission = _mission;
            var onMission = !MissionOver;
            // #1285: the Contracts are fixed from the first Approval on; until then a Mission's proposed plan can still be withdrawn.
            var chosen = onMission ? (BeforeFirstApproval(mission) ? mission.Chosen : null) : _folded;
            var refusal = onMission && !BeforeFirstApproval(mission) ? "the Mission has started" : chosen == null ? "no Contracts are chosen"
                : _craftCheck.Running ? "the Craft Check's request is out; wait for its verdict"
                : _snapshot == null || _snapshot.Contracts == null || _snapshot.Contracts.Accepted.Count == 0 ? "no accepted Contracts are read now"
                : "";
            var start = _contractStart;
            ContractCheck check = null;
            // #1292: the three refusals that leave the player pressing a button that seems broken each say why on the folded line.
            var said = refusal.StartsWith("no accepted") ? "no accepted Contracts are read now." : "";
            if (refusal.Length == 0)
            {
                check = ContractCheck.Work(_snapshot, start.Goal, start.Remote, _config.CraftCheckMarginPercent, GivenTarget(start.Goal, start.Target));
                refusal = !check.Priceable ? "the craft's delta-v isn't known (turn on the stock delta-v readout), so the Contracts can't be priced"
                    : !check.WordsFit ? "the typed words alone no longer fit the craft" : "";
                said = !check.Priceable ? "Mission Control can't read this craft's delta-v (turn on the stock delta-v readout)."
                    : !check.WordsFit ? "the words you typed no longer fit this craft." : "";
            }
            if (refusal.Length > 0)
            {
                _changeRefusal = said.Length > 0 ? "Can't change Contracts: " + said : "";
                LogToKsp("Change Contracts pressed and refused: " + refusal + ".");
                return;
            }
            var accepted = new HashSet<string>(_snapshot.Contracts.Accepted.Select(c => c.Guid));
            var kept = chosen.Guids.Where(accepted.Contains).ToList();
            _changeRefusal = "";
            check.TickOnly(kept);
            var ticked = check.TickedGuids;
            var gone = chosen.Guids.Where(g => !ticked.Contains(g)).ToList();
            LogToKsp("Change Contracts: the Contract Check reopens on the Chosen Contracts still accepted [" + string.Join(", ", ticked.ToArray()) + "]"
                + (gone.Count > 0 ? "; chosen but not ticked now [" + string.Join(", ", gone.ToArray()) + "]" : "") + "; " + check.Headline(ticked.Count));
            if (onMission) WithdrawMission(mission);
            _contractCheck = check;
            _reopened = true;
            _folded = null;
        }

        /// <summary>#1285: Change Contracts before the first Approval: the Mission and its proposed (or coming) Mission Plan are withdrawn, so a plan for the old set can't be approved. Its folder stays as history, marked ended.</summary>
        private void WithdrawMission(Mission mission)
        {
            mission.Stage = MissionStage.Ended;
            try
            {
                MissionFolder.WriteEnded(mission.Folder, mission.Id, "Withdrawn");
            }
            catch (Exception e) when (IsFileError(e))
            {
                // Withdrawn in memory all the same; the folder just lacks the marker.
            }
            DropFinished(mission);
            LogToKsp("Change Contracts: Mission " + mission.Id + " withdrawn before its first Approval.");
        }

        /// <summary>A Mission starting from the card: its words, its Contract Route and the card's line in its transcript.</summary>
        private void TakeContracts(Mission mission, StartMission start)
        {
            if (start.Contracts == null || start.Contracts.Guids.Count == 0) return;
            mission.Chosen = start.Contracts;
            mission.ContractWords = start.Contracts.Words;
            mission.ContractRoute = start.Contracts.Section;
            mission.ChosenRequirements = start.Contracts.Requirements;
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
            // #1285: once the Mission is approved the Contracts are fixed: the line says so, in place of the button.
            if (!MissionOver && !BeforeFirstApproval(_mission) && (_mission.Chosen != null || _mission.ChosenRequirements.Count > 0))
                return new ContractCheckView { Folded = true, Summary = _mission.Chosen != null ? _mission.Chosen.Summary : "", FixedNote = FixedLine };
            if (!MissionOver && _mission.Chosen != null)
                return new ContractCheckView { Folded = true, Summary = _mission.Chosen.Summary, ChangeOffered = true, CanChange = !_craftCheck.Running, ChangeNote = _craftCheck.Running ? ChangeWaits : _changeRefusal };
            if (_folded != null && MissionOver)
                return new ContractCheckView { Folded = true, Summary = _folded.Summary, ChangeOffered = true, CanChange = !_craftCheck.Running, ChangeNote = _craftCheck.Running ? ChangeWaits : _changeRefusal };
            var check = _contractCheck;
            if (check == null || !MissionOver) return null;
            var ticked = Ticked();
            return new ContractCheckView
            {
                Accepted = check.Accepted, Words = check.Words,
                Rows = check.Fitting.ToList(),
                Headline = check.Headline(ticked.Count),
                TickNote = ticked.Count == 0 ? "Tick a Contract to plan it."
                    : _reopened ? "Ticked Contracts are the ones you chose. A click leaves one out, or adds it back."
                    : "Ticked Contracts are Mission Control's suggestion for this flight. A click leaves one out, or adds it back.",
                SearchNote = check.SearchNote, OrderNote = check.OrderNote,
                NotThisFlight = check.NotThisFlight.ToList(),
                CraftDeltaV = check.CraftDeltaV, RouteDeltaV = check.RouteDeltaV, RouteIsEstimate = check.RouteIsEstimate, ReservePercent = check.ReservePercent, Reserve = check.Reserve, Spare = check.Spare,
                PlanLabel = ticked.Count > 0 ? "Plan these " + ticked.Count : "Pick a Contract",
                CanPlan = ticked.Count > 0 && check.SetFits,
                Shortfall = check.Problem,
                CanPlanWithout = check.Words.Length > 0,
            };
        }
    }
}
