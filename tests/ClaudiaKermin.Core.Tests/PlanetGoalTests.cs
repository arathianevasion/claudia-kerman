// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// A planet goal is planned like any other since transfer_to flies to planets (#459): #670's guard, which made Claudia decline
    /// one, is gone from propose_mission_plan and from plan_transfer's planet answer, and plan_transfer's description names
    /// transfer_to for a planet's window as for a moon's.
    /// </summary>
    public sealed class PlanetGoalTests
    {
        /// <summary>The stock body table, with the craft in a 100 km circular orbit of Kerbin.</summary>
        private static GameSnapshot Parked()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Ut = 0;
            snapshot.Orbit = new SnapshotOrbit { SemiMajorAxis = 700000, Eccentricity = 0, Body = "Kerbin", Periapsis = 100000, Apoapsis = 100000 };
            return snapshot;
        }

        private static string Plan(string newGoal = "")
        {
            return "{\"why\":\"Eject in the Duna window with transfer_to.\",\"sequences\":[{\"name\":\"Ejection\",\"intent\":\"Burn for Duna.\",\"delta_v\":1100,"
                + "\"timing\":\"at the window\",\"due_time_ut\":-1,\"manual\":false,\"vessel\":\"craft\"}],\"total_delta_v\":1100,\"expected_duration\":\"a year\","
                + "\"mission_time_s\":-1,\"new_goal\":" + JsonSerializer.Serialize(newGoal) + ",\"child_vessels\":[],\"goal_steps\":[\"Orbit Duna\"]}";
        }

        [Fact]
        public void APlanWithSequencesForAPlanetIsAccepted()
        {
            var use = new ToolUseBlock { Id = "u", Name = ProposedPlans.ToolName, InputJson = Plan(), InputIsValid = true };
            Assert.Equal(ProposedPlans.AcceptedJson, ProposedPlans.Check(use, new List<string>(), false, false).Json);
            var newGoal = new ToolUseBlock { Id = "v", Name = ProposedPlans.ToolName, InputJson = Plan("Fly to Duna orbit"), InputIsValid = true };
            Assert.Equal(ProposedPlans.AcceptedJson, ProposedPlans.Check(newGoal, new List<string>(), false, true).Json);
        }

        [Fact]
        public void PlanTransfersPlanetAnswerHasNoDecline()
        {
            var duna = PlanningTools.Default().Run("plan_transfer", "{\"target\":\"Duna\",\"target_pe\":100000}", Parked(), new NoPlanningBody());
            Assert.False(duna.IsError, duna.Json);
            using (var doc = JsonDocument.Parse(duna.Json))
                Assert.False(doc.RootElement.TryGetProperty("decline", out _));
        }

        [Fact]
        public void PlanTransfersDescriptionNamesTransferToForAPlanetsWindowAndStaysAsShort()
        {
            var description = PlanningTools.Default().Definitions(ToolOffer.Mission).Single(t => t.Name == "plan_transfer").Description;
            Assert.InRange(description.Length, 1, 457);
            Assert.Contains("a sibling: soonest, optimal windows", description);
            Assert.Contains("Its block is transfer_to", description);
            Assert.DoesNotContain("no burn", description);
        }
    }
}
