// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The craft an Entry flies (#814): what is gone before the air and whether its Entry Shield jettisons, by <c>reentry</c>'s own rules from
    /// <see cref="EntryStack"/> and <see cref="EntryFinish"/>, so <c>return</c>, its aimed correction and <c>deorbit</c> predict the landing of the craft
    /// that enters. A stack the Entry drops is left out and the shield jettisons, if a decoupler is on or beside it (a stuck stack stays, the shield goes with it); with landing legs above the shield it drops on a powered finish too (#940). No Entry Shield or a stack kept (on the engines, or
    /// by <c>separate</c> "none") is the craft as it is; a kept craft's shield jettisons only with nothing below it, as <see cref="Jettisons"/> has it, and none when <c>jettison_stage</c> is "none".
    /// A block reads it once, at its start; the predictor then flies the parts it names. It fires nothing.
    /// </summary>
    public sealed class EntryCraft
    {
        /// <summary>The parts gone before the air: the block's own drop, and the stack when the Entry drops it.</summary>
        public readonly List<SnapshotPart> LeftOut = new List<SnapshotPart>();
        /// <summary>The Entry Shield when it jettisons; null when it stays (none, or a stack kept on it).</summary>
        public SnapshotPart ShieldDropped;
        /// <summary>The whole craft now, the craft left once <see cref="LeftOut"/> has gone, and the propellant in the parts that go, tonnes.</summary>
        public double WholeT;
        public double KeptT;
        public double GonePropellantT;
        /// <summary>Lit engines that stay: any at all and the kept tanks pay for a burn before the drop (<see cref="DropGuard.AfterBurn"/>).</summary>
        public int BurnersKept;
        /// <summary>The parts that enter and the shield's fate, for comparing the craft aimed for with the craft flown: parts already gone don't count.</summary>
        public string Key = "";
        private string _why = "";

        /// <summary>
        /// The craft the Entry flies, for the craft in <paramref name="snapshot"/> once <paramref name="blockDrop"/> (parts the block drops itself, as
        /// <c>deorbit</c>'s <c>separate</c>) has gone: shield number <paramref name="number"/> from <paramref name="home"/>, its Chute Group the Last
        /// Entry's for number 1, <paramref name="chutes"/> false when none will open, <paramref name="drop"/> false for <c>reentry</c>'s <c>separate</c> "none".
        /// </summary>
        public static EntryCraft Of(GameSnapshot snapshot, ICollection<uint> blockDrop, uint? home, int number, bool chutes, bool drop, string jettisonStage = "auto", uint? core = null)
        {
            var left = new HashSet<uint>(blockDrop ?? new uint[0]);
            var after = Without(snapshot, left);
            var stack = EntryStack.Of(after.Craft.Parts, home, number);
            var finish = stack.Shield == null ? null : EntryFinish.For(after, stack, drop, number == 1, chutes, core);
            return From(snapshot, left, stack, finish, drop, jettisonStage);
        }

        /// <summary><paramref name="snapshot"/> as it reads once the parts <paramref name="gone"/> have left the craft.</summary>
        internal static GameSnapshot Without(GameSnapshot snapshot, ICollection<uint> gone)
        {
            var after = snapshot.At(snapshot.Ut);
            after.Craft = new SnapshotCraft
            {
                Name = snapshot.Craft.Name, Stage = snapshot.Craft.Stage, Seats = snapshot.Craft.Seats, VesselId = snapshot.Craft.VesselId,
                Parts = snapshot.Craft.Parts.Where(p => !gone.Contains(p.Id)).ToList(),
                MassTonnes = snapshot.Craft.MassTonnes - snapshot.Craft.Parts.Where(p => gone.Contains(p.Id)).Sum(p => p.MassT),
            };
            return after;
        }

        /// <summary>The same from <paramref name="stack"/> and <paramref name="finish"/> already read (null with no shield), as <c>ENTRYFINISH</c> has them.</summary>
        public static EntryCraft From(GameSnapshot snapshot, ICollection<uint> blockDrop, EntryStack stack, EntryFinish finish, bool drop, string jettisonStage = "auto")
        {
            var left = new HashSet<uint>(blockDrop ?? new uint[0]);
            var craft = new EntryCraft { WholeT = snapshot.Craft.MassTonnes };
            if (stack.Shield == null) craft._why = "as it is, no Entry Shield";
            else
            {
                var kept = finish.KeepsStack || !drop;
                if (kept) craft._why = "as it is, " + (stack.Parts.Count > 0 ? "its stack kept" : "nothing below the shield") + (finish.Powered ? ", on its engines" : "");
                else if (stack.Stuck.Count > 0) craft._why = "as it is, " + string.Join(", ", EntryStack.Names(stack.Stuck).ToArray()) + " stuck";
                else
                {
                    foreach (var part in stack.Leaves) left.Add(part.Id);
                    craft._why = stack.Gone.Count > 0 ? "without its stack (" + string.Join(", ", EntryStack.Names(stack.Gone).ToArray()) + ")" : "nothing below the shield";
                }
                if (Jettisons(jettisonStage, kept, stack.Parts.Count > 0) && Droppable(snapshot, left, stack.Shield)) craft.ShieldDropped = stack.Shield;
            }
            craft.LeftOut.AddRange(snapshot.Craft.Parts.Where(p => left.Contains(p.Id)));
            craft.KeptT = craft.WholeT - craft.LeftOut.Sum(p => p.MassT);
            var burning = snapshot.Craft.Parts.Where(Burns).ToList();
            // Nothing burns yet (a flamed-out last stage, the next one unstaged): the engines the next staging lights pay; none known, every resource, as before.
            var payers = burning.Count > 0 ? burning : NextStaged(snapshot);
            var burned = new HashSet<string>(payers.SelectMany(p => p.EnginePropellants));
            craft.GonePropellantT = craft.LeftOut.Sum(p => p.Resources.Count > 0 && burned.Count > 0
                ? p.Resources.Where(r => burned.Contains(r.Name)).Sum(r => r.Amount * r.Density)
                : (p.DryMassT > 0 ? System.Math.Max(0, p.MassT - p.DryMassT) : 0)); // no resources or engines read (an older recording): every resource, as before
            craft.BurnersKept = burning.Count(p => !left.Contains(p.Id));
            craft.Key = string.Join(",", snapshot.Craft.Parts.Where(p => !left.Contains(p.Id)).Select(p => p.Id).OrderBy(id => id)
                .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray()) + (craft.ShieldDropped != null ? "|shield jettisoned" : "|shield kept");
            return craft;
        }

        /// <summary>
        /// The one rule for whether <c>reentry</c> jettisons the Entry Shield (#869; it was <c>reentry_jettison_uid</c>): unless <paramref name="jettisonStage"/> is
        /// "none", or the stack below it is kept on (<paramref name="stackKept"/>: <c>separate</c> "none", which a powered entry also sets) with a stack below
        /// (<paramref name="stackBelow"/>). The shield's decoupler takes what is on both sides (#516), so a stuck stack goes with it. Nothing below: it goes.
        /// </summary>
        public static bool Jettisons(string jettisonStage, bool stackKept, bool stackBelow)
        {
            return !string.Equals(jettisonStage, "none", System.StringComparison.OrdinalIgnoreCase) && !(stackKept && stackBelow);
        }

        /// <summary>The unlit engines in the highest stage still to fire (at or below the craft's current stage): what the next staging lights.</summary>
        private static List<SnapshotPart> NextStaged(GameSnapshot snapshot)
        {
            var unlit = snapshot.Craft.Parts.Where(p => p.Tags.Contains("engine") && !p.EngineLit && p.EnginePropellants.Count > 0 && p.Stage <= snapshot.Craft.Stage).ToList();
            if (unlit.Count == 0) return unlit;
            var next = unlit.Max(p => p.Stage);
            return unlit.Where(p => p.Stage == next).ToList();
        }

        /// <summary>An engine burning now: lit with available thrust above 0, as <c>drop_parts_kept</c> counts it.</summary>
        private static bool Burns(SnapshotPart part)
        {
            return part.EngineLit && part.Tags.Contains("engine") && part.EngineAvailableThrustKn > 0;
        }

        /// <summary>
        /// Whether a decoupler on the craft that is left (not in <paramref name="left"/>) is the shield or sits against it, as <c>reentry_shield_modules</c>
        /// finds them: with none, nothing jettisons the shield and it stays on. The snapshot's "decoupler" tag has no ModuleJettison and can't tell a
        /// decoupler already fired, so a shield beside only those reads as kept, or as jettisoned.
        /// </summary>
        private static bool Droppable(GameSnapshot snapshot, HashSet<uint> left, SnapshotPart shield)
        {
            return snapshot.Craft.Parts.Any(p => !left.Contains(p.Id) && p.Tags.Contains("decoupler")
                && (p.Id == shield.Id || p.ParentId == shield.Id || shield.ParentId == p.Id));
        }

        /// <summary>
        /// Whether the craft aimed for (<paramref name="aimedKey"/>, read earlier) is the one flown (<paramref name="flownKey"/>, read from <paramref name="snapshot"/>):
        /// the same shield fate, and the same parts entering of those still aboard, so a part staged away since (an empty stage, drop_parts) is no difference.
        /// </summary>
        public static bool Same(string aimedKey, string flownKey, GameSnapshot snapshot)
        {
            var aboard = new HashSet<string>(snapshot.Craft.Parts.Select(p => p.Id.ToString(CultureInfo.InvariantCulture)));
            Func<string, string> now = key =>
            {
                var bar = key.IndexOf('|');
                var ids = (bar < 0 ? key : key.Substring(0, bar)).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Where(aboard.Contains);
                return string.Join(",", ids.ToArray()) + (bar < 0 ? "" : key.Substring(bar));
            };
            return now(aimedKey) == now(flownKey);
        }

        /// <summary>The craft in a few words for the log, with its mass.</summary>
        public string Describe()
        {
            return _why + (ShieldDropped != null ? ", shield jettisoned" : ", shield kept") + ", " + KeptT.ToString("0.00", CultureInfo.InvariantCulture) + " t";
        }
    }
}
