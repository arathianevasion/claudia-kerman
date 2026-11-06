// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// What of the stage table a Mission Plan request carries by where the craft is (#680): the Craft Check's plan_ascent only on
    /// the launch pad, the Kerbin surface row only on the ground at Kerbin; the vacuum row, the TWR rows and plan_transfer rows always.
    /// </summary>
    public sealed class StageTableSituationTests
    {
        private const string Goal = "Land on the Mun and return to Kerbin.";
        private const string Ascent = "{\"target_ap\":80000}: {\"apoapsis\":80000}";
        private const string SurfaceRow = "stage_performance {\"body\":\"Kerbin\",\"situation\":\"surface\"}: ";
        private const string AscentLine = "The Craft Check's plan_ascent ";

        private static GameSnapshot Pad(Action<GameSnapshot> change = null)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "mun-shot-pad.json")));
            if (change != null) change(snapshot);
            return snapshot;
        }

        private static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        [Fact]
        public void OnThePadTheStageTableIsByteForByteWhatItWasBefore680()
        {
            var text = MissionPrompts.StageTable(Pad(), Goal, Ascent);
            Assert.Contains(SurfaceRow, text);
            Assert.EndsWith(AscentLine + Ascent + "\n", text);
            // The table as it was before #680, pinned by its length and hash.
            Assert.Equal(2729, text.Length);
            Assert.Equal("962C1C91BF3F503926AC600B70E6AB4B89BE1FD405219C95A74E388FB6EDBDD9", Sha256(text));
        }

        [Fact]
        public void InOrbitTheTableHasNeitherTheSurfaceRowNorTheCraftChecksAscentAndKeepsTheRest()
        {
            var orbit = MissionPrompts.StageTable(Pad(s => s.Situation = "ORBITING"), Goal, Ascent);
            var pad = MissionPrompts.StageTable(Pad(), Goal, Ascent);
            Assert.DoesNotContain(SurfaceRow, orbit);
            Assert.DoesNotContain(AscentLine, orbit);
            Assert.DoesNotContain("plan_ascent", orbit);
            Assert.StartsWith("\nStage table: these are the figures stage_performance would give for this craft now (JSON data). ", orbit);
            Assert.Contains("TWR in vacuum at the surface gravity of each body the Mission Goal names, per stage: ", orbit);
            // Every other line of the pad's table stays as it was: the vacuum row, the TWR row and any plan_transfer rows.
            var kept = pad.Split('\n').Skip(2).Where(l => !l.StartsWith(SurfaceRow, StringComparison.Ordinal) && !l.StartsWith(AscentLine, StringComparison.Ordinal));
            Assert.Equal(kept, orbit.Split('\n').Skip(2));
            Assert.True(orbit.Length < pad.Length);
        }

        [Theory]
        [InlineData("LANDED")]
        [InlineData("SPLASHED")]
        public void OnTheGroundAtKerbinOffThePadTheSurfaceRowStaysButNotTheCraftChecksAscent(string situation)
        {
            var text = MissionPrompts.StageTable(Pad(s => s.Situation = situation), Goal, Ascent);
            Assert.Contains(SurfaceRow, text);
            Assert.DoesNotContain(AscentLine, text);
        }

        [Fact]
        public void OnTheGroundAtAnotherBodyNeitherIsSent()
        {
            var text = MissionPrompts.StageTable(Pad(s => { s.Situation = "LANDED"; s.Body = "Mun"; }), Goal, Ascent);
            Assert.DoesNotContain(SurfaceRow, text);
            Assert.DoesNotContain(AscentLine, text);
            Assert.Contains("stage_performance {\"situation\":\"vacuum\"}: ", text);
        }
    }
}
