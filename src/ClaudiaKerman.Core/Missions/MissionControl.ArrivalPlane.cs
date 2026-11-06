// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>What the Mission keeps for the arrival plane (#788): the plane <c>transfer_to</c> aimed for, and the last site <c>find_landing_site</c> gave.</summary>
    public sealed partial class MissionControl
    {
        private sealed partial class Mission
        {
            /// <summary>The plane <c>transfer_to</c> aimed for, as <see cref="TransferFlightStep.AimedPlane"/> gives it, for <c>capture</c>; "" for none.</summary>
            public string ArrivalPlane = "";
            /// <summary>What the block that aimed the landing predicted (<see cref="LandingLine.Record"/>), for the reentry that may come in a later run (#790).</summary>
            public string LandingAim = "";
            /// <summary><c>find_landing_site</c>'s first site in its last answer; null for none.</summary>
            public PickedSite PickedSite;
        }

        private void ArrivalPlaneRecorded(ArrivalPlaneAimed aimed)
        {
            if (_mission != null) _mission.ArrivalPlane = aimed.Record;
        }

        /// <summary>Keeps the first site of a <c>find_landing_site</c> answer in this step, for a next leg's arrival plane. A later answer replaces it; one with no site leaves it.</summary>
        private static void NoteSitePicked(Mission mission, ToolStepResult step)
        {
            var answers = step.Results.Content.OfType<ToolResultBlock>().ToList();
            for (var i = 0; i < step.Calls.Count && i < answers.Count; i++)
            {
                var call = step.Calls[i];
                if (call.Name != "find_landing_site" || call.IsError || call.Refusal.Length > 0) continue;
                var site = PickedSiteOf(answers[i].Content);
                if (site != null) mission.PickedSite = site;
            }
        }

        /// <summary>The body and first site's latitude of a <c>find_landing_site</c> answer; null when it names none.</summary>
        internal static PickedSite PickedSiteOf(string answer)
        {
            JsonNode root;
            try
            {
                root = JsonReader.Parse(answer);
            }
            catch (JsonException)
            {
                return null;
            }
            var sites = root["sites"];
            if (sites == null || sites.Kind != JsonKind.Array || sites.Items.Count == 0) return null;
            var latitude = JsonNode.Double(sites.Items[0]["latitude"]);
            var body = JsonNode.Str(root["body"]);
            return latitude.HasValue && !string.IsNullOrEmpty(body) ? new PickedSite { Body = body, Latitude = latitude.Value } : null;
        }
    }
}
