// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ClaudiaKerman.Core.Kos;
using kOS.Safe;
using kOS.Safe.Binding;
using kOS.Safe.Callback;
using kOS.Safe.Compilation;
using kOS.Safe.Compilation.KS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Execution;
using kOS.Safe.Function;
using kOS.Safe.Module;
using kOS.Safe.Persistence;
using kOS.Safe.Screen;
using kOS.Safe.Serialization;
using kOS.Safe.Utilities;

namespace ClaudiaKerman.Kerboscript.Tests
{
    /// <summary>
    /// Compiles kerboscript with kOS.Safe and runs it on a real kOS CPU with no KSP, the way kOS's own
    /// test suite does. Only kOS.Safe's built-ins exist (print, maths, strings), plus the
    /// <c>lexicon</c> and <c>list</c> constructors from <see cref="TestFunctions"/>; anything that needs
    /// the game (ship, time, stage) is unavailable, so only pure helpers can run here.
    /// </summary>
    internal static class KosHarness
    {
        private const string Done = "__claudia_test_done__";
        private static readonly object Gate = new object();
        private static bool _initialised;
        private static readonly HashSet<Type> Registered = new HashSet<Type>();

        /// <summary>
        /// Adds the [Function]s nested in <paramref name="rigFunctions"/> to kOS's function table, once. That table is
        /// process-wide and unsynchronised, and test classes run in parallel, so this is the only way into it (#681): under
        /// <see cref="Gate"/>, after <see cref="Init"/>'s own walk, never while a test builds a FunctionManager from it.
        /// </summary>
        public static void Register(Type rigFunctions)
        {
            lock (Gate)
            {
                Init();
                if (Registered.Contains(rigFunctions)) return;
                foreach (var type in rigFunctions.GetNestedTypes())
                    foreach (FunctionAttribute attribute in type.GetCustomAttributes(typeof(FunctionAttribute), false))
                        FunctionManager.RegisterMethod(attribute, type);
                Registered.Add(rigFunctions);
            }
        }

        /// <summary>Compiles <paramref name="source"/>; returns null or the compiler's message.</summary>
        public static string CompileError(string source)
        {
            lock (Gate)
            {
                Init();
                try
                {
                    var shared = NewShared();
                    var parts = shared.ScriptHandler.Compile(GlobalPath.FromString("0:/test.ks"), 1, source, "test", Options(shared));
                    return parts == null ? "compiler returned no code" : null;
                }
                catch (Exception e)
                {
                    return e.GetType().Name + ": " + e.Message;
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="source"/> to the end and returns what it printed, one entry per
        /// <c>print</c>. Throws if it doesn't compile, fails at run time or runs too long.
        /// </summary>
        public static IList<string> Run(string source)
        {
            int steps;
            return Run(source, 2000, 20000, out steps);
        }

        private static int _ipu = 2000;

        /// <summary>
        /// Runs <paramref name="source"/> with <paramref name="ipu"/> instructions per step and returns what it printed;
        /// <paramref name="steps"/> is how many steps it took, so <c>steps * ipu</c> is the most instructions it cost
        /// (kOS stops a step early only when the script ends or waits, so it is within one step of the real count).
        /// </summary>
        public static IList<string> Run(string source, int ipu, int maxSteps, out int steps)
        {
            lock (Gate)
            {
                Init();
                _ipu = ipu;
                var printed = new List<string>();
                var errors = new List<string>();
                var shared = NewShared();
                CapturingScreen screen;
                shared.Screen = CapturingScreen.Create(printed, out screen);
                ((TestLogger)SafeHouse.Logger).Errors = errors;

                var cpu = new CPU(shared);
                shared.Cpu = cpu;
                cpu.Boot();
                printed.Clear();

                cpu.GetCurrentContext().AddParts(Link(source, shared));

                steps = 0;
                for (var step = 0; step < maxSteps; step++)
                {
                    cpu.KOSFixedUpdate(0.02);
                    steps = step + 1;
                    if (printed.Contains(Done))
                    {
                        printed.Remove(Done);
                        return printed;
                    }
                    if (errors.Count > 0) break;
                }
                throw new InvalidOperationException("script did not finish: " + string.Join("\n", errors.Concat(screen.All)));
            }
        }

        /// <summary>How many times this process has compiled the Core Library for <see cref="Run(string)"/>.</summary>
        public static int LibraryCompiles { get; private set; }

        /// <summary>
        /// The Core Library compiled for <see cref="Run(string)"/>, per leading directives and <see cref="Register"/> state: the
        /// directives decide how the library compiles in a bundle, and so do the registered [Function]s, which the compiler
        /// calls as built-ins where a name is one. Each entry's counters say where its labels and scope ids end.
        /// </summary>
        private static readonly Dictionary<string, CompiledLibrary> Libraries = new Dictionary<string, CompiledLibrary>();

        private sealed class CompiledLibrary
        {
            public List<CodePart> Parts;
            public int Labels;
            public short Scopes;
        }

        /// <summary>kOS keeps a compile's label and scope counters in a per-KSScript context it doesn't expose (#1074).</summary>
        private static readonly FieldInfo ContextsField = typeof(KSScript).GetField("contexts", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("kOS.Safe's KSScript has no 'contexts' field: the shared Core Library needs it");

        private static readonly Regex Directive = new Regex(@"\G(?:\s+|//[^\n]*|(@(?:lazyglobal|clobberbuiltins)\s+(?:on|off)\s*\.))",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// The program for <paramref name="source"/>, with the end marker after the Mission Script. A <see cref="CoreLibrary.Bundle"/>
        /// is not compiled whole (#1074): its Core Library is compiled once and shared, then its Mission Script is compiled in
        /// a context that numbers labels and scopes on from the library's, and the library's parts go first, so its hoisted
        /// functions are stored before the Mission Script runs, as they are at the top of a bundle.
        /// </summary>
        private static List<CodePart> Link(string source, SafeSharedObjects shared)
        {
            var path = GlobalPath.FromString("0:/test.ks");
            var libraryStart = CoreLibrary.LibraryStartLine(source);
            if (libraryStart == int.MaxValue)
                return shared.ScriptHandler.Compile(path, 1, source + "\nprint \"" + Done + "\".\n", "test", Options(shared));

            var script = string.Join("\n", source.Replace("\r\n", "\n").Split('\n').Take(libraryStart - 1));
            var library = Library(LeadingDirectives(script), shared);
            Contexts(shared.ScriptHandler)["test"] = new Context { LabelIndex = library.Labels, MaxScopeIdSoFar = library.Scopes };
            var own = shared.ScriptHandler.Compile(path, 1, script + "\nprint \"" + Done + "\".\n", "test", Options(shared));
            return library.Parts.Concat(own).ToList();
        }

        private static CompiledLibrary Library(string directives, SafeSharedObjects shared)
        {
            var key = directives + "|" + string.Join(",", Registered.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal));
            CompiledLibrary library;
            if (Libraries.TryGetValue(key, out library)) return library;
            var compiler = new KSScript();
            var parts = compiler.Compile(GlobalPath.FromString("0:/test.ks"), 1, directives + CoreLibrary.Bundle(""), "library", Options(shared));
            var context = Contexts(compiler)["library"];
            library = new CompiledLibrary { Parts = parts, Labels = context.LabelIndex, Scopes = context.MaxScopeIdSoFar };
            Libraries.Add(key, library);
            LibraryCompiles++;
            return library;
        }

        private static Dictionary<string, Context> Contexts(object scriptHandler)
        {
            return (Dictionary<string, Context>)ContextsField.GetValue(scriptHandler);
        }

        /// <summary>The <c>@lazyglobal</c> and <c>@clobberbuiltins</c> lines kOS reads at the top of <paramref name="script"/>.</summary>
        private static string LeadingDirectives(string script)
        {
            var text = new StringBuilder();
            for (var match = Directive.Match(script); match.Success && match.Length > 0; match = match.NextMatch())
                if (match.Groups[1].Success) text.Append(match.Groups[1].Value).Append('\n');
            return text.ToString();
        }

        private static CompilerOptions Options(SafeSharedObjects shared)
        {
            return new CompilerOptions
            {
                LoadProgramsInSameAddressSpace = false,
                IsCalledFromRun = false,
                AllowClobberBuiltins = false,
                FuncManager = shared.FunctionManager,
                BindManager = shared.BindingMgr,
            };
        }

        private static void Init()
        {
            if (_initialised) return;
            SafeHouse.Init(Stub<IConfig>.Create(), new VersionInfo(1, 6, 0, 1), "", false, System.IO.Path.GetTempPath());
            SafeHouse.Logger = new TestLogger();
            // kOS's walk also registers the [Function]s in assemblies already loaded (this one's
            // TestFunctions); naming this assembly too would register them twice.
            AssemblyWalkAttribute.Walk(new[] { typeof(SafeHouse).Assembly });
            Opcode.InitMachineCodeData();
            CompiledObject.InitTypeData();
            SafeSerializationMgr.CheckIDumperStatics();
            _initialised = true;
        }

        private static SafeSharedObjects NewShared()
        {
            var shared = new SafeSharedObjects();
            shared.FunctionManager = new FunctionManager(shared);
            shared.BindingMgr = Stub<IBindingManager>.Create();
            shared.GameEventDispatchManager = Stub<IGameEventDispatchManager>.Create();
            shared.Processor = Stub<IProcessor>.Create();
            shared.ScriptHandler = new KSScript();
            shared.UpdateHandler = new UpdateHandler();
            shared.VolumeMgr = new VolumeManager();
            shared.Logger = SafeHouse.Logger;
            return shared;
        }

        /// <summary>Any kOS interface, answering every call with a harmless default.</summary>
        public class Stub<T> : DispatchProxy
        {
            public static T Create()
            {
                return DispatchProxy.Create<T, Stub<T>>();
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                var type = method.ReturnType;
                if (method.Name == "get_InstructionsPerUpdate") return _ipu;
                if (type == typeof(string)) return "";
                return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }

        /// <summary>The terminal: each <c>print</c> becomes one entry; everything else is a no-op.</summary>
        public class CapturingScreen : Stub<IScreenBuffer>
        {
            public List<string> Printed;
            public readonly List<string> All = new List<string>();

            public static IScreenBuffer Create(List<string> printed, out CapturingScreen screen)
            {
                var proxy = DispatchProxy.Create<IScreenBuffer, CapturingScreen>();
                screen = (CapturingScreen)(object)proxy;
                screen.Printed = printed;
                return proxy;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if ((method.Name == "Print" || method.Name == "PrintLine") && args.Length > 0)
                {
                    Printed.Add((string)args[0]);
                    All.Add((string)args[0]);
                }
                if (method.Name == "get_ColumnCount") return 80;
                if (method.Name == "get_RowCount") return 40;
                return base.Invoke(method, args);
            }
        }

        private sealed class TestLogger : ILogger
        {
            public List<string> Errors = new List<string>();
            public void Log(string text) { }
            public void Log(Exception e) { Errors.Add(e.Message); }
            public void SuperVerbose(string s) { }
            public void LogWarning(string s) { }
            public void LogWarningAndScreen(string s) { }
            public void LogException(Exception exception) { Errors.Add(exception.Message); }
            public void LogError(string s) { Errors.Add(s); }
        }
    }

    /// <summary>
    /// Stand-ins for kOS's <c>lexicon</c> and <c>list</c> constructors, which live in kOS.dll (it needs
    /// KSP). They build the same kOS.Safe structures the real ones do.
    /// </summary>
    internal static class TestFunctions
    {
        [Function("lexicon", "lex")]
        public class FunctionLexicon : CollectionFunction
        {
            public override void Execute(SafeSharedObjects shared)
            {
                ReturnValue = new Lexicon(PopAll(shared));
            }
        }

        [Function("list")]
        public class FunctionList : CollectionFunction
        {
            public override void Execute(SafeSharedObjects shared)
            {
                ReturnValue = new ListValue(PopAll(shared));
            }
        }

        public abstract class CollectionFunction : SafeFunctionBase
        {
            protected List<Structure> PopAll(SafeSharedObjects shared)
            {
                var args = new Structure[CountRemainingArgs(shared)];
                for (var i = args.Length - 1; i >= 0; i--) args[i] = PopStructureAssertEncapsulated(shared, true);
                AssertArgBottomAndConsume(shared);
                return args.ToList();
            }
        }
    }
}
