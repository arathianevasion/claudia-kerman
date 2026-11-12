// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// What <c>launch</c> and <c>ascend</c> clear from the ground before the throttle comes up (#945): for each engine that gives thrust, the decoupler
    /// nearest it on its far side from home, when one sits under it, with the parts that go with it. It fires nothing (ADR 0014): the blocks do, with
    /// <c>claudia_fire_drop</c>. Engines in later stages give no thrust yet, so a launch craft's upper stages with stages under them name nothing. A launch
    /// clamp is no decoupler (the adapter doesn't tag it), a heat shield is named like any decoupler when it is the nearest (#950; its own omni decoupler releases both neighbours), and a decoupler is left
    /// alone when what it would take holds a thrusting engine, the root or the part running the script.
    /// </summary>
    public sealed class LiftoffClear
    {
        /// <summary>The decouplers to fire, nearest each thrusting engine, each once.</summary>
        public readonly List<SnapshotPart> Decouplers = new List<SnapshotPart>();
        /// <summary>The parts those decouplers take.</summary>
        public readonly List<SnapshotPart> Gone = new List<SnapshotPart>();
        /// <summary>The parts still under a thrusting engine once those go: no decoupler to fire, or one left alone. Launch clamps and thrusting engines are not among them.</summary>
        public readonly List<SnapshotPart> Stuck = new List<SnapshotPart>();

        /// <summary>The decouplers under the thrusting engines of <paramref name="parts"/>, from <paramref name="home"/> (null: the root) and the part running the script (<paramref name="core"/>, null when not known).</summary>
        public static LiftoffClear Of(IList<SnapshotPart> parts, uint? home, uint? core)
        {
            var clear = new LiftoffClear();
            var toward = PartTree.TowardHome(parts, home);
            var below = parts.Where(p => toward[p.Id].HasValue && !p.SurfaceAttached).ToLookup(p => toward[p.Id].Value);
            var every = parts.Where(p => toward[p.Id].HasValue).ToLookup(p => toward[p.Id].Value);
            foreach (var engine in parts.Where(Thrusts))
            {
                var decoupler = Nearest(engine, below);
                if (decoupler == null || clear.Decouplers.Contains(decoupler)) continue;
                var gone = Subtree(decoupler, every);
                if (gone.Any(p => !toward[p.Id].HasValue || p.Id == core || Thrusts(p))) continue;
                clear.Decouplers.Add(decoupler);
                clear.Gone.AddRange(gone.Where(p => !clear.Gone.Contains(p)));
            }
            foreach (var engine in parts.Where(Thrusts))
            {
                foreach (var part in Subtree(engine, every).Skip(1))
                {
                    if (!clear.Gone.Contains(part) && !clear.Stuck.Contains(part) && !Thrusts(part) && !part.Name.StartsWith("launchClamp")) clear.Stuck.Add(part);
                }
            }
            return clear;
        }

        private static bool Thrusts(SnapshotPart part)
        {
            return part.Tags.Contains("engine") && part.EngineAvailableThrustKn > 0;
        }

        /// <summary>The decoupler fewest parts under <paramref name="engine"/>; null for none.</summary>
        private static SnapshotPart Nearest(SnapshotPart engine, ILookup<uint, SnapshotPart> below)
        {
            var level = below[engine.Id].ToList();
            while (level.Count > 0)
            {
                var found = level.FirstOrDefault(p => p.Tags.Contains("decoupler"));
                if (found != null) return found;
                level = level.SelectMany(p => below[p.Id]).ToList();
            }
            return null;
        }

        /// <summary>Everything on the far side of <paramref name="top"/> from home, radially attached parts too, the part itself too.</summary>
        private static List<SnapshotPart> Subtree(SnapshotPart top, ILookup<uint, SnapshotPart> below)
        {
            var all = new List<SnapshotPart> { top };
            for (var i = 0; i < all.Count; i++) all.AddRange(below[all[i].Id]);
            return all;
        }
    }
}
