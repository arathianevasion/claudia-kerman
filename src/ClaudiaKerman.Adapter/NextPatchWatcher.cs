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
    /// The game side of the next-patch watch between runs (#867): while the Mission's craft warps with no script running (a plan pending, Auto Mode,
    /// the mod's warp to a Due Time or the player's own), reads its orbit for <see cref="NextPatchWatch"/> when a check is due, by game time, in any
    /// scene, and on an unplanned moon ahead drops the warp and tells the core, which raises the request. Core says which craft (the view's
    /// <c>NextPatchVesselId</c>); with none, or at 1x, it reads nothing. A failure is logged and ends the watch until the craft or the plan changes.
    /// </summary>
    public sealed class NextPatchWatcher
    {
        private readonly Action<MissionInput> _send;
        private NextPatchWatch _watch;
        private string _key = "";
        private string _vesselId;
        private List<SnapshotBody> _bodies;
        private int _checks;
        private bool _failed;
        private bool _warping;

        public NextPatchWatcher(Action<MissionInput> send)
        {
            _send = send;
        }

        /// <summary>A save was loaded: a watch held belongs to another timeline.</summary>
        public void Reset()
        {
            _watch = null;
            _key = "";
            _vesselId = null;
            _bodies = null;
            _checks = 0;
            _failed = false;
            _warping = false;
        }

        /// <summary>Each frame: the craft Core names ("" for none) and the bodies a planned coast meets. Reads the orbit only when a check is due.</summary>
        public void Update(string vesselId, string planned)
        {
            var key = (vesselId ?? "") + "|" + (planned ?? "");
            if (key != _key)
            {
                // The same craft with another planned set keeps the meeting it answered (#878); another craft or a loaded save starts clean.
                var kept = vesselId == _vesselId ? _watch : null;
                Reset();
                _key = key;
                _vesselId = vesselId;
                if (!string.IsNullOrEmpty(vesselId)) _watch = new NextPatchWatch(planned, kept);
            }
            var warping = TimeWarp.fetch != null && TimeWarp.CurrentRateIndex > 0;
            // Each new warp reads the orbit at once: it may have changed at 1x.
            if (warping && !_warping && _watch != null) _watch.Rearm();
            _warping = warping;
            if (_watch == null || _failed || !warping || Planetarium.fetch == null) return;
            var ut = Planetarium.GetUniversalTime();
            if (!_watch.Due(ut)) return;
            try
            {
                var vessel = Find(vesselId);
                if (vessel == null || vessel.orbit == null) return;
                var snapshot = SnapshotReader.ReadOrbit(vessel, _bodies);
                _bodies = snapshot.Bodies;
                _checks++;
                var line = _watch.Check(snapshot);
                if (line.Length == 0) return;
                DueTimeActions.DropWarp();
                Debug.Log(ModInfo.LogTag + " Next-patch watch: " + line + " (check " + _checks.ToString(CultureInfo.InvariantCulture) + ")");
                _send(new NextPatchDropped(line));
            }
            catch (Exception e)
            {
                Debug.LogWarning(ModInfo.LogTag + " Next-patch watch failed; warp goes on unwatched: " + e);
                _failed = true;
            }
        }

        private static Vessel Find(string vesselId)
        {
            foreach (var vessel in FlightGlobals.Vessels)
                if (vessel.id.ToString("N") == vesselId) return vessel;
            return null;
        }
    }
}
