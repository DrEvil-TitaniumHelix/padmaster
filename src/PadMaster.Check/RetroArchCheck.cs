using System.IO;
using System.Xml.Linq;
using PadMaster.Readers;

static class RetroArchCheck
{
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("retroarch: " + message); }
    static readonly Dictionary<string, string> Expected = new() { ["A"] = "B", ["B"] = "A", ["Start"] = "start", ["Select"] = "back", ["Up"] = "dpad_up", ["Down"] = "dpad_down", ["Left"] = "dpad_left", ["Right"] = "dpad_right" };
    const string Nes = "Nintendo Entertainment System";

    public static void Run()
    {
        Fixture();
        Real(LaunchBoxDir.Value, "Blaster Master");
        if (Directory.Exists(@"C:\LaunchBox-Test\Data")) Real(@"C:\LaunchBox-Test", "Contra");
    }

    static void Real(string root, string title)
    {
        var emu = RetroArch.FromXml(root, Nes);
        Require(emu is { App: var app } && app.EndsWith("retroarch.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(app), $"{root} NES emulator is RetroArch");
        var rom = XDocument.Load(Path.Combine(root, "Data", "Platforms", Nes + ".xml")).Root!.Elements("Game").Where(g => (string?)g.Element("Title") == title).Select(g => (string?)g.Element("ApplicationPath")).FirstOrDefault(p => p != null);
        Require(rom != null, $"{title} ApplicationPath in {root}");
        var map = RetroArch.Read(emu!.Value.App, emu.Value.CommandLine, rom, Nes);
        Require(map.Count == 8 && Expected.All(e => map.GetValueOrDefault(e.Key) == e.Value), $"{root} bindings {string.Join(" ", map.Select(m => $"{m.Key}->{m.Value}"))}");
        Console.WriteLine($"OK  retroarch reader: NES on {root} -> {map.Count} bindings, A->{map["A"]} B->{map["B"]} {string.Join(" ", map.Where(m => m.Key is not ("A" or "B")).Select(m => $"{m.Key}->{m.Value}"))}");
    }

    static void Fixture()
    {
        var ra = Path.Combine(Path.GetTempPath(), "padmaster_retroarch_" + Guid.NewGuid().ToString("N"));
        try
        {
            void W(string rel, string text) { var p = Path.Combine(ra, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
            W("retroarch.exe", "");
            W("retroarch.cfg", "input_joypad_driver = \"xinput\"\ninput_player1_a_btn = \"1\"\ninput_player1_b_btn = \"0\"\ninput_player1_x_btn = \"3\"\ninput_player1_y_btn = \"2\"\ninput_player1_start_btn = \"6\"\ninput_player1_select_btn = \"nul\"\ninput_player1_up_btn = \"h0up\"\ninput_player1_down_btn = \"h0down\"\ninput_player1_left_btn = \"h0left\"\ninput_player1_right_btn = \"h0right\"\n");
            W("info/nestopia_libretro.info", "display_name = \"Nintendo - NES / Famicom (Nestopia)\"\ncorename = \"Nestopia\"\n");
            W("autoconfig/xinput/Xbox 360 Wired Controller.cfg", "input_driver = \"xinput\"\ninput_device = \"XInput Controller\"\ninput_select_btn = \"7\"\n");
            var exe = Path.Combine(ra, "retroarch.exe");
            var rom = Path.Combine(ra, "roms", "Game (USA).zip");
            var cmd = "-L \"cores\\nestopia_libretro.dll\" -f";
            var plain = RetroArch.Read(exe, cmd, rom, Nes);
            Require(plain.Count == 8 && Expected.All(e => plain.GetValueOrDefault(e.Key) == e.Value), $"fixture plain {string.Join(" ", plain.Select(m => $"{m.Key}->{m.Value}"))}");
            W("config/Nestopia/Game (USA).cfg", "input_player1_start_btn = \"7\"\n");
            W("config/remaps/Nestopia/Game (USA).rmp", "input_player1_btn_b = \"8\"\ninput_player1_btn_a = \"0\"\n");
            var remapped = RetroArch.Read(exe, cmd, rom, Nes);
            Require(remapped.Count == 8 && remapped["A"] == "A" && remapped["B"] == "B" && remapped["Start"] == "back" && remapped["Select"] == "back" && remapped["Up"] == "dpad_up", $"fixture remap + override {string.Join(" ", remapped.Select(m => $"{m.Key}->{m.Value}"))}");
            W("config/Nestopia/Game (USA).opt", "nestopia_button_shift = \"enabled\"\n");
            var shifted = RetroArch.Read(exe, cmd, rom, Nes);
            Require(shifted["A"] == "B" && shifted["B"] == "X", $"fixture button_shift {string.Join(" ", shifted.Select(m => $"{m.Key}->{m.Value}"))}");
            Require(RetroArch.Read(exe, "-L \"cores\\fceumm_libretro.dll\"", rom, Nes).Count == 0, "unknown core -> absent");
            Console.WriteLine("OK  retroarch fixture: game .rmp swaps A/B, game override start->back, autoconfig fills nul select, button_shift option, unknown core absent");
        }
        finally { try { Directory.Delete(ra, true); } catch { } }
    }
}
