using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class Citra
{
    static readonly string[] Xi = ["A", "B", "X", "Y", "lb", "rb", "back", "start", "ls", "rs", "home"]; // SDL_xinputjoystick.c s_XInputButtons
    internal static readonly Dictionary<int, string> Axes = new() { [0] = "ls", [1] = "ls", [2] = "rs", [3] = "rs", [4] = "lt", [5] = "rt" };

    internal static string? Button(string value, Dictionary<int, string> axes)
    {
        if (!value.Contains("engine:sdl")) return null;
        if (Regex.Match(value, @"button:(\d+)") is { Success: true } b && int.Parse(b.Groups[1].Value) < Xi.Length) return Xi[int.Parse(b.Groups[1].Value)];
        if (Regex.IsMatch(value, @"hat:\d") && Regex.Match(value, @"direction:(up|down|left|right)") is { Success: true } d) return "dpad_" + d.Groups[1].Value;
        return Regex.Match(value, @"(?<!_)axis:(\d+)") is { Success: true } a ? axes.GetValueOrDefault(int.Parse(a.Groups[1].Value)) : null;
    }

    internal static string? Stick(string value, Dictionary<int, string> axes) => value.Contains("sdl") && Regex.Match(value, @"axis_x:(\d+)|axis\$0(\d+)") is { Success: true } m ? axes.GetValueOrDefault(int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)) : null;

    internal static string? Guid(IEnumerable<string> values) => values.Select(v => Regex.Match(v, @"guid:(\w+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value;

    public static ReadResult Read(string exe)
    {
        var ini = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "user", "config", "qt-config.ini");
        if (!File.Exists(ini)) return new(new(), [], "qt-config.ini not found", ini);
        var controls = Pcsx2.Ini(ini).GetValueOrDefault("Controls") ?? new(); string? Get(string key) => controls.GetValueOrDefault(key)?[0].Trim('"');
        var profile = Get("profile") is { } p && int.TryParse(p, out var n) ? n : 0; var prefix = $@"profiles\{profile + 1}\"; // Qt arrays are 1-based on disk
        var result = new Dictionary<string, string>(); var notes = new List<string>(); var raw = new List<string>();
        foreach (var (key, name) in new[] { ("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y"), ("up", "Up"), ("down", "Down"), ("left", "Left"), ("right", "Right"), ("l", "L"), ("r", "R"), ("start", "Start"), ("select", "Select"), ("zl", "ZL"), ("zr", "ZR"), ("home", "Home") })
        {
            if (Get(prefix + "button_" + key) is not { } value) continue;
            raw.Add(value);
            if (Button(value, Axes) is { } k) result[name] = k; else notes.Add($"not on the gamepad: {name} = {value}");
        }
        foreach (var (key, name) in new[] { ("circle_pad", "Circle Pad"), ("c_stick", "C-Stick") }) if (Get(prefix + key) is { } value && Stick(value, Axes) is { } k) result[name] = k;
        if (Guid(raw) is { } id) notes.Add("bound to SDL pad " + id);
        return new(result, notes, result.Count == 0 ? $"input profile {profile} has no gamepad bindings" : null, ini);
    }
}
