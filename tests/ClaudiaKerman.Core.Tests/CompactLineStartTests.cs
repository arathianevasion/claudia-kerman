// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>
    /// #1002: the per-turn compact Delta-v Map line of a Mission Plan request priced from Kerbin's pad for a craft away from home. It now starts
    /// where the craft is, with the Craft Check's figures (#998), and a pad start is unchanged.
    /// </summary>
    public sealed class CompactLineStartTests
    {
        private const string MinmusGoal = "land in the greater flats on minmus";

        private static GameSnapshot Load(string name)
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", name)));
        }

        private static string Whole(double v)
        {
            return v.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);
        }

        [Fact]
        public void LandedOnTheMunTheLineStartsFromTheSurfaceWithTheCraftChecksFigures()
        {
            var snapshot = Load("mun-shot-v2-landed-mun.json");
            var line = MissionPrompts.DeltaVMapLine(snapshot, MinmusGoal);
            var check = DeltaVMap.Compute(snapshot.Bodies, "Kerbin").ToText(MissionPrompts.GoalBodies(snapshot, MinmusGoal), snapshot);

            Assert.DoesNotContain("launch:Kerbin", line);
            Assert.DoesNotContain("transfer:Minmus ", line);
            Assert.Contains("ascend:Mun " + Whole(Figure(check, "ascend:Mun")), line);
            Assert.Contains("transfer:Mun>Minmus " + Whole(Figure(check, "transfer:Mun>Minmus")), line);
            Assert.StartsWith("\nDelta-v Map, m/s, no margin; budget each leg at least this: ascend:Mun ", line);
        }

        [Fact]
        public void InMinmusOrbitTheLineStartsFromThatOrbit()
        {
            var snapshot = Load("mun-shot-v2-landed-mun.json");
            snapshot.Situation = "ORBITING";
            snapshot.Body = "Minmus";
            snapshot.Orbit.Body = "Minmus";
            snapshot.Orbit.Eccentricity = 0.01;
            snapshot.Orbit.SemiMajorAxis = 100000;
            snapshot.Orbit.Periapsis = 30000;
            snapshot.Orbit.Apoapsis = 31000;
            snapshot.Altitude = 30000;
            var line = MissionPrompts.DeltaVMapLine(snapshot, MinmusGoal);
            var check = DeltaVMap.Compute(snapshot.Bodies, "Kerbin").ToText(MissionPrompts.GoalBodies(snapshot, MinmusGoal), snapshot);

            Assert.DoesNotContain("launch:Kerbin", line);
            Assert.StartsWith("\nDelta-v Map, m/s, no margin; budget each leg at least this: land:Minmus ", line);
            Assert.DoesNotContain("transfer:Minmus ", line);
            Assert.Contains("land:Minmus " + Whole(Figure(check, "land:Minmus")), line);
            Assert.Contains("return:Minmus", line);
        }

        [Fact]
        public void OnKerbinsPadTheLineIsTheOneItWas()
        {
            var snapshot = Load("on-pad.json");
            var named = MissionPrompts.GoalBodies(snapshot, MinmusGoal);
            var old = DeltaVMap.Compute(snapshot.Bodies, "Kerbin").CompactLine(named);

            Assert.StartsWith("launch:Kerbin ", old);
            Assert.Equal("\nDelta-v Map, m/s, no margin; budget each leg at least this: " + old + "\n", MissionPrompts.DeltaVMapLine(snapshot, MinmusGoal));
        }

        private static double Figure(string text, string id)
        {
            var line = text.Split('\n').Single(l => l.StartsWith("- " + id + ":", StringComparison.Ordinal));
            var at = line.IndexOf(" m/s", StringComparison.Ordinal);
            var start = line.LastIndexOf(": ", at, StringComparison.Ordinal) + 2;
            return double.Parse(line.Substring(start, at - start).Replace(",", ""));
        }
    }
}
