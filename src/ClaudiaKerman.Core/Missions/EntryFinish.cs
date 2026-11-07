// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// How an Entry finishes (#824, ADR 0020): on its chutes, or on its engines with everything kept. <c>reentry</c> asks at block start, through
    /// <c>ADDONS:CLAUDIA:ENTRYFINISH</c>, before it drops anything. <see cref="Decide"/> predicts the touchdown with what the Entry would drop (<see cref="EntryStack"/>)
    /// and the Chute Group left (<see cref="ChuteGroups"/>). Over <see cref="ThresholdFor"/> its body (<see cref="EngineThreshold"/>, or 15 on Kerbin, Eve and Laythe) with an engine that can brake, it keeps everything and predicts again.
    /// Braking, throttle and lighting the engines are the block's.
    /// </summary>
    public sealed class EntryFinish
    {
        /// <summary>The touchdown speed, m/s, over which an Entry lands on its engines: under <see cref="ChuteGroups.TouchdownLimit"/>, which the Craft Check judges by and the block fails on, for the prediction's error (#682).</summary>
        public const double EngineThreshold = 10;

        /// <summary>The threshold on Kerbin, Eve and Laythe (<see cref="Entries.KeepsStackDangerous"/>, #840): a kept stack there runs close to the heat limit, so chutes up to the failure limit beat engines. The prediction's error has no room left there.</summary>
        public const double DangerousStackThreshold = ChuteGroups.TouchdownLimit;

        /// <summary>The touchdown speed over which an Entry on <paramref name="body"/> lands on its engines.</summary>
        public static double ThresholdFor(string body)
        {
            return Entries.KeepsStackDangerous(body) ? DangerousStackThreshold : EngineThreshold;
        }

        /// <summary>It keeps everything and lands on its engines.</summary>
        public bool Powered;
        /// <summary>The touchdown predicted with the Entry's drop and chutes, m/s; -1 unknown.</summary>
        public double Speed = -1;
        /// <summary>The touchdown predicted for the craft as kept, m/s, before any burn; -1 on chutes or unknown.</summary>
        public double KeptSpeed = -1;
        /// <summary>The engines to light: those that can brake in the highest stage still aboard. All of a stage's, as one of a cluster would turn the craft.</summary>
        public readonly List<SnapshotPart> Engines = new List<SnapshotPart>();
        /// <summary>The other engines that can brake, to light if those flame out.</summary>
        public readonly List<SnapshotPart> Spare = new List<SnapshotPart>();
        private string _unknown = "";
        private bool _noEngine;

        /// <summary>Whether this prediction calls for engines: over the threshold, or no prediction and nothing to open.</summary>
        public static bool NeedsEngines(EntryChutes entry)
        {
            return entry.TouchdownSpeed > ThresholdFor(entry.Body) || (entry.TouchdownSpeed < 0 && entry.Chutes.Count == 0);
        }

        /// <summary>
        /// The decision for the craft in <paramref name="snapshot"/>: <paramref name="gone"/> the parts the Entry would drop, <paramref name="lastEntry"/> as for its
        /// Chute Group, <paramref name="chutes"/> false when none will open. A craft with no engine that can brake stays on the chutes path: what else opens is #827's.
        /// </summary>
        public static EntryFinish Decide(GameSnapshot snapshot, ICollection<uint> gone, bool lastEntry, bool chutes)
        {
            var dropped = ChuteGroups.ForEntryNow(snapshot, lastEntry, gone, chutes);
            var finish = new EntryFinish { Speed = dropped.TouchdownSpeed, _unknown = dropped.Unpredicted };
            if (!NeedsEngines(dropped)) return finish;
            var fuelled = snapshot.Craft.Parts.Where(p => p.Tags.Contains("engine") && p.EngineFuelled).ToList();
            if (fuelled.Count == 0)
            {
                finish._noEngine = true;
                return finish;
            }
            var top = fuelled.Max(p => p.Stage);
            finish.Powered = true;
            finish.Engines.AddRange(fuelled.Where(p => p.Stage == top));
            finish.Spare.AddRange(fuelled.Where(p => p.Stage != top));
            finish.KeptSpeed = ChuteGroups.ForEntryNow(snapshot, lastEntry, null, chutes).TouchdownSpeed;
            return finish;
        }

        /// <summary>The decision, its predicted speeds and which engine, for the block call's log line.</summary>
        public string Why()
        {
            var speed = Speed >= 0 ? Speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "speed unknown" + (_unknown.Length > 0 ? " (" + _unknown + ")" : "");
            if (!Powered) return "chutes, " + speed + (_noEngine ? ", no engine with fuel" : "");
            return "engines, keeping everything: " + speed + " with its drop and chutes, "
                + (KeptSpeed >= 0 ? KeptSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "speed unknown") + " as kept, on "
                + string.Join(", ", Engines.Select(p => p.Title.Length > 0 ? p.Title : p.Name).ToArray());
        }
    }
}
