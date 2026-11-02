// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;
using ClaudiaKermin.Core.Tools;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>One line of a Building Block's trace (<c>ADDONS:CLAUDIA:BLOCKTRACE</c>, #365) as KSP.log has it.</summary>
    public sealed class TraceLine
    {
        public string Block = "";
        public double Ut;
        public readonly Dictionary<string, string> Fields = new Dictionary<string, string>();

        private static readonly Regex Shape = new Regex(@"(?<block>[A-Za-z_]+) trace at UT (?<ut>-?[0-9.]+): (?<fields>.*?)(?: \('[^']*'\))?\s*$", RegexOptions.Compiled);

        /// <summary>The trace line in a KSP.log line (the log's own prefix and the run's label are ignored), or null when it holds none.</summary>
        public static TraceLine Parse(string line)
        {
            var match = Shape.Match(line ?? "");
            if (!match.Success) return null;
            var trace = new TraceLine { Block = match.Groups["block"].Value, Ut = double.Parse(match.Groups["ut"].Value, CultureInfo.InvariantCulture) };
            string last = null;
            foreach (var piece in match.Groups["fields"].Value.Split(new[] { ", " }, StringSplitOptions.None))
            {
                var space = piece.IndexOf(' ');
                if (space < 0 || piece.Substring(0, space).Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                {
                    // A value that held ", ": it goes on the last field.
                    if (last != null) trace.Fields[last] += ", " + piece;
                    continue;
                }
                last = piece.Substring(0, space);
                trace.Fields[last] = piece.Substring(space + 1);
            }
            return trace;
        }

        public double Num(string field, double missing = double.NaN)
        {
            string text;
            double value;
            return Fields.TryGetValue(field, out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : missing;
        }

        public string Text(string field)
        {
            string text;
            return Fields.TryGetValue(field, out text) ? text : "";
        }
    }

    /// <summary>One altitude the replay compares: the sim's row and the game's there.</summary>
    public sealed class ReplayRow
    {
        public double Altitude;
        public double SimPitch, GamePitch, SimPrograde, GamePrograde, SimSpeed, GameSpeed, SimQ, GameQ, SimMass, GameMass;
        public string SimPhase = "", GamePhase = "";
        /// <summary>The game's seconds between passes of launch's loop there, and that in physics ticks (at 0.02 s a tick).</summary>
        public double GameGap, GameTicks;
    }

    /// <summary>One stage lighting in the sim and in the game.</summary>
    public sealed class ReplayStaging
    {
        public int Stage;
        public double SimAltitude, SimSpeed;
        public double GameAltitude = double.NaN, GameSpeed = double.NaN;
    }

    /// <summary>What the replay found; <see cref="Text"/> is the table a person reads.</summary>
    public sealed class ReplayReport
    {
        public double TurnShape, MaxAoa;
        public string LaunchCall = "";
        public bool SimReachesOrbit;
        public string SimFailure = "";
        public readonly List<ReplayRow> Rows = new List<ReplayRow>();
        public readonly List<ReplayStaging> Stagings = new List<ReplayStaging>();
        public double SimCoastAp, GameCoastAp = double.NaN, SimMaxQ, GameMaxQ = double.NaN;
        public double SimDvToOrbit, GameLaunchDv = double.NaN, GameCircularizeDv = double.NaN;
        /// <summary>The first altitude where pitch differs by over 5 degrees or speed by over 5%; NaN if none.</summary>
        public double DivergeAltitude = double.NaN;
        public string DivergeWhy = "", DivergeSimPhase = "", DivergeGamePhase = "";
        public int LoopPasses;
        public double LoopMeanGap, LoopMaxGap, LoopMaxTicks;
        public int DropScans;
        public double DropScanMean, DropScanMax;
        public string Text = "";
    }

    /// <summary>
    /// The replay comparison (#531): rebuilds <c>plan_ascent</c>'s sim from a Mission's folder (the Game Snapshot and input the Craft
    /// Check saved, the turn_shape and options the transcript's launch call flew) and lines its climb up against the game's trace
    /// (the <c>launch</c> trace lines of KSP.log) by altitude. Test-side: no Planning Tool, nothing in the prompt.
    /// </summary>
    public static class AscentReplay
    {
        public const double PitchTolerance = 5;
        public const double SpeedTolerance = 0.05;
        private const double TickSeconds = 0.02;

        /// <summary>The KSP.log (or any text holding trace lines) at <paramref name="logPath"/>.</summary>
        public static ReplayReport Run(string missionFolder, string logPath)
        {
            return Run(missionFolder, File.ReadLines(logPath));
        }

        public static ReplayReport Run(string missionFolder, IEnumerable<string> logLines)
        {
            var snapshot = GameSnapshot.Parse(File.ReadAllText(Path.Combine(missionFolder, MissionStore.AscentPlanSnapshotFile)));
            var saved = JsonReader.Parse(File.ReadAllText(Path.Combine(missionFolder, MissionStore.AscentPlanInputFile)));
            var call = LaunchCall(Path.Combine(missionFolder, MissionFolder.TranscriptFile));
            var report = new ReplayReport { LaunchCall = call.Count == 0 ? "(none found: defaults)" : string.Join(", ", call.Select(p => p.Key + " " + p.Value)) };

            PlanAscent.Setup setup;
            var refusal = PlanAscent.Prepare(JsonReader.Parse(MergedInput(saved, call)), snapshot, out setup);
            if (refusal != null) throw new InvalidOperationException("the saved ascent can't be set up: " + refusal.Json);
            var given = new Dictionary<string, string>();
            foreach (var pair in call) given[pair.Key] = pair.Value;
            report.TurnShape = Number(given, "turn_shape", 0.45);
            report.MaxAoa = Number(given, "max_aoa", 5);
            setup.Options.TurnShape = report.TurnShape;
            setup.Options.MaxAoa = report.MaxAoa;
            setup.Options.Track = true;
            var flight = AscentSim.Fly(setup.Options, setup.Air, setup.Drag, setup.Stages);
            report.SimReachesOrbit = flight.ReachesOrbit;
            report.SimFailure = flight.Failure;
            report.SimCoastAp = flight.CoastAp;
            report.SimMaxQ = flight.MaxQ;
            report.SimDvToOrbit = flight.DeltaVToOrbit;

            var lines = logLines.Select(TraceLine.Parse).Where(l => l != null).ToList();
            var climb = lines.Where(l => l.Block == "launch" && l.Text("phase") != "end" && l.Text("phase") != "drop_scan" && !double.IsNaN(l.Num("alt"))).ToList();
            // A log may hold several launches: the last one's rows (its t starts over), and only its burning, as the sim's Track is.
            var restart = 0;
            for (var i = 1; i < climb.Count; i++)
                if (climb[i].Num("t") < climb[i - 1].Num("t")) restart = i;
            climb = climb.Skip(restart).ToList();
            climb = climb.Where(l => l.Text("phase") != "coast").ToList();
            Compare(report, flight, climb);

            var end = lines.LastOrDefault(l => l.Block == "launch" && l.Text("phase") == "end");
            if (end != null)
            {
                report.GameCoastAp = end.Num("coast_ap");
                report.GameMaxQ = end.Num("max_q");
                report.GameLaunchDv = end.Num("dv_used");
            }
            var circularize = lines.LastOrDefault(l => l.Block == "circularize" && l.Text("phase") == "end");
            if (circularize != null) report.GameCircularizeDv = circularize.Num("dv_used");

            var gaps = climb.Select(l => l.Num("gap", 0)).ToList();
            report.LoopPasses = gaps.Count;
            if (gaps.Count > 0)
            {
                report.LoopMeanGap = gaps.Average();
                report.LoopMaxGap = gaps.Max();
                report.LoopMaxTicks = report.LoopMaxGap / TickSeconds;
            }
            var scans = lines.Where(l => l.Block == "launch" && l.Text("phase") == "drop_scan").Select(l => l.Num("scan_s", 0)).ToList();
            report.DropScans = scans.Count;
            if (scans.Count > 0)
            {
                report.DropScanMean = scans.Average();
                report.DropScanMax = scans.Max();
            }
            report.Text = Render(report);
            return report;
        }

        private static void Compare(ReplayReport report, AscentFlight flight, List<TraceLine> climb)
        {
            foreach (var sim in flight.Track)
            {
                // The sim's row is its first tick over each 1000 m; the game is read at that row's altitude, and the row is named for the 1000 m.
                var at = sim[0];
                var row = new ReplayRow
                {
                    Altitude = Math.Floor(at / 1000) * 1000,
                    SimPitch = sim[1],
                    SimPrograde = sim[2],
                    SimQ = sim[3],
                    SimMass = sim[6],
                    SimSpeed = Math.Sqrt(sim[9] * sim[9] + sim[10] * sim[10]),
                    SimPhase = AscentFlight.TrackPhases[(int)sim[11]],
                };
                // The two trace lines either side of this altitude on the way up: the game's values are read between them.
                TraceLine below = null, above = null;
                foreach (var line in climb)
                {
                    if (line.Num("alt") <= at) below = line;
                    else
                    {
                        above = line;
                        break;
                    }
                }
                if (below != null && above != null)
                {
                    var f = (at - below.Num("alt")) / Math.Max(1e-9, above.Num("alt") - below.Num("alt"));
                    Func<Func<TraceLine, double>, double> between = read => read(below) + f * (read(above) - read(below));
                    row.GamePitch = between(l => l.Num("pitch"));
                    row.GamePrograde = between(l => l.Num("prograde"));
                    row.GameSpeed = between(Speed);
                    row.GameQ = between(l => l.Num("q"));
                    row.GameMass = between(l => l.Num("mass"));
                    row.GamePhase = below.Text("phase");
                    row.GameGap = below.Num("gap", 0);
                    row.GameTicks = row.GameGap / TickSeconds;
                    report.Rows.Add(row);
                    if (double.IsNaN(report.DivergeAltitude))
                    {
                        var pitchOff = Math.Abs(row.GamePitch - row.SimPitch) > PitchTolerance;
                        var speedOff = row.SimSpeed > 0 && Math.Abs(row.GameSpeed - row.SimSpeed) / row.SimSpeed > SpeedTolerance;
                        if (pitchOff || speedOff)
                        {
                            report.DivergeAltitude = row.Altitude;
                            report.DivergeWhy = pitchOff && speedOff ? "pitch and speed" : pitchOff ? "pitch" : "speed";
                            report.DivergeSimPhase = row.SimPhase;
                            report.DivergeGamePhase = row.GamePhase;
                        }
                    }
                }
            }

            foreach (var lit in flight.Lit)
            {
                var staging = new ReplayStaging { Stage = lit.Stage, SimAltitude = lit.Altitude, SimSpeed = lit.Speed };
                var first = climb.FirstOrDefault(l => (int)l.Num("stage", -1) == lit.Stage);
                if (first != null)
                {
                    staging.GameAltitude = first.Num("alt");
                    staging.GameSpeed = Speed(first);
                }
                report.Stagings.Add(staging);
            }
        }

        private static double Speed(TraceLine line)
        {
            var h = line.Num("h_speed");
            var v = line.Num("v_speed");
            return Math.Sqrt(h * h + v * v);
        }

        private static double Number(Dictionary<string, string> values, string name, double missing)
        {
            string text;
            double value;
            return values.TryGetValue(name, out text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : missing;
        }

        private static readonly string[] PlanKeys = { "target_ap", "stage_limit", "turn_start_speed", "throttle_max_q", "target_inc", "launch_at" };

        /// <summary>plan_ascent's saved input with what the launch call flew laid over it (not turn_shape: the replay flies the one shape).</summary>
        private static string MergedInput(JsonNode saved, List<KeyValuePair<string, string>> call)
        {
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var member in saved.Members)
                if (member.Key != "turn_shape" && !call.Any(p => p.Key == member.Key && PlanKeys.Contains(p.Key))) fields.Add(new KeyValuePair<string, string>(member.Key, member.Value.Raw));
            foreach (var pair in call)
            {
                if (!PlanKeys.Contains(pair.Key)) continue;
                double number;
                var raw = double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    ? number.ToString("R", CultureInfo.InvariantCulture)
                    : "\"" + pair.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                // launch_at "now" is launch's default, not a window to find.
                if (pair.Key == "launch_at" && pair.Value == "now") continue;
                fields.Add(new KeyValuePair<string, string>(pair.Key, raw));
            }
            return "{" + string.Join(",", fields.Select(f => "\"" + f.Key + "\":" + f.Value).ToArray()) + "}";
        }

        private static readonly Regex Launch = new Regex(@"\blaunch\s*\(\s*lexicon\s*\(", RegexOptions.Compiled);

        /// <summary>The text of the last <c>launch(lexicon(...))</c> call's arguments in script, up to the bracket that closes them; null if none.</summary>
        private static string LastLaunchArguments(string script)
        {
            var match = Launch.Matches(script).Cast<Match>().LastOrDefault();
            if (match == null) return null;
            var depth = 1;
            var inString = false;
            for (var i = match.Index + match.Length; i < script.Length; i++)
            {
                var ch = script[i];
                if (ch == '"') inString = !inString;
                else if (!inString && ch == '(') depth++;
                else if (!inString && ch == ')' && --depth == 0) return script.Substring(match.Index + match.Length, i - match.Index - match.Length);
            }
            return null;
        }

        /// <summary>Splits arguments at the commas outside brackets and strings.</summary>
        private static List<string> SplitArguments(string args)
        {
            var parts = new List<string>();
            var depth = 0;
            var inString = false;
            var start = 0;
            for (var i = 0; i < args.Length; i++)
            {
                var ch = args[i];
                if (ch == '"') inString = !inString;
                else if (!inString && ch == '(') depth++;
                else if (!inString && ch == ')') depth--;
                else if (!inString && depth == 0 && ch == ',')
                {
                    parts.Add(args.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(args.Substring(start).Trim());
            return parts;
        }

        /// <summary>
        /// The options of the last <c>launch(lexicon(...))</c> in the transcript, in order: from a tool call's script or from a Mission
        /// Plan reply's "script". Empty when there is none, or the call has no options.
        /// </summary>
        public static List<KeyValuePair<string, string>> LaunchCall(string transcriptPath)
        {
            var found = new List<KeyValuePair<string, string>>();
            if (!File.Exists(transcriptPath)) return found;
            string args = null;
            foreach (var message in JsonReader.Parse(File.ReadAllText(transcriptPath))["messages"].Items)
            {
                if (JsonNode.Str(message["role"]) != "assistant") continue;
                var content = message["content"];
                if (content == null) continue;
                foreach (var block in content.Items)
                {
                    foreach (var script in Scripts(block))
                    {
                        args = LastLaunchArguments(script) ?? args;
                    }
                }
            }
            if (args == null) return found;
            var parts = SplitArguments(args);
            for (var i = 0; i + 1 < parts.Count; i += 2)
                found.Add(new KeyValuePair<string, string>(parts[i].Trim('"'), parts[i + 1].Trim('"')));
            return found;
        }

        private static IEnumerable<string> Scripts(JsonNode block)
        {
            var type = JsonNode.Str(block["type"]);
            if (type == "tool_use")
            {
                var script = JsonNode.Str(block["input"] == null ? null : block["input"]["script"]);
                if (script != null) yield return script;
            }
            else if (type == "text")
            {
                var text = JsonNode.Str(block["text"]) ?? "";
                JsonNode reply = null;
                try
                {
                    reply = JsonReader.Parse(text);
                }
                catch (JsonException)
                {
                }
                var script = reply != null && reply.Kind == JsonKind.Object ? JsonNode.Str(reply["script"]) : null;
                yield return script ?? text;
            }
        }

        private static string Render(ReplayReport r)
        {
            var c = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.AppendLine("launch call: " + r.LaunchCall);
            text.AppendLine("sim: turn_shape " + r.TurnShape.ToString("0.00", c) + ", max_aoa " + r.MaxAoa.ToString("0.#", c)
                + (r.SimReachesOrbit ? ", reaches orbit" : ", fails: " + r.SimFailure));
            text.AppendLine();
            text.AppendLine("every 1000 m (sim | game): pitch and prograde from vertical (deg), speed (m/s), q (Pa), mass (t), phase; loop gap (s, physics ticks)");
            text.AppendLine("  alt     pitch         prograde      speed          q              mass           phase                    gap     ticks");
            foreach (var row in r.Rows)
                text.AppendLine("  " + row.Altitude.ToString("0", c).PadLeft(5)
                    + "  " + Pair(row.SimPitch, row.GamePitch, "0.0") + "  " + Pair(row.SimPrograde, row.GamePrograde, "0.0")
                    + "  " + Pair(row.SimSpeed, row.GameSpeed, "0") + "  " + Pair(row.SimQ, row.GameQ, "0") + "  " + Pair(row.SimMass, row.GameMass, "0.00")
                    + "  " + (row.SimPhase + "|" + row.GamePhase).PadRight(22) + "  " + row.GameGap.ToString("0.000", c) + "  " + row.GameTicks.ToString("0.0", c));
            text.AppendLine();
            text.AppendLine("stagings (altitude m, speed m/s; sim | game):");
            foreach (var s in r.Stagings)
                text.AppendLine("  stage " + s.Stage + ": " + s.SimAltitude.ToString("0", c) + " m, " + s.SimSpeed.ToString("0", c) + " m/s | "
                    + (double.IsNaN(s.GameAltitude) ? "not in the trace" : s.GameAltitude.ToString("0", c) + " m, " + s.GameSpeed.ToString("0", c) + " m/s"));
            text.AppendLine();
            text.AppendLine("coast_ap (m): sim " + r.SimCoastAp.ToString("0", c) + " | game " + Show(r.GameCoastAp));
            text.AppendLine("max_q (Pa): sim " + r.SimMaxQ.ToString("0", c) + " | game " + Show(r.GameMaxQ));
            text.AppendLine("delta-v to orbit (m/s): sim " + r.SimDvToOrbit.ToString("0", c) + " | game launch " + Show(r.GameLaunchDv) + " + circularize " + Show(r.GameCircularizeDv)
                + " = " + Show(r.GameLaunchDv + r.GameCircularizeDv));
            text.AppendLine();
            text.AppendLine(double.IsNaN(r.DivergeAltitude)
                ? "no divergence: pitch within " + PitchTolerance + " degrees and speed within " + (SpeedTolerance * 100).ToString("0", c) + "% at every 1000 m compared"
                : "first divergence at " + r.DivergeAltitude.ToString("0", c) + " m (" + r.DivergeWhy + "): sim phase " + r.DivergeSimPhase + ", game phase " + r.DivergeGamePhase);
            text.AppendLine("launch loop: " + r.LoopPasses + " trace lines, gap between passes mean " + r.LoopMeanGap.ToString("0.000", c) + " s, longest "
                + r.LoopMaxGap.ToString("0.000", c) + " s (" + r.LoopMaxTicks.ToString("0.0", c) + " physics ticks at 0.02 s)");
            text.AppendLine("drop_empty scans: " + r.DropScans + (r.DropScans > 0 ? ", mean " + r.DropScanMean.ToString("0.000", c) + " s, longest " + r.DropScanMax.ToString("0.000", c) + " s" : ""));
            return text.ToString();
        }

        private static string Pair(double sim, double game, string format)
        {
            var c = CultureInfo.InvariantCulture;
            return (sim.ToString(format, c) + "|" + game.ToString(format, c)).PadRight(13);
        }

        private static string Show(double value)
        {
            return double.IsNaN(value) ? "-" : value.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
