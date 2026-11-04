// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Config;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Roster;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>The rename from Claudia Kermin to Claudia Kerman (ADR 0015, #665): what an install from before it carries over.</summary>
    public class RenameMigrationTests
    {
        private static readonly Func<string, string> NoEnv = name => null;

        private static string MacFolder(string home, string name)
        {
            return Path.Combine(home, "Library", "Application Support", name);
        }

        private static string OldMacFolderWithKey(TempDir home, string key)
        {
            var old = MacFolder(home.Path, RenameMigration.OldFolderName);
            new ApiKeyStore(old, NoEnv).Save(key);
            File.WriteAllText(Path.Combine(old, PlayerConfig.FileName), "debug_tools = true\n");
            return old;
        }

        // ---- The config folder ----

        [Fact]
        public void TheOldConfigFolderMovesToTheNewNameAndTheApiKeyStillLoads()
        {
            using (var home = new TempDir())
            {
                var old = OldMacFolderWithKey(home, "sk-kept-1234");
                var lines = new List<string>();

                var folder = ConfigDirectory.Migrate(HostOs.MacOS, home.Path, NoEnv, lines.Add);

                Assert.Equal(MacFolder(home.Path, "ClaudiaKerman"), folder);
                Assert.Equal(ConfigDirectory.Resolve(HostOs.MacOS, home.Path, NoEnv), folder);
                Assert.False(Directory.Exists(old));
                Assert.Equal("sk-kept-1234", new ApiKeyStore(folder, NoEnv).Load());
                Assert.True(PlayerConfig.LoadOrCreate(folder).DebugTools);
                Assert.Contains("moved", Assert.Single(lines));
            }
        }

        [Fact]
        public void TheMovedKeyStaysReadableOnlyByTheUser()
        {
            if (OperatingSystem.IsWindows()) return;
            using (var home = new TempDir())
            {
                OldMacFolderWithKey(home, "sk-kept-1234");

                var folder = ConfigDirectory.Migrate(HostOs.MacOS, home.Path, NoEnv, null);

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, ApiKeyStore.FileName)));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
            }
        }

        [Fact]
        public void WhenBothConfigFoldersExistTheNewOneIsUsedAndTheOldOneLeftAlone()
        {
            using (var home = new TempDir())
            {
                var old = OldMacFolderWithKey(home, "sk-old-1111");
                var current = MacFolder(home.Path, "ClaudiaKerman");
                new ApiKeyStore(current, NoEnv).Save("sk-new-2222");
                var lines = new List<string>();

                var folder = ConfigDirectory.Migrate(HostOs.MacOS, home.Path, NoEnv, lines.Add);

                Assert.Equal(current, folder);
                Assert.Equal("sk-new-2222", new ApiKeyStore(folder, NoEnv).Load());
                Assert.Equal("sk-old-1111", new ApiKeyStore(old, NoEnv).Load());
                Assert.Empty(lines);
            }
        }

        [Fact]
        public void WithNoOldConfigFolderNothingMovesAndNothingIsCreated()
        {
            using (var home = new TempDir())
            {
                var lines = new List<string>();
                var folder = ConfigDirectory.Migrate(HostOs.Linux, home.Path, NoEnv, lines.Add);

                Assert.Equal(Path.Combine(home.Path, ".config", "ClaudiaKerman"), folder);
                Assert.False(Directory.Exists(folder));
                Assert.Empty(lines);
            }
        }

        [Fact]
        public void AFailedMoveReadsTheOldConfigFolderAndLogsOnce()
        {
            using (var home = new TempDir())
            {
                var old = OldMacFolderWithKey(home, "sk-kept-1234");
                var lines = new List<string>();

                var folder = ConfigDirectory.Migrate(HostOs.MacOS, home.Path, NoEnv, lines.Add, (from, to) => throw new IOException("busy"));

                Assert.Equal(old, folder);
                Assert.Equal("sk-kept-1234", new ApiKeyStore(folder, NoEnv).Load());
                Assert.False(Directory.Exists(MacFolder(home.Path, "ClaudiaKerman")));
                Assert.Contains("busy", Assert.Single(lines));
            }
        }

        [Fact]
        public void TheWindowsConfigFolderMovesUnderAppData()
        {
            using (var home = new TempDir())
            {
                var appData = Path.Combine(home.Path, "Roaming");
                Func<string, string> env = name => name == "APPDATA" ? appData : null;
                new ApiKeyStore(Path.Combine(appData, RenameMigration.OldFolderName), NoEnv).Save("sk-kept-1234");

                var folder = ConfigDirectory.Migrate(HostOs.Windows, home.Path, env, null);

                Assert.Equal(Path.Combine(appData, "ClaudiaKerman"), folder);
                Assert.Equal("sk-kept-1234", new ApiKeyStore(folder, NoEnv).Load());
            }
        }

        // ---- The Mission folders ----

        private static string OldSaveWithMission(TempDir save, string id)
        {
            var mission = Path.Combine(save.Path, RenameMigration.OldFolderName, "missions", id);
            Directory.CreateDirectory(mission);
            File.WriteAllText(Path.Combine(mission, MissionFolder.TranscriptFile), "{\"mission_id\":\"" + id + "\"}");
            File.WriteAllText(Path.Combine(mission, "mission.json"), "{}");
            File.WriteAllText(Path.Combine(mission, "events.jsonl"), "{}\n");
            Directory.CreateDirectory(Path.Combine(save.Path, RenameMigration.OldFolderName, "snapshots"));
            File.WriteAllText(Path.Combine(save.Path, RenameMigration.OldFolderName, "snapshots", "snapshot-ut1.json"), "{}");
            return mission;
        }

        [Fact]
        public void AnOldMissionFolderMovesToTheNewNameWithEverythingInIt()
        {
            using (var save = new TempDir())
            {
                OldSaveWithMission(save, "m20261101-160346-2997");
                var lines = new List<string>();

                var root = RenameMigration.MissionFolder(save.Path, lines.Add);

                Assert.Equal(Path.Combine(save.Path, "ClaudiaKerman"), root);
                Assert.Equal(root, MissionFolder.Root(save.Path));
                Assert.False(Directory.Exists(Path.Combine(save.Path, RenameMigration.OldFolderName)));
                var folder = MissionFolder.For(save.Path, "m20261101-160346-2997");
                foreach (var file in new[] { MissionFolder.TranscriptFile, "mission.json", "events.jsonl" })
                    Assert.True(File.Exists(Path.Combine(folder, file)), file);
                Assert.True(File.Exists(Path.Combine(root, "snapshots", "snapshot-ut1.json")));
                Assert.Equal(new[] { "m20261101-160346-2997" }, MissionHistory.Ids(save.Path).ToArray());
                Assert.Single(lines);
            }
        }

        [Fact]
        public void AMissionFolderThatCouldNotMoveIsStillReadInPlace()
        {
            using (var save = new TempDir())
            {
                var mission = OldSaveWithMission(save, "m1");
                var lines = new List<string>();

                var root = RenameMigration.MissionFolder(save.Path, lines.Add, (from, to) => throw new UnauthorizedAccessException("read-only"));

                Assert.Equal(Path.Combine(save.Path, RenameMigration.OldFolderName), root);
                Assert.Equal(root, MissionFolder.Root(save.Path));
                Assert.Equal(mission, MissionFolder.For(save.Path, "m1"));
                Assert.Equal(new[] { "m1" }, MissionHistory.Ids(save.Path).ToArray());
                Assert.Contains("read-only", Assert.Single(lines));
            }
        }

        [Fact]
        public void ANewSaveWritesUnderTheNewName()
        {
            using (var save = new TempDir())
            {
                Assert.Equal(Path.Combine(save.Path, "ClaudiaKerman"), RenameMigration.MissionFolder(save.Path, null));
                Assert.Equal(Path.Combine(save.Path, "ClaudiaKerman", "missions", "m1"), MissionFolder.For(save.Path, "m1"));
            }
        }

        [Fact]
        public void WhenBothMissionFoldersExistTheNewOneIsRead()
        {
            using (var save = new TempDir())
            {
                OldSaveWithMission(save, "m-old");
                Directory.CreateDirectory(Path.Combine(save.Path, "ClaudiaKerman", "missions", "m-new"));

                RenameMigration.MissionFolder(save.Path, null);

                Assert.Equal(new[] { "m-new" }, MissionHistory.Ids(save.Path).ToArray());
                Assert.True(Directory.Exists(Path.Combine(save.Path, RenameMigration.OldFolderName)));
            }
        }

        // ---- Her crew entry ----

        private static List<RosterAction> Plan(params KerbalSnapshot[] kerbals)
        {
            return RosterPlanner.Plan(kerbals, new RosterState(), 1000, 500, max => 0);
        }

        [Fact]
        public void HerOldEntryIsFoundByTraitAndRenamedNotTakenForAClash()
        {
            var actions = Plan(new KerbalSnapshot("Jebediah Kerman", "Pilot", KerbalKind.Crew, KerbalStanding.Available),
                new KerbalSnapshot(RenameMigration.OldName, ClaudiaIdentity.TraitName, KerbalKind.Crew, KerbalStanding.Assigned));

            var rename = Assert.Single(actions);
            Assert.Equal(RosterActionType.RevertName, rename.Type);
            Assert.Equal(RenameMigration.OldName, rename.Target);
            Assert.Equal("Claudia Kerman", rename.NewName);
        }

        [Fact]
        public void HerOldEntryIsFoundByNameWhenAModChangedHerTrait()
        {
            var actions = Plan(new KerbalSnapshot(RenameMigration.OldName, "Pilot", KerbalKind.Crew, KerbalStanding.Available));

            Assert.Equal(RosterActionType.RevertName, Assert.Single(actions).Type);
        }

        [Fact]
        public void HerOldEntryWinsOverAnotherKerbalWhoGotHerTrait()
        {
            var actions = Plan(new KerbalSnapshot("Bob Kerman", ClaudiaIdentity.TraitName, KerbalKind.Crew, KerbalStanding.Available),
                new KerbalSnapshot(RenameMigration.OldName, ClaudiaIdentity.TraitName, KerbalKind.Crew, KerbalStanding.Available));

            Assert.Equal(RenameMigration.OldName, Assert.Single(actions, a => a.Type == RosterActionType.RevertName).Target);
            Assert.Equal("Bob Kerman", Assert.Single(actions, a => a.Type == RosterActionType.RerollTrait).Target);
            Assert.DoesNotContain(actions, a => a.Type == RosterActionType.PromptRename || a.Type == RosterActionType.AddClaudia);
        }

        [Fact]
        public void HerOldEntryDeadInAnOldSaveIsRenamedThenSetMissingUnderHerNewName()
        {
            var actions = Plan(new KerbalSnapshot(RenameMigration.OldName, ClaudiaIdentity.TraitName, KerbalKind.Crew, KerbalStanding.Dead));

            Assert.Equal(new[] { RosterActionType.RevertName, RosterActionType.SetMissing }, actions.Select(a => a.Type).ToArray());
            Assert.Equal("Claudia Kerman", actions[1].Target);
        }

        [Fact]
        public void PrepareToUninstallFindsHerInAnOldSave()
        {
            var actions = UninstallPlanner.Plan(new[] { new KerbalSnapshot(RenameMigration.OldName, ClaudiaIdentity.TraitName, KerbalKind.Crew, KerbalStanding.Available) });

            Assert.Equal(RenameMigration.OldName, Assert.Single(actions).Target);
        }

        [Fact]
        public void AGameSnapshotTakenBeforeTheRosterRenameStillSeesHerAboard()
        {
            var snapshot = new GameSnapshot();
            snapshot.Crew.Add(new SnapshotCrewMember { Name = RenameMigration.OldName, Trait = ClaudiaIdentity.TraitName });
            snapshot.Crew.Add(new SnapshotCrewMember { Name = "Valentina Kerman", Trait = "Pilot" });

            Assert.True(snapshot.ClaudiaAboard);
            Assert.Equal(1, snapshot.Passengers);
        }

        // ---- The save file and the install ----

        [Fact]
        public void TheOldClaudiaFlagUrlMapsToTheNewOne()
        {
            Assert.Equal(ClaudiaFlagKeeper.Url, RenameMigration.FlagUrl(RenameMigration.OldFlagUrl));
            Assert.Equal("ClaudiaKerman/Flags/ClaudiaFlag", ClaudiaFlagKeeper.Url);
            Assert.Equal("Squad/Flags/default", RenameMigration.FlagUrl("Squad/Flags/default"));
            Assert.Null(RenameMigration.FlagUrl(null));
        }

        [Fact]
        public void BothGameDataFoldersInstalledGivesTheDuplicateMessage()
        {
            using (var gameData = new TempDir())
            {
                Directory.CreateDirectory(Path.Combine(gameData.Path, "ClaudiaKerman"));
                Assert.Equal("", RenameMigration.DuplicateInstall(gameData.Path));

                Directory.CreateDirectory(Path.Combine(gameData.Path, RenameMigration.OldFolderName));
                var message = RenameMigration.DuplicateInstall(gameData.Path);

                Assert.Contains("delete GameData/ClaudiaKermin", message);
                Assert.Contains("keep GameData/ClaudiaKerman", message);
            }
        }
    }
}
