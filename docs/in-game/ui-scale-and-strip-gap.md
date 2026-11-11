# Claudia follows the stock UI scale, and the Capcom strip fits the gap (#991)

Claudia's scale is UI Scale × Apps scale × 1.2 (the UI Scale and Apps scale sliders in KSP's settings; settings.cfg `UI_SCALE` and `UI_SCALE_APPS`). The unmoved Capcom strip fills the gap between the stock altimeter and the app launcher bar.

Before you start, in the Flight scene with a Mission running, double-click the strip's status row so it is unmoved.

1. **100% / 100% looks as before.** Set UI Scale 100% and Apps 100%. Expect the Mission window, Settings window, Missions list and the strip's text at the same sizes as before #991 (Mission window opens 500×800, Settings 450×700, body text 17 px).
2. **Apps 150% grows 1.5×, 80% shrinks.** With the Mission and Settings windows open, set Apps to 150%. Expect their text and size to grow by half, in step with the stock app windows, on the next frame with no restart. Then set Apps to 80%: expect both to shrink. Each window keeps its position and stays on screen.
3. **Strip in the gap at 100%/100%.** In Flight, expect the strip's left edge a few pixels right of the altimeter, its right edge a few pixels left of the leftmost app launcher button, and its top level with the altimeter's top. Expand the tab: the docked Mission window opens below at the strip's width.
4. **Same at Apps 150% and in map view.** Set Apps 150%: expect the strip to re-fit the gap. Press M for map view: expect it still in the gap.
5. **The bar grows.** Add a button to the app launcher (another mod, or a resolution whose app bar holds more). Expect the strip to narrow to the new gap.
6. **Narrow gap.** Lower the resolution (or raise UI Scale) until the gap is narrower than the strip's minimum. Expect the strip under the altimeter, centred on it, not overlapping the altimeter or the bar.
7. **Drag and double-click.** Drag the strip by its status row. Expect it to stay where it was dropped, also after a scale change. Hover the row: the tooltip reads "double-click to put it back between the altimeter and the app bar". Double-click: expect it back in the gap.
8. **Fallback.** If the strip sits centred about 70 px down at 100%/100% instead of in the gap, the stock edges could not be read: note it on #991.
