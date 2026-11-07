# Claudia Kerman

A Kerbal Space Program mod in which an AI mission controller plans and flies missions through kOS, turning a player's stated goal into a sequence of executed maneuvers.

## Language

**Claudia Kerman**:
The name of the mod as a whole.
_Avoid_: Kerman (alone), Kermin (the spelling before ADR 0015), the plugin

**Claudia**:
The AI mission controller the player talks to, who is also a Kerbal crew member (a standard female Kerbal) that can be assigned to missions.
_Avoid_: the bot, the chatbot, the AI, Claude

**Kerbin**:
The home planet in KSP. The mod name's "Kerman" is the stock Kerbal surname and never refers to the planet.
_Avoid_: Kerman (for the planet)

**Super Intelligence**:
Claudia's crew role, combining the abilities of a Pilot, an Engineer and a Scientist.
_Avoid_: trait (alone), class, profession

**Mission Goal**:
The player's plain-language statement of what a mission must achieve, e.g. "Land on the Mun, run 3 experiments, return to Kerbin." It changes only when the player gives Approval to a revised Mission Plan that names a new goal (an Approval Auto Mode gives counts); Claudia cannot change it on her own.
_Avoid_: objective, prompt, task

**Mission Settings**:
The player's choices for how Claudia meets a Mission Goal (e.g. daylight landing, time versus delta-v, physics warp on Coasts), set when the Mission starts and editable later; changing one makes Claudia revise the Mission Plan.
_Avoid_: mission parameters, options (which are Building Block settings)

**Craft Check**:
Claudia's assessment of whether a player-supplied craft can achieve a Mission Goal.
_Avoid_: validation, craft verification

**Blocker**:
A Craft Check finding that prevents the Mission from starting; it cannot be overridden.
_Avoid_: error, hard fail

**Warning**:
A Craft Check finding the player must acknowledge before the Mission starts. Once a Mission Plan exists the mod also reports a Warning that a Maneuver Sequence's budget is under the Delta-v Map; that one is shown, not acknowledged.
_Avoid_: soft fail, notice

**Game Snapshot**:
A plain copy of the game at one moment (the craft, its stages and parts, orbit, situation, resources, crew, science and link state) that the mod takes and gives to Claudia and the Craft Check, so neither reads the live game; Game Snapshots recorded from real play are also what the mod's tests run against.
_Avoid_: game state, telemetry (which Telemetry Checks measure while Running), dump

**Grounded**:
The craft is on the ground or the pad (PRELAUNCH, LANDED or SPLASHED): no flight yet, so its ascent or burn starts from the surface.
_Avoid_: landed (alone, which leaves out PRELAUNCH)

**Delta-v Map**:
The reference table of delta-v needed for each leg of travel between every body, computed from the game's body data, against which a Mission Plan is budgeted.
_Avoid_: dv chart, cheat sheet

**Maneuver Sequence**:
One self-contained stretch of flight that Claudia plans and kOS executes in a single go, e.g. launch to orbit, Mun transfer burn, landing.
_Avoid_: step, phase, maneuver (alone), burn

**Leg**:
In code, one entry of a Mission Plan's sequence list (a `PlannedSequence`), read from the Mission Goal's words, which are split into one leg per clause (at punctuation, "and" and "then"). Before a Mission Plan exists, each goal clause is a leg (`ChuteGroups.GoalLegs`, #641). `SequenceWords.LegBody` gives the body where a leg's surface work happens, and `NextBody` the body the next leg starts from (Kerbin after a return home); for "Land on the Mun and return to Kerbin" these differ. Open: whether the glossary should name this unit Maneuver Sequence or keep leg; until that is decided, the glossary's Maneuver Sequence is what Claudia plans and executes.
_Avoid_: stage (which is a rocket stage), step

**Coast**:
A stretch of flight with no engine or RCS thrust, in an atmosphere or not (e.g. launch's climb out of the air after cutoff, a fall under parachutes, the fall before a landing burn); the only stretch on which a Mission Script may use physics warp.
_Avoid_: drift, glide, cruise

**RCS Use**:
A stretch on which a Mission Script has RCS switched on, always for a stated reason: a big turn (even with time to spare), turning when the reaction wheels alone can't make the turn in time, settling an attitude error the wheels can't, or translating. A turn on a craft with wheels is a burst turn (#692): one RCS burst spins it up, it coasts with RCS off, one burst stops it, and the wheels settle the last degrees, with at most two more burst pairs when the wheels can't close what's left in time. Outside RCS Use, RCS is off, whatever the player set before launch, so a craft holding attitude on a Coast doesn't fire thrusters. Two exceptions (#506): `land`'s braking to touchdown keeps RCS off unless the craft has no wheel torque, and on the ground (LANDED, SPLASHED or PRELAUNCH) RCS never comes on, in any block.
_Avoid_: RCS mode, RCS assist

**Aligned**:
The craft points at its steering target (a maneuver node's burn, prograde, retrograde, a launch heading) within a set tolerance and has stopped turning, or has stayed within it for a few seconds while the target itself turns. Being Aligned both lets a burn start and ends RCS Use for a turn; Claudia can tighten or loosen the tolerance per block.
_Avoid_: on target, settled, pointed

**Burn Start**:
The moment a maneuver node's burn lights its engines: ahead of the node by about the time it takes to burn half the node's delta-v, worked out from every stage the burn will use. Leads before a burn (when warp ends, when the craft must be Aligned) count back from the Burn Start, not from the node.
_Avoid_: node time, burn time (the burn's length)

**Course Correction**:
A burn between departure and arrival that trims the predicted arrival at a body or vessel to the target periapsis or closest approach, and optionally to the target arrival inclination or, with the periapsis held, a landing point along the track (#793), and across it when the delta-v left less the **reserve** covers that (#790).
_Avoid_: mid-course correction, MCC

**Broken-plane transfer**:
A transfer to a body out of the craft's plane whose departure stays in that plane, with a plane change partway out, where the arc is slow, that makes the encounter; its delta-v is reported as `plane_dv`, apart from the Course Corrections' `correction_dv`. `transfer_to` flies it in place of the halfway Course Correction (#619).
_Avoid_: dog-leg

**Rendezvous**:
A transfer that meets its target where the target is at arrival: a body, or a vessel's orbit with the vessel on it. A transfer to a body is always one. This is the transfer mode (`mode` "rendezvous"); the `rendezvous` Building Block is the whole approach to the Target Vessel, of which such a transfer is one step.
_Avoid_: encounter (the patched-conic event of entering a sphere of influence), meet

**`rendezvous` (the Building Block)**:
The Building Block that flies the craft from orbit to the Target Vessel's physics range (loaded, about 2.2 km) at under 2 m/s relative, where `dock` takes over: a plane match, a phasing orbit, a Rendezvous transfer, a Course Correction and matching velocities, each step solved when it starts (#455).
_Avoid_: rendezvous (alone, when the transfer mode could be meant), approach, intercept

**Docking**:
Joining the craft to the Target Vessel port to port, flown by the `dock` Building Block from `rendezvous`'s hand-off: the craft lines its port up on the target port's axis and closes along it on RCS translation. While it flies, the mod runs its bundled `dock_hold` on the Target Vessel's kOS processor (when it has electric charge and a link by the Child Vessel link test) to turn the target's port to the craft, and stops it when `dock` ends; a target without one is met as it is, and refused if it turns faster than 1°/s (#456).
_Avoid_: berthing, mating, hard dock (KSP's ports dock on contact)

**Intercept**:
A transfer that reaches a target orbit anywhere on it, wherever the target is, such as a parking orbit; free to choose the arrival point, it is never dearer than a Rendezvous with the same orbit.
_Avoid_: flyby, orbit change (one burn, not a transfer)

**Capture**:
The arrival burn of a transfer: circular at the target periapsis round a body, or matching the target orbit's velocity. A transfer solved with capture is the cheapest departure and capture together; without, the cheapest departure alone.
_Avoid_: insertion, orbit insertion

**Landing target default**:
What a `return` or `deorbit` at Kerbin aims for when Claudia gives no `target`: KSC, set by the mod (`HomeLanding`, #790), with no prompt text, as it sets `transfer_to`'s plane. A target she gives wins, as does a Mission Goal that names another place at home: a biome, a Point of Interest, water, a splashdown, or a latitude and longitude. A leg ahead that names a Point of Interest or a latitude and longitude there counts too, and so does a site picked there. A `return` with a periapsis over the air, a `window` or a `target_inc` isn't aimed: the first is no landing, and the others are timed or shaped for something else. From another planet the same default chooses the way in at `plan_transfer`: capture then `deorbit` when the delta-v left less the **reserve** covers both, else straight in.

**Reserve**:
What aiming a landing never spends: the larger of 50 m/s and 10% of the delta-v left, plus what a craft with no parachutes needs for its powered landing. One rule, `DeorbitBurn.AutoMaxDv`, for `deorbit`'s auto `max_dv`, `return`'s aimed correction and the choice of capture on a trip home.

**Arrival plane**:
The plane and direction of travel a transfer should arrive in, set on the approach, inside the target's sphere of influence, where any plane containing the approach direction is nearly free (#788). `transfer_to`'s `plane` picks it: Claudia's value, a Target Vessel orbiting the arrival body, the next leg's landing site, the next leg's moon, or none. A pole or a biome, named by a leg or the Mission Goal, is a landing site: 90 degrees at a pole or polar biome, another biome's latitude where the game's map gives one (#806). A body or vessel gives its whole plane in its own direction; a number or a site gives an inclination only, the crossing point free. Core records it for `capture`. The Delta-v Map's `polar:` row prices a polar arrival flown this way, an upper bound fitted to the flight's solves; it adds no allowance for any other plane (ADR 0016).
_Avoid_: capture plane, target plane

**`capture`**:
The Building Block that flies the Capture from inside the arrival body's sphere of influence: a circular orbit at `alt` in the recorded arrival plane. A tilt the approach left over it burns the cheaper way, folded into the burn at periapsis or at a high ellipse's apoapsis; over `max_plane_dv` it captures in the plane it has and reports the leftover without failing. With a Target Vessel orbiting there it circularizes at the vessel's periapsis altitude in its plane, so `rendezvous` only has to phase.
_Avoid_: insertion burn

**Transfer Window**:
A departure time and arrival time for a transfer to another planet, or to a moon of the craft's planet (#647), with what it costs: the ejection from the parking orbit and, with Capture, the capture at the target periapsis. The window search gives two, Soonest and Optimal. Between bodies with the same parent (the body the craft departs from and the target, or the target's parent: Kerbin to Duna, Kerbin to Ike through Duna) the player picks one (#686): on the Craft Check card before Start, again for each revised Mission Plan, and (#782) at the first Sequence Plan of a departure no pick covers, such as a return from Duna flown straight on: a landed craft's ascent, otherwise the departure itself. Each time it is for the craft as it is then, before Approval. The pick is Soonest or Optimal, not a date, and it is never Auto Mode's to make. Within one sphere of influence (Kerbin to the Mun, a moon back to its planet, the Mun or Minmus home) the Mission Setting Time versus delta-v picks one: Fastest takes Soonest, Most efficient takes Optimal, Balanced takes Soonest unless Optimal saves at least 10%.
_Avoid_: porkchop (the plot, which the mod never draws), launch window (the time to lift off into a target's plane)

**Soonest**:
The earliest Transfer Window whose total delta-v is within the Craft Check's margin of Optimal's.
_Avoid_: next window, fastest (a Mission Setting)

**Optimal**:
The cheapest Transfer Window within one synodic period of the two planets, or one orbit of the moon. A moon's Optimal is its Soonest unless it saves at least 5 m/s: the Mun, in the parking orbit's plane, costs the same at any departure; Minmus is cheapest near a node. From the ground, a moon out of the site's plane is costed as a launch into its plane at the next launch window (#690), so Soonest is Optimal and the wait is under a body day; from a site past the plane, as a launch due east with the residual fixed on the way, and the moon's horizon is bounded by Time versus delta-v (Fastest one parking orbit, Balanced a quarter of the moon's orbit, Most efficient one orbit, #727). Each row names that launch (its target_inc and time), its ascent loss and plane change, and the case it took.
_Avoid_: best window, most efficient (a Mission Setting)

**Direct Route**:
From a moon's orbit to another planet of its star, in one Transfer Window (#760): a fall from the moon to a low periapsis at its planet, at the time that lines the periapsis up with the ejection, and the ejection at that periapsis. It saves about twice the capture at the planet over the Two-Step Route. Flown by return, then transfer_to, each with its window from plan_transfer's row. Time versus delta-v picks between the two routes as it does between windows: Soonest is the route arriving first if it is within the Craft Check's margin of the cheaper, else the cheaper.
_Avoid_: slingshot, gravity assist (the planet's gravity does no turning for free here), Oberth route

**Two-Step Route**:
From a moon's orbit to another planet of its star: a return to the planet's low orbit, a capture there, and the ejection from that orbit at the Transfer Window.

**Daylight**:
At a landing site, the sun is more than 20° above the horizon there. A daylight landing touches down only in Daylight; a sun lower than that, though up, is not Daylight.
_Avoid_: lit, sunlit, daytime

**Boost stage**:
A stage that lights while the craft is on a suborbital trajectory (periapsis below the atmosphere top, or below the surface where there is no air), in or out of the atmosphere; a stage that lights suborbital stays a Boost stage even if its burn reaches orbit. It may have a thrust-to-weight ratio below 1 when the climb before it was steep enough.
_Avoid_: booster, lower stage, first stage

**Orbital stage**:
A stage that lights while the craft is already in orbit: transfers, plane changes and the burns that follow.
_Avoid_: upper stage, insertion stage, sustainer

**Underhang**:
The parts of a craft that reach below its landing legs' feet when it stands upright, such as a spent transfer stage under a lander. A landing drops them before touchdown, so the craft comes down on its legs.
_Avoid_: parts below the legs, lower stage

**Descent Shape**:
The one number that sets how a landing's braking trades height for speed: a small shape holds height until the craft is nearly stopped and then drops steeply, and a large one sinks early. `land` picks it from the terrain under its approach (ADR 0011).
_Avoid_: glide slope, descent profile (as a name for the number), approach angle

**Chute Group**:
All the chutes in one stage, by the part's own stage number; a chute with staging disabled (stage -1) is the last group. An Entry that isn't the Last Entry opens the group of the highest stage aboard, part by part and never by staging; a Last Entry opens every chute aboard. Read fresh, with no memory of use, so a repacked chute counts again and a restaged one is in its new group (ADR 0019). The mod judges each Entry's group by the touchdown predictor; one it can't bring under reentry's touchdown limit is a powered finish.
_Avoid_: Chute Allocation, chute budget, parachute plan

**Entry**:
A remaining Maneuver Sequence that lands on a body with air, reenters or returns home. A return and its reentry, or a deorbit and its landing, with no ascent between, are one Entry. Manual legs, airless bodies and aerobraking aren't counted.
_Avoid_: reentry (the block), atmospheric pass

**Last Entry**:
An Entry with no later sequence in the current Mission Plan that has delta-v. Only it drops the stack below the pod's own heat shield.
_Avoid_: final reentry

**Entry Shield**:
The heat shield an Entry uses: counted from the pod outward, by position and never by stage number, shield number n, where n is the Entries left, this one included, plus one if a sequence after the final Entry has delta-v. Everything below it drops before the air, and it jettisons once the worst heating is over. With no shield n the Entry has none, and nothing drops (ADR 0018).
_Avoid_: active shield, current shield

**Ship Systems**:
The parts the mod moves at flight transitions on the craft Claudia flies: solar panels, deployable antennas and deployable radiators (extended and retracted) and lights (on and off). Its own rule, with Building Blocks only cueing the moments they alone know; each move goes to the Mission's events and trace, not the prompt, and what the player toggles between transitions stands until the next one. Not cargo bays, drills, ISRU or science parts.
_Avoid_: deployables, auto-deploy

**Encounter Watch**:
The mod's check of an encounter during `transfer_to`'s long coasts (and, by the same rule, the check when the player is back from a Coast Checkpoint): at intervals of game time, tighter near the coast's two ends, it reads the craft's orbit from KSP with no physics and asks whether the encounter the coast was planned on still holds. Only a lost encounter, a re-patch onto a body the coast doesn't go round, or a periapsis drifted past its limit ends the wait early, and the block then plans the restore. It never raises warp.
_Avoid_: encounter guard, warp monitor

**Coast Checkpoint**:
A place in a long coast of `transfer_to` or `return` where the block ends Nominal so the player can fly something else, without any setting or reply. It falls before each of the block's correction points more than 6 h away, and only when the encounter is confirmed, the predicted periapsis is out of the atmosphere and off the surface (a trip home that ends in reentry, whose periapsis is in the air on purpose, passes while the correction comes before the home body's sphere of influence entry, and never after it), and no sphere of influence change comes first (the target's own entry aside, which the finer correction sits just inside); otherwise the block corrects or keeps flying. The mod writes a Nominal Debrief and a resumed Sequence Plan with the same `plan_step`, with no request, entering the flight at the coast with no ejection; its Due Time is the correction point less 30 min of game time, never less than the turn time plus 60 s (the resumed script warps the rest itself). A far switch, or leaving Flight for the Space Center or the Tracking Station (not the main menu, quit or revert), while the block only warps on such a coast makes one too. When the player is back on the craft the mod checks the trajectory on the Game Snapshot: kept and within tolerance, the resume stands; drifted (over 10% of the target's sphere of influence, or an unsafe periapsis) or lost, Claudia plans the next sequence from the check's figures. The Mission Plan never names one.
_Avoid_: coast pause, save point, parking

**Mission Plan**:
The ordered list of Maneuver Sequences Claudia intends to fly to reach a Mission Goal, each with its delta-v budget and Due Time or rough timing; approved once to start the Mission and revised as Debriefs require.
_Avoid_: flight plan, roadmap

**Due Time**:
The earliest time a Maneuver Sequence may start; the player may fly elsewhere until then, and the Mission calls them back at it. Claudia sets one in a Sequence Plan when the wait before it needs the player (e.g. "come back when you've docked"). The mod sets one on the first Maneuver Sequence when it would wait on the pad more than 6 h for a Transfer Window to another planet or a moon: the window less the launch lead. A goal at a moon of another planet waits for that planet's window. A craft landed on another body waits there too: when its next legs are an ascent and a departure for another planet, the ascent carries the Due Time, the departure less the ascent lead. A shorter wait for a window, and any wait for daylight or a hold, is not a Due Time: the Mission Script warps through it (ADR 0004 and its amendment). A Coast Checkpoint's Due Time is the one soft kind: with the player still in the craft the resumed sequence need not wait for it, and its alarm matters only if they have left.
_Avoid_: window (which is a transfer window), scheduled time

**Sequence Plan**:
Claudia's proposal for the next Maneuver Sequence: its intent, Building Blocks and settings, Due Time, target outcome with tolerances, expected delta-v and duration, abort conditions, Telemetry Checks and risks.
_Avoid_: maneuver plan, proposal, step plan

**Telemetry Check**:
A quantity the mod can measure during a Maneuver Sequence (e.g. delta-v used, apoapsis, electric charge) with a threshold, set in the Sequence Plan or by default; while Running, the mod checks it, and crossing it raises an Alert. Orbit checks (apoapsis, periapsis, inclination) arm only once a reading is on the safe side, and disarm while the craft is landed, splashed, on the pad or in atmosphere; propulsion checks (delta-v left, delta-v used, fuel margin) disarm once no engine is aboard. A standing crossing rules out a Nominal Debrief; a recovered one doesn't.
_Avoid_: watch limit, alarm, tripwire, suspended (a disarmed check), held (a check waiting out a burn)

**Alert**:
The mod's message to Claudia that a Telemetry Check was crossed while Running; she answers by letting the sequence carry on or recommending the player Abort. Also the mod's message that the player came back after a Due Time's Transfer Window and the next window doesn't fit the Craft Check budget; she answers with a revised Mission Plan or a reduced goal.
_Avoid_: alarm (the stock Alarm Clock's word), warning (a Craft Check finding)

**Debrief**:
Claudia's chat-based evaluation after a Maneuver Sequence: what happened versus the plan, progress toward the Mission Goal, and the proposed next Maneuver Sequence with its reasoning.
_Avoid_: report (what a script does with its Script Result), evaluation, review

**Margin check**:
The mod's own check, with no Claude, inside every Debrief: the craft as it is now (delta-v, thrust over weight where it must land or lift off, electric charge, monopropellant and the Craft Check findings the mod decides itself) against the Maneuver Sequences still to fly in the Mission Plan, ending enough, tight or short. Its line and findings go into the Debrief and onto its card.
_Avoid_: margin recheck (which is Claudia's)

**Margin recheck**:
The Craft Check run again inside a Debrief whose Margin check is tight or short (or every Debrief, if the player config asks), on the craft as it is now against the Maneuver Sequences still to fly in the Mission Plan; charged to the Mission like any other request, and skipped when the Spending Cap has no room for it beside the Debrief.
_Avoid_: second Craft Check, re-check

**Second look**:
The mod asking Claudia again, thinking harder, for a Debrief, Craft Check, Margin recheck or Sequence Plan when her first reply says she is unsure, and why; the second reply replaces the first, which is never kept, and is skipped when the Spending Cap has no room for it.
_Avoid_: retry (which is the player's), escalation, re-ask

**Nominal**:
A Debrief outcome: the Maneuver Sequence ended within its Sequence Plan's tolerances.

**Off-Nominal**:
A Debrief outcome: the Maneuver Sequence ended outside tolerance, but the Mission Goal is still reachable.

**Failed**:
A Debrief outcome: the Mission Goal can no longer be reached as stated.
_Avoid_: Lost (which is a Mission outcome, not a Debrief outcome)

**Approval**:
The player's go-ahead, given in chat, for Claudia to execute the proposed Maneuver Sequence; under Auto Mode the player has given it in advance.
_Avoid_: confirmation, sign-off

**Auto Mode**:
A per-Mission switch on the Mission window, which the player may flip at any point in the Mission, under which the player's Approval is given in advance: every plan Claudia proposes after the first Mission Plan (Sequence Plans, revised Mission Plans, a reduced or changed Mission Goal) is approved the moment it arrives and an unsafe script is re-planned once, each only while the Spending Cap has room for the worst case of the Debrief and next plan that follow; an Abort she recommends is always taken. It never starts a Mission, acknowledges a Warning, raises the Spending Cap, resumes a Paused Mission, picks a Transfer Window, or stands in for the player at a Manual stretch or Due Time, but for a Coast Checkpoint's Due Time with the player still in the craft and at no other kind; there, and when the Spending Cap has no room, it stops and calls the player. New Missions take the player's default, off unless set otherwise.
_Avoid_: autopilot, autonomous mode (Blackout's word to avoid), auto-approve (as a noun)

**Mission**:
One Mission Goal pursued by Claudia with one craft and any Child Vessels, from its Craft Check until it succeeds or is abandoned.
_Avoid_: flight, run, job

**Child Vessel**:
A vessel that separated from a Mission's craft during the Mission and carries a kOS processor, which Claudia may fly with Maneuver Sequences of its own while she has a link to it. The craft stays the vessel Claudia is aboard (or, for a remote Mission, the one with the original processor).
_Avoid_: probe, drone, secondary craft

**Target Vessel**:
The one other vessel a Mission rendezvouses or docks with, or lands beside when it is landed (`land`'s target `"vessel"`, #662), only for a Mission Goal that asks for rendezvous or docking or names a vessel: the player's pick in the start form, else the vessel the goal names, else KSP's target. The start form shows it, or why there is none, before Start; changing it makes Start check the craft again. Between sequences the Mission window's Choose and Clear change it, and the pending plan becomes Re-plan for a revised Mission Plan (#661). A Child Vessel can be one. The Game Snapshot carries it as one compact entry only when the Mission has one (ADR 0013). It is flown only by the bundled `dock_hold`, never by Claudia's scripts.
_Avoid_: target (alone, which is also a transfer's body or orbit), station, other vessel

**Point of Interest**:
A stock anomaly on a body's surface (a monolith, the Mun's rock arches, the Duna Face...): a place to land beside, never on (#713). The game gives its position (its surface objects); Core's table gives its friendly name, a note and whether it is landable (the buried Monolith TMA-7 is not), and leaves out the KSC and launch sites. `find_poi` lists a body's; `find_landing_site`'s `poi` and `land`'s target `lexicon("poi", name)` land in the ring 150 m to 1.5 km round it, the point itself kept clear. It is not a Target Vessel (a vessel, not scenery) and not a biome (an area, not a point).
_Avoid_: anomaly (alone), easter egg, landmark, POI (in Claudia's text)

**Claudia Flag**:
The flag the mod ships (`ClaudiaKerman/Flags/ClaudiaFlag`). A Mission's craft and Child Vessels fly it, and it is the agency flag while a Mission is under way (in play, Paused or not); the player's own agency flag comes back when none is.
_Avoid_: Mission flag, mod flag

**Spending Cap**:
The most a Mission may spend on Claudia's API calls, set by the player when the Mission starts and raisable later.
_Avoid_: budget (which means delta-v), limit, quota

**Running**:
The state of a Mission while a Maneuver Sequence's script executes; it ends at the Debrief. A Mission can be Running during a Blackout; taking Manual Control ends the run.
_Avoid_: executing, active, in progress

**Paused**:
The state of a Mission that has stopped making progress, e.g. at its Spending Cap or on an API error, and can be resumed by the player.
_Avoid_: suspended, on hold, abandoned

**Blackout**:
A period when Claudia has no link to Kerbin and carries on without the player's Approval: she flies the approved Mission Plan, and may revise it within the Mission Goal and Spending Cap; her messages and revisions wait for the player until the link returns. A Mission in Blackout is not Paused.
_Avoid_: autonomous mode, offline, comms loss

**Manual Control**:
The state of a Mission while the player flies the craft and Claudia only advises; it ends when the player hands the craft back. A Mission in Manual Control is not Paused.
_Avoid_: takeover, override, manual mode

**Interrupted**:
Of a Maneuver Sequence: it was Running when the scene changed, a save was loaded, the game quit or crashed, its kOS processor was lost, or its craft unloaded (the player switched vessels, or went out of range of it), so its script is gone; a far switch or a leave for the Space Center or Tracking Station that makes a Coast Checkpoint is not an interruption. It is never restarted; Claudia gives it an Interrupted Debrief when the player is back in Flight, judging from the craft as it now is. Not a Debrief outcome: the Debrief still ends Nominal, Off-Nominal or Failed.
_Avoid_: restarted, resumed (an interrupted sequence is Debriefed, never run again)

**Interrupted Debrief**:
The Debrief Claudia gives for a Maneuver Sequence that was Interrupted: the Debrief is owed from the interruption and given when the player is back in Flight. It is owed for each cause the Interrupted entry gives.
_Avoid_: interrupted-sequence Debrief, restart Debrief

**Hand back**:
The player's act, with an optional message about what they did, of giving the craft back to Claudia and ending Manual Control. Claudia gives the manual stretch a short Debrief (judged against its target if it was planned, a plain account if not), then revises the Mission Plan for Approval.
_Avoid_: give back, return control, release

**Manual stretch**:
A Maneuver Sequence in the Mission Plan that the player flies by hand (e.g. EVA science; docking is the `dock` block's), so it has no script. Approving it starts Manual Control; the player's Hand back ends it.
_Avoid_: manual sequence, manual mode (Manual Control is the state)

**Lost**:
The outcome of a Mission that ends because Claudia was killed aboard the craft.
_Avoid_: failed, crashed, KIA (for the Mission)

**Capcom strip**:
The slim bar in Flight (under the altimeter unless the player drags it elsewhere) through which the player follows a Mission without leaving the flight view: Claudia's portrait and latest line, where the Mission is in its Mission Plan, the Spending Cap, what is locked, and a tab that expands the Mission window docked to it (amber when Claudia needs the player).
_Avoid_: HUD, status bar, ticker

**Missions list**:
The read-only list, in the Space Center and Tracking Station, of every Mission in the save: the active one, Paused ones, then finished ones (succeeded, ended or Lost) kept as history with their final Debrief and cost. A save holds several Missions with one active; starting or resuming another Pauses the active one at its next sequence boundary (never mid-sequence). The list switches to a Mission's vessel, resumes a Paused Mission, and deletes a finished one with its folder.
_Avoid_: Mission history (the finished part of the list), Mission manager

### Scripts

**Core Library**:
The fixed set of Building Blocks that ships with Claudia Kerman and is the same for every Mission.
_Avoid_: primitives, stdlib, script bundle

**Building Block**:
One reusable flight routine in the Core Library (e.g. launch, transfer_to, land) that Claudia sets up for a Maneuver Sequence.
_Avoid_: primitive, routine, module

**Planning Tool**:
A read-only calculation the mod runs for Claudia while she plans (e.g. predicting the orbit after a burn, finding a landing site), whose answer shapes the plan; it never changes the game, and only Building Blocks fly the craft.
_Avoid_: function, tool call, API, flight computer

**Reply Tool**:
A tool through which Claudia hands in part of her reply for the mod to check (e.g. `propose_mission_plan`, which carries a Mission Plan), as opposed to a Planning Tool, which only reads (ADR 0006); the mod answers ok or the problems, she may call it again in the same turn, and her reply takes the last part it accepted (ADR 0012).
_Avoid_: submit tool, output tool, Planning Tool (for this kind)

**Mission Script**:
A script Claudia writes for one Mission to carry out a Maneuver Sequence, kept with that Mission.
_Avoid_: generated script, glue script

**Custom Script**:
A Mission Script with flight logic of its own beyond running Building Blocks in order, branching on their Script Results, adding the maneuver node an exec_node call flies, and reporting (e.g. a hand-written landing-site search before a land call; never a bare stage., which the Script Check refuses), used where no Building Block fits; a script that calls no Building Block at all is one too.
_Avoid_: raw script, freeform script

**Script Result**:
The values a Mission Script hands back once, when its Maneuver Sequence ends (e.g. apoapsis, delta-v used, situation), which the mod passes to Claudia as data for her Debrief; a script that ends without one has failed. The script is said to report it.
_Avoid_: report (as a noun), outcome (which is a Debrief's), return value

**Blocks trail**:
The mod's compact list of every Building Block result in a Mission Script's run, one entry per block (e.g. transfer_to's encounter_ut, science's kept), whatever Script Result the script reports; it goes in the Debrief turn text ("Blocks the script ran, oldest first"), into plans.jsonl as debrief.blocks, and into the save as blockTrail, so a hand-made lexicon can't hide what the blocks did (#609).
_Avoid_: block log, block history, results list

**Script Check**:
The mod's check of a compiled script against a forbidden list (e.g. save and load, file operations, changing other vessels) before Approval, and of each Building Block call whose inline options lexicon misses a required option or names one the block doesn't take (#606); a script that fails is rewritten or never offered.
_Avoid_: sandbox, validation

**Couldn't produce a safe script**:
The state of a Mission when a Sequence Plan's script failed the Script Check, was rewritten once automatically (billed under the Spending Cap), and failed again. The plan is dropped, nothing can be approved, and the player needs to act: **Retry** asks Claudia for the script again, **Retry with a note** does so with the player's guidance, and **Re-plan** has her plan the Maneuver Sequence afresh.
_Avoid_: script error, unsafe script (as a name for the state), rejected script
