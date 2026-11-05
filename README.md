# Padmaster AI Assistant (beta)

## What it is
A chat panel inside LaunchBox, powered by your own AI subscription. Ask about your games, find a manual, check which emulator a game uses, or have it build a playlist from your library. Type or use the microphone. This is version 0.2.0-beta, an early test release.

## Requirements
- Windows.
- LaunchBox 14.1.x only.
- Claude Code or Codex installed and signed in with your own subscription. The panel can start the sign-in for you.

## Install
1. Close LaunchBox first. A running LaunchBox locks the plugin's DLL during an update, and a reinstall over a running LaunchBox leaves the old plugin in memory until you restart it.
2. Download `Padmaster-AI-Assistant-0.2.0-beta.zip` from the [Releases page](https://github.com/DrEvil-TitaniumHelix/padmaster/releases) and unzip it into your LaunchBox folder. It adds `Plugins\PadMaster`, `Local\Plugins\Padmaster AI Assistant`, and this README.md at the LaunchBox root.
3. Start LaunchBox. Nothing shows yet. That is by design, not a failed install.
4. Go to Tools > Manage > Plugins > Padmaster AI Assistant > Configure, tick Enable, then press Save.

The Plugin Manager toggle already reads ON before you tick Enable. Only the Enable box in Configure turns the assistant on. To turn it off, untick Enable and press Save.

## First run
The panel opens. Choose Claude Code or Codex under Your AI. If it is missing or not signed in, you see a setup card:
- Leave the CLI path blank to find the selected assistant automatically, or use Browse to pick its `.exe` or `.cmd` file.
- Click Sign in. The selected assistant's login opens in a console and your browser. The card moves on by itself when you finish. Switching assistants starts a fresh conversation with that assistant.

Using the panel:
- Enter sends. Shift+Enter adds a line.
- The microphone button turns Windows dictation on and off. Dictated text goes into the box and is never sent by itself.
- The gear opens the settings: AI, CLI path, Port, Placement, Open at startup, Confirm bulk changes and Bulk threshold. Back to chat returns.
- Placement: Right panel sits above the game details, Left sidebar sits below the sidebar, or use a Floating window.
- Tools > Padmaster AI Assistant reopens the chat.
- Configure has these same settings plus Enable.

## Hook in your AI

**Claude Code.** [Install Claude Code](https://code.claude.com/docs/en/overview), then use Sign in or `claude auth login --claudeai` with your Claude subscription. The plugin runs a continuing `claude -p` session with JSON input/output and the local LaunchBox connection. Read, Grep and Glob can read files; shell and file-editing tools are unavailable.

**Codex.** [Install Codex CLI](https://developers.openai.com/codex/cli/), then use Sign in or `codex login` with your ChatGPT subscription. The plugin runs `codex exec --json` for each message and `codex exec resume` for later messages, with a read-only sandbox, shell tools disabled, and the local LaunchBox connection approved. It uses Codex's default model and ignores your chosen model, extra MCP servers, hooks and rules. Codex can use the LaunchBox tools only: it cannot read emulator configurations or manual files. API-key sign-ins are not accepted.

## What it can do
Read and change games, playlists, platforms, emulators, images and manuals; launch games; refresh the view. It cannot delete anything: every removing or clearing call is refused inside the plugin, not by instruction. The one exception is taking games out of a playlist. It is told to say what it will change before changing it.

## IMPORTANT

> The assistant can change library data and launch games without a confirmation dialog. It cannot delete: removing games, playlists, platforms, emulators or anything else is locked in the plugin, and no setting turns it on. Changes are still real changes. It is told to ask first, but that is an instruction, not a lock. LaunchBox keeps automatic backups in Backups\\; make one before letting it loose on a library you care about.

Bulk rewrites, and taking more than 20 games out of a playlist at once (20 is the default), wait for your Yes in the panel; change or turn this off in Configure. This checks one change at a time, so many small changes in a row do not trigger it.

It has no tool for editing files directly; library changes go through LaunchBox's plugin API. Those changes are still real changes to your library.

## Privacy
- It uses your AI account and your subscription. The plugin never asks for a key and stores none. API-key and Console logins are refused by design.
- What you type and what the assistant reads goes to your AI provider. Claude Code's Read, Grep and Glob tools reach any file your Windows account can read, except `~/.claude`, `~/.ssh`, `~/.aws`, `~/.azure` and `~/.gnupg`. Codex has no file-reading tool in this plugin.
- The plugin's own server listens on 127.0.0.1 only and requires a token, made new at each start and kept in `Plugins\PadMaster\mcp.json`. Codex receives that local token through its environment, never its command line. Your assistant connects to your AI provider, and some LaunchBox services the assistant can reach may contact their own providers.

## Known limits
- Claude Code and Codex are supported. Codex uses its default model and cannot read emulator configurations or manual files.
- LaunchBox 14.1.x only. LaunchBox 14.2 will refuse the Plugin Manager part until an update.
- The right panel needs a selected game; otherwise a floating window appears.
- The playlist sidebar needs the flat Platforms view. A Platform Categories sidebar will not show a new playlist unless it has a Category set.
- Slash commands are not available.
- Replies may take a while: each message goes through your AI.
- The microphone needs a Windows speech recognizer. "mic unavailable" means no usable recognizer or microphone was found.
- Automatic controller mapping is not built yet.

## Build from source
Needs Windows, the .NET 10 SDK, Python 3 and a LaunchBox 14.1.x install. The build references `Core\Unbroken.LaunchBox.Plugins.dll` from your LaunchBox folder, set by the MSBuild property `LaunchBoxDir` (default `C:\LaunchBox`, see `src\Directory.Build.props`). If LaunchBox lives elsewhere, pass `-p:LaunchBoxDir=D:\LaunchBox`, set a `LaunchBoxDir` environment variable, or create the git-ignored `src\Directory.Build.local.props`:

```
<Project><PropertyGroup><LaunchBoxDir>D:\LaunchBox</LaunchBoxDir></PropertyGroup></Project>
```

Then:

```
dotnet build src/PadMaster.sln -c Release
dotnet run --project src/PadMaster.Check -c Release
python tools/make_package.py
```

`make_package.py` writes the release zip to `dist\`. The code is dense and comment-free on purpose; see CONVENTION.md and DESIGN.md.

## Uninstall
With LaunchBox closed, delete `Plugins\PadMaster` and `Local\Plugins\Padmaster AI Assistant`. To remove saved settings too, delete `Local\Plugins\.data\70cf6f66-65ce-43a6-a6cb-00673c71558e`. You can also delete this README.md from the LaunchBox root.

The Uninstall button in Plugin Manager is not enough: `Plugins\PadMaster` is separate and would keep loading.

## Beta feedback
Report bugs and ideas in GitHub issues: https://github.com/DrEvil-TitaniumHelix/padmaster/issues

For a bug report, attach `Plugins\PadMaster\padmaster.log`. It rotates to `padmaster.log.1` at 1 MB. Typed text is never logged, but it may hold local paths, so read it before sharing. Never share `mcp.json`.

## Credits
Author: DrEvil-TitaniumHelix. LaunchBox is by Unbroken Software. Claude Code is by Anthropic. Codex is by OpenAI. This plugin is independent of those companies.

## License
Copyright (c) 2026 DrEvil-TitaniumHelix

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
