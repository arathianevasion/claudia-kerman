// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #776: the Craft Check counted a moon's capture twice (transfer:Mun holds it, capture:Mun repeated it) and left the landing's
    /// gravity loss to Claudia. The mod now gives the finished figures, and keeps each check's request and reply.
    /// </summary>
    public sealed class CraftCheckDeltaVTests : IDisposable
    {
        private const string Goal = "Land on the Mun and return to Kerbin";
        private readonly Background _background = new Background(e => throw new Exception("background error", e));
        private readonly TempDir _save = new TempDir();
        private readonly List<string> _log = new List<string>();

        public void Dispose()
        {
            _save.Dispose();
        }

        /// <summary>Mun Shot v2's shape: 7,393 m/s in vacuum, the last stage (a TWR of about 6 on the Mun) flying both the landing and the ascent.</summary>
        private static GameSnapshot MunShot(bool stages = true)
        {
            var snapshot = new GameSnapshot { Body = "Kerbin", Situation = "PRELAUNCH", DeltaVSimulator = true, Bodies = DeltaVMapTests.StockSystem() };
            snapshot.Craft.Name = "Mun Shot v2";
            if (!stages) return snapshot;
            snapshot.Stages.Add(new SnapshotStage { Number = 3, DeltaVVac = 2500, ThrustVacKn = 1000, StartMassT = 60, EndMassT = 30 });
            snapshot.Stages.Add(new SnapshotStage { Number = 2, DeltaVVac = 2000, ThrustVacKn = 500, StartMassT = 20, EndMassT = 10 });
            snapshot.Stages.Add(new SnapshotStage { Number = 1, DeltaVVac = 2893, ThrustVacKn = 60, StartMassT = 6.1, EndMassT = 3 });
            return snapshot;
        }

        private static string Facts(string goal, GameSnapshot snapshot)
        {
            return CraftCheckPrompts.Facts(goal, snapshot, 15, null);
        }

        private static string Line(string text, string id)
        {
            return text.Split('\n').Single(l => l.StartsWith("- " + id + ":"));
        }

        private static double Figure(string line)
        {
            var at = line.IndexOf(" m/s", StringComparison.Ordinal);
            var start = line.LastIndexOf(": ", at, StringComparison.Ordinal) + 2;
            return double.Parse(line.Substring(start, at - start).Replace(",", ""));
        }

        [Fact]
        public void TheMunLandingAndAscentCarryTheCraftsGravityLossAndNoRuleSentenceIsLeft()
        {
            var snapshot = MunShot();
            var text = Facts(Goal, snapshot);
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            var land = Line(text, "land:Mun");
            var ascend = Line(text, "ascend:Mun");
            // 10% and 0.4/TWR at a TWR of about 6: about +17%, on the bare figure.
            Assert.Equal(map.WithGravityLoss("land:Mun", 6.03), Figure(land), 0);
            Assert.Equal(map.WithGravityLoss("ascend:Mun", 6.03), Figure(ascend), 0);
            Assert.InRange(Figure(land), 660, 700);
            Assert.Contains("gravity loss", land);
            Assert.Contains("TWR 6.0", land);
            Assert.Contains(((int)Math.Round(map.Leg("land:Mun").DeltaV)).ToString() + " without", land);
            Assert.DoesNotContain("add to a landing", text);
            Assert.DoesNotContain("0.4", text);
        }

        [Fact]
        public void ATransferHoldsItsCaptureAndNoSeparateCaptureLineFollowsIt()
        {
            var text = Facts(Goal, MunShot());

            Assert.Contains("includes the capture", Line(text, "transfer:Mun"));
            Assert.DoesNotContain("- capture:Mun:", text);
            // The route's total from the lines is the transfer once, not the transfer and its capture again.
            var map = DeltaVMap.Compute(DeltaVMapTests.StockSystem(), "Kerbin");
            Assert.Equal(map.Leg("transfer:Mun").DeltaV, Figure(Line(text, "transfer:Mun")), 0);
        }

        [Fact]
        public void ACaptureStandsAloneNowhereATripLineHoldsIt()
        {
            var bodies = DeltaVMapTests.StockSystem();
            var map = DeltaVMap.Compute(bodies, "Kerbin");
            foreach (var goal in new[] { "Mun", "Ike", "Duna", "Eve" })
            {
                var text = map.ToText(bodies.Where(b => b.Name == goal).ToList());
                foreach (var line in text.Split('\n').Where(l => l.StartsWith("- capture:")))
                {
                    var body = line.Substring("- capture:".Length).Split(':')[0];
                    Assert.DoesNotContain("- transfer:" + body + ":", text);
                    Assert.DoesNotContain(">" + body + ":", text);
                }
            }
        }

        [Fact]
        public void AnUnknownTwrGivesTheBareFigureMarked()
        {
            var text = Facts(Goal, MunShot(stages: false));
            var map = DeltaVMap.Compute(DeltaVMapTests.StockSystem(), "Kerbin");

            var land = Line(text, "land:Mun");
            Assert.Equal(map.Leg("land:Mun").DeltaV, Figure(land), 0);
            Assert.Contains("bare figure", land);
            Assert.Contains("unknown", land);
        }

        [Fact]
        public void AnAtmosphereBodyAndAGoalWithNoBodyAreUnchanged()
        {
            var snapshot = MunShot();
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");

            var duna = Facts("Land on Duna", snapshot);
            Assert.Equal(map.Leg("land:Duna").DeltaV, Figure(Line(duna, "land:Duna")), 0);
            Assert.DoesNotContain("includes this craft", Line(duna, "land:Duna"));

            var none = Facts("Reach a low orbit", snapshot);
            Assert.Contains(map.ToText(new List<SnapshotBody>()), none);
            Assert.DoesNotContain("includes this craft", none);
        }

        [Fact]
        public void APlanWithSeparateTransferAndCaptureSequencesPaysTheCaptureOnce()
        {
            var snapshot = MunShot();
            var map = DeltaVMap.Compute(snapshot.Bodies, "Kerbin");
            var plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Launch to orbit", DeltaV = 3400 },
                new PlannedSequence { Name = "Transfer to the Mun", DeltaV = 860 },
                new PlannedSequence { Name = "Capture at the Mun", DeltaV = 310 },
            };

            var legs = PlanLegs.Judge(snapshot, plan, "");
            var trip = legs.Single(l => l.Sequence.Name == "Transfer to the Mun");
            var capture = legs.Single(l => l.Sequence.Name == "Capture at the Mun");

            Assert.Equal("transfer:Mun", trip.Legs);
            Assert.Equal("capture:Mun", capture.Legs);
            // Departure and capture, once between them: the map's transfer line.
            Assert.Equal(map.Leg("transfer:Mun").DeltaV, trip.Figure + capture.Figure, 3);

            // The same when one sequence is named for both, and a later one only circularizes: nothing is added.
            plan = new List<PlannedSequence>
            {
                new PlannedSequence { Name = "Launch to orbit", DeltaV = 3400 },
                new PlannedSequence { Name = "Transfer to the Mun and capture", DeltaV = 1170 },
                new PlannedSequence { Name = "Capture burn at the Mun", DeltaV = 0 },
            };
            legs = PlanLegs.Judge(snapshot, plan, "");
            Assert.Equal(map.Leg("transfer:Mun").DeltaV, legs.Sum(l => l.Figure) - legs[0].Figure, 3);
        }

        // ---- The kept request and reply ----

        private MissionControl Control(int replies, bool saveLoaded = true)
        {
            var transport = new StreamingFakeTransport(Enumerable.Range(0, replies).Select(i => ScriptedResponse.Fixture("craft-check-pass.sse", 64)).ToArray());
            var client = new ClaudeClient(transport, () => "sk-ant-test", line => { lock (_log) _log.Add(line); });
            client.Timing.Wait = (d, c) => { };
            var mission = new MissionControl(client, _background, new PlayerConfig(), () => true, new PassingScriptCheck());
            if (saveLoaded) mission.Send(new SaveLoaded(_save.Path));
            mission.Send(new SceneChanged(GameScene.Editor));
            mission.Send(GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "on-pad.json"))));
            return mission;
        }

        private void Check(MissionControl mission)
        {
            mission.Send(new RunCraftCheck(Goal, 10m, "claude-sonnet-5-5"));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (mission.View.CraftCheck.State == CraftCheckState.Running && DateTime.UtcNow < deadline)
            {
                _background.Drain();
                Thread.Sleep(2);
            }
            _background.Drain();
            Assert.Equal(CraftCheckState.Done, mission.View.CraftCheck.State);
        }

        private string[] Dumps()
        {
            var root = Path.Combine(_save.Path, "ClaudiaKerman", "craft-checks");
            return Directory.Exists(root) ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToArray() : new string[0];
        }

        [Fact]
        public void ACraftChecksRequestAndReplyAreSavedAndOneLineIsLogged()
        {
            var mission = Control(1);
            Check(mission);

            var dump = Assert.Single(Dumps());
            var files = Directory.GetFiles(dump).Select(Path.GetFileName).ToArray();
            Assert.Single(files, f => f.EndsWith(".json"));
            Assert.Single(files, f => f.EndsWith(".sse"));
            Assert.Contains("Land on the Mun", File.ReadAllText(Directory.GetFiles(dump, "*.json").Single()));
            Assert.Contains("message_start", File.ReadAllText(Directory.GetFiles(dump, "*.sse").Single()));
            Assert.DoesNotContain("sk-ant-test", File.ReadAllText(Directory.GetFiles(dump, "*.json").Single()));

            string[] lines;
            lock (_log) lines = _log.Where(l => l.StartsWith("Craft-check verdict: ")).ToArray();
            var line = Assert.Single(lines);
            Assert.Contains("Pass", line);
            Assert.Contains("delta-v findings: none", line);
            Assert.Contains(dump, line);
        }

        [Fact]
        public void OnlyTheLastTenChecksAreKept()
        {
            var mission = Control(12);
            for (var i = 0; i < 12; i++) Check(mission);

            Assert.Equal(10, Dumps().Length);
        }

        [Fact]
        public void StartCopiesTheLatestChecksFilesIntoTheMissionsFolder()
        {
            var mission = Control(1);
            Check(mission);
            var dump = Assert.Single(Dumps());

            mission.Send(new StartMission(Goal, 10m, "claude-sonnet-5-5"));

            var copied = Path.Combine(_save.Path, "ClaudiaKerman", "missions", mission.View.MissionId, "craft-check");
            Assert.Equal(Directory.GetFiles(dump).Select(Path.GetFileName).OrderBy(n => n), Directory.GetFiles(copied).Select(Path.GetFileName).OrderBy(n => n));
            foreach (var file in Directory.GetFiles(dump))
                Assert.Equal(File.ReadAllText(file), File.ReadAllText(Path.Combine(copied, Path.GetFileName(file))));
        }
    }
}
