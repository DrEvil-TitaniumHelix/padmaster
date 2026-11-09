using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PadMaster;

public sealed record MappingState(string Name, Dictionary<string, string> Front, Dictionary<string, string> Back, IReadOnlyList<string> Unbound);
public sealed record RenderedState(MappingState State, string FrontPath, string? BackPath);

public static class Mapping
{
    public static string FileName(string value)
    {
        var name = new string(value.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        for (var i = name.Length - 1; i >= 0 && name[i] is '.' or ' '; i--) name = name[..i] + '_' + name[(i + 1)..];
        if (name.Length == 0) return "_";
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || Enumerable.Range(1, 9).Any(n => stem == "COM" + n || stem == "LPT" + n) ? "_" + name : name;
    }

    // The active pad = --controller id, else the one named in controllers\active.txt, else the only folder. Never a guess among several.
    public static string Controller(string root, string? id = null)
    {
        var folder = Path.Combine(root, "controllers"); var active = Path.Combine(folder, "active.txt"); var all = Directory.GetDirectories(folder);
        id ??= File.Exists(active) ? File.ReadAllText(active).Trim() : null;
        if (id == null) return all.Length == 1 ? all[0] : throw new InvalidDataException($"{all.Length} controllers and no controllers\\active.txt");
        return all.FirstOrDefault(d => string.Equals(Path.GetFileName(d), id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("no controller folder named " + id);
    }

    public static string GamePath(string root, string platform, string title) => Path.Combine(root, "games", FileName(platform), FileName(title) + ".json");
    // After a controller's anchors change: its cached pictures for every game go, so the next draw is fresh (the cache key covers anchors.json too; this is belt and braces for the LB artwork refresh).
    public static void ClearRenders(string root, string controllerId) { var renders = Path.Combine(root, "renders"); if (!Directory.Exists(renders)) return; foreach (var dir in Directory.GetDirectories(renders, controllerId, SearchOption.AllDirectories)) try { Directory.Delete(dir, true); } catch (IOException) { } }
    public static string RenderDirectory(string root, string platform, string title) => Path.Combine(root, "renders", FileName(platform), FileName(title));
    // The rule every game file passes, whether a person's batch or the AI wrote it. Null = good.
    public static string? Problem(JsonObject game, IEnumerable<string> keys)
    {
        var allowed = keys.ToHashSet(); var type = game["source"]?["type"]?.GetValue<string>(); var path = game["source"]?["path"]?.GetValue<string>() ?? "";
        if (type is not ("manual" or "web") || path.Length == 0 || (type == "web") != path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "source must be a manual path or a web address";
        if (game["states"] is not JsonArray states || states.Count is < 1 or > 6) return "one to six states";
        if (game["evidence"] is not JsonObject evidence) return "evidence missing";
        foreach (var state in states)
        {
            var name = state?["name"]?.GetValue<string>() ?? ""; if (name.Length == 0 || state!["controls"] is not JsonObject controls || controls.Count == 0) return "state needs a name and controls";
            foreach (var control in controls)
            {
                var where = $"{name}/{control.Key}";
                if (control.Key.Split('+', '/').Any(k => !allowed.Contains(k.Trim()))) return where + ": button not on this system";
                var label = control.Value?.GetValue<string>() ?? "";
                if (label.Length == 0 || label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w != "/") > 6) return where + ": label must be 1 to 6 words";
                if (label.Equals("primary action", StringComparison.OrdinalIgnoreCase)) return where + ": generic label";
                if (!System.Text.RegularExpressions.Regex.IsMatch(evidence[where]?.GetValue<string>() ?? "", @"^(p(df)?\d+( [^:]*)?|https?://\S+): .+")) return where + ": no source quote";
            }
        }
        return null;
    }

    public static void WriteGame(string root, JsonObject game)
    {
        var path = GamePath(root, game["platform"]!.GetValue<string>(), game["game"]!.GetValue<string>());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, game.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static readonly string[] LogoTypes = ["Clear Logo", "Arcade - Marquee", "Banner", "Box - Front"];
    public static string? Logo(string launchBoxRoot, string platform, string title)
    {
        var pattern = FileName(title) + "-01.*";
        IEnumerable<string> Files(string dir, SearchOption option) => Directory.EnumerateFiles(dir, pattern, option).Order(StringComparer.Ordinal);
        return LogoTypes.Select(t => Path.Combine(launchBoxRoot, "Images", FileName(platform), t)).Where(Directory.Exists)
            .SelectMany(dir => Files(dir, SearchOption.TopDirectoryOnly).Concat(Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal).SelectMany(sub => Files(sub, SearchOption.AllDirectories)))).FirstOrDefault();
    }
    public static string? Logo(IEnumerable<string> candidates) => candidates.FirstOrDefault(File.Exists);
    public static string SourceText(string type, string path) => type + " " + (type == "web" ? new Uri(path).Host : Path.GetFileName(path));
    public static Renderer.Header Header(string gameJsonPath, string? logo, string? emulator)
    {
        var game = JsonNode.Parse(File.ReadAllText(gameJsonPath))!; var source = game["source"]!;
        return new(game["game"]!.GetValue<string>(), game["platform"]!.GetValue<string>(), "", false, logo, SourceText(source["type"]!.GetValue<string>(), source["path"]!.GetValue<string>()), emulator ?? "");
    }

    public static IReadOnlyList<MappingState> Compose(string controllerDir, string gameJsonPath, IReadOnlyDictionary<string, string> bindings)
    {
        var frontKeys = Renderer.Keys(controllerDir, "front").ToHashSet(); var backKeys = Renderer.Keys(controllerDir, "back").ToHashSet();
        var game = JsonNode.Parse(File.ReadAllText(gameJsonPath))!;
        var states = new List<MappingState>();
        foreach (var state in game["states"]!.AsArray())
        {
            var front = new Dictionary<string, string>(); var back = new Dictionary<string, string>(); var unbound = new List<string>();
            bool OnPad(string key) => frontKeys.Contains(key) || backKeys.Contains(key);
            // A pad with a joystick and no d-pad (arcade panel, single-stick pad) takes the d-pad's labels on the stick.
            string Resolve(string key) => OnPad(key) ? key : key.StartsWith("dpad_", StringComparison.Ordinal) && OnPad("ls") ? "ls" : key;
            bool Bound(string input) => bindings.TryGetValue(input, out var key) && OnPad(Resolve(key));
            void Add(string input, string text)
            {
                var key = Resolve(bindings[input]); var labels = frontKeys.Contains(key) ? front : back;
                labels[key] = labels.TryGetValue(key, out var own) ? own + " / " + text : text;
            }
            foreach (var control in state!["controls"]!.AsObject().OrderBy(c => c.Key.Contains('+')))
            {
                var text = control.Value!.GetValue<string>();
                foreach (var input in control.Key.Split('/', StringSplitOptions.TrimEntries))
                {
                    var combo = input.Split('+', StringSplitOptions.TrimEntries);
                    if (!combo.All(Bound)) { unbound.Add($"{input} ({text})"); continue; }
                    Add(combo[^1], combo.Length > 1 ? $"{input}: {text}" : text);
                }
            }
            states.Add(new(state["name"]!.GetValue<string>(), front, back, unbound));
        }
        return states;
    }

    public static IReadOnlyList<RenderedState> Render(string controllerDir, string gameJsonPath, IReadOnlyDictionary<string, string> bindings, string outputDir, string? retroarchConfig = null, string? logo = null, string? emulator = null)
    {
        var states = Compose(controllerDir, gameJsonPath, bindings); var header = Header(gameJsonPath, logo, emulator);
        var anchorsPath = Path.Combine(controllerDir, "anchors.json"); var anchors = JsonNode.Parse(File.ReadAllText(anchorsPath))!;
        var inputs = new[] { gameJsonPath, anchorsPath, retroarchConfig, logo, typeof(Renderer).Assembly.Location }.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Cast<string>()
            .Concat(anchors["photos"]!.AsObject().Select(p => Path.Combine(controllerDir, p.Value!.GetValue<string>()))).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in inputs) hash.AppendData(File.ReadAllBytes(file));
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bindings.OrderBy(b => b.Key)) + header.Text));
        var signature = Convert.ToHexString(hash.GetHashAndReset()); var newest = inputs.Max(File.GetLastWriteTimeUtc);
        Directory.CreateDirectory(outputDir); var stamp = Path.Combine(outputDir, "cache.sha256");
        var changed = !File.Exists(stamp) || File.ReadAllText(stamp) != signature;
        var result = states.Select(s => new RenderedState(s, Path.Combine(outputDir, FileName(s.Name) + ".png"), s.Back.Count == 0 ? null : Path.Combine(outputDir, FileName(s.Name) + ".back.png"))).ToArray();
        var paths = result.SelectMany(s => s.BackPath == null ? new[] { s.FrontPath } : new[] { s.FrontPath, s.BackPath }).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidDataException("State names must have distinct file names.");
        void Write(string path, Dictionary<string, string> labels, string view, string state) { if (changed || !File.Exists(path) || File.GetLastWriteTimeUtc(path) < newest) File.WriteAllBytes(path, Renderer.Render(controllerDir, labels, view, header with { State = state, Back = view == "back" })); }
        foreach (var state in result) { Write(state.FrontPath, state.State.Front, "front", state.State.Name); if (state.BackPath != null) Write(state.BackPath, state.State.Back, "back", state.State.Name); }
        if (changed) File.WriteAllText(stamp, signature);
        return result;
    }
}
