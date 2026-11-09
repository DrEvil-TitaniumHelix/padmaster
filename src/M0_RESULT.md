# M0 bridge — build result (2026-11-03)

## Build
`dotnet build src\PadMaster.sln -c Release` → Build succeeded, 0 Warning(s), 0 Error(s).
One fix during build: WPF projects' implicit usings omit System.IO → added `using System.IO;` to Mcp.cs.
.NET 10 `dotnet new sln` defaults to .slnx; used `--format sln` to get PadMaster.sln as specified.

## Check
`dotnet run --project src\PadMaster.Check -c Release` →
```
OK  tools/call round-trip 1.0 ms
exit=0
```
Check runs Mcp on 127.0.0.1:47801 (not 47800, so it never collides with a live LaunchBox) and asserts:
initialize (200, serverInfo.name=launchbox, protocolVersion echoed 2025-06-18, id echoed) · notifications/initialized → 202 ·
tools/list (1 tool) · tools/call ok (isError false, text parses) · tools/call handler throw → isError true + message ·
unknown method → -32601 · ping → {} · GET → 405.
No PC Matic block: HttpListener on 127.0.0.1 bound without a urlacl reservation, Check exe ran.

## Deployed
E:\LaunchBox\Plugins\PadMaster\PadMaster.Bridge.dll (24 KB) + PadMaster.Bridge.deps.json. Unbroken.LaunchBox.Plugins.dll NOT copied (Private=false).
Log file will appear at E:\LaunchBox\Plugins\PadMaster\padmaster.log on first LaunchBox start.

## API names confirmed from decompiled Unbroken.LaunchBox.Plugins.dll (re-decompiled with ilspycmd; scratchpad had been wiped)
- ImageDetails: FilePath, ImageType, Region (class, ctor(filePath, imageType, region))
- IPlatform.Name; IEmulatorPlatform: Platform, CommandLine (string?), IsDefault, EmulatorId
- IEmulator: Id, Title, ApplicationPath, CommandLine, GetAllEmulatorPlatforms()
- IGame: Id, Title, Platform, ApplicationPath, EmulatorId, ManualPath, GetManualPath(), FrontImagePath, ScreenshotImagePath, GetAllImagesWithDetails()
- IDataManager: GetAllGames(), GetGameById(string?), GetAllEmulators(), GetEmulatorById(string), GetAllPlatforms()
- SystemEventTypes.LaunchBoxStartupCompleted / LaunchBoxShutdownBeginning (namespace Unbroken.LaunchBox.Plugins.Data)
- PluginHelper.DataManager (namespace Unbroken.LaunchBox.Plugins)
- LaunchBox runtimeconfig: net10.0, Microsoft.NETCore.App + Microsoft.WindowsDesktop.App 10.0.14 (WPF available in-process)

## Decisions worth knowing
- Claude launch = `cmd.exe /k "%USERPROFILE%\.local\bin\claude.exe"` (full path; LaunchBox's inherited PATH may not include .local\bin). Skipped when `.nolaunch` exists in the repo root.
- game/emulator `commandLine` = platform-specific CommandLine when non-blank, else emulator CommandLine (LaunchBox stores "" for "use default", so `??` alone would return "").
- `emulator` tool: among emulators listing the platform, prefers one with IsDefault for that platform.
- show_image reuses one Window; BitmapImage loads with OnLoad + IgnoreImageCache so a re-rendered PNG at the same path refreshes. No Activate() (no focus steal beyond the window appearing).
- Repo root is NOT a git repo (no .git); nothing to commit anyway.

## Files
src\PadMaster.sln · src\PadMaster.Bridge\{PadMaster.Bridge.csproj, Mcp.cs, Plugin.cs} · src\PadMaster.Check\{PadMaster.Check.csproj, Program.cs} · .mcp.json · .gitignore · DESIGN.md (+1 line under Layout)
