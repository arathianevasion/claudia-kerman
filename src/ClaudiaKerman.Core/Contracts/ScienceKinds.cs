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
    /// the air (low-flying, or high over a body with air) need an aircraft and are "not yet", as are the EVA experiments (the science block
    /// leaves EVA reports and samples to the player) and ground surveys on Kerbin.
    /// </summary>
    internal static class ScienceKinds
    {
        public const string CollectScience = "CollectScience";
        public const string Survey = "SurveyContract";
        private const string SurveyWaypointKind = "SurveyWaypointParameter";

        public const string LowFlyingNotYet = "not yet: low-flying surveys need an aircraft over the waypoint";
        public const string HighFlyingNotYet = "not yet: surveys flown high in the air need an aircraft over the waypoint";
        public const string EvaNotYet = "not yet: needs an EVA in a Manual stretch, not planned yet";
        public const string HomeGroundNotYet = "not yet: ground surveys on Kerbin aren't planned yet";

        /// <summary>The stock bodies with air: a survey band over them is flown in the air, never in space.</summary>
        private static readonly HashSet<string> StockAir = new HashSet<string> { "Kerbin", "Eve", "Duna", "Jool", "Laythe" };

        /// <summary>The experiments the science block can't run: the player's job on EVA.</summary>
        private static readonly HashSet<string> EvaExperiments = new HashSet<string> { "evaReport", "surfaceSample" };

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

        private static ReadRequirement ReadWaypoint(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var body = ContractKinds.BodyAt(ContractKinds.Setting(requirement, "targetBody"));
            var band = ContractKinds.Setting(requirement, "band").ToUpperInvariant();
            var experiment = ContractKinds.Setting(requirement, "experiment");
            var latitude = ContractKinds.Number(requirement, "wpLatitude");
            var longitude = ContractKinds.Number(requirement, "wpLongitude");
            var threshold = ContractKinds.Number(requirement, "flightThreshold");
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (!latitude.HasValue || !longitude.HasValue) return ContractKinds.NotYet(one, "not yet: the survey names no waypoint");
            if (experiment.Length == 0) return ContractKinds.NotYet(one, "not yet: the survey names no experiment");
            if (EvaExperiments.Contains(experiment)) return ContractKinds.NotYet(one, EvaNotYet);
            var waypoint = new SurveyWaypoint { Key = requirement.Key, Body = body, Latitude = latitude.Value, Longitude = longitude.Value };
            if (band == "GROUND")
            {
                if (body == home) return ContractKinds.NotYet(one, HomeGroundNotYet);
                waypoint.Ground = true;
                one.Experiment = experiment;
                one.Waypoint = waypoint;
                return ContractKinds.Visit(one, body, ContractReach.Land, "take a " + experiment + " landed at the waypoint " + waypoint.Where() + " on " + ContractKinds.Named(body), home);
            }
            if (band != "LOW" && band != "HIGH") return ContractKinds.NotYet(one, "not yet: the survey's band (" + band + ") isn't one Claudia knows");
            if (StockAir.Contains(body)) return ContractKinds.NotYet(one, band == "LOW" ? LowFlyingNotYet : HighFlyingNotYet);
            if (!threshold.HasValue) return ContractKinds.NotYet(one, "not yet: the survey names no altitude for its band");
            if (band == "LOW") waypoint.MaxAltitude = threshold;
            else waypoint.MinAltitude = threshold;
            one.Experiment = experiment;
            one.Waypoint = waypoint;
            var km = (threshold.Value / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km";
            return ContractKinds.Visit(one, body, ContractReach.Orbit,
                "take a " + experiment + " over the waypoint " + waypoint.Where() + ", " + (band == "LOW" ? "below " : "above ") + km + " over " + ContractKinds.Named(body), home);
        }
    }
}
