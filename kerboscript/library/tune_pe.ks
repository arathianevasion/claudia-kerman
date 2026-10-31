// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block tune_pe: sets the periapsis of the current orbit, or of the next patch (the orbit
// after a sphere-of-influence change), by planning one maneuver node at a point it chooses and flying it
// with exec_node. Built on claudia_lib.ks; block-only helpers are prefixed tune_pe_. The "//|" lines
// are Claudia's docs for it; the first is its summary.
// It keeps its kerboscript maths rather than ADDONS:CLAUDIA:ORBITBURN (#444): patch "current" is closed form on the
// live orbit, and patch "next" searches the node against kOS's own patched conics (nextpatch), the path KSP flies.

@lazyglobal off.

//| tune_pe(options): sets the periapsis, this orbit's or the next patch's.
//| One burn it places itself. Use it to fine-tune the periapsis at a moon before arrival or at Kerbin on the way
//| home; the burn is planned as a maneuver node and flown by exec_node.
//| Options:
//|   value [required]: the periapsis altitude wanted, m above the surface.
//|   patch ["current"]: "current" burns at the apoapsis of the orbit the craft is on (the cheapest point
//|     for moving the periapsis) and needs a closed orbit whose apoapsis comes before any sphere-of-influence
//|     change. If the periapsis is inside the atmosphere (or below the surface) and the craft reaches it
//|     before that apoapsis, it burns instead as soon as a burn can be led (about 2 minutes on), the
//|     cheapest point before the periapsis, so the craft is not lost to it; "next" burns near the start or halfway to the sphere-of-influence change (the cheaper) and aims the periapsis of the orbit
//|     after it (at the Mun before arrival, at Kerbin on the way home), earlier if the burn would not end 60 s before the change.
//|   tolerance [1000]: m the periapsis may end from value; more fails the block.
//|   max_dv [50]: m/s the burn may cost; more fails the block before any burn.
//|   max_duration ["auto"]: as for exec_node (the wait to the burn, twice the burn, plus 300 s).
//|   plus min_ec_pct, abort_on_part_loss, stage_limit and drop_empty as for every block.
//| Extra results: pe_actual (m, the periapsis of the chosen patch when the block ended), burn_ut (UT of
//| the burn; -1 if none was needed). Succeeds at once, with no burn, if the periapsis is already within
//| tolerance. Fails with "value is required", an option value it can't read, "a maneuver node is already
//| planned: remove it first", "no next patch" (patch "next" with no sphere-of-influence change ahead),
//| "the orbit escapes" or "the apoapsis comes after the sphere-of-influence change" (patch "current"),
//| "the periapsis is inside the atmosphere or the surface and comes in <s> s, too soon to plan and fly a
//| burn before it" (patch "current", under 5 minutes to it), "no prograde burn at the burn point reaches
//| a periapsis of <m> m from the centre",
//| "value is above the apoapsis", "the sphere-of-influence change is too close", "the burn (<s> s from <s>
//| s before its node) does not fit before the sphere-of-influence change in <s> s" (patch "next"), "needs <dv> m/s, over
//| max_dv <max>", "found no burn that reaches <value> m", the reasons exec_node gives (no thrust, out of
//| fuel, an abort), or "periapsis <pe> m is over tolerance <t> m from <value> m"; on an exec_node failure
//| the node is left in place.
//| Tool: solve_burn (set_pe or next_patch_pe) for the delta-v to budget; pass tune_pe the value, not that burn.
//| Example: `local res is tune_pe(lexicon("value", 50000, "patch", "next")). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function tune_pe_defaults {
  return lexicon("value", "required", "patch", "current", "tolerance", 1000, "max_dv", 50).
}

// "" or why an option value isn't one the block can use.
function tune_pe_unreadable {
  parameter opt.
  if opt:value:istype("String") and opt:value = "required" {
    return "value is required".
  }
  if not opt:value:istype("Scalar") or opt:value <= 0 {
    return "value must be metres above 0, not " + opt:value.
  }
  if not opt:patch:istype("String") or (opt:patch <> "current" and opt:patch <> "next") {
    return "patch must be 'current' or 'next', not " + opt:patch.
  }
  if not opt:tolerance:istype("Scalar") or opt:tolerance <= 0 {
    return "tolerance must be metres above 0, not " + opt:tolerance.
  }
  if not opt:max_dv:istype("Scalar") or opt:max_dv <= 0 {
    return "max_dv must be m/s above 0, not " + opt:max_dv.
  }
  return "".
}

// Where to burn, from plain values: patch, ecc (eccentricity), eta_ap, eta_pe and period (s, of the current
// orbit), pe_unsafe (the periapsis is inside the atmosphere or below the surface), has_next and soi_eta (s to
// the sphere-of-influence change). Returns lexicon("error", "" or why not, "in", s from now, "early", true if
// the burn is not at the apoapsis).
function tune_pe_burn_point {
  parameter patch, ecc, eta_ap, eta_pe, period, pe_unsafe, has_next, soi_eta.
  local point is lexicon("error", "", "in", 0, "early", false).
  if patch = "next" {
    if not has_next {
      set point:error to "no next patch: the orbit stays in this sphere of influence".
    } else if soi_eta < 300 {
      set point:error to "the sphere-of-influence change is too close (" + round(soi_eta) + " s) to plan and fly a burn".
    } else {
      set point:in to soi_eta / 2.
    }
    return point.
  }
  if ecc >= 1 {
    set point:error to "the orbit escapes (ecc " + round(ecc, 3) + "): no apoapsis to burn at".
    return point.
  }
  // An apoapsis under 2 minutes off is too soon to turn and lead the burn: take the next one.
  set point:in to eta_ap.
  if eta_ap < 120 {
    set point:in to eta_ap + period.
  }
  // The craft reaches an unsafe periapsis before that burn: burn before it, as soon as a burn can be led.
  // Past the apoapsis a burn is cheapest the further it is from the periapsis, so the earliest is cheapest.
  if pe_unsafe and eta_pe < point:in {
    if eta_pe < 300 {
      set point:error to "the periapsis is inside the atmosphere or the surface and comes in " + round(eta_pe) + " s, too soon to plan and fly a burn before it".
      return point.
    }
    set point:in to 120.
    set point:early to true.
  }
  if has_next and soi_eta < point:in {
    set point:error to "the apoapsis comes after the sphere-of-influence change (in " + round(soi_eta) + " s): use patch next".
  }
  return point.
}

// The burn points (s from now) patch next tries, soi_eta s to the sphere-of-influence change: near the start (300 s on,
// time to turn and lead) and halfway. An encounter is mostly fixed in timing, which is cheapest early: on f623 (#538) the
// retrograde fix cost about 25 m/s at 300 s and over 100 halfway. Under 600 s halfway the start is no earlier, so halfway alone.
function tune_pe_next_times {
  parameter soi_eta.
  local mid is soi_eta / 2.
  if mid >= 600 {
    return list(300, mid).
  }
  return list(mid).
}

// The prograde burn (m/s, negative to slow down) at the apoapsis, rad_ap m from the body's centre on an
// orbit of semi-major axis sma, that leaves the periapsis rad_pe m from the centre; "error" is "" or why
// not (the periapsis wanted is above the apoapsis).
function tune_pe_apoapsis_burn {
  parameter mu, rad_ap, sma, rad_pe.
  local burn is lexicon("error", "", "prograde", 0).
  if rad_pe > rad_ap {
    set burn:error to "value is above the apoapsis (" + round(rad_ap) + " m from the centre): a burn there can't lift the periapsis past it".
    return burn.
  }
  local sma_new is (rad_ap + rad_pe) / 2.
  set burn:prograde to sqrt(mu * (2 / rad_ap - 1 / sma_new)) - sqrt(mu * (2 / rad_ap - 1 / sma)).
  return burn.
}

// The prograde burn (m/s, negative to slow down) at a point rad m from the body's centre, moving at speed m/s
// with radial (up) speed vrad, that leaves the periapsis rad_pe m from the centre; "error" is "" or why not.
// At the periapsis the speed is h / rad_pe, which gives the speed after the burn in closed form.
function tune_pe_burn_at {
  parameter mu, rad, speed, vrad, rad_pe.
  local burn is lexicon("error", "", "prograde", 0).
  if rad_pe >= rad {
    set burn:error to "value is not below the burn point (" + round(rad) + " m from the centre): a burn there can't set the periapsis".
    return burn.
  }
  local tangential is sqrt(max(speed * speed - vrad * vrad, 0)).
  local k is 1 - (rad * tangential / speed) ^ 2 / rad_pe ^ 2.
  if k >= 0 {
    set burn:error to "no prograde burn at the burn point reaches a periapsis of " + round(rad_pe) + " m from the centre".
    return burn.
  }
  set burn:prograde to sqrt(2 * mu * (1 / rad - 1 / rad_pe) / k) - speed.
  return burn.
}

// "" or why a burn of dv m/s is over the budget max_dv.
function tune_pe_over_budget {
  parameter dv, max_dv.
  if abs(dv) > max_dv {
    return "needs " + round(abs(dv), 1) + " m/s, over max_dv " + max_dv.
  }
  return "".
}

// The periapsis (m) of the patch the block tunes, or -1 if there is no such patch.
function tune_pe_actual {
  parameter patch.
  if patch = "next" {
    if not ship:orbit:hasnextpatch {
      return -1.
    }
    return ship:orbit:nextpatch:periapsis.
  }
  return ship:orbit:periapsis.
}

// Searches nd's burn for the one that puts the next patch's periapsis at value: sets nd's
// components and returns the m the periapsis is still off by.
function tune_pe_search {
  parameter nd, value, tolerance.
  local dest is ship:orbit:nextpatch:body.
  local found is claudia_descend({
    parameter x.
    set nd:radialout to x[0].
    set nd:normal to x[1].
    set nd:prograde to x[2].
    return abs(claudia_pe_error(nd:orbit, dest, value)).
  }, list(0, 0, 0), list(1, 1, 1), 10, tolerance / 4).
  set nd:radialout to found:x[0].
  set nd:normal to found:x[1].
  set nd:prograde to found:x[2].
  return found:cost.
}

// The next-patch burn's node UT so the burn ends 60 s before the sphere-of-influence change at soi_ut (#291): node_ut
// if it already does, else moved earlier to the latest that does. The burn starts lead s before its node and lasts
// burn_time s (claudia_burn_timing; burn_time -1, unknown, is left to exec_node). A burn that would start under 120 s
// from now, too soon to turn to and lead, doesn't fit. Returns lexicon("error", "" or why it doesn't fit, "ut", the node's
// UT, "moved", true if it moved). Pure.
function tune_pe_fit {
  parameter now, node_ut, soi_ut, burn_time, lead.
  local fit is lexicon("error", "", "ut", node_ut, "moved", false).
  if burn_time < 0 {
    return fit.
  }
  local latest is soi_ut - 60 - (burn_time - lead).
  if node_ut > latest {
    set fit:ut to latest.
    set fit:moved to true.
  }
  if fit:ut - lead < now + 120 {
    set fit:error to "the burn (" + round(burn_time) + " s from " + round(lead) + " s before its node) does not fit before the sphere-of-influence change in "
      + round(soi_ut - now) + " s".
  }
  return fit.
}

// Places the next-patch burn: searches nd at its UT, then moves it earlier and searches again while the burn would not
// end before the sphere-of-influence change (tune_pe_fit); a burn still too long after three moves doesn't fit.
// Returns lexicon("error", "" or why not, "off_by", m the periapsis is still off by).
function tune_pe_place_next {
  parameter nd, opt, soi_ut.
  local placed is lexicon("error", "", "off_by", 0).
  local tries is 0.
  until false {
    set placed:off_by to tune_pe_search(nd, opt:value, opt:tolerance).
    local timing is claudia_burn_timing(nd:deltav:mag, claudia_burn_stages(opt:stage_limit), nd:eta, "half_burn").
    local fit is tune_pe_fit(time:seconds, nd:time, soi_ut, timing:burn_time, timing:lead).
    if fit:error <> "" {
      set placed:error to fit:error.
      return placed.
    }
    if not fit:moved {
      return placed.
    }
    if tries >= 3 {
      set placed:error to "the burn does not fit before the sphere-of-influence change: it still runs into it after moving the node three times".
      return placed.
    }
    set nd:time to fit:ut.
    set tries to tries + 1.
  }
}

// Plans nd at each of the burn points times (s from now) with tune_pe_place_next and leaves it at the cheapest burn that
// reaches the periapsis within tolerance and max_dv; if none does, at the one that came closest. Returns tune_pe_place_next's
// lexicon for that burn point.
function tune_pe_place_best {
  parameter nd, opt, soi_ut, times.
  local best is lexicon("error", "", "off_by", 0).
  local best_score is 1e30.
  local best_time is 0.
  local best_x is list(0, 0, 0).
  local now is time:seconds.
  for t in times {
    set nd:radialout to 0.
    set nd:normal to 0.
    set nd:prograde to 0.
    set nd:time to now + t.
    local placed is tune_pe_place_next(nd, opt, soi_ut).
    local dv is nd:deltav:mag.
    // A burn that works scores its delta-v; one that doesn't scores above any of those, by how far off it is (a burn that
    // doesn't fit at all, last).
    local score is dv.
    if placed:error <> "" {
      set score to 1e9.
    } else if placed:off_by > opt:tolerance or dv > opt:max_dv {
      set score to 1e6 + placed:off_by.
    }
    if score < best_score {
      set best to placed.
      set best_score to score.
      set best_time to nd:time.
      set best_x to list(nd:radialout, nd:normal, nd:prograde).
    }
  }
  set nd:time to best_time.
  set nd:radialout to best_x[0].
  set nd:normal to best_x[1].
  set nd:prograde to best_x[2].
  return best.
}

function tune_pe {
  parameter options is lexicon().
  local extra is lexicon("pe_actual", -1, "burn_ut", -1).
  local ctx is claudia_begin("tune_pe", options, tune_pe_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is tune_pe_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }
  local o is ship:orbit.
  set extra:pe_actual to round(tune_pe_actual(opt:patch)).
  if extra:pe_actual >= 0 and abs(extra:pe_actual - opt:value) <= opt:tolerance {
    return claudia_finish(ctx, true, "", extra).
  }

  local soi_eta is 0.
  if o:hasnextpatch {
    set soi_eta to o:nextpatcheta.
  }
  local eta_ap is 0.
  local period is 0.
  local eta_pe is 0.
  local pe_unsafe is false.
  if o:eccentricity < 1 {
    set eta_ap to eta:apoapsis.
    set period to o:period.
    set eta_pe to eta:periapsis.
    local safe_pe is 0.
    if ship:body:atm:exists {
      set safe_pe to ship:body:atm:height.
    }
    set pe_unsafe to o:periapsis < safe_pe.
  }
  local point is tune_pe_burn_point(opt:patch, o:eccentricity, eta_ap, eta_pe, period, pe_unsafe, o:hasnextpatch, soi_eta).
  if point:error <> "" {
    return claudia_finish(ctx, false, point:error, extra).
  }
  local burn_ut is time:seconds + point:in.

  local nd is node(burn_ut, 0, 0, 0).
  if opt:patch = "current" {
    local burn is lexicon().
    if point:early {
      local pos is positionat(ship, burn_ut) - ship:body:position.
      local vel is velocityat(ship, burn_ut):orbit.
      set burn to tune_pe_burn_at(ship:body:mu, pos:mag, vel:mag, vdot(vel, pos:normalized), ship:body:radius + opt:value).
    } else {
      set burn to tune_pe_apoapsis_burn(ship:body:mu, ship:body:radius + o:apoapsis, o:semimajoraxis, ship:body:radius + opt:value).
    }
    if burn:error <> "" {
      return claudia_finish(ctx, false, burn:error, extra).
    }
    local over is tune_pe_over_budget(burn:prograde, opt:max_dv).
    if over <> "" {
      return claudia_finish(ctx, false, over, extra).
    }
    set nd:prograde to burn:prograde.
    add nd.
  } else {
    add nd.
    local placed is tune_pe_place_best(nd, opt, time:seconds + soi_eta, tune_pe_next_times(soi_eta)).
    if placed:error <> "" {
      remove nd.
      return claudia_finish(ctx, false, placed:error, extra).
    }
    set burn_ut to nd:time.
    local off_by is placed:off_by.
    if off_by > opt:tolerance {
      remove nd.
      return claudia_finish(ctx, false, "found no burn that reaches " + round(opt:value) + " m (best is " + round(off_by) + " m off)", extra).
    }
    local over is tune_pe_over_budget(nd:deltav:mag, opt:max_dv).
    if over <> "" {
      remove nd.
      return claudia_finish(ctx, false, over, extra).
    }
  }
  set extra:burn_ut to round(burn_ut, 1).

  // The burn is judged by the periapsis it leaves, not exec_node's residual.
  local burn_opts is claudia_burn_options(ctx, nd).
  set burn_opts:max_residual to 1000000.
  local flown is exec_node(burn_opts).
  claudia_add_result(ctx, flown).
  set extra:pe_actual to round(tune_pe_actual(opt:patch)).
  if not flown:success {
    return claudia_finish(ctx, false, flown:reason, extra).
  }
  if extra:pe_actual < 0 or abs(extra:pe_actual - opt:value) > opt:tolerance {
    return claudia_finish(ctx, false, "periapsis " + extra:pe_actual + " m is over tolerance " + opt:tolerance + " m from " + round(opt:value) + " m", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
