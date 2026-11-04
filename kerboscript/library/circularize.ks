// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block circularize: makes the orbit circular at apoapsis, periapsis or an altitude, by
// planning one maneuver node and flying it with exec_node. Built on claudia_lib.ks; block-only
// helpers are prefixed circularize_. The "//|" lines are Claudia's docs for it; the first is its summary.
// It keeps its kerboscript maths rather than ADDONS:CLAUDIA:ORBITBURN (#444): the burn is closed form on the live
// orbit, already solved when the block flies, and needs no Game Snapshot.

@lazyglobal off.

//| circularize(options): circular orbit at ap, pe or an altitude.
//| One burn, planned as a maneuver node and flown by exec_node. After launch, in the same Mission
//| Script, it finishes the orbit; at "pe" inside a new body's SOI it is the capture burn.
//| Options:
//|   at ["ap"]: where to burn: "ap", "pe" or an altitude in m between pe and ap (the next pass).
//|   tolerance [0.01]: the eccentricity it must end within; more fails the block. Unset: skips a burn under 5 m/s at ecc under 0.03, pe safe.
//|   finish_fraction [0.05]: as for exec_node, judged by ap and pe within this share of the burn's radius (from the
//|     body's centre), pe above the atmosphere.
//|   max_duration ["auto"]: as for exec_node.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block.
//| Extra results: ecc (the eccentricity when the block ended), stop ("node"; "ecc_min": cut short
//| where the orbit was roundest), residual (m/s the node still needed), finished_short, skipped (skip_note says why).
//| Succeeds at once, with no burn, if the orbit is already within tolerance. Fails with "no apoapsis"
//| on an escape orbit (use "pe"), an altitude the orbit doesn't pass, a burn point inside the
//| atmosphere or below the surface, the reasons exec_node gives (no thrust, out of fuel, an abort), or
//| "eccentricity <e> is over tolerance <t>"; on failure the node is left in place.
//| Example: `local res is launch(lexicon("target_ap", 80000)). if res:success { set res to circularize(). }
//| addons:claudia:report(res).`

// The block's own options and defaults (max_duration "auto" is resolved by exec_node).
function circularize_defaults {
  return lexicon("at", "ap", "tolerance", 0.01, "finish_fraction", 0.05).
}

// "" or why the at option isn't one the block knows.
function circularize_unreadable {
  parameter at_opt.
  if at_opt:istype("String") {
    if at_opt <> "ap" and at_opt <> "pe" {
      return "at must be 'ap', 'pe' or an altitude in m, not '" + at_opt + "'".
    }
  }
  return "".
}

// The burn that makes the orbit (gravitational parameter mu, semi-major axis sma in m, negative for
// an escape orbit, eccentricity ecc) circular where the craft is rad m from the body's centre, climbing
// or falling there: lexicon("prograde", m/s, "radial", m/s, outward), as for a maneuver node.
function circularize_burn {
  parameter mu, rad, sma, ecc, climbing.
  local v_now is sqrt(mu * (2 / rad - 1 / sma)).
  local v_circ is sqrt(mu / rad).
  local h is sqrt(mu * sma * (1 - ecc ^ 2)).
  local cos_gamma is min(1, h / (rad * v_now)).
  local sin_gamma is sqrt(1 - cos_gamma ^ 2).
  if not climbing {
    set sin_gamma to -sin_gamma.
  }
  return lexicon("prograde", v_circ * cos_gamma - v_now, "radial", -v_circ * sin_gamma).
}

// The true anomaly (degrees, 0 to 180: the climbing pass; 360 minus it is the falling one) at which
// an orbit of sma and ecc is rad m from the body's centre, or -1 if it never is.
function circularize_anomaly {
  parameter rad, sma, ecc.
  if ecc <= 0 {
    return -1.
  }
  local c is (sma * (1 - ecc ^ 2) / rad - 1) / ecc.
  if abs(c) > 1 + 1e-9 {
    return -1.
  }
  return arccos(max(-1, min(1, c))).
}

// Seconds to go from true anomaly now_deg to target_deg on an elliptical orbit of ecc and period.
function circularize_time_to {
  parameter now_deg, target_deg, ecc, period.
  local diff is circularize_mean_anomaly(target_deg, ecc) - circularize_mean_anomaly(now_deg, ecc).
  return claudia_wrap_angle(diff) / 360 * period.
}

// The mean anomaly (degrees) at true anomaly nu (degrees) on an elliptical orbit of ecc.
function circularize_mean_anomaly {
  parameter nu, ecc.
  local e_anom is arctan2(sqrt(1 - ecc ^ 2) * sin(nu), ecc + cos(nu)).
  // A literal rather than CONSTANT:PI, so this stays pure.
  return e_anom - ecc * sin(e_anom) * 180 / 3.141592653589793.
}

// Where and how to burn: lexicon("error", "" or why not, "ut", s, "prograde", m/s, "radial", m/s, "rad", the burn
// point's m from the body's centre, "floor_alt", m the periapsis must stay above).
function circularize_plan {
  parameter at_opt.
  local o is ship:orbit.
  local mu is ship:body:mu.
  local radius is ship:body:radius.
  local plan is lexicon("error", "", "ut", 0, "prograde", 0, "radial", 0, "rad", 0, "floor_alt", 0).
  local rad is 0.
  local climbing is true.
  if at_opt:istype("String") and at_opt = "ap" {
    if o:eccentricity >= 1 {
      set plan:error to "no apoapsis: the orbit escapes (ecc " + round(o:eccentricity, 3) + "); circularize at pe".
      return plan.
    }
    set rad to radius + o:apoapsis.
    set plan:ut to time:seconds + eta:apoapsis.
  } else if at_opt:istype("String") {
    set rad to radius + o:periapsis.
    set plan:ut to time:seconds + eta:periapsis.
  } else {
    if o:eccentricity >= 1 {
      set plan:error to "an altitude needs a closed orbit (ecc " + round(o:eccentricity, 3) + "); circularize at pe".
      return plan.
    }
    set rad to radius + at_opt.
    local nu is circularize_anomaly(rad, o:semimajoraxis, o:eccentricity).
    if nu < 0 {
      set plan:error to "the orbit doesn't pass " + round(at_opt) + " m (pe " + round(o:periapsis) + " m, ap " + round(o:apoapsis) + " m)".
      return plan.
    }
    local up_in is circularize_time_to(o:trueanomaly, nu, o:eccentricity, o:period).
    local down_in is circularize_time_to(o:trueanomaly, 360 - nu, o:eccentricity, o:period).
    set climbing to up_in <= down_in.
    set plan:ut to time:seconds + min(up_in, down_in).
  }
  local floor_alt is 0.
  if ship:body:atm:exists {
    set floor_alt to ship:body:atm:height.
  }
  if rad <= radius + floor_alt {
    set plan:error to "the burn point at " + round(rad - radius) + " m is inside the atmosphere or below the surface".
    return plan.
  }
  local burn is circularize_burn(mu, rad, o:semimajoraxis, o:eccentricity, climbing).
  set plan:prograde to burn:prograde.
  set plan:radial to burn:radial.
  set plan:rad to rad.
  set plan:floor_alt to floor_alt.
  return plan.
}

// The block's extra results before it burns: stop "" (no burn ended), residual -1 (no node), not finished short.
function circularize_extra {
  parameter ecc.
  return lexicon("ecc", round(ecc, 5), "stop", "", "residual", -1, "finished_short", false, "skipped", false).
}

// Whether an orbit over tolerance still finishes the circularization (#577): apoapsis ap and periapsis pe (m above a
// body of radius m) both within frac of the burn point's radius rad (m from the body's centre), and pe above floor_alt.
// An escape orbit's negative ap is never within. Pure.
function circularize_near {
  parameter ap, pe, radius, rad, frac, floor_alt.
  return pe > floor_alt and abs(radius + ap - rad) <= frac * rad and abs(radius + pe - rad) <= frac * rad.
}

// A node that finishes the circularization where the craft is now (#577): circularize_burn for the orbit as it is.
function circularize_trim_node {
  local o is ship:orbit.
  local burn is circularize_burn(ship:body:mu, ship:body:radius + ship:altitude, o:semimajoraxis, o:eccentricity, ship:verticalspeed >= 0).
  return node(time:seconds, burn:radial, 0, burn:prograde).
}

// exec_node's resolve (#673): circularize_burn again for the orbit as it is after the turn, at nd's UT (read with nd zeroed,
// just before it, so on the coast's patch). Returns "".
function circularize_resolve {
  parameter nd.
  set nd:radialout to 0.
  set nd:prograde to 0.
  local at_ut is nd:time - 0.01.
  local pos is positionat(ship, at_ut) - ship:body:position.
  local vel is velocityat(ship, at_ut):orbit.
  local o is ship:orbit.
  local burn is circularize_burn(ship:body:mu, pos:mag, o:semimajoraxis, o:eccentricity, vdot(vel, pos) >= 0).
  set nd:radialout to burn:radial.
  set nd:prograde to burn:prograde.
  return "".
}

function circularize {
  parameter options is lexicon().
  local extra is circularize_extra(ship:orbit:eccentricity).
  local ctx is claudia_begin("circularize", options, circularize_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local given is options:haskey("tolerance").
  local unreadable is circularize_unreadable(opt:at).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if ship:orbit:eccentricity <= opt:tolerance {
    return claudia_finish(ctx, true, "", extra).
  }

  local plan is circularize_plan(opt:at).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  // The burn is timed on this patch: with a sphere-of-influence change before it (transfer_to then circularize, no coast:
  // 20ac, #610) the craft would fly past its encounter to burn at the old body's apsis. Refuse, leaving it on its path.
  local first is claudia_soi_first_at(plan:ut).
  if first <> "" {
    return claudia_finish(ctx, false, first, extra).
  }
  local worth is addons:claudia:burnworth(lexicon("block", "circularize", "given", given, "dv", sqrt(plan:prograde ^ 2 + plan:radial ^ 2),
    "ecc", ship:orbit:eccentricity)).
  if worth:skip {
    return claudia_skip(ctx, worth, extra).
  }
  local nd is node(plan:ut, plan:radial, 0, plan:prograde).
  add nd.
  // The burn is judged by the eccentricity it leaves, not exec_node's residual.
  local flight is claudia_common_opts(opt).
  set flight:node to nd.
  set flight:max_residual to 1000000.
  set flight:max_duration to opt:max_duration. // circularize hands its own budget on; claudia_common_opts leaves max_duration out
  // A long burn passes its node half way through and the node swings; stop where the orbit is roundest (#532).
  set flight:stop_at_min to "ecc".
  // Solved again after the turn, whose RCS can move the orbit (#673).
  set flight:resolve to circularize_resolve@.
  // The burn is traced to the log (ADDONS:CLAUDIA:BLOCKTRACE, #531): its start, and its end with the delta-v used.
  addons:claudia:blocktrace("circularize", lexicon("phase", "start", "burn_ut", round(plan:ut, 1), "prograde", round(plan:prograde, 1),
    "radial", round(plan:radial, 1), "ap", round(ship:orbit:apoapsis), "pe", round(ship:orbit:periapsis), "ecc", round(ship:orbit:eccentricity, 5),
    "mass", round(ship:mass, 3), "stage", stage:number)).
  local flown is exec_node(flight).
  addons:claudia:blocktrace("circularize", lexicon("phase", "end", "dv_used", round(flown:dv_used, 1), "ap", round(ship:orbit:apoapsis),
    "pe", round(ship:orbit:periapsis), "ecc", round(ship:orbit:eccentricity, 5), "mass", round(ship:mass, 3), "stage", stage:number,
    "ok", flown:success)).
  set ctx:dv_used to flown:dv_used.
  claudia_note_dropped(ctx, flown).
  set extra:ecc to round(ship:orbit:eccentricity, 5).
  // exec_node says which stop ended the burn once it has read its options.
  if flown:haskey("stop") {
    set extra:stop to flown:stop.
  }
  set extra:residual to flown:residual.
  if not flown:success {
    return claudia_finish(ctx, false, flown:reason, extra).
  }
  // A burn a few percent short (#577) is trimmed once at low throttle through a node where the craft is, not failed.
  if ship:orbit:eccentricity > opt:tolerance and circularize_near(ship:orbit:apoapsis, ship:orbit:periapsis, ship:body:radius, plan:rad, opt:finish_fraction, plan:floor_alt) {
    // exec_node's staging dropped parts on purpose; the trim's ticks count from here.
    set ctx:parts to ship:parts:length.
    set ctx:last_ut to time:seconds.
    local trim is circularize_trim_node().
    add trim.
    local trimmed is claudia_trim(ctx, { return trim:deltav. }, 0.1).
    remove trim.
    set extra:ecc to round(ship:orbit:eccentricity, 5).
    if trimmed <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, trimmed, extra).
    }
    claudia_release_controls().
  }
  if ship:orbit:eccentricity > opt:tolerance {
    if not circularize_near(ship:orbit:apoapsis, ship:orbit:periapsis, ship:body:radius, plan:rad, opt:finish_fraction, plan:floor_alt) {
      return claudia_finish(ctx, false, "eccentricity " + round(ship:orbit:eccentricity, 4) + " is over tolerance " + opt:tolerance, extra).
    }
    set extra:finished_short to true.
  }
  return claudia_finish(ctx, true, "", extra).
}
