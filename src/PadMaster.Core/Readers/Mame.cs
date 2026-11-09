using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PadMaster.Readers;

public static class Mame
{
    static readonly string[] Xi = ["A", "B", "X", "Y", "lb", "rb", "start", "back", "ls", "rs"]; // input_xinput.h xinput_buttons order, MAME 0.230

    public static string? Key(string seq)
    {
        var keys = seq.Split(" OR ", StringSplitOptions.TrimEntries).Select(One).Where(k => k != null).ToArray();
        return keys.FirstOrDefault(k => k!.StartsWith("dpad_")) ?? keys.FirstOrDefault(); // stick OR d-pad both work; the d-pad has one anchor per direction
    }

    static string? One(string alt)
    {
        var m = Regex.Match(alt, @"^JOYCODE_1_(\w+)$");
        if (!m.Success) return null;
        var code = m.Groups[1].Value;
        if (Regex.Match(code, @"^BUTTON(\d+)$") is { Success: true } b && int.Parse(b.Groups[1].Value) is >= 1 and <= 10 and var n) return Xi[n - 1];
        if (code.StartsWith("DPAD")) return "dpad_" + code[4..].ToLowerInvariant();
        if (code.StartsWith("RXAXIS") || code.StartsWith("RYAXIS")) return "rs";
        if (code.StartsWith("XAXIS") || code.StartsWith("YAXIS")) return "ls";
        if (code.StartsWith("RZAXIS")) return "lt"; // input_xinput.cpp: RT = ZAXIS, LT = RZAXIS
        return code.StartsWith("ZAXIS") ? "rt" : null;
    }

    static string? Name(string port) => port switch
    {
        "P1_JOYSTICK_UP" => "Up", "P1_JOYSTICK_DOWN" => "Down", "P1_JOYSTICK_LEFT" => "Left", "P1_JOYSTICK_RIGHT" => "Right", "P1_START" => "Start", "P1_SELECT" => "Select", "COIN1" => "Coin",
        _ => Regex.Match(port, @"^P1_BUTTON(\d+)$") is { Success: true } m ? "Button " + m.Groups[1].Value : null,
    };

    public static ReadResult Read(string exe, string? rom)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!; var notes = new List<string>(); var ports = new Dictionary<string, string>();
        var ini = Path.Combine(dir, "mame.ini"); var text = File.Exists(ini) ? File.ReadAllText(ini) : "";
        string Opt(string key, string def) => Regex.Match(text, $@"^{key}[ \t]+(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : def;
        var provider = Opt("joystickprovider", "auto");
        if (Opt("joystick", "0") != "1") return new(new(), notes, "mame.ini has joystick input off", ini);
        if (provider is not ("auto" or "winhybrid" or "xinput")) return new(new(), notes, $"joystickprovider is {provider}; pad buttons are only known for XInput", ini);
        var cfgDir = Path.GetFullPath(Opt("cfg_directory", "cfg"), dir); var main = Path.Combine(cfgDir, "default.cfg");
        void Load(string path)
        {
            if (!File.Exists(path)) return;
            try { foreach (var port in XDocument.Load(path).Descendants("port")) if ((string?)port.Attribute("type") is { } type && port.Elements("newseq").FirstOrDefault(s => (string?)s.Attribute("type") == "standard") is { } seq) ports[type] = seq.Value.Trim(); }
            catch (XmlException e) { notes.Add($"unreadable {Path.GetFileName(path)}: {e.Message}"); }
        }
        Load(main); if (rom != null) Load(Path.Combine(cfgDir, Path.GetFileNameWithoutExtension(rom) + ".cfg"));
        foreach (var (port, seq) in new[] { ("P1_JOYSTICK_UP", "JOYCODE_1_YAXIS_UP_SWITCH"), ("P1_JOYSTICK_DOWN", "JOYCODE_1_YAXIS_DOWN_SWITCH"), ("P1_JOYSTICK_LEFT", "JOYCODE_1_XAXIS_LEFT_SWITCH"), ("P1_JOYSTICK_RIGHT", "JOYCODE_1_XAXIS_RIGHT_SWITCH") }.Concat(Enumerable.Range(1, 10).Select(n => ($"P1_BUTTON{n}", $"JOYCODE_1_BUTTON{n}"))))
            if (ports.TryAdd(port, seq)) notes.Add($"{port}: MAME built-in default (src/emu/inpttype.ipp), no config entry");
        var result = new Dictionary<string, string>();
        foreach (var (port, seq) in ports) if (Name(port) is { } name) { if (Key(seq) is { } key) result[name] = key; else if (seq != "NONE") notes.Add($"not on the gamepad: {name} = {seq}"); }
        foreach (var (stick, prefix) in new[] { ("Left stick", "P1_JOYSTICKLEFT_"), ("Right stick", "P1_JOYSTICKRIGHT_") })
        {
            var keys = new[] { "UP", "DOWN", "LEFT", "RIGHT" }.Select(d => ports.TryGetValue(prefix + d, out var s) ? Key(s) : null).Where(k => k != null).Distinct().ToArray();
            if (keys.Length == 1) result[stick] = keys[0]!; else if (keys.Length > 1) notes.Add($"{stick} mixes pad inputs: {string.Join(", ", keys)}");
        }
        return new(result, notes, result.Count == 0 ? "no P1 inputs on the gamepad in default.cfg" : null, main);
    }
}
