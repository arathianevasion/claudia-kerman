// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The craft's next encounter for the Game Snapshot's <c>encounter</c> (#748): Core's own patched-conic prediction (the same
    /// <see cref="BodySystem"/> the Planning Tools use) from the snapshot's current orbit and body table, so Claudia can judge an
    /// approach in a Debrief, where no tool is allowed. The Adapter's patches are not read.
    /// </summary>
    internal sealed class NextEncounter
    {
        /// <summary>How many patches ahead to look (the stock map view shows about as many).</summary>
        private const int Patches = 4;

        public string Body;
        /// <summary>The periapsis above the body's surface, m.</summary>
        public double Pe;
        /// <summary>When that periapsis is passed, s.</summary>
        public double Ut;
        /// <summary>When the craft enters the body's sphere of influence, s.</summary>
        public double SoiUt;

        /// <summary>The first encounter on the craft's path, or null when it meets no other body or the snapshot cannot predict one.</summary>
        public static NextEncounter Of(GameSnapshot snapshot)
        {
            var orbit = snapshot.Orbit;
            if (orbit == null || snapshot.Bodies.Count == 0) return null;
            var system = new BodySystem(snapshot.Bodies);
            var at = system.Find(string.IsNullOrEmpty(orbit.Body) ? snapshot.Body : orbit.Body);
            if (at == null || at.Gm <= 0) return null;
            try
            {
                var start = Conic.FromElements(at.Gm, orbit.SemiMajorAxis, orbit.Eccentricity, orbit.Inclination, orbit.Lan,
                    orbit.ArgumentOfPeriapsis, orbit.MeanAnomalyAtEpoch, orbit.Epoch);
                var path = SingleShooting.Follow(system, at, start, snapshot.Ut, Patches);
                if (path == null) return null;
                for (var i = 0; i + 1 < path.Count; i++)
                {
                    if (path[i].End != PatchEnd.Encounter) continue;
                    var inside = path[i + 1];
                    var pe = inside.Orbit.NextPeriapsis(inside.StartUt);
                    return new NextEncounter
                    {
                        Body = inside.Body.Name,
                        Pe = Math.Round(inside.Orbit.PeR - inside.Body.Radius),
                        Ut = pe ?? inside.StartUt,
                        SoiUt = inside.StartUt,
                    };
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
            return null;
        }
    }
}
