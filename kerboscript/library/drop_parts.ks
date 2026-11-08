// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block drop_parts: fires the decouplers that hold the named parts, now. Built on
// claudia_lib.ks (the same part matching and decoupler firing as the drop_empty option); block-only
// helpers are prefixed drop_parts_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| drop_parts(options): drops the named parts now, by firing the decoupler that holds each, and
//| reports what it dropped. Use it to shed a spent booster before a landing.
//| Options:
//|   parts [required]: a list of part names or tags (e.g. list("radialTank", "boosters")), or one name.
//|   allow_no_engine (false): true lets a drop leave no delta-v in space on an orbit missing the air.
//|   The common options do nothing here: it never burns, stages or waits, its drops are not part loss,
//|     and it ignores stage_limit.
//| It drops whatever the decoupler holds, fuel or not; use the drop_empty option on a burning block to
//| drop tanks only once empty. It fails, dropping nothing, for a part with no decoupler, a drop taking
//| the script's part or the root part, or one leaving no delta-v in space with periapsis above any atmosphere. Extra results: dropped (names of the parts that left),
//| parts_dropped, dropped_dv (the delta-v it took).
//| Fails with "no part matches <name>", "<name> has no decoupler", "<name> would drop the part
//| running the script", a drop the plan needs, or "nothing separated: the decouplers did not fire".
//| Tool: stage_performance for the delta-v left.
//| Example: `local res is drop_parts(lexicon("parts", list("boosters"))). addons:claudia:report(res).`

// The block's own options and defaults ("required" means the Mission Script must give it).
function drop_parts_defaults {
  return lexicon("parts", "required", "allow_no_engine", false).
}

// Vacuum delta-v (m/s) of a craft of wet mass wet (t) burning prop (t) of propellant at isp (s): the
// rocket equation, 0 with no isp or no propellant. Pure.
function drop_parts_rocket_dv {
  parameter isp, wet, prop.
  if isp <= 0 or prop <= 0 or wet <= prop {
    return 0.
  }
  return isp * 9.80665 * ln(wet / (wet - prop)).
}

// A resource name as a key both a part's resources (LiquidFuel) and an engine's consumedresources
// (which may give the display name, Liquid Fuel) agree on. Pure.
function drop_parts_res_key {
  parameter res_name.
  return res_name:replace(" ", ""):tolower.
}

// Whether a drop must be refused (#608): the craft is in space (not on the ground: status), its orbit
// misses the air (periapsis pe in m above atm_height, or atm_height < 0: no atmosphere), and the drop
// takes the last of its delta-v: dv_now > 0 before it, dv_left = 0 after (a craft with none already loses
// nothing). allow: the allow_no_engine option. Pure.
function drop_parts_strands {
  parameter status_now, pe, atm_height, dv_now, dv_left, allow.
  if allow or dv_left > 0 or dv_now <= 0 {
    return false.
  }
  if status_now = "LANDED" or status_now = "SPLASHED" or status_now = "PRELAUNCH" {
    return false.
  }
  return atm_height < 0 or pe > atm_height.
}

// The delta-v (m/s) a drop took: stock's figure before less after (stock_before, stock_after; -1 unknown at
// a trickle throttle), or, when stock is unknown, reads 0 or has not changed (not recounted yet), the
// proxy's (proxy_now less proxy_left: drop_parts_dv_left). Never below 0: a dropped empty tank gains. Pure.
function drop_parts_dv_taken {
  parameter stock_before, stock_after, proxy_now, proxy_left.
  if stock_before > 0 and stock_after >= 0 and stock_after <> stock_before {
    return round(max(0, stock_before - stock_after), 1).
  }
  return round(max(0, proxy_now - proxy_left), 1).
}

// The refusal's reason: the delta-v the drop would take (dv_taken, m/s; -1 unknown) and the periapsis. Pure.
function drop_parts_strand_reason {
  parameter dv_taken, pe, atm_height.
  local took is choose "all " + round(dv_taken) + " m/s of delta-v" if dv_taken >= 0 else "all the delta-v".
  local air is choose "periapsis " + round(pe / 1000) + " km is above the atmosphere" if atm_height >= 0 else "the body has no atmosphere".
  return "the drop would take " + took + " while " + air + ", leaving no way to change the orbit; drop with reentry's separate, which checks the orbit first, or set allow_no_engine to true".
}

// The vacuum delta-v (m/s) the craft keeps once plan's parts are gone (a sound proxy, not stock's
// staged figure): the engines that stay, burning the propellant they use from the parts that stay, as one
// stage with their mean vacuum isp; jets don't count. 0 when no engine stays or none of its propellant does.
function drop_parts_dv_left {
  parameter plan.
  local kept is drop_parts_kept(plan).
  return drop_parts_rocket_dv(kept:isp, kept:wet, kept:prop).
}

// drop_parts_dv_left's figures (isp, wet and prop, t), and for a burn first (#835; DropGuard.AfterBurn has the rule) gone_prop, the t of what the
// engines burning now use in plan's parts, and burners_kept, how many of those engines stay.
function drop_parts_kept {
  parameter plan.
  local gone is claudia_drop_uids(plan).
  local thrusts is list(). local isps is list(). local uses is list(). local burns is list(). local burners_kept is 0.
  for e in ship:engines {
    local keys is list().
    for k in e:consumedresources:keys { keys:add(drop_parts_res_key(k)). }
    if e:ignition and e:availablethrust > 0 {
      if not gone:contains(e:uid) { set burners_kept to burners_kept + 1. }
      for k in keys { burns:add(k). }
    }
    // A jet (it breathes intake air) can't burn in space.
    if not gone:contains(e:uid) and not keys:contains("intakeair") {
      thrusts:add(e:maxpossiblethrustat(0)).
      isps:add(e:ispat(0)).
      for k in keys { uses:add(k). }
    }
  }
  local wet is 0. local prop is 0. local gone_prop is 0.
  for p in ship:parts {
    local stays is not gone:contains(p:uid).
    if stays { set wet to wet + p:mass. }
    for res in p:resources {
      if claudia_is_propellant(res:name) and uses:contains(drop_parts_res_key(res:name)) and stays { set prop to prop + res:amount * res:density. }
      if claudia_is_propellant(res:name) and burns:contains(drop_parts_res_key(res:name)) and not stays { set gone_prop to gone_prop + res:amount * res:density. }
    }
  }
  return lexicon("isp", claudia_mean_isp(thrusts, isps), "wet", wet, "prop", prop, "gone_prop", gone_prop, "burners_kept", burners_kept).
}

// The wanted names as a list, or the reason the option can't be used (a String).
function drop_parts_names {
  parameter parts_opt.
  if parts_opt:istype("String") {
    if parts_opt = "required" {
      return "parts is required".
    }
    if parts_opt = "" {
      return "parts must be a list of part names or tags".
    }
    return list(parts_opt).
  }
  if not parts_opt:istype("List") or parts_opt:length = 0 {
    return "parts must be a list of part names or tags".
  }
  return parts_opt.
}

function drop_parts {
  parameter options is lexicon().
  local extra is lexicon("parts_dropped", 0, "dropped_dv", 0).
  local ctx is claudia_begin("drop_parts", options, drop_parts_defaults(), 60).
  if ctx:error <> "" {
    return claudia_finish(ctx, false, ctx:error, extra).
  }
  if not ctx:opt:allow_no_engine:istype("Boolean") {
    return claudia_finish(ctx, false, "allow_no_engine must be true or false", extra).
  }
  local names is drop_parts_names(ctx:opt:parts).
  if names:istype("String") {
    return claudia_finish(ctx, false, names, extra).
  }
  // Work out every decoupler to fire before firing any, so a name that can't be dropped drops nothing.
  local plan is claudia_drop_plan(names).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  // A drop that leaves no delta-v in space, on an orbit missing the air, strands the craft (#608): only with allow_no_engine.
  local dv_before is claudia_dv_remaining().
  local atm_height is choose ship:body:atm:height if ship:body:atm:exists else -1.
  local dv_now is drop_parts_dv_left(lexicon("gone", list())).
  local dv_left is drop_parts_dv_left(plan).
  if drop_parts_strands(status, ship:periapsis, atm_height, dv_now, dv_left, ctx:opt:allow_no_engine) {
    set extra:dropped_dv to drop_parts_dv_taken(dv_before, 0, dv_now, dv_left).
    return claudia_finish(ctx, false, drop_parts_strand_reason(extra:dropped_dv, ship:periapsis, atm_height), extra).
  }
  local before is ship:parts:length.
  local not_fired is claudia_fire_drop(ctx, plan).
  set extra:parts_dropped to before - ship:parts:length.
  // Stock delta-v is recounted after the parts left; what it lost is what the drop took.
  set extra:dropped_dv to drop_parts_dv_taken(dv_before, claudia_dv_recounted(), dv_now, dv_left).
  return claudia_finish(ctx, not_fired = "", not_fired, extra).
}
