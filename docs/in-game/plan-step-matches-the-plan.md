# A plan_step naming another sequence gives way to the one the plan flies (#977)

1. On the pad, start a Mission "Fly to Duna, land, send a crew report and come home" with a craft that can do the round trip. Approve the Mission Plan and fly it on to the landing.
2. Once landed on Duna, watch each Sequence Plan's card and the plan bar. Expect the plan bar's current step to be the sequence the plan's name says (the crew report, then the ascent), whatever `plan_step` Claudia sent.
3. If Claudia sent a `plan_step` of another sequence, expect a `plan_step_resolved` line in the Mission's `events.jsonl` ("plan_step N is ...; this plan flies step M, ..."), and the same line at the start of the next request in `transcript.json`.
4. At the ascent from Duna's Sequence Plan: expect a Due Time for "the Kerbin window" (the ascent waits on the surface), a KSP alarm, and no launch until it is reached.
5. After the ascent, at the transfer to Kerbin: expect no margin Alert that counts the ascent (a leg already flown) as still to fly, and `plan_transfer`'s `later_legs_dv` to be the return's budget alone, so the soonest window fits when the craft has the delta-v for it.
