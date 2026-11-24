// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #1297: the <c>science</c> block's part choice. At each survey site it takes one reading per experiment, on a free part first,
    /// keeps the data aboard, and transmits only to free a part.
    /// </summary>
    public sealed class ScienceChoiceTests
    {
        /// <summary>A craft's parts of one experiment, flown through calls at several sites.</summary>
        private sealed class Flight
        {
            public readonly List<SciencePart> Parts = new List<SciencePart>();
            public int Readings, Sent;
            public readonly List<IList<ScienceStep>> Calls = new List<IList<ScienceStep>>();

            public Flight(int parts, bool rerunnable = true)
            {
                for (var i = 0; i < parts; i++) Parts.Add(new SciencePart { Rerunnable = rerunnable });
            }

            /// <summary>One call of the block; returns the readings it took. The game's science left here stays up until data is sent.</summary>
            public int Call(ScienceAsk ask)
            {
                ask.Parts = Parts;
                var steps = ScienceChoice.Choose(ask);
                Calls.Add(steps);
                var took = 0;
                for (var i = 0; i < Parts.Count; i++)
                {
                    var step = steps[i];
                    if (step.Do == "send" || step.Do == "free")
                    {
                        Parts[i].HasData = false;
                        Sent++;
                    }
                    if (step.Do == "run" || step.Do == "free")
                    {
                        Parts[i].HasData = true;
                        took++;
                        if (step.Then == "transmit")
                        {
                            Parts[i].HasData = false;
                            Sent++;
                        }
                    }
                }
                Readings += took;
                return took;
            }
        }

        private static ScienceAsk Survey(string mode = "auto", bool link = true)
        {
            return new ScienceAsk { Mode = mode, Returning = true, CanSend = link, EcPct = 90, MinEcPct = 10, Left = 8, Tracked = true };
        }

        [Fact]
        public void Six_sensors_crew_aboard_link_up_take_one_reading_at_each_of_five_sites_keep_it_and_send_nothing()
        {
            var flight = new Flight(6);
            foreach (var site in Enumerable.Range(1, 5))
                Assert.Equal(1, flight.Call(Survey()));
            Assert.Equal(0, flight.Sent);
            Assert.Equal(5, flight.Parts.Count(p => p.HasData));
        }

        [Fact]
        public void One_sensor_at_five_sites_sends_the_held_reading_to_free_the_part_then_runs_it()
        {
            var flight = new Flight(1);
            Assert.Equal("run", flight.Call(Survey()) == 1 ? flight.Calls[0][0].Do : "none");
            foreach (var site in Enumerable.Range(2, 4))
            {
                Assert.Equal(1, flight.Call(Survey()));
                Assert.Equal("free", flight.Calls.Last()[0].Do);
            }
            Assert.Equal(4, flight.Sent);
            Assert.True(flight.Parts[0].HasData);
        }

        [Fact]
        public void Nothing_is_sent_while_a_free_part_of_the_experiment_remains()
        {
            var flight = new Flight(2);
            flight.Parts[0].HasData = true;
            var steps = ScienceChoice.Choose(new ScienceAsk { Mode = "auto", CanSend = true, Left = 8, Parts = flight.Parts });
            Assert.Equal("skip", steps[0].Do);
            Assert.Equal("run", steps[1].Do);
            Assert.StartsWith("keep", steps[1].Then);
        }

        [Theory]
        [InlineData("auto", false, 50, "no free part: no antenna or no link")]
        [InlineData("auto", true, 5, "no free part: electric charge is under min_ec_pct")]
        [InlineData("keep", true, 50, "no free part: transmit is 'keep'")]
        public void With_no_free_part_and_none_freed_the_experiment_says_no_free_part(string mode, bool link, double ec, string why)
        {
            var parts = new List<SciencePart> { new SciencePart { HasData = true }, new SciencePart { HasData = true } };
            var steps = ScienceChoice.Choose(new ScienceAsk { Mode = mode, CanSend = link, EcPct = ec, MinEcPct = 10, Left = 8, Parts = parts });
            Assert.All(steps, s => Assert.Equal("skip", s.Do));
            Assert.Equal(why, steps[0].Why);
            Assert.Equal(why.Substring("no free part: ".Length), ScienceChoice.Missed(new ScienceAsk { Mode = mode, CanSend = link, EcPct = ec, MinEcPct = 10, Left = 8, Parts = parts }));
        }

        [Fact]
        public void A_part_that_is_not_rerunnable_is_not_freed()
        {
            var parts = new List<SciencePart> { new SciencePart { HasData = true, Rerunnable = false } };
            var steps = ScienceChoice.Choose(new ScienceAsk { Mode = "auto", CanSend = true, Left = 8, Parts = parts });
            Assert.Equal("skip", steps[0].Do);
            Assert.Equal("no free part: every part holds data and none is rerunnable", steps[0].Why);
        }

        [Fact]
        public void Keep_never_transmits_and_transmit_sends_held_data_on_request()
        {
            var keep = new Flight(1);
            foreach (var site in Enumerable.Range(1, 3)) keep.Call(Survey("keep"));
            Assert.Equal(0, keep.Sent);
            Assert.Equal(1, keep.Readings);

            var parts = new List<SciencePart> { new SciencePart { HasData = true }, new SciencePart { HasData = true }, new SciencePart() };
            var steps = ScienceChoice.Choose(new ScienceAsk { Mode = "transmit", CanSend = true, EcPct = 90, Left = 8, Parts = parts });
            Assert.Equal(new[] { "send", "send", "run" }, steps.Select(s => s.Do));
            Assert.Equal("transmit", steps[2].Then);
        }

        [Fact]
        public void Transmit_with_no_link_keeps_held_data_and_the_fresh_reading()
        {
            var parts = new List<SciencePart> { new SciencePart { HasData = true }, new SciencePart() };
            var steps = ScienceChoice.Choose(new ScienceAsk { Mode = "transmit", CanSend = false, EcPct = 90, Left = 8, Parts = parts });
            Assert.Equal("hold", steps[0].Do);
            Assert.Equal("keep: no antenna or no link", steps[0].Why);
            Assert.Equal("run", steps[1].Do);
            Assert.Equal("keep: no antenna or no link", steps[1].Then);
        }

        [Fact]
        public void With_no_science_left_here_nothing_runs_and_nothing_is_missed()
        {
            var parts = new List<SciencePart> { new SciencePart { HasData = true }, new SciencePart() };
            var ask = new ScienceAsk { Mode = "auto", CanSend = true, Left = 0, Parts = parts };
            Assert.Equal(new[] { "skip", "skip" }, ScienceChoice.Choose(ask).Select(s => s.Do));
            Assert.Equal("no science left here", ScienceChoice.Choose(ask)[1].Why);
            Assert.Equal("", ScienceChoice.Missed(ask));
        }

        [Fact]
        public void A_named_experiment_runs_whether_or_not_science_is_left()
        {
            var ask = new ScienceAsk { Mode = "keep", Explicit = true, Left = 0, Parts = new List<SciencePart> { new SciencePart() } };
            Assert.Equal("run", ScienceChoice.Choose(ask)[0].Do);
        }

        [Fact]
        public void In_Sandbox_an_experiment_runs_once_then_its_held_data_is_enough()
        {
            var flight = new Flight(2);
            var sandbox = new ScienceAsk { Mode = "keep", Tracked = false };
            Assert.Equal(1, flight.Call(sandbox));
            Assert.Equal(0, flight.Call(new ScienceAsk { Mode = "keep", Tracked = false }));
            Assert.Equal("", ScienceChoice.Missed(new ScienceAsk { Mode = "keep", Tracked = false, Parts = flight.Parts }));
        }

        [Fact]
        public void Inoperable_parts_unnamed_parts_and_an_unavailable_experiment_are_skipped_with_why()
        {
            var parts = new List<SciencePart> { new SciencePart { Inoperable = true }, new SciencePart { Named = false }, new SciencePart() };
            var steps = ScienceChoice.Choose(new ScienceAsk { Explicit = true, Available = false, Parts = parts });
            Assert.Equal(new[] { "inoperable", "not in experiments", "nothing to measure in this situation" }, steps.Select(s => s.Why));
            Assert.All(steps, s => Assert.Equal("skip", s.Do));
        }
    }
}
