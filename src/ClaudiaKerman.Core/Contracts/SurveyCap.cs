// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1343, the player's rulings (grill, 2026-11-24): a whole orbital survey ends within the lesser of 24 game hours from its first
    /// Sequence Plan and the Contract's deadline. While waypoints are open and another Survey Plane's window (<see cref="SurveySchedule.PlaneWindow"/>)
    /// fits before that cap, the next Sequence Plan flies the next Survey Plane; when it doesn't, Claudia proposes dropping the Contract in a
    /// revised Mission Plan, for the player's Approval.
    /// </summary>
    public static class SurveyCap
    {
        /// <summary>The whole survey's length from its first Sequence Plan, game seconds (24 hours).</summary>
        public const double WholeSurvey = 86400;

        /// <summary>The Sequence Plan request's ask, which a test finds it by.</summary>
        public const string NextPlaneWords = "Fly the next Survey Plane before leaving the body";

        /// <summary>The revised Mission Plan request's ask when the next window won't fit.</summary>
        public const string StopWords = "No room for another Survey Plane";

        /// <summary>The cap: the first Sequence Plan's UT plus 24 hours, or the earliest of the open Contracts' deadlines if that comes first.</summary>
        public static double Cap(double startUt, IEnumerable<double> deadlines)
        {
            return deadlines.Aggregate(startUt + WholeSurvey, (cap, deadline) => System.Math.Min(cap, deadline));
        }

        /// <summary>Whether another Survey Plane's window, begun at <paramref name="nowUt"/>, ends within <paramref name="cap"/>.</summary>
        public static bool WindowFits(double nowUt, double cap)
        {
            return nowUt + SurveySchedule.PlaneWindow <= cap;
        }

        /// <summary>The Sequence Plan request's line: the waypoints still open, and the next Survey Plane asked for.</summary>
        public static string NextPlaneLine(IEnumerable<string> open, string body)
        {
            return "Survey waypoints still open at " + body + " (the mod's reading of the game, data): " + Named(open) + ". "
                + NextPlaneWords + " (" + body + "): this Sequence Plan flies the next Survey Plane, from the survey_schedule tool's survey_plane and node. "
                + "It is approved like any other Sequence Plan, under Auto Mode too.\n";
        }

        /// <summary>The revised Mission Plan request's text when no other window fits before <paramref name="cap"/>, whose cause is <paramref name="why"/>.</summary>
        public static string StopLine(IEnumerable<string> open, string body, string why)
        {
            return "Survey waypoints still open at " + body + " (the mod's reading of the game, data): " + Named(open) + ". "
                + StopWords + ": " + why + ". Propose dropping their Contracts instead, with the reason: a revised Mission Plan with no leg that serves them. "
                + "It waits for the player's Approval, under Auto Mode too.\n";
        }

        /// <summary>Why the next window won't fit: the 24 hours or the deadline, whichever is the cap.</summary>
        public static string WhyNoRoom(double startUt, double nowUt, IList<double> deadlines)
        {
            var window = SurveySchedule.PlaneWindow.ToString("N0", CultureInfo.InvariantCulture);
            var day = startUt + WholeSurvey;
            if (deadlines.Count > 0 && deadlines.Min() < day)
                return "the next " + window + " s window would end after the Contract's deadline";
            return "the next " + window + " s window would end after 24 game hours from the survey's first Sequence Plan";
        }

        private static string Named(IEnumerable<string> open)
        {
            return string.Join("; ", open.Select(o => "\"" + o + "\"").ToArray());
        }
    }
}
