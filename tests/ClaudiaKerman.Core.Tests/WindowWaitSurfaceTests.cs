// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #774: a wait for a Transfer Window is spent on the ground. On the pad for a goal at a moon of another planet; on another
    /// body's surface before the ascent that starts a trip to another planet, home included.
    /// </summary>
    public sealed partial class WindowWaitTests
    {
        private const string LaunchScript = "local res is launch(lexicon(\\\"target_ap\\\", 60000)). addons:claudia:report(res).\\n";

        private static ScriptedResponse HomePlan(params string[] steps)
        {
            var sequences = string.Join(",", steps.Select(s =>
            {
                var manual = s.StartsWith("manual:");
                var name = manual ? s.Substring(7) : s;
                return "{\"name\":\"" + name + "\",\"intent\":\"" + name + ".\",\"delta_v\":500,\"timing\":\"soon\",\"due_time_ut\":-1,\"manual\":" + (manual ? "true" : "false") + "}";
            }));
            return Reply("{\"kind\":\"mission_plan\",\"say\":\"Mission Plan.\",\"reply_intent\":\"none\","
                + "\"mission_plan\":{\"why\":\"Home.\",\"sequences\":[" + sequences + "],"
                + "\"total_delta_v\":1000,\"expected_duration\":\"a year\",\"mission_time_s\":-1},\"sequence_plan\":null,\"debrief\":null}");
        }

        private static ScriptedResponse AscentPlan(string script = LaunchScript, int step = 1, double due = -1)
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: Ascend.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Ascend\",\"plan_step\":" + step + ",\"intent\":\"Reach low orbit.\",\"why\":\"First.\",\"target\":\"low orbit\","
                + "\"tolerances\":\"Ap within 5 km\",\"expected_delta_v\":500,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"" + script + "\","
                + "\"due_time_ut\":" + due.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse SurfaceStay()
        {
            return Reply("{\"kind\":\"sequence_plan\",\"say\":\"Sequence Plan: surface operations.\",\"reply_intent\":\"none\",\"mission_plan\":null,"
                + "\"sequence_plan\":{\"name\":\"Surface operations\",\"plan_step\":1,\"manual\":true,\"intent\":\"Hand over control.\",\"why\":\"The player asked.\",\"target\":\"\","
                + "\"tolerances\":\"\",\"expected_delta_v\":0,\"expected_duration_s\":600,\"abort_conditions\":[],\"risks\":[],"
                + "\"custom_script\":false,\"custom_script_reason\":\"\",\"script\":\"\","
                + "\"due_time_ut\":-1,\"window_closes_ut\":-1,\"late_window_dv\":-1},\"debrief\":null}");
        }

        private static ScriptedResponse Stream(string name)
        {
            return new ScriptedResponse { Body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "streams", name)) };
        }

        /// <summary>The craft landed on <paramref name="body"/>, at the equator.</summary>
        private static GameSnapshot Landed(string body, double ut, double deltaV, string situation = "LANDED")
        {
            var snapshot = OnPad(ut, deltaV);
            snapshot.Situation = situation;
            snapshot.Body = body;
            if (snapshot.Orbit != null) snapshot.Orbit.Body = body;
            return snapshot;
        }

        /// <summary>A Mission started landed on <paramref name="body"/> at <paramref name="ut"/>, to its first Sequence Plan awaiting Approval.</summary>
        private void ToAscent(MissionControl mission, string body, double ut, string goal = "Return to Kerbin.", double deltaV = 20000)
        {
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(Landed(body, ut, deltaV));
            CraftCheckRouter.StartChecked(mission, _background, Landed(body, ut, deltaV), new StartMission(goal, 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(Landed(body, ut, deltaV), goal)));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            // No form or Mission Plan asks for a moon's way home: its ascent does (#782), and these tests are about what follows the window.
            if (mission.View.WindowPickNeeded) mission.Send(new ChooseWindow(mission.View.Windows.Rows.First(r => r.DepartureUt == Solved(body, ut).Window.DepartureUt).Kind));
        }

        private static SurfaceDeparture Solved(string body, double ut, string target = "Kerbin", bool twoStep = false)
        {
            var snapshot = Landed(body, ut, 20000);
            var departure = new SurfaceDeparture { Planet = snapshot.Bodies.Single(b => b.Name == target), TwoStep = twoStep };
            WindowWaits.Solve(snapshot, departure, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, ut);
            Assert.NotNull(departure.Window);
            return departure;
        }

        // ---- On the pad: a moon of another planet ----

        [Theory]
        [InlineData("Ike", "Duna")]
        [InlineData("Laythe", "Jool")]
        [InlineData("Gilly", "Eve")]
        public void AMoonOfAnotherPlanetGivesThatPlanet(string moon, string planet)
        {
            Assert.Equal(planet, WindowWaits.Planet(OnPad(1203.48, 9000), "Go to " + moon + ".").Name);
        }

        [Theory]
        [InlineData("Orbit the Mun.", "Mun")]
        [InlineData("Orbit Minmus.", "Minmus")]
        [InlineData("Orbit Duna.", "Duna")]
        [InlineData("Go to Ike and return to Kerbin.", "Duna")]
        public void GoalsTheCraftsPlanetReachesAreUnchanged(string goal, string body)
        {
            Assert.Equal(body, WindowWaits.Planet(OnPad(1203.48, 9000), goal).Name);
            Assert.Null(WindowWaits.Planet(OnPad(1203.48, 9000), "Stay at Kerbin."));
        }

        [Theory]
        [InlineData("Ike", "Duna")]
        [InlineData("Laythe", "Jool")]
        [InlineData("Gilly", "Eve")]
        public void AGoalAtAMoonOfAnotherPlanetIsDueOnThePadAtThatPlanetsWindow(string moon, string planet)
        {
            var mission = Control(new StreamingFakeTransport(DunaPlan(), LaunchPlan()));
            const double now = 1203.48;
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(OnPad(now, 20000));
            CraftCheckRouter.StartChecked(mission, _background, OnPad(now, 20000), new StartMission("Go to " + moon + ".", 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(OnPad(now, 20000), "Go to " + moon + ".")));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);

            var snapshot = OnPad(now, 9000);
            var window = WindowWaits.Window(snapshot, snapshot.Bodies.Single(b => b.Name == planet), new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent);
            Assert.Equal(window.DepartureUt - WindowWaits.LaunchLeadS, mission.View.DueTime.Ut, 0);
            Assert.Equal("the " + planet + " window", mission.View.DueTime.WaitsFor);
        }

        // ---- The ascent lead ----

        [Fact]
        public void TheAscentLeadIsTheAscentOneLowOrbitAndTheMargin()
        {
            var duna = OnPad(0, 0).Bodies.Single(b => b.Name == "Duna");
            var low = DeltaVMap.LowOrbit(duna);
            var orbit = 2 * Math.PI * Math.Sqrt(low * low * low / duna.Gm);
            Assert.Equal(600 + orbit + 900, WindowWaits.AscentLeadS(duna), 3);
            // About an hour and a quarter, the launch lead's size.
            Assert.InRange(WindowWaits.AscentLeadS(duna), 3600, 5400);
            Assert.InRange(WindowWaits.AscentLeadS(OnPad(0, 0).Bodies.Single(b => b.Name == "Ike")), 3000, 5400);
        }

        // ---- Reading the plan ----

        private static List<PlannedSequence> Steps(params string[] names)
        {
            return names.Select(n => new PlannedSequence { Name = n, Intent = n }).ToList();
        }

        [Theory]
        [InlineData("Duna", "Kerbin", false, "Ascend from Duna", "Return to Kerbin")]
        [InlineData("Duna", "Kerbin", false, "Ascend from Duna and return to Kerbin")]
        [InlineData("Duna", "Jool", false, "Launch to orbit", "Transfer to Laythe")]
        [InlineData("Ike", "Kerbin", false, "Ascend from Ike", "Return to Kerbin")]
        [InlineData("Ike", "Kerbin", false, "Ascend from Ike", "Return to Duna", "Transfer to Kerbin")]
        [InlineData("Ike", "Kerbin", true, "Ascend from Ike", "Return to Duna", "Capture at Duna", "Transfer to Kerbin")]
        [InlineData("Mun", "Duna", false, "Ascend from the Mun", "Transfer to Duna")]
        public void AnAscentThenADepartureForAnotherPlanetIsRead(string body, string planet, bool twoStep, params string[] steps)
        {
            var plan = new SequencePlan { Name = "Ascend", Script = "launch(lexicon()).\n" };
            var departure = WindowWaits.AfterAscent(Landed(body, 1000, 9000), plan, Steps(steps), 1);
            Assert.Equal(planet, departure.Planet.Name);
            Assert.Equal(twoStep, departure.TwoStep);
        }

        [Theory]
        [InlineData("Mun", "Ascend from the Mun", "Return to Kerbin")]
        [InlineData("Minmus", "Ascend from Minmus", "Return to Kerbin")]
        [InlineData("Ike", "Ascend from Ike", "Return to Duna")]
        [InlineData("Duna", "Ascend from Duna", "Transfer to Ike")]
        [InlineData("Duna", "Ascend from Duna", "Land at the north pole", "Return to Kerbin")]
        [InlineData("Duna", "Ascend from Duna")]
        public void NoOtherPlanetOrNoDepartureNextIsNoSurfaceWait(string body, params string[] steps)
        {
            var plan = new SequencePlan { Name = "Ascend", Script = "launch(lexicon()).\n" };
            Assert.Null(WindowWaits.AfterAscent(Landed(body, 1000, 9000), plan, Steps(steps), 1));
        }

        [Theory]
        [InlineData("launch")]
        [InlineData("ascend")]
        public void TheScriptsFirstBlockIsTheAscentWhateverThePlanIsNamed(string block)
        {
            var plan = new SequencePlan { Name = "To orbit", Script = block + "(lexicon()).\n" };
            Assert.Equal("Kerbin", WindowWaits.AfterAscent(Landed("Ike", 1000, 9000), plan, Steps("To orbit", "Return to Kerbin"), 1).Planet.Name);
        }

        [Fact]
        public void TheScriptsOwnTransferToIsADeparture()
        {
            var plan = new SequencePlan { Name = "Leave", Script = "launch(lexicon()).\ntransfer_to(lexicon(\"target\", \"Kerbin\")).\n" };
            Assert.Equal("Kerbin", WindowWaits.AfterAscent(Landed("Duna", 1000, 9000), plan, null, 1).Planet.Name);
        }

        [Fact]
        public void OnlyALandedOrSplashedCraftWaitsOnTheSurfaceAndOnlyBeforeAnAscent()
        {
            var ascent = new SequencePlan { Name = "Ascend", Script = "launch(lexicon()).\n" };
            var steps = Steps("Ascend", "Return to Kerbin");
            Assert.NotNull(WindowWaits.AfterAscent(Landed("Duna", 1000, 9000, "SPLASHED"), ascent, steps, 1));
            Assert.Null(WindowWaits.AfterAscent(Landed("Duna", 1000, 9000, "PRELAUNCH"), ascent, steps, 1));
            Assert.Null(WindowWaits.AfterAscent(Landed("Duna", 1000, 9000, "ORBITING"), ascent, steps, 1));
            Assert.Null(WindowWaits.AfterAscent(Landed("Duna", 1000, 9000), new SequencePlan { Name = "Science", Script = "print 1.\n" }, Steps("Science", "Return to Kerbin"), 1));
        }

        // ---- The ascent carries the Due Time ----

        [Fact]
        public void LandedOnDunaTheAscentIsDueAtTheLaunchBeforeTheKerbinWindow()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            const double now = 1203.48;
            ToAscent(mission, "Duna", now);
            clock.Take(mission);

            var solved = Solved("Duna", now);
            var duna = Landed("Duna", now, 0).Bodies.Single(b => b.Name == "Duna");
            var latest = solved.Window.DepartureUt - WindowWaits.AscentLeadS(duna);
            var view = mission.View;
            Assert.True(view.DueTime.HasDueTime);
            Assert.Equal(solved.DueUt.Value, view.DueTime.Ut, 0);
            // The launch that holds the asymptote: no later than the lead, and within a turn of Duna.
            Assert.InRange(view.DueTime.Ut, latest - Math.Abs(duna.RotationPeriod), latest + 1e-3);
            Assert.Equal("the Kerbin window", view.DueTime.WaitsFor);
            Assert.Equal(view.DueTime.Ut, clock.Alarms[view.MissionId], 0);
            Assert.Contains(view.Log, e => e.Kind == LogKind.Notice && e.Text.StartsWith("Waiting for the Kerbin window: the ascent is due") && e.Text.Contains("Fly other Missions meanwhile"));
            // Approve before it starts nothing.
            mission.Send(new Approve());
            Assert.Empty(clock.Take(mission).OfType<StartScript>());
        }

        [Fact]
        public void WithNoRotationToTimeALaunchByTheDueTimeIsTheDepartureLessTheLead()
        {
            const double now = 1203.48;
            var snapshot = Landed("Duna", now, 20000);
            var duna = snapshot.Bodies.Single(b => b.Name == "Duna");
            duna.RotationPeriod = 0;
            var departure = new SurfaceDeparture { Planet = snapshot.Bodies.Single(b => b.Name == "Kerbin") };
            WindowWaits.Solve(snapshot, departure, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, now);
            Assert.Equal(departure.Window.DepartureUt - WindowWaits.AscentLeadS(duna), departure.DueUt.Value, 3);
        }

        [Fact]
        public void LandedOnDunaWithTheKerbinWindowUnderSixHoursOffThereIsNoDueTime()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            var start = Solved("Duna", 1203.48).Window.DepartureUt - 3600;
            Assert.Null(Solved("Duna", start).DueUt);
            ToAscent(mission, "Duna", start);
            clock.Take(mission);

            Assert.False(mission.View.DueTime.HasDueTime);
            Assert.Empty(clock.Alarms);
            mission.Send(new Approve());
            Assert.Single(clock.Take(mission).OfType<StartScript>());
        }

        [Fact]
        public void LandedOnIkeTheAscentIsDueBeforeTheDirectRoutesBurnOutOfIkesOrbit()
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Ike", "Return to Kerbin"), AscentPlan()));
            const double now = 1203.48;
            ToAscent(mission, "Ike", now);
            clock.Take(mission);

            var solved = Solved("Ike", now);
            var ike = Landed("Ike", now, 0).Bodies.Single(b => b.Name == "Ike");
            var view = mission.View;
            Assert.Equal(solved.Window.DepartureUt - WindowWaits.AscentLeadS(ike), view.DueTime.Ut, 0);
            Assert.Equal("the Kerbin window", view.DueTime.WaitsFor);
            Assert.Equal(view.DueTime.Ut, clock.Alarms[view.MissionId], 0);
        }

        [Fact]
        public void LandedOnIkeWithTheBurnUnderSixHoursOffThereIsNoDueTime()
        {
            var first = Solved("Ike", 1203.48);
            // The search moves a little with its start; the burn stays near, close enough to the window.
            var start = first.Window.DepartureUt - WindowWaits.LongWaitS / 2;
            var near = Solved("Ike", start);
            Assert.True(near.Window.DepartureUt - start < WindowWaits.LongWaitS + near.LeadS, "the burn is " + (near.Window.DepartureUt - start) + " s off");
            Assert.Null(near.DueUt);

            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Ike", "Return to Kerbin"), AscentPlan()));
            ToAscent(mission, "Ike", start);
            Assert.False(mission.View.DueTime.HasDueTime);
        }

        [Fact]
        public void ThePlanNamingACaptureAtThePlanetLeavesIkeTheFallBeforeTheEjection()
        {
            const double now = 1203.48;
            var twoStep = Solved("Ike", now, "Kerbin", true);
            var direct = Solved("Ike", now, "Kerbin", false);
            var ike = Landed("Ike", now, 0).Bodies.Single(b => b.Name == "Ike");
            Assert.Equal(twoStep.Window.DepartureUt - WindowWaits.AscentLeadS(ike), twoStep.DueUt.Value, 3);
            // Both lean on one window, months from now; the two-step's total carries the capture the direct doesn't.
            Assert.True(twoStep.Window.DepartureUt - now > 10 * Day);
            Assert.True(twoStep.Window.TotalDv > direct.Window.TotalDv);
        }

        [Theory]
        [InlineData("Mun")]
        [InlineData("Minmus")]
        public void LandedOnAMoonOfHomeAReturnToKerbinGetsNoDueTime(string moon)
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from " + (moon == "Mun" ? "the Mun" : moon), "Return to Kerbin"), AscentPlan()));
            ToAscent(mission, moon, 1203.48);

            Assert.False(mission.View.DueTime.HasDueTime);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Waiting for"));
        }

        [Fact]
        public void AutoModeNeverApprovesAtTheAscentsDueTime()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToAscent(mission, "Duna", 1203.48);
            mission.Send(new SetAutoMode(true));
            mission.Send(new UtAdvanced(mission.View.DueTime.Ut + 1));
            _background.Drain();

            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Equal("Auto Mode is waiting for you: a Due Time.", mission.View.AutoModeStop);
        }

        [Fact]
        public void TheAscentsDueTimeSurvivesASaveAndLoad()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan()));
            ToAscent(mission, "Duna", 1203.48);
            var due = mission.View.DueTime.Ut;
            var reread = MissionSaveState.FromValues(Assert.Single(mission.ExportStates()).ToValues());
            Assert.Equal(due, reread.SequencePlan.DueTimeUt);
            Assert.Equal("Kerbin", reread.WindowPlanet);
        }

        // ---- A missed window ----

        [Theory]
        [InlineData("Duna")]
        [InlineData("Ike")]
        public void AMissedWindowFromTheSurfaceMovesTheDueTimeToTheNextOneLessTheAscentLead(string body)
        {
            var clock = new FakeAlarmClock();
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from " + body, "Return to Kerbin"), AscentPlan()));
            const double now = 1203.48;
            ToAscent(mission, body, now);
            clock.Take(mission);

            var back = Solved(body, now).Window.DepartureUt + 120 * Day;
            mission.Send(new UtAdvanced(back));
            clock.Take(mission);
            mission.Send(new Approve());
            var commands = clock.Take(mission);

            // The Mission's Game Snapshot is the one from the start: its site has turned since then.
            var snapshot = Landed(body, now, 20000);
            var next = new SurfaceDeparture { Planet = snapshot.Bodies.Single(b => b.Name == "Kerbin") };
            WindowWaits.Solve(snapshot, next, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, back);
            Assert.True(next.DueUt.HasValue);
            Assert.Empty(commands.OfType<StartScript>());
            Assert.Equal(next.DueUt.Value, mission.View.DueTime.Ut, 0);
            Assert.Equal(next.DueUt.Value, clock.Alarms[mission.View.MissionId], 0);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("The Kerbin window of") && e.Text.Contains("was missed") && e.Text.Contains("the Due Time moves to"));
        }

        [Fact]
        public void AMissedWindowFromTheSurfaceOutsideTheBudgetRaisesAnAlert()
        {
            var mission = Control(new StreamingFakeTransport(HomePlan("Ascend from Duna", "Return to Kerbin"), AscentPlan(), HomePlan("Ascend from Duna", "Return to Kerbin")));
            const double now = 1203.48;
            ToAscent(mission, "Duna", now, deltaV: 1000);

            mission.Send(new UtAdvanced(Solved("Duna", now).Window.DepartureUt + 120 * Day));
            mission.Send(new Approve());

            Assert.Empty(mission.TakeCommands().OfType<StartScript>());
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("Claudia is revising the Mission Plan"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
        }

        [Fact]
        public void InMidMissionAMissedWindowIsBudgetedOnTheLegsStillToFly()
        {
            var clock = new FakeAlarmClock();
            const double now = 1203.48;
            var mission0 = Solved("Duna", now);
            var back = mission0.Window.DepartureUt + 120 * Day;
            var snapshot = Landed("Duna", now, 20000);
            var next = new SurfaceDeparture { Planet = snapshot.Bodies.Single(b => b.Name == "Kerbin") };
            WindowWaits.Solve(snapshot, next, new PlayerConfig().DefaultTimeVersusDeltaV, new PlayerConfig().CraftCheckMarginPercent, back);
            // Enough for the two legs left at 500 m/s each, with the margin; not for all four.
            var deltaV = (1000 - mission0.Window.TotalDv + next.Window.TotalDv) * 1.2;
            var mission = Control(new StreamingFakeTransport(HomePlan("Launch from Kerbin", "Land on Duna", "Ascend from Duna", "Return to Kerbin"), AscentPlan(step: 3)));
            ToAscent(mission, "Duna", now, deltaV: deltaV);
            clock.Take(mission);

            mission.Send(new UtAdvanced(back));
            mission.Send(new Approve());

            Assert.Equal(MissionStage.AwaitingSequenceApproval, mission.View.Stage);
            Assert.Equal(next.DueUt.Value, mission.View.DueTime.Ut, 0);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("Claudia is revising the Mission Plan"));
        }

        // ---- A manual stretch comes first ----

        private MissionControl ToSurfaceStay(StreamingFakeTransport transport, double now)
        {
            var mission = Control(transport);
            // The player asks for the manual stretch (#818), so none is refused.
            ToAscent(mission, "Duna", now, "Return to Kerbin. I'll fly the surface stay by hand.");
            Assert.False(mission.View.DueTime.HasDueTime);
            return mission;
        }

        /// <summary>Back from the stretch at <paramref name="back"/>: its Debrief, the revised Mission Plan, and the ascent's Sequence Plan offered.</summary>
        private void HandedBackAt(MissionControl mission, double back)
        {
            mission.Send(new UtAdvanced(back));
            mission.Send(Landed("Duna", back, 20000));
            mission.Send(new HandBack("done"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            // The revised Mission Plan asks for the window again (#686).
            mission.Send(new ChooseWindow(WindowPicks.AsTheSetting(Landed("Duna", back, 20000), mission.View.Goal).Kind));
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
        }

        private static StreamingFakeTransport StayThenAscent()
        {
            var steps = new[] { "manual:Surface operations", "Ascend from Duna", "Return to Kerbin" };
            return new StreamingFakeTransport(HomePlan(steps), SurfaceStay(), Stream("mission-debrief-manual.sse"), HomePlan(steps), AscentPlan(step: 2));
        }

        [Fact]
        public void AManualStretchBeforeTheAscentHasNoDueTimeAndIsNotCutShortByTheWindow()
        {
            var clock = new FakeAlarmClock();
            const double now = 1203.48;
            var mission = ToSurfaceStay(StayThenAscent(), now);
            clock.Take(mission);
            Assert.Empty(clock.Alarms);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.StartsWith("Waiting for"));

            // Past the window, Approve still starts the stretch, and the stretch runs on past it.
            var late = Solved("Duna", now).Window.DepartureUt + Day;
            mission.Send(new UtAdvanced(late));
            mission.Send(new Approve());
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            mission.Send(new UtAdvanced(late + 100 * Day));
            _background.Drain();
            Assert.Equal(MissionStage.ManualControl, mission.View.Stage);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed"));
        }

        [Fact]
        public void HandedBackBeforeTheWindowTheAscentCarriesTheDueTime()
        {
            var clock = new FakeAlarmClock();
            const double now = 1203.48;
            var mission = ToSurfaceStay(StayThenAscent(), now);
            mission.Send(new Approve());
            var back = now + 2 * Day;
            HandedBackAt(mission, back);
            clock.Take(mission);

            Assert.Equal(Solved("Duna", back).DueUt.Value, mission.View.DueTime.Ut, 0);
            Assert.Equal("the Kerbin window", mission.View.DueTime.WaitsFor);
            Assert.Equal(mission.View.DueTime.Ut, clock.Alarms[mission.View.MissionId], 0);
        }

        [Fact]
        public void HandedBackInsideTheLeadTheNotedWindowStandsAndApprovingStartsTheAscent()
        {
            var clock = new FakeAlarmClock();
            const double now = 1203.48;
            var mission = ToSurfaceStay(StayThenAscent(), now);
            mission.Send(new Approve());
            var noted = Solved("Duna", now);
            HandedBackAt(mission, noted.Window.DepartureUt - noted.LeadS / 2);
            clock.Take(mission);

            Assert.True(mission.View.DueTime.Reached);
            Assert.Equal("the Kerbin window", mission.View.DueTime.WaitsFor);
            mission.Send(new Approve());
            Assert.Single(clock.Take(mission).OfType<StartScript>());
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed"));
        }

        [Fact]
        public void HandedBackAfterTheWindowApprovingTheAscentIsTheMissedWindowHandling()
        {
            var clock = new FakeAlarmClock();
            const double now = 1203.48;
            var mission = ToSurfaceStay(StayThenAscent(), now);
            mission.Send(new Approve());
            var back = Solved("Duna", now).Window.DepartureUt + 120 * Day;
            HandedBackAt(mission, back);
            clock.Take(mission);
            Assert.True(mission.View.DueTime.Reached);

            mission.Send(new Approve());
            var commands = clock.Take(mission);

            var next = Solved("Duna", back);
            Assert.Empty(commands.OfType<StartScript>());
            Assert.Equal(next.DueUt.Value, mission.View.DueTime.Ut, 0);
            Assert.Equal(next.DueUt.Value, clock.Alarms[mission.View.MissionId], 0);
            Assert.Contains(mission.View.Log, e => e.Kind == LogKind.Notice && e.Text.Contains("was missed") && e.Text.Contains("the Due Time moves to"));
        }
    }
}
