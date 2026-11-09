// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block reentry: brings a capsule down through an atmosphere. Built on claudia_lib.ks (its stage drop plan,
// for separate); block-only helpers are prefixed reentry_. The "//|" lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| reentry(options): lands through the atmosphere: drops the stack below this entry's shield,
//| waits for the atmosphere if still in space, holds the heat shield forward, drops it once
//| the worst heating is over, opens the parachutes only when safe, physics-warping the fall
//| (1x below 100 m), and ends once landed or splashed. If its drop and chutes would touch down over 10 m/s (15 on Kerbin, Eve, Laythe) and an engine has fuel, it
//| drops nothing, lights the engine (no staging) and brakes by radar, chutes and gear down, to 2 m/s or until the fuel is gone.
//| Options:
//|   chute_mode ["safe"]: drogues first (under 500 m/s, over 0.02 atm, or by 3000 m above the ground), then
//|     mains once they are out (under 250 m/s, over 0.04 atm, or by 1500 m); "none" never opens them.
//|   chutes ["auto"]: the highest stage aboard, all on the last entry, plus later stages while over 15 m/s is predicted with no engine to land on (even over "none"); or "none".
//|   separate ["auto"]: this entry's heat shield is number n from the pod: the entries left in the Mission Plan,
//|     plus 1 if burns follow the last. Drops what is below it 600 s before the air; no shield n, nothing drops. Or "none".
//|   jettison_stage ["auto"]: drops that shield, unless its stack is kept; or "none".
//|   hold ["retrograde"]: points the craft's forward axis at surface retrograde; "none" leaves it.
//|   max_duration ["auto"]: the wait for the atmosphere plus 1800 s.
//|   plus min_ec_pct (not with Pe in air), abort_on_part_loss, stage_limit.
//| Extra results: max_g (peak g), max_temp_pct (hottest part, % of limit), geo (lat, lng, alt),
//| splashed, touchdown_speed (m/s at contact; over 15 fails), passes (max 3; a skip-out waits for the
//| next, else "skipped out ..."), shield_dropped, chutes_used, chutes_kept (titles), powered_dv (m/s), sun_elevation_deg.
//| Fails with "body has no atmosphere", an orbit that never reaches the atmosphere, an option it can't read,
//| "stack attached below the heat shield: <names>", a hard touchdown, or an abort (a mid-air abort still
//| opens the parachutes if safe). Fly it after a return or a de-orbit burn, periapsis inside the atmosphere;
//| at most one small trim. Example: `local res is reentry(). addons:claudia:report(res).`

// Main parachutes: the most speed (m/s) and least air pressure (atm) at which opening is safe, and the
// height above the ground (m) by which they open regardless. That last resort is not speed-aware on
// purpose: below 1500 m there is no time left to slow down, and an unopened chute is certain death where
// an early one is only a risk. The drogues are staged before the mains, so by then the craft has been
// slowing on them since 3000 m.
function reentry_chute_max_speed { return 250. }
function reentry_chute_min_pressure { return 0.04. }
function reentry_chute_last_agl { return 1500. }
// Drogue chutes are built for the fast part of the descent: higher speed limit, thinner air, and the
// height by which they open whatever the speed (the one chute that can take it, so the cheapest last resort).
function reentry_drogue_max_speed { return 500. }
function reentry_drogue_min_pressure { return 0.02. }
function reentry_drogue_last_agl { return 3000. }
// The heat shield goes at or below this speed (m/s), or by this height above the ground (m). The height
// is not speed-aware on purpose: at 3000 m the air is thick enough that a falling capsule is well under
// the speed at which it heats, so the shield is only dead weight over the chutes, and waiting on a speed
// that never comes would keep it on to the ground.
function reentry_shield_speed { return 400. }
function reentry_shield_last_agl { return 3000. }
// Touchdown faster than this (m/s) fails the block.
function reentry_touchdown_limit { return 15. }

// Powered touchdown (#628, #824). The braking burn comes down to soft m/s by end m of radar altitude (the craft's
// centre of mass stands a few metres up on its legs), then holds soft to the ground.
// Braking starts with the thrust over margin, lag s of the fall early (the throttle and the last of the turn).
function reentry_powered_soft { return 2. }
function reentry_powered_end { return 10. }
function reentry_powered_margin { return 1.2. }
function reentry_powered_lag { return 1. }
// The height steps the stopping distance is worked over, and the share of the drag measured now it counts:
// drag falls with the Mach number as the craft slows, and air pressure stands in for density.
function reentry_powered_steps { return 20. }
function reentry_powered_drag_share { return 0.5. }

// Physics warp (the cap reentry gives claudia_warp_to, under the q rule): at most hot_cap times while the
// hottest part is over hot_pct percent of its heat limit; drogue_cap under drogues; main_cap under mains.
// Always 1x below low_agl metres above the ground, so the touchdown speed is measured fairly, and in the
// last event_s seconds before a parachute opens or the heat shield drops.
function reentry_warp_hot_pct { return 50. }
function reentry_warp_hot_cap { return 2. }
function reentry_warp_drogue_cap { return 3. }
function reentry_warp_main_cap { return 4. }
function reentry_warp_low_agl { return 100. }
function reentry_warp_event_s { return 10. }

// The block's own options and defaults (max_duration "auto" is resolved once the entry is known).
function reentry_defaults {
  return lexicon("chute_mode", "safe", "jettison_stage", "auto", "hold", "retrograde", "separate", "auto", "chutes", "auto").
}

// Why claudia_begin refused options (error): touchdown, an option until #824, has its own reason.
function reentry_option_error {
  parameter options, error.
  if options:haskey("touchdown") { return "touchdown is no longer an option: reentry decides for itself whether to land on its engines". }
  return error.
}

// "" or why an option isn't one the block knows.
function reentry_unreadable {
  parameter opt.
  if opt:chute_mode <> "safe" and opt:chute_mode <> "none" {
    return "chute_mode must be 'safe' or 'none', not '" + opt:chute_mode + "'".
  }
  if opt:hold <> "retrograde" and opt:hold <> "none" {
    return "hold must be 'retrograde' or 'none', not '" + opt:hold + "'".
  }
  for key in list("jettison_stage", "separate", "chutes") {
    if opt[key]:typename <> "String" or (opt[key] <> "auto" and opt[key] <> "none") {
      return key + " must be 'auto' or 'none', not " + opt[key].
    }
  }
  return "".
}

// What to do about the parachutes now: "deploy" or "wait". mode: "safe" or "none"; out: they are
// already open; speed in m/s and agl (height above the ground) in m; pressure in atm.
function reentry_chute_action {
  parameter mode, out, speed, agl, pressure.
  return reentry_open_action(mode, out, speed, agl, pressure, reentry_chute_last_agl(), reentry_chute_max_speed(), reentry_chute_min_pressure()).
}

// What to do about the drogue chutes now: "deploy" or "wait". Same arguments as reentry_chute_action.
function reentry_drogue_action {
  parameter mode, out, speed, agl, pressure.
  return reentry_open_action(mode, out, speed, agl, pressure, reentry_drogue_last_agl(), reentry_drogue_max_speed(), reentry_drogue_min_pressure()).
}

// The chute rule, with its last height (m), most speed (m/s) and least pressure (atm).
function reentry_open_action {
  parameter mode, out, speed, agl, pressure, last_agl, max_speed, min_pressure.
  if mode = "none" or out {
    return "wait".
  }
  if agl <= last_agl {
    return "deploy".
  }
  if speed <= max_speed and pressure >= min_pressure {
    return "deploy".
  }
  return "wait".
}

// Which parachutes to open now: "drogues", "mains" or "wait". Drogues go before mains: the mains wait
// for the drogues to be out (or for there to be none). has_drogues: the craft carries drogue chutes;
// drogues_out and mains_out: those are already open.
function reentry_chute_stage {
  parameter mode, has_drogues, drogues_out, mains_out, speed, agl, pressure.
  if mode = "none" or mains_out {
    return "wait".
  }
  if has_drogues and not drogues_out {
    if reentry_drogue_action(mode, drogues_out, speed, agl, pressure) = "deploy" {
      return "drogues".
    }
    return "wait".
  }
  if reentry_chute_action(mode, mains_out, speed, agl, pressure) = "deploy" {
    return "mains".
  }
  return "wait".
}

// True if a parachute part named name is a drogue chute.
function reentry_is_drogue {
  parameter name.
  return name:tolower:contains("drogue").
}

// True if the heat shield should drop now. gone: it has already been dropped (or there is none); braking: the engines are about to fire, and it goes first.
function reentry_shield_due {
  parameter gone, speed, agl, braking is false.
  if gone {
    return false.
  }
  return braking or speed <= reentry_shield_speed() or agl <= reentry_shield_last_agl().
}

// Seconds until a falling value (height in m or speed in m/s) reaches limit, at rate per second: 0 if
// it is there already, -1 if it is not falling (so no event is predicted).
function reentry_eta_to {
  parameter value, limit, rate.
  if value <= limit {
    return 0.
  }
  if rate <= 0 {
    return -1.
  }
  return (value - limit) / rate.
}

// The sooner of two etas from reentry_eta_to (-1 counts as none).
function reentry_eta_first {
  parameter a, b.
  if a < 0 {
    return b.
  }
  if b < 0 {
    return a.
  }
  return min(a, b).
}

// The physics rate cap now (4 is no extra cap; the q rule applies under it). hot_pct: the hottest part in
// percent of its heat limit; drogues_out, mains_out: those chutes are open; agl: height above the ground
// in m; event_eta: seconds to the next predicted chute opening or heat-shield drop, -1 for none.
function reentry_warp_cap {
  parameter hot_pct, drogues_out, mains_out, agl, event_eta.
  if agl < reentry_warp_low_agl() {
    return 1.
  }
  if event_eta >= 0 and event_eta <= reentry_warp_event_s() {
    return 1.
  }
  if mains_out {
    return reentry_warp_main_cap().
  }
  if drogues_out {
    return reentry_warp_drogue_cap().
  }
  if hot_pct > reentry_warp_hot_pct() {
    return reentry_warp_hot_cap().
  }
  return 4.
}

// Touchdown within what the crew and craft survive: reentry_touchdown_limit.
function reentry_touchdown_ok { parameter speed. return speed <= reentry_touchdown_limit(). }

// ---- Chutes (#628) ----

// The parachutes among parts.
function reentry_chutes_in {
  parameter parts.
  local found is list().
  for p in parts {
    if p:hasmodule("ModuleParachute") {
      found:add(p).
    }
  }
  return found.
}

// This entry's Chute Group (ADR 0019) from CHUTEGROUP's answer to ask (none: the script's "none"; engine: it lands on its engines). An error: every chute aboard (parts), or none for "none": a shut chute is the worse mistake.
function reentry_group_of {
  parameter ask, answer, parts.
  if answer:error = "" { return answer. }
  local group is lexicon("last", true, "stages", list(), "uids", list(), "why", "").
  for p in reentry_chutes_in(parts) {
    if not ask:none { group:uids:add(p:uid). }
  }
  return group.
}

// True if chute p is in group now: its stage as read this moment (a group may span stages, #827), or any chute on a Last Entry.
function reentry_in_group {
  parameter p, group.
  return group:last or group:stages:contains(p:stage).
}

// True if chute p is stowed, with its deploy event there to fire.
function reentry_stowed {
  parameter p.
  return p:getmodule("ModuleParachute"):hasevent("deploy chute").
}

// True if group has nothing left to open and this entry opened nothing (titles): a drop or restage took it. A skip-out's next pass has opened some, so asks for no further group.
function reentry_group_gone {
  parameter group, parts, opened.
  if opened:length > 0 { return false. }
  for p in reentry_chutes_in(parts) {
    if reentry_in_group(p, group) and reentry_stowed(p) { return false. }
  }
  return true.
}

// group, or if it has gone (reentry_group_gone), what CHUTEGROUP gives now. Read before each opening, an abort's too.
function reentry_fresh_group {
  parameter group, ask, opened.
  if reentry_group_gone(group, ship:parts, opened) { return reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts). }
  return group.
}

// True if a stowed chute of group is a drogue.
function reentry_has_drogues {
  parameter parts, group.
  for p in reentry_chutes_in(parts) {
    if reentry_in_group(p, group) and reentry_is_drogue(p:name) and reentry_stowed(p) {
      return true.
    }
  }
  return false.
}

// Opens every stowed parachute of one kind, drogues (true) or mains (false), of group among parts; none outside it is armed. Returns the titles it opened.
function reentry_open_chutes {
  parameter parts, drogues, group.
  local opened is list().
  for p in reentry_chutes_in(parts) {
    if reentry_in_group(p, group) and reentry_is_drogue(p:name) = drogues and reentry_stowed(p) {
      p:getmodule("ModuleParachute"):doevent("deploy chute").
      opened:add(p:title).
    }
  }
  return opened.
}

// After landing, unless this is the Last Entry: cuts any chute still open, so it can't drag on the ascent. One stock has cut (autoCutSpeed) offers no event. Returns the titles it cut.
function reentry_cut_chutes {
  parameter parts, last.
  local cut is list().
  if last { return cut. }
  for p in reentry_chutes_in(parts) {
    local m is p:getmodule("ModuleParachute").
    for event in list("cut chute", "cut parachute") {
      if m:hasevent(event) { m:doevent(event). cut:add(p:title). break. }
    }
  }
  return cut.
}

// ---- Powered touchdown (#628, #824) ----

// Lights those of engs (the craft's engines) whose uid is in uids, part by part: no stage fires.
function reentry_light {
  parameter engs, uids.
  for e in engs {
    if uids:contains(e:uid) and not e:ignition { e:activate(). }
  }
}

// Metres of fall a braking burn takes to bring the downward speed v_down (m/s) to soft, with net m/s^2
// of thrust over gravity (it may be negative) and drag k v^2 (k in 1/m) for each step m of height below
// the craft, ks listing k from the top step down. -1 if it doesn't get there within the steps, so not
// before the ground. Each step is solved exactly for its k. Pure.
function reentry_stop_distance {
  parameter v_down, net, ks, step, soft.
  local v2 is v_down ^ 2.
  local goal is soft ^ 2.
  if v2 <= goal {
    return 0.
  }
  local x is 0.
  for k in ks {
    local next is v2 - 2 * net * step.
    if k > 1e-9 {
      local c is net / k.
      set next to (v2 + c) * 2.718281828459045 ^ (-2 * k * step) - c.
      if next <= goal and goal + c > 0 {
        return x + ln((v2 + c) / (goal + c)) / (2 * k).
      }
    } else if next <= goal {
      return x + (v2 - goal) / (2 * net).
    }
    set v2 to next.
    set x to x + step.
  }
  return -1.
}

// The drag coefficients (1/m) for reentry_stop_distance over radar m below the craft in steps of radar /
// reentry_powered_steps(): reentry_powered_drag_share of the drag now (m/s^2, at speed m/s and pressure
// p_now atm) as k v^2, scaled at each step's middle by pressure_at (a delegate: metres below the craft ->
// atm) over p_now. All 0 with no drag measured. Pure.
function reentry_drag_steps {
  parameter drag, speed, p_now, radar, pressure_at.
  local n is reentry_powered_steps().
  local step is radar / n.
  local k0 is 0.
  if drag > 0 and speed > 1 and p_now > 0 {
    set k0 to reentry_powered_drag_share() * drag / speed ^ 2.
  }
  local ks is list().
  for i in range(n) {
    if k0 > 0 {
      ks:add(k0 * pressure_at((i + 0.5) * step) / p_now).
    } else {
      ks:add(0).
    }
  }
  return ks.
}

// The radar altitude (m) at which a powered touchdown's braking must start, falling at v_down m/s with
// a_up m/s^2 of upward thrust at full throttle (reentry_powered_margin held back) against gravity g and
// the drag in ks (reentry_drag_steps' over radar m): the stopping distance to reentry_powered_soft plus
// reentry_powered_end and reentry_powered_lag s of the fall. A craft that only stops in time on all its thrust
// gets the figure for all of it; -1 if not even that stops it before the ground: brake now. Pure.
function reentry_brake_height {
  parameter v_down, a_up, g, ks, radar.
  local step is radar / max(1, ks:length).
  local s is reentry_stop_distance(v_down, a_up / reentry_powered_margin() - g, ks, step, reentry_powered_soft()).
  if s < 0 {
    set s to reentry_stop_distance(v_down, a_up - g, ks, step, reentry_powered_soft()).
  }
  if s < 0 {
    return -1.
  }
  return s + reentry_powered_end() + max(0, v_down) * reentry_powered_lag().
}

// True if the braking must start now at radar m (reentry_brake_height's figure brake_h), at speed m/s. A fall the engines can't stop waits for the
// air to do its work, as for the shield: on a shallow path high up, thrust barely points up, and the fuel would go at orbital speed. Pure.
function reentry_brake_due {
  parameter radar, brake_h, speed.
  return radar <= brake_h or (brake_h < 0 and reentry_shield_due(false, speed, radar)).
}

// The throttle (0 to 1) braking a powered touchdown, a_up m/s^2 of upward thrust at full throttle against
// gravity g: above reentry_powered_end m of radar, the even deceleration that reaches reentry_powered_soft
// there; below it, or once that slow, what holds the soft speed. Drag isn't counted: it is worked out
// again every tick, so drag only lowers it. Pure.
function reentry_brake_throttle {
  parameter radar, v_down, a_up, g.
  if a_up <= 0 {
    return 1.
  }
  local soft is reentry_powered_soft().
  local h is radar - reentry_powered_end().
  local need is g + 2 * (v_down - soft).
  if h > 0 and v_down > soft {
    set need to (v_down ^ 2 - soft ^ 2) / (2 * h) + g.
  }
  return min(1, max(0, need / a_up)).
}

// The g felt, in g: the craft's change in velocity over dt (s) plus gravity's pull at position pos
// (from the body's centre, m), all in the same non-rotating frame; mu is the body's gravitational
// parameter. Vectors come as plain components (dv = v_now - v_prev) so this stays pure.
function reentry_proper_g {
  parameter dvx, dvy, dvz, dt, px, py, pz, mu.
  local dist is sqrt(px * px + py * py + pz * pz).
  if dt <= 0 or dist <= 0 {
    return 0.
  }
  local pull is mu / (dist * dist * dist).
  local ax is dvx / dt + px * pull.
  local ay is dvy / dt + py * pull.
  local az is dvz / dt + pz * pull.
  return sqrt(ax * ax + ay * ay + az * az) / 9.80665.
}

// reentry_proper_g from two velocity vectors and the craft's position from the body's centre.
function reentry_g_felt {
  parameter v_prev, v_now, dt, pos, mu.
  local dv is v_now - v_prev.
  return reentry_proper_g(dv:x, dv:y, dv:z, dt, pos:x, pos:y, pos:z, mu).
}

// Passes through the atmosphere reentry flies before it fails a craft that keeps skipping out (#592).
function reentry_max_passes { return 3. }

// True if the craft has left the atmosphere on a pass: it was in it (in_air), is above it (height, atm_h in
// m) and is climbing (vspeed, m/s).
function reentry_skipped_out {
  parameter in_air, height, atm_h, vspeed.
  return in_air and height > atm_h and vspeed > 0.
}

// "" or why a skip-out is the end: pe (m) is above the atmosphere, so there is no next pass, or the
// craft has flown passes (counting this one) of the most it flies.
function reentry_skip_refusal {
  parameter passes, pe, atm_h.
  if pe >= atm_h {
    return "skipped out: periapsis " + round(pe) + " m is above the atmosphere".
  }
  if passes >= reentry_max_passes() {
    return "skipped out of the atmosphere " + passes + " times (periapsis " + round(pe) + " m)".
  }
  return "".
}

// max_duration "auto" after a skip-out: the time already run (elapsed, s), the wait for the next entry
// (entry_in, s) and 1800 s. An explicit max_duration (a number the caller gave) stays: auto is whether it was "auto".
function reentry_next_deadline {
  parameter auto, current, elapsed, entry_in.
  if auto {
    return elapsed + entry_in + 1800.
  }
  return current.
}

// The decoupler modules on or beside the Entry Shield (shield_uid; "" for none): no other shield's (#823).
function reentry_shield_modules {
  parameter shield_uid, parts.
  local found is list().
  if shield_uid = "" {
    return found.
  }
  for p in parts {
    if claudia_is_decoupler(p) and reentry_shield_beside(p, shield_uid) {
      for name in list("ModuleDecouple", "ModuleAnchoredDecoupler", "ModuleJettison") {
        if p:hasmodule(name) {
          local m is p:getmodule(name).
          local event is claudia_decoupler_event(m:alleventnames).
          if event <> "" {
            found:add(lexicon("module", m, "event", event)).
          }
        }
      }
    }
  }
  return found.
}

// Fires the shield's decouplers one at a time, the part first (ENTRYDROP's jettison_first, #940) before any other; returns how many events it fired. The list is rebuilt from
// parts_of() (a delegate to the craft's parts) before each event, so a module on a part that has left, by
// an earlier event splitting the craft, is not there to fire (#558).
function reentry_shield_drop {
  parameter shield_uid, parts_of, first is "".
  local fired is list().
  until false {
    local next is "".
    for drop in reentry_shield_modules(shield_uid, parts_of()) {
      if not fired:contains(drop:module:part:uid + drop:event) and (next:istype("String") or drop:module:part:uid = first) {
        set next to drop.
      }
    }
    if next:istype("String") {
      return fired:length.
    }
    fired:add(next:module:part:uid + next:event).
    next:module:doevent(next:event).
  }
}

// True if the decoupler p is the heat shield shield_uid or sits against it.
function reentry_shield_beside {
  parameter p, shield_uid.
  if p:uid = shield_uid or (p:hasparent and p:parent:uid = shield_uid) {
    return true.
  }
  for child in p:children {
    if child:uid = shield_uid {
      return true.
    }
  }
  return false.
}

// ---- What this entry drops (#557, #823): ADDONS:CLAUDIA:ENTRYDROP decides, the block fires ----

// What separate drops, of ENTRYDROP's lexicon below, as claudia_drop_plan's lexicon with the parts themselves: nothing for
// "none"; for "auto" the stack through its decouplers, or an error if a part of it has none.
function reentry_separation {
  parameter sep, below, parts.
  local plan is claudia_drop_plan(list()).
  if sep = "auto" {
    if below:stuck:length > 0 {
      set plan:error to below:stuck:join(", ") + ": no decoupler between it and the heat shield".
      return plan.
    }
    return claudia_drop_of(below, parts).
  }
  return plan.
}

function reentry_agl {
  return ship:altitude - max(0, ship:geoposition:terrainheight).
}

// The hottest part in percent of its limit. kOS parts have no temperature suffixes, so the addon reads them.
function reentry_hottest {
  return addons:claudia:hottestpart.
}

function reentry_geo {
  return lexicon("lat", round(ship:geoposition:lat, 4), "lng", round(ship:geoposition:lng, 4), "alt", round(ship:altitude)).
}

// The blocknote lexicon for the predicted landing (#478; impact: REENTRYIMPACT's answer). A predictor error
// or an answer with no landing point says why; reentry flies on unchanged either way. Pure.
function reentry_prediction_note {
  parameter impact.
  if not impact:haskey("lat") {
    local why is "no landing point".
    if impact:haskey("error") and impact:error <> "" { set why to impact:error. }
    return lexicon("phase", "predicted_landing", "error", why, "note", "flying on without a prediction").
  }
  return lexicon("phase", "predicted_landing", "predicted_geo", lexicon("lat", round(impact:lat, 4), "lng", round(impact:lng, 4)), "landing_ut", round(impact:landing_ut)).
}

// Notes where the craft came down against the prediction (impact: REENTRYIMPACT's answer): the actual geo,
// the touchdown speed (m/s) and, with a prediction, prediction_miss_km (Core's PREDICTIONMISS). Log-only.
function reentry_note_landing {
  parameter ctx, impact, speed.
  local landed is lexicon("phase", "landed", "actual_geo", reentry_geo(), "touchdown_speed", round(speed, 1)).
  if impact:haskey("lat") {
    local miss is addons:claudia:predictionmiss(lexicon("lat", impact:lat, "lng", impact:lng, "actual_lat", ship:geoposition:lat, "actual_lng", ship:geoposition:lng, "radius", ship:body:radius)).
    set landed:prediction_miss_km to round(miss:km, 2).
  }
  addons:claudia:blocknote(ctx:block, landed).
}

// What an abort mid-descent leaves behind: the parachutes of group open if that is safe, then a
// safe craft. mode: chute_mode, "none" with no chute to open. Returns the titles of the chutes it opened.
function reentry_abort {
  parameter mode, group.
  local opened is list().
  if ship:body:atm:exists and not claudia_on_ground() {
    local pressure is ship:body:atm:altitudepressure(ship:altitude).
    local speed is ship:velocity:surface:mag.
    if reentry_drogue_action(mode, false, speed, reentry_agl(), pressure) = "deploy" {
      for t in reentry_open_chutes(ship:parts, true, group) { opened:add(t). }
    }
    if reentry_chute_action(mode, false, speed, reentry_agl(), pressure) = "deploy" {
      for t in reentry_open_chutes(ship:parts, false, group) { opened:add(t). }
    }
  }
  claudia_make_safe().
  return opened.
}

// The titles of the parachutes among parts still stowed.
function reentry_stowed_titles {
  parameter parts.
  local found is list().
  for p in reentry_chutes_in(parts) {
    if p:getmodule("ModuleParachute"):hasevent("deploy chute") {
      found:add(p:title).
    }
  }
  return found.
}

// Upward acceleration (m/s^2) at full throttle with the thrust along the vector along, at least a tenth of
// it so a craft lying flat still reads some.
function reentry_up_accel {
  parameter along.
  local a is ship:availablethrust / max(0.001, ship:mass).
  if along:mag < 0.001 {
    return a.
  }
  return a * max(0.1, vdot(along:normalized, ship:up:vector)).
}

function reentry {
  parameter options is lexicon().
  local extra is lexicon("max_g", 0, "max_temp_pct", 0, "geo", lexicon(), "splashed", false, "touchdown_speed", -1, "passes", 0, "shield_dropped", false,
    "chutes_used", list(), "chutes_kept", list(), "powered_dv", 0).
  local ctx is claudia_begin("reentry", options, reentry_defaults(), "auto").
  if ctx:error <> "" {
    return claudia_finish(ctx, false, reentry_option_error(options, ctx:error), extra).
  }
  local opt is ctx:opt.
  local unreadable is reentry_unreadable(opt).
  if unreadable <> "" {
    return claudia_finish(ctx, false, unreadable, extra).
  }
  local body_ is ship:body.
  if not body_:atm:exists {
    return claudia_finish(ctx, false, "body has no atmosphere", extra).
  }
  addons:claudia:blocknote(ctx:block, lexicon("phase", "charge", "at", "start", "units", round(ship:electriccharge), "pct", round(claudia_ec_pct(), 1))).
  set extra:geo to reentry_geo().
  set extra:chutes_kept to reentry_stowed_titles(ship:parts).
  // Wait (on rails) for the atmosphere if the craft is still above it, on the orbit the drop leaves.
  local entry_in is 0.
  if ship:altitude > body_:atm:height and not claudia_on_ground() {
    set entry_in to claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
    if entry_in < 0 {
      return claudia_finish(ctx, false, "the orbit never reaches the atmosphere (periapsis " + round(ship:periapsis) + " m)", extra).
    }
  }
  // This entry's stack (#557, #823) is read at block start, where ENTRYDROP refuses one separate can't drop, still above the air, and again at the drop
  // point (#815), its lead s before the air or at once inside it: that read flies, the engines light, the stack drops (Pe and stack noted at the air) and ENTRYDROP refuses one still on.
  local entry_ask is lexicon("separate", opt:separate, "jettison_stage", opt:jettison_stage, "chutes", opt:chutes <> "none" and opt:chute_mode <> "none", "core", core:part:uid).
  local below is addons:claudia:entrydrop(entry_ask).
  if below:error <> "" {
    return claudia_finish(ctx, false, below:error, extra).
  }
  set entry_ask:home to below:home.
  local auto_duration is opt:max_duration:istype("String").
  if auto_duration {
    set opt:max_duration to entry_in + 1800.
  }
  // An abort before the drop keeps the stack, and reads the Chute Group (ADR 0019) fresh: none is chosen yet.
  local ask is lexicon("none", opt:chutes = "none", "engine", false).
  // Core decides one late trim toward the Mission's landing target (#892); anything else flies nothing, the reason in the log, and the block goes on.
  claudia_fly_correction(ctx, addons:claudia:entrytrim(lexicon("to_air", entry_in, "home", entry_ask:home, "core", entry_ask:core, "has_node", hasnode, "stage_limit", opt:stage_limit, "dv_left", claudia_dv_recounted(), "isp", claudia_engine_isp()))).
  local held is opt:hold = "retrograde".
  local chute_mode is opt:chute_mode.
  local braking is false.
  local group is lexicon("last", false, "stages", list(), "uids", list(), "why", ""). // Chosen at the drop; empty, the first read below is fresh.
  // Falling into the air (#942), low charge doesn't end the block: Core's LOWCHARGE says when it lets go of the steering (the braking burn keeps its own: it alone points the engine) and when it arms the group's chutes once, which need no charge. Asked by claudia_tick, after the trim.
  local ec_step is 0.
  set ctx:ec_tick to {
    local low is addons:claudia:lowcharge(lexicon("ec_pct", claudia_ec_pct(), "min_ec_pct", opt:min_ec_pct, "pe", ship:periapsis, "air_top", body_:atm:height)).
    if low:carry_on and not braking { set held to false. unlock steering. sas off. claudia_rcs_off("low charge"). }
    if low:step > ec_step {
      set ec_step to low:step.
      set group to reentry_fresh_group(group, ask, extra:chutes_used).
      if ec_step = 2 and chute_mode <> "none" and group:uids:length > 0 {
        for drogues in list(true, false) { for t in reentry_open_chutes(ship:parts, drogues, group) { extra:chutes_used:add(t). } }
      }
      addons:claudia:blocknote(ctx:block, lexicon("phase", "low_charge", "pct", round(claudia_ec_pct(), 1), "steering_released", low:carry_on and not braking, "chutes_used", extra:chutes_used:join(", "))).
    }
    return low:abort_pct.
  }.
  if entry_in > below:lead {
    set entry_in to claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
    local waited is claudia_wait_until(ctx, time:seconds + entry_in - below:lead).
    if waited <> "" {
      reentry_abort(opt:chute_mode, reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts)).
      return claudia_finish(ctx, false, waited, extra).
    }
    set below to addons:claudia:entrydrop(entry_ask).
    if below:error <> "" {
      return claudia_finish(ctx, false, below:error, extra).
    }
  }
  // Its engines or its chutes (#824): Core decides, with this drop and these chutes, and whether a powered entry keeps its stack (#940); the block lights the engines.
  local finish is addons:claudia:entryfinish(lexicon("home", entry_ask:home, "chutes", entry_ask:chutes, "drop", opt:separate <> "none", "jettison_stage", opt:jettison_stage, "core", entry_ask:core)).
  local powered is false.
  if finish:error <> "" { addons:claudia:blocknote(ctx:block, lexicon("phase", "finish", "error", finish:error)). }
  if finish:error = "" {
    addons:claudia:blocknote(ctx:block, lexicon("phase", "finish", "why", finish:why)).
    if finish:powered and not claudia_on_ground() {
      set ship:control:pilotmainthrottle to 0.
      reentry_light(ship:engines, finish:engines).
      wait 0.1.
      if ship:availablethrust <= 0 { reentry_light(ship:engines, finish:spare). wait 0.1. }
      set powered to ship:availablethrust > 0.
      if powered and finish:keeps_stack { set opt:separate to "none". }
    }
  }
  local dropped is list().
  if below:names:length > 0 and not claudia_on_ground() {
    local drop is reentry_separation(opt:separate, below, ship:parts).
    local drop_error is drop:error.
    if drop_error = "" and claudia_drop_holds_core(drop:gone) {
      set drop_error to "it would drop the part running the script".
    }
    if drop_error = "" and drop:decouplers:length > 0 {
      local pe_before is ship:periapsis.
      set drop_error to claudia_fire_drop(ctx, drop, false). // landing first (#944): DropGuard's reason is noted, not obeyed
      if drop_error = "" { set dropped to list(pe_before, ship:periapsis, drop:gone[0]:ship). }
    }
    set below to addons:claudia:entrydrop(lexicon("home", entry_ask:home, "separate", opt:separate, "jettison_stage", opt:jettison_stage, "drop_error", drop_error)).
    if below:error <> "" {
      return claudia_finish(ctx, false, below:error, extra).
    }
  }
  // This entry's Chute Group (ADR 0019), chosen after any drop; none outside it is ever armed.
  set ask:engine to powered.
  set group to reentry_group_of(ask, addons:claudia:chutegroup(ask), ship:parts).
  set ec_step to min(ec_step, 1). // Armed before the drop, the group chosen now is armed too (stowed chutes only).
  if group:why <> "" { addons:claudia:blocknote(ctx:block, lexicon("phase", "chutes_widened", "why", group:why)). }
  if group:uids:length = 0 {
    set chute_mode to "none".
  }
  addons:claudia:shipsystems("entry").
  // The Entry Shield's decouplers, found on the craft that is left.
  local shield_uid is below:jettison.
  local shield is reentry_shield_modules(shield_uid, ship:parts).
  local shield_gone is shield:length = 0.
  // Log-only (#478): where the predictor puts the landing, with the shield drop and chutes this entry flies.
  local shield_uids is claudia_shield_drop(ship:parts, list(), shield).
  local predicted is addons:claudia:reentryimpact(lexicon("chutes", chute_mode <> "none", "shield_drop", shield_uids)).
  addons:claudia:blocknote(ctx:block, reentry_prediction_note(predicted)).
  local air_ut is time:seconds + (choose claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly) if entry_in > 0 else 0).
  local hold_ut is choose time:seconds + addons:claudia:turntime(lexicon("max_stopping_time", steeringmanager:maxstoppingtime, "to_air", air_ut - time:seconds)):hold_in if held else air_ut. // #941: hold from a turn time plus margin before the air (Core's hold_in)
  local waited is choose claudia_wait_until(ctx, hold_ut) if hold_ut > time:seconds else "".
  if waited = "" { if held { lock steering to srfretrograde. } set waited to choose claudia_warp_to(ctx, air_ut, air_ut - time:seconds) if air_ut > time:seconds else "". }
  if waited <> "" {
    reentry_abort(chute_mode, reentry_fresh_group(group, ask, extra:chutes_used)).
    return claudia_finish(ctx, false, waited, extra).
  }
  addons:claudia:blocknote(ctx:block, lexicon("phase", "charge", "at", "air_top", "units", round(ship:electriccharge), "pct", round(claudia_ec_pct(), 1))).
  if dropped:length > 0 {
    addons:claudia:blocknote(ctx:block, lexicon("phase", "stack_dropped", "pe_before", round(dropped[0]), "pe_after", round(dropped[1]), "stack_m", choose -1 if dropped[2]:isdead else round(dropped[2]:distance))).
  }

  local has_drogues is reentry_has_drogues(ship:parts, group).
  local drogues_out is false.
  local mains_out is false.
  local last_speed is ship:velocity:surface:mag.
  local sample_ut is time:seconds.
  local sample_v is ship:velocity:orbit.
  local sample_speed is last_speed.
  local decel is 0.
  local hot is 0.
  local event_eta is -1.
  local passes is 0.
  local in_air is false.
  local skipped is false.
  // Powered touchdown: the radar altitude braking starts at (reentry_brake_height; 0 until the first sample).
  local brake_h is 0.

  // Opens the parachutes due now (speed m/s, agl m, pressure atm). Open parachutes steer the craft
  // themselves, except while braking.
  local chute_tick is {
    parameter speed, agl, pressure.
    local chute_plan is reentry_chute_stage(chute_mode, has_drogues, drogues_out, mains_out, speed, agl, pressure).
    // Read again before each opening: the group may have gone, and a drogue with it.
    if chute_plan <> "wait" {
      set group to reentry_fresh_group(group, ask, extra:chutes_used).
      set has_drogues to reentry_has_drogues(ship:parts, group).
      set chute_plan to reentry_chute_stage(chute_mode, has_drogues, drogues_out, mains_out, speed, agl, pressure).
    }
    if chute_plan <> "wait" {
      for t in reentry_open_chutes(ship:parts, chute_plan = "drogues", group) {
        extra:chutes_used:add(t).
      }
      if chute_plan = "drogues" {
        set drogues_out to true.
      } else {
        set mains_out to true.
      }
      if not braking {
        unlock steering.
        claudia_rcs_off("steering released").
      }
    }
  }.

  // One tick of the fall; true once the craft is on the ground. The wait loop (claudia_warp_to) calls it
  // each tick as its stop and physics-warps the fall under reentry_warp_cap.
  local fall_tick is {
    if claudia_on_ground() {
      return true.
    }
    local now is time:seconds.
    set last_speed to ship:velocity:surface:mag.
    if ship:altitude <= body_:atm:height and not in_air {
      set in_air to true.
      set passes to passes + 1.
      set extra:passes to passes.
    }
    // Out of the atmosphere again: end the wait so the pass loop below can wait for the next.
    if reentry_skipped_out(in_air, ship:altitude, body_:atm:height, ship:verticalspeed) {
      set skipped to true.
      return true.
    }
    local agl is reentry_agl().
    local pressure is body_:atm:altitudepressure(ship:altitude).

    // Heat and g, sampled a quarter second apart so frame noise doesn't read as g.
    if now - sample_ut >= 0.25 {
      local felt is reentry_g_felt(sample_v, ship:velocity:orbit, now - sample_ut, ship:position - body_:position, body_:mu).
      set extra:max_g to max(extra:max_g, felt).
      // Engines off, the g felt is the drag (chutes included).
      if powered {
        local alt_now is ship:altitude.
        local ks is reentry_drag_steps(felt * 9.80665, last_speed, pressure, agl, { parameter down_m. return body_:atm:altitudepressure(alt_now - down_m). }).
        set brake_h to reentry_brake_height(-ship:verticalspeed, reentry_up_accel(-ship:velocity:surface), body_:mu / (body_:radius + alt_now) ^ 2, ks, agl).
      }
      set hot to reentry_hottest().
      set extra:max_temp_pct to max(extra:max_temp_pct, hot).
      set decel to (sample_speed - last_speed) / (now - sample_ut).
      set sample_ut to now.
      set sample_v to ship:velocity:orbit.
      set sample_speed to last_speed.
    }

    if reentry_shield_due(shield_gone, last_speed, agl, powered and reentry_brake_due(agl, brake_h, last_speed)) {
      local count_before is ship:parts:length.
      reentry_shield_drop(shield_uid, { return ship:parts. }, below:jettison_first).
      set shield_gone to true.
      // The shield leaving is not a part lost: count what is left once it has gone.
      wait 0.5.
      set extra:shield_dropped to ship:parts:length < count_before.
      set ctx:parts to ship:parts:length.
    }
    chute_tick(last_speed, agl, pressure).
    if agl < 1000 and not gear { gear on. }
    if powered and reentry_brake_due(agl, brake_h, last_speed) {
      set braking to true.
      return true.
    }

    // The next parachute or shield drop, by height (falling at the vertical speed) or by speed (slowing
    // at the sampled rate); the air-pressure test is not predicted.
    local descent is -ship:verticalspeed.
    set event_eta to reentry_eta_to(agl, reentry_warp_low_agl(), descent).
    if not shield_gone {
      set event_eta to reentry_eta_first(event_eta, reentry_eta_first(reentry_eta_to(agl, reentry_shield_last_agl(), descent), reentry_eta_to(last_speed, reentry_shield_speed(), decel))).
    }
    if powered {
      set event_eta to reentry_eta_first(event_eta, reentry_eta_to(agl, max(0, brake_h), descent)).
    }
    if chute_mode = "safe" and not mains_out {
      local chute_eta is 0.
      if has_drogues and not drogues_out {
        set chute_eta to reentry_eta_first(reentry_eta_to(agl, reentry_drogue_last_agl(), descent), reentry_eta_to(last_speed, reentry_drogue_max_speed(), decel)).
      } else {
        set chute_eta to reentry_eta_first(reentry_eta_to(agl, reentry_chute_last_agl(), descent), reentry_eta_to(last_speed, reentry_chute_max_speed(), decel)).
      }
      set event_eta to reentry_eta_first(event_eta, chute_eta).
    }
    return claudia_on_ground().
  }.
  // Physics warp is never used above the atmosphere.
  local cap is {
    if ship:altitude > body_:atm:height {
      return 1.
    }
    return reentry_warp_cap(hot, drogues_out, mains_out, reentry_agl(), event_eta).
  }.
  local fell is claudia_warp_to(ctx, time:seconds + opt:max_duration + 3600, 0, fall_tick, cap).
  // A skip-out waits on rails for the next entry, then falls again (#592).
  until fell <> "" or not skipped {
    set skipped to false.
    set in_air to false.
    local refusal is reentry_skip_refusal(passes, ship:periapsis, body_:atm:height).
    if refusal <> "" {
      set fell to refusal.
    } else {
      set entry_in to claudia_fall_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
      if entry_in < 0 {
        set fell to "skipped out onto an orbit that never reaches the atmosphere again (periapsis " + round(ship:periapsis) + " m)".
      } else {
        set opt:max_duration to reentry_next_deadline(auto_duration, opt:max_duration, time:seconds - ctx:start_ut, entry_in).
        set fell to claudia_wait_until(ctx, time:seconds + entry_in).
        if fell = "" {
          set fell to claudia_warp_to(ctx, time:seconds + opt:max_duration + 3600, 0, fall_tick, cap).
        }
      }
    }
  }
  // The braking burn of a powered touchdown, at 1x: down to reentry_powered_soft by reentry_powered_end m of
  // radar, then held there to the ground, along surface retrograde until slow, then up leaning against
  // the drift. It never stages, so the delta-v it burns is the throttle's. Out of fuel it lights what else has some and falls on: only the touchdown's speed fails it.
  if fell = "" and braking and not claudia_on_ground() {
    local dv_before is ctx:dv_used.
    local tset is 0.
    local steer is -ship:velocity:surface.
    lock steering to steer.
    lock throttle to tset.
    gear on.
    // RCS stays off while braking unless the craft has no wheel torque, as for land's (#506).
    set ctx:rcs_braking to true.
    until claudia_on_ground() {
      local up_v is ship:up:vector.
      local vel is ship:velocity:surface.
      set last_speed to vel:mag.
      local agl is reentry_agl().
      if ship:availablethrust <= 0 { reentry_light(ship:engines, finish:spare). }
      if vel:mag > 5 and ship:verticalspeed < 0 {
        set steer to -vel.
      } else {
        set steer to up_v - vxcl(up_v, vel) * 0.1.
      }
      set tset to reentry_brake_throttle(agl, -ship:verticalspeed, reentry_up_accel(ship:facing:forevector), body_:mu / (body_:radius + ship:altitude) ^ 2).
      chute_tick(last_speed, agl, body_:atm:altitudepressure(ship:altitude)).
      local reason is claudia_tick(ctx, tset, false).
      if reason <> "" {
        set fell to reason.
        break.
      }
      wait 0.
    }
    set tset to 0.
    set extra:powered_dv to round(ctx:dv_used - dv_before, 1).
  }
  if fell <> "" {
    for t in reentry_abort(chute_mode, reentry_fresh_group(group, ask, extra:chutes_used)) {
      extra:chutes_used:add(t).
    }
    set extra:chutes_kept to reentry_stowed_titles(ship:parts).
    set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
    set extra:max_g to round(extra:max_g, 2).
    set extra:geo to reentry_geo().
    return claudia_finish(ctx, false, fell, extra).
  }

  if powered {
    claudia_release_controls().
  } else {
    unlock steering.
    claudia_rcs_off("block end").
  }
  addons:claudia:blocknote(ctx:block, lexicon("phase", "chutes_cut", "titles", reentry_cut_chutes(ship:parts, group:last):join(", "))).
  set extra:chutes_kept to reentry_stowed_titles(ship:parts).
  set extra:touchdown_speed to round(last_speed, 1).
  set extra:max_g to round(extra:max_g, 2).
  set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
  set extra:geo to reentry_geo().
  set extra:splashed to status = "SPLASHED".
  set extra:sun_elevation_deg to round(90 - vang(ship:up:vector, body("Sun"):position), 1).
  reentry_note_landing(ctx, predicted, last_speed).
  if not reentry_touchdown_ok(last_speed) {
    return claudia_finish(ctx, false, "touchdown at " + round(last_speed, 1) + " m/s is over " + reentry_touchdown_limit() + " m/s", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
