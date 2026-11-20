// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>One served Contract Requirement as the game has it now, for the Script Result's data.</summary>
    public sealed class RequirementReading
    {
        public string Key = "";
        /// <summary>The Contract's title, "Explore the Mun".</summary>
        public string Contract = "";
        /// <summary>What it asks, "orbit the Mun".</summary>
        public string Requirement = "";
        /// <summary>met, not met, failed, or the Contract's own end (completed, failed, deadline passed, no longer accepted).</summary>
        public string State = "";
        /// <summary>Why it isn't met, when the mod can say; "" otherwise.</summary>
        public string Gap = "";
    }

    /// <summary>
    /// #1025: the mod watching the chosen Contracts through the flight (ADR 0021: the mod's own logic, with no request). It reads only the
    /// Game Snapshot's Contracts, never the live game. Between Debriefs it notes what changed for the chosen Contracts: a Contract the game
    /// completed (with the mod's own chat line), failed, let expire or that left the accepted list, and a Requirement failing or falling back
    /// to unmet. Each goes into the next Debrief as data; one that can't complete any more may be dropped by the next revised plan. It never
    /// raises an Alert and is never a Telemetry Check: nothing it sees changes a flight.
    /// </summary>
    public sealed class ContractWatch
    {
        /// <summary>Game time a hold waits after Core first sees a Requirement's conditions hold, s.</summary>
        public const double HoldSeconds = 60;

        private readonly Dictionary<string, string> _requirementStates = new Dictionary<string, string>();
        private readonly Dictionary<string, SnapshotContract> _contracts = new Dictionary<string, SnapshotContract>();
        private readonly List<string> _news = new List<string>();
        /// <summary>The chosen Contracts that ended other than completed (failed, expired, cancelled, gone), by GUID: they can't complete.</summary>
        private readonly HashSet<string> _ended = new HashSet<string>();

        /// <summary>The chosen Contracts that can't complete any more, by GUID: the next revised plan drops them whatever its legs.</summary>
        public ICollection<string> Ended
        {
            get { return _ended; }
        }
        /// <summary>The chosen Contracts that can't complete any more, or may not, by GUID, with the reason: a revised plan may drop them.</summary>
        private readonly Dictionary<string, string> _droppable = new Dictionary<string, string>();
        /// <summary>The Requirement that made each droppable Contract so, by GUID: met later, it isn't droppable any more.</summary>
        private readonly Dictionary<string, string> _droppableBy = new Dictionary<string, string>();

        /// <summary>The Contracts a revised plan may leave unserved, with the reason it may.</summary>
        public IDictionary<string, string> Droppable
        {
            get { return _droppable; }
        }

        /// <summary>The Contracts the game completed since the watch began, by GUID: their Requirements need no leg any more.</summary>
        public readonly HashSet<string> Completed = new HashSet<string>();

        /// <summary>
        /// A Game Snapshot's Contracts against the chosen Requirements: notes what changed for the next Debrief, and returns the mod's chat
        /// line for each Contract the game completed since the last one ("Contract complete: Explore the Mun · +10,000 √").
        /// </summary>
        public IList<string> See(SnapshotContracts contracts, IList<ChosenRequirement> chosen, double ut)
        {
            var lines = new List<string>();
            if (contracts == null || chosen.Count == 0) return lines;
            foreach (var contract in chosen.GroupBy(r => GuidOf(r.Key)))
            {
                var guid = contract.Key;
                var title = contract.First().Title;
                var now = contracts.Accepted.FirstOrDefault(c => c.Guid == guid);
                SnapshotContract before;
                var seen = _contracts.TryGetValue(guid, out before);
                if (now != null && now.State == "Active")
                {
                    _contracts[guid] = now;
                    if (now.DeadlineUt.HasValue && ut > now.DeadlineUt.Value) Ends(guid, title, "its deadline passed");
                    foreach (var requirement in contract) Requirement(guid, title, requirement, now);
                    continue;
                }
                if (!seen || Completed.Contains(guid) || _ended.Contains(guid)) continue;
                var state = now != null ? now.State : StateOf(contracts, guid);
                if (state == "Completed")
                {
                    Completed.Add(guid);
                    var line = "Contract complete: " + title + " · +" + Whole(before.Funds) + " √";
                    lines.Add(line);
                    _news.Add(Quoted(title) + " completed: the game paid +" + Whole(before.Funds) + " funds (the mod posted \"" + line + "\")");
                    continue;
                }
                Ends(guid, title, state == "Failed" ? "the game failed it"
                    : state == "DeadlineExpired" ? "its deadline passed"
                    : state == "Cancelled" ? "it was cancelled"
                    : "it is no longer accepted");
            }
            return lines;
        }

        private void Requirement(string guid, string title, ChosenRequirement requirement, SnapshotContract contract)
        {
            var found = Find(contract.Requirements, requirement.Key);
            var state = found == null ? "" : found.State;
            string before;
            _requirementStates.TryGetValue(requirement.Key, out before);
            _requirementStates[requirement.Key] = state;
            // Met after all (a retry): what made it droppable is over, unless the Contract itself ended.
            string missedBy;
            if (state == "Complete" && !_ended.Contains(guid) && _droppableBy.TryGetValue(guid, out missedBy) && missedBy == requirement.Key)
            {
                _droppable.Remove(guid);
                _droppableBy.Remove(guid);
            }
            if (before == null || before == state) return;
            if (state == "Failed")
            {
                _news.Add(Quoted(title) + ": \"" + requirement.Words + "\" failed in the game");
                MakeDroppable(guid, requirement, "\"" + requirement.Words + "\" failed in the game");
            }
            else if (before == "Complete" && state == "Incomplete")
            {
                _news.Add(Quoted(title) + ": \"" + requirement.Words + "\" fell back to unmet");
                MakeDroppable(guid, requirement, "\"" + requirement.Words + "\" fell back to unmet");
            }
        }

        private void MakeDroppable(string guid, ChosenRequirement requirement, string why)
        {
            _droppable[guid] = why;
            _droppableBy[guid] = requirement.Key;
        }

        private void Ends(string guid, string title, string why)
        {
            if (!_ended.Add(guid)) return;
            _droppable[guid] = why;
            _news.Add(Quoted(title) + " can't complete: " + why);
        }

        /// <summary>The chosen Contracts the deadline recheck found too late, by GUID: dropped from the list again when a later recheck finds them on time.</summary>
        private readonly HashSet<string> _late = new HashSet<string>();

        /// <summary>
        /// #1026: the deadline recheck's reading. A Contract it finds too late may be dropped by the next revised plan (kept only while a leg
        /// still to fly serves it); one it found late before and finds on time now is no longer droppable for that.
        /// </summary>
        public void Rechecked(IList<LateContract> late, IEnumerable<string> checkedGuids)
        {
            foreach (var guid in checkedGuids.Where(g => _late.Contains(g) && late.All(l => l.Guid != g)).ToList())
            {
                _late.Remove(guid);
                if (!_ended.Contains(guid)) _droppable.Remove(guid);
            }
            foreach (var one in late)
            {
                if (_droppable.ContainsKey(one.Guid) && !_late.Contains(one.Guid)) continue;
                _late.Add(one.Guid);
                _droppable[one.Guid] = one.Reason;
            }
        }

        /// <summary>A hold that timed out, or a Requirement the flown leg left unmet: it may be dropped, or flown again.</summary>
        public void Missed(ChosenRequirement requirement, string gap)
        {
            var guid = GuidOf(requirement.Key);
            if (!_droppable.ContainsKey(guid)) MakeDroppable(guid, requirement, "\"" + requirement.Words + "\" isn't met: " + gap);
        }

        /// <summary>What changed for the chosen Contracts since the last Debrief, for the next one's data; "" when nothing did. Taking it clears it.</summary>
        public string TakeNews()
        {
            if (_news.Count == 0) return "";
            var text = "Contracts since the last Debrief (the mod's reading of the game, data): " + string.Join("; ", _news.ToArray()) + ".\n"
                + "For a Contract that can't complete, propose dropping it, with the reason, in a revised Mission Plan for the player's Approval. "
                + "A revised plan only ever drops Contracts: never add one the player didn't ask for.\n";
            _news.Clear();
            return text;
        }

        /// <summary>
        /// The state of each Requirement in <paramref name="served"/> as the snapshot has it, with the gaps the mod knows. A placed one the
        /// game hasn't marked met (#1035) "completes after the Mission" only once the snapshot shows its placement made (#1244); else it is
        /// not met, with the placement's gap.
        /// </summary>
        public List<RequirementReading> Read(GameSnapshot snapshot, IEnumerable<ChosenRequirement> served, IDictionary<string, string> gaps)
        {
            var contracts = snapshot == null ? null : snapshot.Contracts;
            var readings = new List<RequirementReading>();
            foreach (var requirement in served)
            {
                var guid = GuidOf(requirement.Key);
                var reading = new RequirementReading { Key = requirement.Key, Contract = requirement.Title, Requirement = requirement.Words };
                var contract = contracts == null ? null : contracts.Accepted.FirstOrDefault(c => c.Guid == guid);
                if (Completed.Contains(guid)) reading.State = "completed";
                else if (contract == null || contract.State != "Active") reading.State = _droppable.ContainsKey(guid) ? _droppable[guid] : "no longer accepted";
                else
                {
                    var found = Find(contract.Requirements, requirement.Key);
                    var unplaced = PlacementKinds.Unplaced(snapshot, requirement);
                    reading.State = found == null ? "not found" : found.State == "Complete" ? "met" : found.State == "Failed" ? "failed"
                        // #1035: placed, it is the game's to complete after the Mission: no hold waits for it and it is no miss.
                        : requirement.AfterMission != null && unplaced == null ? CompletesAfter : "not met";
                    // #1244: a placement not made is a miss like any other, its gap the placement's.
                    if (reading.State == "not met" && unplaced != null) reading.Gap = unplaced;
                }
                string gap;
                if (reading.State == "not met" && gaps != null && gaps.TryGetValue(requirement.Key, out gap)) reading.Gap = gap;
                readings.Add(reading);
            }
            return readings;
        }

        /// <summary>The readings as one line of JSON data for the Debrief request.</summary>
        public static string Line(IList<RequirementReading> readings)
        {
            return "Contract Requirements this sequence served (the mod's reading of the game, JSON data): " + Json(readings) + "\n"
                + AfterMissionWords(readings)
                + (readings.Any(r => r.State == "not met") ? "For a Requirement not met, propose a trim burn or a retry leg (priced at the plan proposal), or dropping its Contract, with the reason.\n" : "");
        }

        /// <summary>#1035: the reading of a Requirement the game completes after the Mission, still open.</summary>
        public const string CompletesAfter = ContractCheck.CompletesAfter;

        /// <summary>A Gap given a hands-on Requirement still not met when the player hands the craft back.</summary>
        public const string NotMetAtHandBack = "the game hasn't marked it met after the Manual stretch";

        /// <summary>
        /// #1032: the readings of the Requirements a Manual stretch served, for the short Debrief at Hand back: one met is said so; one not met
        /// is offered again as a new planned Manual stretch, or dropped.
        /// </summary>
        public static string HandBackLine(IList<RequirementReading> readings)
        {
            return "Contract Requirements this Manual stretch served (the mod's reading of the game, JSON data): " + Json(readings) + "\n"
                + (readings.Any(r => r.State == "met" || r.State == "completed") ? "For a Requirement met, say in the Debrief that it is met.\n" : "")
                + AfterMissionWords(readings)
                + (readings.Any(r => r.State == "not met") ? "For a Requirement not met, offer the player a retry (a new planned Manual stretch, in the revised Mission Plan) or dropping its Contract, with the reason.\n" : "");
        }

        private static string AfterMissionWords(IEnumerable<RequirementReading> readings)
        {
            return readings.Any(r => r.State == CompletesAfter) ? "For a Requirement that " + CompletesAfter + ", say in the Debrief that the game completes it after the Mission once its placement is made (the telescope in solar orbit, the station set up); the game doesn't mark the placement itself.\n" : "";
        }

        private static string Json(IEnumerable<RequirementReading> readings)
        {
            var json = new JsonWriter().BeginArray();
            foreach (var r in readings)
            {
                json.BeginObject().Field("contract", r.Contract).Field("requirement", r.Requirement).Field("state", r.State);
                if (r.Gap.Length > 0) json.Field("gap", r.Gap);
                json.EndObject();
            }
            return json.EndArray().ToString();
        }

        /// <summary>
        /// The vessel that serves <paramref name="requirement"/> meets what it asks, by the snapshot: the game should mark it met. That is
        /// the craft, or (#1243) the released Child Vessel whose GUID is <paramref name="childVesselId"/> when the plan serves it from that
        /// Child Vessel's leg; "" for the craft.
        /// </summary>
        public static bool ConditionsHold(GameSnapshot snapshot, ChosenRequirement requirement, string home, string childVesselId = "")
        {
            // #1244: a placed one is met by its placement; the game marks only what completes after the Mission.
            if (requirement.AfterMission != null) return snapshot != null && PlacementKinds.Unplaced(snapshot, requirement) == null;
            var serving = Serving.Of(snapshot, childVesselId);
            if (serving == null) return false;
            var down = serving.Situation == "LANDED" || serving.Situation == "SPLASHED";
            if (requirement.Return) return serving.Body == home && down;
            if (serving.Body != requirement.Body) return false;
            switch (requirement.Reach)
            {
                case ContractReach.Land: return down;
                case ContractReach.Orbit: return serving.Situation == "ORBITING" && OrbitMiss(snapshot, serving, requirement) == null;
                default: return true;
            }
        }

        /// <summary>Why the vessel that serves <paramref name="requirement"/> (as in <see cref="ConditionsHold"/>) doesn't meet it: where it is against what is asked.</summary>
        public static string Gap(GameSnapshot snapshot, ChosenRequirement requirement, string childVesselId = "")
        {
            if (requirement.AfterMission != null && snapshot != null) return PlacementKinds.Unplaced(snapshot, requirement) ?? "";
            var serving = Serving.Of(snapshot, childVesselId);
            if (serving == null) return "the mod has no reading of the " + (string.IsNullOrEmpty(childVesselId) ? "craft" : "Child Vessel");
            var miss = serving.Situation == "ORBITING" && serving.Body == requirement.Body ? OrbitMiss(snapshot, serving, requirement) : null;
            if (miss != null) return miss;
            return serving.Noun + " is " + serving.Situation + " at " + ContractKinds.Named(serving.Body) + ", and the Requirement asks it to " + requirement.Words;
        }

        /// <summary>#1031: a satellite's orbit against the Contract's window, "inclination 3.4° outside the 5% window"; null when inside, or not a satellite.</summary>
        private static string OrbitMiss(GameSnapshot snapshot, Serving serving, ChosenRequirement requirement)
        {
            // A Child Vessel's orbit not read is no reading of it, never a pass; the craft's is judged as before (#1031).
            if (requirement.Orbit != null && !serving.HasOrbit && serving.Child) return "the mod has no reading of the Child Vessel's orbit";
            var body = requirement.Orbit == null || !serving.HasOrbit ? null : snapshot.Bodies.FirstOrDefault(b => b.Name == requirement.Orbit.Body);
            return body == null ? null : requirement.Orbit.Miss(body, serving.Apoapsis, serving.Periapsis, serving.Inclination);
        }

        /// <summary>
        /// #1243: where the vessel serving a Requirement is, by the snapshot: the craft, or a released Child Vessel found by its GUID among
        /// the snapshot's vessels (or the craft's own entry, when the player is in it). Null when the snapshot has no reading of it.
        /// </summary>
        private sealed class Serving
        {
            public bool Child;
            public string Noun = "the craft";
            public string Body = "";
            public string Situation = "";
            public bool HasOrbit;
            public double? Apoapsis;
            public double? Periapsis;
            public double? Inclination;

            public static Serving Of(GameSnapshot snapshot, string childVesselId)
            {
                if (snapshot == null) return null;
                var child = !string.IsNullOrEmpty(childVesselId);
                if (!child || snapshot.Craft.VesselId == childVesselId)
                {
                    var orbit = snapshot.Orbit;
                    return new Serving
                    {
                        Child = child, Noun = child ? "the Child Vessel" : "the craft", Body = snapshot.Body ?? "", Situation = snapshot.Situation ?? "", HasOrbit = orbit != null,
                        Apoapsis = orbit == null ? (double?)null : orbit.Apoapsis, Periapsis = orbit == null ? (double?)null : orbit.Periapsis,
                        Inclination = orbit == null ? (double?)null : orbit.Inclination,
                    };
                }
                var vessel = snapshot.Vessels.FirstOrDefault(v => v.VesselId == childVesselId);
                if (vessel == null) return null;
                return new Serving
                {
                    Child = true, Noun = "the Child Vessel", Body = vessel.Body, Situation = vessel.Situation, HasOrbit = vessel.PeriapsisM.HasValue || vessel.Inclination.HasValue,
                    Apoapsis = vessel.ApoapsisM, Periapsis = vessel.PeriapsisM, Inclination = vessel.Inclination,
                };
            }
        }

        public static string GuidOf(string key)
        {
            var slash = (key ?? "").LastIndexOf('/');
            return slash < 0 ? key ?? "" : key.Substring(0, slash);
        }

        private static string StateOf(SnapshotContracts contracts, string guid)
        {
            var finished = contracts.Finished.FirstOrDefault(f => f.Guid == guid);
            return finished == null ? "" : finished.State;
        }

        internal static ContractRequirement Find(IEnumerable<ContractRequirement> requirements, string key)
        {
            foreach (var requirement in requirements)
            {
                if (requirement.Key == key) return requirement;
                var under = Find(requirement.Requirements, key);
                if (under != null) return under;
            }
            return null;
        }

        private static string Quoted(string title)
        {
            return MissionPrompts.Quoted(title);
        }

        private static string Whole(double value)
        {
            return System.Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
