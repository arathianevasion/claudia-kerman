// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.ScriptCheck.Tests
{
    /// <summary>
    /// The real Script Check, compiling with kOS.Safe: a script using each forbidden item fails naming that
    /// item (the list is the spec's, user story 139), and each allowed item passes.
    /// </summary>
    public sealed class KosScriptCheckTests
    {
        private readonly IScriptCheck _check = new KosScriptCheck();

        private void AssertForbidden(string item, string script)
        {
            var result = _check.Check(script);
            Assert.True(result.CompileError == null, result.CompileError);
            Assert.False(result.Passed, "passed: " + script);
            Assert.Contains(result.Forbidden, f => f.EndsWith("(" + item + ")"));
        }

        private void AssertAllowed(string script)
        {
            var result = _check.Check(script);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()) + "\n" + script);
        }

        // ---- Lints: warn, never refuse ----

        [Theory]
        [InlineData("circularize().\nfrom {local i is 0.} until i > 20 step {set i to i + 1.} do {\n  local p is latlng(i, 10).\n  print p:terrainheight.\n}\n", "landing-site search")]
        [InlineData("until ship:orbit:nextpatch:periapsis > 30000 {\n  set nextnode:prograde to nextnode:prograde + 1.\n}\n", "next patch")]
        public void AReinventionLintWarnsAndTheScriptStillPasses(string script, string warning)
        {
            var result = _check.Check(script);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Contains(result.Warnings, w => w.Contains(warning));
            Assert.DoesNotContain(result.Problems, p => p.Contains(warning));
        }

        [Fact]
        public void AnOrdinaryScriptHasNoWarnings()
        {
            var result = _check.Check("local res is lexicon(\"success\", true).\nuntil res:success { print 1. }\nlocal p is latlng(1, 2).\n");
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public void TheBaseLayersWorkedExamplePassesWithNoWarnings()
        {
            var result = _check.Check(ClaudiaKermin.Core.Missions.MissionPrompts.WorkedExampleScript);
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Empty(result.Warnings);
        }

        // ---- The stack-drop lint (#557): reentry after return, transfer_to or coast with nothing dropped ----

        // b5f3's "Ascend, return and reentry", as Claudia wrote it.
        private const string B5f3AscendReturnReentry =
            "local res is ascend(lexicon(\"target_alt\", 30000, \"target_inc\", 0, \"stage_limit\", 1)).\n"
            + "if res:success {\n"
            + "  set res to return(lexicon(\"target_pe\", 35000, \"stage_limit\", 1)).\n"
            + "  if res:success {\n"
            + "    if ship:body:name <> \"Kerbin\" {\n"
            + "      set res to coast(lexicon(\"until\", time:seconds + ship:orbit:nextpatcheta + 60)).\n"
            + "    }\n"
            + "    if res:success { set res to reentry(lexicon(\"stage_limit\", 1)). }\n"
            + "  }\n"
            + "}\n"
            + "unlock steering.\n"
            + "unlock throttle.\n"
            + "set ship:control:pilotmainthrottle to 0.\n"
            + "addons:claudia:report(res).";

        // b5f3's "Lower periapsis, drop stack, reenter".
        private const string B5f3LowerDropReenter =
            "local res is tune_pe(lexicon(\"value\", 27000, \"tolerance\", 1500, \"max_dv\", 60, \"stage_limit\", 1)).\n"
            + "if res:success {\n"
            + "  set res to drop_parts(lexicon(\"parts\", list(\"Decoupler.1\"))).\n"
            + "  if res:success { set res to reentry(lexicon(\"stage_limit\", 1, \"max_duration\", 12000)). }\n"
            + "}\n"
            + "unlock steering.\n"
            + "unlock throttle.\n"
            + "set ship:control:pilotmainthrottle to 0.\n"
            + "addons:claudia:report(res).";

        // 44e9's "Return to Kerbin and reentry".
        private const string Ksp44e9ReturnReentry =
            "local k is 0.\n"
            + "until not hasnode or k > 5 {\n"
            + "  remove nextnode.\n"
            + "  set k to k + 1.\n"
            + "}\n"
            + "local res is return(lexicon(\"target_pe\", 35000)).\n"
            + "if res:success {\n"
            + "  set res to reentry().\n"
            + "}\n"
            + "addons:claudia:report(res).";

        private const string StackLint = "with no drop named";

        [Theory]
        [InlineData(B5f3AscendReturnReentry, "line 8: reentry after return")]
        [InlineData(Ksp44e9ReturnReentry, "line 8: reentry after return")]
        public void The_stack_drop_lint_warns_on_b5f3s_and_44e9s_return_then_reentry_and_the_script_still_passes(string script, string warning)
        {
            // As the mod checks it: the Mission Script with the Core Library bundled after it, whose lines it skips.
            var result = _check.Check(CoreLibrary.Bundle(script));
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            var lint = Assert.Single(result.Warnings, w => w.Contains(StackLint));
            Assert.StartsWith(warning, lint);
            Assert.Contains("separate", lint);
            Assert.DoesNotContain("drop_parts", lint);
            Assert.DoesNotContain(result.Problems, p => p.Contains(StackLint));
        }

        [Fact]
        public void The_stack_drop_lint_is_quiet_on_b5f3s_lower_periapsis_drop_stack_reenter()
        {
            Assert.DoesNotContain(_check.Check(CoreLibrary.Bundle(B5f3LowerDropReenter)).Warnings, w => w.Contains(StackLint));
        }

        [Theory]
        [InlineData("local res is transfer_to(lexicon(\"target\", \"Kerbin\")).\nset res to reentry().\n", true)]
        [InlineData("local res is coast(lexicon(\"until\", time:seconds + 60)).\nset res to reentry().\n", true)]
        [InlineData("local res is return().\nset res to reentry(lexicon(\"separate\", \"none\")).\n", true)]
        // Something leaves before the atmosphere: drop_parts, deorbit's separate, or reentry's own.
        [InlineData("local res is return().\nset res to drop_parts(lexicon(\"parts\", \"Decoupler.1\")).\nset res to reentry().\n", false)]
        [InlineData("local res is return().\nset res to deorbit(lexicon(\"target\", \"ksc\", \"separate\", 1)).\nset res to reentry().\n", false)]
        [InlineData("local res is return().\nset res to reentry(lexicon(\"separate\", \"auto\")).\n", false)]
        [InlineData("local res is return().\nset res to reentry(lexicon(\"separate\", 2)).\n", false)]
        // No return, transfer_to or coast before it; a reentry before the return; and a comment.
        [InlineData("local res is reentry(lexicon(\"max_duration\", 20000)).\n", false)]
        [InlineData("local res is reentry().\nset res to return().\n", false)]
        [InlineData("// return(lexicon()) then reentry()\nlocal res is reentry().\n", false)]
        public void The_stack_drop_lint_fires_only_for_a_reentry_after_a_leg_home_with_nothing_dropped(string script, bool fires)
        {
            var warnings = _check.Check(script).Warnings;
            Assert.Equal(fires, warnings.Any(w => w.Contains(StackLint)));
        }

        // ---- The drop-before-reentry lint (#608): drop_parts as the block call just before reentry ----

        private const string DropLint = "drop_parts just before reentry";

        // a107's "Return to Kerbin": the drop that took the Terrier stack with the periapsis at 601 km.
        private const string A107ReturnDropReentry =
            "local res is return(lexicon(\"target_pe\", 35000)).\n"
            + "if res:success {\n"
            + "  set res to drop_parts(lexicon(\"parts\", \"RCSFuelTank\")).\n"
            + "  if res:success { set res to reentry(). }\n"
            + "}\n"
            + "addons:claudia:report(res).";

        [Theory]
        [InlineData(A107ReturnDropReentry, "line 3: ")]
        [InlineData(B5f3LowerDropReenter, "line 3: ")]
        public void The_drop_lint_warns_on_drop_parts_then_reentry_in_a_bundled_mission_script_and_the_script_still_passes(string script, string line)
        {
            var result = _check.Check(CoreLibrary.Bundle(script));
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            var lint = Assert.Single(result.Warnings, w => w.Contains(DropLint));
            Assert.StartsWith(line, lint);
            Assert.Contains("reentry's separate", lint);
            Assert.DoesNotContain(result.Problems, p => p.Contains(DropLint));
        }

        [Fact]
        public void The_drop_lint_is_quiet_on_a_return_then_reentry_that_follows_the_stack_drop_lints_advice()
        {
            // The stack-drop lint names reentry's separate, not drop_parts: a return then reentry with the separate it asks for.
            const string followsAdvice =
                "local res is return(lexicon(\"target_pe\", 35000)).\n"
                + "if res:success {\n"
                + "  set res to reentry(lexicon(\"separate\", \"auto\")).\n"
                + "}\n"
                + "addons:claudia:report(res).";
            var result = _check.Check(CoreLibrary.Bundle(followsAdvice));
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.DoesNotContain(result.Warnings, w => w.Contains(StackLint));
            Assert.DoesNotContain(result.Warnings, w => w.Contains(DropLint));
        }

        [Fact]
        public void The_drop_lint_never_warns_on_library_lines()
        {
            Assert.DoesNotContain(_check.Check(CoreLibrary.Bundle("")).Warnings, w => w.Contains(DropLint));
            Assert.DoesNotContain(_check.Check(CoreLibrary.Bundle(Ksp44e9ReturnReentry)).Warnings, w => w.Contains(DropLint));
        }

        [Theory]
        [InlineData("local res is drop_parts(lexicon(\"parts\", \"x\")).\nset res to reentry().\n", true)]
        [InlineData("local res is DROP_PARTS(lexicon(\"parts\", \"x\")).\nset res to Reentry ().\n", true)]
        // Another block between them, reentry first, a comment, or the names inside a string.
        [InlineData("local res is drop_parts(lexicon(\"parts\", \"x\")).\nset res to tune_pe(lexicon(\"value\", 30000)).\nset res to reentry().\n", false)]
        [InlineData("local res is reentry().\nset res to drop_parts(lexicon(\"parts\", \"x\")).\n", false)]
        [InlineData("// drop_parts(lexicon()) then reentry()\nlocal res is reentry().\n", false)]
        [InlineData("print \"drop_parts(x) reentry(y)\".\nlocal res is reentry().\n", false)]
        public void The_drop_lint_fires_only_when_reentry_is_the_next_block_call_after_drop_parts(string script, bool fires)
        {
            Assert.Equal(fires, _check.Check(script).Warnings.Any(w => w.Contains(DropLint)));
        }

        // ---- The descent-loop lint (#627): a throttle lock in an until loop that tests alt:radar or LANDED ----

        private const string DescentLint = "a hand-written descent loop";

        // The Duna benchmark's loop shape.
        private const string DunaDescentLoop =
            "lock steering to srfretrograde.\n"
            + "until ship:status = \"LANDED\" {\n"
            + "  lock throttle to 1.\n"
            + "  wait 0.1.\n"
            + "}\n"
            + "lock throttle to 0.";

        [Theory]
        [InlineData("until alt:radar < 50 {\n  lock throttle to 0.5.\n}\n", true)]
        [InlineData("until LANDED { if ship:verticalspeed < -5 { lock throttle to 1. } }\n", true)]
        // No throttle lock, a condition that tests neither, a comment, or a string.
        [InlineData("until alt:radar < 50 {\n  wait 0.1.\n}\n", false)]
        [InlineData("until ship:altitude > 70000 {\n  lock throttle to 1.\n}\n", false)]
        [InlineData("// until LANDED { lock throttle to 1. }\nprint 1.\n", false)]
        [InlineData("print \"until LANDED { lock throttle to 1. }\".\n", false)]
        public void The_descent_loop_lint_fires_on_a_throttle_lock_in_an_until_loop_that_tests_radar_or_landed(string script, bool fires)
        {
            var result = _check.Check(script);
            Assert.Equal(fires, result.Warnings.Any(w => w.Contains(DescentLint)));
            if (fires) Assert.Contains(result.Warnings, w => w.Contains("use reentry for a landing through air"));
        }

        [Fact]
        public void The_descent_loop_lint_warns_on_the_benchmarks_loop_and_never_on_the_bundled_core_library()
        {
            Assert.DoesNotContain(_check.Check(CoreLibrary.Bundle("")).Warnings, w => w.Contains(DescentLint));
            var result = _check.Check(CoreLibrary.Bundle(DunaDescentLoop));
            Assert.True(result.Passed, string.Join("; ", result.Problems.ToArray()));
            Assert.Single(result.Warnings, w => w.Contains(DescentLint));
        }

        // ---- Forbidden: one theory per item of the spec's list ----

        [Theory]
        [InlineData("kuniverse:reverttolaunch().")]
        [InlineData("kuniverse:reverttoeditor().")]
        [InlineData("kuniverse:revertto(\"VAB\").")]
        public void Revert(string script) { AssertForbidden("revert", script); }

        [Theory]
        [InlineData("kuniverse:quickload().")]
        [InlineData("kuniverse:quickloadfrom(\"before burn\").")]
        public void Quickload(string script) { AssertForbidden("quickload", script); }

        [Theory]
        [InlineData("kuniverse:quicksave().")]
        [InlineData("kuniverse:quicksaveto(\"before burn\").")]
        public void Quicksave(string script) { AssertForbidden("quicksave", script); }

        [Theory]
        [InlineData("set kuniverse:activevessel to vessel(\"Probe\").")]
        [InlineData("kuniverse:forcesetactivevessel(vessel(\"Probe\")).")]
        [InlineData("vessel(\"Probe\"):forceactive().")]
        public void SwitchVessels(string script) { AssertForbidden("switch vessels", script); }

        [Theory]
        [InlineData("kuniverse:launchcraft(kuniverse:getcraft(\"Lander\", \"VAB\")).")]
        [InlineData("kuniverse:launchcraftfrom(kuniverse:getcraft(\"Lander\", \"VAB\"), \"LaunchPad\").")]
        public void LaunchCraft(string script) { AssertForbidden("launch craft", script); }

        [Theory]
        [InlineData("kuniverse:pause().")]
        public void Pause(string script) { AssertForbidden("pause", script); }

        [Theory]
        [InlineData("set warpmode to \"PHYSICS\".")]
        [InlineData("set warpmode to claudia_mode.")]
        [InlineData("set kuniverse:timewarp:mode to \"PHYSICS\".")]
        [InlineData("set kuniverse:timewarp:mode to claudia_mode.")]
        [InlineData("local tw is kuniverse:timewarp.\nset tw:mode to \"PHYSICS\".")]
        [InlineData("set warpmode to choose \"PHYSICS\" if ship:altitude < 70000 else \"RAILS\".")]
        public void PhysicsWarpIsNoLongerRefusedByTheScriptCheck(string script)
        {
            // Whether the craft is on a Coast is known only at run time (ADR 0008); the runtime gate decides.
            AssertAllowed(script);
        }

        [Theory]
        [InlineData("copypath(\"0:/a.ks\", \"1:/a.ks\").")]
        [InlineData("movepath(\"1:/a.ks\", \"1:/b.ks\").")]
        [InlineData("deletepath(\"1:/a.ks\").")]
        [InlineData("log \"x\" to \"0:/out.txt\".")]
        [InlineData("run other.")]
        [InlineData("runpath(\"0:/other.ks\").")]
        [InlineData("runoncepath(\"0:/other.ks\").")]
        [InlineData("writejson(list(1), \"1:/x.json\").")]
        [InlineData("print readjson(\"1:/x.json\").")]
        [InlineData("local f is open(\"1:/x.txt\").")]
        [InlineData("create(\"1:/x.txt\").")]
        [InlineData("createdir(\"1:/d\").")]
        [InlineData("print exists(\"1:/x.txt\").")]
        [InlineData("list files.")]
        [InlineData("list volumes in vols.")]
        [InlineData("switch to 0.")]
        [InlineData("cd(\"1:/d\").")]
        [InlineData("compile \"0:/a.ks\" to \"1:/a.ksm\".")]
        [InlineData("edit \"x.ks\".")]
        [InlineData("print archive:files.")]
        [InlineData("print core:volume:freespace.")]
        [InlineData("print volume(0):name.")]
        [InlineData("local c is copypath@.\nc:call(\"0:/a\", \"1:/a\").")]
        public void TouchFiles(string script) { AssertForbidden("touch files", script); }

        [Theory]
        [InlineData("set config:ipu to 2000.")]
        [InlineData("local c is config.\nset c:ipu to 2000.")]
        public void ChangeKosConfig(string script) { AssertForbidden("change kOS config", script); }

        [Theory]
        [InlineData("reboot.")]
        [InlineData("shutdown.")]
        [InlineData("processor(\"second\"):deactivate().")]
        public void RebootOrShutDownProcessors(string script) { AssertForbidden("reboot or shut down processors", script); }

        [Theory]
        [InlineData("set core:bootfilename to \"boot.ks\".")]
        [InlineData("set processor(\"second\"):bootfilename to \"boot.ks\".")]
        public void SetBootFiles(string script) { AssertForbidden("set boot files", script); }

        [Theory]
        [InlineData("print addons:rt:available.")]
        [InlineData("addons:tr:settarget(body(\"Mun\")).")]
        [InlineData("local a is addons.\nprint a:claudia.")]
        public void UseAddonsOtherThanClaudias(string script) { AssertForbidden("use addons other than Claudia's", script); }

        [Theory]
        [InlineData("vessel(\"Probe\"):connection:sendmessage(\"hi\").")]
        [InlineData("target:connection:sendmessage(\"hi\").")]
        [InlineData("vessel(\"Probe\"):partstagged(\"dish\")[0]:getmodule(\"ModuleRTAntenna\"):doevent(\"activate\").")]
        [InlineData("local p is vessel(\"Probe\").\np:parts[0]:getmodule(\"ModuleLight\"):doaction(\"toggle light\", true).")]
        [InlineData("set vessel(\"Probe\"):shipname to \"Renamed\".")]
        [InlineData("set vessel(\"Probe\"):control:pilotmainthrottle to 1.")]
        [InlineData("list targets in t.\nt[0]:parts[0]:getmodule(\"ModuleLight\"):doevent(\"lights on\").")]
        public void ChangeOrMessageOtherVessels(string script) { AssertForbidden("change or message other vessels", script); }

        // ADR 0013: the Target Vessel takes no new rule. Reading TARGET and SET TARGET TO pass; changing it stays refused.
        [Theory]
        [InlineData("set target to \"Kerbin Station Alpha\".\nprint target:orbit:apoapsis.")]
        [InlineData("set target to vessel(\"Kerbin Station Alpha\").\nlocal ports is target:dockingports.\nprint ports:length.")]
        [InlineData("print target:partstagged(\"fore\")[0]:nodetype.\nprint target:position:mag.")]
        public void ReadingTheTargetAndSettingItPass(string script) { AssertAllowed(script); }

        [Theory]
        [InlineData("target:partstagged(\"fore\")[0]:getmodule(\"ModuleDockingNode\"):doevent(\"undock\").")]
        [InlineData("set target:shipname to \"Renamed\".")]
        public void ChangingTheTargetStaysRefused(string script) { AssertForbidden("change or message other vessels", script); }

        [Theory]
        [InlineData("local g is gui(200).\ng:show().")]
        public void OpenGuiWindows(string script) { AssertForbidden("open GUI windows", script); }

        [Theory]
        [InlineData("set a:b[1] to 2.", 1)]
        [InlineData("local a is lexicon().\nset a:b:c[\"k\"] to 2.", 2)]
        public void SetOfASuffixIndexIsRefusedWithItsLine(string script, int line)
        {
            var result = _check.Check(script);
            Assert.False(result.Passed);
            Assert.Contains(result.Forbidden, f => f.StartsWith("line " + line + ": ") && f.Contains("assign through a local"));
        }

        [Theory]
        [InlineData("local x is a:b.\nset x[1] to 2.")]
        [InlineData("local x is lexicon().\nset x[1] to 2.")]
        [InlineData("print a:b[1].")]
        [InlineData("local res is lexicon().\nset res[\"k\"] to 2.")]
        [InlineData("local res is lexicon().\nset res[\"k\"] to status = \"LANDED\" or status = \"SPLASHED\".")]
        [InlineData("local res is lexicon().\nset res[\"k\"] to status = \"LANDED\".")]
        public void SetOfALocalsIndexAndReadsOfASuffixIndexPass(string script) { AssertAllowed(script); }

        [Fact]
        public void TheCoreLibraryBundleHasNoSetOfASuffixIndex()
        {
            AssertAllowed(CoreLibrary.Bundle(""));
        }

        [Theory]
        [InlineData("debugfreezegame(1).")]
        public void FreezeTheGame(string script) { AssertForbidden("freeze the game", script); }

        [Theory]
        [InlineData("local d is makebuiltindelegate(ship:name).")]
        public void MakeBuiltinDelegateWithAComputedName(string script) { AssertForbidden("makebuiltindelegate without a constant argument", script); }

        [Fact]
        public void MakeBuiltinDelegateOfAForbiddenFunctionIsThatFunction()
        {
            AssertForbidden("touch files", "local d is makebuiltindelegate(\"deletepath\").");
        }

        // ---- Allowed ----

        [Fact]
        public void ReadingTheStageIsAllowed() { AssertAllowed("wait until stage:ready.\nprint stage:number."); }

        // #537: a bare stage. threw away a Swivel stage with its fuel aboard; the Core Library's own staging is still allowed.
        private const string BareStageHint = "a bare stage. can throw away a stage that still holds fuel: drop parts with drop_parts, or set a block's stage_limit for its staging";

        [Theory]
        [InlineData("stage.")]
        [InlineData("print 1.\nif ship:altitude > 10 { stage. }")]
        [InlineData("until false {\n  if stage:liquidfuel < 0.1 { stage. }\n  wait 0.1.\n}\n")]
        [InlineData("local d is stage@.\nd().")]
        public void ABareStageIsRefusedWithItsLineAndTheHint(string script)
        {
            AssertForbidden(BareStageHint, script);
            AssertForbidden(BareStageHint, CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheRefusalNamesTheLine()
        {
            var result = _check.Check("print 1.\nprint 2.\nstage.\n");
            Assert.Contains(result.Forbidden, f => f.StartsWith("line 3: STAGE"));
        }

        [Fact]
        public void TheCoreLibrarysOwnStagingStillPasses()
        {
            Assert.Matches(@"(?m)^\s*stage\.", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("local res is exec_node(lexicon()).\naddons:claudia:report(res)."));
        }

        [Fact]
        public void ActionGroups() { AssertAllowed("toggle ag1.\nag2 on.\ngear on.\nset brakes to true.\nlights off.\nrcs on.\nsas off."); }

        [Fact]
        public void DoEventOnOwnParts()
        {
            AssertAllowed("ship:partstagged(\"chute\")[0]:getmodule(\"ModuleParachute\"):doevent(\"deploy chute\").\n"
                + "local p is ship:partstagged(\"light\")[0].\np:getmodule(\"ModuleLight\"):doevent(\"lights on\").");
        }

        [Fact]
        public void DoActionOnOwnParts()
        {
            AssertAllowed("core:part:getmodule(\"ModuleLight\"):doaction(\"toggle light\", true).\n"
                + "for p in ship:partstagged(\"dish\") { p:getmodule(\"ModuleDeployableAntenna\"):doaction(\"extend antenna\", true). }");
        }

        [Fact]
        public void RailsWarp()
        {
            AssertAllowed("set warpmode to \"RAILS\".\nset warp to 3.\nset kuniverse:timewarp:mode to \"RAILS\".\n"
                + "kuniverse:timewarp:warpto(time:seconds + 600).\nwarpto(time:seconds + 600).\nkuniverse:timewarp:cancelwarp().\nset warp to 0.");
        }

        [Fact]
        public void ManeuverNodes()
        {
            AssertAllowed("local n is node(time:seconds + 600, 0, 0, 120).\nadd n.\nset n:prograde to 130.\n"
                + "print nextnode:deltav:mag.\nlock steering to nextnode:burnvector.\nremove nextnode.");
        }

        [Fact]
        public void HudText() { AssertAllowed("hudtext(\"Claudia: burn in 10 s\", 5, 2, 15, white, false)."); }

        [Fact]
        public void VecDraw()
        {
            AssertAllowed("local arrow is vecdraw(v(0,0,0), ship:velocity:orbit, red, \"velocity\", 1, true).\n"
                + "set arrow:show to false.\nclearvecdraws().");
        }

        [Fact]
        public void ReadingOtherVessels()
        {
            AssertAllowed("local probe is vessel(\"Probe\").\nprint probe:altitude.\nprint probe:orbit:apoapsis.\n"
                + "print vessel(\"Probe\"):partstagged(\"dish\"):length.\nprint probe:parts[0]:getmodule(\"ModuleLight\"):hasevent(\"lights on\").\n"
                + "list targets in t.\nprint t[0]:name.\nprint (probe:position - ship:position):mag.");
        }

        [Fact]
        public void TargetingBodies() { AssertAllowed("set target to body(\"Mun\").\nprint target:name.\nprint target:orbit:inclination."); }

        [Fact]
        public void MakeBuiltinDelegateWithAConstantArgument()
        {
            AssertAllowed("local d is makebuiltindelegate(\"round\").\nprint d:call(1.25).\nlocal e is print@.\ne:call(\"x\").");
        }

        [Fact]
        public void ReportingTheScriptResultThroughClaudiasAddon()
        {
            AssertAllowed("if addons:available(\"claudia\") { addons:claudia:report(lexicon(\"success\", true, \"reason\", \"done\")). }");
        }

        [Theory]
        [InlineData("local p is addons:claudia:planningtool(\"predict_burn\", lexicon(\"burns\", list())).")]
        [InlineData("local c is addons:claudia.\nprint c:planningtool(\"predict_burn\", lexicon()).")]
        [InlineData("print addons:claudia:PlanningTool(\"stage_performance\", lexicon(\"situation\", \"vacuum\")):stages.")]
        public void CallPlanningToolsFromAMissionScript(string script) { AssertForbidden("call Planning Tools", script); }

        [Fact]
        public void APlanningToolCallPassesOnlyTheBlockTestsCheck()
        {
            var script = "local p is addons:claudia:planningtool(\"predict_burn\", lexicon(\"burns\", list())).\nprint p.";
            Assert.True(new KosScriptCheck(forToolTests: true).Check(script).Passed);
            Assert.False(new KosScriptCheck().Check(script).Passed);
            // The block tests' check still refuses everything else on the list.
            var quicksave = new KosScriptCheck(forToolTests: true).Check("kuniverse:quicksave().");
            Assert.Contains(quicksave.Forbidden, f => f.EndsWith("(quicksave)"));
        }

        [Fact]
        public void EveryToolScenarioScriptBundledWithTheLibraryPassesTheBlockTestsCheckAndNoMissionCheck()
        {
            var tools = Directory.GetDirectories(Path.Combine(RepoRoot(), "tests", "tools"));
            Assert.NotEmpty(tools);
            foreach (var dir in tools)
            {
                var script = CoreLibrary.Bundle(File.ReadAllText(Path.Combine(dir, "test.ks")));
                var asTest = new KosScriptCheck(forToolTests: true).Check(script);
                Assert.True(asTest.Passed, dir + ": " + string.Join("; ", asTest.Problems.ToArray()));
                Assert.Contains(_check.Check(script).Forbidden, f => f.EndsWith("(call Planning Tools)"));
            }
        }

        // ---- Building Block options (#606): a call the block would refuse at once fails, before anything flies ----

        [Fact]
        public void A2d0cTransferMissingTargetPeFailsAndItsRetryPasses()
        {
            var failed = _check.Check(CoreLibrary.Bundle("local res is transfer_to(lexicon(\"target\", \"Mun\", \"stage_limit\", 3)).\nprint res.\n"));
            Assert.Null(failed.CompileError);
            Assert.False(failed.Passed);
            var problem = Assert.Single(failed.Problems);
            Assert.Equal("line 1: transfer_to: missing required option target_pe; options: target*, target_pe*, window, correction (* required), plus the common options", problem);

            AssertAllowed(CoreLibrary.Bundle("local res is transfer_to(lexicon(\"target\", \"Mun\", \"target_pe\", 30000, \"stage_limit\", 3)).\nprint res.\n"));
        }

        [Fact]
        public void AnUnknownBlockOptionFailsNamingTheKey()
        {
            var result = _check.Check(CoreLibrary.Bundle("transfer_to(lexicon(\"target\",\"Mun\",\"target_pe\",30000,\"pe\",30000))."));
            Assert.False(result.Passed);
            Assert.Contains(result.BlockOptions, p => p.StartsWith("line 1: transfer_to: unknown option pe;"));
        }

        [Fact]
        public void ABlockTestMayPassOptionsTheBlockRefuses()
        {
            Assert.True(new KosScriptCheck(forToolTests: true).Check(CoreLibrary.Bundle("print coast(lexicon()).")).Passed);
        }

        // ---- Block-internal calls (ADR 0006, ADR 0007): the Core Library's, never a Mission Script's ----

        [Theory]
        [InlineData("local s is addons:claudia:landingsite(lexicon(\"biome\", \"Midlands\")).")]
        [InlineData("local c is addons:claudia.\nprint c:landingsite(lexicon()).")]
        [InlineData("local f is addons:claudia:LandingSite@.\nprint f(lexicon()).")]
        [InlineData("local p is addons:claudia:landingpass(lexicon(\"lat\", 0, \"lng\", 20, \"r_pe\", 203000)).\nprint p:burn_ut.")]
        [InlineData("local c is addons:claudia.\nprint c:LandingPass(lexicon()):next_pass_ut.")]
        [InlineData("local p is addons:claudia:brakepath(lexicon(\"lat\", 0, \"lng\", 20, \"shape\", \"auto\")).\nprint p:descent_shape.")]
        [InlineData("local c is addons:claudia.\nprint c:BrakePath(lexicon()):clears.")]
        [InlineData("local b is addons:claudia:deorbitburn(lexicon(\"lat\", 0, \"lng\", -74)).")]
        [InlineData("print addons:claudia:reentryimpact(lexicon()):lat.")]
        [InlineData("local c is addons:claudia.\nprint c:ReentryImpact(lexicon()).")]
        [InlineData("print addons:claudia:turntime(lexicon()):turn_time.")]
        [InlineData("local t is addons:claudia:TurnTime@.\nprint t(lexicon(\"max_stopping_time\", 2)).")]
        [InlineData("local e is addons:claudia:ejectionburn(lexicon(\"target\", \"Mun\", \"target_pe\", 30000)).\nadd node(e:ut, e:radial, e:normal, e:prograde).")]
        [InlineData("local c is addons:claudia.\nprint c:EjectionBurn(lexicon(\"target\", \"Minmus\", \"target_pe\", 20000, \"window\", \"next\")):dv.")]
        [InlineData("local w is addons:claudia:launchwindow(lexicon(\"inc\", 6, \"lan\", 78)).\nprint w:wait.")]
        [InlineData("local k is addons:claudia:correctionburn(lexicon(\"target\", \"Mun\", \"target_pe\", 30000)).\nadd node(k:ut, k:radial, k:normal, k:prograde).")]
        [InlineData("local c is addons:claudia.\nprint c:CorrectionBurn(lexicon(\"target\", \"Kerbin\", \"target_pe\", 35000, \"target_inc\", 0)):dv.")]
        [InlineData("local o is addons:claudia:orbitburn(lexicon(\"goal\", \"set_inc\", \"value\", 90)).\nadd node(o:ut, o:radial, o:normal, o:prograde).")]
        [InlineData("local c is addons:claudia.\nprint c:OrbitBurn(lexicon(\"goal\", \"match_plane\", \"target\", \"Minmus\")):dv.")]
        [InlineData("local h is addons:claudia:returnburn(lexicon(\"target_pe\", 35000)).\nadd node(h:ut, h:radial, h:normal, h:prograde).")]
        [InlineData("local c is addons:claudia.\nprint c:ReturnBurn(lexicon(\"target_pe\", 35000, \"target_inc\", 30)):dv.")]
        [InlineData("local a is addons:claudia:chuteallocation().\nprint a:uids.")]
        [InlineData("local c is addons:claudia.\nprint c:ChuteAllocation:titles.")]
        [InlineData("local t is addons:claudia:targetvessel().\nprint t:name.")]
        [InlineData("local c is addons:claudia.\nprint c:TargetVessel:body.")]
        [InlineData("local h is addons:claudia:dockhold(lexicon(\"port\", \"1\", \"toward\", \"2\")).\nprint h:state.")]
        [InlineData("local c is addons:claudia.\nprint c:DockHoldState:reason.")]
        public void ABlockInternalCallInAMissionScriptFails(string script)
        {
            AssertForbidden("call block-internal calculations", script);
            AssertForbidden("call block-internal calculations", CoreLibrary.Bundle(script));
        }

        // #335: the warp trace's wait marks are common calls, like BLOCKSTART: a report to the mod's trace, not a calculation
        // (ADR 0006), so a Custom Script's own claudia_warp_to, or the script itself, may make them.
        [Fact]
        public void AMissionScriptMayMarkAWaitForTheWarpTrace()
        {
            AssertAllowed("addons:claudia:waitstart(time:seconds + 600).\naddons:claudia:waitend().");
            AssertAllowed(CoreLibrary.Bundle("addons:claudia:waitstart(time:seconds + 600).\naddons:claudia:waitend()."));
        }

        // #365: a block's trace line is a report to the mod's log too, so land, or a Custom Script, may write one.
        [Fact]
        public void AMissionScriptMayTraceABlocksStateForTheLog()
        {
            AssertAllowed("addons:claudia:blocktrace(\"land\", lexicon(\"phase\", \"fall\", \"h\", 3000)).");
            Assert.Contains("addons:claudia:blocktrace(", CoreLibrary.Bundle("").ToLowerInvariant());
        }

        // #407: BLOCKNOTE writes the same line to KSP.log without keeping it as the block's last state; Script Check allows it anywhere.
        [Fact]
        public void AMissionScriptMayNoteABlocksStateForTheLogOnly()
        {
            AssertAllowed("addons:claudia:blocknote(\"land\", lexicon(\"phase\", \"rcs\")).");
            AssertAllowed(CoreLibrary.Bundle("addons:claudia:blocknote(\"land\", lexicon(\"phase\", \"rcs\"))."));
        }

        // #407: the RCS Use trace and the SAS-off note go to KSP.log only (BLOCKNOTE), so a crash report keeps the block's own state.
        // Every BLOCKTRACE and BLOCKNOTE call in the Core Library takes exactly two arguments: the suffixes are fixed-arity, and an
        // optional-argument form failed every call in game.
        [Fact]
        public void TheRcsUseTraceAndTheSasOffNoteAreNotKeptAsTheLastTracedState()
        {
            var bundle = CoreLibrary.Bundle("").Replace("\r", "");
            List<string> Calls(string suffix) => bundle.Split(new[] { "addons:claudia:" + suffix + "(" }, System.StringSplitOptions.None).Skip(1)
                .Select(c => c.Substring(0, c.IndexOf(").\n", System.StringComparison.Ordinal) + 1)).ToList();
            var notes = Calls("blocknote");
            var traces = Calls("blocktrace");

            Assert.Single(notes, c => c.Contains("\"rcs\""));
            Assert.Single(notes, c => c.Contains("\"sas\""));
            Assert.DoesNotContain(traces, c => c.Contains("\"rcs\"") || c.Contains("\"sas\""));
            Assert.All(notes.Concat(traces), c => Assert.False(c.EndsWith(", false)") || c.EndsWith(", true)"), "a third argument: " + c));
        }

        // Every ADDONS:CLAUDIA suffix is a fixed-arity kOS suffix. kOS 1.6 reads a varargs suffix's 'params' from the C# method, so a
        // method without it is called with one argument and every call fails in game; nothing off KSP runs that call path. An
        // optional argument is a second suffix instead (BLOCKTRACE and BLOCKNOTE).
        [Fact]
        public void TheAddonsSuffixesAreAllFixedArity()
        {
            var fixedArity = new HashSet<string> { "Suffix", "NoArgsSuffix", "NoArgsVoidSuffix", "OneArgsSuffix", "OneArgsVoidSuffix", "TwoArgsSuffix", "TwoArgsVoidSuffix", "ThreeArgsSuffix", "SetSuffix" };
            var found = Directory.GetFiles(Path.Combine(RepoRoot(), "src", "ClaudiaKermin.KOS"), "*.cs")
                .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"new\s+(\w*Suffix)\s*[<(]").Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => Path.GetFileName(f) + ": " + m.Groups[1].Value))
                .ToList();

            Assert.Contains(found, f => f.EndsWith(": TwoArgsSuffix"));
            Assert.All(found, f => Assert.True(fixedArity.Contains(f.Substring(f.LastIndexOf(' ') + 1)), "not a fixed-arity suffix: " + f));
        }

        // #398: an RCS switch is a report to the mod's log, like a trace, so the helper calls and a Custom Script may write one.
        [Fact]
        public void AMissionScriptMayReportAnRcsSwitch()
        {
            AssertAllowed("addons:claudia:rcsswitch(lexicon(\"on\", true, \"reason\", \"translating\")).");
        }

        // #402 (RCS Use): SAS stays off while a Mission Script runs. The library's own abort (claudia_make_safe) turns it on.
        [Theory]
        [InlineData("sas on.")]
        [InlineData("set sas to true.")]
        [InlineData("local x is true.\nset sas to x.")]
        [InlineData("set sas to not sas.")]
        [InlineData("if ship:altitude > 10 { sas on. }")]
        public void SwitchingSasOnFails(string script)
        {
            AssertForbidden("SAS must stay off while a Mission Script runs: it fights kOS steering and fires RCS through holds", script);
            AssertForbidden("SAS must stay off while a Mission Script runs: it fights kOS steering and fires RCS through holds", CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData("sas off.")]
        [InlineData("set sas to false.")]
        [InlineData("rcs on.")]
        [InlineData("rcs off.")]
        [InlineData("set rcs to true.\nprint sas.")]
        public void SwitchingSasOffAndRcsOnOrOffPass(string script)
        {
            AssertAllowed(script);
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheCoreLibrarysOwnSasOnPasses()
        {
            Assert.Contains("sas on.", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("claudia_make_safe(lexicon())."));
        }

        [Fact]
        public void TheCoreLibrarysOwnBlockInternalCallPasses()
        {
            // land calls it for a biome target; the bundle's library lines are the Core Library's.
            Assert.Contains("addons:claudia:landingsite(", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("local res is land(lexicon(\"target\", lexicon(\"biome\", \"Midlands\"))).\naddons:claudia:report(res)."));
        }

        [Fact]
        public void DeorbitsOwnReentryPredictorCallsPass()
        {
            var bundle = CoreLibrary.Bundle("").ToLowerInvariant();
            Assert.Contains("addons:claudia:deorbitburn(", bundle);
            Assert.Contains("addons:claudia:reentryimpact(", bundle);
            AssertAllowed(CoreLibrary.Bundle("local res is deorbit(lexicon(\"target\", \"ksc\")).\nif res:burn_ut > 0 { set res to reentry(). }\naddons:claudia:report(res)."));
        }

        [Fact]
        public void ExecNodesOwnTurnTimeCallPasses()
        {
            Assert.Contains("addons:claudia:turntime(", CoreLibrary.Bundle("").ToLowerInvariant());
            AssertAllowed(CoreLibrary.Bundle("add node(time:seconds + 600, 0, 0, 120).\nlocal res is exec_node().\naddons:claudia:report(res)."));
        }

        [Fact]
        public void ABlockInternalCallFailsOnTheMissionScriptsOwnLineInABundle()
        {
            var result = _check.Check(CoreLibrary.Bundle("print 1.\nprint addons:claudia:landingsite(lexicon())."));
            Assert.Contains("line 2: :LANDINGSITE (call block-internal calculations)", result.Forbidden);
        }

        [Fact]
        public void AMissionScriptCopyingTheLibrarysBannerIsStillAMissionScript()
        {
            // Only the bundle's real tail is the Core Library; a copy of its banner line in the script changes nothing.
            var script = "// ---- The Core Library, bundled by Claudia Kermin ----\nprint addons:claudia:landingsite(lexicon()).";
            AssertForbidden("call block-internal calculations", CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData(0x0D)]
        [InlineData(0x2028)]
        [InlineData(0x2029)]
        [InlineData(0x85)]
        [InlineData(0x0B)]
        [InlineData(0x0C)]
        public void OtherLineBreaksCannotMoveAMissionScriptCallOntoTheLibrarysLines(int lineBreak)
        {
            // Enough to pass the library's first line, were any of them counted as a line by kOS and not by the check.
            var padding = new string((char)lineBreak, 50);
            var result = _check.Check(CoreLibrary.Bundle(padding + "print addons:claudia:landingsite(lexicon())."));
            Assert.False(result.Passed);
        }

        [Fact]
        public void AMissionScriptCannotSwapItsOwnCodeIntoABlockWithALock()
        {
            // A lock is stored when its line runs, over the library's function of that name: a lock named land_pass_plan would get land's
            // site from the block-internal search and could keep it without the block flying it.
            var result = _check.Check(CoreLibrary.Bundle("print 1.\nlock land_pass_plan to lexicon(\"error\", \"x\").\nprint land(lexicon(\"target\", lexicon(\"biome\", \"Midlands\")))."));
            Assert.Contains("line 2: LAND_PASS_PLAN* (redefine a Core Library function)", result.Forbidden);
        }

        [Fact]
        public void AMissionScriptCannotDefineAFunctionTheLibraryDefines()
        {
            // The Mission Script's function wins over the library's (LandAscendTests shows it on kOS), so land_pass_plan could keep the site.
            var result = _check.Check(CoreLibrary.Bundle("print 1.\n\nfunction land_pass_plan {\n  parameter a, b, c.\n  return lexicon(\"error\", \"x\").\n}"));
            Assert.Contains(result.Forbidden, f => f.EndsWith("FUNCTION LAND_PASS_PLAN (redefine a Core Library function)"));
            Assert.All(result.Forbidden, f => Assert.True(int.Parse(f.Split(' ', ':')[1]) <= 6, f));
            AssertForbidden("redefine a Core Library function", CoreLibrary.Bundle("global function claudia_finish { parameter a, b, c, d. return a. }"));
        }

        [Fact]
        public void ALockInsideAMissionScriptFunctionIsCaughtToo()
        {
            AssertForbidden("redefine a Core Library function", CoreLibrary.Bundle("function f {\n  lock land_site_query to lexicon().\n}\nf()."));
        }

        [Fact]
        public void AMissionScriptMayLockSteeringAndThrottleAndNameItsOwnFunctions()
        {
            AssertAllowed(CoreLibrary.Bundle("function my_helper { return 1. }\nlock steering to prograde.\nlock throttle to 0.\nprint my_helper().\nunlock steering.\nunlock throttle."));
        }

        [Fact]
        public void LandsFileAlonePassesOnlyAsCoreLibrary()
        {
            // CI checks each kerboscript/library file alone: as the library's, not as a Mission Script.
            var land = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "land.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(land).Passed);
            Assert.Contains(_check.Check(land).Forbidden, f => f.EndsWith("(call block-internal calculations)"));
            // The library's check refuses everything else on the list.
            Assert.Contains(new KosScriptCheck(forCoreLibrary: true).Check("kuniverse:quicksave().").Forbidden, f => f.EndsWith("(quicksave)"));
        }

        [Fact]
        public void ChangeOrbitsFileAlonePassesOnlyAsCoreLibrary()
        {
            // change_orbit makes ORBITBURN inline (#444): allowed as the library's, refused as a Mission Script's.
            var block = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "change_orbit.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(block).Passed);
            Assert.Contains(_check.Check(block).Forbidden, f => f.EndsWith(":ORBITBURN (call block-internal calculations)"));
        }

        [Fact]
        public void DockFileAlonePassesOnlyAsCoreLibrary()
        {
            // dock reads the Target Vessel and its ports inline under dk_ names (#456) and starts dock_hold through the mod:
            // allowed as the library's, its calls refused as a Mission Script's.
            var block = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "dock.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(block).Passed);
            var forbidden = _check.Check(block).Forbidden;
            foreach (var call in new[] { "TARGETVESSEL", "DOCKHOLD", "DOCKHOLDSTATE" })
                Assert.Contains(forbidden, f => f.EndsWith(":" + call + " (call block-internal calculations)"));
        }

        [Fact]
        public void AMissionScriptStillCannotChangeTheTargetVesselsPorts()
        {
            // ADR 0003: dock_hold is the mod's program on the target; a Mission Script's own DOEVENT or CONTROLFROM there stays refused.
            AssertForbidden("change or message other vessels",
                "local t is vessel(\"Station\").\nfor p in t:dockingports { p:controlfrom(). }");
            AssertForbidden("change or message other vessels",
                "for p in target:dockingports { p:getmodule(\"ModuleDockingNode\"):doevent(\"undock\"). }");
        }

        [Fact]
        public void TheDockHoldProgramPassesTheCheckAsTheModsOwn()
        {
            // The Script Check never reads dock_hold (the mod's program, not Claudia's), but it changes only its own vessel: the
            // runtime gates it runs under would let it through.
            var program = ClaudiaKermin.Core.Kos.DockHold.Program("1", "2");
            Assert.Empty(new KosScriptCheck(forCoreLibrary: true).Check(program).Forbidden);
        }

        [Fact]
        public void RendezvousFileAlonePassesOnlyAsCoreLibrary()
        {
            // rendezvous makes TARGETVESSEL, ORBITBURN, EJECTIONBURN and CORRECTIONBURN inline (#455), and only reads the Target
            // Vessel: allowed as the library's, its calls refused as a Mission Script's.
            var block = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "rendezvous.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(block).Passed);
            var forbidden = _check.Check(block).Forbidden;
            foreach (var call in new[] { "TARGETVESSEL", "ORBITBURN", "EJECTIONBURN", "CORRECTIONBURN" })
                Assert.Contains(forbidden, f => f.EndsWith(":" + call + " (call block-internal calculations)"));
        }

        [Fact]
        public void ReturnsFileAlonePassesOnlyAsCoreLibrary()
        {
            // return makes RETURNBURN inline (#450): allowed as the library's, refused as a Mission Script's.
            var block = File.ReadAllText(Path.Combine(RepoRoot(), "kerboscript", "library", "return.ks"));
            Assert.True(new KosScriptCheck(forCoreLibrary: true).Check(block).Passed);
            Assert.Contains(_check.Check(block).Forbidden, f => f.EndsWith(":RETURNBURN (call block-internal calculations)"));
        }

        [Theory]
        [InlineData("land_biome_mun")]
        [InlineData("land_biome_minmus")]
        public void TheLandBiomeBlockTestsBundledWithTheLibraryPass(string test)
        {
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", test, "test.ks"));
            AssertAllowed(script);
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void TheBlockTestsCheckStillRefusesABlockInternalCallInTheScript()
        {
            var result = new KosScriptCheck(forToolTests: true).Check("print addons:claudia:landingsite(lexicon()).");
            Assert.Contains(result.Forbidden, f => f.EndsWith("(call block-internal calculations)"));
        }

        [Fact]
        public void ReadingKosConfig() { AssertAllowed("print config:ipu."); }

        [Fact]
        public void EnginesCanBeActivatedAndShutDown()
        {
            AssertAllowed("list engines in engs.\nfor e in engs { e:activate(). }\nengs[0]:shutdown().");
        }

        // ---- A bare return(...) statement ----

        [Theory]
        [InlineData("return(lexicon(\"stage_limit\", 2)).")]
        [InlineData("return (lexicon(\"stage_limit\", 2)).")]
        [InlineData("if ship:altitude > 1 { return(lexicon()). }")]
        public void ABareReturnStatementFailsTellingTheScriptToUseTheValue(string script)
        {
            var result = _check.Check(script);
            Assert.False(result.Passed, "passed: " + script);
            Assert.NotNull(result.CompileError);
            Assert.Contains("line:1", result.CompileError);
            Assert.Contains("local res is return(...)", result.CompileError);
        }

        [Fact]
        public void TheReturnBuildingBlockCalledAsAValuePasses()
        {
            AssertAllowed("function return { parameter o. return lexicon(\"success\", true). }\n"
                + "local res is return(lexicon(\"stage_limit\", 2)).\nprint res:success.\n"
                + "print return(lexicon()):success.\naddons:claudia:report(return(lexicon())).");
        }

        [Fact]
        public void ReturnStatementsInsideFunctionsPass()
        {
            AssertAllowed("function f { parameter x. if x > 1 { return (x - 1). } return x. }\nprint f(3).");
        }

        // ---- Compiling ----

        [Fact]
        public void AScriptThatDoesNotCompileFailsWithTheCompilerMessage()
        {
            var result = _check.Check("function broken {\n  local x is 1\n  print x.\n}");
            Assert.False(result.Passed);
            Assert.NotNull(result.CompileError);
        }

        [Fact]
        public void FindingsNameTheLineAndWhatWasCalled()
        {
            var result = _check.Check("print \"ok\".\ncopypath(\"0:/a\", \"1:/a\").");
            Assert.Equal(new[] { "line 2: COPYPATH (touch files)" }, result.Forbidden);
        }

        [Fact]
        public void ForbiddenCallsInsideFunctionsAndTriggersAreFound()
        {
            var result = _check.Check("function f { kuniverse:quicksave(). }\nwhen altitude > 1000 then { reboot. }\nf().");
            Assert.Contains(result.Forbidden, f => f.EndsWith("(quicksave)"));
            Assert.Contains(result.Forbidden, f => f.EndsWith("(reboot or shut down processors)"));
        }

        [Theory]
        [InlineData("change kOS config", "set config:ipu to choose 2000 if ship:altitude > 1 else 500.")]
        public void ChoosingBetweenValuesStillChecksEachOne(string item, string script) { AssertForbidden(item, script); }

        [Fact]
        public void ChoosingBetweenAllowedValuesPasses()
        {
            AssertAllowed("set warpmode to choose \"RAILS\" if ship:altitude < 70000 else \"RAILS\".\n"
                + "set warp to choose 3 if ship:altitude > 100000 else 1.");
        }

        [Fact]
        public void AnotherVesselPassedToAFunctionIsStillAnotherVessel()
        {
            AssertForbidden("change or message other vessels",
                "function poke { parameter other. other:parts[0]:getmodule(\"ModuleDecoupler\"):doevent(\"decouple\"). }\npoke(vessel(\"Other\")).");
        }

        [Fact]
        public void OwnPartsPassedToAFunctionStayAllowed()
        {
            AssertAllowed("function deploy { parameter p. p:getmodule(\"ModuleParachute\"):doevent(\"deploy chute\"). }\n"
                + "deploy(ship:partstagged(\"chute\")[0]).\nprint vessel(\"Other\"):altitude.");
        }

        [Fact]
        public void TheShippedDebugScriptsPass()
        {
            var dir = Path.Combine(RepoRoot(), "kerboscript", "debug");
            foreach (var file in Directory.GetFiles(dir, "*.ks"))
            {
                var result = _check.Check(File.ReadAllText(file));
                Assert.True(result.Passed, file + ": " + string.Join("; ", result.Problems.ToArray()));
            }
        }

        // ---- The Core Library (ADR 0003: it must pass the same check), as the mod bundles it ----

        [Fact]
        public void TheCoreLibraryBundledWithAMissionScriptPasses()
        {
            // The block test's Mission Script: it stages, warps on rails and flies a maneuver node through exec_node.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", "exec_node", "test.ks"));
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Theory]
        [InlineData("transfer_to")]
        [InlineData("return")]
        [InlineData("return_ike")]
        [InlineData("change_orbit_inc")]
        [InlineData("change_orbit_match_minmus")]
        [InlineData("change_orbit_resonant")]
        [InlineData("rendezvous")]
        [InlineData("dock")]
        public void EachBlockTestScriptBundledWithTheLibraryPasses(string test)
        {
            // transfer_to calls the ejection solver inline; return calls the block kOS lets be named return; change_orbit
            // calls ORBITBURN inline.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "tests", "blocks", test, "test.ks"));
            AssertAllowed(CoreLibrary.Bundle(script));
        }

        [Fact]
        public void AForbiddenCallInABundledScriptFailsOnTheScriptsOwnLine()
        {
            var result = _check.Check(CoreLibrary.Bundle("print \"hello\".\nkuniverse:quicksave()."));
            Assert.False(result.Passed);
            Assert.Contains("line 2: :QUICKSAVE (quicksave)", result.Forbidden);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ClaudiaKermin.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
