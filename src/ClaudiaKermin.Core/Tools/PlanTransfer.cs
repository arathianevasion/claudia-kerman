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
    /// <c>target_pe</c>); an orbit given by its apsides and plane is reached anywhere on it (an intercept). Another planet of the
    /// same star gets its Transfer Windows instead (<see cref="TransferWindows"/>, #458): Soonest, Optimal and the Mission
    /// Setting's pick, costed from the parking orbit (the Delta-v Map's low orbit when the craft has none yet). A moon of the craft's
    /// planet that is cheaper to reach later (Minmus near a node, #647) gets its Soonest and Optimal too, under <c>windows</c>, or as
    /// the whole answer from the pad, where there is no orbit to plan the burn from. A separate tool rather
    /// than a <c>solve_burn</c> goal, because its inputs (a target, an orbit, capture) share nothing with that tool's; not
    /// named <c>transfer</c>, which reads as the block <c>transfer_to</c> and is part of that name in the cached block summaries.
    /// </summary>
    internal sealed class PlanTransfer : IPlanningTool
    {
        public const string Model = "two impulsive burns on a Lambert arc from this turn's Game Snapshot, the next window; to a body, the departure "
            + "is sized on patched conics to arrive at target_pe and the arrival is the burn that circularizes there";

        public const string WindowModel = "Lambert arcs between the planets round the star, the ejection from the parking orbit's periapsis and the capture "
            + "circular at target_pe; soonest is the first departure within the Craft Check margin of optimal, the cheapest in a synodic period";

        public const string MoonWindowModel = "Lambert arcs round the planet from the parking orbit's plane, the plane change where the arc is slow, the capture "
            + "circular at target_pe; soonest is the first departure within the Craft Check margin of optimal, the cheapest in one orbit of the moon";

        public string Name
        {
            get { return "plan_transfer"; }
        }

        public string Description
        {
            get
            {
                // A planet's windows (#458) were paid for by shorter wording, at the same 457 characters (prefix cost). A planet gets
                // dates and delta-v, not a burn (#670); when #459's transfer_to flies to planets, point a planet at it here.
                return "The cheapest two-burn transfer in the next window, from this turn's Game Snapshot: to target (a moon of the "
                    + "craft's body, or a planet) at target_pe (m), or to orbit (ap, pe m; inc, lan deg) anywhere on it. Gives the departure burn "
                    + "(maneuver-node components) and time and the arrival burn; a planet: soonest and optimal dates and delta-v, no burn. "
                    + "capture false minimises the departure alone. Its block is transfer_to. Don't call it for other planets' moons.";
            }
        }

        public string InputSchemaJson
        {
            get
            {
                // The fields are explained in the description: the tools list rides on every Mission request (prompt cost, #212).
                return "{\"type\":\"object\",\"properties\":{"
                    + "\"target\":{\"type\":\"string\"},\"target_pe\":{\"type\":\"number\"},"
                    + "\"orbit\":{\"type\":\"object\",\"properties\":{\"ap\":{\"type\":\"number\"},\"pe\":{\"type\":\"number\"},\"inc\":{\"type\":\"number\"},\"lan\":{\"type\":\"number\"}},"
                    + "\"required\":[\"ap\",\"pe\"],\"additionalProperties\":false},"
                    + "\"capture\":{\"type\":\"boolean\"}"
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
            if (target != null && TransferWindows.IsPlanetTransfer(snapshot, target))
            {
                var capture = JsonNode.Bool(input["capture"]) ?? true;
                return Windows(TransferWindows.Find(snapshot, target, targetPe.Value, capture, snapshot.MarginPercent), snapshot, capture);
            }
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
            var moon = MoonWindows(snapshot, target, ask);
            if (plan.Error != null) return moon != null ? Windows(moon, snapshot, ask.Capture) : PlanningJson.Error(plan.Error);

            var now = snapshot.Ut;
            var name = target ?? "the orbit";
            var json = new JsonWriter().BeginObject().Field("model", Model).Field("target", name)
                .Field("mode", orbit != null ? "intercept" : "rendezvous").Field("capture", ask.Capture);
            PlanningJson.Time(json, "depart", plan.Ut, now);
            json.Name("dv").BeginObject()
                .Field("prograde", PlanningJson.Round(plan.Prograde, 2)).Field("normal", PlanningJson.Round(plan.Normal, 2)).Field("radial", PlanningJson.Round(plan.Radial, 2))
                .EndObject()
                .Field("departure_dv", PlanningJson.Round(plan.DeltaV, 1));
            // A broken-plane plan (#619): its plane change, in the result alone so the description stays as it was (#565).
            if (plan.PlaneUt.HasValue)
            {
                PlanningJson.Time(json, "plane", plan.PlaneUt.Value, now);
                json.Field("plane_dv", PlanningJson.Round(plan.PlaneDv, 1));
            }
            json.Field("arrival_dv", PlanningJson.Round(plan.ArrivalDv, 1))
                .Field("total_dv", PlanningJson.Round(plan.Total(true), 1));
            if (orbit == null)
            {
                PlanningJson.Time(json, "encounter", plan.EncounterUt, now);
                json.Field("target_pe_actual", PlanningJson.Round(plan.PeActual, 0));
            }
            PlanningJson.Time(json, "arrive", plan.ArrivalUt, now);
            json.Field("synodic_s", PlanningJson.Round(plan.Synodic, 0));
            if (plan.CheapestUt.HasValue)
                json.Field("note", "not the cheapest window: the one at UT " + Num(plan.CheapestUt.Value) + " can't be aimed at target_pe on the patched conics; this is the first that can");
            if (moon != null)
            {
                json.Name("windows").BeginObject();
                Choice(json, moon, snapshot);
                json.EndObject();
            }
            json.EndObject();
            return new ToolOutcome
            {
                Json = json.ToString(),
                Summary = name + ": " + Num(plan.DeltaV) + " m/s out at UT " + Num(plan.Ut)
                    + (plan.PlaneUt.HasValue ? ", " + Num(plan.PlaneDv) + " m/s plane change at UT " + Num(plan.PlaneUt.Value) : "")
                    + ", " + Num(plan.ArrivalDv) + " m/s on arrival",
            };
        }

        /// <summary>
        /// A moon's Transfer Windows (#647) when Optimal saves anything over Soonest; null otherwise (the Mun, in the parking plane,
        /// costs the same at any departure), so a moon in the plane adds nothing to the answer.
        /// </summary>
        private static WindowSearch MoonWindows(GameSnapshot snapshot, string target, TransferAsk ask)
        {
            if (target == null || !TransferWindows.IsMoonTransfer(snapshot, target)) return null;
            var found = TransferWindows.Find(snapshot, target, ask.TargetPe, ask.Capture, snapshot.MarginPercent);
            return found.Error == null && found.Optimal != found.Soonest ? found : null;
        }

        /// <summary>A planet's Transfer Windows (#458), or a moon's from the pad (#647): Soonest and Optimal, and the one the Mission Setting Time versus delta-v picks.</summary>
        private static ToolOutcome Windows(WindowSearch found, GameSnapshot snapshot, bool capture)
        {
            if (found.Error != null) return PlanningJson.Error(found.Error);
            var pick = TransferWindows.Pick(found, snapshot.TimeVersusDeltaV);
            var json = new JsonWriter().BeginObject().Field("model", found.Moon ? MoonWindowModel : WindowModel).Field("target", found.Target).Field("capture", capture);
            // Until #459's transfer_to flies to planets (#670), in a planet's answer alone; #459 removes it with the plan check's guard.
            if (!found.Moon) json.Field("decline", "no Building Block flies to a planet yet: hand in a Mission Plan with no sequences");
            json.Field("parking_pe", PlanningJson.Round(found.ParkingPe, 0));
            if (found.ParkingAssumed) json.Field("parking_note", "assumed: the craft is not in a closed orbit clear of the atmosphere");
            if (!found.Moon && !found.Fits)
            {
                // No window fits (#648): the cheapest total against what the craft has, in the result alone (the description is unchanged).
                json.Field("fits", false).Field("cheapest_total_dv", PlanningJson.Round(found.Optimal.TotalDv, 1)).Field("craft_dv", PlanningJson.Round(found.CraftDv.Value, 1));
                if (found.LaterLegsDv.HasValue)
                    json.Field("later_legs_dv", PlanningJson.Round(found.LaterLegsDv.Value, 1)).Field("available_dv", PlanningJson.Round(found.CraftDv.Value - found.LaterLegsDv.Value, 1));
                var later = found.LaterLegsDv.HasValue ? ", " + Num(found.LaterLegsDv.Value) + " of it for the later legs" : "";
                json.Field("note", "no window in the next synodic period fits: the cheapest costs more than the craft has" + (found.LaterLegsDv.HasValue ? " after the Mission Plan's later legs" : ""));
                return new ToolOutcome
                {
                    Json = json.EndObject().ToString(),
                    Summary = found.Target + ": no window fits, the cheapest " + Num(found.Optimal.TotalDv) + " m/s, the craft has " + Num(found.CraftDv.Value) + " m/s" + later,
                };
            }
            if (found.Moon)
            {
                Choice(json, found, snapshot);
                return new ToolOutcome { Json = json.EndObject().ToString(), Summary = Summary(found, pick) };
            }
            if (found.CraftDv.HasValue) json.Field("fits", true).Field("craft_dv", PlanningJson.Round(found.CraftDv.Value, 1));
            if (found.CraftDv.HasValue && found.LaterLegsDv.HasValue)
                json.Field("later_legs_dv", PlanningJson.Round(found.LaterLegsDv.Value, 1)).Field("available_dv", PlanningJson.Round(found.CraftDv.Value - found.LaterLegsDv.Value, 1));
            Window(json, "soonest", found.Soonest, snapshot.Ut, capture);
            Window(json, "optimal", found.Optimal, snapshot.Ut, capture);
            json.Field("pick", pick == found.Optimal && pick != found.Soonest ? "optimal" : "soonest")
                .Field("setting", snapshot.TimeVersusDeltaV == TimeVersusDeltaV.Fastest ? "Fastest" : snapshot.TimeVersusDeltaV == TimeVersusDeltaV.MostEfficient ? "Most efficient" : "Balanced")
                .Field("synodic_s", PlanningJson.Round(found.Synodic, 0))
                .EndObject();
            return new ToolOutcome { Json = json.ToString(), Summary = Summary(found, pick) };
        }

        private static string Summary(WindowSearch found, TransferWindow pick)
        {
            return found.Target + " window: " + Num(pick.TotalDv) + " m/s, leaving at UT " + Num(pick.DepartureUt);
        }

        /// <summary>A moon's Soonest and Optimal, each its departure and total, and the setting's pick (#647).</summary>
        private static void Choice(JsonWriter json, WindowSearch found, GameSnapshot snapshot)
        {
            foreach (var w in new[] { found.Soonest, found.Optimal })
            {
                json.Name(w == found.Soonest ? "soonest" : "optimal").BeginObject();
                PlanningJson.Time(json, "depart", w.DepartureUt, snapshot.Ut);
                json.Field("total_dv", PlanningJson.Round(w.TotalDv, 1)).EndObject();
            }
            var pick = TransferWindows.Pick(found, snapshot.TimeVersusDeltaV);
            json.Field("pick", pick == found.Optimal && pick != found.Soonest ? "optimal" : "soonest");
        }

        private static void Window(JsonWriter json, string name, TransferWindow w, double now, bool capture)
        {
            json.Name(name).BeginObject();
            PlanningJson.Time(json, "depart", w.DepartureUt, now);
            json.Field("arrive_ut", PlanningJson.Round(w.ArrivalUt, 0))
                .Field("ejection_dv", PlanningJson.Round(w.EjectionDv, 1))
                .Field("arrival_v_inf", PlanningJson.Round(w.ArrivalVInf, 1));
            if (capture) json.Field("capture_dv", PlanningJson.Round(w.CaptureDv, 1));
            json.Field("total_dv", PlanningJson.Round(w.TotalDv, 1)).EndObject();
        }

        private static string Num(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
