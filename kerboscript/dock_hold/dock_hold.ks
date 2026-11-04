// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson
//
// dock_hold (#456): the mod's own bundled program, which it runs on the Target Vessel's kOS processor while dock flies
// (ADR 0013). Not part of the Core Library: never bundled with a Mission Script, never in Claudia's docs, and Claudia's
// scripts never run on the target. The mod puts the two ports' kOS uids in (DockHold.Program) and starts it under ADR 0003's
// runtime gates like any run it starts. It controls from the target port, points it at the craft's port (reading the
// craft only) and holds that direction, so damps the target's rotation. It re-aims only once the craft is over 15 degrees
// off the held direction and the ports over 10 m apart, then holds again (#706): tracking the craft's port each tick
// coupled the two vessels' turns into a limit cycle, and close in a few cm off the axis is many degrees. It reports when its
// port has settled on the aim, and again when a re-aim unsettles it (#711). It ends once its port leaves Ready (capture or docking), and the mod
// stops it when dock ends, however dock ends. It reports success true with reason "docked" when its port was caught.

@lazyglobal off.

local dh_port_uid is "%PORT%".
local dh_toward_uid is "%TOWARD%".
local dh_port is 0.
local dh_found is false.
for dh_p in ship:dockingports {
  if dh_p:uid = dh_port_uid {
    set dh_port to dh_p.
    set dh_found to true.
  }
}
local dh_toward is 0.
local dh_seen is false.
if dh_found {
  local dh_all is list().
  list targets in dh_all.
  for dh_v in dh_all {
    if dh_v:loaded {
      for dh_q in dh_v:dockingports {
        if dh_q:uid = dh_toward_uid {
          set dh_toward to dh_q.
          set dh_seen to true.
        }
      }
    }
  }
}
local dh_reason is "".
if not dh_found {
  set dh_reason to "its port is gone".
} else if not dh_seen {
  set dh_reason to "the craft's port is not loaded".
} else {
  dh_port:controlfrom().
  local dh_aim is dh_toward:nodeposition - dh_port:nodeposition.
  local dh_up is ship:facing:topvector.
  lock steering to lookdirup(dh_aim, dh_up).
  // Settled (#711): the port within 2 degrees of the aim and turning under 0.1 deg/s for 2 s. The mod passes it to dock,
  // whose craft holds still until then; a re-aim unsettles it. Near (#714): 5 degrees and 0.5 deg/s, dock
  // closes on it only after its settle_timeout. A level holds until a re-aim.
  local dh_level is "".
  local dh_since is time:seconds.
  local dh_near is dh_since.
  until dh_port:state <> "Ready" {
    local dh_now is dh_toward:nodeposition - dh_port:nodeposition.
    if vang(dh_now, dh_aim) > 15 and dh_now:mag > 10 {
      set dh_aim to dh_now.
      set dh_up to ship:facing:topvector.
      set dh_since to time:seconds.
      set dh_near to dh_since.
      if dh_level <> "" {
        set dh_level to "".
        addons:claudia:dockholdsettled("").
      }
    }
    local dh_off is vang(dh_port:portfacing:forevector, dh_aim).
    local dh_rate is ship:angularvel:mag * constant:radtodeg.
    if dh_off > 2 or dh_rate > 0.1 {
      set dh_since to time:seconds.
    }
    if dh_off > 5 or dh_rate > 0.5 {
      set dh_near to time:seconds.
    }
    if dh_level <> "settled" and time:seconds >= dh_since + 2 {
      set dh_level to "settled".
      addons:claudia:dockholdsettled(dh_level).
    } else if dh_level = "" and time:seconds >= dh_near + 2 {
      set dh_level to "near".
      addons:claudia:dockholdsettled(dh_level).
    }
    wait 0.
  }
  unlock steering.
  set dh_reason to "port " + dh_port:state.
  if dh_port:state:startswith("Acquire") or dh_port:state:startswith("Docked") or dh_port:state = "PreAttached" {
    set dh_reason to "docked".
  }
}
addons:claudia:report(lexicon("success", dh_reason = "docked", "reason", dh_reason)).
