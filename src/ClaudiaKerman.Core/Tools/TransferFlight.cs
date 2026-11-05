// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Missions;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>What <c>transfer_to</c> tells <see cref="TransferFlight.Next"/>: its options, its start, and how far it has got.</summary>
    internal sealed class TransferFlightAsk
    {
        public string Target;
        public double TargetPe;
        /// <summary>The window's UT, or null for "next".</summary>
        public double? Window;
        public bool Correction = true;
        public double PeTolerance = 1000;
        public double StartUt;
        /// <summary>The block's max_duration, s, or null while it is "auto" and no plan has set it.</summary>
        public double? MaxDuration;
        /// <summary>Whether max_duration was "auto" when the block began: the plan sets it, and a correction after a miss moves it.</summary>
        public bool Auto = true;
        /// <summary>TURNTIME's turn_time, s.</summary>
        public double TurnTime;
        /// <summary>The craft's delta-v, m/s (stock's; 0 or less for unknown).</summary>
        public double DvLeft;
        /// <summary>Where the flight is: <see cref="TransferFlight.Start"/> first, then the phase the last step named.</summary>
        public string Phase = TransferFlight.Start;
        /// <summary>The plan's arrival (UT) and a broken-plane plan's plane change (UT; -1 for none), carried from the start.</summary>
        public double ArrivalUt = -1;
        public double PlaneUt = -1;
        /// <summary>Why the finer correction was not flown or failed, carried to its check; "" for none.</summary>
        public string Why = "";
        /// <summary>m/s the last burn used.</summary>
        public double LastDv;
        /// <summary>Whether the target shares the parent of the body the craft began round (carried from the start).</summary>
        public bool Sibling;
        /// <summary>Whether a lost encounter has had its recovery (#747): a second loss fails the block.</summary>
        public bool Recovered;
        /// <summary>Whether a lost encounter has had its restore (#770): a second loss goes on to the recovery.</summary>
        public bool Restored;
        /// <summary>The Encounter Watch ended the last wait early (#770): the flight goes to its restore, whatever phase the wait named.</summary>
        public bool WatchDropped;
        /// <summary>How many times the Encounter Watch has dropped the warp this flight; from <see cref="TransferFlight.MaxDrops"/> a coast goes unwatched.</summary>
        public int Drops;
        /// <summary>The UT a far target's halfway correction burns at once its watched coast is over; -1 before it is planned (#770).</summary>
        public double MidUt = -1;
        /// <summary>The flight may stop at a Coast Checkpoint (#775): a Mission's run. Off for a block test or a bare call.</summary>
        public bool Checkpoints;
        /// <summary>Where a checkpoint left this flight: the first step resumes there, with no ejection; null otherwise.</summary>
        public CoastToken Resume;
    }

    /// <summary>The next thing <c>transfer_to</c> does: fly a node, wait until a UT, or end; or why it fails.</summary>
    internal sealed class TransferFlightStep
    {
        public const string Burn = "burn";
        public const string Wait = "wait";
        public const string Done = "done";

        /// <summary>"" or why the block fails, after <see cref="Report"/> is folded into its result.</summary>
        public string Error = "";
        public string Do = Done;
        /// <summary>A burn's kind: "ejection", "correction" or "plane" (its m/s go to correction_dv or plane_dv).</summary>
        public string Kind = "";
        /// <summary>The node's UT and components (m/s), or the UT to wait until.</summary>
        public double Ut;
        public double Prograde;
        public double Normal;
        public double Radial;
        /// <summary>exec_node's max_residual, m/s; -1 for its default.</summary>
        public double MaxResidual = -1;
        /// <summary>A burn that fails ends the block with this before exec_node's reason, unless <see cref="KeepGoing"/>.</summary>
        public string FailPrefix = "";
        /// <summary>A failed burn's reason is carried as <see cref="TransferFlightAsk.Why"/> to the next phase instead of ending the block.</summary>
        public bool KeepGoing;
        /// <summary>The phase to ask next.</summary>
        public string Phase = "";
        /// <summary>The block's max_duration from here, s, when the plan set it; null to leave it.</summary>
        public double? MaxDuration;
        public double ArrivalUt = -1;
        public double PlaneUt = -1;
        public string Why = "";
        public bool Sibling;
        public bool Recovered;
        public bool Restored;
        public int Drops;
        public double MidUt = -1;
        /// <summary>For return's coast ask (#775): the UT its wait runs to.</summary>
        public double WaitUt;
        /// <summary>A wait's Encounter Watch (#770); null for a wait with nothing to watch.</summary>
        public EncounterWatchPlan Watch;
        /// <summary>Script Result keys and values to set.</summary>
        public readonly Dictionary<string, object> Report = new Dictionary<string, object>();
    }

    /// <summary>
    /// <c>transfer_to</c>'s flight (#459, ADR 0014): which node to fly, where to wait and whether the arrival is met, one step at a
    /// time, so the block only flies nodes with exec_node, warps and reports. To a moon of the body the craft orbits, the flight
    /// it had in Kerboscript: the ejection (<see cref="Transfer"/>), a broken-plane plan's plane change or the correction that
    /// makes a missed encounter, the Course Correction halfway, then a finer one within pe_tolerance (#672). To a body sharing
    /// its parent (a planet, or a moon of the same planet), the window re-solved round the planned one and the ejection from
    /// the parking orbit (<see cref="SiblingEjection"/>), then, once out of the craft's sphere of influence, the Course
    /// Correction on the parent's patch and the same finer one. Every check reads the Game Snapshot's patched conics.
    /// To a far target (a body sharing the parent, or a planet; #747) the far-out corrections only keep or restore the
    /// encounter, not held to pe_tolerance, and the trim to target_pe waits for the target's sphere of influence (<see cref="Near"/>).
    /// A long coast to a far target is watched (<see cref="EncounterWatch"/>, #770). A lost encounter gets a restore on this window,
    /// then the missed-ejection recovery once inside max_duration or, in "auto", <see cref="RecoveryWait"/>
    /// (<see cref="Recover"/>), before the block fails with the figures. In a Mission's run a coast of over 6 h to a correction may stop
    /// at a Coast Checkpoint (<see cref="CoastCheckpoints"/>, #775), and the resumed flight starts where it stopped.
    /// </summary>
    internal static class TransferFlight
    {
        public const string Start = "start";
        private const string Ejected = "ejected";
        private const string Planed = "planed";
        private const string Made = "made";
        private const string Mid = "mid";
        private const string MidCheck = "mid_check";
        private const string Fine = "fine";
        /// <summary>Inside a far target's sphere of influence, before the trim (#747).</summary>
        private const string Near = "near";
        private const string FineFlown = "fine_flown";
        private const string FineCheck = "fine_check";
        private const string Escaping = "escaping";
        private const string Escaped = "escaped";
        /// <summary>The next step after the Encounter Watch dropped the warp (#770), and the check after a restore's burn.</summary>
        private const string Restoring = "restore";
        private const string RestoreCheck = "restore_check";
        /// <summary>The first step of a flight resumed from a Coast Checkpoint (#775).</summary>
        private const string Resuming = "resuming";
        /// <summary>m/s under which a Course Correction is not flown; the finer one's max_residual (#672); a burn's lead, s, past its turn.</summary>
        private const double SkipDv = 0.1;
        private const double FineResidual = 0.1;
        private const double Lead = 60;
        /// <summary>s past the end of max_duration a Course Correction must leave to burn in; s past the arrival "auto" leaves.</summary>
        private const double BurnRoom = 120;
        private const double AutoSlack = 900;
        /// <summary>The same slack for a resumed plan's expected duration (#775).</summary>
        internal const double AutoSlackS = AutoSlack;
        /// <summary>s past leaving the sphere of influence the wait ends, as return waits; and past entering a far target's (#747).</summary>
        private const double PastSoi = 30;
        /// <summary>The most patches to the target: out of a moon, round the planet, into another moon.</summary>
        private const int Patches = 4;
        /// <summary>The steps one call may take before it gives up on a loop: a step that flies nothing goes straight on.</summary>
        private const int MaxPhases = 12;
        /// <summary>From this tilt, degrees, between the craft's arc and the target's orbit, a missed ejection is put down to the plane (#732).</summary>
        private const double ResidualDeg = 1;
        /// <summary>
        /// How far past the sphere of influence's edge the post-escape correction waits, as a share of its radius (#770): 5 %, 4.2 Mm
        /// at Kerbin. Mission m20261105-174251-8f20 was put back inside KSP's own edge from 190 s past Core's, about 0.5 % of the radius off.
        /// </summary>
        internal const double ClearOfSoi = 0.05;
        /// <summary>
        /// In "auto", the longest wait, s, for a far target's next-window recovery: 30 Kerbin days (#770). Past it the block fails with the figures
        /// so Claudia replans with the player, never a burn years out.
        /// </summary>
        internal const double RecoveryWait = 30 * 21600;
        /// <summary>The Encounter Watch's drops one flight may have (#770): after them its coasts go unwatched, and the arrival's check decides.</summary>
        internal const int MaxDrops = 3;

        public static TransferFlightStep Next(GameSnapshot snapshot, TransferFlightAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            if (ask.WatchDropped)
            {
                ask.Phase = Restoring;
                ask.MidUt = -1;
                ask.WatchDropped = false;
                ask.Drops++;
            }
            if (ask.Resume != null) ask.Phase = Resuming;
            double? movedMax = null;
            for (var i = 0; i < MaxPhases; i++)
            {
                ToolOutcome error;
                var setup = PredictBurn.Setup.Prepare(snapshot, ask.Target, out error);
                if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
                var step = Phase(snapshot, setup, ask);
                step.Sibling |= ask.Sibling;
                step.Recovered |= ask.Recovered;
                step.Restored |= ask.Restored;
                step.Drops = ask.Drops;
                if (step.MaxDuration.HasValue) movedMax = step.MaxDuration;
                else step.MaxDuration = movedMax;
                if (step.Error != "" || step.Do != TransferFlightStep.Done || step.Phase == "") return step;
                // A step that flies nothing goes on to the phase it named, with what it carries.
                Carry(ask, step);
            }
            return Fail("the flight's steps did not settle");
        }

        /// <summary>What a step carries into the ask for the next one.</summary>
        internal static void Carry(TransferFlightAsk ask, TransferFlightStep step)
        {
            ask.Phase = step.Phase;
            if (step.MaxDuration.HasValue) ask.MaxDuration = step.MaxDuration;
            ask.ArrivalUt = step.ArrivalUt;
            ask.PlaneUt = step.PlaneUt;
            ask.Why = step.Why;
            ask.Sibling = step.Sibling;
            ask.Recovered = step.Recovered;
            ask.Restored = step.Restored;
            ask.Drops = step.Drops;
            ask.MidUt = step.MidUt;
        }

        private static TransferFlightStep Phase(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var target = setup.Target;
            switch (ask.Phase)
            {
                case Start:
                    return Begin(snapshot, setup, ask);
                case Ejected:
                    return AfterEjection(snapshot, setup, ask);
                case Planed:
                    return Encounter(setup) == null ? Fail("no encounter with " + target.Name + " after the plane change") : Go(ask, Fine);
                case Made:
                    return Encounter(setup) == null ? Recover(snapshot, setup, ask, Lost(target)) : Go(ask, Mid);
                case Mid:
                    return MidCorrection(snapshot, setup, ask);
                case MidCheck:
                    return Encounter(setup) == null ? Recover(snapshot, setup, ask, Lost(target)) : Go(ask, Fine);
                case Fine:
                    return FineCorrection(snapshot, setup, ask);
                case Near:
                    return NearCorrection(snapshot, setup, ask);
                case FineFlown:
                    // A finer correction that burned nothing is no correction.
                    return Go(ask, FineCheck, ask.Why == "" && ask.LastDv == 0 ? "its burn, under 0.1 m/s, isn't flown" : ask.Why);
                case FineCheck:
                    return ask.Correction && Encounter(setup) == null
                        ? Recover(snapshot, setup, ask, Lost(target) + (string.IsNullOrEmpty(ask.Why) ? "" : ": " + ask.Why))
                        : Arrive(snapshot, setup, ask);
                case Escaping:
                    return LeaveHome(setup, ask);
                case Escaped:
                    return OnParent(snapshot, setup, ask);
                case Restoring:
                    return Restore(snapshot, setup, ask);
                case RestoreCheck:
                    return AfterRestore(snapshot, setup, ask);
                case Resuming:
                    return Resume(snapshot, setup, ask);
                default:
                    return Fail("no transfer_to phase named " + ask.Phase);
            }
        }

        /// <summary>
        /// Back from a Coast Checkpoint (#775): no ejection, the flight's carried state from the token, and the watched coast to the
        /// correction it stopped for, or straight to it when the time has passed. An encounter gone since the mod's check: the recovery.
        /// </summary>
        private static TransferFlightStep Resume(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var token = ask.Resume;
            ask.Resume = null;
            ask.ArrivalUt = token.ArrivalUt;
            ask.PlaneUt = token.PlaneUt;
            ask.Sibling = token.Sibling;
            ask.Recovered = token.Recovered;
            ask.Restored = token.Restored;
            ask.Drops = token.Drops;
            ask.MidUt = token.MidUt;
            var enc = Encounter(setup);
            if (enc == null) return Recover(snapshot, setup, ask, Lost(setup.Target));
            var step = token.WaitUt > setup.Now
                ? new TransferFlightStep { Do = TransferFlightStep.Wait, Ut = token.WaitUt, Phase = token.Phase, MidUt = token.MidUt, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt, Watch = Watch(setup, enc, ask, token.PointUt, token.WaitUt, token.Phase, token.MidUt) }
                : new TransferFlightStep { Phase = token.Phase, MidUt = token.MidUt, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt };
            if (ask.Auto) step.MaxDuration = enc.PeUt - ask.StartUt + AutoSlack;
            // The wait that reported it was the run before this one's; a script after the block reads it from this result.
            step.Report["encounter_ut"] = Math.Round(token.SoiUt >= 0 ? token.SoiUt : enc.SoiUt);
            return step;
        }

        /// <summary>
        /// The block ends Nominal here (#775) when the correction at <paramref name="pointUt"/> is over <see cref="WindowWaits.LongWaitS"/> off and
        /// the safety test passes; null when it keeps flying. The token holds all a resumed flight needs.
        /// </summary>
        private static TransferFlightStep Checkpoint(PredictBurn.Setup setup, Arrival enc, TransferFlightAsk ask, double pointUt, double waitUt, string phase, double midUt)
        {
            if (pointUt - setup.Now <= WindowWaits.LongWaitS) return null;
            var token = Token(setup, enc, ask, pointUt, waitUt, phase, midUt);
            if (token == null || CoastCheckpoints.Unsafe(setup, enc, pointUt) != "") return null;
            var step = new TransferFlightStep();
            step.Report[CoastCheckpoints.TokenKey] = token.ToString();
            step.Report[CoastCheckpoints.PointKey] = Math.Round(pointUt);
            step.Report["encounter_ut"] = Math.Round(enc.SoiUt);
            step.Report["target_pe_actual"] = Math.Round(enc.Pe);
            step.Report["arrival_ut"] = Math.Round(enc.PeUt);
            step.ArrivalUt = ask.ArrivalUt;
            step.PlaneUt = ask.PlaneUt;
            return step;
        }

        /// <summary>The token of a coast on <paramref name="enc"/>; null when the flight may not checkpoint.</summary>
        private static CoastToken Token(PredictBurn.Setup setup, Arrival enc, TransferFlightAsk ask, double pointUt, double waitUt, string phase, double midUt)
        {
            if (!ask.Checkpoints || !string.IsNullOrEmpty(ask.Why)) return null;
            var bodies = string.Join(",", enc.Bodies.ToArray());
            if (!CoastToken.Plain(bodies) || !CoastToken.Plain(setup.Target.Name)) return null;
            return new CoastToken
            {
                Target = setup.Target.Name, Pe = enc.Pe, PeLimit = CoastCheckpoints.PeLimit(setup.Target, ask.PeTolerance), PointUt = pointUt, LeadS = ask.TurnTime + BurnPoints.TurnMarginSeconds,
                WaitUt = waitUt, Phase = phase, ArriveUt = enc.PeUt, SoiUt = enc.SoiUt, MidUt = midUt, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt, Sibling = ask.Sibling,
                Recovered = ask.Recovered, Restored = ask.Restored, Drops = ask.Drops, Bodies = bodies,
            };
        }

        /// <summary>The ejection: to a moon, <see cref="Transfer"/>'s; to a body sharing the craft's parent, <see cref="SiblingEjection"/>'s.</summary>
        private static TransferFlightStep Begin(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var home = setup.Body;
            var target = setup.Target;
            var sibling = TransferWindows.IsSiblingTransfer(snapshot, target.Name);
            if (!sibling && !string.Equals(target.Parent, home.Name, StringComparison.OrdinalIgnoreCase))
                return Fail(target.Name + " does not orbit " + home.Name + ", the body the craft orbits" + (string.IsNullOrEmpty(home.Parent) ? "" : ", or " + home.Parent));
            var earliest = Earliest(ask.StartUt, ask.TurnTime, ask.Window);
            TransferFlightStep step;
            double arrival;
            if (sibling)
            {
                var found = SiblingEjection.Find(snapshot, setup, target.Name, ask.TargetPe, earliest, ask.Window, ArrivalRule.Capture(snapshot, target.Name, null));
                if (found.Error != null) return Fail(found.Error);
                step = Node("ejection", found.Ut, found.Prograde, found.Normal, found.Radial, Escaping);
                step.Sibling = true;
                arrival = found.ArrivalUt;
                step.Report["v_inf"] = Math.Round(found.VInf, 1);
            }
            else
            {
                var plan = Transfer.Find(snapshot, new TransferAsk { Target = target.Name, TargetPe = ask.TargetPe, EarliestUt = earliest, WindowUt = ask.Window, Capture = ArrivalRule.Capture(snapshot, target.Name, null) });
                if (plan.Error != null) return Fail(plan.Error);
                step = Node("ejection", plan.Ut, plan.Prograde, plan.Normal, plan.Radial, Ejected);
                arrival = plan.ArrivalUt;
                step.PlaneUt = plan.PlaneUt ?? -1;
            }
            step.ArrivalUt = arrival;
            step.Report["ejection_wait"] = Math.Round(step.Ut - ask.StartUt);
            step.Report["departure_ut"] = Math.Round(step.Ut);
            if (ask.Auto) step.MaxDuration = arrival - ask.StartUt + AutoSlack;
            return step;
        }

        /// <summary>
        /// After a moon's ejection: a broken-plane plan's plane change at its planned time; else, with no encounter, the correction
        /// that makes one (#363); else on to the Course Correction.
        /// </summary>
        private static TransferFlightStep AfterEjection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var target = setup.Target;
            var now = setup.Now;
            if (ask.PlaneUt >= 0)
            {
                string late;
                var ut = CorrectionUt(ask.PlaneUt, now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out late);
                if (late != "") return Fail(late);
                var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, AimUt = ask.ArrivalUt });
                return FlyCorrection(ask, fix, "plane", Planed);
            }
            if (Encounter(setup) != null) return Go(ask, Mid);

            // A residual plane (#727, #732): a craft launched due east from past the target's plane ejects in its own plane and
            // misses by the target's height out of it. The fix is mid-course, where the arc is slow, aimed through the planned
            // arrival (tens of m/s; in the parking orbit it would be hundreds), then the finer correction as after a plane change.
            var fixedMid = ResidualPlane(snapshot, setup, ask);
            if (fixedMid != null) return fixedMid;

            return Missed(snapshot, setup, ask, "no encounter with " + target.Name + " after the ejection burn");
        }

        /// <summary>"no encounter with &lt;target&gt; after the correction": the encounter a correction lost.</summary>
        private static string Lost(SnapshotBody target)
        {
            return "no encounter with " + target.Name + " after the correction";
        }

        /// <summary>
        /// A lost encounter (#747, #770): first, once, a correction that restores this window's encounter; then, once, the
        /// missed-ejection recovery (<see cref="Missed"/>) from the orbit the craft is on, inside max_duration or, in "auto",
        /// <see cref="RecoveryWait"/>; each reported as <c>recovered</c> (why the encounter was lost). After both the block fails;
        /// with neither to fly it fails with the figures.
        /// </summary>
        private static TransferFlightStep Recover(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask, string lost)
        {
            if (ask.Recovered)
                return Fail(lost + Figures(snapshot, CorrectionAsk(setup.Target.Name, ask.TargetPe, setup.Now, ask.TurnTime, ask.StartUt, ask.MaxDuration ?? 0, ask.Auto, ask.DvLeft, ArrivalRule.Capture(snapshot, setup.Target.Name, null)), setup.Now, ""));
            var restoreCost = "";
            if (!ask.Restored)
            {
                var burn = RestoreBurn(snapshot, setup, ask, lost, out restoreCost);
                if (burn != null) return burn;
            }
            var step = Missed(snapshot, setup, ask, lost, restoreCost);
            if (step.Error != "") return step;
            step.Recovered = true;
            step.Report["recovered"] = lost;
            return step;
        }

        /// <summary>
        /// The burn that restores this window's encounter (#770): as soon as the craft can turn to it, the encounter's time free
        /// (aimed through the planned arrival only to start the solve), within dv_left; null with <paramref name="cost"/> saying why not.
        /// </summary>
        private static TransferFlightStep RestoreBurn(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask, string lost, out string cost)
        {
            var lead = ask.TurnTime + Lead;
            string late;
            var ut = CorrectionUt(setup.Now + lead, setup.Now, lead, ask.StartUt, ask.MaxDuration, out late);
            if (late != "")
            {
                cost = "no time for a restore";
                return null;
            }
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, AimUt = ask.ArrivalUt > ut ? ask.ArrivalUt : (double?)null,
            });
            if (!fix.HasBurn || fix.Error != null)
            {
                cost = "no restore found";
                return null;
            }
            if (OverDvLeft(ask, fix))
            {
                cost = "a restore costs " + Num(fix.DeltaV) + " m/s of " + Num(ask.DvLeft) + " m/s left";
                return null;
            }
            cost = "";
            var step = Node("correction", fix.Ut, fix.Prograde, fix.Normal, fix.Radial, RestoreCheck);
            step.FailPrefix = lost + ", and the correction failed: ";
            step.ArrivalUt = ask.ArrivalUt;
            step.PlaneUt = ask.PlaneUt;
            step.Restored = true;
            step.Report["recovered"] = lost;
            return step;
        }

        /// <summary>
        /// After a restore's burn: the encounter held, the flight goes on, "auto" moving max_duration to the arrival now planned;
        /// not held, on to the recovery.
        /// </summary>
        private static TransferFlightStep AfterRestore(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var enc = Encounter(setup);
            if (enc == null) return Recover(snapshot, setup, ask, Lost(setup.Target));
            var step = Go(ask, Mid);
            if (ask.Auto) step.MaxDuration = enc.PeUt - ask.StartUt + AutoSlack;
            return step;
        }

        /// <summary>
        /// The Encounter Watch dropped the warp (#770). Back round the body the craft left, as in the Mission that found this: the
        /// way out again first, clear of the edge, and the correction on the parent's patch takes up what that cost. An encounter
        /// that drifted but holds: a burn back to target_pe, or, none found, straight on, the trim in the target's sphere of
        /// influence being still ahead. An encounter gone: <see cref="Recover"/>.
        /// </summary>
        private static TransferFlightStep Restore(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var target = setup.Target;
            if (BackRoundHome(setup, ask)) return Go(ask, Escaping);
            var enc = Encounter(setup);
            if (enc == null) return Recover(snapshot, setup, ask, Lost(target));
            var lead = ask.TurnTime + Lead;
            string late;
            var ut = CorrectionUt(setup.Now + lead, setup.Now, lead, ask.StartUt, ask.MaxDuration, out late);
            if (late != "") return Go(ask, Mid);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut });
            if (!fix.HasBurn || fix.Error != null || OverDvLeft(ask, fix)) return Go(ask, Mid);
            return FlyCorrection(ask, fix, "correction", RestoreCheck);
        }

        /// <summary>Whether a sibling flight has the craft round neither the target nor their parent: KSP has it back round the body it left (#770).</summary>
        private static bool BackRoundHome(PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            return ask.Sibling && setup.Body != setup.Target && !string.Equals(setup.Body.Name, setup.Target.Parent, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a correction costs more than the craft's dv_left (0 or less for unknown).</summary>
        private static bool OverDvLeft(TransferFlightAsk ask, CorrectionPlan fix)
        {
            return ask.DvLeft > 0 && fix.DeltaV > ask.DvLeft;
        }

        /// <summary>
        /// No encounter (<paramref name="missed"/> says after what, #363): the cheapest burn from the orbit the craft is on that
        /// meets the target, found as the ejection was, within dv_left and max_duration or, in "auto", <see cref="RecoveryWait"/>
        /// (#770); or why none fits, with what the next window would cost and <paramref name="restoreCost"/> when a restore was tried.
        /// </summary>
        private static TransferFlightStep Missed(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask, string missed, string restoreCost = "")
        {
            var target = setup.Target;
            var now = setup.Now;
            var correctionAsk = CorrectionAsk(target.Name, ask.TargetPe, now, ask.TurnTime, ask.StartUt, ask.MaxDuration ?? 0, ask.Auto, ask.DvLeft, ArrivalRule.Capture(snapshot, target.Name, null));
            if (correctionAsk.LatestUt.HasValue && correctionAsk.LatestUt.Value < correctionAsk.EarliestUt)
                return Fail(missed + ", and no correction fits: max_duration (" + Num(ask.MaxDuration ?? 0) + " s) leaves no time for it and the Course Correction after it" + Figures(snapshot, correctionAsk, now, restoreCost));
            // A moon's next window is days off, and a limit changes which departures Transfer picks: only a far target's is held to it.
            if (ask.Auto && Far(setup, ask)) correctionAsk.LatestUt = correctionAsk.EarliestUt + RecoveryWait;
            var found = Transfer.Find(snapshot, correctionAsk);
            if (found.Error != null) return Fail(missed + ", and no correction fits: " + found.Error.Replace("found no transfer: ", "") + Figures(snapshot, correctionAsk, now, restoreCost));
            var step = Node("correction", found.Ut, found.Prograde, found.Normal, found.Radial, Made);
            step.FailPrefix = missed + ", and the correction failed: ";
            if (ask.Auto) step.MaxDuration = found.ArrivalUt - ask.StartUt + AutoSlack;
            step.ArrivalUt = ask.ArrivalUt;
            return step;
        }

        /// <summary>
        /// What a failed recovery tells Claudia to replan with (#770): the restore's cost and the next window's cost and wait,
        /// whatever the block's limits; "" when there is neither.
        /// </summary>
        private static string Figures(GameSnapshot snapshot, TransferAsk limited, double now, string restoreCost)
        {
            var figures = restoreCost != "" ? "; " + restoreCost : "";
            var next = Transfer.Find(snapshot, new TransferAsk { Target = limited.Target, TargetPe = limited.TargetPe, EarliestUt = limited.EarliestUt, BrokenPlane = false, Capture = limited.Capture });
            if (next.Error != null) return figures;
            return figures + "; the next window costs " + Num(next.DeltaV) + " m/s at UT " + Num(next.Ut) + ", " + Num(next.Ut - now) + " s from now";
        }

        /// <summary>
        /// The mid-course plane fix for a craft whose arc is tilted <see cref="ResidualDeg"/> or more from the target's orbit and
        /// misses it (#732): halfway to the planned arrival (sooner if max_duration ends first), the burn whose Lambert arc meets
        /// the target then, sized to target_pe; null when there is no planned arrival, no tilt, no time or no such burn the craft
        /// can pay for, and the correction after a missed ejection is tried instead.
        /// </summary>
        private static TransferFlightStep ResidualPlane(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var now = setup.Now;
            if (!(ask.ArrivalUt > now)) return null;
            var tilt = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(setup.Start.W, setup.System.OrbitOf(setup.Target).W)))) * 180 / Math.PI;
            if (tilt < ResidualDeg) return null;
            string late;
            var ut = CorrectionUt((now + ask.ArrivalUt) / 2, now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out late);
            if (late != "") return null;
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, AimUt = ask.ArrivalUt });
            if (!fix.HasBurn || fix.Error != null || ask.DvLeft > 0 && fix.DeltaV > ask.DvLeft) return null;
            return FlyCorrection(ask, fix, "plane", Planed);
        }

        /// <summary>
        /// What the correction after a missed ejection asks <see cref="Transfer"/>: the soonest burn the craft can turn to (turn_time
        /// plus a minute for the lead), one the craft's dv_left (m/s; 0 or less for unknown) pays for, and with max_duration given
        /// (not auto) one that leaves the Course Correction after it its lead and 120 s. One burn: no broken-plane plan, whose plane
        /// change this path would not fly. Pure (transfer_to_correction_ask before #459).
        /// </summary>
        internal static TransferAsk CorrectionAsk(string target, double targetPe, double now, double turnTime, double startUt, double maxDuration, bool auto, double dvLeft, bool capture = true)
        {
            var ask = new TransferAsk { Target = target, TargetPe = targetPe, EarliestUt = now + turnTime + Lead, BrokenPlane = false, Capture = capture };
            if (!auto) ask.LatestUt = startUt + maxDuration - BurnRoom - turnTime - Lead;
            if (dvLeft > 0) ask.MaxDv = dvLeft;
            return ask;
        }

        /// <summary>
        /// The Course Correction halfway to the encounter (sooner if max_duration ends first, #368), when the periapsis is outside
        /// pe_tolerance; to a far target it keeps the encounter, aimed at target_pe but not held to pe_tolerance (#747: far out,
        /// a flown error of 0.05 m/s moves the periapsis by tens of km), so one the solver can't bring within it is not flown.
        /// Under 0.1 m/s, which exec_node can't fly accurately, it is not flown either. No encounter: the recovery.
        /// </summary>
        private static TransferFlightStep MidCorrection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (!ask.Correction) return Go(ask, Fine);
            var enc = Encounter(setup);
            if (enc == null) return Recover(snapshot, setup, ask, Lost(setup.Target));
            if (Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, MidCheck);
            string late;
            var lead = ask.TurnTime + Lead;
            var ut = CorrectionUt(ask.MidUt >= 0 ? Math.Max(ask.MidUt, setup.Now + lead) : (setup.Now + enc.SoiUt) / 2, setup.Now, lead, ask.StartUt, ask.MaxDuration, out late);
            // A wake too late for the planned burn goes on to the trim: only the first planning of it can fail the block.
            if (late != "") return ask.MidUt >= 0 ? Go(ask, MidCheck) : Fail(late);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut });
            var far = Far(setup, ask);
            if (far && (!fix.HasBurn || fix.Error != null)) return Go(ask, MidCheck);
            if (ask.MidUt < 0 && fix.HasBurn && fix.Error == null && fix.DeltaV >= SkipDv)
            {
                var held = Checkpoint(setup, enc, ask, ut, ut - lead - Lead, Mid, ut);
                if (held != null) return held;
            }
            // exec_node's warp to a node has no stop: a far target's long coast to it is a watched wait, and the node is planned at its end (#770).
            if (far && ask.MidUt < 0 && fix.DeltaV >= SkipDv && ut > setup.Now + 2 * lead + Lead)
                return new TransferFlightStep { Do = TransferFlightStep.Wait, Ut = ut - lead - Lead, Phase = Mid, MidUt = ut, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt, Why = ask.Why, Watch = Watch(setup, enc, ask, ut, ut - lead - Lead, Mid, ut) };
            return FlyCorrection(ask, fix, "correction", MidCheck);
        }

        /// <summary>
        /// Whether the target is far (#747): a body sharing the parent of the body the craft began round, or a planet. Its
        /// periapsis is trimmed inside its sphere of influence: out on the parent's patch a burn flown 0.05 m/s off moves it by
        /// tens of km. A moon from its planet keeps the finer correction halfway.
        /// </summary>
        private static bool Far(PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (ask.Sibling) return true;
            var parent = setup.System.Parent(setup.Target);
            return parent != null && string.IsNullOrEmpty(parent.Parent);
        }

        /// <summary>
        /// After the Course Correction or the plane change, a periapsis outside pe_tolerance gets one finer correction (#672),
        /// halfway to the encounter, within pe_tolerance and a tighter max_residual. Why it was not flown or failed is carried to the check.
        /// </summary>
        private static TransferFlightStep FineCorrection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (!ask.Correction) return Arrive(snapshot, setup, ask);
            var enc = Encounter(setup);
            if (enc != null && Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, FineCheck, "");
            if (enc == null) return Go(ask, FineCheck, "");
            // To a far target the coast is long, and a burn halfway flown 0.1 m/s off misses by far more than pe_tolerance
            // (#747: 0.02 m/s moved Duna's periapsis 57 km). The trim waits for the target's sphere of influence (Near) and is
            // planned there from the orbit flown, no sooner than that error, carried to the periapsis, stays within pe_tolerance;
            // the block then ends inside it.
            var far = Far(setup, ask);
            var lead = ask.TurnTime + Lead;
            if (far && setup.Body != setup.Target)
            {
                var into = enc.SoiUt + PastSoi;
                if (into > setup.Now && (!ask.MaxDuration.HasValue || into + 2 * (lead + BurnRoom) <= ask.StartUt + ask.MaxDuration.Value))
                {
                    var held = Checkpoint(setup, enc, ask, into, into, Near, -1);
                    if (held != null) return held;
                    var wait = new TransferFlightStep { Do = TransferFlightStep.Wait, Ut = into, Phase = Near, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt, Why = ask.Why, Watch = Watch(setup, enc, ask, into, into, Near, -1) };
                    wait.Report["encounter_ut"] = Math.Round(enc.SoiUt);
                    return wait;
                }
            }
            var mid = ask.MidUt >= 0 ? Math.Max(ask.MidUt, setup.Now + lead) : (setup.Now + enc.SoiUt) / 2;
            if (far) mid = Math.Max(Math.Max(mid, enc.PeUt - ask.PeTolerance / FineResidual), setup.Now + lead);
            string why;
            var ut = CorrectionUt(mid, setup.Now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out why);
            if (why != "") return Go(ask, FineCheck, why);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, PeTolerance = ask.PeTolerance,
            });
            if (!fix.HasBurn || fix.Error != null) return Go(ask, FineCheck, fix.Error);
            if (fix.DeltaV < SkipDv) return Go(ask, FineCheck, "its burn, under 0.1 m/s, isn't flown");
            if (ask.MidUt < 0)
            {
                var stop = Checkpoint(setup, enc, ask, ut, ut - lead - Lead, Fine, ut);
                if (stop != null) return stop;
            }
            var step = Node("correction", fix.Ut, fix.Prograde, fix.Normal, fix.Radial, FineFlown);
            if (setup.Body != setup.Target && fix.Ut > enc.SoiUt) step.Report["encounter_ut"] = Math.Round(enc.SoiUt);
            step.MaxResidual = FineResidual;
            step.KeepGoing = true;
            step.ArrivalUt = ask.ArrivalUt;
            return step;
        }

        /// <summary>
        /// Just inside a far target's sphere of influence (#747): where the trim to target_pe would burn sooner than its flown
        /// error allows (<see cref="FineCorrection"/>), a coarser correction first, now, while moving the periapsis is cheap;
        /// then the trim. Not found, under 0.1 m/s or out of time: straight to the trim.
        /// </summary>
        private static TransferFlightStep NearCorrection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var enc = Encounter(setup);
            if (enc == null || setup.Body != setup.Target || Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, Fine);
            var lead = ask.TurnTime + Lead;
            string late;
            var ut = CorrectionUt(setup.Now + lead, setup.Now, lead, ask.StartUt, ask.MaxDuration, out late);
            // Worth it only when the trim's own burn is at least as far again off: a coarse burn there is no cheaper.
            if (late != "" || enc.PeUt - ask.PeTolerance / FineResidual - ut < ask.PeTolerance / FineResidual) return Go(ask, Fine);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut });
            if (!fix.HasBurn || fix.Error != null) return Go(ask, Fine);
            return FlyCorrection(ask, fix, "correction", Fine);
        }

        /// <summary>The arrival: the encounter and its periapsis reported, then a miss outside pe_tolerance (with correction) fails the block.</summary>
        private static TransferFlightStep Arrive(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var target = setup.Target;
            var enc = Encounter(setup);
            var why = ask.Why ?? "";
            if (enc == null)
                return Fail("no encounter with " + target.Name + " after the " + (ask.Correction ? "correction" : "ejection burn") + (why != "" ? ": " + why : ""));
            var step = new TransferFlightStep();
            // Inside the target's sphere of influence already (a sibling's finer correction), the entry was reported before it.
            if (setup.Body != target) step.Report["encounter_ut"] = Math.Round(enc.SoiUt);
            step.Report["target_pe_actual"] = Math.Round(enc.Pe);
            step.Report["arrival_ut"] = Math.Round(enc.PeUt);
            step.Report["v_inf"] = Math.Round(enc.VInf, 1);
            if (ask.Correction)
            {
                var miss = Miss(target.Name, enc.Pe, ask.TargetPe, ask.PeTolerance, why);
                if (miss != "") return Fail(miss, step);
            }
            // It ends before arrival: a capture straight after it would burn at this body's apsis (20ac, #610). Turn text, not docs.
            var entry = ArrivalRule.Note(snapshot, target.Name, ArrivalRule.Capture(snapshot, target.Name, null));
            if (entry != null) step.Report["arrival_note"] = entry;
            else step.Report["next"] = "coast to encounter_ut + 60 before a capture";
            return step;
        }

        /// <summary>
        /// "" or why the block fails after its corrections (#672): the periapsis <paramref name="pe"/> m is over
        /// <paramref name="tolerance"/> m from target_pe, or why the finer correction couldn't be found or flown (not "": the block
        /// fails whatever pe is). Pure (transfer_to_miss before #459).
        /// </summary>
        internal static string Miss(string target, double pe, double targetPe, double tolerance, string why)
        {
            if (why == "" && Within(pe - targetPe, tolerance)) return "";
            var miss = "after the correction, the periapsis at " + target + " is " + Km(pe) + " km, " + Km(Math.Abs(pe - targetPe)) + " km off target_pe "
                + Km(targetPe) + " km (pe_tolerance " + Km(tolerance) + " km)";
            return why != "" ? miss + ": " + why : miss;
        }

        /// <summary>To a body sharing the parent: the wait out of the craft's sphere of influence before the correction on the parent's patch.</summary>
        private static TransferFlightStep LeaveHome(PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            var home = setup.Body;
            if (string.Equals(home.Name, setup.Target.Parent, StringComparison.OrdinalIgnoreCase)) return Go(ask, Escaped);
            var path = SingleShooting.Follow(setup.System, home, setup.Start, setup.Now, 2);
            var first = path != null && path.Count > 0 ? path[0] : null;
            if (first == null || first.End != PatchEnd.Escape || first.EndUt <= setup.Now)
                return Fail("the ejection burn does not leave " + home.Name + "'s sphere of influence"
                    + (first != null && first.End == PatchEnd.Encounter ? ": it meets " + first.Next.Name + " first" : ""));
            var step = new TransferFlightStep { Do = TransferFlightStep.Wait, Ut = ClearUt(setup, ask, home, first, path.Count > 1 ? path[1] : null), Phase = Escaped, ArrivalUt = ask.ArrivalUt };
            return step;
        }

        /// <summary>
        /// When the craft is clear of <paramref name="home"/>'s edge (#770): <see cref="ClearOfSoi"/> past it on the parent's patch, so
        /// KSP's own edge, which a burn or the next warp's packing can find the craft inside, is well behind. Never sooner than
        /// <see cref="PastSoi"/> past the edge, and no later than halfway to the planned arrival or than leaves max_duration
        /// room for the correction and the one after it.
        /// </summary>
        private static double ClearUt(PredictBurn.Setup setup, TransferFlightAsk ask, SnapshotBody home, Patch first, Patch onParent)
        {
            var soonest = first.EndUt + PastSoi;
            if (onParent == null || !(home.SoiRadius > 0)) return soonest;
            var latest = double.PositiveInfinity;
            if (ask.ArrivalUt > soonest) latest = (soonest + ask.ArrivalUt) / 2;
            if (ask.MaxDuration.HasValue) latest = Math.Min(latest, ask.StartUt + ask.MaxDuration.Value - BurnRoom - 2 * (ask.TurnTime + Lead));
            var homeOrbit = setup.System.OrbitOf(home);
            var edge = home.SoiRadius * (1 + ClearOfSoi);
            Func<double, double> gap = ut => (onParent.Orbit.Position(ut) - homeOrbit.Position(ut)).Length;
            var before = soonest;
            var after = soonest;
            for (var dt = 60.0; gap(after) < edge; dt *= 2)
            {
                before = after;
                after += dt;
                if (after >= latest) return Math.Max(soonest, latest);
            }
            for (var i = 0; i < 30 && after - before > 1; i++)
            {
                var mid = (before + after) / 2;
                if (gap(mid) < edge) before = mid;
                else after = mid;
            }
            return after;
        }

        /// <summary>
        /// On the parent's patch: the Course Correction as soon as the craft can turn to it, aimed through the planned arrival when
        /// the ejection left no encounter; then the halfway one, and the finer one late enough that its residual stays within
        /// pe_tolerance. Planned from the orbit the ejection left, so it takes up the ejection's finite-burn error (#747: a 230 s
        /// burn round low Kerbin orbit loses about 2 % of its node, TransferFlightTests). With the encounter held, one not found
        /// is not flown; with none and no such burn, the missed-ejection recovery. Without correction, the encounter is checked
        /// as it stands.
        /// </summary>
        private static TransferFlightStep OnParent(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            // KSP must report the parent after the warp (#770): still round the body the craft left, the way out first.
            if (BackRoundHome(setup, ask)) return Go(ask, Escaping);
            if (!ask.Correction) return Arrive(snapshot, setup, ask);
            var enc = Encounter(setup);
            if (enc != null && Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, Mid);
            string late;
            var soon = setup.Now + ask.TurnTime + Lead;
            var ut = CorrectionUt(soon, setup.Now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out late);
            if (late != "") return Fail(late);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, AimUt = ask.ArrivalUt > ut ? ask.ArrivalUt : (double?)null,
            });
            var found = fix.HasBurn && fix.Error == null && !OverDvLeft(ask, fix);
            if (enc != null && !found) return Go(ask, Mid);
            if (enc == null && !found) return Missed(snapshot, setup, ask, "no encounter with " + setup.Target.Name + " after the ejection burn");
            return FlyCorrection(ask, fix, "correction", Mid);
        }

        /// <summary>
        /// The Course Correction's burn UT (#368): <paramref name="mid"/>, or sooner when max_duration ends before then, leaving 120 s
        /// of it to burn in. A burn sooner than <paramref name="lead"/> s from now can't be flown: <paramref name="reason"/> says
        /// why, else "". Pure; return asks it through TRANSFERSTEP (#731).
        /// </summary>
        internal static double CorrectionUt(double mid, double now, double lead, double startUt, double? maxDuration, out string reason)
        {
            reason = "";
            var ut = maxDuration.HasValue ? Math.Min(mid, startUt + maxDuration.Value - BurnRoom) : mid;
            if (ut < now + lead)
                reason = maxDuration.HasValue
                    ? "max_duration (" + Num(maxDuration.Value) + " s) ends at UT " + Num(startUt + maxDuration.Value) + ", too soon for the Course Correction: its burn needs "
                        + Num(lead + BurnRoom) + " s from UT " + Num(now)
                    : "the arrival comes too soon for the Course Correction";
            return ut;
        }

        /// <summary>The earliest departure (#346, #357): the block's start plus the turn time, or the window if that is a later UT.</summary>
        internal static double Earliest(double startUt, double turnTime, double? window)
        {
            var earliest = startUt + turnTime;
            return window.HasValue ? Math.Max(earliest, window.Value) : earliest;
        }

        /// <summary>A Course Correction found: its error ends the block, under 0.1 m/s it isn't flown (straight on to <paramref name="then"/>), else its node.</summary>
        private static TransferFlightStep FlyCorrection(TransferFlightAsk ask, CorrectionPlan fix, string kind, string then)
        {
            if (!fix.HasBurn || fix.Error != null) return Fail(fix.Error);
            if (fix.DeltaV < SkipDv) return Go(ask, then);
            var step = Node(kind, fix.Ut, fix.Prograde, fix.Normal, fix.Radial, then);
            step.ArrivalUt = ask.ArrivalUt;
            step.PlaneUt = ask.PlaneUt;
            return step;
        }

        /// <summary>The craft's arrival at the target on the snapshot's patched conics: the first patch round it, or null.</summary>
        internal sealed class Arrival
        {
            public double SoiUt;
            public double Pe;
            public double PeUt;
            public double VInf;
            /// <summary>The bodies the path goes round on its way, the target last.</summary>
            public List<string> Bodies = new List<string>();
        }

        internal static Arrival Encounter(PredictBurn.Setup setup)
        {
            var path = SingleShooting.Follow(setup.System, setup.Body, setup.Start, setup.Now, Patches);
            if (path == null) return null;
            var bodies = new List<string>();
            foreach (var patch in path)
            {
                bodies.Add(patch.Body.Name);
                if (patch.Body == setup.Target)
                {
                    var o = patch.Orbit;
                    return new Arrival
                    {
                        SoiUt = patch.StartUt, Pe = o.PeR - patch.Body.Radius, PeUt = o.NextPeriapsis(patch.StartUt) ?? patch.StartUt,
                        VInf = o.Closed ? 0 : Math.Sqrt(o.Mu / Math.Abs(o.A)), Bodies = bodies,
                    };
                }
                if (patch.Error != null) return null;
            }
            return null;
        }

        /// <summary>The Encounter Watch for a coast on <paramref name="enc"/>; null once the flight has had its <see cref="MaxDrops"/>.</summary>
        private static EncounterWatchPlan Watch(PredictBurn.Setup setup, Arrival enc, TransferFlightAsk ask, double pointUt, double waitUt, string phase, double midUt)
        {
            if (ask.Drops >= MaxDrops) return null;
            return new EncounterWatchPlan
            {
                Target = setup.Target.Name, Pe = enc.Pe, SoiUt = enc.SoiUt, SinceUt = setup.Now,
                PeLimit = CoastCheckpoints.PeLimit(setup.Target, ask.PeTolerance), Bodies = enc.Bodies,
                Checkpoint = Convert.ToString(Token(setup, enc, ask, pointUt, waitUt, phase, midUt), CultureInfo.InvariantCulture) ?? "",
            };
        }

        private static bool Within(double offBy, double tolerance)
        {
            return Math.Abs(offBy) <= tolerance;
        }

        private static TransferFlightStep Node(string kind, double ut, double prograde, double normal, double radial, string then)
        {
            return new TransferFlightStep { Do = TransferFlightStep.Burn, Kind = kind, Ut = ut, Prograde = prograde, Normal = normal, Radial = radial, Phase = then };
        }

        /// <summary>Straight on to <paramref name="phase"/>, carrying the ask (and <paramref name="why"/> when given).</summary>
        private static TransferFlightStep Go(TransferFlightAsk ask, string phase, string why = null)
        {
            return new TransferFlightStep { Phase = phase, ArrivalUt = ask.ArrivalUt, PlaneUt = ask.PlaneUt, Why = why ?? ask.Why };
        }

        private static TransferFlightStep Fail(string why, TransferFlightStep with = null)
        {
            var step = with ?? new TransferFlightStep();
            step.Error = why ?? "";
            step.Do = TransferFlightStep.Done;
            return step;
        }

        private static string Km(double m)
        {
            return Math.Round(m / 1000, 1).ToString(CultureInfo.InvariantCulture);
        }

        private static string Num(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
