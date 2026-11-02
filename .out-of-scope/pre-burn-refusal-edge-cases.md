# Mod-written Debriefs for every pre-burn refusal

The mod writes the Debrief of a pre-burn refusal itself (#579) only in the common case: the run ended in view of the craft, the Mission is Running, and no other turn is out, queued or compacting. Every other pre-burn refusal goes to Claudia's Debrief as before.

## Why this is out of scope

The cases left to Claudia are:

- an owed Debrief (`DebriefOnReturn`), where the player was in another vessel when the run ended;
- a run that ended while the Mission was Paused;
- a refusal that lands while a question, an Alert or a compaction is in flight or queued.

They are rare. Across the first 79 logged Missions there was one pause, no owed on-return Debrief, and 12 compactions, and a pre-burn refusal would have to land on one of these by chance. Each one that happens costs about $0.06 to $0.09 and 1 to 2 requests.

Each case would also need state the shortcut avoids on purpose. An owed Debrief has to keep the fact until the player is back in the craft. A Paused run's Debrief belongs to Resume. A queued turn would commit before the transcript fact it was sent without, which is why `DebriefedRefusal` bails when `Pending`, `Queue` or `Compacting` is set. Keeping those paths on Claudia's full Debrief is the simpler and safer trade.

## Prior requests

- #589: "Owed, on-return and Paused Debriefs of a pre-burn refusal still go to Claudia"
