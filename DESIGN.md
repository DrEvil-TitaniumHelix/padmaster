# Padmaster — Design

## Goal
Inside LaunchBox, right-click a game (or click a badge on it) → **"Generate and show controller mapping"** → a rendered
image of **the controller the player actually holds**, with that game's controls drawn on it. Multiple in-game states.
Controller selectable; the AI builds the controller template so there is no manual drawing. Pre-rendered cache first,
live generation when missing. An AI assistant (the user's own Claude Code or Codex subscription) runs beside LaunchBox
and talks both ways.

Not the goal: LaunchBox's static "Arcade - Controls Information" PNGs. They are not dynamic, not per-state, not per-controller.

## Settled decisions
- First target = **NES on an XInput controller** via RetroArch. Then one platform at a time.
- Billing = the user's own AI subscription through the CLI the plugin launches. No API key, ever.
- Nothing rather than a guess. Source attribution on every game JSON.
- Mapping (which physical button is NES A) is **read from the emulator's config, per emulator**, never asked.

## Architecture

```
 game JSON (original-platform terms, states)      ← assistant, from manual PDF → model (flagged) → web (flagged)
        ∘ emulator binding (orig input → physical) ← reader per emulator (RetroArch first), read, never asked
        → controller template SVG (anchor per button) ← assistant draws it once per pad, user approves preview
        = PNG per (game, controller, state)         ← deterministic renderer, cached
```

### 1. Bridge plugin (C#, `ISystemEventsPlugin`)
Starts with LaunchBox. Keeps an assistant session alive beside it (the user's CLI and subscription). Local MCP server
both ways. Exposes to the assistant: games / platforms / emulator per platform / image + manual paths for a game /
"show image X in the viewer" / connected controller identity. Accepts from the assistant: rendered files, status lines.
Everything else rides on this.

### 2. Controller onboarding (AI-templated)
Plugin reads the connected pad's XInput/HID identity (VID/PID/name). The assistant produces the template: a clean drawn
controller (SVG) + one named anchor per physical button (`a`, `b`, `x`, `y`, `lb`, `rb`, `lt`, `rt`, `start`, `back`,
`dpad_up`..., `ls`, `rs`, extras). The user sees one preview and approves. Stored in `controllers\<id>\`.
Adding a pad = this flow again, nothing else changes.

### 3. Game layer (model-using)
One JSON per game, in the **original platform's** terms (NES: A, B, Start, Select, D-pad, combos), with **states**
(e.g. Blaster Master: on foot / tank / overhead). Source order: manual PDF beside the ROM → model knowledge (flagged
`source: model`) → web (flagged with URL). Absent = `null` → viewer says "no mapping found". No generic labels. Combos
only when a source states them. Stored in `games\<Platform>\<Game>.json`.

### 4. Mapping layer (per emulator, deterministic)
LaunchBox's emulator list says which emulator runs the platform → the reader for that emulator parses its own binding
file → `{orig_input: physical_anchor}`. RetroArch reader ships with NES (`retroarch.cfg` + core remap + per-game remap).
Each new platform adds its emulator's reader once. Game layer and renderer never change per emulator.

### 5. Renderer (deterministic, no model)
game JSON ∘ mapping → labels at the anchors on the controller SVG → PNG per state. Cache key = game + controller + state
under `renders\`. Re-render on controller switch is instant.

### 6. Controls plugin + viewer (C#, `IGameMenuItemPlugin` + `IGameBadge`)
Right-click item "Generate and show controller mapping" + badge "has mapping". Viewer window: controller dropdown,
state tabs, the image. Missing → request through the bridge, progress line, render, show. Batch = whole platform from
the platform's menu.

## Milestones (acceptance = what the player sees on screen)
- **M0 Bridge.** LaunchBox starts → assistant session is up beside it. The assistant lists NES games through the bridge.
  Plugin shows a test image in the viewer on the assistant's command. Round-trip < 1 s.
- **M1 Controller.** Pad plugged in → plugin names it → the assistant renders the template → the user approves one preview.
- **M2 One game.** Right-click *Blaster Master* → controller image with on-foot and tank tabs; NES A shows on the
  physical button `retroarch.cfg` binds it to. Five more NES games checked by hand against their manuals.
- **M3 Batch NES.** Whole platform pre-rendered; badge on every mapped game; "no mapping found" count reported, never templated.
- **M4 Viewer polish.** Controller dropdown re-renders live; a second controller added by the onboarding flow alone.

## As built (0.2.0-beta)
- `src\PadMaster.Bridge`: the bridge plus the assistant chat panel (right panel, left sidebar or floating window).
  MCP over streamable HTTP on 127.0.0.1 (HttpListener, stdlib, token per start), generic `lb_*` tools over the
  LaunchBox plugin API, no deletion (refused in the plugin), bulk guard on large rewrites.
- `src\PadMaster.Settings`: the managed Plugin Manager package (Configure form).
- `src\PadMaster.Check`: the runnable self-check (`dotnet run --project src\PadMaster.Check -c Release`).
- `src\Directory.Build.props`: `LaunchBoxDir` (default `C:\LaunchBox`) locates `Core\Unbroken.LaunchBox.Plugins.dll`.
- `tools\make_package.py`: builds and zips the release.
- Controller mapping (M1-M4) is not built yet.
