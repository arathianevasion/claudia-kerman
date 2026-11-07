# The player decides which chutes open, by staging

An Entry that isn't the Last Entry opens every stowed chute in the highest-numbered stage aboard: that stage's Chute Group, and no other. A Last Entry opens every stowed chute aboard. This replaces #626's Chute Allocation, which divided the chutes among the Entries by need and kept the pod's for the return. Flight m20261106-203509-34a6 lost its capsule to that rule at Duna: `reentry` read its set once, the stack drop took all four chutes in it, and the pod's chute stayed shut because it was saved for Kerbin. The player chose this rule on 2026-11-06, because the player can see the plan in the VAB, and it can't go stale.

| Chutes | Entries | Result |
|---|---|---|
| Stage 2 (four), stage 0 (one) | Duna, then Kerbin | Duna: stage 2. Kerbin (Last Entry): stage 0 |
| The same, with stage 2 repacked at Duna | Duna, then Kerbin | Kerbin is the Last Entry: every stowed chute aboard opens |
| Stage 2, stage 0 | Kerbin return only | Last Entry: all aboard |
| Stage 0 only | Duna, then Kerbin | Craft Check Blocker; with a planned Engineer repack, a Warning |
| Stage 2 on tanks dropped before Duna | Duna, then Kerbin | Craft Check Warning: stage 0 would open at Duna, leaving Kerbin none |

- **A chute's stage** is the part's own stage number as KSP reports it (`inverseStage`). A chute with staging disabled (stage -1) is the last group: it comes after every numbered stage, so a stage 0 chute and a stage -1 chute are two groups.
- **The stage is never fired.** Chutes open part by part, with today's speed and pressure rules (`chute_mode`), drogues before mains. The rocket can be on any stage. Drogues in one stage and mains in another are simply two groups.
- **No memory.** State and stage are read when the block chooses, after any drop, and again before each opening. A repacked chute reads as stowed. A restaged chute is in its new group. An open or cut chute doesn't count. A chute that left with a drop isn't there. If the whole group has gone before anything has opened, the block asks again, an abort's opening included, and the rule moves on to the next group.
- **Skip-outs are the same Entry.** A second pass in the same `reentry` uses the chutes it has and asks for no further group.
- **After landing,** on an Entry that isn't the Last Entry, `reentry` cuts any chutes still open, so they can't drag on the ascent. The Mk16's config has `autoCutSpeed = 0.5`, so stock looks to cut them once settled. The in-game checklist (`docs/in-game/entry-shield.md`) says how to confirm that. If stock has already cut a chute, its event isn't there and the cut does nothing.
- **Option.** `chutes` takes `"auto"` and `"none"`. `"all"` and tag lists are gone: Claudia can't override the player's staging.
- **In Core, opening in Kerboscript (ADR 0014).** `ChuteGroups.Choose` is the rule, behind `ADDONS:CLAUDIA:CHUTEGROUP`, and `reentry_chute_set` is deleted. Opening and cutting stay in `reentry`. ADR 0014 asks for a parity test when logic moves; this ticket changes the behaviour on purpose, so there is none to pin. The Last Entry is shield number 1 (ADR 0018), and the Entries come from its reader, so the shield and the chutes can't disagree. An answer in error opens every chute aboard, as a chute left shut is the worse mistake.

**The checks walk the Entries in order,** each losing what its own drop takes (`EntryStack`) and then taking its group, with the same predictor judging each touchdown. The Margin Check and the Craft Check give:

- **Blocker:** a later Entry with no group (`chutes_short_for_landing`). With an Engineer repack planned (`RepackPlan`), a Warning that says it relies on one (`chutes_thin_for_landing`).
- **Blocker:** a group in a stage at or above an unlit engine the plan still needs (`chute_group_above_engine`). `claudia_stage_unsafe` refuses to fire a stage holding chutes, so the craft ends in "out of fuel". The refusal stays. An engine already lit, an engine the plan doesn't reach, a chute at stage -1 and a stage already fired aren't flagged.
- **Warning:** a group likely to leave before its Entry (`chute_group_leaves_early`): the parts holding it drop in a stage the delta-v through that Entry burns through first. The walk still gives the Entry that group, as a likely drop is not a certain one.
- **Warning:** a group that lands too fast (`chute_group_too_fast`). A powered finish short of delta-v stays the Blocker it was, and one under 25% spare stays the thin Warning, as #629 has them.

**Kept:** the touchdown predictor. What a group too weak to land does is #824's with an engine (ADR 0020) and #827's without; `Choose` is the seam they extend.

**Removed:** the by-need split, the pod's reservation for the return, and the plan fingerprint that reused a held allocation (#645): the checks now work the groups out afresh each time.

A second `reentry` block after a failed one on the same Entry chooses afresh, and open chutes don't count, so it takes the next group. We left that as it is, since a block that failed mid-air is already an abort.

We rejected a chute's module or need deciding, as the plan then lives where the player can't see it, and a memory of which chutes were spent, as a repack or a restage would make it wrong.

Decided on #826 (2026-11-06).
