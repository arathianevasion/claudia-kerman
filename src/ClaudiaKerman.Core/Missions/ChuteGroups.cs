// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Tools;

namespace ClaudiaKerman.Core.Missions
{
    /// <summary>One usable parachute of the craft (stowed or armed), with the stage that puts it in a Chute Group.</summary>
    public sealed class GroupChute
    {
        /// <summary>The part's <c>persistentId</c>, as the Game Snapshot has it.</summary>
        public uint PartId;
        public string Title = "";
        /// <summary>The part's own stage number (KSP's <c>inverseStage</c>); -1 with staging disabled.</summary>
        public int Stage;
        /// <summary>The stage whose separation takes it, by the staging plan; null when no separation does.</summary>
        public int? LeavesAtStage;
    }

    /// <summary>One remaining Entry of the Mission Plan and the chutes the rule gives it.</summary>
    public sealed class EntryChutes
    {
        /// <summary>The Entry's Maneuver Sequence: its index in the Mission Plan (0 first) and name.</summary>
        public int Step;
        public string Sequence = "";
        public string Body = "";
        /// <summary>The Kerbin return.</summary>
        public bool Return;
        /// <summary>It is the Last Entry: it opens every chute aboard, not one stage's.</summary>
        public bool Last;
        /// <summary>The chutes it opens: its Chute Group, or on the Last Entry all that are left.</summary>
        public readonly List<GroupChute> Chutes = new List<GroupChute>();
        /// <summary>The mass it comes down with, kg.</summary>
        public double MassKg;
        /// <summary>The touchdown speed its chutes leave it at, m/s, by the reentry predictor; -1 when it couldn't be predicted (<see cref="Unpredicted"/>).</summary>
        public double TouchdownSpeed = -1;
        /// <summary>Its chutes can't bring it under <see cref="ChuteGroups.TouchdownLimit"/>: the engines finish the landing from <see cref="TouchdownSpeed"/>.</summary>
        public bool PoweredFinish;
        /// <summary>Over <see cref="EntryFinish.ThresholdFor"/> its body with an engine aboard: <c>reentry</c> would land on it, so the delta-v is budgeted (<see cref="MarginCheck"/>), though under <see cref="ChuteGroups.TouchdownLimit"/> it isn't needed to survive.</summary>
        public bool EngineFinish;
        /// <summary>The height above sea level, m, its fall was predicted down to (<see cref="ChuteGroups.EndAltitudeOf"/>).</summary>
        public double EndAltitude;
        /// <summary>Why <see cref="TouchdownSpeed"/> is unknown; "" when it is known.</summary>
        public string Unpredicted = "";
    }

    /// <summary>The Entry in hand's chutes as <see cref="ChuteGroups.Widen"/> leaves them (#827).</summary>
    public sealed class Widened
    {
        public EntryChutes Entry;
        /// <summary>Groups opened beyond the Entry's own.</summary>
        public int Extra;
        /// <summary>The touchdown predicted with the Entry's own group, m/s; -1 unknown.</summary>
        public double Before = -1;
        /// <summary>The script's <c>chutes: "none"</c> was overridden: a certain crash with chutes aboard.</summary>
        public bool Overrode;
        /// <summary>For the Mission window and Debrief: nothing can land the craft, said while it is above the air; "" otherwise.</summary>
        public string Warning = "";

        /// <summary>Why, with the predicted speeds before and after, for the log; "" when nothing was widened or overridden.</summary>
        public string Why()
        {
            if (Extra == 0 && !Overrode) return "";
            var stages = string.Join(", ", Entry.Chutes.Select(c => c.Stage).Distinct().OrderByDescending(s => s).Select(s => s.ToString(CultureInfo.InvariantCulture)).ToArray());
            return (Overrode ? (Before < 0 ? "chutes none overridden: no engine, and the touchdown prediction failed" + (Entry.Unpredicted.Length > 0 ? " (" + Entry.Unpredicted + ")" : "") + ". "
                    : "chutes none overridden: no engine, and a crash otherwise. ") : "")
                + "Opening " + Entry.Chutes.Count + " chutes of stage " + stages + (Extra > 0 ? ", " + Extra + " more than its own group" : "")
                + ": predicted " + ChuteGroups.Speed(Before) + " with its own group, " + ChuteGroups.Speed(Entry.TouchdownSpeed) + " now.";
        }
    }

    /// <summary>
    /// Chute Groups (ADR 0019): the player places chutes by stage, and the staging list decides which open. An Entry that isn't the Last
    /// Entry opens the Chute Group of the highest stage aboard; a Last Entry opens every chute aboard. <see cref="Choose"/> is the rule:
    /// <c>reentry</c> asks it (and re-reads each chute's stage and state against the answer before each opening) through <c>ADDONS:CLAUDIA:CHUTEGROUP</c> (<see cref="BlockCalls.ChuteGroupCall"/>)
    /// for the Entry in hand, and <see cref="Of"/> walks it down the Entries left, each taking its group after its own drop, for the
    /// Margin Check, the Craft Check and the cards. Everything is read from the Game Snapshot it is given, with no memory of use: a repacked
    /// chute reads as stowed and counts again, a restaged one is in its new group, an open or cut one doesn't count, and one that has left isn't there.
    /// </summary>
    public sealed class ChuteGroups
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

        /// <summary>The remaining Entries, in plan order.</summary>
        public readonly List<EntryChutes> Entries = new List<EntryChutes>();
        /// <summary>The Kerbin return's touchdown can't be brought under the limit by its chutes (the Craft Check's Blocker).</summary>
        public bool ReturnUncovered;
        /// <summary>The craft's usable chutes (stowed or armed), whichever Entry has them.</summary>
        public readonly List<GroupChute> Chutes = new List<GroupChute>();

        /// <summary>The Kerbin return entry, or null for none.</summary>
        public EntryChutes Return
        {
            get { return Entries.FirstOrDefault(e => e.Return); }
        }

        /// <summary>
        /// The rule: of the chutes <paramref name="aboard"/>, those an Entry opens. A Last Entry opens all of them; any other Entry those of
        /// the highest stage, and stage -1 (staging disabled) comes after every numbered stage. <paramref name="extra"/> adds that many of the
        /// next stages' groups, in that order (#827).
        /// </summary>
        public static List<GroupChute> Choose(IEnumerable<GroupChute> aboard, bool lastEntry, int extra = 0)
        {
            var left = aboard.ToList();
            if (lastEntry || left.Count == 0) return left;
            var stages = left.Select(c => c.Stage).Distinct().OrderByDescending(s => s).Take(1 + Math.Max(0, extra)).ToList();
            return left.Where(c => stages.Contains(c.Stage)).ToList();
        }

        /// <summary>The craft's usable chutes as this Game Snapshot reads them: an open one, or one with nothing read (cut), has nothing to give.</summary>
        public static List<GroupChute> Usable(GameSnapshot snapshot, bool remote = false, uint? processorPartId = null)
        {
            var leaving = new Dictionary<uint, int>();
            foreach (var separation in ChildVessels.Staging(snapshot, remote, processorPartId).Separations)
                foreach (var id in separation.PartIds) leaving[id] = separation.Stage;
            var usable = new List<GroupChute>();
            foreach (var part in snapshot.Craft.Parts)
            {
                var chute = part.Chute;
                if (chute == null || chute.State == ReentryChuteState.SemiDeployed || chute.State == ReentryChuteState.Deployed) continue;
                int stage;
                usable.Add(new GroupChute
                {
                    PartId = part.Id, Title = part.Title.Length > 0 ? part.Title : part.Name, Stage = part.Stage,
                    LeavesAtStage = leaving.TryGetValue(part.Id, out stage) ? stage : (int?)null,
                });
            }
            return usable;
        }

        /// <summary>
        /// The Entry in hand, for the craft as it is now on the body it is at, less the parts <paramref name="gone"/> (its drop, if any): what
        /// <see cref="Choose"/> gives it, or none with <paramref name="chutes"/> false, with its predicted touchdown.
        /// </summary>
        public static EntryChutes ForEntryNow(GameSnapshot snapshot, bool lastEntry, ICollection<uint> gone = null, bool chutes = true, int extra = 0)
        {
            var leaving = gone ?? new uint[0];
            var mass = snapshot.Craft.MassTonnes - snapshot.Craft.Parts.Where(p => leaving.Contains(p.Id)).Sum(p => p.MassT);
            var entry = new EntryChutes { Body = Missions.Entries.At(snapshot), Last = lastEntry, MassKg = mass * 1000 };
            if (chutes) entry.Chutes.AddRange(Choose(Usable(snapshot).Where(c => !leaving.Contains(c.PartId)), lastEntry, extra));
            Judge(snapshot, entry);
            return entry;
        }

        /// <summary>
        /// What the Entry in hand opens when its own group can't land the craft and no engine will (#827, survival beats saving chutes): the
        /// Chute Group of <see cref="ForEntryNow"/>, and while its predicted touchdown is over <see cref="TouchdownLimit"/> the next stage's group too, one at a
        /// time, until it is at or under, or none is left. A predictor error leaves the Entry its own group. With <paramref name="none"/> (the script's
        /// <c>chutes: "none"</c>) the own group is empty, and a fall over the limit overrides it, as a certain crash; so does an unknown speed (#846), with the own group only. With <paramref name="engine"/> (the Entry lands on
        /// its engines, #824) nothing is widened. A touchdown still over the limit while the craft is above the air gives the <see cref="Widened.Warning"/>.
        /// </summary>
        public static Widened Widen(GameSnapshot snapshot, bool lastEntry, bool none, bool engine)
        {
            var entry = ForEntryNow(snapshot, lastEntry, null, !none);
            var widened = new Widened { Entry = entry, Before = entry.TouchdownSpeed };
            if (engine) return widened;
            if (none)
            {
                // An unknown speed (-1) is not a known soft landing: only a known one at or under the limit keeps "none" (#846).
                if (entry.TouchdownSpeed >= 0 && entry.TouchdownSpeed <= TouchdownLimit) return widened;
                var forced = ForEntryNow(snapshot, lastEntry);
                if (forced.Chutes.Count > 0)
                {
                    widened.Entry = entry = forced;
                    widened.Overrode = true;
                }
            }
            while (entry.TouchdownSpeed > TouchdownLimit)
            {
                var next = ForEntryNow(snapshot, lastEntry, null, true, widened.Extra + 1);
                if (next.Chutes.Count == entry.Chutes.Count) break;
                widened.Extra++;
                widened.Entry = entry = next;
            }
            var body = SequenceWords.Body(snapshot, entry.Body);
            if (entry.TouchdownSpeed > TouchdownLimit && body != null && snapshot.Altitude > body.AtmosphereHeight)
                widened.Warning = "Nothing can land this craft safely: it is predicted to touch down at " + Speed(entry.TouchdownSpeed) + ", over " + Speed(TouchdownLimit)
                    + ", with " + (entry.Chutes.Count == 0 ? "no chutes" : "every chute it can open") + " and no engine to finish it";
            return widened;
        }

        internal static string Speed(double speed)
        {
            return speed >= 0 ? speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : "speed unknown";
        }

        /// <summary>One sentence per entry, for the cards, the log and tests; "" with no Entry left.</summary>
        public string Describe()
        {
            return string.Join(" ", Entries.Select(e => e.Sequence + " (" + e.Body + "): " + GroupOf(e)
                + (e.TouchdownSpeed >= 0 ? ", " + e.TouchdownSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s" : ", speed unknown (" + e.Unpredicted + ")")
                + (e.PoweredFinish || e.EngineFinish ? ", powered finish" : "") + ".").ToArray());
        }

        private static string GroupOf(EntryChutes entry)
        {
            if (entry.Chutes.Count == 0) return "no chutes";
            var count = entry.Chutes.Count + (entry.Chutes.Count == 1 ? " chute" : " chutes");
            return entry.Last ? "every chute aboard (" + count + ")" : "stage " + entry.Chutes[0].Stage.ToString(CultureInfo.InvariantCulture) + " (" + count + ")";
        }

        /// <summary>
        /// The groups for <paramref name="snapshot"/>'s craft over the Mission Plan's <paramref name="sequences"/> from step
        /// <paramref name="firstStep"/> (0 first) on, worked out now. The pod is the part the craft keeps (<see cref="ChildVessels.Staging"/>:
        /// aboard, the crew part nearest the root; remote, the processor <paramref name="processorPartId"/>).
        /// </summary>
        public static ChuteGroups Of(GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep, bool remote, uint? processorPartId)
        {
            Func<PlannedSequence, bool> flown = s => snapshot == null || Missions.Entries.Flies(snapshot, s, "");
            return Walked(snapshot, sequences, firstStep, remote, processorPartId, flown, s => flown(s) && s.DeltaV > 0);
        }

        /// <summary>
        /// The Mission Goal as legs before any Mission Plan exists (#641): one per clause of the goal (split at
        /// punctuation, "and" and "then"), read by <see cref="SequenceWords"/> as the plan's legs are. Only a craft on the ground
        /// has a leg with delta-v: the Delta-v Map's launch from its body, first, so the entries come down at the mass the launch
        /// leaves. The others carry none (no plan has budgeted them; the Craft Check's delta_v_short judges the route).
        /// </summary>
        public static List<PlannedSequence> GoalLegs(GameSnapshot snapshot, string goal)
        {
            var legs = new List<PlannedSequence>();
            if (snapshot == null) return legs;
            var grounded = snapshot.IsGrounded;
            var launch = grounded ? DeltaVMap.Compute(snapshot.Bodies, snapshot.Body).Leg("launch:" + snapshot.Body) : null;
            if (launch != null) legs.Add(new PlannedSequence { Name = "Launch from " + snapshot.Body, DeltaV = launch.DeltaV });
            foreach (var clause in GoalClause.Split(goal ?? ""))
                if (clause.Trim().Length > 0) legs.Add(new PlannedSequence { Name = clause.Trim() });
            return legs;
        }

        private static readonly Regex GoalClause = new Regex(@"[.;:,!?]|\b(?:and|then)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// The groups over the goal's <paramref name="legs"/> (<see cref="GoalLegs"/>); null when a parachute of the craft has no facts read
        /// (a cut chute, or a snapshot that read none), so the chutes aren't judged on a guess, and for a craft with no parachute at all
        /// (no_parachutes_for_return is Claude's; a powered landing at home isn't refused unasked). A goal's clauses carry no budget, so a later
        /// ascent, trip or docking in words counts as burns to come, as for the Entry Shield.
        /// </summary>
        public static ChuteGroups ForGoal(GameSnapshot snapshot, IList<PlannedSequence> legs, bool remote, uint? processorPartId)
        {
            if (snapshot == null) return null;
            var chutes = snapshot.Craft.Parts.Where(p => p.Tags.Contains("parachute")).ToList();
            if (chutes.Count == 0 || chutes.Any(p => p.Chute == null)) return null;
            return Walked(snapshot, legs, 0, remote, processorPartId, s => true, Missions.Entries.BurnsInWords);
        }

        /// <summary>
        /// The Entries in order of the sequences the craft flies (<paramref name="flown"/>), each losing what its own drop takes and then taking its group
        /// (<paramref name="burns"/>: a later leg that stops the final Entry being the Last Entry). A projection: each counts as flown from where it is, so the
        /// block's own doubt about a reentry the words don't place (<see cref="Entries.ShieldNumber"/>) doesn't apply until it is flown.
        /// </summary>
        private static ChuteGroups Walked(GameSnapshot snapshot, IList<PlannedSequence> sequences, int firstStep, bool remote, uint? processorPartId, Func<PlannedSequence, bool> flown, Func<PlannedSequence, bool> burns)
        {
            var groups = new ChuteGroups();
            if (snapshot == null || sequences == null) return groups;
            var first = Math.Max(0, firstStep);
            groups.Chutes.AddRange(Usable(snapshot, remote, processorPartId));
            var found = Missions.Entries.Find(snapshot, sequences, first, flown);
            if (found.Count == 0) return groups;
            var tail = Missions.Entries.BurnsAfter(sequences, found[found.Count - 1].Step, burns) ? 1 : 0;
            var keeper = ChildVessels.Keeper(snapshot, remote, processorPartId);
            var leaving = new HashSet<uint>(ChildVessels.Staging(snapshot, remote, processorPartId).Separations.SelectMany(s => s.PartIds));
            var podMassKg = snapshot.Craft.Parts.Where(p => !leaving.Contains(p.Id)).Sum(p => p.MassT) * 1000;
            var aboard = groups.Chutes.ToList();
            var engines = new HashSet<uint>(snapshot.Craft.Parts.Where(p => p.Tags.Contains("engine")).Select(p => p.Id));
            for (var i = 0; i < found.Count; i++)
            {
                var entry = new EntryChutes { Step = found[i].Step, Sequence = found[i].Sequence, Body = found[i].Body, Return = found[i].Return, Last = i == found.Count - 1 && tail == 0 };
                var dropped = new HashSet<uint>(EntryStack.Of(snapshot.Craft.Parts, keeper, found.Count - i + tail).Gone.Select(p => p.Id));
                var before = aboard.ToList();
                aboard.RemoveAll(c => dropped.Contains(c.PartId));
                entry.Chutes.AddRange(Choose(aboard, entry.Last));
                aboard.RemoveAll(entry.Chutes.Contains);
                entry.MassKg = entry.Return ? (podMassKg > 0 ? podMassKg : snapshot.Craft.MassTonnes * 1000)
                    : MassAfter(snapshot, sequences.Skip(first).Take(entry.Step - first).Sum(s => Math.Max(0, s.DeltaV)));
                Judge(snapshot, entry);
                // An entry that needs them keeps its stack, so it flies with that stack's chutes and engines, and so do the Entries after (ADR 0020).
                entry.EngineFinish = entry.TouchdownSpeed > EntryFinish.ThresholdFor(entry.Body) && engines.Count > 0;
                if (entry.EngineFinish && !entry.Return && before.Count != aboard.Count + entry.Chutes.Count)
                {
                    aboard = before;
                    entry.Chutes.Clear();
                    entry.Chutes.AddRange(Choose(aboard, entry.Last));
                    aboard.RemoveAll(entry.Chutes.Contains);
                    Judge(snapshot, entry);
                }
                else if (!entry.EngineFinish) engines.ExceptWith(dropped);
                groups.Entries.Add(entry);
            }
            var home = groups.Return;
            groups.ReturnUncovered = home != null && home.PoweredFinish;
            return groups;
        }

        /// <summary>
        /// Sets the entry's touchdown speed by the reentry predictor, and whether it is a powered finish: a fall from a low
        /// orbit of its body through the body's air (the Game Snapshot's rows) to sea level, opening its chutes by reentry's rules.
        /// </summary>
        private static void Judge(GameSnapshot snapshot, EntryChutes entry)
        {
            var body = SequenceWords.Body(snapshot, entry.Body);
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
            var chutes = entry.Chutes.Select(c => Copy(snapshot.Craft.Parts.First(p => p.Id == c.PartId).Chute)).ToList();
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
        private static double MassAfter(GameSnapshot snapshot, double deltaV)
        {
            var stages = snapshot.Stages.Where(s => s.DeltaVVac > 0 && s.StartMassT > s.EndMassT && s.EndMassT > 0).OrderByDescending(s => s.Number).ToList();
            var mass = snapshot.Craft.MassTonnes;
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

        /// <summary>A body's air for the predictor from its rows in the Game Snapshot (<see cref="BodyAir"/>, as plan_ascent reads it).</summary>
        internal sealed class SnapshotAir : IReentryAir
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

        /// <summary>The speed at the ground of a craft of this mass with no chutes, m/s, from a low entry on <paramref name="body"/>, by the predictor, for the reserve (#790); 0 when it can't say.</summary>
        internal static double TouchdownWithoutChutes(SnapshotBody body, double massKg)
        {
            if (body.Gm <= 0 || body.Radius <= 0 || body.AtmosphereHeight <= 0 || massKg <= 0) return 0;
            var top = body.Radius + body.AtmosphereHeight;
            var apo = top + Math.Max(5000, 0.1 * body.AtmosphereHeight);
            var peri = body.Radius + 0.3 * body.AtmosphereHeight;
            var orbit = Conic.FromElements(body.Gm, (apo + peri) / 2, (apo - peri) / (apo + peri), 0, 0, 0, Math.PI, 0);
            var frame = new BodyFrame { LonOffset = 0, Handed = 1, Ut = 0, RotationPeriod = body.RotationPeriod };
            var prediction = ReentryPredictor.Predict(body, orbit, 0, EndAltitudeOf(body), massKg, new SnapshotAir(body), new FallingCraft(massKg, new ReentryChute[0]), frame);
            return prediction.Error == null ? prediction.LandingSpeed : 0;
        }

        /// <summary>The entry's craft for the predictor: its mass, <see cref="BodyDragArea"/>, no lift, its chutes, the heat shield kept.</summary>
        internal sealed class FallingCraft : IReentryCraft
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
