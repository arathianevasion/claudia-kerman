// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// One stock surface object as the adapter reads it from the game (#713): <c>CelestialBody.pqsSurfaceObjects</c>, its internal
    /// name, where it stands and the terrain height there. Plain data, so Core never touches the game.
    /// </summary>
    public sealed class SurfaceObject
    {
        public string Body = "";
        /// <summary>The game's internal name (<c>PQSSurfaceObject.SurfaceObjectName</c>), e.g. "RockArch00".</summary>
        public string Name = "";
        public double Latitude = double.NaN;
        public double Longitude = double.NaN;
        /// <summary>Terrain height above sea level at the object, m; NaN when not read.</summary>
        public double Altitude = double.NaN;
        /// <summary>The game marks it a launch site or a space centre facility: never a Point of Interest.</summary>
        public bool Facility;
    }

    /// <summary>A Point of Interest (GLOSSARY.md): a place to land beside, never on, after the table's names are merged with the game's positions.</summary>
    public sealed class PointOfInterest
    {
        public string Body = "";
        /// <summary>The friendly name from the table, or the game's internal name for an object the table doesn't know.</summary>
        public string Name = "";
        /// <summary>The game's internal name (the table's, or "" when unknown, for a table entry standing in).</summary>
        public string Internal = "";
        public string Note = "";
        public double Latitude;
        public double Longitude;
        /// <summary>Terrain height above sea level there, m; NaN when not read.</summary>
        public double Altitude = double.NaN;
        /// <summary>False for one there is nothing to land beside (TMA-7 is buried).</summary>
        public bool Landable = true;
    }

    /// <summary>
    /// The Points of Interest (#713): the stock anomalies, from a curated table merged with the game's own surface objects. The
    /// table gives friendly names, notes, aliases for the goal hint, a landable flag and the research positions (the Anomaly
    /// Surveyor contract pack's configs, normalized to -180..180, E and N positive); the game's positions win. A game object
    /// matches a table entry by its internal name, or else by standing within <see cref="MatchArcDeg"/> of the entry's position.
    /// The KSC, launch sites and facilities are never Points of Interest.
    /// </summary>
    internal static class PointsOfInterest
    {
        /// <summary>A game object this close to a table entry's position (degrees of arc) is that entry, whatever its name.</summary>
        public const double MatchArcDeg = 1;

        internal sealed class Entry
        {
            public string Body;
            /// <summary>The game's internal name, when known; null to match by position alone.</summary>
            public string Name;
            public string Friendly;
            public string Note = "";
            public bool Landable = true;
            /// <summary>False for the KSC, launch sites and facilities: never listed.</summary>
            public bool IsPoi = true;
            public double Latitude = double.NaN;
            public double Longitude = double.NaN;
            /// <summary>Words a Mission Goal may name it by, besides <see cref="Friendly"/> (whole words, any case).</summary>
            public string[] Aliases = new string[0];

            public bool HasPosition
            {
                get { return !double.IsNaN(Latitude) && !double.IsNaN(Longitude); }
            }
        }

        private static Entry Poi(string body, string name, string friendly, double lat, double lon, string note, params string[] aliases)
        {
            return new Entry { Body = body, Name = name, Friendly = friendly, Latitude = lat, Longitude = lon, Note = note, Aliases = aliases };
        }

        private static Entry Facility(string name)
        {
            return new Entry { Body = "Kerbin", Name = name, Friendly = name, IsPoi = false };
        }

        private const double None = double.NaN;

        /// <summary>The curated table, seeded from #713's research.</summary>
        internal static readonly Entry[] Table =
        {
            Poi("Mun", "RockArch00", "Mun Rock Arch", -12.465, -140.954, "a rock arch; one Mun arch is shattered, with an artifact inside", "rock arch", "arch"),
            Poi("Mun", "RockArch02", "Mun Rock Arch 2", None, None, "a second rock arch", "rock arch", "arch"),
            Poi("Mun", null, "Monolith TMA-5", -9.831, 25.918, "a monolith", "TMA-5", "monolith"),
            Poi("Mun", null, "Monolith TMA-6", 57.660, 9.142, "a monolith", "TMA-6", "monolith"),
            new Entry
            {
                Body = "Mun", Friendly = "Monolith TMA-7", Latitude = -82.206, Longitude = 102.928, Landable = false,
                Note = "buried about 182 m below the surface: nothing to land beside", Aliases = new[] { "TMA-7", "monolith" },
            },
            Poi("Mun", "ArmstrongMemorial", "Mun Memorial", 0.703, 22.747, "a memorial, in Northwest Crater", "memorial"),
            Poi("Mun", "UFO", "Mun Crashed UFO", -70.955, -68.136, "a crashed UFO", "crashed UFO", "UFO"),
            Poi("Minmus", null, "Monolith TMA-8", 23.776, 60.046, "a monolith", "TMA-8", "monolith"),
            Poi("Kerbin", null, "KSC Monolith", 0.102, -74.568, "the monolith by the KSC (TMA-0)", "TMA-0", "monolith"),
            Poi("Kerbin", null, "Monolith TMA-1", -0.640, -80.767, "a monolith", "TMA-1", "monolith"),
            Poi("Kerbin", null, "Monolith TMA-2", 20.671, -146.497, "a monolith", "TMA-2", "monolith"),
            Poi("Kerbin", null, "Monolith TMA-3", -28.808, -13.440, "a monolith", "TMA-3", "monolith"),
            Poi("Kerbin", null, "Monolith TMA-4", 35.571, -74.977, "a monolith", "TMA-4", "monolith"),
            Poi("Kerbin", "Pyramids", "Desert Pyramids", -6.500, -141.680, "pyramids in the desert", "pyramids", "pyramid"),
            Poi("Kerbin", "UFO", "Arctic UFO", 81.955, -128.518, "a UFO in the arctic ice", "UFO"),
            Poi("Kerbin", "IslandAirfield", "Island Airfield", None, None, "the abandoned island airfield", "island airfield"),
            Poi("Duna", "Face", "Duna Face", 17.060, -85.459, "a face in the rock", "Duna Face", "the Face"),
            Poi("Duna", "MSL", "Duna MSL Rover", -30.342, -28.810, "a rover", "MSL rover", "MSL"),
            Poi("Moho", "Mohole", "The Mohole", 90.000, 0.000, "a deep hole at the north pole", "Mohole"),
            Poi("Tylo", "Cave", "Tylo Cave", 40.176, 174.042, "a cave", "Tylo Cave", "cave"),
            Poi("Vall", "Icehenge", "Icehenge", -60.090, 83.779, "a ring of ice pillars", "Icehenge"),
            Poi("Bop", "DeadKraken", "Dead Kraken", 68.428, 117.008, "the dead kraken, in a crater", "dead kraken", "kraken"),
            Facility("KSC"), Facility("KSC2"), Facility("DesertAirfield"), Facility("Desert_Airfield"), Facility("DesertLaunchSite"),
            Facility("Desert_Launch_Site"), Facility("Woomerang"), Facility("Woomerang_Launch_Site"),
        };

        /// <summary>The KSC, a launch site or a facility, by the game's flag, the table, or the name.</summary>
        public static bool IsFacility(SurfaceObject obj)
        {
            if (obj.Facility) return true;
            var name = obj.Name ?? "";
            if (name.StartsWith("KSC", StringComparison.OrdinalIgnoreCase) || Squash(name).IndexOf("launchsite", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Table.Any(e => !e.IsPoi && SameName(e.Name, name));
        }

        /// <summary>
        /// The body's Points of Interest: each of the game's surface objects there (<paramref name="game"/>, any body; the
        /// facilities left out) under the table's name and note if it matches an entry, else under its internal name with no note,
        /// at the game's position (the table's where the game's can't be read). A table entry the game doesn't have is left out,
        /// unless the game lists nothing on the body at all (a read that failed), when the table's entries with a position stand
        /// in. Sorted landable first, then by name.
        /// </summary>
        public static List<PointOfInterest> Merge(string body, IEnumerable<SurfaceObject> game)
        {
            var entries = Table.Where(e => e.IsPoi && Same(e.Body, body)).ToList();
            var objects = (game ?? new SurfaceObject[0]).Where(o => o != null && Same(o.Body, body)).ToList();
            var found = new List<PointOfInterest>();
            if (objects.Count == 0)
            {
                foreach (var e in entries.Where(e => e.HasPosition))
                    found.Add(new PointOfInterest { Body = e.Body, Name = e.Friendly, Internal = e.Name ?? "", Note = e.Note, Latitude = e.Latitude, Longitude = e.Longitude, Landable = e.Landable });
                return Sorted(found);
            }
            var taken = new HashSet<Entry>();
            foreach (var obj in objects)
            {
                if (IsFacility(obj)) continue;
                var entry = Match(obj, entries, taken);
                if (entry != null) taken.Add(entry);
                var readable = !double.IsNaN(obj.Latitude) && !double.IsNaN(obj.Longitude);
                if (!readable && (entry == null || !entry.HasPosition)) continue;
                found.Add(new PointOfInterest
                {
                    Body = entries.Count > 0 ? entries[0].Body : obj.Body,
                    Name = entry != null ? entry.Friendly : obj.Name,
                    Internal = obj.Name ?? "",
                    Note = entry != null ? entry.Note : "",
                    Latitude = readable ? obj.Latitude : entry.Latitude,
                    Longitude = readable ? LandingSiteSearch.Wrap180(obj.Longitude) : entry.Longitude,
                    Altitude = obj.Altitude,
                    Landable = entry == null || entry.Landable,
                });
            }
            return Sorted(found);
        }

        /// <summary>The table entry a game object is: by internal name (the nearest, if several share it), else the nearest within <see cref="MatchArcDeg"/>.</summary>
        private static Entry Match(SurfaceObject obj, List<Entry> entries, HashSet<Entry> taken)
        {
            var free = entries.Where(e => !taken.Contains(e)).ToList();
            var readable = !double.IsNaN(obj.Latitude) && !double.IsNaN(obj.Longitude);
            Func<Entry, double> away = e => readable && e.HasPosition ? LandingSiteSearch.ArcDegrees(obj.Latitude, obj.Longitude, e.Latitude, e.Longitude) : 1000;
            var named = free.Where(e => SameName(e.Name, obj.Name)).OrderBy(away).FirstOrDefault();
            if (named != null) return named;
            return free.Where(e => away(e) <= MatchArcDeg).OrderBy(away).FirstOrDefault();
        }

        private static List<PointOfInterest> Sorted(List<PointOfInterest> found)
        {
            return found.OrderBy(p => p.Landable ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The Point of Interest <paramref name="name"/> names among <paramref name="pois"/>: its friendly or internal name, or one of
        /// the table's aliases when only one listed has it (any case, spaces and punctuation ignored). Null when none, or several, match.
        /// </summary>
        public static PointOfInterest Find(IList<PointOfInterest> pois, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var exact = pois.Where(p => SameName(p.Name, name) || SameName(p.Internal, name)).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) return null;
            var aliased = pois.Where(p => Table.Any(e => e.IsPoi && SameName(e.Friendly, p.Name) && e.Aliases.Any(a => SameName(a, name)))).ToList();
            return aliased.Count == 1 ? aliased[0] : null;
        }

        /// <summary>Names match ignoring case, spaces and punctuation ("TMA 8" is "TMA-8"); a null or empty name never matches.</summary>
        public static bool SameName(string a, string b)
        {
            var x = Squash(a);
            return x.Length > 0 && string.Equals(x, Squash(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Same(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static string Squash(string text)
        {
            return new string((text ?? "").Where(char.IsLetterOrDigit).ToArray());
        }
    }
}
