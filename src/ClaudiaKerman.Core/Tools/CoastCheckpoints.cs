// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Claude;
using ClaudiaKerman.Core.Kos;
using ClaudiaKerman.Core.Missions;
using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Core.Tools
{
    /// <summary>
    /// Where a Coast Checkpoint left a flight (#775): all the resumed block needs and all the check on return reads, written into
    /// the mod's resumed Mission Script as one string (the <c>checkpoint</c> option), so the block carries it without reading it.
    /// </summary>
    internal sealed class CoastToken
    {
        public const string Prefix = "resume:";

        public string Block = "transfer_to";
        public string Target = "";
        /// <summary>The periapsis the coast was planned on, m above the target's sea level, and how far it may drift, m (the Encounter Watch's).</summary>
        public double Pe;
        public double PeLimit;
        /// <summary>The UT of the next correction point, and the lead before it (<see cref="CoastCheckpoints.Lead"/>).</summary>
        public double PointUt;
        public double LeadS;
        /// <summary>The UT the coast's wait runs to, and the phase after it.</summary>
        public double WaitUt;
        public string Phase = "";
        /// <summary>The UT the coast ends: the periapsis at the target, or the reentry.</summary>
        public double ArriveUt;
        /// <summary>The UT of the target's sphere of influence, for the resumed block's <c>encounter_ut</c>; -1 for none.</summary>
        public double SoiUt = -1;
        public double MidUt = -1;
        public double ArrivalUt = -1;
        public double PlaneUt = -1;
        public bool Sibling;
        public bool Recovered;
        public bool Restored;
        public int Drops;
        /// <summary>The bodies the coast goes round, the target last.</summary>
        public string Bodies = "";

        /// <summary>When the player is called back: the correction point less the lead.</summary>
        public double DueUt
        {
            get { return PointUt - LeadS; }
        }

        public override string ToString()
        {
            return Prefix + string.Join(";", new[]
            {
                "block=" + Block, "target=" + Target, "pe=" + Num(Pe), "pe_limit=" + Num(PeLimit), "point=" + Num(PointUt), "lead=" + Num(LeadS), "wait=" + Num(WaitUt),
                "phase=" + Phase, "arrive=" + Num(ArriveUt), "soi=" + Num(SoiUt), "mid=" + Num(MidUt), "arrival=" + Num(ArrivalUt), "plane=" + Num(PlaneUt),
                "sibling=" + (Sibling ? "1" : "0"), "recovered=" + (Recovered ? "1" : "0"), "restored=" + (Restored ? "1" : "0"), "drops=" + Drops.ToString(CultureInfo.InvariantCulture), "bodies=" + Bodies,
            });
        }

        /// <summary>The token <see cref="ToString"/> wrote; null for any other text (a save is not trusted).</summary>
        public static CoastToken Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var values = new Dictionary<string, string>();
            foreach (var pair in text.Substring(Prefix.Length).Split(';'))
            {
                var at = pair.IndexOf('=');
                if (at <= 0) return null;
                values[pair.Substring(0, at)] = pair.Substring(at + 1);
            }
            string target;
            if (!values.TryGetValue("target", out target) || !Plain(target)) return null;
            var token = new CoastToken { Target = target };
            token.Block = Get(values, "block");
            token.Phase = Get(values, "phase");
            token.Bodies = Get(values, "bodies");
            token.Sibling = Get(values, "sibling") == "1";
            token.Recovered = Get(values, "recovered") == "1";
            token.Restored = Get(values, "restored") == "1";
            if (token.Block != "transfer_to" && token.Block != "return") return null;
            if (!Plain(token.Phase) || !Plain(token.Bodies)) return null;
            int drops;
            if (!int.TryParse(Get(values, "drops"), NumberStyles.Integer, CultureInfo.InvariantCulture, out drops)) return null;
            token.Drops = drops;
            return Read(values, "pe", out token.Pe) && Read(values, "pe_limit", out token.PeLimit) && Read(values, "point", out token.PointUt)
                && Read(values, "lead", out token.LeadS) && Read(values, "wait", out token.WaitUt) && Read(values, "arrive", out token.ArriveUt) && Read(values, "soi", out token.SoiUt)
                && Read(values, "mid", out token.MidUt) && Read(values, "arrival", out token.ArrivalUt) && Read(values, "plane", out token.PlaneUt) ? token : null;
        }

        /// <summary>Whether text may sit inside the token and a Kerboscript string: a body's name or a phase.</summary>
        public static bool Plain(string text)
        {
            return text != null && text.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == ',');
        }

        private static string Get(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : "";
        }

        private static bool Read(IDictionary<string, string> values, string key, out double value)
        {
            return double.TryParse(Get(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Num(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The check on return's verdict (#775).</summary>
    internal enum CoastVerdictKind
    {
        Kept,
        Drifted,
        Lost,
    }

    /// <summary>What the check on return found, with the figures Claudia plans from when it isn't clean.</summary>
    internal sealed class CoastVerdict
    {
        public CoastVerdictKind Kind;
        /// <summary>The periapsis predicted now, m; null with no encounter.</summary>
        public double? Pe;
        public double? SoiUt;
        public double? PeUt;
        /// <summary>Game seconds past the correction point; 0 when not late.</summary>
        public double LateS;
        /// <summary>The verdict and its figures in one line.</summary>
        public string Line = "";
    }

    /// <summary>
    /// Coast Checkpoints (#775, GLOSSARY): during a long coast of <c>transfer_to</c> or <c>return</c>, the block may end Nominal so the
    /// player can fly elsewhere. Core decides everything here: the safety test, the check on return (the Encounter Watch's own
    /// rule on the Game Snapshot), the option that lets a Mission's run checkpoint, and the resumed Mission Script. The block only
    /// passes the option through (ADR 0014).
    /// </summary>
    internal static class CoastCheckpoints
    {
        /// <summary>The Script Result keys a checkpoint adds: the resume token, and the UT of the next correction point.</summary>
        public const string TokenKey = CoastArmed.ResultKey;
        public const string PointKey = "checkpoint_ut";
        /// <summary>The <c>checkpoint</c> option: no checkpoints (the default: a block test, a bare call), or a Mission's run that may have them.</summary>
        public const string Off = "off";
        public const string On = "on";
        /// <summary>What a Due Time at a checkpoint waits for.</summary>
        public const string WaitsFor = "the next correction";
        /// <summary>return's own lead before its correction, s (return.ks: 300 s to turn to and lead the burn).</summary>
        public const double ReturnBurnLeadS = 300;
        /// <summary>How long before the correction a checkpoint's Due Time falls, game s (#779): time for the scene change, the check on return and a plan.</summary>
        public const double DueLeadS = 1800;

        /// <summary>A checkpoint's lead before its correction: <see cref="DueLeadS"/>, never less than the turn time plus <see cref="BurnPoints.TurnMarginSeconds"/>.</summary>
        public static double Lead(double turnTime)
        {
            return Math.Max(DueLeadS, turnTime + BurnPoints.TurnMarginSeconds);
        }

        /// <summary>What may not run twice: a burn, a stage or a held control before the coast's block means the script can't start over.</summary>
        private static readonly Regex NotTwice = new Regex(@"\b(stage|add|remove|lock|wait|warpto|toggle|doevent|doaction|on|off|ag\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LexiconOpen = new Regex(@"\G\s*(lexicon|lex)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---- The safety test ----

        /// <summary>
        /// "" or why the craft may not be left: item 3 of #775. The encounter must be there; its periapsis clear of the atmosphere and the
        /// ground, so a late return finds the craft safe; and the craft must stay on this patch up to the correction point, but for
        /// the target's own sphere of influence, which the finer correction sits just inside and <paramref name="enc"/>'s safe periapsis covers.
        /// </summary>
        public static string Unsafe(PredictBurn.Setup setup, TransferFlight.Arrival enc, double pointUt)
        {
            var target = setup.Target;
            if (enc == null) return "no encounter with " + target.Name;
            if (PeUnsafe(setup, enc.Pe, pointUt, enc.SoiUt)) return "the periapsis at " + target.Name + " is " + Km(enc.Pe) + " km, in its atmosphere or at its surface";
            var path = SingleShooting.Follow(setup.System, setup.Body, setup.Start, setup.Now, 2);
            var first = path != null && path.Count > 0 ? path[0] : null;
            if (first == null || first.End == PatchEnd.Stable || first.EndUt >= pointUt) return "";
            if (first.End == PatchEnd.Encounter && first.Next == target) return "";
            return "the craft changes sphere of influence" + (first.End == PatchEnd.Encounter ? " into " + first.Next.Name : " leaving " + setup.Body.Name) + " before the next correction";
        }

        /// <summary>
        /// The periapsis is in the atmosphere or at the ground. A trip home that ends in reentry (<see cref="ArrivalRule.EndsInReentry(GameSnapshot, string)"/>)
        /// plans its periapsis in the air on purpose (#779): it is safe while the correction at <paramref name="pointUt"/> comes before the
        /// target's sphere of influence at <paramref name="soiUt"/>, as the craft can't reach the air before it. The ground is never safe.
        /// </summary>
        private static bool PeUnsafe(PredictBurn.Setup setup, double pe, double pointUt, double soiUt)
        {
            var target = setup.Target;
            if (pe <= Math.Max(0, target.TerrainHeight)) return true;
            if (pointUt < soiUt && ArrivalRule.EndsInReentry(setup.Snapshot, target.Name)) return false;
            return pe <= target.AtmosphereHeight;
        }

        // ---- The check ----

        /// <summary>The check on the Game Snapshot, with no Claude call: kept and within tolerance, drifted (over the Encounter Watch's limit, or an unsafe periapsis) or lost.</summary>
        public static CoastVerdict Judge(GameSnapshot snapshot, CoastToken token, double now)
        {
            var verdict = new CoastVerdict { Kind = CoastVerdictKind.Lost, LateS = Math.Max(0, now - token.PointUt) };
            if (snapshot == null)
            {
                verdict.Line = "there is no Game Snapshot of the craft";
                return verdict;
            }
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, token.Target, out error);
            if (setup == null)
            {
                verdict.Line = "the " + token.Target + " encounter is lost: " + JsonNode.Str(JsonReader.Parse(error.Json)["error"]);
                return verdict;
            }
            if (!token.Bodies.Split(',').Contains(setup.Body.Name))
            {
                verdict.Line = "the " + token.Target + " encounter is lost: KSP has the craft round " + setup.Body.Name;
                return verdict;
            }
            var enc = TransferFlight.Encounter(setup);
            if (enc == null)
            {
                verdict.Line = "the " + token.Target + " encounter is lost: the path no longer reaches its sphere of influence";
                return verdict;
            }
            verdict.Pe = enc.Pe;
            verdict.SoiUt = enc.SoiUt;
            verdict.PeUt = enc.PeUt;
            // Round the target, a path with no periapsis ahead gives the patch's start, which is now.
            if (now > enc.PeUt || (setup.Body == setup.Target && enc.PeUt <= setup.Now))
            {
                verdict.Line = "the " + token.Target + " encounter is lost: the craft went past its periapsis, " + Km(enc.Pe) + " km";
                return verdict;
            }
            var drift = Math.Abs(enc.Pe - token.Pe);
            if (drift > token.PeLimit || PeUnsafe(setup, enc.Pe, token.PointUt, enc.SoiUt))
            {
                verdict.Kind = CoastVerdictKind.Drifted;
                verdict.Line = "the " + token.Target + " encounter has drifted: its periapsis is " + Km(enc.Pe) + " km, " + Km(drift) + " km off the " + Km(token.Pe) + " km planned (limit " + Km(token.PeLimit) + " km)";
                return verdict;
            }
            verdict.Kind = CoastVerdictKind.Kept;
            verdict.Line = "the " + token.Target + " encounter is kept: its periapsis is " + Km(enc.Pe) + " km";
            return verdict;
        }

        /// <summary>"" or why a far switch can't leave the craft now: the check isn't clean, or the safety test fails.</summary>
        public static string CannotLeave(GameSnapshot snapshot, CoastToken token, double now)
        {
            if (snapshot == null) return "there is no Game Snapshot of the craft";
            if (now >= token.DueUt) return "the next correction is too near";
            var verdict = Judge(snapshot, token, now);
            if (verdict.Kind != CoastVerdictKind.Kept) return verdict.Line;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, token.Target, out error);
            return setup == null ? "the craft's orbit can't be read" : Unsafe(setup, TransferFlight.Encounter(setup), token.PointUt);
        }

        // ---- The script ----

        /// <summary>The steps that are safe to run twice, so a script that starts with one can still start over (#778): an explicit, small list.</summary>
        private static readonly Regex SafeTwice = new Regex(@"\bif\s+hasnode\s*\{\s*remove\s+nextnode\s*\.\s*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LocalLexicon = new Regex(@"\blocal\s+(?<name>[a-z_]\w*)\s+is\s+(?<lex>lexicon|lex)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Identifier = new Regex(@"^[a-z_]\w*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Quoted = new Regex("^\"(?<n>[^\"]+)\"$", RegexOptions.CultureInvariant);

        /// <summary>The script's coast block and where the mod's option goes, or why the script can't be resumed (#778).</summary>
        private sealed class CoastShape
        {
            public BlockCall Call;
            public string Code;
            public string Masked;
            /// <summary>What blocks resuming, as a clause that follows "this script"; "" when the script can be resumed.</summary>
            public string Blocker = "";
            public IDictionary<string, string> Options;
            /// <summary>Where the option goes in <see cref="Code"/>; <see cref="Wrap"/> when it needs a <c>lexicon(...)</c> around it.</summary>
            public int InsertAt;
            public bool Wrap;
        }

        /// <summary>
        /// The script's first <c>transfer_to</c> or <c>return</c>, and whether the mod can write its resume by starting the script over: no other block
        /// first, nothing before it that can't run twice but <see cref="SafeTwice"/>, and its options inline or in a local lexicon only it uses.
        /// Null with no such block.
        /// </summary>
        private static CoastShape Shape(string script)
        {
            var shape = new CoastShape { Code = CoreLibrary.MissionScript(script), Masked = CoreLibrary.MissionScript(script, true) };
            var calls = BlockOptionCheck.Calls(script);
            var at = calls.ToList().FindIndex(c => c.Block == "transfer_to" || c.Block == "return");
            if (at < 0) return null;
            var call = shape.Call = calls[at];
            if (at > 0)
            {
                shape.Blocker = "runs " + calls[0].Block + " before " + call.Block;
                return shape;
            }
            var before = SafeTwice.Replace(shape.Masked.Substring(0, call.Index), m => new string(' ', m.Length));
            var open = shape.Masked.IndexOf('(', call.Index) + 1;
            var inline = LexiconOpen.Match(shape.Masked, open);
            if (call.Options != null)
            {
                shape.Options = call.Options;
                shape.Wrap = !inline.Success;
                shape.InsertAt = inline.Success ? inline.Index + inline.Length : open;
            }
            else
            {
                var name = LocalArgument(shape, open);
                var declared = name == null ? null : LocalLexicon.Matches(before).Cast<Match>().LastOrDefault(m => m.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase));
                // Only the coast's block may read it: a shared lexicon would give the other block an option it doesn't know.
                var options = declared == null || Occurrences(shape.Masked, name) != 2 ? null : BlockOptionCheck.Lexicon(shape.Code.Substring(declared.Groups["lex"].Index));
                if (options == null)
                {
                    shape.Blocker = "passes " + call.Block + " its options in a way the mod can't read";
                    return shape;
                }
                shape.Options = options;
                shape.InsertAt = declared.Index + declared.Length;
                // The declaration runs twice harmlessly: it is not a step.
                before = before.Remove(declared.Index, declared.Length).Insert(declared.Index, new string(' ', declared.Length));
            }
            var step = NotTwice.Match(before);
            if (step.Success) shape.Blocker = "uses " + step.Value.ToLowerInvariant() + " before " + call.Block;
            return shape;
        }

        /// <summary>The one identifier passed to the call whose "(" ends at <paramref name="open"/>; null when the argument is anything else.</summary>
        private static string LocalArgument(CoastShape shape, int open)
        {
            var close = shape.Masked.IndexOf(')', open);
            if (close < 0) return null;
            var argument = shape.Code.Substring(open, close - open).Trim();
            return Identifier.IsMatch(argument) ? argument : null;
        }

        private static int Occurrences(string masked, string name)
        {
            return Regex.Matches(masked, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
        }

        /// <summary>The script with the <c>checkpoint</c> option set on its coast's block; unchanged when it has no such block, or the option is already there.</summary>
        private static string WithOption(string script, string value)
        {
            if (string.IsNullOrWhiteSpace(script) || value.IndexOf('"') >= 0) return script;
            var shape = Shape(script);
            if (shape == null || shape.Blocker.Length > 0 || shape.Options.ContainsKey(TokenKey)) return script;
            var option = "\"" + TokenKey + "\", \"" + value + "\"";
            var code = shape.Code;
            if (shape.Wrap) return code.Substring(0, shape.InsertAt) + "lexicon(" + option + ")" + code.Substring(shape.InsertAt);
            return code.Substring(0, shape.InsertAt) + option + (shape.Options.Count > 0 ? ", " : "") + code.Substring(shape.InsertAt);
        }

        // ---- When a long coast can't checkpoint (#778) ----

        /// <summary>
        /// "" or what stops the script's long coast from resuming at a Coast Checkpoint, as a clause after "this script" (e.g. "runs circularize before
        /// transfer_to"). "" for a script with no coast, one that can be resumed, or a short coast: the estimate is the time to the correction midway along
        /// a Hohmann transfer against <see cref="WindowWaits.LongWaitS"/>; a coast the snapshot can't estimate says nothing.
        /// </summary>
        public static string LongCoastBlocker(string script, GameSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(script)) return "";
            var shape = Shape(script);
            if (shape == null || shape.Blocker.Length == 0) return "";
            // Options the mod can't read name no target: a transfer_to with them could coast as long as any.
            var unknown = shape.Call.Block == "transfer_to" && shape.Call.Options == null;
            return unknown || CoastSeconds(shape.Call, snapshot) > WindowWaits.LongWaitS ? shape.Blocker : "";
        }

        /// <summary>The mod's line that says why, for the plan card and the log.</summary>
        public static string BlockerLine(string blocker)
        {
            return "This script " + blocker + ", so its coast can't stop at a Coast Checkpoint.";
        }

        /// <summary>Game seconds to the correction midway along the transfer; 0 when the snapshot can't give it.</summary>
        private static double CoastSeconds(BlockCall call, GameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Orbit == null) return 0;
            var home = Body(snapshot, string.IsNullOrEmpty(snapshot.Orbit.Body) ? snapshot.Body : snapshot.Orbit.Body);
            if (home == null) return 0;
            SnapshotBody parent;
            double r1, r2;
            if (call.Block == "return")
            {
                // Down from the moon's orbit to the planet it leaves for.
                parent = Body(snapshot, home.Parent);
                if (parent == null) return 0;
                r1 = home.Sma;
                r2 = parent.Radius + parent.AtmosphereHeight;
            }
            else
            {
                var named = Quoted.Match(call.Option("target") ?? "");
                var target = named.Success ? Body(snapshot, named.Groups["n"].Value) : null;
                if (target == null) return 0;
                if (string.Equals(target.Parent, home.Name, StringComparison.OrdinalIgnoreCase))
                {
                    parent = home;
                    r1 = snapshot.Orbit.SemiMajorAxis;
                }
                else if (home.Parent.Length > 0 && string.Equals(target.Parent, home.Parent, StringComparison.OrdinalIgnoreCase))
                {
                    parent = Body(snapshot, home.Parent);
                    r1 = home.Sma;
                }
                else return 0;
                r2 = target.Sma;
            }
            if (parent == null || parent.Gm <= 0 || r1 <= 0 || r2 <= 0) return 0;
            var a = (r1 + r2) / 2;
            return Math.PI * Math.Sqrt(a * a * a / parent.Gm) / 2;
        }

        private static SnapshotBody Body(GameSnapshot snapshot, string name)
        {
            return snapshot.Bodies.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>What a Mission's run of the plan's script runs: its coast's block may checkpoint. Whatever else the script does stays as Claudia wrote it.</summary>
        public static string Armed(string script)
        {
            return WithOption(script, On);
        }

        /// <summary>The token a run's Script Result carries when its block ended at a checkpoint; null otherwise.</summary>
        public static CoastToken Of(RunOutcome outcome)
        {
            if (outcome == null || outcome.Kind != RunOutcomeKind.Finished || outcome.Report == null) return null;
            object token;
            return outcome.Report.TryGetValue(TokenKey, out token) ? CoastToken.Parse(token as string) : null;
        }

        /// <summary>
        /// The resumed Sequence Plan: the same <c>plan_step</c> and script, entering the flight at the coast (no ejection), its Due Time the
        /// correction point less the lead. Null when the script can't be resumed.
        /// </summary>
        public static SequencePlan Resumed(SequencePlan plan, CoastToken token, double now)
        {
            var text = token.ToString();
            var script = WithOption(StripOption(plan.Script), text);
            if (script.IndexOf(text, StringComparison.Ordinal) < 0) return null;
            return new SequencePlan
            {
                Name = plan.Name,
                PlanStep = plan.PlanStep,
                Intent = plan.Intent,
                Why = "The mod's own resume of the coast: no request. It warps to the correction at " + KerbinTime.Date(token.PointUt) + ", with the Encounter Watch running.",
                Target = plan.Target,
                Tolerances = plan.Tolerances,
                ExpectedDeltaV = 0,
                ExpectedDurationSeconds = Math.Ceiling(Math.Max(token.ArriveUt, token.PointUt) - now + TransferFlight.AutoSlackS),
                AbortConditions = plan.AbortConditions,
                Risks = plan.Risks,
                TelemetryChecks = plan.TelemetryChecks,
                Script = script,
                DueTimeUt = token.DueUt,
                Vessel = plan.Vessel,
                ReasonGiven = plan.ReasonGiven,
                CoastCheckpoint = token.ToString(),
            };
        }

        /// <summary>The script as Claudia wrote it (a resumed plan is resumed again from its own token's place).</summary>
        private static string StripOption(string script)
        {
            return Regex.Replace(script ?? "", "\"" + TokenKey + "\"\\s*,\\s*\"[^\"]*\"\\s*,?\\s*", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // ---- return's step (transfer_to's are TransferFlight's) ----

        /// <summary>
        /// What <c>return</c> does before its correction at <paramref name="mid"/>: end at a checkpoint when it is over <see cref="WindowWaits.LongWaitS"/> off
        /// and safe; resumed, wait to it with the Encounter Watch armed; else go on. <c>ut</c> is the correction's UT.
        /// </summary>
        public static TransferFlightStep ReturnCoast(GameSnapshot snapshot, string target, double mid, double turnTime, double arriveUt, string mode)
        {
            var step = new TransferFlightStep { Do = "go", Ut = mid };
            if (snapshot == null || string.IsNullOrEmpty(mode) || mode == Off) return step;
            ToolOutcome error;
            var setup = PredictBurn.Setup.Prepare(snapshot, target, out error);
            if (setup == null) return step;
            var enc = TransferFlight.Encounter(setup);
            var now = setup.Now;
            var token = CoastToken.Parse(mode);
            if (token != null)
            {
                step.Ut = Math.Max(token.PointUt, now + ReturnBurnLeadS);
                step.Do = TransferFlightStep.Wait;
                step.WaitUt = Math.Max(now, step.Ut - ReturnBurnLeadS - BurnPoints.TurnMarginSeconds);
                step.Watch = ReturnWatch(setup, token);
                return step;
            }
            if (mid - now <= WindowWaits.LongWaitS || enc == null || !string.IsNullOrEmpty(Unsafe(setup, enc, mid))) return step;
            var at = new CoastToken
            {
                Block = "return", Target = target, Pe = enc.Pe, PeLimit = PeLimit(setup.Target, 1000), PointUt = mid, LeadS = Lead(turnTime),
                WaitUt = mid - ReturnBurnLeadS - BurnPoints.TurnMarginSeconds, Phase = "correction", ArriveUt = arriveUt, Bodies = string.Join(",", enc.Bodies.ToArray()),
            };
            if (!CoastToken.Plain(at.Bodies)) return step;
            step.Do = TransferFlightStep.Done;
            step.Report[TokenKey] = at.ToString();
            step.Report[PointKey] = Math.Round(mid);
            step.Report["planet_pe"] = Math.Round(enc.Pe);
            return step;
        }

        /// <summary>Round the planet a Kepler orbit doesn't drift, so the watch mostly stands as the coast a far switch can leave; on the moon's patch it watches as ever.</summary>
        private static EncounterWatchPlan ReturnWatch(PredictBurn.Setup setup, CoastToken token)
        {
            return new EncounterWatchPlan
            {
                Target = token.Target, Pe = token.Pe, PeLimit = token.PeLimit, SoiUt = token.ArriveUt, SinceUt = setup.Now, Bodies = token.Bodies.Split(',').ToList(), Checkpoint = token.ToString(),
            };
        }

        /// <summary>The Encounter Watch's limit: <see cref="EncounterWatch.DriftShare"/> of the target's sphere of influence, or the block's tolerance if wider.</summary>
        public static double PeLimit(SnapshotBody target, double tolerance)
        {
            return Math.Max(tolerance, EncounterWatch.DriftShare * target.SoiRadius);
        }

        // ---- The Debrief and what Claudia hears ----

        /// <summary>The line the fact starts with; <see cref="MissionLogRebuild"/> finds the mod-written card by it.</summary>
        public const string FactPrefix = "The mod debriefed the run itself, with no request: it stopped at a Coast Checkpoint, Nominal (JSON data): ";
        public const string CardWhy = "The mod's own Debrief: the coast is clean, so it stopped at a Coast Checkpoint and no request was made.";

        /// <summary>The short user-side fact in place of a Debrief turn: the sequence, what happened and the run's report so far.</summary>
        public static string Fact(string sequence, string whatHappened, string report)
        {
            return FactPrefix + new JsonWriter().BeginObject().Field("sequence", sequence ?? "").Field("what_happened", whatHappened ?? "").EndObject() + (string.IsNullOrEmpty(report) ? "" : "\n" + report);
        }

        /// <summary>The what_happened a fact carries; null for any other text.</summary>
        public static string ReadFact(string text)
        {
            if (text == null || !text.StartsWith(FactPrefix, StringComparison.Ordinal)) return null;
            var json = text.Substring(FactPrefix.Length);
            var end = json.IndexOf('\n');
            if (end >= 0) json = json.Substring(0, end);
            try
            {
                return JsonNode.Str(JsonReader.Parse(json)["what_happened"]);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static Debrief Debrief(string whatHappened)
        {
            return new Debrief
            {
                Outcome = "Nominal",
                WhatHappened = whatHappened,
                OutcomeVsTarget = whatHappened,
                FollowUp = FollowUps.None,
            };
        }

        /// <summary>What the Debrief says happened.</summary>
        public static string WhatHappened(CoastToken token)
        {
            return "Coasting to " + token.Target + ": the encounter is confirmed and its periapsis, " + Km(token.Pe) + " km, is safe. The next correction is at " + KerbinTime.Date(token.PointUt) + ", so the flight stopped at a Coast Checkpoint.";
        }

        /// <summary>The turn text when the check isn't clean: the verdict's figures, with the lateness.</summary>
        public static string CheckReport(CoastVerdict verdict, CoastToken token)
        {
            var text = new StringBuilder("The player came back to the craft from a Coast Checkpoint in " + token.Block + ", and the mod's check found that " + verdict.Line + ".");
            text.Append("\nThe correction that was next: UT " + Whole(token.PointUt) + ".");
            if (verdict.SoiUt.HasValue) text.Append(" The sphere of influence of " + token.Target + " comes at UT " + Whole(verdict.SoiUt.Value) + ", the periapsis at UT " + Whole(verdict.PeUt.Value) + ".");
            if (verdict.LateS > 0) text.Append("\nThe player is late: the correction point passed " + Span(verdict.LateS) + " ago (" + Whole(verdict.LateS) + " s).");
            text.Append("\nPropose the next Sequence Plan from these figures, for the same plan_step: " + token.Block + " does not resume this coast.");
            return text.ToString();
        }

        /// <summary>The line in the log when the check is clean.</summary>
        public static string CleanLine(CoastVerdict verdict, CoastToken token)
        {
            return "Checked on return: " + verdict.Line + ". The resume stands, with no request." + (verdict.LateS > 0 ? " You are " + Span(verdict.LateS) + " late for the correction, so it burns as soon as the craft can turn." : "");
        }

        private static string Km(double m)
        {
            return Math.Round(m / 1000, 1).ToString(CultureInfo.InvariantCulture);
        }

        private static string Whole(double x)
        {
            return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Span(double s)
        {
            return KerbinTime.Span(s);
        }
    }
}
