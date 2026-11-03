// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudiaKermin.Core.Kos
{
    /// <summary>
    /// A failing part of the Script Check (#606): a Building Block call in the Mission Script whose inline <c>lexicon(...)</c>
    /// misses a required option or names one the block doesn't take, which the block would refuse at once in flight. Each
    /// block's options are read from its <c>&lt;block&gt;_defaults()</c> in the bundled Core Library (the value <c>"required"</c>
    /// marks a required one), plus <c>claudia_common_defaults()</c>: the same lexicons the runtime merges, never a second table.
    /// It judges only a lexicon written inline with every key a literal string; any other form (a variable, a computed key)
    /// is skipped, so it never fails a script the block would take. It reads the Mission Script only, never the library.
    /// </summary>
    public static class BlockOptionCheck
    {
        private static readonly Lazy<IDictionary<string, IList<KeyValuePair<string, bool>>>> LoadedBlocks =
            new Lazy<IDictionary<string, IList<KeyValuePair<string, bool>>>>(LoadBlocks);
        private static readonly Lazy<IList<string>> LoadedCommon = new Lazy<IList<string>>(() =>
            Options(CoreLibrary.Files.First(f => !f.IsBlock).Source, "claudia_common_defaults").Select(o => o.Key).ToList());
        private static readonly Lazy<Regex> Call = new Lazy<Regex>(() => new Regex(
            @"\b(?<block>" + string.Join("|", Blocks.Keys.Select(Regex.Escape).ToArray()) + @")\s*\(\s*(lexicon|lex)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        private static readonly Regex Literal = new Regex("^\"(?<key>[^\"]*)\"$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Lazy<IDictionary<string, IList<KeyValuePair<string, string>>>> LoadedDefaults =
            new Lazy<IDictionary<string, IList<KeyValuePair<string, string>>>>(LoadDefaults);
        private static readonly Lazy<Regex> AnyCall = new Lazy<Regex>(() => new Regex(
            @"\b(?<block>" + string.Join("|", Blocks.Keys.Select(Regex.Escape).ToArray()) + @")\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        private static readonly Regex LexiconOpen = new Regex(@"\G\s*(lexicon|lex)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Each block by name, with its own options in <c>_defaults()</c> order, each true when it is required.</summary>
        public static IDictionary<string, IList<KeyValuePair<string, bool>>> Blocks
        {
            get { return LoadedBlocks.Value; }
        }

        /// <summary>The options every block also takes, from <c>claudia_common_defaults()</c>.</summary>
        public static IList<string> CommonOptions
        {
            get { return LoadedCommon.Value; }
        }

        /// <summary>
        /// One problem per judged call that fails, e.g. "line 3: transfer_to: missing required option target_pe; options: target*,
        /// target_pe*, window, correction, pe_tolerance (* required), plus the common options". <paramref name="script"/> is a Mission Script,
        /// or a bundle whose library lines it skips.
        /// </summary>
        public static IList<string> Problems(string script)
        {
            var problems = new List<string>();
            var code = CoreLibrary.MissionScript(script);
            var masked = CoreLibrary.MissionScript(script, true);
            foreach (Match m in Call.Value.Matches(masked))
            {
                if (IsSuffix(masked, m.Index)) continue;
                var args = Arguments(masked, code, m.Index + m.Length);
                if (args == null || args.Count % 2 != 0) continue;
                var keys = new List<string>();
                for (var i = 0; i < args.Count; i += 2)
                {
                    var key = Literal.Match(args[i]);
                    if (!key.Success) break;
                    keys.Add(key.Groups["key"].Value);
                }
                if (keys.Count * 2 != args.Count) continue;

                var name = m.Groups["block"].Value.ToLowerInvariant();
                var options = Blocks[name];
                var given = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                var known = new HashSet<string>(options.Select(o => o.Key).Concat(CommonOptions), StringComparer.OrdinalIgnoreCase);
                var missing = options.Where(o => o.Value && !given.Contains(o.Key)).Select(o => o.Key).ToList();
                var unknown = keys.Where(k => !known.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count == 0 && unknown.Count == 0) continue;

                var found = new List<string>();
                if (missing.Count > 0) found.Add("missing required option" + (missing.Count > 1 ? "s " : " ") + string.Join(", ", missing.ToArray()));
                if (unknown.Count > 0) found.Add("unknown option" + (unknown.Count > 1 ? "s " : " ") + string.Join(", ", unknown.ToArray()));
                var line = 1 + code.Take(m.Index).Count(c => c == '\n');
                problems.Add("line " + line + ": " + name + ": " + string.Join("; ", found.ToArray()) + "; options: "
                    + string.Join(", ", options.Select(o => o.Key + (o.Value ? "*" : "")).ToArray())
                    + (options.Any(o => o.Value) ? " (* required)," : ",") + " plus the common options");
            }
            return problems;
        }

        /// <summary>
        /// Every Building Block call in the Mission Script, in script order (#696): its options when written inline as a
        /// <c>lexicon(...)</c> of literal keys (or none at all), each key's value as its source text; null options otherwise.
        /// </summary>
        public static IList<BlockCall> Calls(string script)
        {
            var calls = new List<BlockCall>();
            var code = CoreLibrary.MissionScript(script);
            var masked = CoreLibrary.MissionScript(script, true);
            foreach (Match m in AnyCall.Value.Matches(masked))
            {
                if (IsSuffix(masked, m.Index)) continue;
                var args = Arguments(masked, code, m.Index + m.Length);
                if (args == null) continue;
                var call = new BlockCall { Block = m.Groups["block"].Value.ToLowerInvariant(), Index = m.Index };
                var inline = LexiconOpen.Match(masked, m.Index + m.Length);
                if (args.Count == 0) call.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                else if (args.Count == 1 && inline.Success) call.Options = LiteralOptions(Arguments(masked, code, inline.Index + inline.Length));
                calls.Add(call);
            }
            return calls;
        }

        /// <summary>The source text of <paramref name="option"/>'s default in the block's <c>_defaults()</c> (e.g. <c>"ap"</c>, quotes kept); null if it has none.</summary>
        public static string Default(string block, string option)
        {
            IList<KeyValuePair<string, string>> values;
            if (!LoadedDefaults.Value.TryGetValue(block, out values)) return null;
            return values.Where(v => string.Equals(v.Key, option, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).FirstOrDefault();
        }

        /// <summary>Key to value text, or null when a key isn't a literal string or the lexicon doesn't pair up.</summary>
        private static IDictionary<string, string> LiteralOptions(IList<string> args)
        {
            if (args == null || args.Count % 2 != 0) return null;
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Count; i += 2)
            {
                var key = Literal.Match(args[i]);
                if (!key.Success) return null;
                options[key.Groups["key"].Value] = args[i + 1];
            }
            return options;
        }

        private static IDictionary<string, IList<KeyValuePair<string, bool>>> LoadBlocks()
        {
            var blocks = new Dictionary<string, IList<KeyValuePair<string, bool>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in LoadedDefaults.Value)
                blocks[block.Key] = block.Value.Select(o => new KeyValuePair<string, bool>(o.Key, o.Value == "\"required\"")).ToList();
            return blocks;
        }

        private static IDictionary<string, IList<KeyValuePair<string, string>>> LoadDefaults()
        {
            var blocks = new Dictionary<string, IList<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in CoreLibrary.Blocks) blocks[block.Name] = OptionValues(block.Source, block.Name + "_defaults");
            return blocks;
        }

        /// <summary>The keys of the lexicon <paramref name="function"/> returns in <paramref name="source"/>, each true when "required".</summary>
        private static IList<KeyValuePair<string, bool>> Options(string source, string function)
        {
            return OptionValues(source, function).Select(o => new KeyValuePair<string, bool>(o.Key, o.Value == "\"required\"")).ToList();
        }

        /// <summary>The keys of the lexicon <paramref name="function"/> returns in <paramref name="source"/>, each with its value's source text.</summary>
        private static IList<KeyValuePair<string, string>> OptionValues(string source, string function)
        {
            var code = CoreLibrary.MissionScript(source);
            var masked = CoreLibrary.MissionScript(source, true);
            var start = Regex.Match(masked, @"\bfunction\s+" + Regex.Escape(function) + @"\s*\{\s*(parameter\b[^.]*\.\s*)?return\s+lexicon\s*\(");
            if (!start.Success) throw new InvalidOperationException(function + " doesn't return lexicon(...) in the bundled library");
            var args = Arguments(masked, code, start.Index + start.Length);
            var options = new List<KeyValuePair<string, string>>();
            for (var i = 0; args != null && i + 1 < args.Count; i += 2)
            {
                var key = Literal.Match(args[i]);
                if (!key.Success) throw new InvalidOperationException(function + " has a key that isn't a literal string: " + args[i]);
                options.Add(new KeyValuePair<string, string>(key.Groups["key"].Value, args[i + 1]));
            }
            return options;
        }

        /// <summary>
        /// The arguments of the call whose "(" ends just before <paramref name="from"/>, trimmed, read off <paramref name="code"/>
        /// where <paramref name="masked"/> (the same text, strings blanked) is at nesting depth 0; null when the call never closes.
        /// </summary>
        private static IList<string> Arguments(string masked, string code, int from)
        {
            var args = new List<string>();
            var depth = 0;
            var start = from;
            for (var i = from; i < masked.Length; i++)
            {
                var c = masked[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if ((c == ')' || c == ']' || c == '}') && depth > 0) depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(code.Substring(start, i - start).Trim());
                    start = i + 1;
                }
                else if (c == ')')
                {
                    var last = code.Substring(start, i - start).Trim();
                    if (last.Length > 0 || args.Count > 0) args.Add(last);
                    return args;
                }
            }
            return null;
        }

        /// <summary>Whether the call at <paramref name="at"/> is a suffix (<c>thing:coast(...)</c>), not a block.</summary>
        private static bool IsSuffix(string masked, int at)
        {
            var i = at - 1;
            while (i >= 0 && char.IsWhiteSpace(masked[i])) i--;
            return i >= 0 && masked[i] == ':';
        }
    }

    /// <summary>One Building Block call in a Mission Script (<see cref="BlockOptionCheck.Calls"/>).</summary>
    public sealed class BlockCall
    {
        /// <summary>The block's name, lower case.</summary>
        public string Block;
        /// <summary>Where the call starts in the Mission Script.</summary>
        public int Index;
        /// <summary>The options written inline, key to value source text; empty for a call with none; null when they can't be read.</summary>
        public IDictionary<string, string> Options;

        /// <summary>The option's source text, given or the block's default; null when the options can't be read or it has none.</summary>
        public string Option(string name)
        {
            if (Options == null) return null;
            string given;
            return Options.TryGetValue(name, out given) ? given : BlockOptionCheck.Default(Block, name);
        }
    }
}
