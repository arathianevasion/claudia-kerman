// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// What a burn goes to (#650): a name, the body it orbits and its orbit there. A body of the snapshot's table or the Mission's
    /// Target Vessel (<see cref="VesselTarget"/>) converts to it, so the burn tools take one target, not a pair.
    /// </summary>
    internal sealed class BurnTarget
    {
        private readonly SnapshotBody _body;
        private readonly VesselTarget _vessel;

        private BurnTarget(SnapshotBody body, VesselTarget vessel)
        {
            _body = body;
            _vessel = vessel;
        }

        /// <summary>The body's name, or the vessel's.</summary>
        public string Name { get { return _vessel != null ? _vessel.Name : _body.Name; } }

        /// <summary>The name of the body it orbits.</summary>
        public string Parent { get { return _vessel != null ? _vessel.Body.Name : _body.Parent; } }

        /// <summary>True when this is the Target Vessel rather than a body.</summary>
        public bool IsVessel { get { return _vessel != null; } }

        public static implicit operator BurnTarget(SnapshotBody body)
        {
            return body == null ? null : new BurnTarget(body, null);
        }

        public static implicit operator BurnTarget(VesselTarget vessel)
        {
            return vessel == null ? null : new BurnTarget(null, vessel);
        }

        /// <summary>Its orbit round <see cref="Parent"/>; a body's is read from <paramref name="system"/>.</summary>
        public Conic OrbitIn(BodySystem system)
        {
            return _vessel != null ? _vessel.Orbit : system.OrbitOf(_body);
        }

        /// <summary>Why a craft round <paramref name="craftBody"/> can't aim at it (it is not in that sphere of influence), or null.</summary>
        public string Elsewhere(SnapshotBody craftBody, string reference)
        {
            if (_vessel != null) return _vessel.Elsewhere(craftBody, reference);
            if (string.Equals(_body.Parent, craftBody.Name, StringComparison.OrdinalIgnoreCase)) return null;
            return _body.Name + " is not in orbit round " + craftBody.Name + ", the body the craft is round at that time, so " + reference + " is not defined";
        }
    }
}
