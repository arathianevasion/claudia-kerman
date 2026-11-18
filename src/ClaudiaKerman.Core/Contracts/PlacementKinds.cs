// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>#1035: the wait Core estimates for a Requirement that completes after the Mission, once its placement is flown.</summary>
    public sealed class MissionWait
    {
        /// <summary>Game time from the placement until the game should mark it met, s.</summary>
        public double Seconds;
        /// <summary>What happens in the wait, for the card and the request: "the telescope maps 8 asteroids near Kerbin, about 80 days".</summary>
        public string Words = "";
    }

    /// <summary>#1035: a Sentinel telescope put into solar orbit near a planet's orbit, priced from low orbit round home.</summary>
    public sealed class SolarPlacement
    {
        /// <summary>The Requirement's key.</summary>
        public string Key = "";
        /// <summary>The planet of the Sun whose orbit the telescope is placed near.</summary>
        public string Planet = "";
    }

    /// <summary>
    /// #1035: Contracts that complete after the Mission (ADR 0021: Core's kind table; spec #1021 decision 12). Claudia plans the part she
    /// can fly, the placement: for a Sentinel or comet-detection Contract, a Sentinel telescope (with an antenna and a power source) in solar
    /// orbit near the planet's orbit the Contract names, Kerbin's for comets; for a deployed-science Contract, a landing on the body and a
    /// Manual stretch to set up the station on EVA. The game then completes the time-based Requirement itself, after the Mission: the
    /// telescope maps its asteroids or comets, the station collects its share of the science. Each type has a wait estimate
    /// (<see cref="SentinelDaysPerAsteroid"/>, <see cref="DaysPerComet"/>, <see cref="DeployedDaysPerPercent"/>), provisional until the
    /// playtest confirms them; a Contract that doesn't give what its estimate needs (the count, the share) is "not yet".
    /// </summary>
    public static class PlacementKinds
    {
        public const string Sentinel = "SentinelContract";
        public const string CometDetection = "CometDetectionContract";
        public const string DeployedScience = "DeployedScienceContract";

        /// <summary>The SENTINEL Infrared Telescope's part name.</summary>
        public const string TelescopePart = "InfraredTelescope";
        /// <summary>The telescope as the player knows it.</summary>
        public const string TelescopeTitle = "SENTINEL Infrared Telescope";

        /// <summary>Provisional (#1035): Kerbin days a Sentinel telescope takes to map one asteroid. No public source gives the rate; the playtest checks it.</summary>
        public const double SentinelDaysPerAsteroid = 10;
        /// <summary>Provisional (#1035): Kerbin days a Sentinel telescope takes to detect one comet, rarer than asteroids.</summary>
        public const double DaysPerComet = 30;
        /// <summary>Provisional (#1035): Kerbin days a deployed science station takes per percent of its subject's science.</summary>
        public const double DeployedDaysPerPercent = 1;

        /// <summary>#1245: the Adapter's tag on every Breaking Ground deployable science part: experiments, the Central Station, the ground solar panel.</summary>
        public const string DeployableTag = "deployable_science";
        /// <summary>#1245: the tag on a deployable experiment.</summary>
        public const string ExperimentTag = "deployable_experiment";
        /// <summary>#1245: the tag on a Central Station, the controller a deployed station's experiments report to.</summary>
        public const string StationTag = "deployable_station";

        public const string NoDeployableScience = "this craft can't: no deployable science aboard";
        public const string NoKerbalForStation ="this craft can't: setting up a deployed science station needs a kerbal aboard to go on EVA";
        /// <summary>The telescope's burn is priced from low orbit round home, so a craft elsewhere can't place one yet.</summary>
        public const string AwayNotYet = "not yet: a Sentinel telescope is placed from Kerbin orbit only";
        public const string HomeStationNotYet = "not yet: deployed science on Kerbin isn't planned yet";

        /// <summary>The stock moons by the planet of the Sun they go round with.</summary>
        private static readonly Dictionary<string, string> MoonPlanets = new Dictionary<string, string>
        {
            { "Mun", "Kerbin" }, { "Minmus", "Kerbin" }, { "Gilly", "Eve" }, { "Ike", "Duna" },
            { "Laythe", "Jool" }, { "Vall", "Jool" }, { "Tylo", "Jool" }, { "Bop", "Jool" }, { "Pol", "Jool" },
        };

        public static bool Plans(string kind)
        {
            return kind == Sentinel || kind == CometDetection || kind == DeployedScience;
        }

        internal static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            if (contract.Kind == Sentinel && requirement.Kind == "SentinelParameter") return ReadSentinel(one, requirement, home);
            if (contract.Kind == CometDetection && requirement.Kind == "CometDetectionParameter") return ReadComets(one, requirement, home);
            if (contract.Kind == DeployedScience && requirement.Kind == "CollectDeployedScience") return ReadStation(one, requirement, home);
            return ContractKinds.NotYet(one, "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet");
        }

        private static ReadRequirement ReadSentinel(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var focus = BodyOf(requirement, "FocusBody", "focusBody", "targetBody", "body");
            if (focus == null || focus == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            var count = Count(requirement, "RemainingDiscoveries", "TotalDiscoveries", "discoveryCount", "count");
            if (!count.HasValue) return ContractKinds.NotYet(one, "not yet: the Sentinel Contract names no count of asteroids, so the wait after the Mission can't be estimated");
            string planet;
            if (!MoonPlanets.TryGetValue(focus, out planet)) planet = focus;
            Telescope(one, requirement, planet, home);
            one.AfterMission = Wait(count.Value * SentinelDaysPerAsteroid, "the telescope maps " + count.Value + " asteroid" + (count.Value == 1 ? "" : "s") + " near " + ContractKinds.Named(focus));
            return one;
        }

        private static ReadRequirement ReadComets(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var count = Count(requirement, "count", "Count", "cometCount", "RemainingDiscoveries", "TotalDiscoveries");
            if (!count.HasValue) return ContractKinds.NotYet(one, "not yet: the comet-detection Contract names no count of comets, so the wait after the Mission can't be estimated");
            Telescope(one, requirement, home, home);
            one.AfterMission = Wait(count.Value * DaysPerComet, "the telescope detects " + count.Value + " comet" + (count.Value == 1 ? "" : "s"));
            return one;
        }

        private static ReadRequirement ReadStation(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var body = BodyOf(requirement, "targetBody", "body", "TargetBody");
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            if (body == home) return ContractKinds.NotYet(one, HomeStationNotYet);
            var percent = new[] { "percentage", "SciencePercentage", "percent", "sciencePercentage" }.Select(n => ContractKinds.Number(requirement, n)).FirstOrDefault(v => v.HasValue && v.Value > 0);
            if (!percent.HasValue) return ContractKinds.NotYet(one, "not yet: the deployed-science Contract names no share of science to collect, so the wait after the Mission can't be estimated");
            one.Manual = "set up the deployed science station on EVA";
            ContractKinds.Visit(one, body, ContractReach.Land, "set up a deployed science station on " + ContractKinds.Named(body), home);
            one.AfterMission = Wait(percent.Value * DeployedDaysPerPercent, "the station collects " + Whole(percent.Value) + "% of its subject's science");
            return one;
        }

        /// <summary>The placement of a Sentinel telescope near <paramref name="planet"/>'s solar orbit: the launch to low orbit round home, then the telescope's own burn.</summary>
        private static void Telescope(ReadRequirement one, ContractRequirement requirement, string planet, string home)
        {
            one.Body = home;
            one.Reach = ContractReach.Orbit;
            one.Phase = "Solar orbit near " + ContractKinds.Named(planet);
            one.Words = "place a Sentinel telescope in solar orbit near " + ContractKinds.Named(planet) + "'s orbit";
            one.Placement = new SolarPlacement { Key = requirement.Key, Planet = planet };
            one.ChildMay = true;
            one.Vessel = new VesselNeeds
            {
                Noun = "Sentinel probe", Tags = new List<string> { "antenna", "electric_generator" }, Parts = new List<string> { TelescopePart }, ChildMay = true,
            };
        }

        private static MissionWait Wait(double days, string what)
        {
            return new MissionWait { Seconds = days * KerbinTime.SecondsPerDay, Words = what + ", about " + Whole(days) + " days" };
        }

        /// <summary>
        /// "not yet: …" when the telescope's placement can't be priced from where the craft is: the craft is away from home (the burn is
        /// priced from low orbit round it), or the planet isn't in the Game Snapshot's body table; "" otherwise.
        /// </summary>
        internal static string Unpriced(GameSnapshot snapshot, SolarPlacement placement, string home)
        {
            var at = snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
            if (at != home) return AwayNotYet;
            return snapshot.Bodies.Any(b => b.Name == placement.Planet) ? "" : "not yet: the Contract's body isn't one Claudia knows";
        }

        /// <summary>
        /// The telescope's burn from low orbit round <paramref name="home"/> into solar orbit near <paramref name="placement"/>'s planet, m/s,
        /// with the time it takes, s: near home's own orbit, the escape from low orbit (the coast out of the sphere of influence); near another
        /// planet's, the Hohmann departure and plane change and the circularisation at its orbit, with no capture.
        /// </summary>
        internal static double Price(SolarPlacement placement, SnapshotBody home, DeltaVMap map, double transferSeconds, out double seconds)
        {
            seconds = 0;
            if (placement.Planet == home.Name)
            {
                var low = DeltaVMap.LowOrbit(home);
                var circular = Math.Sqrt(home.Gm / low);
                if (home.SoiRadius > low)
                {
                    var a = (low + home.SoiRadius) / 2;
                    seconds = Math.PI * Math.Sqrt(a * a * a / home.Gm);
                }
                return circular * (Math.Sqrt(2) - 1);
            }
            var trip = map.Trip(home.Name, placement.Planet);
            if (trip == null) return 0;
            seconds = transferSeconds;
            return trip.Departure + trip.PlaneChange + trip.ArrivalExcess;
        }

        /// <summary>
        /// #1244: how near a telescope's solar orbit must come to the planet's, as a share of the planet's orbit radius: the planet's orbit
        /// lies between the telescope's periapsis less this share and its apoapsis plus it. Provisional, as the game's own test isn't public.
        /// </summary>
        public const double NearShare = 0.1;

        /// <summary>
        /// #1244: why a placed Requirement's placement isn't made, by the snapshot (the game marks no Requirement for it): no vessel carrying
        /// the telescope (the craft or a released one) in solar orbit near the planet's orbit, or no Central Station with an experiment landed
        /// on the body. Null when the placement is made, or <paramref name="requirement"/> isn't placed.
        /// </summary>
        public static string Unplaced(GameSnapshot snapshot, ChosenRequirement requirement)
        {
            if (requirement.AfterMission == null || snapshot == null) return null;
            if (requirement.Placement != null)
                return TelescopesOf(snapshot).Any(t => NearOrbit(snapshot, t, requirement.Placement.Planet)) ? null
                    : "no " + TelescopeTitle + " is in solar orbit near " + ContractKinds.Named(requirement.Placement.Planet) + "'s orbit";
            var landed = snapshot.DeployedScience.Where(p => p.Body == requirement.Body && p.Situation == "LANDED").ToList();
            return landed.Any(p => p.Station) && landed.Any(p => !p.Station) ? null : "no deployed science station is set up on " + ContractKinds.Named(requirement.Body);
        }

        /// <summary>The periapsis and apoapsis altitudes of each vessel carrying a telescope round the Sun: the craft and the released ones.</summary>
        private static IEnumerable<double[]> TelescopesOf(GameSnapshot snapshot)
        {
            if (snapshot.Orbit != null && snapshot.Orbit.Body == "Sun" && snapshot.Craft.Parts.Any(p => p.Name == TelescopePart))
                yield return new[] { snapshot.Orbit.Periapsis, snapshot.Orbit.Apoapsis };
            foreach (var vessel in snapshot.Vessels.Where(v => v.CarriesTelescope && v.Body == "Sun" && v.Situation == "ORBITING" && v.PeriapsisM.HasValue && v.ApoapsisM.HasValue))
                yield return new[] { vessel.PeriapsisM.Value, vessel.ApoapsisM.Value };
        }

        private static bool NearOrbit(GameSnapshot snapshot, double[] altitudes, string planet)
        {
            var sun = snapshot.Bodies.FirstOrDefault(b => b.Name == "Sun");
            var at = snapshot.Bodies.FirstOrDefault(b => b.Name == planet);
            if (sun == null || at == null || at.Sma <= 0) return false;
            var near = at.Sma * NearShare;
            return altitudes[0] + sun.Radius - near <= at.Sma && at.Sma <= altitudes[1] + sun.Radius + near;
        }

        /// <summary>
        /// "this craft can't: …" when a deployed science station's Manual stretch can't be flown: the craft carries no Central Station or no
        /// deployable experiment (the parts the Adapter tags, #1245), or has no kerbal to go on EVA; "" otherwise.
        /// </summary>
        internal static string Lacks(GameSnapshot snapshot)
        {
            var parts = snapshot.Craft.Parts;
            if (!parts.Any(p => p.Tags.Contains(StationTag)) || !parts.Any(p => p.Tags.Contains(ExperimentTag))) return NoDeployableScience;
            return snapshot.Crew.Count > 0 ? "" : NoKerbalForStation;
        }

        private static int? Count(ContractRequirement requirement, params string[] names)
        {
            var count = names.Select(n => ContractKinds.Number(requirement, n)).FirstOrDefault(v => v.HasValue && v.Value >= 1);
            return count.HasValue ? (int)Math.Round(count.Value) : (int?)null;
        }

        /// <summary>The body a setting names, by its index in the game's body list or by name; null when none or unknown.</summary>
        private static string BodyOf(ContractRequirement requirement, params string[] names)
        {
            var value = names.Select(n => ContractKinds.Setting(requirement, n)).FirstOrDefault(v => v.Length > 0);
            if (value == null) return null;
            return ContractKinds.BodyAt(value) ?? ContractKinds.StockBody(value);
        }

        private static string Whole(double value)
        {
            return Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
