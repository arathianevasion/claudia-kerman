// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// Identical repeats within one turn's tool loop (#508): the same Planning Tool with the same input (key order and spacing
    /// aside) as an answered call whose result is still in the turn's messages, or as an earlier call of the same reply. Each
    /// is answered with where its result already is, without running; a discarded loop's calls are gone, so they may run again.
    /// </summary>
    internal static class ToolRepeats
    {
        /// <summary>For each tool_use of <paramref name="reply"/>, in order, why it is a repeat of a call in <paramref name="earlier"/> or of the reply's own; null when it isn't.</summary>
        public static string[] Of(IEnumerable<Message> earlier, AssistantMessage reply)
        {
            var messages = earlier.ToList();
            // Only a call that answered counts: one refused, timed out or over a limit has no result above to point to.
            var answered = new HashSet<string>(messages.SelectMany(m => m.Content.OfType<ToolResultBlock>()).Where(r => !r.IsError).Select(r => r.ToolUseId), StringComparer.Ordinal);
            // Each earlier call's step, numbered from 1 by the turn's tool_use replies.
            var steps = new Dictionary<string, int>(StringComparer.Ordinal);
            var step = 0;
            foreach (var message in messages.Where(m => m.Role == Role.Assistant))
            {
                var uses = message.Content.OfType<ToolUseBlock>().ToList();
                if (uses.Count == 0) continue;
                step++;
                foreach (var use in uses.Where(u => answered.Contains(u.Id ?? "")))
                {
                    var key = Key(use);
                    if (!steps.ContainsKey(key)) steps[key] = step;
                }
            }

            var calls = reply.Content.OfType<ToolUseBlock>().ToList();
            var why = new string[calls.Count];
            var inReply = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < calls.Count; i++)
            {
                var key = Key(calls[i]);
                int at;
                if (steps.TryGetValue(key, out at)) why[i] = "you already called this in step " + at.ToString(CultureInfo.InvariantCulture) + "; its result is above";
                else if (!inReply.Add(key)) why[i] = "you already called this in this reply; its result is with the first call's";
            }
            return why;
        }

        /// <summary>The tool and its input in one canonical form: object members sorted, numbers by value, no spacing.</summary>
        private static string Key(ToolUseBlock use)
        {
            var name = use.Name ?? "";
            // Input cut off mid-stream is its own call, never the same as a complete one.
            if (!use.InputIsValid) return name + " invalid " + use.InputJson;
            try
            {
                return name + " " + Canonical(JsonReader.Parse(string.IsNullOrEmpty(use.InputJson) ? "{}" : use.InputJson));
            }
            catch (JsonException)
            {
                return name + " invalid " + use.InputJson;
            }
        }

        private static string Canonical(JsonNode node)
        {
            switch (node.Kind)
            {
                case JsonKind.Object:
                    return "{" + string.Join(",", node.Members.OrderBy(m => m.Key, StringComparer.Ordinal)
                        .Select(m => new JsonWriter().String(m.Key) + ":" + Canonical(m.Value)).ToArray()) + "}";
                case JsonKind.Array:
                    return "[" + string.Join(",", node.Items.Select(Canonical).ToArray()) + "]";
                case JsonKind.String:
                    return new JsonWriter().String(node.StringValue).ToString();
                case JsonKind.Number:
                    var value = JsonNode.Double(node);
                    return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : node.Raw;
                case JsonKind.Bool:
                    return node.BoolValue ? "true" : "false";
                default:
                    return "null";
            }
        }
    }
}
