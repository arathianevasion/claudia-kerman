// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
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
        /// <summary>The engines to light: those that can brake in the highest stage that can stop the fall, or with none that can the one with the most thrust (#842, <see cref="Choose"/>). All of a stage's, as one of a cluster would turn the craft.</summary>
        public readonly List<SnapshotPart> Engines = new List<SnapshotPart>();
        /// <summary>The other engines that can brake, to light if those flame out.</summary>
        public readonly List<SnapshotPart> Spare = new List<SnapshotPart>();
        private string _unknown = "";
        private bool _noEngine;
        private string _stageNote = "";

        /// <summary>How much more than the weight an engine stage's thrust at the body's surface must be to stop the fall (#842). Core has no braking figure of its own: the block brakes by <c>BrakeGuidance</c> in flight, from where it stands.</summary>
        public const double StopsTheFallTimesWeight = 1.5;

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
            var fuelled = snapshot.Craft.Parts.Where(p => p.Tags.Contains("engine") && p.EngineFuelled && WillLight(snapshot, p)).ToList();
            if (fuelled.Count == 0)
            {
                finish._noEngine = true;
                return finish;
            }
            var chosen = Choose(snapshot, dropped.Body, fuelled, out finish._stageNote);
            finish.Powered = true;
            finish.Engines.AddRange(fuelled.Where(p => p.Stage == chosen));
            finish.Spare.AddRange(fuelled.Where(p => p.Stage != chosen));
            finish.KeptSpeed = ChuteGroups.ForEntryNow(snapshot, lastEntry, null, chutes).TouchdownSpeed;
            return finish;
        }

        /// <summary>
        /// Whether the block's <c>activate</c> will light <paramref name="engine"/> at the drop point (#879), from what the snapshot read at block start. The adapter's
        /// <see cref="SnapshotPart.EngineFuelled"/> tries every engine module, a disabled mode's too; <see cref="SnapshotPart.EnginePropellants"/> holds only the running
        /// module's. So with the parts' resources read, an engine with none running (disabled), or one whose running mode burns a propellant nothing aboard holds, won't
        /// light. Intake resources (a name starting "Intake": IntakeAir, IntakeAtm) are not counted: no part holds them, the air supplies them, and an air-breathing mode
        /// stays as accepted as before #879. Resources not read (an older recording): <see cref="SnapshotPart.EngineFuelled"/> stands.
        /// </summary>
        private static bool WillLight(GameSnapshot snapshot, SnapshotPart engine)
        {
            if (!snapshot.Craft.Parts.Any(p => p.Resources.Count > 0)) return true;
            return engine.EnginePropellants.Count > 0
                && engine.EnginePropellants.All(name => name.StartsWith("Intake", StringComparison.Ordinal)
                    || snapshot.Craft.Parts.Sum(p => p.Resources.Where(r => r.Name == name).Sum(r => r.Amount)) > 0);
        }

        /// <summary>
        /// The stage to land on (#842): going down from the highest stage with a fuelled engine, the first whose engines together give thrust at the body's surface
        /// (<see cref="SeaLevelThrust"/>: a vacuum engine counts for its low thrust there) of at least <see cref="StopsTheFallTimesWeight"/> times the weight of the craft as kept.
        /// With none that can, the stage with the most thrust; a tie goes to the higher stage. <paramref name="note"/> says why when it isn't the highest.
        /// </summary>
        public static int Choose(GameSnapshot snapshot, string bodyName, IList<SnapshotPart> fuelled, out string note)
        {
            var body = SequenceWords.Body(snapshot, bodyName);
            var atm = body != null ? body.SeaLevelAtm : 0;
            var need = StopsTheFallTimesWeight * snapshot.Craft.MassTonnes * (body != null ? body.SurfaceGravity : 0);
            var stages = fuelled.Select(p => p.Stage).Distinct().OrderByDescending(s => s).ToList();
            var thrust = stages.ToDictionary(s => s, s => SeaLevelThrust.Of(fuelled.Where(p => p.Stage == s), atm));
            note = "";
            var top = stages[0];
            if (body == null)
            {
                note = "stage " + top + ", the highest: the Game Snapshot has no body " + bodyName + " to judge thrust by";
                return top;
            }
            foreach (var stage in stages)
            {
                if (thrust[stage] < need) continue;
                if (stage != top)
                    note = "stage " + stage + ", the highest that can stop the fall: stage " + string.Join(", ", stages.TakeWhile(s => s != stage).Select(s => s + " gives " + Kn(thrust[s])).ToArray()) + " at the surface, needing " + Kn(need);
                return stage;
            }
            var strongest = stages.OrderByDescending(s => thrust[s]).ThenByDescending(s => s).First();
            note = "stage " + strongest + ", none can stop the fall (needs " + Kn(need) + " at the surface), so the most thrust: " + Kn(thrust[strongest]);
            return strongest;
        }

        private static string Kn(double kn)
        {
            return kn.ToString("0.#", CultureInfo.InvariantCulture) + " kN";
        }

        /// <summary>The decision, its predicted speeds and which engine, for the block call's log line.</summary>
        public string Why()
        {
            var speed = Speed >= 0 ? Speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "speed unknown" + (_unknown.Length > 0 ? " (" + _unknown + ")" : "");
            if (!Powered) return "chutes, " + speed + (_noEngine ? ", no engine with fuel" : "");
            return "engines, keeping everything: " + speed + " with its drop and chutes, "
                + (KeptSpeed >= 0 ? KeptSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "speed unknown") + " as kept, on "
                + string.Join(", ", Engines.Select(p => p.Title.Length > 0 ? p.Title : p.Name).ToArray())
                + (_stageNote.Length > 0 ? " (" + _stageNote + ")" : "");
        }
    }
}
