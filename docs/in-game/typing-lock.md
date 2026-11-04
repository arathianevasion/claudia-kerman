# Typing in a Claudia text field never stages the craft (#735)

The keyboard lock (`ControlTypes.KEYBOARDINPUT`) now follows a focus flag that each window records inside its own draw (Core `TypingFocus`), and a Layout pass never clears it. Install and `KSP.log` location are as in `phase-3-gate.md`.

Set `debug_tools = true` in the player config first. Each time a window's typing lock turns on or off, `KSP.log` gets one line: `[ClaudiaKermin] typing lock on: Mission window` (or `off`). The names are `Mission window`, `Capcom strip`, `Chat window`, `Settings window`.

Use a craft with a stage still to fire (an uncrewed one is the worst case). Watch the stage counter.

## Check 1: waiting for plan approval

1. In flight, start a Mission and wait until it asks for plan approval (Claudia holds no craft lock then).
2. Dock the panel under the Capcom strip. Click into the prompt and type several spaces and words. Nothing stages; the stage counter does not move. `KSP.log` shows `typing lock on: Capcom strip`.
3. Click out of the prompt (on empty panel space). `KSP.log` shows `typing lock off: Capcom strip`; space now stages again (check it with the prompt unfocused, then undo nothing: that stage is yours to fire).
4. Undock the panel into the floating Mission window and repeat: `typing lock on` then `off` for `Mission window`.

## Check 2: Manual Control

1. Take Manual Control (the action row's button). Repeat check 1 steps 2 to 4 in the docked strip and in the floating window. Nothing stages; the lock lines are the same.

## Check 3: the chat window

1. Open the chat window from the toolbar. Click into its text field and type spaces: nothing stages; `typing lock on: Chat window`, then `off` after clicking out.
2. Press F2 while the field has focus: `typing lock off` for the window, and space stages again once it is back.

## Check 4: Settings

1. Click into the API key field, type spaces: no staging, `typing lock on: Settings window`; click out: `off`.
