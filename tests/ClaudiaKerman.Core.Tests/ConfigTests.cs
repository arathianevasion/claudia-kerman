// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.IO;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Config;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    public sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claudia-test-" + Guid.NewGuid().ToString("N"));
        public string Sub { get { return System.IO.Path.Combine(Path, "ClaudiaKerman"); } }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    public class ConfigTests
    {
        private static Func<string, string> Env(string key = null)
        {
            return name => name == "ANTHROPIC_API_KEY" ? key : null;
        }

        [Fact]
        public void EnvironmentKeyBeatsStoredKey()
        {
            using (var dir = new TempDir())
            {
                new ApiKeyStore(dir.Sub, Env()).Save("sk-from-file-1111");
                var store = new ApiKeyStore(dir.Sub, Env("sk-from-env-2222"));

                Assert.Equal("sk-from-env-2222", store.Load());
                Assert.True(store.UsingEnvironmentKey);
                Assert.Equal("sk-from-file-1111", store.LoadStored());
            }
        }

        [Fact]
        public void StoredKeyUsedWhenEnvironmentUnsetOrBlank()
        {
            using (var dir = new TempDir())
            {
                new ApiKeyStore(dir.Sub, Env()).Save("  sk-from-file-1111\n");
                Assert.Equal("sk-from-file-1111", new ApiKeyStore(dir.Sub, Env()).Load());
                Assert.Equal("sk-from-file-1111", new ApiKeyStore(dir.Sub, Env("   ")).Load());
            }
        }

        [Fact]
        public void NoKeyAnywhereLoadsNull()
        {
            using (var dir = new TempDir())
                Assert.Null(new ApiKeyStore(dir.Sub, Env()).Load());
        }

        [Fact]
        public void KeyFileAndFolderAreOwnerOnly()
        {
            if (OperatingSystem.IsWindows()) return;
            using (var dir = new TempDir())
            {
                var store = new ApiKeyStore(dir.Sub, Env());
                store.Save("sk-secret-1234");
                store.Save("sk-secret-5678"); // overwriting keeps the mode

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir.Sub));
            }
        }

        [Fact]
        public void TheManualControlLinkGraceIsFiveSecondsByDefaultAndReadFromTheFile()
        {
            using (var dir = new TempDir())
            {
                Assert.Equal(5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("manual_link_grace_s = 5", File.ReadAllText(path));

                File.WriteAllText(path, "manual_link_grace_s = 12.5\n");
                Assert.Equal(12.5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
                File.WriteAllText(path, "manual_link_grace_s = -1\n");
                Assert.Equal(5, PlayerConfig.LoadOrCreate(dir.Sub).ManualLinkGraceSeconds);
            }
        }

        [Fact]
        public void TheMarginRecheckIsAutoByDefaultAndReadFromTheFile()
        {
            using (var dir = new TempDir())
            {
                Assert.Equal(MarginRecheckMode.Auto, PlayerConfig.LoadOrCreate(dir.Sub).MarginRecheck);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("margin_recheck = auto", File.ReadAllText(path));

                File.WriteAllText(path, "margin_recheck = always\n");
                Assert.Equal(MarginRecheckMode.Always, PlayerConfig.LoadOrCreate(dir.Sub).MarginRecheck);
                File.WriteAllText(path, "margin_recheck = Never\n");
                Assert.Equal(MarginRecheckMode.Never, PlayerConfig.LoadOrCreate(dir.Sub).MarginRecheck);
                File.WriteAllText(path, "margin_recheck = sometimes\n");
                Assert.Equal(MarginRecheckMode.Auto, PlayerConfig.LoadOrCreate(dir.Sub).MarginRecheck);
            }
        }

        [Fact]
        public void ScriptIpuDefaultsTo1000IsWrittenToTheFileAndClampedTo50To2000()
        {
            using (var dir = new TempDir())
            {
                Assert.Equal(1000, PlayerConfig.LoadOrCreate(dir.Sub).ScriptIpu);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("script_ipu = 1000", File.ReadAllText(path));

                File.WriteAllText(path, "script_ipu = 400\n");
                Assert.Equal(400, PlayerConfig.LoadOrCreate(dir.Sub).ScriptIpu);
                File.WriteAllText(path, "script_ipu = 5000\n");
                Assert.Equal(2000, PlayerConfig.LoadOrCreate(dir.Sub).ScriptIpu);
                File.WriteAllText(path, "script_ipu = 10\n");
                Assert.Equal(50, PlayerConfig.LoadOrCreate(dir.Sub).ScriptIpu);
                File.WriteAllText(path, "script_ipu = fast\n");
                Assert.Equal(1000, PlayerConfig.LoadOrCreate(dir.Sub).ScriptIpu);
            }
        }

        [Fact]
        public void PhysicsWarpIsOnByDefaultWrittenToTheFileAndReadFromIt()
        {
            using (var dir = new TempDir())
            {
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("physics_warp = true", File.ReadAllText(path));

                File.WriteAllText(path, "physics_warp = false\n");
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
                File.WriteAllText(path, "physics_warp = TRUE\n");
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).PhysicsWarp);
            }
        }

        [Fact]
        public void SavingAnEmptyKeyIsRejected()
        {
            using (var dir = new TempDir())
                Assert.Throws<ArgumentException>(() => new ApiKeyStore(dir.Sub, Env()).Save("   "));
        }

        [Fact]
        public void MaskShowsOnlyTheLastFourCharacters()
        {
            Assert.Equal("****abcd", ApiKeyStore.Mask("sk-ant-api03-SECRET-abcd"));
            Assert.Equal("", ApiKeyStore.Mask(null));
            Assert.DoesNotContain("SECRET", ApiKeyStore.Mask("sk-ant-api03-SECRET-abcd"));
        }

        [Fact]
        public void ConfigFolderIsResolvedPerOs()
        {
            Assert.Equal("/Users/a/Library/Application Support/ClaudiaKerman",
                ConfigDirectory.Resolve(HostOs.MacOS, "/Users/a", n => null));
            Assert.Equal("/home/a/.config/ClaudiaKerman",
                ConfigDirectory.Resolve(HostOs.Linux, "/home/a", n => null));
            Assert.Equal("/x/cfg/ClaudiaKerman",
                ConfigDirectory.Resolve(HostOs.Linux, "/home/a", n => n == "XDG_CONFIG_HOME" ? "/x/cfg" : null));
            Assert.Equal(Path.Combine("C:\\roaming", "ClaudiaKerman"),
                ConfigDirectory.Resolve(HostOs.Windows, "C:\\u", n => n == "APPDATA" ? "C:\\roaming" : null));
        }

        [Fact]
        public void PlayerConfigIsCreatedWithDebugToolsOff()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.False(config.DebugTools);
                Assert.Contains("debug_tools = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
            }
        }

        [Fact]
        public void PlayerConfigReadsDebugToolsOn()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "# c\ndebug_tools = true\n");
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DebugTools);
            }
        }

        [Fact]
        public void PlayerConfigOffersHaikuFirstThenSonnetWithATenDollarCap()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.Equal(new[] { "claude-haiku-5-5", "claude-sonnet-5-5" }, config.Models);
                Assert.Equal("claude-haiku-5-5", config.DefaultModel);
                Assert.Equal(new[] { "claude-haiku-5-5", "claude-sonnet-5-5" }, new PlayerConfig().Models);
                Assert.Contains("\nmodels = claude-haiku-5-5, claude-sonnet-5-5\n", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
                Assert.Equal(10m, config.DefaultSpendingCap);
                Assert.Equal(150000, config.CompactionTriggerTokens);
            }
        }

        [Fact]
        public void PlayerConfigReadsModelsCapAndCompactionTriggerAndIgnoresBadValues()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName),
                    "models = claude-opus-5-5 , claude-sonnet-5-5\ndefault_spending_cap = 4.50\ncompaction_trigger = 80000\n");
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal("claude-opus-5-5", config.DefaultModel);
                Assert.Equal(4.5m, config.DefaultSpendingCap);
                Assert.Equal(80000, config.CompactionTriggerTokens);

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "models = ,\ndefault_spending_cap = -1\ncompaction_trigger = lots\n");
                config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal("claude-haiku-5-5", config.DefaultModel);
                Assert.Equal(10m, config.DefaultSpendingCap);
                Assert.Equal(150000, config.CompactionTriggerTokens);
            }
        }

        [Fact]
        public void PlayerConfigHasNoWaitCapsAndIgnoresTheWaitCapKeysAnOlderFileStillHolds()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.DoesNotContain("wait_cap", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));

                // An older config.cfg (ADR 0004): the keys are ignored, not errors, and the lines around them still read.
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName),
                    "craft_check_margin = 20\nwait_cap_orbits = 2.5\nwait_cap_ground_s = soon\nreplan_tolerance = 30\n");
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(20, config.CraftCheckMarginPercent);
                Assert.Equal(30, config.ReplanTolerancePercent);
                Assert.Null(typeof(PlayerConfig).GetField("WaitCapOrbits"));
                Assert.Null(typeof(PlayerConfig).GetField("WaitCapGroundSeconds"));
            }
        }

        [Fact]
        public void PlayerConfigReadsTheRunawayMultiplierWithThreeHundredPercentByDefault()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(3.0, config.RunawayMultiplier);
                Assert.Contains("runaway_multiplier = 300%", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 450%\n");
                Assert.Equal(4.5, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);

                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 2\n");
                Assert.Equal(2.0, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);

                // Under 100% would stop a script before its own plan says it ends.
                File.WriteAllText(Path.Combine(dir.Sub, PlayerConfig.FileName), "runaway_multiplier = 50%\n");
                Assert.Equal(3.0, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);
            }
        }

        // ---- Saving the Mission defaults (Settings, Space Center) ----

        [Fact]
        public void SavedMissionDefaultsLoadBack()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.SetDefaultModel("claude-haiku-5-5");
                config.DefaultSpendingCap = 7.5m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.MostEfficient;
                config.DefaultRemote = true;
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);

                var loaded = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.Equal(new[] { "claude-haiku-5-5", "claude-sonnet-5-5" }, loaded.Models);
                Assert.Equal("claude-haiku-5-5", loaded.DefaultModel);
                Assert.Equal(7.5m, loaded.DefaultSpendingCap);
                Assert.False(loaded.DefaultDaylightLanding);
                Assert.Equal(Missions.TimeVersusDeltaV.MostEfficient, loaded.DefaultTimeVersusDeltaV);
                Assert.True(loaded.DefaultRemote);
                Assert.True(loaded.DefaultAutoMode);
            }
        }

        [Fact]
        public void SavingMissionDefaultsRewritesOnlyTheirLinesAndKeepsEverythingElseInOrder()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                File.WriteAllText(path,
                    "# My own notes\n"
                    + "debug_tools = true\n"
                    + "models = claude-sonnet-5-5, claude-opus-5-5, claude-fable-5-1\n"
                    + "  # indented comment\n"
                    + "my_unknown_key = keep me\n"
                    + "default_spending_cap = 10\n"
                    + "\n"
                    + "price.claude-fable-5-1 = 10, 50\n"
                    + "daylight_landing = true\n"
                    + "time_vs_dv = Balanced\n"
                    + "default_remote = false\n"
                    + "default_auto_mode = false\n"
                    + "runaway_multiplier = 250%\n");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.SetDefaultModel("claude-fable-5-1");
                config.DefaultSpendingCap = 3.25m;
                config.DefaultDaylightLanding = false;
                config.DefaultTimeVersusDeltaV = Missions.TimeVersusDeltaV.Fastest;
                config.DefaultRemote = true;
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);

                Assert.Equal(
                    "# My own notes\n"
                    + "debug_tools = true\n"
                    + "models = claude-fable-5-1, claude-sonnet-5-5, claude-opus-5-5\n"
                    + "  # indented comment\n"
                    + "my_unknown_key = keep me\n"
                    + "default_spending_cap = 3.25\n"
                    + "\n"
                    + "price.claude-fable-5-1 = 10, 50\n"
                    + "daylight_landing = false\n"
                    + "time_vs_dv = Fastest\n"
                    + "default_remote = true\n"
                    + "default_auto_mode = true\n"
                    + "runaway_multiplier = 250%\n"
                    // Added at the load before: the one-time Haiku move (#639) left this edited list alone.
                    + PlayerConfig.HaikuDefaultMarkerComment + "\n"
                    + PlayerConfig.HaikuDefaultMarker + " = true\n"
                    + "# Claudia manages ship systems: panels, antennas, radiators and lights extend, retract and switch at flight transitions on the craft she flies.\n"
                    + "manage_ship_systems = true\n",
                    File.ReadAllText(path));
                Assert.Equal(2.5, PlayerConfig.LoadOrCreate(dir.Sub).RunawayMultiplier);
            }
        }

        [Fact]
        public void SavingMissionDefaultsAppendsTheKeysTheFileLacks()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                // An older file: no default_remote or default_auto_mode, and the player deleted the cap line. No final newline either.
                File.WriteAllText(path, "# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced");

                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                config.DefaultRemote = true;
                config.DefaultAutoMode = true;
                config.SaveDefaults(dir.Sub);

                var text = File.ReadAllText(path);
                Assert.StartsWith("# old\r\ndebug_tools = false\r\nmodels = claude-sonnet-5-5\r\ndaylight_landing = true\r\ntime_vs_dv = Balanced\r\n", text);
                Assert.EndsWith("default_spending_cap = 10\r\n# Mission type prefilled in the start form: false is crewed (Claudia flies aboard), true is remote (Claudia at Mission Control).\r\ndefault_remote = true\r\n"
                    + "# Start new Missions in Auto Mode: Claudia's plans after the first Mission Plan are approved for you while the Spending Cap has room.\r\ndefault_auto_mode = true\r\n"
                    + "# Claudia manages ship systems: panels, antennas, radiators and lights extend, retract and switch at flight transitions on the craft she flies.\r\nmanage_ship_systems = true\r\n", text);
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DefaultRemote);
                Assert.True(PlayerConfig.LoadOrCreate(dir.Sub).DefaultAutoMode);
            }
        }

        // #756: "Claudia manages ship systems" is on by default and persists like the other settings.
        [Fact]
        public void ManageShipSystemsIsOnByDefaultAndPersists()
        {
            using (var dir = new TempDir())
            {
                var config = PlayerConfig.LoadOrCreate(dir.Sub);
                Assert.True(config.ManageShipSystems);
                Assert.Contains("manage_ship_systems = true", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
                config.ManageShipSystems = false;
                config.SaveDefaults(dir.Sub);
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).ManageShipSystems);
            }
        }

        // ---- The one-time move to Haiku as the default model (#639) ----

        private const string OldShipped = "models = claude-sonnet-5-5, claude-haiku-5-5";

        [Fact]
        public void TheOldShippedModelsLineIsMovedToHaikuFirstOnceAndTheRestOfTheFileIsKept()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                var before = "# Claudia Kerman player config\r\ndebug_tools = true\r\n# Models offered when a Mission starts, default first.\r\n"
                    + "  " + OldShipped + "\r\nmy_unknown_key = keep me\r\ndefault_spending_cap = 6\r\n";
                File.WriteAllText(path, before);

                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.Equal(new[] { "claude-haiku-5-5", "claude-sonnet-5-5" }, config.Models);
                Assert.True(config.DebugTools);
                Assert.Equal(6m, config.DefaultSpendingCap);
                var after = File.ReadAllText(path);
                Assert.StartsWith(before.Replace(OldShipped, "models = claude-haiku-5-5, claude-sonnet-5-5"), after);
                Assert.EndsWith(PlayerConfig.HaikuDefaultMarkerComment + "\r\n" + PlayerConfig.HaikuDefaultMarker + " = true\r\n", after);

                // Runs once: the player puts Sonnet first again (the old line, exactly), and it stays.
                File.WriteAllText(path, after.Replace("models = claude-haiku-5-5, claude-sonnet-5-5", OldShipped));
                var second = File.ReadAllText(path);
                Assert.Equal("claude-sonnet-5-5", PlayerConfig.LoadOrCreate(dir.Sub).DefaultModel);
                Assert.Equal(second, File.ReadAllText(path));
            }
        }

        [Fact]
        public void AModelsLineThePlayerEditedIsLeftAloneAndMarked()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                var before = "debug_tools = false\nmodels = claude-sonnet-5-5, claude-haiku-5-5, claude-opus-5-5\nreplan_tolerance = 30";
                File.WriteAllText(path, before);

                var config = PlayerConfig.LoadOrCreate(dir.Sub);

                Assert.Equal(new[] { "claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5" }, config.Models);
                Assert.Equal(30, config.ReplanTolerancePercent);
                // Marked, so a later Settings save of exactly the old line is never moved.
                Assert.Equal(before + "\n" + PlayerConfig.HaikuDefaultMarkerComment + "\n" + PlayerConfig.HaikuDefaultMarker + " = true\n", File.ReadAllText(path));

                config.Models = new System.Collections.Generic.List<string> { "claude-sonnet-5-5", "claude-haiku-5-5" };
                config.SaveDefaults(dir.Sub);
                Assert.Equal("claude-sonnet-5-5", PlayerConfig.LoadOrCreate(dir.Sub).DefaultModel);
            }
        }

        [Fact]
        public void AMigrationThatCannotWriteIsSaidOnceAndTheFileIsReadAsItIs()
        {
            using (var dir = new TempDir())
            {
                Directory.CreateDirectory(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                File.WriteAllText(path, OldShipped + "\n");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    var warnings = new System.Collections.Generic.List<string>();

                    var config = PlayerConfig.LoadOrCreate(dir.Sub, warnings.Add);

                    Assert.Equal("claude-sonnet-5-5", config.DefaultModel);
                    Assert.Single(warnings);
                    Assert.Contains("config.cfg", warnings[0]);
                }
                finally
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void ANewConfigFileIsMarkedSoItIsNeverMigrated()
        {
            using (var dir = new TempDir())
            {
                PlayerConfig.LoadOrCreate(dir.Sub);
                var path = Path.Combine(dir.Sub, PlayerConfig.FileName);
                Assert.Contains("\n" + PlayerConfig.HaikuDefaultMarker + " = true\n", File.ReadAllText(path));

                File.WriteAllText(path, File.ReadAllText(path).Replace("models = claude-haiku-5-5, claude-sonnet-5-5", OldShipped));
                var text = File.ReadAllText(path);
                Assert.Equal("claude-sonnet-5-5", PlayerConfig.LoadOrCreate(dir.Sub).DefaultModel);
                Assert.Equal(text, File.ReadAllText(path));
            }
        }

        [Fact]
        public void AFailedSaveOfTheDefaultsSaysWhy()
        {
            using (var dir = new TempDir())
            {
                // The config folder's path is taken by a file: the write can't happen.
                Directory.CreateDirectory(dir.Path);
                File.WriteAllText(dir.Sub, "not a folder");
                var config = new PlayerConfig();

                var error = config.TrySaveDefaults(dir.Sub);

                Assert.StartsWith("Couldn't save config.cfg: ", error);
                Assert.True(error.Length > "Couldn't save config.cfg: ".Length);
            }
        }

        [Fact]
        public void ANewConfigFileOffersACrewedMissionByDefault()
        {
            using (var dir = new TempDir())
            {
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).DefaultRemote);
                Assert.Contains("default_remote = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
            }
        }

        [Fact]
        public void ANewConfigFileStartsMissionsWithAutoModeOff()
        {
            using (var dir = new TempDir())
            {
                Assert.False(PlayerConfig.LoadOrCreate(dir.Sub).DefaultAutoMode);
                Assert.Contains("default_auto_mode = false", File.ReadAllText(Path.Combine(dir.Sub, PlayerConfig.FileName)));
            }
        }

        [Fact]
        public void SavingChecksTheKeyAndReportsTheResult()
        {
            using (var dir = new TempDir())
            {
                var store = new ApiKeyStore(dir.Sub, Env());
                var background = new Background();
                var transport = new FakeTransport { Status = 401 };
                var settings = new KeySettings(store, new ClaudeClient(transport, store.Load, s => { }), background);

                settings.Save("sk-typed-9999");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (settings.Checking && DateTime.UtcNow < deadline)
                {
                    background.Drain();
                    System.Threading.Thread.Sleep(5);
                }

                Assert.Equal("Key was rejected by Anthropic.", settings.Status);
                Assert.Equal("****9999", settings.MaskedKey);
                Assert.Equal("sk-typed-9999", transport.Requests[0].Headers.Find(h => h.Key == "x-api-key").Value);
            }
        }
    }
}
