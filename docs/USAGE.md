# PRTSCN user guide

Every key, option and tool in detail. For the overview, see the [README](../README.md).

## Run it

1. Copy `PRTSCN.exe` anywhere (for example `%LOCALAPPDATA%\PRTSCN\`) and double-click it. It's self-contained, so there's nothing to install.
2. It lives in the tray.
3. On Windows 11, turn off *Settings > Accessibility > Keyboard > "Use the Print screen key to open screen capture"*. PRTSCN warns you if this is on.

Windows SmartScreen will warn that the exe is unsigned ("More info > Run anyway"). Code signing is on the roadmap.

## Keys

| | |
| --- | --- |
| **Print Screen** | capture an area |
| **Ctrl + Shift + Print Screen** | capture a window: hover highlights it, a click selects it and brings it to the front |
| tray > **Scrolling capture** | select the part of the page that scrolls; PRTSCN scrolls it and joins it into one long image (Esc stops). Also the ⇣ button in the action bar. Optional hotkey in Options |
| tray > **Capture after a delay** | 3 / 5 / 10 s countdown for an area or a window, so you can open a menu or hover first (Esc or a tray click cancels) |
| **Shift + Print Screen** | save the monitor under the mouse straight to the screenshots folder (and copy it) |
| drag / click | select an area / a click selects the whole monitor (in window capture, a click picks the window) |
| P L A R M B N T | pen, line, arrow, rectangle, marker, blur, numbered steps, text (press again to go back to move mode) |
| 1-8 | ink colour |
| [ ] or mouse wheel | ink size (also text size and blur strength); or the size button in the toolbar |
| Shift while drawing | snap lines to 45°, square rectangles |
| arrows / Shift+arrows | nudge the selection 1 / 10 px; add Ctrl to resize |
| Ctrl+A | select the whole monitor |
| Ctrl+C, Enter, double-click | copy and close |
| Ctrl+S / Ctrl+P | save as / print |
| Ctrl+Z / Ctrl+Y | undo / redo |
| Esc, right-click | cancel (right-click clears the selection first) |

Text tool: click, type, Enter to finish, Shift+Enter for a new line, Ctrl+V to paste.

## Screen recording (MP4)

Needs Windows 10 2004 or later. Videos are H.264 MP4, saved to `Videos\PRTSCN` by default.

| | |
| --- | --- |
| **Ctrl + Alt + Print Screen** | open the record screen; while recording, stop and save |
| drag / click a window / **F** | record an area / that window (follows it when it moves) / the whole monitor |
| the size label (`1280 × 720 ▾`) | exact sizes: 1080p, 720p, square, 4:5, 9:16, or custom |
| record bar | Area · Window · Screen · Follow · All, mouse pointer, click highlights, keystrokes (off / shortcuts / all keys), spotlight, magnifier, computer sound, microphone, frame rate (15/30/60), countdown (off/3/5/10 s), **Record** |
| **Follow** | a fixed-size view (1280 × 720 by default) that glides after the mouse pointer |
| **All** | every monitor in one video |
| **Enter** or double-click | start recording |
| **Pause** | pause / resume while recording |
| control bar | timer and file size, sound level meters (computer, mic), mic mute, pause, restart, discard, **Stop** (drag it anywhere; it never shows in the video) |
| tray > **Record area / window / full screen** | the same from the menu; a left-click on the tray icon stops a recording |
| tray > **Record sound only** | computer sound and/or microphone to an `.m4a` file |
| record bar > **MP4 · 30 fps** | pick MP4 at 15 / 30 / 60 fps, or **GIF** (no sound; made from a temporary MP4 when you stop) |
| tray > **Recent recordings** / **Open a recording…** | open a recording in the editor |
| control bar > **pen** | draw on the screen while recording; it shows in the video. While drawing, a second row on the bar picks the colour (8) and pen size (4); 1–8 colour, [ ] or the mouse wheel any size (2–40), right-click or Ctrl+Z undo, Delete clears, Esc stops drawing (the strokes stay until cleared). The last colour and size are remembered. In a window recording the strokes are drawn into the video by PRTSCN, since the window capture can't see the drawing layer |
| tray > **Instant replay** | keeps recording the screen with the mouse in the background (30 s – 5 min, Options > Recording) |
| **Ctrl + Alt + R** | save the instant replay to the recordings folder |
| tray > **Schedule a recording…** | the full screen or sound only, starting at a time of day, for some minutes (PRTSCN must be running) |

Options > Recording also has **Zoom in on clicks** (the video zooms in where you click, follows the pointer, then zooms out) and a **watermark** (text and/or an image in a corner of every video, the instant replay included).

### Editor

A saved video opens in the editor (turn this off in Options > Recording > After recording; clicking the "Recording saved" note opens it too). The original file is never changed: **Save** writes a new file next to it (`… (edited).mp4`, `….gif` or `….m4a`).

| | |
| --- | --- |
| **Space** / click the picture | play / pause (removed parts are skipped) |
| **←** **→** / **Shift+←** **→** | one frame / 5 seconds |
| drag the amber handles, or **I** / **O** | trim the start / end at the playhead |
| **Blur** or **B**, then drag a box on the picture | pixelate that part (for the selected part of the timeline, or all of it); drag a box to move it, its corners or sides to resize it; click a box to remove it; Esc or **Done** finishes |
| the blue bars under the thumbnails | one per blur: drag an end to change when the blur starts or stops, drag the middle to move it in time, click it to jump to its start |
| drag across the timeline, then **Cut** or **Delete** | remove a part from the middle (red); click a red part to put it back |
| **Cut** with nothing selected, move, **Cut to here** | the same in two steps: mark the start at the playhead, then the end (Esc cancels) |
| **Ctrl+Z** / **Ctrl+Y** | undo / redo |
| **Frame** | save the current frame as an image (screenshot folder) and copy it |
| **Ctrl+S** / **Ctrl+Shift+S** | save / save as: MP4 (quality, size, frame rate, sound), GIF (width, frame rate, dither) or sound only |
| Copy file · Open folder · Rename… · Delete | act on the recording itself (Delete moves it to the Recycle Bin) |

GIFs store only what changes between frames, with a 255-colour palette per frame, so mostly still screens stay small.

Sound is AAC 160 kbit/s, 48 kHz stereo. Options > Recording > Sound picks the microphone and sets each source's volume (0–200%).

Click highlights, keystrokes, the spotlight and the magnifier are drawn into the video only; nothing changes on your screen. Options > Recording > Mouse and keys sets them and the magnifier zoom (1.5×, 2×, 3×). Keystrokes are off by default: "All keys" shows typed text, including passwords, so prefer "Shortcuts", which shows only key combinations, F-keys and Esc.

Areas wider or taller than 4096 px (for example a 5120 × 1440 monitor) are scaled down to fit the encoder. Options > Recording sets the folder, quality, hardware encoding, countdown, the frame around the area and auto-stop limits.

Settings are in `%APPDATA%\PRTSCN\settings.json`, and errors are logged to `%APPDATA%\PRTSCN\error.log`. The last scrolling capture writes every step to `%APPDATA%\PRTSCN\scroll.log`. Screenshots are saved to `Pictures\PRTSCN` by default.

## Build

Needs the .NET 8 SDK or newer. With only a newer runtime installed (for example .NET 10), set `DOTNET_ROLL_FORWARD=Major` first so the tests and the Debug build run.

```
dotnet test tests/PRTSCN.Core.Tests                         # geometry, layout, settings
dotnet run --project src/PRTSCN                             # Windows only
dotnet publish src/PRTSCN -c Release -r win-x64 -o dist     # one self-contained PRTSCN.exe
```

Developer checks (Windows): `PRTSCN.exe --record-test screen|area|window|audio <seconds> <out> [fps] [sound|mic|both]` records, pauses and writes a log (with sound levels) plus a snapshot PNG; `PRTSCN.exe --ui-shots <folder>` renders Options and the recording bar to PNG files; `PRTSCN.exe --export-test <recording> <out> mp4|gif|audio [cutFrom-cutTo] [width] [fps]` decodes, times and exports a recording; `PRTSCN.exe --editor-shot <recording> <out.png> [format 0-2]` renders the editor, plays through a cut, adds a blur and saves; `PRTSCN.exe --replay-test <seconds> <folder>` runs the instant replay with 2 s segments and saves it twice. `--record-test` takes effects as a 7th argument: `clicks,keys,spot,mag,zoom,wm,draw`. Environment switches for these checks: `PRTSCN_DRAWMODE=1` (draw test in drawing mode), `PRTSCN_HW=0|1` and `PRTSCN_REAL_SETTINGS=1` (editor check with hardware encoding on/off, or with your own settings).

After changing `design/tokens.json`, run `python3 tools/gen_theme.py`. `tools/make_icon.py` redraws the icon.

## Layout

```
src/PRTSCN.Core     pure logic (selection maths, toolbar placement, settings, file names), unit-tested
src/PRTSCN          WinForms app: tray, hotkeys, capture overlay, annotations, options
src/PRTSCN/Recording  screen recorder: Windows.Graphics.Capture, Direct2D, Media Foundation H.264 + AAC, WASAPI sound (NAudio)
tests/              xUnit tests for PRTSCN.Core
design/             design tokens (colours, sizes) and component notes
docs/               this guide and the README images
tools/              theme + icon generators
```

## Status

Runs on Windows 11. Screenshots, the recorder (every mode and effect), drawing, instant replay, scheduled recordings, the watermark and the editor have been tried by hand. Not built yet: captions, webcam overlay, sharing, WebM/MKV, code signing.

If a microphone or speaker can't be opened when a recording starts, the recording goes on without it and the "Recording" note says why (for example "no microphone is connected or enabled").
