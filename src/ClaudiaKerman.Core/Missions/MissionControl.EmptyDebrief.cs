// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// A Debrief reply with no filled debrief (#748: a null debrief, or a kind that isn't "debrief") is corrected once, with a short
    /// line after the turn's own messages. If the reply to that is empty too, the mod writes its own Off-Nominal Debrief from the run's
    /// report and events and the Mission waits for the player (Resume asks for the next Sequence Plan), in Auto Mode and Blackout alike.
    /// Never more than two requests for one Debrief.
    /// </summary>
    public sealed partial class MissionControl
    {
        /// <summary>The Debrief reply lacked a debrief: resend once with the correction, or (already corrected, or no room under the Spending Cap) debrief it ourselves.</summary>
        private void EmptyDebriefReply(Mission mission, Turn turn)
        {
            if (!turn.DebriefRetry)
            {
                turn.DebriefRetry = true;
                turn.Messages.Add(Message.SystemText(EmptyDebrief.Note));
                if (HasCapRoom(mission, turn))
                {
                    Event(mission, "debrief_retry", "the reply had no debrief");
                    mission.Activity = Thinking(turn);
                    Send(mission, turn);
                    return;
                }
            }
            ModDebriefed(mission, turn);
        }

        /// <summary>The mod's own Debrief, with no further request: the card, the transcript's fact (carrying the run's report), and a Pause that owes the next Sequence Plan.</summary>
        private void ModDebriefed(Mission mission, Turn turn)
        {
            var debrief = EmptyDebrief.Debrief(turn.Report);
            mission.Pending = null;
            mission.HandBack = false;
            mission.ManualPlanned = false;
            mission.ManualOrigin = "";
            mission.HandBackMessage = "";
            mission.Crossings.Clear();
            // The fact joins the conversation first, so a quit before Resume still rebuilds the card.
            mission.Conversation.Add(Message.UserText(EmptyDebrief.Fact(debrief.WhatHappened) + "\n" + turn.UserText));
            WriteTranscript(mission);
            ClearUnanswered(mission);
            Event(mission, "mod_debrief", "Claudia's Debrief reply had no debrief twice");
            var entry = AddCard(mission, LogKind.Debrief, "Debrief: " + debrief.Outcome + TakeStopReason(mission), MissionCards.DebriefBody(debrief, ""), EmptyDebrief.CardWhy);
            entry.Outcome = debrief.Outcome;
            entry.Debrief = DetailOf(mission, debrief);
            LogModDebrief(mission, debrief);
            HerNews("The mod wrote the Debrief: " + debrief.Outcome + ".");
            mission.Stage = MissionStage.Sequencing;
            mission.SequenceCounted = false;
            mission.Owed = Owed.SequencePlan;
            Pause(mission, "Claudia's Debrief reply had no Debrief twice, so the mod wrote an Off-Nominal one from the run's report. Resume asks her for the next Sequence Plan.");
        }
    }

    /// <summary>The correction and the mod's own Debrief for a Debrief reply with no debrief (#748).</summary>
    internal static class EmptyDebrief
    {
        public const string Note = "Your reply had no debrief. A Debrief is required now: reply with kind \"debrief\" and debrief filled in. "
            + "No tools are available for a Debrief; the craft's next encounter, if any, is in the Game Snapshot.";

        /// <summary>The line the fact starts with; <see cref="MissionLogRebuild"/> finds the mod-written card by it.</summary>
        public const string FactPrefix = "The mod debriefed the run itself, with no further request: Claudia's Debrief reply had no debrief twice, Off-Nominal (JSON data): ";
        public const string CardWhy = "The mod's own Debrief: Claudia's reply had no debrief twice, so this is written from the run's report and events.";

        public static string Fact(string whatHappened)
        {
            return FactPrefix + new JsonWriter().BeginObject().Field("what_happened", whatHappened ?? "").EndObject();
        }

        /// <summary>The what_happened a fact carries; null for any other text.</summary>
        public static string ReadFact(string text)
        {
            if (text == null || !text.StartsWith(FactPrefix, StringComparison.Ordinal)) return null;
            var json = text.Substring(FactPrefix.Length);
            var end = json.IndexOf('\n');
            if (end >= 0) json = json.Substring(0, end);
            try
            {
                return JsonNode.Str(JsonReader.Parse(json)["what_happened"]);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static Debrief Debrief(IDictionary<string, object> report)
        {
            return Debrief(Account(report));
        }

        public static Debrief Debrief(string whatHappened)
        {
            return new Debrief
            {
                Outcome = "Off-Nominal",
                WhatHappened = whatHappened,
                OutcomeVsTarget = whatHappened,
                FollowUp = FollowUps.CorrectiveSequence,
                FollowUpText = "next Sequence Plan",
            };
        }

        private static string Account(IDictionary<string, object> report)
        {
            var said = "Claudia's Debrief reply had no debrief twice, so the mod rated this run Off-Nominal without judging it.";
            if (report == null || report.Count == 0) return said + " The script gave no report.";
            return said + " The script's report: " + string.Join(", ", report.Select(p => p.Key + " " + p.Value).ToArray()) + ".";
        }
    }
}
