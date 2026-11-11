# Claudia follows the stock UI scale, and the Capcom strip fits the gap (#991)

Claudia's scale is UI Scale × Apps scale × 0.96 (1.2 before #993; the UI Scale and Apps scale sliders in KSP's settings; settings.cfg `UI_SCALE` and `UI_SCALE_APPS`). The unmoved Capcom strip fills the gap between the stock altimeter and the app launcher bar.

Before you start, in the Flight scene with a Mission running, double-click the strip's status row so it is unmoved.

1. **100% / 100% is 20% under the look before #991.** Set UI Scale 100% and Apps 100%. Expect the Mission window, Settings window, Missions list and the strip's text 20% smaller than before #991 (#993: Mission window opens 400×640, Settings 380×560 (at its minimum width, whose paddings stay in pixels), body text 13 px).
2. **Apps 150% grows 1.5×, 80% shrinks.** With the Mission and Settings windows open, set Apps to 150%. Expect their text and size to grow by half, in step with the stock app windows, on the next frame with no restart. Then set Apps to 80%: expect both to shrink. Each window keeps its position and stays on screen.
3. **A remembered size follows the scale.** At 100%/100%, resize the Mission window in the Space Center and the docked panel in Flight. Set Apps 150% in Flight, then go to the Space Center: expect the Mission window there 1.5× the size you left it, its text in step. Quit and restart KSP: expect the same sizes, and the docked panel 1.5× the size you gave it. windows.cfg lines now end with the scale (`, 1.44`).
4. **Strip in the gap at 100%/100%.** In Flight, expect the strip's left edge a few pixels right of the altimeter, its right edge a few pixels left of the leftmost app launcher button, and its top level with the altimeter's top. Expand the tab: the docked Mission window opens below at the strip's width.
5. **Same at Apps 150% and in map view.** Set Apps 150%: expect the strip to re-fit the gap. Press M for map view: expect it still in the gap.
6. **The bar grows.** Add a button to the app launcher (another mod, or a resolution whose app bar holds more). Expect the strip to narrow to the new gap. If the app launcher bar runs vertically down the right edge of the screen in Flight instead of along the top, report it on #991.
7. **No buttons for a moment.** Press F2 to hide the UI and F2 again; also open and close map view. Expect the strip back in the gap at once, never at a spot about 56 px down in the middle.
8. **Narrow gap.** Lower the resolution (or raise UI Scale) until the gap is narrower than the strip's minimum. Expect the strip under the altimeter, centred on it, and below the app bar too if it reaches under it: not overlapping the altimeter or the bar.
9. **Drag and double-click.** Drag the strip by its status row. Expect it to stay where it was dropped, also after a scale change. Hover the row: the tooltip reads "double-click to put it back between the altimeter and the app bar". Double-click: expect it back in the gap.
10. **Fallback.** If the strip sits centred about 56 px down at 100%/100% instead of in the gap, the stock edges could not be read: note it on #991, with any `couldn't read the altimeter and app bar edges` line from KSP.log.

## 20% smaller than the stock UI (#993)

Compare with a build of 966289ca, at UI Scale 130% and Apps 100% (the KSP_spike settings).

11. **Text and windows 20% smaller.** Expect Claudia's text and windows 20% smaller than with 966289ca: the Mission window opens 520×832 (651×1041 before), body text 17 px (22 before). The stock UI is unchanged.
12. **Sizes from before open 20% smaller.** With a windows.cfg written by 966289ca (a Mission window you resized, the docked panel), open the Mission window and the docked panel: expect each at 0.8× the size it had, in the same place, its text in step.
13. **The strip still fits the gap.** In Flight, expect the unmoved strip between the altimeter and the leftmost app launcher button, a few pixels from each, its top level with the altimeter's.
14. **Everything usable.** Every button (title-bar close, gear, Clear, Undock, Dock, End Mission, Pause Mission, the quick replies, the strip's expand tab) is easy to click; type in the prompt box and send; the transcript scrolls and shows at least a few lines in the docked panel at its smallest height (shrink the screen or drag the strip near the bottom). In the Mission window at its minimum width, the control bar's buttons all show. At UI Scale 200%, the transcript still shows lines in the docked panel at its smallest height.
15. **Strip at its minimum width.** Narrow the gap (lower the resolution or move the app bar's buttons) until the strip just fits it at its minimum (521 px at 100%/100%, 645 at 130%), expand the panel and start a Mission: expect the status row's portrait, name, chip, line and spend meter, the track and its arrows, and the docked control bar's gear, Clear, Undock, Pause Mission and End Mission all whole, nothing clipped.
16. **Settings at its minimum width.** Open Settings and drag its edge as narrow as it goes (464 px at 130%): expect the Claudia Kerman header (portrait, name, subtitle, chevron), Save and check and Check saved key, Cancel and Apply, and every row whole, nothing clipped.
