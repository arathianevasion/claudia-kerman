// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// The next-patch watch (#867): while the Mission's craft warps with no planned encounter, whether its path meets a moon's sphere of
    /// influence nobody planned, read as the Encounter Watch reads (from KSP's own orbit through the Game Snapshot, so with no physics, at any
    /// warp). It answers when the meeting is <see cref="Margin"/> ahead or nearer, so warp drops before it. It only reads and answers: between runs
    /// the adapter asks it and drops the warp; in <c>transfer_to</c>'s wait for its ejection the block asks it through the Encounter Watch's call and
    /// cancels the warp (ADR 0014). Each check follows the path <see cref="MaxGap"/> plus the margin ahead (<see cref="MoonCrossings.Follow"/>):
    /// one encounter search per loop in that span, a few hundred two-body positions per moon per loop; the next check is due when the meeting found
    /// comes within the margin, or after <see cref="MaxGap"/> with none.
    /// </summary>
    public sealed class NextPatchWatch
    {
        /// <summary>The least warning: an hour of game time, so an orbit of under an hour still drops warp an hour ahead.</summary>
        internal const double MinMargin = 3600;
        /// <summary>Game seconds between checks with nothing found: at most a Kerbin day (6 h); never under a minute.</summary>
        internal const double MaxGap = 21600;
        internal const double MinGap = 60;

        private readonly string[] _planned;
        private double _nextUt = double.NegativeInfinity;
        private string _body;
        /// <summary>The meeting last answered, so a warp raised again after the drop isn't dropped for it a second time.</summary>
        private string _answeredMoon = "";
        private double _answeredUt = double.NaN;

        /// <summary>A watch that lets pass the bodies of <paramref name="planned"/> (comma-separated; "" for none).</summary>
        public NextPatchWatch(string planned)
        {
            _planned = (planned ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray();
        }

        /// <summary>Whether a check is due at <paramref name="ut"/>, so the reader takes a snapshot only then.</summary>
        public bool Due(double ut)
        {
            return ut >= _nextUt;
        }

        /// <summary>
        /// Warp has just been raised (the adapter calls it on each new warp): the next read is due at once, since the orbit may have changed at 1x
        /// since the last one (a burn by hand), and a schedule from before it would miss a meeting it brings nearer.
        /// </summary>
        public void Rearm()
        {
            _nextUt = double.NegativeInfinity;
        }

        /// <summary>One loop, and at least <see cref="MinMargin"/>: how far ahead of a meeting warp drops.</summary>
        internal static double Margin(Conic orbit)
        {
            return Math.Max(MinMargin, orbit.Closed ? orbit.Period : 0);
        }

        /// <summary>
        /// One check: null while nothing unplanned is within the margin; else the moon and the UT it is met (now, when KSP already has the craft round a
        /// moon nobody planned), and warp should drop. One meeting is answered once, even across <see cref="Rearm"/>: the next check is due after it.
        /// </summary>
        internal MoonCrossing Meeting(GameSnapshot snapshot)
        {
            var ut = snapshot.Ut;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, null, out error);
            // Nothing to read is no finding: the watch looks again in a minute.
            if (setup == null)
            {
                _nextUt = ut + MinGap;
                return null;
            }
            if (_body == null) _body = setup.Body.Name;
            if (!string.Equals(_body, setup.Body.Name, StringComparison.OrdinalIgnoreCase))
            {
                var parent = setup.System.Parent(setup.Body);
                var moved = parent != null && string.Equals(parent.Name, _body, StringComparison.OrdinalIgnoreCase) && !_planned.Any(p => string.Equals(p, setup.Body.Name, StringComparison.OrdinalIgnoreCase));
                _body = setup.Body.Name;
                if (moved)
                {
                    _nextUt = ut + MaxGap;
                    return new MoonCrossing { Body = parent, Moon = setup.Body, Ut = ut, EdgeR = setup.Body.Sma - setup.Body.SoiRadius };
                }
            }
            var margin = Margin(setup.Start);
            var met = MoonCrossings.Follow(setup.System, setup.Body, setup.Start, ut, ut + MaxGap + margin, _planned);
            if (met == null)
            {
                _nextUt = ut + MaxGap;
                return null;
            }
            if (met.Ut.Value - ut <= margin)
            {
                _nextUt = met.Ut.Value + MinGap;
                // The player raised the warp again after this meeting's drop: it is theirs now.
                if (met.Moon.Name == _answeredMoon && Math.Abs(met.Ut.Value - _answeredUt) < margin) return null;
                _answeredMoon = met.Moon.Name;
                _answeredUt = met.Ut.Value;
                return met;
            }
            _nextUt = Math.Max(ut + MinGap, met.Ut.Value - margin);
            return null;
        }

        /// <summary>One check for the adapter between runs: "" while nothing is wrong, else the Mission's line (<see cref="Dropped"/>), and warp should drop.</summary>
        public string Check(GameSnapshot snapshot)
        {
            var met = Meeting(snapshot);
            return met == null ? "" : Dropped(met, snapshot.Ut);
        }

        /// <summary>A block's failure after the watch dropped its warp: "would meet Ike at UT 169,104,601".</summary>
        internal static string WouldMeet(MoonCrossing met)
        {
            return "would meet " + met.Moon.Name + " at UT " + MoonCrossing.Num(met.Ut ?? 0);
        }

        /// <summary>
        /// The Mission's line and request when the watch dropped the warp with no script running: "Warp stopped: the craft would meet Ike's sphere of
        /// influence at UT 169,104,601, in 58 min. Fly change_orbit set_ap 1,520 km (about 26 m/s; ...) first, or Re-plan.".
        /// </summary>
        internal static string Dropped(MoonCrossing met, double ut)
        {
            var at = met.Ut ?? ut;
            var when = at <= ut ? "KSP has the craft in " + met.Moon.Name + "'s sphere of influence since UT " + MoonCrossing.Num(at)
                : "the craft would meet " + met.Moon.Name + "'s sphere of influence at UT " + MoonCrossing.Num(at) + ", in " + Ui.KerbinTime.Span(at - ut);
            return "Warp stopped: " + when + (met.FixAltitude.HasValue && at > ut ? ". Fly " + met.Fix() + " first, or Re-plan." : ". Re-plan from here.");
        }
    }
}
