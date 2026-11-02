// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Config;
using ClaudiaKermin.Core.Missions;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// The Target Vessel (ADR 0013): its compact entry in the Game Snapshot, where a Mission's target comes from, that only a
    /// Mission with one sends the entry, and that it survives a save and load.
    /// </summary>
    public sealed class TargetVesselTests : IDisposable
    {
        private const string Goal = "Rendezvous with Kerbin Station Alpha and dock.";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        private static SnapshotTarget Station(uint id = 4242)
        {
            var target = new SnapshotTarget
            {
                Id = id,
                Name = "Kerbin Station Alpha",
                Body = "Kerbin",
                Situation = "ORBITING",
                Orbit = new SnapshotOrbit
                {
                    Apoapsis = 120000, Periapsis = 118000, Inclination = 0.1, Eccentricity = 0.0003, SemiMajorAxis = 719000,
                    Lan = 12.5, ArgumentOfPeriapsis = 80, MeanAnomalyAtEpoch = 1.25, Epoch = 5000, Body = "Kerbin",
                },
            };
            target.FreePorts["size1"] = 2;
            target.FreePorts["size0"] = 1;
            target.PortTags["fore"] = "size1";
            return target;
        }

        /// <summary>The pad craft, fitted to dock (#456): the goal docks, so without a port, RCS and monopropellant the Craft Check blocks it.</summary>
        private static GameSnapshot OnPad()
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json")));
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9001, Name = "dockingPort2", Title = "Clamp-O-Tron Docking Port", PortSize = "size1", Tags = { "docking_port" } });
            snapshot.Craft.Parts.Add(new SnapshotPart { Id = 9002, Name = "RCSBlock.v2", Title = "RV-105 RCS Thruster Block", Tags = { "rcs" } });
            snapshot.Resources.Add(new SnapshotResource { Name = "MonoPropellant", Amount = 30, Max = 30 });
            return snapshot;
        }

        // ---- The entry ----

        [Fact]
        public void ASnapshotWithNoTargetHasNoEntry()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });

            Assert.DoesNotContain("\"target\"", snapshot.ToJson(false));
            Assert.DoesNotContain("Kerbin Station Alpha", snapshot.ToJson(true));
        }

        [Fact]
        public void TheEntryCarriesNameBodyOrbitAndFreePortsByCountWithTags()
        {
            var snapshot = OnPad();
            snapshot.Target = Station();

            var json = snapshot.ToJson(false);
            using (var doc = JsonDocument.Parse(json))
            {
                var target = doc.RootElement.GetProperty("target");
                Assert.Equal("Kerbin Station Alpha", target.GetProperty("name").GetString());
                Assert.Equal("Kerbin", target.GetProperty("body").GetString());
                Assert.Equal("ORBITING", target.GetProperty("situation").GetString());
                Assert.Equal(719000, target.GetProperty("orbit").GetProperty("semi_major_axis").GetDouble());
                Assert.Equal(5000, target.GetProperty("orbit").GetProperty("epoch").GetDouble());
                Assert.Equal(2, target.GetProperty("free_ports").GetProperty("size1").GetInt32());
                Assert.Equal(1, target.GetProperty("free_ports").GetProperty("size0").GetInt32());
                Assert.Equal("size1", target.GetProperty("port_tags").GetProperty("fore").GetString());
                // Claudia's turns leave out the id: the Mission keeps it.
                Assert.False(target.TryGetProperty("id", out _));
            }
            // The dynamic snapshot, which leaves out the craft's parts, still carries it.
            Assert.Contains("\"target\":{", snapshot.ToJson(false, false));
        }

        [Fact]
        public void TheEntryStaysCompact()
        {
            var snapshot = OnPad();
            var without = snapshot.ToJson(false).Length;
            var target = Station();
            // Orbit elements as KSP reads them, at full precision.
            target.Orbit = new SnapshotOrbit
            {
                Apoapsis = 120384.61538461538, Periapsis = 118922.30769230769, Inclination = 0.10471975511965977, Eccentricity = 0.0010175644531249998,
                SemiMajorAxis = 719653.4615384615, Lan = 12.566370614359172, ArgumentOfPeriapsis = 80.11061266653972,
                MeanAnomalyAtEpoch = 1.2566370614359172, Epoch = 51234.567890123456, Body = "Kerbin",
            };
            snapshot.Target = target;

            // About 150 tokens by the mod's estimate (characters / 3): the cost of a turn's snapshot for a Mission with a target.
            Assert.InRange(snapshot.ToJson(false).Length - without, 300, 480);
        }

        [Fact]
        public void ATargetWithNoTaggedPortsLeavesPortTagsOut()
        {
            var snapshot = OnPad();
            var target = Station();
            target.PortTags.Clear();
            snapshot.Target = target;

            Assert.DoesNotContain("port_tags", snapshot.ToJson(false));
        }

        [Fact]
        public void TheRecordingRoundTripsTheEntryWithItsId()
        {
            var snapshot = OnPad();
            snapshot.Target = Station();

            var read = GameSnapshot.Parse(snapshot.ToJson()).Target;

            Assert.NotNull(read);
            Assert.Equal(4242u, read.Id);
            Assert.Equal("Kerbin Station Alpha", read.Name);
            Assert.Equal(1.25, read.Orbit.MeanAnomalyAtEpoch);
            Assert.Equal("Kerbin", read.Orbit.Body);
            Assert.Equal(2, read.FreePorts["size1"]);
            Assert.Equal("size1", read.PortTags["fore"]);
        }

        // ---- A landed target's position (#662) ----

        private static SnapshotTarget MunBase(string situation = "LANDED")
        {
            var target = Station();
            target.Name = "Mun Base";
            target.Body = "Mun";
            target.Situation = situation;
            target.Position = new SnapshotPosition { Latitude = 0.123456789, Longitude = -45.987654321, Altitude = 1234.5678 };
            return target;
        }

        [Theory]
        [InlineData("LANDED")]
        [InlineData("SPLASHED")]
        public void ALandedOrSplashedTargetsEntryCarriesLatLngAndAlt(string situation)
        {
            var snapshot = OnPad();
            snapshot.Target = MunBase(situation);

            using (var doc = JsonDocument.Parse(snapshot.ToJson(false)))
            {
                var target = doc.RootElement.GetProperty("target");
                // About a metre's precision is plenty to land beside it.
                Assert.Equal(0.12346, target.GetProperty("lat").GetDouble());
                Assert.Equal(-45.98765, target.GetProperty("lng").GetDouble());
                Assert.Equal(1234.6, target.GetProperty("alt").GetDouble());
            }
            Assert.Contains("\"lat\":", snapshot.ToJson(false, false));
        }

        [Fact]
        public void AnOrbitingTargetsEntryIsUnchangedEvenWithAPositionRead()
        {
            var snapshot = OnPad();
            snapshot.Target = Station();
            var before = snapshot.ToJson(false);
            var orbiting = Station();
            orbiting.Position = new SnapshotPosition { Latitude = 1, Longitude = 2, Altitude = 3 };
            snapshot.Target = orbiting;

            Assert.Equal(before, snapshot.ToJson(false));
        }

        [Fact]
        public void TheRecordingRoundTripsALandedTargetsPosition()
        {
            var snapshot = OnPad();
            snapshot.Target = MunBase();

            var read = GameSnapshot.Parse(snapshot.ToJson()).Target;

            Assert.Equal(0.12346, read.Position.Latitude);
            Assert.Equal(-45.98765, read.Position.Longitude);
            Assert.Equal(1234.6, read.Position.Altitude);
            Assert.Null(GameSnapshot.Parse(new GameSnapshot { Target = Station() }.ToJson()).Target.Position);
        }

        // ---- Where the target comes from (#643): the pick, then the vessel the goal names, then KSP's target ----

        private static TargetChoice Choose(string goal, GameSnapshot snapshot, TargetPick pick = null)
        {
            return TargetVessel.Choose(goal, pick ?? TargetPick.Auto, TargetFacts.Of(snapshot));
        }

        private static SnapshotVessel Vessel(uint id, string name, string body = "Kerbin", string situation = "ORBITING")
        {
            return new SnapshotVessel { Id = id, Name = name, Body = body, Situation = situation };
        }

        [Fact]
        public void AGoalThatNeedsNoTargetHasNoLineEvenWithAKspTarget()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));

            var choice = Choose("Land on the Mun and take an EVA report.", snapshot);

            Assert.False(choice.Needed);
            Assert.Null(choice.Target);
            Assert.Equal("", choice.Line);
            Assert.Equal("", choice.LogLine);
        }

        [Fact]
        public void TheVesselTheGoalNamesComesBeforeKspsTarget()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));

            var choice = Choose("Dock with the Tug.", snapshot);

            Assert.Equal(9u, choice.Target.Id);
            Assert.Equal("named in the Mission Goal", choice.Reason);
            Assert.Equal("Target Vessel: Tug · Kerbin · orbiting (named in the Mission Goal)", choice.Line);
        }

        [Fact]
        public void KspsTargetServesAGoalThatMeetsButNamesNoVessel()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));

            var choice = Choose("Rendezvous with the station in low orbit.", snapshot);

            Assert.Equal(7u, choice.Target.Id);
            Assert.Equal("KSP's target", choice.Reason);
            Assert.Equal("Target Vessel: Kerbin Station Alpha (KSP's target).", choice.LogLine);
        }

        [Fact]
        public void TheLongestNameTheGoalHoldsIsTheTarget()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(1, "Kerbin Station"));
            snapshot.Vessels.Add(Vessel(2, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(3, "Mun Lander"));

            var choice = Choose("Rendezvous with kerbin station alpha, then dock.", snapshot);

            // "Kerbin Station" is inside it too.
            Assert.Equal(2u, choice.Target.Id);
            Assert.Equal("Kerbin Station Alpha", choice.Target.Name);
        }

        [Fact]
        public void ANameMatchesOnlyWholeWordsAndShortNamesNever()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(1, "Tug"));
            snapshot.Vessels.Add(Vessel(2, "A"));

            var choice = Choose("Tugboat practice: reach a 100 km orbit.", snapshot);

            Assert.False(choice.Needed);
            Assert.Null(choice.Target);
        }

        [Fact]
        public void AGoalNamingAVesselNeedsATargetWithoutRendezvousOrDocking()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(1, "Mun Base", "Mun", "LANDED"));

            var choice = Choose("Fly to Mun Base.", snapshot);

            Assert.True(choice.Needed);
            Assert.Equal(1u, choice.Target.Id);
            // A landed target shows its situation; the in-orbit note is only for a goal that meets it.
            Assert.Equal("Target Vessel: Mun Base · Mun · landed (named in the Mission Goal)", choice.Line);
            Assert.Equal("", choice.Note);
        }

        [Fact]
        public void ALandedTargetOfARendezvousGoalSaysItMustBeInOrbit()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(1, "Mun Base", "Mun", "LANDED"));
            snapshot.Vessels.Add(Vessel(2, "Sea Lab", "Kerbin", "SPLASHED"));
            snapshot.Vessels.Add(Vessel(3, "Glider", "Kerbin", "FLYING"));

            var landed = Choose("Rendezvous with Mun Base.", snapshot);
            Assert.Equal(1u, landed.Target.Id);
            Assert.Equal("Target Vessel: Mun Base · Mun · landed (named in the Mission Goal); landed: rendezvous and docking need it in orbit", landed.Line);
            Assert.Equal("landed: rendezvous and docking need it in orbit", Choose("Dock with Sea Lab.", snapshot).Note);

            var flying = Choose("Dock with the Glider.", snapshot);
            Assert.Equal(3u, flying.Target.Id);
            Assert.Equal("", flying.Note);
            Assert.Contains("Glider · Kerbin · flying", flying.Line);
        }

        // ---- Why there is none ----

        [Fact]
        public void AGoalNamingSeveralVesselsOfOneNameHasNoTargetAndSaysWhy()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(1, "Relay"));
            snapshot.Vessels.Add(Vessel(2, "Relay"));
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));

            var choice = Choose("Dock with the Relay.", snapshot);

            Assert.Null(choice.Target);
            Assert.Equal("several vessels are named \"Relay\"", choice.Reason);
            Assert.Equal("Target Vessel: none (several vessels are named \"Relay\")", choice.Line);
            Assert.Equal("No Target Vessel: several vessels are named \"Relay\".", choice.LogLine);
        }

        [Fact]
        public void AVesselNamedAsTheCraftIsNeverTheTargetAndTheLineSaysSo()
        {
            var snapshot = OnPad();
            snapshot.Craft.Name = "Mun Lander";
            snapshot.Vessels.Add(Vessel(1, "Mun Lander"));

            var choice = Choose("Rendezvous with Mun Lander.", snapshot);

            Assert.True(choice.Needed);
            Assert.Null(choice.Target);
            Assert.Equal("\"Mun Lander\" is the craft's own name", choice.Reason);
        }

        [Fact]
        public void TheCraftsOwnNameAsksForNoTargetSoALeftoverKspTargetStaysOut()
        {
            var snapshot = OnPad();
            snapshot.Craft.Name = "Mun Lander";
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(1, "Mun Lander"));
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));

            var choice = Choose("Fly Mun Lander to the Mun and back.", snapshot);

            Assert.False(choice.Needed);
            Assert.Null(choice.Target);
            // A goal that docks still takes KSP's target, whatever the craft is called.
            Assert.Equal(7u, Choose("Dock Mun Lander in orbit.", snapshot).Target.Id);
        }

        [Fact]
        public void ATargetsPortsReadAfterTheCheckMakeStartCheckAgain()
        {
            CraftCheckRouter router;
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")), out router);
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            mission.Send(snapshot);
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            // The adapter reads the form's target a snapshot after it was chosen: the check that ran without its ports is stale.
            var read = OnPad();
            read.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            read.FormTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, read, new StartMission(Goal, 10m, "claude-sonnet-5-5"));

            Assert.Equal(2, router.CheckRequests.Count);
        }

        [Fact]
        public void AGoalThatMeetsButMatchesNoVesselSaysSo()
        {
            var choice = Choose(Goal, OnPad());

            Assert.True(choice.Needed);
            Assert.Null(choice.Target);
            Assert.Equal("no vessel matches", choice.Reason);
        }

        [Fact]
        public void KspsTargetWhoseNameAnotherVesselSharesIsNotTaken()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(8, "kerbin station alpha "));

            var choice = Choose("Rendezvous in orbit.", snapshot);

            Assert.Null(choice.Target);
            Assert.Equal("several vessels are named \"Kerbin Station Alpha\"", choice.Reason);
        }

        [Fact]
        public void KspsTargetNamedAsTheCraftIsNotTaken()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Craft.Name = "Kerbin Station Alpha";

            Assert.Equal("KSP's target \"Kerbin Station Alpha\" is the craft's own name", Choose("Dock in orbit.", snapshot).Reason);
        }

        // ---- The player's pick ----

        [Fact]
        public void ThePickComesFirstAndStaysThroughGoalEdits()
        {
            var snapshot = OnPad();
            snapshot.KspTarget = Station(7);
            snapshot.Vessels.Add(Vessel(7, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));
            var pick = TargetPick.Of(Vessel(9, "Tug"));

            foreach (var goal in new[] { "Rendezvous with Kerbin Station Alpha.", "Dock in orbit.", "Rendezvous with Kerbin Station Alpha, then dock." })
            {
                var choice = Choose(goal, snapshot, pick);
                Assert.Equal(9u, choice.Target.Id);
                Assert.Equal("your pick", choice.Reason);
            }
            // A goal that needs no target has none, whatever the pick.
            Assert.Null(Choose("Land on the Mun.", snapshot, pick).Target);
        }

        [Fact]
        public void ClearHasNoneAndAutoGoesBackToTheModsChoice()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));

            var cleared = Choose(Goal, snapshot, TargetPick.Cleared);
            Assert.Null(cleared.Target);
            Assert.Equal("Target Vessel: none (cleared)", cleared.Line);

            Assert.Equal(4242u, Choose(Goal, snapshot, TargetPick.Auto).Target.Id);
        }

        [Fact]
        public void APickNoLongerInTheListHasNoneAndSaysWhy()
        {
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));

            var choice = Choose(Goal, snapshot, TargetPick.Of(Vessel(9, "Tug")));

            Assert.Null(choice.Target);
            Assert.Equal("\"Tug\", your pick, is no longer in the list", choice.Reason);
        }

        [Fact]
        public void TheListLeavesOutTheCraftsNameAndSharedNamesAndSortsTheCraftsBodyFirst()
        {
            var snapshot = OnPad();
            snapshot.Body = "Kerbin";
            snapshot.Craft.Name = "Mun Lander";
            snapshot.Vessels.Add(Vessel(1, "Mun Lander", "Mun", "LANDED"));
            snapshot.Vessels.Add(Vessel(2, "Relay", "Kerbin"));
            snapshot.Vessels.Add(Vessel(3, "relay", "Mun"));
            snapshot.Vessels.Add(Vessel(4, "Zeta Station", "Kerbin"));
            snapshot.Vessels.Add(Vessel(5, "Alpha Base", "Mun", "LANDED"));
            snapshot.Vessels.Add(Vessel(6, "beta Station", "Kerbin"));
            snapshot.Vessels.Add(Vessel(6, "beta Station", "Kerbin"));

            var options = TargetVessel.Options(TargetFacts.Of(snapshot));

            Assert.Equal(new uint[] { 6, 4, 5 }, options.Select(v => v.Id).ToArray());
            Assert.Equal("Alpha Base · Mun · landed", TargetVessel.Describe(options[2]));
        }

        // ---- Through the Mission ----

        [Fact]
        public void AMissionStartedWithKspsTargetSendsTheEntryAndShowsTheTarget()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Equal(4242u, mission.View.TargetVesselId);
            Assert.Equal("Kerbin Station Alpha", mission.View.TargetVesselName);
            Assert.Contains("\"target\":{\"name\":\"Kerbin Station Alpha\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void AMissionWithNoTargetSendsNoEntryWhateverTheAdapterRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            CraftCheckRouter.StartChecked(mission, _background, OnPad(), new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            // A target the player set after the start, or another Mission's, is not this Mission's.
            var later = OnPad();
            later.Target = Station();
            later.KspTarget = Station();
            mission.Send(later);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Null(mission.View.TargetVesselId);
            Assert.Equal("", mission.View.TargetVesselName);
            Assert.DoesNotContain("\"target\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void AGoalThatNeedsNoTargetStartsWithNoneEvenWithAKspTarget()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            Assert.Null(mission.View.TargetVesselId);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Target Vessel"));
        }

        [Fact]
        public void ThePickIsTheMissionsTargetVesselAndTheLogSaysSo()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));
            CraftCheckRouter.StartChecked(mission, _background, snapshot,
                new StartMission(Goal, 10m, "claude-sonnet-5-5", false, TargetPick.Of(Vessel(9, "Tug"))));

            Assert.Equal(9u, mission.View.TargetVesselId);
            Assert.Equal("Tug", mission.View.TargetVesselName);
            Assert.Contains(mission.View.Log, e => e.Text == "Target Vessel: Tug (your pick).");
        }

        [Fact]
        public void ClearStartsTheMissionWithNoTargetAndTheLogSaysWhy()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5", false, TargetPick.Cleared));

            Assert.Null(mission.View.TargetVesselId);
            Assert.Contains(mission.View.Log, e => e.Text == "No Target Vessel: cleared.");
        }

        [Fact]
        public void ChangingTheTargetMakesStartCheckTheCraftAgain()
        {
            CraftCheckRouter router;
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")), out router);
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));
            mission.Send(snapshot);
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);
            Assert.Single(router.CheckRequests);

            // The check judged the goal's vessel; Start with another target runs it again before the Mission starts.
            CraftCheckRouter.StartChecked(mission, _background, snapshot,
                new StartMission(Goal, 10m, "claude-sonnet-5-5", false, TargetPick.Of(Vessel(9, "Tug"))));

            Assert.Equal(2, router.CheckRequests.Count);
            Assert.Equal(9u, mission.View.TargetVesselId);
        }

        [Fact]
        public void TheSameTargetLetsStartGoAheadOnTheCheckThatRan()
        {
            CraftCheckRouter router;
            var mission = Control(new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse")), out router);
            var snapshot = OnPad();
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            mission.Send(snapshot);
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));

            Assert.Single(router.CheckRequests);
            Assert.Equal(4242u, mission.View.TargetVesselId);
        }

        [Fact]
        public void TheCraftCheckJudgesPortSizesAgainstTheChosenTargetNotKspsTarget()
        {
            var mission = Control(new StreamingFakeTransport());
            var snapshot = OnPad();
            // KSP's target has a size1 port free, as the craft does; the Tug the goal names has only size2.
            snapshot.KspTarget = Station();
            var tug = Station(9);
            tug.Name = "Tug";
            tug.FreePorts.Clear();
            tug.FreePorts["size2"] = 1;
            snapshot.FormTarget = tug;
            snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
            snapshot.Vessels.Add(Vessel(9, "Tug"));
            mission.Send(snapshot);

            mission.Send(new RunCraftCheck("Rendezvous with the Tug and dock.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.CraftCheck.State == CraftCheckState.Done);

            Assert.Contains(mission.View.CraftCheck.Findings, f => f.Code == CraftCheckRules.DockingPortSize && f.Explanation.Contains("Tug"));
        }

        [Fact]
        public void TheTargetIsNeverASavedDefault()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                var client = new ClaudeClient(new CraftCheckRouter(new StreamingFakeTransport()), () => "sk-ant-test", l => { });
                client.Timing.Wait = (d, c) => { };
                var mission = new MissionControl(client, _background, config, () => true, new PassingScriptCheck());
                mission.Send(new SaveLoaded(_save.Path));
                mission.Send(new SceneChanged(GameScene.Editor));
                var snapshot = OnPad();
                snapshot.Vessels.Add(Vessel(4242, "Kerbin Station Alpha"));
                snapshot.Vessels.Add(Vessel(9, "Tugboat Nine"));
                CraftCheckRouter.StartChecked(mission, _background, snapshot,
                    new StartMission(Goal, 10m, "claude-sonnet-5-5", false, TargetPick.Of(Vessel(9, "Tugboat Nine"))));
                Assert.Equal("Tugboat Nine", mission.View.TargetVesselName);

                Assert.Equal("", config.TrySaveDefaults(dir.Sub));
                foreach (var file in Directory.GetFiles(dir.Sub, "*", SearchOption.AllDirectories))
                {
                    var text = File.ReadAllText(file);
                    Assert.DoesNotContain("Tugboat", text);
                    Assert.DoesNotContain("Kerbin Station Alpha", text);
                }
            }
        }

        [Fact]
        public void AnEntryForAnotherVesselThanTheTargetIsDropped()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            var other = OnPad();
            other.Target = Station(99);
            mission.Send(other);
            Assert.DoesNotContain("\"target\"", Captured(mission));

            var own = OnPad();
            own.Target = Station();
            mission.Send(own);
            Assert.Contains("\"target\":{\"id\":4242,", Captured(mission));
        }

        /// <summary>The snapshot the Mission holds now, through the debug capture button.</summary>
        private static string Captured(MissionControl mission)
        {
            mission.Send(new CaptureSnapshot());
            const string Saved = "Game Snapshot saved to ";
            var line = mission.View.Log.Last(e => e.Text.StartsWith(Saved, StringComparison.Ordinal)).Text;
            return File.ReadAllText(line.Substring(Saved.Length));
        }

        [Fact]
        public void TheMissionPlanWaitsForTheTargetTheGoalNamedToBeRead()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            _background.Drain();

            // Named in the goal but not yet read: no request until a snapshot carries it.
            Assert.Equal("Kerbin Station Alpha", mission.View.TargetVesselName);
            Assert.Equal(MissionStage.WaitingForFlight, mission.View.Stage);
            Assert.Empty(transport.Requests);

            var read = OnPad();
            read.Vessels.Add(new SnapshotVessel { Id = 4242, Name = "Kerbin Station Alpha" });
            read.Target = Station();
            mission.Send(read);
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            Assert.Contains("\"target\":{\"name\":\"Kerbin Station Alpha\"", LastUserText(transport.Requests.Last()));
        }

        [Fact]
        public void TheTargetSurvivesASaveAndLoad()
        {
            var transport = new StreamingFakeTransport(ScriptedResponse.Fixture("mission-plan.sse"));
            var mission = Control(transport);
            var snapshot = OnPad();
            snapshot.KspTarget = Station();
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission(Goal, 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);

            // Through the flat values a ConfigNode holds, as the ScenarioModule writes and reads them.
            var saved = mission.ExportStates().Select(s => MissionSaveState.FromValues(s.ToValues())).ToList();
            var loaded = Control(transport);
            loaded.Send(new MissionsRestored(_save.Path, saved));

            Assert.Equal(4242u, loaded.View.TargetVesselId);
            Assert.Equal("Kerbin Station Alpha", loaded.View.TargetVesselName);
        }

        [Fact]
        public void TheSaveStateRoundTripsTheTargetAndAnOlderSaveHasNone()
        {
            var state = new MissionSaveState { Id = "m1", TargetVesselId = 4242, TargetVesselName = "Station {A}/1" };
            var read = MissionSaveState.FromValues(state.ToValues());
            Assert.Equal(4242u, read.TargetVesselId);
            Assert.Equal("Station {A}/1", read.TargetVesselName);

            var older = MissionSaveState.FromValues(new MissionSaveState { Id = "m2" }.ToValues());
            Assert.Null(older.TargetVesselId);
            Assert.Equal("", older.TargetVesselName);
            Assert.False(new MissionSaveState { Id = "m3" }.ToValues().ContainsKey("targetVesselId"));
        }

        // ---- Rig ----

        private MissionControl Control(StreamingFakeTransport transport)
        {
            CraftCheckRouter router;
            return Control(transport, out router);
        }

        private MissionControl Control(StreamingFakeTransport transport, out CraftCheckRouter router)
        {
            router = new CraftCheckRouter(transport);
            var client = new ClaudeClient(router, () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            return mission;
        }

        private static string LastUserText(HttpRequestSpec request)
        {
            using (var doc = JsonDocument.Parse(request.Body))
            {
                var user = doc.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user");
                var content = user.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String) return content.GetString();
                return string.Concat(content.EnumerateArray()
                    .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
            }
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
            Assert.True(condition(mission.View), "never reached; stage is " + mission.View.Stage + " " + mission.View.PauseReason);
        }
    }
}
