// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClaudiaKerman.Core.Missions;
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

        /// <summary>The line under its box: "Mun · +1,170 m/s · deadline Y1 D30 (in 12d)", or "no deadline".</summary>
        public string Detail(double now)
        {
            return Phase + " · +" + Math.Round(AddedDeltaV).ToString("#,0", CultureInfo.InvariantCulture) + " m/s · "
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
        /// <summary>Past the most the search takes.</summary>
        NotSearched,
        /// <summary>#1036: the player's typed words rule out what it needs; the words win.</summary>
        CantWithWords,
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
        /// <summary>The player can tick it: Core plans it (it isn't "not yet" or "not searched"), #1023. Ticked, its reason gives way to its detail for the set.</summary>
        public bool Tickable;
        public bool Ticked;
        /// <summary>Where it fits in place of others, the swap that does it in one click; null when none.</summary>
        public SwapOffer Swap;
    }

    /// <summary>#1023: "Swap in X for Y": the ticked Contracts that go out so the one comes in and the set still fits.</summary>
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
        /// <summary>Above this many plannable Contracts the rest are left out of the search (spec #1021's grouping is a later ticket).</summary>
        public const int MostSearched = 10;
        /// <summary>The spare a deadline needs: this fraction of the time to it, and at least a Kerbin day.</summary>
        public const double DeadlineSpareFraction = 0.10;
        /// <summary>The Serves tag for what the player typed.</summary>
        public const string WordsTag = "your words";

        public string Words = "";
        public bool Remote;
        /// <summary>How many Contracts are accepted.</summary>
        public int Accepted;
        /// <summary>The craft's delta-v is known (the stock simulator is on and reads true), so there is anything to price.</summary>
        public bool Priceable;
        /// <summary>The typed words alone fit the craft with the reserve; when not, no Contract is suggested and the Craft Check says why.</summary>
        public bool WordsFit;
        public double CraftDeltaV;
        /// <summary>The suggested set's route with the player's words, m/s.</summary>
        public double RouteDeltaV;
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
        public List<ContractCheckRow> Fitting = new List<ContractCheckRow>();
        public List<LeftOutContract> NotThisFlight = new List<LeftOutContract>();
        /// <summary>#1036: the ticked set's route leg by leg, with what each serves.</summary>
        public List<RouteLeg> Legs = new List<RouteLeg>();

        private GameSnapshot _snapshot;
        private ContractRoutes _routes;
        private string _home = CraftCheckFlow.HomeBody;
        private readonly List<Planned> _planned = new List<Planned>();
        private readonly HashSet<string> _ticked = new HashSet<string>();

        /// <summary>A plannable Contract and what it needs.</summary>
        private sealed class Planned
        {
            public SnapshotContract Contract;
            public string Title = "";
            public List<ReadRequirement> Requirements = new List<ReadRequirement>();
            public string Note = "";
        }

        /// <summary>The check of <paramref name="snapshot"/>'s accepted Contracts with the player's <paramref name="words"/>.</summary>
        public static ContractCheck Work(GameSnapshot snapshot, string words, bool remote, int marginPercent)
        {
            var check = new ContractCheck
            {
                Words = (words ?? "").Trim(), Remote = remote, ReservePercent = marginPercent, _snapshot = snapshot,
                Accepted = snapshot.Contracts == null ? 0 : snapshot.Contracts.Accepted.Count,
            };
            check.Priceable = snapshot.DeltaVSimulator && snapshot.Stages.Count > 0 && !snapshot.DeltaVUnreliable;
            if (check.Accepted == 0 || !check.Priceable) return check;
            check.CraftDeltaV = snapshot.Stages.Sum(s => s.DeltaVVac);
            check._routes = new ContractRoutes(snapshot, check.Words, check._home);
            check.Read();
            check.WordsFit = check.Fits(check._routes.WordsAlone());
            if (check.WordsFit) check.Search();
            return check;
        }

        private void Read()
        {
            foreach (var contract in _snapshot.Contracts.Accepted)
            {
                var read = ContractKinds.Read(contract, _home);
                var title = Title(contract, read);
                var blocking = read.FirstOrDefault(r => !r.Plannable && !r.Requirement.Optional);
                if (blocking != null)
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.NotYet, Reason = blocking.NotYet });
                    continue;
                }
                var ruledOut = read.Where(r => r.Plannable).Select(r => new { Read = r, Why = CantWith(r) }).Where(r => r.Why != null).ToList();
                var must = ruledOut.FirstOrDefault(r => !r.Read.Requirement.Optional);
                if (must != null)
                {
                    NotThisFlight.Add(new LeftOutContract { Guid = contract.Guid, Title = title, Kind = LeftOutKind.CantWithWords, Reason = must.Why });
                    continue;
                }
                var skipped = read.Where(r => !r.Plannable).Select(r => r.NotYet.Substring("not yet: ".Length)).Concat(ruledOut.Select(r => r.Why)).Distinct().ToList();
                _planned.Add(new Planned
                {
                    Contract = contract, Title = title, Requirements = read.Where(r => r.Plannable && ruledOut.All(o => o.Read != r)).ToList(),
                    Note = skipped.Count == 0 ? "" : "optional Requirement skipped: " + string.Join("; ", skipped.ToArray()),
                });
            }
            var searched = _planned.OrderByDescending(p => p.Contract.Funds).ThenByDescending(p => p.Contract.Science).ThenByDescending(p => p.Contract.Reputation).Take(MostSearched).ToList();
            foreach (var over in _planned.Where(p => !searched.Contains(p)).ToList())
            {
                NotThisFlight.Add(new LeftOutContract { Guid = over.Contract.Guid, Title = over.Title, Kind = LeftOutKind.NotSearched, Reason = "not searched: only the " + MostSearched + " best paid Contracts are searched" });
                _planned.Remove(over);
            }
        }

        /// <summary>"can't with your words: ..." when the player's words rule out what the Requirement needs; null when they don't.</summary>
        private string CantWith(ReadRequirement requirement)
        {
            var clause = _routes.Reading.Forbids(requirement.Body, requirement.Reach);
            return clause == null ? null : "can't with your words: \"" + clause + "\" rules out \"" + requirement.Words + "\"";
        }

        private static string Title(SnapshotContract contract, List<ReadRequirement> read)
        {
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
            return _routes.Best(needs, home, r => Late(set, r) == null);
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
            }
            if (route.HomeUt.HasValue && route.Order.Count > 0)
            {
                var serves = new List<string>();
                if (_routes.WordsReturn) serves.Add(WordsTag);
                serves.AddRange(set.Where(p => p.Requirements.Any(r => r.Return)).Select(p => p.Title));
                legs.Add(new RouteLeg { Text = "return to " + _home, Serves = serves.Distinct().ToArray() });
            }
            if (legs.Count == 0) legs.Add(new RouteLeg { Text = "reach low " + _home + " orbit", Serves = set.Select(p => p.Title).Distinct().ToArray() });
            return legs;
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
                var done = planned.Requirements.Select(r => route.DoneUt(r, _home)).Where(d => d.HasValue).Select(d => d.Value).DefaultIfEmpty(_snapshot.Ut).Max();
                var spare = Math.Max(DeadlineSpareFraction * (deadline - _snapshot.Ut), KerbinTime.SecondsPerDay);
                if (done > deadline - spare) return Tuple.Create(planned, done);
            }
            return null;
        }

        private bool Works(IList<Planned> set, PricedRoute route)
        {
            return Fits(route.DeltaV) && Late(set, route) == null;
        }

        // ---- The search ----

        private void Search()
        {
            Searches++;
            List<Planned> best = new List<Planned>();
            PricedRoute bestRoute = Route(best);
            for (var size = _planned.Count; size > 0; size--)
            {
                List<Planned> found = null;
                PricedRoute foundRoute = null;
                foreach (var set in Combinations(_planned, size))
                {
                    var route = Route(set);
                    if (!Works(set, route)) continue;
                    if (found == null || Better(set, route, found, foundRoute))
                    {
                        found = set;
                        foundRoute = route;
                    }
                }
                if (found == null) continue;
                best = found;
                bestRoute = foundRoute;
                break;
            }
            foreach (var planned in _planned)
            {
                if (best.Contains(planned))
                {
                    _ticked.Add(planned.Contract.Guid);
                    Fitting.Add(new ContractCheckRow
                    {
                        Guid = planned.Contract.Guid, Title = planned.Title, DeadlineUt = planned.Contract.DeadlineUt, Note = planned.Note, Phase = PhaseOf(planned),
                    });
                }
                else NotThisFlight.Add(WhyNot(planned, best));
            }
            Reprice();
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
        }

        /// <summary>Applies the swap offered for <paramref name="guid"/>: the others go out and it comes in. Nothing when none is offered now.</summary>
        public void SwapIn(string guid)
        {
            var offer = Fitting.Where(r => r.Guid == guid).Select(r => r.Swap).Concat(NotThisFlight.Where(l => l.Guid == guid).Select(l => l.Swap)).FirstOrDefault(o => o != null);
            if (offer == null) return;
            foreach (var gone in offer.Drop) _ticked.Remove(gone);
            _ticked.Add(guid);
            Reprice();
        }

        /// <summary>The ticked Contracts.</summary>
        public IList<string> TickedGuids
        {
            get { return _planned.Where(p => _ticked.Contains(p.Contract.Guid)).Select(p => p.Contract.Guid).ToList(); }
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
            SetFits = Fits(route.DeltaV) && late == null;
            Problem = SetFits ? "" : !Fits(route.DeltaV) ? Whole(Short(route.DeltaV)) + " m/s short"
                : "completes later: " + late.Item1.Title + " done about " + KerbinTime.ShortDate(late.Item2) + ", too near its deadline";
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
                if (ticked) left.Reason = PhaseOf(planned) + " · +" + Whole(delta) + " m/s";
                else
                {
                    var fresh = WhyNot(planned, set);
                    left.Kind = fresh.Kind;
                    left.Reason = fresh.Reason;
                }
            }
        }

        /// <summary>
        /// The swap that brings <paramref name="wanted"/> into <paramref name="set"/> by taking out the fewest ticked Contracts, when it doesn't
        /// fit as it is and does fit that way; among the fewest, the least reward given up, then the least delta-v. Only the ticked set's
        /// subsets are tried: the Contract search isn't run again. Null when it fits as a plain tick, or can't fit at all.
        /// </summary>
        private SwapOffer SwapFor(Planned wanted, List<Planned> set)
        {
            var alone = new List<Planned> { wanted };
            if (!Works(alone, Route(alone))) return null;
            var plain = set.Concat(alone).ToList();
            if (Works(plain, Route(plain))) return null;
            for (var size = 1; size <= set.Count; size++)
            {
                List<Planned> bestGone = null, bestKept = null;
                PricedRoute bestRoute = null;
                foreach (var gone in Combinations(set, size))
                {
                    var kept = set.Where(p => !gone.Contains(p)).Concat(alone).ToList();
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
                if (bestGone == null) continue;
                return new SwapOffer
                {
                    Guid = wanted.Contract.Guid, Drop = bestGone.Select(p => p.Contract.Guid).ToArray(),
                    Label = "Swap in " + wanted.Title + " for " + string.Join(" and ", bestGone.Select(p => p.Title).ToArray()),
                };
            }
            return null;
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
            if (late != null)
            {
                left.Kind = LeftOutKind.CompletesLater;
                left.Reason = "completes later: done about " + KerbinTime.ShortDate(late.Item2) + ", too near its deadline of " + KerbinTime.ShortDate(planned.Contract.DeadlineUt.Value);
            }
            else if (!Fits(route.DeltaV)) left.Reason = "this craft can't: " + Whole(Short(route.DeltaV)) + " m/s short";
            else if (Fits(together.DeltaV) && lateTogether != null)
            {
                left.Kind = LeftOutKind.CompletesLater;
                left.Reason = "completes later with the others: done about " + KerbinTime.ShortDate(lateTogether.Item2);
            }
            else left.Reason = "this craft can't: " + Whole(Math.Max(0, Short(together.DeltaV))) + " m/s short with the others";
            return left;
        }

        /// <summary>The card's first line: "2 of 3 Contracts · route 4,912 m/s of 8,036 · reserve 737 (15%) · 2,387 m/s to spare".</summary>
        public string Headline(int chosen)
        {
            return chosen + " of " + Accepted + " Contracts" + (Words.Length > 0 ? " with your words" : "") + " · route " + Whole(RouteDeltaV) + " m/s of "
                + Whole(CraftDeltaV) + " · reserve " + Whole(Reserve) + " (" + ReservePercent + "%) · " + SpareText(Spare);
        }

        private static string SpareText(double spare)
        {
            return spare >= 0 ? Whole(spare) + " m/s to spare" : Whole(-spare) + " m/s short";
        }

        private static IEnumerable<List<Planned>> Combinations(List<Planned> items, int size, int from = 0)
        {
            if (size == 0)
            {
                yield return new List<Planned>();
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
            choice.Legs = LegsOf(set, route);
            choice.RouteWords = string.Join("; ", choice.Legs.Select(l => l.Text).ToArray());
            var spare = CraftDeltaV - route.DeltaV * (1 + ReservePercent / 100.0);
            choice.Summary = "Mission Control · Contract check: " + set.Count + " of " + Accepted + " Contracts chosen · route " + Whole(route.DeltaV) + " m/s · "
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
            text.Append("The Contracts:\n");
            foreach (var planned in set)
            {
                text.Append("- ").Append(MissionPrompts.Quoted(planned.Title));
                if (planned.Contract.DeadlineUt.HasValue) text.Append(", deadline ").Append(KerbinTime.ShortDate(planned.Contract.DeadlineUt.Value));
                text.Append(": ").Append(string.Join("; ", planned.Requirements.Select(r => r.Words + " (phase: " + r.Phase + ")").ToArray()));
                if (planned.Note.Length > 0) text.Append(" (").Append(planned.Note).Append(")");
                text.Append('\n');
            }
            var left = NotThisFlight.Where(l => !chosen.Contains(l.Guid)).Select(l => MissionPrompts.Quoted(l.Title) + ": " + l.Reason)
                .Concat(_planned.Where(p => !chosen.Contains(p.Contract.Guid) && NotThisFlight.All(l => l.Guid != p.Contract.Guid)).Select(p => MissionPrompts.Quoted(p.Title) + ": the player left it out"))
                .ToList();
            if (left.Count > 0) text.Append("Not this flight: ").Append(string.Join("; ", left.ToArray())).Append(".\n");
            text.Append("Plan the Mission to serve every chosen Contract Requirement in the phase given, with the player's words; never add a Contract.\n");
            choice.Section = text.ToString();
            return choice;
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
