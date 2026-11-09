using System.Diagnostics;
using System.IO;
using System.Text;

namespace PadMaster;

// Finding and starting the user's own AI command line. Shared by source between the plugins.
internal static class Cli
{
    public static string? Detect(string configured = "", string kind = "claude-code")
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Existing(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
        var command = kind == "codex" ? "codex" : "claude";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { command + ".exe", command + ".cmd" })
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var found = Existing(Path.Combine(directory.Trim().Trim('"'), name));
                if (found != null) return found;
            }
        return kind == "codex" ? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd")) : Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"));
    }

    static string? Existing(string path)
    {
        try { return File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }

    public static ProcessStartInfo Command(string cli, string cwd, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(cli) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        if (Path.GetExtension(cli).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new[] { cli }.Concat(args).ToArray();
            if (parts.Any(x => x.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0)) throw new ArgumentException("Unsupported character in command path.");
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            start.Arguments = "/d /s /c \"" + string.Join(" ", parts.Select(x => "\"" + x + "\"")) + "\"";
        }
        else foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || name.Equals("CODEX_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        return start;
    }
}
