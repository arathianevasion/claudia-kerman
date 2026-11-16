// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>One Contract Requirement the player chose on the Contract check card, kept for the plan check (#1024).</summary>
    public sealed class ChosenRequirement
    {
        /// <summary>The Requirement's key: its Contract's GUID and its place in the tree.</summary>
        public string Key = "";
        public string Title = "";
        /// <summary>What it asks, "orbit the Mun".</summary>
        public string Words = "";
        public string Body = "";
        public ContractReach Reach;
        /// <summary>Met only once the craft is home again.</summary>
        public bool Return;
        /// <summary>#1029: the part test or haul, with the window its test is run in; null for every other kind.</summary>
        public PartTestSpec Test;
        /// <summary>#1030: the experiment the science block runs for it, "" for any; null when it isn't collect science or a survey.</summary>
        public string Experiment;
        /// <summary>#1031: the satellite orbit it asks for; null for every other kind.</summary>
        public ContractOrbit Orbit;
        /// <summary>#1031: a Child Vessel the craft releases may serve it (a satellite).</summary>
        public bool ChildMay;
        /// <summary>#1032: the hands-on work the player does in a Manual stretch after the leg that lands there, "plant a flag on EVA"; "" when none.</summary>
        public string Manual = "";
        /// <summary>#1035: the wait after the Mission before the game marks it met, once placed; null when it is met in flight.</summary>
        public MissionWait AfterMission;
        /// <summary>#1035: the Sentinel telescope's solar orbit it places; null for every other kind.</summary>
        public SolarPlacement Placement;
    }

    /// <summary>The Contract Route's record of one chosen Requirement: the leg of the Mission Plan that serves it, -1 when none does.</summary>
    public sealed class ServedRequirement
    {
        public string Key = "";
        public string Title = "";
        public string Words = "";
        public int Leg = -1;
        public string LegName = "";
    }

    /// <summary>
    /// #1024: Core's check of Claudia's Mission Plan against the Contract Requirements the player chose (ADR 0021: the mod's own logic;
    /// ADR 0012: at the one plan proposal). Each leg is read by what its words do (go to a body, capture, land, come home, as the Craft
    /// Check reads them), and each Requirement is assigned the first leg that can meet it: a leg that takes the craft to that body, at
    /// least as far as the Requirement asks (a landing meets an orbit; a flyby doesn't). A Requirement no leg meets is a problem for
    /// Claudia to fix before the player sees the plan. The assignment is the Serves tags, so they come from the Requirements and the
    /// plan's legs, never from anything Claudia says a leg serves. The player's own typed words are tagged too, where a leg serves them.
    /// </summary>
    internal sealed class PlanServes
    {
        private static readonly Regex Orbit = new Regex(@"\borbit(?:s|ing|ed)?\b", RegexOptions.IgnoreCase);
        /// <summary>#1030: a leg that runs the science block.</summary>
        private static readonly Regex ScienceWords = new Regex(@"\bscience\b|\bexperiments?\b", RegexOptions.IgnoreCase);
        /// <summary>#1029: a leg that hops below orbit over home, as a Contract Route's hop is flown.</summary>
        private static readonly Regex HopWords = new Regex(@"\bhop(?:s|ping)?\b|\bsuborbital\b", RegexOptions.IgnoreCase);

        /// <summary>What Claudia must fix, one finding per Requirement left unserved or unmeetable; empty when the plan covers them all.</summary>
        public readonly List<string> Problems = new List<string>();
        /// <summary>The Serves tags of each leg of the plan, in the plan's order (the player's words first).</summary>
        public readonly List<string[]> Legs = new List<string[]>();
        /// <summary>Each chosen Requirement and the leg that serves it.</summary>
        public readonly List<ServedRequirement> Record = new List<ServedRequirement>();

        private sealed class Touch
        {
            public int Leg;
            public int Order;
            public string Body = "";
            public ContractReach Reach;
            /// <summary>The craft comes home at this touch (Body is home).</summary>
            public bool Home;
            /// <summary>#1029: a hop below orbit over home: up through the air and back down.</summary>
            public bool Hop;
            /// <summary>#1160: the launch options the leg's words write; null when it writes none (then plan_ascent's default shape is judged).</summary>
            public WrittenLaunch Launch;
            /// <summary>#1031: the leg is flown by a Child Vessel the craft released, not the craft: it serves only a satellite.</summary>
            public bool Child;
        }

        /// <summary>#1031: an orbit's figures as a leg writes them: "apoapsis 563.8 km", "periapsis 536 km", "inclination 88.4°".</summary>
        private static double? Figure(string words, string name, bool distance)
        {
            var m = Regex.Match(words, @"\b" + name + @"\b\s*(?:=|:|of|to|at)?\s*(\d[\d,]*(?:\.\d+)?)\s*(km\b|m\b|°|deg)?", RegexOptions.IgnoreCase);
            double value;
            if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return null;
            return distance && m.Groups[2].Value.ToLowerInvariant() != "m" ? value * 1000 : value;
        }

        /// <param name="from">#1025: the first leg that may serve a Requirement; the legs before it are walked only for where the craft is.</param>
        public static PlanServes Check(GameSnapshot snapshot, MissionPlan plan, IList<ChosenRequirement> chosen, string words, string home, int from = 0)
        {
            var result = new PlanServes();
            var whole = Read(snapshot, plan, home);
            var all = whole.Where(t => t.Leg >= from).ToList();
            var touches = all.Where(t => !t.Child).ToList();
            var orbitLegs = new Dictionary<string, Touch>();
            var tags = plan.Sequences.Select(_ => new List<string>()).ToList();
            var reading = WordsReading.Read(snapshot, words, home);
            var judge = new PartTestJudge(snapshot, home);
            foreach (var need in reading.Needs)
            {
                var leg = touches.FirstOrDefault(t => !t.Home && t.Body == need.Key && t.Reach >= need.Value);
                if (leg != null) Tag(tags[leg.Leg], ContractCheck.WordsTag);
            }
            if (SequenceWords.ReturnHome.IsMatch(words ?? ""))
            {
                var back = touches.FirstOrDefault(t => t.Home);
                if (back != null) Tag(tags[back.Leg], ContractCheck.WordsTag);
            }
            // #1031: a satellite's orbit first, so its other Requirements are served where it is reached.
            foreach (var requirement in chosen.OrderBy(r => r.Orbit == null ? 1 : 0))
            {
                var served = new ServedRequirement { Key = requirement.Key, Title = requirement.Title, Words = requirement.Words };
                result.Record.Add(served);
                var by = requirement.Manual.Length > 0 ? ManualTouch(snapshot, whole.Where(t => !t.Child).ToList(), requirement, result.Problems, plan, from)
                    : requirement.Test != null && requirement.Test.Band != null && requirement.Test.Window != TestWindow.None
                    ? TestTouch(snapshot, touches, requirement, result.Problems, plan, judge, home)
                    : requirement.Orbit != null ? OrbitTouch(snapshot, all, requirement, result.Problems, plan)
                    : ServingTouch(requirement.ChildMay ? all : touches, requirement, result.Problems, plan);
                if (by == null) continue;
                Touch orbitLeg;
                var guid = ContractWatch.GuidOf(requirement.Key);
                if (requirement.Orbit != null) orbitLegs[guid] = by;
                else if (orbitLegs.TryGetValue(guid, out orbitLeg)) by = orbitLeg;
                if (requirement.Experiment != null && !RunsScience(touches, by, plan, requirement.Experiment))
                    result.Problems.Add("\"" + requirement.Title + "\" needs \"" + requirement.Words + "\", but no leg runs the science block there: add a science step to leg "
                        + (by.Leg + 1) + " \"" + plan.Sequences[by.Leg].Name + "\", or a leg after it before the craft leaves");
                served.Leg = by.Leg;
                served.LegName = plan.Sequences[by.Leg].Name;
                Tag(tags[by.Leg], requirement.Title);
            }
            result.Legs.AddRange(tags.Select(t => t.ToArray()));
            return result;
        }

        /// <summary>
        /// #1030: a leg from the one serving it until the craft goes elsewhere says it runs the science block (its words name science, an
        /// experiment, or the experiment itself), so collect science or a survey waypoint is taken where it is met.
        /// </summary>
        private static bool RunsScience(IList<Touch> touches, Touch by, MissionPlan plan, string experiment)
        {
            var next = touches.FirstOrDefault(t => t.Order > by.Order && t.Leg > by.Leg && (t.Body != by.Body || t.Home != by.Home));
            var end = next == null ? plan.Sequences.Count : next.Leg;
            for (var leg = by.Leg; leg < end; leg++)
            {
                var words = plan.Sequences[leg].Name + " " + plan.Sequences[leg].Intent;
                if (ScienceWords.IsMatch(words) || (experiment.Length > 0 && words.IndexOf(experiment, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            }
            return false;
        }

        private static void Tag(List<string> tags, string tag)
        {
            if (tag.Length > 0 && !tags.Contains(tag)) tags.Add(tag);
        }

        /// <summary>The leg that serves <paramref name="requirement"/>; null, with the finding, when none can.</summary>
        private static Touch ServingTouch(IList<Touch> touches, ChosenRequirement requirement, List<string> problems, MissionPlan plan)
        {
            var visit = touches.FirstOrDefault(t => !t.Home && t.Body == requirement.Body && t.Reach >= requirement.Reach);
            var what = "\"" + requirement.Title + "\" needs \"" + requirement.Words + "\"";
            if (visit == null)
            {
                var near = touches.Where(t => !t.Home && t.Body == requirement.Body).OrderByDescending(t => t.Reach).FirstOrDefault();
                problems.Add(near == null
                    ? what + ", and no leg of the plan goes there: add a leg that does"
                    : what + ", but leg " + (near.Leg + 1) + " \"" + plan.Sequences[near.Leg].Name + "\" only " + Does(near.Reach) + " "
                        + ContractKinds.Named(requirement.Body) + ": make that leg " + Needs(requirement.Reach) + ", or add one that does");
                return null;
            }
            if (!requirement.Return) return visit;
            var back = touches.FirstOrDefault(t => t.Home && t.Order >= visit.Order);
            if (back == null) problems.Add(what + ", and no leg of the plan brings the craft home after it: add the return to Kerbin");
            return back;
        }

        /// <summary>
        /// #1032: the Manual stretch that serves a hands-on Requirement: the craft's first manual leg after the landing that meets it, before
        /// the craft goes elsewhere. The landing may be flown already (a retry after Hand back), so it is looked for in the whole plan; the
        /// Manual stretch is one still to fly (from <paramref name="from"/>). A plan with none there is a finding.
        /// </summary>
        private static Touch ManualTouch(GameSnapshot snapshot, IList<Touch> touches, ChosenRequirement requirement, List<string> problems, MissionPlan plan, int from)
        {
            var landing = ServingTouch(touches, requirement, problems, plan);
            if (landing == null) return null;
            var next = touches.FirstOrDefault(t => t.Order > landing.Order && (t.Body != landing.Body || t.Home));
            var end = next == null ? plan.Sequences.Count : next.Leg;
            for (var leg = Math.Max(landing.Leg + 1, from); leg < end; leg++)
                if (plan.Sequences[leg].Manual && MissionCards.FliesCraft(snapshot.Craft.Name, plan.Sequences[leg].Vessel))
                    return new Touch { Leg = leg, Order = landing.Order, Body = landing.Body, Reach = landing.Reach };
            problems.Add("\"" + requirement.Title + "\" needs \"" + requirement.Words + "\", by hand: no Manual stretch for the player to " + requirement.Manual
                + " after leg " + (landing.Leg + 1) + " \"" + plan.Sequences[landing.Leg].Name + "\" lands there: add one (manual true and an empty script) after that leg, before the craft leaves");
            return null;
        }

        /// <summary>
        /// #1029: the leg whose flight runs a part test, its band judged there as pricing judges it (<see cref="PartTestJudge"/>): the launch
        /// (a hop's straight up, any other's to orbit) for one on the ascent; the way down home (the return, or a hop's fall) for one on the
        /// descent. A leg that misses the band is a finding with the figures; the leg is still the one that serves it.
        /// </summary>
        private static Touch TestTouch(GameSnapshot snapshot, IList<Touch> touches, ChosenRequirement requirement, List<string> problems, MissionPlan plan, PartTestJudge judge, string home)
        {
            var test = requirement.Test;
            var what = "\"" + requirement.Title + "\" needs \"" + requirement.Words + "\"";
            var ascent = test.Window == TestWindow.Ascent;
            var by = ascent ? touches.FirstOrDefault(t => !t.Home && t.Body == home) : touches.FirstOrDefault(t => t.Home) ?? touches.FirstOrDefault(t => t.Hop);
            if (by == null)
            {
                problems.Add(what + (ascent ? ", and no leg of the plan launches through it: add the launch" : ", and no leg brings the craft down home through it: add the return to " + home));
                return null;
            }
            string why;
            var body = SequenceWords.Body(snapshot, home);
            var launch = by.Launch != null && (!by.Hop || by.Launch.TargetAp.HasValue) ? by.Launch : null;
            var window = ascent
                ? judge.Window(test, launch != null && by.Hop ? launch.TargetAp : by.Hop && body != null ? test.Band.TargetAp(body) : (double?)null, !by.Hop, null, out why, launch)
                : judge.Window(test, null, false, judge.DownKg(0), out why);
            if (window == TestWindow.None)
                problems.Add(what + ", but leg " + (by.Leg + 1) + " \"" + plan.Sequences[by.Leg].Name + "\" misses it: " + why
                    + (ascent && body != null ? ": fly it as a hop straight up to the band (launch with target_ap " + Math.Round(test.Band.TargetAp(body)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " and turn_start_speed 100000), or drop the Contract" : ""));
            return by;
        }

        /// <summary>
        /// #1031: the leg that puts the satellite (the craft or one Child Vessel) into the Contract's orbit: from the first leg that reaches
        /// orbit round the body, the first until the craft goes elsewhere that writes the orbit's figures. Figures outside the deviation
        /// window, or none written, are a finding; with none written the first leg in orbit is the one that serves it.
        /// </summary>
        private static Touch OrbitTouch(GameSnapshot snapshot, IList<Touch> touches, ChosenRequirement requirement, List<string> problems, MissionPlan plan)
        {
            var visit = ServingTouch(touches, requirement, problems, plan);
            if (visit == null) return null;
            var body = snapshot.Bodies.FirstOrDefault(b => b.Name == requirement.Orbit.Body);
            var next = touches.FirstOrDefault(t => t.Order > visit.Order && !t.Child && (t.Body != visit.Body || t.Home));
            var end = next == null ? plan.Sequences.Count : next.Leg;
            var what = "\"" + requirement.Title + "\" needs \"" + requirement.Words + "\"";
            // A parking orbit's figures on the way may come first: the leg inside the window serves it, else the last that writes any.
            int? written = null;
            string missed = null;
            for (var leg = visit.Leg; leg < end && body != null; leg++)
            {
                var words = plan.Sequences[leg].Name + " " + plan.Sequences[leg].Intent;
                var ap = Figure(words, "apoapsis", true);
                var pe = Figure(words, "periapsis", true);
                var inc = Figure(words, "inclination", false);
                if (!ap.HasValue && !pe.HasValue && !inc.HasValue) continue;
                written = leg;
                missed = requirement.Orbit.Miss(body, ap, pe, inc);
                if (missed == null) break;
            }
            if (written.HasValue)
            {
                var leg = written.Value;
                if (missed != null) problems.Add(what + ", but leg " + (leg + 1) + " \"" + plan.Sequences[leg].Name + "\" misses the orbit: " + missed + ": fly " + requirement.Orbit.Words(body));
                return touches.FirstOrDefault(t => t.Leg == leg) ?? new Touch { Leg = leg, Order = visit.Order, Body = visit.Body, Reach = visit.Reach, Child = visit.Child };
            }
            if (body != null) problems.Add(what + ", and no leg of the plan writes the orbit it flies to: write " + requirement.Orbit.Words(body) + " in the leg that reaches it");
            return visit;
        }

        private static string Does(ContractReach reach)
        {
            return reach == ContractReach.Flyby ? "flies by" : reach == ContractReach.Orbit ? "orbits" : "lands on";
        }

        private static string Needs(ContractReach reach)
        {
            return reach == ContractReach.Flyby ? "fly by it" : reach == ContractReach.Orbit ? "capture into orbit round it" : "land on it";
        }

        /// <summary>Where each leg takes the craft, in the plan's order; the Craft Check's reading of a leg's words (<see cref="SequenceWords"/>).</summary>
        private static List<Touch> Read(GameSnapshot snapshot, MissionPlan plan, string home)
        {
            var touches = new List<Touch>();
            var homeBody = SequenceWords.Body(snapshot, home);
            // The whole plan is walked, flown legs included (a revision keeps them), so it starts where a Mission does: at home.
            var at = homeBody;
            for (var i = 0; i < plan.Sequences.Count; i++)
            {
                var sequence = plan.Sequences[i];
                var words = SequenceWords.ActionWords(sequence);
                if (!MissionCards.FliesCraft(snapshot.Craft.Name, sequence.Vessel))
                {
                    // #1031: a Child Vessel's leg starts where the craft is; it may put a satellite in orbit there.
                    var there = (SequenceWords.Trip.IsMatch(words) ? SequenceWords.TripTarget(snapshot, words, at, home) : null) ?? SequenceWords.LegBody(snapshot, words, at) ?? at;
                    if (there != null && (SequenceWords.Capture.IsMatch(words) || Orbit.IsMatch(words)))
                        touches.Add(new Touch { Leg = i, Order = touches.Count, Body = there.Name, Reach = ContractReach.Orbit, Child = true });
                    continue;
                }
                var written = WrittenLaunch.Read(sequence.Name + " " + sequence.Intent);
                var landing = SequenceWords.Landing.IsMatch(words);
                var captures = SequenceWords.Capture.IsMatch(words) || Orbit.IsMatch(words);
                var trips = SequenceWords.Trip.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words);
                var target = trips ? SequenceWords.TripTarget(snapshot, words, at, home) : null;
                var goesHome = trips && target != null && target.Name == home && at != null && at.Name != home;
                var add = new System.Action<string, ContractReach, bool>((body, reach, back) =>
                    touches.Add(new Touch { Leg = i, Order = touches.Count, Body = body, Reach = reach, Home = back, Launch = written }));
                var homeNow = at != null && at.Name == home;
                if (homeNow && (written != null && written.IsHop(homeBody) || HopWords.IsMatch(sequence.Name + " " + words) && !captures))
                {
                    // #1029: up through the air over home and back down; it goes nowhere else.
                    touches.Add(new Touch { Leg = i, Order = touches.Count, Body = home, Reach = ContractReach.Hop, Hop = true, Launch = written });
                    continue;
                }
                if (homeNow && touches.Any(t => t.Body == home && !t.Home) && (SequenceWords.KerbinDown.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(sequence.Name + " " + words)))
                {
                    // #1034: down home again from orbit round it ("Deorbit and land at KSC"): a tourist in Kerbin orbit is brought home here.
                    add(home, ContractReach.Land, true);
                    continue;
                }
                if (goesHome && landing && SequenceWords.Landing.Match(words).Index < SequenceWords.Trip.Match(words).Index)
                {
                    // "Land on the Mun and return to Kerbin": down there first, then home.
                    var named = SequenceWords.NamedBody(snapshot, words);
                    var site = named != null && named.Name != home ? named : at;
                    if (site != null && site.Name != home) add(site.Name, ContractReach.Land, false);
                    landing = false;
                }
                if (goesHome)
                {
                    // Coming home passes the body it leaves, at least as a flyby; a capture there is its own leg's.
                    add(home, ContractReach.Land, true);
                    at = target;
                    continue;
                }
                if (trips && target != null && at != null && target != at)
                {
                    var reach = SequenceWords.Flyby.IsMatch(words) ? ContractReach.Flyby : landing ? ContractReach.Land : captures ? ContractReach.Orbit : ContractReach.Flyby;
                    add(target.Name, reach, false);
                    at = target;
                    continue;
                }
                var here = SequenceWords.LegBody(snapshot, words, at);
                if (here == null) continue;
                if (landing) add(here.Name, ContractReach.Land, false);
                else if (captures || (SequenceWords.Ascent.IsMatch(words) && here.Name == home)) add(here.Name, ContractReach.Orbit, false);
                else if (SequenceWords.Flyby.IsMatch(words)) add(here.Name, ContractReach.Flyby, false);
                at = here;
            }
            return touches;
        }
    }
}
