// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using ClaudiaKerman.Core.Missions;
using Xunit;

namespace ClaudiaKerman.Core.Tests
{
    /// <summary>#852: a next-patch time stored in a variable before the coast reads as #833's direct form, and only when nothing about the variable is in doubt.</summary>
    public sealed class StoredNextPatchTests
    {
        private const double Now = 3769816;
        private const double End = 63000;
        private const string Ut = "time:seconds + ship:orbit:nextpatcheta";
        private const string Eta = "ship:orbit:nextpatcheta";
        private const string Capture = " set res to capture(lexicon(\"target_pe\", 80000)).";

        private static string Coast(string until)
        {
            return "local res is coast(lexicon(\"until\", " + until + ")).";
        }

        /// <summary>The craft is 1000 s from apoapsis, so a burn timed on the orbit left would add to the coast's wait. The snapshot is older than now: a wait worked from it is 500 s out.</summary>
        private static GameSnapshot Snapshot()
        {
            var snapshot = BurnPointsTests.Orbiting(1000);
            snapshot.Ut = Now - 500;
            snapshot.NextPatchEndUt = Now + End;
            return snapshot;
        }

        // ---- Each recorded form raises a short duration, with the exact wait ----

        [Theory]
        // m20261030-023345-9f50 and m20261030-173711-f623, with the block before them gone: First stops at any other block.
        [InlineData("local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60))." + Capture, 63060)]
        [InlineData("local res is lexicon(\"success\", true).\nif res:success {\n  local t is " + Ut + ".\n  set res to coast(lexicon(\"until\", t + 60)).\n  if res:success { set res to circularize(lexicon(\"at\", \"pe\")). }\n}", 63060)]
        [InlineData("local res is lexicon(\"success\", true).\nif res:success {\n    local enc is " + Ut + ".\n    set res to coast(lexicon(\"until\", enc + 60)).\n    set res to circularize(lexicon(\"at\", \"pe\")).\n}", 63060)]
        // m20261029-181119-0584: its name, its guard on the variable, and the coast one block further in.
        [InlineData("local res is lexicon(\"success\", true).\nif res:success {\n  if ship:body:name <> \"Kerbin\" {\n    local patch_wait is " + Eta + ".\n    if patch_wait > 0 {\n      set res to coast(lexicon(\"until\", time:seconds + patch_wait + 60)).\n    }\n  }\n}\nset res to capture(lexicon(\"target_pe\", 80000)).", 63060)]
        [InlineData("local eta is " + Eta + ". local res is coast(lexicon(\"until\", time:seconds + eta))." + Capture, 63000)]
        // m20261102-161833-be82: the offset in the assignment, a bare name in the coast.
        [InlineData("local t1 is " + Ut + " + 60.\nlocal c1 is coast(lexicon(\"until\", t1)).\nlocal res is capture(lexicon(\"target_pe\", 80000)).", 63060)]
        public void A_recorded_form_raises_a_short_duration_with_the_exact_wait(string script, double wait)
        {
            var plan = new SequencePlan { Script = script, ExpectedDurationSeconds = 20000, ExpectedDeltaV = 600 };
            var short_ = BurnPoints.ShortOf(plan, Snapshot(), Now);
            Assert.NotNull(short_);
            Assert.Equal(wait, short_.WaitSeconds, 3);
            Assert.Contains("next patch", short_.Point);
            Assert.Equal(Math.Ceiling((wait + BurnPoints.BurnAllowanceSeconds + BurnPoints.TurnMarginSeconds) / 10) * 10, short_.MinimumDurationSeconds, 3);
            plan.ExpectedDurationSeconds = short_.MinimumDurationSeconds;
            Assert.Null(BurnPoints.ShortOf(plan, Snapshot(), Now));
        }

        // ---- Offsets count once, wherever they are written ----

        [Theory]
        [InlineData("local t is " + Ut + ".", "t", 0)]
        [InlineData("local t is " + Ut + ".", "t + 600", 600)]
        [InlineData("local t is " + Ut + " + 100.", "t", 100)]
        [InlineData("local t is " + Ut + " + 100.", "t + 600", 700)]
        [InlineData("local t is " + Ut + " + 0.5.", "t + 1.25", 1.75)]
        [InlineData("local e is " + Eta + ".", "time:seconds + e", 0)]
        [InlineData("local e is " + Eta + ".", "time:seconds + e + 600", 600)]
        [InlineData("local e is " + Eta + " + 100.", "time:seconds + e", 100)]
        [InlineData("local e is " + Eta + " + 100.", "time:seconds + e + 600", 700)]
        // kOS names don't care about case, nor its statements about spacing.
        [InlineData("LOCAL Enc IS TIME:SECONDS + SHIP:ORBIT:NEXTPATCHETA.", "eNC + 600", 600)]
        [InlineData("local t is time : seconds  +  ship : orbit : nextpatcheta  +  100 .", "t + 600", 700)]
        // Every way of writing the one assignment.
        [InlineData("set t to " + Ut + ".", "t + 600", 600)]
        [InlineData("global t is " + Ut + ".", "t + 600", 600)]
        [InlineData("local t to " + Ut + ".", "t + 600", 600)]
        [InlineData("declare t to " + Ut + ".", "t + 600", 600)]
        [InlineData("declare local t to " + Ut + ".", "t + 600", 600)]
        public void An_offset_is_counted_once(string assignment, string until, double offset)
        {
            var wait = BurnPoints.First(assignment + " " + Coast(until) + Capture, Snapshot(), Now, 600);
            Assert.NotNull(wait);
            Assert.Equal(End + offset, wait.WaitSeconds, 3);
        }

        [Theory]
        // Reading the variable isn't assigning it.
        [InlineData("local t is " + Ut + ". print t. print \"at \" + t.")]
        [InlineData("local t is " + Ut + ". if t > 0 { print 1. }")]
        // Text that only looks like a second assignment.
        [InlineData("// set t to 5\nlocal t is " + Ut + ".")]
        [InlineData("local t is " + Ut + ". print \"set t to 5\".")]
        [InlineData("set t2 to 1. set my_t to 2. if x:t > 3 { print 1. } local t is " + Ut + ".")]
        // Clearing the nodes and building a result pass no time.
        [InlineData("for n in allnodes { remove n. } local r0 is lexicon(\"success\", true). local t is " + Ut + ".")]
        // m20261101-160346-2997's way of clearing them, and a suffix read in an if's condition.
        [InlineData("local stale is allnodes. for nd in stale { remove nd. } local t is " + Ut + ".")]
        [InlineData("if res:success { print 1. } else if ship:body:name <> \"Kerbin\" { print 2. } local t is " + Ut + ".")]
        // Switching something on is no trigger; "on" opens one only when something follows it.
        [InlineData("sas on. rcs off. if a { lights on. } local t is " + Ut + ".")]
        // A name that only starts like one of the words.
        [InlineData("local run_time is 1. local abort_at is 2. local ag1x is 3. local t is " + Ut + ".")]
        // The calls that build a value or are counted, however they're spaced.
        [InlineData("local n is node (1, 0, 0, 2). local l is list (1). local r0 is lex (\"a\", 1). local t is " + Ut + ".")]
        public void Other_text_about_the_name_leaves_it_read(string before)
        {
            var wait = BurnPoints.First(before + " " + Coast("t + 60") + Capture, Snapshot(), Now, 600);
            Assert.NotNull(wait);
            Assert.Equal(End + 60, wait.WaitSeconds, 3);
        }

        // ---- What stays unread: the plan stays unchecked ----

        private static void Unread(string script)
        {
            Assert.Null(BurnPoints.First(script, Snapshot(), Now, 600));
            var plan = new SequencePlan { Script = script, ExpectedDurationSeconds = 100, ExpectedDeltaV = 600 };
            Assert.Null(BurnPoints.ShortOf(plan, Snapshot(), Now));
        }

        [Theory]
        [InlineData("local t is " + Ut + ". set t to t + 60. ")]
        [InlineData("local t is 0. set t to " + Ut + ". ")]
        [InlineData("local T is " + Ut + ". set t to 5. ")]
        [InlineData("set t to 5. local t is " + Ut + ". ")]
        [InlineData("function f { local t is 1. } local t is " + Ut + ". ")]
        [InlineData("function f { set t to 1. } set t to " + Ut + ". ")]
        [InlineData("function t { return 1. } local t is " + Ut + ". ")]
        [InlineData("parameter t. local t is " + Ut + ". ")]
        [InlineData("parameter a is 0.5, t is 2. local t is " + Ut + ". ")]
        [InlineData("for t in allnodes { print t. } local t is " + Ut + ". ")]
        [InlineData("list parts in t. local t is " + Ut + ". ")]
        [InlineData("lock t to 5. local t is " + Ut + ". ")]
        [InlineData("unset t. local t is " + Ut + ". ")]
        [InlineData("local t is " + Ut + ". toggle t. ")]
        [InlineData("local t is " + Ut + ". t off. ")]
        [InlineData("local t is " + Ut + ". unset all. ")]
        public void A_variable_assigned_more_than_once_stays_unread(string before)
        {
            Unread(before + Coast("t + 60") + Capture);
        }

        /// <summary>A kOS string may span lines; the masking restarts at each line, so what it shows after one can't be trusted, in either direction.</summary>
        [Theory]
        [InlineData("local t is " + Ut + ".\nlocal s is \"a\nb\". set t to 5.\n")]
        [InlineData("local s is \"a\nb\". set t to 5.\nlocal t is " + Ut + ".\n")]
        [InlineData("local t is " + Ut + ".\nlocal s is \"a\nb\". wait 1000.\n")]
        public void A_string_across_lines_leaves_it_unread(string before)
        {
            Unread(before + Coast("t + 60") + Capture);
        }

        [Fact]
        public void An_assignment_after_the_coast_counts_as_a_second_one()
        {
            Unread("local t is " + Ut + ". " + Coast("t + 60") + " set t to 5." + Capture);
        }

        [Theory]
        [InlineData("until done { local t is " + Ut + ". " + "local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("for n in allnodes { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("from { local i is 0. } until i > 1 step { set i to i + 1. } do { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        // An unbraced loop body is a loop too.
        [InlineData("until done local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60))." + Capture)]
        [InlineData("set t to " + Ut + ". until done coast(lexicon(\"until\", t + 60))." + Capture)]
        // Assigned once outside, but the coast runs over and over.
        [InlineData("local t is " + Ut + ". until done { local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("local t is " + Ut + ". if a { until done { local res is coast(lexicon(\"until\", t + 60)). } }" + Capture)]
        // A function, whenever it is called.
        [InlineData("function go { local t is " + Ut + ". return coast(lexicon(\"until\", t + 60)). } local res is go()." + Capture)]
        [InlineData("local t is " + Ut + ". function go { return coast(lexicon(\"until\", t + 60)). } local res is go()." + Capture)]
        [InlineData("local go is { local t is " + Ut + ". return coast(lexicon(\"until\", t + 60)). }. local res is go()." + Capture)]
        [InlineData("when a then { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        // A loop or a function behind an if: the first word says branch, the rest doesn't.
        [InlineData("if a until done { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if a for n in allnodes { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if a when b then { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if a on ag1 { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if a { print 1. } else until done { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if f({ local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }) { print 1. }" + Capture)]
        [InlineData("local t is " + Ut + ". if a until done coast(lexicon(\"until\", t + 60))." + Capture)]
        [InlineData("local t is " + Ut + ". if a until done set res to coast(lexicon(\"until\", t + 60))." + Capture)]
        // A function in a call's brackets, where the call is one that builds a value: only the brackets say so.
        [InlineData("if list({ local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }) { print 1. }" + Capture)]
        [InlineData("if lexicon(\"k\", { local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60)). }) { print 1. }" + Capture)]
        public void A_variable_in_a_loop_or_function_stays_unread(string script)
        {
            Unread(script);
        }

        [Theory]
        // Named like a time, holding something else.
        [InlineData("local patch_end is time:seconds + 5000.", "patch_end + 60")]
        [InlineData("local nextpatcheta is 5000.", "time:seconds + nextpatcheta")]
        [InlineData("local enc is ship:orbit:eta:periapsis.", "time:seconds + enc")]
        [InlineData("local enc is time:seconds + ship:orbit:eta:apoapsis.", "enc + 60")]
        // Holding a part of the form or more than it: m20261026-211744-5a99's halfway point.
        [InlineData("local mid is " + Ut + " / 2.", "mid")]
        [InlineData("local mid is " + Eta + " / 2.", "time:seconds + mid")]
        [InlineData("local t is " + Ut + " * 2 + 1.", "t")]
        [InlineData("local t is " + Ut + " - 60.", "t")]
        [InlineData("local t is 60 + " + Ut + ".", "t")]
        [InlineData("local t is time:seconds + nd:orbit:nextpatcheta.", "t")]
        [InlineData("local t is time:seconds + ship:orbit:nextpatch:eta.", "t")]
        [InlineData("local t is time:seconds + ship:orbit:nextpatcheta:x.", "t")]
        [InlineData("local t is pick(" + Ut + ").", "t")]
        [InlineData("local t is \"" + Ut + "\".", "t")]
        // Behind an if with no braces: it may not run, and "t" is then not this.
        [InlineData("if a set t to " + Ut + ".", "t + 60")]
        // Out of scope (#852): other spellings.
        [InlineData("local t is time:seconds + obt:nextpatcheta.", "t")]
        // A lock is no stored value.
        [InlineData("lock t to " + Ut + ".", "t")]
        [InlineData("local lock t to " + Ut + ".", "t")]
        // The coast doing more with it than the form: m20261101-160346-2997's early stop.
        [InlineData("local enc0 is " + Ut + ".", "enc0 - 150000")]
        [InlineData("local t is " + Ut + ".", "t * 2")]
        [InlineData("local t is " + Ut + ".", "60 + t")]
        [InlineData("local t is " + Ut + ".", "t + 60 + 5")]
        [InlineData("local t is " + Ut + ".", "t:seconds")]
        // A UT used as a wait and a wait used as a UT: neither is the patch's end, nor even near it.
        [InlineData("local t is " + Ut + ".", "time:seconds + t")]
        [InlineData("local e is " + Eta + ".", "e")]
        [InlineData("local e is " + Eta + ".", "e + 60")]
        // Never assigned here: eta is kOS's own.
        [InlineData("print 1.", "time:seconds + eta")]
        [InlineData("print 1.", "t + 60")]
        public void A_variable_assigned_from_anything_else_stays_unread(string assignment, string until)
        {
            Unread(assignment + " " + Coast(until) + Capture);
        }

        [Theory]
        [InlineData("local res is coast(lexicon(\"until\", t + 60)). local t is " + Ut + "." + Capture)]
        // Out of scope by the time of the coast, or in another branch.
        [InlineData("if a { local t is " + Ut + ". } local res is coast(lexicon(\"until\", t + 60))." + Capture)]
        [InlineData("if a { local t is " + Ut + ". } else { local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        [InlineData("if a { local t is " + Ut + ". } if b { local res is coast(lexicon(\"until\", t + 60)). }" + Capture)]
        public void A_coast_the_assignment_does_not_reach_stays_unread(string script)
        {
            Unread(script);
        }

        /// <summary>
        /// <c>time:seconds + e</c> at the coast is the patch's end only if no time passed since <c>e</c> was read: a wait of 100 s puts it 100 s past. And a burn moves
        /// the patch's end whichever form holds it.
        /// </summary>
        [Theory]
        [InlineData("wait 100.")]
        [InlineData("wait until ship:altitude > 100.")]
        [InlineData("until done { print 1. }")]
        [InlineData("for n in allnodes { print n. }")]
        [InlineData("settle().")]
        [InlineData("run other.")]
        [InlineData("set warp to 3.")]
        [InlineData("kuniverse:timewarp:warpto(5).")]
        [InlineData("local res0 is coast(lexicon(\"until\", time:seconds + 100)).")]
        [InlineData("lock throttle to 1.")]
        [InlineData("stage.")]
        // kOS calls a suffix method with no brackets; a suffix may also set the throttle.
        [InlineData("pause:call.")]
        [InlineData("set ship:control:pilotmainthrottle to 1.")]
        public void What_can_pass_time_or_burn_between_the_assignment_and_the_coast_leaves_it_unread(string between)
        {
            Unread("local e is " + Eta + ". " + between + " " + Coast("time:seconds + e + 60") + Capture);
            Unread("local t is " + Ut + ". " + between + " " + Coast("t + 60") + Capture);
        }

        /// <summary>A wait or a burn before the assignment may take the craft past the snapshot's patch, or move its end: what the variable then holds isn't the snapshot's figure.</summary>
        [Theory]
        [InlineData("wait 100000.")]
        [InlineData("wait until ship:altitude > 100.")]
        [InlineData("until done { print 1. }")]
        [InlineData("lock throttle to 1.")]
        [InlineData("set ship:control:pilotmainthrottle to 1.")]
        [InlineData("stage.")]
        [InlineData("burn_it().")]
        [InlineData("kuniverse:timewarp:warpto(5).")]
        // A call needs no name before its bracket.
        [InlineData("local fs is list(late@). fs[0]().")]
        [InlineData("local fs is list(late@). fs#0().")]
        [InlineData("(late@)().")]
        [InlineData("list(late@)[0]().")]
        [InlineData("mylist(1).")]
        // An action group may fire an engine.
        [InlineData("toggle ag1.")]
        [InlineData("ag1 on.")]
        [InlineData("abort on.")]
        // A suffix outside an if's condition may be a call (kOS calls one with no brackets) or a write, which no list of names can tell from a read.
        [InlineData("set ship:control:mainthrottle to 1.")]
        [InlineData("set kuniverse:timewarp:rate to 100.")]
        [InlineData("local g is late@. g:call.")]
        [InlineData("local g is late@. local r is g:call.")]
        [InlineData("if g:call { print 1. }")]
        [InlineData("if a g:call.")]
        [InlineData("if a set ship:control:mainthrottle to 1.")]
        [InlineData("part:activate.")]
        [InlineData("print x:y.")]
        [InlineData("set x:t to 3.")]
        // Repeated, or run later, so the coasts in them aren't counted as written: the craft may be anywhere by the assignment.
        [InlineData("for i in list(1,2,3) { local a is coast(lexicon(\"until\", time:seconds + 30000)). }")]
        [InlineData("for n in allnodes { local a is coast(lexicon(\"until\", time:seconds + 30000)). }")]
        [InlineData("when a then { local b is coast(lexicon(\"until\", time:seconds + 30000)). }")]
        [InlineData("on ag1 { local b is coast(lexicon(\"until\", time:seconds + 30000)). }")]
        [InlineData("if a { when b then { print 1. } }")]
        // Only a loop that does nothing but clear what it walks is left alone.
        [InlineData("for n in allnodes { print n. }")]
        [InlineData("for n in allnodes { remove other. }")]
        [InlineData("for n in allnodes { remove n. print n. }")]
        public void What_can_pass_time_or_burn_before_the_assignment_leaves_it_unread(string before)
        {
            Unread(before + " local t is " + Ut + ". " + Coast("t + 60") + Capture);
            Unread(before + " local e is " + Eta + ". " + Coast("time:seconds + e + 60") + Capture);
        }

        /// <summary>The coast's other options are worked out before it holds, later than the assignment: a lead that takes a while puts <c>time:seconds + e</c> past the end, and may take the craft past it.</summary>
        [Theory]
        [InlineData("\"lead\", slow(), \"until\", {0}")]
        [InlineData("\"until\", {0}, \"lead\", slow()")]
        [InlineData("\"lead\", g:call, \"until\", {0}")]
        [InlineData("\"lead\", ship:orbit:period, \"until\", {0}")]
        public void What_can_pass_time_in_the_coasts_own_options_leaves_it_unread(string options)
        {
            Unread("local e is " + Eta + ". local res is coast(lexicon(" + string.Format(options, "time:seconds + e") + "))." + Capture);
            Unread("local t is " + Ut + ". local res is coast(lexicon(" + string.Format(options, "t") + "))." + Capture);
        }

        [Theory]
        [InlineData("\"lead\", 120, \"until\", {0}")]
        [InlineData("\"until\", {0}, \"max_duration\", \"auto\", \"abort_on_part_loss\", false")]
        // Words in a string are no work.
        [InlineData("\"until\", {0}, \"note\", \"wait: on (x)\"")]
        public void Plain_options_in_the_coast_leave_it_read(string options)
        {
            Assert.Equal(End, BurnPoints.First("local e is " + Eta + ". local res is coast(lexicon(" + string.Format(options, "time:seconds + e") + "))." + Capture, Snapshot(), Now, 600).WaitSeconds, 3);
            Assert.Equal(End, BurnPoints.First("local t is " + Ut + ". local res is coast(lexicon(" + string.Format(options, "t") + "))." + Capture, Snapshot(), Now, 600).WaitSeconds, 3);
        }

        /// <summary>A decimal's point ends no statement, so the if is still seen as the block's head.</summary>
        [Fact]
        public void A_decimal_in_an_ifs_condition_leaves_it_read()
        {
            var wait = BurnPoints.First("if x > 1.5 { local t is " + Ut + ". " + Coast("t + 60") + Capture + " }", Snapshot(), Now, 600);
            Assert.NotNull(wait);
            Assert.Equal(End + 60, wait.WaitSeconds, 3);
        }

        // ---- After reading, as #833 ----

        [Theory]
        [InlineData("set res to circularize().")]
        [InlineData("set res to capture(lexicon(\"target_pe\", 80000)).")]
        [InlineData("set res to change_orbit(lexicon(\"at\", \"pe\", \"target_ap\", 90000)).")]
        [InlineData("set res to tune_pe(lexicon(\"value\", 50000)).")]
        [InlineData("set res to transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 50000)).")]
        [InlineData("set res to return(lexicon()).")]
        public void A_burn_after_the_coast_gets_the_floor(string burn)
        {
            var wait = BurnPoints.First("local t is " + Ut + ". " + Coast("t + 60") + " " + burn, Snapshot(), Now, 600);
            Assert.NotNull(wait);
            Assert.Contains("the wait is the coast's", wait.Point);
            // Exactly the coast: timed on the orbit left it would be 1000 s or a period more.
            Assert.Equal(63060, wait.WaitSeconds, 3);
            Assert.Equal(63060, wait.LongestWaitSeconds, 3);
        }

        [Theory]
        [InlineData("local t is " + Ut + ". local u is " + Ut + ". local a is coast(lexicon(\"until\", t)). local b is coast(lexicon(\"until\", u + 60)). capture().")]
        [InlineData("local a is coast(lexicon(\"until\", " + Ut + ")). local t is " + Ut + ". local b is coast(lexicon(\"until\", t + 60)). capture().")]
        [InlineData("local t is " + Ut + ". local a is coast(lexicon(\"until\", t)). local b is coast(lexicon(\"until\", " + Ut + " + 60)). capture().")]
        [InlineData("local e is " + Eta + ". local a is coast(lexicon(\"until\", time:seconds + e)). local b is coast(lexicon(\"until\", time:seconds + e + 60)). capture().")]
        // The one variable twice: the second is a coast past the end the first reached.
        [InlineData("local t is " + Ut + ". local a is coast(lexicon(\"until\", t)). local b is coast(lexicon(\"until\", t + 60)). capture().")]
        public void A_second_next_patch_coast_leaves_the_plan_unchecked(string script)
        {
            Unread(script);
        }

        [Fact]
        public void No_next_patch_in_the_snapshot_leaves_it_unread()
        {
            // The snapshot gives an end only for ENCOUNTER and ESCAPE (#833): every other patch end arrives here as null.
            var snapshot = Snapshot();
            snapshot.NextPatchEndUt = null;
            Assert.Null(BurnPoints.First("local t is " + Ut + ". " + Coast("t + 60") + Capture, snapshot, Now, 600));
            Assert.Null(BurnPoints.First("local e is " + Eta + ". " + Coast("time:seconds + e") + Capture, snapshot, Now, 600));
        }

        [Fact]
        public void A_patch_end_already_passed_by_a_coast_before_it_leaves_it_unread()
        {
            var past = "local a is coast(lexicon(\"until\", time:seconds + " + (End + 1000) + ")). local t is " + Ut + ". " + "local b is coast(lexicon(\"until\", t)). capture().";
            Assert.Null(BurnPoints.First(past, Snapshot(), Now, 600));
        }

        [Fact]
        public void A_coast_before_the_assignment_or_after_the_coast_counts_with_it()
        {
            var before = BurnPoints.First("local a is coast(lexicon(\"until\", time:seconds + 1000)). local t is " + Ut + ". local b is coast(lexicon(\"until\", t + 10)). capture().", Snapshot(), Now, 600);
            Assert.Equal(End + 10, before.WaitSeconds, 3);
            // The wait form read after a coast: the patch's end still, as eta is read then.
            var eta = BurnPoints.First("local a is coast(lexicon(\"until\", time:seconds + 1000)). local e is " + Eta + ". local b is coast(lexicon(\"until\", time:seconds + e + 10)). capture().", Snapshot(), Now, 600);
            Assert.Equal(End + 10, eta.WaitSeconds, 3);
            var after = BurnPoints.First("local t is " + Ut + ". local a is coast(lexicon(\"until\", t)). local b is coast(lexicon(\"until\", time:seconds + 100)). capture().", Snapshot(), Now, 600);
            Assert.Equal(End + 100, after.WaitSeconds, 3);
        }

        // ---- The Due Time paths in MissionControl ----

        private const double SunAltitude = 13599840256 - 261600000;
        private const string Transfer = "local res is transfer_to(lexicon(\"target\", \"Duna\", \"target_pe\", 50000, \"window\", 41000000)).";

        /// <summary>
        /// TransferWait makes a Due Time from <see cref="BurnPoints.DueTime"/> of <see cref="BurnPoints.First"/>. A plan unread before gets no Due Time now: after a next-patch
        /// coast a transfer is a floor with no departure, though the same transfer with no coast before it is a Due Time.
        /// </summary>
        [Theory]
        [InlineData("local t is " + Ut + ". local a is coast(lexicon(\"until\", t + 60)). " + Transfer)]
        [InlineData("local e is " + Eta + ". local a is coast(lexicon(\"until\", time:seconds + e + 60)). " + Transfer)]
        public void A_transfer_after_a_stored_next_patch_coast_makes_no_due_time(string script)
        {
            var rig = TransferRig.Circular("Sun", SunAltitude, 0);
            var alone = BurnPoints.First(Transfer, rig.Snapshot(), 0, 1000);
            Assert.True(BurnPoints.DueTime(alone, 0).HasValue);

            var snapshot = rig.Snapshot();
            snapshot.NextPatchEndUt = 50e6;
            var wait = BurnPoints.First(script, snapshot, 0, 1000);
            Assert.NotNull(wait);
            Assert.Null(wait.DepartureUt);
            Assert.False(BurnPoints.DueTime(wait, 0).HasValue);
            // So ShortOf keeps the whole wait instead of cutting it to the launch lead a Due Time asks for.
            var short_ = BurnPoints.ShortOf(new SequencePlan { Script = script, ExpectedDurationSeconds = 100, ExpectedDeltaV = 1000 }, snapshot, 0);
            Assert.Equal(50e6 + 60, short_.WaitSeconds, 3);
        }

        /// <summary>
        /// A plan with a Due Time of Claudia's own ahead runs then, when the patch's end may be behind it and the snapshot's clock isn't the run's: a raise counted from now
        /// is the wrong figure, and #830's raise isn't asked again at the Due Time. It stays as it was before #852: unread.
        /// </summary>
        [Theory]
        [InlineData("local t is " + Ut + ". local res is coast(lexicon(\"until\", t + 60))." + Capture)]
        [InlineData("local e is " + Eta + ". local res is coast(lexicon(\"until\", time:seconds + e + 60))." + Capture)]
        public void A_plan_not_yet_due_stays_unread(string script)
        {
            var plan = new SequencePlan { Script = script, ExpectedDurationSeconds = 100, ExpectedDeltaV = 600, DueTimeUt = Now + 1000 };
            Assert.Null(BurnPoints.ShortOf(plan, Snapshot(), Now));
            // At or past its Due Time it runs now, and reads.
            plan.DueTimeUt = Now;
            Assert.Equal(63060, BurnPoints.ShortOf(plan, Snapshot(), Now).WaitSeconds, 3);
            plan.DueTimeUt = Now - 1000;
            Assert.Equal(63060, BurnPoints.ShortOf(plan, Snapshot(), Now).WaitSeconds, 3);
        }

        [Fact]
        public void The_direct_form_on_a_plan_not_yet_due_is_as_it_was()
        {
            var plan = new SequencePlan { Script = Coast(Ut + " + 60") + Capture, ExpectedDurationSeconds = 100, ExpectedDeltaV = 600, DueTimeUt = Now + 1000 };
            Assert.Equal(63060, BurnPoints.ShortOf(plan, Snapshot(), Now).WaitSeconds, 3);
        }
    }
}
