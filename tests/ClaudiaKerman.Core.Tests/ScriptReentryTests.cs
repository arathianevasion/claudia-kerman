// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #970: the running Mission Script's <c>reentry</c> call, read so <c>deorbit</c> (and <c>return</c>) aim for the craft <c>reentry</c> flies.
    /// Flight m20261109-200745-96f6 aimed for the craft without its stack (19 t) while its <c>reentry</c> kept it (separate "none", 41 t), and landed 95 km off.
    /// </summary>
    public sealed class ScriptReentryTests : IDisposable
    {
        /// <summary>The flight's script, as approved.</summary>
        private const string FlightScript = "local res is deorbit(lexicon(\"target\", lexicon(\"lat\", 3.002, \"lng\", 1.463), \"max_wait\", 43740, \"max_dv\", 550)). "
            + "if res:burn_ut > 0 { set res to reentry(lexicon(\"separate\", \"none\", \"jettison_stage\", \"none\")). } addons:claudia:report(res).";

        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();

        public void Dispose()
        {
            _save.Dispose();
        }

        [Fact]
        public void The_flights_reentry_keeps_its_stack_and_its_shield()
        {
            var reentry = ScriptReentry.Of(FlightScript);
            Assert.True(reentry.KeepsStack);
            Assert.Equal("none", reentry.JettisonStage);
        }

        [Theory]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). if res:burn_ut > 0 { set res to reentry(). }")]
        [InlineData("set res to reentry(lexicon(\"separate\", \"auto\")).")]
        [InlineData("set res to reentry(lexicon(\"separate\", 1)).")]
        [InlineData("set res to reentry(lexicon(\"separate\", list(\"tank\"))).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\", \"separate\", \"none\")). addons:claudia:report(res).")]
        [InlineData("// set res to reentry(lexicon(\"separate\", \"none\")).\nset res to reentry().")]
        [InlineData("print \"reentry(lexicon(separate, none))\".")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_but_separate_none_is_reentrys_auto_as_before(string script)
        {
            var reentry = ScriptReentry.Of(script);
            Assert.False(reentry.KeepsStack);
            Assert.Equal("auto", reentry.JettisonStage);
        }

        /// <summary>
        /// Review of #970: a script that sheds the stack itself after deorbit then asks reentry for separate "none" (nothing left to drop)
        /// flies light, so deorbit must aim as reentry's "auto" does, not for the whole craft.
        /// </summary>
        [Theory]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). stage. set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")).\nSTAGE.\nset res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). drop_parts(lexicon(\"parts\", list(\"tank\"))). set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). exec_node(lexicon(\"drop_empty\", list(\"tank\"))). set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). tune_pe(lexicon(\"pe\", 30000)). set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). ship:partstagged(\"dec\")[0]:getmodule(\"ModuleDecouple\"):doevent(\"decouple\"). set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). toggle ag1. set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is return(lexicon(\"body\", \"kerbin\")). stage. set res to reentry(lexicon(\"separate\", \"none\")).")]
        public void A_separation_between_deorbit_and_reentry_reads_auto(string script)
        {
            Assert.False(ScriptReentry.Of(script).KeepsStack);
        }

        [Theory]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\")). print stage:number. set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("stage. local res is deorbit(lexicon(\"target\", \"ksc\")). coast(lexicon(\"ut\", time:seconds + 60)). set res to reentry(lexicon(\"separate\", \"none\")).")]
        [InlineData("local res is deorbit(lexicon(\"target\", \"ksc\", \"stage_limit\", 2)). set res to reentry(lexicon(\"separate\", \"none\", \"jettison_stage\", \"none\")).")]
        public void Nothing_separating_between_deorbit_and_reentry_still_keeps_the_stack(string script)
        {
            Assert.True(ScriptReentry.Of(script).KeepsStack);
        }

        [Fact]
        public void The_words_are_read_whatever_their_case_as_kOS_compares_them()
        {
            var reentry = ScriptReentry.Of("SET res TO REENTRY(LEXICON(\"Separate\", \"NONE\", \"JETTISON_STAGE\", \"None\")).");
            Assert.True(reentry.KeepsStack);
            Assert.Equal("none", reentry.JettisonStage);
        }

        [Fact]
        public void Reentry_calls_that_disagree_are_read_as_auto()
        {
            var reentry = ScriptReentry.Of("set a to reentry(lexicon(\"separate\", \"none\")). set b to reentry(lexicon(\"separate\", \"auto\")).");
            Assert.False(reentry.KeepsStack);
            Assert.True(ScriptReentry.Of("set a to reentry(lexicon(\"separate\", \"none\")). set b to reentry(lexicon(\"separate\", \"none\")).").KeepsStack);
        }

        [Fact]
        public void Only_the_Mission_Script_is_read_not_the_Core_Library_bundled_after_it()
        {
            Assert.True(ScriptReentry.Of(CoreLibrary.Bundle(FlightScript)).KeepsStack);
            Assert.False(ScriptReentry.Of(CoreLibrary.Bundle("set res to reentry().")).KeepsStack);
        }

        /// <summary>A block call's snapshot gets the running script's reentry through Stamp; with no Mission (a block test) it stays auto.</summary>
        [Fact]
        public void Stamp_carries_the_running_scripts_reentry_to_a_block_call()
        {
            var view = new MissionView { Stage = MissionStage.Running, ScriptReentry = ScriptReentry.Of(FlightScript) };
            Assert.True(MissionView.Stamp(new GameSnapshot(), view).ScriptReentry.KeepsStack);
            Assert.False(MissionView.Stamp(new GameSnapshot(), null).ScriptReentry.KeepsStack);
            Assert.False(new GameSnapshot().ScriptReentry.KeepsStack);
        }

        [Fact]
        public void A_running_Sequence_Plan_puts_its_scripts_reentry_on_the_view_and_none_runs_before_it()
        {
            var mission = InFlight(Transport("reentry(lexicon(\\\\\\\"separate\\\\\\\", \\\\\\\"none\\\\\\\")). print \\\\\\\"Claudia: link check.\\\\\\\"."));
            mission.Send(new StartMission("Prove the kOS link from the launch pad.", 10m, "claude-sonnet-5-5"));
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingPlanApproval);
            mission.Send(new Approve());
            DrainUntil(mission, v => v.Stage == MissionStage.AwaitingSequenceApproval);
            Assert.False(mission.View.ScriptReentry.KeepsStack);
            mission.Send(new Approve());
            Assert.Equal(MissionStage.Running, mission.View.Stage);
            Assert.True(mission.View.ScriptReentry.KeepsStack);
            Assert.True(MissionView.Stamp(new GameSnapshot(), mission.View).ScriptReentry.KeepsStack);
        }

        private static StreamingFakeTransport Transport(string escapedLine)
        {
            var sequence = Encoding.UTF8.GetString(Fixtures.Bytes("mission-sequence-plan.sse"));
            const string printLine = "print \\\\\\\"Claudia: link check.\\\\\\\".";
            Assert.Contains(printLine, sequence);
            sequence = sequence.Replace(printLine, escapedLine);
            return new StreamingFakeTransport(
                ScriptedResponse.Fixture("mission-plan.sse"),
                new ScriptedResponse { Body = Encoding.UTF8.GetBytes(sequence), ChunkSize = 4096 },
                ScriptedResponse.Fixture("mission-debrief.sse"));
        }

        private MissionControl InFlight(StreamingFakeTransport transport)
        {
            var client = new ClaudeClient(new CraftCheckRouter(transport), () => "sk-ant-test", l => { });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Flight));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
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
