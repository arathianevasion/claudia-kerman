// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Tools;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>The Game Snapshot's next encounter (#748): Core's patched conics from the snapshot's orbit, in Claudia's request form only.</summary>
    public sealed class NextEncounterTests
    {
        private static GameSnapshot Snapshot()
        {
            return GameSnapshot.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshots", "kerbin-orbit-100km.json")));
        }

        /// <summary>A long Kerbin ellipse reaching out to the Mun's distance, turned until its path meets the Mun's sphere of influence.</summary>
        private static GameSnapshot OnAMunEncounter()
        {
            var snapshot = Snapshot();
            for (var argPe = 0; argPe < 360; argPe += 10)
                for (var meanAnomaly = 0.0; meanAnomaly < 6.3; meanAnomaly += 0.5)
                {
                    var o = snapshot.Orbit;
                    o.SemiMajorAxis = 6350000;
                    o.Eccentricity = 0.8898;
                    o.Inclination = 0;
                    o.ArgumentOfPeriapsis = argPe;
                    o.MeanAnomalyAtEpoch = meanAnomaly;
                    o.Epoch = snapshot.Ut;
                    if (NextEncounter.Of(snapshot) != null) return snapshot;
                }
            throw new InvalidOperationException("no scanned orbit met the Mun");
        }

        [Fact]
        public void ACraftOnAnEncounterPathCarriesTheBodyItsPeriapsisAndWhen()
        {
            var snapshot = OnAMunEncounter();
            var found = NextEncounter.Of(snapshot);

            using (var doc = JsonDocument.Parse(snapshot.ToJson(false)))
            {
                var encounter = doc.RootElement.GetProperty("encounter");
                Assert.Equal("Mun", encounter.GetProperty("body").GetString());
                Assert.Equal(found.Pe, encounter.GetProperty("pe").GetDouble());
                Assert.Equal(found.Ut, encounter.GetProperty("ut").GetDouble(), 3);
                Assert.Equal(found.SoiUt, encounter.GetProperty("soi_ut").GetDouble(), 3);
                // The SOI is entered before the periapsis is passed, and after now.
                Assert.InRange(found.SoiUt, snapshot.Ut, found.Ut);
                // Altitude above the surface, never a radius from the centre.
                Assert.InRange(found.Pe, -200000, 2400000);
            }
        }

        [Fact]
        public void ACraftWithNoEncounterOmitsTheField()
        {
            using (var doc = JsonDocument.Parse(Snapshot().ToJson(false)))
                Assert.False(doc.RootElement.TryGetProperty("encounter", out _));
        }

        [Fact]
        public void TheRecordingFormKeepsTheSnapshotAsTheGameGaveIt()
        {
            using (var doc = JsonDocument.Parse(OnAMunEncounter().ToJson(true)))
                Assert.False(doc.RootElement.TryGetProperty("encounter", out _));
        }

        [Fact]
        public void ASnapshotWithNoOrbitOrNoBodiesOmitsItWithoutFailing()
        {
            var noOrbit = Snapshot();
            noOrbit.Orbit = null;
            Assert.Null(NextEncounter.Of(noOrbit));
            var noBodies = OnAMunEncounter();
            noBodies.Bodies.Clear();
            Assert.Null(NextEncounter.Of(noBodies));
        }
    }
}
