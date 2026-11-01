// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKermin.Core.Claude;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>
    /// The run's blocks trail (#609): one compact entry for every Building Block result (<c>ADDONS:CLAUDIA:BLOCKRESULT</c>), in
    /// order, whatever the Mission Script reports at the end. A hand-made report can't hide what the blocks did. Each entry is
    /// the block, success, reason and up to three key results named per block (<see cref="KeyResults"/>), small enough for Haiku.
    /// The Debrief turn text and plans.jsonl carry it; a save keeps it (<see cref="ToJson"/>).
    /// </summary>
    public sealed class BlockTrail
    {
        /// <summary>The most entries listed; past that the oldest successful ones collapse into a count.</summary>
        public const int MaxEntries = 12;
        /// <summary>A longer reason is cut to this many characters.</summary>
        public const int MaxReason = 200;
        /// <summary>The label of the Debrief line, next to "Report (JSON data)".</summary>
        public const string LinePrefix = "Blocks the script ran, oldest first (JSON data): ";

        /// <summary>The results named for each block, beside success and reason; any other block has only those.</summary>
        public static readonly IDictionary<string, string[]> KeyResults = new Dictionary<string, string[]>
        {
            { "transfer_to", new[] { "encounter_ut", "target_pe_actual" } },
            { "return", new[] { "planet_pe", "correction_dv" } },
            { "land", new[] { "distance_to_target", "biome" } },
            { "science", new[] { "kept" } },
            { "drop_parts", new[] { "parts_dropped", "dropped_dv" } },
            { "circularize", new[] { "ecc" } },
            { "tune_pe", new[] { "pe_actual" } },
        };

        private sealed class Entry
        {
            /// <summary>The compact entry, a JSON object.</summary>
            public string Json;
            public bool Success;
            /// <summary>The block's whole result, to tell whether the report is it; null after a load.</summary>
            public IDictionary<string, object> Result;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        // Kept down as they come, so a script that loops over blocks doesn't grow the save.
        private int _collapsedOk;
        private int _collapsedFailed;

        public int Count
        {
            get { return _entries.Count + _collapsedOk + _collapsedFailed; }
        }

        public void Clear()
        {
            _entries.Clear();
            _collapsedOk = 0;
            _collapsedFailed = 0;
        }

        /// <summary>A block returned <paramref name="result"/>.</summary>
        public void Add(string block, IDictionary<string, object> result)
        {
            result = result ?? new Dictionary<string, object>();
            object value;
            var success = result.TryGetValue("success", out value) && value is bool && (bool)value;
            var reason = result.TryGetValue("reason", out value) && value != null ? value.ToString() : "";
            if (reason.Length > MaxReason) reason = reason.Substring(0, MaxReason) + "…";
            var name = string.IsNullOrEmpty(block) ? "?" : block;
            var json = new JsonWriter().BeginObject().Field("block", name).Field("success", success).Field("reason", reason);
            string[] keys;
            if (KeyResults.TryGetValue(name, out keys))
                foreach (var key in keys)
                    if (result.TryGetValue(key, out value)) json.Name(key).Raw(Value(value));
            _entries.Add(new Entry { Json = json.EndObject().ToString(), Success = success, Result = result });
            // One spare over the cap: the report's own block may yet come off the list.
            Collapse(_entries, MaxEntries + 1, ref _collapsedOk, ref _collapsedFailed);
        }

        /// <summary>
        /// The Debrief line, or "" with nothing to list. A block whose result the script reported as it was, or with more
        /// added, is left out: the report already says it.
        /// </summary>
        public string Line(IDictionary<string, object> report)
        {
            var json = Json(report);
            return json == null ? "" : LinePrefix + json;
        }

        /// <summary>The trail as a JSON array, the collapse line last; null with nothing to list.</summary>
        public string Json(IDictionary<string, object> report)
        {
            var listed = _entries.Where(e => report == null || !Reported(e.Result, report)).ToList();
            int ok = _collapsedOk, failed = _collapsedFailed;
            Collapse(listed, MaxEntries, ref ok, ref failed);
            if (listed.Count == 0 && ok + failed == 0) return null;
            var json = new JsonWriter().BeginArray();
            foreach (var entry in listed) json.Raw(entry.Json);
            if (ok + failed > 0)
                json.String(failed == 0 ? ok + " more blocks succeeded" : (ok + failed) + " more blocks: " + ok + " succeeded, " + failed + " failed");
            return json.EndArray().ToString();
        }

        /// <summary>The trail for a save; "" when empty.</summary>
        public string ToJson()
        {
            if (Count == 0) return "";
            var json = new JsonWriter().BeginObject().Field("collapsed_ok", _collapsedOk).Field("collapsed_failed", _collapsedFailed)
                .Name("entries").BeginArray();
            foreach (var entry in _entries) json.Raw(entry.Json);
            return json.EndArray().EndObject().ToString();
        }

        /// <summary>The trail from <see cref="ToJson"/>; empty for "" or text that isn't one.</summary>
        public void Restore(string text)
        {
            Clear();
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var root = JsonReader.Parse(text);
                var items = root["entries"];
                if (items != null)
                    foreach (var node in items.Items.Where(n => n.Kind == JsonKind.Object))
                        _entries.Add(new Entry { Json = node.Raw, Success = JsonNode.Bool(node["success"]) == true });
                _collapsedOk = (int)(JsonNode.Long(root["collapsed_ok"]) ?? 0);
                _collapsedFailed = (int)(JsonNode.Long(root["collapsed_failed"]) ?? 0);
            }
            catch (JsonException)
            {
                Clear();
            }
        }

        /// <summary>Collapses the oldest successful entries, then the oldest failed ones, until at most <paramref name="max"/> are left.</summary>
        private static void Collapse(List<Entry> entries, int max, ref int ok, ref int failed)
        {
            while (entries.Count > max)
            {
                var at = entries.FindIndex(e => e.Success);
                if (at < 0) { at = 0; failed++; }
                else ok++;
                entries.RemoveAt(at);
            }
        }

        /// <summary>Whether <paramref name="report"/> is this block result, as it was or with more added.</summary>
        private static bool Reported(IDictionary<string, object> result, IDictionary<string, object> report)
        {
            if (result == null || result.Count == 0) return false;
            foreach (var pair in result)
            {
                object value;
                if (!report.TryGetValue(pair.Key, out value) || Value(value) != Value(pair.Value)) return false;
            }
            return true;
        }

        private static string Value(object value)
        {
            var json = MissionPrompts.ReportJson(new Dictionary<string, object> { { "v", value } });
            return json.Substring(5, json.Length - 6);
        }
    }
}
