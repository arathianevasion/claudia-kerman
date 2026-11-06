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
        /// <summary>Who picked it: "number", "body", "vessel", "target_vessel", "site" or "moon".</summary>
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
    /// words); the next leg's moon of this body; none, as before. A trip that ends in reentry is out of scope: "auto" gives it none. Pure.
    /// </summary>
    internal static class ArrivalPlanes
    {
        /// <summary>A pair of signed numbers in a leg's words, "(3.1, 24.6)": a site's latitude and longitude.</summary>
        private static readonly Regex LatLng = new Regex(@"\(\s*(-?\d+(?:\.\d+)?)\s*°?\s*,\s*-?\d+(?:\.\d+)?\s*°?\s*\)");

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
            // Only Claudia's own value applies to a trip that ends in reentry.
            if (ArrivalRule.EndsInReentry(snapshot, arrival.Name, legs.Skip(1))) return null;
            return OfTargetVessel(snapshot, arrival) ?? OfLegs(snapshot, system, arrival, legs);
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
                // The leg in hand's trip is this one.
                if (i == 0) continue;
                if (SequenceWords.Trip.IsMatch(words) && !SequenceWords.ReturnHome.IsMatch(words))
                    return MoonOf(system, arrival, SequenceWords.TripTarget(snapshot, words, arrival, CraftCheckFlow.HomeBody));
                if (SequenceWords.Ascent.IsMatch(words) || SequenceWords.Reentry.IsMatch(words) || SequenceWords.Aero.IsMatch(words) || SequenceWords.ReturnHome.IsMatch(words)) return null;
            }
            return null;
        }

        /// <summary><paramref name="body"/>'s plane when it is a moon of the arrival body, else null: a leg elsewhere says nothing of this arrival.</summary>
        private static ArrivalPlane MoonOf(BodySystem system, SnapshotBody arrival, SnapshotBody body)
        {
            return body != null && system.Parent(body) == arrival ? OfBody(system, arrival, body, "moon") : null;
        }

        /// <summary>The landing leg's site, or none: its inclination is |latitude| + <see cref="ArrivalPlane.SiteMargin"/>, prograde; an equatorial site sets no target.</summary>
        private static ArrivalPlane OfSite(GameSnapshot snapshot, SnapshotBody arrival, PlannedSequence leg)
        {
            var latitude = SiteLatitude(snapshot, arrival, leg);
            if (!latitude.HasValue || Math.Abs(latitude.Value) < ArrivalPlane.SiteMargin) return null;
            return new ArrivalPlane { Arrival = arrival.Name, Source = "site", Inc = Math.Min(90, Math.Abs(latitude.Value) + ArrivalPlane.SiteMargin) };
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
            return null;
        }
    }
}
