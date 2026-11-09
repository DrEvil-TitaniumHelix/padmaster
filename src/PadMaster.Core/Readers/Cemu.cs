using System.Xml;
using System.Xml.Linq;

namespace PadMaster.Readers;

public static class Cemu
{
    static readonly string?[] Names = [null, "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "Plus", "Minus", "Up", "Down", "Left", "Right", "L3", "R3", "Left stick", "Left stick", "Left stick", "Left stick", "Right stick", "Right stick", "Right stick", "Right stick", null, null, "Home"]; // VPADController.h ButtonId

    internal static string? Key(int code) => code switch // XInputController.cpp: button n = wButtons bit n; Controller.h: kAxisXP = 38
    {
        0 => "dpad_up", 1 => "dpad_down", 2 => "dpad_left", 3 => "dpad_right", 4 => "start", 5 => "back", 6 => "ls", 7 => "rs", 8 => "lb", 9 => "rb", 12 => "A", 13 => "B", 14 => "X", 15 => "Y",
        38 or 39 or 44 or 45 => "ls", 40 or 41 or 46 or 47 => "rs", 42 or 48 => "lt", 43 or 49 => "rt", _ => null,
    };

    public static ReadResult Read(string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!; var xml = Path.Combine(dir, "controllerProfiles", "controller0.xml"); var notes = new List<string>();
        if (!File.Exists(xml)) return new(new(), notes, "controller0.xml not found", xml);
        XElement root; try { root = XDocument.Load(xml).Root!; } catch (XmlException e) { return new(new(), notes, "unreadable controller0.xml: " + e.Message, xml); }
        if ((string?)root.Element("type") is not "Wii U GamePad") return new(new(), notes, $"emulated controller is {(string?)root.Element("type")}, only the GamePad layout is sourced", xml);
        var pad = root.Elements("controller").FirstOrDefault(c => (string?)c.Element("api") == "XInput" && (string?)c.Element("uuid") == "0" && c.Element("mappings")?.HasElements == true);
        if (pad == null) return new(new(), notes, "no XInput slot 0 mapping in controller0.xml", xml);
        var result = new Dictionary<string, string>(); var mixed = new HashSet<string>();
        foreach (var entry in pad.Element("mappings")!.Elements("entry"))
        {
            if (!int.TryParse((string?)entry.Element("mapping"), out var id) || id <= 0 || id >= Names.Length || Names[id] is not { } name || !int.TryParse((string?)entry.Element("button"), out var code) || Key(code) is not { } key) continue;
            if (result.TryGetValue(name, out var old) && old != key) mixed.Add(name);
            result[name] = key;
        }
        foreach (var name in mixed) { result.Remove(name); notes.Add($"{name} directions land on different pad inputs; left out"); }
        var games = Path.Combine(dir, "gameProfiles");
        if (Directory.Exists(games) && Directory.EnumerateFiles(games, "*.ini").Any(f => File.ReadLines(f).Any(l => l.TrimStart().StartsWith("controllerProfile", StringComparison.OrdinalIgnoreCase)))) notes.Add("a per-game controller profile exists; title id of this game is not known here");
        return new(result, notes, result.Count == 0 ? "no gamepad bindings in controller0.xml" : null, xml);
    }
}
