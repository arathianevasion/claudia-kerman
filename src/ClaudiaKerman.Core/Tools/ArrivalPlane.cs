// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What a block's <c>plane</c> option says (#788): "auto", "none", a number, a body's name, or a vessel's orbit.</summary>
    internal sealed class PlaneOption
    {
        public const string AutoWord = "auto";
        public const string NoneWord = "none";

        /// <summary>"auto", "none" or a body's name; null for a number or an orbit.</summary>
        public string Word = AutoWord;
        public double? Inc;
        /// <summary>A vessel's plane, degrees (its orbit's inclination and LAN), the vessel's name and the body it orbits.</summary>
        public double? OrbitInc;
        public double OrbitLan;
        public string VesselName = "";
        /// <summary>The vessel is on the ground, where its orbit says nothing of a plane.</summary>
        public bool VesselDown;

        public static readonly PlaneOption Auto = new PlaneOption();

        /// <summary>The option as a block call's input gives it: a string, a number, or a lexicon of inc, lan, name and body. Null or missing: auto.</summary>
        public static PlaneOption Read(object value)
        {
            if (value is double) return new PlaneOption { Word = null, Inc = (double)value };
            var text = value as string;
            if (text != null) return new PlaneOption { Word = text };
            var orbit = value as IDictionary<string, object>;
            if (orbit == null) return Auto;
            object inc, lan, name, status;
            orbit.TryGetValue("inc", out inc);
            orbit.TryGetValue("lan", out lan);
            orbit.TryGetValue("name", out name);
            orbit.TryGetValue("status", out status);
            return new PlaneOption { Word = null, OrbitInc = inc as double?, OrbitLan = lan as double? ?? 0, VesselName = name as string ?? "", VesselDown = GameSnapshot.IsGroundedSituation(status as string ?? "") };
        }

        public bool IsAuto
        {
            get { return string.Equals(Word, AutoWord, StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsNone
        {
            get { return string.Equals(Word, NoneWord, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// The arrival plane (GLOSSARY, #788): the plane and direction of travel a craft should arrive in at <see cref="Arrival"/>, set on
    /// the approach by <see cref="TransferFlight"/> and flown by <c>capture</c> (<see cref="CaptureFlight"/>). A body or vessel gives the
    /// whole plane, as a <see cref="Normal"/> whose sign is the direction of travel, so a moon at 6° is never met at 174°; a number or a
    /// landing site gives only an <see cref="Inc"/> (over 90° retrograde), the crossing point free.
    /// </summary>
    internal sealed class ArrivalPlane
    {
        /// <summary>How far past a site's latitude the orbit is inclined, degrees: the body turns the site under the orbit.</summary>
        public const double SiteMargin = 0.5;
        /// <summary>The <see cref="Source"/> of a record that says Claudia asked for no plane, so <c>capture</c> doesn't work one out.</summary>
        public const string NoneSource = "none";

        public string Arrival = "";
        /// <summary>Who picked it: "number", "body", "vessel", "target_vessel", "site", "moon", or "home" for a trip home's far-out aim (#986).</summary>
        public string Source = "";
        /// <summary>The whole plane, a unit vector along the angular momentum wanted; null when only the inclination is asked.</summary>
        public Vec3? Normal;
        /// <summary>The inclination wanted, degrees from 0 to 180.</summary>
        public double Inc;
        /// <summary>The periapsis altitude to arrive and capture at, m: a Target Vessel's round the arrival body (#788 item 6); null otherwise.</summary>
        public double? VesselPe;

        /// <summary>The plane through a point: its normal, and the degrees from there to the plane wanted (0 when it reaches it).</summary>
        internal struct Reach
        {
            public Vec3 Normal;
            public double LeftoverDeg;
        }

        /// <summary>
        /// The plane closest to this one that contains the direction <paramref name="through"/>: a burn at a point can only reach planes
        /// through that point, and any plane containing the approach direction is nearly free. With a choice (the two planes of one
        /// inclination through a point), the nearer to <paramref name="current"/>, the normal of the orbit flown now. Pure.
        /// </summary>
        public Reach Through(Vec3 through, Vec3 current)
        {
            var r = through.Normalized;
            if (Normal.HasValue)
            {
                var wanted = Normal.Value;
                var on = wanted - r * Vec3.Dot(wanted, r);
                if (on.Length < 1e-6) return new Reach { Normal = OrKeep(current, r), LeftoverDeg = 90 };
                var n = on.Normalized;
                return new Reach { Normal = n, LeftoverDeg = Angle(n, wanted) };
            }
            var north = new Vec3(0, 0, 1);
            var up = north - r * Vec3.Dot(north, r);
            if (up.Length < 1e-6) return new Reach { Normal = OrKeep(current, r), LeftoverDeg = Math.Abs(90 - Inc) };
            var cosInc = Math.Cos(Inc * Math.PI / 180);
            var a = cosInc / up.Length;
            var u = up.Normalized;
            if (Math.Abs(a) >= 1)
            {
                var pole = u * Math.Sign(a);
                return new Reach { Normal = pole, LeftoverDeg = Math.Abs(Deg(Math.Acos(Clamp(pole.Z))) - Inc) };
            }
            var side = Vec3.Cross(r, u) * Math.Sqrt(1 - a * a);
            var one = u * a + side;
            var other = u * a - side;
            return new Reach { Normal = Vec3.Dot(one, current) >= Vec3.Dot(other, current) ? one : other, LeftoverDeg = 0 };
        }

        /// <summary>The degrees between an orbit of normal <paramref name="w"/> and this plane, direction included.</summary>
        public double OffBy(Vec3 w)
        {
            if (Normal.HasValue) return Angle(w, Normal.Value);
            return Math.Abs(Deg(Math.Acos(Clamp(w.Z))) - Inc);
        }

        private static Vec3 OrKeep(Vec3 current, Vec3 r)
        {
            var on = current - r * Vec3.Dot(current, r);
            return on.Length < 1e-9 ? current : on.Normalized;
        }

        internal static double Angle(Vec3 a, Vec3 b)
        {
            return Deg(Math.Atan2(Vec3.Cross(a, b).Length, Vec3.Dot(a, b)));
        }

        private static double Deg(double radians)
        {
            return radians * 180 / Math.PI;
        }

        private static double Clamp(double x)
        {
            return Math.Max(-1, Math.Min(1, x));
        }

        // ---- The record: what Core keeps for capture between transfer_to and it, in the Mission's save ----

        /// <summary>The plane as the Mission keeps it, one line of invariant numbers and plain words.</summary>
        public string ToRecord()
        {
            var n = Normal ?? default(Vec3);
            return string.Join("|", new[]
            {
                Arrival, Source, Num(Inc), Normal.HasValue ? "1" : "0", Num(n.X), Num(n.Y), Num(n.Z), VesselPe.HasValue ? Num(VesselPe.Value) : "",
            });
        }

        /// <summary>The plane from <see cref="ToRecord"/>; null for an empty or unreadable record.</summary>
        public static ArrivalPlane FromRecord(string record)
        {
            if (string.IsNullOrEmpty(record)) return null;
            var parts = record.Split('|');
            double inc, x, y, z, pe;
            if (parts.Length != 8 || !Read(parts[2], out inc) || !Read(parts[4], out x) || !Read(parts[5], out y) || !Read(parts[6], out z)) return null;
            var plane = new ArrivalPlane { Arrival = parts[0], Source = parts[1], Inc = inc };
            if (parts[3] == "1") plane.Normal = new Vec3(x, y, z);
            if (Read(parts[7], out pe)) plane.VesselPe = pe;
            return plane;
        }

        private static string Num(double x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Read(string text, out double x)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
        }
    }

    /// <summary>A landing site <c>find_landing_site</c> gave this Mission (#788): the body and latitude, for the next leg's arrival plane.</summary>
    public sealed class PickedSite
    {
        public string Body = "";
        public double Latitude;
    }

    /// <summary>
    /// Who picks the arrival plane (#788 item 3), first wins: Claudia's explicit value; a Target Vessel orbiting the arrival body;
    /// the next leg's landing site (<c>find_landing_site</c>'s pick, a Point of Interest, a landed Target Vessel, or a latitude in its
    /// words, or a pole or biome its words or the Mission Goal name, #806, or the equator its words name, #986); the next leg's moon of this body; none, as before.
    /// A trip that ends in reentry gets none from "auto" (a leg in hand that names its capture doesn't, #986); a trip home's far-out aim is <see cref="Home"/>. Pure.
    /// </summary>
    internal static class ArrivalPlanes
    {
        /// <summary>A pair of signed numbers in a leg's words, "(3.1, 24.6)": a site's latitude and longitude.</summary>
        private static readonly Regex LatLng = new Regex(@"\(\s*(-?\d+(?:\.\d+)?)\s*°?\s*,\s*-?\d+(?:\.\d+)?\s*°?\s*\)");
        /// <summary>A pole in a leg's or goal's words (#806); "non-polar" and "pole-mounted" are not.</summary>
        private static readonly Regex PoleWord = new Regex(@"(?<!\w)(?<!non-)(poles?|polar)(?!\w)(?!-mounted)", RegexOptions.IgnoreCase);
        /// <summary>The equator in a leg's words (#986): "equator", "equatorial"; "non-equatorial" is not.</summary>
        private static readonly Regex EquatorWord = new Regex(@"(?<![\w-])equator(ial)?(?!\w)", RegexOptions.IgnoreCase);
        /// <summary>Words before the equator in its clause that say away from it (#986): "far from the equator", "not on the equator", "off the equator".</summary>
        private static readonly Regex NotEquator = new Regex(@"(?:\b(?:far|away)\s+from|\b(?:not|off|avoid(?:s|ing)?)\b[^,.;]*?)\s+(?:the\s+)?$", RegexOptions.IgnoreCase);
        /// <summary>Words before a capture in its clause that say none flies (#986): "skip capture", "no capture", "without capturing".</summary>
        private static readonly Regex NoCapture = new Regex(@"\b(?:no|not|never|skip(?:s|ping)?|without|don['’]t)\b", RegexOptions.IgnoreCase);
        private static readonly char[] ClauseEnds = { ',', '.', ';' };

        /// <summary>
        /// The direction an arrival on <paramref name="orbit"/> comes in from: a hyperbola's incoming asymptote, the line any plane cheap to reach contains
        /// (not the point it enters the sphere of influence, which is off it by the miss distance, 10 degrees at the Mun); a closed orbit's position at <paramref name="ut"/>.
        /// </summary>
        public static Vec3 WayIn(Conic orbit, double ut)
        {
            if (orbit.Closed) return orbit.Position(ut);
            var nu = Math.Acos(-1 / orbit.E);
            return orbit.P * Math.Cos(nu) - orbit.Q * Math.Sin(nu);
        }

        /// <summary>The plane for an arrival at <paramref name="arrival"/>; null for none; <paramref name="error"/> says why the option can't be read.</summary>
        public static ArrivalPlane Resolve(GameSnapshot snapshot, BodySystem system, SnapshotBody arrival, PlaneOption option, out string error)
        {
            error = "";
            option = option ?? PlaneOption.Auto;
            var legs = snapshot.LegsAhead ?? new List<PlannedSequence>();
            if (option.IsNone) return null;
            if (option.Inc.HasValue)
            {
                if (!(option.Inc.Value >= 0 && option.Inc.Value <= 180))
                {
                    error = "plane must be an inclination from 0 to 180 degrees, not " + option.Inc.Value.ToString("0.###", CultureInfo.InvariantCulture);
                    return null;
                }
                return new ArrivalPlane { Arrival = arrival.Name, Source = "number", Inc = option.Inc.Value };
            }
            if (option.OrbitInc.HasValue)
            {
                if (option.VesselDown)
                {
                    error = "plane can't be " + option.VesselName + ": it is on the ground, so it has no plane to match";
                    return null;
                }
                return OfVessel(snapshot, arrival, option);
            }
            if (!option.IsAuto)
            {
                var named = system.Find(option.Word);
                if (named == null)
                {
                    error = "plane must be \"auto\", \"none\", a number, a body or a vessel, not " + option.Word;
                    return null;
                }
                if (system.Parent(named) == null)
                {
                    error = named.Name + " orbits nothing, so it has no plane to match";
                    return null;
                }
                return OfBody(system, arrival, named, "body");
            }
            // Only Claudia's own value applies to a trip that ends in reentry. A leg in hand that names its capture ends in orbit, whatever lands after it (#986).
            if (ArrivalRule.EndsInReentry(snapshot, arrival.Name) || !CapturesHere(legs) && ArrivalRule.EndsInReentry(snapshot, arrival.Name, legs.Skip(1))) return null;
            return OfTargetVessel(snapshot, arrival) ?? OfLegs(snapshot, system, arrival, legs);
        }

        /// <summary>
        /// Whether the leg in hand's words name a capture into orbit, not an aerocapture: "coast to Duna, and capture into a 60 km orbit". A capture its clause turns down
        /// ("skip capture, straight down") is none.
        /// </summary>
        private static bool CapturesHere(IList<PlannedSequence> legs)
        {
            if (legs.Count == 0) return false;
            var words = SequenceWords.Of(legs[0]);
            return !SequenceWords.Aero.IsMatch(words) && SequenceWords.Capture.Matches(words).Cast<Match>().Any(m => !NoCapture.IsMatch(Clause(words, m.Index)));
        }

        /// <summary>Whether <paramref name="words"/> name the equator as the place (#986), not as where it isn't: "Land at Duna, far from the equator" doesn't.</summary>
        private static bool NamesEquator(string words)
        {
            return EquatorWord.Matches(words).Cast<Match>().Any(m => !NotEquator.IsMatch(Clause(words, m.Index)));
        }

        /// <summary>The words of <paramref name="words"/>'s clause before <paramref name="at"/>.</summary>
        private static string Clause(string words, int at)
        {
            if (at <= 0) return "";
            var start = words.LastIndexOfAny(ClauseEnds, at - 1) + 1;
            return words.Substring(start, at - start);
        }

        /// <summary>
        /// #986: the plane a trip home aims for far out, the landing's (KSC, at 0.1° S: the equator), prograde; null for none. It is not the arrival plane: inside the
        /// sphere of influence the landing aim (#915) flies, and a trip that ends in reentry records none for <c>capture</c>.
        /// </summary>
        public static ArrivalPlane Home(GameSnapshot snapshot, SnapshotBody arrival, double targetPe)
        {
            var landing = HomeLanding.ArrivalLanding(snapshot, arrival.Name, targetPe);
            if (landing == null) return null;
            var latitude = Math.Abs(landing.Latitude);
            return new ArrivalPlane { Arrival = arrival.Name, Source = "home", Inc = latitude < ArrivalPlane.SiteMargin ? 0 : Math.Min(90, latitude + ArrivalPlane.SiteMargin) };
        }

        private static ArrivalPlane OfBody(BodySystem system, SnapshotBody arrival, SnapshotBody body, string source)
        {
            var w = system.OrbitOf(body).W;
            return new ArrivalPlane { Arrival = arrival.Name, Source = source, Normal = w, Inc = Math.Acos(Math.Max(-1, Math.Min(1, w.Z))) * 180 / Math.PI };
        }

        private static ArrivalPlane OfVessel(GameSnapshot snapshot, SnapshotBody arrival, PlaneOption option)
        {
            var target = snapshot.Target;
            var isTarget = target != null && string.Equals(target.Name, option.VesselName, StringComparison.Ordinal);
            if (isTarget)
            {
                var own = OfTargetVessel(snapshot, arrival);
                if (own != null) return own;
            }
            return new ArrivalPlane { Arrival = arrival.Name, Source = "vessel", Normal = NormalOf(option.OrbitInc.Value, option.OrbitLan), Inc = option.OrbitInc.Value };
        }

        /// <summary>The Mission's Target Vessel's plane and periapsis altitude, when it orbits the arrival body; else null.</summary>
        private static ArrivalPlane OfTargetVessel(GameSnapshot snapshot, SnapshotBody arrival)
        {
            var target = snapshot.Target;
            if (target == null || target.IsDown || target.Orbit == null || target.Orbit.SemiMajorAxis == 0) return null;
            if (!string.Equals(target.Body, arrival.Name, StringComparison.OrdinalIgnoreCase)) return null;
            return new ArrivalPlane
            {
                Arrival = arrival.Name, Source = "target_vessel", Normal = NormalOf(target.Orbit.Inclination, target.Orbit.Lan), Inc = target.Orbit.Inclination, VesselPe = target.Orbit.Periapsis,
            };
        }

        /// <summary>The angular momentum's direction of an orbit of this inclination and LAN (degrees), as <see cref="Conic.FromElements"/> has it.</summary>
        private static Vec3 NormalOf(double incDeg, double lanDeg)
        {
            return Conic.FromElements(1, 1, 0, incDeg, lanDeg, 0, 0, 0).W;
        }

        /// <summary>The plane the legs from here on ask of the arrival: the leg in hand's landing, then the next leg that does something there; else null.</summary>
        private static ArrivalPlane OfLegs(GameSnapshot snapshot, BodySystem system, SnapshotBody arrival, IList<PlannedSequence> legs)
        {
            for (var i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (leg.Manual || !(string.IsNullOrEmpty(leg.Vessel) || string.Equals(leg.Vessel, "craft", StringComparison.OrdinalIgnoreCase))) continue;
                var words = SequenceWords.ActionWords(leg);
                if (SequenceWords.Landing.IsMatch(words))
                {
                    var at = SequenceWords.NamedBody(snapshot, words);
                    if (at != null && at != arrival) return MoonOf(system, arrival, at);
                    return OfSite(snapshot, arrival, leg);
                }
                // A leg with no landing counts only for a pole; the biome map still says whether its biome is one.
                if (WordsLatitude(snapshot, arrival, SequenceWords.Of(leg)) == 90) return AtLatitude(arrival, 90);
                // The leg in hand's trip is this one.
                if (i == 0) continue;
                if (SequenceWords.Trip.IsMatch(words) && !SequenceWords.ReturnHome.IsMatch(words))
                    return MoonOf(system, arrival, SequenceWords.TripTarget(snapshot, words, arrival, CraftCheckFlow.HomeBody)) ?? OfGoal(snapshot, arrival);
                if (SequenceWords.Ascent.IsMatch(words) || SequenceWords.Reentry.IsMatch(words) || SequenceWords.Aero.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words)) return OfGoal(snapshot, arrival);
            }
            return OfGoal(snapshot, arrival);
        }

        /// <summary>The plane of a pole or biome the Mission Goal gives this arrival, where no leg gave it one; else null.</summary>
        private static ArrivalPlane OfGoal(GameSnapshot snapshot, SnapshotBody arrival)
        {
            var latitude = GoalLatitude(snapshot, arrival);
            return latitude.HasValue ? AtLatitude(arrival, latitude.Value) : null;
        }

        /// <summary>|latitude| + <see cref="ArrivalPlane.SiteMargin"/>, prograde; an equatorial one sets no target.</summary>
        private static ArrivalPlane AtLatitude(SnapshotBody arrival, double latitude)
        {
            if (Math.Abs(latitude) < ArrivalPlane.SiteMargin) return null;
            return new ArrivalPlane { Arrival = arrival.Name, Source = "site", Inc = Math.Min(90, Math.Abs(latitude) + ArrivalPlane.SiteMargin) };
        }

        /// <summary>
        /// Whether <paramref name="words"/> put a pole on the arrival body. A pole word belongs to the body its clause names, the nearest one, and home only if the clause names no other
        /// ("Launch from Kerbin to a polar orbit of the Mun"); with none there, the last body named before it, else the first after ("Fly to the Mun, land at the north pole, return to Kerbin");
        /// with none at all, to this arrival. It is a name match, for what words alone can say: it can't read a negation ("not polar") beyond "non-polar".
        /// </summary>
        private static bool NamesPole(GameSnapshot snapshot, SnapshotBody arrival, string words)
        {
            var named = snapshot.Bodies.Where(b => b.Name.Length > 0)
                .SelectMany(b => Regex.Matches(words, @"\b" + Regex.Escape(b.Name) + @"\b", RegexOptions.IgnoreCase).Cast<Match>().Select(m => new { Body = b, m.Index })).ToList();
            foreach (Match pole in PoleWord.Matches(words))
            {
                var start = words.LastIndexOfAny(ClauseEnds, pole.Index) + 1;
                var end = words.IndexOfAny(ClauseEnds, pole.Index);
                if (end < 0) end = words.Length;
                var clause = named.Where(n => n.Index >= start && n.Index < end).ToList();
                var away = clause.Where(n => n.Body.Name != CraftCheckFlow.HomeBody).ToList();
                var owner = (away.Count > 0 ? away : clause).OrderBy(n => Math.Abs(n.Index - pole.Index)).FirstOrDefault()
                    ?? named.Where(n => n.Index < pole.Index).OrderByDescending(n => n.Index).FirstOrDefault()
                    ?? named.OrderBy(n => n.Index).FirstOrDefault();
                if (owner == null || owner.Body == arrival) return true;
            }
            return false;
        }

        /// <summary>The biome of the arrival body <paramref name="words"/> name, the longest name first; null for none.</summary>
        private static string BiomeNamed(SnapshotBody arrival, string words)
        {
            return arrival.BiomeNames.Where(n => n.Trim().Length > 0).OrderByDescending(n => n.Length)
                .FirstOrDefault(n => Regex.IsMatch(words, @"\b" + Regex.Escape(n) + @"\b", RegexOptions.IgnoreCase));
        }

        /// <summary>A leg's own words' latitude: the biome it names, as the game's map has it; else a pole in its words; else null.</summary>
        private static double? WordsLatitude(GameSnapshot snapshot, SnapshotBody arrival, string words)
        {
            return BiomeLatitude(snapshot, arrival, BiomeNamed(arrival, words)) ?? (NamesPole(snapshot, arrival, words) ? 90 : (double?)null);
        }

        /// <summary>The Mission Goal's latitude for this arrival: the biome it names on this body (a goal naming no body means this one), or a pole; else null.</summary>
        private static double? GoalLatitude(GameSnapshot snapshot, SnapshotBody arrival)
        {
            var goal = snapshot.MissionGoal ?? "";
            if (MissionPrompts.GoalBodies(snapshot, goal).Count == 0) return WordsLatitude(snapshot, arrival, goal);
            var biome = MissionPrompts.GoalBiomes(snapshot, goal).FirstOrDefault(g => g.Body == arrival.Name);
            return BiomeLatitude(snapshot, arrival, biome == null ? null : biome.Biome) ?? (NamesPole(snapshot, arrival, goal) ? 90 : (double?)null);
        }

        /// <summary>The biome's latitude where the game's map can give one; null for no biome, no map, or a biome too small for its survey.</summary>
        private static double? BiomeLatitude(GameSnapshot snapshot, SnapshotBody arrival, string biome)
        {
            if (biome == null || snapshot.BiomeLatitudesOf == null) return null;
            var table = snapshot.BiomeLatitudesOf(arrival.Name);
            double latitude;
            return table != null && table.TryGetValue(biome, out latitude) ? latitude : (double?)null;
        }

        /// <summary><paramref name="body"/>'s plane when it is a moon of the arrival body, else null: a leg elsewhere says nothing of this arrival.</summary>
        private static ArrivalPlane MoonOf(BodySystem system, SnapshotBody arrival, SnapshotBody body)
        {
            return body != null && system.Parent(body) == arrival ? OfBody(system, arrival, body, "moon") : null;
        }

        /// <summary>
        /// The landing leg's site, or none: its inclination is |latitude| + <see cref="ArrivalPlane.SiteMargin"/>, prograde. An equatorial site sets no target, unless the leg's
        /// words say the equator (#986): then the equator itself, 0.
        /// </summary>
        private static ArrivalPlane OfSite(GameSnapshot snapshot, SnapshotBody arrival, PlannedSequence leg)
        {
            var latitude = SiteLatitude(snapshot, arrival, leg);
            if (latitude.HasValue && Math.Abs(latitude.Value) >= ArrivalPlane.SiteMargin) return AtLatitude(arrival, latitude.Value);
            return NamesEquator(SequenceWords.Of(leg)) ? new ArrivalPlane { Arrival = arrival.Name, Source = "site", Inc = 0 } : null;
        }

        private static double? SiteLatitude(GameSnapshot snapshot, SnapshotBody arrival, PlannedSequence leg)
        {
            var site = snapshot.PickedSite;
            if (site != null && string.Equals(site.Body, arrival.Name, StringComparison.OrdinalIgnoreCase)) return site.Latitude;
            var words = SequenceWords.Of(leg);
            foreach (var poi in PointsOfInterest.Merge(arrival.Name, new SurfaceObject[0]))
                if (Regex.IsMatch(words, @"(?<![\w-])" + Regex.Escape(poi.Name) + @"(?![\w-])", RegexOptions.IgnoreCase)) return poi.Latitude;
            var target = snapshot.Target;
            if (target != null && target.IsDown && target.Position != null && string.Equals(target.Body, arrival.Name, StringComparison.OrdinalIgnoreCase)) return target.Position.Latitude;
            var pair = LatLng.Match(words);
            double latitude;
            if (pair.Success && double.TryParse(pair.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)) return latitude;
            // The leg's own equator comes before the Mission Goal's pole or biome (#986).
            return WordsLatitude(snapshot, arrival, words) ?? (NamesEquator(words) ? 0 : GoalLatitude(snapshot, arrival));
        }
    }
}
