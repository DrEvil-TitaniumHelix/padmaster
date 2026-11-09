namespace PadMaster.Readers;

public static class Citron
{
    static readonly Dictionary<int, string> Axes = new() { [0] = "ls", [1] = "ls", [2] = "lt", [3] = "rs", [4] = "rs", [5] = "rt" }; // real qt-config: ZL = axis 2, ZR = axis 5

    public static ReadResult Read(string exe)
    {
        var config = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "user", "config");
        var ini = Path.Combine(config, "qt-config.ini");
        if (!File.Exists(ini)) return new(new(), [], "qt-config.ini not found", ini);
        var controls = Pcsx2.Ini(ini).GetValueOrDefault("Controls") ?? new(); string? Get(string key) => controls.GetValueOrDefault(key)?[0].Trim('"');
        var result = new Dictionary<string, string>(); var notes = new List<string>(); var raw = new List<string>();
        foreach (var (key, name) in new[] { ("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y"), ("l", "L"), ("r", "R"), ("zl", "ZL"), ("zr", "ZR"), ("plus", "Plus"), ("minus", "Minus"), ("dup", "Up"), ("ddown", "Down"), ("dleft", "Left"), ("dright", "Right"), ("lstick", "L3"), ("rstick", "R3"), ("home", "Home") })
        {
            if (Get("player_0_button_" + key) is not { } value || value == "[empty]") continue;
            raw.Add(value);
            if (Citra.Button(value, Axes) is { } k) result[name] = k; else notes.Add($"not on the gamepad: {name} = {value}");
        }
        foreach (var (key, name) in new[] { ("lstick", "Left stick"), ("rstick", "Right stick") }) if (Get("player_0_" + key) is { } value && Citra.Stick(value, Axes) is { } k) result[name] = k;
        if (Citra.Guid(raw) is { } id) notes.Add("bound to SDL pad " + id);
        var custom = Path.Combine(config, "custom");
        if (Directory.Exists(custom) && Directory.EnumerateFiles(custom, "*.ini").Any(f => File.ReadLines(f).Any(l => l.StartsWith("player_0_button_")))) notes.Add("a per-game input override exists; title id of this game is not known here");
        return new(result, notes, result.Count == 0 ? "player 1 has no gamepad bindings" : null, ini);
    }
}
