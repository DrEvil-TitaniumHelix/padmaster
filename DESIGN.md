# Pad Master — Design (authoritative; written 2026-11-03)

## Goal
Inside LaunchBox, right-click a game (or click a badge on it) → **"Generate and show controller mapping"** → a rendered
image of **the controller the player actually holds**, with that game's controls drawn on it. Multiple in-game states.
Controller selectable; the AI builds the controller template so there is no manual drawing. Pre-rendered cache first,
live generation when missing. A Claude Code persona (Pad Master) runs beside LaunchBox and talks both ways.

Not the goal: LaunchBox's static "Arcade - Controls Information" PNGs. They are not dynamic, not per-state, not per-controller.

## Settled decisions (do not re-open)
- Owner = Pad Master persona. Mastermind keeps design + acceptance + rulings. The owner rules everything else.
- First target = **NES on the GuliKit KK3 Max**. Then one platform at a time, the owner picks.
- Billing = Claude Code subscription through the bridge. No API key, ever.
- Nothing rather than a guess. Source attribution on every game JSON.
- Mapping (which physical button is NES A) is **read from the emulator's config, per emulator**, never asked.
- Controls_Finder (PycharmProjects) is retired; its JSONs are a comparison set only.

## Verified facts about this PC (2026-11-03)
- LaunchBox = `E:\LaunchBox`. Plugin API = `Core\Unbroken.LaunchBox.Plugins.dll`; interfaces present:
  `IGameMenuItemPlugin`, `IGameMultiMenuItemPlugin`, `IGameBadge`, `ISystemEventsPlugin`, `IGameLaunchingPlugin`,
  `IGameDataManager`, `IGameMediaCapability`, `ICustomField`. Plugins live in `E:\LaunchBox\Plugins\` (example: `eXoPlugin.dll`).
- Emulator per platform = `E:\LaunchBox\Data\Emulators.xml` (~20 emulators: RetroArch, Dolphin, PCSX2, PPSSPP, MAME 0.230,
  Cemu, Ryujinx, Xemu, Citra, Xenia, ScummVM, TeknoParrot, RPCS3, Yuzu, Citron, ShadPS4...). NES → RetroArch
  (`E:\LaunchBox\Emulators\RetroArch`), core `nestopia_libretro.dll`, command `-L "cores\nestopia_libretro.dll" -f`.
- RetroArch binding (`retroarch.cfg`): `input_joypad_driver = xinput`, autodetect on, `input_remap_binds_enable = true`,
  remaps in `config\remaps\<core>\`. Player 1: a_btn=1, b_btn=0, x_btn=3, y_btn=2, start_btn=6, select_btn=7, d-pad = hat h0.
  Nestopia: RetroPad A→NES A, RetroPad B→NES B (so NES A lands on the Xbox-layout **B** position of the KK3 Max; that is
  exactly why the render must be dynamic).
- KK3 Max = GuliKit KingKong 3 Max (NS39) via its USB adapter → Windows sees "Xbox 360 Controller for Windows", XInput slot 0.
  Proven readable with ctypes `XInputGetState` (mastermind scratchpad `xi.py`). Mode button double-click; see memory
  `reference_gulikit_kk3_max_controller.md`.
- Manuals = `E:\LaunchBox\Manuals\<Platform>\` : NES 1,027 files (PDF + some .txt), SNES 593. Ground truth for the game layer.
- Reference bridge = `RustroverProjects\UnrealClaudeMCP` (Frostsmith's MCP over a local TCP socket). Copy the shape, not the code.
- Images path LaunchBox already indexes: `E:\LaunchBox\Images\<Platform>\<ImageType>\<Game>.png`. Optional: also drop the
  default-controller render into "Arcade - Controls Information" so Big Box shows it with zero code. Nice-to-have, not the goal.

## Architecture

```
 game JSON (original-platform terms, states)      ← Pad Master, from manual PDF → model (flagged) → web (flagged)
        ∘ emulator binding (orig input → physical) ← reader per emulator (RetroArch first), read, never asked
        → controller template SVG (anchor per button) ← Pad Master draws it once per pad, the owner approves preview
        = PNG per (game, controller, state)         ← deterministic renderer, cached
```

### 1. Bridge plugin (C#, `ISystemEventsPlugin`)
Starts with LaunchBox. Keeps a Pad Master session alive beside it (Claude Code, subscription). Local socket both ways,
MCP-shaped like UnrealClaudeMCP. Exposes to Pad Master: games / platforms / emulator per platform / image + manual paths
for a game / "show image X in the viewer" / connected controller identity. Accepts from Pad Master: rendered files,
status lines. Everything else rides on this.

### 2. Controller onboarding (AI-templated)
Plugin reads the connected pad's XInput/HID identity (VID/PID/name). Pad Master produces the template: a clean drawn
controller (SVG) + one named anchor per physical button (`a`, `b`, `x`, `y`, `lb`, `rb`, `lt`, `rt`, `start`, `back`,
`dpad_up`..., `ls`, `rs`, extras). The owner sees one preview and approves. Stored in `controllers\<id>\`.
Adding a pad = this flow again, nothing else changes.

### 3. Game layer (model-using)
One JSON per game, in the **original platform's** terms (NES: A, B, Start, Select, D-pad, combos), with **states**
(e.g. Blaster Master: on foot / tank / overhead). Source order: manual PDF beside the ROM → model knowledge (flagged
`source: model`) → web (flagged with URL). Absent = `null` → viewer says "no mapping found". No generic labels. Combos
only when a source states them. Stored in `games\<Platform>\<Game>.json`.

### 4. Mapping layer (per emulator, deterministic)
`Emulators.xml` says which emulator runs the platform → the reader for that emulator parses its own binding file →
`{orig_input: physical_anchor}`. RetroArch reader ships with NES (`retroarch.cfg` + core remap + per-game remap).
Each new platform adds its emulator's reader once (Pad Master writes it on demand; a reader is ~30 lines).
Game layer and renderer never change per emulator.

### 5. Renderer (deterministic, no model)
game JSON ∘ mapping → labels at the anchors on the controller SVG → PNG per state. Cache key = game + controller + state
under `renders\`. Re-render on controller switch is instant.

### 6. Controls plugin + viewer (C#, `IGameMenuItemPlugin` + `IGameBadge`)
Right-click item "Generate and show controller mapping" + badge "has mapping". Viewer window: controller dropdown,
state tabs, the image. Missing → request through the bridge, progress line, render, show. Batch = whole platform from
the platform's menu.

## Milestones (acceptance = what the user sees on screen)
- **M0 Bridge.** LaunchBox starts → Pad Master session is up beside it. Pad Master lists NES games through the bridge.
  Plugin shows a test image in the viewer on Pad Master's command. Round-trip < 1 s.
- **M1 Controller.** KK3 Max plugged in → plugin names it → Pad Master renders the template → the owner approves one preview.
- **M2 One game.** Right-click *Blaster Master* → KK3 Max image with on-foot and tank tabs; NES A shows on the
  Xbox-layout B position because that is what `retroarch.cfg` says. Five more NES games checked by hand against their manuals.
- **M3 Batch NES.** Whole platform pre-rendered; badge on every mapped game; "no mapping found" count reported, never templated.
  Spot-check 10 random games against Controls_Finder's old JSONs; disagreements resolve by the manual PDF.
- **M4 Viewer polish.** Controller dropdown re-renders live; a second controller added by the onboarding flow alone.

## Layout (proposed; Pad Master may adjust and must record it here)
```
PadMaster\
  CLAUDE.md  DESIGN.md
  src\           ONE C# solution (.NET, Rider; SDK 10.0.114 installed). Projects: PadMaster.Bridge (ISystemEventsPlugin +
                 MCP-shaped local socket), PadMaster.Controls (menu item + badge + viewer), PadMaster.Core (emulator
                 readers, SVG→PNG renderer via Svg.Skia/SkiaSharp, one self-check each). DLLs → E:\LaunchBox\Plugins\.
                 Language = C# only (one language, one IDE). Target framework = whatever LaunchBox.exe runs; verify first.
  controllers\   <pad-id>\template.svg + anchors.json (approved)
  games\         <Platform>\<Game>.json (sourced)
  renders\       cache PNGs
```
M0 as built (2026-11-03): bridge = PadMaster.Bridge only; MCP over streamable HTTP 127.0.0.1:47800/mcp hosted in-plugin (HttpListener, stdlib), no Python, no separate server; Claude Code session launched by the plugin via cmd /k claude in the repo dir; .mcp.json in repo root; check = src\PadMaster.Check.
