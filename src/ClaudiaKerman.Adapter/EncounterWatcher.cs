// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using UnityEngine;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// The game side of the Encounter Watch (#770): holds the watch a <c>transfer_to</c> wait arms (<c>ADDONS:CLAUDIA:ENCOUNTERWATCH</c>),
    /// and on each poll from that wait's stop (<c>ENCOUNTERWATCHDROPPED</c>) reads KSP's orbit for <see cref="EncounterWatch"/> when a
    /// check is due, by game time, and records a drop in the Mission's events and in KSP.log. It runs only inside the wait's own
    /// poll, so an ended script leaves nothing running. It never touches the warp: the block's stop cancels it (warp stays in
    /// Kerboscript, ADR 0014), and with no raise and no fixed rate it can't fight the player's warp or KSP's limits.
    /// </summary>
    public sealed class EncounterWatcher
    {
        /// <summary>The watcher of this game session, for the block calls.</summary>
        public static EncounterWatcher Current { get; private set; }

        private readonly Action<MissionInput> _send;
        private EncounterWatch _watch;
        private Vessel _vessel;
        private List<SnapshotBody> _bodies;
        private string _line = "";
        private int _checks;

        /// <summary>Whether the watch armed last has dropped the warp.</summary>
        public bool Dropped { get; private set; }

        public EncounterWatcher(Action<MissionInput> send)
        {
            _send = send;
            Current = this;
        }

        /// <summary>A save was loaded: a watch held belongs to another timeline.</summary>
        public void Reset()
        {
            _watch = null;
            _vessel = null;
            _line = "";
            _checks = 0;
            Dropped = false;
        }

        /// <summary>A wait began with TRANSFERSTEP's <paramref name="plan"/> (empty: nothing to watch).</summary>
        public void Arm(IDictionary<string, object> plan, Vessel vessel)
        {
            Reset();
            _watch = new EncounterWatch(plan);
            _vessel = vessel;
            // The coast a far switch could make a checkpoint of (#775): Core decides when the player asks.
            object token;
            _send(new CoastArmed(plan.TryGetValue(CoastArmed.ResultKey, out token) ? token as string : ""));
        }

        /// <summary>Each tick of the wait: whether the watch has dropped the warp. Reads the orbit only when a check is due. A failure is logged and ends the watch, never the flight.</summary>
        public bool Poll(Vessel vessel)
        {
            if (Dropped || _watch == null || !ReferenceEquals(vessel, _vessel)) return Dropped;
            var ut = Planetarium.GetUniversalTime();
            if (!_watch.Due(ut)) return false;
            try
            {
                var snapshot = SnapshotReader.ReadOrbit(vessel, _bodies);
                _bodies = snapshot.Bodies;
                _checks++;
                var line = _watch.Check(snapshot);
                if (line.Length == 0) return false;
                _line = line;
                Dropped = true;
                Debug.Log(ModInfo.LogTag + " " + line + " (check " + _checks.ToString(CultureInfo.InvariantCulture) + ")");
                _send(new EncounterWatchDropped(line));
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " Encounter Watch failed; the wait goes on unwatched: " + e);
                _watch = null;
            }
            return Dropped;
        }

        /// <summary>The drop's line once, for the block trace; "" otherwise.</summary>
        public string TakeLine()
        {
            var line = _line;
            _line = "";
            return line;
        }

        /// <summary>For the block call's time line.</summary>
        public string Describe()
        {
            return _checks.ToString(CultureInfo.InvariantCulture) + " checks" + (Dropped ? ", dropped" : "");
        }
    }
}
