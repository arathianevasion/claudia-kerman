// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block return: flies home from orbit around any moon to a periapsis in the atmosphere of the
// planet it orbits. The ejection is the mod's solver, ADDONS:CLAUDIA:RETURNBURN. Built on claudia_lib.ks (its
// patch, descent and Course Correction helpers) and exec_node.ks; block-only helpers are prefixed return_. kOS
// lets a function be named return when it is called as a value. The "//|" lines are Claudia's docs for it; the
// first is its summary.

@lazyglobal off.

//| return(options): flies home from orbit around any moon to its planet: an ejection
//| burn onto a path whose periapsis at the planet is target_pe, then a mid-course correction halfway to
//| reentry, both flown with exec_node. It ends after the correction, still in space; the craft comes
//| down anywhere on the planet. Call it as a value, `local res is return().`: a bare `return().` is kOS's
//| own return statement.
//| Options:
//|   target_pe [35000]: the periapsis altitude at the planet to aim for, m; inside its atmosphere the
//|     craft comes down.
//|   target_inc ["any"]: the inclination round the planet to arrive at, degrees from 0 to 180; "any" for none.
//|   window ["next"]: "next" ejects at the burn point's next pass once the craft can have turned to the
//|     burn, as transfer_to departs; a time (UT seconds) ejects at its first pass from that time. The
//|     block warps on rails to the pass however far off it is.
//|   target ["none"]: LATLNG(lat, lng), lexicon("lat", x, "lng", y) or "ksc": where to come down. The
//|     mid-course correction then aims the landing point the mod's reentry predictor gives (the craft's
//|     own drag, held retrograde as reentry flies it) at the target, with target_pe only drawing a path
//|     that misses the air towards it (and target_inc left out); the ejection burn is unchanged. "none"
//|     aims for target_pe only. The landing is hard to steer from here: read predicted_error_km, and when
//|     it is too far for the Mission Goal, capture at the planet and call deorbit instead. Aim a
//|     splashdown offshore.
//|   correction [true]: false leaves out the mid-course correction.
//|   max_duration ["auto"]: the time to the predicted reentry plus 900 s; a shorter one moves the correction sooner.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block; each burn gets them too.
//| Extra results: planet_pe (m, the predicted periapsis at the planet), reentry_ut (UT of the predicted
//| entry into the planet's atmosphere, or of periapsis if that is above it), correction_dv (m/s the
//| mid-course correction burned), predicted_geo (lexicon lat, lng: where the craft is predicted to come
//| down, from the path it is on after the correction, with the prediction leaving out parachutes and
//| lift; empty without a target or a prediction) and predicted_error_km (km from the target to
//| predicted_geo; -1 without one).
//| The correction is skipped when the prediction is already within 1 km of target_pe and 0.5 degrees of
//| target_inc (with a target: within 2 km of it) or the fix is under 0.1 m/s. When leaving the moon's
//| sphere of influence comes before halfway, the block warps through that first, however long it takes.
//| Fails with an option value it can't read, "not in orbit", "already orbiting <body>, a planet: lower
//| the periapsis with exec_node instead", "return starts from orbit around a moon, not <body>", "a
//| maneuver node is already planned: remove it first", "found no return: <why>", "found no correction:
//| <why>", "no return to <planet> after the ejection burn" (or "after the correction"), an exec_node
//| failure (that burn's node is left in place), or an abort.
//| Follow it with reentry, in the same sequence or the next (no Due Time needed): once the craft is
//| in the planet's sphere of influence (after the correction it usually is; if ship:body is still the
//| moon, coast until ship:orbit:nextpatcheta has passed), reentry warps to the atmosphere itself.
//| Tool: stage_performance for the delta-v; it plans its own burns, so don't plan them with predict_burn or solve_burn.
//| Example: `local res is return(). addons:claudia:report(res).`

// The block's own options and defaults.
function return_defaults {
  return lexicon("target_pe", 35000, "target_inc", "any", "window", "next", "correction", true, "target", "none").
}

// Whether the Mission Script gave a landing target.
function return_aims {
  parameter opt.
  return not (opt:target:istype("String") and opt:target = "none").
}

// What return asks ADDONS:CLAUDIA:REENTRYIMPACT: where the path orb round the planet comes down, and how
// far that is from site (lat, lng) whose ground is ground_alt m (sea level for water). The path begins
// at from_ut (the craft may not be on it yet), so the fall is taken from there. Pure.
function return_impact_query {
  parameter orb, from_ut, site, ground_alt.
  return lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt), "from_ut", from_ut,
    "orbit_sma", orb:semimajoraxis, "orbit_ecc", orb:eccentricity, "orbit_inc", orb:inclination,
    "orbit_lan", orb:lan, "orbit_argpe", orb:argumentofperiapsis, "orbit_mna", orb:meananomalyatepoch,
    "orbit_epoch", orb:epoch, "orbit_body", orb:body:name).
}

// "" or why an option value isn't one the block can use.
function return_unreadable {
  parameter opt.
  local pe is claudia_pe_unreadable(opt:target_pe).
  if pe <> "" {
    return pe.
  }
  if opt:target_inc:istype("String") {
    if opt:target_inc <> "any" {
      return "target_inc must be 'any' or degrees from 0 to 180, not '" + opt:target_inc + "'".
    }
  } else if not opt:target_inc:istype("Scalar") {
    return "target_inc must be 'any' or degrees from 0 to 180, not a " + opt:target_inc:typename.
  } else if opt:target_inc < 0 or opt:target_inc > 180 {
    return "target_inc must be 'any' or degrees from 0 to 180, not " + opt:target_inc.
  }
  if return_aims(opt) {
    local bad_target is claudia_target_unreadable(opt:target).
    if bad_target <> "" {
      return bad_target.
    }
  }
  local window is claudia_window_unreadable(opt:window).
  if window <> "" {
    return window.
  }
  return claudia_correction_unreadable(opt:correction).
}

// The ask of a block call (ADDONS:CLAUDIA:RETURNBURN or CORRECTIONBURN) with target_inc in it when the Mission
// Script gave one ("any" leaves it out). Pure.
function return_with_inc {
  parameter ask, target_inc.
  if target_inc:istype("Scalar") {
    set ask:target_inc to target_inc.
  }
  return ask.
}

// Whether the path p (claudia_patch's result round the planet) needs the plain Course Correction: its periapsis
// is over claudia_correction_good_m() from target_pe, or its inclination over 0.5 degrees from a target_inc
// given, or it has no path to the planet. Pure.
function return_needs_correction {
  parameter p, target_pe, target_inc.
  if p:istype("String") {
    return true.
  }
  if abs(p:orbit:periapsis - target_pe) > claudia_correction_good_m() {
    return true.
  }
  if target_inc:istype("Scalar") {
    return abs(p:orbit:inclination - target_inc) > 0.5.
  }
  return false.
}

// Seconds an orbit (semi-major axis a in m, negative for a hyperbola; eccentricity ecc; body mu)
// takes from radius (m), coming in, down to periapsis; 0 if it never comes down that far.
function return_time_to_pe {
  parameter a, ecc, mu, radius.
  if ecc < 0.000001 or radius <= a * (1 - ecc) {
    return 0.
  }
  local c is (1 - radius / a) / ecc.
  if a > 0 {
    // Kepler's equation with the eccentric anomaly; radius beyond apoapsis counts from apoapsis.
    local big_e is arccos(max(-1, c)).
    local mean is big_e * 3.141592653589793 / 180 - ecc * sin(big_e).
    return mean / sqrt(mu / a ^ 3).
  }
  local sinh_h is sqrt(c ^ 2 - 1).
  local mean is ecc * sinh_h - ln(c + sinh_h).
  return mean / sqrt(mu / abs(a) ^ 3).
}

// UT of the next periapsis of the orbit patch orb: mean anomaly is zero there, negative before it on
// a hyperbola, and an ellipse's wraps into its next orbit.
function return_pe_ut {
  parameter orb.
  local n is sqrt(orb:body:mu / abs(orb:semimajoraxis ^ 3)).
  local pi is 3.141592653589793.
  local delta is -orb:meananomalyatepoch * pi / 180.
  if delta < 0 {
    set delta to delta + 2 * pi.
  }
  return orb:epoch + delta / n.
}

// UT of the craft entering the atmosphere on the orbit patch orb (or of periapsis, if it stays
// above the atmosphere).
function return_reentry_ut {
  parameter orb.
  local pe_ut is return_pe_ut(orb).
  local top is orb:body:radius + orb:body:atm:height.
  return pe_ut - return_time_to_pe(orb:semimajoraxis, orb:eccentricity, orb:body:mu, top).
}

// Before the correction at mid (UT): leaving the moon's sphere of influence before then is waited out (on rails, however
// long), so the node is placed on the orbit round the planet the craft will be on. Returns "" or why the wait failed.
function return_leave_moon {
  parameter ctx, mid.
  if ship:orbit:hasnextpatch {
    local soi_ut is time:seconds + ship:orbit:nextpatcheta.
    if soi_ut < mid - 120 {
      return claudia_wait_until(ctx, soi_ut + 30).
    }
  }
  return "".
}

// km the predicted landing may be from the target before the aimed correction stops looking.
function return_aim_good_km {
  return 2.
}

// What the aimed correction (return_aimed_correction) lowers for the path orb: the km from the
// target to where it lands; over any real landing (no planet with moons is 2000 km round) when it misses
// the atmosphere, so the search first moves towards the air by target_pe, or 1e5 with no path to dest.
// p is claudia_patch's result for the path.
function return_aim_cost {
  parameter aim, p, target_pe.
  if p:istype("String") {
    return 1e5.
  }
  local km is aim(p:orbit, p:soi_ut).
  if km < 0 {
    return 1e4 + abs(p:orbit:periapsis - target_pe) / 1000.
  }
  return km.
}

// The correction with a landing target: a node at ut whose burn aims the landing the predictor gives at the target, by
// coordinate descent over its components, flown with exec_node. aim is a delegate of an orbit patch round dest and the UT
// it begins, that returns the km its predicted landing is from the target, or -1 if it doesn't come down; target_pe only
// draws a path that misses the atmosphere towards it. The search stops once the landing is within return_aim_good_km()
// of the target (the block asks only when it isn't yet), and nothing is burned when the fix is under 0.1 m/s. Returns
// lexicon("reason", "" or why the block fails, "dv", m/s burned).
function return_aimed_correction {
  parameter ctx, dest, target_pe, ut, aim.
  local done is lexicon("reason", "", "dv", 0).
  local good is return_aim_good_km().
  local nd is node(ut, 0, 0, 0).
  add nd.
  local found is claudia_descend({
    parameter x.
    set nd:radialout to x[0].
    set nd:normal to x[1].
    set nd:prograde to x[2].
    return return_aim_cost(aim, claudia_patch(nd:orbit, dest), target_pe).
  }, list(0, 0, 0), list(1, 1, 1), 10, good).
  set nd:radialout to found:x[0].
  set nd:normal to found:x[1].
  set nd:prograde to found:x[2].
  if nd:deltav:mag < 0.1 {
    remove nd.
    return done.
  }
  local burned is claudia_burn(ctx, nd).
  set done:dv to burned:dv_used.
  if not burned:success {
    set done:reason to burned:reason.
  }
  return done.
}

function return {
  parameter options is lexicon().
  local extra is lexicon("planet_pe", -1, "reentry_ut", -1, "correction_dv", 0, "predicted_geo", lexicon(), "predicted_error_km", -1).
  local ctx is claudia_begin("return", options, return_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  local unreadable is return_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }

  local moon is ship:body.
  if status <> "ORBITING" {
    return claudia_finish(ctx, false, "not in orbit: return starts from orbit around a moon", extra).
  }
  if not moon:hasbody {
    return claudia_finish(ctx, false, "return starts from orbit around a moon, not " + moon:name, extra).
  }
  if not moon:body:hasbody {
    return claudia_finish(ctx, false, "already orbiting " + moon:name + ", a planet: lower the periapsis with exec_node instead", extra).
  }
  local planet is moon:body.
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }

  // With a target, the correction aims the predicted landing at it. The prediction is the mod's reentry
  // predictor, a block-internal call (ADR 0007): inline here, asked of a path round the planet that the craft
  // may not be on yet.
  local aim is 0.
  local predict is 0.
  if return_aims(opt) {
    local site is claudia_target_site(opt:target, planet:name).
    if site:error <> "" {
      return claudia_finish(ctx, false, site:error, extra).
    }
    local ground_alt is planet:geopositionlatlng(site:lat, site:lng):terrainheight.
    set predict to {
      parameter orb, from_ut.
      return addons:claudia:reentryimpact(return_impact_query(orb, from_ut, site, ground_alt)).
    }.
    set aim to {
      parameter orb, from_ut.
      local landing is predict(orb, from_ut).
      if landing:error <> "" {
        return -1.
      }
      return landing:error_km.
    }.
  }

  // The ejection is the mod's solver, solved here from the live orbit: a block-internal call (ADR 0007), so it sits in
  // the block. Its earliest departure leaves time to turn to the burn, as transfer_to's does (claudia_earliest). A call
  // that threw gives only its error (#439), as a solve that found nothing does.
  local turn is addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime)).
  if turn:error <> "" {
    return claudia_finish(ctx, false, turn:error, extra).
  }
  local found is addons:claudia:returnburn(return_with_inc(lexicon("target_pe", opt:target_pe,
    "earliest_ut", claudia_earliest(ctx:start_ut, turn:turn_time, opt:window)), opt:target_inc)).
  if found:error <> "" {
    return claudia_finish(ctx, false, found:error, extra).
  }
  local nd is node(found:ut, found:radial, found:normal, found:prograde).
  add nd.
  local planned is claudia_patch(nd:orbit, planet).
  if planned:istype("String") {
    remove nd.
    return claudia_finish(ctx, false, "found no return: the ejection node does not reach " + planet:name, extra).
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to return_reentry_ut(planned:orbit) - ctx:start_ut + 900.
  }

  local ejected is claudia_burn(ctx, nd).
  if not ejected:success {
    return claudia_finish(ctx, false, ejected:reason, extra).
  }
  local home is claudia_patch(ship:orbit, planet).
  if home:istype("String") {
    return claudia_finish(ctx, false, "no return to " + planet:name + " after the ejection burn", extra).
  }

  if opt:correction {
    local ticked is claudia_tick(ctx, 0).
    if ticked <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, ticked, extra).
    }
    // Halfway to reentry, once out of the moon's sphere of influence; sooner if max_duration ends first (#368).
    local mid is (time:seconds + return_reentry_ut(home:orbit)) / 2.
    local waited is return_leave_moon(ctx, mid).
    if waited <> "" {
      claudia_make_safe().
      return claudia_finish(ctx, false, waited, extra).
    }
    local fixed is lexicon("reason", "", "dv", 0).
    local needed is false.
    if return_aims(opt) {
      set needed to return_aim_cost(aim, claudia_patch(ship:orbit, planet), opt:target_pe) > return_aim_good_km().
    } else {
      set needed to return_needs_correction(claudia_patch(ship:orbit, planet), opt:target_pe, opt:target_inc).
    }
    if needed {
      // 300 s to turn to and lead the burn, as before #450, so the aimed correction's timing is unchanged.
      local when is claudia_correction_ut(mid, time:seconds, 300, ctx:start_ut, opt:max_duration).
      if when:reason <> "" {
        return claudia_finish(ctx, false, when:reason, extra).
      }
      if return_aims(opt) {
        set fixed to return_aimed_correction(ctx, planet, opt:target_pe, when:ut, aim).
      } else {
        // The Course Correction is the mod's solver, a block-internal call (ADR 0007), so it sits in the block.
        set fixed to claudia_fly_correction(ctx, addons:claudia:correctionburn(
          return_with_inc(claudia_correction_ask(planet:name, opt:target_pe, when:ut), opt:target_inc))).
      }
    }
    set extra:correction_dv to round(fixed:dv, 2).
    if fixed:reason <> "" {
      return claudia_finish(ctx, false, fixed:reason, extra).
    }
    set home to claudia_patch(ship:orbit, planet).
    if home:istype("String") {
      return claudia_finish(ctx, false, "no return to " + planet:name + " after the correction", extra).
    }
  }
  set extra:planet_pe to round(home:orbit:periapsis).
  set extra:reentry_ut to round(return_reentry_ut(home:orbit)).
  if return_aims(opt) {
    // Where the path flown comes down, after the correction's own error.
    local landing is predict(home:orbit, home:soi_ut).
    if landing:error = "" {
      set extra:predicted_geo to claudia_geo(landing).
      set extra:predicted_error_km to round(landing:error_km, 2).
    }
  }
  return claudia_finish(ctx, true, "", extra).
}
