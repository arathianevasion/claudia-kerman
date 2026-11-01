// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block change_orbit: flies any of solve_burn's orbit goals (an inclination, a plane match, a LAN, a resonant
// orbit...) as one maneuver node. The node is solved when the block starts, from the orbit as it is then, by
// ADDONS:CLAUDIA:ORBITBURN (ADR 0006: tools read the Snapshot, blocks re-solve), the same C# as solve_burn, and flown with
// exec_node; the block judges by the element the goal sets. Built on claudia_lib.ks and exec_node.ks; block-only helpers
// are prefixed change_orbit_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| change_orbit(options): inclination, plane, LAN, resonance.
//| Any of these orbit changes in one burn: call it rather than hand-write the node. The burn is solved from the orbit
//| when the block starts and flown by exec_node.
//| Options:
//|   goal [required]: set_inc, match_plane, shift_lan, resonant, stationary, set_sma, set_ecc, ellipticize, set_ap,
//|     set_pe, circularize or next_patch_pe, with value, at, target, pe, ap, of and direction as solve_burn takes them.
//|   tolerance ["auto"]: how far the element set may end from the goal, in its units: 1000 m (pe, ap, sma,
//|     next_patch_pe), 0.01 (ecc), 0.5 degrees (inc, rel_inc, lan), 1% of the period; more fails the block.
//|   max_dv ["none"]: m/s the burn may cost; more fails the block before any burn.
//|   max_duration ["auto"]: as for exec_node (the wait to the burn, twice the burn, plus 300 s).
//|   plus min_ec_pct, abort_on_part_loss, stage_limit and drop_empty as for every block.
//| Extra results: <element>_actual for each element the goal sets, when the block ended: inc_actual (set_inc),
//| rel_inc_actual (degrees off target's plane: match_plane), lan_actual and inc_actual (kept: shift_lan), period_actual (s:
//| resonant, stationary), sma_actual, ecc_actual (also circularize), ap_actual, pe_actual (both: ellipticize),
//| next_patch_pe_actual; burn_ut (UT of the burn; -1 if none was needed). Succeeds at once, with no burn, when the
//| burn is under 0.05 m/s. Fails with "goal is required", an option value it can't read, "a maneuver node is already
//| planned: remove it first", solve_burn's reason for a goal it can't reach, "needs <dv> m/s, over max_dv <max>", the
//| reasons exec_node gives (no thrust, out of fuel, an abort; its node stays), or "<element> <x> is over tolerance
//| <t> from <goal>".
//| Example: `local res is change_orbit(lexicon("goal", "set_inc", "value", 90)). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it; "none", left out).
function change_orbit_defaults {
  return lexicon("goal", "required", "value", "none", "at", "none", "target", "none", "pe", "none", "ap", "none", "of", "none",
    "direction", "none", "tolerance", "auto", "max_dv", "none").
}

// The options passed on to ADDONS:CLAUDIA:ORBITBURN as solve_burn's inputs, when given.
function change_orbit_inputs {
  return list("value", "at", "target", "pe", "ap", "of", "direction").
}

// "" or why an option value isn't one the block can use. The goal and its inputs are solve_burn's to judge: ORBITBURN
// gives its reason. Pure.
function change_orbit_unreadable {
  parameter opt.
  if not opt:goal:istype("String") {
    return "goal must be a solve_burn goal's name, not " + opt:goal.
  }
  if opt:goal = "required" {
    return "goal is required".
  }
  for key in list("value", "pe", "ap", "of") {
    if not opt[key]:istype("Scalar") and not change_orbit_is(opt[key], "none") {
      return key + " must be a number, not " + opt[key].
    }
  }
  if not opt:at:istype("Scalar") and not opt:at:istype("String") {
    return "at must be a word or a UT, not " + opt:at.
  }
  if not opt:target:istype("String") and not opt:target:istype("Body") {
    return "target must be a body or its name".
  }
  if not opt:direction:istype("String") {
    return "direction must be 'prograde' or 'retrograde', not " + opt:direction.
  }
  if not change_orbit_is(opt:tolerance, "auto") and not change_orbit_positive(opt:tolerance) {
    return "tolerance must be 'auto' or a number above 0, not " + opt:tolerance.
  }
  if not change_orbit_is(opt:max_dv, "none") and not change_orbit_positive(opt:max_dv) {
    return "max_dv must be m/s above 0 or 'none', not " + opt:max_dv.
  }
  return "".
}

// Whether x is the string word (an option's "none" or "auto"), never comparing a number with a string. Pure.
function change_orbit_is {
  parameter x, word.
  if x:istype("String") {
    return x = word.
  }
  return false.
}

// Whether x is a number above 0. Pure.
function change_orbit_positive {
  parameter x.
  if x:istype("Scalar") {
    return x > 0.
  }
  return false.
}

// What the block asks ADDONS:CLAUDIA:ORBITBURN: the goal and each of its inputs the Mission Script gave, a body target by
// its name. Pure for a target given as a name.
function change_orbit_ask {
  parameter opt.
  local ask is lexicon("goal", opt:goal).
  for key in change_orbit_inputs() {
    local given is opt[key].
    if given:istype("Body") {
      set given to given:name.
    }
    if not change_orbit_is(given, "none") {
      set ask[key] to given.
    }
  }
  return ask.
}

// The tolerance an element is judged by: tolerance_opt, or with "auto" the element's own (1000 m, 0.01 of
// eccentricity, 0.5 degrees, 1% of the period wanted). Pure.
function change_orbit_tolerance {
  parameter element, wanted, tolerance_opt.
  if tolerance_opt:istype("Scalar") {
    return tolerance_opt.
  }
  if element = "ecc" {
    return 0.01.
  }
  if element = "inc" or element = "rel_inc" or element = "lan" {
    return 0.5.
  }
  if element = "period" {
    return 0.01 * wanted.
  }
  return 1000.
}

// How far actual is from wanted, in the element's units; the LAN the short way round the circle. Pure.
function change_orbit_off {
  parameter element, actual, wanted.
  local off is actual - wanted.
  if element = "lan" {
    set off to claudia_wrap_angle(off + 180) - 180.
  }
  return abs(off).
}

// An element's figure rounded for the result: degrees to 4 places, eccentricity to 5, a period to 0.1 s, metres whole. Pure.
function change_orbit_round {
  parameter element, x.
  if element = "inc" or element = "rel_inc" or element = "lan" {
    return round(x, 4).
  }
  if element = "ecc" {
    return round(x, 5).
  }
  if element = "period" {
    return round(x, 1).
  }
  return round(x).
}

// "" or why the orbit missed: the first element of wanted (element: figure, from ORBITBURN) whose actual (element: figure
// read after the burn) is over its tolerance. Pure.
function change_orbit_judge {
  parameter wanted, actual, tolerance_opt.
  for element in wanted:keys {
    local tol is change_orbit_tolerance(element, wanted[element], tolerance_opt).
    if change_orbit_off(element, actual[element], wanted[element]) > tol {
      return element + " " + change_orbit_round(element, actual[element]) + " is over tolerance " + change_orbit_round(element, tol)
        + " from " + change_orbit_round(element, wanted[element]).
    }
  }
  return "".
}

// The block's flight, with its three contacts with the game passed in so the kOS harness can fly it: solve(ask) is
// ORBITBURN's answer for the orbit as it is now, fly(plan) flies its node and returns exec_node's result, read(element)
// is the element's figure now, first(ut) is "" or why a burn at ut must wait for a sphere-of-influence change
// (claudia_soi_first, #610). Solves first, so the node is the live orbit's, not the plan's. Returns
// lexicon("reason", "" or why the block fails, "extra", the block's extra results).
function change_orbit_run {
  parameter opt, solve, fly, read, first.
  local done is lexicon("reason", "", "extra", lexicon("burn_ut", -1)).
  local plan is solve(change_orbit_ask(opt)).
  if plan:error <> "" {
    set done:reason to plan:error.
    return done.
  }
  if opt:max_dv:istype("Scalar") and plan:dv > opt:max_dv {
    set done:reason to "needs " + round(plan:dv, 1) + " m/s, over max_dv " + opt:max_dv.
    return done.
  }
  local flown is lexicon("success", true, "reason", "").
  if plan:dv >= 0.05 {
    local soi is first(plan:ut).
    if soi <> "" {
      set done:reason to soi.
      return done.
    }
    set done:extra:burn_ut to round(plan:ut, 1).
    set flown to fly(plan).
  }
  local actual is lexicon().
  local extra is done:extra.
  for element in plan:wanted:keys {
    local figure is read(element).
    set actual[element] to figure.
    set extra[element + "_actual"] to change_orbit_round(element, figure).
  }
  if not flown:success {
    set done:reason to flown:reason.
    return done.
  }
  set done:reason to change_orbit_judge(plan:wanted, actual, opt:tolerance).
  return done.
}

// The angle (degrees) between the craft's orbit plane and the plane of tgt's orbit round the same body.
function change_orbit_rel_inc {
  parameter tgt.
  local own is vcrs(ship:position - ship:body:position, ship:velocity:orbit).
  local theirs is vcrs(tgt:position - tgt:body:position, tgt:velocity:orbit).
  return vang(own, theirs).
}

// An element's figure on the craft's orbit now (change_orbit_judge's names); next_patch_pe is -1 with no next patch.
function change_orbit_actual {
  parameter element, tgt.
  local o is ship:orbit.
  if element = "inc" {
    return o:inclination.
  }
  if element = "rel_inc" {
    return change_orbit_rel_inc(tgt).
  }
  if element = "lan" {
    return o:lan.
  }
  if element = "period" {
    return o:period.
  }
  if element = "pe" {
    return o:periapsis.
  }
  if element = "ap" {
    return o:apoapsis.
  }
  if element = "sma" {
    return o:semimajoraxis.
  }
  if element = "ecc" {
    return o:eccentricity.
  }
  if o:hasnextpatch {
    return o:nextpatch:periapsis.
  }
  return -1.
}

function change_orbit {
  parameter options is lexicon().
  local extra is lexicon("burn_ut", -1).
  local ctx is claudia_begin("change_orbit", options, change_orbit_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is change_orbit_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }
  local tgt is opt:target.
  if tgt:istype("String") and tgt <> "none" and bodyexists(tgt) {
    set tgt to body(tgt).
  }
  // ORBITBURN is made here, inline in the block (ADR 0007), not in a helper a Mission Script could call.
  local done is change_orbit_run(opt,
    { parameter ask. return addons:claudia:orbitburn(ask). },
    {
      parameter plan.
      add node(plan:ut, plan:radial, plan:normal, plan:prograde).
      // The burn is judged by the element it sets, not exec_node's residual.
      local burn_opts is claudia_burn_options(ctx, nextnode).
      set burn_opts:max_residual to 1000000.
      local flown is exec_node(burn_opts).
      claudia_add_result(ctx, flown).
      return flown.
    },
    { parameter element. return change_orbit_actual(element, tgt). },
    { parameter ut. return claudia_soi_first_at(ut). }).
  return claudia_finish(ctx, done:reason = "", done:reason, done:extra).
}
