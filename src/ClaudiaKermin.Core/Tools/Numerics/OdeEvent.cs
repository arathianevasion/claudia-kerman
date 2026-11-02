/*
 * Copyright Lamont Granquist, Sebastien Gaggini and the MechJeb contributors
 * SPDX-License-Identifier: LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+
 */
// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;

namespace ClaudiaKermin.Core.Tools.Numerics
{
    /// <summary>
    /// An event for <see cref="Bs3"/>, ported from MechJebLib's <c>ODE/Event.cs</c> (MechJeb2 a958383): a function of the state
    /// and time whose zero ends the integration there. As in MechJeb every event is terminal; a caller that changes its model at
    /// the event (a parachute opening) starts a new solve from the state it ends with. Unlike MechJeb's, it holds no per-solve
    /// values, so one event may be shared between solves.
    /// </summary>
    internal sealed class OdeEvent
    {
        /// <summary>The event function of the state and time; the event is where it crosses zero.</summary>
        public readonly Func<double[], double, double> F;
        /// <summary>Which crossings count: 1 rising through zero, -1 falling through it, 0 either.</summary>
        public readonly int Direction;

        /// <summary>An event on <paramref name="f"/>; <see cref="Bs3.Solve"/> refuses one with no function or a direction outside -1 to 1.</summary>
        public OdeEvent(Func<double[], double, double> f, int direction = 0)
        {
            F = f;
            Direction = direction;
        }

        /// <summary>Whether a step from <paramref name="last"/> to <paramref name="now"/> crosses zero the way this event counts.</summary>
        public bool Crossed(double last, double now)
        {
            var up = last <= 0 && now >= 0;
            var down = last >= 0 && now <= 0;
            return (up && Direction > 0) || (down && Direction < 0) || ((up || down) && Direction == 0);
        }
    }
}
