// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What an Entry drops, from its Entry Shield (ADR 0018): the parts on the far side of it from home. It fires nothing (ADR 0014): the
    /// <c>reentry</c> block fires the decouplers. A decoupler stays with the part above it, so the one next to the shield is not the stack;
    /// a heat shield is never the decoupler that drops one (#516).
    /// </summary>
    public sealed class EntryStack
    {
        /// <summary>
        /// Seconds before the atmosphere <c>reentry</c> drops the stack (#815; <c>ENTRYDROP</c>'s <c>lead</c>): late, so the drop's kick (about 0.1 m/s)
        /// barely moves the landing, and early enough that the stack is clear above the air. On the capsule model a 0.1 m/s kick this far out moves a
        /// Mun return's landing 0.6 km at most and a Duna low-orbit descent's 1.8 km, and the capsule is about 80 m off the stack's path at the air (EntryDropLeadTests).
        /// </summary>
        public const double DropLeadS = 600;

        /// <summary>
        /// Why <c>reentry</c> with <c>separate</c> "auto" fails on this stack above the air, or "" (#815; it was the block's <c>reentry_stack_refusal</c>).
        /// At block start (<paramref name="dropError"/> null) a stack it can't drop: one with <see cref="Stuck"/> parts, or one that takes the part
        /// running the script (<paramref name="takesCore"/>). After its own drop, any stack still on, with what the drop said ("" if it said nothing).
        /// </summary>
        public string Refusal(string dropError, bool takesCore)
        {
            if (Parts.Count == 0) return "";
            var why = dropError;
            if (why == null)
            {
                if (Stuck.Count > 0) why = string.Join(", ", Names(Stuck).ToArray()) + ": no decoupler between it and the heat shield";
                else if (takesCore) why = "it would drop the part running the script";
                else return "";
            }
            return "stack attached below the heat shield: " + string.Join(", ", Names(Parts).ToArray()) + "; drop it first or use separate" + (why == "" ? "" : " (separate: " + why + ")");
        }

        /// <summary>The Entry Shield; null for none, and then nothing drops and everything below stays empty.</summary>
        public SnapshotPart Shield;
        /// <summary>The stack: what the Entry drops, stuck parts included.</summary>
        public readonly List<SnapshotPart> Parts = new List<SnapshotPart>();
        /// <summary>The decouplers nearest the shield that drop the stack.</summary>
        public readonly List<SnapshotPart> Decouplers = new List<SnapshotPart>();
        /// <summary>The parts those decouplers take.</summary>
        public readonly List<SnapshotPart> Gone = new List<SnapshotPart>();
        /// <summary>Stack parts with no decoupler between them and the shield: the Entry can't drop them.</summary>
        public readonly List<SnapshotPart> Stuck = new List<SnapshotPart>();

        /// <summary>
        /// The stack of <paramref name="parts"/> for shield number <paramref name="number"/> counted from <paramref name="home"/> (the part the
        /// crew rides in; the root if null) outward, by parts between, never by stage: stock shields aren't in the staging list. With home under
        /// the shield (a craft built from the far side) no decoupler drops the rest without taking home, so all of it is stuck.
        /// </summary>
        public static EntryStack Of(IList<SnapshotPart> parts, uint? home, int number)
        {
            var stack = new EntryStack();
            var byId = new Dictionary<uint, SnapshotPart>();
            foreach (var part in parts) byId[part.Id] = part;
            stack.Shield = ShieldNumbered(parts, byId, home, number);
            if (stack.Shield == null) return stack;

            var anchor = stack.Shield.Id;
            var walker = new Walker(byId, anchor, PartTree.TowardHome(parts, home));
            var reversed = home.HasValue && byId.ContainsKey(home.Value) && walker.Up(byId[home.Value]).Under;
            var seen = new HashSet<uint>();
            foreach (var part in parts)
                if (part.Id != anchor) stack.Classify(part, walker.Up(part), reversed, seen);
            return stack;
        }

        private void Classify(SnapshotPart part, Walk way, bool reversed, HashSet<uint> seen)
        {
            if (reversed)
            {
                if (way.Under) return;
                Parts.Add(part);
                Stuck.Add(part);
            }
            else if (way.Under)
            {
                if (way.Top == null)
                {
                    Parts.Add(part);
                    Stuck.Add(part);
                }
                else if (way.Top != part)
                {
                    Parts.Add(part);
                    Gone.Add(part);
                    if (seen.Add(way.Top.Id)) Decouplers.Add(way.Top);
                }
            }
        }

        /// <summary>The names of parts, each once, in order.</summary>
        public static List<string> Names(IEnumerable<SnapshotPart> parts)
        {
            return parts.Select(p => p.Name).Distinct().ToList();
        }

        /// <summary>
        /// The shields on the stack, nearest home first (by parts between, then part order). The walk follows stack attachments only and
        /// stops at a docking port: a shield on a radial branch or past a port (another vessel) takes no number (#843).
        /// </summary>
        private static SnapshotPart ShieldNumbered(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> byId, uint? home, int number)
        {
            var depth = Reach(parts, byId, home, false);
            return parts.Where(p => p.Tags.Contains("heat_shield") && depth.ContainsKey(p.Id)).OrderBy(p => depth[p.Id]).Skip(number - 1).FirstOrDefault();
        }

        /// <summary>
        /// The heat shields reached along stack attachments only by crossing a docking port (#845): the ones <see cref="Of"/> does not count.
        /// It is the same walk with the port let through, so a radial shield is in neither.
        /// </summary>
        public static List<SnapshotPart> ShieldsPastPorts(IList<SnapshotPart> parts, uint? home)
        {
            var byId = new Dictionary<uint, SnapshotPart>();
            foreach (var part in parts) byId[part.Id] = part;
            var counted = Reach(parts, byId, home, false);
            var all = Reach(parts, byId, home, true);
            return parts.Where(p => p.Tags.Contains("heat_shield") && all.ContainsKey(p.Id) && !counted.ContainsKey(p.Id)).ToList();
        }

        /// <summary>The parts reached from home over stack attachments, with their parts-between depth; a docking port is reached but not passed unless <paramref name="crossPorts"/>.</summary>
        private static Dictionary<uint, int> Reach(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> byId, uint? home, bool crossPorts)
        {
            var depth = new Dictionary<uint, int>();
            var start = home.HasValue && byId.ContainsKey(home.Value) ? byId[home.Value] : parts.FirstOrDefault(p => !p.ParentId.HasValue);
            if (start == null) return depth;
            var children = parts.Where(p => p.ParentId.HasValue && !p.SurfaceAttached).ToLookup(p => p.ParentId.Value);
            var queue = new Queue<SnapshotPart>();
            depth[start.Id] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var part = queue.Dequeue();
                if (!crossPorts && part != start && part.Tags.Contains("docking_port")) continue;
                var near = children[part.Id].ToList();
                SnapshotPart parent;
                if (part.ParentId.HasValue && (part == start || !part.SurfaceAttached) && byId.TryGetValue(part.ParentId.Value, out parent)) near.Add(parent);
                foreach (var next in near)
                {
                    if (depth.ContainsKey(next.Id)) continue;
                    depth[next.Id] = depth[part.Id] + 1;
                    queue.Enqueue(next);
                }
            }
            return depth;
        }

        /// <summary>A walk up from a part: whether it reached the anchor, and the decoupler on the way nearest it.</summary>
        private struct Walk
        {
            public bool Under;
            public SnapshotPart Top;
        }

        private sealed class Walker
        {
            private readonly Dictionary<uint, SnapshotPart> _byId;
            private readonly uint _anchor;

            private readonly Dictionary<uint, uint?> _toward;

            public Walker(Dictionary<uint, SnapshotPart> byId, uint anchor, Dictionary<uint, uint?> toward)
            {
                _byId = byId;
                _toward = toward;
                _anchor = anchor;
            }

            public Walk Up(SnapshotPart part)
            {
                SnapshotPart top = null;
                for (var cur = part; cur != null; cur = Parent(cur))
                {
                    if (cur.Id == _anchor) return new Walk { Under = true, Top = top };
                    if (cur.Tags.Contains("decoupler") && !cur.Tags.Contains("heat_shield")) top = cur;
                }
                return new Walk();
            }

            private SnapshotPart Parent(SnapshotPart part)
            {
                SnapshotPart parent;
                uint? next;
                return _toward.TryGetValue(part.Id, out next) && next.HasValue && _byId.TryGetValue(next.Value, out parent) ? parent : null;
            }
        }
    }
}
