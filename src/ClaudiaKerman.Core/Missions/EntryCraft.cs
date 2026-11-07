// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The craft an Entry flies (#814): what is gone before the air and whether its Entry Shield jettisons, by <c>reentry</c>'s own rules from
    /// <see cref="EntryStack"/> and <see cref="EntryFinish"/>, so <c>return</c>, its aimed correction and <c>deorbit</c> predict the landing of the craft
    /// that enters. A stack the Entry drops is left out and the shield jettisons. No Entry Shield, a stuck stack, or a stack kept (on the engines, or
    /// by <c>separate</c> "none") is the craft as it is; a kept craft's shield jettisons only with nothing below it, as <c>reentry_jettison_uid</c> has it.
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
        public static EntryCraft Of(GameSnapshot snapshot, ICollection<uint> blockDrop, uint? home, int number, bool chutes, bool drop)
        {
            var left = new HashSet<uint>(blockDrop ?? new uint[0]);
            var after = snapshot.At(snapshot.Ut);
            after.Craft = new SnapshotCraft
            {
                Name = snapshot.Craft.Name, Stage = snapshot.Craft.Stage, Seats = snapshot.Craft.Seats, VesselId = snapshot.Craft.VesselId,
                Parts = snapshot.Craft.Parts.Where(p => !left.Contains(p.Id)).ToList(),
                MassTonnes = snapshot.Craft.MassTonnes - snapshot.Craft.Parts.Where(p => left.Contains(p.Id)).Sum(p => p.MassT),
            };
            var stack = EntryStack.Of(after.Craft.Parts, home, number);
            var finish = stack.Shield == null ? null : EntryFinish.Decide(after, drop ? stack.Gone.Select(p => p.Id).ToList() : new List<uint>(), number == 1, chutes);
            return From(snapshot, left, stack, finish, drop);
        }

        /// <summary>The same from <paramref name="stack"/> and <paramref name="finish"/> already read (null with no shield), as <c>ENTRYFINISH</c> has them.</summary>
        public static EntryCraft From(GameSnapshot snapshot, ICollection<uint> blockDrop, EntryStack stack, EntryFinish finish, bool drop)
        {
            var left = new HashSet<uint>(blockDrop ?? new uint[0]);
            var craft = new EntryCraft { WholeT = snapshot.Craft.MassTonnes };
            if (stack.Shield == null) craft._why = "as it is, no Entry Shield";
            else
            {
                var kept = finish.Powered || !drop;
                if (kept) craft._why = "as it is, " + (stack.Parts.Count > 0 ? "its stack kept" : "nothing below the shield") + (finish.Powered ? ", on its engines" : "");
                else if (stack.Stuck.Count > 0) craft._why = "as it is, " + string.Join(", ", EntryStack.Names(stack.Stuck).ToArray()) + " stuck";
                else
                {
                    foreach (var part in stack.Gone) left.Add(part.Id);
                    craft._why = stack.Gone.Count > 0 ? "without its stack (" + string.Join(", ", EntryStack.Names(stack.Gone).ToArray()) + ")" : "nothing below the shield";
                }
                if (stack.Stuck.Count == 0 && (!kept || stack.Parts.Count == 0)) craft.ShieldDropped = stack.Shield;
            }
            craft.LeftOut.AddRange(snapshot.Craft.Parts.Where(p => left.Contains(p.Id)));
            craft.KeptT = craft.WholeT - craft.LeftOut.Sum(p => p.MassT);
            craft.GonePropellantT = craft.LeftOut.Where(p => p.DryMassT > 0).Sum(p => System.Math.Max(0, p.MassT - p.DryMassT));
            craft.BurnersKept = snapshot.Craft.Parts.Count(p => p.EngineLit && p.Tags.Contains("engine") && !left.Contains(p.Id));
            craft.Key = string.Join(",", snapshot.Craft.Parts.Where(p => !left.Contains(p.Id)).Select(p => p.Id).OrderBy(id => id)
                .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray()) + (craft.ShieldDropped != null ? "|shield jettisoned" : "|shield kept");
            return craft;
        }

        /// <summary>The craft in a few words for the log, with its mass.</summary>
        public string Describe()
        {
            return _why + (ShieldDropped != null ? ", shield jettisoned" : ", shield kept") + ", " + KeptT.ToString("0.00", CultureInfo.InvariantCulture) + " t";
        }
    }
}
