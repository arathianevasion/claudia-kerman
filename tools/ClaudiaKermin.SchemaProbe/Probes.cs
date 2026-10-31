// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Reflection;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.SchemaProbe
{
    /// <summary>
    /// The requests behind the #534 probes, built without sending anything so <c>--count</c> and <c>--print</c> (and the tests)
    /// can show them. <see cref="MissionSchema"/> is the one reply schema a Mission would send throughout: the flight schema
    /// with mission_plan in <c>kind</c> and no mission_plan part. <see cref="ProposeMissionPlan"/> is the non-strict Reply Tool
    /// that would carry the Mission Plan instead.
    /// </summary>
    public static class Probes
    {
        public const string ProposeToolName = "propose_mission_plan";

        /// <summary>The effort the cache probe's baseline sends, and the one it changes to.</summary>
        public const string BaselineEffort = "medium";
        public const string ChangedEffort = "high";

        /// <summary>The flight schema (answer, sequence_plan, debrief, alert) with mission_plan added to the kind enum.</summary>
        public static string MissionSchema()
        {
            var flight = MissionReply.SchemaFor(MissionReply.AnswerKind, MissionReply.SequencePlanKind, MissionReply.DebriefKind, MissionReply.AlertKind);
            const string from = "\"enum\":[\"sequence_plan\",";
            if (flight.IndexOf(from, StringComparison.Ordinal) < 0) throw new InvalidOperationException("the flight schema's kind enum changed");
            return flight.Replace(from, "\"enum\":[\"mission_plan\",\"sequence_plan\",");
        }

        /// <summary>Today's mission_plan part, as the planning schema embeds it.</summary>
        public static string MissionPlanPartSchema()
        {
            var field = typeof(MissionReply).GetField("MissionPlanSchema", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null) throw new InvalidOperationException("MissionReply.MissionPlanSchema is gone");
            return (string)field.GetRawConstantValue();
        }

        public static ToolDefinition ProposeMissionPlan()
        {
            return new ToolDefinition
            {
                Name = ProposeToolName,
                Description = "Hand in a Mission Plan for the mod to check. It answers ok, or the problems to fix before you call it again.",
                InputSchemaJson = MissionPlanPartSchema(),
                Strict = false,
            };
        }

        /// <summary>The Mission tools as the mod sends them (Planning Tools, check_script), plus propose_mission_plan when asked.</summary>
        public static IList<ToolDefinition> MissionTools(bool withPropose)
        {
            var tools = new List<ToolDefinition>(PlanningTools.Default(new AlwaysPasses()).Definitions(ToolOffer.Mission));
            if (withPropose) tools.Add(ProposeMissionPlan());
            return tools;
        }

        /// <summary>The grammar probe's request: the one Mission schema, with or without propose_mission_plan in the tool list.</summary>
        public static MessagesRequest GrammarRequest(string model, bool withPropose)
        {
            var request = new MessagesRequest { Model = model, MaxTokens = 16, OutputSchemaJson = MissionSchema() };
            request.Tools.AddRange(MissionTools(withPropose));
            request.Messages.Add(Message.UserText("Reply with kind answer and say ok."));
            return request;
        }

        /// <summary>
        /// The cache probe's steps, in the order they are sent: baseline, baseline again (a read), effort changed alone,
        /// tool_choice changed alone (auto to none). Every request carries the same long system layer, tools, schema and message.
        /// </summary>
        public static IList<KeyValuePair<string, MessagesRequest>> CacheRequests(string model, string longText)
        {
            return new List<KeyValuePair<string, MessagesRequest>>
            {
                Step("baseline (writes the cache)", model, longText, BaselineEffort, false),
                Step("baseline again (should read it)", model, longText, BaselineEffort, false),
                Step("only effort changed " + BaselineEffort + " -> " + ChangedEffort, model, longText, ChangedEffort, false),
                Step("only tool_choice changed auto -> none", model, longText, BaselineEffort, true),
                Step("effort changed by a system message before the last turn (" + ChangedEffort + ")", model, longText, BaselineEffort, false, ChangedEffort),
            };
        }

        /// <summary>Distinct numbered lines, so the text is not one compressible repeat; about 10k tokens, over every model's minimum cacheable length.</summary>
        public static string LongText(string nonce)
        {
            var text = new System.Text.StringBuilder("Cache probe " + nonce + ". Reference telemetry log, read it and ignore it.\n");
            for (var i = 1; i <= 700; i++)
                text.Append("Line ").Append(i).Append(": sample ").Append(i * 37 % 1013).Append(" altitude ").Append(i * 211 % 9973)
                    .Append(" m, speed ").Append(i * 53 % 2411).Append(" m/s, fuel ").Append(i * 17 % 997).Append(" units, status nominal.\n");
            return text.ToString();
        }

        /// <remarks>
        /// The long text sits in the conversation, not the system layer: effort and tool_choice changes are documented to
        /// drop the cached messages but keep the system prompt and tools, and a Mission's history is what costs.
        /// </remarks>
        private static KeyValuePair<string, MessagesRequest> Step(string label, string model, string longText, string effort, bool toolChoiceNone,
            string systemEffort = null)
        {
            var request = new MessagesRequest
            {
                Model = model,
                MaxTokens = 16,
                Effort = effort,
                OutputSchemaJson = MissionSchema(),
                NoToolChoice = toolChoiceNone,
            };
            request.System.Add("Cache probe. Reply as the schema says.");
            request.Tools.AddRange(MissionTools(true));
            request.Messages.Add(Message.UserText(longText));
            var earlier = new Message { Role = Role.Assistant };
            earlier.Content.Add(new TextBlock { Text = "{\"kind\":\"answer\",\"say\":\"ok\"}" });
            request.Messages.Add(earlier);
            if (systemEffort != null) request.Messages.Add(Message.SystemEffort(systemEffort));
            request.Messages.Add(Message.UserText("Reply with kind answer and say ok."));
            return new KeyValuePair<string, MessagesRequest>(label, request);
        }

        /// <summary>A script check that accepts anything: only its tool definition is used.</summary>
        private sealed class AlwaysPasses : IScriptCheck
        {
            public ScriptCheckResult Check(string script)
            {
                return ScriptCheckResult.Pass();
            }
        }
    }
}
