// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ClaudiaKermin.Core.Claude;
using ClaudiaKermin.Core.Missions;

namespace ClaudiaKermin.SchemaProbe
{
    /// <summary>
    /// Does the API compile this structured-output schema (#199)? Sends one Messages request (max_tokens 16, a
    /// one-line user message, output_config.format = the schema, no retries) through the mod's ClaudeClient and
    /// prints the HTTP status and the API's error message. Never prints the key or any request header.
    /// The key is read from the file named by CLAUDIA_PROBE_KEY_FILE. With --count it only counts the schema's
    /// unions, optional parameters and size, and with --print it writes the schema; neither sends anything.
    /// Exit code 0: compiled (2xx) or counted; 1: the API refused or the request failed; 2: bad usage.
    /// </summary>
    public static class Program
    {
        private const string KeyFileVariable = "CLAUDIA_PROBE_KEY_FILE";

        /// <summary>The cheapest current model with structured outputs.</summary>
        private const string DefaultModel = "claude-haiku-5-5";

        private const string Usage =
            "usage: ClaudiaKermin.SchemaProbe <source> [--model <id>] [--count | --print]\n"
            + "  mission-reply-planning   the planning schema, as the mod sends it (Mission Plan, Settings, replies, changes)\n"
            + "  mission-reply-flight     the flight schema, as the mod sends it (Sequence Plan, Debrief, Alert, questions)\n"
            + "  mission-reply-all        every reply kind in one schema (what #199 replaced)\n"
            + "  mission-reply:<k>,<k>    the schema for these kinds: mission_plan, sequence_plan, debrief, answer, alert\n"
            + "  mission-one              the one Mission schema (#534): the flight schema with mission_plan in kind, no mission_plan part\n"
            + "  mission-grammar          #534 grammar probe: mission-one with a Mission tool list incl. non-strict propose_mission_plan, then a control without it\n"
            + "  cache-probe              #534 cache probe: one long prompt, baseline twice, then only effort changed, then only tool_choice changed; prints usage only\n"
            + "  craft-check            CraftCheckRules.SchemaJson() as built\n"
            + "  schema.json              a schema file, e.g. tools/ClaudiaKermin.SchemaProbe/schemas/*.json\n"
            + "  --count counts only and --print writes the schema JSON; neither sends anything.\n"
            + "  The API key is read from the file named by " + KeyFileVariable + ".";

        private const string GrammarSource = "mission-grammar";
        private const string CacheSource = "cache-probe";

        /// <summary>The cache probe's default model: one that takes effort and has a modest price.</summary>
        private const string CacheModel = "claude-sonnet-5-5";

        private const string KindsPrefix = "mission-reply:";

        public static int Main(string[] args)
        {
            string source = null;
            var model = DefaultModel;
            var modelGiven = false;
            var countOnly = false;
            var print = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--count") countOnly = true;
                else if (args[i] == "--print") print = true;
                else if (args[i] == "--model" && i + 1 < args.Length) { model = args[++i]; modelGiven = true; }
                else if (source == null && !args[i].StartsWith("--", StringComparison.Ordinal)) source = args[i];
                else return Fail(Usage);
            }
            if (source == null) return Fail(Usage);

            if (source == GrammarSource) return RunGrammar(model, countOnly, print);
            if (source == CacheSource) return RunCache(modelGiven ? model : CacheModel, countOnly, print);

            string schema;
            if (source == "mission-one") schema = Probes.MissionSchema();
            else if (source == "mission-reply-planning") schema = MissionReply.PlanningSchemaJson;
            else if (source == "mission-reply-flight") schema = MissionReply.FlightSchemaJson;
            else if (source == "mission-reply-all") schema = MissionReply.SchemaFor(MissionReply.AllKinds);
            else if (source.StartsWith(KindsPrefix, StringComparison.Ordinal))
            {
                var kinds = source.Substring(KindsPrefix.Length).Split(',');
                var unknown = kinds.FirstOrDefault(k => !MissionReply.AllKinds.Contains(k));
                if (unknown != null) return Fail("unknown kind: " + unknown + "\n" + Usage);
                schema = MissionReply.SchemaFor(kinds);
            }
            else if (source == "craft-check") schema = CraftCheckRules.SchemaJson();
            else if (File.Exists(source)) schema = File.ReadAllText(source).Trim();
            else return Fail("not found: " + source + "\n" + Usage);

            if (print)
            {
                Console.WriteLine(schema);
                return 0;
            }

            int unions, optional, size;
            try
            {
                unions = SchemaComplexity.Unions(schema);
                optional = SchemaComplexity.OptionalParameters(schema);
                size = SchemaComplexity.Size(schema);
            }
            catch (JsonException e)
            {
                return Fail("not a JSON schema: " + e.Message);
            }
            Console.WriteLine("schema: " + source + " unions=" + Number(unions) + " optional=" + Number(optional) + " size=" + Number(size));
            if (countOnly) return 0;

            var client = NewClient(out var keyError);
            if (client == null) return Fail(keyError);
            var request = new MessagesRequest { Model = model, MaxTokens = 16, OutputSchemaJson = schema };
            request.Messages.Add(Message.UserText("Reply with kind answer and say ok."));

            var result = client.Stream(request, null, CancellationToken.None);
            Console.WriteLine("model: " + model);
            if (result.HttpStatus == 0)
            {
                Console.WriteLine("no HTTP response (" + result.Failure + ")");
                return 1;
            }
            Console.WriteLine("HTTP " + Number(result.HttpStatus));
            if (result.ErrorMessage != null) Console.WriteLine("error: " + result.ErrorMessage);
            if (result.Status == StreamStatus.Completed)
            {
                Console.WriteLine("compiled (stop_reason " + result.Message.RawStopReason + ")");
                return 0;
            }
            // A 2xx means the schema compiled, even if the stream broke off afterwards.
            Console.WriteLine("failed (" + result.Failure + ")");
            return result.HttpStatus >= 200 && result.HttpStatus < 300 ? 0 : 1;
        }

        private static ClaudeClient NewClient(out string error)
        {
            error = null;
            var keyFile = Environment.GetEnvironmentVariable(KeyFileVariable);
            if (string.IsNullOrEmpty(keyFile) || !File.Exists(keyFile)) { error = "set " + KeyFileVariable + " to a file holding the API key"; return null; }
            var key = File.ReadAllText(keyFile).Trim();
            if (key.Length == 0) { error = "the key file is empty"; return null; }

            // The client's log lines hold no key or headers, but the probe prints nothing it doesn't need.
            var client = new ClaudeClient(new HttpWebRequestTransport(), () => key, line => { });
            client.Timing.RetriesBeforeContent = 0;
            client.Timing.RetriesAfterContent = 0;
            return client;
        }

        /// <summary>Does the one Mission schema still compile with propose_mission_plan in the tool list? The control leaves the tool out.</summary>
        private static int RunGrammar(string model, bool countOnly, bool print)
        {
            var schema = Probes.MissionSchema();
            var withTool = Probes.GrammarRequest(model, true);
            var control = Probes.GrammarRequest(model, false);
            if (print)
            {
                Console.WriteLine("# with propose_mission_plan\n" + withTool.ToJson() + "\n# control, tool removed\n" + control.ToJson());
                return 0;
            }
            Console.WriteLine("schema: mission-one unions=" + Number(SchemaComplexity.Unions(schema)) + " optional=" + Number(SchemaComplexity.OptionalParameters(schema))
                + " size=" + Number(SchemaComplexity.Size(schema)));
            Console.WriteLine("tools: " + string.Join(", ", withTool.Tools.Select(t => t.Name + (t.Strict ? " (strict)" : "")).ToArray()));
            if (countOnly) return 0;

            var client = NewClient(out var keyError);
            if (client == null) return Fail(keyError);
            Console.WriteLine("model: " + model);
            var withOutcome = SendGrammar(client, "with propose_mission_plan", withTool);
            var controlOutcome = SendGrammar(client, "control, tool removed", control);
            string verdict;
            if (withOutcome == "inconclusive" || controlOutcome == "inconclusive") verdict = "inconclusive (a request failed for a reason other than a 2xx or a 400); run it again";
            else if (withOutcome == controlOutcome) verdict = "the tool " + (withOutcome == "compiled" ? "adds nothing to the grammar budget (both compiled)" : "is not the cause (both refused)");
            else verdict = controlOutcome == "compiled" ? "THE TOOL ADDS TO THE GRAMMAR BUDGET (only the control compiled)" : "unexpected: only the tool request compiled";
            Console.WriteLine("verdict: " + verdict);
            return withOutcome == "compiled" && controlOutcome == "compiled" ? 0 : 1;
        }

        /// <summary>compiled (2xx), refused (400: the grammar or the request), or inconclusive (anything else, e.g. a 429 or 5xx).</summary>
        private static string SendGrammar(ClaudeClient client, string label, MessagesRequest request)
        {
            var result = client.Stream(request, null, CancellationToken.None);
            var outcome = result.HttpStatus >= 200 && result.HttpStatus < 300 ? "compiled" : result.HttpStatus == 400 ? "refused" : "inconclusive";
            Console.WriteLine(label + ": HTTP " + Number(result.HttpStatus) + " " + outcome
                + (result.ErrorMessage != null ? " error: " + result.ErrorMessage : ""));
            return outcome;
        }

        /// <summary>Does changing effort alone, or tool_choice alone, cost the prompt cache? Prints usage only.</summary>
        private static int RunCache(string model, bool countOnly, bool print)
        {
            var steps = Probes.CacheRequests(model, print || countOnly ? "<the long text>" : Probes.LongText(Guid.NewGuid().ToString("N")));
            if (print)
            {
                foreach (var step in steps) Console.WriteLine("# " + step.Key + "\n" + step.Value.ToJson());
                return 0;
            }
            if (countOnly)
            {
                Console.WriteLine("long text: " + Number(Probes.LongText("n").Length) + " characters, about 10k tokens");
                foreach (var step in steps) Console.WriteLine(step.Key + ": effort=" + step.Value.Effort + " tool_choice=" + (step.Value.NoToolChoice ? "none" : "auto"));
                return 0;
            }

            var client = NewClient(out var keyError);
            if (client == null) return Fail(keyError);
            Console.WriteLine("model: " + model);
            var failed = false;
            foreach (var step in steps)
            {
                var result = client.Stream(step.Value, null, CancellationToken.None);
                var usage = result.Message != null && result.HttpStatus >= 200 && result.HttpStatus < 300 ? result.Message.Usage : null;
                Console.WriteLine(step.Key + ": HTTP " + Number(result.HttpStatus) + (usage == null
                    ? " no usage" + (result.ErrorMessage != null ? " error: " + result.ErrorMessage : "")
                    : " cache_creation_input_tokens=" + usage.CacheCreationInputTokens.ToString(CultureInfo.InvariantCulture)
                        + " cache_read_input_tokens=" + usage.CacheReadInputTokens.ToString(CultureInfo.InvariantCulture)
                        + " input_tokens=" + usage.InputTokens.ToString(CultureInfo.InvariantCulture)));
                if (usage == null) failed = true;
            }
            return failed ? 1 : 0;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            return 2;
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
