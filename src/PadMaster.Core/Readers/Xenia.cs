using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class Xenia
{
    public static ReadResult Read(string exe)
    {
        var toml = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "xenia.config.toml");
        if (!File.Exists(toml)) return new(new(), [], "xenia.config.toml not found", toml);
        var hid = Regex.Match(File.ReadAllText(toml), @"^hid\s*=\s*""(\w+)""", RegexOptions.Multiline).Groups[1].Value;
        if (hid is not ("xinput" or "any")) return new(new(), [], $"hid = {hid}: not an XInput pad", toml);
        return new(new() { ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y", ["LB"] = "lb", ["RB"] = "rb", ["LT"] = "lt", ["RT"] = "rt", ["Back"] = "back", ["Start"] = "start", ["Guide"] = "home", ["Up"] = "dpad_up", ["Down"] = "dpad_down", ["Left"] = "dpad_left", ["Right"] = "dpad_right", ["Left stick"] = "ls", ["Right stick"] = "rs", ["L3"] = "ls", ["R3"] = "rs" },
            ["Xenia has no button-mapping settings: its XInput driver passes the pad straight through (src/xenia/hid/xinput/xinput_input_driver.cc), hid = " + hid], null, toml);
    }
}
