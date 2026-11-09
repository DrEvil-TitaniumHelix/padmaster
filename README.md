# Pad Master (beta)

## What it is
Video: https://www.youtube.com/watch?v=qEOHQhiyElI

Pad Master draws each game's controls on a picture of the controller you actually hold. The labels come from the game's manual (or the web when there is no manual) and the button positions come from your emulator's own settings, so NES "A" lands on whichever physical button your emulator uses for it. Never guessed: a game with no trustworthy source says "No mapping found".

You see the picture:
- Right-click a game > Show controller mapping. One tab per in-game state (on foot, driving, menus).
- The PM badge marks every game that has a mapping.
- The pictures are saved as LaunchBox "Arcade - Controls Information" images, so any theme that shows that image type shows them.

The same plugin includes the Padmaster AI Assistant, a chat panel inside LaunchBox on your own AI subscription (Claude Code or Codex). It is what generates a missing mapping and adds your own controller, and you can also ask it about your games, find a manual, check which emulator a game uses, or have it build a playlist. This is a beta.

## Requirements
- Windows.
- LaunchBox 14.1.x only.
- Claude Code or Codex installed and signed in with your own subscription. The panel can start the sign-in for you.

## Install
1. Close LaunchBox first. A running LaunchBox locks the plugin's DLL during an update, and a reinstall over a running LaunchBox leaves the old plugin in memory until you restart it.
2. Unzip into your LaunchBox folder. It adds `Plugins\PadMaster`, `Local\Plugins\Padmaster AI Assistant`, and this README.md at the LaunchBox root.
3. Start LaunchBox. Nothing shows yet; that is by design.
4. Turn it on when you want it: Tools > Manage > Plugins > Padmaster AI Assistant > Configure, tick Enable, Save. Until then nothing runs. The chat panel does not open by itself; the first time, open Tools > Padmaster AI Assistant once and sign in there if asked.
5. Open the BADGES menu and tick "Pad Master controller mapping". Every game that has a map now shows a PM badge. (LaunchBox starts a new badge switched off.)
6. Right-click a game and choose Show controller mapping. A badged game shows its picture; any other game asks "Generate one now?".

The Plugin Manager toggle already reads ON before you tick Enable. Only the Enable box in Configure turns the plugin on. To turn it off, untick Enable and press Save.

The chat panel stays out of the way: Pad Master works with the assistant in the background (right-click pictures, badge, generating maps, adding controllers), and Tools > Padmaster AI Assistant opens the chat whenever you want it. Tick "Open the chat panel on its own" in Configure (or the panel's gear) if you want it open at every start.

## Controller pictures
- Right-click a game and choose Show controller mapping. A game with no map yet asks "Generate one now?": your assistant reads the game's manual first and the web after that, a minute or two, and the picture opens. If no trustworthy source exists it tells you so rather than guess. 44 games ship mapped (NES and PlayStation 2); every other game is one click away.
- Your controller: choose it in Configure > "Controller to draw" (shipped: GuliKit KK3 Max, Xbox Series Controller, PlayStation DualSense) or in the picture window's dropdown, which redraws and saves the choice. To add your own controller, pick "Add my controller..." in that dropdown: your assistant finds a picture of it and places the buttons; this needs the assistant signed in. The right-click item's text is set in Configure too ("Pad Master right-click menu text").
- Emulators read for button positions: RetroArch, PCSX2, Dolphin, PPSSPP, MAME, Cemu, Ryujinx, Xemu, Citra, Xenia, RPCS3. A game on another emulator says so and names these.
- The pictures are saved as "Arcade - Controls Information" images with a Pad Master marker, so any theme that shows that image type shows them; delete them and they are written again at the next start.

## Assistant
Tools > Padmaster AI Assistant opens the panel. Choose Claude Code or Codex under Your AI. If it is missing or not signed in, you see a setup card:
- Leave the CLI path blank to find the selected assistant automatically, or use Browse to pick its `.exe` or `.cmd` file.
- Click Sign in. The selected assistant's login opens in a console and your browser. The card moves on by itself when you finish. Switching assistants starts a fresh conversation with that assistant.

Using the AI chat assistant panel:
- Enter sends. Shift+Enter adds a line.
- The microphone button turns Windows dictation on and off. Dictated text goes into the box and is never sent by itself.
- The gear opens the settings: AI, CLI path, Port, Reset token, Placement, Open the chat panel on its own, Confirm bulk changes and Bulk threshold. Back to chat returns.
- The CLI path box shows the file found for the chosen AI, greyed. Leave it as it is to keep auto-detect, or use Browse. "Not found on this PC - click Browse" means that AI is not installed where the plugin looks.
- Placement: Right panel sits above the game details, Left sidebar sits below the sidebar, or use a Floating window.
- Tools > Padmaster AI Assistant reopens the chat.
- Configure has these same settings plus Enable, plus Pad Master's own two: menu text and controller.

## Hook in your AI

**Claude Code.** [Install Claude Code](https://code.claude.com/docs/en/overview), then use Sign in or `claude auth login --claudeai` with your Claude subscription. The plugin runs a continuing `claude -p` session with JSON input/output and the local LaunchBox connection. Read, Grep and Glob can read files; shell and file-editing tools are unavailable. Generating a mapping runs a separate `claude -p` in the manual's page folder with web search; it can read only that folder.

**Codex.** [Install Codex CLI](https://developers.openai.com/codex/cli/), then use Sign in or `codex login` with your ChatGPT subscription. The plugin runs `codex exec --json` for each message and `codex exec resume` for later messages, with a read-only sandbox, shell tools disabled, and the local LaunchBox connection approved. It uses Codex's default model and ignores your chosen model, extra MCP servers, hooks and rules. Codex can use the LaunchBox tools only: it cannot read emulator configurations or manual files, and it cannot generate mappings. API-key sign-ins are not accepted.

## Use your own Claude Code session
The plugin's local LaunchBox connection also works from a Claude Code session you start yourself, in any folder, while LaunchBox is open:
- Copy `Plugins\PadMaster\mcp.json` into your project as `.mcp.json`, or start with `claude --mcp-config <LaunchBox>\Plugins\PadMaster\mcp.json`.
- The token inside is private to this PC and stays the same across LaunchBox starts, so one copy keeps working. Your own session has none of the panel's locks: it can delete, and nothing waits for a Yes.
- If the token ever leaks, press Reset token in the panel's gear. mcp.json gets a new token, the panel restarts its session, and any copy you made stops working until you copy the file again.
- Never share `mcp.json`.

## What the assistant can do
Read and change games, playlists, platforms, emulators, images and manuals; launch games; refresh the view. It cannot delete anything: every removing or clearing call is refused inside the plugin, not by instruction. The one exception is taking games out of a playlist. It is told to say what it will change before changing it.

## IMPORTANT

> The assistant can change library data and launch games without a confirmation dialog. It cannot delete: removing games, playlists, platforms, emulators or anything else is locked in the plugin, and no setting turns it on. Changes are still real changes. It is told to ask first, but that is an instruction, not a lock. LaunchBox keeps automatic backups in Backups\\; make one before letting it loose on a library you care about.

Bulk rewrites, and taking more than 20 games out of a playlist at once (20 is the default), wait for your Yes in the panel; change or turn this off in Configure. This checks one change at a time, so many small changes in a row do not trigger it.

It has no tool for editing files directly; library changes go through LaunchBox's plugin API. Those changes are still real changes to your library.

## Privacy
- It uses your AI account and your subscription. The plugin never asks for a key and stores none. API-key and Console logins are refused by design.
- What you type and what the assistant reads goes to your AI provider. Claude Code's Read, Grep and Glob tools reach any file your Windows account can read, except `~/.claude`, `~/.ssh`, `~/.aws`, `~/.azure` and `~/.gnupg`. Generating a mapping sends the manual's pages and the game's title to your AI provider and searches the web. Codex has no file-reading tool in this plugin.
- The plugin's own server listens on 127.0.0.1 only and requires a token, kept in `Plugins\PadMaster\mcp.json` and in `Local\Plugins\.data\PadMaster`. It stays the same across starts until you press Reset token in the gear. The assistant cannot read or change that token through the LaunchBox tools. Codex receives that local token through its environment, never its command line. Your assistant connects to your AI provider, and some LaunchBox services the assistant can reach may contact their own providers.

## Uninstall
With LaunchBox closed, delete `Plugins\PadMaster` and `Local\Plugins\Padmaster AI Assistant`. To remove saved settings and the connection token too, delete `Local\Plugins\.data\70cf6f66-65ce-43a6-a6cb-00673c71558e` and `Local\Plugins\.data\PadMaster`. You can also delete this README.md from the LaunchBox root. The controller pictures stay in your images folder; they carry a Pad Master marker.

The Uninstall button in Plugin Manager is not enough: `Plugins\PadMaster` is separate and would keep loading.

## Beta feedback
Report bugs and ideas in GitHub issues: https://github.com/DrEvil-TitaniumHelix/padmaster/issues

For a bug report, attach `Plugins\PadMaster\padmaster.log`. It rotates to `padmaster.log.1` at 1 MB. Typed text is never logged, but it may hold local paths, so read it before sharing. Never share `mcp.json`.

## Credits
Author: DrEvil-TitaniumHelix. LaunchBox is by Unbroken Software. Claude Code is by Anthropic. Codex is by OpenAI. This plugin is independent of those companies. Controller photos: see `controllers\<pad>\source.txt` for each photo's author and licence.

## License
Copyright (c) 2026 DrEvil-TitaniumHelix

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
