// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>
    /// The attitude an Entry flies, for reading a craft's drag along it (#924): shield first with home behind it, so <c>return</c> at the Mun and <c>reentry</c> above the air read the same
    /// faces of the same craft. It is read from the parts that enter, never from the control point the craft has when the block asks: that may be on the stack, or the pod's Reversed one.
    /// </summary>
    public sealed class EntryAttitude
    {
        /// <summary>The part the crew rides in, else the first command part, else the root of what enters; null with no parts.</summary>
        public SnapshotPart Home;
        /// <summary>The heat shield nearest home, whichever shield number this Entry flies (they lie on one axis); null when none enters, and then the axis is home's own.</summary>
        public SnapshotPart Shield;

        /// <summary>The craft's nose by this attitude, given each part's own up and its place (any one frame): the shield's up turned toward home, or home's up with no shield; null with no parts.</summary>
        public double[] NoseOf(Func<uint, double[]> up, Func<uint, double[]> place)
        {
            if (Home == null) return null;
            if (Shield == null) return up(Home.Id);
            var from = place(Shield.Id);
            var to = place(Home.Id);
            return Nose(up(Shield.Id), new[] { to[0] - from[0], to[1] - from[1], to[2] - from[2] });
        }

        /// <summary>The attitude of <paramref name="parts"/> without those in <paramref name="gone"/> (the stack the Entry drops, by <see cref="SnapshotPart.Id"/>).</summary>
        public static EntryAttitude Of(IList<SnapshotPart> parts, ICollection<uint> gone)
        {
            var kept = parts.Where(p => gone == null || !gone.Contains(p.Id)).ToList();
            var ids = new HashSet<uint>(kept.Select(p => p.Id));
            var command = PartTree.CommandPart(kept, null);
            var home = kept.FirstOrDefault(p => p.Id == command) ?? kept.FirstOrDefault(p => !p.ParentId.HasValue || !ids.Contains(p.ParentId.Value));
            return new EntryAttitude { Home = home, Shield = home == null ? null : EntryStack.Of(kept, home.Id, 1).Shield };
        }

        /// <summary>
        /// The craft's nose (an entry holds it at surface retrograde, so the air comes from the other end): <paramref name="axis"/>, a part's own up, turned to point toward home along
        /// <paramref name="towardHome"/>, so a shield built upside down still leads. Unturned when home's way can't be told (the two parts at one place).
        /// </summary>
        public static double[] Nose(double[] axis, double[] towardHome)
        {
            var dot = axis[0] * towardHome[0] + axis[1] * towardHome[1] + axis[2] * towardHome[2];
            return dot < 0 ? new[] { -axis[0], -axis[1], -axis[2] } : new[] { axis[0], axis[1], axis[2] };
        }
    }
}
