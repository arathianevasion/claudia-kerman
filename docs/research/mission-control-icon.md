# How a mod marks a Contract in Mission Control's lists

Research ticket #1017 for map #1010 (Contract-driven Missions in Career). The question: how a KSP 1.12 mod can put an icon (the Claudia Flag) on a Contract's row in Mission Control's offered and accepted lists, with hover text, using public sources only.

No KSP DLL was decompiled, no repository of decompiled KSP code was read, and no stock source is quoted. The facts below come from open-source mods that drive Mission Control (Contract Configurator, RP-1, RP-AXE), the community API docs mirror (KSPDocsSite, KSP 1.12.4 Doxygen), Anatid's XML docs, and a save file KSP wrote. KSP calls a Contract Requirement a `ContractParameter`; that name appears only when citing the API.

## Summary

- **It can be done, and other mods already do it.** Mission Control is `KSP.UI.Screens.MissionControl`, a singleton (`MissionControl.Instance`) that exists only while the Mission Control building is open. Each row is an `MCListItem` cloned from the public prefab field `PrfbMissionListItem` and added to the public `UIList scrollListContracts`. Contract Configurator builds its own rows from that prefab and adds a child `GameObject` holding an `Image` (a status dot) to each. That's the same thing we'd do with the Claudia Flag. [CC `MissionControlUI.cs`; KSPDocsSite `MissionControl`, `MCListItem`]
- **The public surface is enough to find rows and their Contracts.** `MissionControl` has `scrollListContracts`, `displayMode` (`Available`, `Active`, `Archive`), `RebuildContractList()`, `AddItem(Contract contract, bool isAvailable, string label = "")` and `selectedMission` (a `MissionSelection` with public `contract`, `isAvailable` and `listItem`). `MCListItem` has `Setup(Contract contract, string label)` and the fields `container` (`UIListItem`), `title` (TextMeshPro), `difficulty`, `logoSprite` and `radioButton`. `UIListItem` has `object Data`. [KSPDocsSite]
- **Hooks.** No `GameEvent` fires per row. The usable signals are: `GameEvents.onGUIMissionControlSpawn` and `onGUIMissionControlDespawn` (the building opened or closed); `MissionControl.Instance` going non-null (Contract Configurator polls this in `Update()`); `GameEvents.Contract.onContractsListChanged`, `onOffered`, `onAccepted`, `onDeclined` and `onFinished`; and Harmony patches on `MissionControl.RebuildContractList` and `AddItem` (RP-1 replaces `RebuildContractList` with a Harmony prefix) or on `MCListItem.Setup`.
- **Mapping a row to its Contract.** The reliable public points are the `contract` argument of `AddItem` or `MCListItem.Setup`, captured in a Harmony postfix together with the row, and Contract Configurator's own row data (`UIListItem.Data` is a `MissionControlUI.ContractContainer`, whose public `contract` is null for a "contract type" placeholder). What stock puts in `UIListItem.Data` isn't documented anywhere public. From the row, key by `contract.ContractGuid`, never by title.
- **Offered Contracts read the same as accepted ones.** An offered Contract sits in `ContractSystem.Instance.Contracts` with `state = Offered`, and the save writes its `guid`, `type`, Contract-level keys and full `PARAM` Requirement tree, just as for an active one. So `Contract.Save(ConfigNode)` and the `GetParameter` walk from #1011 both work on offered Contracts. Classification doesn't need Mission Control at all; the icon only displays a verdict Core already made. [kRPC 1.12.5 career save]
- **Hover text.** Add a `KSP.UI.TooltipTypes.TooltipController_Text` to the row (or to the icon), set its `prefab` to `AssetBase.GetPrefab<Tooltip_Text>("Tooltip_Text")`, and call `SetText(...)`. KSPCommunityFixes and RP-1 both use this pattern. [KSPCommunityFixes `StaticHelpers.AddUITooltip`; KSPDocsSite `TooltipController_Text`]
- **The biggest pitfall is Contract Configurator.** When Mission Control opens, CC (if installed) replaces `GameEvents.Contract.onContractsListChanged` with a fresh `EventVoid`. That silently drops every subscriber, ours included. CC also strips the Available/Accept/Decline/Cancel listeners and builds its own Available and "All" lists. It builds the "All" tree without calling `MCListItem.Setup` or `MissionControl.AddItem`, so a patch on those alone misses CC's grouped rows. The Active and Archive tabs are still stock's flat list under CC.
- **Recommended approach.** Use a small Space Center `MonoBehaviour` that is idempotent and redraws on every rebuild, with no dependence on event order:
  1. Harmony postfix on `MCListItem.Setup(Contract, string)` that stores `row → ContractGuid` in a weak map.
  2. While `MissionControl.Instance != null`, sweep `scrollListContracts` every few frames. For each row with no marker child yet, resolve its Contract: the Setup map first, then `Data` read reflectively as anything with a public `contract` field (CC's container, or `MissionSelection`). Then add a `ClaudiaMark` child (`Image` with the Claudia Flag sprite) and a `TooltipController_Text`.
  3. Rows are destroyed and recreated on every rebuild, so the marker goes with them and the sweep re-adds it. Nothing needs cleaning up.
  4. Read the verdict ("Claudia can plan this" or the "not yet" reason) from a per-GUID cache that Core filled. Don't classify inside the sweep.

## Mission Control as mods see it

| Piece | Public members (KSP 1.12.4) | Source |
|---|---|---|
| `KSP.UI.Screens.MissionControl` | `static Instance`; `UIList scrollListContracts`; `MCListItem PrfbMissionListItem`; `DisplayMode displayMode` (`Available`, `Active`, `Archive`); `ArchiveMode archiveMode` (`All`, `Completed`, `Failed`, `Cancelled`); `MissionSelection selectedMission`; `RebuildContractList()`; `AddItem(Contract, bool isAvailable, string label = "")`; `OnClickAvailable/Active/Archive(bool)`; `SetDisplayModeAvailable/Active/Archive(bool force = false)`; `ClearInfoPanel()`; `UpdateInfoPanelContract(Contract)`; toggles `toggleDisplayModeAvailable/Active/Archive`; buttons `btnAccept`, `btnDecline`, `btnCancel`; text `textMCStats`, `contractText` | [KSPDocsSite MissionControl](https://kspmoddinglibs.github.io/KSPDocsSite/class_k_s_p_1_1_u_i_1_1_screens_1_1_mission_control.html) |
| `MissionControl.MissionSelection` | `MissionSelection(bool isAvailable, Contract contract, UIListItem listItem)`; fields `contract`, `isAvailable`, `listItem` | [KSPDocsSite MissionSelection](https://kspmoddinglibs.github.io/KSPDocsSite/class_k_s_p_1_1_u_i_1_1_screens_1_1_mission_control_1_1_mission_selection.html) |
| `KSP.UI.Screens.MCListItem` (one row) | `Setup(Contract contract, string label)`; `UIListItem container`; `TextMeshProUGUI title`; `UIStateImage difficulty`; `RawImage logoSprite`; `UIRadioButton radioButton` | [KSPDocsSite MCListItem](https://kspmoddinglibs.github.io/KSPDocsSite/class_k_s_p_1_1_u_i_1_1_screens_1_1_m_c_list_item.html) |
| `KSP.UI.UIListItem` | `object Data`; `GetTextElement`, `GetButtonElement`, `GetStateButtonElement` | [KSPDocsSite UIListItem](https://kspmoddinglibs.github.io/KSPDocsSite/class_k_s_p_1_1_u_i_1_1_u_i_list_item.html) |
| `KSP.UI.UIList` | Enumerable as `UIListData<UIListItem>` (`.listItem`); `AddItem`, `RemoveItem`, `Clear(bool)`, `GetIndex` | CC `MissionControlUI.cs` |
| `KSP.UI.TooltipTypes.TooltipController_Text` | `Tooltip_Text prefab`; `string textString`; `SetText(string)`; `continuousUpdate` | [KSPDocsSite TooltipController_Text](https://kspmoddinglibs.github.io/KSPDocsSite/class_k_s_p_1_1_u_i_1_1_tooltip_types_1_1_tooltip_controller___text.html) |

The offered list is `displayMode == Available` and the accepted list is `Active`. `Archive` holds finished Contracts and isn't in scope. With Contract Configurator installed there is a fourth "All" toggle (CC's grouped tree), which is shown as `displayMode == Available` while CC's private `displayModeAll` is true.

## How the open-source mods do it

### Contract Configurator: `MissionControlUI`

[`MissionControlUI.cs`](https://github.com/jrossignol/ContractConfigurator/blob/master/source/ContractConfigurator/MissionControlUI.cs) is a `[KSPAddon(KSPAddon.Startup.SpaceCentre, false)]` `MonoBehaviour`.

- **Waiting for the screen.** In `Update()` it does nothing while `MissionControl.Instance == null`, and unsubscribes its handlers then. It counts frames (`ticks`) once the instance exists. It wires everything on tick 1 and restores the last tab on ticks 1 and 2. It doesn't rely on `onGUIMissionControlSpawn`.
- **Building rows.** It calls `Object.Instantiate<MCListItem>(MissionControl.Instance.PrfbMissionListItem)`, sets `mcListItem.container.Data = <its ContractContainer>`, hooks `radioButton.onTrueBtn` and `onFalseBtn`, then calls `scrollListContracts.AddItem(mcListItem.container, true)`. The Available tab calls `mcListItem.Setup(contract, label)`. The "All" tree sets `title.text` itself and never calls `Setup`.
- **Decorating a row.** It creates `new GameObject("StatusImage")`, adds `RectTransform`, `CanvasRenderer`, `UIStateImage` and `Image`, anchors it at the left middle (`anchorMin`/`anchorMax` `(0, 0.5)`, 10×10 px, offset 16 px), and parents it to `mcListItem.transform`. It moves the `"Text"` child 60 px left and resizes it, retextures the row's `Image` and the radio button sprites, and repositions `difficulty`. This is the closest prior art for adding the Claudia Flag icon.
- **Mapping row to Contract.** It enumerates `scrollListContracts`, reads `item.Data as ContractContainer`, and compares `container.contract == c`. Its `ContractContainer` is public: `contract`, `contractType`, `missionSelection`, `mcListItem` and others. It builds `new MissionControl.MissionSelection(true, contract, mcListItem.container)` for the selection.
- **Pitfalls it creates for others:**
  - `GameEvents.Contract.onContractsListChanged = new EventVoid("onContractsListChanged");` runs every time Mission Control opens. The comment calls it a "very harsh way to disable the onContractsListChanged in the stock mission control". Every mod's subscription is dropped too, so don't depend on that event in the Space Center scene.
  - It calls `RemoveAllListeners()` on `toggleDisplayModeAvailable`, `btnAccept`, `btnDecline` and `btnCancel`.
  - When a Contract is offered, declined or finished, it inserts or removes single rows. To keep indentation it inserts by reflection into `UIList`'s private list data, so rows can appear without a full rebuild.
  - Its rows are `Destroy`ed and rebuilt on every tab click.

### RP-1: Harmony on `MissionControl`

[`Harmony/MissionControl.cs`](https://github.com/KSP-RO/RP-1/blob/master/Source/RP0/Harmony/MissionControl.cs) replaces `RebuildContractList` with a `[HarmonyPrefix]` that returns `false`. It clears `scrollListContracts`, sorts `ContractSystem.Instance.Contracts` (or `ContractsFinished` for Archive), and calls the public `__instance.AddItem(contract, isAvailable, label)` per Contract. The `label` is rich text (for example a coloured "Completed" prefix before the title). So:

- `AddItem(Contract, bool, string)` is a stable, patchable entry point per stock row, and its `label` is where text goes.
- Another mod can replace `RebuildContractList` outright. A postfix on `RebuildContractList` still runs after a prefix that skips the original (Harmony runs postfixes regardless), and a postfix on `AddItem` still sees every row RP-1 adds.

[`Harmony/Contract.cs`](https://github.com/KSP-RO/RP-1/blob/master/Source/RP0/Harmony/Contract.cs) patches `Contract.MissionControlTextRich`, the text in the right-hand info panel. That's an alternative place for the "why / why not" text when a Contract is selected.

[`SpaceCenter/SCMEvents.cs`](https://github.com/KSP-RO/RP-1/blob/master/Source/RP0/SpaceCenter/SCMEvents.cs) and [`UI/KCT/GUI_AbMcButton.cs`](https://github.com/KSP-RO/RP-1/blob/master/Source/RP0/UI/KCT/GUI_AbMcButton.cs) use `GameEvents.onGUIMissionControlSpawn` and `onGUIMissionControlDespawn` for building open and close. These events are also listed in [Anatid's `GameEvents.cs`](https://github.com/Anatid/XML-Documentation-for-the-KSP-API/blob/master/src/GameEvents.cs).

### RP-AXE: living with CC's tree

[`AxeProgramGroupVisibility.cs`](https://github.com/miskoltrans/RP-AXE/blob/main/Source/AXE/Contracts/AxeProgramGroupVisibility.cs) documents CC traps from experience:

- Only CC's "All" tab is the grouped tree. Active and Archive return early to stock's flat list.
- Rebuilding the list from inside `contract.Accept()` destroys the row CC's accept handler is still using. So it defers the rebuild to the next `Update()`.
- `MissionControlUI.Instance` is a static that CC never nulls. In other scenes it's a destroyed object, and only Unity's `==` overload makes it read as null.

Lesson for us: never rebuild Mission Control's list ourselves. Only add children to rows that already exist.

### Contracts Window + and Contract Parser

Neither touches Mission Control. A search of [Contracts Window +](https://github.com/DMagic1/KSP_Contract_Window) and [Contract Parser](https://github.com/DMagic1/KSP_Contract_Parser) finds no `MissionControl` or `MCListItem` reference. CW+ is its own window, built from its own asset bundle, so having it installed doesn't conflict with marking Mission Control rows. Decorating CW+'s window too would be a separate question.

### Hover text: `TooltipController_Text`

[KSPCommunityFixes `StaticHelpers.AddUITooltip`](https://github.com/KSPModdingLibs/KSPCommunityFixes/blob/master/KSPCommunityFixes/Library/StaticHelpers.cs) is the whole recipe:

```csharp
TooltipController_Text tooltip = go.AddComponent<TooltipController_Text>();
tooltip.prefab = AssetBase.GetPrefab<Tooltip_Text>("Tooltip_Text");
tooltip.SetText("...");
```

RP-1 uses the same `GetPrefab<Tooltip_Text>("Tooltip_Text")` in its widgets and `KSCFacilityContextMenu`. The target needs a raycast-receiving `Graphic`. The row's own background `Image` qualifies, so putting the controller on the row's `GameObject` gives hover over the whole row. A small icon alone is a hard target. That matters for unmarked rows, which have no icon to hover and still need the "not yet" reason.

## Offered and accepted Contracts read the same way

kRPC's [1.12.5 career save](https://github.com/krpc/krpc/blob/main/tools/krpctools/krpctools/game/ksp/saves/krpctest_career.sfs) has ten `state = Offered` Contracts. Each is written with `guid`, `type`, `prestige`, `agent`, deadline and expiry types, `values`, its Contract-level keys (e.g. `targetBody`, `targetBand` on a `SurveyContract`) and full `PARAM` nodes with `name`, `state = Incomplete` and their setting keys. That's the same shape as an active Contract. `ContractSystem.Instance.Contracts` holds both offered and active ones. So everything in the [Contract API research](https://github.com/arathianevasion/claudia-kerman/blob/research/contract-api/docs/research/contract-api.md) (`Save(ConfigNode)`, the `GetParameter` walk, `ContractGuid`) applies to offered Contracts unchanged. The differences:

- An offered Contract's dates (`DateAccepted`, `DateDeadline`) aren't set yet. Its deadline span is in `values`, and its expiry is `DateExpire`.
- Offered Contracts come and go between visits (`onOffered`, `onDeclined`, and expiry as `OfferExpired`), so the verdict cache is keyed by GUID and stale entries are dropped.
- Contract Configurator's "All" tree also shows Contract types with no Contract behind them (`ContractContainer.contract == null`), and its Available list can include `ContractPreLoader` pending Contracts that aren't in `ContractSystem.Contracts`. Both are mod Contracts, which are out of scope for #1010, so leave them unmarked.

## Pitfalls

1. **Rows are disposable.** Stock rebuilds the list on every tab change and on `onContractsListChanged`. CC destroys and recreates rows on every tab click and inserts single rows between rebuilds. Any decoration has to be re-applied on each new row. An idempotent sweep (does the row have a `ClaudiaMark` child?) handles every case.
2. **Don't rely on `onContractsListChanged` in the Space Center.** CC replaces the event object when Mission Control opens. Poll instead, or re-subscribe after `MissionControl.Instance` appears and accept that you may still be dropped.
3. **Scene lifetime.** `MissionControl.Instance` exists only while the building is open, in the Space Center scene. Use a `[KSPAddon(Startup.SpaceCentre, false)]` behaviour that does nothing while it's null. Compare Unity objects with `==`, not `is null` or `ReferenceEquals`, because destroyed objects read as null only through Unity's overload.
4. **Layout.** The row's `"Text"` child spans the row, and `difficulty` sits at the right. CC's status dot sits at the left (x = 16 px) and moves the text 60 px. An icon placed in the same spot would overlap CC's dot when CC is installed. Put the Claudia icon at the right edge next to `difficulty`, or at the far left in stock and shifted when a `"StatusImage"` sibling exists. Set `layoutIgnore` with a `LayoutElement`, or plain anchoring, so the list's layout group doesn't resize the row.
5. **Never accept, decline or rebuild.** Claudia only reads. Don't call `RebuildContractList`, `OnClick*` or `SetDisplayMode*`, and don't touch `btnAccept` and the other buttons. RP-AXE's accept-time crash shows how easy it is to break CC's handlers.
6. **Don't key on titles.** `Title` is localised, and RP-1 and CC rewrite labels with rich-text prefixes and colours. Key on `ContractGuid`.
7. **Classification is not done in the sweep.** The sweep runs on the main thread every few frames. It only looks up a precomputed verdict per GUID, filled when Contracts load or change (`onContractsLoaded`, `onOffered`, `onAccepted` and the rest, or a re-read when Mission Control opens).
8. **Precedent.** [`.out-of-scope/stock-trait-icon.md`](../../.out-of-scope/stock-trait-icon.md) rejected a Harmony patch that adds an image to a stock UI prefab (`CrewListItem`) for a cosmetic icon. The player's 2026-11-13 ruling now asks for exactly that kind of change in Mission Control. The design should keep it small and fail-safe: wrap every sweep in try/catch and log once, and disable the marker for the session on the first exception rather than break Mission Control.

## Recommended approach

1. **Adapter: `MissionControlMarker`**, a `[KSPAddon(KSPAddon.Startup.SpaceCentre, false)]` `MonoBehaviour`:
   - While `MissionControl.Instance == null`, do nothing.
   - Otherwise, every N frames (a handful per second), enumerate `MissionControl.Instance.scrollListContracts`. For each `UIListData<UIListItem>`, get the `MCListItem` from `listItem.GetComponent<MCListItem>()`. If it's null (CC's group rows are also `MCListItem`s, so check for a Contract instead), or the row already has a `ClaudiaMark` child, skip it.
   - Resolve the Contract in order: (a) the weak map filled by a Harmony postfix on `MCListItem.Setup(Contract, string)`, keyed by the row; (b) `listItem.Data`, read reflectively for a public instance field named `contract` of type `Contract`, which covers CC's `ContractContainer` and a `MissionSelection` if stock stores one there. No Contract means no mark.
   - Look up the verdict by `contract.ContractGuid`. If Claudia can plan it, add the `ClaudiaMark` child (`Image` with a sprite made from the Claudia Flag texture, small, anchored at the right edge before `difficulty`) and a `TooltipController_Text` on the row saying so. If not, add only the tooltip with the "not yet" reason, plus an empty `ClaudiaMark` child so the row isn't processed again.
2. **Harmony:** one postfix on `MCListItem.Setup`. Optionally add one on `MissionControl.AddItem` as a second capture point, with the row being the last entry of `scrollListContracts` after the call. Both are public methods, and RP-1 shows `AddItem` patched safely alongside CC.
3. **Core:** the verdict ("plannable" or "not yet: <reason>") per GUID, from the Contract snapshot that #1011's research describes. Mission Control is only a view of it.
4. **Optional:** a postfix on `Contract.MissionControlTextRich` (as RP-1 does) to add a "Claudia can plan this" or "Not yet: ..." line to the info panel when a Contract is selected. That's text only, with no layout risk.

## Only an in-game check or decompiling could confirm (flagged, not done)

An in-game probe (log `Data?.GetType()` and the row hierarchy) closes most of these without decompiling:

- What stock `MissionControl.AddItem` stores in `UIListItem.Data` (probably a `MissionSelection`, since that's what `selectedMission` holds, but undocumented), and whether stock `AddItem` calls `MCListItem.Setup`.
- The child names and layout of the `MCListItem` prefab beyond `"Text"` (seen in CC), and the free space at the right edge for an icon.
- Whether stock Mission Control subscribes to `onContractsListChanged` for its rebuild. CC's comment implies it does.
- Whether `onGUIMissionControlSpawn` fires before or after `MissionControl.Instance` is set and the first list is built. Polling makes this moot.
