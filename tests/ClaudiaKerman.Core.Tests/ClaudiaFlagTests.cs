// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// The Claudia Flag (#642): which Mission vessels fly it, and when the agency flag is Claudia's. The keeper decides from
    /// the Mission view; the adapter sets the flags and tells it what it flew.
    /// </summary>
    public class ClaudiaFlagTests
    {
        private const string Agency = "Squad/Flags/default";
        private const string Claudia = ClaudiaFlagKeeper.Url;

        private static MissionView View(MissionStage stage, string craft = "", string id = "m1", params string[] children)
        {
            var view = new MissionView
            {
                Stage = stage,
                MissionId = id,
                CanEndMission = stage != MissionStage.None && stage != MissionStage.Succeeded && stage != MissionStage.Ended && stage != MissionStage.Lost,
                CraftVesselId = craft,
            };
            foreach (var child in children) view.ChildVessels.Add(new ChildVesselView { Name = "probe", VesselId = child, Separated = child.Length > 0 });
            return view;
        }

        // ---- The agency flag ----

        [Fact]
        public void StartingAMissionSavesTheAgencyFlagAndSwitchesToClaudias()
        {
            var keeper = new ClaudiaFlagKeeper();
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.None), Agency));

            Assert.Equal(Claudia, keeper.AgencyFlagFor(View(MissionStage.WaitingForFlight), Agency));
            Assert.Equal(Agency, keeper.SavedAgencyFlag);
            // No continuous enforcement: once switched, nothing more until the Mission is no longer under way.
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.Running), Claudia));
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.Running), "Player/Flags/mine"));
        }

        [Fact]
        public void TheFlagIsRestoredWhenNoMissionIsUnderWayAnyMore()
        {
            foreach (var stop in new[] { MissionStage.Succeeded, MissionStage.Ended, MissionStage.Lost, MissionStage.None })
            {
                var keeper = new ClaudiaFlagKeeper();
                keeper.AgencyFlagFor(View(MissionStage.Planning), Agency);

                Assert.Equal(Agency, keeper.AgencyFlagFor(View(stop), Claudia));
                Assert.Null(keeper.SavedAgencyFlag);
                Assert.Null(keeper.AgencyFlagFor(View(stop), Agency));
            }
        }

        [Fact]
        public void APlayerChangedAgencyFlagIsNotRestoredOver()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.AgencyFlagFor(View(MissionStage.Running), Agency);

            // The player picked their own flag mid-Mission: it wins at the end.
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.Ended), "Player/Flags/mine"));
            Assert.Null(keeper.SavedAgencyFlag);
        }

        [Fact]
        public void ASecondMissionDoesNotSaveTheClaudiaFlagOverTheOriginal()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.AgencyFlagFor(View(MissionStage.Running, id: "m1"), Agency);

            // Another Mission takes over while the first is still under way (the switch never leaves "under way").
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.WaitingForFlight, id: "m2"), Claudia));
            Assert.Equal(Agency, keeper.SavedAgencyFlag);

            // The agency flag is already Claudia's at a later start (the player chose it): nothing is saved, nothing restored.
            var fresh = new ClaudiaFlagKeeper();
            Assert.Null(fresh.AgencyFlagFor(View(MissionStage.Running), Claudia));
            Assert.Null(fresh.SavedAgencyFlag);
            Assert.Null(fresh.AgencyFlagFor(View(MissionStage.Ended), Claudia));
        }

        [Fact]
        public void TheSavedAgencyFlagSurvivesASaveAndLoad()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.AgencyFlagFor(View(MissionStage.Running), Agency);
            var saved = keeper.SavedAgencyFlag;

            // A load mid-Mission: the save holds Claudia's flag as the agency flag and the original beside it.
            var loaded = new ClaudiaFlagKeeper();
            loaded.Load(saved);
            Assert.Null(loaded.AgencyFlagFor(View(MissionStage.Running), Claudia));
            Assert.Equal(Agency, loaded.SavedAgencyFlag);
            Assert.Equal(Agency, loaded.AgencyFlagFor(View(MissionStage.Succeeded), Claudia));

            // A save with nothing kept loads as nothing kept, and a resumed Mission saves the flag in force.
            var empty = new ClaudiaFlagKeeper();
            empty.Load(null);
            Assert.Null(empty.SavedAgencyFlag);
            Assert.Equal(Claudia, empty.AgencyFlagFor(View(MissionStage.Running), Agency));
            Assert.Equal(Agency, empty.SavedAgencyFlag);
        }

        [Fact]
        public void ALoadWithNoMissionUnderWayRestoresTheFlagItKept()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.Load(Agency);
            Assert.Equal(Agency, keeper.AgencyFlagFor(View(MissionStage.Ended), Claudia));
            Assert.Null(keeper.SavedAgencyFlag);
        }

        [Fact]
        public void PauseAndResumeLeaveTheAgencyFlagAlone()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.AgencyFlagFor(View(MissionStage.Running), Agency);

            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.Paused), Claudia));
            Assert.Equal(Agency, keeper.SavedAgencyFlag);
            Assert.Null(keeper.AgencyFlagFor(View(MissionStage.Running), Claudia));
            Assert.Equal(Agency, keeper.SavedAgencyFlag);
        }

        [Fact]
        public void AStaleSavedAgencyFlagIsRefreshedWhenAMissionStartsWithAnotherFlag()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.Load("Old/Flags/stale");

            // The player changed their flag while no Mission was under way (the save's kept one is stale).
            Assert.Equal(Claudia, keeper.AgencyFlagFor(View(MissionStage.WaitingForFlight), Agency));
            Assert.Equal(Agency, keeper.SavedAgencyFlag);

            // An empty read (no game flag yet) never wipes a kept flag.
            var kept = new ClaudiaFlagKeeper();
            kept.Load("Old/Flags/kept");
            Assert.Equal(Claudia, kept.AgencyFlagFor(View(MissionStage.Running), ""));
            Assert.Equal("Old/Flags/kept", kept.SavedAgencyFlag);
        }

        // ---- The Mission's vessels ----

        [Fact]
        public void TheCraftAndItsChildVesselsFlyTheFlagOnce()
        {
            var keeper = new ClaudiaFlagKeeper();
            // In the editor there is no craft yet.
            Assert.Empty(keeper.VesselsToFlag(View(MissionStage.WaitingForFlight)));

            var view = View(MissionStage.Running, "craft", "m1", "", "child");
            Assert.Equal(new[] { "craft", "child" }, keeper.VesselsToFlag(view));
            // Asked for again until the adapter says it flew it (a vessel it could not reach yet stays asked for).
            keeper.Flagged("craft");
            Assert.Equal(new[] { "child" }, keeper.VesselsToFlag(view));
            keeper.Flagged("child");
            Assert.Empty(keeper.VesselsToFlag(view));

            // A new Child Vessel separates: only it.
            Assert.Equal(new[] { "child2" }, keeper.VesselsToFlag(View(MissionStage.Running, "craft", "m1", "child", "child2")));
            Assert.True(keeper.IsMissionVessel(View(MissionStage.Running, "craft", "m1", "child"), "child"));
        }

        [Fact]
        public void ADockingRefliesTheMissionVesselWithTheJoinedParts()
        {
            var keeper = new ClaudiaFlagKeeper();
            var view = View(MissionStage.Running, "craft");
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(view));
            keeper.Flagged("craft");

            // A docking that doesn't touch a Mission vessel changes nothing.
            keeper.Docked("station", "tug");
            Assert.Empty(keeper.VesselsToFlag(view));

            keeper.Docked("craft", "station");
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(view));
            // The merged vessel kept the station's id: that's the craft now.
            keeper.Docked("station", "craft");
            Assert.Equal(new[] { "station" }, keeper.VesselsToFlag(View(MissionStage.Running, "station")));
        }

        [Fact]
        public void ADockingDoesNotDependOnWhetherTheMissionHasTakenItInYet()
        {
            var keeper = new ClaudiaFlagKeeper();
            keeper.VesselsToFlag(View(MissionStage.Running, "craft"));
            keeper.Flagged("craft");

            // After the Mission took the docking in, the craft is the merged vessel (the station's id)...
            keeper.Docked("craft", "station");
            Assert.Equal(new[] { "station" }, keeper.VesselsToFlag(View(MissionStage.Running, "station")));
            keeper.Flagged("station");
            // ...and before, it is the old id; the same call, the same result.
            var before = new ClaudiaFlagKeeper();
            before.VesselsToFlag(View(MissionStage.Running, "craft"));
            before.Flagged("craft");
            before.Docked("craft", "station");
            Assert.Equal(new[] { "craft" }, before.VesselsToFlag(View(MissionStage.Running, "craft")));
        }

        [Fact]
        public void AVesselThatIsGoneIsGivenUpOnButAskedForAgainAfterADockingOrALoad()
        {
            var keeper = new ClaudiaFlagKeeper();
            var view = View(MissionStage.Running, "craft", "m1", "gone");
            keeper.VesselsToFlag(view);
            keeper.Flagged("craft");
            for (var i = 0; i < ClaudiaFlagKeeper.MaxMisses; i++)
            {
                Assert.Equal(new[] { "gone" }, keeper.VesselsToFlag(view));
                keeper.Missed("gone");
            }
            Assert.Empty(keeper.VesselsToFlag(view));

            keeper.Docked("gone", "station");
            Assert.Equal(new[] { "gone" }, keeper.VesselsToFlag(view));
            keeper.Missed("gone");
            keeper.Load(null);
            Assert.Equal(new[] { "craft", "gone" }, keeper.VesselsToFlag(view));
        }

        [Fact]
        public void DebrisAndOtherVesselsAreNeverFlagged()
        {
            var keeper = new ClaudiaFlagKeeper();
            var view = View(MissionStage.Running, "craft");
            Assert.False(keeper.IsMissionVessel(view, "debris"));
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(view));

            // A finished Mission's vessels are ordinary vessels again: nothing more is flagged, though they keep the flag.
            Assert.Empty(keeper.VesselsToFlag(View(MissionStage.Ended, "craft")));
            Assert.False(keeper.IsMissionVessel(View(MissionStage.Ended, "craft"), "craft"));
        }

        [Fact]
        public void AResumeOrANewMissionFliesTheFlagAgain()
        {
            var keeper = new ClaudiaFlagKeeper();
            var view = View(MissionStage.Running, "craft");
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(view));
            keeper.Flagged("craft");
            Assert.Empty(keeper.VesselsToFlag(view));

            // A load re-applies it (decals of a reverted or quickloaded craft).
            keeper.Load(null);
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(view));
            keeper.Flagged("craft");

            // Another Mission on the same craft.
            Assert.Equal(new[] { "craft" }, keeper.VesselsToFlag(View(MissionStage.Running, "craft", "m2")));
        }
    }
}
