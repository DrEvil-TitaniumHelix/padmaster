using System.Xml;
using System.Xml.Linq;

namespace PadMaster.Readers;

public static class TeknoParrot
{
    internal static string? Mask(int code) => code switch { 1 => "dpad_up", 2 => "dpad_down", 4 => "dpad_left", 8 => "dpad_right", 16 => "start", 32 => "back", 64 => "ls", 128 => "rs", 256 => "lb", 512 => "rb", 1024 => "home", 4096 => "A", 8192 => "B", 16384 => "X", 32768 => "Y", _ => null }; // XINPUT_GAMEPAD_* masks

    public static ReadResult Read(string exe, string? rom)
    {
        var profile = rom != null && rom.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && File.Exists(rom) ? rom : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "UserProfiles", Path.GetFileNameWithoutExtension(rom ?? "") + ".xml");
        if (!File.Exists(profile)) return new(new(), [], "no TeknoParrot profile for this game", profile);
        XElement root; try { root = XDocument.Load(profile).Root!; } catch (XmlException e) { return new(new(), [], "unreadable profile: " + e.Message, profile); }
        var api = (string?)root.Descendants("FieldInformation").FirstOrDefault(f => (string?)f.Element("FieldName") == "Input API")?.Element("FieldValue");
        if (api != "XInput") return new(new(), [], $"profile uses {api ?? "no input API"}, not XInput: its raw device buttons cannot be matched to the pad", profile);
        var result = new Dictionary<string, string>(); var notes = new List<string>();
        foreach (var button in root.Descendants("JoystickButtons").Where(b => b.Element("ButtonName") != null))
        {
            var name = (string)button.Element("ButtonName")!; var x = button.Element("XInputButton"); if (x == null) continue;
            bool Is(string tag) => (string?)x.Element(tag) == "true";
            if ((string?)x.Element("XInputIndex") != "0") { notes.Add($"not on pad 0: {name}"); continue; }
            var key = Is("IsButton") ? Mask(int.TryParse((string?)x.Element("ButtonCode"), out var code) ? code : -1) : Is("IsLeftTrigger") ? "lt" : Is("IsRightTrigger") ? "rt" : Is("IsLeftThumbX") || Is("IsLeftThumbY") ? "ls" : Is("IsRightThumbX") || Is("IsRightThumbY") ? "rs" : null;
            if (key != null) result[name] = key;
        }
        return new(result, notes, result.Count == 0 ? "profile has no XInput bindings" : null, profile);
    }
}
