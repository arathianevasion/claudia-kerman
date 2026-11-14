# Contract limits, deadlines and lifetime (KSP 1.12)

Research for [#1013](https://github.com/arathianevasion/claudia-kerman/issues/1013), part of the map [#1010](https://github.com/arathianevasion/claudia-kerman/issues/1010) (Contract-driven Missions in Career). Date: 2026-11-13.

Terms: **Contract** and **Contract Requirement**. KSP calls a Contract Requirement a `ContractParameter`; that name appears below only when citing code or docs.

Sources are public only: the KSP wiki (read as raw wikitext), and the open-source mods kRPC, Contract Configurator, Contracts Window + and CapCom. None of this comes from decompiling KSP. Where a public source does not settle a point, the point is marked **Unverified**.

## Answer in brief

- **Active Contract limit, by Mission Control level**: level 1 allows 2, level 2 (75,000 funds) allows 7, and level 3 (282,000 funds) has no limit. Stock difficulty settings do not change these numbers.
- **Difficulty changes the money, not the count**: the Funding and Reputation Penalty sliders (50% to 200% across the presets, custom range 10% to 1,000%) scale the penalties. Hard turns off reverting and quickloading.
- **Two separate clocks**: an *offer expiry* (the time an offered Contract stays on the board) and a *deadline* (the time allowed after acceptance). Both run in game time (Universal Time, seconds), so time warp uses them up.
- **The deadline is a duration until the Contract is accepted**, then it becomes an absolute Universal Time. `TimeDeadline` holds the duration and `DateDeadline` holds the absolute UT; 0 means no deadline.
- **When the deadline passes** on an accepted Contract, the Contract ends in a `DeadlineExpired` state and is treated as failed: the failure penalties apply.
- **When a critical Contract Requirement fails**, the Contract fails, for example when the Kerbal in a rescue Contract dies. Cancelling is different: it is not a failure. It takes back the advance and costs a small reputation penalty.
- **Revert and quickload** reload a saved game state, and Contract state is saved with it. So a Contract completed or failed after that save point goes back to the state it was saved in, and so do its rewards. This is inferred from how saves work, not documented.
- **A Contract can complete mid-flight.** A Contract Requirement completes, and pays its own reward, the moment its condition is met. The Contract completes as soon as every non-optional Contract Requirement is complete, even while optional ones are still open.

## 1. How many Contracts can be accepted

### Stock limit by Mission Control level

The KSP wiki's Mission Control page ([raw wikitext](https://wiki.kerbalspaceprogram.com/index.php?title=Mission_Control&action=raw)) lists:

| Level | Upgrade cost | Max active Contracts |
|---|---|---|
| 1 | Free | 2 |
| 2 | 75,000 funds | 7 |
| 3 | 282,000 funds | Unlimited |

The game looks the limit up with `GameVariables.Instance.GetActiveContractsLimit(ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl))`. It compares that against `ContractSystem.Instance.GetActiveContractCount()`. Both [Contract Configurator](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/MissionControlUI.cs) (`MissionControlUI.cs`, lines 335 and 1271) and [CapCom](https://github.com/DMagic1/CapCom/blob/master/Source/CapComWindow.cs) (`CapComWindow.cs`, line 145) do exactly this. "Unlimited" comes back as `int.MaxValue`: Contract Configurator checks `maxActive == int.MaxValue`, and CapCom treats any value of 1,000,000 or more as having no limit.

For us this means a Missions list is bounded by the same number. A mod can read the limit through that same call, so we never need to hard-code 2/7/unlimited.

### Difficulty

The wiki's [Game Difficulty](https://wiki.kerbalspaceprogram.com/index.php?title=Game_Difficulty&action=raw) page lists every stock difficulty option, and none of them sets the number of Contracts. The options that matter for Contracts are below. All but the first two are Career-only and fixed once the save starts.

| Option | Easy | Normal | Moderate | Hard | Custom range |
|---|---|---|---|---|---|
| Allow Reverting Flights | yes | yes | yes | **no** | |
| Allow Quickloading | yes | yes | yes | **no** | |
| Funding Rewards | 200% | 100% | 90% | 60% | 10%–1,000% |
| Reputation Rewards | 200% | 100% | 90% | 60% | 10%–1,000% |
| Science Rewards | 200% | 100% | 90% | 60% | 10%–1,000% |
| Funding Penalties | 50% | 100% | 150% | 200% | 10%–1,000% |
| Reputation Penalties | 50% | 100% | 150% | 200% | 10%–1,000% |

**Unverified**: the wiki does not say exactly which penalties the Penalty sliders scale. That they scale Contract failure and decline penalties is a reasonable inference from the names, and forum players report that the funds slider also scales facility upgrade costs.

### With Contract Configurator installed (common in modded Careers)

Contract Configurator adds a second, tighter limit for each prestige level, on top of the stock limit ([`ContractConfigurator.cs` `ContractLimit`/`CanAccept`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ContractConfigurator.cs), lines 552–574). The limit grows with reputation and Mission Control level, scaled by its own difficulty setting, `ActiveContractMultiplier` (0.2–8.0, default 1.0, [`SettingsBuilder.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/SettingsBuilder.cs), line 46). The per-prestige count skips auto-accepted Contracts (`!c.AutoAccept`). Each Contract type in a pack can also set `maxSimultaneous` (default 4) and `maxCompletions` ([Contract-Type wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Contract-Type)). This is why a level-3 Mission Control can still refuse a Contract when Contract Configurator is installed.

**Unverified**: whether stock `GetActiveContractCount()` counts auto-accepted Contracts. Contract Configurator and CapCom both filter them out themselves when they count by prestige, which suggests the stock count may not. No public source settles it.

## 2. How deadlines are expressed and measured

Every Contract carries these times as Universal Time in seconds, documented in kRPC's wrapper ([`Contract.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/Contract.cs), lines 342–375):

- `DateAccepted`: "Universal time at which the contract was accepted, in seconds. Zero if the contract has not been accepted."
- `DateDeadline`: "Universal time by which the contract must be completed, in seconds. Zero if the contract has no deadline."
- `DateExpire`: "Universal time at which the contract offer expires, in seconds. Zero if the contract has no expiry date."
- `DateFinished`: "Universal time at which the contract was completed or failed."

The deadline starts as a **duration** and becomes an **absolute** time on acceptance:

- CapCom shows an *offered* Contract's deadline either as `DateDeadline − now` or, when `DateDeadline` is 0, as a "Mission Duration" taken from `TimeDeadline`. For an *active* Contract it shows only `DateDeadline − now` ([`CapComWindow.cs`](https://github.com/DMagic1/CapCom/blob/master/Source/CapComWindow.cs), lines 1131–1157). So before acceptance the deadline is held as a duration, and after acceptance it is an absolute UT.
- Contract Configurator sets the deadline as a duration (`SetDeadlineDays(contractType.deadline, ...)`) and the offer expiry as a duration (`SetExpiry(min, max)`). It then has to fill in the absolute `dateExpire = GameTime + TimeExpiry` itself, with the comment "in stock this is normally done on Contract.Offer()" ([`ConfiguredContract.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ConfiguredContract.cs), lines 218–240). So the offer clock starts when the Contract is offered.
- Contracts Window + works out time remaining as `Root.DateDeadline − Planetarium.GetUniversalTime()`, and treats `DateDeadline <= 0` as no deadline ([`contractWindow.cs`](https://github.com/DMagic1/KSP_Contract_Window/blob/master/Source/ContractsWindow/PanelInterfaces/contractWindow.cs), lines 517–526).
- The wiki ([Contract](https://wiki.kerbalspaceprogram.com/index.php?title=Contract&action=raw)): "each can have its own deadline (or none at all). Note that the time remaining until the deadline is listed in GAME time. A mission that expires in a year can expire in mere moments of real time when time acceleration is activated."
- Story-line Contracts (World-Firsts progression) "do not have a deadline once they are accepted but they are not offered indefinitely and can thus expire" (same page).

Contract Configurator states its durations in days: `deadline` defaults to none, and `minExpiry`/`maxExpiry` default to 5 and 100 days, with 0/0 meaning the offer never expires ([Contract-Type wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Contract-Type)).

**Unverified**: whether "days" means 6-hour Kerbin days or 24-hour Earth days follows the game's calendar setting. That is likely, but no public source confirms it.

## 3. What happens at the end of a Contract

kRPC lists every Contract state ([`ContractState.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractState.cs)): `Active, Canceled, Completed, DeadlineExpired, Declined, Failed, Generated, Offered, OfferExpired, Withdrawn`. A Contract Requirement has three states: `Incomplete`, `Complete` and `Failed` (kRPC [`ContractParameter.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractParameter.cs), lines 205–215).

| Event | Resulting state | Effect | Source |
|---|---|---|---|
| Offer not accepted in time | `OfferExpired` | Leaves the board; no penalty | wiki Contract ("ignored without penalty"); kRPC enum |
| Player declines | `Declined` | "usually about −1 Reputation" | wiki Contract |
| Player cancels an accepted Contract | `Canceled` | "does not count as failure"; the advance funds and reputation are taken back, plus a small reputation penalty; frees the slot | wiki Contract |
| Deadline passes while accepted | `DeadlineExpired` | The Contract fails and the failure penalties apply | wiki [0.24 notes](https://wiki.kerbalspaceprogram.com/index.php?title=0.24&action=raw): "Contracts will fail if the deadline expires"; kRPC enum; Contract Configurator's `OnDeadlineExpired` hook |
| A critical Contract Requirement fails | `Failed` | Failure penalties (funds, and reputation "if the failure involved the loss of kerbonauts") | wiki 0.24 notes ("killing a Kerbal in a mission to rescue him"); wiki Contract |
| Every non-optional Contract Requirement complete | `Completed` | Completion rewards | see section 4 |
| Contract withdrawn by the game | `Withdrawn` | For example, Contract Configurator withdraws an offered Contract whose type was uninstalled (an active one is failed) | [`ConfiguredContract.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ConfiguredContract.cs), lines 480–490 |

The advance is part of the failure cost: Contract Configurator passes `advanceFunds + failureFunds` as the failure funds, and its docs say "advanceFunds are automatically added to the failureFunds" ([Contract-Type wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Contract-Type)).

**Unverified**: whether stock charges the same penalty for `DeadlineExpired` as for a `Failed` Contract Requirement. Both count as failure in the 0.24 notes, but a forum post with no source says otherwise. Whether any stock Contract Requirement failure leaves the Contract running (a "non-critical" failure) is also open. Contract Configurator has a `fakeFailures` flag for exactly that, which suggests stock fails the Contract on any failed child, but that is not confirmed for stock.

### Revert and quickload

- Contracts are saved to and loaded from the save file (Contract Configurator's `OnSave`/`OnLoad` write each Contract's state, Contract Requirements and behaviours "to persistance file", [`ConfiguredContract.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ConfiguredContract.cs)). Quickload (F9) loads a quicksave ([wiki Key bindings](https://wiki.kerbalspaceprogram.com/index.php?title=Key_bindings&action=raw)). Revert loads the state saved at launch.
- So, **inferred**: after a revert or quickload, every Contract is back in the state it had when that save was made. A Contract completed during the reverted flight is active again, with its reward taken back. A Contract that failed is active again. A Contract accepted after the save is offered again. Deadlines are absolute UT, so a revert also winds back the game clock the deadline is measured against. Forum reports fit this (for example ["completed contracts reverting to uncompleted"](https://forum.kerbalspaceprogram.com/topic/170001-ps4-completed-contracts-reverting-to-uncompleted)), but no official page states it.
- On Hard, neither is allowed (Game Difficulty table above). A Contract outcome is then final.
- **Unverified**: Contract side effects that happen outside the save, such as a vessel or Kerbal spawned by a rescue Contract when it is accepted, are presumably reloaded from the same save, but nothing public confirms this for every Contract type.

## 4. Completing mid-flight while other Contract Requirements are open

- A Contract Requirement completes the moment its condition is met in flight, and pays its own rewards then. In Contract Configurator, `SetState(Complete)` calls `AwardCompletion()` straight away and tells the parent ([`ContractConfiguratorParameter.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/Parameter/ContractConfiguratorParameter.cs), lines 245–271). kRPC exposes per-Requirement `FundsCompletion`, `ScienceCompletion` and `ReputationCompletion`.
- A Contract completes when every **non-optional** Contract Requirement is complete. Contract Configurator's replacement for stock `AllChildParametersComplete` returns true when every child is `Complete` "&& !param.Optional", and its doc comment says "Whether all non-optional parameters are complete" (same file, lines 273–288). Its wiki says "Optional parameters do not need to be completed" ([Parameters](https://github.com/jrossignol/ContractConfigurator/wiki/Parameters)).
- So **yes**: a Contract can complete in the middle of a flight while some of its *optional* Contract Requirements are still open. It cannot complete while a *required* one is still open. Some Requirements can also go back from `Complete` to `Incomplete` before the Contract completes, for example when the vessel leaves an altitude band. Contract Configurator's `disableOnStateChange = false` allows that, and its default varies by Requirement ([Parameters wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Parameters)).
- Stock example from the wiki ([Contract → Satellite](https://wiki.kerbalspaceprogram.com/index.php?title=Contract&action=raw)): "the contract is considered fulfilled as soon as the probe stays in the requisite orbit for ten seconds, and not monitored for compliance afterward." The vessel is still flying when the Contract completes.
- Several Contracts can complete in the same flight ("It's also possible for multiple contracts to be completed by the same craft", wiki Contract).
- Order can matter: Contract Configurator's `completeInSequence` makes a Contract Requirement wait until the Requirements listed before it at the same level are complete.

**Unverified**: what stock does with optional Contract Requirements that are still open when the Contract completes (they likely stay unpaid and are closed off), and whether stock's own `AllChildParametersComplete` ignores optional Requirements. Contract Configurator replaced the stock method with one that does, which hints that stock may count them.

## What this means for Contract-driven Missions

- Read the limit from the game at run time, using `GetActiveContractsLimit` with the Mission Control level and `GetActiveContractCount`. Expect Contract Configurator's lower per-prestige limits in modded saves.
- Treat `DateDeadline` (absolute UT) as the deadline for accepted Contracts. Measure the time left against `Planetarium.GetUniversalTime()`, because time warp uses up the deadline.
- A Mission must expect its Contract to complete partway through a flight, and expect individual Contract Requirements to complete, or occasionally go back to incomplete, before that.
- A revert or quickload can undo a completion we already reported. After a scene load, Contract state should be read again from the game, not cached.

## Sources

- KSP wiki, read as raw wikitext: [Mission Control](https://wiki.kerbalspaceprogram.com/index.php?title=Mission_Control&action=raw), [Contract](https://wiki.kerbalspaceprogram.com/index.php?title=Contract&action=raw), [Game Difficulty](https://wiki.kerbalspaceprogram.com/index.php?title=Game_Difficulty&action=raw), [0.24 release notes](https://wiki.kerbalspaceprogram.com/index.php?title=0.24&action=raw), [Key bindings](https://wiki.kerbalspaceprogram.com/index.php?title=Key_bindings&action=raw). The rendered wiki pages are behind a bot check.
- kRPC: [`Contract.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/Contract.cs), [`ContractState.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractState.cs), [`ContractParameter.cs`](https://github.com/krpc/krpc/blob/main/service/SpaceCenter/src/Services/ContractParameter.cs).
- Contract Configurator: [Contract-Type wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Contract-Type), [Parameters wiki](https://github.com/jrossignol/ContractConfigurator/wiki/Parameters), [`ConfiguredContract.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ConfiguredContract.cs), [`ContractConfigurator.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/ContractConfigurator.cs), [`MissionControlUI.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/MissionControlUI.cs), [`ContractConfiguratorParameter.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/Parameter/ContractConfiguratorParameter.cs), [`SettingsBuilder.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/SettingsBuilder.cs).
- Contracts Window +: [`contractWindow.cs`](https://github.com/DMagic1/KSP_Contract_Window/blob/master/Source/ContractsWindow/PanelInterfaces/contractWindow.cs).
- CapCom: [`CapComWindow.cs`](https://github.com/DMagic1/CapCom/blob/master/Source/CapComWindow.cs).
- Forum (anecdotal, supporting only): [completed contracts reverting to uncompleted](https://forum.kerbalspaceprogram.com/topic/170001-ps4-completed-contracts-reverting-to-uncompleted).
