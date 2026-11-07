# PRTSCN components

Every component is painted by hand in the capture overlay (GDI+). There are no child controls, so DPI scaling stays exact. Sizes are at 96 DPI and multiply by `scale`, which comes from the DPI of the monitor the selection sits on. Icons are original line drawings in `Icons.cs` (no third-party icon art). Font: Segoe UI, which ships with Windows (nothing to license).

```
Dim mask
  look      the frozen screenshot with bg at alpha.dim (55%) over it, except inside the selection
  states    idle (whole screen dimmed, hint pill centred on the cursor's monitor), selecting, selected
  used on   S01, S02

Selection
  look      1px accent border; 8 handles (8px squares, handle-fill with an accent border)
  states    dragging (no handles), selected, moving (cursor: SizeAll), resizing (resize cursor per handle)
  keyboard  arrows nudge 1px (Shift = 10px); Ctrl+arrows resize
  used on   S02

Size label
  look      pill, bg, text xs/600, "1280 × 720", 6px above the top-left corner (inside if no room)
  extra     shows "· 3px" (the ink width) for 1s after the mouse wheel changes it
  used on   S02

Tool button (vertical toolbar)
  items     Pen (P), Line (L), Arrow (A), Rectangle (R), Marker (M), Text (T), Colour, Undo (Ctrl+Z)
  size      32×32, icon 18, stroke 1.75
  states    default (text-muted icon), hover (surface bg, text icon), active (surface-active bg, accent icon, 2px accent bar on the left), pressed
  tooltip   pill next to the button: label + hotkey
  used on   S02

Action button (horizontal toolbar)
  items     Copy (Ctrl+C), Save (Ctrl+S), Print (Ctrl+P), Close (Esc)
  states    as for the tool button; Close hover uses the danger icon colour
  used on   S02

Toolbar
  look      bg at alpha.toolbar, radius md, 1px border, 4px padding
  placement vertical: right of the selection, else left, else inside; horizontal: below and right-aligned, else above, else inside
            Both are clamped to the selection's monitor (tested in PRTSCN.Core.Tests)
  used on   S02

Colour swatch + palette
  look      swatch = 18px circle in the current ink colour with a 1px white ring
  palette   8 swatches (tokens.ink) in a row beside the toolbar; the last cell "…" opens the Windows colour dialog
  states    closed, open, hover, selected (accent ring)
  keyboard  1-8 pick a colour from the palette
  used on   S02

Text editor (inline)
  look      dashed accent box around the text being typed; caret blinks at 530 ms
  keys      type; Enter = commit; Shift+Enter = new line; Esc = cancel; Backspace
  size      font px = 10 + 3 × ink width
  used on   S02

Toast (tray balloon)
  variants  info, success ("Copied", "Saved to …"), error (hotkey conflict)
  used on   S03, F04, F06

Tray icon + menu
  items     Capture (PrtScr), Capture full screen (Shift+PrtScr), Open screenshots folder, Options…, About, Exit
  states    idle; double-click = capture
  used on   S05

Options window (S06)
  look      a standard Windows dialog (light/system), Segoe UI 9pt, 12px spacing
  fields    capture hotkey, instant hotkey (HotkeyBox), format PNG/JPG, JPG quality, save folder (+ Browse), copy after instant capture, start with Windows, show notifications
  states    default; hotkey conflict (red helper text under the field)
  a11y      native controls, so keyboard, Tab order and screen readers work
```
