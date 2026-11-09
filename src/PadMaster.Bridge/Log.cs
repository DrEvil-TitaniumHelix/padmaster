using System.IO;

namespace PadMaster;

public static class RotatingLog
{
    public const int Limit = 1024 * 1024;
    static readonly object Gate = new();
    public static void Write(string path, string text)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
                if (new FileInfo(path) is { Exists: true } info && info.Length + System.Text.Encoding.UTF8.GetByteCount(line) > Limit)
                {
                    try { File.Move(path, path + ".1", true); } catch { return; }
                }
                for (var attempt = 0; ; attempt++)
                {
                    try { File.AppendAllText(path, line); return; }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(2); }
                }
            }
        }
        catch { }
    }
}
