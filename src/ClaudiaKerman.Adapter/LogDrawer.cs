// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Draws the transcript: the player's lines as bubbles, Claudia's under her name, notices as muted lines, and the
    /// Mission Plan, Sequence Plans and Debriefs as cards with a "Why" expander and, for a Sequence Plan, "Show script";
    /// then the Busy line. Given a send, it leads with the Craft Check as a card (its Warnings are checkboxes that acknowledge
    /// them) and ends with the checkbox that confirms a typed approval. Used by the <see cref="MissionPanel"/>, which the
    /// floating Mission window and the docked Capcom strip both draw. Plain text only:
    /// Claude's replies may contain '&lt;', which rich text would misread.
    /// </summary>
    public class LogDrawer
    {
        private readonly Action<MissionInput> _send;
        private Vector2 _scroll;
        private int _seenLogCount = -1;
        private GUIStyle _bold, _script;
        private readonly BannerDrawer _banner = new BannerDrawer();
        // Built from ClaudiaSkin's styles, so rebuilt when they are (a new skin or UI scale).
        private GUIStyle _stylesFor;

        // Expanded "why" and "Show script" per log entry.
        private readonly HashSet<LogEntry> _showWhy = new HashSet<LogEntry>();
        private readonly HashSet<LogEntry> _showScript = new HashSet<LogEntry>();
        // The formatted text and the scroll position of each shown script; formatting runs once per entry, not per frame.
        private readonly Dictionary<LogEntry, string> _formatted = new Dictionary<LogEntry, string>();
        private readonly Dictionary<LogEntry, Vector2> _scriptScroll = new Dictionary<LogEntry, Vector2>();

        // The Craft Check: a new result (a different set of findings) brings the transcript back to the top, where it is.
        private string _seenCheckResult = "";
        // When the current result first reached the view, for the card's "in -2m" time.
        private double _checkedAt;
        // The Contract check card last drawn: "", "open" or "folded"; a change brings the transcript to the bottom.
        private string _seenCard = "";
        // The approval checkbox appeared: the transcript follows it to the bottom.
        private bool _seenConfirm;
        // The width the entries get; the facts row wraps at it.
        private float _width;

        /// <summary>The Mission Goal is pinned (the card's notice says so); set before <see cref="Draw"/>.</summary>
        public bool GoalSet;
        /// <summary>The estimated cost of the Mission for the chosen model, for the facts row; null for none.</summary>
        public decimal? Estimate;
        /// <summary>The pending setup's model and type, which the welcome banner shows until there is a Mission; set before <see cref="Draw"/>.</summary>
        public string SetupModel = "";
        public bool SetupRemote;
        /// <summary>Builds the input Check again sends, or null when it can't be sent yet (no Mission Goal pinned).</summary>
        public Func<MissionInput> CheckAgain;
        /// <summary>Draws the Transfer Window choice (#686): under the Craft Check before Start, under the plan awaiting Approval in a Mission.</summary>
        public Action WindowChoice;
        // In Flight the Craft Check starts collapsed to one line so the Mission log gets the room; reset when Flight loads.
        private bool _checkCollapsed = true;

        /// <summary>The Mission log alone (the Capcom strip's drawer).</summary>
        public LogDrawer()
        {
        }

        /// <summary>The whole transcript, led by the Craft Check; <paramref name="send"/> takes its acknowledgements.</summary>
        public LogDrawer(Action<MissionInput> send)
        {
            _send = send;
        }

        /// <summary>Flight is loading: the Craft Check starts collapsed again.</summary>
        public void FlightLoading()
        {
            _checkCollapsed = true;
        }

        /// <summary>
        /// Draws the transcript in a scroll view that follows new entries; <paramref name="width"/> is the width the entries
        /// get (for the player's bubbles), and <paramref name="options"/> size the view.
        /// </summary>
        public void Draw(MissionView view, float width, params GUILayoutOption[] options)
        {
            Styles();
            _width = width;
            var confirming = view.ConfirmPrompt != null;
            if (view.Log.Count != _seenLogCount || (confirming && !_seenConfirm))
            {
                _seenLogCount = view.Log.Count;
                _scroll.y = float.MaxValue;
            }
            _seenConfirm = confirming;
            // #1022: Mission Control's Contract check card grows in at the bottom, like a new message: the transcript follows it.
            var card = _send != null ? view.ContractCheck : null;
            var cardState = card == null ? "" : card.Folded ? "folded" : "open";
            if (cardState != _seenCard)
            {
                _seenCard = cardState;
                if (card != null) _scroll.y = float.MaxValue;
            }
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, options);
            // The first item of a full transcript; drawing only, never a log entry.
            if (_send != null) _banner.Draw(view, width, SetupModel, SetupRemote, GoalSet);
            if (_send != null && (view.StartForm.CanStart || view.CraftCheck.State != CraftCheckState.None)) DrawCraftCheck(view);
            if (_send != null && view.StartForm.CanStart && WindowChoice != null) WindowChoice();
            if (_send != null && view.StartForm.CanStart && view.StartForm.Refusal.Length > 0) GUILayout.Label(view.StartForm.Refusal, ClaudiaSkin.DangerText);
            var hidden = _send != null ? HiddenBefore(view) : 0;
            if (hidden > 0) DrawHiddenNotice(view);
            for (var i = hidden; i < view.Log.Count; i++) DrawEntry(view.Log[i], view.Ut, width);
            if (_send != null && !view.StartForm.CanStart && WindowChoice != null) WindowChoice();
            if (_send != null && confirming) DrawApproval(view);
            if (card != null) DrawContractCheck(card, view.Ut);
            GUILayout.EndScrollView();
        }

        // ---- Clear (view only) ----

        // The count of log entries hidden before, per Mission: kept for the session only, and shared by the floating window and the docked panel.
        private readonly Dictionary<string, int> _hiddenBefore = new Dictionary<string, int>();

        /// <summary>How many entries of the Mission's log are hidden from the top; 0 when the transcript is whole.</summary>
        public int HiddenBefore(MissionView view)
        {
            int count;
            return view.MissionId.Length > 0 && _hiddenBefore.TryGetValue(view.MissionId, out count) ? Math.Min(count, view.Log.Count) : 0;
        }

        /// <summary>Hides the entries shown so far and brings the banner back into view. The Mission and Claude's context are untouched.</summary>
        public void HideShown(MissionView view)
        {
            if (view.MissionId.Length == 0) return;
            _hiddenBefore[view.MissionId] = view.Log.Count;
            _scroll.y = 0;
        }

        private void Show(MissionView view)
        {
            _hiddenBefore.Remove(view.MissionId);
        }

        // "Earlier messages hidden · Show", centered under the banner.
        private void DrawHiddenNotice(MissionView view)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(ClearRules.Hidden + " ·", ClaudiaSkin.Notice, GUILayout.ExpandWidth(false));
            if (ClaudiaSkin.TextButton(ClearRules.Show, GUILayout.ExpandWidth(false))) Show(view);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void Styles()
        {
            if (_bold != null && ReferenceEquals(_stylesFor, ClaudiaSkin.Body)) return;
            _stylesFor = ClaudiaSkin.Body;
            _bold = new GUIStyle(ClaudiaSkin.Body) { fontStyle = FontStyle.Bold };
            // Scripts keep their lines and scroll both ways in their own box.
            _script = new GUIStyle(ClaudiaSkin.TextField) { wordWrap = false, font = ClaudiaSkin.Mono.font, fontSize = ClaudiaSkin.Scaled(12) };
        }

        private void DrawEntry(LogEntry entry, double now, float width)
        {
            switch (entry.Kind)
            {
                case LogKind.Player:
                    DrawBubble(entry.Text, width);
                    return;
                case LogKind.Claudia:
                    GUILayout.Label("Claudia", ClaudiaSkin.ClaudiaName);
                    GUILayout.Label(entry.Text, ClaudiaSkin.Body);
                    if (entry.AnsweredBy.Length > 0) GUILayout.Label("[answered by " + entry.AnsweredBy + "]", ClaudiaSkin.Secondary);
                    return;
                case LogKind.Notice:
                    GUILayout.Label(entry.Text, ClaudiaSkin.Notice);
                    return;
            }
            DrawCard(entry, now);
        }

        // Right-aligned, as wide as the text up to 80% of the transcript.
        private static void DrawBubble(string text, float width)
        {
            var style = ClaudiaSkin.PlayerBubble;
            var bubbleWidth = Mathf.Min(style.CalcSize(new GUIContent(text)).x + 1, Mathf.Max(80f, width * 0.8f));
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(text, style, GUILayout.Width(bubbleWidth));
            GUILayout.EndHorizontal();
        }

        private void DrawCard(LogEntry entry, double now)
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginVertical(ClaudiaSkin.CardHeaderRow);
            GUILayout.BeginHorizontal();
            GUILayout.Label(KindText(entry.Kind), ClaudiaSkin.KindLabel, GUILayout.ExpandWidth(false));
            if (entry.Kind == LogKind.Debrief && entry.Outcome.Length > 0) ClaudiaSkin.Chip(entry.Outcome, OutcomeChip(entry.Outcome));
            GUILayout.FlexibleSpace();
            // When the card was made, from now; the date is on hover.
            if (entry.Ut > 0 && now > 0) GUILayout.Label(TimeContent(entry.Ut, now), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            if (entry.Title.Length > 0) GUILayout.Label(entry.Title, _bold);
            GUILayout.EndVertical();

            if (entry.CustomScript)
            {
                ClaudiaSkin.Chip("Custom Script", ClaudiaSkin.ChipKind.Info);
                if (entry.CustomScriptReason.Length > 0) GUILayout.Label(entry.CustomScriptReason, ClaudiaSkin.Secondary);
            }
            GUILayout.Label(entry.Text, ClaudiaSkin.Body);
            if (entry.Debrief != null) DrawDebrief(entry.Debrief);
            // Read before the buttons: a click mustn't add the expanded text to an event that was laid out without it.
            var showWhy = _showWhy.Contains(entry);
            var showScript = _showScript.Contains(entry);
            if (entry.Why.Length > 0 || entry.Script.Length > 0)
            {
                GUILayout.BeginHorizontal();
                if (entry.Why.Length > 0 && ClaudiaSkin.SmallButton(showWhy ? "Hide why" : "Why", GUILayout.ExpandWidth(false)))
                    Toggle(_showWhy, entry);
                if (entry.Script.Length > 0 && ClaudiaSkin.SmallButton(showScript ? "Hide script" : "Show script", GUILayout.ExpandWidth(false)))
                    Toggle(_showScript, entry);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (showWhy) GUILayout.Label("Why: " + entry.Why, ClaudiaSkin.Secondary);
            if (showScript) DrawScript(entry);
            ClaudiaSkin.EndCard();
        }

        // The Debrief's full picture under Claudia's account. The mod's sections come first (the margin bar, resources, the Margin
        // check with any margin recheck, the events, the science seen, the Mission Goal's count), so a lean Nominal Debrief (#581) reads complete; then
        // Claudia's outcome against target, margin and science, the follow-up on offer and a failed run's error text.
        private void DrawDebrief(DebriefDetail detail)
        {
            if (detail.Margin != null)
            {
                GUILayout.Label("Delta-v left vs the rest of the plan", _bold);
                GUILayout.Label(detail.Margin.Describe(), ClaudiaSkin.Body);
                DrawMarginBar(detail.Margin);
            }
            if (detail.ResourcesLeft.Count > 0) Section("Resources left", string.Join(", ", detail.ResourcesLeft));
            if (detail.RcsState.Length > 0) Section("RCS", detail.RcsState);
            if (detail.RecheckSummary.Length > 0 || detail.RecheckFindings.Count > 0)
            {
                Section("Margin recheck", detail.RecheckSummary);
                foreach (var finding in detail.RecheckFindings) GUILayout.Label("- " + finding, ClaudiaSkin.Secondary);
                if (detail.RecheckSkipped.Length > 0) GUILayout.Label("Claudia's recheck not run: " + detail.RecheckSkipped + ".", ClaudiaSkin.Secondary);
            }
            else if (detail.RecheckSkipped.Length > 0) Section("Margin recheck", "Not run: " + detail.RecheckSkipped + ".");
            if (detail.Events.Count > 0)
            {
                GUILayout.Label("Events", _bold);
                foreach (var line in detail.Events) GUILayout.Label("- " + line, ClaudiaSkin.Secondary);
            }
            if (detail.ScienceSeen.Count > 0)
            {
                GUILayout.Label("Science seen", _bold);
                foreach (var line in detail.ScienceSeen) GUILayout.Label("- " + line, ClaudiaSkin.Secondary);
            }
            // The steps themselves are the list under the pinned Mission Goal.
            if (detail.HasChecklist)
                GUILayout.Label("Mission Goal: " + detail.ChecklistDone + " of " + detail.Checklist.Count + " (the steps are under the Mission Goal)", _bold);
            if (detail.OutcomeVsTarget.Length > 0) Section("Outcome vs target", detail.OutcomeVsTarget);
            if (detail.PlanMargin.Length > 0) Section("Against the plan", detail.PlanMargin);
            if (detail.Science.Length > 0) Section("Science", detail.Science);
            if (detail.FollowUp.Length > 0) Section("Next", detail.FollowUp);
            if (detail.ScriptError.Length > 0)
            {
                GUILayout.Label("Script error", _bold);
                GUILayout.Label(detail.ScriptError, ClaudiaSkin.Mono);
            }
        }

        private void Section(string title, string text)
        {
            GUILayout.Label(title, _bold);
            if (text.Length > 0) GUILayout.Label(text, ClaudiaSkin.Body);
        }

        private static void DrawMarginBar(MarginBar bar)
        {
            var rect = GUILayoutUtility.GetRect(0, ClaudiaSkin.Scaled(8), GUILayout.ExpandWidth(true));
            GUI.DrawTexture(rect, ClaudiaSkin.Solid(ClaudiaSkin.Border));
            var color = bar.Level == MarginLevel.Enough ? ClaudiaSkin.Success : bar.Level == MarginLevel.Tight ? ClaudiaSkin.Attention : ClaudiaSkin.Danger;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * (float)bar.Fraction, rect.height), ClaudiaSkin.Solid(color));
        }

        // The Mission Script as formatted for reading (display only; the entry's script is untouched), in a box that
        // scrolls both ways. The text area stays selectable for copying; what it returns is dropped, so edits don't stick.
        private void DrawScript(LogEntry entry)
        {
            string text;
            if (!_formatted.TryGetValue(entry, out text)) _formatted[entry] = text = ScriptFormatter.Format(entry.Script);
            Vector2 scroll;
            _scriptScroll.TryGetValue(entry, out scroll);
            var size = _script.CalcSize(new GUIContent(text));
            // CalcSize already includes the style's padding; the extra is room for the horizontal scrollbar.
            var height = Mathf.Min(size.y + 20, ClaudiaSkin.Scaled(240));
            scroll = GUILayout.BeginScrollView(scroll, true, true, GUILayout.Height(height));
            GUILayout.TextArea(text, _script, GUILayout.MinWidth(size.x));
            GUILayout.EndScrollView();
            _scriptScroll[entry] = scroll;
        }

        private static string KindText(LogKind kind)
        {
            return kind == LogKind.MissionPlan ? "MISSION PLAN" : kind == LogKind.SequencePlan ? "SEQUENCE PLAN" : "DEBRIEF";
        }

        private static ClaudiaSkin.ChipKind OutcomeChip(string outcome)
        {
            var o = outcome.ToLowerInvariant();
            if (o.StartsWith("off", StringComparison.Ordinal)) return ClaudiaSkin.ChipKind.Attention;
            if (o.StartsWith("fail", StringComparison.Ordinal)) return ClaudiaSkin.ChipKind.Danger;
            return o.StartsWith("nominal", StringComparison.Ordinal) ? ClaudiaSkin.ChipKind.Success : ClaudiaSkin.ChipKind.Info;
        }

        /// <summary>A time from now ("in 2d 3h 10m") whose hover tooltip is the Kerbin date; <see cref="WindowFrame.DrawTooltip"/> shows it.</summary>
        public static GUIContent TimeContent(double ut, double now)
        {
            return new GUIContent(KerbinTime.FromNow(ut, now), KerbinTime.Date(ut));
        }

        private static void Toggle(HashSet<LogEntry> set, LogEntry entry)
        {
            if (!set.Remove(entry)) set.Add(entry);
        }

        // ---- Craft Check: the first item of the transcript ----

        private void DrawCraftCheck(MissionView view)
        {
            var check = view.CraftCheck;
            var form = view.StartForm;

            // A check that needs the player is never hidden in Flight: Running, needs the simulator, errored, or Start Mission held closed.
            var needsPlayer = check.State == CraftCheckState.Running || check.State == CraftCheckState.NeedsSimulator
                || check.State == CraftCheckState.Error
                || (check.State == CraftCheckState.Done && form.CanStart && !check.StartAllowed);
            var inFlight = HighLogic.LoadedSceneIsFlight;
            // Forced open without touching the player's choice, which comes back once the check no longer needs them.
            var collapsed = inFlight && _checkCollapsed && !needsPlayer;

            // Any result after a pause (Running, Error, ...) is new, even if it matches the last one.
            if (check.State != CraftCheckState.Done) _seenCheckResult = "";

            if (collapsed)
            {
                DrawCollapsed(check);
                return;
            }

            var notice = MissionWindowRules.CraftNotice(check, !HighLogic.LoadedSceneIsEditor, GoalSet);
            if (notice.Length > 0) GUILayout.Label(notice, ClaudiaSkin.Notice);

            if (check.State != CraftCheckState.Done)
            {
                DrawStateLine(check);
                return;
            }
            DrawResult(view, inFlight && !needsPlayer);
        }

        // In Flight the card is its header line with an expand control.
        private void DrawCollapsed(CraftCheckView check)
        {
            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            if (check.State == CraftCheckState.Done) VerdictIcon(check.Verdict);
            GUILayout.Label(MissionWindowRules.CollapsedTitle(check), _bold);
            GUILayout.FlexibleSpace();
            if (ClaudiaSkin.SmallButton("Expand", GUILayout.ExpandWidth(false))) _checkCollapsed = false;
            GUILayout.EndHorizontal();
            ClaudiaSkin.EndCard();
        }

        // None reads as a notice; Running, NeedsSimulator and Error as Claudia's line, with Check again where it helps.
        private void DrawStateLine(CraftCheckView check)
        {
            var line = MissionWindowRules.StateLine(check);
            var none = check.State == CraftCheckState.None;
            if (none) GUILayout.Label(line, ClaudiaSkin.Notice);
            else DrawClaudiaLine(line);

            // Check again needs the pinned goal, which only the Mission window knows; the strip's drawer has no button.
            var offer = MissionWindowRules.OffersCheckAgain(check.State) && CheckAgain != null;
            // An expanded "not checked yet" in Flight can be put away again.
            var collapsible = none && HighLogic.LoadedSceneIsFlight;
            if (!offer && !collapsible) return;
            GUILayout.BeginHorizontal();
            if (offer)
            {
                var input = CheckAgain();
                var wasEnabled = GUI.enabled;
                GUI.enabled = input != null;
                if (ClaudiaSkin.SecondaryButton("Check again", GUILayout.ExpandWidth(false)) && input != null) _send(input);
                GUI.enabled = wasEnabled;
            }
            if (collapsible && ClaudiaSkin.SmallButton("Collapse", GUILayout.ExpandWidth(false))) _checkCollapsed = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static void DrawClaudiaLine(string text)
        {
            GUILayout.Label("Claudia", ClaudiaSkin.ClaudiaName);
            GUILayout.Label(text, ClaudiaSkin.Body);
        }

        // Amber triangle for Pass with Warnings, red for Fail, green check for Pass.
        private static void VerdictIcon(CraftVerdict verdict)
        {
            var tone = MissionWindowRules.VerdictTone(verdict);
            ClaudiaSkin.IconLabel(verdict == CraftVerdict.Pass ? ClaudiaSkin.IconShape.Check : ClaudiaSkin.IconShape.Warning,
                tone == ChipTone.Success ? ClaudiaSkin.IconGreen : tone == ChipTone.Danger ? ClaudiaSkin.Danger : ClaudiaSkin.Attention, 16);
        }

        private void DrawResult(MissionView view, bool canCollapse)
        {
            var check = view.CraftCheck;
            var signature = check.Summary;
            foreach (var finding in check.Findings) signature += "|" + finding.Code;
            if (signature != _seenCheckResult)
            {
                _seenCheckResult = signature;
                _checkedAt = view.Ut;
                // Before a Mission the Craft Check is what the player reads; during one the log keeps the bottom.
                if (view.StartForm.CanStart) _scroll.y = 0;
            }
            var summary = MissionWindowRules.ClaudiaLine(check);
            if (summary.Length > 0) DrawClaudiaLine(summary);

            ClaudiaSkin.BeginCard();
            GUILayout.BeginHorizontal();
            VerdictIcon(check.Verdict);
            GUILayout.Label(MissionWindowRules.CardTitle(check), _bold);
            GUILayout.FlexibleSpace();
            if (_checkedAt > 0 && view.Ut > 0) GUILayout.Label(TimeContent(_checkedAt, view.Ut), ClaudiaSkin.Secondary, GUILayout.ExpandWidth(false));
            if (canCollapse && ClaudiaSkin.SmallButton("Collapse", GUILayout.ExpandWidth(false))) _checkCollapsed = true;
            GUILayout.EndHorizontal();

            DrawFacts(MissionWindowRules.Facts(check, Estimate));
            foreach (var finding in check.Findings) DrawFinding(finding);
            if (check.ShipSystemsInfo.Length > 0) GUILayout.Label(check.ShipSystemsInfo, ClaudiaSkin.Secondary);

            if (view.StartForm.CanStart)
            {
                var footer = MissionWindowRules.Footer(check);
                if (footer.Text.Length > 0) GUILayout.Label(footer.Text, footer.Tone == ChipTone.Success ? ClaudiaSkin.SuccessText : ClaudiaSkin.Secondary);
            }
            ClaudiaSkin.EndCard();
        }

        // Small chips that wrap onto further lines when the row is full.
        private void DrawFacts(IList<StatusChip> facts)
        {
            // The card's own padding comes off the entry width too.
            var room = _width - 4 * ClaudiaSkin.Padding;
            var used = 0f;
            var open = false;
            foreach (var chip in facts)
            {
                var kind = ClaudiaSkin.KindOf(chip.Tone);
                var size = ClaudiaSkin.ChipStyle(kind).CalcSize(new GUIContent(chip.Text)).x + ClaudiaSkin.Gap;
                if (!open || (used > 0 && used + size > room))
                {
                    if (open) GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    open = true;
                    used = 0;
                }
                ClaudiaSkin.Chip(chip.Text, kind);
                used += size;
            }
            if (open) GUILayout.EndHorizontal();
        }

        // A Warning gets the box and a checkbox that acknowledges it; a Blocker the box alone.
        private void DrawFinding(CraftFinding finding)
        {
            var warning = finding.Severity == CraftFindingSeverity.Warning;
            GUILayout.BeginVertical(warning ? ClaudiaSkin.WarningBox : ClaudiaSkin.BlockerBox);
            GUILayout.Label(MissionWindowRules.FindingLabel(finding.Severity), warning ? ClaudiaSkin.WarningLabel : ClaudiaSkin.BlockerLabel);
            GUILayout.Label(finding.Title, _bold);
            GUILayout.Label(finding.Explanation, ClaudiaSkin.Secondary);
            if (finding.Fix.Length > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Fix:", _bold, GUILayout.ExpandWidth(false));
                GUILayout.Label(finding.Fix, ClaudiaSkin.Body);
                GUILayout.EndHorizontal();
            }
            if (warning)
            {
                // The core can't un-acknowledge: once it has, the box stays checked and takes no clicks.
                var acknowledged = ClaudiaSkin.Checkbox(finding.Acknowledged, "I accept this risk", !finding.Acknowledged);
                if (acknowledged && !finding.Acknowledged) _send(new AcknowledgeWarning(finding.Code));
            }
            GUILayout.EndVertical();
        }

        // ---- #1022: Mission Control's Contract check card (the mod's, no Claude) ----

        private void DrawContractCheck(ContractCheckView card, double now)
        {
            Styles();
            ClaudiaSkin.BeginCard();
            GUILayout.BeginVertical(ClaudiaSkin.CardHeaderRow);
            GUILayout.Label(ContractCheckView.Label.ToUpperInvariant() + "  (the mod, no Claude)", ClaudiaSkin.KindLabel);
            GUILayout.EndVertical();
            if (card.Folded)
            {
                GUILayout.Label(card.Summary, ClaudiaSkin.Secondary);
                ClaudiaSkin.EndCard();
                return;
            }
            var ticked = 0;
            foreach (var row in card.Rows) if (row.Ticked) ticked++;
            GUILayout.Label(ticked + " of " + card.Accepted + " Contracts" + (card.Words.Length > 0 ? " with your words" : "") + " · route " + Whole(card.RouteDeltaV)
                + " m/s of " + Whole(card.CraftDeltaV) + " · reserve " + Whole(card.Reserve) + " (" + card.ReservePercent + "%) · "
                + (card.Spare >= 0 ? Whole(card.Spare) + " m/s to spare" : Whole(-card.Spare) + " m/s short"), _bold);
            DrawRouteBar(card);
            foreach (var row in card.Rows)
            {
                var on = ClaudiaSkin.Checkbox(row.Ticked, row.Title);
                if (on != row.Ticked) _send(new TickContract(row.Guid, on));
                GUILayout.Label(row.Phase + " · +" + Whole(row.AddedDeltaV) + " m/s · " + (row.DeadlineUt.HasValue ? "deadline " + KerbinTime.ShortDate(row.DeadlineUt.Value)
                    + " (" + KerbinTime.FromNow(row.DeadlineUt.Value, now) + ")" : "no deadline"), ClaudiaSkin.Secondary);
                if (row.Note.Length > 0) GUILayout.Label(row.Note, ClaudiaSkin.Secondary);
            }
            if (card.NotThisFlight.Count > 0)
            {
                GUILayout.Label("Not this flight", _bold);
                foreach (var left in card.NotThisFlight)
                {
                    GUILayout.BeginHorizontal();
                    ClaudiaSkin.Chip(left.Reason.StartsWith("not yet", StringComparison.Ordinal) ? "not yet" : left.Reason.StartsWith("completes later", StringComparison.Ordinal) ? "completes later" : "this craft can't",
                        left.Reason.StartsWith("not yet", StringComparison.Ordinal) ? ClaudiaSkin.ChipKind.Danger : ClaudiaSkin.ChipKind.Attention);
                    GUILayout.Label(left.Title + " — " + left.Reason, ClaudiaSkin.Secondary);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.BeginHorizontal();
            if (card.CanPlan)
            {
                if (ClaudiaSkin.PrimaryButton(card.PlanLabel, GUILayout.ExpandWidth(false))) _send(new PlanContracts());
            }
            else ClaudiaSkin.DisabledButton(card.PlanLabel, GUILayout.ExpandWidth(false));
            if (card.CanPlanWithout)
            {
                if (ClaudiaSkin.SecondaryButton("Plan without Contracts", GUILayout.ExpandWidth(false))) _send(new PlanWithoutContracts());
            }
            else ClaudiaSkin.DisabledButton("Plan without Contracts", GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (!card.CanPlanWithout) GUILayout.Label("Type your own words to plan without Contracts.", ClaudiaSkin.Secondary);
            ClaudiaSkin.EndCard();
        }

        /// <summary>The route against the craft: the route's share, then the reserve's, green when the set fits and red when it is short.</summary>
        private static void DrawRouteBar(ContractCheckView card)
        {
            var rect = GUILayoutUtility.GetRect(0, ClaudiaSkin.Scaled(8), GUILayout.ExpandWidth(true));
            GUI.DrawTexture(rect, ClaudiaSkin.Solid(ClaudiaSkin.Border));
            var whole = Math.Max(card.CraftDeltaV, card.RouteDeltaV + card.Reserve);
            if (whole <= 0) return;
            var route = (float)(card.RouteDeltaV / whole);
            var reserve = (float)(card.Reserve / whole);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * route, rect.height), ClaudiaSkin.Solid(card.Spare >= 0 ? ClaudiaSkin.Success : ClaudiaSkin.Danger));
            GUI.DrawTexture(new Rect(rect.x + rect.width * route, rect.y, rect.width * reserve, rect.height), ClaudiaSkin.Solid(ClaudiaSkin.Attention));
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- Approval: a typed reply that reads like an approval ends the transcript with a checkbox ----

        /// <summary>Claudia's ask and the box with the checkbox and Not yet.</summary>
        private void DrawApproval(MissionView view)
        {
            Styles();
            DrawClaudiaLine(MissionWindowRules.ApprovalAsk);
            GUILayout.BeginVertical(ClaudiaSkin.AskBox);
            if (ClaudiaSkin.Checkbox(false, MissionWindowRules.ApprovalLabel(view))) _send(new ConfirmApproval(true));
            GUILayout.BeginHorizontal();
            if (ClaudiaSkin.TextButton("Not yet", GUILayout.ExpandWidth(false))) _send(new ConfirmApproval(false));
            GUILayout.Label("Or keep typing to change it.", ClaudiaSkin.Secondary);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }
    }
}

