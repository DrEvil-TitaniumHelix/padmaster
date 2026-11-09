using System.Xml.Linq;

namespace PadMaster.Readers;

public sealed record ReadResult(Dictionary<string, string> Bindings, IReadOnlyList<string> Notes, string? Error, string? Config);

public static class Select
{
    public static ReadResult Read(string root, string platform, string title)
    {
        var emulators = XDocument.Load(Path.Combine(root, "Data", "Emulators.xml")).Root!;
        var platformFile = Path.Combine(root, "Data", "Platforms", platform + ".xml");
        var game = File.Exists(platformFile) ? XDocument.Load(platformFile).Root!.Elements("Game").FirstOrDefault(g => (string?)g.Element("Title") == title) : null;
        var id = (string?)game?.Element("Emulator") is { Length: > 0 } own ? own : null;
        var ep = emulators.Elements("EmulatorPlatform").Where(e => (string?)e.Element("Platform") == platform && (id == null || (string?)e.Element("Emulator") == id)).OrderByDescending(e => (string?)e.Element("Default") == "true").FirstOrDefault();
        var em = emulators.Elements("Emulator").FirstOrDefault(e => (string?)e.Element("ID") == (id ?? (string?)ep?.Element("Emulator")));
        if (em == null) return new(new(), [], "no emulator for " + platform, null);
        var exe = Path.GetFullPath((string?)em.Element("ApplicationPath") ?? "", root);
        var rom = (string?)game?.Element("ApplicationPath") is { Length: > 0 } app ? Path.GetFullPath(app, root) : null;
        var command = (string?)game?.Element("CommandLine") is { Length: > 0 } c ? c : (string?)ep?.Element("CommandLine") ?? (string?)em.Element("CommandLine") ?? "";
        return ForExe(exe, command, rom, platform);
    }

    public static ReadResult ForExe(string exe, string command, string? rom, string platform)
    {
        switch (Path.GetFileNameWithoutExtension(exe).ToLowerInvariant())
        {
            case "retroarch": return RetroArch.Full(exe, command, rom, platform);
            case "pcsx2-qt" or "pcsx2": var (bindings, notes) = Pcsx2.Read(exe, rom); return new(bindings, notes, null, Pcsx2.IniPath(exe));
            case "mame": return Mame.Read(exe, rom);
            case "citron": return Citron.Read(exe);
            case "xenia": return Xenia.Read(exe);
            case "rpcs3": return Rpcs3.Read(exe, rom);
            case "citra-qt" or "citra": return Citra.Read(exe);
            case "xemu": return Xemu.Read(exe);
            case "cemu": return Cemu.Read(exe);
            case "scummvm": return ScummVm.Read(exe);
            case "teknoparrotui": return TeknoParrot.Read(exe, rom);
            case "ppssppwindows64" or "ppssppwindows": return Ppsspp.Read(exe);
            case "shadps4": return ShadPs4.Read(exe, rom);
            default: return new(new(), [], "no reader for " + Path.GetFileName(exe), null);
        }
    }
}
