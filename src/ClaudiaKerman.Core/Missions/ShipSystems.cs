// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    public enum ShipSystemKind
    {
        Panel,
        Antenna,
        Radiator,
        Light,
    }

    public enum ShipSystemState
    {
        /// <summary>Retracted, or a light that is off.</summary>
        Stowed,
        /// <summary>Extended, or a light that is on.</summary>
        Out,
        /// <summary>On its way in or out.</summary>
        Moving,
        /// <summary>Broken or shielded in a fairing or bay: nothing can move it now.</summary>
        Broken,
    }

    /// <summary>One Ship System on the craft, as the adapter reads it. The adapter keeps which module it is by this object.</summary>
    public sealed class ShipSystemPart
    {
        public uint Id;
        public ShipSystemKind Kind;
        public ShipSystemState State;
        /// <summary>False for a part that can extend but never come back (<see cref="ShipSystemsHold"/>). A light is always true.</summary>
        public bool CanRetract = true;
    }

    public enum ShipSystemsSituation
    {
        Prelaunch,
        Landed,
        Splashed,
        Flying,
        SubOrbital,
        Orbiting,
        Escaping,
        Other,
    }

    /// <summary>A moment only a Building Block knows (the block's one-line cue, <c>ADDONS:CLAUDIA:SHIPSYSTEMS</c>).</summary>
    public enum ShipSystemsCue
    {
        /// <summary>The start of launch or ascend, before throttle-up.</summary>
        LiftOff,
        /// <summary>land begins its terminal descent.</summary>
        FinalDescent,
        /// <summary>The start of deorbit or reentry.</summary>
        EntryStart,
    }

    /// <summary>The craft's flight state, one reading for <see cref="ShipSystemsRule.Step"/>. Heights are metres above sea level unless said.</summary>
    public sealed class ShipSystemsReading
    {
        public double Ut;
        public ShipSystemsSituation Situation;
        public string Body = "";
        /// <summary>The top of the body's atmosphere; 0 for an airless body.</summary>
        public double AtmosphereTop;
        public double Periapsis;
        public double Altitude;
        /// <summary>Metres above the ground.</summary>
        public double Radar;
        /// <summary>m/s, positive climbing.</summary>
        public double VerticalSpeed;
        public double SurfaceSpeed;
        /// <summary>Seconds until the trajectory falls to the atmosphere's top (<see cref="ShipSystemsRule.SecondsToAir"/>); null when it doesn't, or the craft is in the air.</summary>
        public double? SecondsToAir;
        /// <summary>An engine on the craft is making thrust now.</summary>
        public bool EnginesOn;
    }

    public enum ShipSystemsDeployables
    {
        None,
        Extend,
        Retract,
    }

    public enum ShipSystemsLights
    {
        None,
        On,
        Off,
    }

    /// <summary>What a transition asks of the Ship Systems, and why, in the words of the Mission's events.</summary>
    public sealed class ShipSystemsTransition
    {
        public ShipSystemsDeployables Deployables;
        public ShipSystemsLights Lights;
        public string Why = "";
        /// <summary>This Extend is the touchdown: the leg in hand has landed, so its own entry is behind the craft (<see cref="ShipSystemsHold.Find"/>).</summary>
        public bool Landed;
    }

    /// <summary>
    /// The Ship Systems rule (#756): whether this moment is a transition. It fires once per transition and reads no part, so
    /// anything the player toggles between transitions stands until the next one. The first reading after <c>new</c> (a load, a
    /// scene change, the craft taken back from Manual Control) is taken as the state things were left in, so an orbit already
    /// reached doesn't extend anything again. Unity-free and pure apart from its latches; the adapter moves the parts.
    /// </summary>
    public sealed class ShipSystemsRule
    {
        /// <summary>Retract when the trajectory enters the atmosphere and it is this near in time...</summary>
        public const double RetractSeconds = 60;
        /// <summary>...or this near in height.</summary>
        public const double RetractMetres = 10000;
        /// <summary>Lights on below this over the ground, land's default gear_alt.</summary>
        public const double LightsRadar = 1000;
        /// <summary>Stationary this long on the ground of a body with air redeploys.</summary>
        public const double RedeploySeconds = 3;
        /// <summary>Under this, m/s, the craft counts as stationary.</summary>
        public const double StationarySpeed = 0.2;
        /// <summary>Above this over the ground the craft has flown; lower is a bounce or a wave.</summary>
        public const double FlownRadar = 100;

        private readonly List<ShipSystemsCue> _cues = new List<ShipSystemsCue>();
        private bool _read;
        private bool _orbitDone;
        private bool _entryDone;
        private bool _down;
        private bool _lightsDone;
        private bool _flown;
        private double? _stoppedAt;

        /// <summary>A block's cue, acted on at the next <see cref="Step"/>.</summary>
        public void Cue(ShipSystemsCue cue)
        {
            _cues.Add(cue);
        }

        /// <summary>
        /// The transition this reading makes, or null. With <paramref name="managed"/> false (the setting is off) the rule keeps
        /// its place and asks for nothing, so switching it back on doesn't replay what was missed.
        /// </summary>
        public ShipSystemsTransition Step(ShipSystemsReading reading, bool managed = true)
        {
            var transition = new ShipSystemsTransition();
            var cues = _cues.ToList();
            _cues.Clear();
            foreach (var cue in cues) Apply(cue, reading, transition);
            Entry(reading, transition);
            Orbit(reading, transition);
            Touchdown(reading, transition);
            LightsLow(reading, transition);
            _read = true;
            if (!managed) return null;
            return transition.Deployables == ShipSystemsDeployables.None && transition.Lights == ShipSystemsLights.None ? null : transition;
        }

        private static bool HasAir(ShipSystemsReading reading)
        {
            return reading.AtmosphereTop > 0;
        }

        private static bool OnGround(ShipSystemsReading reading)
        {
            return reading.Situation == ShipSystemsSituation.Landed || reading.Situation == ShipSystemsSituation.Splashed;
        }

        private static bool Falling(ShipSystemsReading reading)
        {
            return reading.Situation == ShipSystemsSituation.Flying || reading.Situation == ShipSystemsSituation.SubOrbital;
        }

        private void Apply(ShipSystemsCue cue, ShipSystemsReading reading, ShipSystemsTransition transition)
        {
            switch (cue)
            {
                case ShipSystemsCue.LiftOff:
                    _lightsDone = false;
                    _flown = false;
                    _stoppedAt = null;
                    transition.Lights = ShipSystemsLights.Off;
                    if (StowsAtLiftOff(reading)) Ask(transition, ShipSystemsDeployables.Retract, "lifting off from " + reading.Body);
                    else Say(transition, "lifting off from " + reading.Body);
                    break;
                case ShipSystemsCue.FinalDescent:
                    LightsOn(transition, "final descent to " + reading.Body);
                    break;
                case ShipSystemsCue.EntryStart:
                    if (!HasAir(reading)) break;
                    _orbitDone = true;
                    _entryDone = true;
                    Ask(transition, ShipSystemsDeployables.Retract, "starting down to " + reading.Body + "'s atmosphere");
                    break;
            }
        }

        /// <summary>The lift-off cue stows only from the ground of a body with air: an airless body keeps them out, and a block starting in flight isn't a lift-off.</summary>
        public static bool StowsAtLiftOff(ShipSystemsReading reading)
        {
            return HasAir(reading) && (OnGround(reading) || reading.Situation == ShipSystemsSituation.Prelaunch);
        }

        /// <summary>
        /// The trajectory enters the air, and it is within 60 s of its top, or within 10 km coming down. A craft that started from the
        /// ground has to rise above the air first: a bounce or a wave, or a low hop, is no entry.
        /// </summary>
        private void Entry(ShipSystemsReading reading, ShipSystemsTransition transition)
        {
            if (OnGround(reading) || reading.Situation == ShipSystemsSituation.Prelaunch)
            {
                _down = true;
                _entryDone = false;
                return;
            }
            if (reading.Altitude > reading.AtmosphereTop) _down = false;
            var aloft = Falling(reading) || reading.Situation == ShipSystemsSituation.Orbiting || reading.Situation == ShipSystemsSituation.Escaping;
            if (!HasAir(reading) || !aloft || reading.Periapsis >= reading.AtmosphereTop)
            {
                _entryDone = false;
                return;
            }
            if (_entryDone || _down) return;
            var soon = reading.SecondsToAir.HasValue && reading.SecondsToAir.Value <= RetractSeconds;
            var near = reading.VerticalSpeed < 0 && reading.Altitude <= reading.AtmosphereTop + RetractMetres;
            if (!soon && !near) return;
            _entryDone = true;
            Ask(transition, ShipSystemsDeployables.Retract, "entering " + reading.Body + "'s atmosphere"
                + (reading.SecondsToAir.HasValue ? " in " + reading.SecondsToAir.Value.ToString("0", CultureInfo.InvariantCulture) + " s" : ""));
        }

        /// <summary>ORBITING, the periapsis above the air (or the terrain), the engines off: once per orbit reached, however many vacuum burns follow.</summary>
        private void Orbit(ShipSystemsReading reading, ShipSystemsTransition transition)
        {
            var clear = reading.Situation == ShipSystemsSituation.Orbiting
                && BurnWorth.Safe(reading.Periapsis, BurnWorth.SafeHeight(reading.Body, HasAir(reading), reading.AtmosphereTop));
            if (!clear)
            {
                _orbitDone = false;
                return;
            }
            // An orbit the craft was already in when the rule first read it is no transition, burning or not.
            if (!_read) _orbitDone = true;
            if (_orbitDone || reading.EnginesOn) return;
            _orbitDone = true;
            Ask(transition, ShipSystemsDeployables.Extend, "in orbit of " + reading.Body);
        }

        /// <summary>Down on a body with air after a flight, and stationary for 3 s.</summary>
        private void Touchdown(ShipSystemsReading reading, ShipSystemsTransition transition)
        {
            if (Falling(reading) && reading.Radar > FlownRadar) _flown = true;
            if (!_flown || !HasAir(reading) || !OnGround(reading) || reading.SurfaceSpeed >= StationarySpeed)
            {
                _stoppedAt = null;
                return;
            }
            if (!_stoppedAt.HasValue) _stoppedAt = reading.Ut;
            if (reading.Ut - _stoppedAt.Value < RedeploySeconds) return;
            _flown = false;
            _stoppedAt = null;
            Ask(transition, ShipSystemsDeployables.Extend, "landed on " + reading.Body);
            transition.Landed = transition.Deployables == ShipSystemsDeployables.Extend;
        }

        /// <summary>Coming down below gear_alt; an orbit means the next low pass is a new landing.</summary>
        private void LightsLow(ShipSystemsReading reading, ShipSystemsTransition transition)
        {
            if (reading.Situation == ShipSystemsSituation.Orbiting || reading.Situation == ShipSystemsSituation.Escaping) _lightsDone = false;
            if (Falling(reading) && reading.VerticalSpeed < 0 && reading.Radar < LightsRadar)
                LightsOn(transition, "below " + LightsRadar.ToString("0", CultureInfo.InvariantCulture) + " m over " + reading.Body);
        }

        private void LightsOn(ShipSystemsTransition transition, string why)
        {
            if (_lightsDone) return;
            _lightsDone = true;
            transition.Lights = ShipSystemsLights.On;
            Say(transition, why);
        }

        private static void Ask(ShipSystemsTransition transition, ShipSystemsDeployables deployables, string why)
        {
            // A cue comes first and wins: an entry cue in the tick the orbit test would extend.
            if (transition.Deployables != ShipSystemsDeployables.None) return;
            transition.Deployables = deployables;
            Say(transition, why);
        }

        private static void Say(ShipSystemsTransition transition, string why)
        {
            if (transition.Why.Length > 0) return;
            transition.Why = why;
        }

        /// <summary>
        /// Seconds until a conic falls to radius <paramref name="top"/> from above; null when the craft is at or under it, or the
        /// conic never comes down to it ahead. <paramref name="radius"/> and <paramref name="verticalSpeed"/> place the craft on it.
        /// </summary>
        public static double? SecondsToAir(double mu, double sma, double ecc, double radius, double verticalSpeed, double top)
        {
            if (radius <= top || mu <= 0 || ecc <= 0 || sma == 0) return null;
            var semiLatus = sma * (1 - ecc * ecc);
            if (semiLatus <= 0 || semiLatus / (1 + ecc) >= top) return null;
            var cosAtTop = (semiLatus / top - 1) / ecc;
            if (cosAtTop < -1 || cosAtTop > 1) return null;
            var atTop = -Math.Acos(cosAtTop);
            var now = Math.Acos(Math.Max(-1, Math.Min(1, (semiLatus / radius - 1) / ecc))) * (verticalSpeed < 0 ? -1 : 1);
            var motion = Math.Sqrt(mu / Math.Pow(Math.Abs(sma), 3));
            var dt = (MeanAnomaly(atTop, ecc) - MeanAnomaly(now, ecc)) / motion;
            if (ecc >= 1) return dt >= 0 ? dt : (double?)null;
            return dt >= 0 ? dt : dt + 2 * Math.PI / motion;
        }

        private static double MeanAnomaly(double trueAnomaly, double ecc)
        {
            if (ecc < 1)
            {
                var eccentric = 2 * Math.Atan(Math.Sqrt((1 - ecc) / (1 + ecc)) * Math.Tan(trueAnomaly / 2));
                return eccentric - ecc * Math.Sin(eccentric);
            }
            var x = Math.Sqrt((ecc - 1) / (ecc + 1)) * Math.Tan(trueAnomaly / 2);
            var hyperbolic = Math.Log((1 + x) / (1 - x));
            return ecc * Math.Sinh(hyperbolic) - hyperbolic;
        }
    }

    /// <summary>
    /// Which parts that can't retract must stay stowed (#756): those still on the craft at an atmospheric entry ahead in the Mission
    /// Plan. An entry is a leg, the one in hand included, that lands on a body with air, reenters, returns home or aerobrakes, read by
    /// <see cref="SequenceWords"/>. A part on a stage the staging plan separates counts as dropped before the entry; the plan's words
    /// don't say which leg a stage leaves in. With no Mission Plan yet, nothing is known to be clear, so everything is held.
    /// </summary>
    public sealed class ShipSystemsHold
    {
        private readonly HashSet<uint> _leaving = new HashSet<uint>();
        private bool _holds;
        /// <summary>Why held parts stay stowed, for the Mission's events; "" when none is held.</summary>
        public string Why { get; private set; } = "";

        /// <summary>Holds nothing: for a transition that retracts.</summary>
        public static readonly ShipSystemsHold None = new ShipSystemsHold();

        public bool Holds(uint partId)
        {
            return _holds && !_leaving.Contains(partId);
        }

        /// <summary>The hold over <paramref name="plan"/> from step <paramref name="firstStep"/> (0 first; the leg in hand) on; a null plan is no Mission Plan yet.</summary>
        /// <param name="landed">The leg in hand has just landed: its entry is behind the craft, unless it also goes on home from another body.</param>
        public static ShipSystemsHold Find(GameSnapshot snapshot, IList<PlannedSequence> plan, int firstStep, bool remote, uint? processorPartId, bool landed = false)
        {
            var hold = new ShipSystemsHold();
            if (plan == null || snapshot == null)
            {
                hold._holds = true;
                hold.Why = "there is no Mission Plan yet";
                return hold;
            }
            var entry = FirstEntry(snapshot, plan, Math.Max(0, firstStep), landed);
            if (entry == null) return hold;
            hold._holds = true;
            hold.Why = "the Mission Plan enters " + entry.Value.Key + "'s atmosphere (" + entry.Value.Value + ")";
            foreach (var separation in ChildVessels.Staging(snapshot, remote, processorPartId).Separations)
                foreach (var id in separation.PartIds) hold._leaving.Add(id);
            return hold;
        }

        /// <summary>The first leg from <paramref name="firstStep"/> on through an atmosphere: its body and name; null for none.</summary>
        private static KeyValuePair<string, string>? FirstEntry(GameSnapshot snapshot, IList<PlannedSequence> plan, int firstStep, bool landed)
        {
            var at = snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
            var body = SequenceWords.Body(snapshot, at);
            for (var i = firstStep; i < plan.Count; i++)
            {
                var words = SequenceWords.Of(plan[i]);
                var returnsHome = SequenceWords.ReturnHome.IsMatch(words);
                var onBody = returnsHome ? SequenceWords.Body(snapshot, "Kerbin") ?? body : SequenceWords.LegBody(snapshot, words, body);
                body = SequenceWords.NextBody(snapshot, words, body);
                if (landed && i == firstStep && !(returnsHome && !string.Equals(at, "Kerbin", StringComparison.OrdinalIgnoreCase))) continue;
                if (onBody == null || (onBody.AtmosphereHeight <= 0 && onBody.SeaLevelAtm <= 0)) continue;
                if (SequenceWords.Landing.IsMatch(words) || SequenceWords.Reentry.IsMatch(words) || returnsHome || SequenceWords.Aero.IsMatch(words))
                    return new KeyValuePair<string, string>(onBody.Name, plan[i].Name);
            }
            return null;
        }

        /// <summary>The Craft Check's info line naming the parts that can't retract; "" for a craft with none.</summary>
        public static string CraftCheckLine(GameSnapshot snapshot)
        {
            var parts = snapshot == null ? new List<SnapshotPart>() : snapshot.Craft.Parts.Where(p => p.CannotRetract).ToList();
            if (parts.Count == 0) return "";
            var names = parts.GroupBy(p => p.Title.Length > 0 ? p.Title : p.Name)
                .Select(g => g.Count() > 1 ? g.Key + " x" + g.Count().ToString(CultureInfo.InvariantCulture) : g.Key).ToArray();
            return "Ship Systems that can't retract: " + string.Join(", ", names)
                + ". Claudia leaves them stowed if the Mission Plan enters an atmosphere with them aboard.";
        }
    }

    /// <summary>A transition made concrete: the parts to move, those held back, and the line for the Mission's events.</summary>
    public sealed class ShipSystemsMoves
    {
        public readonly List<ShipSystemPart> Extend = new List<ShipSystemPart>();
        public readonly List<ShipSystemPart> Retract = new List<ShipSystemPart>();
        public readonly List<ShipSystemPart> Held = new List<ShipSystemPart>();
        public readonly List<ShipSystemPart> LightsOn = new List<ShipSystemPart>();
        public readonly List<ShipSystemPart> LightsOff = new List<ShipSystemPart>();
        /// <summary>For the Mission's events and trace; "" when nothing moved and nothing was held back, which is no event.</summary>
        public string Text = "";
        /// <summary>A panel, antenna or radiator was on its way when this was worked out, so it must be asked again once it has stopped.</summary>
        public bool Unsettled;

        /// <summary>What <paramref name="transition"/> does to <paramref name="parts"/> as they are now. Only a part that would change is touched.</summary>
        public static ShipSystemsMoves Of(ShipSystemsTransition transition, IEnumerable<ShipSystemPart> parts, ShipSystemsHold hold)
        {
            var moves = new ShipSystemsMoves();
            foreach (var part in parts)
            {
                if (part.Kind == ShipSystemKind.Light)
                {
                    if (transition.Lights == ShipSystemsLights.On && part.State == ShipSystemState.Stowed) moves.LightsOn.Add(part);
                    if (transition.Lights == ShipSystemsLights.Off && part.State == ShipSystemState.Out) moves.LightsOff.Add(part);
                }
                else if (transition.Deployables != ShipSystemsDeployables.None && part.State == ShipSystemState.Moving)
                {
                    moves.Unsettled = true;
                }
                else if (transition.Deployables == ShipSystemsDeployables.Extend && part.State == ShipSystemState.Stowed)
                {
                    if (!part.CanRetract && hold.Holds(part.Id)) moves.Held.Add(part);
                    else moves.Extend.Add(part);
                }
                else if (transition.Deployables == ShipSystemsDeployables.Retract && part.State == ShipSystemState.Out && part.CanRetract)
                {
                    moves.Retract.Add(part);
                }
            }
            moves.Text = moves.Line(transition, hold);
            return moves;
        }

        private string Line(ShipSystemsTransition transition, ShipSystemsHold hold)
        {
            var moved = new List<string>();
            if (Extend.Count > 0) moved.Add("extended " + Count(Extend));
            if (Retract.Count > 0) moved.Add("retracted " + Count(Retract));
            if (LightsOn.Count > 0) moved.Add("lights on");
            if (LightsOff.Count > 0) moved.Add("lights off");
            var line = new List<string>();
            if (moved.Count > 0)
            {
                line.Add(string.Join(", ", moved.ToArray()));
                line.Add(transition.Why);
            }
            if (Held.Count > 0) line.Add("left " + Count(Held) + " stowed: can't retract, and " + hold.Why);
            return line.Count == 0 ? "" : "Ship Systems: " + string.Join("; ", line.ToArray());
        }

        private static string Count(IEnumerable<ShipSystemPart> parts)
        {
            return string.Join(", ", parts.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => Noun(g.Key, g.Count())).ToArray());
        }

        private static string Noun(ShipSystemKind kind, int count)
        {
            var name = kind == ShipSystemKind.Panel ? "panel" : kind == ShipSystemKind.Antenna ? "antenna" : kind == ShipSystemKind.Radiator ? "radiator" : "light";
            return count.ToString(CultureInfo.InvariantCulture) + " " + name + (count == 1 ? "" : "s");
        }
    }

    /// <summary>What a block's bounded stow wait hears: done, and whether the cap ended it (with the line for its trace).</summary>
    public sealed class ShipSystemsStowAnswer
    {
        public bool Done;
        public bool Capped;
        public string Note = "";

        /// <summary>The answer for no stow to wait for.</summary>
        public static readonly ShipSystemsStowAnswer Nothing = new ShipSystemsStowAnswer { Done = true };

        /// <summary>For <c>ADDONS:CLAUDIA:SHIPSYSTEMS</c>.</summary>
        public IDictionary<string, object> Lexicon()
        {
            return new Dictionary<string, object> { { "done", Done }, { "capped", Capped }, { "note", Note } };
        }
    }

    /// <summary>The wait at lift-off (#756): until the parts report retracted, at most <see cref="CapSeconds"/>; past that the block says so and goes on.</summary>
    public static class ShipSystemsStowWait
    {
        public const double CapSeconds = 10;

        /// <param name="startedUt">When the block gave the cue.</param>
        /// <param name="now">UT now.</param>
        /// <param name="stillOut">Parts not yet retracted, counting a cue not acted on yet as one.</param>
        public static ShipSystemsStowAnswer Step(double startedUt, double now, int stillOut)
        {
            if (stillOut <= 0) return new ShipSystemsStowAnswer { Done = true };
            if (now - startedUt < CapSeconds) return new ShipSystemsStowAnswer();
            return new ShipSystemsStowAnswer
            {
                Done = true,
                Capped = true,
                Note = "Ship Systems: " + stillOut.ToString(CultureInfo.InvariantCulture) + " still not retracted after "
                    + CapSeconds.ToString("0", CultureInfo.InvariantCulture) + " s; lifting off anyway",
            };
        }

        /// <summary>The parts a stow still waits on: every one that can retract and isn't in, lights aside.</summary>
        public static int StillOut(IEnumerable<ShipSystemPart> parts)
        {
            return parts.Count(p => p.Kind != ShipSystemKind.Light && p.CanRetract && (p.State == ShipSystemState.Out || p.State == ShipSystemState.Moving));
        }
    }
}
