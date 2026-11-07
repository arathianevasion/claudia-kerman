// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// A craft that can't land on its Chute Group opens later groups when no engine will finish it (#827, ADR 0019 and 0020): survival beats saving
    /// chutes. A pod at Duna with one Mk16 per stage given, by the real predictor on the recorded snapshot's Duna, with the pod's mass set to
    /// give the speed a test wants.
    /// </summary>
    public class ChuteWidenTests
    {
        private static GameSnapshot Craft(double massT, bool above = true, params int[] stages)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-v2-kerbin-orbit.json")));
            snapshot.Orbit.Body = "Duna";
            var parts = snapshot.Craft.Parts;
            parts.Clear();
            parts.Add(new SnapshotPart { Id = 1, Name = "mk1pod", MassT = massT - 0.1 * stages.Length, CrewCapacity = 1, Tags = new List<string> { "command", "crew_seat" } });
            for (var i = 0; i < stages.Length; i++)
                parts.Add(new SnapshotPart
                {
                    Id = (uint)(10 + i), ParentId = 1, Name = "parachuteSingle", Title = "Mk16 " + (10 + i), Stage = stages[i], MassT = 0.1,
                    Tags = new List<string> { "parachute" }, Chute = ReentrySim.Mk16(),
                });
            snapshot.Craft.MassTonnes = massT;
            var duna = snapshot.Bodies.Single(b => b.Name == "Duna");
            snapshot.Altitude = above ? duna.AtmosphereHeight + 20000 : duna.AtmosphereHeight / 2;
            return snapshot;
        }

        private static double Speed(double massT, int[] stages, int extra = 0)
        {
            return ChuteGroups.ForEntryNow(Craft(massT, true, stages), false, null, true, extra).TouchdownSpeed;
        }

        /// <summary>The mass, by bisection, at which the group of stage 2 alone (extra 0) lands at <paramref name="speed"/>; the lowest of the two it stops at, within 1e-9 t.</summary>
        private static double MassAt(double speed, int[] stages, int extra = 0, bool above = true)
        {
            double lo = 1, hi = 40;
            for (var i = 0; i < 80; i++)
            {
                var mid = (lo + hi) / 2;
                if (Speed(mid, stages, extra) > speed) hi = mid;
                else lo = mid;
            }
            return lo;
        }

        private static Widened Widen(GameSnapshot snapshot, bool none = false, bool engine = false)
        {
            return ChuteGroups.Widen(snapshot, false, none, engine);
        }

        private static int[] Stages(Widened widened)
        {
            return widened.Entry.Chutes.Select(c => c.Stage).OrderByDescending(s => s).ToArray();
        }

        // ---- The worked examples ----

        [Fact]
        public void Stage2OverTheLimitWithNoEngineAndStage0AboardOpensStage2AndStage0()
        {
            // One chute in stage 2 predicts 20 m/s; with the stage-0 chute added it is under the limit.
            var mass = MassAt(20, new[] { 2, 0 });
            var widened = Widen(Craft(mass, true, 2, 0));

            Assert.Equal(new[] { 2, 0 }, Stages(widened));
            Assert.Equal(1, widened.Extra);
            Assert.InRange(widened.Before, 19.9, 20.1);
            Assert.InRange(widened.Entry.TouchdownSpeed, 0, ChuteGroups.TouchdownLimit);
            Assert.Equal("", widened.Warning);
            Assert.Contains("Opening 2 chutes of stage 2, 0, 1 more than its own group: predicted 20.0 m/s with its own group,", widened.Why());
        }

        [Fact]
        public void Stage2At12WithNoEngineOpensStage2Only()
        {
            var widened = Widen(Craft(MassAt(12, new[] { 2, 0 }), true, 2, 0));

            Assert.Equal(new[] { 2 }, Stages(widened));
            Assert.Equal(0, widened.Extra);
            Assert.Equal("", widened.Why());
        }

        [Fact]
        public void Stage2At24WithAnEngineWithFuelLeavesStage0ToTheEngines()
        {
            var widened = Widen(Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0), engine: true);

            Assert.Equal(new[] { 2 }, Stages(widened));
            Assert.Equal(0, widened.Extra);
            Assert.Equal("", widened.Warning);
        }

        [Fact]
        public void Stage2At24WithNoEngineAndNothingElseAboardOpensStage2AndWarnsAboveTheAir()
        {
            var widened = Widen(Craft(MassAt(24, new[] { 2, 2 }), true, 2, 2));

            Assert.Equal(new[] { 2, 2 }, Stages(widened));
            Assert.Equal(0, widened.Extra);
            Assert.Contains("Nothing can land this craft safely", widened.Warning);
            Assert.Contains("every chute it can open and no engine", widened.Warning);
        }

        [Fact]
        public void APredictorErrorMeansItsOwnGroupAsNormal()
        {
            var snapshot = Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0);
            snapshot.Orbit.Body = "Nowhere";

            var widened = Widen(snapshot);

            Assert.Equal(new[] { 2 }, Stages(widened));
            Assert.Equal(-1, widened.Entry.TouchdownSpeed);
            Assert.Equal(0, widened.Extra);
            Assert.Equal("", widened.Warning);
        }

        [Fact]
        public void ChutesNoneWithNoEngineAndChutesAboardIsOverriddenAndLogged()
        {
            var snapshot = Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0);

            var widened = Widen(snapshot, none: true);

            Assert.True(widened.Overrode);
            Assert.Equal(new[] { 2, 0 }, Stages(widened));
            Assert.StartsWith("chutes none overridden: no engine, and a crash otherwise. Opening", widened.Why());
        }

        [Fact]
        public void ChutesNoneIsHonouredWithAnEngine()
        {
            var fast = Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0);
            var withEngine = Widen(fast, none: true, engine: true);
            Assert.Empty(withEngine.Entry.Chutes);
            Assert.False(withEngine.Overrode);

            // An engine also keeps "none" when the fall can't be predicted.
            fast.Orbit.Body = "Nowhere";
            Assert.Empty(Widen(fast, none: true, engine: true).Entry.Chutes);
        }

        [Fact]
        public void ChutesNoneWithNoEngineAPredictorErrorAndNoChutesAboardOverridesNothing()
        {
            var widened = Widen(Craft(5, true), none: true);

            Assert.False(widened.Overrode);
            Assert.Empty(widened.Entry.Chutes);
        }

        [Fact]
        public void ChutesNoneWithNoEngineAndAPredictorErrorOpensTheOwnGroupOnlyAndSaysThePredictionFailed()
        {
            var snapshot = Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0);
            snapshot.Orbit.Body = "Nowhere";

            var widened = Widen(snapshot, none: true);

            Assert.True(widened.Overrode);
            Assert.Equal(new[] { 2 }, Stages(widened));
            Assert.Equal(0, widened.Extra);
            Assert.StartsWith("chutes none overridden: no engine, and the touchdown prediction failed", widened.Why());
            Assert.DoesNotContain("a crash otherwise", widened.Why());
        }

        [Fact]
        public void ChutesNoneWithNoEngineAndAKnownSoftLandingStaysNone()
        {
            // With "none" the own group is empty, so the known speed is the fall with no chute: here a light craft on a weak-gravity Eve, 9.5 m/s.
            var snapshot = Weak(0.05);
            Assert.InRange(ChuteGroups.ForEntryNow(snapshot, false, null, false).TouchdownSpeed, 0, ChuteGroups.TouchdownLimit);

            var widened = Widen(snapshot, none: true);

            Assert.Empty(widened.Entry.Chutes);
            Assert.False(widened.Overrode);
            Assert.Equal("", widened.Why());
        }

        [Fact]
        public void ChutesNoneWithNoEngineAndAKnownFastFallIsOverriddenAsBefore()
        {
            // The same craft with gravity 0.3 of Eve's falls at 23 m/s with no chute: a certain crash, #827's override.
            var snapshot = Weak(0.3);
            Assert.True(ChuteGroups.ForEntryNow(snapshot, false, null, false).TouchdownSpeed > ChuteGroups.TouchdownLimit);

            var widened = Widen(snapshot, none: true);

            Assert.True(widened.Overrode);
            Assert.NotEmpty(widened.Entry.Chutes);
            Assert.StartsWith("chutes none overridden: no engine, and a crash otherwise.", widened.Why());
        }

        /// <summary>A light craft at Eve with its gravity set to <paramref name="share"/> of the real figure, so its fall with no chute is a known speed near the limit.</summary>
        private static GameSnapshot Weak(double share)
        {
            var snapshot = Craft(0.21, true, 2, 0);
            snapshot.Orbit.Body = "Eve";
            snapshot.Bodies.Single(b => b.Name == "Eve").Gm *= share;
            return snapshot;
        }

        // ---- The limit and the order ----

        [Fact]
        public void ExactlyTheLimitOpensNothingExtraAndJustOverDoes()
        {
            var stages = new[] { 2, 0 };
            var at = MassAt(ChuteGroups.TouchdownLimit, stages);
            var under = Craft(at, true, stages);
            var over = Craft(at + 1e-6, true, stages);

            Assert.InRange(ChuteGroups.ForEntryNow(under, false).TouchdownSpeed, ChuteGroups.TouchdownLimit - 1e-3, ChuteGroups.TouchdownLimit);
            Assert.Equal(0, Widen(under).Extra);
            Assert.True(ChuteGroups.ForEntryNow(over, false).TouchdownSpeed > ChuteGroups.TouchdownLimit);
            Assert.Equal(1, Widen(over).Extra);
        }

        [Fact]
        public void GroupsAreAddedOneAtATimeAndItStopsOnceSafe()
        {
            // One chute about 22 m/s, two about 15.6, three about 12.7: it takes the second and the third, and leaves the fourth.
            var stages = new[] { 3, 2, 1, 0 };
            var widened = Widen(Craft(MassAt(22, stages), true, stages));

            Assert.Equal(2, widened.Extra);
            Assert.Equal(new[] { 3, 2, 1 }, Stages(widened));
            Assert.InRange(widened.Entry.TouchdownSpeed, 0, ChuteGroups.TouchdownLimit);
        }

        [Fact]
        public void StageMinusOneComesAfterEveryNumberedStage()
        {
            var stages = new[] { 0, -1, 2 };
            var widened = Widen(Craft(MassAt(40, stages), true, stages));

            // Even a fall this fast opens in the order 2, 0, -1.
            Assert.Equal(new[] { 2, 0, -1 }, Stages(widened));
            Assert.Equal(2, widened.Extra);
        }

        // ---- The warning ----

        [Fact]
        public void TheWarningIsGivenAboveTheAirOnlyAndNeverWhenAnEngineFinishesIt()
        {
            var mass = MassAt(40, new[] { 2 });

            Assert.NotEqual("", Widen(Craft(mass, true, 2)).Warning);
            Assert.Equal("", Widen(Craft(mass, false, 2)).Warning);
            Assert.Equal("", Widen(Craft(mass, true, 2), engine: true).Warning);
            Assert.Equal("", Widen(Craft(MassAt(10, new[] { 2 }), true, 2)).Warning);
        }

        [Fact]
        public void ACraftWithNoChuteAndNoEngineIsWarnedToo()
        {
            var widened = Widen(Craft(2, true));

            Assert.Contains("with no chutes and no engine", widened.Warning);
        }

        // ---- The block call ----

        private static string Uid(uint id)
        {
            return "uid" + id;
        }

        [Fact]
        public void TheBlockCallGivesTheWidenedGroupItsStagesTheWhyAndTheWarning()
        {
            var snapshot = Craft(MassAt(20, new[] { 2, 0 }), true, 2, 0);
            var plan = new[] { new PlannedSequence { Name = "Land on Duna", DeltaV = 100 }, new PlannedSequence { Name = "Return to Kerbin", DeltaV = 600 } };

            var set = BlockCalls.ChuteGroupCall(snapshot, plan, 0, "", Uid, new Dictionary<string, object> { { "none", false }, { "engine", false } });

            Assert.Equal("", set["error"]);
            Assert.Equal(new object[] { 2.0, 0.0 }, (List<object>)set["stages"]);
            Assert.Equal(new object[] { "uid10", "uid11" }, (List<object>)set["uids"]);
            Assert.Contains("1 more than its own group", (string)set["why"]);
            Assert.Equal("", set["warning"]);

            var engine = BlockCalls.ChuteGroupCall(snapshot, plan, 0, "", Uid, new Dictionary<string, object> { { "none", false }, { "engine", true } });
            Assert.Equal(new object[] { "uid10" }, (List<object>)engine["uids"]);
            Assert.Equal("", engine["why"]);
        }

        // ---- Afterward ----

        [Fact]
        public void AfterTheWidenedGroupsOpenedTheMarginCheckFlagsTheLaterEntryAtTheDebrief()
        {
            // At Duna stage 2 and stage 0 both opened (they read as open now), so the Kerbin return has none to open.
            var snapshot = Craft(3, true, 2, 0);
            foreach (var part in snapshot.Craft.Parts.Where(p => p.Chute != null)) part.Chute.State = ReentryChuteState.Deployed;
            var plan = new[]
            {
                new PlannedSequence { Name = "Ascend and return", Intent = "Ascend to low Duna orbit, fly the return to Kerbin, then reenter and land under parachute.", DeltaV = 1500 },
            };

            var chutes = ChuteGroups.Of(snapshot, plan, 0, false, null);
            var check = MarginCheck.Of(snapshot, plan, 15, 10, false, null, chutes, "Land on Duna and come home.", 0);

            var finding = Assert.Single(check.Findings, f => f.Code.StartsWith("chutes_") || f.Code == "no_parachutes_for_return");
            Assert.Equal(CraftFindingSeverity.Blocker, finding.Severity);
            Assert.Contains("Kerbin", finding.Explanation);
        }

        [Fact]
        public void TheWarningIsInTheDebriefsEvents()
        {
            var log = new RunEventLog();
            log.Add(5, RunEventKind.ChuteWarning, "Nothing can land this craft safely: it is predicted to touch down at 24.0 m/s");

            Assert.Contains("+0 s no safe landing: Nothing can land this craft safely", log.DebriefText(5));
        }
    }
}
