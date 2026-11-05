// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>One Maneuver Sequence against the Delta-v Map.</summary>
    internal sealed class JudgedLeg
    {
        public PlannedSequence Sequence;
        /// <summary>The Map legs it matched, "capture:Ike + land:Ike"; "" when it matched none.</summary>
        public string Legs = "";
        /// <summary>What those legs need, m/s, with no margin; 0 when it matched none.</summary>
        public double Figure;

        public bool Matched
        {
            get { return Legs.Length > 0; }
        }

        /// <summary>What the leg counts for in the margin: the larger of its budget and its figure.</summary>
        public double Need
        {
            get { return Math.Max(Sequence.DeltaV, Figure); }
        }

        /// <summary>Its budget is under the figure by more than <see cref="PlanLegs.Tolerance"/>, and by more than <see cref="PlanLegs.Floor"/>.</summary>
        public bool Under
        {
            get { return Matched && Sequence.DeltaV < Figure * (1 - PlanLegs.Tolerance) && Figure - Sequence.DeltaV > PlanLegs.Floor; }
        }
    }

    /// <summary>
    /// #757, the per-leg rule: each Maneuver Sequence of a Mission Plan against the <see cref="DeltaVMap"/>. The start Craft Check runs before
    /// a Mission Plan exists, so it gets the Map for the Mission Goal's bodies; this judges the plan as soon as there is one, at
    /// propose_mission_plan and its card, and again in each Debrief's Margin check and margin recheck.
    /// <para>
    /// <b>Matching.</b> A sequence is read by its name, or its intent when the name says nothing a leg does
    /// (<see cref="SequenceWords.ActionWords"/>), in plan order from where the craft is now, and adds the pieces its words name:
    /// a launch or ascent while on the ground (<c>launch:Home</c> at home, else <c>ascend:Body</c>); a trip (transfer, ejection or return) to
    /// the body it goes to ("to X" first, else the body named, else home for a bare return), priced as departure and plane change; a capture;
    /// a landing (<c>land:Body</c>, and on a body with air only from low orbit, as a landing off a trip is the entry itself). A landing that
    /// also returns home adds the ascent between them. The trip's capture is priced by <see cref="ArrivalRule"/> with the plan to read, so a
    /// return home or a Mission that comes down at Eve pays none; a flyby and an aerocapture pay none. A capture that is owed falls to the
    /// next leg that names one, else the next that lands (unless the trip's own intent says it captures), else the trip itself. Manual legs,
    /// a Child Vessel's legs and legs a revised plan lists as flown are not judged, and neither is a leg with no matching words; they count
    /// at their budget.
    /// </para>
    /// <para>
    /// <b>The figure.</b> The Map's, with v/TWR on an airless landing or ascent at the lowest TWR of the stages the leg will burn
    /// (<see cref="DeltaVMap.Surface"/>). The launch is plan_ascent's figure plus <see cref="LaunchAllowance"/> when the Craft Check made that
    /// call, else the Map's.
    /// </para>
    /// <para>
    /// <b>The tolerance.</b> A budget is under the Map, a <c>leg_under_map</c> Warning, when it is more than <see cref="Tolerance"/> and
    /// <see cref="Floor"/> m/s below the figure. Whatever the finding, the margin counts each leg at the larger of its budget and its figure.
    /// </para>
    /// </summary>
    internal static class PlanLegs
    {
        /// <summary>A budget may be this fraction under the Map's figure with no finding.</summary>
        public const double Tolerance = 0.10;
        /// <summary>And this many m/s: a small leg far under in percent is no matter.</summary>
        public const double Floor = 25;
        /// <summary>
        /// Flown launches over plan_ascent's dv_to_orbit: 4.9% to 6.2% (mean 5.6%) in the 12 Nominal launches of 2026-11-03 to 05 with
        /// only launch and circularize. The simulation stops short of the circularization and steering the flight pays.
        /// </summary>
        public const double LaunchAllowance = 0.06;

        private static readonly Regex AscentFigure = new Regex("\"dv_to_orbit\":\\s*([0-9.]+)");
        /// <summary>A revised plan lists what is already flown ("Launch (flown)", intent "Done: ..."): nothing left to judge.</summary>
        private static readonly Regex Flown = new Regex(@"\bflown\b|\bdone:", RegexOptions.IgnoreCase);

        /// <summary>
        /// <paramref name="rest"/> judged from where <paramref name="snapshot"/>'s craft is now; <paramref name="plannedAscent"/> is the Craft
        /// Check's plan_ascent call, "input: result" ("" when none).
        /// </summary>
        public static List<JudgedLeg> Judge(GameSnapshot snapshot, IList<PlannedSequence> rest, string plannedAscent)
        {
            return new Walk(snapshot, rest, plannedAscent).Run();
        }

        /// <summary>What the legs count for in the margin, m/s.</summary>
        public static double Need(IEnumerable<JudgedLeg> legs)
        {
            return legs.Sum(l => l.Need);
        }

        /// <summary>The <c>leg_under_map</c> finding for the legs under the Map; none when no leg is. One finding, as a Warning is acknowledged by its code.</summary>
        public static IList<MarginFinding> Findings(IEnumerable<JudgedLeg> legs)
        {
            var found = new List<MarginFinding>();
            var under = legs.Where(l => l.Under).ToList();
            if (under.Count == 0) return found;
            var rule = CraftCheckRules.Find(CraftCheckRules.LegUnderMap);
            found.Add(new MarginFinding { Code = rule.Code, Severity = rule.Severity, Title = rule.Title, Explanation = Unders(under) });
            return found;
        }

        /// <summary>The note on an accepted propose_mission_plan; "" when no leg is under the Map.</summary>
        public static string Note(IEnumerable<JudgedLeg> legs)
        {
            var under = legs.Where(l => l.Under).ToList();
            return under.Count == 0 ? "" : Unders(under) + ". The plan stands. Hand it in again with those budgets, unless you have a reason: then say it in why.";
        }

        /// <summary>The line on the Mission Plan card; "" when no leg is under the Map.</summary>
        public static string CardLine(IEnumerable<JudgedLeg> legs)
        {
            var under = legs.Where(l => l.Under).ToList();
            return under.Count == 0 ? "" : "\nWarning: " + Unders(under) + ".";
        }

        /// <summary>
        /// The facts for a margin recheck, so it judges by the same rule: the total at the larger figures and each leg raised; "" when
        /// the map raises no leg over its budget, so the request is unchanged.
        /// </summary>
        public static string RecheckFacts(IList<JudgedLeg> legs)
        {
            var raised = legs.Where(l => l.Figure > l.Sequence.DeltaV).ToList();
            if (raised.Count == 0) return "";
            return "Judge the delta-v on the mod's per-leg rule: each leg counts at the larger of its budget and the Delta-v Map's figure, " + Metres(Need(legs)) + " still to fly in all. Raised: "
                + string.Join("; ", raised.Select(l => MissionPrompts.Quoted(l.Sequence.Name) + " " + Metres(l.Sequence.DeltaV) + " to " + Metres(l.Figure) + " (" + l.Legs + ")").ToArray()) + ".\n";
        }

        private static string Unders(IEnumerable<JudgedLeg> under)
        {
            return string.Join("; ", under.Select(l => "\"" + l.Sequence.Name + "\" budgets " + Metres(l.Sequence.DeltaV) + " against the Delta-v Map's " + Metres(l.Figure)
                + " (" + l.Legs + "), " + (100 * (1 - l.Sequence.DeltaV / l.Figure)).ToString("0", CultureInfo.InvariantCulture) + "% under").ToArray());
        }

        private static string Metres(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
        }

        /// <summary>The plan in order, with where the craft is by then.</summary>
        private sealed class Walk
        {
            private readonly GameSnapshot _snapshot;
            private readonly IList<PlannedSequence> _rest;
            private readonly double? _launch;
            private readonly DeltaVMap _map;
            private SnapshotBody _at;
            private bool _grounded;
            private bool _orbiting;
            /// <summary>A trip came here with its capture still to pay, from <see cref="_origin"/>.</summary>
            private bool _captureOwed;
            private SnapshotBody _origin;
            /// <summary>The delta-v the walk has spent, for the stages a leg will burn.</summary>
            private double _used;
            private JudgedLeg _leg;

            public Walk(GameSnapshot snapshot, IList<PlannedSequence> rest, string plannedAscent)
            {
                _snapshot = snapshot;
                _rest = rest;
                _map = DeltaVMap.Compute(snapshot.Bodies, CraftCheckFlow.HomeBody);
                var figure = AscentFigure.Match(plannedAscent ?? "");
                double toOrbit;
                if (figure.Success && double.TryParse(figure.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out toOrbit) && toOrbit > 0)
                    _launch = toOrbit * (1 + LaunchAllowance);
                var at = snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
                _at = SequenceWords.Body(snapshot, at);
                _grounded = snapshot.IsGrounded;
                _orbiting = snapshot.Situation == "ORBITING";
            }

            public List<JudgedLeg> Run()
            {
                var judged = new List<JudgedLeg>();
                for (var i = 0; i < _rest.Count; i++)
                {
                    _leg = new JudgedLeg { Sequence = _rest[i] };
                    judged.Add(_leg);
                    var words = SequenceWords.ActionWords(_rest[i]);
                    if (!MissionCards.FliesCraft(_snapshot.Craft.Name, _rest[i].Vessel)) continue;
                    if (_rest[i].Manual || Flown.IsMatch(SequenceWords.Of(_rest[i]))) Follow(words);
                    else if (_map.Legs.Count > 0) Read(words, i);
                    _used += _leg.Need;
                }
                return judged;
            }

            /// <summary>A manual leg isn't judged, but the craft goes where it goes.</summary>
            private void Follow(string words)
            {
                _at = SequenceWords.NextBody(_snapshot, words, _at);
                if (SequenceWords.Landing.IsMatch(words)) _grounded = true;
                if (SequenceWords.Ascent.IsMatch(words)) _grounded = false;
            }

            private void Read(string words, int index)
            {
                var landing = SequenceWords.Landing.IsMatch(words);
                var target = Target(words);
                var goesHome = SequenceWords.ReturnHome.IsMatch(words)
                    || (SequenceWords.Trip.IsMatch(words) && target != null && target.Name == _map.Home && _at != null && _at.Name != _map.Home);
                var away = goesHome && landing ? SequenceWords.LegBody(_snapshot, words, _at) : null;
                // "Land on the Mun and return to Kerbin": down here first, then up and away.
                if (away != null && away.Name != _map.Home)
                {
                    _at = away;
                    Land(away);
                    landing = false;
                }
                if (SequenceWords.Ascent.IsMatch(words) || (goesHome && _grounded)) Ascend();
                var tripped = SequenceWords.Trip.IsMatch(words) && target != null && _at != null && target != _at;
                if (tripped) Trip(target, words, landing, index);
                var captures = SequenceWords.Capture.IsMatch(words) || SequenceWords.Aero.IsMatch(words);
                if (captures && !tripped && !SequenceWords.Ascent.IsMatch(words)) Capture(target ?? _at, words);
                if (landing) Land(tripped ? target : SequenceWords.LegBody(_snapshot, words, _at));
            }

            /// <summary>The body the words go to: the one after "to", else the one they name, else home for a bare return.</summary>
            private SnapshotBody Target(string words)
            {
                SnapshotBody first = null;
                var firstAt = int.MaxValue;
                foreach (var body in _snapshot.Bodies.Where(b => b.Name.Length > 0))
                {
                    var match = Regex.Match(words, @"\bto\s+(?:the\s+)?" + Regex.Escape(body.Name) + @"\b", RegexOptions.IgnoreCase);
                    if (match.Success && match.Index < firstAt)
                    {
                        first = body;
                        firstAt = match.Index;
                    }
                }
                if (first != null) return first;
                if (SequenceWords.ReturnHome.IsMatch(words)) return SequenceWords.Body(_snapshot, _map.Home);
                var named = SequenceWords.NamedBody(_snapshot, words);
                if ((named == null || named == _at) && Regex.IsMatch(words, @"\breturn(s|ing)?\b", RegexOptions.IgnoreCase))
                    return SequenceWords.Body(_snapshot, _map.Home);
                return named;
            }

            private void Ascend()
            {
                if (!_grounded || _at == null) return;
                _grounded = false;
                _orbiting = true;
                if (_at.Name == _map.Home)
                {
                    var leg = _map.Leg("launch:" + _at.Name);
                    if (leg != null) Add(leg.Id, _launch ?? leg.DeltaV);
                    return;
                }
                AddSurface("ascend:" + _at.Name, _at);
            }

            private void Trip(SnapshotBody target, string words, bool landsHere, int index)
            {
                var trip = _map.Trip(_at.Name, target.Name);
                if (trip == null) return;
                var id = _at.Name == _map.Home ? "transfer:" + target.Name : target.Name == _map.Home ? "return:" + _at.Name : "transfer:" + _at.Name + ">" + target.Name;
                var later = _rest.Skip(index + 1).ToList();
                // The name says, or its own intent does and no later leg names the capture for itself.
                var named = SequenceWords.Capture.IsMatch(words) || (SequenceWords.Capture.IsMatch(_leg.Sequence.Intent) && !LaterLegNamesCapture(later));
                bool? explicitCapture = SequenceWords.Flyby.IsMatch(words) || SequenceWords.Aero.IsMatch(words) ? false : named ? true : (bool?)null;
                var captured = landsHere && target.AtmosphereHeight > 0 && !named ? false : ArrivalRule.Capture(_snapshot, target.Name, explicitCapture, later);
                var figure = trip.Departure + trip.PlaneChange;
                _captureOwed = false;
                if (captured && !named && !landsHere && CaptureComesLater(later))
                {
                    _captureOwed = true;
                    _origin = _at;
                }
                else if (captured) figure += trip.Arrival;
                Add(id, figure);
                _at = target;
                _grounded = false;
                _orbiting = captured && !_captureOwed;
            }

            /// <summary>The next leg that does anything captures or lands, so the capture is in its budget; an ascent or another trip means it isn't.</summary>
            private static bool CaptureComesLater(IEnumerable<PlannedSequence> later)
            {
                foreach (var leg in later)
                {
                    var words = SequenceWords.ActionWords(leg);
                    if (SequenceWords.Capture.IsMatch(words) || SequenceWords.Aero.IsMatch(words) || SequenceWords.Landing.IsMatch(words)) return true;
                    if (SequenceWords.Ascent.IsMatch(words) || SequenceWords.Trip.IsMatch(words)) return false;
                }
                return false;
            }

            /// <summary>The next leg that does anything names a capture.</summary>
            private static bool LaterLegNamesCapture(IEnumerable<PlannedSequence> later)
            {
                foreach (var leg in later)
                {
                    var words = SequenceWords.ActionWords(leg);
                    if (SequenceWords.Capture.IsMatch(words) || SequenceWords.Aero.IsMatch(words)) return true;
                    if (SequenceWords.Landing.IsMatch(words) || SequenceWords.Ascent.IsMatch(words) || SequenceWords.Trip.IsMatch(words)) return false;
                }
                return false;
            }

            private void Capture(SnapshotBody body, string words)
            {
                if (body == null) return;
                var owed = _captureOwed;
                // A circularize with nothing owed and the craft already in orbit is orbit trimming, not a capture.
                if (!owed && _orbiting && body == _at) return;
                _captureOwed = false;
                _orbiting = true;
                _at = body;
                if (SequenceWords.Aero.IsMatch(words)) return;
                var trip = owed && _origin != null ? _map.Trip(_origin.Name, body.Name) : null;
                var leg = _map.Leg("capture:" + body.Name);
                var figure = trip != null ? trip.Arrival : leg != null ? leg.DeltaV : 0;
                if (figure > 0) Add("capture:" + body.Name, figure);
            }

            private void Land(SnapshotBody body)
            {
                if (body == null) return;
                if (_captureOwed && body == _at && body.AtmosphereHeight <= 0)
                    Capture(body, "");
                _captureOwed = false;
                var fromOrbit = _orbiting;
                _at = body;
                _grounded = true;
                _orbiting = false;
                // With air, a landing off a trip is the entry itself: only leaving a low orbit costs anything.
                if (body.AtmosphereHeight > 0 && !fromOrbit) return;
                AddSurface("land:" + body.Name, body);
            }

            private void AddSurface(string id, SnapshotBody body)
            {
                var leg = _map.Leg(id);
                if (leg == null) return;
                var from = _used + _leg.Figure;
                Add(id, _map.Surface(id, MarginCheck.LowestTwr(_snapshot, body, from, from + Math.Max(1, leg.DeltaV))));
            }

            private void Add(string id, double figure)
            {
                _leg.Legs = _leg.Legs.Length == 0 ? id : _leg.Legs + " + " + id;
                _leg.Figure += figure;
            }
        }
    }
}
