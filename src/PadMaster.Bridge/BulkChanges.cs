using System.Text.Json.Nodes;

namespace PadMaster;

public sealed record BulkPrompt(string Summary, int? Count, Action<bool> Answer);

public sealed class BulkChanges(Func<Settings> settings, Func<BulkPrompt, bool> show, Action<string, string?> completed)
{
    sealed record Pending(string Token, DateTime Expires, Func<JsonNode> Run);
    readonly object _gate = new();
    Pending? _pending;
    bool _running;
    long _generation;
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    public bool Waiting { get { lock (_gate) return _pending != null; } }
    public JsonNode Run(string verb, string type, int? count, Func<JsonNode> action)
    {
        var config = settings();
        if (!config.ConfirmBulk || count is { } known && known <= config.BulkThreshold) return action();
        Pending pending;
        lock (_gate)
        {
            if (_pending != null || _running) return Refused("another change is waiting for your answer");
            pending = _pending = new(Guid.NewGuid().ToString("N"), Now().AddMinutes(5), action);
        }
        var summary = (verb is "remove" or "clear" or "create" ? verb : "change") + " " + PlainType(type, true);
        bool shown;
        try { shown = show(new(summary, count, yes => Answer(pending, yes))); } catch { shown = false; }
        if (!shown)
        {
            lock (_gate) if (ReferenceEquals(_pending, pending)) _pending = null;
            return Refused("The confirmation could not be shown. Open the assistant and try again.");
        }
        lock (_gate) if (!ReferenceEquals(_pending, pending)) return Refused("The session changed. Nothing is waiting for approval.");
        return new JsonObject { ["held"] = true, ["summary"] = summary, ["count"] = count, ["token"] = pending.Token };
    }
    void Answer(Pending pending, bool yes)
    {
        long generation;
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending)) { completed("That change is no longer waiting for an answer.", null); return; }
            _pending = null; generation = _generation;
            if (Now() >= pending.Expires) { completed("That change expired. Nothing changed.", "The held change expired; nothing changed."); return; }
            _running = yes;
        }
        if (!yes) { completed("Nothing changed.", "The user clicked No; nothing changed."); return; }
        _ = Task.Run(() =>
        {
            JsonNode result;
            lock (_gate)
            {
                if (_generation != generation) { _running = false; completed("That change is no longer available. Nothing changed.", null); return; }
            }
            try { result = pending.Run(); }
            catch { result = new JsonObject { ["error"] = "The change could not finish. Check the library before trying again." }; }
            lock (_gate)
            {
                _running = false;
                if (_generation != generation) { completed("The previous change finished. Check your library.", null); return; }
            }
            var json = result.ToJsonString();
            if (json.Length > 6000) json = new JsonObject { ["completed"] = true, ["resultOmitted"] = "Result exceeded the notification limit; read the library to verify." }.ToJsonString();
            completed(result is not JsonObject obj || obj["error"] == null ? "The change was sent to LaunchBox." : "The change could not finish.", "The user clicked Yes on the held change; result: " + json);
        });
    }
    public void Invalidate(string reason)
    {
        bool waiting;
        lock (_gate) { _generation++; waiting = _pending != null; _pending = null; }
        if (waiting) completed(reason, null);
    }
    static JsonObject Refused(string message) => new() { ["refused"] = true, ["message"] = message };
    public static string PlainType(string? type, bool plural = false) => type switch
    {
        "IGame" => plural ? "games" : "a game", "IPlaylist" => plural ? "playlists" : "a playlist", "IPlaylistGame" => plural ? "playlist entries" : "a playlist entry",
        "IPlatform" => plural ? "platforms" : "a platform", "IEmulator" => plural ? "emulators" : "an emulator", "IPlatformCategory" => plural ? "platform categories" : "a platform category",
        "IGameController" => plural ? "controllers" : "a controller", _ => "library data"
    };
}
