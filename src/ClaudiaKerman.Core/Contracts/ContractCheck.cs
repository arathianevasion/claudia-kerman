// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>A Contract on the Contract check card that this craft can fly: its box is pre-ticked when it is in the suggested set.</summary>
    public sealed class ContractCheckRow
    {
        public string Guid = "";
        public string Title = "";
        /// <summary>The phases of flight its Contract Requirements fall in: "Mun", "Kerbin orbit", "Return".</summary>
        public string Phase = "";
        /// <summary>What it adds to the suggested set's route, m/s: the route with it less the route without it.</summary>
        public double AddedDeltaV;
        public double? DeadlineUt;
        public bool Ticked;
        /// <summary>Its optional Requirements skipped, with why; "" when none.</summary>
        public string Note = "";
        /// <summary>Unticked and not fitting as a plain tick: the swap that brings it in (#1023); null when none.</summary>
        public SwapOffer Swap;
        /// <summary>#1032: one of its Requirements is a Manual stretch the player flies (a flag, a scan, a sample).</summary>
        public bool YouFly;
        /// <summary>#1035: one of its Requirements completes after the Mission (a Sentinel telescope mapping, a station collecting).</summary>
        public bool AfterMission;

        /// <summary>The line under its box: "Mun · you fly · completes after the Mission · +1,170 m/s · deadline Y1 D30 (in 12d)", or "no deadline".</summary>
        public string Detail(double now)
        {
            return Phase + (YouFly ? " · you fly" : "") + (AfterMission ? " · " + ContractCheck.CompletesAfter : "") + " · +" + Math.Round(AddedDeltaV).ToString("#,0", CultureInfo.InvariantCulture) + " m/s · "
                + (DeadlineUt.HasValue ? "deadline " + KerbinTime.ShortDate(DeadlineUt.Value) + " (" + KerbinTime.FromNow(DeadlineUt.Value, now) + ")" : "no deadline");
        }
    }

    /// <summary>Why a Contract isn't on this flight, for the card's chip.</summary>
    public enum LeftOutKind
    {
        /// <summary>Core can't plan one of its required Contract Requirements yet.</summary>
        NotYet,
        /// <summary>The craft hasn't the delta-v for it, alone or with the others.</summary>
        CraftCant,
        /// <summary>The route finishes it too near its deadline.</summary>
        CompletesLater,
        /// <summary>#1036: the player's typed words rule out what it needs; the words win.</summary>
        CantWithWords,
        /// <summary>#1033: a Mission has one Target Vessel: another Target Vessel Contract is in the set, or the goal or the player's pick gives the target.</summary>
        OneTarget,
    }

    /// <summary>#1036: one leg of the route and who it serves: the player's words, the Contracts, or both when they share it.</summary>
    public sealed class RouteLeg
    {
        /// <summary>The leg in words, "orbit the Mun".</summary>
        public string Text = "";
        /// <summary>The Serves tags: <see cref="ContractCheck.WordsTag"/> for the player's typed words, and each Contract's title.</summary>
        public string[] Serves = new string[0];
    }

    /// <summary>A Contract on the card's "Not this flight" list, with its reason.</summary>
    public sealed class LeftOutContract
    {
        public string Guid = "";
        public string Title = "";
        public LeftOutKind Kind;
        /// <summary>"this craft can't: 410 m/s short", "not yet: …" or "completes later: …".</summary>
        public string Reason = "";
        /// <summary>The player can tick it: Core plans it (it isn't "not yet"), #1023. Ticked, its reason gives way to its detail for the set.</summary>
        public bool Tickable;
        public bool Ticked;
        /// <summary>Where it fits in place of others, the swap that does it in one click; null when none.</summary>
        public SwapOffer Swap;
    }

    /// <summary>#1023: "Swap in X for Y": the ticked Contract that goes out (#1159: one) so the one comes in and the set still fits.</summary>
    public sealed class SwapOffer
    {
        /// <summary>The Contract that comes in.</summary>
        public string Guid = "";
        /// <summary>The ticked Contracts that go out.</summary>
        public string[] Drop = new string[0];
        public string Label = "";
    }

    /// <summary>The set the player sent with "Plan these": what the Mission starts from.</summary>
    public sealed class ContractChoice
    {
        public List<string> Guids = new List<string>();
        /// <summary>The player's typed words, "" when none.</summary>
        public string Words = "";
        /// <summary>The Contract Route in words, "orbit the Mun; return to Kerbin": what the Craft Check judges with the player's words.</summary>
        public string RouteWords = "";
        /// <summary>#1036: the route's legs and what each serves.</summary>
        public List<RouteLeg> Legs = new List<RouteLeg>();
        /// <summary>#1024: every Contract Requirement chosen, which the Mission's plan proposal is checked against.</summary>
        public List<ChosenRequirement> Requirements = new List<ChosenRequirement>();
        /// <summary>#1033: the vessel of the chosen Target Vessel Contract, the Mission's Target Vessel (ADR 0013's amendment); null when none is chosen.</summary>
        public SnapshotVessel Target;
        /// <summary>#1033: that Contract's GUID and title; "" when none.</summary>
        public string TargetGuid = "";
        public string TargetTitle = "";
        /// <summary>The request's Contract Route section (<see cref="ContractCheck.Choose"/>).</summary>
        public string Section = "";
        /// <summary>The card folded to one line.</summary>
        public string Summary = "";

        /// <summary>The goal the Craft Check judges and the Mission is pinned with: the player's words and then the Contracts' route.</summary>
        public string Goal
        {
            get
            {
                if (RouteWords.Length == 0) return Words;
                return Words.Length == 0 ? "Contracts: " + RouteWords : Words.TrimEnd('.', ' ') + ". Contracts: " + RouteWords;
            }
        }
    }

    /// <summary>
    /// #1022: the Contract check behind the card Mission Control posts at Start Mission (ADR 0021: the mod's logic, no Claude). It reads
    /// the accepted Contracts with <see cref="ContractKinds"/>, prices whole Contract Routes with <see cref="ContractRoutes"/> and searches
    /// the plannable ones (up to <see cref="MostSearched"/>) exhaustively, largest sets first, stopping at the first size where a set fits:
    /// the route, with the Craft Check's reserve on top, within the craft's delta-v, and each Contract done before its deadline with 10% of
    /// the time to it, and at least a Kerbin day, to spare. Ties go to the larger Strategy-adjusted reward (funds, then science, then
    /// reputation), then the least delta-v. The player's typed words are the fixed part of every route and are never dropped.
    /// </summary>
    public sealed class ContractCheck
    {
        /// <summary>#1028: up to this many plannable Contracts are searched one by one; above it, in groups by the bodies they need.</summary>
        public const int MostOneByOne = 10;
        /// <summary>#1028: up to this many groups are searched exhaustively; above it, added greedily by reward per extra m/s.</summary>
        public const int MostGroups = 10;
        /// <summary>#1028: how many times a set the planners price short falls to the next best before it is reported.</summary>
        public const int MostFallbacks = 3;
        /// <summary>#1028: the time the search may take before a suggestion is on the card.</summary>
        public static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(2);
        /// <summary>
        /// #1028: the exhaustive search's share of <see cref="SearchBudget"/>; the rest is for the greedy search should it run out with
        /// nothing, and for the planners' re-pricing.
        /// </summary>
        internal static readonly TimeSpan ExhaustiveBudget = TimeSpan.FromTicks(SearchBudget.Ticks / 2);
        /// <summary>The spare a deadline needs: this fraction of the time to it, and at least a Kerbin day.</summary>
        public const double DeadlineSpareFraction = 0.10;
        /// <summary>The Serves tag for what the player typed.</summary>
        public const string WordsTag = "your words";
        /// <summary>#1035: how the card shows a Contract whose time-based Requirement the game completes after the Mission.</summary>
        public const string CompletesAfter = "completes after the Mission";

        public string Words = "";
        public bool Remote;
        /// <summary>How many Contracts are accepted.</summary>
        public int Accepted;
        /// <summary>The craft's delta-v is known (the stock simulator is on and reads true), so there is anything to price.</summary>
        public bool Priceable;
        /// <summary>
        /// The typed words alone fit the craft with the reserve; when not, no Contract is suggested and the Craft Check says why. With no
        /// typed words, also true when a Contract is met on a hop alone (#1048), so a craft that can't reach orbit is still searched.
        /// </summary>
        public bool WordsFit;
        public double CraftDeltaV;
        /// <summary>The suggested set's route with the player's words, m/s.</summary>
        public double RouteDeltaV;
        /// <summary>#1164: the player has ticked, unticked or swapped, so <see cref="RouteDeltaV"/> is the Delta-v Map's figure, rougher than the planners'; false as first suggested.</summary>
        public bool RouteIsEstimate;
        public int ReservePercent;
        /// <summary>The reserve the route keeps, m/s.</summary>
        public double Reserve;
        /// <summary>What is left over the route and the reserve, m/s; below 0, the shortfall.</summary>
        public double Spare;
        /// <summary>#1023: the ticked set fits the craft with the reserve and keeps every deadline.</summary>
        public bool SetFits = true;
        /// <summary>#1023: why the ticked set doesn't fit, "410 m/s short" or "completes later: …"; "" when it fits.</summary>
        public string Problem = "";
        /// <summary>How many times the Contract search has run: once, at <see cref="Work"/>; ticking, unticking and swapping never search (#1023).</summary>
        public int Searches;
        /// <summary>
        /// #1028: what the planners' re-pricing did to the suggestion: the best set by the Delta-v Map came out short and the next best is
        /// suggested, or every set tried came out short and none is; "" when the best set held.
        /// </summary>
        public string SearchNote = "";
        /// <summary>#1163: "your words give conflicting orders: using the cheapest" when the words order the bodies in a loop; "" otherwise.</summary>
        public string OrderNote = "";
        public List<ContractCheckRow> Fitting = new List<ContractCheckRow>();
        public List<LeftOutContract> NotThisFlight = new List<LeftOutContract>();
        /// <summary>#1036: the ticked set's route leg by leg, with what each serves.</summary>
        public List<RouteLeg> Legs = new List<RouteLeg>();

        private GameSnapshot _snapshot;
        private ContractRoutes _routes;
        /// <summary>#1029: where each part test's band is met, on this craft's own flights.</summary>
        private PartTestJudge _judge;
        private string _home = CraftCheckFlow.HomeBody;
        private readonly List<Planned> _planned = new List<Planned>();
        private readonly HashSet<string> _ticked = new HashSet<string>();
        /// <summary>#1028: the Contracts of a set the planners priced short, with the shortfall, m/s: the first such set each was in.</summary>
        private readonly Dictionary<Planned, double> _plannersShort = new Dictionary<Planned, double>();
        /// <summary>#1033: the Target Vessel the goal or the player's pick gives, which a Contract's vessel never replaces; null when none.</summary>
        private TargetVessel _missionTarget;

        /// <summary>A plannable Contract and what it needs.</summary>
        private sealed class Planned
        {
            public SnapshotContract Contract;
            public string Title = "";
            public List<ReadRequirement> Requirements = new List<ReadRequirement>();
            public string Note = "";
            /// <summary>#1033: the vessel it sends Claudia to, for a Target Vessel Contract; null otherwise.</summary>
            public ContractTarget Target;
        }

        /// <summary>
        /// The check of <paramref name="snapshot"/>'s accepted Contracts with the player's <paramref name="words"/>. #1033: <paramref name="target"/>
        /// is the Target Vessel the goal or the player's pick gives (null for none): a Target Vessel Contract for another vessel is "not this flight".
        /// </summary>
        public static ContractCheck Work(GameSnapshot snapshot, string words, bool remote, int marginPercent, TargetVessel target = null)
        {
            var check = new ContractCheck
            {
                Words = (words ?? "").Trim(), Remote = remote, ReservePercent = marginPercent, _snapshot = snapshot, _missionTarget = target,
                Accepted = snapshot.Contracts == null ? 0 : snapshot.Contracts.Accepted.Count,
            };
            check.Priceable = snapshot.DeltaVSimulator && snapshot.Stages.Count > 0 && !snapshot.DeltaVUnreliable;
            if (check.Accepted == 0 || !check.Priceable) return check;
            check.CraftDeltaV = snapshot.Stages.Sum(s => s.DeltaVVac);
            check._routes = new ContractRoutes(snapshot, check.Words, check._home);
            if (check._routes.ConflictingOrders) check.OrderNote = "your words give conflicting orders: using the cheapest";
            check._judge = new PartTestJudge(snapshot, check._home);
            check.Read();
            // #1048: with no typed words, a Contract met on a hop below orbit may fit a craft that can't reach orbit.
            check.WordsFit = check.Fits(check._routes.WordsAlone()) || (check.Words.Length == 0 && check._planned.Any(p => p.Requirements.All(r => r.Band != null)));
            if (check.WordsFit) check.Search();
            return check;
        }

        /// <summary>
        /// #1090, #1241: for typed words that don't fit this craft, the hop Contracts (met on a hop below orbit) the craft could fly without them,
        /// each by its Contract title as the card shows it, never a part id; "" when there are none, or no words were typed.
        /// </summary>
        public string HopsWithoutWords()
        {
            if (Words.Length == 0 || !Priceable) return "";
            var bare = Work(_snapshot, "", Remote, ReservePercent, _missionTarget);
            var titles = new List<string>();
            foreach (var row in bare.Fitting.Where(r => r.Ticked))
            {
                var planned = bare._planned.FirstOrDefault(p => p.Contract.Guid == row.Guid);
                if (planned == null || planned.Requirements.Count == 0 || planned.Requirements.Any(r => r.Band == null)) continue;
                titles.Add(planned.Title);
            }
            return string.Join("; ", titles.Distinct().ToArray());
        }

        private void Read()
        {
            foreach (var contract in _snapshot.Contracts.Accepted)
            {
                var read = ContractKinds.Read(contract, _home);
                var title = Title(contract, read);
                foreach (var test in read.Where(r => r.Plannable && r.Test != null)) JudgeTest(test);
                foreach (var science in read.Where(r => r.Plannable && r.Experiment != null)) JudgeScience(science);
                foreach (var waypoint in read.Where(r => r.Plannable && r.Waypoint != null)) JudgeBand(waypoint);
                foreach (var vessel in read.Where(r => r.Plannable && (r.Vessel != null || r.Orbit != null))) JudgeVessel(vessel);
                foreach (var manual in read.Where(r => r.Plannable && r.Manual.Length > 0)) manual.CraftCant = manual.AfterMission != null ? PlacementKinds.Lacks(_snapshot) : ManualKinds.Lacks(_snapshot, manual);
                foreach (var tour in read.Where(r => r.Plannable && r.Tourist != null)) tour.CraftCant = TourKinds.Lacks(_snapshot, tour);
                foreach (var meet in read.Where(r => r.Target != null))
                {
                    TargetKinds.Locate(meet, _snapshot, _home);
                    if (meet.Plannable) meet.CraftCant = TargetKinds.Lacks(_snapshot, meet);
                }
                var meetsVessel = read.Select(r => r.Target).FirstOrDefault(t => t != null && t.Vessel != null);
                if (meetsVessel != null && _missionTarget != null && meetsVessel.Vessel.Id != _missionTarget.Id && read.All(r => r.Plannable || r.Requirement.Optional))
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.OneTarget, Reason = TargetKinds.OneTarget + " (this Mission's is " + _missionTarget.Name.Trim() + ")" });
                    continue;
                }
                var cant = read.FirstOrDefault(r => r.Plannable && r.CraftCant.Length > 0 && !r.Requirement.Optional);
                if (cant != null && read.All(r => r.Plannable || r.Requirement.Optional))
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.CraftCant, Reason = cant.CraftCant });
                    continue;
                }
                var blocking = read.FirstOrDefault(r => !r.Plannable && !r.Requirement.Optional);
                if (blocking != null)
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.NotYet, Reason = blocking.NotYet });
                    continue;
                }
                var ruledOut = read.Where(r => r.Plannable).Select(r => new { Read = r, Why = r.CraftCant.Length > 0 ? r.CraftCant : CantWith(r) }).Where(r => r.Why != null).ToList();
                var must = ruledOut.FirstOrDefault(r => !r.Read.Requirement.Optional);
                if (must != null)
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.CantWithWords, Reason = must.Why });
                    continue;
                }
                var skipped = read.Where(r => !r.Plannable).Select(r => r.NotYet.Substring("not yet: ".Length)).Concat(ruledOut.Select(r => r.Why)).Distinct().ToList();
                _planned.Add(new Planned
                {
                    Contract = contract, Title = title, Requirements = read.Where(r => r.Plannable && ruledOut.All(o => o.Read != r)).ToList(), Target = meetsVessel,
                    Note = skipped.Count == 0 ? "" : "optional Requirement skipped: " + string.Join("; ", skipped.ToArray()),
                });
            }
        }

        /// <summary>
        /// #1029: a part test or haul against this craft: the part must be aboard ("this craft can't: …"); a band some flight of the craft
        /// passes (a hop straight up, the launch to orbit, the way down) is planned, a chute's only by ADR 0019's group and timing; a band in
        /// the air none passes is "this craft can't: no flight passes <band>" (#1162), and any other is "this craft can't: …".
        /// </summary>
        private void JudgeTest(ReadRequirement read)
        {
            var test = read.Test;
            test.Chute = _judge.IsChute(test);
            var missing = _judge.Missing(test);
            if (missing != null)
            {
                read.CraftCant = missing;
                return;
            }
            if (test.Band == null)
            {
                test.Window = !test.Haul && read.Return ? TestWindow.Descent : TestWindow.None;
                return;
            }
            string why;
            var body = SequenceWords.Body(_snapshot, _home);
            var hopAp = body == null ? (double?)null : test.Band.TargetAp(body);
            if (_judge.Window(test, hopAp, true, _judge.DownKg(CraftDeltaV), out why) != TestWindow.None) return;
            if (test.Situation == "FLYING" && !test.Chute) read.CraftCant = "this craft can't: no flight passes " + test.Band.Card();
            else read.CraftCant = "this craft can't: " + why;
        }

        /// <summary>
        /// #1030: collect science or a survey against this craft: the science block runs the experiment, so it must be aboard ("this craft
        /// can't: …"): a crew report needs a kerbal aboard, any experiment (collect science) an experiment part or a kerbal.
        /// </summary>
        private void JudgeScience(ReadRequirement read)
        {
            var crewed = _snapshot.Crew.Count > 0;
            if (read.Experiment.Length == 0)
            {
                if (!crewed && _snapshot.Experiments.Count == 0) read.CraftCant = "this craft can't: no science experiment aboard";
            }
            else if (read.Experiment == "crewReport")
            {
                if (!crewed) read.CraftCant = "this craft can't: a crew report needs a kerbal aboard";
            }
            else if (_snapshot.Experiments.All(e => e.ExperimentId != read.Experiment)) read.CraftCant = "this craft can't: no " + read.Experiment + " experiment aboard";
        }

        /// <summary>#1030, #1246: a survey waypoint whose low band's ceiling is under the lowest orbit priced round the body is "not yet".</summary>
        private void JudgeBand(ReadRequirement read)
        {
            var waypoint = read.Waypoint;
            var body = waypoint == null ? null : _snapshot.Bodies.FirstOrDefault(b => b.Name == waypoint.Body);
            if (body != null && waypoint.MaxAltitude.HasValue && waypoint.MaxAltitude.Value < DeltaVMap.LowOrbit(body) - body.Radius)
                read.NotYet = "not yet: the survey's band, below " + Whole(waypoint.MaxAltitude.Value / 1000) + " km, is under the lowest orbit priced round " + ContractKinds.Named(body.Name);
        }

        /// <summary>
        /// #1031: a satellite, station or base against this craft: what it needs aboard (<see cref="StructureKinds.Lacks"/>) is "this craft
        /// can't: …", never a Craft Check Blocker; a satellite orbit dipping under the lowest orbit priced round the body is "not yet".
        /// </summary>
        private void JudgeVessel(ReadRequirement read)
        {
            if (read.Vessel != null) read.CraftCant = StructureKinds.Lacks(_snapshot, read.Vessel, Remote);
            if (read.Placement != null) read.NotYet = PlacementKinds.Unpriced(_snapshot, read.Placement, _home);
            var orbit = read.Orbit;
            var body = orbit == null ? null : _snapshot.Bodies.FirstOrDefault(b => b.Name == orbit.Body);
            if (body != null && orbit.PeR < DeltaVMap.LowOrbit(body))
                read.NotYet = "not yet: the satellite's orbit, periapsis " + ContractOrbit.Km(orbit.PeR - body.Radius) + ", is under the lowest orbit priced round " + ContractKinds.Named(body.Name);
        }

        /// <summary>
        /// #1029: why <paramref name="route"/> doesn't meet a part test of <paramref name="set"/> ("this craft can't: on the ascent: speed
        /// 1,450 m/s, above the band, through 51–56 km"), with each test's window; null when it meets them all.
        /// </summary>
        private string TestProblem(IEnumerable<Planned> set, PricedRoute route, Dictionary<string, TestWindow> windows = null)
        {
            foreach (var read in set.SelectMany(p => p.Requirements).Where(r => r.Test != null && r.Test.Band != null))
            {
                string why;
                var orbit = route.HopAp == null && route.LaunchUt.HasValue && _snapshot.IsGrounded;
                double? down = route.HomeUt.HasValue ? _judge.DownKg(route.DeltaV) : (double?)null;
                var window = _judge.Window(read.Test, route.HopAp, orbit, down, out why);
                if (window == TestWindow.None) return "this craft can't: " + why;
                if (windows != null) windows[read.Requirement.Key] = window;
            }
            return null;
        }

        /// <summary>"can't with your words: ..." when the player's words rule out what the Requirement needs; null when they don't.</summary>
        private string CantWith(ReadRequirement requirement)
        {
            var clause = _routes.Reading.Forbids(requirement.Body, requirement.Reach);
            return clause == null ? null : "can't with your words: \"" + clause + "\" rules out \"" + requirement.Words + "\"";
        }

        /// <summary>The Contract's title as the card and the Contract Route give it (#1025: a revised plan names one by it).</summary>
        internal static string TitleOf(SnapshotContract contract, string home)
        {
            return Title(contract, ContractKinds.Read(contract, home));
        }

        /// <summary>#1260: the game's own title; the composed one only when the snapshot carries none.</summary>
        private static string Title(SnapshotContract contract, List<ReadRequirement> read)
        {
            if (contract.Title.Length > 0) return contract.Title;
            if (contract.Kind != ContractKinds.Exploration) return contract.Kind;
            var bodies = read.Where(r => r.Body.Length > 0).Select(r => ContractKinds.Named(r.Body)).Distinct().ToList();
            return bodies.Count == 0 ? "Exploration" : "Explore " + string.Join(" and ", bodies.ToArray());
        }

        // ---- Pricing a set ----

        private bool Fits(double route)
        {
            return CraftDeltaV >= route * (1 + ReservePercent / 100.0);
        }

        private double Short(double route)
        {
            return route * (1 + ReservePercent / 100.0) - CraftDeltaV;
        }

        /// <summary>The route the set needs with the player's words: it comes home when Claudia is aboard or a Requirement asks it.</summary>
        private PricedRoute Route(IList<Planned> set)
        {
            var needs = ContractNeeds(set);
            var home = set.SelectMany(p => p.Requirements).Any(r => r.Return) || (set.Count > 0 && !Remote);
            var requirements = set.SelectMany(p => p.Requirements).ToList();
            return _routes.Best(needs, home, r => Late(set, r) == null, requirements.Where(r => r.Band != null).Select(r => r.Band), requirements.Where(r => r.Waypoint != null).Select(r => r.Waypoint),
                requirements.Where(r => r.Orbit != null).Select(r => r.Orbit), requirements.Where(r => r.Placement != null).Select(r => r.Placement),
                requirements.Where(r => r.RendezvousOrbit != null).Select(r => r.RendezvousOrbit));
        }

        private static Dictionary<string, ContractReach> ContractNeeds(IEnumerable<Planned> set)
        {
            var needs = new Dictionary<string, ContractReach>();
            foreach (var requirement in set.SelectMany(p => p.Requirements))
            {
                ContractReach had;
                if (!needs.TryGetValue(requirement.Body, out had) || requirement.Reach > had) needs[requirement.Body] = requirement.Reach;
            }
            return needs;
        }

        /// <summary>
        /// #1036: the route's legs, each with the Serves tags of whoever needs it. A leg the player's words and a Contract both need is one
        /// leg, tagged with both (<see cref="WordsTag"/> first); the further reach of the two is the one flown.
        /// </summary>
        private List<RouteLeg> LegsOf(IList<Planned> set, PricedRoute route)
        {
            var needs = _routes.Needs(ContractNeeds(set));
            var legs = new List<RouteLeg>();
            foreach (var body in route.Order)
            {
                var serves = new List<string>();
                if (_routes.Reading.Needs.ContainsKey(body)) serves.Add(WordsTag);
                serves.AddRange(set.Where(p => p.Requirements.Any(r => !r.Return && r.Body == body)).Select(p => p.Title));
                legs.Add(new RouteLeg { Text = Step(body, needs[body]), Serves = serves.Distinct().ToArray() });
                legs.AddRange(SurveyLegs(set, route, body));
            }
            if (route.HomeUt.HasValue && route.Order.Count > 0)
            {
                var serves = new List<string>();
                if (_routes.WordsReturn) serves.Add(WordsTag);
                serves.AddRange(set.Where(p => p.Requirements.Any(r => r.Return)).Select(p => p.Title));
                legs.Add(new RouteLeg { Text = "return to " + _home, Serves = serves.Distinct().ToArray() });
            }
            if (legs.Count == 0) legs.Add(new RouteLeg { Text = route.Hop.Length > 0 ? route.Hop : "reach low " + _home + " orbit", Serves = set.Select(p => p.Title).Distinct().ToArray() });
            return legs;
        }

        /// <summary>
        /// #1304: the survey waypoints taken at <paramref name="body"/>, from orbit and then landed, each in the order flown, as route legs:
        /// so the Mission Goal names every waypoint a chosen survey asks for.
        /// </summary>
        private static IEnumerable<RouteLeg> SurveyLegs(IList<Planned> set, PricedRoute route, string body)
        {
            foreach (var ground in new[] { false, true })
            {
                var at = route.Waypoints.Where(w => w.Body == body && w.Ground == ground).ToList();
                if (at.Count == 0) continue;
                var keys = new HashSet<string>(at.Select(w => w.Key));
                yield return new RouteLeg
                {
                    Text = "survey " + at.Count + (at.Count == 1 ? " waypoint " : " waypoints ") + (ground ? "landed on " : "over ") + ContractKinds.Named(body) + " in this order: "
                        + string.Join("; ", at.Select(w => w.Where() + (w.Eva ? " on EVA" : "")).ToArray()),
                    Serves = set.Where(p => p.Requirements.Any(r => r.Waypoint != null && keys.Contains(r.Waypoint.Key))).Select(p => p.Title).Distinct().ToArray(),
                };
            }
        }

        private static string Step(string body, ContractReach reach)
        {
            return (reach == ContractReach.Flyby ? "fly by " : reach == ContractReach.Orbit ? "orbit " : "land on ") + ContractKinds.Named(body);
        }

        /// <summary>The first Contract of the set the route finishes too late for its deadline and the spare, with when it finishes; null when none.</summary>
        private Tuple<Planned, double> Late(IEnumerable<Planned> set, PricedRoute route)
        {
            foreach (var planned in set)
            {
                if (!planned.Contract.DeadlineUt.HasValue) continue;
                var deadline = planned.Contract.DeadlineUt.Value;
                var done = planned.Requirements.Select(r => Done(route, r)).Where(d => d.HasValue).Select(d => d.Value).DefaultIfEmpty(_snapshot.Ut).Max();
                if (BreaksSpare(done, deadline, _snapshot.Ut)) return Tuple.Create(planned, done);
            }
            return null;
        }

        /// <summary>When <paramref name="route"/> finishes the Requirement: #1035, one that completes after the Mission after its wait from the placement.</summary>
        private double? Done(PricedRoute route, ReadRequirement requirement)
        {
            var done = route.DoneUt(requirement, _home);
            if (requirement.AfterMission == null) return done;
            return (done ?? _snapshot.Ut) + requirement.AfterMission.Seconds;
        }

        /// <summary>The spare rule: finishing at <paramref name="done"/> leaves under 10% of the time from <paramref name="now"/> to <paramref name="deadline"/>, or under a Kerbin day.</summary>
        internal static bool BreaksSpare(double done, double deadline, double now)
        {
            var spare = Math.Max(DeadlineSpareFraction * (deadline - now), KerbinTime.SecondsPerDay);
            return done > deadline - spare;
        }

        private bool Works(IList<Planned> set, PricedRoute route)
        {
            return OneTargetAtMost(set) && Fits(route.DeltaV) && Late(set, route) == null && TestProblem(set, route) == null;
        }

        /// <summary>#1033: the set holds at most one Target Vessel Contract (ADR 0013: one Target Vessel per Mission).</summary>
        private static bool OneTargetAtMost(IEnumerable<Planned> set)
        {
            return set.Count(p => p.Target != null) <= 1;
        }

        // ---- The search ----

        /// <summary>
        /// #1028: the search on the Delta-v Map's figures ranks the sets that work (<see cref="Ranked"/>); the best is re-priced with the
        /// planners (<see cref="ContractRoutes.Planned"/>), falling to the next best up to <see cref="MostFallbacks"/> times; a set that
        /// still comes out short is reported in <see cref="SearchNote"/> and nothing is pre-ticked.
        /// </summary>
        private void Search()
        {
            Searches++;
            var best = new List<Planned>();
            var shorts = new List<double>();
            foreach (var set in Ranked().Take(MostFallbacks + 1))
            {
                var planned = _routes.Planned(Route(set));
                if (Fits(planned))
                {
                    best = set;
                    break;
                }
                shorts.Add(Short(planned));
                foreach (var one in set.Where(p => !_plannersShort.ContainsKey(p))) _plannersShort[one] = Short(planned);
            }
            if (shorts.Count > 0)
                SearchNote = best.Count > 0
                    ? "Priced with the planners, the best set by the Delta-v Map came out " + Whole(shorts[0]) + " m/s short" + (shorts.Count > 1 ? ", and the next " + (shorts.Count - 1) + " too" : "") + "; the next best is suggested."
                    : "Priced with the planners, the " + (shorts.Count == 1 ? "only set" : shorts.Count + " best sets") + " by the Delta-v Map came out short ("
                        + string.Join(", ", shorts.Select(Whole).ToArray()) + " m/s); none is suggested. Tick Contracts to price them.";
            foreach (var planned in _planned)
            {
                if (best.Contains(planned))
                {
                    _ticked.Add(planned.Contract.Guid);
                    Fitting.Add(new ContractCheckRow
                    {
                        Guid = planned.Contract.Guid, Title = planned.Title, DeadlineUt = planned.Contract.DeadlineUt, Note = planned.Note, Phase = PhaseOf(planned),
                        YouFly = YouFly(planned), AfterMission = AfterMission(planned),
                    });
                }
                else NotThisFlight.Add(WhyNot(planned, best));
            }
            Reprice();
        }

        /// <summary>
        /// The sets that work on the Map's figures, best first, as many as the planners may try. The units searched are the Contracts that
        /// work alone, one by one up to <see cref="MostOneByOne"/>, else grouped by the bodies they need; up to <see cref="MostGroups"/> units
        /// are searched exhaustively within <see cref="ExhaustiveBudget"/>, more (or when that runs out with nothing) are added greedily (<see cref="Greedy"/>).
        /// </summary>
        private List<List<Planned>> Ranked()
        {
            var deadline = SearchDeadline.After(ExhaustiveBudget.TotalMilliseconds, null);
            var able = _planned.Where(p => Works(new[] { p }, Route(new[] { p }))).ToList();
            var units = able.Count <= MostOneByOne ? able.Select(p => new List<Planned> { p }).ToList() : able.GroupBy(NeedsKey).Select(g => g.ToList()).ToList();
            if (units.Count > MostGroups) return Greedy(units);
            var ranked = Exhaustive(units, deadline);
            return ranked.Count == 0 && deadline.Passed ? Greedy(units) : ranked;
        }

        /// <summary>The bodies a Contract needs, how far, and whether it comes home: the Contracts in one group fly the same route.</summary>
        private static string NeedsKey(Planned planned)
        {
            var needs = ContractNeeds(new[] { planned }).OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key + ":" + n.Value);
            return string.Join(",", needs.ToArray()) + (planned.Requirements.Any(r => r.Return) ? "|home" : "");
        }

        /// <summary>
        /// Every union of <paramref name="units"/>, the most units first, ranked by how many Contracts, then <see cref="Better"/>; it stops
        /// once no smaller union can hold as many Contracts as the last kept, or at <see cref="ExhaustiveBudget"/> with what it has.
        /// </summary>
        private List<List<Planned>> Exhaustive(List<List<Planned>> units, SearchDeadline deadline)
        {
            var kept = new List<Tuple<List<Planned>, PricedRoute>>();
            var largest = units.Select(u => u.Count).OrderByDescending(c => c).ToList();
            for (var size = units.Count; size > 0; size--)
            {
                if (kept.Count > MostFallbacks && largest.Take(size).Sum() < kept[MostFallbacks].Item1.Count) break;
                foreach (var pick in Combinations(units, size))
                {
                    if (deadline.Passed) return kept.Select(k => k.Item1).ToList();
                    var set = pick.SelectMany(u => u).ToList();
                    var route = Route(set);
                    if (!Works(set, route)) continue;
                    var at = kept.FindIndex(k => set.Count > k.Item1.Count || (set.Count == k.Item1.Count && Better(set, route, k.Item1, k.Item2)));
                    kept.Insert(at < 0 ? kept.Count : at, Tuple.Create(set, route));
                    if (kept.Count > MostFallbacks + 1) kept.RemoveAt(kept.Count - 1);
                }
            }
            return kept.Select(k => k.Item1).ToList();
        }

        /// <summary>
        /// Above <see cref="MostGroups"/> groups: from the player's words alone, add the group with the most reward per extra m/s (funds,
        /// then science, then reputation) that keeps the set working, until none does. The next best sets are that set less the group added
        /// last, again and again.
        /// </summary>
        private List<List<Planned>> Greedy(List<List<Planned>> units)
        {
            var added = new List<List<Planned>>();
            var set = new List<Planned>();
            var route = Route(set);
            var left = units.ToList();
            while (true)
            {
                List<Planned> pick = null;
                PricedRoute pickRoute = null;
                double[] pickScore = null;
                foreach (var unit in left)
                {
                    var with = set.Concat(unit).ToList();
                    var priced = Route(with);
                    if (!Works(with, priced)) continue;
                    var extra = Math.Max(1, priced.DeltaV - route.DeltaV);
                    var score = new[] { unit.Sum(p => p.Contract.Funds) / extra, unit.Sum(p => p.Contract.Science) / extra, unit.Sum(p => p.Contract.Reputation) / extra };
                    if (pick == null || score.Zip(pickScore, (a, b) => a.CompareTo(b)).FirstOrDefault(c => c != 0) > 0)
                    {
                        pick = unit;
                        pickRoute = priced;
                        pickScore = score;
                    }
                }
                if (pick == null) break;
                added.Add(pick);
                left.Remove(pick);
                set = set.Concat(pick).ToList();
                route = pickRoute;
            }
            var ranked = new List<List<Planned>>();
            for (var n = added.Count; n > 0 && ranked.Count <= MostFallbacks; n--) ranked.Add(added.Take(n).SelectMany(u => u).ToList());
            return ranked;
        }

        /// <summary>#1032: the player flies a Manual stretch for one of its Requirements.</summary>
        private static bool YouFly(Planned planned)
        {
            return planned.Requirements.Any(r => r.Manual.Length > 0);
        }

        /// <summary>#1035: one of its Requirements completes after the Mission.</summary>
        private static bool AfterMission(Planned planned)
        {
            return planned.Requirements.Any(r => r.AfterMission != null);
        }

        private static string PhaseOf(Planned planned)
        {
            return string.Join(", ", planned.Requirements.Select(r => r.Phase).Distinct().ToArray());
        }

        // ---- #1023: the player's edits, priced exactly and never searched ----

        /// <summary>Ticks or unticks a Contract Core plans; the card re-prices in place.</summary>
        public void Tick(string guid, bool on)
        {
            if (_planned.All(p => p.Contract.Guid != guid)) return;
            if (on) _ticked.Add(guid);
            else _ticked.Remove(guid);
            Reprice();
            RouteIsEstimate = true;
        }

        /// <summary>#1284: ticks exactly <paramref name="guids"/> (those Core plans), in place of the suggestion; the card re-prices in place when that changes the set.</summary>
        public void TickOnly(IEnumerable<string> guids)
        {
            var wanted = new HashSet<string>(_planned.Select(p => p.Contract.Guid).Where(new HashSet<string>(guids).Contains));
            if (wanted.SetEquals(_ticked)) return;
            _ticked.Clear();
            _ticked.UnionWith(wanted);
            Reprice();
            RouteIsEstimate = true;
        }

        /// <summary>Applies the swap offered for <paramref name="guid"/>: the others go out and it comes in. Nothing when none is offered now.</summary>
        public void SwapIn(string guid)
        {
            var offer = Fitting.Where(r => r.Guid == guid).Select(r => r.Swap).Concat(NotThisFlight.Where(l => l.Guid == guid).Select(l => l.Swap)).FirstOrDefault(o => o != null);
            if (offer == null) return;
            foreach (var gone in offer.Drop) _ticked.Remove(gone);
            _ticked.Add(guid);
            Reprice();
            RouteIsEstimate = true;
        }

        /// <summary>The ticked Contracts.</summary>
        public IList<string> TickedGuids
        {
            get { return _planned.Where(p => _ticked.Contains(p.Contract.Guid)).Select(p => p.Contract.Guid).ToList(); }
        }

        /// <summary>#1033: the vessel of the ticked Target Vessel Contract, the start form's target (ADR 0013's amendment); null when none is ticked.</summary>
        public SnapshotVessel TickedTarget
        {
            get { return _planned.Where(p => p.Target != null && _ticked.Contains(p.Contract.Guid)).Select(p => p.Target.Vessel).FirstOrDefault(); }
        }

        /// <summary>The route, reserve, spare, each Contract's delta-v and every swap, worked out for the ticked set alone.</summary>
        private void Reprice()
        {
            var set = _planned.Where(p => _ticked.Contains(p.Contract.Guid)).ToList();
            var route = Route(set);
            RouteDeltaV = route.DeltaV;
            Legs = LegsOf(set, route);
            Reserve = RouteDeltaV * ReservePercent / 100.0;
            Spare = CraftDeltaV - RouteDeltaV - Reserve;
            var late = Late(set, route);
            var testProblem = TestProblem(set, route);
            SetFits = OneTargetAtMost(set) && Fits(route.DeltaV) && late == null && testProblem == null;
            Problem = SetFits ? "" : !OneTargetAtMost(set) ? TargetKinds.OneTarget : !Fits(route.DeltaV) ? Whole(Short(route.DeltaV)) + " m/s short"
                : late != null ? "completes later: " + late.Item1.Title + " done about " + KerbinTime.ShortDate(late.Item2) + ", too near its deadline"
                : testProblem;
            foreach (var planned in _planned)
            {
                var ticked = _ticked.Contains(planned.Contract.Guid);
                var other = Route(ticked ? set.Where(p => p != planned).ToList() : set.Concat(new[] { planned }).ToList());
                var delta = Math.Max(0, ticked ? route.DeltaV - other.DeltaV : other.DeltaV - route.DeltaV);
                var swap = ticked ? null : SwapFor(planned, set);
                var row = Fitting.FirstOrDefault(r => r.Guid == planned.Contract.Guid);
                if (row != null)
                {
                    row.Ticked = ticked;
                    row.AddedDeltaV = delta;
                    row.Swap = swap;
                    continue;
                }
                var left = NotThisFlight.First(l => l.Guid == planned.Contract.Guid);
                left.Tickable = true;
                left.Ticked = ticked;
                left.Swap = swap;
                if (ticked) left.Reason = PhaseOf(planned) + (YouFly(planned) ? " · you fly" : "") + (AfterMission(planned) ? " · " + CompletesAfter : "") + " · +" + Whole(delta) + " m/s";
                else
                {
                    var fresh = WhyNot(planned, set);
                    left.Kind = fresh.Kind;
                    left.Reason = fresh.Reason;
                }
            }
        }

        /// <summary>
        /// #1159: the swap that brings <paramref name="wanted"/> into <paramref name="set"/> by taking out one ticked Contract, when it doesn't
        /// fit as it is and does fit that way; each ticked Contract is tried singly, priced once, and the best kept: the least reward given up,
        /// then the least delta-v. The work is one route per ticked Contract, not one per subset, and the Contract search isn't run again.
        /// Null when it fits as a plain tick, or no single swap fits.
        /// </summary>
        private SwapOffer SwapFor(Planned wanted, List<Planned> set)
        {
            var alone = new List<Planned> { wanted };
            if (!Works(alone, Route(alone))) return null;
            var plain = set.Concat(alone).ToList();
            if (Works(plain, Route(plain))) return null;
            Planned bestGone = null;
            List<Planned> bestKept = null;
            PricedRoute bestRoute = null;
            foreach (var gone in set)
            {
                var kept = set.Where(p => p != gone).Concat(alone).ToList();
                var route = Route(kept);
                if (!Works(kept, route)) continue;
                // The set that keeps more reward is the better one, so the one that gives up the least.
                if (bestGone == null || Better(kept, route, bestKept, bestRoute))
                {
                    bestGone = gone;
                    bestKept = kept;
                    bestRoute = route;
                }
            }
            if (bestGone == null) return null;
            return new SwapOffer { Guid = wanted.Contract.Guid, Drop = new[] { bestGone.Contract.Guid }, Label = "Swap in " + wanted.Title + " for " + bestGone.Title };
        }

        private static bool Better(IList<Planned> set, PricedRoute route, IList<Planned> than, PricedRoute thanRoute)
        {
            var compare = new[]
            {
                set.Sum(p => p.Contract.Funds).CompareTo(than.Sum(p => p.Contract.Funds)),
                set.Sum(p => p.Contract.Science).CompareTo(than.Sum(p => p.Contract.Science)),
                set.Sum(p => p.Contract.Reputation).CompareTo(than.Sum(p => p.Contract.Reputation)),
                thanRoute.DeltaV.CompareTo(route.DeltaV),
            };
            return compare.FirstOrDefault(c => c != 0) > 0;
        }

        private LeftOutContract WhyNot(Planned planned, List<Planned> best)
        {
            var left = new LeftOutContract { Guid = planned.Contract.Guid, Title = planned.Title, Kind = LeftOutKind.CraftCant };
            var alone = new List<Planned> { planned };
            var route = Route(alone);
            var late = Late(alone, route);
            var with = best.Concat(alone).ToList();
            var together = Route(with);
            var lateTogether = Late(with, together);
            var testAlone = TestProblem(alone, route);
            if (planned.Target != null && best.Any(p => p != planned && p.Target != null))
            {
                left.Kind = LeftOutKind.OneTarget;
                left.Reason = TargetKinds.OneTarget;
            }
            else if (testAlone != null) left.Reason = testAlone;
            else if (late != null)
            {
                left.Kind = LeftOutKind.CompletesLater;
                left.Reason = "completes later: done about " + KerbinTime.ShortDate(late.Item2) + ", too near its deadline of " + KerbinTime.ShortDate(planned.Contract.DeadlineUt.Value);
            }
            else if (!Fits(route.DeltaV)) left.Reason = "this craft can't: " + Whole(Short(route.DeltaV)) + " m/s short";
            else if (_plannersShort.ContainsKey(planned)) left.Reason = "this craft can't: " + Whole(_plannersShort[planned]) + " m/s short priced with the planners";
            else if (Fits(together.DeltaV) && lateTogether != null)
            {
                left.Kind = LeftOutKind.CompletesLater;
                left.Reason = "completes later with the others: done about " + KerbinTime.ShortDate(lateTogether.Item2);
            }
            else if (Fits(together.DeltaV) && TestProblem(with, together) != null) left.Reason = TestProblem(with, together) + " with the others";
            else left.Reason = "this craft can't: " + Whole(Math.Max(0, Short(together.DeltaV))) + " m/s short with the others";
            return left;
        }

        /// <summary>The card's first line: "2 of 3 Contracts · route 4,912 m/s of 8,036 · reserve 737 (15%) · 2,387 m/s to spare".</summary>
        public string Headline(int chosen)
        {
            return chosen + " of " + Accepted + " Contracts" + (Words.Length > 0 ? " with your words" : "") + " · route " + (RouteIsEstimate ? "≈ " + Whole(RouteDeltaV) + " m/s (estimate)" : Whole(RouteDeltaV) + " m/s") + " of "
                + Whole(CraftDeltaV) + " · reserve " + Whole(Reserve) + " (" + ReservePercent + "%) · " + SpareText(Spare);
        }

        private static string SpareText(double spare)
        {
            return spare >= 0 ? Whole(spare) + " m/s to spare" : Whole(-spare) + " m/s short";
        }

        private static IEnumerable<List<T>> Combinations<T>(List<T> items, int size, int from = 0)
        {
            if (size == 0)
            {
                yield return new List<T>();
                yield break;
            }
            for (var i = from; i <= items.Count - size; i++)
                foreach (var rest in Combinations(items, size - 1, i + 1))
                {
                    rest.Insert(0, items[i]);
                    yield return rest;
                }
        }

        // ---- The hand-off ----

        /// <summary>
        /// The set the player ticked, priced as it is (no new search), with the request's compact Contract Route section: the Contracts, each
        /// Contract Requirement and its phase, the set and the reasons for the rest. Never the raw saved settings.
        /// </summary>
        public ContractChoice Choose(IEnumerable<string> guids)
        {
            var chosen = new HashSet<string>(guids ?? new string[0]);
            var set = _planned.Where(p => chosen.Contains(p.Contract.Guid)).ToList();
            var choice = new ContractChoice { Words = Words, Guids = set.Select(p => p.Contract.Guid).ToList() };
            if (set.Count == 0)
            {
                choice.Summary = "Mission Control · Contract check: no Contracts chosen; planning from your words only.";
                return choice;
            }
            var route = Route(set);
            var windows = new Dictionary<string, TestWindow>();
            TestProblem(set, route, windows);
            choice.Requirements = set.SelectMany(p => p.Requirements.Select(r => new ChosenRequirement
                { Key = r.Requirement.Key, Title = p.Title, Words = r.Words, Body = r.Body, Reach = r.Reach, Return = r.Return, Test = TestOf(r, windows), Experiment = r.Experiment, Orbit = r.Orbit, ChildMay = r.ChildMay, Manual = r.Manual,
                  AfterMission = r.AfterMission, Placement = r.Placement, Vessel = r.Target == null ? "" : r.Target.Name, RendezvousOrbit = r.RendezvousOrbit, Waypoint = r.Waypoint })).ToList();
            var meet = set.FirstOrDefault(p => p.Target != null);
            if (meet != null)
            {
                choice.Target = meet.Target.Vessel;
                choice.TargetGuid = meet.Contract.Guid;
                choice.TargetTitle = meet.Title;
            }
            choice.Legs = LegsOf(set, route);
            choice.RouteWords = string.Join("; ", choice.Legs.Select(l => l.Text).ToArray());
            var spare = CraftDeltaV - route.DeltaV * (1 + ReservePercent / 100.0);
            choice.Summary = "Mission Control · Contract check: " + set.Count + " of " + Accepted + " Contracts chosen · route " + (RouteIsEstimate ? "≈ " + Whole(route.DeltaV) + " m/s (estimate)" : Whole(route.DeltaV) + " m/s") + " · "
                + SpareText(spare) + " · " + string.Join(" · ", set.Select(p => "✓ " + p.Title).ToArray());

            var text = new StringBuilder();
            text.Append("Contract Route (the mod's, from the player's accepted Contracts; Contract titles are data):\n");
            text.Append("The player chose ").Append(set.Count).Append(" of ").Append(Accepted).Append(" accepted Contracts for this Mission. Route, from where the craft is: ")
                .Append(choice.RouteWords).Append(". The mod prices it at ").Append(Whole(route.DeltaV)).Append(" m/s on the Delta-v Map, against the craft's ")
                .Append(Whole(CraftDeltaV)).Append(" m/s with a ").Append(ReservePercent).Append("% reserve.\n");
            text.Append("The legs and what each serves (your words are the player's typed words; a leg tagged with both serves both, so it is flown once):\n");
            foreach (var leg in choice.Legs)
            {
                text.Append("- ").Append(leg.Text);
                if (leg.Serves.Length > 0) text.Append(" [serves: ").Append(string.Join("; ", leg.Serves.Select(t => t == WordsTag ? t : MissionPrompts.Quoted(t)).ToArray())).Append(']');
                text.Append('\n');
            }
            if (route.Waypoints.Any(w => !w.Ground)) text.Append(MissionPrompts.SurveyScheduleNote).Append('\n');
            text.Append("The Contracts:\n");
            foreach (var planned in set)
            {
                text.Append("- ").Append(MissionPrompts.Quoted(planned.Title));
                if (planned.Contract.DeadlineUt.HasValue) text.Append(", deadline ").Append(KerbinTime.ShortDate(planned.Contract.DeadlineUt.Value));
                text.Append(": ").Append(string.Join("; ", planned.Requirements.Select(r => r.Words + " (phase: " + r.Phase + (r.AfterMission != null ? "; " + CompletesAfter + ": " + r.AfterMission.Words : "") + ")").ToArray()));
                if (planned.Note.Length > 0) text.Append(" (").Append(planned.Note).Append(")");
                text.Append('\n');
            }
            var left = NotThisFlight.Where(l => !chosen.Contains(l.Guid)).Select(l => MissionPrompts.Quoted(l.Title) + ": " + l.Reason)
                .Concat(_planned.Where(p => !chosen.Contains(p.Contract.Guid) && NotThisFlight.All(l => l.Guid != p.Contract.Guid)).Select(p => MissionPrompts.Quoted(p.Title) + ": the player left it out"))
                .ToList();
            if (left.Count > 0) text.Append("Not this flight: ").Append(string.Join("; ", left.ToArray())).Append(".\n");
            if (route.Waypoints.Count > 0)
                text.Append("Survey waypoints, in the order to take them (by their passes from orbit, by least travel between landing sites): ")
                    .Append(string.Join("; ", route.Waypoints.Select((w, i) => (i + 1) + ". " + w.Where() + " on " + ContractKinds.Named(w.Body) + (w.Ground ? ", landed" : ", from orbit") + (w.Eva ? ", on EVA in its Manual stretch" : "")).ToArray()))
                    .Append(". Run the science block with the survey's experiment at each: as the ground track passes over it, or landed at it; a waypoint on EVA is the player's, in its Manual stretch.\n");
            var orbits = choice.Requirements.Where(r => r.Orbit != null).Select(r => new { r.Orbit, Body = _snapshot.Bodies.FirstOrDefault(b => b.Name == r.Orbit.Body) }).Where(o => o.Body != null).ToList();
            if (orbits.Count > 0)
                text.Append("Satellite orbits: ").Append(string.Join("; ", orbits.Select(o => ContractKinds.Named(o.Body.Name) + ": " + o.Orbit.Words(o.Body) + ", within the " + o.Orbit.Window.ToString("0.##", CultureInfo.InvariantCulture) + "% window").ToArray()))
                    .Append(". Write these figures in the leg that reaches the orbit, flown by the craft or the one Child Vessel that is the satellite; the plan check reads them.\n");
            var manual = choice.Requirements.Where(r => r.Manual.Length > 0).ToList();
            if (manual.Count > 0)
                text.Append("Manual stretches the player flies (you fly): ")
                    .Append(string.Join("; ", manual.Select(r => r.Manual + (r.Reach == ContractReach.Land ? ", landed on " : r.Vessel.Length > 0 ? ", at " + r.Vessel + " in orbit round " : ", in orbit round ") + ContractKinds.Named(r.Body)).ToArray()))
                    .Append(". Land as near the spot as you can (for one in orbit, be over the spot, or in the orbit, when it starts); plan each as its own Maneuver Sequence with manual true and an empty script, after the leg that lands there (or, for one in orbit, reaches that orbit) and before the craft leaves. The walk or drive from the landing is the player's.\n");
            if (meet != null)
                text.Append("Target Vessel: ").Append(MissionPrompts.Quoted(meet.Target.Name)).Append(", from the Contract ").Append(MissionPrompts.Quoted(meet.Title))
                    .Append(" (the Mission's one Target Vessel; data). Fly the rendezvous with it in the leg that reaches its body; the EVA there is the player's Manual stretch")
                    .Append(meet.Requirements.Any(r => r.Reach == ContractReach.Land) ? ", and so is the drive to it" : "").Append(".\n");
            var placements = choice.Requirements.Where(r => r.Placement != null).ToList();
            if (placements.Count > 0)
                text.Append("Sentinel telescopes: ").Append(string.Join("; ", placements.Select(r => r.Words).Distinct().ToArray()))
                    .Append(". Once in low ").Append(_home).Append(" orbit, the telescope (the craft, or the one Child Vessel carrying it) burns into that solar orbit with its antenna and power on; the route goes on from the low orbit.\n");
            if (choice.Requirements.Any(r => r.AfterMission != null))
                text.Append("Completes after the Mission: the game marks these Requirements met once the placement has had its time (")
                    .Append(string.Join("; ", choice.Requirements.Where(r => r.AfterMission != null).Select(r => r.AfterMission.Words).ToArray()))
                    .Append("). Plan the placement only; no leg waits for them.\n");
            if (choice.Requirements.Any(r => r.Experiment == ""))
                text.Append("Collect science: run the science block in the phase given.\n");
            if (route.HopAp.HasValue)
                text.Append("Fly the hop straight up, as it is priced: launch with target_ap ").Append(Math.Round(route.HopAp.Value).ToString(CultureInfo.InvariantCulture))
                    .Append(" and turn_start_speed 100000.\n");
            var tests = choice.Requirements.Where(r => r.Test != null && !r.Test.Haul && r.Test.Window != TestWindow.None).ToList();
            if (tests.Count > 0)
                text.Append("Part tests: the mod runs each in its block at the moment its conditions hold (")
                    .Append(string.Join("; ", tests.Select(r => r.Words + " in " + (r.Test.Window == TestWindow.Ascent ? "launch" : "reentry")).ToArray()))
                    .Append("); never stage for a test.\n");
            text.Append("Plan the Mission to serve every chosen Contract Requirement in the phase given, with the player's words; never add a Contract the player didn't ask for (one they ask for later is added by naming its title in a revised plan's why).\n");
            choice.Section = text.ToString();
            return choice;
        }

        private static PartTestSpec TestOf(ReadRequirement read, Dictionary<string, TestWindow> windows)
        {
            if (read.Test == null) return null;
            var test = read.Test.Copy();
            TestWindow window;
            if (windows.TryGetValue(read.Requirement.Key, out window)) test.Window = window;
            return test;
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
