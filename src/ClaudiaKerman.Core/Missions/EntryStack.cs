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
            var walker = new Walker(byId, anchor);
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

        /// <summary>The shields, nearest home first (by parts between, then part order).</summary>
        private static SnapshotPart ShieldNumbered(IList<SnapshotPart> parts, Dictionary<uint, SnapshotPart> byId, uint? home, int number)
        {
            var depth = new Dictionary<uint, int>();
            var start = home.HasValue && byId.ContainsKey(home.Value) ? byId[home.Value] : parts.FirstOrDefault(p => !p.ParentId.HasValue);
            if (start == null) return null;
            var children = parts.Where(p => p.ParentId.HasValue).ToLookup(p => p.ParentId.Value);
            var queue = new Queue<SnapshotPart>();
            depth[start.Id] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var part = queue.Dequeue();
                var near = children[part.Id].ToList();
                SnapshotPart parent;
                if (part.ParentId.HasValue && byId.TryGetValue(part.ParentId.Value, out parent)) near.Add(parent);
                foreach (var next in near)
                {
                    if (depth.ContainsKey(next.Id)) continue;
                    depth[next.Id] = depth[part.Id] + 1;
                    queue.Enqueue(next);
                }
            }
            return parts.Where(p => p.Tags.Contains("heat_shield") && depth.ContainsKey(p.Id)).OrderBy(p => depth[p.Id]).Skip(number - 1).FirstOrDefault();
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

            public Walker(Dictionary<uint, SnapshotPart> byId, uint anchor)
            {
                _byId = byId;
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
                return part.ParentId.HasValue && _byId.TryGetValue(part.ParentId.Value, out parent) ? parent : null;
            }
        }
    }
}
