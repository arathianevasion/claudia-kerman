// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// Building Block reentry: brings a capsule down through an atmosphere. Built on claudia_lib.ks;
// block-only helpers are prefixed reentry_. The "//|" lines are Claudia's docs for it; the first is
// its summary.

@lazyglobal off.

//| reentry(options): brings the craft down through the atmosphere of its body: waits (on rails) for
//| the atmosphere if it is still in space, holds the heat shield forward (surface retrograde), drops
//| the heat shield once the worst of the heating is over, opens the parachutes only when it is safe
//| (never staged, never fired by hand), physics-warping the fall (1x below 100 m), and ends once landed or splashed.
//| Options:
//|   chute_mode ["safe"]: "safe" opens the parachutes in two stages: drogue chutes first (under 500 m/s,
//|     over 0.02 atm, or at the latest 3000 m above the ground), then the main parachutes once the drogues
//|     are out (under 250 m/s, over 0.04 atm, or at the latest 1500 m above the ground); a craft with no
//|     drogues opens its mains alone. "none" never opens them (for a craft that lands another way).
//|   jettison_stage ["auto"]: the stage number whose decouplers drop the heat shield, "auto" for the
//|     decoupler on or beside the heat shield (just above the capsule), or "none" to keep it.
//|     The decouplers are fired by themselves; other parts of that stage are not staged.
//|   hold ["retrograde"]: "retrograde" points the craft's forward axis at surface retrograde, so a
//|     pod's heat shield leads; "none" leaves the attitude alone.
//|   max_duration ["auto"]: the wait for the atmosphere plus 1800 s.
//|   plus min_ec_pct, abort_on_part_loss and stage_limit as for every block (this block never stages: it holds no burn, so auto-staging never fires).
//| Extra results: max_g (peak g felt, in g), max_temp_pct (hottest part, in percent of its limit),
//| geo (lexicon lat, lng, alt where it ended), splashed (true if it ended in water), touchdown_speed
//| (m/s just before contact; over 15 m/s fails the block), shield_dropped (true if the heat shield's
//| decoupler fired and the craft lost the part). Fails with "body has no atmosphere", an
//| orbit that never reaches the atmosphere, an option it can't read, a hard
//| touchdown, or an abort (a mid-air abort still opens the parachutes if that is safe).
//| Fly it after a return or a de-orbit burn, with the orbit's periapsis inside the atmosphere. It does
//| not plan or burn anything. Example: `local res is reentry(). addons:claudia:report(res).`

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
  return lexicon("chute_mode", "safe", "jettison_stage", "auto", "hold", "retrograde", "separate", "auto").
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

// Touchdown within what the crew and craft survive.
function reentry_touchdown_ok {
  parameter speed.
  return speed <= reentry_touchdown_limit().
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

// The heat-shield decoupler modules chosen by jettison ("auto", "none" or a stage number).
function reentry_shield_modules {
  parameter jettison.
  local found is list().
  if jettison:istype("String") and jettison = "none" {
    return found.
  }
  for p in ship:parts {
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
// decoupler stays with the part above it, so the one next to the shield is not the stack. Fires nothing.
function reentry_stack {
  parameter parts, home_uid.
  local found is lexicon("shielded", false, "parts", list(), "decouplers", list(), "gone", list(), "stuck", list()).
  local up is lexicon().
  local is_dec is lexicon().
  local anchor is home_uid.
  local home is "".
  for p in parts {
    if p:hasparent {
      set up[p:uid] to p:parent.
    } else {
      set up[p:uid] to "".
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
    set reversed to reentry_walk(home, anchor, up, is_dec):under.
  }
  local seen is lexicon().
  for p in parts {
    if p:uid <> anchor {
      local way is reentry_walk(p, anchor, up, is_dec).
      if reversed {
        if not way:under {
          found:parts:add(p).
          found:stuck:add(p).
        }
      } else if way:under {
        local top is way:top.
        if top:istype("String") {
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

// Walking up from part p by up (uid -> parent part or ""): lexicon("under", it reaches the part whose uid
// is anchor, "top", the decoupler on the way nearest anchor, p itself included, or "" for none). is_dec:
// uid -> true for a decoupler that is not a heat shield.
function reentry_walk {
  parameter p, anchor, up, is_dec.
  local top is "".
  local cur is p.
  until cur:istype("String") {
    if cur:uid = anchor {
      return lexicon("under", true, "top", top).
    }
    if is_dec[cur:uid] {
      set top to cur.
    }
    set cur to up[cur:uid].
  }
  return lexicon("under", false, "top", "").
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
// the stack through its decouplers, or an error if a part of it has none; for a stage number, that
// stage's decouplers as deorbit's separate drops them (never a heat shield's).
function reentry_separation {
  parameter sep, stack.
  if sep:istype("Scalar") {
    return deorbit_stage_drop_plan(sep).
  }
  local plan is claudia_drop_plan(list()).
  if sep = "auto" {
    if stack:stuck:length > 0 {
      set plan:error to reentry_names(stack:stuck):join(", ") + ": no decoupler between it and the heat shield".
      return plan.
    }
    for d in stack:decouplers {
      plan:decouplers:add(d).
    }
    for g in stack:gone {
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

// True if the craft carries a drogue chute.
function reentry_has_drogues {
  for p in ship:parts {
    if p:hasmodule("ModuleParachute") and reentry_is_drogue(p:name) {
      return true.
    }
  }
  return false.
}

// Opens every stowed parachute of one kind: drogues (true) or mains (false). Returns how many it opened.
function reentry_open_chutes {
  parameter drogues.
  local opened is 0.
  for p in ship:parts {
    if p:hasmodule("ModuleParachute") and reentry_is_drogue(p:name) = drogues {
      local m is p:getmodule("ModuleParachute").
      if m:hasevent("deploy chute") {
        m:doevent("deploy chute").
        set opened to opened + 1.
      }
    }
  }
  return opened.
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

// What an abort mid-descent leaves behind: parachutes open if that is safe, then a safe craft.
function reentry_abort {
  parameter opt.
  if ship:body:atm:exists and not claudia_on_ground() {
    local pressure is ship:body:atm:altitudepressure(ship:altitude).
    local speed is ship:velocity:surface:mag.
    if reentry_drogue_action(opt:chute_mode, false, speed, reentry_agl(), pressure) = "deploy" {
      reentry_open_chutes(true).
    }
    if reentry_chute_action(opt:chute_mode, false, speed, reentry_agl(), pressure) = "deploy" {
      reentry_open_chutes(false).
    }
  }
  claudia_make_safe().
}

function reentry {
  parameter options is lexicon().
  local extra is lexicon("max_g", 0, "max_temp_pct", 0, "geo", lexicon(), "splashed", false, "touchdown_speed", -1, "shield_dropped", false).
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
  // The stack (#557) goes first, before the atmosphere and any hold: with a service module on, the
  // craft flies engine first and the shield can't lead. A craft with a heat shield that keeps its stack
  // fails here, still above the air; one already in the air flies on as it is.
  local home is reentry_home_uid().
  local stack is reentry_stack(ship:parts, home).
  if (stack:parts:length > 0 or opt:separate:istype("Scalar")) and not claudia_on_ground() {
    local drop is reentry_separation(opt:separate, stack).
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
    set stack to reentry_stack(ship:parts, home).
    if ship:altitude > body_:atm:height {
      local refused is reentry_stack_refusal(stack:shielded, reentry_names(stack:parts), drop_error).
      if refused <> "" {
        return claudia_finish(ctx, false, refused, extra).
      }
    }
  }
  // The shield's decouplers, found on the craft that is left.
  local shield is reentry_shield_modules(opt:jettison_stage).
  local shield_gone is shield:length = 0.
  set extra:geo to reentry_geo().

  // Wait (on rails) for the atmosphere if the craft is still above it.
  local entry_in is 0.
  if ship:altitude > body_:atm:height and not claudia_on_ground() {
    set entry_in to reentry_entry_eta(ship:orbit:eccentricity, ship:orbit:semimajoraxis, body_:mu, body_:radius + body_:atm:height, ship:orbit:trueanomaly).
    if entry_in < 0 {
      return claudia_finish(ctx, false, "the orbit never reaches the atmosphere (periapsis " + round(ship:periapsis) + " m)", extra).
    }
  }
  if opt:max_duration:istype("String") {
    set opt:max_duration to entry_in + 1800.
  }
  local held is opt:hold = "retrograde".
  if held {
    lock steering to srfretrograde.
  }
  if entry_in > 0 {
    local waited is claudia_wait_until(ctx, time:seconds + entry_in).
    if waited <> "" {
      reentry_abort(opt).
      return claudia_finish(ctx, false, waited, extra).
    }
  }

  local has_drogues is reentry_has_drogues().
  local drogues_out is false.
  local mains_out is false.
  local last_speed is ship:velocity:surface:mag.
  local sample_ut is time:seconds.
  local sample_v is ship:velocity:orbit.
  local sample_speed is last_speed.
  local decel is 0.
  local hot is 0.
  local event_eta is -1.

  // One tick of the fall; true once the craft is on the ground. The wait loop (claudia_warp_to) calls it
  // each tick as its stop and physics-warps the fall under reentry_warp_cap.
  local fall_tick is {
    if claudia_on_ground() {
      return true.
    }
    local now is time:seconds.
    set last_speed to ship:velocity:surface:mag.
    local agl is reentry_agl().
    local pressure is body_:atm:altitudepressure(ship:altitude).

    // Heat and g, sampled a quarter second apart so frame noise doesn't read as g.
    if now - sample_ut >= 0.25 {
      set extra:max_g to max(extra:max_g, reentry_g_felt(sample_v, ship:velocity:orbit, now - sample_ut, ship:position - body_:position, body_:mu)).
      set hot to reentry_hottest().
      set extra:max_temp_pct to max(extra:max_temp_pct, hot).
      set decel to (sample_speed - last_speed) / (now - sample_ut).
      set sample_ut to now.
      set sample_v to ship:velocity:orbit.
      set sample_speed to last_speed.
    }

    if reentry_shield_due(shield_gone, last_speed, agl) {
      local count_before is ship:parts:length.
      for drop in shield {
        drop:module:doevent(drop:event).
      }
      set shield_gone to true.
      // The shield leaving is not a part lost: count what is left once it has gone.
      wait 0.5.
      set extra:shield_dropped to ship:parts:length < count_before.
      set ctx:parts to ship:parts:length.
    }
    local chute_plan is reentry_chute_stage(opt:chute_mode, has_drogues, drogues_out, mains_out, last_speed, agl, pressure).
    if chute_plan = "drogues" {
      reentry_open_chutes(true).
      set drogues_out to true.
      // Open parachutes steer the craft themselves.
      unlock steering.
      claudia_rcs_off("steering released").
    } else if chute_plan = "mains" {
      reentry_open_chutes(false).
      set mains_out to true.
      unlock steering.
      claudia_rcs_off("steering released").
    }

    // The next parachute or shield drop, by height (falling at the vertical speed) or by speed (slowing
    // at the sampled rate); the air-pressure test is not predicted.
    local descent is -ship:verticalspeed.
    set event_eta to reentry_eta_to(agl, reentry_warp_low_agl(), descent).
    if not shield_gone {
      set event_eta to reentry_eta_first(event_eta, reentry_eta_first(reentry_eta_to(agl, reentry_shield_last_agl(), descent), reentry_eta_to(last_speed, reentry_shield_speed(), decel))).
    }
    if opt:chute_mode = "safe" and not mains_out {
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
  local cap is { return reentry_warp_cap(hot, drogues_out, mains_out, reentry_agl(), event_eta). }.
  local fell is claudia_warp_to(ctx, time:seconds + opt:max_duration + 3600, 0, fall_tick, cap).
  if fell <> "" {
    reentry_abort(opt).
    set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
    set extra:max_g to round(extra:max_g, 2).
    set extra:geo to reentry_geo().
    return claudia_finish(ctx, false, fell, extra).
  }

  unlock steering.
  claudia_rcs_off("block end").
  set extra:touchdown_speed to round(last_speed, 1).
  set extra:max_g to round(extra:max_g, 2).
  set extra:max_temp_pct to round(max(extra:max_temp_pct, reentry_hottest()), 1).
  set extra:geo to reentry_geo().
  set extra:splashed to status = "SPLASHED".
  if not reentry_touchdown_ok(last_speed) {
    return claudia_finish(ctx, false, "touchdown at " + round(last_speed, 1) + " m/s is over " + reentry_touchdown_limit() + " m/s", extra).
  }
  return claudia_finish(ctx, true, "", extra).
}
