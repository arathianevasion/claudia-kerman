// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Contracts;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// #1307: the accepted surveys' waypoints still to take from orbit over one body, for <c>ADDONS:CLAUDIA:SURVEYSCHEDULE</c>; and the
    /// survey's trigger range from the game's own Contract settings. Read as <see cref="ContractReader"/> reads the Game Snapshot, through
    /// Core's kind table, so the waypoints and their Bands are the ones the Contract Check planned. It only reads. Main thread only.
    /// </summary>
    public static class SurveyWaypointReader
    {
        /// <summary>The stock Contracts.cfg's Survey <c>MaximumTriggerRange</c>, m, where the game's settings can't be read.</summary>
        public const double StockTriggerRange = SurveySchedule.StockTriggerRange;

        /// <summary>The orbital survey waypoints on <paramref name="body"/> not yet met; empty outside Career.</summary>
        public static List<SurveyWaypoint> Orbital(string body)
        {
            var contracts = ContractReader.Read();
            if (contracts == null) return new List<SurveyWaypoint>();
            var home = FlightGlobals.GetHomeBodyName();
            return contracts.Accepted
                .SelectMany(c => ContractKinds.Read(c, home))
                .Where(r => r.NotYet.Length == 0 && r.Waypoint != null && !r.Waypoint.Ground && r.Waypoint.Body == body)
                .Select(r => r.Waypoint)
                .ToList();
        }

        /// <summary>
        /// How near a waypoint the craft must pass in a high survey: the game's Survey <c>MaximumTriggerRange</c> from its Contract settings
        /// (GameData/Squad/Contracts/Contracts.cfg), else the stock figure.
        /// </summary>
        public static double TriggerRange()
        {
            if (GameDatabase.Instance == null) return StockTriggerRange;
            foreach (var contracts in GameDatabase.Instance.GetConfigNodes("CONTRACTS"))
            {
                var survey = contracts.GetNode("Survey");
                double range;
                if (survey != null && double.TryParse(survey.GetValue("MaximumTriggerRange"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out range) && range > 0)
                    return range;
            }
            return StockTriggerRange;
        }
    }
}
