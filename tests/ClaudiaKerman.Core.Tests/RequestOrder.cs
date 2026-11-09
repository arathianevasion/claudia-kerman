// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using System.Text.Json;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#953: the API's rule for a mid-conversation system message, checked on a request body.</summary>
    internal static class RequestOrder
    {
        /// <summary>
        /// Null when the request is valid; otherwise what is wrong. A system message with text must follow a user message, or an
        /// assistant message ending in a server tool result. The empty effort-change form (empty content array) may sit anywhere.
        /// </summary>
        public static string Violation(string body)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                JsonElement messages;
                if (!doc.RootElement.TryGetProperty("messages", out messages)) return null;
                var all = messages.EnumerateArray().ToList();
                for (var i = 0; i < all.Count; i++)
                {
                    if (all[i].GetProperty("role").GetString() != "system") continue;
                    var content = all[i].GetProperty("content");
                    if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 0) continue;
                    // Several notices in a row stand behind the same message: look past system messages to the one they follow.
                    var before = i - 1;
                    while (before >= 0 && all[before].GetProperty("role").GetString() == "system") before--;
                    if (before < 0) return "messages." + i + ": a text system message has no user message before it";
                    var role = all[before].GetProperty("role").GetString();
                    if (role == "user") continue;
                    if (role == "assistant")
                    {
                        var blocks = all[before].GetProperty("content").EnumerateArray().ToList();
                        var type = blocks.Count == 0 ? "" : blocks[blocks.Count - 1].GetProperty("type").GetString();
                        if (type != "tool_result" && type.EndsWith("_tool_result", StringComparison.Ordinal)) continue;
                    }
                    return "messages." + i + ": role 'system' follows a '" + role + "' message";
                }
                // #955, the other half. The docs (mid-conversation system messages): "A mid-conversation system message that carries
                // content (text, tool_addition, or tool_removal blocks) must immediately follow a user turn (or an assistant turn
                // ending in a server tool result), and must either be the last entry in messages or be immediately followed by an
                // assistant turn." So user, system (text), user is rejected. Consecutive system messages are judged together, and
                // several text notices in a row are accepted by the API in committed flights.
                for (var i = 0; i < all.Count; i++)
                {
                    if (all[i].GetProperty("role").GetString() != "system") continue;
                    var content = all[i].GetProperty("content");
                    if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() == 0) continue;
                    var after = i + 1;
                    while (after < all.Count && all[after].GetProperty("role").GetString() == "system") after++;
                    if (after < all.Count && all[after].GetProperty("role").GetString() != "assistant")
                        return "messages." + i + ": a text system message is followed by a '" + all[after].GetProperty("role").GetString() + "' message, not the end or an assistant message";
                }
                return null;
            }
        }
    }
}
