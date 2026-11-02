// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Missions
{
    /// <summary>One parachute of the craft, as the Chute Allocation gives it out.</summary>
    public sealed class AllocatedChute
    {
        /// <summary>The part's <c>persistentId</c>, as the Game Snapshot has it.</summary>
        public uint PartId;
        public string Title = "";
        public bool Drogue;
        /// <summary>The chute stays with the pod to the end (no staged separation takes it): the return's chutes.</summary>
        public bool Pod;
        /// <summary>The drag area it adds fully open, m².</summary>
        public double FullArea;
    }

    /// <summary>One remaining atmospheric entry of the Mission Plan and the chutes it may open.</summary>
    public sealed class EntryChutes
    {
        /// <summary>The entry's Maneuver Sequence: its index in the Mission Plan (0 first) and name.</summary>
        public int Step;
        public string Sequence = "";
        public string Body = "";
        /// <summary>The Kerbin return: served first, from the pod's chutes.</summary>
        public bool Return;
        /// <summary>The chutes it may open, mains with the drogues that serve them.</summary>
        public readonly List<AllocatedChute> Chutes = new List<AllocatedChute>();
        /// <summary>The mass it comes down with, kg, as the allocation took it.</summary>
        public double MassKg;
        /// <summary>The touchdown speed its chutes leave it at, m/s, by the reentry predictor; -1 when it couldn't be predicted (<see cref="Unpredicted"/>).</summary>
        public double TouchdownSpeed = -1;
        /// <summary>Its chutes can't bring it under <see cref="ChuteAllocation.TouchdownLimit"/>: the engines finish the landing from <see cref="TouchdownSpeed"/>.</summary>
        public bool PoweredFinish;
        /// <summary>The height above sea level, m, its fall was predicted down to (<see cref="ChuteAllocation.EndAltitudeOf"/>).</summary>
        public double EndAltitude;
        /// <summary>Why <see cref="TouchdownSpeed"/> is unknown; "" when it is known.</summary>
        public string Unpredicted = "";
    }

    /// <summary>
    /// The Chute Allocation (#626): the chutes each remaining atmospheric entry of the Mission Plan may open, by need, with the
    /// pod's chutes kept for the return. An entry is a remaining Maneuver Sequence that lands on a body with air, or reenters or
    /// returns to Kerbin (<see cref="SequenceWords"/>, the Margin check's matcher; aerobraking and aerocapture are not entries).
    /// The Kerbin return is served first, from the chutes that stay with the pod, with as few as keep its touchdown under
    /// <see cref="TouchdownLimit"/> by the reentry predictor; the other entries then take what they need of the rest in plan
    /// order, the chutes that leave with other stages first. An entry whose share can't reach the limit takes all that is
    /// left and is a powered finish from the speed its chutes leave it at. What no entry needs stays with the return (or the
    /// last entry). Worked out at Mission Plan Approval and again at each Debrief, from the craft as it is; reentry "auto" reads
    /// its entry's set through <c>ADDONS:CLAUDIA:CHUTEALLOCATION</c> (<see cref="BlockCalls.ChuteAllocationCall"/>).
    /// </summary>
    public sealed class ChuteAllocation
    {
        /// <summary>reentry's touchdown limit, m/s (reentry.ks's reentry_touchdown_limit).</summary>
        public const double TouchdownLimit = 15;
        /// <summary>
        /// The craft's own drag area, m², held retrograde, for the fall to its chutes' heights: about a Mk1 pod's. Under an
        /// open chute it hardly counts.
        /// </summary>
        public const double BodyDragArea = 1;

        /// <summary>The share of a body's radius taken as its highest common terrain when the Game Snapshot gives no figure (Duna's highlands are about 2.5%).</summary>
        public const double TerrainShareOfRadius = 0.01;

        /// <summary>
        /// Where an entry on <paramref name="body"/> is predicted to touch down, m above sea level: the body's own terrain
        /// height from the Game Snapshot when it has one, else sea level for Kerbin (the return lands at KSC) and a conservative
        /// <see cref="TerrainShareOfRadius"/> of the radius for other bodies, so thinner air never makes the touchdown too soft.
        /// </summary>
        public static double EndAltitudeOf(SnapshotBody body)
        {
            if (body.TerrainHeight > 0) return body.TerrainHeight;
            if (string.Equals(body.Name, "Kerbin", StringComparison.OrdinalIgnoreCase)) return 0;
            return Math.Min(body.Radius * TerrainShareOfRadius, 0.5 * body.AtmosphereHeight);
        }

        /// <summary>The remaining entries, in plan order.</summary>
        public readonly List<EntryChutes> Entries = new List<EntryChutes>();
        /// <summary>The Kerbin return's touchdown can't be brought under the limit by the pod's chutes (the Craft Check's Blocker).</summary>
        public bool ReturnUncovered;
        /// <summary>The craft's usable chutes (stowed or armed), whoever has them.</summary>
        public readonly List<AllocatedChute> Chutes = new List<AllocatedChute>();

        /// <summary>The plan this was made for: <see cref="PlanOf"/> of the steps it covered. "" for an allocation made for no plan.</summary>
        public string ForPlan = "";

        /// <summary>The Kerbin return entry, or null for none.</summary>
        public EntryChutes Return
        {
            get { return Entries.FirstOrDefault(e => e.Return); }
        }

        /// <summary>
        /// The entry the reentry flown in step <paramref name="step"/> (0 first) on <paramref name="body"/> is: that step's own entry,
        /// or the next entry on the same body (a deorbit step before its landing step). Null for none: the reentry keeps every chute.
        /// </summary>
        public EntryChutes For(int step, string body)
        {
            var same = Entries.Where(e => string.Equals(e.Body, body, StringComparison.OrdinalIgnoreCase)).ToList();
            return same.FirstOrDefault(e => e.Step == step) ?? same.FirstOrDefault(e => e.Step > step);
        }

        /// <summary>One line per entry, for the log and tests.</summary>
        public string Describe()
        {
            if (Entries.Count == 0) return "No atmospheric entry left to fly: the reentry keeps every chute.";
            return string.Join(" ", Entries.Select(e => e.Sequence + " (" + e.Body + (e.Return ? ", the return" : "") + "): "
                + (e.Chutes.Count == 0 ? "no chutes" : string.Join(", ", e.Chutes.Select(c => c.Title).ToArray()))
                + (e.TouchdownSpeed >= 0 ? ", " + e.TouchdownSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : ", speed unknown (" + e.Unpredicted + ")")
                + (e.PoweredFinish ? ", powered finish" : "") + ".").ToArray())
                + (ReturnUncovered ? " The return can't be covered." : "");
        }

        /// <summary>
        /// The allocation for <paramref name="snapshot"/>'s craft over the Mission Plan's <paramref name="sequences"/> from step
        /// <paramref name="firstStep"/> (0 first) on. The pod is the part the craft keeps (<see cref="ChildVessels.Staging"/>:
        /// aboard, the crew part nearest the root; remote, the processor <paramref name="processorPartId"/>).
        /// </summary>
        public static ChuteAllocation Of(GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep, bool remote, uint? processorPartId)
        {
            var allocation = new ChuteAllocation();
            if (snapshot == null || sequences == null) return allocation;
            allocation.ForPlan = PlanOf(sequences, firstStep);
            allocation.Entries.AddRange(FindEntries(snapshot, sequences, Math.Max(0, firstStep)));
            var division = new Division(snapshot, remote, processorPartId, allocation);
            allocation.Chutes.AddRange(division.All);
            if (allocation.Entries.Count > 0) division.Divide(sequences, Math.Max(0, firstStep));
            return allocation;
        }

        /// <summary>
        /// The Mission Goal as legs for the allocation before any Mission Plan exists (#641): one per clause of the goal (split at
        /// punctuation, "and" and "then"), read by <see cref="SequenceWords"/> as the plan's legs are. Only a craft on the ground
        /// has a leg with delta-v: the Delta-v Map's launch from its body, first, so the entries come down at the mass the launch
        /// leaves. The others carry none (no plan has budgeted them; the Craft Check's delta_v_short judges the route).
        /// </summary>
        public static List<PlannedSequence> GoalLegs(GameSnapshot snapshot, string goal)
        {
            var legs = new List<PlannedSequence>();
            if (snapshot == null) return legs;
            var grounded = snapshot.Situation == "PRELAUNCH" || snapshot.Situation == "LANDED" || snapshot.Situation == "SPLASHED";
            var launch = grounded ? DeltaVMap.Compute(snapshot.Bodies, snapshot.Body).Leg("launch:" + snapshot.Body) : null;
            if (launch != null) legs.Add(new PlannedSequence { Name = "Launch from " + snapshot.Body, DeltaV = launch.DeltaV });
            foreach (var clause in GoalClause.Split(goal ?? ""))
                if (clause.Trim().Length > 0) legs.Add(new PlannedSequence { Name = clause.Trim() });
            return legs;
        }

        private static readonly Regex GoalClause = new Regex(@"[.;:,!?]|\b(?:and|then)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// The allocation over the goal's <paramref name="legs"/> (<see cref="GoalLegs"/>), made for no plan; null when a parachute
        /// of the craft has no facts read (a cut chute, or a snapshot that read none), so the chutes aren't judged on a guess, and
        /// for a craft with no parachute at all (no_parachutes_for_return is Claude's; a powered landing at home isn't refused unasked).
        /// </summary>
        public static ChuteAllocation ForGoal(GameSnapshot snapshot, IList<PlannedSequence> legs, bool remote, uint? processorPartId)
        {
            if (snapshot == null) return null;
            var chutes = snapshot.Craft.Parts.Where(p => p.Tags.Contains("parachute")).ToList();
            if (chutes.Count == 0 || chutes.Any(p => p.Chute == null)) return null;
            var allocation = Of(snapshot, legs, 0, remote, processorPartId);
            allocation.ForPlan = "";
            return allocation;
        }

        /// <summary>A fingerprint of the plan from step <paramref name="firstStep"/> on: what the allocation reads of each leg (words, manual, delta-v) and where it starts.</summary>
        public static string PlanOf(IList<PlannedSequence> sequences, int firstStep)
        {
            var first = Math.Max(0, firstStep);
            return first.ToString(CultureInfo.InvariantCulture) + "|" + string.Join("|", sequences.Skip(first)
                .Select(s => SequenceWords.Of(s) + "\u001f" + (s.Manual ? "m" : "") + s.DeltaV.ToString("R", CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>
        /// <paramref name="held"/> when it was made for this plan from this step on, else the allocation worked out again (#645: a
        /// revision between Approval or a Debrief and a Margin Check leaves the held one stale).
        /// </summary>
        public static ChuteAllocation Current(ChuteAllocation held, GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep, bool remote, uint? processorPartId)
        {
            if (held != null && sequences != null && held.ForPlan == PlanOf(sequences, firstStep)) return held;
            return Of(snapshot, sequences, firstStep, remote, processorPartId);
        }

        /// <summary>
        /// The remaining atmospheric entries: legs that land on a body with air, reenter, or return to Kerbin. The body is the
        /// one the words name, else the one the plan is at by then (as the Margin check follows it). A leg on the same body as the
        /// entry before it, with no ascent between, is the same entry (a deorbit leg, then its landing leg); the later leg names it.
        /// </summary>
        private static List<EntryChutes> FindEntries(GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep)
        {
            var entries = new List<EntryChutes>();
            var at = snapshot.Orbit != null && snapshot.Orbit.Body.Length > 0 ? snapshot.Orbit.Body : snapshot.Body;
            var body = SequenceWords.Body(snapshot, at);
            var ascended = false;
            for (var i = firstStep; i < sequences.Count; i++)
            {
                var leg = sequences[i];
                var words = SequenceWords.Of(leg);
                var toKerbin = SequenceWords.ReturnHome.IsMatch(words);
                body = (toKerbin ? SequenceWords.Body(snapshot, "Kerbin") : null) ?? SequenceWords.NamedBody(snapshot, words) ?? body;
                if (SequenceWords.Ascent.IsMatch(words)) ascended = true;
                if (leg.Manual || body == null) continue;
                var landing = SequenceWords.Landing.IsMatch(words);
                var entry = landing || SequenceWords.Reentry.IsMatch(words) || toKerbin;
                if (!entry || (SequenceWords.Aero.IsMatch(words) && !landing)) continue;
                if (body.AtmosphereHeight <= 0 && body.SeaLevelAtm <= 0) continue;
                var last = entries.Count > 0 ? entries[entries.Count - 1] : null;
                if (last != null && last.Body == body.Name && !ascended) entries.Remove(last);
                ascended = false;
                entries.Add(new EntryChutes { Step = i, Sequence = leg.Name, Body = body.Name, Return = body.Name == "Kerbin" });
            }
            // Only the last Kerbin entry is the return; an earlier one (a Kerbin landing before a relaunch) is like any other.
            var home = entries.LastOrDefault(e => e.Return);
            foreach (var entry in entries) entry.Return = entry == home;
            return entries;
        }

        /// <summary>The division by need: the chutes, the pod's, and the predictor's touchdown for a set.</summary>
        private sealed class Division
        {
            private readonly GameSnapshot _snapshot;
            private readonly ChuteAllocation _allocation;
            private readonly Dictionary<uint, ReentryChute> _facts = new Dictionary<uint, ReentryChute>();
            /// <summary>Each chute's module: the separation that takes it (its index), or -1 for the pod's.</summary>
            private readonly Dictionary<uint, int> _module = new Dictionary<uint, int>();
            private readonly HashSet<uint> _taken = new HashSet<uint>();
            private readonly double _podMassKg;
            public readonly List<AllocatedChute> All = new List<AllocatedChute>();

            public Division(GameSnapshot snapshot, bool remote, uint? processorPartId, ChuteAllocation allocation)
            {
                _snapshot = snapshot;
                _allocation = allocation;
                var staging = ChildVessels.Staging(snapshot, remote, processorPartId);
                var leaving = new Dictionary<uint, int>();
                for (var s = 0; s < staging.Separations.Count; s++)
                    foreach (var id in staging.Separations[s].PartIds) leaving[id] = s;
                foreach (var part in snapshot.Craft.Parts)
                {
                    int module;
                    if (!leaving.TryGetValue(part.Id, out module)) module = -1;
                    if (module < 0) _podMassKg += part.MassT * 1000;
                    // A chute open already, or one the adapter couldn't read (cut), has nothing to give.
                    var chute = part.Chute;
                    if (chute == null || chute.State == ReentryChuteState.SemiDeployed || chute.State == ReentryChuteState.Deployed) continue;
                    _facts[part.Id] = chute;
                    _module[part.Id] = module;
                    All.Add(new AllocatedChute
                    {
                        PartId = part.Id, Title = part.Title.Length > 0 ? part.Title : part.Name, Drogue = chute.Drogue, Pod = module < 0, FullArea = chute.FullArea,
                    });
                }
            }

            public void Divide(IList<PlannedSequence> sequences, int firstStep)
            {
                var entries = _allocation.Entries;
                var home = _allocation.Return;
                if (home != null)
                {
                    home.MassKg = _podMassKg > 0 ? _podMassKg : _snapshot.Craft.MassTonnes * 1000;
                    Serve(home, All.Where(c => c.Pod));
                    _allocation.ReturnUncovered = home.PoweredFinish;
                }
                foreach (var entry in entries)
                {
                    if (entry == home) continue;
                    entry.MassKg = MassAfter(sequences.Skip(firstStep).Take(entry.Step - firstStep).Sum(s => Math.Max(0, s.DeltaV)));
                    // The chutes that leave with other stages first, so the pod's spare ones are the last given away.
                    Serve(entry, All.Where(c => !_taken.Contains(c.PartId)).OrderBy(c => c.Pod ? 1 : 0));
                }
                // What no entry needs stays with the return, or the last entry.
                var keeper = home ?? entries[entries.Count - 1];
                var left = All.Where(c => !_taken.Contains(c.PartId)).ToList();
                if (left.Count == 0) return;
                foreach (var chute in left) Take(keeper, chute);
                Judge(keeper);
                if (keeper == home) _allocation.ReturnUncovered = home.PoweredFinish;
            }

            /// <summary>
            /// Gives <paramref name="entry"/> the fewest of <paramref name="offered"/> (those leaving with other stages before
            /// the pod's, largest first) that bring its touchdown under the limit, with the drogues of each main's stage; all of them when none do.
            /// </summary>
            private void Serve(EntryChutes entry, IEnumerable<AllocatedChute> offered)
            {
                var pool = offered.ToList();
                var hasMains = new HashSet<int>(pool.Where(c => !c.Drogue).Select(c => _module[c.PartId]));
                // Mains are the units; a stage's drogues come with its mains, or are units themselves on a stage with none.
                var units = pool.Where(c => !c.Drogue || !hasMains.Contains(_module[c.PartId]))
                    .Select((c, i) => new { Chute = c, Order = i })
                    .OrderBy(u => u.Chute.Pod ? 1 : 0).ThenByDescending(u => u.Chute.FullArea).ThenBy(u => u.Order)
                    .Select(u => u.Chute).ToList();
                Judge(entry);
                foreach (var unit in units)
                {
                    if (Covered(entry)) break;
                    TakeUnit(entry, unit, pool);
                    Judge(entry);
                }
                if (!Covered(entry)) return;
                // Then the smallest mains it can do without.
                foreach (var unit in units.Where(u => entry.Chutes.Contains(u)).OrderBy(u => u.FullArea).ToList())
                {
                    var kept = entry.Chutes.ToList();
                    Release(entry, unit);
                    Judge(entry);
                    if (Covered(entry)) continue;
                    entry.Chutes.Clear();
                    entry.Chutes.AddRange(kept);
                    foreach (var chute in kept) _taken.Add(chute.PartId);
                    Judge(entry);
                }
            }

            private static bool Covered(EntryChutes entry)
            {
                return entry.TouchdownSpeed >= 0 && entry.TouchdownSpeed <= TouchdownLimit;
            }

            private void TakeUnit(EntryChutes entry, AllocatedChute unit, List<AllocatedChute> pool)
            {
                Take(entry, unit);
                if (unit.Drogue) return;
                var module = _module[unit.PartId];
                foreach (var drogue in pool.Where(c => c.Drogue && _module[c.PartId] == module && !_taken.Contains(c.PartId)).ToList()) Take(entry, drogue);
            }

            private void Take(EntryChutes entry, AllocatedChute chute)
            {
                if (!_taken.Add(chute.PartId)) return;
                entry.Chutes.Add(chute);
            }

            /// <summary>Gives back a main, and its stage's drogues when no other main of that stage stays.</summary>
            private void Release(EntryChutes entry, AllocatedChute unit)
            {
                var module = _module[unit.PartId];
                var gone = new List<AllocatedChute> { unit };
                if (!unit.Drogue && !entry.Chutes.Any(c => c != unit && !c.Drogue && _module[c.PartId] == module))
                    gone.AddRange(entry.Chutes.Where(c => c.Drogue && _module[c.PartId] == module));
                foreach (var chute in gone)
                {
                    entry.Chutes.Remove(chute);
                    _taken.Remove(chute.PartId);
                }
            }

            /// <summary>
            /// Sets the entry's touchdown speed by the reentry predictor, and whether it is a powered finish: a fall from a low
            /// orbit of its body through the body's air (the Game Snapshot's rows) to sea level, opening its chutes by reentry's
            /// rules. The return counts only the pod's chutes (the others have gone by then).
            /// </summary>
            private void Judge(EntryChutes entry)
            {
                var body = SequenceWords.Body(_snapshot, entry.Body);
                var chutes = entry.Chutes.Where(c => !entry.Return || c.Pod).Select(c => Copy(_facts[c.PartId])).ToList();
                entry.TouchdownSpeed = -1;
                entry.PoweredFinish = false;
                entry.Unpredicted = "";
                if (body == null || body.Gm <= 0 || body.Radius <= 0 || body.AtmosphereHeight <= 0)
                {
                    entry.Unpredicted = "the Game Snapshot has no air for " + entry.Body;
                    return;
                }
                if (entry.MassKg <= 0)
                {
                    entry.Unpredicted = "the craft's mass is unknown";
                    return;
                }
                entry.EndAltitude = EndAltitudeOf(body);
                var top = body.Radius + body.AtmosphereHeight;
                var apo = top + Math.Max(5000, 0.1 * body.AtmosphereHeight);
                var peri = body.Radius + 0.3 * body.AtmosphereHeight;
                var orbit = Conic.FromElements(body.Gm, (apo + peri) / 2, (apo - peri) / (apo + peri), 0, 0, 0, Math.PI, 0);
                var frame = new BodyFrame { LonOffset = 0, Handed = 1, Ut = 0, RotationPeriod = body.RotationPeriod };
                var craft = new FallingCraft(entry.MassKg, chutes);
                var prediction = ReentryPredictor.Predict(body, orbit, 0, entry.EndAltitude, entry.MassKg, new SnapshotAir(body), craft, frame);
                if (prediction.Error != null)
                {
                    entry.Unpredicted = prediction.Error;
                    return;
                }
                entry.TouchdownSpeed = prediction.LandingSpeed;
                entry.PoweredFinish = entry.TouchdownSpeed > TouchdownLimit;
            }

            private static ReentryChute Copy(ReentryChute chute)
            {
                return new ReentryChute
                {
                    Drogue = chute.Drogue, State = chute.State, MinPressureAtm = chute.MinPressureAtm, DeployAltitude = chute.DeployAltitude,
                    SemiArea = chute.SemiArea, FullArea = chute.FullArea, SemiSeconds = chute.SemiSeconds, FullSeconds = chute.FullSeconds, Curve = chute.Curve,
                };
            }

            /// <summary>
            /// The craft's mass, kg, once the legs before have burned <paramref name="deltaV"/> m/s: its stages in firing order
            /// (highest number first), each from its start mass, as the Margin check spends them; the craft as it is when the
            /// stages aren't known.
            /// </summary>
            private double MassAfter(double deltaV)
            {
                var stages = _snapshot.Stages.Where(s => s.DeltaVVac > 0 && s.StartMassT > s.EndMassT && s.EndMassT > 0).OrderByDescending(s => s.Number).ToList();
                var mass = _snapshot.Craft.MassTonnes;
                if (stages.Count == 0 || deltaV <= 0) return mass * 1000;
                foreach (var stage in stages)
                {
                    if (deltaV < stage.DeltaVVac)
                    {
                        var exhaust = stage.DeltaVVac / Math.Log(stage.StartMassT / stage.EndMassT);
                        return stage.StartMassT * Math.Exp(-deltaV / exhaust) * 1000;
                    }
                    deltaV -= stage.DeltaVVac;
                    mass = stage.EndMassT;
                }
                return mass * 1000;
            }
        }

        /// <summary>A body's air for the predictor from its rows in the Game Snapshot (<see cref="BodyAir"/>, as plan_ascent reads it).</summary>
        private sealed class SnapshotAir : IReentryAir
        {
            private readonly BodyAir _air;

            public SnapshotAir(SnapshotBody body)
            {
                _air = new BodyAir(body);
            }

            public void Air(double altitude, double latitude, out double density, out double speedOfSound)
            {
                double atm;
                _air.Air(altitude, out atm, out density, out speedOfSound);
            }

            public double PressureAtm(double altitude)
            {
                double atm, density, sound;
                _air.Air(altitude, out atm, out density, out sound);
                return atm;
            }
        }

        /// <summary>The entry's craft for the predictor: its mass, <see cref="BodyDragArea"/>, no lift, its chutes, the heat shield kept.</summary>
        private sealed class FallingCraft : IReentryCraft
        {
            private readonly double _mass;
            private readonly IList<ReentryChute> _chutes;

            public FallingCraft(double massKg, IList<ReentryChute> chutes)
            {
                _mass = massKg;
                _chutes = chutes;
            }

            public double MassKg
            {
                get { return _mass; }
            }

            public double DragArea(double mach, double densitySpeed)
            {
                return BodyDragArea;
            }

            public double LiftArea(double mach, double angleOfAttack)
            {
                return 0;
            }

            public IList<ReentryChute> Chutes
            {
                get { return _chutes; }
            }

            public IReentryCraft AfterShield
            {
                get { return null; }
            }
        }
    }
}
