namespace PadMaster.Readers;

public static class Ppsspp
{
    internal static string? Key(int code) => code switch // KeyCodes.h NKCODE_*, XinputDevice.cpp xinput_ctrl_map; 4000 + axis*2 (+1 if negative)
    {
        19 => "dpad_up", 20 => "dpad_down", 21 => "dpad_left", 22 => "dpad_right", 96 => "A", 97 => "B", 99 => "X", 100 => "Y", 102 => "lb", 103 => "rb", 106 => "ls", 107 => "rs", 108 => "start", 109 => "back",
        >= 4000 => ((code - 4000) / 2) switch { 0 or 1 => "ls", 11 or 14 => "rs", 17 => "lt", 18 => "rt", _ => null },
        _ => null,
    };

    public static ReadResult Read(string exe)
    {
        var portable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "memstick", "PSP", "SYSTEM", "controls.ini");
        var ini = File.Exists(portable) ? portable : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PPSSPP", "PSP", "SYSTEM", "controls.ini");
        if (!File.Exists(ini)) return new(new(), [], "controls.ini not found", ini);
        var map = Pcsx2.Ini(ini).GetValueOrDefault("ControlMapping") ?? new(); var result = new Dictionary<string, string>();
        string? Pad(string name) => map.GetValueOrDefault(name)?[0].Split(',').Select(b => b.Trim().Split('-')).Where(p => p.Length == 2 && p[0] == "20" && int.TryParse(p[1], out _)).Select(p => Key(int.Parse(p[1]))).FirstOrDefault(k => k != null);
        foreach (var name in new[] { "Up", "Down", "Left", "Right", "Circle", "Cross", "Square", "Triangle", "Start", "Select", "L", "R" }) if (Pad(name) is { } key) result[name] = key;
        var stick = new[] { "An.Up", "An.Down", "An.Left", "An.Right" }.Select(Pad).Where(k => k != null).Distinct().ToArray();
        if (stick.Length == 1) result["Left stick"] = stick[0]!;
        return new(result, [], result.Count == 0 ? "no XInput bindings in controls.ini" : null, ini);
    }
}
