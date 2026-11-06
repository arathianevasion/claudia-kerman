// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudiaKermin.Core.Tools
{
    /// <summary>
    /// The <c>dock</c> Building Block's guidance (#706, #711, ADR 0014): the closing speed for each stretch of the approach, the
    /// cone the craft must be in to close, the relative velocity it wants, the coast band that decides when RCS translates, the
    /// wait for dock_hold to settle the target, and the monopropellant estimate it checks at the start. dock.ks calls it once a
    /// tick through <c>ADDONS:CLAUDIA:DOCKGUIDE</c> (<see cref="Guide"/>) and at the start through <c>DOCKFUEL</c>
    /// (<see cref="Fuel"/>), both block-internal; kOS still writes <c>ship:control</c> and steers. Everything is in the target
    /// port's frame: <c>along</c> out along its axis from its face, <c>lat</c> the distance off the axis (velocities positive away
    /// from it), <c>side</c> the third axis, square to both.
    /// <para>
    /// #711: the wanted velocity is constant within a stretch (beyond <see cref="NearM"/>, to <see cref="FinalM"/>, inside it) and
    /// changes only at a stretch's edge or when the craft's lateral or alignment state changes, never with distance each tick; the
    /// band is a share of the wanted speed. So one burn reaches the wanted velocity and the craft coasts until the error leaves the
    /// band again. Until dock_hold reports the target settled the craft only nulls its relative velocity, rather than chase an
    /// axis that is still turning.
    /// </para>
    /// </summary>
    public static class DockGuidance
    {
        /// <summary>m/s: the closing speed far out (beyond <see cref="NearM"/>), or approach_speed if that is faster.</summary>
        public const double FarSpeed = 2;

        /// <summary>
        /// m: the stretches' edges: approach_speed inside <see cref="NearM"/>, final_speed inside <see cref="FinalM"/>. 10 m and 5 m
        /// further out than the 50 m and 10 m dock's docs give, so one burn has reached the slower speed by then.
        /// </summary>
        public const double NearM = 60, FinalM = 15;

        /// <summary>
        /// The share of the stretch's closing speed the craft moves toward the axis at while lining up (off the cone) or drifting in
        /// (on it, further off than <see cref="Inner"/>). Over <see cref="BandShare"/>, so reaching the inner zone is a burn that
        /// stops the drift, not a coast across the axis.
        /// </summary>
        public const double LateralShare = 0.3;

        /// <summary>m: inside this distance the craft closes only while its port points within <see cref="AlignDeg"/> of the axis: a state change, not a stretch.</summary>
        public const double AlignM = 20;

        /// <summary>m: off the axis the craft keeps when it starts behind the target port's face, to pass the target's side.</summary>
        public const double ClearM = 50;

        /// <summary>Degrees off the target port's axis the craft's port may point inside 20 m and still close.</summary>
        public const double AlignDeg = 5;

        /// <summary>
        /// m/s of velocity error: RCS starts translating over the band's edge, <see cref="BandShare"/> of the wanted speed and at
        /// least <see cref="BandFloor"/>, and stops under <see cref="StopShare"/> of it. At final_speed's default 0.3 m/s the band
        /// is 0.075 m/s, so the craft meets the port within that of it.
        /// </summary>
        public const double BandShare = 0.25, BandFloor = 0.03, StopShare = 0.25;

        /// <summary>m/s of velocity error that commands full RCS on an axis.</summary>
        public const double FullAt = 0.2;

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
        /// m/s: the closing speed for the stretch at distance <paramref name="d"/> (m), constant within it: the larger of
        /// <see cref="FarSpeed"/> and approach_speed beyond <see cref="NearM"/>, approach_speed to <see cref="FinalM"/>, final_speed inside.
        /// </summary>
        public static double Speed(double d, Options opt)
        {
            if (d > NearM) return Math.Max(FarSpeed, opt.ApproachSpeed);
            if (d > FinalM) return opt.ApproachSpeed;
            return opt.FinalSpeed;
        }

        /// <summary>m: how far off the axis the craft may be at <paramref name="along"/> and still close: max_lateral inside 10 m, widening by 5 cm a metre beyond.</summary>
        public static double Cone(double along, Options opt)
        {
            return opt.MaxLateral + 0.05 * Math.Max(0, along - 10);
        }

        /// <summary>
        /// m: on the cone the craft drifts toward the axis until it is this close to it, then wants no lateral speed: half the
        /// cone at its stretch's near edge, a fixed figure for the stretch (1.35 m far out, 0.225 m to <see cref="FinalM"/>,
        /// half max_lateral inside, at the defaults).
        /// </summary>
        public static double Inner(double distance, Options opt)
        {
            return Cone(distance > NearM ? NearM : distance > FinalM ? FinalM : 0, opt) / 2;
        }

        /// <summary>
        /// dock_hold's state (<c>DOCKHOLDSTATE</c>) has the target still turning to its aim: starting, or active and not yet
        /// settled. The craft then only nulls its relative velocity. A passive target is never waited for.
        /// </summary>
        public static bool Waits(string hold)
        {
            return hold == "starting" || hold == "active" || hold == "near";
        }

        /// <summary>s: the default settle_timeout, how long dock waits for dock_hold's strict settle before it takes a loose one (#714).</summary>
        public const double SettleTimeout = 300;

        /// <summary>wait_since after a loose settle ended the wait: the craft closes, and the clock stays stopped until the hold leaves "near".</summary>
        private const double Accepted = -2;

        /// <summary>
        /// #714: the wait for dock_hold, one tick. <paramref name="since"/> is when the wait began (negative: none running) and the
        /// clock starts when the hold first <see cref="Waits"/>, so a re-aim (settled back to active) restarts it. A hold that
        /// is "near" (dock_hold's loose settle: its port within 5° of its aim, turning under 0.5°/s, for 2 s) ends the wait once
        /// <paramref name="timeout"/> s have passed; one still starting or active at twice that ends it as "gave up", with
        /// <c>Error</c> naming the hold. <c>End</c> is "" while the wait goes on (or none ran), else how it ended.
        /// </summary>
        public static WaitTick Wait(string hold, double ut, double since, double timeout, string target, double offDeg, double spinDps)
        {
            var tick = new WaitTick { Since = -1, End = "", Error = "" };
            if (since == Accepted && hold == "near")
            {
                // A loose settle already ended this wait: the craft closes on it from here, with no new clock.
                tick.Since = Accepted;
            }
            else if (Waits(hold))
            {
                tick.Since = since < 0 ? ut : since;
                tick.Waited = Math.Max(0, ut - tick.Since);
                tick.Holds = true;
                if (hold == "near" && tick.Waited >= timeout)
                {
                    tick.Holds = false;
                    tick.End = "loosely settled";
                    tick.Since = Accepted;
                }
                else if (hold != "near" && tick.Waited >= 2 * timeout)
                {
                    tick.End = "gave up";
                    tick.Error = string.Format(CultureInfo.InvariantCulture, "{0}'s dock_hold never settled: {1:0.#}° off its aim, turning {2:0.##}°/s, after {3:0} s", target, offDeg, spinDps, tick.Waited);
                }
                if (tick.End == "gave up") tick.Since = -1;
            }
            else if (since >= 0)
            {
                tick.Waited = Math.Max(0, ut - since);
                if (hold == "settled") tick.End = "settled";
            }
            return tick;
        }

        /// <summary>One tick of the wait (<see cref="Wait"/>): whether the craft still holds still, the clock to carry to the next tick, and how the wait ended.</summary>
        public struct WaitTick
        {
            public bool Holds;
            public double Since, Waited;
            public string End, Error;
        }

        /// <summary>
        /// The relative velocity the craft wants now, constant within a stretch (<see cref="Speed"/>). Over a metre behind the
        /// port's face it first moves <see cref="ClearM"/> off the axis and then out past the face; off the cone it holds its
        /// distance along (backing out inside 2 m) and moves onto the axis at <see cref="LateralShare"/> of the speed; on the cone
        /// it closes at the speed, drifting in at that share while further off than <see cref="Inner"/>, except inside
        /// <see cref="AlignM"/> while its port points over <see cref="AlignDeg"/> off the axis.
        /// </summary>
        public static Velocity Want(double along, double lat, double distance, double alignDeg, Options opt)
        {
            var speed = Speed(distance, opt);
            if (along < -1)
                return lat < ClearM ? V(0, Math.Min(speed, 2)) : V(Math.Min(speed, 2), 0);
            var drift = -LateralShare * speed;
            if (lat > Cone(along, opt)) return V(along < 2 ? opt.FinalSpeed : 0, drift);
            var toward = lat > Inner(distance, opt) ? drift : 0;
            if (distance < AlignM && alignDeg > AlignDeg) return V(0, toward);
            return V(-speed, toward);
        }

        /// <summary>
        /// The coast band: whether RCS translates this tick, given the velocity error (m/s, its size), the wanted speed (m/s) and
        /// whether it translated last tick. It starts over <see cref="Band"/> and goes on until the error is under
        /// <see cref="StopShare"/> of it; between the two the craft keeps doing what it did, so it coasts until the error leaves the band.
        /// </summary>
        public static bool Fires(double error, double wantSpeed, bool firing)
        {
            var band = Band(wantSpeed);
            return firing ? error >= band * StopShare : error > band;
        }

        /// <summary>m/s: the coast band's edge for the wanted speed (m/s): <see cref="BandShare"/> of it, at least <see cref="BandFloor"/>.</summary>
        public static double Band(double wantSpeed)
        {
            return Math.Max(BandFloor, BandShare * wantSpeed);
        }

        /// <summary>
        /// One tick, as <c>ADDONS:CLAUDIA:DOCKGUIDE</c> takes it. Input: <c>mode</c> ("close", the default; "back_off": out
        /// along the line from the port at 0.5 m/s; "hold": still), <c>hold</c> (dock_hold's state: while it <see cref="Waits"/>,
        /// "close" holds still too, until <see cref="Wait"/> ends it), <c>ut</c>, <c>wait_since</c>, <c>settle_timeout</c>, <c>target</c>,
        /// <c>hold_off_deg</c>, <c>spin_dps</c> (the wait, #714), <c>along</c>, <c>lat</c>, <c>distance</c> (m),
        /// <c>align_deg</c>, <c>v_along</c>, <c>v_lat</c>, <c>v_side</c> (the relative velocity, m/s), <c>firing</c> (RCS
        /// translated last tick), and the options <c>approach_speed</c>, <c>final_speed</c>, <c>max_lateral</c>. Gives
        /// <c>along</c>, <c>lat</c> (the wanted velocity), <c>dv_error</c> (m/s, the velocity error's size), <c>fire</c> (RCS
        /// translates this tick) and <c>cmd_along</c>, <c>cmd_lat</c>, <c>cmd_side</c>: the RCS command in the port's frame, the
        /// error over <see cref="FullAt"/> (kOS turns it into fore, starboard and top and clips each to 1); all 0 while it coasts; and the wait's <c>wait_since</c>, <c>wait_s</c>, <c>wait_end</c> and <c>wait_error</c>.
        /// </summary>
        public static IDictionary<string, object> Guide(IDictionary<string, object> input)
        {
            var along = Number(input, "along");
            var lat = Number(input, "lat");
            var distance = Number(input, "distance");
            var opt = new Options { ApproachSpeed = Number(input, "approach_speed"), FinalSpeed = Number(input, "final_speed"), MaxLateral = Number(input, "max_lateral") };
            var mode = Text(input, "mode");
            object since, timeout;
            var wait = Wait(Text(input, "hold"), Number(input, "ut"), input.TryGetValue("wait_since", out since) && since is double ? (double)since : -1,
                input.TryGetValue("settle_timeout", out timeout) && timeout is double ? (double)timeout : SettleTimeout,
                Text(input, "target"), Number(input, "hold_off_deg"), Number(input, "spin_dps"));
            Velocity want;
            if (mode == "back_off")
            {
                var d = Math.Max(distance, 0.01);
                want = V(0.5 * along / d, 0.5 * lat / d);
            }
            else if (mode == "hold" || wait.Holds) want = V(0, 0);
            else want = Want(along, lat, distance, Number(input, "align_deg"), opt);
            var eAlong = want.Along - Number(input, "v_along");
            var eLat = want.Lat - Number(input, "v_lat");
            var eSide = -Number(input, "v_side");
            var error = Math.Sqrt(eAlong * eAlong + eLat * eLat + eSide * eSide);
            object firingValue;
            var fire = Fires(error, Math.Sqrt(want.Along * want.Along + want.Lat * want.Lat), input.TryGetValue("firing", out firingValue) && firingValue is bool && (bool)firingValue);
            var k = fire ? 1 / FullAt : 0;
            return new Dictionary<string, object>
            {
                { "along", want.Along },
                { "lat", want.Lat },
                { "dv_error", error },
                { "fire", fire },
                { "cmd_along", eAlong * k },
                { "cmd_lat", eLat * k },
                { "cmd_side", eSide * k },
                { "wait_since", wait.Since },
                { "wait_s", wait.Waited },
                { "wait_end", wait.End },
                { "wait_error", wait.Error },
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
        /// The start-of-dock check (<c>ADDONS:CLAUDIA:DOCKFUEL</c>): <paramref name="monoprop"/> units at <paramref name="ispVac"/> s
        /// (the RCS thrusters' vacuum Isp) on a <paramref name="massT"/> t craft, <paramref name="distance"/> m from the port at
        /// <paramref name="speed"/> m/s. Gives <c>error</c> ("" when the RCS delta-v on board covers <see cref="Needed"/>, else
        /// why not), <c>dv</c> (m/s on board) and <c>need</c> (m/s).
        /// </summary>
        public static IDictionary<string, object> Fuel(double monoprop, double ispVac, double massT, double distance, double speed)
        {
            var have = DeltaV(monoprop, ispVac, massT);
            var need = Needed(distance, speed);
            var error = have >= need ? "" : string.Format(CultureInfo.InvariantCulture,
                "too little monopropellant: {0:0.0} units give {1:0.0} m/s of RCS, and {2:0} m to the port needs about {3:0.0}", monoprop, have, distance, need);
            return new Dictionary<string, object> { { "error", error }, { "dv", have }, { "need", need } };
        }

        /// <summary>
        /// <c>ADDONS:CLAUDIA:DOCKFUEL</c> as the addon maps it: <c>distance</c> and <c>speed</c> from dock's lexicon, and the craft as
        /// read live: its MonoPropellant (units), mass (t), and each enabled RCS module's thrust (kN) and vacuum Isp (s), averaged
        /// by thrust. Gives <see cref="Fuel"/>'s answer with the <c>isp</c> it used.
        /// </summary>
        public static IDictionary<string, object> FuelCall(IDictionary<string, object> input, double monoprop, double massT, IList<double> thrusts, IList<double> isps)
        {
            double power = 0, weighted = 0;
            for (var i = 0; i < thrusts.Count; i++)
            {
                if (thrusts[i] <= 0) continue;
                power += thrusts[i];
                weighted += thrusts[i] * isps[i];
            }
            var isp = power > 0 ? weighted / power : 0;
            var fuel = Fuel(monoprop, isp, massT, Number(input, "distance"), Number(input, "speed"));
            fuel["isp"] = isp;
            return fuel;
        }

        private static Velocity V(double along, double lat)
        {
            return new Velocity { Along = along, Lat = lat };
        }

        private static string Text(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) ? value as string : null;
        }

        private static double Number(IDictionary<string, object> input, string key)
        {
            object value;
            return input != null && input.TryGetValue(key, out value) && value is double ? (double)value : 0;
        }
    }
}
