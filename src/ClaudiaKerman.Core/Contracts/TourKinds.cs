// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Contracts
{
    /// <summary>
    /// #1034: tourism and Grand Tour Contracts (ADR 0021: Core's kind table). A tourism Contract is one tour per tourist: each destination
    /// it lists (a body and a flyby, orbit, landing or suborbital hop) is a visit Claudia plans, and the tour itself is met when the tourist
    /// is brought home, so it is a return from the furthest of them. The tourist must be aboard, in a seat (<see cref="Lacks"/>). The high-gee
    /// variant, where the tourist must black out from g-force, is "not yet". A Grand Tour is one craft visiting every body it lists: each is
    /// a visit like any other, never a Child Vessel's, so the whole route priced as one Mission is what judges whether this craft reaches
    /// them all.
    /// </summary>
    internal static class TourKinds
    {
        public const string Tourism = "TourismContract";
        public const string GrandTour = "GrandTour";
        private const string TourKind = "KerbalTourParameter";
        private const string DestinationKind = "KerbalDestinationParameter";
        private const string GeeKind = "KerbalGeeAdventureParameter";
        private const string VesselDestinationKind = "VesselDestinationParameter";

        public const string GeeNotYet = "not yet: high-gee tourism (the tourist must black out from g-force) isn't planned yet";

        public static bool Plans(string kind)
        {
            return kind == Tourism || kind == GrandTour;
        }

        /// <summary>A tourist's tour: read whole, its destinations and the trip home, by <see cref="ReadTour"/>.</summary>
        public static bool IsTour(SnapshotContract contract, ContractRequirement requirement)
        {
            return contract.Kind == Tourism && requirement.Kind == TourKind;
        }

        /// <summary>One leaf of a tourism or Grand Tour Contract: a destination, or why it is "not yet".</summary>
        public static ReadRequirement Read(ReadRequirement one, SnapshotContract contract, ContractRequirement requirement, string home)
        {
            if (requirement.Kind == GeeKind) return ContractKinds.NotYet(one, GeeNotYet);
            if ((contract.Kind == Tourism && requirement.Kind == DestinationKind) || (contract.Kind == GrandTour && requirement.Kind == VesselDestinationKind))
                return Destination(one, requirement, home);
            return ContractKinds.NotYet(one, "not yet: " + requirement.Kind + " isn't a Contract Requirement Claudia can plan yet");
        }

        /// <summary>
        /// A tourist's tour: each destination still to visit, then the tour itself, met when the tourist is home again: a return from the
        /// furthest destination (an away body before home, the deepest reach), or from the launch when every destination is met already.
        /// </summary>
        public static List<ReadRequirement> ReadTour(SnapshotContract contract, ContractRequirement tour, string home)
        {
            var read = tour.Requirements.Where(r => r.State != "Complete").Select(r => Read(new ReadRequirement { Requirement = r }, contract, r, home)).ToList();
            var name = ContractKinds.Setting(tour, "kerbalName");
            var back = new ReadRequirement { Requirement = tour, Tourist = name, Return = true, Phase = "Return" };
            back.Words = name.Length > 0 ? "bring tourist " + name + " home" : "bring the tourist home";
            var furthest = read.Where(r => r.Plannable).OrderBy(r => r.Body == home ? 0 : 1).ThenBy(r => r.Reach).LastOrDefault();
            var notYet = read.FirstOrDefault(r => !r.Plannable && !r.Requirement.Optional);
            if (furthest != null)
            {
                back.Body = furthest.Body;
                back.Reach = furthest.Reach;
            }
            else if (notYet != null) back.NotYet = notYet.NotYet;
            else
            {
                back.Body = home;
                back.Reach = ContractReach.Hop;
            }
            read.Add(back);
            return read;
        }

        /// <summary>
        /// A destination, as a tourist's or a Grand Tour's saved settings name it: <c>targetBody</c> by its index in the game's body list,
        /// <c>targetType</c> the flight log's entry. Away from home a flyby, orbit or landing is that visit; at home an orbit is the launch to
        /// orbit and a suborbital trip is a hop out of the air, met on the way up. Anything else is "not yet".
        /// </summary>
        private static ReadRequirement Destination(ReadRequirement one, ContractRequirement requirement, string home)
        {
            var index = ContractKinds.Setting(requirement, "targetBody");
            var body = ContractKinds.BodyAt(index);
            if (body == null || body == "Sun") return ContractKinds.NotYet(one, "not yet: the Contract's body isn't one Claudia knows");
            var type = ContractKinds.Setting(requirement, "targetType").ToUpperInvariant();
            if (type == "SUBORBIT")
            {
                if (body != home) return ContractKinds.NotYet(one, ContractKinds.AwayHopNotYet);
                return ContractKinds.Hop(one, home, new HopBand { AboveAir = true });
            }
            ContractReach reach;
            if (type == "FLYBY") reach = ContractReach.Flyby;
            else if (type == "ORBIT") reach = ContractReach.Orbit;
            else if (type == "LAND" || type == "LANDING") reach = ContractReach.Land;
            else return ContractKinds.NotYet(one, "not yet: " + (type.Length > 0 ? "a " + type.ToLowerInvariant() + " destination" : "an unnamed destination") + " isn't planned yet");
            if (body == home && reach != ContractReach.Orbit) return ContractKinds.NotYet(one, "not yet: " + ContractKinds.Verb(reach) + " " + home + " isn't planned yet");
            return ContractKinds.Visit(one, body, reach, ContractKinds.Verb(reach) + " " + ContractKinds.Named(body), home);
        }

        /// <summary>
        /// "this craft can't: …" when the tourist the tour names isn't aboard: "no seat for tourist …" when the craft has no free seat for
        /// them, else "tourist … isn't aboard"; "" when they are.
        /// </summary>
        public static string Lacks(GameSnapshot snapshot, ReadRequirement tour)
        {
            var aboard = tour.Tourist.Length > 0
                ? snapshot.Crew.Any(c => c.Name == tour.Tourist)
                : snapshot.Crew.Any(c => c.Trait == "Tourist");
            if (aboard) return "";
            var who = tour.Tourist.Length > 0 ? "tourist " + tour.Tourist : "the tourist";
            return snapshot.FreeSeats == 0 ? "this craft can't: no seat for " + who : "this craft can't: " + who + " isn't aboard";
        }
    }
}
