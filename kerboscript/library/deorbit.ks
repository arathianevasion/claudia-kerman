// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block deorbit: from orbit round a body with an atmosphere, plans the one burn that brings
// the craft down on a target and flies it with exec_node. The burn comes from the mod's reentry
// predictor (ADDONS:CLAUDIA:DEORBITBURN: the Kepler orbit to the atmosphere, then the craft's own drag
// cubes held surface retrograde, as reentry flies it), and the landing is predicted again from the orbit
// actually flown (ADDONS:CLAUDIA:REENTRYIMPACT). Both are block-internal calls the Script Check allows
// only in the Core Library (ADR 0007), so they stay inline in deorbit. Built on claudia_lib.ks and
// exec_node.ks; block-only helpers are prefixed deorbit_. The "//|" lines are Claudia's docs for it;
// the first is its summary.

@lazyglobal off.

//| deorbit(options): from orbit round a body with an atmosphere (Kerbin, Eve, Duna...), burns
//| once to bring the craft down near a target. It chooses the burn itself with the mod's
//| reentry predictor (the craft's own drag, held retrograde as reentry flies it) and flies it with
//| exec_node; it fails rather than burn when no burn in max_wait lands within accuracy_km. It does
//| not fly the descent: call reentry next.
//| Options:
//|   target ["auto"]: LATLNG(lat, lng), lexicon("lat", x, "lng", y), or "ksc" (auto at Kerbin). Aim a
//|     splashdown offshore of a coast, so a miss lands in the water.
//|   max_wait ["auto"]: s the burn may wait; "auto" is one turn of the body (about 6 h at Kerbin). The
//|     burn is at least 4 minutes ahead. Give expected_duration_s that wait.
//|   accuracy_km [5]: km the predicted landing may be from the target; a worse best burn fails the
//|     block before any burn, and a flown burn predicted worse reports success false.
//|   max_dv ["auto"]: m/s the burn may cost; the cheapest burn within accuracy_km is taken. "auto" is the
//|     delta-v left less 50 m/s or 10%, plus a powered landing's need without chutes; give it if stock
//|     reports none left.
//|   separate ["none"]: what to drop once the burn is flown, so only the capsule re-enters: a stage
//|     number (its decouplers), or a list of part names or tags (or
//|     one), as drop_parts. The burn is chosen for the craft that stays (its mass and drag; it
//|     pays the burn if its own engine burns); the landing is predicted again once they have gone. Give it for a transfer stage that would otherwise ride
//|     down with the capsule.
//|   max_duration ["auto"]: the wait to the burn plus 900 s.
//|   plus min_ec_pct, abort_on_part_loss, stage_limit and drop_empty as for every block.
//| Extra results: burn_ut (UT of the burn; -1 if it never burned), predicted_geo (lexicon lat, lng:
//| where the craft comes down, predicted after the burn and separation, or for the best burn when
//| it fails over accuracy; empty without one), predicted_error_km
//| (km from the target to predicted_geo; -1 without one); dropped (names of the parts separate
//| dropped); dv_used is the burn's. Succeeds only with a landing predicted within accuracy_km after
//| the burn. Fails with "target is required", an option value it can't read, "body has no
//| atmosphere", "ksc is on Kerbin", a craft on the ground, "a maneuver node is already planned:
//| remove it first", a separate it can't drop or the plan needs ("separate: ..."), an orbit it can't deorbit from
//| (escaping, or a periapsis already in the atmosphere: fly reentry), "the best burn ... lands <x> km
//| from the target, over accuracy_km", "the cheapest burn ... over max_dv" (its cost and UT), not
//| enough delta-v, or the reasons exec_node gives (no thrust, out of fuel, an abort); after the burn, with "the burn is flown, but separate failed: ...", "...the craft is
//| predicted to land <x> km from the target, over accuracy_km" or "...its landing can't be predicted".
//| The prediction leaves out parachutes and lift, so expect a few km more.
//| Once the burn has started (burn_ut over 0), call reentry whatever the result: the craft is coming down.
//| Tool: stage_performance for the delta-v; it chooses its own burn, so don't plan one with predict_burn or solve_burn.
//| Example: `local res is deorbit(lexicon("target", "ksc", "separate", 1)). if res:burn_ut > 0 { set res to reentry(). } addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function deorbit_defaults {
  return lexicon("target", "auto", "max_wait", "auto", "accuracy_km", 5, "separate", "none", "max_dv", "auto").
}

// "" or why a separate option value isn't one the block can use: "none", a whole stage number from
// 0, or part names or tags (a list, or one).
function deorbit_separate_unreadable {
  parameter sep.
  local rule is "separate must be 'none', a stage number or part names or tags, not ".
  if sep:istype("String") {
    if sep = "" {
      return rule + "''".
    }
  } else if sep:istype("Scalar") {
    if sep < 0 or sep <> round(sep) {
      return rule + sep.
    }
  } else if sep:istype("List") {
    if sep:length = 0 {
      return rule + "an empty list".
    }
  } else {
    return rule + "a " + sep:typename.
  }
  return "".
}

// What separate drops (claudia_drop_plan's lexicon): nothing for "none", a stage's decouplers for a
// number, else the named parts. Fires nothing.
function deorbit_separation {
  parameter sep.
  if sep:istype("Scalar") {
    return claudia_stage_drop_plan(sep).
  }
  if sep:istype("String") {
    if sep = "none" {
      return claudia_drop_plan(list()).
    }
    return claudia_drop_plan(list(sep)).
  }
  return claudia_drop_plan(sep).
}

// "" if the landing predicted after the burn (a REENTRYIMPACT result, error_km against the target)
// is within accuracy_km, else why the block reports success false. The craft is coming down either way.
function deorbit_after_burn {
  parameter after, accuracy_km.
  if after:error <> "" {
    return "the burn is flown, but its landing can't be predicted (" + after:error + ")".
  }
  if after:error_km > accuracy_km {
    return "the burn is flown, but the craft is predicted to land " + round(after:error_km) + " km from the target, over accuracy_km " + accuracy_km + ": it is coming down, so call reentry".
  }
  return "".
}

// How far ahead the burn is at the soonest, s: time to warp out, turn and lead the burn.
function deorbit_lead_s {
  return 240.
}

// "" or why an option value isn't one the block can use.
function deorbit_unreadable {
  parameter opt.
  local bad_target is claudia_target_unreadable(opt:target).
  if bad_target <> "" {
    return bad_target.
  }
  if opt:max_wait:istype("String") {
    if opt:max_wait <> "auto" {
      return "max_wait must be 'auto' or seconds above 0, not '" + opt:max_wait + "'".
    }
  } else if not opt:max_wait:istype("Scalar") or opt:max_wait <= 0 {
    return "max_wait must be 'auto' or seconds above 0, not " + opt:max_wait.
  }
  if not opt:accuracy_km:istype("Scalar") or opt:accuracy_km <= 0 {
    return "accuracy_km must be km above 0, not " + opt:accuracy_km.
  }
  if opt:max_dv:istype("String") {
    if opt:max_dv <> "auto" {
      return "max_dv must be 'auto' or m/s above 0, not '" + opt:max_dv + "'".
    }
  } else if not opt:max_dv:istype("Scalar") or opt:max_dv <= 0 {
    return "max_dv must be 'auto' or m/s above 0, not " + opt:max_dv.
  }
  return deorbit_separate_unreadable(opt:separate).
}

// "" or why the craft can't deorbit where it is: has_atm (its body has an atmosphere) and situation
// (kOS's STATUS).
function deorbit_body_problem {
  parameter has_atm, situation.
  if not has_atm {
    return "body has no atmosphere".
  }
  if situation = "LANDED" or situation = "SPLASHED" or situation = "PRELAUNCH" {
    return "the craft is " + situation:tolower + ", not in orbit: deorbit burns from orbit".
  }
  return "".
}

// max_wait in seconds: opt_value, or one turn of the body (rotation_period s) for "auto".
function deorbit_max_wait {
  parameter opt_value, rotation_period.
  if opt_value:istype("String") {
    return rotation_period.
  }
  return opt_value.
}

// What deorbit asks ADDONS:CLAUDIA:DEORBITBURN: the site, the window (earliest_ut, then max_wait s),
// the ground's height at the site (m, sea level for water), accuracy_km, the engines' isp (s, for
// the mass after the burn; 0 if unknown), craft (ENTRYCRAFT's: what enters, and who pays the burn), max_dv (a
// number, or "auto") and dv_left (stock's, 0 if it can't say). Pure.
function deorbit_query {
  parameter site, earliest_ut, max_wait, ground_alt, accuracy_km, isp, craft, max_dv, dv_left.
  return lexicon("lat", site:lat, "lng", site:lng, "earliest_ut", earliest_ut, "max_wait", max_wait,
    "ground_alt", max(0, ground_alt), "accuracy_km", accuracy_km, "isp", isp, "craft", craft,
    "max_dv", max_dv, "dv_left", dv_left).
}

function deorbit {
  parameter options is lexicon().
  local extra is lexicon("burn_ut", -1, "predicted_geo", lexicon(), "predicted_error_km", -1).
  local ctx is claudia_begin("deorbit", options, deorbit_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  local opt is ctx:opt.
  set opt:target to claudia_landing_target(opt:target, lexicon("planet", body:name)).
  set opt:target to choose "required" if opt:target:istype("String") and opt:target = "none" else opt:target.
  local unreadable is deorbit_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local problem is deorbit_body_problem(body:atm:exists, status).
  if problem <> "" {
    return claudia_finish(ctx, false, problem, extra).
  }
  local site is claudia_target_site(opt:target, body:name).
  if site:error <> "" {
    return claudia_finish(ctx, false, site:error, extra).
  }
  if hasnode {
    return claudia_finish(ctx, false, "a maneuver node is already planned: remove it first", extra).
  }
  // A craft whose engines aren't lit yet stages into them (within stage_limit), so the isp is known.
  local lit is claudia_stage_into_engines(ctx).
  if lit <> "" {
    return claudia_finish(ctx, false, lit, extra).
  }

  // What separate drops after the burn, worked out now: one that can't be dropped fails before any burn, and the search flies only the craft that re-enters.
  local separation is deorbit_separation(opt:separate).
  if separation:error <> "" {
    return claudia_finish(ctx, false, "separate: " + separation:error, extra).
  }
  // Refused here too (#825): the drop fires after the burn, but a descent isn't committed to first.
  local refused is claudia_drop_refusal(separation).
  if refused <> "" {
    return claudia_finish(ctx, false, "separate: " + refused, extra).
  }
  // The craft reentry will fly (#814): what separate and this entry drop, and whether its shield goes. Its error is the search's.
  local craft is addons:claudia:entrycraft(lexicon("home", reentry_home_uid(), "leave_out", claudia_drop_uids(separation))).
  local ground_alt is body:geopositionlatlng(site:lat, site:lng):terrainheight.
  local max_wait is deorbit_max_wait(opt:max_wait, body:rotationperiod).
  ship:deltav:forcecalc(). wait 0.5. // Stock recounts first: after a staging or a load it reads 0. The search is a block-internal call (ADR 0007): inline here.
  local found is addons:claudia:deorbitburn(deorbit_query(site, time:seconds + deorbit_lead_s(), max_wait, ground_alt, opt:accuracy_km, claudia_engine_isp(), craft, opt:max_dv, ship:deltav:current)).
  if found:haskey("lat") {
    set extra:predicted_geo to claudia_geo(found).
    set extra:predicted_error_km to round(found:error_km, 2).
  }
  if found:error <> "" {
    return claudia_finish(ctx, false, found:error, extra).
  }
  local late is claudia_drop_refusal(separation, found:dv). // #835: again with the burn counted, before any node
  if late <> "" { return claudia_finish(ctx, false, "separate: " + late, extra). }
  addons:claudia:shipsystems("entry").
  if opt:max_duration:istype("String") {
    set opt:max_duration to max(0, found:burn_ut - time:seconds) + 900.
  }

  local nd is node(found:burn_ut, found:radial, found:normal, found:prograde).
  add nd.
  local flown is claudia_burn(ctx, nd).
  // burn_ut says the engines fired: a failure before ignition (no thrust, an abort in the wait) leaves it -1.
  if flown:burn_time > 0 or flown:dv_used > 0 {
    set extra:burn_ut to round(found:burn_ut, 1).
  }
  if not flown:success {
    remove nd.
    return claudia_finish(ctx, false, flown:reason, extra).
  }

  // The craft that re-enters: drop what separate names, now the burn no longer needs it.
  local separate_failed is choose claudia_fire_drop(ctx, separation) if separation:decouplers:length > 0 else "".

  // Where the orbit actually flown comes down, with the craft as it is now: the burn's own error moves it.
  local after is addons:claudia:reentryimpact(lexicon("lat", site:lat, "lng", site:lng, "ground_alt", max(0, ground_alt), "craft", craft)).
  if after:error = "" {
    set extra:predicted_geo to claudia_geo(after).
    set extra:predicted_error_km to round(after:error_km, 2).
  }
  if separate_failed <> "" {
    return claudia_finish(ctx, false, "the burn is flown, but separate failed: " + separate_failed, extra).
  }
  local landing is deorbit_after_burn(after, opt:accuracy_km).
  return claudia_finish(ctx, landing = "", landing, extra).
}
