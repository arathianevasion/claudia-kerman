// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The <c>dock</c> Building Block's guidance (#706, ADR 0014): the closing speed by distance, the cone the craft must be in to
    /// close, the relative velocity it wants, the coast band that decides when RCS translates, and the monopropellant estimate it
    /// checks at the start. dock.ks calls it once a tick through <c>ADDONS:CLAUDIA:DOCKGUIDE</c> (<see cref="Guide"/>) and at
    /// the start through <c>DOCKFUEL</c> (<see cref="FuelCheck"/>), both block-internal; kOS still writes <c>ship:control</c>
    /// and steers. Everything is in the target port's frame: <c>along</c> out along its axis from its face, <c>lat</c> the
    /// distance off the axis (velocities positive away from it), <c>side</c> the third axis, square to both.
    /// </summary>
    public static class DockGuidance
    {
        /// <summary>m/s: the closing speed far out (beyond 100 m).</summary>
        public const double FarSpeed = 5;

        /// <summary>m: off the axis the craft keeps when it starts behind the target port's face, to pass the target's side.</summary>
        public const double ClearM = 50;

        /// <summary>Degrees off the target port's axis the craft's port may point inside 20 m and still close.</summary>
        public const double AlignDeg = 5;

        /// <summary>m/s of velocity error: RCS starts translating over <see cref="CoastOut"/> and stops under <see cref="CoastIn"/>.</summary>
        public const double CoastOut = 0.1, CoastIn = 0.05;

        /// <summary>m/s of velocity error that commands full RCS on an axis.</summary>
        public const double FullAt = 0.2;

        /// <summary>The share of the starting monopropellant at which the block stops closing, backs off and fails.</summary>
        public const double ReserveShare = 0.15;

        /// <summary>m/s of RCS the start check asks for per metre to the port, on top of <see cref="ReserveDv"/> and the speed now.</summary>
        public const double DvPerMetre = 0.03;

        /// <summary>m/s of RCS the start check keeps in reserve.</summary>
        public const double ReserveDv = 3;

        private const double G0 = 9.80665;

        /// <summary>kg of one unit of MonoPropellant.</summary>
        private const double MonopropKg = 4;

        /// <summary>dock's options the guidance reads (m/s, m/s, m).</summary>
        public struct Options
        {
            public double ApproachSpeed, FinalSpeed, MaxLateral;
        }

        /// <summary>The relative velocity wanted, m/s: out along the axis (negative closes) and away from it (negative toward it).</summary>
        public struct Velocity
        {
            public double Along, Lat;
        }

        /// <summary>
        /// m/s: the closing speed at distance <paramref name="d"/> (m): up to <see cref="FarSpeed"/> beyond 100 m, approach_speed
        /// from 100 to 50 m, tapering to final_speed at 10 m and holding it inside.
        /// </summary>
        public static double Speed(double d, Options opt)
        {
            if (d > 100) return Math.Min(FarSpeed, Math.Max(opt.ApproachSpeed, opt.ApproachSpeed * d / 100));
            if (d > 50) return opt.ApproachSpeed;
            if (d > 10) return opt.FinalSpeed + (opt.ApproachSpeed - opt.FinalSpeed) * (d - 10) / 40;
            return opt.FinalSpeed;
        }

        /// <summary>m: how far off the axis the craft may be at <paramref name="along"/> and still close: max_lateral inside 10 m, widening by 5 cm a metre beyond.</summary>
        public static double Cone(double along, Options opt)
        {
            return opt.MaxLateral + 0.05 * Math.Max(0, along - 10);
        }

        /// <summary>
        /// The relative velocity the craft wants now. Over a metre behind the port's face it first moves <see cref="ClearM"/> off
        /// the axis and then out past the face; off the cone it holds its distance along (backing out inside 2 m) and moves onto
        /// the axis; on the cone it closes at <see cref="Speed"/>, except inside 20 m while its port points over
        /// <see cref="AlignDeg"/> off the axis.
        /// </summary>
        public static Velocity Want(double along, double lat, double distance, double alignDeg, Options opt)
        {
            var speed = Speed(distance, opt);
            if (along < -1)
                return lat < ClearM ? V(0, Math.Min(speed, 2)) : V(Math.Min(speed, 2), 0);
            var toward = -Math.Min(speed, 0.2 * lat);
            if (lat > Cone(along, opt)) return V(along < 2 ? opt.FinalSpeed : 0, toward);
            if (distance < 20 && alignDeg > AlignDeg) return V(0, toward);
            return V(-speed, toward);
        }

        /// <summary>
        /// The coast band: whether RCS translates this tick, given the velocity error (m/s, its size) and whether it translated
        /// last tick. It starts over <see cref="CoastOut"/> and goes on until the error is under <see cref="CoastIn"/>; between
        /// the two the craft keeps doing what it did, so it coasts until the error leaves the band again.
        /// </summary>
        public static bool Fires(double error, bool firing)
        {
            return firing ? error >= CoastIn : error > CoastOut;
        }

        /// <summary>
        /// One tick, as <c>ADDONS:CLAUDIA:DOCKGUIDE</c> takes it. Input: <c>mode</c> ("close", the default; "back_off": out
        /// along the line from the port at 0.5 m/s; "hold": still), <c>along</c>, <c>lat</c>, <c>distance</c> (m),
        /// <c>align_deg</c>, <c>v_along</c>, <c>v_lat</c>, <c>v_side</c> (the relative velocity, m/s), <c>firing</c> (RCS
        /// translated last tick), and the options <c>approach_speed</c>, <c>final_speed</c>, <c>max_lateral</c>. Gives
        /// <c>along</c>, <c>lat</c> (the wanted velocity), <c>error</c> (m/s, the velocity error's size), <c>fire</c> (RCS
        /// translates this tick) and <c>cmd_along</c>, <c>cmd_lat</c>, <c>cmd_side</c>: the RCS command in the port's frame, the
        /// error over <see cref="FullAt"/> (kOS turns it into fore, starboard and top and clips each to 1); all 0 while it coasts.
        /// </summary>
        public static IDictionary<string, object> Guide(IDictionary<string, object> input)
        {
            var along = Number(input, "along");
            var lat = Number(input, "lat");
            var distance = Number(input, "distance");
            var opt = new Options { ApproachSpeed = Number(input, "approach_speed"), FinalSpeed = Number(input, "final_speed"), MaxLateral = Number(input, "max_lateral") };
            object modeValue;
            var mode = input.TryGetValue("mode", out modeValue) ? modeValue as string : null;
            Velocity want;
            if (mode == "back_off")
            {
                var d = Math.Max(distance, 0.01);
                want = V(0.5 * along / d, 0.5 * lat / d);
            }
            else if (mode == "hold") want = V(0, 0);
            else want = Want(along, lat, distance, Number(input, "align_deg"), opt);
            var eAlong = want.Along - Number(input, "v_along");
            var eLat = want.Lat - Number(input, "v_lat");
            var eSide = -Number(input, "v_side");
            var error = Math.Sqrt(eAlong * eAlong + eLat * eLat + eSide * eSide);
            object firingValue;
            var fire = Fires(error, input.TryGetValue("firing", out firingValue) && firingValue is bool && (bool)firingValue);
            var k = fire ? 1 / FullAt : 0;
            return new Dictionary<string, object>
            {
                { "along", want.Along },
                { "lat", want.Lat },
                { "error", error },
                { "fire", fire },
                { "cmd_along", eAlong * k },
                { "cmd_lat", eLat * k },
                { "cmd_side", eSide * k },
            };
        }

        /// <summary>m/s of RCS on board: the rocket equation on <paramref name="monoprop"/> units at <paramref name="ispVac"/> s from <paramref name="massT"/> t.</summary>
        public static double DeltaV(double monoprop, double ispVac, double massT)
        {
            var spent = monoprop * MonopropKg / 1000;
            if (ispVac <= 0 || massT <= 0 || monoprop <= 0) return 0;
            if (spent >= massT) return double.PositiveInfinity;
            return ispVac * G0 * Math.Log(massT / (massT - spent));
        }

        /// <summary>m/s of RCS the dock is estimated to need from <paramref name="distance"/> m at <paramref name="speed"/> m/s.</summary>
        public static double Needed(double distance, double speed)
        {
            return ReserveDv + DvPerMetre * Math.Max(0, distance) + Math.Max(0, speed);
        }

        /// <summary>
        /// The start-of-dock check (<c>ADDONS:CLAUDIA:DOCKFUEL</c>): "" when the RCS delta-v on board covers <see cref="Needed"/>,
        /// else why not. The figures it gives are the ones the reason names.
        /// </summary>
        public static string FuelCheck(double monoprop, double ispVac, double massT, double distance, double speed)
        {
            var have = DeltaV(monoprop, ispVac, massT);
            var need = Needed(distance, speed);
            if (have >= need) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "too little monopropellant: {0:0.0} units give {1:0.0} m/s of RCS, and {2:0} m to the port needs about {3:0.0}", monoprop, have, distance, need);
        }

        private static Velocity V(double along, double lat)
        {
            return new Velocity { Along = along, Lat = lat };
        }

        private static double Number(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value is double ? (double)value : 0;
        }
    }
}
