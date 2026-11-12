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
    /// <b>The figure.</b> The Map's, with a gravity loss on an airless landing or ascent at the lowest TWR of the stages the leg will burn
    /// (<see cref="DeltaVMap.WithGravityLoss"/>). The launch is plan_ascent's figure plus <see cref="LaunchAllowance"/> when the Craft Check made that
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
        /// Check's plan_ascent call, "input: result" ("" when none). <paramref name="price"/> where the plan is priced up front (its proposal,
        /// a margin recheck): the planners run (<see cref="RoutePlanners"/>); in flight, the figures they priced, or the Map's.
        /// </summary>
        public static List<JudgedLeg> Judge(GameSnapshot snapshot, IList<PlannedSequence> rest, string plannedAscent, bool price = false)
        {
            return new Walk(snapshot, rest, plannedAscent, price).Run();
        }

        /// <summary>A revised plan lists this sequence as already flown (#784): nothing is left of it to fly.</summary>
        public static bool IsFlown(PlannedSequence sequence)
        {
            return Flown.IsMatch(SequenceWords.Of(sequence));
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

        /// <summary>The note on an accepted propose_mission_plan, with the margin by the rule; "" when no leg is under the Map.</summary>
        public static string Note(GameSnapshot snapshot, IList<JudgedLeg> legs, int wantedPercent)
        {
            var under = legs.Where(l => l.Under).ToList();
            return under.Count == 0 ? "" : Unders(under) + "." + MarginAtLargerFigures(snapshot, legs, wantedPercent)
                + " The plan stands. Hand it in again with those budgets, unless you have a reason: then say it in why.";
        }

        /// <summary>The line on the Mission Plan card, with the margin by the rule; "" when no leg is under the Map.</summary>
        public static string CardLine(GameSnapshot snapshot, IList<JudgedLeg> legs, int wantedPercent)
        {
            var under = legs.Where(l => l.Under).ToList();
            return under.Count == 0 ? "" : "\nWarning: " + Unders(under) + "." + MarginAtLargerFigures(snapshot, legs, wantedPercent);
        }

        /// <summary>The first margin a plan gets, before any Debrief: each leg at the larger figure, as the Margin check will count it; "" with no delta-v known.</summary>
        private static string MarginAtLargerFigures(GameSnapshot snapshot, IList<JudgedLeg> legs, int wantedPercent)
        {
            if (!snapshot.DeltaVSimulator || snapshot.Stages.Count == 0 || snapshot.DeltaVUnreliable) return "";
            var bar = MarginBar.Compute(snapshot.Stages.Sum(s => s.DeltaVVac), Need(legs), wantedPercent);
            return bar == null ? "" : " Each leg at the larger figure: " + bar.Describe();
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
            /// <summary>The craft starts between bodies, its ejection flown: the first trip only says where to.</summary>
            private bool _inTransit;
            /// <summary>A trip came here with its capture still to pay, from <see cref="_origin"/>.</summary>
            private bool _captureOwed;
            private SnapshotBody _origin;
            /// <summary>#866: the trip in to a planet whose next trip goes on to its moon, and that trip, by the planet's high orbit (<see cref="DeltaVMap.HighRouteLegs"/>); null otherwise.</summary>
            private DeltaVLeg[] _high;
            /// <summary>The delta-v the walk has spent, for the stages a leg will burn.</summary>
            private double _used;
            /// <summary>#998: the craft hasn't left the body it is at now, so its ascent and trip are the planners' (<see cref="RoutePlanners"/>).</summary>
            private bool _atStart = true;
            /// <summary>The planners may search (<see cref="Judge"/>'s price).</summary>
            private readonly bool _price;
            private JudgedLeg _leg;

            public Walk(GameSnapshot snapshot, IList<PlannedSequence> rest, string plannedAscent, bool price)
            {
                _snapshot = snapshot;
                _price = price;
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
                _inTransit = _at != null && !_grounded && ((_at.Sma <= 0 && _at.Name != _map.Home) || (snapshot.Orbit != null && snapshot.Orbit.Eccentricity >= 1));
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
                var next = SequenceWords.NextBody(_snapshot, words, _at);
                if (next != _at) _atStart = false;
                _at = next;
                if (SequenceWords.Landing.IsMatch(words)) _grounded = true;
                if (SequenceWords.Ascent.IsMatch(words)) _grounded = false;
            }

            private void Read(string words, int index)
            {
                var landing = SequenceWords.Landing.IsMatch(words);
                var target = SequenceWords.TripTarget(_snapshot, words, _at, _map.Home);
                var trips = SequenceWords.Trip.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words);
                var goesHome = trips && target != null && target.Name == _map.Home && _at != null && _at.Name != _map.Home;
                // "Land on the Mun and return to Kerbin": the landing comes first in the words, so it is down here, then up and away.
                // "Return and parachute landing": the landing comes after, so it is the entry at home.
                if (goesHome && landing && SequenceWords.Landing.Match(words).Index < SequenceWords.Trip.Match(words).Index)
                {
                    var named = SequenceWords.NamedBody(_snapshot, words);
                    Land(named != null && named.Name != _map.Home ? named : _at);
                    landing = false;
                }
                if (SequenceWords.Ascent.IsMatch(words) || (goesHome && _grounded)) Ascend();
                var tripped = trips && target != null && _at != null && target != _at;
                if (tripped) Trip(target, words, landing, index);
                var captures = SequenceWords.Capture.IsMatch(words) || SequenceWords.Aero.IsMatch(words);
                if (captures && !tripped && !SequenceWords.Ascent.IsMatch(words)) Capture(target ?? _at, words);
                if (landing) Land(tripped ? target : SequenceWords.LegBody(_snapshot, words, _at));
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
                // #998: from where the craft is, plan_ascent's figure; where it refuses, the Map's as before.
                string why;
                var planned = _atStart && _snapshot.IsGrounded ? RoutePlanners.Ascent(_snapshot, out why, _price) : null;
                if (planned != null) Add("ascend:" + _at.Name, planned.DeltaV);
                else AddSurface("ascend:" + _at.Name, _at);
            }

            private void Trip(SnapshotBody target, string words, bool landsHere, int index)
            {
                var fromStart = _atStart;
                _atStart = false;
                // Already on the way (round the star, or escaping): the ejection is flown, and no low orbit is left to price a trip from.
                if (_inTransit)
                {
                    _inTransit = false;
                    _at = target;
                    _orbiting = false;
                    _high = HighRouteOn(target, index);
                    return;
                }
                var trip = _map.Trip(_at.Name, target.Name);
                if (trip == null) return;
                // #998: the first trip from where the craft is, unless a planet to its own moon or a moon to its planet, is plan_transfer's at the pick; where it refuses, the Map's.
                string why;
                var planned = fromStart && RoutePlanners.PlansTrip(_snapshot, _at, target, _map.Home) ? RoutePlanners.Transfer(_snapshot, target, null, _snapshot.MarginPercent, out why, _price) : null;
                // #866: in to a planet with its moon next, the capture stops in the high orbit; on to the moon, the trip leaves from there.
                if (_at.Name == _map.Home)
                {
                    _high = HighRouteOn(target, index);
                    if (_high != null) trip = _high[0];
                }
                else
                {
                    if (_high != null && _at.Name == _high[0].Bodies[1] && target.Name == _high[1].Bodies[1]) trip = _high[1];
                    // Used, or passed by: a later capture here or trip on is the low circle's.
                    _high = null;
                }
                if (planned != null)
                {
                    // A high-route capture stops short of the low circle the window's is costed to (#866), as the Craft Check's row says.
                    var saving = _high != null && _at.Name == _map.Home ? _high[0].HighSaving : 0;
                    trip = new DeltaVLeg { Departure = planned.DeltaV - planned.Capture, Arrival = planned.Capture - saving, Bodies = trip.Bodies };
                }
                var id = _at.Name == _map.Home ? (_map.Leg("transfer:" + target.Name) != null ? "transfer:" + target.Name : "transfer:" + _at.Name + ">" + target.Name)
                    : target.Name == _map.Home ? "return:" + _at.Name : "transfer:" + _at.Name + ">" + target.Name;
                var later = _rest.Skip(index + 1).ToList();
                // The name says, or its own intent does and no later leg names the capture for itself.
                var named = SequenceWords.Capture.IsMatch(words) || (SequenceWords.Capture.IsMatch(_leg.Sequence.Intent) && !LaterLegNamesCapture(later));
                bool? explicitCapture = SequenceWords.Flyby.IsMatch(words) || SequenceWords.Aero.IsMatch(words) ? false : named ? true : (bool?)null;
                var captured = landsHere && target.AtmosphereHeight > 0 && !named ? false : ArrivalRule.Capture(_snapshot, target.Name, explicitCapture, later);
                var figure = trip.Departure + trip.PlaneChange;
                _captureOwed = false;
                // A budget that holds the whole trip, as the Map's line gives it, has the capture in it, whatever the next leg lands.
                var budgetHoldsCapture = _leg.Sequence.DeltaV >= (figure + trip.Arrival) * (1 - Tolerance) && !LaterLegNamesCapture(later);
                if (captured && !named && !landsHere && !budgetHoldsCapture && CaptureComesLater(later))
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

            /// <summary>The high route (<see cref="DeltaVMap.HighRouteLegs"/>) when the plan's next trip from <paramref name="planet"/>, from leg <paramref name="index"/> on, is to one of its moons (<see cref="HighRoute.NextMoon"/>); else null.</summary>
            private DeltaVLeg[] HighRouteOn(SnapshotBody planet, int index)
            {
                var ahead = _snapshot.At(_snapshot.Ut);
                ahead.LegsAhead = _rest.Skip(index).ToList();
                var system = new BodySystem(_snapshot.Bodies);
                var at = system.Find(planet.Name);
                double? deadline;
                string why;
                var moon = at == null ? null : HighRoute.NextMoon(ahead, system, at, out deadline, out why);
                return moon == null ? null : _map.HighRouteLegs(moon.Name);
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
                var high = _high != null && _high[0].Bodies[1] == body.Name ? _high[0] : null;
                var trip = owed && _origin != null ? high ?? _map.Trip(_origin.Name, body.Name) : high;
                var leg = _map.Leg("capture:" + body.Name);
                var figure = trip != null ? trip.Arrival : leg != null ? leg.DeltaV : 0;
                if (figure > 0) Add("capture:" + body.Name, figure);
            }

            private void Land(SnapshotBody body)
            {
                // Already down: "Run the crew report on the landed pod" lands nothing.
                if (body == null || _grounded) return;
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
                Add(id, _map.WithGravityLoss(id, MarginCheck.LowestTwr(_snapshot, body, from, from + Math.Max(1, leg.DeltaV))));
            }

            private void Add(string id, double figure)
            {
                _leg.Legs = _leg.Legs.Length == 0 ? id : _leg.Legs + " + " + id;
                _leg.Figure += figure;
            }
        }
    }
}
