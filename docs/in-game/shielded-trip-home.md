# A craft that can enter comes home by direct entry (#981)

In mission m20261110-203402-45d1 (a Duna round trip, the craft carrying an Entry Shield and an Mk16 on the pod), the per-turn `plan_transfer` row for Kerbin said "arrival: 1125 of 1711 m/s: target_pe 80000, capture(), deorbit(), reentry()", because the delta-v covered a capture. Claudia planned the capture (1,718 m/s against 671 for a direct entry), circularized at 262 km and ran short. Now `HomeLanding.Arrival` takes a capture first only when the craft can't enter as it is. A capture asked for (`capture` true) is still priced.

What "can enter" means (`EntryCraft.CanEnter`, `HomeLanding.DirectEntryExcessLimit`):
- **The Last Entry's shield** (the one nearest the pod) holds at least half its Ablator (`AblatorShareNeeded`).
- **A usable chute on what lands:** stowed or armed, not opened on Duna, and not on the stack the Entry drops or any stage still to be decoupled. At Kerbin an engine doesn't count instead.
- **The arrival is within 1,000 m/s of excess** at Kerbin (the Delta-v Map's figure): about 3.4 km/s at the top of the air, against the 3.2 km/s of a shielded Mun return, the only entry heat on record. Duna (about 920) and Eve (about 780) returns qualify; Dres, Moho, Jool and Eeloo keep the capture. Half the ablator and this limit are stated margins, not measured: this check is what measures them.

A direct entry gives up the capture's precise KSC landing for the Late Trim's best.

1. **Fly a Duna round trip** with a craft that has an Entry Shield under the pod and a parachute on the pod, and a Mission Goal like "land on Duna and return to Kerbin" that names no place on Kerbin. Let Claudia plan as usual.
2. **The Kerbin row.** Once in Duna orbit (or at the leg's Mission Plan request), the stage table's `plan_transfer {"target":"Kerbin","target_pe":80000}` row in the request (the transcript's user message) has `"capture":false`, no `capture_dv` in its windows, and `"arrival_note":"arrival: entry at 21 km, no capture burn; shielded: transfer_to, reentry()"`. Its `total_dv` is the ejection alone (about 670 m/s from low Duna orbit).
3. **The plan.** The Mission Plan's Kerbin leg is budgeted at about that figure, with no "capture into orbit" or "deorbit" leg, and its Sequence Plan flies `transfer_to` to Kerbin then `reentry`. A capture leg in Claudia's plan fails this check, unless the Mission Goal or the player asked for an orbit first.
4. **It comes home.** The craft enters Kerbin's air from the transfer, the shield takes the heat, and the chutes land it. On #981 say whether it did, with: the Kerbin row's note; the arrival's `arrival_v_inf`; the shield's peak temperature as a share of its limit (the Overheating events, if any) and the Ablator left after the entry (right-click the shield before it jettisons, or the last snapshot); the peak g; and the landing's distance from KSC (Tracking Station or the finished.json landing figures).
5. **No chute keeps the capture.** Optional: the same trip with the pod's parachute removed (the transfer engine still aboard) and more than enough delta-v gets "capture(), deorbit(), reentry()" in the Kerbin row.
