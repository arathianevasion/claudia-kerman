// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1030: one survey waypoint: where it is, and whether it is taken on the ground or from a band in space.</summary>
    public sealed class SurveyWaypoint
    {
        /// <summary>The Contract Requirement's key.</summary>
        public string Key = "";
        public string Body = "";
        /// <summary>Degrees, north and east positive.</summary>
        public double Latitude;
        public double Longitude;
        /// <summary>Taken landed at the waypoint; false for a pass over it in a space band.</summary>
        public bool Ground;
        /// <summary>#1246: taken by the player on EVA in a Manual stretch (an EVA report, a surface sample), not by the science block.</summary>
        public bool Eva;
        /// <summary>A space band's floor and ceiling above the surface, m; null where it has none.</summary>
        public double? MinAltitude;
        public double? MaxAltitude;

        /// <summary>"3.0° N 10.0° E".</summary>
        public string Where()
        {
            return Degrees(Latitude, "N", "S") + " " + Degrees(Longitude, "E", "W");
        }

        private static string Degrees(double value, string plus, string minus)
        {
            return Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture) + "° " + (value < 0 ? minus : plus);
        }
    }

    /// <summary>
    /// #1030: collect-science and survey Contract Requirements (ADR 0021: Core's kind table). Collect science is the science block run at
    /// the body: in space near it, an orbit of it; on its surface, a landing on it (on Kerbin, home again after the reentry). A survey
    /// waypoint in a space band (a band over a body with no air) is a pass over it from orbit; on the ground, a landing at it. Surveys flown in
    /// the air (low-flying, or high over a body with air) need an aircraft and are "not yet", as are ground surveys on Kerbin. #1246: the EVA
    /// experiments (an EVA report, a surface sample) the science block leaves to the player: each is a Manual stretch at the leg that meets
    /// the waypoint, after the landing at it or in the orbit that passes over it.
    /// </summary>
    internal static class ScienceKinds
    {
        public const string CollectScience = "CollectScience";
        public const string Survey = "SurveyContract";
        private const string SurveyWaypointKind = "SurveyWaypointParameter";

        public const string LowFlyingNotYet = "not yet: low-flying surveys need an aircraft over the waypoint";
        public const string HighFlyingNotYet = "not yet: surveys flown high in the air need an aircraft over the waypoint";
        public const string HomeGroundNotYet = "not yet: ground surveys on Kerbin aren't planned yet";
        public const string SampleInSpaceNotYet = "not yet: a surface sample is taken landed, never from a band in space";

        /// <summary>The stock bodies with air: a survey band over them is flown in the air, never in space.</summary>
        private static readonly HashSet<string> StockAir = new HashSet<string> { "Kerbin", "Eve", "Duna", "Jool", "Laythe" };

        /// <summary>The experiments the science block can't run, in words: the player's job on EVA in a Manual stretch.</summary>
        private static readonly Dictionary<string, string> EvaExperiments = new Dictionary<string, string>
            { { "evaReport", "an EVA report" }, { "surfaceSample", "a surface sample" } };

        public static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            if (contract.Kind == CollectScience && requirement.Kind == CollectScience) return ReadCollect(one, requirement, home);
            if (contract.Kind == Survey && requirement.Kind == SurveyWaypointKind) return ReadWaypoint(one, requirement, home);
            return ContractKinds.NotYet(one, "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet");
        }

        private static ReadRequirement ReadCollect(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var body = ContractKinds.BodyAt(ContractKinds.Setting(requirement, "body"));
            var location = ContractKinds.Setting(requirement, "location").ToUpperInvariant();
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            one.Experiment = "";
            if (location == "SPACE") return ContractKinds.Visit(one, body, ContractReach.Orbit, "collect science in space near " + ContractKinds.Named(body), home);
            if (location != "SURFACE") return ContractKinds.NotYet(one, "not yet: the Contract names no place to collect science");
            if (body != home) return ContractKinds.Visit(one, body, ContractReach.Land, "collect science on the surface of " + ContractKinds.Named(body), home);
            one.Body = home;
            one.Reach = ContractReach.Land;
            one.Return = true;
            one.Phase = "Return";
            one.Words = "collect science on " + home + "'s surface, home again";
            return one;
        }

        /// <summary>
        /// #1260: a waypoint's flight band, as the game saves it: a number, the value of KSP's <c>FinePrint.Utilities.FlightBand</c>
        /// enum in its declared order (NONE 0, GROUND 1, LOW 2, HIGH 3), or its name. Only HIGH = 3 is confirmed in game so far: the Career
        /// Test capture of 2026-11-14 saves a HIGH survey's waypoints as <c>band = 3</c>. Any other number stays as written, for the
        /// "not yet" line.
        /// </summary>
        private static string Band(string saved)
        {
            switch (saved)
            {
                case "1": return "GROUND";
                case "2": return "LOW";
                case "3": return "HIGH";
                default: return saved.ToUpperInvariant();
            }
        }

        private static ReadRequirement ReadWaypoint(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var body = ContractKinds.BodyAt(ContractKinds.Setting(requirement, "targetBody"));
            var band = Band(ContractKinds.Setting(requirement, "band"));
            var experiment = ContractKinds.Setting(requirement, "experiment");
            var latitude = ContractKinds.Number(requirement, "wpLatitude");
            var longitude = ContractKinds.Number(requirement, "wpLongitude");
            var threshold = ContractKinds.Number(requirement, "flightThreshold");
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (!latitude.HasValue || !longitude.HasValue) return ContractKinds.NotYet(one, "not yet: the survey names no waypoint");
            if (experiment.Length == 0) return ContractKinds.NotYet(one, "not yet: the survey names no experiment");
            string eva;
            EvaExperiments.TryGetValue(experiment, out eva);
            var waypoint = new SurveyWaypoint { Key = requirement.Key, Body = body, Latitude = latitude.Value, Longitude = longitude.Value };
            if (band == "GROUND")
            {
                if (body == home) return ContractKinds.NotYet(one, HomeGroundNotYet);
                waypoint.Ground = true;
                one.Waypoint = waypoint;
                if (eva != null)
                {
                    waypoint.Eva = true;
                    one.Manual = "walk to the waypoint " + waypoint.Where() + " and take " + eva + " there on EVA";
                    return ContractKinds.Visit(one, body, ContractReach.Land, "take " + eva + " at the waypoint " + waypoint.Where() + " on " + ContractKinds.Named(body), home);
                }
                one.Experiment = experiment;
                return ContractKinds.Visit(one, body, ContractReach.Land, "take a " + experiment + " landed at the waypoint " + waypoint.Where() + " on " + ContractKinds.Named(body), home);
            }
            if (band != "LOW" && band != "HIGH") return ContractKinds.NotYet(one, "not yet: the survey's band (" + band + ") isn't one Claudia knows");
            if (StockAir.Contains(body)) return ContractKinds.NotYet(one, band == "LOW" ? LowFlyingNotYet : HighFlyingNotYet);
            if (!threshold.HasValue) return ContractKinds.NotYet(one, "not yet: the survey names no altitude for its band");
            if (experiment == "surfaceSample") return ContractKinds.NotYet(one, SampleInSpaceNotYet);
            if (band == "LOW") waypoint.MaxAltitude = threshold;
            else waypoint.MinAltitude = threshold;
            one.Waypoint = waypoint;
            var km = (threshold.Value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
            var over = "over the waypoint " + waypoint.Where() + ", " + (band == "LOW" ? "below " : "above ") + km;
            if (eva != null)
            {
                waypoint.Eva = true;
                one.Manual = "take " + eva + " on EVA " + over;
                return ContractKinds.Visit(one, body, ContractReach.Orbit, "take " + eva + " " + over + " over " + ContractKinds.Named(body), home);
            }
            one.Experiment = experiment;
            return ContractKinds.Visit(one, body, ContractReach.Orbit, "take a " + experiment + " " + over + " over " + ContractKinds.Named(body), home);
        }
    }
}
