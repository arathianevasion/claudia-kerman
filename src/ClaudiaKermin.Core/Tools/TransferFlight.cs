// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.Core.Tools
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
        private const string FineFlown = "fine_flown";
        private const string FineCheck = "fine_check";
        private const string Escaping = "escaping";
        private const string Escaped = "escaped";
        /// <summary>m/s under which a Course Correction is not flown; the finer one's max_residual (#672); a burn's lead, s, past its turn.</summary>
        private const double SkipDv = 0.1;
        private const double FineResidual = 0.1;
        private const double Lead = 60;
        /// <summary>s past the end of max_duration a Course Correction must leave to burn in; s past the arrival "auto" leaves.</summary>
        private const double BurnRoom = 120;
        private const double AutoSlack = 900;
        /// <summary>s past leaving the sphere of influence the wait ends, as return waits.</summary>
        private const double PastSoi = 30;
        /// <summary>The most patches to the target: out of a moon, round the planet, into another moon.</summary>
        private const int Patches = 4;
        /// <summary>The steps one call may take before it gives up on a loop: a step that flies nothing goes straight on.</summary>
        private const int MaxPhases = 12;

        public static TransferFlightStep Next(GameSnapshot snapshot, TransferFlightAsk ask)
        {
            if (snapshot == null) return Fail("no Game Snapshot");
            for (var i = 0; i < MaxPhases; i++)
            {
                ToolOutcome error;
                var setup = PredictBurn.Setup.Prepare(snapshot, ask.Target, out error);
                if (setup == null) return Fail(JsonNode.Str(JsonReader.Parse(error.Json)["error"]));
                var step = Phase(snapshot, setup, ask);
                step.Sibling |= ask.Sibling;
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
                    return Encounter(setup) == null ? Fail("no encounter with " + target.Name + " after the correction") : Go(ask, Mid);
                case Mid:
                    return MidCorrection(snapshot, setup, ask);
                case MidCheck:
                    return Encounter(setup) == null ? Fail("no encounter with " + target.Name + " after the correction") : Go(ask, Fine);
                case Fine:
                    return FineCorrection(snapshot, setup, ask);
                case FineFlown:
                    // A finer correction that burned nothing is no correction.
                    return Go(ask, FineCheck, ask.Why == "" && ask.LastDv == 0 ? "its burn, under 0.1 m/s, isn't flown" : ask.Why);
                case FineCheck:
                    return Arrive(setup, ask);
                case Escaping:
                    return LeaveHome(setup, ask);
                case Escaped:
                    return OnParent(snapshot, setup, ask);
                default:
                    return Fail("no transfer_to phase named " + ask.Phase);
            }
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
                var found = SiblingEjection.Find(snapshot, setup, target.Name, ask.TargetPe, earliest, ask.Window);
                if (found.Error != null) return Fail(found.Error);
                step = Node("ejection", found.Ut, found.Prograde, found.Normal, found.Radial, Escaping);
                step.Sibling = true;
                arrival = found.ArrivalUt;
                step.Report["v_inf"] = Math.Round(found.VInf, 1);
            }
            else
            {
                var plan = Transfer.Find(snapshot, new TransferAsk { Target = target.Name, TargetPe = ask.TargetPe, EarliestUt = earliest, WindowUt = ask.Window });
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

            // A missed ejection (#363): the cheapest burn from the orbit the craft is on that meets the target, found as the ejection was.
            var missed = "no encounter with " + target.Name + " after the ejection burn";
            var correctionAsk = CorrectionAsk(target.Name, ask.TargetPe, now, ask.TurnTime, ask.StartUt, ask.MaxDuration ?? 0, ask.Auto, ask.DvLeft);
            if (correctionAsk.LatestUt.HasValue && correctionAsk.LatestUt.Value < correctionAsk.EarliestUt)
                return Fail(missed + ", and no correction fits: max_duration (" + Num(ask.MaxDuration ?? 0) + " s) leaves no time for it and the Course Correction after it");
            var found = Transfer.Find(snapshot, correctionAsk);
            if (found.Error != null) return Fail(missed + ", and no correction fits: " + found.Error.Replace("found no transfer: ", ""));
            var step = Node("correction", found.Ut, found.Prograde, found.Normal, found.Radial, Made);
            step.FailPrefix = missed + ", and the correction failed: ";
            if (ask.Auto) step.MaxDuration = found.ArrivalUt - ask.StartUt + AutoSlack;
            step.ArrivalUt = ask.ArrivalUt;
            return step;
        }

        /// <summary>
        /// What the correction after a missed ejection asks <see cref="Transfer"/>: the soonest burn the craft can turn to (turn_time
        /// plus a minute for the lead), one the craft's dv_left (m/s; 0 or less for unknown) pays for, and with max_duration given
        /// (not auto) one that leaves the Course Correction after it its lead and 120 s. One burn: no broken-plane plan, whose plane
        /// change this path would not fly. Pure (transfer_to_correction_ask before #459).
        /// </summary>
        internal static TransferAsk CorrectionAsk(string target, double targetPe, double now, double turnTime, double startUt, double maxDuration, bool auto, double dvLeft)
        {
            var ask = new TransferAsk { Target = target, TargetPe = targetPe, EarliestUt = now + turnTime + Lead, BrokenPlane = false };
            if (!auto) ask.LatestUt = startUt + maxDuration - BurnRoom - turnTime - Lead;
            if (dvLeft > 0) ask.MaxDv = dvLeft;
            return ask;
        }

        /// <summary>The Course Correction halfway to the encounter (sooner if max_duration ends first, #368), when the periapsis is outside pe_tolerance.</summary>
        private static TransferFlightStep MidCorrection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (!ask.Correction) return Go(ask, Fine);
            var enc = Encounter(setup);
            if (enc != null && Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, MidCheck);
            if (enc == null) return Fail("no encounter with " + setup.Target.Name + " after the correction");
            string late;
            var ut = CorrectionUt((setup.Now + enc.SoiUt) / 2, setup.Now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out late);
            if (late != "") return Fail(late);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk { Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut });
            return FlyCorrection(ask, fix, "correction", MidCheck);
        }

        /// <summary>
        /// After the Course Correction or the plane change, a periapsis outside pe_tolerance gets one finer correction (#672),
        /// halfway to the encounter, within pe_tolerance and a tighter max_residual. Why it was not flown or failed is carried to the check.
        /// </summary>
        private static TransferFlightStep FineCorrection(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (!ask.Correction) return Arrive(setup, ask);
            var enc = Encounter(setup);
            if (enc != null && Within(enc.Pe - ask.TargetPe, ask.PeTolerance)) return Go(ask, FineCheck, "");
            var soi = enc != null ? enc.SoiUt : setup.Now;
            // To a body sharing the parent the coast is long, and a burn halfway flown 0.1 m/s off misses by far more than
            // pe_tolerance: the finer correction waits until that error, carried to the periapsis, would stay within it; that is
            // inside the target's sphere of influence, where the block then ends.
            var mid = (setup.Now + soi) / 2;
            if (ask.Sibling && enc != null) mid = Math.Max(mid, enc.PeUt - ask.PeTolerance / FineResidual);
            string why;
            var ut = CorrectionUt(mid, setup.Now, ask.TurnTime + Lead, ask.StartUt, ask.MaxDuration, out why);
            if (why != "") return Go(ask, FineCheck, why);
            var fix = CorrectionBurn.Find(snapshot, new CorrectionAsk
            {
                Target = setup.Target.Name, TargetPe = ask.TargetPe, EarliestUt = ut, LatestUt = ut, PeTolerance = ask.PeTolerance,
            });
            if (!fix.HasBurn || fix.Error != null) return Go(ask, FineCheck, fix.Error);
            if (fix.DeltaV < SkipDv) return Go(ask, FineCheck, "its burn, under 0.1 m/s, isn't flown");
            var step = Node("correction", fix.Ut, fix.Prograde, fix.Normal, fix.Radial, FineFlown);
            if (enc != null && fix.Ut > enc.SoiUt) step.Report["encounter_ut"] = Math.Round(enc.SoiUt);
            step.MaxResidual = FineResidual;
            step.KeepGoing = true;
            step.ArrivalUt = ask.ArrivalUt;
            return step;
        }

        /// <summary>The arrival: the encounter and its periapsis reported, then a miss outside pe_tolerance (with correction) fails the block.</summary>
        private static TransferFlightStep Arrive(PredictBurn.Setup setup, TransferFlightAsk ask)
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
            step.Report["next"] = "coast to encounter_ut + 60 before a capture";
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
            var path = SingleShooting.Follow(setup.System, home, setup.Start, setup.Now, 1);
            var first = path != null && path.Count > 0 ? path[0] : null;
            if (first == null || first.End != PatchEnd.Escape || first.EndUt <= setup.Now)
                return Fail("the ejection burn does not leave " + home.Name + "'s sphere of influence"
                    + (first != null && first.End == PatchEnd.Encounter ? ": it meets " + first.Next.Name + " first" : ""));
            var step = new TransferFlightStep { Do = TransferFlightStep.Wait, Ut = first.EndUt + PastSoi, Phase = Escaped, ArrivalUt = ask.ArrivalUt };
            return step;
        }

        /// <summary>
        /// On the parent's patch: the Course Correction as soon as the craft can turn to it, aimed through the planned arrival when
        /// the ejection left no encounter; then the halfway one, and the finer one late enough that its residual stays within
        /// pe_tolerance. Without correction, the encounter is checked as it stands.
        /// </summary>
        private static TransferFlightStep OnParent(GameSnapshot snapshot, PredictBurn.Setup setup, TransferFlightAsk ask)
        {
            if (!ask.Correction) return Arrive(setup, ask);
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
            var step = FlyCorrection(ask, fix, "correction", Mid);
            if (enc == null && step.Error != "") step.Error = "no encounter with " + setup.Target.Name + " after the ejection burn, and no correction fits: " + step.Error.Replace("found no correction: ", "");
            return step;
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
        }

        internal static Arrival Encounter(PredictBurn.Setup setup)
        {
            var path = SingleShooting.Follow(setup.System, setup.Body, setup.Start, setup.Now, Patches);
            if (path == null) return null;
            foreach (var patch in path)
            {
                if (patch.Body == setup.Target)
                {
                    var o = patch.Orbit;
                    return new Arrival
                    {
                        SoiUt = patch.StartUt, Pe = o.PeR - patch.Body.Radius, PeUt = o.NextPeriapsis(patch.StartUt) ?? patch.StartUt,
                        VInf = o.Closed ? 0 : Math.Sqrt(o.Mu / Math.Abs(o.A)),
                    };
                }
                if (patch.Error != null) return null;
            }
            return null;
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
