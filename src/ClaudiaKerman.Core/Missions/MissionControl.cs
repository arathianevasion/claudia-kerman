// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The core's one seam with the adapter, and the Mission loop behind it (ADR 0002: the mod owns
    /// execution). In: <see cref="Send"/> takes player actions, Game Snapshots, game events, kOS run events
    /// and UT. Out: <see cref="TakeCommands"/> (adapter commands), <see cref="View"/> (the UI view model) and
    /// writes to the Mission folder. Main thread only; Claude's replies arrive through <see cref="Background"/>,
    /// which the adapter drains each frame. One request per Mission is in flight at a time.
    ///
    /// No script reaches Approval or runs unless it passes the Script Check (ADR 0003, <see cref="CheckScript"/>):
    /// a fresh plan, a rewrite, a retry, a re-plan and a plan restored from a save alike.
    ///
    /// The Spending Cap pre-send check is <see cref="MaySend"/>, the Craft Check runs before a Mission starts,
    /// and persistence of the loop state across saves lives in MissionControl.Persistence.cs.
    /// </summary>
    public sealed partial class MissionControl
    {
        private readonly ClaudeClient _client;
        private readonly Background _background;
        private readonly PlayerConfig _config;
        private readonly Func<bool> _hasKey;
        private readonly IScriptCheck _scriptCheck;
        private readonly List<AdapterCommand> _commands = new List<AdapterCommand>();

        private string _saveDirectory;
        private GameScene _scene = GameScene.Other;
        private double _ut;
        private GameSnapshot _snapshot;
        private Mission _mission;
        private int _lastRunId;
        private MissionView _view = new MissionView();
        private readonly CraftCheckFlow _craftCheck;
        /// <summary>Start Mission was pressed and the Craft Check it triggered is running; a Pass lets the Mission start.</summary>
        private StartMission _pendingStart;
        /// <summary>Why the last Start Mission was turned away before the Craft Check; "" when it wasn't.</summary>
        private string _startRefusal = "";

        public MissionControl(ClaudeClient client, Background background, PlayerConfig config, Func<bool> hasKey, IScriptCheck scriptCheck)
        {
            _client = client;
            _background = background;
            _config = config;
            _hasKey = hasKey;
            _scriptCheck = scriptCheck;
            _craftCheck = new CraftCheckFlow(client, background, config, hasKey, CraftCheckCompleted);
            // No check_script on Mission turns (#570): the Script Check runs on every Sequence Plan reply anyway.
            Tools = PlanningTools.Default();
            Rebuild();
        }

        /// <summary>The view model as of the last input or response. Replaced, never mutated, so it is safe to hold for a frame.</summary>
        public MissionView View
        {
            get { return _view; }
        }

        /// <summary>The adapter commands issued since the last call, oldest first.</summary>
        public IList<AdapterCommand> TakeCommands()
        {
            var taken = _commands.ToList();
            _commands.Clear();
            return taken;
        }

        public void Send(MissionInput input)
        {
            if (HeldForLink(input)) { }
            else if (input is GameSnapshot) Snapshot((GameSnapshot)input);
            else if (input is SaveLoaded) Loaded(((SaveLoaded)input).SaveDirectory);
            else if (input is MissionsRestored) Restored((MissionsRestored)input);
            else if (input is SceneChanged) SceneChanging(((SceneChanged)input).Scene);
            else if (input is UtAdvanced) { _ut = ((UtAdvanced)input).Ut; WarpSeen(((UtAdvanced)input).Warping); ContinueRunEnd(); }
            else if (input is StartMission) Start((StartMission)input);
            else if (input is RunCraftCheck) RunCraftCheck((RunCraftCheck)input);
            else if (input is StartFormChanged) _form = (StartFormChanged)input;
            else if (input is AcknowledgeWarning) _craftCheck.Acknowledge(((AcknowledgeWarning)input).Code);
            else if (input is ChooseProcessor) _craftCheck.Choose(((ChooseProcessor)input).PartId, _snapshot);
            else if (input is EndMission) EndRequested();
            else if (input is DismissMission) DismissRequested();
            else if (input is Approve) Approved();
            else if (input is ConfirmApproval) Confirmed(((ConfirmApproval)input).Yes);
            else if (input is Reply) Replied(((Reply)input).Text);
            else if (input is ChangeSomething) ChangeRequested(((ChangeSomething)input).Text);
            else if (input is AskQuestion) Asked(((AskQuestion)input).Text);
            else if (input is Retry || input is Resume) Retried();
            else if (input is RaiseCap) Raised(((RaiseCap)input).SpendingCap);
            else if (input is RetryWithNote) { if (!RefusalRetriedWithNote(((RetryWithNote)input).Note) && !ReducedGoalAsked(((RetryWithNote)input).Note)) ScriptRetried(((RetryWithNote)input).Note ?? "", false); }
            else if (input is ChooseWindow) WindowChosen(((ChooseWindow)input).Kind);
            else if (input is ChooseModel) ModelChosen(((ChooseModel)input).Model);
            else if (input is Replan) Replanned();
            else if (input is TakeManualControl) TakeManual("the Manual Control button");
            else if (input is ManualInputHeld) InputHeld(((ManualInputHeld)input).Seconds);
            else if (input is HandBack) HandedBack(((HandBack)input).Message);
            else if (input is CaptureSnapshot) Capture();
            else if (input is KosRunRefused) RunRefused((KosRunRefused)input);
            else if (input is KosRunEnded) RunEnded((KosRunEnded)input);
            else if (input is EditSettings) SettingsEdited(((EditSettings)input).Settings);
            else if (input is WarpToDueTime) WarpRequested();
            else if (input is GoToVessel) GoToRequested();
            else if (input is ClaudiaKilled) Killed(((ClaudiaKilled)input).VesselId, ((ClaudiaKilled)input).Traced);
            else if (input is KosRunBlocked) RunBlocked((KosRunBlocked)input);
            else if (input is ArrivalPlaneAimed) ArrivalPlaneRecorded((ArrivalPlaneAimed)input);
            else if (input is KosBlockStarted) BlockStarted((KosBlockStarted)input);
            else if (input is KosBlockEnded) BlockEnded((KosBlockEnded)input);
            else if (input is KosWarpRequested) WarpRequestSeen((KosWarpRequested)input);
            else if (input is KosRcsSwitched) RcsSwitched((KosRcsSwitched)input);
            else if (input is ShipSystemsMoved) ShipSystemsLine((ShipSystemsMoved)input);
            else if (input is EncounterWatchDropped) EncounterWatchLine((EncounterWatchDropped)input);
            else if (input is NextPatchDropped) NextPatchDroppedSeen((NextPatchDropped)input);
            else if (input is ChuteWarning) ChuteWarningLine((ChuteWarning)input);
            else if (input is CoastArmed) CoastArmedSeen((CoastArmed)input);
            else if (input is AbortSequence) AbortPressed();
            else if (input is VesselsSeen) VesselsChanged(((VesselsSeen)input).Vessels);
            else if (input is VesselsDocked) Docked((VesselsDocked)input);
            else if (input is VesselsSeparated) Separated((VesselsSeparated)input);
            else if (input is CraftRange) RangeSeen((CraftRange)input);
            else if (input is CraftSafingUnreached) SafingUnreached((CraftSafingUnreached)input);
            else if (input is VesselUnloaded) Unloaded(((VesselUnloaded)input).VesselId);
            else if (input is FarSwitchConfirmed) FarSwitched(((FarSwitchConfirmed)input).VesselName);
            else if (input is LeaveFlightConfirmed) LeavingFlight(((LeaveFlightConfirmed)input).Destination);
            else if (input is RunEventSeen) EventSeen((RunEventSeen)input);
            else if (input is CraftSeparated) Separated(((CraftSeparated)input).Throttle);
            else if (input is AcceptReducedGoal) ReducedGoalAccepted();
            else if (input is StockAlarmFired) StockAlarmWentOff();
            else if (input is ResumeMission) ResumeRequested(((ResumeMission)input).Id);
            else if (input is PauseMission) PauseRequested();
            else if (input is DeleteMission) DeleteRequested(((DeleteMission)input).Id);
            else if (input is GoToMissionVessel) GoToMissionVesselRequested(((GoToMissionVessel)input).Id);
            else if (input is CraftLinkSeen) CraftLinkReported((CraftLinkSeen)input);
            else if (input is SetAutoMode) AutoModeSet(((SetAutoMode)input).On);
            else if (input is ChangeTarget) TargetChanged(((ChangeTarget)input).Pick);
            CheckDue();
            CheckBlackout();
            CheckScriptWarp();
            CheckRunaway();
            CheckTelemetry();
            CheckFlyable();
            CheckCoastReturn();
            TryPlan();
            TryTargetRevision();
            TryOwed();
            Rebuild();
        }

        // ---- The Mission and its turns ----

        private enum Purpose
        {
            MissionPlan,
            SequencePlan,
            Debrief,
            Reply,
            Change,
            Question,
            /// <summary>Mission Settings were edited: a revised Mission Plan is asked for.</summary>
            Settings,
            /// <summary>A Telemetry Check was crossed while Running: Claudia judges it (MissionControl.Live.cs).</summary>
            Alert,
            /// <summary>The margin recheck of a Debrief (MissionControl.Debrief.cs); only ever the stand-in for its charge.</summary>
            Margin,
        }

        /// <summary>
        /// One request's worth of appended messages: an effort change when needed, a user message (facts or
        /// the player's words) and a mod notice. Built when it is first sent and kept unchanged for Retry;
        /// appended to the conversation, with the reply, only once a usable reply arrives.
        /// </summary>
        private sealed class Turn
        {
            public Purpose Purpose;
            public string Effort;
            public string UserText;
            public string Notice;
            /// <summary>The script's report, kept so the Debrief's run delta-v line can say when its dv_used differs.</summary>
            public IDictionary<string, object> Report;
            /// <summary>The player's words of a question asked with a plan pending, which may turn out to be a revision (MissionControl.TypedReply.cs); null otherwise.</summary>
            public string PlayerText;
            /// <summary>A rewrite after a failed Script Check: if this script fails too, there is no further automatic rewrite.</summary>
            public bool ScriptRewrite;
            /// <summary>A correction after a Mission Plan named a vessel that never left the craft: if it does so again, the Mission Pauses.</summary>
            public bool ChildVesselRetry;
            /// <summary>
            /// A revision the mod asked for while the Mission Goal stands (after an Off-Nominal Debrief or a Hand back's Debrief): a plan
            /// with no sequences is refused (#697). Set where the turn begins; false on the first Mission Plan, a typed one, and the
            /// revisions that may decline (a missed window, a changed Target Vessel whose ports no longer fit).
            /// </summary>
            public bool GoalStands;
            /// <summary>The Debrief asked again, with the player's note, after a Failed Debrief offered no reduced goal: a load while it is out sends it again, not an Interrupted Debrief.</summary>
            public bool ReducedGoalRetry;
            /// <summary>
            /// Sent while a run is live or near one (a hand-back Debrief and its revision, a late-Approval re-plan): at the
            /// Spending Cap it waits for Raise cap instead of Pausing the Mission (<see cref="WaitForCap"/>). A turn sent
            /// while Running always waits.
            /// </summary>
            public bool Waits;
            /// <summary>
            /// The turn is in its reduced form after a cut-off reply (<see cref="ResendFromScratch"/>): medium effort and
            /// the cut-off note. At most one automatic retry per turn, and any Retry sends this form, never the identical request.
            /// </summary>
            public bool CutOffRetry;
            /// <summary>The turn was resent once with <see cref="MissionPrompts.PlanCallNote"/> after a mission_plan reply with no accepted plan (#667); not saved.</summary>
            public bool PlanCallRetry;
            /// <summary>A Debrief turn was resent once with <see cref="EmptyDebrief.Note"/> after a reply with no debrief (#748); not saved.</summary>
            public bool DebriefRetry;
            /// <summary>The reply to the final request at the step limit was refused off_schema (#668): <see cref="Pause"/> keeps the tool loop and Retry sends the final request again. Memory only.</summary>
            public bool KeepLoop;
            /// <summary>
            /// The turn is in its Second look (<see cref="SecondLook"/>): high effort and the first reply's reason for doubt added, which
            /// is never "" once set. At most one per turn, and never with a <see cref="CutOffRetry"/>, in either order.
            /// </summary>
            public string SecondLookReason = "";
            /// <summary>An Alert's crossed Telemetry Check, which its verdict is recorded on; null for other turns.</summary>
            public Crossing Crossing;
            /// <summary>A Debrief's extras from the mod: its events, the rest of the plan and the margin recheck (MissionControl.Debrief.cs); null before it begins.</summary>
            public DebriefGate Gate;
            /// <summary>The tool loop's steps so far (MissionControl.Tools.cs): each added Claude's tool_use reply and its results to <see cref="Messages"/>.</summary>
            public int ToolSteps;
            /// <summary>What the turn has used over all its requests, for the per-plan log (MissionControl.PlanLog.cs); a rewrite after a failed Script Check carries its turn's on.</summary>
            public TurnStats Stats = new TurnStats();
            /// <summary>The step limit is reached and the turn's request goes with tool_choice none (#509): a reply that still calls tools fails the turn.</summary>
            public bool ToolLimitSent;
            /// <summary>A propose_mission_plan call was refused on a turn that doesn't ask for a Mission Plan (#607): the turn's later requests go with tool_choice none.</summary>
            public bool PlanCallRefused;
            /// <summary>The last tool step was only refused repeats (#571): the next request is the last, as at the step limit.</summary>
            public bool RepeatStop;
            /// <summary>The last tool step was only refused repeats on a turn still owed a Mission Plan (#571): the next step may call propose_mission_plan alone.</summary>
            public bool PlanOnly;
            /// <summary>The Game Snapshot taken for the turn when its messages were built: what its tools calculate from.</summary>
            public GameSnapshot ToolSnapshot;
            /// <summary>
            /// The compaction block in force when the turn's messages were built (null for none) and how many messages it stood in
            /// for: every request of the turn goes out behind it (MissionControl.Compaction.cs).
            /// </summary>
            public CompactionBlock CompactionBase;
            public int CompactedBase;
            public List<Message> Messages;
            public CancellationTokenSource Cancel;
        }

        private sealed partial class Mission
        {
            public string Id;
            public string Goal;
            public decimal SpendingCap;
            public string Model;
            public string Folder;
            public readonly List<string> System = new List<string>();
            public readonly List<Message> Conversation = new List<Message>();
            /// <summary>The effort the committed conversation last set; null before the first turn.</summary>
            public string Effort;
            public MissionStage Stage;
            public MissionStage StageBeforePause;
            /// <summary>The processor the running script was started on.</summary>
            public uint? RunningProcessor;
            /// <summary>The id of the current run; run events carrying any other id are late and ignored.</summary>
            public int? RunId;
            /// <summary>The runaway hard stop's UT for the current run (MissionControl.Gates.cs); null when nothing runs.</summary>
            public double? RunDeadlineUt;
            /// <summary>The target UT of the current run's latest warp request (#830); the runaway stop holds while it, plus <see cref="RunawayStop.WarpAllowanceSeconds"/>, is ahead.</summary>
            public double? WarpTargetUt;
            /// <summary>The UT warp was last seen running during the current run (#834); null if never. Not persisted: a resumed run starts fresh.</summary>
            public double? LastWarpingUt;
            /// <summary>Whether warp was running at the latest UtAdvanced (#834).</summary>
            public bool Warping;
            /// <summary>Why the mod stopped the current run ("blocked: …", "timed out"), for its Debrief; null otherwise.</summary>
            public string StopReason;
            public MissionPlan Plan;
            /// <summary>The Chute Groups of the Entries left (ADR 0019): worked out at Mission Plan Approval, each Debrief and each Margin Check; null before (or with no Game Snapshot then).</summary>
            public ChuteGroups Chutes;
            /// <summary>The Mission Goal's steps (<see cref="GoalSteps"/>, #581): set by a Mission Plan, ticked by Debriefs, rebuilt from the transcript on load.</summary>
            public List<GoalItem> GoalSteps = new List<GoalItem>();
            public SequencePlan PendingSequence;
            public int SequencesProposed;
            /// <summary>The Maneuver Sequence in hand has had a Sequence Plan offered (so revisions don't count again).</summary>
            public bool SequenceCounted;
            /// <summary>The Mission Plan step the Sequence Plan in hand named, 1-based (#413); null when it named none, so the count stands in.</summary>
            public int? PlanStep;
            /// <summary>The <see cref="PlanStep"/> from before the Sequence Plan in hand was counted: what a plan dropped unflown leaves.</summary>
            public int? PlanStepBefore;
            /// <summary>What the Script Check last found, while "Couldn't produce a safe script" is shown.</summary>
            public IList<string> UnsafeProblems;
            public readonly List<LogEntry> Log = new List<LogEntry>();
            public string LatestLine = "";
            public string Activity = "";
            public string PauseReason = "";
            public string ConfirmPrompt;
            /// <summary>In flight, or failed and waiting for Retry, or the waiting call; the turns after it are in <c>Queue</c> (MissionControl.Live.cs).</summary>
            public Turn Pending;
            public bool InFlight;
            public IRawStreamDump Dump;
            /// <summary>The Mission Settings, by name; persisted with the Mission; EditSettings changes them (MissionSettingsValues reads and writes them).</summary>
            public readonly Dictionary<string, string> Settings = new Dictionary<string, string>();
            /// <summary>The Mission's vessel (its id and name), noted when the craft is first in Flight; the Go to button switches to it.</summary>
            public string VesselId = "";
            public string VesselName = "";
            /// <summary>The Target Vessel (ADR 0013), chosen when the Mission starts; null for none.</summary>
            public TargetVessel Target;
            /// <summary>The Due Time of the Sequence Plan awaiting Approval has been reached and announced (warp dropped, message posted).</summary>
            public bool DueNotified;
            /// <summary>The Transfer Window the pending Sequence Plan's Due Time waits for (#616): its planet, departure UT and total delta-v; "" and null for none.</summary>
            public string WindowPlanet = "";
            public double? WindowUt;
            public double? WindowDeltaV;
            /// <summary>Persistence (MissionControl.Persistence.cs): a turn the loop owes Claudia once the craft is in Flight.</summary>
            public Owed Owed;
            public RunOutcome OwedOutcome;
            /// <summary>Said to Claudia with the next turn (a save was loaded, or the Mission was resumed without a snapshot).</summary>
            public string LoadNotice;
            /// <summary>A save was loaded during a request: it completes, then this state is restored.</summary>
            public Restore DeferredRestore;
            /// <summary>No snapshot in the loaded save: Paused until the player resumes it or ends it.</summary>
            public bool AwaitingResume;
            /// <summary>The cost log couldn't be read at load: Paused, with the restore to carry out once it can be.</summary>
            public Restore UnreadableCostLogRestore;
            public bool UnreadableCostLogResumeAfterQuit;
            /// <summary>Flown remotely: Claudia stays at Mission Control. False is crewed (she flies aboard).</summary>
            public bool Remote;
            /// <summary>While Paused for EVA science: what the player is to do by hand; "" otherwise.</summary>
            public string EvaScience = "";

            // ---- Spending (state #42 snapshots: SpendingCap, Spent, Held) ----
            /// <summary>Cost so far from real usage.</summary>
            public decimal Spent;
            /// <summary>Input costs kept for cancelled and lost requests (a request that got no reply at all can't have billed output; one that returned anything is paid at its usage instead).</summary>
            public decimal Held;
            /// <summary>The worst case of the request in flight; replaced by its real usage.</summary>
            public decimal PendingCharge;
            /// <summary>The input part of <see cref="PendingCharge"/>: all the request holds if it is cancelled or lost with no reply at all.</summary>
            public decimal PendingInput;
            public bool Warned;
            /// <summary>Paused because of the Spending Cap, so Raise cap resumes it.</summary>
            public bool PausedForCap;
            /// <summary>The waiting call: the pending turn would pass the Spending Cap, so it waits unsent for Raise cap while the Mission carries on.</summary>
            public bool WaitingForCap;
            /// <summary>What the waiting call's screen message said, for the view; "" when nothing waits.</summary>
            public string CapWaitText = "";
            public int Requests;
            /// <summary>How the last request ended, for the cost log.</summary>
            public string Settlement = "";
            public string Outcome = "";
            public decimal Settled;

            /// <summary>What counts against the Spending Cap: with a compaction in flight, its worst case too (MissionControl.Compaction.cs).</summary>
            public decimal Committed
            {
                get { return Spent + Held + CompactionCharge; }
            }

            /// <summary>Claudia answered the Mission Plan with no sequences: the goal can't be flown, and there is nothing to approve.</summary>
            public bool Declined
            {
                get { return Stage == MissionStage.AwaitingPlanApproval && Plan != null && Plan.Sequences.Count == 0; }
            }

            public bool AwaitingApproval
            {
                get { return Stage == MissionStage.AwaitingPlanApproval || Stage == MissionStage.AwaitingSequenceApproval; }
            }
        }

        private bool MissionOver
        {
            get { return _mission == null || _mission.Stage == MissionStage.Succeeded || _mission.Stage == MissionStage.Ended || _mission.Stage == MissionStage.Lost; }
        }

        private void Start(StartMission start)
        {
            var goal = (start.Goal ?? "").Trim();
            if (goal.Length == 0 || start.SpendingCap <= 0 || !_config.Models.Contains(start.Model) || _saveDirectory == null) return;
            // Another Mission in play pauses at its next sequence boundary first, before the new craft is checked (MissionControl.Missions.cs).
            if (!MissionOver && !MakeRoom(new QueuedSwitch { Kind = SwitchKind.Start, Start = start })) return;

            // A remote Mission needs a link (or the pad); a crewed one can't have Claudia while she is Missing. No request is spent on either.
            _startRefusal = StartRefusal(start);
            if (_startRefusal.Length > 0)
            {
                _pendingStart = null;
                NeedsPlayer(_startRefusal);
                return;
            }

            // The player picks the Transfer Window of a transfer between bodies with the same parent (#686); no request is spent without one.
            var windows = OfferFor(goal, _snapshot);
            var window = WindowChoice.Held(start.Window, windows);
            if (windows.Needed && window.IsNone)
            {
                _pendingStart = null;
                _startRefusal = WindowChoice.PickFirst;
                NeedsPlayer(_startRefusal);
                return;
            }

            // No Mission starts on a craft that hasn't been checked for this goal, Target Vessel and window, or whose check has a Blocker or an open Warning.
            var target = ChooseTarget(goal, start.Target);
            if (!_craftCheck.IsCurrent(goal, _snapshot, start.Remote, target.Target, window.Key))
            {
                _pendingStart = start;
                _craftCheck.Run(goal, start.Model, _snapshot, start.SpendingCap, start.Remote, target.Target, windows.RowFor(window));
                if (!_craftCheck.Running) _pendingStart = null;
                return;
            }
            if (!_craftCheck.Cleared) return;
            _pendingStart = null;

            var id = NewMissionId();
            var mission = new Mission
            {
                Id = id,
                Goal = goal,
                SpendingCap = start.SpendingCap,
                Model = start.Model,
                Folder = MissionFolder.For(_saveDirectory, id),
                Stage = MissionStage.WaitingForFlight,
                Remote = start.Remote,
                AutoMode = _config.DefaultAutoMode,
                WindowPick = window,
            };
            // Frozen for the whole Mission, so the cached prefix never changes.
            mission.System.Add(MissionPrompts.BaseLayer);
            var bodyTable = MissionPrompts.BodyTable(_snapshot != null ? _snapshot.Bodies : null);
            mission.PlannedTurnShape = _craftCheck.PlannedTurnShape;
            mission.PlannedAscent = _craftCheck.PlannedAscent;
            mission.System.Add(MissionPrompts.MissionLayer(goal, bodyTable, mission.PlannedTurnShape));
            var settings = start.Settings != null ? start.Settings.Copy() : MissionSettingsValues.Defaults(_config);
            settings.ConfigAllowsPhysicsWarp = _config.PhysicsWarp;
            settings.WriteTo(mission.Settings);
            mission.System.Add(MissionPrompts.TimingLayer(id, settings));
            // Raw stream dumps are a debug tool: on with debug_tools = true in the player config.
            if (_config.DebugTools) mission.Dump = new FileStreamDump(Path.Combine(mission.Folder, "stream-dumps"));
            _mission = mission;
            // The form's goal is spent: no offer is worked out for it any more.
            _form = null;
            SaveSetupAsDefaults(start);
            Note(LogKind.Player, "Mission Goal: " + goal);
            TakeTarget(mission, target);
            if (!window.IsNone) Note(LogKind.Player, "Transfer Window to " + window.Body + ": " + window.Kind + ".");
            RecordStart(mission);
            LogEffortProblems(mission);
            try
            {
                MissionStore.WriteBodyTable(mission.Folder, bodyTable);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission's body table: " + e.Message);
            }
            try
            {
                MissionStore.WriteAscentPlan(mission.Folder, _craftCheck.PlannedAscentInput, _craftCheck.PlannedAscentSnapshot);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission's ascent plan snapshot: " + e.Message);
            }
            try
            {
                _craftCheck.CopyDumpTo(mission.Folder);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't copy the Craft Check's request and reply: " + e.Message);
            }
            FoldCraftCheckCost(mission, _craftCheck.TakeCost());
            // Started from the editor with her not aboard: the mod puts her in a free seat (the Craft Check made sure there is one).
            if (!mission.Remote && _scene == GameScene.Editor && _snapshot != null && !_snapshot.ClaudiaAboard && _snapshot.FreeSeats > 0)
            {
                _commands.Add(new SeatClaudia());
                Note(LogKind.Notice, "Claudia is taking a seat aboard.");
            }
        }

        /// <summary>
        /// A started Mission's setup becomes the player's defaults, the set Settings' Apply saves (#639): its model moves to
        /// the front of the models, and its Spending Cap, Mission type and Mission Settings are the next form's. Auto Mode
        /// isn't in the setup, so it keeps its default. A setup that never starts never gets here, so it saves nothing.
        /// </summary>
        private void SaveSetupAsDefaults(StartMission start)
        {
            _config.SetDefaultModel(start.Model);
            _config.DefaultSpendingCap = start.SpendingCap;
            _config.DefaultRemote = start.Remote;
            if (start.Settings != null)
            {
                _config.DefaultDaylightLanding = start.Settings.DaylightLanding;
                _config.DefaultTimeVersusDeltaV = start.Settings.TimeVersusDeltaV;
            }
            _commands.Add(new SaveConfigDefaults());
        }

        /// <summary>Why this Start Mission can't go ahead before any Craft Check, or "".</summary>
        private string StartRefusal(StartMission start)
        {
            var snapshot = _snapshot;
            if (snapshot == null) return "";
            if (start.Remote)
            {
                // The launch pad counts as a link: Claudia can start the craft from there.
                if (!snapshot.Linked && snapshot.Situation != "PRELAUNCH")
                    return "A remote Mission needs a CommNet link to Kerbin, or the craft on the launch pad. Restore the link, then start it.";
                return "";
            }
            if (snapshot.ClaudiaStanding == "Missing" || snapshot.ClaudiaStanding == "Dead")
                return "Claudia is " + snapshot.ClaudiaStanding + " and can't fly aboard. A remote Mission can still start; a crewed one waits until she returns.";
            // Seating happens only in the editor: in Flight she has to be aboard the active vessel already (not Assigned elsewhere).
            if (_scene != GameScene.Editor && !snapshot.ClaudiaAboard)
                return CraftCheckFlow.NoSeatReason + " Seating happens in the editor; a crewed Mission started in Flight needs her already aboard this vessel. A remote Mission can still start.";
            return "";
        }

        private void RunCraftCheck(RunCraftCheck run)
        {
            _pendingStart = null;
            _startRefusal = "";
            var windows = OfferFor((run.Goal ?? "").Trim(), _snapshot);
            _craftCheck.Run(run.Goal, run.Model, _snapshot, run.SpendingCap, run.Remote, ChooseTarget(run.Goal, run.Target).Target, windows.RowFor(run.Window));
        }

        /// <summary>A Craft Check request ended: if Start Mission asked for it and it passed outright, the Mission starts now.</summary>
        private void CraftCheckCompleted()
        {
            var start = _pendingStart;
            _pendingStart = null;
            if (start != null && _craftCheck.Passed) Start(start);
            TryPlan();
            Rebuild();
        }

        private void TryPlan()
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.WaitingForFlight) return;
            if (_scene != GameScene.Flight || _snapshot == null || TargetUnread(mission, _snapshot)) return;
            NoteCraft(mission, _snapshot);
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.MissionPlan,
                UserText = "Mission Goal, in the player's words (quoted data): " + MissionPrompts.Quoted(mission.Goal) + "\n"
                    + MissionPrompts.MissionMode(mission.Remote)
                    + MissionPrompts.ModeFacts(mission.Remote, _snapshot) + "\n"
                    + "Spending Cap: $" + mission.SpendingCap.ToString("0.00", CultureInfo.InvariantCulture) + ".\n"
                    + MissionPrompts.SettingsText(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config))) + "\n"
                    + MissionPrompts.Facts(_ut, _snapshot) + ChildVesselFacts(mission)
                    + PlanFigures(mission, mission.Goal),
                Notice = MissionPrompts.PlanNotice,
            });
        }

        /// <summary>What every request for a Mission Plan carries from its own Game Snapshot (#508, #513): the stage table, and the pad wording while the craft is on the pad.</summary>
        private string PlanFigures(Mission mission, string goal)
        {
            if (_snapshot != null)
            {
                // A planet's plan_transfer row picks its Transfer Window by the player's pick or the Mission Setting, as a tool call would (#458, #686).
                _snapshot.StampMission(MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config)).TimeVersusDeltaV, _config.CraftCheckMarginPercent, mission.WindowPick);
                _snapshot.MissionGoal = goal;
            }
            return MissionPrompts.StageTable(_snapshot, goal, mission.PlannedAscent) + MissionPrompts.DeltaVMapLine(_snapshot, goal) + MissionPrompts.PadPlanning(_snapshot);
        }

        /// <summary>
        /// A Mission belongs to its save. Until Missions persist across saves (a later ticket), loading a
        /// different save drops the Mission in memory and cancels its request; its folder stays in the old save.
        /// </summary>
        private void Loaded(string saveDirectory)
        {
            if (saveDirectory == _saveDirectory) return;
            _saveDirectory = saveDirectory;
            _craftCheck.SaveDirectory = saveDirectory;
            _snapshot = null;
            _active = null;
            _craftLink = null;
            _seen = new List<SeenVessel>();
            _pendingStart = null;
            _startRefusal = "";
            _craftCheck.Reset();
            _parked.Clear();
            _finishedCache.Clear();
            _switch = null;
            _historyVersion++;
            if (_mission == null) return;
            if (_mission.InFlight) _mission.Pending.Cancel.Cancel();
            CancelCompaction(_mission);
            _mission = null;
        }

        private void Snapshot(GameSnapshot snapshot)
        {
            if (snapshot.Ut > _ut) _ut = snapshot.Ut;
            // Another vessel than the craft: the Mission keeps the craft's last facts (MissionControl.Vessels.cs).
            FitTarget(snapshot);
            if (!TakeSnapshot(snapshot)) return;
            var previous = _snapshot;
            _snapshot = snapshot;
            _craftLink = snapshot.Linked;
            _craftCheck.OnSnapshot(snapshot);
            LinkSeen(previous, snapshot);
            var mission = _mission;
            if (mission == null) return;
            ContinueRunEnd(mission);
            // A plan restored from a save was never offered against a snapshot: the first one after the load is its baseline.
            if (mission.AwaitingApproval && mission.PlanBaseline == null && !mission.InFlight && _scene == GameScene.Flight) mission.PlanBaseline = snapshot;
            // An approved plan restored from a save has no Chute Groups yet: the first snapshot of the craft after the load gives it one.
            if (mission.Chutes == null && mission.Plan != null && mission.Stage != MissionStage.AwaitingPlanApproval && _scene == GameScene.Flight) GroupChutes(mission);
            CarryOnWhenSceneAllows();
        }

        // ---- Approval ----

        private bool CanAnswerPlan
        {
            get { return _mission != null && _mission.AwaitingApproval && !_mission.InFlight; }
        }

        private void Approved()
        {
            if (!CanAnswerPlan || _mission.Declined) return;
            _mission.ConfirmPrompt = null;
            Approval();
        }

        private void Confirmed(bool yes)
        {
            if (!CanAnswerPlan || _mission.Declined || _mission.ConfirmPrompt == null) return;
            _mission.ConfirmPrompt = null;
            if (yes) Approval();
            else Note(LogKind.Notice, "Not approved yet.");
        }

        /// <summary>An Approval: the Approve button, or "Yes, approve" after a typed reply.</summary>
        private void Approval()
        {
            var mission = _mission;
            // Approve is Re-plan once the craft has changed beyond the plan's tolerances: a stale plan never runs.
            var stale = StaleBecause(mission);
            if (stale.Length > 0)
            {
                Note(LogKind.Notice, (mission.TargetChange.Length > 0 ? "The Target Vessel" : "The craft") + " has changed since this plan was made (" + stale + "). Re-plan instead.");
                return;
            }
            if (WindowPickMissing(mission))
            {
                Note(LogKind.Notice, WindowChoice.PickFirst);
                return;
            }
            if (mission.Stage == MissionStage.AwaitingPlanApproval)
            {
                Notice("Mission Plan approved.");
                mission.MissionPlanApproved = true;
                var newGoal = ApplyGoalChange(mission);
                mission.Stage = MissionStage.Sequencing;
                GroupChutes(mission);
                mission.SequenceCounted = false;
                Begin(new Turn
                {
                    Purpose = Purpose.SequencePlan,
                    UserText = "The player approved the Mission Plan.\n"
                        + (newGoal != null ? "The Mission Goal is now (quoted data): " + MissionPrompts.Quoted(newGoal) + "\n" : "")
                        + WindowPickLine(mission)
                        + MissionPrompts.Facts(_ut, _snapshot),
                    Notice = MissionPrompts.SequenceNotice,
                });
                return;
            }

            var plan = mission.PendingSequence;
            // Back after the Transfer Window: the next one, or an Alert (#616).
            if (!plan.Manual && WindowWasMissed(mission) && !NextWindow(mission, plan)) return;
            // The mod never starts a sequence before its Due Time, and never warps there for the player (but for a coast checkpoint's, which is soft: #775).
            if (plan.DueTimeUt.HasValue && _ut < plan.DueTimeUt.Value && !SoftDueTime(mission, plan))
            {
                Note(LogKind.Notice, "Not before the Due Time, " + KerbinTime.Date(plan.DueTimeUt.Value) + " (in "
                    + KerbinTime.Span(plan.DueTimeUt.Value - _ut) + "). Warp to Due Time, then Approve.");
                return;
            }
            if (WindowMissed(plan) && !TakeNextWindow(mission, plan)) return;
            if (plan.Manual)
            {
                // The player flies a manual stretch in whatever vessel they are in: no Child Vessel run.
                mission.RunningChild = "";
                StartPlannedManual(mission, plan);
                return;
            }
            // The craft's processor, or a Child Vessel's over a CommNet path from Claudia (MissionControl.ChildVessels.cs).
            var processor = SequenceProcessor(mission, plan);
            if (!processor.HasValue) return;
            // The plan was checked before it was offered; the bundle that starts is checked again, whatever path
            // (a reply, a restored save, a refused start) brought the plan here. No automatic rewrite: the plan
            // was offered already, so the player decides what happens next.
            var bundle = Bundle(plan);
            var check = _scriptCheck.Check(bundle);
            if (!check.Passed)
            {
                ShowUnsafeScript(mission, plan, check.Problems);
                return;
            }
            Notice("Sequence Plan approved: the mod is starting the script.");
            mission.Stage = MissionStage.Running;
            mission.CoastBlocked = CoastBlock(mission, plan);
            if (mission.CoastBlocked.Length > 0) Note(LogKind.Notice, CoastCheckpoints.BlockerLine(mission.CoastBlocked));
            if (plan.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            ClearWindow(mission);
            mission.RunningProcessor = processor.Value;
            mission.RunId = ++_lastRunId;
            mission.NoRunSinceSnapshot = false;
            // A new run means the craft was reachable; an owed make-safe must not cut it off later.
            mission.SafePending = "";
            StartRunawayTimer(mission, plan);
            RunStarting(mission);
            StartLive(mission, plan);
            StartedOn(mission, plan);
            _commands.Add(new StartScript(processor.Value, "claudia:" + mission.Id + ":" + plan.Name, bundle, mission.RunId.Value));
            CompactAtRunStart(mission, plan);
        }

        private void Replied(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !CanAnswerPlan) return;
            _mission.ConfirmPrompt = null;
            Note(LogKind.Player, text);
            Begin(new Turn
            {
                Purpose = Purpose.Reply,
                UserText = MissionPrompts.PlayerReplied(text),
                Notice = MissionPrompts.ReplyNotice(_mission.Stage == MissionStage.AwaitingPlanApproval, _mission.Declined),
                PlayerText = text,
            });
        }

        private void ChangeRequested(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !CanAnswerPlan) return;
            _mission.ConfirmPrompt = null;
            Note(LogKind.Player, "Change: " + text);
            Begin(new Turn
            {
                Purpose = Purpose.Change,
                UserText = MissionPrompts.PlayerWantsChange(text),
                Notice = MissionPrompts.ChangeNotice(_mission.Stage == MissionStage.AwaitingPlanApproval),
            });
        }

        private void Asked(string text)
        {
            text = (text ?? "").Trim();
            var mission = _mission;
            if (text.Length == 0 || mission == null || mission.Stage == MissionStage.Paused
                || mission.Stage == MissionStage.WaitingForFlight || mission.Stage == MissionStage.Lost) return;
            // While Running a question queues behind the request in hand; otherwise one at a time.
            var running = mission.Stage == MissionStage.Running;
            if (mission.InFlight && !running) return;
            Note(LogKind.Player, text);
            Begin(new Turn
            {
                Purpose = Purpose.Question,
                // While Running it carries a compact craft snapshot; it never touches the run.
                UserText = MissionPrompts.PlayerAsks(text) + (running ? "\n" + CompactFacts(mission) : ""),
                Notice = mission.AwaitingApproval ? MissionPrompts.QuestionWithPlanPendingNotice + (mission.Declined ? MissionPrompts.DeclinedPlanLine : "") : MissionPrompts.QuestionNotice,
                PlayerText = mission.AwaitingApproval ? text : null,
                Waits = running,
            });
        }

        // ---- Timing: Due Times, late Approval and Mission Settings ----

        /// <summary>The Due Time of the Sequence Plan awaiting Approval, or null.</summary>
        private double? PendingDueTime
        {
            get
            {
                var mission = _mission;
                return mission != null && mission.Stage == MissionStage.AwaitingSequenceApproval && mission.PendingSequence != null
                    ? mission.PendingSequence.DueTimeUt : null;
            }
        }

        /// <summary>The plan's window has closed: it is later than the plan expected.</summary>
        private bool WindowMissed(SequencePlan plan)
        {
            return plan.WindowClosesUt.HasValue && _ut > plan.WindowClosesUt.Value;
        }

        /// <summary>
        /// Late Approval: the planned window has closed. Within tolerance, the script takes the next window and the
        /// player gets a one-line notice (true: carry on starting); otherwise Claudia is asked to re-plan (false).
        /// </summary>
        private bool TakeNextWindow(Mission mission, SequencePlan plan)
        {
            var next = plan.LateWindowDeltaV;
            var limit = plan.ExpectedDeltaV * (1 + _config.LateApprovalToleranceDvPercent / 100.0);
            if (next.HasValue && next.Value <= limit)
            {
                Note(LogKind.Notice, "Late Approval: the planned window closed at " + KerbinTime.Date(plan.WindowClosesUt.Value)
                    + "; the script takes the next window (" + MetresPerSecond(next.Value) + " against " + MetresPerSecond(plan.ExpectedDeltaV) + " planned).");
                return true;
            }
            Note(LogKind.Notice, "Late Approval: the planned window closed at " + KerbinTime.Date(plan.WindowClosesUt.Value)
                + " and the next one" + (next.HasValue ? " costs " + MetresPerSecond(next.Value) + " against " + MetresPerSecond(plan.ExpectedDeltaV) + " planned" : " is not known")
                + ", so Claudia is re-planning.");
            if (plan.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            mission.Stage = MissionStage.Sequencing;
            mission.PendingSequence = null;
            Begin(new Turn
            {
                Purpose = Purpose.SequencePlan,
                UserText = MissionPrompts.LateApprovalReplan(_ut, plan.ExpectedDeltaV, next) + "\n" + MissionPrompts.Facts(_ut, _snapshot),
                Notice = MissionPrompts.LateReplanNotice,
                Waits = true,
            });
            return false;
        }

        /// <summary>
        /// At the Due Time: warp drops to 1× and the player is told, whether they were watching or not. A missed
        /// Due Time is not acted on: the sequence waits for the player's Approval.
        /// </summary>
        private void CheckDue()
        {
            var mission = _mission;
            var due = PendingDueTime;
            if (mission == null || mission.DueNotified || !due.HasValue || _ut < due.Value) return;
            mission.DueNotified = true;
            var text = _ut - due.Value > 60
                ? "Due Time missed by " + KerbinTime.Span(_ut - due.Value) + ": " + mission.PendingSequence.Name + " waits for your Approval."
                : "Due Time reached: " + mission.PendingSequence.Name + " is ready for your Approval.";
            _commands.Add(new DropWarp());
            Note(LogKind.Notice, text);
            NeedsPlayer(text);
        }

        private void WarpRequested()
        {
            var due = PendingDueTime;
            if (!due.HasValue || _ut >= due.Value || _mission.InFlight) return;
            // #867: never a warp through an orbit that crosses a moon.
            var blocker = CrossingBlockerNow(_mission);
            if (blocker.Length > 0)
            {
                Note(LogKind.Notice, blocker + " The mod won't warp to the Due Time: Re-plan, or fly the fix yourself first.");
                return;
            }
            _commands.Add(new WarpToUt(due.Value));
        }

        /// <summary>Go to <i>vessel</i> is on offer: the Due Time has been reached and the player is not in the Mission's vessel.</summary>
        private bool GoToOffered
        {
            get
            {
                var mission = _mission;
                var due = PendingDueTime;
                if (mission == null || !due.HasValue || _ut < due.Value || mission.VesselId.Length == 0) return false;
                return _scene != GameScene.Flight || ElsewhereInFlight;
            }
        }

        private void GoToRequested()
        {
            if (GoToOffered) _commands.Add(new SwitchToVessel(_mission.VesselId));
        }

        private bool CanEditSettings
        {
            get
            {
                var mission = _mission;
                return mission != null && !MissionOver && !mission.InFlight
                    && (mission.Stage == MissionStage.AwaitingPlanApproval || mission.Stage == MissionStage.AwaitingSequenceApproval
                        || mission.Stage == MissionStage.WaitingForFlight);
            }
        }

        /// <summary>
        /// The Mission Settings were edited: they are kept and persisted, and Claudia is asked for a revised Mission
        /// Plan, which needs Approval again. A Sequence Plan awaiting Approval is dropped with the old plan.
        /// </summary>
        private void SettingsEdited(MissionSettingsValues edited)
        {
            var mission = _mission;
            if (edited == null || !CanEditSettings) return;
            var current = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
            if (current.SameAs(edited)) return;
            edited = edited.Copy();
            edited.ConfigAllowsPhysicsWarp = _config.PhysicsWarp;
            edited.WriteTo(mission.Settings);
            Note(LogKind.Player, "Mission Settings: " + edited.Summary());
            if (mission.Stage == MissionStage.WaitingForFlight)
            {
                // No plan yet: Claudia's first Mission Plan is asked for with the new settings (TryPlan).
                return;
            }
            if (mission.Stage == MissionStage.AwaitingSequenceApproval)
            {
                DropPendingSequence(mission);
            }
            mission.ConfirmPrompt = null;
            mission.Stage = MissionStage.Planning;
            Begin(new Turn
            {
                Purpose = Purpose.Settings,
                UserText = MissionPrompts.SettingsChanged(edited) + "\n" + MissionPrompts.Facts(_ut, _snapshot) + PlanFigures(mission, mission.Goal),
                Notice = MissionPrompts.SettingsNotice,
            });
        }

        // ---- kOS ----

        private void RunRefused(KosRunRefused refused)
        {
            var reason = refused.Reason;
            var mission = _mission;
            if (mission == null || !IsRunning(mission) || mission.RunId != refused.RunId) return;
            mission.RunId = null;
            mission.RunDeadlineUt = null;
            EndLive(mission);
            // Paused from Running, the Mission stays Paused and Resume goes back to approval, not to Running.
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.AwaitingSequenceApproval;
            else mission.Stage = MissionStage.AwaitingSequenceApproval;
            Notice("The script didn't start: " + reason);
        }

        private void RunEnded(KosRunEnded ended)
        {
            var mission = _mission;
            if (mission == null) return;
            var running = mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
            if (!running || mission.RunId != ended.RunId) return;
            mission.RunId = null;
            TimedOutMidWarp(mission, ended.Outcome);
            mission.RunDeadlineUt = null;
            mission.RcsUse.Close(_ut);
            EndLive(mission);
            var outcome = StoppedOutcome(mission, ended.Outcome);
            NoteRunOutcome(mission, outcome);
            Notice("The script ended: " + outcome.Summary + ".");
            if (RunEndedAtCheckpoint(mission, outcome)) return;
            if (DebriefOnReturn(mission, outcome)) return;
            AwaitPostRunSnapshot(mission, outcome, ended.Ut);
        }

        /// <summary>The Debrief of the run that ended, from the Game Snapshot read after it (MissionControl.RunSettle.cs).</summary>
        private void BeginRunDebrief(Mission mission, RunOutcome outcome)
        {
            if (DebriefedRefusal(mission, outcome)) return;
            var turn = new Turn
            {
                Purpose = Purpose.Debrief,
                UserText = MissionPrompts.RunReport(mission.PendingSequence, outcome, CrossingsText(mission), mission.Blocks, true) + "\n" + ChildRunNote(mission) + MissionPrompts.Facts(_ut, _snapshot),
                Report = outcome.Report,
                Notice = MissionPrompts.DebriefNotice,
            };
            if (mission.Stage == MissionStage.Paused) mission.StageBeforePause = MissionStage.Debriefing;
            else mission.Stage = MissionStage.Debriefing;
            Begin(turn);
        }

        // ---- Lost ----

        /// <summary>
        /// Claudia was killed. A crewed Mission ends as Lost: a request in flight is cancelled (it keeps its worst-case
        /// charge), a running script is aborted, and Mission Control closes the Mission with a final Debrief. That
        /// Debrief is the mod's own, not a Claude request: Claudia can't write it, and nothing spends past the
        /// Spending Cap. Late run events carry a run id the Mission no longer holds, so they are ignored. A remote
        /// Mission never had her aboard and carries on.
        /// </summary>
        private void Killed(string vesselId, Kos.TracedBlock traced)
        {
            var mission = _mission;
            if (MissionOver || mission.Remote) return;
            // She died aboard some other craft: not this Mission's loss.
            if (vesselId.Length > 0 && mission.VesselId.Length > 0 && vesselId != mission.VesselId) return;
            var doing = LostDoing(mission);
            var wasRunning = IsRunning(mission);
            if (mission.InFlight) mission.Pending.Cancel.Cancel();
            else mission.Pending = null;
            CancelCompaction(mission);
            if (IsRunning(mission) && mission.RunningProcessor.HasValue)
                _commands.Add(new AbortScript(mission.RunningProcessor.Value, "Claudia was killed aboard"));
            mission.RunId = null;
            mission.RunningProcessor = null;
            mission.Queue.Clear();
            if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            EndLive(mission);
            mission.Owed = Owed.None;
            mission.OwedOutcome = null;
            mission.ConfirmPrompt = null;
            mission.EvaScience = "";
            mission.PauseReason = "";
            mission.PausedForCap = false;
            mission.WaitingForCap = false;
            mission.CapWaitText = "";
            mission.Stage = MissionStage.Lost;
            ClearUnanswered(mission);

            mission.LatestLine = "Mission Control: we have lost Claudia. The Mission is Lost.";
            Note(LogKind.Claudia, mission.LatestLine);
            // Only a run that was going on at her death has a last word; a trace carried for any other stage is not the Mission's.
            var lastTrace = traced != null && wasRunning ? Kos.BlockTrace.Describe(traced) : "";
            if (lastTrace.Length > 0) Note(LogKind.Notice, "Last traced state of the running block: " + lastTrace + ".");
            LostDebrief(mission, doing, lastTrace);
            NeedsPlayer("Claudia was killed aboard. The Mission is Lost.");
            Note(LogKind.Notice, "Claudia was killed aboard the craft: the Mission is over.");
            RecordFinished(mission);
        }

        /// <summary>What the Mission was in the middle of when she died, for the final Debrief.</summary>
        private static string LostDoing(Mission mission)
        {
            if (IsRunning(mission) && mission.PendingSequence != null) return "while \"" + mission.PendingSequence.Name + "\" was running";
            switch (mission.Stage)
            {
                case MissionStage.AwaitingPlanApproval: return "while the Mission Plan waited for Approval";
                case MissionStage.AwaitingSequenceApproval: return "while a Sequence Plan waited for Approval";
                case MissionStage.WaitingForFlight: return "before the craft left the editor";
                default: return "during the Mission";
            }
        }

        /// <summary>
        /// The final Debrief from Mission Control. Its Debrief outcome is Failed (the Mission Goal can't be reached as
        /// stated); the Mission's outcome is Lost.
        /// </summary>
        private void LostDebrief(Mission mission, string doing, string traced = "")
        {
            var card = AddCard(mission, LogKind.Debrief, MissionCards.LostTitle, MissionCards.LostBody(mission.Goal, doing, mission.Spent, mission.SpendingCap, traced: traced), MissionCards.LostWhy);
            card.Outcome = "Failed";
        }

        // ---- End Mission ----

        /// <summary>
        /// A player action (the adapter has already asked for confirmation). The Mission is dropped, so a late
        /// reply or script report is ignored; a running script is aborted through the adapter.
        /// </summary>
        private void EndRequested()
        {
            var mission = _mission;
            if (MissionOver) return;
            if (mission.InFlight) mission.Pending.Cancel.Cancel();
            CancelCompaction(mission);
            var running = mission.Stage == MissionStage.Running
                || (mission.Stage == MissionStage.Paused && mission.StageBeforePause == MissionStage.Running);
            if (running && mission.RunningProcessor.HasValue)
                _commands.Add(new AbortScript(mission.RunningProcessor.Value, "the player ended the Mission"));
            if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            // End Mission on a Failed Debrief's wait records the Mission as finished, as the auto-end did before it waited on the player.
            if (mission.Stage == MissionStage.AwaitingReducedGoal)
            {
                mission.Stage = MissionStage.Ended;
                RecordFinished(mission);
            }
            try
            {
                MissionFolder.WriteEnded(mission.Folder, mission.Id, mission.Stage.ToString());
                MissionStore.WriteVessel(mission.Folder, mission.VesselId, mission.VesselName);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The Mission still ends; the folder just lacks the marker.
            }
            _mission = null;
        }

        // ---- Requests ----

        /// <summary>The effort table's entry for a turn's reply kind (<see cref="Config.EffortTable"/>); the Margin recheck has its own request.</summary>
        private string EffortFor(Purpose purpose)
        {
            return _config.Efforts.For(KindOf(purpose));
        }

        /// <summary>A Debrief and an Alert are answered with no tool (#587): tool_choice none, the tools list unchanged so the cache holds (ADR 0012).</summary>
        private static bool TakesNoTool(Purpose purpose)
        {
            return purpose == Purpose.Debrief || purpose == Purpose.Alert;
        }

        /// <summary>The effort table's key for a turn's reply kind.</summary>
        private static string KindOf(Purpose purpose)
        {
            switch (purpose)
            {
                case Purpose.MissionPlan: return "mission_plan";
                case Purpose.SequencePlan: return "sequence_plan";
                case Purpose.Debrief: return "debrief";
                case Purpose.Reply: return "reply";
                case Purpose.Change: return "change";
                case Purpose.Question: return "question";
                case Purpose.Settings: return "settings";
                case Purpose.Alert: return "alert";
                case Purpose.Margin: return "margin_recheck";
                default: throw new ArgumentOutOfRangeException("purpose");
            }
        }

        /// <summary>What the Capcom strip says while a request is out: "thinking", or the Second look.</summary>
        private static string Thinking(Turn turn)
        {
            return turn.SecondLookReason.Length > 0 ? SecondLook.Activity : "thinking";
        }

        /// <summary>A bad entry in the config's effort table keeps its default; the Mission's event log says so, once, as the Mission starts.</summary>
        private void LogEffortProblems(Mission mission)
        {
            foreach (var problem in _config.Efforts.Problems) Event(mission, "effort_table", problem);
        }

        private void Begin(Turn turn)
        {
            if (turn.Effort == null) turn.Effort = EffortFor(turn.Purpose);
            var mission = _mission;
            // A compaction is never in a turn's way mid-loop: it waits for a turn boundary, and a turn then waits for it.
            if (mission.Pending != null || WaitsForCompaction(mission, turn))
            {
                Enqueue(mission, turn);
                return;
            }
            turn.UserText = DepartureLine(mission) + turn.UserText;
            if (turn.Purpose == Purpose.Debrief && PrepareDebrief(mission, turn)) return;
            if (turn.Purpose == Purpose.MissionPlan || (turn.Purpose == Purpose.SequencePlan && !turn.ScriptRewrite)) turn.UserText = WithGoalBiomes(mission, turn.UserText);
            mission.Pending = turn;
            RecordUnanswered(mission, turn);
            Send(mission, turn);
        }

        /// <summary>
        /// #699: a plan request's text with the line naming the Mission Goal's biomes (<see cref="MissionPrompts.GoalBiomeLine"/>), and #713's naming its Points of Interest,
        /// while a landing is still to fly: before the Mission Plan's steps, or while one not yet ticked names the biome or a landing.
        /// Unchanged for a goal that names none, so other Missions' requests don't grow.
        /// </summary>
        private string WithGoalBiomes(Mission mission, string text)
        {
            foreach (var line in new[] { MissionPrompts.GoalBiomeLine(GoalBiomesToLand(mission)), MissionPrompts.GoalPoiLine(GoalPoisToLand(mission), _snapshot) })
                if (line.Length > 0 && !(text ?? "").Contains(line)) text = text + "\n" + line;
            return text;
        }

        /// <summary>#713: the Mission Goal's Points of Interest whose landing is still to fly, as its biomes' (<see cref="LandingToFly"/>).</summary>
        private List<GoalPoi> GoalPoisToLand(Mission mission)
        {
            return MissionPrompts.GoalPois(_snapshot, mission.Goal).Where(p => PoiToFly(mission.GoalSteps, p)).ToList();
        }

        /// <summary>#713: before the Mission Plan's steps, always; after, while a step not yet ticked names the words, or a landing on one of their bodies.</summary>
        internal static bool PoiToFly(IList<GoalItem> steps, GoalPoi poi)
        {
            return poi.Entries.Select(e => e.Body).Distinct().Any(body => LandingToFly(steps, new GoalBiome { Body = body, Biome = poi.Term }));
        }

        /// <summary>The Mission Goal's biomes whose landing is still to fly (<see cref="LandingToFly"/>).</summary>
        private List<GoalBiome> GoalBiomesToLand(Mission mission)
        {
            return MissionPrompts.GoalBiomes(_snapshot, mission.Goal).Where(b => LandingToFly(mission, b)).ToList();
        }

        private static readonly System.Text.RegularExpressions.Regex LandingStep = new System.Text.RegularExpressions.Regex(@"\bland(ing)?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Before the Mission Plan's steps, always; after, while a step not yet ticked names the biome, or a landing on its body.
        /// </summary>
        internal static bool LandingToFly(IList<GoalItem> steps, GoalBiome biome)
        {
            if (steps.Count == 0) return true;
            return steps.Any(s => !s.Done && (s.Item.IndexOf(biome.Biome, StringComparison.OrdinalIgnoreCase) >= 0
                || (LandingStep.IsMatch(s.Item) && s.Item.IndexOf(biome.Body, StringComparison.OrdinalIgnoreCase) >= 0)));
        }

        private static bool LandingToFly(Mission mission, GoalBiome biome)
        {
            return LandingToFly(mission.GoalSteps, biome);
        }

        private void Send(Mission mission, Turn turn)
        {
            if (!_hasKey())
            {
                Pause(mission, "Add your Anthropic API key in the Claudia Kerman settings, then Retry.");
                return;
            }
            if (HeldForCraft(mission, turn)) return;

            var request = BuildRequest(mission, turn);
            decimal worstCase;
            // In a Blackout nothing Pauses at the cap: the player couldn't answer, so the request waits (the waiting call).
            if (!MaySend(mission, request, turn.Waits || IsRunning(mission) || mission.Blackout, out worstCase)) return;
            mission.WaitingForCap = false;
            mission.CapWaitText = "";

            // Recorded before sending; replaced by the real usage, or kept if the request is cancelled or lost.
            mission.PendingCharge = worstCase;
            mission.PendingInput = RequestCharge.InputCost(_config.Prices, mission.Model, request, worstCase);
            mission.Requests++;
            LogCost(mission, "pending", worstCase, "", mission.PendingInput);
            mission.InFlight = true;
            mission.Activity = Thinking(turn);
            turn.Stats.Sent();
            turn.Cancel = new CancellationTokenSource();
            var cancel = turn.Cancel.Token;
            var dump = mission.Dump;
            _background.Run(
                () => _client.Stream(request, update => _background.Post(() => Streamed(mission, turn, update)), cancel, dump),
                result => Finished(mission, turn, result),
                error => Finished(mission, turn, null));
        }

        /// <summary>The turn's request: its messages are built once, on first need, and kept unchanged for Retry (<see cref="ResendFromScratch"/> is the one way to build them again).</summary>
        private MessagesRequest BuildRequest(Mission mission, Turn turn)
        {
            if (turn.Messages == null)
            {
                if (turn.Effort == null) turn.Effort = EffortFor(turn.Purpose);
                turn.ToolSnapshot = _snapshot;
                turn.CompactionBase = mission.CompactionBlock;
                turn.CompactedBase = mission.CompactedMessages;
                turn.Messages = new List<Message>();
                if (turn.Effort != mission.Effort) turn.Messages.Add(Message.SystemEffort(turn.Effort));
                turn.Messages.Add(Message.UserText(ShapedUserText(mission, turn)));
                var notice = turn.Notice + BlackoutNotice(mission) + (mission.AutoMode ? "\n\n" + MissionPrompts.AutoModeNotice : "");
                if (turn.Purpose == Purpose.SequencePlan && _snapshot != null && _snapshot.Falling) notice += "\n\n" + MissionPrompts.FallingNotice;
                if (turn.CutOffRetry) notice += "\n\n" + MissionPrompts.CutOffNote;
                if (turn.SecondLookReason.Length > 0) notice += "\n\n" + SecondLook.Note(turn.SecondLookReason);
                turn.Messages.Add(Message.SystemText(notice));
            }

            var request = new MessagesRequest
            {
                Model = mission.Model,
                MaxTokens = MissionPrompts.MaxTokens,
                OutputSchemaJson = MissionReply.MissionSchemaJson,
                NoToolChoice = turn.ToolLimitSent || turn.PlanCallRefused || TakesNoTool(turn.Purpose),
            };
            request.Tools.AddRange(MissionTools());
            request.System.AddRange(mission.System);
            request.Messages.AddRange(Sent(mission, turn.CompactionBase, turn.CompactedBase));
            request.Messages.AddRange(turn.Messages);
            AddDiagnostics(mission, request);
            return request;
        }

        /// <summary>
        /// Discards the turn's uncommitted messages and builds them again from the turn's own fields, with the
        /// Game Snapshot of this moment: the one step of "discard the uncommitted turn and resend it from scratch"
        /// (the cut-off retry, a discarded tool loop, and the Second look). A tool loop in the turn goes with it. <paramref name="reshape"/> changes
        /// the turn first (its effort, a note). The turn is recorded as unanswered again, so a quit or save before
        /// the reply loads it in its new form, and nothing is sent: <see cref="Send"/> does that.
        /// </summary>
        private void ResendFromScratch(Mission mission, Turn turn, Action<Turn> reshape)
        {
            if (reshape != null) reshape(turn);
            turn.Messages = null;
            turn.Cancel = null;
            turn.ToolSteps = 0;
            turn.ToolLimitSent = false;
            turn.PlanCallRefused = false;
            turn.RepeatStop = false;
            turn.PlanOnly = false;
            turn.KeepLoop = false;
            turn.ToolSnapshot = null;
            RecordUnanswered(mission, turn);
        }

        /// <summary>
        /// A reply stopped at <c>max_tokens</c> (#267). It is never committed. The turn is put into its reduced form (medium
        /// effort and the cut-off note) and, the first time, resent at once if the Spending Cap has room for its worst case;
        /// otherwise, or after a second cut-off, it fails as it always did, and the player's Retry sends the reduced form.
        /// </summary>
        private void CutOff(Mission mission, Turn turn)
        {
            var again = turn.CutOffRetry;
            // A Second look that is cut off never stacks an automatic retry on it: the turn fails, and the player's Retry sends the reduced form.
            var looked = turn.SecondLookReason.Length > 0;
            if (!again)
                ResendFromScratch(mission, turn, t => { t.CutOffRetry = true; t.Effort = MissionPrompts.CutOffEffort; t.SecondLookReason = ""; });
            if (!again && !looked && HasCapRoom(mission, turn))
            {
                Event(mission, "cut_off_retry", turn.Purpose.ToString());
                mission.Activity = "thinking";
                Send(mission, turn);
                return;
            }
            Failed(mission, turn, "Claudia's reply was cut short.");
            if (mission.Stage == MissionStage.Paused && mission.Pending == turn) mission.PauseCause = PauseCause.CutOff;
        }

        /// <summary>
        /// The Second look (#272) for a turn's reply: when it is of an eligible kind and says it is unsure, the reply is dropped
        /// uncommitted and the turn goes again at high effort with its reason, if the Spending Cap has room for that worst case;
        /// without room the reply is kept and the skip noted, as a skipped margin recheck is. True when the turn was resent.
        /// The second reply's own doubt is only logged.
        /// </summary>
        private bool TakeSecondLook(Mission mission, Turn turn, MissionReply reply)
        {
            if (reply.UnsureBecause.Length == 0) return false;
            if (turn.SecondLookReason.Length > 0)
            {
                Event(mission, "second_look_unsure", turn.Purpose + ": " + reply.UnsureBecause);
                return false;
            }
            if (turn.CutOffRetry || !SecondLook.Eligible(KindOf(turn.Purpose), turn.Effort)) return false;
            if (!SecondLookRoom(mission, turn, reply.UnsureBecause))
                return false;
            ResendFromScratch(mission, turn, t => { t.SecondLookReason = reply.UnsureBecause; t.Effort = SecondLook.Effort; });
            Event(mission, "second_look", turn.Purpose + ": " + reply.UnsureBecause);
            Note(LogKind.Notice, "Claudia was unsure of her first reply (" + Shorten(reply.UnsureBecause) + ") and is taking a second look.");
            mission.Activity = Thinking(turn);
            Send(mission, turn);
            return true;
        }

        /// <summary>Whether a Second look fits the Spending Cap; with none, the skip is logged. Judged on the request just answered (it holds more than the resend will) plus the reason line.</summary>
        private bool SecondLookRoom(Mission mission, Turn turn, string reason)
        {
            var request = BuildRequest(mission, turn);
            var estimatedInput = (request.ToJson().Length + SecondLook.Note(reason).Length + 400 + 2) / 3;
            var worst = _config.Prices.WorstCase(mission.Model, estimatedInput, request.MaxTokens);
            if (mission.Committed + worst <= mission.SpendingCap) return true;
            LogCost(mission, "blocked", worst, "over_cap");
            Event(mission, "second_look_skipped", turn.Purpose + ": " + reason);
            Note(LogKind.Notice, "A second look was skipped: it could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). Claudia's first reply was unsure because: " + Shorten(reason));
            return false;
        }

        /// <summary>Whether the Spending Cap has room for the turn's request at its worst case; unlike <see cref="MaySend"/> it never Pauses or waits.</summary>
        private bool HasCapRoom(Mission mission, Turn turn)
        {
            return mission.Committed + RequestCharge.WorstCase(_config.Prices, mission.Model, BuildRequest(mission, turn)) <= mission.SpendingCap;
        }

        /// <summary>
        /// The Spending Cap's pre-send check: spent + kept charges + the request's estimated input and its
        /// worst-case <c>max_tokens</c> output must fit the cap, or the request is not sent. A request that
        /// <paramref name="waits"/> becomes the waiting call (<see cref="WaitForCap"/>); any other Pauses the
        /// Mission with Raise cap or End Mission (story 124).
        /// </summary>
        private bool MaySend(Mission mission, MessagesRequest request, bool waits, out decimal worstCase)
        {
            worstCase = RequestCharge.WorstCase(_config.Prices, mission.Model, request);
            if (mission.Committed + worstCase <= mission.SpendingCap) return true;
            LogCost(mission, "blocked", worstCase, "over_cap");
            if (waits) WaitForCap(mission);
            else PauseForCap(mission, "The next request could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.");
            return false;
        }

        /// <summary>
        /// The waiting call (story 125): a request sent while a run is live, or near one, that would pass the Spending Cap
        /// is kept unsent as the pending turn. The Mission stays where it is (a run carries on without Claudia), a screen
        /// message says so, and Raise cap sends it (<see cref="Raised"/>).
        /// </summary>
        private void WaitForCap(Mission mission)
        {
            if (mission.WaitingForCap) return;
            mission.WaitingForCap = true;
            mission.CapWaitText = "Claudia's next request waits: it could pass the Spending Cap ($" + Dollars(mission.SpendingCap) + "). "
                + (IsRunning(mission) ? "The sequence carries on without her; Raise cap to send it." : "Raise cap to send it, or End Mission.");
            Note(LogKind.Notice, mission.CapWaitText);
            _commands.Add(new PostScreenMessage(mission.CapWaitText, always: true));
        }

        private void Streamed(Mission mission, Turn turn, StreamUpdate update)
        {
            if (mission != _mission || mission.Pending != turn || !mission.InFlight) return;
            var activity = mission.Activity;
            if (update.Kind == StreamUpdateKind.Restarted) activity = Thinking(turn);
            else if (update.Kind == StreamUpdateKind.TextDelta && turn.SecondLookReason.Length == 0) activity = "writing";
            if (activity == mission.Activity) return;
            mission.Activity = activity;
            Rebuild();
        }

        private void Finished(Mission mission, Turn turn, StreamResult result)
        {
            if (mission.Pending != turn || !mission.InFlight) return;
            mission.InFlight = false;
            mission.Activity = "";
            turn.Cancel.Dispose();
            // Settled and logged before the reply is applied, which may send the next request.
            var spentBefore = mission.Spent;
            Settle(mission, turn, result);
            TrackCache(mission, result);
            turn.Stats.Ended(mission.Spent - spentBefore, result != null && result.Status == StreamStatus.Completed ? result.Message : null);
            LogCost(mission, mission.Settlement, mission.Settled, mission.Outcome);
            // The Mission was dropped (End Mission, or another save loaded) while this request ran.
            if (mission != _mission) return;
            // Claudia was killed while this ran: it is paid for, and its reply (if one got through) is not applied.
            if (mission.Stage == MissionStage.Lost)
            {
                mission.Pending = null;
                // A save loaded while it ran still gets its state back (a quickload to before she died).
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
                return;
            }
            try
            {
                Answered(mission, turn, result);
                if (mission == _mission) AfterCharge(mission);
            }
            finally
            {
                if (mission.DeferredRestore != null) CompleteDeferredRestore(mission);
                Rebuild();
            }
        }

        private void Answered(Mission mission, Turn turn, StreamResult result)
        {
            if (result == null)
            {
                Failed(mission, turn, "Something went wrong getting Claudia's reply; see KSP.log.");
                return;
            }
            if (result.Status != StreamStatus.Completed)
            {
                if (result.Failure == StreamFailure.ModelNotFound) ModelGone(mission, turn);
                else Failed(mission, turn, Describe(result));
                return;
            }
            var message = result.Message;
            if (message.StopReason == StopReason.Refusal)
            {
                Refused(mission, turn);
                return;
            }
            if (message.StopReason == StopReason.MaxTokens)
            {
                CutOff(mission, turn);
                return;
            }
            if (message.StopReason == StopReason.ToolUse)
            {
                ToolsCalled(mission, turn, message);
                return;
            }
            var reply = ReadReply(turn, message);
            var problem = reply == null ? "it wasn't in the Mission reply format" : Mismatch(turn.Purpose, reply);
            if (problem != null)
            {
                if (turn.Purpose == Purpose.Debrief && reply != null && !PlanNotProposed(reply) && reply.Debrief == null && mission.DeferredRestore == null)
                {
                    EmptyDebriefReply(mission, turn);
                    return;
                }
                if (reply != null && PlanNotProposed(reply) && TakesMissionPlan(turn.Purpose) && !turn.PlanCallRetry && mission.DeferredRestore == null)
                {
                    PlanCallCorrection(mission, turn, problem);
                    return;
                }
                turn.KeepLoop = turn.ToolSteps >= ToolStep.MaxSteps && turn.Messages != null && mission.DeferredRestore == null;
                Failed(mission, turn, "Claudia's reply didn't fit: " + problem + ".");
                return;
            }
            if (TakeSecondLook(mission, turn, reply)) return;

            // Append-only: the turn and its reply join the conversation together, then the transcript is rewritten.
            var committed = message.ToMessage();
            DropStaleThinking(mission, turn, committed);
            mission.Conversation.AddRange(turn.Messages);
            mission.Conversation.Add(committed);
            foreach (var m in turn.Messages)
                if (m.Effort != null) mission.Effort = m.Effort;
            mission.Pending = null;
            SnapshotCommitted(mission, turn);
            TrackContext(mission, turn, message);
            WriteTranscript(mission);
            ClearUnanswered(mission);
            // A save was loaded while this ran: the reply stays in the conversation, the loop state is the save's.
            if (mission.DeferredRestore != null) return;

            Apply(mission, turn, reply, message.FallbackRan ? message.Model ?? "" : "");
            if (mission == _mission) ApproveInAdvance(mission);

            SendQueued(mission);
        }

        /// <summary>Nothing is in hand: the next queued turn goes, unless the Mission is Paused.</summary>
        private void SendQueued(Mission mission)
        {
            if (mission.Pending != null || mission.Stage == MissionStage.Paused) return;
            var next = TakeQueued(mission);
            if (next != null) Begin(next);
        }

        /// <summary>
        /// A reply that can't be used. A question asked while Running never affects the run: it is dropped with a notice
        /// and the next queued turn goes. Anything else Pauses the Mission for Retry (story 134).
        /// </summary>
        private void Failed(Mission mission, Turn turn, string reason)
        {
            if (turn.Purpose != Purpose.Question || !IsRunning(mission) || mission.Stage == MissionStage.Paused)
            {
                Pause(mission, reason);
                return;
            }
            mission.Pending = null;
            ClearUnanswered(mission);
            Note(LogKind.Notice, "Claudia couldn't answer that: " + reason + " The sequence carries on.");
            SendQueued(mission);
        }

        /// <summary>The turn's reply, its Mission Plan the one propose_mission_plan accepted in the turn; null when it isn't a Mission reply.</summary>
        private static MissionReply ReadReply(Turn turn, AssistantMessage message)
        {
            var reply = MissionReply.Parse(message.Text);
            return reply != null ? reply.WithProposed(ProposedPlans.LastAccepted(turn.Messages ?? new List<Message>())) : null;
        }

        /// <summary>A reply of kind mission_plan whose plan no propose_mission_plan call accepted.</summary>
        private static bool PlanNotProposed(MissionReply reply)
        {
            return reply.Kind == MissionReply.MissionPlanKind && reply.MissionPlan == null;
        }

        /// <summary>
        /// A mission_plan reply with no accepted plan on a turn that takes one (#667). It is never committed. The turn is resent
        /// once with the correction after its own messages (a tool loop stays), as the last step that may call propose_mission_plan
        /// alone, if the Spending Cap has room; otherwise, or after a second refusal, it fails as it always did.
        /// </summary>
        private void PlanCallCorrection(Mission mission, Turn turn, string problem)
        {
            turn.PlanCallRetry = true;
            turn.Messages.Add(Message.SystemText(MissionPrompts.PlanCallNote));
            turn.ToolLimitSent = false;
            turn.RepeatStop = false;
            turn.PlanOnly = true;
            if (!HasCapRoom(mission, turn))
            {
                Failed(mission, turn, "Claudia's reply didn't fit: " + problem + ".");
                return;
            }
            Event(mission, "plan_call_retry", turn.Purpose.ToString());
            mission.Activity = Thinking(turn);
            Send(mission, turn);
        }

        /// <summary>
        /// Why a reply doesn't fit its request (reported as off_schema), or null when it does. The schema takes every kind
        /// (ADR 0012), so this is where a kind the request can't come back as is refused.
        /// </summary>
        private static string Mismatch(Purpose purpose, MissionReply reply)
        {
            if (PlanNotProposed(reply)) return "no Mission Plan was accepted through propose_mission_plan";
            switch (purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    // A Mission Plan with no sequences is valid: it is how Claudia declines a goal that can't be flown.
                    return reply.MissionPlan == null ? "no Mission Plan" : null;
                case Purpose.Reply:
                case Purpose.Change:
                    // A typed reply comes back as an answer, a Sequence Plan or a Mission Plan, never a Debrief or an Alert verdict.
                    return reply.Kind == MissionReply.DebriefKind || reply.Kind == MissionReply.AlertKind || reply.Debrief != null || reply.Alert != null
                        ? "a " + reply.Kind + " reply to the player's typed words" : null;
                case Purpose.SequencePlan:
                    // A manual stretch has no script: the player flies it.
                    return reply.SequencePlan == null || (reply.SequencePlan.Script.Trim().Length == 0 && !reply.SequencePlan.Manual) ? "no Sequence Plan with a script" : null;
                case Purpose.Debrief:
                    return reply.Debrief == null ? "no Debrief" : null;
                case Purpose.Alert:
                    return reply.Alert == null ? "no Alert verdict" : null;
                default:
                    return null;
            }
        }

        private void Apply(Mission mission, Turn turn, MissionReply reply, string answeredBy)
        {
            var planPending = mission.Stage == MissionStage.AwaitingPlanApproval;
            // The Script Check (ADR 0003) comes first: the player never sees a plan whose script fails it.
            var offered = SequenceOffered(turn.Purpose, reply, planPending);
            IList<string> checkWarnings = new List<string>();
            if (offered != null)
            {
                var check = CheckScript(mission, offered);
                // Beside it, the plan's own figures (#674, #696): one wrong by the snapshot goes back in the same one correction.
                var findings = PlanFigures(mission, offered);
                if (!check.Passed) findings.ScriptProblems = check.Problems;
                if (findings.ScriptFailed || (findings.FiguresWrong && FiguresCorrectionWanted(mission, turn, findings)))
                {
                    ScriptCheckFailed(mission, turn, offered, findings);
                    return;
                }
                checkWarnings = check.Warnings.Concat(findings.Warnings()).ToList();
                // #867: a crossing the correction left (or had no room to send back) is the plan's Blocker.
                CrossingOffered(mission, offered, findings);
            }

            // Only a vessel that left the craft may be a Child Vessel: the player never sees a plan that names another.
            var unseparated = PlanNamingUnseparated(mission, turn, reply, planPending);
            if (unseparated.Count > 0)
            {
                ChildVesselRefused(mission, turn, unseparated);
                return;
            }

            // An Alert's verdict is her line, prefixed with the check: a carry on leaves just this log line.
            mission.LatestLine = turn.Purpose == Purpose.Alert
                ? "Alert, " + turn.Crossing.Check.Name() + ": " + (reply.Alert.Abort ? "Abort recommended. " : "carry on. ") + reply.Say
                : SayWithWhy(turn.Purpose, reply);
            Note(LogKind.Claudia, mission.LatestLine).AnsweredBy = answeredBy;

            switch (turn.Purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    OfferMissionPlan(mission, reply.MissionPlan);
                    break;
                case Purpose.SequencePlan:
                    OfferSequencePlan(mission, reply.SequencePlan);
                    break;
                case Purpose.Debrief:
                    Debriefed(mission, reply);
                    LogDebrief(mission, turn, reply.Debrief);
                    break;
                case Purpose.Reply:
                    // ADR 0002: Claude's reading of typed words never starts anything; only a button press does.
                    if ((reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear) && !mission.Declined)
                        mission.ConfirmPrompt = planPending ? "Approve the Mission Plan?" : "Approve the Sequence Plan?";
                    else if ((reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear) && reply.MissionPlan == null && turn.PlayerText != null)
                        PlanAgain(mission, turn.PlayerText);
                    else Revised(mission, reply, planPending);
                    break;
                case Purpose.Change:
                    Revised(mission, reply, planPending);
                    break;
                case Purpose.Question:
                    QuestionAnswered(mission, turn, reply);
                    break;
                case Purpose.Alert:
                    Alerted(mission, turn, reply);
                    break;
            }
            if (reply.SequencePlan != null && ReferenceEquals(mission.PendingSequence, reply.SequencePlan))
            {
                ShowWarnings(reply.SequencePlan, checkWarnings);
                LogPlan(mission, turn, reply.SequencePlan, checkWarnings);
            }
            if (reply.MissionPlan != null && ReferenceEquals(mission.Plan, reply.MissionPlan)) LogMissionPlan(mission, turn, false);
        }

        /// <summary>
        /// #836: each warning of the Script Check and the plan's findings is one Mission window event as the plan is offered, for the
        /// player only (the Mission's log never goes into a request to the model, the Debrief's included). One the Custom badge's reason
        /// already shows isn't repeated. It never blocks Approval.
        /// </summary>
        private void ShowWarnings(SequencePlan plan, IList<string> warnings)
        {
            foreach (var warning in warnings.Distinct())
                if (!plan.CustomScriptReason.Contains(warning)) Notice("Script Check: " + warning);
        }

        /// <summary>The ids a Mission Plan this reply would offer names as Child Vessels without their having left the craft; empty if it offers none.</summary>
        private static List<string> PlanNamingUnseparated(Mission mission, Turn turn, MissionReply reply, bool planPending)
        {
            var plan = reply.MissionPlan;
            if (plan == null) return new List<string>();
            switch (turn.Purpose)
            {
                case Purpose.MissionPlan:
                case Purpose.Settings:
                    return UnseparatedVesselIds(mission, plan);
                case Purpose.Reply:
                case Purpose.Change:
                    var approving = turn.Purpose == Purpose.Reply && (reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear) && !mission.Declined;
                    var offered = !approving && plan != null && (planPending || (mission.AwaitingApproval && ChangesGoal(reply)));
                    return offered ? UnseparatedVesselIds(mission, plan) : new List<string>();
                default:
                    return new List<string>();
            }
        }

        /// <summary>
        /// A Mission Plan named a vessel that never left the craft. The first time it goes back to Claudia with the reason (a
        /// billed turn) and the player sees nothing of the plan; if the correction names one too, the Mission Pauses for Retry.
        /// </summary>
        private void ChildVesselRefused(Mission mission, Turn turn, IList<string> ids)
        {
            var named = string.Join(", ", ids.ToArray());
            if (turn.ChildVesselRetry)
            {
                Failed(mission, turn, "Claudia's Mission Plan still names a Child Vessel that has not left the craft (" + named + ").");
                return;
            }
            Note(LogKind.Notice, "The Mission Plan names a vessel that hasn't left the craft (" + named + "); Claudia is revising it.");
            // The correction is the refused turn asked again: its own Purpose (so the reply is read, and the plan offered, as it
            // would have been) and its own wait at the Spending Cap, through the same Begin as any other turn.
            var typed = turn.Purpose == Purpose.Reply || turn.Purpose == Purpose.Change;
            Begin(new Turn
            {
                Purpose = turn.Purpose,
                ChildVesselRetry = true,
                Waits = turn.Waits,
                GoalStands = turn.GoalStands,
                // The refused turn was never committed to the conversation, so what it asked (the player's words, the new settings) goes again.
                UserText = turn.UserText + "\n" + MissionPrompts.ChildVesselNotLeft(ids),
                Notice = MissionPrompts.ChildVesselNotice +(typed ? MissionPrompts.ChildVesselTypedNotice : ""),
            });
        }

        /// <summary>Claudia's line; when she declines the goal with a Mission Plan of no sequences, her <c>why</c> follows it (there is no card to carry it).</summary>
        private static string SayWithWhy(Purpose purpose, MissionReply reply)
        {
            var plan = reply.MissionPlan;
            if ((purpose != Purpose.MissionPlan && purpose != Purpose.Settings) || plan == null || plan.Sequences.Count > 0 || string.IsNullOrWhiteSpace(plan.Why)) return reply.Say;
            return reply.Say + " " + plan.Why;
        }

        /// <summary>The Sequence Plan this reply would offer for Approval, or null (the same cases as <see cref="Apply"/>).</summary>
        private static SequencePlan SequenceOffered(Purpose purpose, MissionReply reply, bool planPending)
        {
            var plan = reply.SequencePlan;
            // A manual stretch has no script to check.
            if (plan == null || plan.Manual || plan.Script.Trim().Length == 0) return null;
            switch (purpose)
            {
                case Purpose.SequencePlan:
                    return plan;
                case Purpose.Reply:
                    return planPending || reply.Intent == ReplyIntent.Approve || reply.Intent == ReplyIntent.Unclear ? null : plan;
                case Purpose.Change:
                    return planPending ? null : plan;
                default:
                    return null;
            }
        }

        /// <summary>
        /// A script failed the Script Check. The first time, it goes back to Claudia for one automatic (billed)
        /// rewrite, and the player sees neither the plan nor its line. If a rewrite fails too, the plan is shown
        /// as "Couldn't produce a safe script" with Retry, Retry with a note or Re-plan, and nothing can be approved.
        /// </summary>
        private void ScriptCheckFailed(Mission mission, Turn turn, SequencePlan plan, PlanFindings findings)
        {
            if (!turn.ScriptRewrite)
            {
                var wrong = FiguresWrongNote(findings);
                Note(LogKind.Notice, findings.ScriptFailed
                    ? "The script didn't pass the Script Check" + (findings.FiguresWrong ? " and " + wrong : "") + "; Claudia is rewriting it."
                    : char.ToUpperInvariant(wrong[0]) + wrong.Substring(1) + "; Claudia is revising it.");
                Begin(FiguresCorrection(turn, findings));
                return;
            }
            ShowUnsafeScript(mission, plan, findings.ScriptProblems);
        }

        /// <summary>The one automatic correction's turn (also the probe the Spending Cap room check prices).</summary>
        private static Turn FiguresCorrection(Turn turn, PlanFindings findings)
        {
            return new Turn
            {
                Purpose = Purpose.SequencePlan,
                ScriptRewrite = true,
                Stats = turn.Stats,
                UserText = MissionPrompts.PlanCheckFailed(findings),
                Notice = MissionPrompts.PlanFixNotice(findings),
            };
        }

        /// <summary>The log's words for the figures found wrong, e.g. "a Telemetry Check is certain to cross by the plan's own figures".</summary>
        private static string FiguresWrongNote(PlanFindings findings)
        {
            var wrong = new List<string>();
            if (findings.Crossings.Count > 0) wrong.Add("a Telemetry Check is certain to cross by the plan's own figures");
            if (findings.BiomeMiss != null) wrong.Add(MissionPrompts.GoalBiomeMissed(findings.BiomeMiss));
            if (findings.DeorbitNoTarget != null) wrong.Add(MissionPrompts.DeorbitNoTargetMissed(findings.DeorbitNoTarget));
            if (findings.MoonCrossing != null) wrong.Add("the craft would wait in an orbit that crosses " + findings.MoonCrossing.Moon.Name + "'s sphere of influence");
            return string.Join(" and ", wrong.ToArray());
        }

        /// <summary>
        /// The plan's own figures checked against the Game Snapshot of the craft it flies (#674, #696); nothing is found for a
        /// Manual stretch, another vessel, or when the snapshot can't give the figure. Its landing's biome against the goal's (#699).
        /// </summary>
        private PlanFindings PlanFigures(Mission mission, SequencePlan plan)
        {
            var findings = new PlanFindings { ExpectedDeltaV = plan.ExpectedDeltaV };
            if (plan.Manual) return findings;
            // #699: a landing aimed at another biome than the Mission Goal names, for whichever vessel lands.
            var craft = FliesCraft(mission, plan.Vessel);
            if (_snapshot != null)
                findings.BiomeMiss = LandingBiomes.Miss(plan.Script, GoalBiomesToLand(mission), _snapshot, craft ? _snapshot.Body : "", _body.Biome);
            if (!craft) return findings;
            var snapshot = MissionVesselSnapshot(mission);
            var start = LiveReadings.DeltaVLeftOf(snapshot);
            findings.StartDeltaV = start ?? 0;
            findings.DeorbitNoTarget = DeorbitTargets.Missing(plan.Script, snapshot);
            // #867: the orbit the plan waits in for its Due Time, if it crosses a moon.
            findings.MoonCrossing = WaitCrossing(mission, plan, snapshot);
            findings.Crossings = PredictedCrossings.Find(plan.TelemetryChecks, plan.ExpectedDeltaV, start);
            // #830: a duration short of the first burn (#696) is raised by the mod itself, on every path (the landing target default's wait, #790, was the first):
            // no request back to Claudia, no prompt text about it; plans.jsonl logs the raise.
            var shortOf = BurnPoints.ShortOf(plan, snapshot, _ut, _body);
            if (shortOf != null)
            {
                findings.DurationRaised = MissionPrompts.DurationRaisedWarning(shortOf, plan.ExpectedDurationSeconds);
                plan.ExpectedDurationSeconds = shortOf.MinimumDurationSeconds;
            }
            return findings;
        }

        /// <summary>Whether a plan whose figures are wrong goes back: not after a correction already, and only with Spending Cap room for it.</summary>
        private bool FiguresCorrectionWanted(Mission mission, Turn turn, PlanFindings findings)
        {
            if (turn.ScriptRewrite) return false;
            return HasCapRoom(mission, FiguresCorrection(turn, findings.FiguresOnly()));
        }

        /// <summary>"Couldn't produce a safe script": the plan is dropped, nothing can be approved, and the player chooses Retry, Retry with a note or Re-plan.</summary>
        private void ShowUnsafeScript(Mission mission, SequencePlan plan, IList<string> problems)
        {
            mission.Stage = MissionStage.UnsafeScript;
            mission.PendingSequence = null;
            mission.ConfirmPrompt = null;
            mission.UnsafeProblems = problems;
            UnsafeScriptCard(mission, plan);
            PlanNews("Claudia couldn't produce a safe script.");
        }

        /// <summary>The card for "Couldn't produce a safe script"; <paramref name="plan"/> is null once the failed plan is gone (after a load).</summary>
        private static void UnsafeScriptCard(Mission mission, SequencePlan plan)
        {
            var card = AddCard(mission, LogKind.SequencePlan, plan != null ? plan.Name : "Sequence Plan",
                "Couldn't produce a safe script. The Script Check found: " + string.Join("; ", (mission.UnsafeProblems ?? new List<string>()).ToArray()),
                plan != null ? plan.Why : "");
            if (plan == null) return;
            card.CustomScript = plan.CustomScript;
            card.CustomScriptReason = plan.CustomScriptReason;
        }

        /// <summary>What actually runs: the plan's script bundled with the Core Library.</summary>
        private string Bundle(SequencePlan plan)
        {
            // #775: a Mission's run of the craft may stop at a Coast Checkpoint; a block test or a Child Vessel's run never does.
            return CoreLibrary.Bundle(_mission != null && FliesCraft(_mission, plan.Vessel) ? CoastCheckpoints.Armed(plan.Script) : plan.Script);
        }

        /// <summary>
        /// The Script Check over what would run (<see cref="Bundle"/>): the script comes first in it, so the line numbers are
        /// the script's. A Mission Plan step the plan names outside the Mission Plan fails it too (<see cref="PlanStepProblem"/>).
        /// </summary>
        private ScriptCheckResult CheckScript(Mission mission, SequencePlan plan, bool restored = false)
        {
            var check = _scriptCheck.Check(Bundle(plan));
            var step = PlanStepProblem(mission, plan, restored);
            if (step == null) return check;
            return new ScriptCheckResult(check.CompileError, check.Forbidden.Concat(new[] { step }).ToList()) { Warnings = check.Warnings, BlockOptions = check.BlockOptions };
        }

        /// <summary>Retry, Retry with a note (<paramref name="note"/>) or Re-plan, after "Couldn't produce a safe script".</summary>
        private void ScriptRetried(string note, bool replan)
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.UnsafeScript || mission.InFlight) return;
            if (note != null)
            {
                note = note.Trim();
                if (note.Length == 0) return;
                Note(LogKind.Player, "Retry with a note: " + note);
            }
            mission.Stage = MissionStage.Sequencing;
            Begin(ScriptRetryTurn(mission, note, replan));
        }

        /// <summary>The turn for Retry, Retry with a note or Re-plan after an unsafe script; also the probe the Auto Mode room check prices.</summary>
        private static Turn ScriptRetryTurn(Mission mission, string note, bool replan)
        {
            return new Turn
            {
                Purpose = Purpose.SequencePlan,
                // A Re-plan is a new plan, so it gets its own automatic rewrite; a retry of the script doesn't.
                ScriptRewrite = !replan,
                UserText = replan ? MissionPrompts.ReplanAfterScriptCheck(mission.UnsafeProblems)
                    : MissionPrompts.RetryAfterScriptCheck(mission.UnsafeProblems, note),
                Notice = replan ? MissionPrompts.ReplanNotice : MissionPrompts.RewriteNotice,
            };
        }

        /// <summary>
        /// Drops the pending Sequence Plan unflown: its Due Time alarm goes, and it stops counting towards
        /// <c>SequencesProposed</c> (and the step it named gives way to the one before), so the plan bar and the
        /// rest-of-plan figures don't run a step ahead (#263). Not for a plan that is asked for again as the same
        /// sequence (the unsafe-script and late-Approval re-plans, a script that fails its check on load): those keep
        /// <c>SequenceCounted</c> so the retry isn't counted twice.
        /// </summary>
        private void DropPendingSequence(Mission mission)
        {
            if (mission.PendingSequence != null && mission.PendingSequence.DueTimeUt.HasValue) _commands.Add(new ClearDueTimeAlarm(mission.Id));
            mission.PendingSequence = null;
            if (mission.SequenceCounted)
            {
                mission.SequencesProposed--;
                mission.PlanStep = mission.PlanStepBefore;
            }
            mission.SequenceCounted = false;
        }

        /// <summary>
        /// The Chute Groups (ADR 0019) of the craft as it is now over the Mission Plan's steps not yet begun: at Mission Plan
        /// Approval, the whole plan; at a Debrief, the steps after the one just flown. Only in Flight, where the snapshot carries
        /// the chutes' numbers: elsewhere (or with no Game Snapshot) there is none until the first snapshot in Flight gives one.
        /// </summary>
        private void GroupChutes(Mission mission)
        {
            mission.Chutes = null;
            if (_snapshot == null || mission.Plan == null || _scene != GameScene.Flight) return;
            mission.Chutes = ChuteGroups.Of(_snapshot, mission.Plan.Sequences, StepsBegun(mission), mission.Remote, mission.ProcessorPartId);
        }

        /// <summary>
        /// How many Mission Plan steps are begun: the step the Sequence Plan in hand named (#413), or, when it named
        /// none, how many Sequence Plans were counted. The plan bar's current step is the last one begun, and "later"
        /// (the rest-of-plan delta-v, the Debrief's "still to fly") is every step after it.
        /// </summary>
        private static int StepsBegun(Mission mission)
        {
            return Math.Max(0, mission.PlanStep ?? mission.SequencesProposed);
        }

        /// <summary>
        /// The Script Check's problem with the step a Sequence Plan names, or null when it names one in the Mission Plan
        /// or the Mission has no Mission Plan. Under a Mission Plan a plan naming none is a problem too (#837): the count
        /// would stand in and could run ahead of the craft. (A saved Mission's stepless plans are never re-checked on load,
        /// so <see cref="StepsBegun"/> keeps the count for them: <paramref name="restored"/>.) Checked with the script, so Claudia corrects it as she would a script.
        /// </summary>
        private static string PlanStepProblem(Mission mission, SequencePlan plan, bool restored = false)
        {
            if (mission.Plan == null || (restored && !plan.PlanStep.HasValue)) return null;
            var steps = mission.Plan.Sequences.Count;
            if (!plan.PlanStep.HasValue) return steps == 0 ? null : "plan_step is missing: name the Mission Plan step this sequence flies, 1 to " + steps.ToString(CultureInfo.InvariantCulture) + ".";
            var step = plan.PlanStep.Value;
            if (step >= 1 && step <= steps) return null;
            return "plan_step " + step.ToString(CultureInfo.InvariantCulture) + " is not a step of the Mission Plan: name the step this sequence flies, "
                + (steps == 1 ? "1 (the plan has one step)" : "1 to " + steps.ToString(CultureInfo.InvariantCulture)) + ".";
        }

        private void Revised(Mission mission, MissionReply reply, bool planPending)
        {
            if (reply.MissionPlan != null && (planPending || (mission.AwaitingApproval && ChangesGoal(reply))))
            {
                // A new goal makes the pending Sequence Plan, if that is what waited, moot.
                DropPendingSequence(mission);
                OfferMissionPlan(mission, reply.MissionPlan);
            }
            else if (!planPending && reply.SequencePlan != null && (reply.SequencePlan.Manual || reply.SequencePlan.Script.Trim().Length > 0)) OfferSequencePlan(mission, reply.SequencePlan);
        }

        /// <param name="restored">A load offers the saved plan again: its window pick was asked for when it first arrived.</param>
        private void OfferMissionPlan(Mission mission, MissionPlan plan, bool restored = false)
        {
            var first = mission.Plan == null;
            mission.Plan = plan;
            mission.PlanBaseline = _snapshot;
            PlanOffered(mission);
            PlanChildVessels(mission, plan);
            mission.Stage = MissionStage.AwaitingPlanApproval;
            if (plan.Sequences.Count == 0)
            {
                // Claudia can't fly this goal: her line is in the log, and there is nothing to approve.
                HerNews("Claudia can't fly this Mission Goal as stated.");
                return;
            }
            if (!restored) AskForWindow(mission, first);
            // As MissionLogRebuild does with the plan's card, so a load has the same steps.
            mission.GoalSteps = GoalSteps.Replace(mission.GoalSteps, plan.GoalSteps);
            // #757: the Craft Check ran before there was a plan, so the plan's own legs are judged against the Delta-v Map here.
            var underMap = _snapshot == null ? "" : PlanLegs.CardLine(_snapshot, PlanLegs.Judge(_snapshot, RestOfPlan(mission), mission.PlannedAscent), _config.CraftCheckMarginPercent);
            if (underMap.Length > 0) Event(mission, "leg_under_map", underMap.Trim());
            // Each Entry's Chute Group and predicted touchdown, from the craft as it is (only Flight's snapshot has the chutes' numbers).
            var groups = _snapshot == null || _scene != GameScene.Flight ? "" : ChuteGroups.Of(_snapshot, plan.Sequences, StepsBegun(mission), mission.Remote, mission.ProcessorPartId).Describe();
            AddCard(mission, LogKind.MissionPlan, "Mission Plan", MissionCards.MissionPlanBody(plan, mission.VesselName) + underMap + (groups.Length > 0 ? "\nChutes: " + groups : ""), plan.Why);
            PlanNews("Claudia has a Mission Plan for your Approval.");
        }

        /// <summary>Offers a Sequence Plan for Approval; its script has passed the Script Check (<see cref="Apply"/>).</summary>
        /// <param name="restored">A load offers the saved plan again: its Due Time and window were decided when it first arrived.</param>
        private void OfferSequencePlan(Mission mission, SequencePlan plan, bool restored = false)
        {
            if (!restored)
            {
                // Only the mod marks its own resume (#775).
                plan.CoastCheckpoint = "";
                // A departure that waits for the player's pick has its Due Time from that pick (#782).
                mission.WindowOwnDue = plan.DueTimeUt;
                if (!DeparturePickMissing(mission, plan))
                {
                    WindowWait(mission, plan);
                    TransferWait(mission, plan);
                }
            }
            // #867: a Due Time the mod set just now (a window's) was not there for the plan's figures: its wait is judged here, a Blocker with no correction.
            CrossingAtOffer(mission, plan);
            if (!mission.SequenceCounted)
            {
                mission.SequencesProposed++;
                mission.SequenceCounted = true;
                mission.PlanStepBefore = mission.PlanStep;
            }
            // The step it names, not the count, says where the Mission is: a retry names its step again (#413). A manual
            // stretch skips the Script Check, so a step outside the Mission Plan is ignored here and the count stands in.
            mission.PlanStep = PlanStepProblem(mission, plan) == null ? plan.PlanStep : null;
            mission.PendingSequence = plan;
            mission.PlanBaseline = _snapshot;
            PlanOffered(mission);
            mission.Stage = MissionStage.AwaitingSequenceApproval;
            DueTimeOffered(mission, plan);
            var card = AddCard(mission, LogKind.SequencePlan, plan.Name, PlanCardText(mission, plan), plan.Why);
            card.Script = plan.Script;
            card.CustomScript = plan.CustomScript;
            card.CustomScriptReason = plan.CustomScriptReason;
            PlanNews("Claudia has a Sequence Plan for your Approval.");
        }

        /// <summary>The plan on offer has its Due Time: the alarm for it, and whether the player has been told it is reached.</summary>
        private void DueTimeOffered(Mission mission, SequencePlan plan)
        {
            if (plan.DueTimeUt.HasValue)
            {
                // A stock Alarm Clock alarm at the Due Time; the mod itself never warps between sequences.
                if (plan.DueTimeUt.Value > _ut) _commands.Add(new SetDueTimeAlarm(mission.Id, plan.DueTimeUt.Value, "Claudia: " + plan.Name));
                mission.DueNotified = plan.DueTimeUt.Value <= _ut;
            }
            else mission.DueNotified = false;
        }

        private void Debriefed(Mission mission, MissionReply reply)
        {
            var debrief = reply.Debrief;
            // The Debrief of a manual stretch ends it: a revised Mission Plan follows, not the next Sequence Plan.
            var handedBack = mission.HandBack;
            mission.HandBack = false;
            mission.ManualPlanned = false;
            mission.ManualOrigin = "";
            mission.HandBackMessage = "";
            // A standing crossing rules out Nominal (story 107), whatever Claudia rated it; a recovered one doesn't (#572). The card lists them all.
            var crossings = CrossingsText(mission);
            mission.Crossings.Clear();
            if (MissionPrompts.RulesOutNominal(crossings) && debrief.Outcome == "Nominal")
            {
                debrief.Outcome = "Off-Nominal";
                Note(LogKind.Notice, "Rated Off-Nominal, not Nominal: a Telemetry Check was crossed during the run.");
            }
            // Ticking twice is harmless: a Debrief already in a log rebuilt from the transcript has ticked them once.
            GoalSteps.Tick(mission.GoalSteps, debrief.GoalStepsDone);
            var rebuiltLog = mission.SuppressCards;
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome + TakeStopReason(mission),
                MissionCards.DebriefBody(debrief, crossings), debrief.Why);
            // Only this card is already in a log rebuilt from the transcript; what follows the Debrief is new.
            mission.SuppressCards = false;
            entry.Outcome = debrief.Outcome;
            entry.Debrief = DetailOf(mission, debrief);
            // A card rebuilt from the transcript knows nothing of the crossings: this one replaces it.
            var rebuilt = rebuiltLog ? mission.Log.LastOrDefault(e => e.Kind == LogKind.Debrief) : null;
            if (rebuilt != null)
            {
                rebuilt.Title = entry.Title;
                rebuilt.Text = entry.Text;
                rebuilt.Outcome = entry.Outcome;
                rebuilt.Debrief = entry.Debrief;
            }
            HerNews("Claudia's Debrief: " + debrief.Outcome + ".");
            if (debrief.GoalReached)
            {
                mission.Stage = MissionStage.Succeeded;
                Note(LogKind.Notice, "Mission Goal met: the Mission is over.");
                RecordFinished(mission);
                return;
            }
            if (debrief.Outcome == "Failed")
            {
                FailedDebrief(mission, debrief);
                return;
            }
            if (handedBack)
            {
                RevisePlanAfterHandBack(mission);
                return;
            }
            mission.Stage = MissionStage.Sequencing;
            mission.SequenceCounted = false;
            if (debrief.EvaScience.Length > 0)
            {
                // Claudia doesn't go on EVA: the Mission waits for the player to do it by hand. The next Sequence Plan
                // is owed (and asked for on Resume), so it survives a save, a load or a quit while the player is out.
                mission.EvaScience = debrief.EvaScience;
                mission.Owed = Owed.SequencePlan;
                Pause(mission, EvaPauseReason(debrief.EvaScience));
                return;
            }
            BeginAfterDebrief(mission, debrief);
        }

        private static string EvaPauseReason(string eva)
        {
            return "Over to you: " + eva + " Claudia doesn't go on EVA, so do it by hand, get back aboard the craft, then Resume.";
        }

        private void Pause(Mission mission, string reason)
        {
            // A tool loop never survives a Pause: Retry or Resume sends the turn from scratch.
            // Except the one whose final reply at the step limit was refused off_schema (#668): Retry sends that request again.
            if (mission.Pending != null && !mission.InFlight && !mission.Pending.KeepLoop) DiscardLoop(mission, mission.Pending);
            if (mission.Stage != MissionStage.Paused) mission.StageBeforePause = mission.Stage;
            mission.Stage = MissionStage.Paused;
            mission.PauseCause = PauseCause.Other;
            mission.PauseReason = reason;
            Event(mission, "paused", reason);
            Note(LogKind.Notice, reason);
            NeedsPlayer("Claudia's Mission is Paused.");
        }

        /// <summary>
        /// Retry of a turn whose final reply at the step limit was refused (#668): the tool loop is kept, the retry line is added, and
        /// the final request goes again with tool_choice none. A turn still owed a Mission Plan gets one step first on which only
        /// propose_mission_plan is answered (#571).
        /// </summary>
        private void KeepLoopRetry(Mission mission, Turn turn)
        {
            turn.KeepLoop = false;
            turn.Messages.Add(Message.SystemText(MissionPrompts.StepLimitRetryLine));
            var planOwed = TakesMissionPlan(turn.Purpose) && ProposedPlans.LastAccepted(turn.Messages) == null;
            turn.PlanOnly = planOwed;
            turn.ToolLimitSent = !planOwed;
            turn.RepeatStop = false;
            Event(mission, "tool_loop_kept", turn.Purpose + " after " + turn.ToolSteps + " steps; Retry sends the final request again" + (planOwed ? " after one propose_mission_plan step" : ""));
        }

        private void Retried()
        {
            var mission = _mission;
            if (mission != null && mission.Stage == MissionStage.UnsafeScript)
            {
                ScriptRetried(null, false);
                return;
            }
            if (mission == null || mission.Stage != MissionStage.Paused || mission.InFlight) return;
            // A retired model is only left by Choose model (or End Mission).
            if (mission.PauseCause == PauseCause.ModelGone) return;
            if (mission.UnreadableCostLogRestore != null)
            {
                // Spend is unknown until the log reads; then the load goes ahead as it would have.
                if (MissionStore.ReadCostTotals(mission.Folder).Unreadable)
                {
                    Note(LogKind.Notice, CostLogUnreadableReason);
                    return;
                }
                Reinstate(mission.UnreadableCostLogRestore, mission.UnreadableCostLogResumeAfterQuit);
                return;
            }
            var refusal = ResumeRefusal(mission);
            if (refusal.Length > 0)
            {
                Note(LogKind.Notice, refusal);
                return;
            }
            mission.PausedCannotFly = false;
            CraftCheckOnResume(mission);
            if (mission.EvaScience.Length > 0)
            {
                Note(LogKind.Player, "EVA science done by hand: " + mission.EvaScience);
                mission.LoadNotice = (mission.LoadNotice != null ? mission.LoadNotice + "\n\n" : "")
                    + "The player has done the EVA science you asked for by hand (quoted data): " + MissionPrompts.Quoted(mission.EvaScience)
                    + ". Treat the Game Snapshot as the truth about the craft and carry on with the next Sequence Plan.";
                mission.EvaScience = "";
            }
            mission.Stage = mission.StageBeforePause;
            mission.PauseReason = "";
            mission.PauseCause = PauseCause.Other;
            mission.PausedForCap = false;
            mission.AwaitingResume = false;
            if (mission.Pending != null)
            {
                if (mission.Pending.Messages == null) ApplyLoadNotice(mission, mission.Pending);
                else if (mission.Pending.KeepLoop) KeepLoopRetry(mission, mission.Pending);
                Send(mission, mission.Pending);
            }
            else SendQueued(mission);
        }

        private void WriteTranscript(Mission mission)
        {
            try
            {
                MissionFolder.WriteTranscript(mission.Folder, mission.Id, mission.Model, mission.System, mission.Conversation,
                    mission.CompactionBlock, mission.CompactedMessages, mission.ContextTokens, mission.LastMessageId);
            }
            catch (Exception e) when (IsFileError(e))
            {
                Note(LogKind.Notice, "Couldn't write the Mission transcript: " + e.Message);
            }
        }

        private void Capture()
        {
            if (_snapshot == null || _saveDirectory == null) return;
            try
            {
                var path = MissionFolder.WriteSnapshot(_saveDirectory, _snapshot);
                if (_mission != null) Note(LogKind.Notice, "Game Snapshot saved to " + path);
            }
            catch (Exception e) when (IsFileError(e))
            {
                if (_mission != null) Note(LogKind.Notice, "Couldn't save the Game Snapshot: " + e.Message);
            }
        }

        private static bool IsFileError(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException;
        }

        private void NeedsPlayer(string text)
        {
            _commands.Add(new PostScreenMessage(text));
        }

        // ---- Spending Cap and cost ----

        /// <summary>Settles the request's pending charge by <see cref="RequestCharge.Settle"/>, the rules the Craft Check shares.</summary>
        private void Settle(Mission mission, Turn turn, StreamResult result)
        {
            var settlement = RequestCharge.Settle(_config.Prices, mission.PendingCharge, result, mission.Model, mission.PendingInput);
            mission.PendingCharge = 0m;
            mission.PendingInput = 0m;
            mission.Settled = settlement.Amount;
            mission.Spent += settlement.Spent;
            mission.Held += settlement.Held;
            mission.Settlement = settlement.Kind;
            mission.Outcome = settlement.Outcome ?? PaidOutcome(turn, result.Message);
        }

        /// <summary>How a paid reply ended, for the cost log: completed, or refused, cut off or off the Mission reply schema.</summary>
        private static string PaidOutcome(Turn turn, AssistantMessage message)
        {
            if (message.StopReason == StopReason.Refusal) return "refused";
            if (message.StopReason == StopReason.MaxTokens) return "cut_off";
            if (message.StopReason == StopReason.ToolUse) return "tool_use";
            if (turn.Purpose == Purpose.Margin) return CraftCheckReply.Parse(message.Text) == null ? "off_schema" : "completed";
            var reply = ReadReply(turn, message);
            return reply == null || Mismatch(turn.Purpose, reply) != null ? "off_schema" : "completed";
        }

        /// <summary>After a request settled: the 80% warning (once), and Paused at the cap.</summary>
        private void AfterCharge(Mission mission)
        {
            if (MissionOver) return;
            if (!mission.Warned && mission.Committed >= mission.SpendingCap * 0.8m)
            {
                mission.Warned = true;
                var text = "80% of the Spending Cap is spent: $" + Dollars(mission.Committed) + " of $" + Dollars(mission.SpendingCap) + ". Raise cap in time if the Mission needs more.";
                Note(LogKind.Notice, text);
                NeedsPlayer(text);
            }
            if (mission.Committed < mission.SpendingCap || mission.PausedForCap || mission.WaitingForCap) return;
            // While Running the Mission is never Paused (story 125): the script finishes, a live call waits at the cap,
            // and the Debrief Pauses for Raise cap (story 124).
            if (IsRunning(mission) || mission.Blackout) return;
            var reason = "The Spending Cap is reached ($" + Dollars(mission.Committed) + " of $" + Dollars(mission.SpendingCap) + "). Raise cap to carry on, or End Mission.";
            if (mission.Stage == MissionStage.Paused) mission.PauseReason += " " + reason;
            else Pause(mission, reason);
            mission.PausedForCap = true;
        }

        private void PauseForCap(Mission mission, string reason)
        {
            Pause(mission, reason);
            mission.PausedForCap = true;
        }

        private void Raised(decimal cap)
        {
            var mission = _mission;
            if (MissionOver || cap <= mission.SpendingCap) return;
            mission.SpendingCap = cap;
            mission.Warned = mission.Committed >= cap * 0.8m;
            Note(LogKind.Notice, "Spending Cap raised to $" + Dollars(cap) + ".");
            LogCost(mission, "cap_raised", cap, "");
            WriteInfo(mission);
            CarryOnIfCapCovers(mission);
            if (mission.WaitingForCap && mission.Pending != null && !mission.InFlight && mission.Stage != MissionStage.Paused)
                Send(mission, mission.Pending);
        }

        /// <summary>A Mission Paused at the Spending Cap whose cap now covers what it has spent sends the waiting request, as a raise does (also after a load that finds a cap raised since the save).</summary>
        private void CarryOnIfCapCovers(Mission mission)
        {
            if (mission.Stage != MissionStage.Paused || !mission.PausedForCap || mission.Committed >= mission.SpendingCap) return;
            if (mission.EvaScience.Length == 0) Retried();
            else
            {
                // The cap was only half of what held the Mission: it still waits for the player's EVA.
                mission.PausedForCap = false;
                mission.PauseReason = EvaPauseReason(mission.EvaScience);
            }
        }

        /// <summary>A cap-paused Mission whose carrying on was refused (the wrong scene or vessel) goes ahead once the scene allows it, quietly until then (#220).</summary>
        private void CarryOnWhenSceneAllows()
        {
            var mission = _mission;
            if (mission == null || mission.Stage != MissionStage.Paused || !mission.PausedForCap || mission.InFlight) return;
            if (mission.Committed >= mission.SpendingCap || mission.UnreadableCostLogRestore != null || ResumeRefusal(mission).Length > 0) return;
            CarryOnIfCapCovers(mission);
        }

        private void LogCost(Mission mission, string kind, decimal amount, string outcome, decimal? input = null)
        {
            try
            {
                MissionFolder.AppendCostEvent(mission.Folder, kind, mission.Requests, mission.Model, amount, outcome,
                    mission.Spent, mission.Held, mission.SpendingCap, input);
            }
            catch (Exception e) when (IsFileError(e))
            {
                // The charge still counts in memory; only the log line is missing.
            }
        }

        private static string Dollars(decimal value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ---- View ----

        private LogEntry Note(LogKind kind, string text)
        {
            var entry = new LogEntry { Kind = kind, Text = text ?? "" };
            QueueIfBlackout(_mission, entry);
            if (_mission != null) _mission.Log.Add(entry);
            return entry;
        }

        private static LogEntry AddCard(Mission mission, LogKind kind, string title, string body, string why)
        {
            var entry = new LogEntry { Kind = kind, Title = title, Text = body, Why = why ?? "" };
            if (mission.SuppressCards) return entry;
            QueueIfBlackout(mission, entry);
            mission.Log.Add(entry);
            return entry;
        }

        private static string MetresPerSecond(double value)
        {
            return MissionCards.MetresPerSecond(value);
        }

        private void Rebuild()
        {
            SwitchIfReady();
            var mission = _mission;
            var view = new MissionView
            {
                Version = _view.Version + 1,
                Ut = _ut,
                CanCaptureSnapshot = _snapshot != null && _saveDirectory != null,
                CanEndMission = !MissionOver,
                CanPauseMission = !MissionOver && _switch == null,
                SwitchQueued = _switch != null,
                Missions = Missions(),
            };
            view.StartForm.Models = _config.Models.ToList();
            view.StartForm.DefaultModel = _config.DefaultModel;
            view.StartForm.DefaultSpendingCap = _config.DefaultSpendingCap;
            foreach (var model in view.StartForm.Models)
                view.StartForm.Estimates[model] = Math.Round(_config.Prices.EstimateMission(model), 2);
            view.StartForm.CanStart = _saveDirectory != null && MissionOver;
            view.StartForm.DefaultSettings = MissionSettingsValues.Defaults(_config);
            view.PhysicsWarpAllowed = _config.PhysicsWarp;
            view.StartForm.DefaultRemote = _config.DefaultRemote;
            view.StartForm.Refusal = _startRefusal;
            view.StartForm.Targets = CurrentTargetFacts();
            // Worked out only for a goal the form has pinned, and only while it can start: a search is not for every keystroke or frame.
            if (view.StartForm.CanStart && _form != null) view.StartForm.Windows = OfferFor((_form.Goal ?? "").Trim(), _snapshot);
            var formWindow = _form == null ? WindowPick.None : WindowChoice.Held(_form.Window, view.StartForm.Windows);
            view.StartForm.WindowPickMissing = view.StartForm.Windows.Needed && formWindow.IsNone;
            _craftCheck.Populate(view.CraftCheck, _snapshot, _form == null ? null : _form.Goal,
                _form == null ? null : ChooseTarget(_form.Goal, _form.Target).Target, _form != null, formWindow.Key);

            if (mission != null)
            {
                view.Stage = mission.Stage;
                view.MissionId = mission.Id;
                view.Goal = mission.Goal;
                if (!MissionOver && mission.Target != null)
                {
                    view.TargetVesselId = mission.Target.Id;
                    view.TargetVesselName = mission.Target.Name;
                }
                view.GoalSteps = GoalSteps.Copy(mission.GoalSteps);
                view.Model = mission.Model;
                view.SpendingCap = mission.SpendingCap;
                view.Spent = mission.Spent;
                view.HeldCharges = mission.Held;
                view.PendingCharge = mission.PendingCharge;
                view.CapWarning = mission.Committed >= mission.SpendingCap * 0.8m;
                view.LatestLine = mission.LatestLine;
                // A Paused Mission waits on the player, not on Claude: the line clears on Pause.
                view.Busy = mission.InFlight && mission.Stage != MissionStage.Paused;
                view.Activity = view.Busy ? mission.Activity : "";
                view.PauseReason = mission.PauseReason;
                view.Remote = mission.Remote;
                view.AutoMode = mission.AutoMode;
                view.AutoModeStop = AutoModeStopLine(mission);
                view.CapRoomStop = view.AutoModeStop.Length > 0 && view.AutoModeStop == mission.AutoModeRoomStop ? view.AutoModeStop : "";
                view.EvaScience = mission.EvaScience;
                view.ConfirmPrompt = mission.ConfirmPrompt;
                view.CapWait = mission.CapWaitText;
                // Entries are stamped with the UT of the rebuild that first shows them, for "3h 5m ago" in the log.
                foreach (var entry in mission.Log)
                    if (entry.Ut <= 0) entry.Ut = _ut;
                view.Log = mission.Log.ToList();
                if (mission.Plan != null)
                {
                    view.PlanSequences = mission.Plan.Sequences.Select(s => s.Name).ToList();
                    view.PlanSequencesManual = mission.Plan.Sequences.Select(s => s.Manual).ToList();
                }
                view.CurrentSequence = Math.Min(StepsBegun(mission), view.PlanSequences.Count) - 1;
                view.PlanLegs = mission.Plan != null ? mission.Plan.Sequences : null;
                view.ProcessorPartId = mission.ProcessorPartId;
                view.ChuteGroups = mission.Chutes;
                view.RunningChild = mission.RunningChild;
                view.ArrivalPlane = mission.ArrivalPlane;
                view.PickedSite = mission.PickedSite;
                if (mission.Stage == MissionStage.Running && mission.PendingSequence != null)
                {
                    view.RunStatus = "Running: " + mission.PendingSequence.Name + (mission.RunningChild.Length > 0 ? " on " + mission.RunningChild : "");
                    view.RunProgress = Progress(mission);
                }
                // Under Auto Mode a recommendation is Auto Mode's to take, never the player's to answer: no Abort attention.
                view.AbortRecommended = mission.AutoMode ? "" : mission.AbortRecommendation;
                // While Running a question queues behind whatever is in hand, so typing stays open.
                view.CanType = (!mission.InFlight || mission.Stage == MissionStage.Running) && (mission.Stage != MissionStage.Paused || mission.PauseCause == PauseCause.Refusal || mission.PauseCause == PauseCause.CutOff)
                    && mission.Stage != MissionStage.WaitingForFlight && mission.Stage != MissionStage.Lost
                    && mission.Stage != MissionStage.AwaitingReducedGoal;

                view.LeavingFlightNeedsConfirmation = IsRunning(mission);
                var cannotLeave = CannotLeaveCraft(mission);
                view.FarSwitchLeavesAtCheckpoint = cannotLeave == "";
                view.FarSwitchWarning = cannotLeave ?? "";
                view.CraftVesselId = FollowsCraft(mission) ? mission.VesselId : "";
                NextPatchWatchView(mission, view);
                view.ChildVessels = ChildVesselViews(mission);
                view.RunningChildVesselId = RunningChildVesselId(mission);
                view.Settings = MissionSettingsValues.FromDictionary(mission.Settings, MissionSettingsValues.Defaults(_config));
                view.MarginPercent = _config.CraftCheckMarginPercent;
                view.CanEditSettings = CanEditSettings;
                view.PhysicsWarpAllowed = view.Settings.PhysicsWarpOn;
                var due = PendingDueTime;
                if (due.HasValue)
                {
                    view.DueTime = new DueTimeView
                    {
                        HasDueTime = true,
                        Ut = due.Value,
                        Text = KerbinTime.FromNow(due.Value, _ut),
                        Date = KerbinTime.Date(due.Value),
                        SecondsLeft = Math.Max(0, due.Value - _ut),
                        Reached = _ut >= due.Value,
                        VesselName = mission.VesselName,
                        WaitsFor = mission.WindowPlanet.Length > 0 ? WindowWaits.WaitsFor(mission.WindowPlanet) : CoastPlanToken(mission.PendingSequence) != null ? CoastCheckpoints.WaitsFor : "",
                    };
                }
                view.Locks = LocksNow();
                view.ManualControlAllowed = ManualAllowed;
                view.ManualControlUnavailable = ManualUnavailableBecause();
                view.LinkLossTakeBackIn = LinkLossTakeBackIn();
                view.HandBackUnavailable = HandBackUnavailableBecause();
                view.PlanStale = StaleBecause(mission);
                view.PlanStaleTarget = view.PlanStale.Length > 0 && mission.TargetChange.Length > 0;
                view.PlanDeclined = mission.Declined;
                view.WindowPick = mission.WindowPick;
                view.WindowPickNeeded = WindowPickMissing(mission);
                view.Windows = MissionOffer(mission);
                view.TargetNeeded = TargetNeeded(mission);
                view.CanChangeTarget = CanChangeTarget;
                if (mission.Stage == MissionStage.ManualControl)
                    view.QuickReplies = new List<QuickReply> { QuickReply.HandBack };
                else if (mission.Stage == MissionStage.Running)
                {
                    // Abort once the run can still be stopped (not after the mod already stopped it).
                    var replies = new List<QuickReply>();
                    if (mission.RunId.HasValue && mission.StopReason == null) replies.Add(QuickReply.Abort);
                    replies.Add(QuickReply.AskQuestion);
                    if (CanTakeManual && ManualAllowed) replies.Add(QuickReply.ManualControl);
                    view.QuickReplies = replies;
                }
                else if (mission.Stage == MissionStage.Paused && !mission.InFlight)
                    view.QuickReplies = PausedReplies(mission) ?? (mission.PausedForCap
                        ? new List<QuickReply> { QuickReply.RaiseCap, QuickReply.EndMission }
                        : new List<QuickReply> { mission.AwaitingResume || mission.EvaScience.Length > 0 || mission.PausedCannotFly ? QuickReply.Resume : QuickReply.Retry });
                else if (mission.Stage == MissionStage.UnsafeScript && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Retry, QuickReply.RetryWithNote, QuickReply.Replan };
                else if (mission.Stage == MissionStage.AwaitingReducedGoal && !mission.InFlight)
                    view.QuickReplies = mission.ReducedGoal.Length > 0
                        ? new List<QuickReply> { QuickReply.AcceptReducedGoal, QuickReply.EndMission }
                        : new List<QuickReply> { QuickReply.RetryWithNote, QuickReply.EndMission };
                else if (mission.Declined && !mission.InFlight)
                    view.QuickReplies = new List<QuickReply> { QuickReply.ChangeSomething, QuickReply.EndMission };
                else if (mission.AwaitingApproval && !mission.InFlight && view.PlanStale.Length > 0)
                    view.QuickReplies = new List<QuickReply> { QuickReply.Replan, QuickReply.ChangeSomething, QuickReply.AskQuestion };
                else if (mission.AwaitingApproval && !mission.InFlight)
                {
                    // Before the Due Time: Warp to Due Time instead of Approve. At it, away from the vessel: Go to the vessel first.
                    var first = view.DueTime.HasDueTime && !view.DueTime.Reached ? (QuickReply?)QuickReply.WarpToDueTime
                        : GoToOffered ? (QuickReply?)QuickReply.GoToVessel : null;
                    view.QuickReplies = mission.ConfirmPrompt != null
                        ? new List<QuickReply> { QuickReply.ConfirmApproval, QuickReply.NotYet }
                        : new List<QuickReply> { first ?? QuickReply.Approve, QuickReply.ChangeSomething, QuickReply.AskQuestion };
                    // No pick, no Approve (#686): the row draws it greyed out, and the choice is the card above it.
                    if (view.WindowPickNeeded) view.QuickReplies.Remove(QuickReply.Approve);
                    // A coast checkpoint's Due Time is soft: with the player in the craft, one Approve warps there too (#775).
                    if (mission.ConfirmPrompt == null && first == QuickReply.WarpToDueTime && mission.PendingSequence != null && SoftDueTime(mission, mission.PendingSequence))
                        view.QuickReplies.Insert(0, QuickReply.Approve);
                }
                // Paused mid-run, the script still flies: Abort stays on offer.
                if (mission.Stage == MissionStage.Paused && IsRunning(mission) && mission.RunId.HasValue && mission.StopReason == null)
                    view.QuickReplies = new[] { QuickReply.Abort }.Concat(view.QuickReplies).ToList();
                if (mission.WaitingForCap && !view.QuickReplies.Contains(QuickReply.RaiseCap))
                    view.QuickReplies = view.QuickReplies.Concat(new[] { QuickReply.RaiseCap }).ToList();
                ShowBlackout(view, mission);
            }
            // The Craft Check is a request too, before a Mission and when one resumes.
            if (!view.Busy && view.CraftCheck.State == CraftCheckState.Running)
            {
                view.Busy = true;
                view.Activity = _craftCheck.ToolActivity.Length > 0 ? _craftCheck.ToolActivity : _craftCheck.SecondLooking ? SecondLook.Activity : "checking the craft";
            }
            _view = view;
        }

        internal static string Describe(StreamResult result)
        {
            switch (result.Failure)
            {
                case StreamFailure.Unauthorized:
                    return "Anthropic rejected the API key. Check it in the Claudia Kerman settings, then Retry.";
                case StreamFailure.LowCredit:
                    return "Your Anthropic credit balance is too low. Top it up, then Retry.";
                case StreamFailure.NoNetwork:
                    return "No network: Claudia couldn't be reached. Retry when you're back online.";
                case StreamFailure.None:
                    return "Claudia's reply was cancelled.";
                case StreamFailure.HttpStatus:
                    return "Claudia couldn't answer (HTTP " + result.HttpStatus.ToString(CultureInfo.InvariantCulture)
                        + (result.ErrorMessage != null ? ": " + result.ErrorMessage : "") + ").";
                default:
                    return "Claudia's reply broke off. Retry in a moment.";
            }
        }
    }
}
