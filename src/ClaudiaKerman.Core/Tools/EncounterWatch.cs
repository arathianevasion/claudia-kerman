// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What <see cref="TransferFlight"/> planned a coast on (#770): what a wait hands the Encounter Watch.</summary>
    internal sealed class EncounterWatchPlan
    {
        public string Target;
        /// <summary>The periapsis the coast was planned on, m above the target's sea level, and how far it may drift, m.</summary>
        public double Pe;
        public double PeLimit;
        /// <summary>The UT of the target's sphere of influence, and the UT the coast was planned at.</summary>
        public double SoiUt;
        public double SinceUt;
        /// <summary>The bodies the coast goes round, the target last: any other means KSP has re-patched the craft.</summary>
        public List<string> Bodies = new List<string>();
        /// <summary>What the mod would resume from if the player left now (#775, <see cref="CoastToken"/>); "" when the flight may not checkpoint.</summary>
        public string Checkpoint = "";
        /// <summary>A wait with no planned encounter (#867, the wait for a far ejection): the next-patch watch (<see cref="NextPatchWatch"/>) instead.</summary>
        public bool NextPatch;

        public IDictionary<string, object> ToLexicon()
        {
            return new Dictionary<string, object>
            {
                { "target", Target }, { "pe", Pe }, { "pe_limit", PeLimit }, { "soi_ut", SoiUt }, { "since_ut", SinceUt }, { "bodies", string.Join(",", Bodies.ToArray()) }, { "checkpoint", Checkpoint },
                { "next_patch", NextPatch },
            };
        }
    }

    /// <summary>
    /// The Encounter Watch (#770, GLOSSARY): during a long coast of <c>transfer_to</c>, whether the encounter it was planned on still
    /// holds, read from KSP's own orbit through the Game Snapshot, so with no physics, at any warp. It only reads and answers: the
    /// adapter asks it (<c>EncounterWatcher</c>) and the block cancels the warp, so it never raises warp, and KSP's own limits (an SOI
    /// change, "Orbit event imminent" capping at 50x) only change how much game time passes between checks. One drop ends it:
    /// what comes next is the block's next step, and that step's wait has its own watch.
    /// </summary>
    public sealed class EncounterWatch
    {
        /// <summary>
        /// A periapsis this share of the target's sphere of influence off the plan wakes the block: 10 %, 4.8 Mm at Duna, under 1 m/s
        /// to fix early in the coast (6 to 7 Mm per m/s along the track) and far dearer inside the sphere of influence, where the trim is.
        /// </summary>
        internal const double DriftShare = 0.1;
        /// <summary>Game seconds between checks (<see cref="Gap"/>): a twentieth of the time to the nearer of the coast's two ends, from a minute to a Kerbin day (6 h).</summary>
        internal const double EndsShare = 0.05;
        internal const double MinGap = 60;
        internal const double MaxGap = 21600;

        private readonly string _target;
        private readonly double _pe;
        private readonly double _peLimit;
        private readonly double _soiUt;
        private readonly double _sinceUt;
        private readonly string[] _bodies;
        private double _nextUt;
        private bool _suspect;
        /// <summary>The next-patch watch a wait with no planned encounter asks instead (#867); null for an encounter's.</summary>
        private readonly NextPatchWatch _nextPatch;

        /// <summary>
        /// A watch from TRANSFERSTEP's <c>watch</c> lexicon; with no target (the empty lexicon of a wait with nothing to watch) it is never due.
        /// With <c>next_patch</c> (#867) it is the next-patch watch, which drops the warp before a moon nobody planned.
        /// </summary>
        public EncounterWatch(IDictionary<string, object> plan)
        {
            object target, bodies, nextPatch;
            plan.TryGetValue("target", out target);
            plan.TryGetValue("bodies", out bodies);
            if (plan.TryGetValue("next_patch", out nextPatch) && nextPatch as bool? == true) _nextPatch = new NextPatchWatch("");
            _target = target as string ?? "";
            _pe = Number(plan, "pe");
            _peLimit = Number(plan, "pe_limit");
            _soiUt = Number(plan, "soi_ut");
            _sinceUt = Number(plan, "since_ut");
            _nextUt = _sinceUt;
            _bodies = (bodies as string ?? "").Split(',');
        }

        /// <summary>Whether a check is due at <paramref name="ut"/>, so the adapter reads a snapshot only then.</summary>
        public bool Due(double ut)
        {
            if (_nextPatch != null) return _nextPatch.Due(ut);
            return _target != "" && ut >= _nextUt;
        }

        /// <summary>The time to the next check from <paramref name="ut"/>: <see cref="EndsShare"/> of the time to the nearer end, within <see cref="MinGap"/> and <see cref="MaxGap"/>.</summary>
        internal double Gap(double ut)
        {
            return Math.Max(MinGap, Math.Min(MaxGap, EndsShare * Math.Min(ut - _sinceUt, _soiUt - ut)));
        }

        /// <summary>
        /// One check: "" while nothing is wrong, else the line for the trace and the events, and the warp should drop. A reading that
        /// looks wrong is read once more, at the next tick, before it counts: a reading between KSP's two re-patches isn't a loss.
        /// </summary>
        public string Check(GameSnapshot snapshot)
        {
            if (_nextPatch != null)
            {
                var met = _nextPatch.Meeting(snapshot);
                return met == null ? "" : "Next-patch watch: " + NextPatchWatch.WouldMeet(met) + "; warp dropped before it";
            }
            var ut = snapshot.Ut;
            var trouble = Trouble(snapshot);
            if (trouble == "")
            {
                _suspect = false;
                _nextUt = ut + Gap(ut);
                return "";
            }
            if (!_suspect)
            {
                _suspect = true;
                return "";
            }
            return "Encounter Watch: " + trouble.Replace("{ut}", ut.ToString("0", CultureInfo.InvariantCulture)) + "; warp dropped to restore it";
        }

        /// <summary>"" or what is wrong, with {ut} for the UT.</summary>
        private string Trouble(GameSnapshot snapshot)
        {
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, _target, out error);
            // Nothing to read is no finding: the wait goes on, and its end is checked as before.
            if (setup == null || setup.Body == setup.Target) return "";
            if (!_bodies.Contains(setup.Body.Name)) return _target + " encounter lost at UT {ut}: KSP has the craft round " + setup.Body.Name;
            var enc = TransferFlight.Encounter(setup);
            if (enc == null) return _target + " encounter lost at UT {ut}";
            var drift = Math.Abs(enc.Pe - _pe);
            if (drift > _peLimit) return _target + " periapsis off by " + Math.Round(drift / 1000).ToString("0", CultureInfo.InvariantCulture) + " km at UT {ut}";
            return "";
        }

        private static double Number(IDictionary<string, object> plan, string key)
        {
            object value;
            return plan.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
        }
    }
}
