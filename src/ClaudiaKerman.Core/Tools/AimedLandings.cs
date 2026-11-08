// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Claude;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Where each vessel last aimed its landing (#913), by vessel id, for <c>reentry</c>'s late trim and a trip home's aim. A vessel's calls write and read only
    /// its own entry, so a Child Vessel's aim can't replace its parent's. The id is the opaque one a <see cref="Missions.GameSnapshot"/> carries.
    /// </summary>
    public sealed class AimedLandings
    {
        private readonly Dictionary<string, LandingTarget> _byVessel = new Dictionary<string, LandingTarget>();

        /// <summary>The vessel's aimed target; null for none.</summary>
        public LandingTarget Of(string vesselId)
        {
            LandingTarget target;
            return _byVessel.TryGetValue(vesselId ?? "", out target) ? target : null;
        }

        /// <summary>Records the vessel's aim, replacing its last; a null target (an aim at no landing) forgets it.</summary>
        public void Aim(string vesselId, LandingTarget target)
        {
            if (target == null) _byVessel.Remove(vesselId ?? "");
            else _byVessel[vesselId ?? ""] = target;
        }

        /// <summary>A vessel's id changed (staging or an undock can give the Mission's craft a new one): its aim goes with it. An aim already made under the new id is the newer, and stands.</summary>
        public void Move(string fromId, string toId)
        {
            fromId = fromId ?? "";
            toId = toId ?? "";
            LandingTarget target;
            if (fromId == toId || !_byVessel.TryGetValue(fromId, out target)) return;
            _byVessel.Remove(fromId);
            if (!_byVessel.ContainsKey(toId)) _byVessel[toId] = target;
        }

        public AimedLandings Copy()
        {
            var copy = new AimedLandings();
            foreach (var entry in _byVessel) copy._byVessel[entry.Key] = entry.Value;
            return copy;
        }

        /// <summary>For the save; "" for none.</summary>
        public string ToJson()
        {
            if (_byVessel.Count == 0) return "";
            var json = new JsonWriter().BeginArray();
            foreach (var entry in _byVessel.OrderBy(e => e.Key, StringComparer.Ordinal))
                json.BeginObject().Field("vessel_id", entry.Key).Field("body", entry.Value.Body).Field("lat", entry.Value.Latitude).Field("lng", entry.Value.Longitude).Field("target_pe", entry.Value.TargetPe).EndObject();
            return json.EndArray().ToString();
        }

        /// <summary>What <see cref="ToJson"/> wrote; none for "" or anything unreadable.</summary>
        public static AimedLandings FromJson(string text)
        {
            var read = new AimedLandings();
            if (string.IsNullOrEmpty(text)) return read;
            JsonNode root;
            try
            {
                root = JsonReader.Parse(text);
            }
            catch (JsonException)
            {
                return read;
            }
            if (root.Kind != JsonKind.Array) return read;
            foreach (var node in root.Items.Where(n => n.Kind == JsonKind.Object))
            {
                var vesselId = JsonNode.Str(node["vessel_id"]);
                var body = JsonNode.Str(node["body"]);
                var latitude = JsonNode.Double(node["lat"]);
                var longitude = JsonNode.Double(node["lng"]);
                if (vesselId == null || string.IsNullOrEmpty(body) || !latitude.HasValue || !longitude.HasValue) continue;
                read._byVessel[vesselId] = new LandingTarget { Body = body, Latitude = latitude.Value, Longitude = longitude.Value, TargetPe = JsonNode.Double(node["target_pe"]) };
            }
            return read;
        }
    }
}
