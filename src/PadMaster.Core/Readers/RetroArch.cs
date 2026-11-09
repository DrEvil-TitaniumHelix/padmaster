using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PadMaster.Readers;

public static class RetroArch
{
    static readonly string[] XInput = ["A", "B", "X", "Y", "lb", "rb", "start", "back", "ls", "rs"];
    static readonly string[] Pad = ["b", "y", "select", "start", "up", "down", "left", "right", "a", "x", "l", "r", "l2", "r2", "l3", "r3"];
    const string D = ";Up=up;Down=down;Left=left;Right=right";
    const string Genesis = "A=y;B=b;C=a;X=l;Y=x;Z=r;Mode=select;Start=start" + D, Sms = "Button 1=b;Button 2=a;Start=start" + D, Pce = "I=a;II=b;III=y;IV=x;V=l;VI=r;Select=select;Run=start" + D, Nes = "A=a;B=b;Select=select;Start=start" + D, Gb = "A=a;B=b;Select=select;Start=start" + D;
    const string N64 = "A=b;B=y;L=l;R=r;Z=l2;Start=start" + D + ";Analog stick=ls;C-Up=rs;C-Down=rs;C-Left=rs;C-Right=rs", N64Alt = "A=b;B=y;C-Right=r;C-Left=l;C-Down=a;C-Up=x;Z=l2;R=r2;L=select;Start=start" + D + ";Analog stick=ls";

    // Console name = RetroPad id, from each core's own retro_input_descriptor table in its libretro source (github.com/libretro/<core>; flyinghead/flycast, stella-emu/stella, drhelius/Gearcoleco, hrydgard/ppsspp).
    static readonly Dictionary<string, (string Core, string Map)> Tables = new()
    {
        ["Nintendo Entertainment System"] = ("Nestopia", Nes), ["Nintendo Famicom Disk System"] = ("Nestopia", Nes),
        ["Super Nintendo Entertainment System"] = ("Snes9x", "A=a;B=b;X=x;Y=y;L=l;R=r;Select=select;Start=start" + D),
        ["Sony Playstation"] = ("SwanStation", "Cross=b;Circle=a;Square=y;Triangle=x;L1=l;R1=r;L2=l2;R2=r2;L3=l3;R3=r3;Select=select;Start=start" + D + ";Left stick=ls;Right stick=rs"),
        ["Sony PSP"] = ("PPSSPP", "Cross=b;Circle=a;Square=y;Triangle=x;L=l;R=r;Select=select;Start=start" + D + ";Left stick=ls"),
        ["Nintendo DS"] = ("DeSmuME", "A=a;B=b;X=x;Y=y;L=l;R=r;Select=select;Start=start" + D),
        ["Nintendo Game Boy"] = ("Gambatte", Gb), ["Nintendo Game Boy Color"] = ("Gambatte", Gb),
        ["Nintendo Game Boy Advance"] = ("VBA Next", "A=a;B=b;L=l;R=r;Select=select;Start=start" + D),
        ["Nintendo 64"] = ("Mupen64Plus-Next", N64),
        ["Nintendo GameCube"] = ("Dolphin", "A=a;B=b;X=x;Y=y;Z=r;L=l2;R=r2;Start=start" + D + ";Control Stick=ls;C-Stick=rs"),
        ["Nintendo Wii"] = ("Dolphin", "A=a;B=b;1=x;2=y;Plus=start;Minus=select;Home=r3" + D),
        ["Nintendo Virtual Boy"] = ("Beetle VB", "A=a;B=b;L=l;R=r;Select=select;Start=start;Left D-Pad Up=up;Left D-Pad Down=down;Left D-Pad Left=left;Left D-Pad Right=right;Right D-Pad Up=l2;Right D-Pad Down=l3;Right D-Pad Left=r2;Right D-Pad Right=r3"),
        ["Nintendo Pokemon Mini"] = ("PokeMini", "A=a;B=b;C=r;Shake=l;Power=select" + D),
        ["Sega Genesis"] = ("Genesis Plus GX", Genesis), ["Sega CD"] = ("Genesis Plus GX", Genesis), ["Sega 32X"] = ("PicoDrive", Genesis),
        ["Sega Master System"] = ("Genesis Plus GX", Sms), ["Sega Game Gear"] = ("Genesis Plus GX", Sms),
        ["Sega Saturn"] = ("Yabause", "A=b;B=a;C=r;X=y;Y=x;Z=l;L=l2;R=r2;Start=start" + D + ";Analog stick=ls"),
        ["Sega Dreamcast"] = ("Flycast", "A=b;B=a;X=y;Y=x;L=l2;R=r2;Start=start" + D + ";Analog stick=ls"),
        ["Sega Naomi"] = ("Flycast", "Button 1=b;Button 2=a;Button 3=y;Button 4=x;Button 5=r;Button 6=l;Button 7=r2;Button 8=l2;Start=start;Coin=select;Test=l3;Service=r3" + D),
        ["NEC TurboGrafx-16"] = ("Beetle PCE Fast", Pce), ["NEC TurboGrafx-CD"] = ("Beetle PCE Fast", Pce), ["PC Engine SuperGrafx"] = ("Beetle SuperGrafx", Pce),
        ["NEC PC-FX"] = ("Beetle PC-FX", "I=a;II=b;III=x;IV=y;V=l;VI=r;Select=select;Run=start" + D),
        ["WonderSwan"] = ("Beetle WonderSwan", Swan), ["WonderSwan Color"] = ("Beetle WonderSwan", Swan),
        ["SNK Neo Geo Pocket"] = ("Beetle NeoPop", "A=b;B=a;Option=start" + D), ["SNK Neo Geo Pocket Color"] = ("Beetle NeoPop", "A=b;B=a;Option=start" + D),
        ["Atari 2600"] = ("Stella", "Fire=b;Trigger=a;Booster=y;Reset=start;Select=select" + D),
        ["Atari 5200"] = ("a5200", "Fire 1=a;Fire 2=b;Pause=select;Start=start;Keypad #=x;Keypad *=y;Keypad 0=r;Keypad 1=r2;Keypad 3=l2;Keypad 5=r3;Keypad 7=l3" + D),
        ["Atari 7800"] = ("ProSystem", "Button 1=b;Button 2=a;Reset=x;Select=select;Pause=start" + D),
        ["Atari Lynx"] = ("Handy", "A=a;B=b;Option 1=l;Option 2=r;Pause=start" + D),
        ["Atari Jaguar"] = ("Virtual Jaguar", "A=a;B=b;C=y;Pause=select;Option=start;Keypad 0=x;Keypad 1=l;Keypad 2=r;Keypad 3=l2;Keypad 4=r2;Keypad 5=l3;Keypad 6=r3" + D),
        ["ColecoVision"] = ("Gearcoleco", "Left Fire=b;Right Fire=a;Keypad 1=y;Keypad 2=x;Keypad 3=l;Keypad 4=r;Keypad 5=l2;Keypad 6=r2;Keypad 7=l3;Keypad 8=r3;Keypad *=start;Keypad #=select" + D),
        ["GCE Vectrex"] = ("vecx", "Button 1=a;Button 2=b;Button 3=x;Button 4=y" + D),
        ["Magnavox Odyssey 2"] = ("O2EM", "Action=b" + D), ["Philips Videopac+"] = ("O2EM", "Action=b" + D),
        ["3DO Interactive Multiplayer"] = ("Opera", "A=y;B=b;C=a;L=l;R=r;X=select;P=start" + D),
    };
    const string Swan = "A=a;B=b;Start=start;X Cursor Up=up;X Cursor Down=down;X Cursor Left=left;X Cursor Right=right;Y Cursor Up=r2;Y Cursor Down=l2;Y Cursor Left=l;Y Cursor Right=r";
    static readonly HashSet<string> Keyboard = ["MS-DOS", "Commodore 64", "Commodore 128", "Commodore PET", "Commodore Plus 4", "Commodore VIC-20", "Commodore Amiga", "Commodore Amiga CD32", "Atari ST", "Atari 800", "Amstrad CPC", "Microsoft MSX", "Sinclair ZX Spectrum", "Sinclair ZX-81", "Apple II", "NEC PC-8801", "NEC PC-9801"];

    public static (string App, string CommandLine)? FromXml(string launchBoxRoot, string platform)
    {
        var doc = XDocument.Load(Path.Combine(launchBoxRoot, "Data", "Emulators.xml")).Root!;
        var ep = doc.Elements("EmulatorPlatform").Where(e => (string?)e.Element("Platform") == platform).OrderByDescending(e => (string?)e.Element("Default") == "true").FirstOrDefault();
        var app = ep == null ? null : (string?)doc.Elements("Emulator").FirstOrDefault(e => (string?)e.Element("ID") == (string?)ep.Element("Emulator"))?.Element("ApplicationPath");
        return string.IsNullOrEmpty(app) ? null : (Path.GetFullPath(Path.Combine(launchBoxRoot, app)), (string?)ep!.Element("CommandLine") ?? "");
    }

    static Dictionary<string, string> Cfg(string path, Dictionary<string, string>? into = null)
    {
        into ??= new();
        if (!File.Exists(path)) return into;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim(); var i = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || i < 0) continue;
            into[line[..i].Trim()] = line[(i + 1)..].Trim().Trim('"');
        }
        return into;
    }

    static string? First(IEnumerable<string?> paths) => paths.FirstOrDefault(p => p != null && File.Exists(p));

    static string? AxisKey(string? v) => v == null || !Regex.IsMatch(v, @"^[+-]\d$") ? null : v[1] switch { '0' or '1' => "ls", '2' or '3' => "rs", '4' => "lt", '5' => "rt", _ => null };

    public static Dictionary<string, string> Read(string retroarchExe, string commandLine, string? romPath, string platform) => Full(retroarchExe, commandLine, romPath, platform).Bindings;

    public static ReadResult Full(string retroarchExe, string commandLine, string? romPath, string platform)
    {
        var result = new Dictionary<string, string>();
        ReadResult Bad(string error, string? config = null) => new(result, [], error, config);
        if (!Path.GetFileNameWithoutExtension(retroarchExe).Equals("retroarch", StringComparison.OrdinalIgnoreCase)) return Bad("not RetroArch");
        if (Keyboard.Contains(platform)) return Bad(platform + " is keyboard-driven: the game decides, no fixed pad layout to read");
        var dir = Path.GetDirectoryName(Path.GetFullPath(retroarchExe))!;
        var config = First([Path.Combine(dir, "retroarch.cfg"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroArch", "retroarch.cfg")]);
        var cfg = Cfg(config ?? "");
        if (cfg.Count == 0) return Bad("retroarch.cfg not found");
        if (cfg.GetValueOrDefault("input_joypad_driver", "xinput") != "xinput") return Bad("input_joypad_driver is not xinput", config);
        string Abs(string v) => Path.GetFullPath(v.StartsWith(':') ? dir + v[1..] : v);
        string Dir(string key, string def) { var v = cfg.GetValueOrDefault(key, def); return Abs(v is "" or "default" ? def : v); }
        var lib = Regex.Match(commandLine, "-L\\s+(?:\"([^\"]+)\"|(\\S+))");
        var stem = lib.Success ? Path.GetFileNameWithoutExtension(lib.Groups[1].Success ? lib.Groups[1].Value : lib.Groups[2].Value) : null;
        var core = stem == null ? null : Cfg(Path.Combine(Dir("libretro_info_path", @":\info"), stem + ".info")).GetValueOrDefault("corename");
        if (core == null) return Bad($"core {stem ?? "(none in command line)"} is not installed", config);
        if (!Tables.TryGetValue(platform, out var table) || table.Core != core) return Bad($"no sourced button table for {core} on {platform}", config);
        var game = romPath == null ? null : Path.GetFileNameWithoutExtension(romPath);
        var folder = romPath == null ? null : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(romPath)));
        var configDir = Path.Combine(Dir("rgui_config_directory", @":\config"), core);
        foreach (var name in new[] { core, folder, game }) if (name != null) Cfg(Path.Combine(configDir, name + ".cfg"), cfg);
        var autoDir = Path.Combine(Dir("joypad_autoconfig_dir", @":\autoconfig"), "xinput");
        var auto = cfg.GetValueOrDefault("input_autodetect_enable", "true") == "true" && Directory.Exists(autoDir)
            ? Directory.EnumerateFiles(autoDir, "*.cfg").Select(p => Cfg(p)).FirstOrDefault(c => c.GetValueOrDefault("input_device") == "XInput Controller") ?? new() : new();
        var remap = Enumerable.Range(0, Pad.Length).ToArray();
        var rmp = cfg.GetValueOrDefault("input_remap_binds_enable", "true") == "true" ? First(new[] { game, folder, core }.Select(n => n == null ? null : Path.Combine(Dir("input_remapping_directory", @":\config\remaps"), core, n + ".rmp"))) : null;
        var device = cfg.GetValueOrDefault("input_libretro_device_p1", "1");
        if (rmp != null) { var r = Cfg(rmp); device = r.GetValueOrDefault("input_libretro_device_p1", device); for (var j = 0; j < Pad.Length; j++) if (int.TryParse(r.GetValueOrDefault($"input_player1_btn_{Pad[j]}"), out var v)) remap[j] = v; }
        if (platform == "Nintendo Wii" && device != "1") return Bad($"Wii controller type is device {device}, not the upright Wiimote this table is sourced for", config);
        var opt = cfg.GetValueOrDefault("global_core_options") == "true" ? null : First(new[] { game, folder, core }.Select(n => n == null ? null : Path.Combine(configDir, n + ".opt")));
        opt ??= cfg.GetValueOrDefault("core_options_path") is { Length: > 0 } cop ? Abs(cop) : Path.Combine(dir, "retroarch-core-options.cfg");
        var options = Cfg(opt);
        var shifted = options.GetValueOrDefault("nestopia_button_shift") == "enabled";
        var map = platform == "Nintendo 64" && options.GetValueOrDefault("mupen64plus-alt-map", "False") != "False" ? N64Alt : table.Map;
        string? Stick(string side) => AxisKey(cfg.GetValueOrDefault($"input_player1_{side}_x_plus_axis") is { Length: > 0 } a && a != "nul" ? a : auto.GetValueOrDefault($"input_{side}_x_plus_axis"));
        foreach (var part in map.Split(';'))
        {
            var (name, token) = (part[..part.IndexOf('=')], part[(part.IndexOf('=') + 1)..]);
            if (token is "ls" or "rs") { if (Stick(token[0..1]) is { } stick) result[name] = stick; continue; }
            var id = Array.IndexOf(Pad, token);
            var coreId = shifted && name is "A" or "B" ? (name == "A" ? 0 : 1) : id;
            var j = remap[coreId] == coreId ? coreId : Array.IndexOf(remap, coreId);
            if (j < 0) continue;
            var bind = cfg.GetValueOrDefault($"input_player1_{Pad[j]}_btn", "nul");
            if (bind == "nul") bind = auto.GetValueOrDefault($"input_{Pad[j]}_btn", "nul");
            var key = bind.StartsWith("h0") ? "dpad_" + bind[2..] : int.TryParse(bind, out var b) && b < XInput.Length ? XInput[b] : null;
            if (key == null) { var axis = cfg.GetValueOrDefault($"input_player1_{Pad[j]}_axis", "nul"); key = AxisKey(axis != "nul" ? axis : auto.GetValueOrDefault($"input_{Pad[j]}_axis")); }
            if (key != null) result[name] = key;
        }
        return new(result, [$"console names from the {core} core's input descriptors"], null, config);
    }
}
