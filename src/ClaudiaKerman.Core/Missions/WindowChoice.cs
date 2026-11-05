// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

        /// <summary>Whether the Delta-v Map's leg <paramref name="id"/> is this window's trip.</summary>
        internal bool Budgets(string id)
        {
            return id == "transfer:" + Body || id == "transfer:" + From + ">" + Body || id == "return:" + From;
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
    /// influence the Mission Setting Time versus delta-v still picks (#669). Only the player ever picks: Auto Mode never does.
    /// </summary>
    public static class WindowChoice
    {
        /// <summary>What Start and Approval say when the pick is missing.</summary>
        public const string PickFirst = "Pick a Transfer Window first: Soonest or Optimal.";

        /// <summary>A rework at most this often in game time, s, so a row's date and wait stay about right while the player reads them.</summary>
        private const double ReworkEveryS = 3600;

        /// <summary><paramref name="pick"/> when it is a pick from <paramref name="offer"/>; otherwise <see cref="WindowPick.None"/>.</summary>
        public static WindowPick Held(WindowPick pick, WindowOffer offer)
        {
            return offer != null && offer.RowFor(pick) != null ? pick : WindowPick.None;
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

        /// <summary>What the offer depends on, so it is worked out again only when one of these moves: one search per change, not per frame.</summary>
        internal static string Key(GameSnapshot snapshot, string goal, int marginPercent, double now)
        {
            var dv = snapshot.Stages.Sum(s => s.DeltaVVac);
            var orbit = snapshot.Orbit;
            return (goal ?? "") + "|" + snapshot.Body + "|" + snapshot.Situation + "|" + (orbit == null ? "" : orbit.Body) + "|" + marginPercent
                + "|" + Math.Round(dv / 10).ToString(CultureInfo.InvariantCulture) + "|" + Math.Floor(now / ReworkEveryS).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The offer for <paramref name="goal"/> from <paramref name="now"/>, from the same search the Mission request's plan_transfer rows make.</summary>
        internal static WindowOffer Work(GameSnapshot snapshot, string goal, int marginPercent, double now)
        {
            var body = Transfer(snapshot, goal);
            if (body == null) return WindowOffer.None;
            var at = snapshot.At(now);
            var found = TransferWindows.Find(at, body.Name, MissionPrompts.ArrivalPe(body), ArrivalRule.Capture(at, body.Name, null), marginPercent);
            if (found.Error != null) return new WindowOffer { Problem = "Transfer Window to " + body.Name + ": " + found.Error + ". The Mission Setting picks." };
            var from = snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body;
            return new WindowOffer
            {
                Body = body.Name,
                Caption = "Transfer Window to " + body.Name + ": pick one",
                Rows = new List<WindowRow>
                {
                    Row(at, WindowKind.Soonest, found.Soonest, from, body.Name, now),
                    Row(at, WindowKind.Optimal, found.Optimal, from, body.Name, now),
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
            return new WindowRow { Kind = kind, Text = text, From = from, Body = body, DepartureUt = window.DepartureUt, TotalDv = window.TotalDv, CaptureDv = window.CaptureDv };
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
