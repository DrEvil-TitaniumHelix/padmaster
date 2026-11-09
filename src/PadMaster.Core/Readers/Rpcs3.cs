using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class Rpcs3
{
    static readonly Dictionary<string, string> Keys = new() { ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y", ["LB"] = "lb", ["RB"] = "rb", ["LT"] = "lt", ["RT"] = "rt", ["LS"] = "ls", ["RS"] = "rs", ["Back"] = "back", ["Start"] = "start", ["Guide"] = "home", ["Up"] = "dpad_up", ["Down"] = "dpad_down", ["Left"] = "dpad_left", ["Right"] = "dpad_right" };
    static readonly Dictionary<string, string> Names = new() { ["Cross"] = "Cross", ["Circle"] = "Circle", ["Square"] = "Square", ["Triangle"] = "Triangle", ["L1"] = "L1", ["L2"] = "L2", ["L3"] = "L3", ["R1"] = "R1", ["R2"] = "R2", ["R3"] = "R3", ["Start"] = "Start", ["Select"] = "Select", ["PS Button"] = "PS", ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right" };

    static (string Handler, Dictionary<string, string> Config) Player1(string yml)
    {
        var config = new Dictionary<string, string>(); var handler = ""; var inside = false; var block = false;
        foreach (var raw in File.ReadLines(yml))
        {
            if (raw.Length > 0 && raw[0] != ' ') { inside = raw.StartsWith("Player 1 Input:"); block = false; continue; }
            if (!inside) continue;
            var m = Regex.Match(raw, @"^(\s*)([^:]+):\s*(.*)$"); if (!m.Success) continue;
            var (indent, key, value) = (m.Groups[1].Length, m.Groups[2].Value, m.Groups[3].Value.Trim().Trim('"'));
            if (indent == 2) { if (key == "Handler") handler = value; block = key == "Config"; }
            else if (indent == 4 && block) config[key] = value;
        }
        return (handler, config);
    }

    public static ReadResult Read(string exe, string? rom)
    {
        var inputs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "config", "input_configs"); var notes = new List<string>();
        var serial = rom == null ? null : Regex.Match(rom, @"\b[A-Z]{4}\d{5}\b") is { Success: true } s ? s.Value : null;
        var active = Path.Combine(inputs, "active_input_configurations.yml"); var chosen = File.Exists(active) ? File.ReadAllLines(active).Select(l => l.Trim().Split(": ", 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]) : new();
        var perGame = serial != null && Directory.Exists(Path.Combine(inputs, serial)) ? Path.Combine(inputs, serial, chosen.GetValueOrDefault(serial, "Default") + ".yml") : null;
        var yml = perGame != null && File.Exists(perGame) ? perGame : Path.Combine(inputs, "global", chosen.GetValueOrDefault("global", "Default") + ".yml");
        if (serial == null && Directory.Exists(inputs) && Directory.EnumerateDirectories(inputs).Any(d => Path.GetFileName(d) != "global")) notes.Add("per-game input config may apply; serial unknown");
        if (!File.Exists(yml)) return new(new(), notes, "input config not found", yml);
        var (handler, config) = Player1(yml);
        if (handler != "XInput") return new(new(), notes, $"Player 1 uses {handler}, not an XInput pad", yml);
        var result = new Dictionary<string, string>();
        foreach (var (key, name) in Names) if (config.GetValueOrDefault(key) is { Length: > 0 } v) { if (Keys.TryGetValue(v, out var k)) result[name] = k; else notes.Add($"not on the gamepad: {name} = {v}"); }
        foreach (var (stick, prefix, axis) in new[] { ("Left stick", "Left Stick ", "ls"), ("Right stick", "Right Stick ", "rs") })
            if (new[] { "Left", "Right", "Up", "Down" }.All(d => config.GetValueOrDefault(prefix + d)?.StartsWith(axis.ToUpperInvariant() + " ") == true)) result[stick] = axis;
        return new(result, notes, result.Count == 0 ? "no gamepad bindings for Player 1" : null, yml);
    }
}
