namespace PadMaster.Readers;

public static class ScummVm
{
    public static ReadResult Read(string exe) => new(new(), [], "ScummVM has no console buttons: each game engine defines its own pad actions, and scummvm.ini holds no fixed layout", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "scummvm.ini"));
}
