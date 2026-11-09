using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class Xemu
{
    public static ReadResult Read(string exe)
    {
        var toml = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "xemu.toml");
        if (!File.Exists(toml)) return new(new(), [], "xemu.toml not found", toml);
        var text = File.ReadAllText(toml);
        var driver = Regex.Match(text, @"^port1_driver\s*=\s*'([^']*)'", RegexOptions.Multiline).Groups[1].Value;
        if (driver != "usb-xbox-gamepad") return new(new(), [], $"port 1 driver is {(driver == "" ? "not set" : driver)}, not a standard Xbox pad", toml);
        var id = Regex.Match(text, @"^port1\s*=\s*'([^']*)'", RegexOptions.Multiline).Groups[1].Value;
        if (id == "") return new(new(), [], "no pad bound to port 1", toml);
        if (Regex.Match(text, @"\{[^}]*gamepad_id\s*=\s*'" + Regex.Escape(id) + @"'[^}]*\}") is { Success: true } m && Regex.IsMatch(m.Value, @"controller_mapping|\ba\s*=")) return new(new(), [], "port 1 has a custom button map, not decoded", toml);
        return new(new() { ["A"] = "A", ["B"] = "B", ["X"] = "X", ["Y"] = "Y", ["White"] = "lb", ["Black"] = "rb", ["Left Trigger"] = "lt", ["Right Trigger"] = "rt", ["Back"] = "back", ["Start"] = "start", ["Guide"] = "home", ["Up"] = "dpad_up", ["Down"] = "dpad_down", ["Left"] = "dpad_left", ["Right"] = "dpad_right", ["Left stick"] = "ls", ["Right stick"] = "rs", ["L3"] = "ls", ["R3"] = "rs" },
            ["xemu built-in default map (config_spec.yml controller_mapping defaults, ui/xemu-input.c), no custom mapping in xemu.toml", "bound to SDL pad " + id], null, toml);
    }
}
