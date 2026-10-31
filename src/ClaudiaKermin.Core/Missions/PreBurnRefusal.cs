// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>Where the craft was, for <see cref="PreBurnRefusal"/>: its body, situation and orbit (m and degrees).</summary>
    public sealed class OrbitFix
    {
        public string Body = "";
        public string Situation = "";
        public double Apoapsis;
        public double Periapsis;
        public double Inclination;

        /// <summary>The snapshot's orbit; null without a snapshot or an orbit.</summary>
        public static OrbitFix Of(GameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Orbit == null) return null;
            return new OrbitFix
            {
                Body = snapshot.Body ?? "",
                Situation = snapshot.Situation ?? "",
                Apoapsis = snapshot.Orbit.Apoapsis,
                Periapsis = snapshot.Orbit.Periapsis,
                Inclination = snapshot.Orbit.Inclination,
            };
        }

        /// <summary>The orbit a Script Result reports (body, situation and orbit's ap, pe and inc, as the blocks write them); null when any is missing.</summary>
        public static OrbitFix OfReport(IDictionary<string, object> report)
        {
            if (report == null) return null;
            object value;
            var orbit = report.TryGetValue("orbit", out value) ? value as IDictionary<string, object> : null;
            var ap = PreBurnRefusal.Number(orbit, "ap");
            var pe = PreBurnRefusal.Number(orbit, "pe");
            var inc = PreBurnRefusal.Number(orbit, "inc");
            if (!ap.HasValue || !pe.HasValue || !inc.HasValue) return null;
            return new OrbitFix
            {
                Body = report.TryGetValue("body", out value) ? value as string ?? "" : "",
                Situation = report.TryGetValue("situation", out value) ? value as string ?? "" : "",
                Apoapsis = ap.Value,
                Periapsis = pe.Value,
                Inclination = inc.Value,
            };
        }
    }

    /// <summary>
    /// A pre-burn refusal (#579): a run whose block refused before burning anything (<c>land</c>'s "terrain on the approach" at
    /// 6 s, <c>tune_pe</c>'s "found no burn", <c>transfer_to</c>'s "no flyby found"), so the craft is as it was when the plan was
    /// offered. The mod writes its Debrief card itself, Off-Nominal, and asks straight for the next Sequence Plan: no margin
    /// recheck and no Debrief request. The rule, all of it needed: the run Finished, not aborted, with success false and a reason;
    /// dv_used at most 1 m/s (or, with none reported, the stock delta-v within 2 m/s of the run's start), and the stock delta-v
    /// within 2 m/s whenever both readings are there; the same body and situation, apoapsis and periapsis within 1 km or 1%
    /// and inclination within 0.5° of the run's start; and no Telemetry Check crossed, recovered or not.
    /// The orbit's tolerance is the mod's, not the Sequence Plan's: those are prose, and a craft that burned nothing moves less
    /// than any plan's tolerance.
    /// </summary>
    public static class PreBurnRefusal
    {
        public const double MaxDeltaVUsed = 1;
        public const double MaxStockDeltaVChange = 2;
        public const double OrbitMetres = 1000;
        public const double OrbitFraction = 0.01;
        public const double InclinationDegrees = 0.5;

        /// <summary>The block's reason when the run is a pre-burn refusal; null when it is not.</summary>
        /// <param name="before">The craft at the run's start.</param>
        /// <param name="after">The craft now (or as the report gives it).</param>
        /// <param name="startDeltaV">The stock delta-v left at the run's start; null when not known.</param>
        /// <param name="nowDeltaV">The stock delta-v left now; null when not known.</param>
        /// <param name="crossed">A Telemetry Check was crossed during the run.</param>
        public static string Reason(RunOutcome outcome, OrbitFix before, OrbitFix after, double? startDeltaV, double? nowDeltaV, bool crossed)
        {
            if (outcome == null || outcome.Kind != RunOutcomeKind.Finished || outcome.WasAborted || outcome.Report == null || crossed) return null;
            var report = outcome.Report;
            object value;
            if (!report.TryGetValue("success", out value) || !(value is bool) || (bool)value) return null;
            var reason = report.TryGetValue("reason", out value) ? (value as string ?? "").Trim() : "";
            if (reason.Length == 0) return null;

            var stockChange = startDeltaV.HasValue && nowDeltaV.HasValue ? Math.Abs(startDeltaV.Value - nowDeltaV.Value) : (double?)null;
            if (stockChange.HasValue && stockChange.Value > MaxStockDeltaVChange) return null;
            var used = Number(report, "dv_used");
            if (used.HasValue ? used.Value > MaxDeltaVUsed : !stockChange.HasValue) return null;

            if (before == null || after == null) return null;
            if (before.Body != after.Body || before.Situation != after.Situation) return null;
            if (!Near(before.Apoapsis, after.Apoapsis) || !Near(before.Periapsis, after.Periapsis)) return null;
            if (Math.Abs(before.Inclination - after.Inclination) > InclinationDegrees) return null;
            return reason;
        }

        private static bool Near(double before, double after)
        {
            return Math.Abs(after - before) <= Math.Max(OrbitMetres, OrbitFraction * Math.Abs(before));
        }

        /// <summary>A report's number by name; null when it is missing or not a number.</summary>
        internal static double? Number(IDictionary<string, object> report, string name)
        {
            object value;
            if (report == null || !report.TryGetValue(name, out value) || value == null || value is bool || value is string) return null;
            var convertible = value as IConvertible;
            if (convertible == null) return null;
            try
            {
                return convertible.ToDouble(CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        // ---- The card and the transcript's fact ----

        /// <summary>The line the fact starts with; <see cref="MissionLogRebuild"/> finds the mod-written card by it.</summary>
        public const string FactPrefix = "The mod debriefed the run itself, with no request: refused before any burn, Off-Nominal (JSON data): ";
        public const string FollowUpText = "next Sequence Plan";

        /// <summary>
        /// The short user-side fact committed to the transcript in place of a Debrief turn: the sequence's name and the reason,
        /// as JSON on one line. Never an assistant turn, so the cached prefix and the message order stay valid.
        /// </summary>
        public static string Fact(string sequence, string reason)
        {
            return FactPrefix + new JsonWriter().BeginObject().Field("sequence", sequence ?? "").Field("reason", reason ?? "").EndObject();
        }

        /// <summary>Reads a fact <see cref="Fact"/> wrote; false for any other text.</summary>
        public static bool ReadFact(string text, out string sequence, out string reason)
        {
            sequence = null;
            reason = null;
            if (text == null || !text.StartsWith(FactPrefix, StringComparison.Ordinal)) return false;
            var json = text.Substring(FactPrefix.Length);
            var end = json.IndexOf('\n');
            if (end >= 0) json = json.Substring(0, end);
            try
            {
                var root = JsonReader.Parse(json);
                sequence = JsonNode.Str(root["sequence"]) ?? "";
                reason = JsonNode.Str(root["reason"]) ?? "";
            }
            catch (JsonException)
            {
                return false;
            }
            return reason.Length > 0;
        }

        /// <summary>The Debrief the mod writes for one: Off-Nominal, the reason, the goal checklist as the last Debrief left it, and the next Sequence Plan to follow.</summary>
        public static Debrief Debrief(string reason, IEnumerable<GoalItem> checklist)
        {
            var said = "Refused before any burn: " + reason;
            return new Debrief
            {
                Outcome = "Off-Nominal",
                WhatHappened = said,
                OutcomeVsTarget = said,
                Checklist = (checklist ?? Enumerable.Empty<GoalItem>()).Select(i => new GoalItem { Item = i.Item, Done = i.Done }).ToList(),
                FollowUp = FollowUps.CorrectiveSequence,
                FollowUpText = FollowUpText,
            };
        }

        /// <summary>The Sequence Plan request's opening line after one: the run report goes after it, then the UT and Game Snapshot.</summary>
        public const string NextTurnLead =
            "The block refused before any burn, so the craft is unchanged since the Sequence Plan was offered. The mod debriefed it itself "
            + "(Off-Nominal). Propose the next Sequence Plan with the reason in mind.\n";
    }
}
