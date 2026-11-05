using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace PadMaster;

public sealed record Settings
{
    public bool Enabled { get; init; }
    public string Assistant { get; init; } = "";
    public string Cli { get; init; } = "";
    public int Port { get; init; } = 47800;
    public bool ConfirmBulk { get; init; } = true;
    public int BulkThreshold { get; init; } = 20;
    public bool AutoOpen { get; init; } = true;
    public string Placement { get; init; } = "right";
    public bool Expanded { get; init; } = true;
    public long Revision { get; init; }
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static Settings ReadRequired(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    public static Settings Read(string path, Action<string> log)
    {
        try { return ReadRequired(path); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception e) { log($"settings read: {e.GetType().Name} code=0x{e.HResult:X8}"); return new(); }
    }

    public static Task<Settings> UpdateAsync(string path, Func<Settings, Settings> change) => Task.Run(() =>
    {
        var next = change(ReadRequired(path)); next.Write(path); return next;
    });

    public static Settings Decode(byte[]? bytes)
    {
        if (bytes == null) return new();
        var value = JsonSerializer.Deserialize<Settings>(bytes, Json) ?? throw new JsonException("Settings must be an object.");
        if (value.BulkThreshold < 1 || value.Port is < 1024 or > 65535 || value.Cli == null || value.Assistant is not ("" or "claude-code" or "codex") || value.Placement is not ("right" or "left" or "floating")) throw new JsonException("Invalid settings values.");
        return value;
    }

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, Encode());
            for (var attempt = 0; ; attempt++)
            {
                try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 4) { Thread.Sleep(25 * (1 << attempt)); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool TryApply(Settings current, IReadOnlyDictionary<string, string?> fields, bool allowEnable, out Settings value, out string error)
    {
        value = current; error = "";
        string Get(string key, string fallback) => fields.TryGetValue(key, out var text) ? text ?? "" : fallback;
        var assistant = Get("assistant", current.Assistant.Length == 0 ? "claude-code" : current.Assistant);
        var placement = Get("placement", current.Placement);
        if (assistant is not ("claude-code" or "codex")) { error = "Choose Claude Code or Codex."; return false; }
        if (!int.TryParse(Get("port", current.Port.ToString()), out var port) || port is < 1024 or > 65535) { error = "Choose a port between 1024 and 65535."; return false; }
        if (!int.TryParse(Get("bulkThreshold", current.BulkThreshold.ToString()), out var bulkThreshold) || bulkThreshold < 1) { error = "Choose a bulk threshold of at least 1."; return false; }
        if (!bool.TryParse(Get("confirmBulk", current.ConfirmBulk.ToString()), out var confirmBulk)) { error = "Invalid bulk confirmation value."; return false; }
        if (placement is not ("right" or "left" or "floating")) { error = "Choose a placement."; return false; }
        if (!bool.TryParse(Get("autoOpen", current.AutoOpen.ToString()), out var autoOpen) || !bool.TryParse(Get("enabled", current.Enabled.ToString()), out var enabled)) { error = "Invalid on/off value."; return false; }
        var cli = Get("cli", current.Cli).Trim();
        if (assistant != (current.Assistant.Length == 0 ? "claude-code" : current.Assistant) && cli == current.Cli) cli = "";
        value = current with { Enabled = allowEnable ? enabled : current.Enabled, Assistant = assistant, Cli = cli, Port = port, Placement = placement, AutoOpen = autoOpen, ConfirmBulk = confirmBulk, BulkThreshold = bulkThreshold, Revision = current.Revision + 1 };
        return true;
    }

    public static string AssistantName(string kind) => kind == "codex" ? "Codex" : "Claude Code";
    public static string? DetectCli(string configured = "", string kind = "claude-code")
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
}

public static class PackageIdentity
{
    public const string Id = "70cf6f66-65ce-43a6-a6cb-00673c71558e";
    public const string Name = "Padmaster AI Assistant";
    public const string StoreKey = "settings";
    public static string StorePath(string root) => Path.Combine(root, "Local", "Plugins", ".data", Id, "values", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StoreKey))) + ".dat");
    public static string RootFromBridge(string directory)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!string.Equals(folder.Parent?.Name, "Plugins", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bridge must be installed in Plugins/PadMaster.");
        var root = folder.Parent!.Parent ?? throw new InvalidOperationException("LaunchBox root is missing.");
        if (root.Name.Equals("Core", StringComparison.OrdinalIgnoreCase)) root = root.Parent ?? root;
        return root.FullName;
    }
}

public sealed record AuthState(bool Known, bool SignedIn, string Reason, bool OtherAccount = false)
{
    public static readonly AuthState Unknown = new(false, false, "Sign-in could not be checked. Try opening the panel again.");
    public bool ShouldStop(bool busy) => Known && !SignedIn && !busy;
    public static AuthState ParseCodex(string text, int exitCode)
    {
        var status = text.Trim();
        if (exitCode == 0 && status == "Logged in using ChatGPT") return new(true, true, "");
        if (status.StartsWith("Logged in using an API key", StringComparison.OrdinalIgnoreCase)) return new(true, false, "A ChatGPT subscription is required. Sign in with ChatGPT instead of an API key.", true);
        if (status == "Not logged in") return new(true, false, "Sign in with your ChatGPT subscription.");
        return Unknown;
    }
    public static AuthState Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("loggedIn", out var logged) || logged.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Unknown;
            if (logged.ValueKind == JsonValueKind.False) return new(true, false, "Sign in with your Claude subscription.");
            var subscription = root.TryGetProperty("apiProvider", out var provider) && provider.GetString() == "firstParty" && root.TryGetProperty("authMethod", out var method) && method.GetString() == "claude.ai";
            return new(true, subscription, subscription ? "" : "A Claude subscription is required. Sign in with Claude, rather than a Console or API key login.", !subscription);
        }
        catch { return Unknown; }
    }
}

public sealed record SetupState(string Assistant, string? Cli, bool SignedIn, bool Suppressed = false, bool Checking = false, string Reason = "")
{
    public bool Ready => !Suppressed && !Checking && Assistant is "claude-code" or "codex" && !string.IsNullOrWhiteSpace(Cli) && SignedIn;
    public bool NeedsSetup => !Suppressed && !Checking && !Ready;
    public bool CanSignIn => NeedsSetup && Assistant is "claude-code" or "codex" && !string.IsNullOrWhiteSpace(Cli);
    public static bool ParseAuth(string json) => AuthState.Parse(json).SignedIn;
}

public static class FailureText
{
    public static string Folder => "The plugin folder is not writable. Allow writing to Plugins/PadMaster, then reopen the panel.";
    public static string Listener(Exception error, int port = 47800) => error is IOException or UnauthorizedAccessException ? Folder : error is System.Net.HttpListenerException { ErrorCode: 183 or 32 or 10048 } ? $"port {port} in use - change it in Configure" : "The local listener could not start. Check the port in Configure and restart LaunchBox.";
    public static string Configuration(Exception error) => error is IOException or UnauthorizedAccessException ? Folder : "The assistant could not start. Check the CLI path in Configure.";
}

public sealed class AuthCoordinator
{
    AuthState? _pending;
    public string ConnectedStatus { get; set; } = "connected";
    public string Status { get; private set; } = "offline";
    public bool Apply(AuthState auth, bool hasSession, bool busy, Action stop, Action<AuthState> show)
    {
        if (hasSession && (!auth.Known || (!auth.SignedIn && busy)))
        {
            _pending = auth.Known ? auth : _pending; Status = ConnectedStatus; return false;
        }
        _pending = null;
        if (hasSession && auth.ShouldStop(busy)) stop();
        show(auth); return true;
    }
    public void BetweenTurns(bool busy, Action stop, Action<AuthState> show)
    {
        if (_pending is not { } auth || !auth.ShouldStop(busy)) return;
        _pending = null; stop(); show(auth);
    }
    public void Reset() => _pending = null;
    public static string CheckingStatus(bool hasSession, string connected, string? cli, string listenerFailure) => listenerFailure.Length > 0 ? "offline · " + listenerFailure : hasSession ? connected : cli == null ? "assistant not found" : "checking assistant…";
}
