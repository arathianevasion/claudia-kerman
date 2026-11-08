// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#905: a missed return window (#887) has its own wording, not the transfer text; a missed transfer window keeps its own.</summary>
    public sealed partial class WindowWaitTests
    {
        [Fact]
        public void AMissedReturnWindowIsWordedAsAReturnInTheLogThePromptAndTheNotice()
        {
            double window, dueBy;
            var snapshot = WindowPastItsAlignment(out window, out dueBy);
            var clock = new FakeAlarmClock();
            var transport = new StreamingFakeTransport(ReturnMissionPlan(), ReturnSequencePlan(), ReturnMissionPlan());
            var mission = Control(transport);
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(snapshot);
            CraftCheckRouter.StartChecked(mission, _background, snapshot, new StartMission("Return to Kerbin.", 10m, "claude-sonnet-5-5", window: WindowPicks.AsTheSetting(snapshot, "Return to Kerbin.")));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            clock.Take(mission);

            var back = window + 600;
            mission.Send(new UtAdvanced(back));
            mission.Send(OverTheMun(snapshot.Orbit.Lan, back));
            clock.Take(mission);
            mission.Send(new Approve());
            Assert.Empty(clock.Take(mission).OfType<StartScript>());

            var ut = Regex.Escape(((long)System.Math.Round(window)).ToString());
            var line = mission.View.Log.Single(e => e.Text.StartsWith("Missed the return window")).Text;
            Assert.Matches("^Missed the return window the Due Time waited for \\(UT " + ut + "\\)\\. (Next cheap window: UT \\d+ \\(in [\\d.]+ h\\)\\.|No return window found within the alignment plus 60 h\\.)$", line);
            Assert.DoesNotContain(mission.View.Log, e => e.Text.Contains("was missed") || e.Text.Contains("no next window"));

            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            var asked = Messages(transport.Requests.Last());
            Assert.Contains("The return window this step waited for has passed. ", asked);
            Assert.Matches("(`return` will take the next one at UT \\d+ unless you re-plan\\.|No return window found within the alignment plus 60 h\\.)", asked);
            Assert.DoesNotContain("Transfer Window", asked);
            Assert.DoesNotContain("transfer rows", asked);
            Assert.Contains("Revise the Mission Plan for a later window", asked);
        }

        [Fact]
        public void TheReturnTextsAreExact()
        {
            Assert.Equal("Missed the return window the Due Time waited for (UT 5000). Next cheap window: UT 72000 (in 18.6 h).", MissionPrompts.ReturnWindowMissedLine(5000, 72000, 5000));
            Assert.Equal("The return window this step waited for has passed. `return` will take the next one at UT 72000 unless you re-plan.", MissionPrompts.ReturnWindowMissed(72000));
            Assert.Equal("Missed the return window the Due Time waited for (UT 5000). No return window found within the alignment plus 60 h.", MissionPrompts.ReturnWindowMissedLine(5000, null, 5000));
            Assert.Equal("The return window this step waited for has passed. No return window found within the alignment plus 60 h.", MissionPrompts.ReturnWindowMissed(null));
            Assert.DoesNotContain("transfer rows", MissionPrompts.ReturnWindowMissedNotice);
        }

        [Fact]
        public void ATransferWindowsMissedWordingIsUnchanged()
        {
            Assert.Equal("The player came back after the Transfer Window the launch's Due Time waited for, and the mod dropped the Sequence Plan. X",
                MissionPrompts.WindowMissed("X"));
            Assert.Contains("(the transfer rows above are from now)", MissionPrompts.WindowMissedNotice);
        }
    }
}
