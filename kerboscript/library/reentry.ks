// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block reentry: brings a capsule down through an atmosphere. Built on claudia_lib.ks (and
// deorbit.ks's stage drop plan, for separate); block-only helpers are prefixed reentry_. The "//|"
// lines are Claudia's docs for it; the first is its summary.

@lazyglobal off.

//| reentry(options): brings the craft down through its body's atmosphere: drops the stack below the heat shield, waits (on rails)
//| for the atmosphere if still in space, holds the heat shield forward (surface retrograde), drops it once
//| the worst heating is over, opens the parachutes only when safe (never staged), physics-warping the fall
//| (1x below 100 m), and ends once landed or splashed.
//| Options:
//|   chute_mode ["safe"]: drogues first (under 500 m/s, over 0.02 atm, or by 3000 m above the ground), then
//|     mains once they are out (under 250 m/s, over 0.04 atm, or by 1500 m); "none" never opens them.
//|   chutes ["auto"]: the Chute Allocation's for this entry, "all", "none" or a list of part tags; no other is armed.
//|   touchdown ["chutes"]: or "powered": falls under its chutes (or none), then brakes on its lit engines by
//|     radar altitude (the stopping distance counts drag and thrust over weight) to 2 m/s; over 6 m/s fails. With no lit engine it fails at once.
//|   jettison_stage ["auto"]: the stage whose decouplers drop the heat shield, "auto" for the decoupler on
//|     or beside it (if powered, not one whose drop takes the engines), or "none". Only its decouplers fire.
//|   separate ["auto"]: drops the stack (service module, tanks) before the atmosphere, never by the shield's
//|     decoupler, and if powered only what hangs below the engines; "none" keeps it; or a stage number.
//|   hold ["retrograde"]: points the craft's forward axis at surface retrograde; "none" leaves it.
//|   max_duration ["auto"]: the wait for the atmosphere plus 1800 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block (it never stages).
//| Extra results: max_g (peak g), max_temp_pct (hottest part, percent of its limit), geo (lat, lng, alt),
//| splashed, touchdown_speed (m/s at contact; over 15 m/s fails), passes (max 3; a skip-out waits for the
//| next, else "skipped out ..."), shield_dropped, chutes_used and chutes_kept (titles), powered_dv (m/s).
//| Fails with "body has no atmosphere", an orbit that never reaches the atmosphere, an option it can't read,
//| "stack attached below the heat shield: <names>", a hard touchdown, or an abort (a mid-air abort still
//| opens the parachutes if safe). Fly it after a return or a de-orbit burn, periapsis inside the atmosphere;
//| it plans no burn. Example: `local res is reentry(). addons:claudia:report(res).`

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

// Powered touchdown (#628). The braking burn comes down to soft m/s by end m of radar altitude (the craft's
// centre of mass stands a few metres up on its legs), then holds soft to the ground; a touchdown over
// limit m/s fails the block, well under reentry_touchdown_limit since the engines can do better than chutes.
// Braking starts with the thrust over margin, lag s of the fall early (the throttle and the last of the turn).
function reentry_powered_soft { return 2. }
function reentry_powered_end { return 10. }
function reentry_powered_limit { return 6. }
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
  return lexicon("chute_mode", "safe", "jettison_stage", "auto", "hold", "retrograde", "separate", "auto", "touchdown", "chutes", "chutes", "auto").
}

// "" or why an option isn't one the block knows. A bare chutes tag becomes a list of one.
function reentry_unreadable {
  parameter opt.
  if opt:chute_mode <> "safe" and opt:chute_mode <> "none" {
    return "chute_mode must be 'safe' or 'none', not '" + opt:chute_mode + "'".
  }
  if opt:touchdown <> "chutes" and opt:touchdown <> "powered" {
    return "touchdown must be 'chutes' or 'powered', not '" + opt:touchdown + "'".
  }
  local chutes_rule is "chutes must be 'auto', 'none', 'all' or a list of part tags".
  if opt:chutes:istype("String") {
    if not list("auto", "none", "all"):contains(opt:chutes) {
      set opt:chutes to list(opt:chutes).
    }
  } else if opt:chutes:istype("List") {
    for tag in opt:chutes {
      if not tag:istype("String") {
        return chutes_rule.
      }
    }
  } else {
    return chutes_rule.
  }
  if opt:hold <> "retrograde" and opt:hold <> "none" {
    return "hold must be 'retrograde' or 'none', not '" + opt:hold + "'".
  }
  if opt:jettison_stage:istype("String") {
    if opt:jettison_stage <> "auto" and opt:jettison_stage <> "none" {
      return "jettison_stage must be 'auto', 'none' or a stage number, not '" + opt:jettison_stage + "'".
    }
  }
  // separate is read as deorbit's, without part names.
  local sep is opt:separate.
  local sep_rule is "separate must be 'auto', 'none' or a stage number, not ".
  if sep:istype("String") {
    if sep <> "auto" and sep <> "none" {
      return sep_rule + "'" + sep + "'".
    }
  } else if sep:istype("Scalar") {
    if sep < 0 or sep <> round(sep) {
      return sep_rule + sep.
    }
  } else {
    return sep_rule + "a " + sep:typename.
  }
  return "".
}

// What to do about the parachutes now: "deploy" or "wait". mode: "safe" or "none"; out: they are
// already open; speed in m/s and agl (height above the ground) in m; pressure in atm.
function reentry_chute_action {
  parameter mode, out, speed, agl, pressure.
  if mode = "none" or out {
    return "wait".
  }
  if agl <= reentry_chute_last_agl() {
    return "deploy".
  }
  if speed <= reentry_chute_max_speed() and pressure >= reentry_chute_min_pressure() {
    return "deploy".
  }
  return "wait".
}

// What to do about the drogue chutes now: "deploy" or "wait". Same arguments as reentry_chute_action.
function reentry_drogue_action {
  parameter mode, out, speed, agl, pressure.
  if mode = "none" or out {
    return "wait".
  }
  if agl <= reentry_drogue_last_agl() {
    return "deploy".
  }
  if speed <= reentry_drogue_max_speed() and pressure >= reentry_drogue_min_pressure() {
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

// True if the heat shield should drop now. gone: it has already been dropped (or there is none).
function reentry_shield_due {
  parameter gone, speed, agl.
  if gone {
    return false.
  }
  return speed <= reentry_shield_speed() or agl <= reentry_shield_last_agl().
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

// Touchdown within what the crew and craft survive: reentry_touchdown_limit, or reentry_powered_limit if powered.
function reentry_touchdown_ok {
  parameter speed, powered is false.
  return speed <= reentry_touchdown_max(powered).
}

function reentry_touchdown_max {
  parameter powered.
  if powered {
    return reentry_powered_limit().
  }
  return reentry_touchdown_limit().
}

// ---- Chutes (#628) ----

// The chutes reentry may open. choice: "auto", "none", "all" or a list of part tags; aboard: the craft's
// parachutes (anything answering uid and tag); set: addons:claudia:chuteallocation()'s lexicon, read for
// "auto" (an error or all keeps every chute, as before the Chute Allocation). Returns lexicon("error", ""
// or the tag no chute carries, "uids", the uids it may open).
function reentry_chute_set {
  parameter choice, aboard, set.
  local found is lexicon("error", "", "uids", list()).
  if choice:istype("String") and choice = "none" {
    return found.
  }
  local every is choice:istype("String") and (choice = "all" or set:error <> "" or set:all).
  if every {
    for p in aboard {
      found:uids:add(p:uid).
    }
    return found.
  }
  if choice:istype("String") {
    for uid in set:uids {
      found:uids:add(uid).
    }
    return found.
  }
  for tag in choice {
    local any is false.
    for p in aboard {
      if p:tag = tag {
        set any to true.
        if not found:uids:contains(p:uid) {
          found:uids:add(p:uid).
        }
      }
    }
    if not any {
      set found:error to "chutes: no parachute is tagged '" + tag + "'".
      return found.
    }
  }
  return found.
}

// True if part p is a parachute.
function reentry_is_chute {
  parameter p.
  return p:hasmodule("ModuleParachute").
}

// The parachutes among parts.
function reentry_chutes_in {
  parameter parts.
  local found is list().
  for p in parts {
    if reentry_is_chute(p) {
      found:add(p).
    }
  }
  return found.
}

// True if one of the chutes reentry may open (uids) is a drogue.
function reentry_has_drogues {
  parameter parts, uids.
  for p in reentry_chutes_in(parts) {
    if uids:contains(p:uid) and reentry_is_drogue(p:name) {
      return true.
    }
  }
  return false.
}

// Opens every stowed parachute of one kind, drogues (true) or mains (false), among parts whose uid is in
// uids; a chute outside uids is never armed. Returns the titles of those it opened.
function reentry_open_chutes {
  parameter parts, drogues, uids.
  local opened is list().
  for p in reentry_chutes_in(parts) {
    if uids:contains(p:uid) and reentry_is_drogue(p:name) = drogues {
      local m is p:getmodule("ModuleParachute").
      if m:hasevent("deploy chute") {
        m:doevent("deploy chute").
        opened:add(p:title).
      }
    }
  }
  return opened.
}

// ---- Powered touchdown (#628) ----

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

// True if the braking must start now at radar m (reentry_brake_height's figure brake_h).
function reentry_brake_due {
  parameter radar, brake_h.
  return brake_h < 0 or radar <= brake_h.
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

// Seconds until an orbit (ecc, sma in m, true anomaly ta in degrees) next falls to radius r_entry
// from above, around a body of gravitational parameter mu. 0 if it is at or under r_entry now;
// -1 if the orbit never gets that low.
function reentry_entry_eta {
  parameter ecc, sma, mu, r_entry, ta.
  return claudia_fall_eta(ecc, sma, mu, r_entry, ta).
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

// The heat-shield decoupler modules chosen by jettison ("auto", "none" or a stage number).
function reentry_shield_modules {
  parameter jettison, parts.
  local found is list().
  if jettison:istype("String") and jettison = "none" {
    return found.
  }
  for p in parts {
    local wanted is false.
    if claudia_is_decoupler(p) {
      if jettison:istype("String") {
        set wanted to reentry_shield_beside(p).
      } else {
        set wanted to p:stage = jettison.
      }
    }
    if wanted {
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

// Fires the shield's decouplers one at a time; returns how many events it fired. The list is rebuilt from
// parts_of() (a delegate to the craft's parts) before each event, so a module on a part that has left, by
// an earlier event splitting the craft, is not there to fire (#558).
function reentry_shield_drop {
  parameter jettison, parts_of.
  local fired is list().
  until false {
    local next is "".
    for drop in reentry_shield_modules(jettison, parts_of()) {
      if next:istype("String") and not fired:contains(drop:module:part:uid + drop:event) {
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

// True if the decoupler p is a heat shield or sits against one.
function reentry_shield_beside {
  parameter p.
  if p:hasmodule("ModuleAblator") {
    return true.
  }
  if p:hasparent and p:parent:hasmodule("ModuleAblator") {
    return true.
  }
  for child in p:children {
    if child:hasmodule("ModuleAblator") {
      return true.
    }
  }
  return false.
}

// ---- The stack (#557) ----

// The stack: the parts on the far side of the heat shield from home (the uid of the crewed or command
// part), or with no heat shield the parts below the first decouplers under home. parts: kOS parts, or
// anything answering uid, name, hasparent, parent and hasmodule. Returns lexicon("shielded", the craft
// has a heat shield, "parts", the stack, "decouplers", those nearest the shield (or home) that drop it,
// "gone", the parts they take, "stuck", stack parts with no decoupler between them and the shield). A
// decoupler stays with the part above it, so the one next to the shield is not the stack. keep_engines
// (a powered touchdown, #628): every part with an engine, the parts between it and the shield (or home)
// and those hanging on them with no decoupler between are not the stack but "kept". Fires nothing.
function reentry_stack {
  parameter parts, home_uid, keep_engines is false.
  local found is lexicon("shielded", false, "parts", list(), "decouplers", list(), "gone", list(), "stuck", list(), "kept", list()).
  local parent_of is lexicon().
  local is_dec is lexicon().
  local anchor is home_uid.
  local home is "".
  for p in parts {
    if p:hasparent {
      set parent_of[p:uid] to p:parent.
    } else {
      set parent_of[p:uid] to "".
    }
    local is_shield is p:hasmodule("ModuleAblator").
    // A heat shield is never the decoupler that drops the stack (#516).
    set is_dec[p:uid] to claudia_is_decoupler(p) and not is_shield.
    if is_shield and not found:shielded {
      set found:shielded to true.
      set anchor to p:uid.
    }
    if p:uid = home_uid {
      set home to p.
    }
  }
  // Home under the shield (a craft built from the far side): the stack is what is not under it, and
  // no decoupler drops it without taking home too.
  local reversed is false.
  if found:shielded and not home:istype("String") {
    set reversed to reentry_walk(home, anchor, parent_of, is_dec):under.
  }
  local kept is lexicon().
  if keep_engines and not reversed {
    for p in parts {
      if p:hasmodule("ModuleEngines") or p:hasmodule("ModuleEnginesFX") {
        if reentry_walk(p, anchor, parent_of, is_dec):under {
          local cur is p.
          until cur:istype("String") or cur:uid = anchor {
            set kept[cur:uid] to true.
            set cur to parent_of[cur:uid].
          }
        }
      }
    }
  }
  local seen is lexicon().
  for p in parts {
    if kept:haskey(p:uid) {
      found:kept:add(p).
    } else if p:uid <> anchor {
      local way is reentry_walk(p, anchor, parent_of, is_dec, kept).
      if reversed {
        if not way:under {
          found:parts:add(p).
          found:stuck:add(p).
        }
      } else if way:under {
        local top is way:top.
        if top:istype("String") and way:at <> anchor {
          found:kept:add(p).
        } else if top:istype("String") {
          if found:shielded {
            found:parts:add(p).
            found:stuck:add(p).
          }
        } else if top:uid <> p:uid {
          found:parts:add(p).
          found:gone:add(p).
          if not seen:haskey(top:uid) {
            set seen[top:uid] to true.
            found:decouplers:add(top).
          }
        }
      }
    }
  }
  return found.
}

// Walking up from part p by parent_of (uid -> parent part or ""): lexicon("under", it reaches the part whose uid
// is anchor, or one in stop (uid -> true), "at", the uid it reached, "top", the decoupler on the way nearest
// it, p itself included, or "" for none). is_dec: uid -> true for a decoupler that is not a heat shield.
function reentry_walk {
  parameter p, anchor, parent_of, is_dec, stop is lexicon().
  local top is "".
  local cur is p.
  until cur:istype("String") {
    if cur:uid = anchor or stop:haskey(cur:uid) {
      return lexicon("under", true, "at", cur:uid, "top", top).
    }
    if is_dec[cur:uid] {
      set top to cur.
    }
    set cur to parent_of[cur:uid].
  }
  return lexicon("under", false, "at", "", "top", "").
}

// The names of parts, each once, in order.
function reentry_names {
  parameter parts.
  local names is list().
  for p in parts {
    if not names:contains(p:name) {
      names:add(p:name).
    }
  }
  return names.
}

// What separate drops of a reentry_stack, as claudia_drop_plan's lexicon: nothing for "none"; for "auto"
// the stack through its decouplers, or an error if a part of it has none, and nothing on a craft with no
// heat shield (as today); for a stage number, that stage's decouplers as deorbit's separate drops them
// (never a heat shield's).
function reentry_separation {
  parameter sep, below.
  if sep:istype("Scalar") {
    return deorbit_stage_drop_plan(sep).
  }
  local plan is claudia_drop_plan(list()).
  if sep = "auto" and below:shielded {
    if below:stuck:length > 0 {
      set plan:error to reentry_names(below:stuck):join(", ") + ": no decoupler between it and the heat shield".
      return plan.
    }
    for d in below:decouplers {
      plan:decouplers:add(d).
    }
    for g in below:gone {
      plan:gone:add(g).
    }
  }
  return plan.
}

// "" or why reentry fails before the atmosphere: a craft with a heat shield (shielded) whose stack
// (names, a list) is still on; drop_error: why the drop failed, or "".
function reentry_stack_refusal {
  parameter shielded, names, drop_error.
  if not shielded or names:length = 0 {
    return "".
  }
  local reason is "stack attached below the heat shield: " + names:join(", ") + "; drop it first or use separate".
  if drop_error <> "" {
    set reason to reason + " (separate: " + drop_error + ")".
  }
  return reason.
}

// The uid of the part the crew rides in, else of the first command part, else of the part running the script.
function reentry_home_uid {
  if ship:crew:length > 0 {
    return ship:crew[0]:part:uid.
  }
  for p in ship:parts {
    if p:hasmodule("ModuleCommand") {
      return p:uid.
    }
  }
  return core:part:uid.
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

// What an abort mid-descent leaves behind: the parachutes it may open (uids) open if that is safe, then a
// safe craft. mode: chute_mode, "none" with no chute to open. Returns the titles of the chutes it opened.
function reentry_abort {
  parameter mode, uids.
  local opened is list().
  if ship:body:atm:exists and not claudia_on_ground() {
    local pressure is ship:body:atm:altitudepressure(ship:altitude).
    local speed is ship:velocity:surface:mag.
    if reentry_drogue_action(mode, false, speed, reentry_agl(), pressure) = "deploy" {
      for t in reentry_open_chutes(ship:parts, true, uids) { opened:add(t). }
    }
    if reentry_chute_action(mode, false, speed, reentry_agl(), pressure) = "deploy" {
      for t in reentry_open_chutes(ship:parts, false, uids) { opened:add(t). }
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
    return claudia_finish(ctx, false, ctx:error, extra).
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
  set extra:geo to reentry_geo().
  local powered is opt:touchdown = "powered".
  // The chutes it may open (#628): the Chute Allocation's for "auto"; none outside them is ever armed.
  local allocation is lexicon("error", "", "all", true, "uids", list()).
  if opt:chutes:istype("String") and opt:chutes = "auto" {
    set allocation to addons:claudia:chuteallocation().
  }
  local allowed is reentry_chute_set(opt:chutes, reentry_chutes_in(ship:parts), allocation).
  if allowed:error <> "" {
    return claudia_finish(ctx, false, allowed:error, extra).
  }
  local uids is allowed:uids.
  local chute_mode is opt:chute_mode.
  if uids:length = 0 {
    set chute_mode to "none".
  }
  set extra:chutes_kept to reentry_stowed_titles(ship:parts).

  // Wait (on rails) for the atmosphere if the craft is still above it.
  local entry_in is 0.
  if ship:altitude > body_:atm:height and not claudia_on_ground() {
    set entry_in to reentry_entry_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
    if entry_in < 0 {
      return claudia_finish(ctx, false, "the orbit never reaches the atmosphere (periapsis " + round(ship:periapsis) + " m)", extra).
    }
  }
  // The stack (#557) goes once the orbit is known to come down, before the atmosphere and any hold:
  // with a service module on, the craft flies engine first and the shield can't lead. A craft with a
  // heat shield that keeps its stack fails here, still above the air; one already in the air flies on.
  local home is reentry_home_uid().
  local below is reentry_stack(ship:parts, home, powered).
  if (below:parts:length > 0 or opt:separate:istype("Scalar")) and not claudia_on_ground() {
    local drop is reentry_separation(opt:separate, below).
    local drop_error is drop:error.
    if drop_error = "" and claudia_drop_holds_core(drop:gone) {
      set drop_error to "it would drop the part running the script".
    }
    // A stage number that drops nothing fails as deorbit's does.
    if drop_error <> "" and opt:separate:istype("Scalar") {
      return claudia_finish(ctx, false, "separate: " + drop_error, extra).
    }
    if drop_error = "" and drop:decouplers:length > 0 {
      set drop_error to claudia_fire_drop(ctx, drop).
    }
    set below to reentry_stack(ship:parts, home, powered).
    if ship:altitude > body_:atm:height {
      local refused is reentry_stack_refusal(below:shielded, reentry_names(below:parts), drop_error).
      if refused <> "" {
        return claudia_finish(ctx, false, refused, extra).
      }
    }
  }
  // A powered touchdown needs its engines lit: reentry never stages.
  if powered and ship:availablethrust <= 0 {
    return claudia_finish(ctx, false, "touchdown 'powered' needs a lit engine (reentry never stages)", extra).
  }
  // The shield's decouplers, found on the craft that is left; a powered touchdown keeps a shield ("auto")
  // whose drop would take the engines with it.
  local shield is reentry_shield_modules(opt:jettison_stage, ship:parts).
  if powered and below:shielded and below:kept:length > 0 and opt:jettison_stage:istype("String") {
    set shield to list().
  }
  local shield_gone is shield:length = 0.
  local auto_duration is opt:max_duration:istype("String").
  if auto_duration {
    set opt:max_duration to entry_in + 1800.
  }
  local held is opt:hold = "retrograde".
  if held {
    lock steering to srfretrograde.
  }
  if entry_in > 0 {
    local waited is claudia_wait_until(ctx, time:seconds + entry_in).
    if waited <> "" {
      reentry_abort(chute_mode, uids).
      return claudia_finish(ctx, false, waited, extra).
    }
  }

  local has_drogues is reentry_has_drogues(ship:parts, uids).
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
  local braking is false.
  local brake_h is 0.

  // Opens the parachutes due now (speed m/s, agl m, pressure atm). Open parachutes steer the craft
  // themselves, except while braking.
  local chute_tick is {
    parameter speed, agl, pressure.
    local chute_plan is reentry_chute_stage(chute_mode, has_drogues, drogues_out, mains_out, speed, agl, pressure).
    if chute_plan <> "wait" {
      for t in reentry_open_chutes(ship:parts, chute_plan = "drogues", uids) {
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

    if reentry_shield_due(shield_gone, last_speed, agl) {
      local count_before is ship:parts:length.
      reentry_shield_drop(opt:jettison_stage, { return ship:parts. }).
      set shield_gone to true.
      // The shield leaving is not a part lost: count what is left once it has gone.
      wait 0.5.
      set extra:shield_dropped to ship:parts:length < count_before.
      set ctx:parts to ship:parts:length.
    }
    chute_tick(last_speed, agl, pressure).
    if powered and reentry_brake_due(agl, brake_h) {
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
      set entry_in to reentry_entry_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
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
  // the drift. It never stages, so the delta-v it burns is the throttle's.
  if fell = "" and braking and not claudia_on_ground() {
    local dv_before is ctx:dv_used.
    local tset is 0.
    local steer is -ship:velocity:surface.
    lock steering to steer.
    lock throttle to tset.
    gear on.
    until claudia_on_ground() {
      local up_v is ship:up:vector.
      local vel is ship:velocity:surface.
      set last_speed to vel:mag.
      local agl is reentry_agl().
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
    for t in reentry_abort(chute_mode, uids) {
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
  set extra:chutes_kept to reentry_stowed_titles(ship:parts).
  set extra:touchdown_speed to round(last_speed, 1).
  set extra:max_g to round(extra:max_g, 2).
  set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
  set extra:geo to reentry_geo().
  set extra:splashed to status = "SPLASHED".
  if not reentry_touchdown_ok(last_speed, powered) {
    return claudia_finish(ctx, false, "touchdown at " + round(last_speed, 1) + " m/s is over " + reentry_touchdown_max(powered) + " m/s", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
