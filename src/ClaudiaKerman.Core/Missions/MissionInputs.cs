// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Kos;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// Everything the adapter tells <see cref="MissionControl"/>: player actions, Game Snapshots, game
    /// events, kOS run events and UT. Plain data, built on the main thread.
    /// </summary>
    public abstract class MissionInput
    {
    }

    // ---- Player actions ----

    /// <summary>The Mission start form: the Mission Goal, Spending Cap (USD) and model, which is then locked for the Mission.</summary>
    public sealed class StartMission : MissionInput
    {
        public readonly string Goal;
        public readonly decimal SpendingCap;
        public readonly string Model;
        /// <summary>A remote Mission: Claudia stays at Mission Control and flies a craft with no one aboard for her. False is crewed (she flies aboard).</summary>
        public readonly bool Remote;

        /// <summary>The Mission Settings chosen in the form; null takes the defaults from the player config.</summary>
        public readonly MissionSettingsValues Settings;
        /// <summary>The form's Target Vessel pick (#643); Auto lets the mod choose. Never saved as a default.</summary>
        public readonly TargetPick Target;

        public StartMission(string goal, decimal spendingCap, string model, bool remote = false, TargetPick target = null)
        {
            Goal = goal;
            SpendingCap = spendingCap;
            Model = model;
            Remote = remote;
            Target = target ?? TargetPick.Auto;
        }

        public StartMission(string goal, decimal spendingCap, string model, MissionSettingsValues settings, bool remote = false, TargetPick target = null)
            : this(goal, spendingCap, model, remote, target)
        {
            Settings = settings;
        }
    }

    /// <summary>Check the craft now, in the editor or in Flight, against the Mission Goal as typed so far.</summary>
    public sealed class RunCraftCheck : MissionInput
    {
        public readonly string Goal;
        /// <summary>The Spending Cap the player has entered; the check is sent only if its worst case fits.</summary>
        public readonly decimal SpendingCap;
        public readonly string Model;
        /// <summary>The check is for a remote Mission (see <see cref="StartMission.Remote"/>); false is crewed.</summary>
        public readonly bool Remote;
        /// <summary>The form's Target Vessel pick (#643); the check judges the craft against the target it chooses.</summary>
        public readonly TargetPick Target;

        public RunCraftCheck(string goal, decimal spendingCap, string model, bool remote = false, TargetPick target = null)
        {
            Goal = goal;
            SpendingCap = spendingCap;
            Model = model;
            Remote = remote;
            Target = target ?? TargetPick.Auto;
        }
    }

    /// <summary>
    /// The start form's Mission Goal text and Target Vessel pick as they stand now (#664): UI state only, never a request to Claude.
    /// The Craft Check card shows its result as stale when either differs from what the check ran on.
    /// </summary>
    public sealed class StartFormChanged : MissionInput
    {
        public readonly string Goal;
        public readonly TargetPick Target;

        public StartFormChanged(string goal, TargetPick target = null)
        {
            Goal = goal;
            Target = target ?? TargetPick.Auto;
        }
    }

    /// <summary>The player accepts a Craft Check Warning knowingly; Start Mission opens once every Warning is accepted.</summary>
    public sealed class AcknowledgeWarning : MissionInput
    {
        public readonly string Code;
        public AcknowledgeWarning(string code) { Code = code; }
    }

    /// <summary>The player's choice of Claudia's kOS processor, by part; null goes back to the one nearest the root.</summary>
    public sealed class ChooseProcessor : MissionInput
    {
        public readonly uint? PartId;
        public ChooseProcessor(uint? partId) { PartId = partId; }
    }

    /// <summary>The Approve button: an unambiguous Approval of the pending Mission Plan or Sequence Plan.</summary>
    public sealed class Approve : MissionInput
    {
    }

    /// <summary>"Change something", with what the player wants changed.</summary>
    public sealed class ChangeSomething : MissionInput
    {
        public readonly string Text;
        public ChangeSomething(string text) { Text = text; }
    }

    /// <summary>"Ask a question", or a question at any time; it never changes the Mission's state.</summary>
    public sealed class AskQuestion : MissionInput
    {
        public readonly string Text;
        public AskQuestion(string text) { Text = text; }
    }

    /// <summary>Free text typed while a plan awaits Approval; Claude classifies it.</summary>
    public sealed class Reply : MissionInput
    {
        public readonly string Text;
        public Reply(string text) { Text = text; }
    }

    /// <summary>The player's answer to "Approve the pending plan?" after a typed reply.</summary>
    public sealed class ConfirmApproval : MissionInput
    {
        public readonly bool Yes;
        public ConfirmApproval(bool yes) { Yes = yes; }
    }

    /// <summary>Raise cap, from a Mission Paused at its Spending Cap: sets a higher Spending Cap (USD) and resumes. A cap that is not higher is ignored.</summary>
    public sealed class RaiseCap : MissionInput
    {
        public readonly decimal SpendingCap;
        public RaiseCap(decimal spendingCap) { SpendingCap = spendingCap; }
    }

    /// <summary>Sends the turn that failed again, unchanged; after "Couldn't produce a safe script", asks for the script again.</summary>
    public sealed class Retry : MissionInput
    {
    }

    /// <summary>Choose model, from a Mission Paused because its model is retired or not found: the Mission carries on with <see cref="Model"/>.</summary>
    public sealed class ChooseModel : MissionInput
    {
        public readonly string Model;
        public ChooseModel(string model) { Model = model; }
    }

    /// <summary>After "Couldn't produce a safe script" or a refusal: Claudia tries again, with the player's note.</summary>
    public sealed class RetryWithNote : MissionInput
    {
        public readonly string Note;
        public RetryWithNote(string note) { Note = note; }
    }

    /// <summary>After "Couldn't produce a safe script": Claudia proposes a different Sequence Plan.</summary>
    public sealed class Replan : MissionInput
    {
    }

    /// <summary>The Mission window's Choose or Clear for the Target Vessel between sequences (#661): a vessel from the list, or Cleared.</summary>
    public sealed class ChangeTarget : MissionInput
    {
        public readonly TargetPick Pick;
        public ChangeTarget(TargetPick pick) { Pick = pick; }
    }

    /// <summary>
    /// End Mission, after the player confirmed: cancels any request in flight, aborts a running script, marks
    /// the Mission ended in its folder (folder and transcript are kept) and frees the start form.
    /// </summary>
    public sealed class EndMission : MissionInput
    {
    }

    /// <summary>
    /// Clear on a finished Mission (Succeeded, Ended or Lost): it stops being the current Mission, so the window starts fresh.
    /// The folder and transcript are kept and marked ended, so a load doesn't bring it back. Does nothing for an active Mission.
    /// </summary>
    public sealed class DismissMission : MissionInput
    {
    }

    /// <summary>
    /// The Manual Control button. A running script is aborted and the craft made safe first; the controls are then
    /// the player's until <see cref="HandBack"/>. Refused without a link or other non-Tourist crew aboard.
    /// </summary>
    public sealed class TakeManualControl : MissionInput
    {
    }

    /// <summary>
    /// The adapter saw attitude, translation or throttle input held for <see cref="Seconds"/> without a break.
    /// The core decides: held over <c>PlayerConfig.ManualHoldSeconds</c> starts Manual Control as the button does.
    /// </summary>
    public sealed class ManualInputHeld : MissionInput
    {
        public readonly double Seconds;
        public ManualInputHeld(double seconds) { Seconds = seconds; }
    }

    /// <summary>
    /// Hand back to Claudia, with an optional message about what the player did. Claudia Debriefs the stretch (judged for
    /// a planned stretch, a plain account for Manual Control nobody planned) and revises the Mission Plan for Approval.
    /// </summary>
    public sealed class HandBack : MissionInput
    {
        public readonly string Message;
        public HandBack(string message) { Message = message; }
    }

    /// <summary>
    /// The Abort button while Running: the script is stopped and the craft made safe through the abort path, and the
    /// mod records that the player aborted it (the run's Debrief says so). Claudia may recommend it; she never does it.
    /// </summary>
    public sealed class AbortSequence : MissionInput
    {
    }

    /// <summary>The debug capture button: writes the latest Game Snapshot to the save's <c>ClaudiaKerman/snapshots</c> folder.</summary>
    public sealed class CaptureSnapshot : MissionInput
    {
    }

    // ---- Game events ----

    public enum GameScene
    {
        /// <summary>The VAB or SPH.</summary>
        Editor,
        Flight,
        /// <summary>Anywhere else (KSC, Tracking Station, main menu).</summary>
        Other,
    }

    /// <summary>A save was loaded; Mission folders go under <c>&lt;SaveDirectory&gt;/ClaudiaKerman/</c>.</summary>
    public sealed class SaveLoaded : MissionInput
    {
        public readonly string SaveDirectory;
        public SaveLoaded(string saveDirectory) { SaveDirectory = saveDirectory; }
    }

    public sealed class SceneChanged : MissionInput
    {
        public readonly GameScene Scene;
        public SceneChanged(GameScene scene) { Scene = scene; }
    }

    /// <summary>Game time, in seconds of UT.</summary>
    public sealed class UtAdvanced : MissionInput
    {
        public readonly double Ut;
        public UtAdvanced(double ut) { Ut = ut; }
    }

    /// <summary>
    /// Claudia was killed. Only a crewed Mission cares (she was aboard the craft): it ends as Lost. A remote
    /// Mission goes on, since she was never aboard. A crewed Mission is Lost only if she died aboard its craft:
    /// <see cref="VesselId"/> names the vessel she was on ("" when the adapter could not tell, which counts as aboard).
    /// <see cref="Traced"/> is the innermost open block of the run in progress that has traced its state (#375), which the adapter
    /// reads off the run's <see cref="ClaudiaKerman.Core.Kos.KosRun.InnermostTraced"/>; null when no block had. The input carries it
    /// because the aborted run's own outcome arrives after the Mission is closed.
    /// </summary>
    public sealed class ClaudiaKilled : MissionInput
    {
        public readonly string VesselId;
        public readonly ClaudiaKerman.Core.Kos.TracedBlock Traced;
        public ClaudiaKilled(string vesselId = "", ClaudiaKerman.Core.Kos.TracedBlock traced = null) { VesselId = vesselId ?? ""; Traced = traced; }
    }

    // ---- kOS run events ----

    /// <summary>The kOS bridge would not start the script (busy or missing processor, kOS incompatible...).</summary>
    public sealed class KosRunRefused : MissionInput
    {
        public readonly string Reason;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        public KosRunRefused(string reason, int runId) { Reason = reason; RunId = runId; }
    }

    /// <summary>The script started by <see cref="StartScript"/> ended: its report, or why it didn't report, and any error text.</summary>
    public sealed class KosRunEnded : MissionInput
    {
        public readonly RunOutcome Outcome;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        /// <summary>UT when the run ended, 0 when the adapter didn't say: a Game Snapshot older than this is not trusted for the Debrief (#671).</summary>
        public readonly double Ut;
        public KosRunEnded(RunOutcome outcome, int runId, double ut = 0) { Outcome = outcome; RunId = runId; Ut = ut; }
    }

    /// <summary>A Building Block began in the run (<c>ADDONS:CLAUDIA:BLOCKSTART</c>, from the Core Library's <c>claudia_begin</c>).</summary>
    public sealed class KosBlockStarted : MissionInput
    {
        public readonly string Block;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        public KosBlockStarted(string block, int runId) { Block = block; RunId = runId; }
    }

    /// <summary>A Building Block returned its result lexicon (<c>ADDONS:CLAUDIA:BLOCKRESULT</c>, from <c>claudia_finish</c>); not the Script Result.</summary>
    public sealed class KosBlockEnded : MissionInput
    {
        public readonly string Block;
        public readonly System.Collections.Generic.IDictionary<string, object> Result;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        public KosBlockEnded(string block, System.Collections.Generic.IDictionary<string, object> result, int runId)
        {
            Block = block;
            Result = result;
            RunId = runId;
        }
    }

    /// <summary>A block switched RCS on or off (<c>ADDONS:CLAUDIA:RCSSWITCH</c>); <see cref="RcsSwitch"/> says what the lexicon holds.</summary>
    public sealed class KosRcsSwitched : MissionInput
    {
        public readonly System.Collections.Generic.IDictionary<string, object> Switch;
        public readonly double Ut;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        public KosRcsSwitched(System.Collections.Generic.IDictionary<string, object> values, double ut, int runId)
        {
            Switch = values;
            Ut = ut;
            RunId = runId;
        }
    }

    /// <summary>The Encounter Watch dropped the warp (#770): one line for the Mission's events and log. It never reaches the prompt.</summary>
    public sealed class EncounterWatchDropped : MissionInput
    {
        public readonly string Text;
        public EncounterWatchDropped(string text)
        {
            Text = text;
        }
    }

    /// <summary>The adapter moved Ship Systems at a transition (#756): one line for the Mission's events and log. It never reaches the prompt.</summary>
    public sealed class ShipSystemsMoved : MissionInput
    {
        public readonly string Text;
        public ShipSystemsMoved(string text)
        {
            Text = text;
        }
    }

    /// <summary>
    /// A runtime gate (ADR 0003) refused a call from the script started by <see cref="StartScript"/>: the bridge
    /// has stopped the program and made the craft safe, and its <see cref="KosRunEnded"/> follows.
    /// </summary>
    public sealed class KosRunBlocked : MissionInput
    {
        /// <summary>What the script tried, e.g. "KUNIVERSE:QUICKSAVE"; the Debrief reason is "blocked: " and this.</summary>
        public readonly string What;
        /// <summary>The <see cref="StartScript.RunId"/> this answers.</summary>
        public readonly int RunId;
        public KosRunBlocked(string what, int runId) { What = what; RunId = runId; }
    }
}
