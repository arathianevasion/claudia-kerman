// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>The two Transfer Windows a window search gives.</summary>
    public enum WindowKind
    {
        Soonest,
        Optimal,
    }

    /// <summary>
    /// The player's pick of a Transfer Window (#686): which of the two, and the body the transfer goes to, so a pick made for one
    /// transfer is never read as another's. <see cref="None"/> is no pick. The windows' dates are worked out again whenever they are
    /// needed: what the player keeps is Soonest or Optimal.
    /// </summary>
    public sealed class WindowPick
    {
        public static readonly WindowPick None = new WindowPick("", WindowKind.Soonest);

        public readonly string Body;
        public readonly WindowKind Kind;

        public WindowPick(string body, WindowKind kind)
        {
            Body = body ?? "";
            Kind = kind;
        }

        public bool IsNone
        {
            get { return Body.Length == 0; }
        }

        /// <summary>"Duna:Soonest", or "" for no pick: what a Craft Check result and a save hold it by.</summary>
        public string Key
        {
            get { return IsNone ? "" : Body + ":" + Kind; }
        }

        /// <summary>Whether this pick is for the transfer to <paramref name="body"/>.</summary>
        public bool IsFor(string body)
        {
            return !IsNone && string.Equals(Body, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One row of the choice: its window's name, and its figures in one line for the player.</summary>
    public sealed class WindowRow
    {
        public WindowKind Kind;
        /// <summary>"Soonest: leaves Year 1, Day 12, 03:20 (in 12d), ejection 1,040 m/s (about 2m 5s), capture 640 m/s, total 1,680 m/s, flight 212d".</summary>
        public string Text = "";
        /// <summary>The body the craft departs from and the one it goes to.</summary>
        internal string From = "";
        internal string Body = "";
        internal double DepartureUt;
        internal double TotalDv;
        internal double CaptureDv;
        /// <summary>#782: the literal UT a departure's script gives transfer_to for this window (<see cref="WindowChoice.Retimed"/>), and return's on the direct route (null for no return).</summary>
        internal double WindowUt;
        internal double? ReturnWindow;

        /// <summary>Whether the Delta-v Map's leg <paramref name="id"/> is this window's trip.</summary>
        internal bool Budgets(string id)
        {
            return id == "transfer:" + Body || id == "transfer:" + From + ">" + Body || (Body == CraftCheckFlow.HomeBody && id == "return:" + From);
        }
    }

    /// <summary>The windows on offer for a Mission Goal: two rows when the player chooses, none when the setting does or no window was found.</summary>
    public sealed class WindowOffer
    {
        public static readonly WindowOffer None = new WindowOffer();

        /// <summary>The body the transfer goes to; "" when there is no choice to make.</summary>
        public string Body = "";
        /// <summary>The card's heading, e.g. "Transfer Window to Duna: pick one".</summary>
        public string Caption = "";
        /// <summary>Why a transfer that would take a choice offers none (the search found no window); "" otherwise.</summary>
        public string Problem = "";
        public IList<WindowRow> Rows = new List<WindowRow>();

        /// <summary>The player must pick before the Mission or the plan goes on.</summary>
        public bool Needed
        {
            get { return Rows.Count > 0; }
        }

        /// <summary>The row for <paramref name="pick"/>; null when it isn't a pick from this offer.</summary>
        public WindowRow RowFor(WindowPick pick)
        {
            return pick != null && pick.IsFor(Body) ? Rows.FirstOrDefault(r => r.Kind == pick.Kind) : null;
        }
    }

    /// <summary>
    /// Who picks the Transfer Window (#686, amending #488). For a transfer between bodies that orbit the same parent, the player does:
    /// the body the craft departs from and the target, or the target's parent (Kerbin to Ike goes through Duna). Within one sphere of
    /// influence the Mission Setting Time versus delta-v still picks (#669). Only the player ever picks: Auto Mode never does. A departure
    /// no pick covers asks at its first Sequence Plan (#782: <see cref="Departure"/>).
    /// </summary>
    public static class WindowChoice
    {
        /// <summary>What Start and Approval say when the pick is missing.</summary>
        public const string PickFirst = "Pick a Transfer Window first: Soonest or Optimal.";

        /// <summary>A literal UT as a block's window option, as plan_transfer's rows write it.</summary>
        private static readonly Regex LiteralUt = new Regex(@"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>A rework at most this often in game time, s (a Kerbin day), so a row's date and wait stay about right while the player reads them.</summary>
        private const double ReworkEveryS = 21600;

        /// <summary><paramref name="pick"/> when it is a pick from <paramref name="offer"/>; otherwise <see cref="WindowPick.None"/>.</summary>
        public static WindowPick Held(WindowPick pick, WindowOffer offer)
        {
            return offer != null && offer.RowFor(pick) != null ? pick : WindowPick.None;
        }

        /// <summary>
        /// #966: the craft has left on the transfer <paramref name="pick"/> is for, so its ejection has flown: it is in orbit of the
        /// target's parent, or within the target's own sphere of influence. Read from where the craft is, not from the plan's legs.
        /// </summary>
        internal static bool Departed(GameSnapshot snapshot, WindowPick pick)
        {
            if (snapshot == null || pick == null || pick.IsNone || snapshot.Bodies == null) return false;
            var system = new BodySystem(snapshot.Bodies);
            var target = system.Find(pick.Body);
            var parent = target == null ? null : system.Parent(target);
            var orbit = snapshot.Orbit;
            var at = system.Find(orbit != null && !string.IsNullOrEmpty(orbit.Body) ? orbit.Body : snapshot.Body);
            if (parent == null || at == null) return false;
            if (string.Equals(at.Name, parent.Name, StringComparison.OrdinalIgnoreCase)) return true;
            // Up from where the craft is to the body that orbits the target's parent: the target itself once it has arrived, the body it departs from (or another) before.
            for (var body = at; system.Parent(body) != null; body = system.Parent(body))
            {
                if (string.Equals(system.Parent(body).Name, parent.Name, StringComparison.OrdinalIgnoreCase)) return string.Equals(body.Name, target.Name, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// The body of the first transfer the Mission Goal needs that the player picks a window for; null for none. A craft already
        /// leaving its body (on an escape path) has made its choice, so none is asked.
        /// </summary>
        internal static SnapshotBody Transfer(GameSnapshot snapshot, string goal)
        {
            if (snapshot == null) return null;
            var orbit = snapshot.Orbit;
            if (orbit != null && !snapshot.IsGrounded && !(orbit.Eccentricity < 1)) return null;
            var system = new BodySystem(snapshot.Bodies);
            foreach (var named in MissionPrompts.GoalBodies(snapshot, goal))
                for (var body = named; body != null; body = system.Parent(body))
                    if (TransferWindows.IsSiblingTransfer(snapshot, body.Name)) return body;
            return null;
        }

        /// <summary>
        /// #784: whether the Maneuver Sequences still to fly (<paramref name="rest"/>) need the player's pick, read from the legs
        /// <see cref="PlanLegs"/> matches and not from the Mission Goal's words. They do when one is matched to a transfer or return
        /// leg, or to a launch or ascent (the departure chain of #782). Any sequence this craft still flies that matches no leg is
        /// asked about too: a guess from a name never skips the player. Sequences listed as flown, and a Child Vessel's, are not the
        /// craft's to fly.
        /// </summary>
        internal static bool LegsAsk(GameSnapshot snapshot, IList<PlannedSequence> rest, string plannedAscent)
        {
            foreach (var leg in PlanLegs.Judge(snapshot, rest, plannedAscent))
            {
                if (PlanLegs.IsFlown(leg.Sequence) || !MissionCards.FliesCraft(snapshot.Craft.Name, leg.Sequence.Vessel)) continue;
                if (!leg.Matched) return true;
                foreach (var id in leg.Legs.Split(new[] { " + " }, StringSplitOptions.None))
                    if (id.StartsWith("transfer:", StringComparison.Ordinal) || id.StartsWith("return:", StringComparison.Ordinal)
                        || id.StartsWith("launch:", StringComparison.Ordinal) || id.StartsWith("ascend:", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>What the offer depends on, so it is worked out again only when one of these moves: one search per change, not per frame.</summary>
        internal static string Key(GameSnapshot snapshot, string goal, int marginPercent, double now)
        {
            var orbit = snapshot.Orbit;
            return (goal ?? "") + "|" + snapshot.Craft.VesselId + "|" + snapshot.Body + "|" + snapshot.Situation + "|" + (orbit == null ? "" : orbit.Body + ":" + Whole(orbit.SemiMajorAxis / 1000)) + "|" + marginPercent
                + "|" + Whole(snapshot.Stages.Sum(s => s.DeltaVVac) / 10) + "|" + Whole(snapshot.Stages.Sum(s => s.ThrustVacKn)) + "|" + Whole(Period(now));
        }

        /// <summary>Which rework period <paramref name="now"/> is in.</summary>
        internal static double Period(double now)
        {
            return Math.Floor(now / ReworkEveryS);
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The offer for <paramref name="goal"/> from <paramref name="now"/>, from the same search the Mission request's plan_transfer rows make.</summary>
        internal static WindowOffer Work(GameSnapshot snapshot, string goal, int marginPercent, double now)
        {
            var body = Transfer(snapshot, goal);
            if (body == null) return WindowOffer.None;
            var at = snapshot.At(now);
            var found = TransferWindows.Find(at, body.Name, MissionPrompts.ArrivalPe(body), ArrivalRule.Capture(at, body.Name, null), marginPercent);
            return found.Error != null ? Unworked(body.Name, found.Error) : Offer(at, body.Name, now, found.Soonest, found.Optimal);
        }

        /// <summary>
        /// #782: the departure <paramref name="plan"/> starts that the player picks a window for; null for none. A landed craft's is the
        /// ascent's (<see cref="WindowWaits.AfterAscent"/>). In a closed orbit it is the script's transfer_to to a body with the same
        /// parent, or to another planet from a moon's orbit (<see cref="DirectRoute.Applies"/>), or a return from a moon when the Mission
        /// Plan's legs lead on to another planet. A return from the Mun or Minmus leads only to Kerbin: the setting's, as ever.
        /// </summary>
        internal static SurfaceDeparture Departure(GameSnapshot snapshot, SequencePlan plan, IList<PlannedSequence> legs, int step)
        {
            if (snapshot == null || plan == null || plan.Manual) return null;
            var surface = WindowWaits.AfterAscent(snapshot, plan, legs, step);
            if (surface != null) return surface;
            var orbit = snapshot.Orbit;
            if (orbit == null || snapshot.IsGrounded || !(orbit.Eccentricity < 1)) return null;
            foreach (var call in BlockOptionCheck.Calls(plan.Script))
            {
                if (call.Block == "return")
                {
                    var moon = SequenceWords.Body(snapshot, !string.IsNullOrEmpty(orbit.Body) ? orbit.Body : snapshot.Body);
                    var onward = moon == null ? null : WindowWaits.Departure(snapshot, moon, legs ?? new List<PlannedSequence>(), step - 1);
                    if (onward != null) return onward;
                    continue;
                }
                if (call.Block != "transfer_to") continue;
                var target = SequenceWords.Body(snapshot, (call.Option("target") ?? "").Trim('"'));
                if (target != null && (TransferWindows.IsSiblingTransfer(snapshot, target.Name) || DirectRoute.Applies(snapshot, target.Name))) return new SurfaceDeparture { Planet = target };
            }
            return null;
        }

        /// <summary>#782: the two rows for <paramref name="departure"/>, each the window its pick would really take: from the surface, what the ascent's Due Time is worked from (<see cref="WindowWaits.Solve"/>).</summary>
        internal static WindowOffer WorkDeparture(GameSnapshot snapshot, SurfaceDeparture departure, TimeVersusDeltaV setting, int marginPercent, double now)
        {
            var name = departure.Planet.Name;
            var at = snapshot.At(now);
            at.StampMission(setting, marginPercent);
            if (at.IsGrounded)
            {
                var up = Taken(snapshot, departure, setting, marginPercent, now, WindowKind.Soonest);
                var late = Taken(snapshot, departure, setting, marginPercent, now, WindowKind.Optimal);
                if (up.Window == null || late.Window == null) return Unworked(name, "no window from its low orbit");
                var fromGround = Offer(at, name, now, up.Window, late.Window);
                if (up.Route != null) Retimes(fromGround.Rows[0], up.Route);
                if (late.Route != null) Retimes(fromGround.Rows[1], late.Route);
                return fromGround;
            }
            if (DirectRoute.Applies(at, name))
            {
                string error;
                var soonest = Routed(at, departure, WindowKind.Soonest, out error);
                var optimal = soonest == null ? null : Routed(at, departure, WindowKind.Optimal, out error);
                if (optimal == null) return Unworked(name, error);
                var offer = Offer(at, name, now, WindowWaits.AsWindow(soonest), WindowWaits.AsWindow(optimal));
                Retimes(offer.Rows[0], soonest);
                Retimes(offer.Rows[1], optimal);
                return offer;
            }
            var found = TransferWindows.Find(at, name, MissionPrompts.ArrivalPe(departure.Planet), ArrivalRule.Capture(at, name, null), marginPercent);
            return found.Error != null ? Unworked(name, found.Error) : Offer(at, name, now, found.Soonest, found.Optimal);
        }

        private static SurfaceDeparture Taken(GameSnapshot snapshot, SurfaceDeparture departure, TimeVersusDeltaV setting, int marginPercent, double now, WindowKind kind)
        {
            var taken = new SurfaceDeparture { Planet = departure.Planet, TwoStep = departure.TwoStep };
            WindowWaits.Solve(snapshot, taken, setting, marginPercent, now, new WindowPick(departure.Planet.Name, kind));
            return taken;
        }

        private static RouteRow Routed(GameSnapshot at, SurfaceDeparture departure, WindowKind kind, out string error)
        {
            var name = departure.Planet.Name;
            var picked = at.At(at.Ut);
            picked.WindowPick = new WindowPick(name, kind);
            var routes = DirectRoute.Find(picked, name, MissionPrompts.ArrivalPe(departure.Planet), ArrivalRule.Capture(picked, name, null));
            error = routes.Error;
            return routes.Error != null ? null : departure.TwoStep ? routes.TwoStep : routes.Pick;
        }

        private static void Retimes(WindowRow row, RouteRow route)
        {
            row.WindowUt = route.EjectWindow;
            row.ReturnWindow = route.ReturnWindow;
        }

        /// <summary>
        /// #782: <paramref name="script"/>, written before the player picked, flying <paramref name="row"/>'s window: a literal window
        /// option on the departure's own calls becomes that window's. Any other value is left: transfer_to's "next" reads the pick as it
        /// flies, and return's burns at its next pass with the window taken at transfer_to from the planet.
        /// </summary>
        internal static string Retimed(string script, WindowRow row)
        {
            var calls = BlockOptionCheck.Calls(script).ToList();
            for (var i = calls.Count - 1; i >= 0; i--)
            {
                var call = calls[i];
                double? ut = null;
                if (call.Block == "return") ut = row.ReturnWindow;
                else if (call.Block == "transfer_to" && string.Equals((call.Option("target") ?? "").Trim('"'), row.Body, StringComparison.OrdinalIgnoreCase)) ut = row.WindowUt;
                if (!ut.HasValue) continue;
                string given;
                if (call.Options == null || !call.Options.TryGetValue("window", out given) || !LiteralUt.IsMatch(given)) continue;
                var end = i + 1 < calls.Count ? calls[i + 1].Index : script.Length;
                var written = new Regex("\"window\"\\s*,\\s*(?<ut>" + Regex.Escape(given) + ")").Match(script, call.Index, end - call.Index);
                if (written.Success) script = script.Substring(0, written.Groups["ut"].Index) + Whole(ut.Value) + script.Substring(written.Groups["ut"].Index + written.Groups["ut"].Length);
            }
            return script;
        }

        private static WindowOffer Unworked(string body, string why)
        {
            return new WindowOffer { Problem = "Transfer Window to " + body + ": " + why + ". The Mission Setting picks." };
        }

        private static WindowOffer Offer(GameSnapshot at, string body, double now, TransferWindow soonest, TransferWindow optimal)
        {
            var from = at.Orbit != null && !string.IsNullOrEmpty(at.Orbit.Body) ? at.Orbit.Body : at.Body;
            return new WindowOffer
            {
                Body = body,
                Caption = "Transfer Window to " + body + ": pick one",
                Rows = new List<WindowRow>
                {
                    Row(at, WindowKind.Soonest, soonest, from, body, now),
                    Row(at, WindowKind.Optimal, optimal, from, body, now),
                },
            };
        }

        private static WindowRow Row(GameSnapshot snapshot, WindowKind kind, TransferWindow window, string from, string body, double now)
        {
            var burn = PredictBurn.BurnSeconds(snapshot, window.EjectionDv, AscentDv(snapshot));
            var wait = window.DepartureUt - now;
            var text = kind + ": leaves " + KerbinTime.Date(window.DepartureUt) + (wait > 0 ? " (in " + KerbinTime.Span(wait) + ")" : " (now)")
                + ", ejection " + Dv(window.EjectionDv) + " (" + (burn.HasValue ? "about " + KerbinTime.Span(burn.Value) : "burn time unknown") + ")"
                + (window.AscentLossDv >= 1 ? ", ascent loss " + Dv(window.AscentLossDv) : "")
                + ", capture " + Dv(window.CaptureDv) + ", total " + Dv(window.TotalDv) + ", flight " + KerbinTime.Span(window.ArrivalUt - window.DepartureUt);
            return new WindowRow { Kind = kind, Text = text, From = from, Body = body, DepartureUt = window.DepartureUt, TotalDv = window.TotalDv, CaptureDv = window.CaptureDv, WindowUt = window.DepartureUt };
        }

        /// <summary>From the ground, what the ascent spends first: the ejection burns on the stages left after it.</summary>
        private static double AscentDv(GameSnapshot snapshot)
        {
            if (!snapshot.IsGrounded) return 0;
            var legs = DeltaVMap.Compute(snapshot.Bodies, CraftCheckFlow.HomeBody).Legs;
            var ascent = legs.FirstOrDefault(l => l.Id == "launch:" + snapshot.Body || l.Id == "ascend:" + snapshot.Body);
            return ascent == null ? 0 : ascent.DeltaV;
        }

        private static string Dv(double value)
        {
            return value.ToString("#,0", CultureInfo.InvariantCulture) + " m/s";
        }
    }
}
