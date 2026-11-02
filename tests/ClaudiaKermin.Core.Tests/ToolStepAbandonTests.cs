// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Kos;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>A call ToolStep abandons at its timeout stops (#544): it queues no more surveys, and a later call isn't slowed by it.</summary>
    public sealed class ToolStepAbandonTests
    {
        /// <summary>
        /// The main thread's survey queue as a worker sees it: one lane, first come first served, each survey taking
        /// <see cref="ServeMs"/>. Like the adapter's, a worker stops waiting when its call is abandoned.
        /// </summary>
        private sealed class Lane : PlanningBodyBase
        {
            public const int ServeMs = 30;
            private readonly object _gate = new object();
            private readonly Queue<object> _line = new Queue<object>();
            public int Surveys;

            public override void Survey(string body, IList<SiteSample> samples, SiteFacts facts)
            {
                Interlocked.Increment(ref Surveys);
                var ticket = new object();
                lock (_gate)
                {
                    _line.Enqueue(ticket);
                    while (_line.Peek() != ticket) Monitor.Wait(_gate, 5);
                }
                Thread.Sleep(ServeMs);
                lock (_gate)
                {
                    _line.Dequeue();
                    Monitor.PulseAll(_gate);
                }
            }

            public override double TerrainAltitude(string body, double latitude, double longitude)
            {
                var sample = new SiteSample();
                Survey(body, new List<SiteSample> { sample }, SiteFacts.Altitude);
                return 0;
            }

            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }
            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                latitude = longitude = 0;
                return false;
            }
        }

        private sealed class Tool : IPlanningTool
        {
            public string Name { get; set; }
            public string Description { get { return "A test tool."; } }
            public string InputSchemaJson { get { return "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}"; } }
            public bool OnCraftCheck { get { return true; } }
            public string Activity { get { return "testing"; } }
            public Func<IPlanningBody, ToolOutcome> Body;
            public int Ended;

            public ToolOutcome Run(JsonNode input, GameSnapshot snapshot, IPlanningBody body)
            {
                try
                {
                    return Body(body);
                }
                finally
                {
                    Interlocked.Increment(ref Ended);
                }
            }
        }

        private static ToolOutcome Ok()
        {
            return new ToolOutcome { Json = "{\"ok\":true}", Summary = "ok" };
        }

        /// <summary>A search that never ends on its own: survey after survey.</summary>
        private static Tool Endless(string name)
        {
            return new Tool { Name = name, Body = body => { while (true) body.Survey("Mun", new List<SiteSample> { new SiteSample() }, SiteFacts.Altitude); } };
        }

        private static AssistantMessage Reply(params string[] names)
        {
            var reply = new AssistantMessage();
            for (var i = 0; i < names.Length; i++) reply.Content.Add(new ToolUseBlock { Id = "toolu_" + i, Name = names[i] });
            return reply;
        }

        private static ToolStepResult Step(Tool[] tools, Lane lane, TimeSpan timeout, params string[] names)
        {
            return ToolStep.Run(new PlanningTools(tools), Reply(names), (Func<string, string>)null, false, new GameSnapshot(), lane, timeout);
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < end) Thread.Sleep(2);
            Assert.True(condition());
        }

        [Fact]
        public void An_abandoned_call_queues_no_more_surveys_and_ends()
        {
            var lane = new Lane();
            var endless = Endless("endless");
            var result = Step(new[] { endless }, lane, TimeSpan.FromMilliseconds(100), "endless");

            Assert.True(result.Calls[0].IsError);
            Assert.Contains("took longer than", result.Calls[0].Summary);
            WaitUntil(() => endless.Ended == 1);
            var surveys = lane.Surveys;
            Thread.Sleep(4 * Lane.ServeMs);
            Assert.Equal(surveys, lane.Surveys);
        }

        [Fact]
        [Trait("Category", "Timing")]
        public void A_later_call_is_not_slowed_by_an_abandoned_one()
        {
            Timing.Within(10 * Lane.ServeMs, () =>
            {
                var lane = new Lane();
                var quick = new Tool { Name = "quick", Body = body => { body.Survey("Mun", new List<SiteSample> { new SiteSample() }, SiteFacts.Altitude); return Ok(); } };
                Step(new[] { Endless("endless"), quick }, lane, TimeSpan.FromMilliseconds(40), "endless");
                // The next step's call asks right after; the abandoned call's surveys must not be in line ahead of it.
                var started = System.Diagnostics.Stopwatch.StartNew();
                var result = Step(new[] { quick }, lane, TimeSpan.FromSeconds(5), "quick");
                Assert.False(result.Calls[0].IsError);
                Assert.True(started.Elapsed.TotalMilliseconds < 3 * Lane.ServeMs, "took " + started.Elapsed.TotalMilliseconds + " ms");
            });
        }

        [Fact]
        public void A_tool_that_finishes_in_time_is_unchanged()
        {
            var lane = new Lane();
            IPlanningBody seen = null;
            var quick = new Tool { Name = "quick", Body = body => { seen = body; body.Survey("Mun", new List<SiteSample> { new SiteSample() }, SiteFacts.Altitude); return Ok(); } };
            var result = Step(new[] { quick }, lane, TimeSpan.FromSeconds(5), "quick", "quick");

            Assert.All(result.Results.Content.OfType<ToolResultBlock>(), r => Assert.Equal("{\"ok\":true}", r.Content));
            Assert.All(result.Calls, c => Assert.False(c.IsError));
            Assert.Equal(2, lane.Surveys);
            Assert.NotNull(seen);
        }

        [Fact]
        public void A_finished_call_keeps_the_body_interfaces_clock()
        {
            var clocked = new ClockedBody();
            double seen = -1;
            var tool = new Tool { Name = "clock", Body = body => { var clock = body as IPlanningClock; seen = clock != null ? clock.ElapsedMs : -1; return Ok(); } };
            Step(new[] { tool }, null, TimeSpan.FromSeconds(5), "clock");
            Assert.Equal(-1, seen);

            ToolStep.Run(new PlanningTools(new[] { tool }), Reply("clock"), (Func<string, string>)null, false, new GameSnapshot(), clocked, TimeSpan.FromSeconds(5));
            Assert.Equal(42, seen);
        }

        private sealed class ClockedBody : PlanningBodyBase, IPlanningClock
        {
            public double ElapsedMs { get { return 42; } }
            public override double TerrainAltitude(string body, double latitude, double longitude) { return 0; }
            public override string Biome(string body, double latitude, double longitude) { return ""; }
            public override double SunElevation(string body, double latitude, double longitude, double ut) { return 0; }
            public override bool SurfaceUnder(string body, double x, double y, double z, out double latitude, out double longitude)
            {
                latitude = longitude = 0;
                return false;
            }
        }
    }
}
