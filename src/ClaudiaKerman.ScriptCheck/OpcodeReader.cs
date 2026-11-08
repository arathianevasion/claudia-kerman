// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using kOS.Safe.Compilation;

namespace ClaudiaKerman.ScriptCheck
{
    /// <summary>
    /// Reads compiled opcodes against the <see cref="ForbiddenList"/>. A symbolic stack follows what each
    /// opcode pushes, so a suffix knows its receiver (CONFIG, ADDONS, the TIMEWARP, another vessel) and a call
    /// knows whether its arguments are constants. It reads both arms of a branch in turn; where an expression's
    /// arms meet again (CHOOSE, AND, OR) their values are merged, so each one is checked. Values computed at
    /// run time can slip past it; the runtime gates catch those (ADR 0003).
    /// </summary>
    internal sealed class OpcodeReader
    {
        private static readonly PropertyInfo PushArgument =
            typeof(OpcodePush).GetProperty("Argument", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>What the stack holds: a constant, a variable, a suffix of a receiver, or a result.</summary>
        private sealed class Value
        {
            public bool Marker;
            public bool IsConstant;
            /// <summary>The constant's text, when it is a string.</summary>
            public string Text;
            /// <summary>The variable pushed, lower case, without '$'.</summary>
            public string Variable;
            /// <summary>The suffix that produced this value, lower case.</summary>
            public string Suffix;
            /// <summary>Another vessel, or something reached from one (its parts, modules, control).</summary>
            public bool OtherVessel;
            /// <summary>Where branches meet: the values either arm may leave (null for a plain value).</summary>
            public List<Value> Alternatives;
        }

        /// <summary>A scope the code is in, and the names it has declared so far on this path.</summary>
        private sealed class Frame
        {
            public int Id;
            public HashSet<string> Declared = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>kOS's global scope, every other scope's last parent.</summary>
        private const int Global = 0;
        /// <summary>A function whose code isn't known.</summary>
        private const int AnyFunction = -1;

        private readonly List<string> _findings = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        private bool _recording;

        // The other-vessel taint (#654) follows kOS's scopes: each PushScope names its scope and its parent's, so a name resolves
        // to the scope kOS would find it in, and a function's parameters hold another vessel only if a call to it passes one.
        // What it can't place it taints everywhere. It is learned again until nothing changes, then the findings are read.
        private readonly Dictionary<int, int> _parents = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _scopeAtLabel = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>Where code a call or trigger runs starts: the labels a delegate or trigger points at.</summary>
        private readonly HashSet<string> _entries = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The scopes that are a function's (or lock's) body: a delegate's code starts with them.</summary>
        private readonly HashSet<int> _functionScopes = new HashSet<int>();
        /// <summary>Each scope's local names, wherever in it they are declared.</summary>
        private readonly Dictionary<int, HashSet<string>> _declared = new Dictionary<int, HashSet<string>>();
        /// <summary>The bindings that may hold another vessel: each scope's names.</summary>
        private readonly Dictionary<int, HashSet<string>> _tainted = new Dictionary<int, HashSet<string>>();
        /// <summary>The names tainted in any scope, and those tainted in every scope.</summary>
        private readonly HashSet<string> _taintedNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _taintedEverywhere = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The function scopes stored under each "name*" (a function or lock).</summary>
        private readonly Dictionary<string, HashSet<int>> _functions = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        private readonly HashSet<int> _parametersTainted = new HashSet<int>();
        private readonly HashSet<int> _returnsTainted = new HashSet<int>();
        /// <summary>Another vessel is passed to, or returned by, a function the reader can't name: any one may be it.</summary>
        private bool _anyParameters;
        private bool _anyReturns;
        /// <summary>How much has been learned: reading stops when a pass learns nothing.</summary>
        private int _learned;
        /// <summary>The scopes the code being read is in, innermost last; null where the reader can't tell.</summary>
        private List<Frame> _frames;
        /// <summary>A block test's script (tests/tools): the test-only bridge to Planning Tools is allowed.</summary>
        private bool _allowPlanningTools;
        /// <summary>The first line that is the Core Library's (ADR 0007): block-internal calls are allowed from there on.</summary>
        private int _libraryFromLine = int.MaxValue;
        /// <summary>The line each labelled opcode is on: a function's code starts at the label its definition pushes.</summary>
        private readonly Dictionary<string, int> _labelLines = new Dictionary<string, int>(StringComparer.Ordinal);
        private string _delegateLabel;
        /// <summary>Every name any store opcode binds, anywhere: a bare call to one of these is a delegate in a variable.</summary>
        private readonly HashSet<string> _storedNames = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Calls to a name nothing defines (#928), "line N: NAME", before the Core Library's lines.</summary>
        private readonly List<string> _undefinedCalls = new List<string>();

        /// <summary>The code is a Mission Script bundled with the Core Library, so it has library functions to replace.</summary>
        private bool IsBundle
        {
            get { return _libraryFromLine != int.MaxValue && _libraryFromLine > 1; }
        }

        /// <summary>
        /// Each finding, e.g. "line 3: COPYPATH (touch files)", in order. The code is read until a pass learns nothing
        /// new about which variables, parameters and functions hold another vessel, wherever they are assigned; then once more
        /// to record the findings.
        /// </summary>
        public static List<string> Read(IEnumerable<List<Opcode>> sections, bool allowPlanningTools = false, int libraryFromLine = int.MaxValue,
            List<string> undefinedCalls = null)
        {
            var reader = new OpcodeReader { _allowPlanningTools = allowPlanningTools, _libraryFromLine = libraryFromLine };
            var list = new List<List<Opcode>>(sections);
            reader.Scopes(list);
            int learned;
            do
            {
                learned = reader._learned;
                foreach (var section in list) reader.Walk(section);
            }
            while (reader._learned != learned);
            reader._recording = true;
            foreach (var section in list) reader.Walk(section);
            if (undefinedCalls != null) undefinedCalls.AddRange(reader._undefinedCalls);
            return reader._findings;
        }

        /// <summary>Each scope's parent, which scopes start a function's code, and the name each function is stored under.</summary>
        private void Scopes(List<List<Opcode>> sections)
        {
            var delegates = new List<string>();
            foreach (var code in sections)
                foreach (var op in code)
                {
                    var push = op as OpcodePushScope;
                    if (push != null)
                    {
                        _parents[push.ScopeId] = push.ParentScopeId;
                        if (!string.IsNullOrEmpty(op.Label)) _scopeAtLabel[op.Label] = push.ScopeId;
                    }
                    var stored = op as OpcodeIdentifierBase;
                    if (stored != null && (op is OpcodeStore || op is OpcodeStoreLocal || op is OpcodeStoreGlobal || op is OpcodeStoreExist))
                        _storedNames.Add(Lower(stored.Identifier).TrimStart('$'));
                    var later = op as OpcodePushRelocateLater;
                    if (later != null) _entries.Add(later.DestinationLabel ?? "");
                    if (op is OpcodePushDelegateRelocateLater) delegates.Add(later.DestinationLabel ?? "");
                }
            foreach (var label in delegates)
            {
                int scope;
                if (_scopeAtLabel.TryGetValue(label, out scope)) _functionScopes.Add(scope);
            }
            // A function's body is read before the store that defines it, so its calls need every definition first.
            foreach (var code in sections)
                for (var at = 0; at + 1 < code.Count; at++)
                {
                    var store = code[at + 1];
                    if (code[at] is OpcodePushDelegateRelocateLater
                        && (store is OpcodeStore || store is OpcodeStoreLocal || store is OpcodeStoreGlobal || store is OpcodeStoreExist))
                        Defined(Lower(((OpcodeIdentifierBase)store).Identifier).TrimStart('$'), ((OpcodePushRelocateLater)code[at]).DestinationLabel ?? "");
                }
        }

        private void Walk(List<Opcode> code)
        {
            var stack = new Stack<Value>();
            // Values an arm left on the stack when it jumped ahead, keyed by the label it jumped to.
            var arms = new Dictionary<string, List<Value>>(StringComparer.Ordinal);
            // The same for a jump over a fixed number of opcodes (an OR or AND), keyed by the opcode it lands on.
            var armsAt = new Dictionary<int, List<Value>>();
            // The scopes a jump ahead leaves, keyed by the label (or the opcode) it lands on: a break pops scopes the code
            // after it is still in. Code after a return or jump that no jump reaches is never run.
            var scopesAt = new Dictionary<string, List<Frame>>(StringComparer.Ordinal);
            var scopesAtIndex = new Dictionary<int, List<Frame>>();
            // The stack under an inline function's code (an anonymous function), set aside where the jump over it lands.
            var setAside = new Dictionary<string, Stack<Value>>(StringComparer.Ordinal);
            // A defaulted parameter's store, reached from its argument test when the caller passed it.
            var defaulted = new HashSet<string>(StringComparer.Ordinal);
            _frames = null;
            var unreached = false;
            for (var at = 0; at < code.Count; at++)
            {
                var op = code[at];
                if (!string.IsNullOrEmpty(op.Label)) _labelLines[op.Label] = op.SourceLine;
                // Code a call or trigger starts: run, in scopes the reader can't tell.
                if (unreached && _entries.Contains(op.Label ?? ""))
                {
                    _frames = null;
                    unreached = false;
                }
                List<Frame> jumpedFrom = null, landed;
                var jumpedHere = false;
                if (!string.IsNullOrEmpty(op.Label) && scopesAt.TryGetValue(op.Label, out landed))
                {
                    scopesAt.Remove(op.Label);
                    jumpedFrom = landed;
                    jumpedHere = true;
                }
                if (scopesAtIndex.TryGetValue(at, out landed))
                {
                    scopesAtIndex.Remove(at);
                    jumpedFrom = jumpedHere ? Meet(jumpedFrom, landed) : landed;
                    jumpedHere = true;
                }
                if (jumpedHere)
                {
                    _frames = unreached ? jumpedFrom : Meet(_frames, jumpedFrom);
                    unreached = false;
                }
                else if (unreached) _frames = null;
                // A scope's id names the scopes it is in, however the code got here.
                if (op is OpcodePushScope) unreached = false;
                var reached = !unreached;
                Stack<Value> under;
                if (!string.IsNullOrEmpty(op.Label) && setAside.TryGetValue(op.Label, out under))
                {
                    setAside.Remove(op.Label);
                    stack = under;
                }
                List<Value> waiting;
                if (!string.IsNullOrEmpty(op.Label) && arms.TryGetValue(op.Label, out waiting))
                {
                    arms.Remove(op.Label);
                    if (stack.Count > 0) waiting.Add(stack.Pop());
                    stack.Push(Merge(waiting));
                }
                if (armsAt.TryGetValue(at, out waiting))
                {
                    armsAt.Remove(at);
                    if (stack.Count > 0) waiting.Add(stack.Pop());
                    stack.Push(Merge(waiting));
                }
                if (!string.IsNullOrEmpty(op.Label) && defaulted.Remove(op.Label) && op is OpcodeStoreLocal)
                    stack.Push(Merge(new List<Value> { Pop(stack), Parameter() }));

                var unconditional = op is OpcodeBranchJump;
                if (op is OpcodeReturn || unconditional) unreached = true;
                var branch = op as BranchOpcode;
                List<Frame> already;
                if (reached && branch != null && string.IsNullOrEmpty(branch.DestinationLabel) && branch.Distance > 0)
                {
                    var leaving = Copy(_frames);
                    scopesAtIndex[at + branch.Distance] = scopesAtIndex.TryGetValue(at + branch.Distance, out already) ? Meet(already, leaving) : leaving;
                }
                if (branch != null && !string.IsNullOrEmpty(branch.DestinationLabel))
                {
                    if (reached)
                    {
                        var leaving = Copy(_frames);
                        scopesAt[branch.DestinationLabel] = scopesAt.TryGetValue(branch.DestinationLabel, out already) ? Meet(already, leaving) : leaving;
                    }
                    if (op is OpcodeBranchIfFalse && at > 0 && code[at - 1] is OpcodeTestArgBottom) defaulted.Add(branch.DestinationLabel);
                    // The jump over an anonymous function's code: its code starts on an empty stack, as a call's does.
                    int scope;
                    var push = at + 1 < code.Count ? code[at + 1] as OpcodePushScope : null;
                    if (unconditional && push != null && _scopeAtLabel.TryGetValue(push.Label ?? "", out scope) && _functionScopes.Contains(scope))
                    {
                        setAside[branch.DestinationLabel] = stack;
                        stack = new Stack<Value>();
                        continue;
                    }
                }

                var jump = op as OpcodeBranchJump;
                // A conditional branch consumes its condition (Step pops it); only a plain jump carries an arm's value ahead.
                if (jump != null && !(op is OpcodeBranchIfTrue) && !(op is OpcodeBranchIfFalse) && stack.Count > 0 && !string.IsNullOrEmpty(jump.DestinationLabel))
                {
                    if (!arms.TryGetValue(jump.DestinationLabel, out waiting)) arms[jump.DestinationLabel] = waiting = new List<Value>();
                    waiting.Add(stack.Pop());
                    continue;
                }
                if (jump != null && !(op is OpcodeBranchIfTrue) && !(op is OpcodeBranchIfFalse) && stack.Count > 0
                    && string.IsNullOrEmpty(jump.DestinationLabel) && jump.Distance > 0)
                {
                    List<Value> ahead;
                    if (!armsAt.TryGetValue(at + jump.Distance, out ahead)) armsAt[at + jump.Distance] = ahead = new List<Value>();
                    ahead.Add(stack.Pop());
                    continue;
                }
                Step(op, stack);
            }
        }

        private static Value Merge(List<Value> values)
        {
            var merged = new Value { Alternatives = new List<Value>() };
            foreach (var value in values)
            {
                merged.Alternatives.AddRange(Each(value));
                merged.OtherVessel |= value.OtherVessel;
            }
            return merged;
        }

        /// <summary>The plain values a value may be: itself, or each alternative where branches met.</summary>
        private static IEnumerable<Value> Each(Value value)
        {
            if (value.Alternatives == null)
            {
                yield return value;
                yield break;
            }
            foreach (var alternative in value.Alternatives)
                foreach (var plain in Each(alternative)) yield return plain;
        }

        private static bool AnyVariable(Value value, string name)
        {
            foreach (var plain in Each(value))
                if (plain.Variable == name) return true;
            return false;
        }

        private void Step(Opcode op, Stack<Value> stack)
        {
            int line = op.SourceLine;
            // A function's code pushed just before: the store after it defines the function.
            var delegateLabel = _delegateLabel;
            _delegateLabel = null;

            var push = op as OpcodePush;
            if (push != null)
            {
                stack.Push(Pushed(PushArgument.GetValue(push, null), line));
                return;
            }
            if (op is OpcodePushRelocateLater || op is OpcodePushDelegate)
            {
                _delegateLabel = op is OpcodePushDelegateRelocateLater ? ((OpcodePushRelocateLater)op).DestinationLabel : null;
                stack.Push(new Value());
                return;
            }

            var get = op as OpcodeGetMember; // and OpcodeGetMethod
            if (get != null)
            {
                var receiver = Pop(stack);
                var id = Lower(get.Identifier);
                Suffix(receiver, id, line);
                stack.Push(new Value
                {
                    Suffix = id,
                    OtherVessel = receiver.OtherVessel,
                });
                return;
            }

            var set = op as OpcodeSetMember;
            if (set != null)
            {
                var value = Pop(stack);
                var receiver = Pop(stack);
                Consumed(value, line);
                SetSuffix(receiver, Lower(set.Identifier), value, line);
                return;
            }

            if (op is OpcodeStore || op is OpcodeStoreLocal || op is OpcodeStoreGlobal || op is OpcodeStoreExist)
            {
                var value = Pop(stack);
                var stored = Lower(((OpcodeIdentifierBase)op).Identifier).TrimStart('$');
                if (op is OpcodeStoreGlobal) StoredGlobal(stored, line);
                else if (op.GetType() == typeof(OpcodeStore) && delegateLabel != null) FunctionStored(stored, delegateLabel, line);
                Stored(op, stored, value, line);
                if (stored == "sas" && !(op is OpcodeStoreLocal) && line < _libraryFromLine && !IsFalse(value))
                    Found(line, "SET SAS", ForbiddenList.SasOn);
                return;
            }

            var call = op as OpcodeCall;
            if (call != null)
            {
                var args = new List<Value>();
                while (stack.Count > 0 && !stack.Peek().Marker) args.Insert(0, stack.Pop());
                if (stack.Count > 0) stack.Pop();
                var destination = call.Destination as string;
                if (destination == null && (call.DestinationLabel ?? "").StartsWith("@LR", StringComparison.Ordinal))
                {
                    // RUN, RUNPATH and RUNONCEPATH call kOS's program loader, which reads a file from a volume.
                    foreach (var arg in args) Consumed(arg, line);
                    Found(line, "RUN", ForbiddenList.TouchFiles);
                    stack.Push(new Value());
                }
                else if (destination == null || destination == "<indirect>")
                {
                    var callee = Pop(stack);
                    foreach (var arg in args) Consumed(arg, line);
                    PassedTo(null, args);
                    // A delegate's :CALL returns what some function returns.
                    var delegateCall = callee.Suffix == null || callee.Suffix == "call";
                    stack.Push(new Value { OtherVessel = callee.OtherVessel || (delegateCall && Returned(null)) });
                }
                else stack.Push(Called(destination, args, line));
                return;
            }

            if (op is OpcodeGetIndex)
            {
                Pop(stack);
                var collection = Pop(stack);
                stack.Push(new Value { OtherVessel = collection.OtherVessel });
                return;
            }
            if (op is OpcodeSetIndex)
            {
                Consumed(Pop(stack), line);
                Pop(stack);
                var collection = Pop(stack);
                // SET a:b[c] TO x compiles to push a, push "b" (a literal where getmember belongs), push c, push x, setindex.
                if (collection.IsConstant && collection.Text != null) Found(line, "SET suffix[index]", ForbiddenList.SetSuffixIndex);
                if (collection.OtherVessel) Found(line, "SET [index]", ForbiddenList.OtherVessels);
                return;
            }

            if (op is BinaryOpcode || op is OpcodeLogicAnd || op is OpcodeLogicOr)
            {
                Consumed(Pop(stack), line);
                Consumed(Pop(stack), line);
                stack.Push(new Value());
                return;
            }
            if (op is OpcodeLogicNot || op is OpcodeLogicToBool || op is OpcodeMathNegate || op is OpcodeExists)
            {
                Consumed(Pop(stack), line);
                stack.Push(new Value());
                return;
            }
            if (op is OpcodeEval)
            {
                stack.Push(Pop(stack));
                return;
            }
            if (op is OpcodeDup)
            {
                var top = Pop(stack);
                stack.Push(top);
                stack.Push(top);
                return;
            }
            if (op is OpcodeSwap)
            {
                var a = Pop(stack);
                var b = Pop(stack);
                stack.Push(a);
                stack.Push(b);
                return;
            }
            if (op is OpcodeTestArgBottom || op is OpcodeTestCancelled)
            {
                stack.Push(new Value());
                return;
            }
            var scope = op as OpcodePushScope;
            if (scope != null)
            {
                _frames = Enter(scope.ScopeId, scope.ParentScopeId);
                return;
            }
            if (op is OpcodePopScope)
            {
                var levels = ((OpcodePopScope)op).NumLevels;
                if (_frames != null && _frames.Count >= levels) _frames.RemoveRange(_frames.Count - levels, levels);
                else _frames = null;
                return;
            }
            if (op is OpcodeReturn)
            {
                var value = Pop(stack);
                Consumed(value, line);
                if (!value.OtherVessel) return;
                var function = CurrentFunction();
                if (function == AnyFunction) Learn(ref _anyReturns);
                else Learn(_returnsTainted.Add(function));
                return;
            }
            if (op is OpcodePop || op is OpcodeBranchIfFalse || op is OpcodeBranchIfTrue
                || op is OpcodeWait || op is OpcodeAddTrigger || op is OpcodeRemoveTrigger || op is OpcodeUnset || op is OpcodeJumpStack)
            {
                Consumed(Pop(stack), line);
            }
            // Scopes, labels, NOPs and jumps leave the stack alone.
        }

        private Value Pushed(object argument, int line)
        {
            if (argument != null && argument.GetType().Name == "KOSArgMarkerType") return new Value { Marker = true };
            var raw = argument as string;
            if (raw != null && raw.StartsWith("$", StringComparison.Ordinal))
            {
                var name = Lower(raw.Substring(1));
                if (name.EndsWith("*", StringComparison.Ordinal))
                {
                    // A function pointer (or lock): pointing at a forbidden built-in is using it.
                    Function(name.TrimEnd('*'), name.TrimEnd('*').ToUpperInvariant() + "@", line);
                    return new Value();
                }
                if (name == "archive") Found(line, "ARCHIVE", ForbiddenList.TouchFiles);
                return new Value
                {
                    Variable = name,
                    OtherVessel = name == "target" || Tainted(name),
                };
            }
            // Any other argument is a constant: a raw compiler string, a StringValue, a number, a boolean.
            return new Value
            {
                IsConstant = true,
                Text = argument == null ? null : Convert.ToString(argument, CultureInfo.InvariantCulture),
            };
        }

        private void Suffix(Value receiver, string id, int line)
        {
            string item;
            if (ForbiddenList.Suffixes.TryGetValue(id, out item) && !(_allowPlanningTools && item == ForbiddenList.PlanningTools)
                && !(line >= _libraryFromLine && item == ForbiddenList.BlockInternal))
                Found(line, ":" + id.ToUpperInvariant(), item);
            if (AnyVariable(receiver, "addons") && !ForbiddenList.AllowedAddonSuffixes.Contains(id))
                Found(line, "ADDONS:" + id.ToUpperInvariant(), ForbiddenList.OtherAddons);
            if (receiver.OtherVessel && ForbiddenList.OtherVesselMutators.Contains(id))
                Found(line, ":" + id.ToUpperInvariant() + " on another vessel", ForbiddenList.OtherVessels);
        }

        private void SetSuffix(Value receiver, string id, Value value, int line)
        {
            var what = "SET :" + id.ToUpperInvariant();
            if (AnyVariable(receiver, "config")) Found(line, "SET CONFIG:" + id.ToUpperInvariant(), ForbiddenList.ChangeConfig);
            else if (AnyVariable(receiver, "addons")) Found(line, "SET ADDONS:" + id.ToUpperInvariant(), ForbiddenList.OtherAddons);
            if (id == "activevessel") Found(line, what, ForbiddenList.SwitchVessels);
            if (id == "bootfilename") Found(line, what, ForbiddenList.SetBootFiles);
            if (receiver.OtherVessel) Found(line, what + " on another vessel", ForbiddenList.OtherVessels);
        }

        /// <summary>
        /// A store: LOCAL (and PARAMETER) binds the name in the scope the code is in, GLOBAL in the global scope, and SET where kOS
        /// finds the name: in the innermost scope that has declared it so far, else the global scope. It taints every binding the
        /// name might be (see <see cref="Bindings"/>), and where the reader can't tell the scopes, the name everywhere.
        /// </summary>
        private void Stored(Opcode op, string name, Value value, int line)
        {
            if (AnyVariable(value, "config")) Found(line, "CONFIG held in a variable", ForbiddenList.ChangeConfig);
            if (AnyVariable(value, "addons")) Found(line, "ADDONS held in a variable", ForbiddenList.OtherAddons);
            if (op is OpcodeStoreLocal && _frames != null)
            {
                var scope = _frames.Count == 0 ? Global : _frames[_frames.Count - 1].Id;
                if (_frames.Count > 0) _frames[_frames.Count - 1].Declared.Add(name);
                HashSet<string> names;
                if (!_declared.TryGetValue(scope, out names)) _declared[scope] = names = new HashSet<string>(StringComparer.Ordinal);
                Learn(names.Add(name));
                if (value.OtherVessel) Taint(scope, name);
                return;
            }
            if (!value.OtherVessel) return;
            if (op is OpcodeStoreGlobal)
            {
                Taint(Global, name);
                return;
            }
            if (_frames == null)
            {
                Learn(_taintedEverywhere.Add(name));
                Learn(_taintedNames.Add(name));
                return;
            }
            foreach (var scope in Bindings(name)) Taint(scope, name);
        }

        /// <summary>Whether the variable read here may hold another vessel: any binding kOS might find.</summary>
        private bool Tainted(string name)
        {
            if (_taintedEverywhere.Contains(name)) return true;
            if (!_taintedNames.Contains(name)) return false;
            if (_frames == null) return true;
            foreach (var scope in Bindings(name))
                if (TaintedIn(scope, name)) return true;
            return false;
        }

        /// <summary>
        /// The bindings a name used here may be: the innermost scope that has surely declared it on this path, or else the global
        /// scope; and any scope inside that one that declares it somewhere, since it may have on this path (on one branch, or
        /// further on in an enclosing scope before this code runs).
        /// </summary>
        private List<int> Bindings(string name)
        {
            var sure = _frames.Count - 1;
            while (sure >= 0 && !_frames[sure].Declared.Contains(name)) sure--;
            var bindings = new List<int> { sure >= 0 ? _frames[sure].Id : Global };
            for (var i = sure + 1; i < _frames.Count; i++)
                if (DeclaredIn(_frames[i].Id, name)) bindings.Add(_frames[i].Id);
            return bindings;
        }

        private bool DeclaredIn(int scope, string name)
        {
            HashSet<string> names;
            return _declared.TryGetValue(scope, out names) && names.Contains(name);
        }

        private bool TaintedIn(int scope, string name)
        {
            HashSet<string> names;
            return _tainted.TryGetValue(scope, out names) && names.Contains(name);
        }

        private void Taint(int scope, string name)
        {
            HashSet<string> names;
            if (!_tainted.TryGetValue(scope, out names)) _tainted[scope] = names = new HashSet<string>(StringComparer.Ordinal);
            Learn(names.Add(name));
            Learn(_taintedNames.Add(name));
        }

        /// <summary>The scopes code in scope <paramref name="id"/> is in: its parent's, kept from here when the code is in it.</summary>
        private List<Frame> Enter(int id, int parent)
        {
            var frames = new List<Frame>();
            var at = _frames == null ? -1 : _frames.FindLastIndex(f => f.Id == parent);
            if (at >= 0) frames.AddRange(_frames.GetRange(0, at + 1));
            else
            {
                for (var scope = parent; scope != Global; )
                {
                    frames.Insert(0, new Frame { Id = scope });
                    int up;
                    // A parent the reader never saw: it can't tell which scopes the code is in.
                    if (!_parents.TryGetValue(scope, out up)) return null;
                    scope = up;
                }
            }
            frames.Add(new Frame { Id = id });
            return frames;
        }

        /// <summary>The scopes as a jump leaves them, with the names declared so far.</summary>
        private static List<Frame> Copy(List<Frame> frames)
        {
            if (frames == null) return null;
            var copy = new List<Frame>();
            foreach (var frame in frames) copy.Add(new Frame { Id = frame.Id, Declared = new HashSet<string>(frame.Declared, StringComparer.Ordinal) });
            return copy;
        }

        /// <summary>Where two paths meet: the same scopes, with the names both declared; or null if they differ.</summary>
        private static List<Frame> Meet(List<Frame> a, List<Frame> b)
        {
            if (a == null || b == null || a.Count != b.Count) return null;
            var met = new List<Frame>();
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].Id != b[i].Id) return null;
                var declared = new HashSet<string>(a[i].Declared, StringComparer.Ordinal);
                declared.IntersectWith(b[i].Declared);
                met.Add(new Frame { Id = a[i].Id, Declared = declared });
            }
            return met;
        }

        /// <summary>The function whose code this is: the innermost function scope the code is in.</summary>
        private int CurrentFunction()
        {
            if (_frames == null) return AnyFunction;
            for (var i = _frames.Count - 1; i >= 0; i--)
                if (_functionScopes.Contains(_frames[i].Id)) return _frames[i].Id;
            return AnyFunction;
        }

        /// <summary>A value popped from an empty stack: in a function, one of its parameters.</summary>
        private Value Parameter()
        {
            var function = CurrentFunction();
            return new Value
            {
                OtherVessel = _anyParameters || (function == AnyFunction ? _parametersTainted.Count > 0 : _parametersTainted.Contains(function)),
            };
        }

        /// <summary>A function or lock defined under <paramref name="id"/> ("name*"), whose code starts at <paramref name="label"/>.</summary>
        private void Defined(string id, string label)
        {
            int scope;
            if (!_scopeAtLabel.TryGetValue(label, out scope) || !_functionScopes.Contains(scope)) scope = AnyFunction;
            HashSet<int> scopes;
            if (!_functions.TryGetValue(id, out scopes)) _functions[id] = scopes = new HashSet<int>();
            Learn(scopes.Add(scope));
        }

        /// <summary>
        /// Arguments to a user function, "name*" (null: a delegate or a name resolved at run time). Another vessel among them
        /// reaches that function's parameters, or, when the reader can't name the function, every function's.
        /// </summary>
        private void PassedTo(string function, List<Value> args)
        {
            var passed = false;
            foreach (var arg in args) passed |= arg.OtherVessel;
            if (!passed) return;
            var scopes = Named(function);
            if (scopes == null)
            {
                Learn(ref _anyParameters);
                return;
            }
            foreach (var scope in scopes) Learn(_parametersTainted.Add(scope));
        }

        /// <summary>Whether a call to the function ("name*"; null: one the reader can't name) may return another vessel.</summary>
        private bool Returned(string function)
        {
            var scopes = Named(function);
            if (scopes == null) return _anyReturns || _returnsTainted.Count > 0;
            foreach (var scope in scopes)
                if (_returnsTainted.Contains(scope)) return true;
            return false;
        }

        /// <summary>The scopes of the functions stored under "name*", or null if the reader can't name every one: any may be it.</summary>
        private HashSet<int> Named(string function)
        {
            HashSet<int> scopes;
            if (function == null || !_functions.TryGetValue(function, out scopes) || scopes.Contains(AnyFunction)) return null;
            return scopes;
        }

        private void Learn(bool added)
        {
            if (added) _learned++;
        }

        private void Learn(ref bool flag)
        {
            if (flag) return;
            flag = true;
            _learned++;
        }

        /// <summary>
        /// A file-level function defined under <paramref name="id"/> ("name*"). Each is stored as the program starts, on the first
        /// statement's line, and kOS keeps the first definition of a name, so a Mission Script's function replaces the library's of
        /// the same name, swapping the script's own code into a block round a block-internal call (ADR 0007): a land_plan that keeps
        /// land's site and fails. It is refused where its code starts (the line of the label its definition pushes).
        /// </summary>
        private void FunctionStored(string id, string label, int line)
        {
            if (!IsBundle || !id.EndsWith("*", StringComparison.Ordinal)) return;
            var name = id.TrimEnd('*');
            int at;
            if (!_labelLines.TryGetValue(label, out at)) at = line;
            if (at < _libraryFromLine && ForbiddenList.LibraryFunctions.Value.Contains(name))
                Found(at, "FUNCTION " + name.ToUpperInvariant(), ForbiddenList.RedefineLibrary);
        }

        /// <summary>
        /// A lock stored under <paramref name="id"/> ("name*") by the Mission Script, when the Core Library has a function of that
        /// name. A lock is stored globally when its line runs, over the library's function, so it too would swap the script's own
        /// code into a block (see <see cref="FunctionStored"/>).
        /// </summary>
        private void StoredGlobal(string id, int line)
        {
            if (!IsBundle || line >= _libraryFromLine || !id.EndsWith("*", StringComparison.Ordinal)) return;
            var name = id.TrimEnd('*');
            if (ForbiddenList.LibraryFunctions.Value.Contains(name)) Found(line, name.ToUpperInvariant() + "*", ForbiddenList.RedefineLibrary);
        }

        /// <summary>A value used as an argument or operand: CONFIG and ADDONS may only be read through a suffix.</summary>
        private void Consumed(Value value, int line)
        {
            if (AnyVariable(value, "config")) Found(line, "CONFIG passed on", ForbiddenList.ChangeConfig);
            if (AnyVariable(value, "addons")) Found(line, "ADDONS passed on", ForbiddenList.OtherAddons);
        }

        private Value Called(string destination, List<Value> args, int line)
        {
            var name = Name(destination);
            // A user function or lock is called as "$name*"; any other name that isn't a built-in is a delegate in a variable.
            var raw = Lower(destination);
            var function = raw.StartsWith("$", StringComparison.Ordinal) && raw.EndsWith("*", StringComparison.Ordinal) ? raw.Substring(1) : null;
            var user = !ForbiddenList.BuiltIns.Contains(name);
            // kOS writes a built-in as "name()", a function as "$name*", and any other name bare: a delegate in a variable if the
            // script ever stores that name, else a function nothing defines, which kOS only finds out when the line runs (#928).
            var bare = !raw.StartsWith("$", StringComparison.Ordinal) && !raw.EndsWith("()", StringComparison.Ordinal);
            if (_recording && bare && line < _libraryFromLine && !_storedNames.Contains(raw))
            {
                var undefinedCall = "line " + line.ToString(CultureInfo.InvariantCulture) + ": " + name;
                if (!_undefinedCalls.Contains(undefinedCall)) _undefinedCalls.Add(undefinedCall);
            }
            foreach (var arg in args) Consumed(arg, line);
            if (user) PassedTo(function, args);
            if (name == "makebuiltindelegate")
            {
                var constant = args.Count == 1;
                if (constant)
                    foreach (var plain in Each(args[0]))
                        constant &= plain.IsConstant && plain.Text != null;
                if (!constant)
                {
                    Found(line, "MAKEBUILTINDELEGATE", ForbiddenList.ComputedDelegate);
                    return new Value();
                }
                foreach (var plain in Each(args[0])) Function(Lower(plain.Text), plain.Text.ToUpperInvariant() + "@", line);
                return new Value();
            }
            Function(name, name.ToUpperInvariant(), line);
            if ((name == "buildlist" || name == "printlist") && args.Count > 0)
                foreach (var plain in Each(args[0]))
                    if (plain.IsConstant && plain.Text != null && ForbiddenList.FileListKinds.Contains(plain.Text))
                        Found(line, "LIST " + plain.Text.ToUpperInvariant(), ForbiddenList.TouchFiles);
            var targets = name == "buildlist" && args.Count > 0 && MayBeText(args[0], "targets");
            return new Value { OtherVessel = name == "vessel" || targets || (user && Returned(function)) };
        }

        private void Function(string name, string what, int line)
        {
            string item;
            if (ForbiddenList.Functions.TryGetValue(name, out item)) Found(line, what, item);
            if (name == "stage" && line < _libraryFromLine) Found(line, what, ForbiddenList.BareStage);
        }

        private void Found(int line, string what, string item)
        {
            if (!_recording) return;
            var finding = "line " + line.ToString(CultureInfo.InvariantCulture) + ": " + what + " (" + item + ")";
            if (_seen.Add(finding)) _findings.Add(finding);
        }

        private Value Pop(Stack<Value> stack)
        {
            return stack.Count > 0 ? stack.Pop() : Parameter();
        }

        /// <summary>The value is certainly the constant false (anything else may switch SAS on).</summary>
        private static bool IsFalse(Value value)
        {
            foreach (var plain in Each(value))
                if (!plain.IsConstant || !string.Equals(plain.Text, "False", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>The value may be that constant.</summary>
        private static bool MayBeText(Value value, string text)
        {
            foreach (var plain in Each(value))
                if (plain.IsConstant && string.Equals(plain.Text, text, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>A call target as written: "copypath()" (built-in), "vessel" (resolved at run time) or "$f*" (user function).</summary>
        private static string Name(string destination)
        {
            var name = Lower(destination).TrimStart('$');
            if (name.EndsWith("()", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            return name.TrimEnd('*');
        }

        private static string Lower(string s)
        {
            return (s ?? "").ToLowerInvariant();
        }
    }
}
