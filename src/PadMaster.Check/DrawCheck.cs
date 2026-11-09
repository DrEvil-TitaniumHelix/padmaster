using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Linq;
using PadMaster;
using PadMaster.Readers;

static class DrawCheck
{
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("draw: " + message); }
    static string Home
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !(Directory.Exists(Path.Combine(d.FullName, "games")) && Directory.Exists(Path.Combine(d.FullName, "controllers")))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("no games + controllers folders above " + AppContext.BaseDirectory);
        }
    }

    public static int Draw(string[] args, TextWriter output)
    {
        string? root = null, pad = null; var open = false; var positional = new List<string>();
        for (var i = 0; i < args.Length; i++) { if (args[i] == "--root" && i + 1 < args.Length) root = args[++i]; else if (args[i] == "--controller" && i + 1 < args.Length) pad = args[++i]; else if (args[i] == "--open") open = true; else positional.Add(args[i]); }
        if (positional.Count != 2) { output.WriteLine("FAIL: --draw \"<Platform>\" \"<Game>\" [--root <LaunchBox dir>] [--controller <id>] [--open]"); return 1; }
        var (platform, game) = (positional[0], positional[1]); root ??= LaunchBoxDir.Value;
        var json = Mapping.GamePath(Home, platform, game);
        if (!File.Exists(json)) { output.WriteLine($"No mapping found for {game}."); output.WriteLine(json); return 2; }
        var read = Select.Read(root, platform, game);
        foreach (var note in read.Notes) output.WriteLine(note);
        if (read.Error != null) { output.WriteLine(read.Error); return 1; }
        if (read.Bindings.Count == 0) { output.WriteLine($"no gamepad bindings read for {platform} under {root}"); return 1; }
        var controller = Mapping.Controller(Home, pad); var logo = Mapping.Logo(root, platform, game); output.WriteLine("logo: " + (logo ?? "none"));
        var states = Mapping.Render(controller, json, read.Bindings, Path.Combine(Mapping.RenderDirectory(Home, platform, game), Path.GetFileName(controller)), read.Config, logo, EmulatorTitle(root, platform, game));
        foreach (var state in states)
        {
            output.WriteLine(state.BackPath == null ? $"{state.State.Name}: {state.FrontPath}" : $"{state.State.Name}: {state.FrontPath} + {state.BackPath}");
            if (state.State.Unbound.Count > 0) output.WriteLine($"  not bound: {string.Join(", ", state.State.Unbound)}");
            if (open) foreach (var png in new[] { state.FrontPath, state.BackPath }.Where(p => p != null)) Process.Start(new ProcessStartInfo(png!) { UseShellExecute = true });
        }
        return 0;
    }

    public static string EmulatorTitle(string root, string platform, string title)
    {
        var emulators = XDocument.Load(Path.Combine(root, "Data", "Emulators.xml")).Root!; var file = Path.Combine(root, "Data", "Platforms", platform + ".xml");
        var id = File.Exists(file) ? (string?)XDocument.Load(file).Root!.Elements("Game").FirstOrDefault(g => (string?)g.Element("Title") == title)?.Element("Emulator") : null;
        id = string.IsNullOrEmpty(id) ? (string?)emulators.Elements("EmulatorPlatform").Where(e => (string?)e.Element("Platform") == platform).OrderByDescending(e => (string?)e.Element("Default") == "true").FirstOrDefault()?.Element("Emulator") : id;
        return (string?)emulators.Elements("Emulator").FirstOrDefault(e => (string?)e.Element("ID") == id)?.Element("Title") ?? "";
    }

    static bool Painted(SkiaSharp.SKBitmap b, float from, float to) => Enumerable.Range(4, Renderer.BandH - 8).Any(y => Enumerable.Range((int)(b.Width * from), (int)(b.Width * (to - from))).Any(x => b.GetPixel(x, y) != Renderer.Band));
    public static bool BandPainted(string png, float from, float to) { using var b = SkiaSharp.SKBitmap.Decode(png); return Painted(b, from, to); }

    public static void Header()
    {
        const string nes = "Nintendo Entertainment System", ps2 = "Sony Playstation 2", blaster = "Blaster Master", eon = "007: Everything or Nothing"; var root = LaunchBoxDir.Value;
        var bm = Mapping.Logo(root, nes, blaster); Require(bm != null && bm.EndsWith(@"\Images\Nintendo Entertainment System\Clear Logo\North America\Blaster Master-01.png", StringComparison.OrdinalIgnoreCase), "Blaster Master logo = Clear Logo\\North America: " + bm);
        var eonLogo = Mapping.Logo(root, ps2, eon); Require(eonLogo != null && eonLogo.EndsWith(@"\Images\Sony Playstation 2\Clear Logo\007_ Everything or Nothing-01.png", StringComparison.OrdinalIgnoreCase), "EoN logo = Clear Logo root: " + eonLogo);
        var scratch = Path.Combine(Path.GetTempPath(), "PadMaster-logo-" + Guid.NewGuid().ToString("N")); var stem = Mapping.FileName(eon);
        string Put(string relative) { var path = Path.Combine(scratch, "Images", ps2, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(bm!, path); return path; }
        Require(Mapping.Logo(scratch, ps2, eon) == null, "no image -> null");
        foreach (var relative in new[] { @"Box - Front\North America\" + stem + "-01.jpg", @"Banner\" + stem + "-01.png", @"Arcade - Marquee\" + stem + "-01.png", @"Clear Logo\" + stem + "-01.png" })
        { var put = Put(relative); Require(Mapping.Logo(scratch, ps2, eon) == put, "fallback order: " + relative); }
        Require(Mapping.Logo([Path.Combine(scratch, "missing.png"), eonLogo!]) == eonLogo, "explicit candidate list: first existing");
        Directory.Delete(scratch, true);
        var controller = Mapping.Controller(Home); var json = Mapping.GamePath(Home, nes, blaster); var read = Select.Read(root, nes, blaster);
        var state = Mapping.Compose(controller, json, read.Bindings)[0]; var header = Mapping.Header(json, bm, EmulatorTitle(root, nes, blaster)) with { State = state.Name };
        Require(header.Title == blaster && header.Line2 == nes + " · " + state.Name && (header with { Back = true }).Line2.EndsWith(" · back view") && header.Source == "manual Blaster Master.pdf" && header.Emulator.Length > 0, "header text: " + header);
        var plain = Renderer.Measure(controller, state.Front, "front"); var banded = Renderer.Measure(controller, state.Front, "front", header);
        Require(banded.Width == plain.Width && banded.Height == plain.Height + Renderer.BandH + Renderer.FootH && banded.OffsetX == plain.OffsetX && banded.OffsetY == plain.OffsetY + Renderer.BandH, "canvas grows by band + footer only");
        Require(banded.Labels.Count == plain.Labels.Count && banded.Labels.Zip(plain.Labels).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y + Renderer.BandH && p.First.Box.Top == p.Second.Box.Top + Renderer.BandH && p.First.Box.Left == p.Second.Box.Left), "anchors offset by band");
        using var png = SkiaSharp.SKBitmap.Decode(Renderer.Render(controller, state.Front, "front", header)); Require(png.Width == banded.Width && png.Height == banded.Height, "PNG matches measured canvas");
        Require(Painted(png, 0, .3f) && Painted(png, .7f, 1), "title left and Clear Logo right inside the band");
        Require(banded.Labels.All(l => png.GetPixel((int)l.X, (int)l.Y) == Renderer.Accent), "dots still on the buttons below the band");
        using var textOnly = SkiaSharp.SKBitmap.Decode(Renderer.Render(controller, state.Front, "front", header with { Logo = null })); Require(Painted(textOnly, 0, .3f) && !Painted(textOnly, .7f, 1), "missing logo -> text only");
        Console.WriteLine("OK  header: Blaster Master front has title/platform/state + Clear Logo; EoN Clear Logo; fallback marquee/banner/box; missing logo -> text only; anchors offset by band");
    }

    public static void Run()
    {
        const string nes = "Nintendo Entertainment System", title = "Blaster Master";
        Dictionary<string, string> Once()
        {
            var w = new StringWriter(); Require(Draw([nes, title, "--root", LaunchBoxDir.Value], w) == 0, "Blaster Master exit 0: " + w);
            var paths = w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.EndsWith(".png") && !l.StartsWith("logo: ")).Select(l => l[(l.IndexOf(": ") + 2)..]).ToArray();
            Require(paths.Length == 2 && paths.All(p => p.EndsWith(".png") && File.Exists(p)), "two existing PNG paths printed: " + w);
            return paths.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        }
        var first = Once();
        foreach (var p in first.Keys.Append(Path.Combine(Path.GetDirectoryName(first.Keys.First())!, "cache.sha256"))) File.Delete(p);
        Require(Once().SequenceEqual(first), "re-rendered PNG bytes identical");
        Console.WriteLine($"OK  draw: {title} -> 2 PNGs");
        var missing = new StringWriter(); var code = Draw([nes, "Nobody: Home?", "--root", LaunchBoxDir.Value], missing);
        var lines = missing.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(code == 2 && lines.Length == 2 && lines[0] == "No mapping found for Nobody: Home?." && lines[1] == Mapping.GamePath(Home, nes, "Nobody: Home?"), "missing game output: " + missing);
        Console.WriteLine("OK  draw: missing game -> No mapping found, exit 2");
        Header();
        Controllers();
    }

    // Every controller folder must be drawable: all standard inputs anchored, photos present, every dot on its photo.
    static void Controllers()
    {
        string[] standard = ["A", "B", "X", "Y", "lb", "rb", "lt", "rt", "back", "start", "ls", "rs", "dpad_up", "dpad_down", "dpad_left", "dpad_right"];
        var dirs = Directory.GetDirectories(Path.Combine(Home, "controllers")); Require(dirs.Length >= 3, "three controllers: " + dirs.Length);
        foreach (var dir in dirs)
        {
            var id = Path.GetFileName(dir); Require(Mapping.Controller(Home, id) == dir, "pick by id: " + id);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "anchors.json")))!; var anchors = doc["anchors"]!.AsObject();
            Require(standard.All(anchors.ContainsKey), id + " anchors every standard input: " + string.Join(",", standard.Where(k => !anchors.ContainsKey(k))));
            foreach (var view in new[] { "front", "back" })
            {
                using var photo = SkiaSharp.SKBitmap.Decode(Path.Combine(dir, doc["photos"]![view]!.GetValue<string>())); Require(photo != null, id + " photo decodes: " + view);
                foreach (var a in anchors.Where(a => a.Value!["view"]!.GetValue<string>() == view)) Require(a.Value!["x"]!.GetValue<int>() is var x && x >= 0 && x < photo!.Width && a.Value["y"]!.GetValue<int>() is var y && y >= 0 && y < photo.Height, $"{id} {a.Key} on its photo");
            }
        }
        Require(Path.GetFileName(Mapping.Controller(Home)) == File.ReadAllText(Path.Combine(Home, "controllers", "active.txt")).Trim(), "active.txt picks the pad");
        try { Mapping.Controller(Home, "no-such-pad"); throw new InvalidOperationException("unknown pad accepted"); } catch (InvalidDataException) { }
        Console.WriteLine($"OK  draw: {dirs.Length} controllers complete; active.txt and --controller pick the pad; unknown pad refused");
    }
}
