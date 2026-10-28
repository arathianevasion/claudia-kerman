// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// <c>plan_transfer</c>: the cheapest two-burn transfer from the craft's orbit (<see cref="Transfer"/>), as <c>transfer_to</c>
    /// would find it, so Claudia can budget the departure and the capture. A body is met where it is (a rendezvous, arriving at
    /// <c>target_pe</c>); an orbit given by its apsides and plane is reached anywhere on it (an intercept). A separate tool rather
    /// than a <c>solve_burn</c> goal, because its inputs (a target, an orbit, capture) share nothing with that tool's; not
    /// named <c>transfer</c>, which reads as the block <c>transfer_to</c> and is part of that name in the cached block summaries.
    /// </summary>
    internal sealed class PlanTransfer : IPlanningTool
    {
        public const string Model = "two impulsive burns on a Lambert arc from this turn's Game Snapshot, the next window; to a body, the departure "
            + "is sized on patched conics to arrive at target_pe and the arrival is the burn that circularizes there";

        public string Name
        {
            get { return "plan_transfer"; }
        }

        public string Description
        {
            get
            {
                return "The cheapest two-burn transfer from the craft's orbit in the next window, worked out from this turn's Game Snapshot: "
                    + "to a body that orbits the craft's body (target, arriving at target_pe), or to an orbit round it (orbit, reached "
                    + "anywhere on it). It gives the departure burn in maneuver-node components (prograde, normal, radial m/s) with its time, "
                    + "the arrival burn (at a body, the capture into a circular orbit at target_pe) and their total; capture false finds the "
                    + "cheapest departure alone. Its block is transfer_to: call it for the delta-v to budget a transfer and its capture "
                    + "instead of working them out yourself, then pass the block the target and target_pe, not the burn. Don't call it for "
                    + "a body round another planet, or to check a burn you already have (use predict_burn).";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"target\":{\"type\":\"string\",\"description\":\"A body orbiting the craft's body.\"},"
                    + "\"target_pe\":{\"type\":\"number\",\"description\":\"With target: the periapsis altitude to arrive at, m.\"},"
                    + "\"orbit\":{\"type\":\"object\",\"properties\":{\"ap\":{\"type\":\"number\"},\"pe\":{\"type\":\"number\"},\"inc\":{\"type\":\"number\"},\"lan\":{\"type\":\"number\"}},"
                    + "\"required\":[\"ap\",\"pe\"],\"additionalProperties\":false,\"description\":\"Instead of target: an orbit round the craft's body, m above sea level and degrees (inc, lan default 0).\"},"
                    + "\"capture\":{\"type\":\"boolean\",\"description\":\"Count the arrival burn in the cost. Default true.\"}"
                    + "},\"additionalProperties\":false}";
            }
        }

        public bool OnCraftCheck
        {
            get { return false; }
        }

        public string Activity
        {
            get { return "planning the transfer"; }
        }

        public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
        {
            var target = JsonNode.Str(input["target"]);
            var targetPe = JsonNode.Double(input["target_pe"]);
            var orbit = input["orbit"];
            if (string.IsNullOrEmpty(target) == (orbit == null)) return PlanningJson.Error("give target (a body) or orbit, one of them");
            if (target != null && !targetPe.HasValue) return PlanningJson.Error("target needs target_pe, the periapsis altitude to arrive at in metres");
            if (orbit != null && targetPe.HasValue) return PlanningJson.Error("target_pe is for a target body; an orbit is reached at its own altitude");
            var ask = new TransferAsk
            {
                Target = target,
                TargetPe = targetPe ?? 0,
                Capture = JsonNode.Bool(input["capture"]) ?? true,
                EarliestUt = snapshot.Ut,
            };
            if (orbit != null)
            {
                var home = new BodySystem(snapshot.Bodies).Find(snapshot.Orbit != null && !string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Orbit.Body : snapshot.Body);
                if (home == null) return PlanningJson.Error("the body table has no body for the craft's orbit");
                double ap = JsonNode.Double(orbit["ap"]).Value, pe = JsonNode.Double(orbit["pe"]).Value;
                if (ap < pe) return PlanningJson.Error("orbit.ap must be at least orbit.pe");
                double ra = home.Radius + ap, rp = home.Radius + pe;
                ask.Mode = TransferMode.Intercept;
                // Periapsis at the ascending node; an intercept reaches the orbit anywhere, so where the target is on it doesn't count.
                ask.TargetOrbit = new SnapshotOrbit
                {
                    SemiMajorAxis = (ra + rp) / 2, Eccentricity = (ra - rp) / (ra + rp), Inclination = JsonNode.Double(orbit["inc"]) ?? 0,
                    Lan = JsonNode.Double(orbit["lan"]) ?? 0, Epoch = snapshot.Ut,
                };
            }
            var plan = Transfer.Find(snapshot, ask);
            if (plan.Error != null) return PlanningJson.Error(plan.Error);

            var now = snapshot.Ut;
            var name = target ?? "the orbit";
            var json = new JsonWriter().BeginObject().Field("model", Model).Field("target", name)
                .Field("mode", orbit != null ? "intercept" : "rendezvous").Field("capture", ask.Capture);
            PlanningJson.Time(json, "depart", plan.Ut, now);
            json.Name("dv").BeginObject()
                .Field("prograde", PlanningJson.Round(plan.Prograde, 2)).Field("normal", PlanningJson.Round(plan.Normal, 2)).Field("radial", PlanningJson.Round(plan.Radial, 2))
                .EndObject()
                .Field("departure_dv", PlanningJson.Round(plan.DeltaV, 1))
                .Field("arrival_dv", PlanningJson.Round(plan.ArrivalDv, 1))
                .Field("total_dv", PlanningJson.Round(plan.DeltaV + plan.ArrivalDv, 1));
            if (orbit == null)
            {
                PlanningJson.Time(json, "encounter", plan.EncounterUt, now);
                json.Field("target_pe_actual", PlanningJson.Round(plan.PeActual, 0));
            }
            PlanningJson.Time(json, "arrive", plan.ArrivalUt, now);
            json.Field("synodic_s", PlanningJson.Round(plan.Synodic, 0)).EndObject();
            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = name + ": " + Num(plan.DeltaV) + " m/s out at UT " + Num(plan.Ut) + ", " + Num(plan.ArrivalDv) + " m/s on arrival",
            };
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
