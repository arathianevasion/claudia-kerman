// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block drop_parts: fires the decouplers that hold the named parts, now. Built on
// claudia_lib.ks (the same part matching and decoupler firing as the drop_empty option); block-only
// helpers are prefixed drop_parts_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| drop_parts(options): drops the named parts now, by firing the decoupler that holds each, and
//| reports what it dropped. Use it to shed a spent booster before a landing, with no hand staging.
//| Options:
//|   parts [required]: a list of part names or tags (e.g. list("radialTank", "boosters")), or one name.
//|   allow_no_engine (false): true lets a drop leave no delta-v in space on an orbit missing the air.
//|   The common options do nothing here: it never burns, stages or waits, its drops are not part loss,
//|     and it ignores stage_limit.
//| It drops whatever the decoupler holds, fuel or not; use the drop_empty option on a burning block to
//| drop tanks only once empty. It drops nothing, and fails, for a part with no decoupler, one whose drop
//| would take the part running the script or the root part, or a drop leaving no delta-v in space with
//| periapsis above any atmosphere. Extra results: dropped (names of the parts that left),
//| parts_dropped, dropped_dv (the delta-v it took).
//| Fails with "no part matches <name>", "<name> has no decoupler", "<name> would drop the part
//| running the script", or "nothing separated: the decouplers did not fire".
//| Tool: stage_performance for the delta-v left once the parts are gone.
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

// The refusal's reason: the delta-v the drop would take (dv_taken, m/s; -1 unknown) and the periapsis. Pure.
function drop_parts_strand_reason {
  parameter dv_taken, pe, atm_height.
  local took is "all the delta-v".
  if dv_taken >= 0 {
    set took to "all " + round(dv_taken) + " m/s of delta-v".
  }
  local air is "the body has no atmosphere".
  if atm_height >= 0 {
    set air to "periapsis " + round(pe / 1000) + " km is above the atmosphere".
  }
  return "the drop would take " + took + " while " + air + ", leaving no way to change the orbit; drop with reentry's separate, which checks the orbit first, or set allow_no_engine to true".
}

// The vacuum delta-v (m/s) the craft keeps once plan's parts are gone (a sound proxy, not stock's
// staged figure): the engines that stay, burning the propellant they use from the parts that stay, as one
// stage with their mean vacuum isp. 0 when no engine stays or none of its propellant does.
function drop_parts_dv_left {
  parameter plan.
  local gone is claudia_drop_uids(plan).
  local engs is list().
  list engines in engs.
  local thrusts is list().
  local isps is list().
  local uses is list().
  for e in engs {
    if not gone:contains(e:uid) {
      thrusts:add(e:maxpossiblethrustat(0)).
      isps:add(e:ispat(0)).
      for k in e:consumedresources:keys {
        uses:add(drop_parts_res_key(k)).
      }
    }
  }
  local wet is 0.
  local prop is 0.
  for p in ship:parts {
    if not gone:contains(p:uid) {
      set wet to wet + p:mass.
      for r in p:resources {
        if claudia_is_propellant(r:name) and uses:contains(drop_parts_res_key(r:name)) {
          set prop to prop + r:amount * r:density.
        }
      }
    }
  }
  return drop_parts_rocket_dv(claudia_mean_isp(thrusts, isps), wet, prop).
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
  local names is drop_parts_names(ctx:opt:parts).
  if names:istype("String") {
    return claudia_finish(ctx, false, names, extra).
  }
  // Work out every decoupler to fire before firing any, so a name that can't be dropped drops nothing.
  local plan is claudia_drop_plan(names).
  if plan:error <> "" {
    return claudia_finish(ctx, false, plan:error, extra).
  }
  // A drop can't be undone (#608): one that leaves no delta-v in space, on an orbit missing the air,
  // strands the craft, so it goes only with allow_no_engine.
  local dv_before is claudia_dv_remaining().
  local atm_height is -1.
  if ship:body:atm:exists {
    set atm_height to ship:body:atm:height.
  }
  local allow is ctx:opt:allow_no_engine.
  if not allow:istype("Boolean") {
    return claudia_finish(ctx, false, "allow_no_engine must be true or false", extra).
  }
  local dv_now is drop_parts_dv_left(lexicon("gone", list())).
  if drop_parts_strands(status, ship:periapsis, atm_height, dv_now, drop_parts_dv_left(plan), allow) {
    set extra:dropped_dv to dv_before.
    return claudia_finish(ctx, false, drop_parts_strand_reason(dv_before, ship:periapsis, atm_height), extra).
  }
  local before is ship:parts:length.
  local not_fired is claudia_fire_drop(ctx, plan).
  set extra:parts_dropped to before - ship:parts:length.
  // Stock delta-v is recounted after the parts left; what it lost is what the drop took (a dropped empty
  // tank gains delta-v: 0). -1: unknown at a trickle throttle.
  ship:deltav:forcecalc().
  wait 0.5.
  local dv_after is claudia_dv_remaining().
  if dv_before < 0 or dv_after < 0 {
    set extra:dropped_dv to -1.
  } else {
    set extra:dropped_dv to round(max(0, dv_before - dv_after), 1).
  }
  if not_fired <> "" {
    return claudia_finish(ctx, false, not_fired, extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
