// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Contracts;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests.Contracts
{
    /// <summary>
    /// #1243, at #1025's seam (the Mission in Core, driven by Game Snapshots with Contracts, Claudia stubbed): a satellite Requirement the
    /// plan serves from a Child Vessel's leg is judged by that Child Vessel's orbit, read from the snapshot's vessels, not by the craft's.
    /// </summary>
    public sealed class SatelliteChildVesselHoldTests : IDisposable
    {
        private const string Sonnet = "claude-sonnet-5-5";
        private const double EndedAt = 448444;
        private const string Guid = "0d5e8a10-1037-4a01-9f00-000000000005";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void AChildVesselInTheContractsOrbitHoldsWithTheCraftStillInItsParkingOrbit()
        {
            var transport = Transport();
            var mission = RunningOnTheProbe(transport, out var start);
            mission.Send(At(EndedAt, "Incomplete", ProbeAt(563000, 537000, 88)));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            mission.Send(At(EndedAt + 20, "Incomplete", ProbeAt(563000, 537000, 88)));
            _background.Drain();

            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.Contains(mission.View.Log, e => e.Text.StartsWith("Holding for the game to mark"));

            mission.Send(At(EndedAt + 25, "Complete", ProbeAt(563000, 537000, 88)));
            DrainUntil(mission, v => MissionRequests(transport) >= 4);
            var debrief = DebriefText(transport);
            Assert.DoesNotContain("outside the", debrief);
            Assert.DoesNotContain("\"state\":\"not met\"", debrief);
        }

        [Fact]
        public void AChildVesselOutsideTheWindowShowsItsOwnGapNotTheCrafts()
        {
            var transport = Transport();
            var mission = RunningOnTheProbe(transport, out var start);
            // The probe's plane is 12.4° off the 88.4° asked: 3.4° past the 5% window. The craft's 80 km equatorial orbit is far worse.
            mission.Send(At(EndedAt, "Incomplete", ProbeAt(563000, 537000, 76)));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => MissionRequests(transport) >= 4);

            var debrief = DebriefText(transport);
            // The probe's gap alone: nothing of the craft's 80 km orbit (apoapsis and periapsis far below the window).
            Assert.Contains("\"gap\":\"inclination 3.4° outside the 5% window\"", debrief);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Holding for the game to mark"));
        }

        [Fact]
        public void AChildVesselTheSnapshotDoesNotCarryIsAGapSaidOfItNotTheCraftsOrbit()
        {
            var transport = Transport();
            var mission = RunningOnTheProbe(transport, out var start);
            mission.Send(At(EndedAt, "Incomplete"));
            mission.Send(new KosRunEnded(Finished(), start.RunId, EndedAt));
            DrainUntil(mission, v => MissionRequests(transport) >= 4);

            var debrief = DebriefText(transport);
            Assert.Contains("no reading of the Child Vessel", debrief);
            Assert.DoesNotContain("outside the", debrief);
        }

        [Fact]
        public void ASatelliteFlownAsTheCraftItselfIsJudgedByTheCraftsOrbitAsBefore()
        {
            var snapshot = At(EndedAt, "Incomplete", ProbeAt(563000, 537000, 88));
            var requirement = ContractKinds.Read(snapshot.Contracts.Accepted[0], "Kerbin").Where(r => r.Orbit != null)
                .Select(r => new ChosenRequirement { Key = r.Requirement.Key, Words = r.Words, Body = r.Body, Reach = r.Reach, Orbit = r.Orbit, ChildMay = r.ChildMay }).Single();

            // The craft in its 80 km parking orbit: the probe in the snapshot's vessels changes nothing when no Child Vessel serves it.
            Assert.False(ContractWatch.ConditionsHold(snapshot, requirement, "Kerbin"));
            Assert.Contains("outside the 5% window", ContractWatch.Gap(snapshot, requirement));

            snapshot.Orbit = new SnapshotOrbit { Body = "Kerbin", Apoapsis = 563000, Periapsis = 537000, Inclination = 88 };
            Assert.True(ContractWatch.ConditionsHold(snapshot, requirement, "Kerbin"));
        }

        // ---- Helpers ----

        /// <summary>The released probe as the snapshot's vessels carry it: its GUID, and its orbit's altitudes (m) and inclination.</summary>
        private static SnapshotVessel ProbeAt(double apoapsis, double periapsis, double inclination)
        {
            return new SnapshotVessel { Id = 77, VesselId = "probe", Name = "Pad Test Probe", Body = "Kerbin", Situation = "ORBITING",
                ApoapsisM = apoapsis, PeriapsisM = periapsis, Inclination = inclination };
        }

        /// <summary>The mothership in its 80 km equatorial parking orbit with the satellite Contract, its orbit Requirement in <paramref name="state"/>.</summary>
        private static GameSnapshot At(double ut, string state, params SnapshotVessel[] vessels)
        {
            var snapshot = Mothership.WithProbe();
            snapshot.GameMode = "CAREER";
            // Enough delta-v for orbit and the satellite's climb: the pad craft alone has too little.
            foreach (var stage in snapshot.Stages) { stage.DeltaVVac += 6000; stage.DeltaVAsl += 6000; }
            snapshot.Contracts = new SnapshotContracts { Limit = 7 };
            // The probe makes its own power: the satellite Contract asks for a generator.
            snapshot.Craft.Parts.Single(p => p.Id == Mothership.ProbeCore).Tags.Add("electric_generator");
            var contract = ContractCheckTests.Fixture("SatelliteContract.json");
            contract.DeadlineUt = null;
            // Its other Requirements (a new probe with antenna and power, stable) are met: only the orbit is judged here.
            foreach (var requirement in contract.Requirements) requirement.State = requirement.Kind == "SpecificOrbitParameter" ? state : "Complete";
            snapshot.Contracts.Accepted.Add(contract);
            snapshot.Contracts.Active = 1;
            snapshot.Ut = ut;
            snapshot.Body = "Kerbin";
            snapshot.Situation = "ORBITING";
            snapshot.Orbit = new SnapshotOrbit { Body = "Kerbin", Apoapsis = 80000, Periapsis = 80000, Inclination = 0 };
            snapshot.Vessels.AddRange(vessels);
            return snapshot;
        }

        private static GameSnapshot OnPad()
        {
            var snapshot = Mothership.WithProbe();
            snapshot.GameMode = "CAREER";
            // Enough delta-v for orbit and the satellite's climb: the pad craft alone has too little.
            foreach (var stage in snapshot.Stages) { stage.DeltaVVac += 6000; stage.DeltaVAsl += 6000; }
            snapshot.Contracts = new SnapshotContracts { Limit = 7 };
            // The probe makes its own power: the satellite Contract asks for a generator.
            snapshot.Craft.Parts.Single(p => p.Id == Mothership.ProbeCore).Tags.Add("electric_generator");
            var contract = ContractCheckTests.Fixture("SatelliteContract.json");
            contract.DeadlineUt = null;
            snapshot.Contracts.Accepted.Add(contract);
            snapshot.Contracts.Active = 1;
            return snapshot;
        }

        /// <summary>One leg, flown by the probe the craft releases: it raises it into the Contract's orbit.</summary>
        private static string PlanJson()
        {
            return "{\"why\":\"Because.\",\"sequences\":["
                + "{\"name\":\"Raise the probe to its orbit\",\"intent\":\"Release the probe and raise its orbit to apoapsis 564 km, periapsis 536 km, inclination 88.4°.\",\"delta_v\":100,\"timing\":\"next\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"Probe\"}"
                + "],\"total_delta_v\":100,\"expected_duration\":\"short\",\"mission_time_s\":-1,\"new_goal\":\"\\\"\\\"\","
                + "\"child_vessels\":[{\"name\":\"Probe\",\"separation_stage\":2,\"vessel_id\":\"\"}],\"goal_steps\":[]}";
        }

        private static StreamingFakeTransport Transport()
        {
            return new StreamingFakeTransport(
                ToolUseStreams.ToolUse("plan", (ProposedPlans.ToolName, PlanJson())),
                ContractWatchTests.MissionPlanKind(),
                ContractWatchTests.Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: the probe.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                    + "\"sequence_plan\":{\"plan_step\":1,\"name\":\"Raise the probe to its orbit\",\"intent\":\"Fly it.\",\"why\":\"It is next.\",\"target\":\"done\",\"tolerances\":\"none\","
                    + "\"expected_delta_v\":10,\"expected_duration_s\":60,\"abort_conditions\":[],\"telemetry_checks\":[],\"risks\":[],"
                    + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"print \\\"go\\\".\\n\","
                    + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1,\"manual\":false,\"vessel\":\"Probe\"},\"debrief\":null}"),
                ScriptedResponse.Fixture("mission-debrief-next.sse"));
        }

        private MissionControl RunningOnTheProbe(StreamingFakeTransport transport, out StartScript start)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-fake-key-0123456789", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig { CheckDurationPercent = 1000 }, () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad());
            mission.Send(new StartMission("", 25m, Sonnet));
            Assert.True(mission.View.ContractCheck != null, "no card: " + string.Join(" / ", mission.View.Log.Select(e => e.Text)));
            var card = mission.View.ContractCheck;
            Assert.True(card.NotThisFlight.Count == 0, string.Join("; ", card.NotThisFlight.Select(n => n.Title + ": " + n.Reason)));
            mission.Send(new PlanContracts());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            mission.Send(new VesselsSeparated(CraftSeen(), ProbeSeen(), false));
            mission.Send(new VesselsSeen(new List<SeenVessel> { CraftSeen(), ProbeSeen() }));
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            start = mission.TakeCommands().OfType<StartScript>().Single();
            Assert.Equal("probe", mission.View.RunningChildVesselId);
            return mission;
        }

        private static SeenVessel CraftSeen()
        {
            var craft = new SeenVessel { Id = "craft", Name = "Pad Test", ClaudiaAboard = true, HasKosProcessor = true, Situation = "ORBITING", LinkedHome = true };
            craft.ProcessorPartIds.Add(3102);
            craft.PartTitles.AddRange(new[] { "Mk1 Command Pod", "CX-4181 Scriptable Control System" });
            return craft;
        }

        private static SeenVessel ProbeSeen()
        {
            var probe = new SeenVessel { Id = "probe", Name = "Pad Test Probe", HasKosProcessor = true, Situation = "ORBITING", LinkedHome = true };
            probe.ProcessorPartIds.Add(Mothership.ProbeCore);
            probe.PartTitles.AddRange(new[] { "TT-38K Radial Decoupler", "Probodobodyne OKTO", "48-7S \"Spark\"" });
            probe.LinkedTo.Add("craft");
            return probe;
        }

        private static RunOutcome Finished()
        {
            return new RunOutcome(RunOutcomeKind.Finished, new Dictionary<string, object> { { "success", true } }, "", "", false);
        }

        private static bool IsMission(HttpRequestSpec request)
        {
            return request.Body.Contains(ProposedPlans.ToolName);
        }

        private static int MissionRequests(StreamingFakeTransport transport)
        {
            return transport.RequestsSoFar().Count(IsMission);
        }

        /// <summary>The Debrief: the 4th Mission request, after the plan, its reply and the Sequence Plan.</summary>
        private static string DebriefText(StreamingFakeTransport transport)
        {
            var requests = transport.RequestsSoFar().Where(IsMission).ToList();
            Assert.True(requests.Count >= 4, "no Debrief request: " + requests.Count + " Mission requests");
            using var doc = JsonDocument.Parse(requests[3].Body);
            var last = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
            var content = last.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String) return content.GetString();
            return string.Concat(content.EnumerateArray().Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
        }

        private void DrainUntil(MissionControl mission, Func<MissionView, bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition(mission.View) && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason + " | " + string.Join(" / ", mission.View.Log.Select(e => e.Text)) + " | card: " + mission.View.ContractCheck?.ToString());
        }
    }
}
