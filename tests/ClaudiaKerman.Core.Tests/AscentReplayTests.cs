// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The replay comparison of plan_ascent's sim with a recorded launch (#531). The fixture folder is a Mission folder as the Craft
    /// Check now leaves it (the Game Snapshot of Mun Shot v2 and plan_ascent's input) with a transcript that flew turn_shape 0.4, and
    /// a trace in KSP.log's form. The trace is SYNTHETIC until the first validation flight: the sim's own track with its pitch
    /// 7 degrees steeper from 12 km, speed 3-10% lower, and a loop that passes every 0.08 s from 8 km. Swap in a flight's lines
    /// (and update the pins) when there is one.
    ///
    /// Run it on a real flight with:
    /// <c>CLAUDIA_REPLAY_MISSION=&lt;mission folder&gt; CLAUDIA_REPLAY_LOG=&lt;KSP.log&gt; dotnet test tests/ClaudiaKerman.Core.Tests --filter RunOnARecordedFlight</c>
    /// (the report goes to the test's output and to CLAUDIA_REPLAY_OUT if set).
    /// </summary>
    public sealed class AscentReplayTests
    {
        private static string Fixture(string name)
        {
            return Path.Combine(AppContext.BaseDirectory, "Fixtures", "ascent-replay", name);
        }

        private static ReplayReport Replay()
        {
            return AscentReplay.Run(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ascent-replay"), Fixture("ksp-trace.txt"));
        }

        [Fact]
        public void TheLaunchCallTheTranscriptFlewIsTheLastOne_NotTheDocsExample()
        {
            var call = AscentReplay.LaunchCall(Fixture("transcript.json"));
            Assert.Equal("0.4", call.Single(p => p.Key == "turn_shape").Value);
            Assert.Equal("3", call.Single(p => p.Key == "stage_limit").Value);
            Assert.Equal("80000", call.Single(p => p.Key == "target_ap").Value);
        }

        // The fields launch traces, and the sim's Track rows, are the same list (#531): alt, pitch, prograde, q, throttle, t, mass, stage, ap, speeds, phase.
        [Fact]
        public void ALaunchCallWithBracketsInItsOptionsIsReadWhole()
        {
            var path = Path.Combine(Path.GetTempPath(), "replay-transcript-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"input\":{\"script\":"
                    + "\"launch(lexicon(\\\"turn_shape\\\", min(0.5, 0.6), \\\"target_ap\\\", 80000, \\\"throttle_max_q\\\", \\\"none\\\")).\"}}]}]}");
                var call = AscentReplay.LaunchCall(path);
                Assert.Equal("min(0.5, 0.6)", call.Single(p => p.Key == "turn_shape").Value);
                Assert.Equal("80000", call.Single(p => p.Key == "target_ap").Value);
                Assert.Equal("none", call.Single(p => p.Key == "throttle_max_q").Value);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ALogWithTwoLaunchesIsReadFromTheLastAndNotItsCoast()
        {
            var lines = File.ReadLines(Fixture("ksp-trace.txt")).ToList();
            var twice = lines.Where(l => l.Contains("launch trace") && !l.Contains("phase end")).Concat(lines).ToList();
            var once = AscentReplay.Run(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ascent-replay"), lines);
            var both = AscentReplay.Run(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ascent-replay"), twice);
            Assert.Equal(once.Rows.Select(r => r.GamePitch), both.Rows.Select(r => r.GamePitch));
            Assert.Equal(once.DivergeAltitude, both.DivergeAltitude);
        }

        [Fact]
        public void TheSimsTrackRowsCarryTheFieldsLaunchTraces()
        {
            Assert.Equal(new[] { "alt", "pitch", "prograde", "q", "throttle", "t", "mass", "stage", "ap", "h_speed", "v_speed", "phase" }, AscentFlight.TrackColumns);
            var line = TraceLine.Parse(File.ReadLines(Fixture("ksp-trace.txt")).First(l => l.Contains("launch trace")));
            foreach (var column in AscentFlight.TrackColumns.Where(c => c != "phase")) Assert.False(double.IsNaN(line.Num(column)), column);
            Assert.Contains(line.Text("phase"), AscentFlight.TrackPhases);
        }

        [Fact]
        public void ATraceLineParsesFromAKspLogLine()
        {
            var line = TraceLine.Parse("[LOG 12:00:01.234] [ClaudiaKerman] launch trace at UT 16020.3: phase turn, why tick, alt 1004.1, pitch 13.78, gap 0.02 ('Mun Shot v2')");
            Assert.Equal("launch", line.Block);
            Assert.Equal(16020.3, line.Ut);
            Assert.Equal("turn", line.Text("phase"));
            Assert.Equal(13.78, line.Num("pitch"));
            Assert.Equal(0.02, line.Num("gap"));
            Assert.Null(TraceLine.Parse("[ClaudiaKerman] warp trace of 'x':"));
        }

        [Fact]
        public void TheSimIsRebuiltFromTheSavedSnapshotWithTheShapeTheLaunchFlew()
        {
            var report = Replay();
            Assert.Equal(0.4, report.TurnShape);
            Assert.True(report.SimReachesOrbit, report.SimFailure);
            // Mun Shot v2 on this snapshot: plan_ascent's own figure for the ascent, ~3,194 m/s.
            Assert.InRange(report.SimDvToOrbit, 3100, 3300);
            Assert.NotEmpty(report.Rows);
        }

        [Fact]
        public void ItLinesTheGameUpAgainstTheSimEvery1000mWithTheLoopPeriod()
        {
            var report = Replay();
            Assert.Equal(report.Rows.Select(r => r.Altitude).OrderBy(a => a), report.Rows.Select(r => r.Altitude));
            Assert.All(report.Rows, r => Assert.Equal(0, r.Altitude % 1000, 6));
            var early = report.Rows.First(r => r.Altitude == 5000);
            // Before 12 km the synthetic game flies the sim's pitch.
            Assert.InRange(early.GamePitch - early.SimPitch, -0.5, 0.5);
            Assert.Equal(0.02, early.GameGap, 3);
            Assert.Equal(1, early.GameTicks, 1);
            var late = report.Rows.First(r => r.Altitude == 20000);
            Assert.InRange(late.SimPitch - late.GamePitch, 6, 8);
            Assert.Equal(0.08, late.GameGap, 3);
            Assert.Equal(4, late.GameTicks, 1);
        }

        [Fact]
        public void ItReportsWhereTheTwoFirstPartWaysAndTheirPhases()
        {
            var report = Replay();
            // Speed is 3% off until 12 km, and pitch 7 degrees off from there: the row for 12 km (its tick is just over 12,000 m) is the first to break 5 degrees.
            Assert.Equal(12000, report.DivergeAltitude);
            Assert.Equal("pitch", report.DivergeWhy);
            Assert.Equal("turn", report.DivergeSimPhase);
            Assert.Equal("turn", report.DivergeGamePhase);
        }

        [Fact]
        public void ItReportsStagingsCoastApMaxQAndTheDeltaVUsed()
        {
            var report = Replay();
            Assert.Contains(report.Stagings, s => s.Stage == 4 && !double.IsNaN(s.GameAltitude) && Math.Abs(s.GameAltitude - s.SimAltitude) < 1500);
            Assert.Equal(79990, report.GameCoastAp);
            Assert.True(report.SimCoastAp > 79000 && report.SimCoastAp < 81000, report.SimCoastAp.ToString());
            Assert.True(report.GameMaxQ > report.SimMaxQ);
            Assert.Equal(2260.4, report.GameLaunchDv);
            Assert.Equal(1865.2, report.GameCircularizeDv);
        }

        [Fact]
        public void TheTextReportHoldsEachTable()
        {
            var text = Replay().Text;
            Assert.Contains("launch call: target_ap 80000, turn_shape 0.4, stage_limit 3", text);
            Assert.Contains("every 1000 m", text);
            Assert.Contains("stagings", text);
            Assert.Contains("coast_ap (m): sim ", text);
            Assert.Contains("delta-v to orbit (m/s): sim ", text);
            Assert.Contains("first divergence at 12000 m (pitch): sim phase turn, game phase turn", text);
            Assert.Contains("launch loop:", text);
        }

        [Fact]
        public void RunOnARecordedFlight()
        {
            var mission = Environment.GetEnvironmentVariable("CLAUDIA_REPLAY_MISSION");
            var log = Environment.GetEnvironmentVariable("CLAUDIA_REPLAY_LOG");
            if (string.IsNullOrEmpty(mission) || string.IsNullOrEmpty(log)) return;
            var report = AscentReplay.Run(mission, log);
            Console.WriteLine(report.Text);
            var output = Environment.GetEnvironmentVariable("CLAUDIA_REPLAY_OUT");
            if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, report.Text);
        }
    }
}
