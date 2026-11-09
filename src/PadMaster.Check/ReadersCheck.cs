using System.IO;
using System.Xml.Linq;
using PadMaster.Readers;

static class ReadersCheck
{
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("readers: " + message); }
    static string Show(ReadResult r) => r.Error != null ? "ERROR " + r.Error : string.Join(" ", r.Bindings.OrderBy(b => b.Key).Select(b => $"{b.Key}->{b.Value}"));

    public static void Run()
    {
        Fixtures();
        var root = LaunchBoxDir.Value; var doc = XDocument.Load(Path.Combine(root, "Data", "Emulators.xml")).Root!;
        var ra = doc.Elements("Emulator").First(e => (string?)e.Element("Title") == "RetroArch"); var exe = Path.GetFullPath((string)ra.Element("ApplicationPath")!, root);
        int good = 0, honest = 0;
        foreach (var ep in doc.Elements("EmulatorPlatform").Where(e => (string?)e.Element("Emulator") == (string?)ra.Element("ID")).OrderBy(e => (string?)e.Element("Platform")))
        {
            var platform = (string)ep.Element("Platform")!; var read = RetroArch.Full(exe, (string?)ep.Element("CommandLine") ?? "", null, platform);
            Require(read.Error != null || read.Bindings.Count > 0, platform + ": neither bindings nor error");
            if (read.Error == null) good++; else honest++;
            Console.WriteLine($"    retroarch {platform}: {Show(read)}");
        }
        Require(good >= 30, $"only {good} RetroArch platforms read");
        var nes = RetroArch.FromXml(root, "Nintendo Entertainment System")!.Value; var n = RetroArch.Full(nes.App, nes.CommandLine, null, "Nintendo Entertainment System");
        Require(n.Bindings.Count == 8 && n.Bindings["A"] == "B" && n.Bindings["B"] == "A" && n.Bindings["Start"] == "start" && n.Bindings["Select"] == "back", "NES changed: " + Show(n));
        var msdos = RetroArch.Full(exe, "-L \"cores\\dosbox_pure_libretro.dll\" -f", null, "MS-DOS"); Require(msdos.Error?.Contains("keyboard") == true, "MS-DOS must be an honest keyboard error");
        Console.WriteLine($"OK  retroarch real: {good} platforms read, {honest} honest errors");
        void Real(string name, string path, string platform, string? rom, bool expectError = false)
        {
            if (!File.Exists(path)) { Console.WriteLine($"SKIP {name}: not installed"); return; }
            var read = Select.ForExe(path, "", rom, platform);
            Require(expectError ? read.Error != null && read.Bindings.Count == 0 : read.Error == null && read.Bindings.Count > 0, $"{name}: {Show(read)}");
            Console.WriteLine($"OK  {name} ({read.Bindings.Count}) {read.Config}: {Show(read)}" + (read.Notes.Count > 0 ? " | notes: " + string.Join("; ", read.Notes.Take(4)) : ""));
        }
        foreach (var platform in new[] { "Super Nintendo Entertainment System", "Sony Playstation", "Sega Genesis", "Nintendo 64", "Nintendo Game Boy Advance" })
        {
            var file = Path.Combine(root, "Data", "Platforms", platform + ".xml");
            var title = XDocument.Load(file).Root!.Elements("Game").Where(g => (string?)g.Element("Emulator") == (string?)ra.Element("ID")).Select(g => (string?)g.Element("Title")).First(g => g != null)!;
            var read = Select.Read(root, platform, title);
            Require(read.Error == null && read.Bindings.Count > 0, $"{platform}/{title}: {Show(read)}");
            Console.WriteLine($"OK  real game {platform} / {title}: {read.Bindings.Count} bindings");
        }
        Real("mame", @"E:\LaunchBox\Emulators\MAME 0.230\mame.exe", "Arcade", @"D:\Arcade\System roms\MAME\roms\ddragon2u.zip");
        var d = Mame.Key("JOYCODE_1_YAXIS_UP_SWITCH OR JOYCODE_1_DPADUP"); Require(d == "dpad_up", "mame prefers the d-pad: " + d);
        Real("citron", @"D:\Arcade\Emulators\Citron-Windows-Canary-Refresh_0.6.1\citron.exe", "Nintendo Switch", @"G:\Nintendo Switch\Old School Musical.nsp");
        Real("xenia", @"D:\Arcade\Emulators\Xenia\xenia.exe", "Microsoft Xbox 360", null);
        Real("rpcs3", @"D:\Arcade\Emulators\rpcs3-v0.0.31-16206-77485747_win64\rpcs3.exe", "Sony Playstation 3", @"K:\PS3\Fracture_(USA).7z");
        Real("citra", @"D:\Arcade\Emulators\Citra3\canary-mingw\citra-qt.exe", "Nintendo 3DS", null);
        Real("xemu", @"D:\Arcade\Emulators\Xemu\xemu.exe", "Microsoft Xbox", null);
        Real("cemu", @"D:\Arcade\Emulators\Cemu\Cemu.exe", "Nintendo Wii U", null);
        Real("scummvm", @"E:\LaunchBox\Emulators\ScummVM\scummvm.exe", "ScummVM", null, expectError: true);
        Real("teknoparrot xinput", @"D:\Arcade\Emulators\TeknoParrot\TeknoParrotUi.exe", "Teknoparrot", @"D:\Arcade\Emulators\TeknoParrot\UserProfiles\acedriv3.xml");
        Real("teknoparrot directinput", @"D:\Arcade\Emulators\TeknoParrot\TeknoParrotUi.exe", "Teknoparrot", @"D:\Arcade\Emulators\TeknoParrot\UserProfiles\Tekken7.xml", expectError: true);
        Real("ppsspp", @"D:\Arcade\Emulators\ppsspp\PPSSPPWindows64.exe", "Sony PSP Minis", null);
        Real("shadps4", @"E:\LaunchBox\Emulators\shadPS4-0.21.0\shadPS4.exe", "Sony Playstation 4", null);
    }

    static void Fixtures()
    {
        var t = Path.Combine(Path.GetTempPath(), "padmaster_readers_" + Guid.NewGuid().ToString("N"));
        string W(string rel, string text) { var p = Path.Combine(t, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); return p; }
        try
        {
            var ra = W(@"ra\retroarch.exe", "");
            W(@"ra\retroarch.cfg", "input_joypad_driver = \"xinput\"\ninput_player1_a_btn = \"1\"\ninput_player1_b_btn = \"0\"\ninput_player1_l2_axis = \"+4\"\ninput_player1_l_x_plus_axis = \"+0\"\ninput_player1_up_btn = \"h0up\"\n");
            W(@"ra\info\swanstation_libretro.info", "corename = \"SwanStation\"\n"); W(@"ra\info\mupen64plus_next_libretro.info", "corename = \"Mupen64Plus-Next\"\n"); W(@"ra\info\snes9x_libretro.info", "corename = \"Snes9x\"\n");
            var psx = RetroArch.Full(ra, "-L \"cores\\swanstation_libretro.dll\"", null, "Sony Playstation");
            Require(psx.Bindings["Cross"] == "A" && psx.Bindings["Circle"] == "B" && psx.Bindings["L2"] == "lt" && psx.Bindings["Left stick"] == "ls" && psx.Bindings["Up"] == "dpad_up", "retroarch PS1 fixture " + Show(psx));
            W(@"ra\retroarch-core-options.cfg", "mupen64plus-alt-map = \"True\"\n");
            var n64 = RetroArch.Full(ra, "-L \"cores\\mupen64plus_next_libretro.dll\"", null, "Nintendo 64"); Require(n64.Bindings.GetValueOrDefault("C-Down") == "B" && n64.Bindings.GetValueOrDefault("A") == "A", "retroarch N64 alt-map fixture " + Show(n64));
            Require(RetroArch.Full(ra, "-L \"cores\\snes9x_libretro.dll\"", null, "Sega Genesis").Error?.Contains("no sourced") == true, "core/platform mismatch must error");

            var mame = W(@"mame\mame.exe", ""); W(@"mame\mame.ini", "joystick                  1\njoystickprovider          auto\ncfg_directory             cfg\n");
            W(@"mame\cfg\default.cfg", "<mameconfig version=\"10\"><system name=\"default\"><input><port type=\"P1_BUTTON1\"><newseq type=\"standard\">KEYCODE_A OR JOYCODE_1_BUTTON3</newseq></port><port type=\"P1_START\"><newseq type=\"standard\">JOYCODE_1_BUTTON7</newseq></port></input></system></mameconfig>");
            W(@"mame\cfg\game.cfg", "<mameconfig version=\"10\"><system name=\"game\"><input><port tag=\":IN\" type=\"P1_BUTTON1\" mask=\"1\" defvalue=\"0\"><newseq type=\"standard\">JOYCODE_1_RZAXIS_POS_SWITCH</newseq></port></input></system></mameconfig>");
            var m = Mame.Read(mame, @"x\game.zip"); Require(m.Bindings["Button 1"] == "lt" && m.Bindings["Start"] == "start" && m.Bindings["Button 2"] == "B" && m.Notes.Any(x => x.Contains("inpttype")), "mame fixture " + Show(m));

            var citron = W(@"citron\citron.exe", ""); W(@"citron\user\config\qt-config.ini", "[Controls]\nplayer_0_button_a=\"engine:sdl,port:0,guid:x,button:1\"\nplayer_0_button_zl=\"engine:sdl,port:0,guid:x,axis:2,threshold:0.5,invert:+\"\nplayer_0_button_dup=\"engine:sdl,port:0,guid:x,hat:0,direction:up\"\nplayer_0_lstick=\"axis_x:0,engine:sdl,axis_y:1\"\n");
            var ci = Citron.Read(citron); Require(ci.Bindings["A"] == "B" && ci.Bindings["ZL"] == "lt" && ci.Bindings["Up"] == "dpad_up" && ci.Bindings["Left stick"] == "ls", "citron fixture " + Show(ci));

            var citra = W(@"citra\citra-qt.exe", ""); W(@"citra\user\config\qt-config.ini", "[Controls]\nbutton_a=\"engine:keyboard,code:90\"\nprofile=0\nprofiles\\1\\button_a=\"button:0,engine:sdl,guid:x,port:0\"\nprofiles\\1\\button_zl=\"axis:4,direction:+,engine:sdl,guid:x,port:0,threshold:0.5\"\nprofiles\\1\\button_up=\"direction:up,engine:sdl,guid:x,hat:0,port:0\"\nprofiles\\1\\c_stick=\"down:axis$03$1direction$0+$1engine$0sdl,engine:analog_from_button\"\n");
            var cr = Citra.Read(citra); Require(cr.Bindings["A"] == "A" && cr.Bindings["ZL"] == "lt" && cr.Bindings["Up"] == "dpad_up" && cr.Bindings["C-Stick"] == "rs", "citra fixture " + Show(cr));

            var xenia = W(@"xenia\xenia.exe", ""); var xt = W(@"xenia\xenia.config.toml", "[HID]\nhid = \"winkey\"\n"); Require(Xenia.Read(xenia).Error != null, "xenia winkey must error"); File.WriteAllText(xt, "hid = \"xinput\"  # x\n"); Require(Xenia.Read(xenia).Bindings["RT"] == "rt", "xenia fixture");

            var rp = W(@"rp\rpcs3.exe", ""); W(@"rp\config\input_configs\active_input_configurations.yml", "Active Configurations:\n  global: Default\n");
            W(@"rp\config\input_configs\global\Default.yml", "Player 1 Input:\n  Handler: XInput\n  Config:\n    Cross: B\n    L2: LT\n    Up: Up\n    Left Stick Left: LS X-\n    Left Stick Right: LS X+\n    Left Stick Up: LS Y+\n    Left Stick Down: LS Y-\nPlayer 2 Input:\n  Handler: \"Null\"\n  Config:\n    Cross: A\n");
            var rr = Rpcs3.Read(rp, null); Require(rr.Bindings["Cross"] == "B" && rr.Bindings["L2"] == "lt" && rr.Bindings["Up"] == "dpad_up" && rr.Bindings["Left stick"] == "ls", "rpcs3 fixture " + Show(rr));

            var xe = W(@"xemu\xemu.exe", ""); var xmt = W(@"xemu\xemu.toml", "[input.bindings]\nport1_driver = 'usb-xbox-gamepad'\nport1 = 'abc'\n"); Require(Xemu.Read(xe).Bindings["White"] == "lb", "xemu fixture"); File.WriteAllText(xmt, "gamepad_mappings = [\n    { gamepad_id = 'abc', controller_mapping = { a = 1 } }\n]\nport1_driver = 'usb-xbox-gamepad'\nport1 = 'abc'\n"); Require(Xemu.Read(xe).Error?.Contains("custom") == true, "xemu custom map must error");

            var cemu = W(@"cemu\Cemu.exe", ""); W(@"cemu\controllerProfiles\controller0.xml", "<emulated_controller><type>Wii U GamePad</type><controller><api>XInput</api><uuid>0</uuid><mappings><entry><mapping>1</mapping><button>13</button></entry><entry><mapping>7</mapping><button>42</button></entry><entry><mapping>17</mapping><button>39</button></entry></mappings></controller></emulated_controller>");
            var ce = Cemu.Read(cemu); Require(ce.Bindings["A"] == "B" && ce.Bindings["ZL"] == "lt" && ce.Bindings["Left stick"] == "ls", "cemu fixture " + Show(ce));

            var tk = W(@"tp\TeknoParrotUi.exe", ""); var prof = W(@"tp\UserProfiles\g.xml", "<GameProfile><ConfigValues><FieldInformation><FieldName>Input API</FieldName><FieldValue>XInput</FieldValue></FieldInformation></ConfigValues><JoystickButtons><JoystickButtons><ButtonName>Jump</ButtonName><XInputButton><ButtonCode>8192</ButtonCode><IsButton>true</IsButton><XInputIndex>0</XInputIndex></XInputButton></JoystickButtons><JoystickButtons><ButtonName>Gas</ButtonName><XInputButton><IsRightTrigger>true</IsRightTrigger><IsButton>false</IsButton><XInputIndex>0</XInputIndex></XInputButton></JoystickButtons></JoystickButtons></GameProfile>");
            var tp = TeknoParrot.Read(tk, prof); Require(tp.Bindings["Jump"] == "B" && tp.Bindings["Gas"] == "rt", "teknoparrot fixture " + Show(tp));

            var pp = W(@"pp\PPSSPPWindows64.exe", ""); W(@"pp\memstick\PSP\SYSTEM\controls.ini", "[ControlMapping]\nCross = 1-59,20-96,10-191\nL = 20-4034\nUp = 20-19\nAn.Up = 20-4002\nAn.Down = 20-4003\n");
            var ps = Ppsspp.Read(pp); Require(ps.Bindings["Cross"] == "A" && ps.Bindings["L"] == "lt" && ps.Bindings["Up"] == "dpad_up" && ps.Bindings["Left stick"] == "ls", "ppsspp fixture " + Show(ps));

            var sh = W(@"sh\shadPS4.exe", ""); W(@"sh\user\input_config\default.ini", "# c\ncross = n\ncross = circle\nl2 = l2\nomit = cross\n"); var sr = ShadPs4.Read(sh, null); Require(sr.Bindings["Cross"] == "B" && sr.Bindings["L2"] == "lt" && sr.Bindings.Count == 2, "shadps4 fixture " + Show(sr));
            Require(ScummVm.Read(W(@"sc\scummvm.exe", "")).Error != null, "scummvm is an honest error");
            Console.WriteLine("OK  readers fixtures: retroarch PS1/N64 alt-map/mismatch, mame default+per-game, citron, citra profile, xenia hid, rpcs3 player block, xemu default+custom, cemu xinput, teknoparrot, ppsspp, shadps4, scummvm");
        }
        finally { try { Directory.Delete(t, true); } catch { } }
    }
}
