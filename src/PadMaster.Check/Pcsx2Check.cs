using System.Buffers.Binary;
using System.IO;
using System.Text;
using PadMaster.Readers;

static class Pcsx2Check
{
    const string Ps2 = "Sony Playstation 2";
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("pcsx2: " + message); }

    public static void Run()
    {
        var emulator = RetroArch.FromXml(LaunchBoxDir.Value, Ps2);
        if (emulator == null || !File.Exists(emulator.Value.App) || !File.Exists(Pcsx2.IniPath(emulator.Value.App))) Console.WriteLine($"SKIP pcsx2 reader: PCSX2 for {Ps2} not installed under {LaunchBoxDir.Value}");
        else
        {
            var read = Select.Read(LaunchBoxDir.Value, Ps2, "007: Everything or Nothing"); var b = read.Bindings;
            Require(read.Error == null && b.Count >= 16, "real PCSX2 bindings: " + (read.Error ?? b.Count.ToString()));
            string[] expected = ["Cross", "A", "Circle", "B", "Square", "X", "Triangle", "Y", "L1", "lb", "R1", "rb", "L2", "lt", "R2", "rt", "L3", "ls", "R3", "rs", "Start", "start", "Select", "back", "Up", "dpad_up", "Down", "dpad_down", "Left", "dpad_left", "Right", "dpad_right", "Left stick", "ls", "Right stick", "rs", "Analog", "home"];
            for (var i = 0; i < expected.Length; i += 2) Require(b.GetValueOrDefault(expected[i]) == expected[i + 1], $"{expected[i]} -> {expected[i + 1]} (got {b.GetValueOrDefault(expected[i])})");
            Require(!b.ContainsKey("LUp") && !b.ContainsKey("RLeft") && !b.ContainsKey("LargeMotor"), "stick directions folded, motors dropped");
            Console.WriteLine($"OK  pcsx2 reader: PS2 on {LaunchBoxDir.Value} -> {b.Count} bindings, Cross->A Circle->B Square->X Triangle->Y L1->lb R2->rt");
        }
        Fixture();
    }

    static void Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "PadMaster-pcsx2-" + Guid.NewGuid().ToString("N")); var exe = Path.Combine(root, "pcsx2-qt.exe");
        foreach (var sub in new[] { "inis", "gamesettings", "inputprofiles", "roms" }) Directory.CreateDirectory(Path.Combine(root, sub));
        File.WriteAllText(exe, ""); File.WriteAllText(Path.Combine(root, "portable.ini"), "");
        File.WriteAllText(Path.Combine(root, "inis", "PCSX2.ini"), "[Folders]\nInputProfiles = inputprofiles\n\n[Pad1]\nCross = SDL-0/FaceSouth\nCircle = SDL-0/FaceEast\nL2 = SDL-0/+LeftTrigger\nLUp = SDL-0/-LeftY\nLDown = SDL-0/+LeftY\nLLeft = SDL-0/-LeftX\nLRight = SDL-0/+LeftX\nAnalog = SDL-0/Guide\nLargeMotor = SDL-0/LargeMotor\n");
        File.WriteAllText(Path.Combine(root, "gamesettings", "SLUS-11111_DEADBEEF.ini"), "[EmuCore]\nInputProfileName = kbd\n");
        File.WriteAllText(Path.Combine(root, "gamesettings", "SLUS-33333_0BADF00D.ini"), "[EmuCore]\nEnableWideScreenPatches = true\n");
        File.WriteAllText(Path.Combine(root, "inputprofiles", "kbd.ini"), "[Pad1]\nCross = Keyboard/E\nCircle = SDL-0/FaceEast\nR1 = Pointer-0/LeftButton\nSquare = SDL-1/FaceWest\nTriangle = SDL-0/FaceNorth\n");
        var profiled = Iso(Path.Combine(root, "roms", "profiled.iso"), "SLUS_111.11"); var plain = Iso(Path.Combine(root, "roms", "plain.iso"), "SLUS_222.22");
        var (b, notes) = Pcsx2.Read(exe, profiled);
        Require(b.Count == 2 && b["Circle"] == "B" && b["Triangle"] == "Y", "profile [Pad1] replaces global: " + string.Join(",", b));
        Require(notes.SequenceEqual(["not on the gamepad: Cross = Keyboard/E", "not on the gamepad: R1 = Pointer-0/LeftButton", "not on the gamepad: Square = SDL-1/FaceWest"]), "keyboard, pointer and other-pad bindings reported: " + string.Join(" | ", notes));
        (b, notes) = Pcsx2.Read(exe, plain);
        Require(b.Count == 5 && b["Cross"] == "A" && b["L2"] == "lt" && b["Left stick"] == "ls" && b["Analog"] == "home" && notes.Count == 0, "known serial without profile -> global, silent: " + string.Join(",", b) + " / " + string.Join(" | ", notes));
        (b, notes) = Pcsx2.Read(exe, Path.Combine(root, "roms", "unknown.chd"));
        Require(b.Count == 5 && b["Cross"] == "A" && notes.SequenceEqual([Pcsx2.SerialWarning]), "unknown serial with a selecting game settings file -> global + warning: " + string.Join(" | ", notes));
        Directory.Delete(root, true);
        Console.WriteLine("OK  pcsx2 fixture: ISO serial selects gamesettings input profile; Keyboard/Pointer/SDL-1 bindings reported, not drawn; unknown serial warns");
    }

    static string Iso(string path, string boot)
    {
        var image = new byte[22 * 2048]; var pvd = image.AsSpan(16 * 2048); pvd[0] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(pvd[1..]);
        pvd[156] = 34; BinaryPrimitives.WriteUInt32LittleEndian(pvd[158..], 20); BinaryPrimitives.WriteUInt32LittleEndian(pvd[166..], 2048);
        var cnf = Encoding.ASCII.GetBytes($"BOOT2 = cdrom0:\\{boot};1\r\nVER = 1.00\r\nVMODE = NTSC\r\n"); cnf.CopyTo(image, 21 * 2048);
        var dir = image.AsSpan(20 * 2048); dir[0] = 34; dir[32] = 1; dir[33] = 0; var rec = dir[34..]; var name = Encoding.ASCII.GetBytes("SYSTEM.CNF;1");
        rec[0] = (byte)(33 + name.Length + 1); BinaryPrimitives.WriteUInt32LittleEndian(rec[2..], 21); BinaryPrimitives.WriteUInt32LittleEndian(rec[10..], (uint)cnf.Length); rec[32] = (byte)name.Length; name.CopyTo(rec[33..]);
        File.WriteAllBytes(path, image); return path;
    }
}
