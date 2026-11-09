using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class Pcsx2
{
    static readonly Dictionary<string, string> Sdl = new()
    {
        ["FaceSouth"] = "A", ["FaceEast"] = "B", ["FaceWest"] = "X", ["FaceNorth"] = "Y", ["LeftShoulder"] = "lb", ["RightShoulder"] = "rb", ["LeftTrigger"] = "lt", ["RightTrigger"] = "rt",
        ["Back"] = "back", ["Start"] = "start", ["Guide"] = "home", ["LeftStick"] = "ls", ["RightStick"] = "rs", ["DPadUp"] = "dpad_up", ["DPadDown"] = "dpad_down", ["DPadLeft"] = "dpad_left", ["DPadRight"] = "dpad_right",
        ["LeftX"] = "ls", ["LeftY"] = "ls", ["RightX"] = "rs", ["RightY"] = "rs",
    };
    public const string SerialWarning = "per-game input profile may apply; serial unknown";

    public static string DataDirectory(string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        return File.Exists(Path.Combine(dir, "portable.ini")) || File.Exists(Path.Combine(dir, "portable.txt")) ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
    }
    public static string IniPath(string exe) => Path.Combine(DataDirectory(exe), "inis", "PCSX2.ini");

    internal static Dictionary<string, Dictionary<string, List<string>>> Ini(string path)
    {
        var doc = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase); var section = doc[""] = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return doc;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim(); var i = line.IndexOf('=');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']') { section = doc[line[1..^1]] = doc.GetValueOrDefault(line[1..^1]) ?? new(StringComparer.OrdinalIgnoreCase); continue; }
            if (i < 0) continue;
            var key = line[..i].Trim(); (section[key] = section.GetValueOrDefault(key) ?? new()).Add(line[(i + 1)..].Trim());
        }
        return doc;
    }

    public static string? Serial(string? rom)
    {
        if (rom == null || !Path.GetExtension(rom).Equals(".iso", StringComparison.OrdinalIgnoreCase) || !File.Exists(rom)) return null;
        try
        {
            using var f = File.OpenRead(rom); var pvd = new byte[2048]; f.Position = 16 * 2048; f.ReadExactly(pvd);
            if (Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;
            var dir = new byte[Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166)), 1 << 20)]; f.Position = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158)) * 2048L; f.ReadExactly(dir);
            for (var i = 0; i + 33 <= dir.Length; i = dir[i] == 0 ? (i / 2048 + 1) * 2048 : i + dir[i])
            {
                if (!Encoding.ASCII.GetString(dir, i + 33, dir[i + 32]).StartsWith("SYSTEM.CNF", StringComparison.OrdinalIgnoreCase)) continue;
                var cnf = new byte[Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i + 10)), 4096)]; f.Position = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i + 2)) * 2048L; f.ReadExactly(cnf);
                var m = Regex.Match(Encoding.ASCII.GetString(cnf), @"BOOT2\s*=\s*cdrom0:\\?([A-Za-z]{4})_(\d{3})\.(\d{2})");
                return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}{m.Groups[3].Value}" : null;
            }
        }
        catch (IOException) { }
        return null;
    }

    public static (Dictionary<string, string> Bindings, List<string> Notes) Read(string exe, string? rom)
    {
        var data = DataDirectory(exe); var ini = Ini(IniPath(exe)); var notes = new List<string>();
        string Folder(string key, string fallback) => Path.GetFullPath(ini.GetValueOrDefault("Folders")?.GetValueOrDefault(key)?[0] ?? fallback, data);
        var global = ini.GetValueOrDefault("Pad1") ?? new(); var pad = global;
        var index = global.Values.SelectMany(v => v).Select(v => Regex.Match(v, @"^SDL-(\d+)/")).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "0";
        var settings = Folder("GameSettings", "gamesettings");
        var selecting = Directory.Exists(settings) ? Directory.EnumerateFiles(settings, "*.ini").Select(f => (File: Path.GetFileNameWithoutExtension(f), Profile: Ini(f).GetValueOrDefault("EmuCore")?.GetValueOrDefault("InputProfileName")?[0])).Where(s => !string.IsNullOrEmpty(s.Profile)).ToArray() : [];
        var serial = Serial(rom);
        var own = serial == null ? null : selecting.FirstOrDefault(s => s.File.Equals(serial, StringComparison.OrdinalIgnoreCase) || s.File.StartsWith(serial + "_", StringComparison.OrdinalIgnoreCase)).Profile;
        if (own != null) pad = Ini(Path.Combine(Folder("InputProfiles", "inputprofiles"), own + ".ini")).GetValueOrDefault("Pad1") ?? new();
        else if (serial == null && selecting.Length > 0) notes.Add(SerialWarning);
        var result = new Dictionary<string, string>();
        foreach (var (input, values) in pad)
        {
            if (input.EndsWith("Motor", StringComparison.OrdinalIgnoreCase)) continue;
            var key = values.Select(v => Regex.Match(v, @"^SDL-(\d+)/[+-]?(\w+)$")).Where(m => m.Success && m.Groups[1].Value == index && Sdl.ContainsKey(m.Groups[2].Value)).Select(m => Sdl[m.Groups[2].Value]).FirstOrDefault();
            if (key != null) result[input] = key; else notes.Add($"not on the gamepad: {input} = {string.Join(" | ", values)}");
        }
        foreach (var (stick, prefix) in new[] { ("Left stick", "L"), ("Right stick", "R") })
        {
            var keys = new[] { "Up", "Down", "Left", "Right" }.Select(d => result.Remove(prefix + d, out var k) ? k : null).Where(k => k != null).Distinct().ToArray();
            if (keys.Length == 1) result[stick] = keys[0]!;
        }
        return (result, notes);
    }
}
