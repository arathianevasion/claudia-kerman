// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// Cache diagnostics (#569), behind <c>debug_tools</c> only: every Mission request names the previous response, and a
    /// response whose cache read fell short of what the request before left cached has its <c>diagnostics</c> written to the
    /// Mission's <c>events.jsonl</c> as a <c>cache_diagnosis</c> event. Without <c>debug_tools</c> none of this runs and the
    /// request is byte-identical to before.
    /// </summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The id of the last completed Mission response (its <c>message_start</c> id), kept in the transcript like <see cref="ContextTokens"/>; null before the first.</summary>
            public string LastMessageId;
            /// <summary>What the last completed response left in the cache (its read plus its writes); 0 when not known, as after a load.</summary>
            public long LastCacheTotal;
        }

        private void AddDiagnostics(Mission mission, MessagesRequest request)
        {
            if (!_config.DebugTools) return;
            request.Diagnostics = true;
            request.PreviousMessageId = mission.LastMessageId;
        }

        private void TrackCache(Mission mission, StreamResult result)
        {
            if (!_config.DebugTools || result == null || result.Status != StreamStatus.Completed) return;
            var message = result.Message;
            var usage = message.Usage;
            if (usage.CacheReadInputTokens < mission.LastCacheTotal)
                Event(mission, "cache_diagnosis", message.DiagnosticsJson ?? "null");
            mission.LastCacheTotal = usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
            if (message.Id != null) mission.LastMessageId = message.Id;
        }
    }
}
