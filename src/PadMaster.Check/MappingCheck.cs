using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using PadMaster;
using PadMaster.Controls;
using PadMaster.Readers;
using Unbroken.LaunchBox.Plugins.Data;

static class MappingCheck
{
    const string Nes = "Nintendo Entertainment System", Controller = "gulikit-kk3-max", Blaster = "Blaster Master";
    static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("mapping: " + message); }
    internal static string Scratch() { var path = Path.Combine(Path.GetTempPath(), "PadMaster-mapping-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    internal static void Copy(string source, string destination)
    {
        foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { var target = Path.Combine(destination, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target); }
    }
    static T Proxy<T>(Func<MethodInfo, object?[], object?> call) where T : class { var proxy = DispatchProxy.Create<T, ApiProxy>(); ((ApiProxy)(object)proxy).Call = call; return proxy; }
    internal static IGame Game(string title, string emulatorId, string rom, string command, Func<object?[], object?> allocate, string platform = Nes, string? logo = null) => Proxy<IGame>((m, a) => m.Name switch
    {
        "get_Title" => title, "GetManualPath" => "", "get_Platform" => platform, "get_EmulatorId" => emulatorId, "get_ApplicationPath" => rom, "get_CommandLine" => command, "GetNextAvailableImageFilePath" => allocate(a),
        "GetAllImagesWithDetails" => logo != null && (string?)a[0] == ImageTypes.ClearLogo ? new[] { new ImageDetails(logo, ImageTypes.ClearLogo, "North America") } : [],
        _ => throw new InvalidOperationException("unexpected game API: " + m.Name)
    });
    internal static (IGame Game, IEmulator Emulator) Library(string root, string title, Func<object?[], object?> allocate, string platform = Nes, string? logo = null)
    {
        var game = XDocument.Load(Path.Combine(root, "Data", "Platforms", platform + ".xml")).Root!.Elements("Game").Single(g => (string?)g.Element("Title") == title);
        var emulators = XDocument.Load(Path.Combine(root, "Data", "Emulators.xml")).Root!;
        var id = (string?)game.Element("Emulator") is { Length: > 0 } own ? own : (string)emulators.Elements("EmulatorPlatform").Where(e => (string?)e.Element("Platform") == platform).OrderByDescending(e => (string?)e.Element("Default") == "true").First().Element("Emulator")!;
        var em = emulators.Elements("Emulator").Single(e => (string?)e.Element("ID") == id);
        var platforms = emulators.Elements("EmulatorPlatform").Where(p => (string?)p.Element("Emulator") == id).Select(p => Proxy<IEmulatorPlatform>((m, _) => m.Name switch
        {
            "get_Platform" => (string?)p.Element("Platform") ?? "", "get_CommandLine" => (string?)p.Element("CommandLine"), _ => throw new InvalidOperationException("unexpected platform API: " + m.Name)
        })).ToArray();
        var emulator = Proxy<IEmulator>((m, _) => m.Name switch
        {
            "get_Id" => id, "get_Title" => (string?)em.Element("Title") ?? "", "get_ApplicationPath" => (string?)em.Element("ApplicationPath") ?? "", "get_CommandLine" => (string?)em.Element("CommandLine") ?? "", "GetAllEmulatorPlatforms" => platforms,
            _ => throw new InvalidOperationException("unexpected emulator API: " + m.Name)
        });
        return (Game(title, id, (string?)game.Element("ApplicationPath") ?? "", (string?)game.Element("CommandLine") ?? "", allocate, platform, logo), emulator);
    }
    internal static string Allocate(string folder, string title, object?[] args)
    {
        Require(args.Length == 3 && (string?)args[0] == ".png" && (string?)args[1] == ImageTypes.ArcadeControlsInformation && args[2] == null, "exact image API arguments");
        Directory.CreateDirectory(folder); var index = 1; string path;
        do { path = Path.Combine(folder, $"{Mapping.FileName(title)}-{index++:00}.png"); } while (File.Exists(path));
        return path;
    }
    static Dictionary<string, string> Hashes(string folder) => Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) != ".log").ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);
    static void Unchanged(IReadOnlyDictionary<string, string> before) { foreach (var pair in before) Require(File.Exists(pair.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value, "untouched file: " + pair.Key); }
    static bool Pending(string manifest) => JsonNode.Parse(File.ReadAllText(manifest))?["refreshPending"]?.GetValue<bool>() ?? false;

    public static IReadOnlyList<RenderedState> RenderGame(bool announce = true)
    {
        var emulator = RetroArch.FromXml(LaunchBoxDir.Value, Nes) ?? throw new InvalidOperationException("NES emulator missing");
        var game = XDocument.Load(Path.Combine(LaunchBoxDir.Value, "Data", "Platforms", Nes + ".xml")).Root!.Elements("Game").Single(g => (string?)g.Element("Title") == Blaster);
        var rom = Path.GetFullPath((string)game.Element("ApplicationPath")!, LaunchBoxDir.Value);
        var bindings = RetroArch.Read(emulator.App, emulator.CommandLine, rom, Nes); Require(bindings.Count > 0, "real RetroArch bindings");
        var controller = Path.Combine(Repo, "controllers", Controller); var json = Mapping.GamePath(Repo, Nes, Blaster); var output = Path.Combine(Mapping.RenderDirectory(Repo, Nes, Blaster), Controller);
        var logo = Mapping.Logo(LaunchBoxDir.Value, Nes, Blaster); var title = DrawCheck.EmulatorTitle(LaunchBoxDir.Value, Nes, Blaster);
        var states = Mapping.Render(controller, json, bindings, output, Path.Combine(Path.GetDirectoryName(emulator.App)!, "retroarch.cfg"), logo, title);
        var doc = JsonNode.Parse(File.ReadAllText(json))!; Require(states.Count == doc["states"]!.AsArray().Count, "every Blaster Master state rendered");
        foreach (var state in states)
        {
            var header = Mapping.Header(json, logo, title) with { State = state.State.Name };
            var png = Renderer.Render(controller, state.State.Front, "front", header); Require(png.SequenceEqual(Renderer.Render(controller, state.State.Front, "front", header)) && png.SequenceEqual(File.ReadAllBytes(state.FrontPath)), "two deterministic front renders");
            if (state.BackPath != null) { var back = Renderer.Render(controller, state.State.Back, "back", header with { Back = true }); Require(back.SequenceEqual(Renderer.Render(controller, state.State.Back, "back", header with { Back = true })) && back.SequenceEqual(File.ReadAllBytes(state.BackPath)), "two deterministic back renders"); }
            if (bindings.GetValueOrDefault("A") == "B") Require(state.State.Front["B"].StartsWith(doc["states"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == state.State.Name)!["controls"]!["A"]!.GetValue<string>(), StringComparison.Ordinal), "NES A text on XInput B");
        }
        if (announce) { Fixture(); Layouts(); Console.WriteLine($"OK  render game: {Blaster} x {states.Count} states -> {Path.GetRelativePath(Repo, output)}\\<state>.png"); }
        return states;
    }

    static Renderer.Layout Layout(string controller, Dictionary<string, string> actions, string view, string context)
    {
        var layout = Renderer.Measure(controller, actions, view);
        using var bitmap = SkiaSharp.SKBitmap.Decode(Renderer.Render(controller, actions, view));
        Require(bitmap.Width == layout.Width && bitmap.Height == layout.Height, context + ": measured canvas equals PNG");
        foreach (var label in layout.Labels)
        {
            var box = label.Box;
            Require(box.Left >= 1 && box.Top >= 1 && box.Right <= layout.Width - 1 && box.Bottom <= layout.Height - 1, $"{context}/{label.Key}: label and stroke inside canvas");
            foreach (var other in layout.Labels.Where(a => a.Key != label.Key))
                Require(box.Right + 1 <= other.Box.Left - 1 || other.Box.Right + 1 <= box.Left - 1 || box.Bottom + 1 <= other.Box.Top - 1 || other.Box.Bottom + 1 <= box.Top - 1, $"{context}: boxes {label.Key}/{other.Key} overlap");
        }
        return layout;
    }
    public static void Layouts()
    {
        var files = Directory.GetFiles(Path.Combine(Repo, "games"), "*.json", SearchOption.AllDirectories); var count = 0; var controller = Path.Combine(Repo, "controllers", Controller);
        foreach (var json in files)
        {
            var doc = JsonNode.Parse(File.ReadAllText(json))!; var platform = doc["platform"]!.GetValue<string>(); var title = doc["game"]!.GetValue<string>();
            Require(XDocument.Load(Path.Combine(LaunchBoxDir.Value, "Data", "Platforms", platform + ".xml")).Root!.Elements("Game").Count(g => (string?)g.Element("Title") == title) == 1, title + ": one LaunchBox game in " + platform);
            var read = Select.Read(LaunchBoxDir.Value, platform, title); var bindings = read.Bindings; Require(read.Error == null && bindings.Count > 0, title + ": real bindings for layout check: " + read.Error);
            foreach (var state in Mapping.Compose(controller, json, bindings))
            {
                Layout(controller, state.Front, "front", title + "/" + state.Name + "/front"); Layout(controller, state.Back, "back", title + "/" + state.Name + "/back"); count++;
            }
        }
        var longText = string.Join(" ", Enumerable.Repeat("Long control description", 12));
        foreach (var (view, key) in new[] { ("back", "top_left"), ("front", "window") })
        {
            var layout = Layout(controller, new() { [key] = longText }, view, "growing " + view);
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(controller, "anchors.json")))!;
            using var photo = SkiaSharp.SKBitmap.Decode(Path.Combine(controller, doc["photos"]![view]!.GetValue<string>()));
            Require(layout.Height > photo.Height && (view != "back" || layout.OffsetY > 0), "canvas grows above/below photo for long labels");
            Require(string.Join(" ", layout.Labels.Single().Lines) == longText, "growing label retains all text");
        }
        Console.WriteLine($"OK  mapping layout: {files.Length} games x {count} states, real front/back boxes never overlap or leave canvas; long top/bottom labels grow canvas");
    }

    static void Fixture()
    {
        var scratch = Scratch(); var controller = Path.Combine(scratch, "controllers", Controller); Copy(Path.Combine(Repo, "controllers", Controller), controller);
        var game = JsonNode.Parse("""{"game":"Combo: Test?","platform":"Nintendo Entertainment System","source":{"type":"manual","path":"fixture.pdf"},"states":[{"name":"One","controls":{"Down+A":"Drop","A":"Jump","B":"Fire","Left/Right":"Walk","Select":"Pause","Up+A":"Aim"}}]}""")!.AsObject();
        Mapping.WriteGame(scratch, game); var json = Mapping.GamePath(scratch, Nes, "Combo: Test?"); Require(Path.GetFileName(json) == "Combo_ Test_.json" && File.Exists(json), "writer/reader share filename coercion");
        var bindings = new Dictionary<string, string> { ["A"] = "B", ["B"] = "rb", ["Left"] = "dpad_left", ["Down"] = "dpad_down" };
        var state = Mapping.Compose(controller, json, bindings).Single();
        Require(state.Front.Count == 2 && state.Front["B"] == "Jump / Down+A: Drop" && state.Front["dpad_left"] == "Walk" && state.Back.Single().Key == "rb" && state.Back["rb"] == "Fire", "base before combo, split direction, back anchor");
        Require(state.Unbound.SequenceEqual(new[] { "Right (Walk)", "Select (Pause)", "Up+A (Aim)" }), "unbound controls and incomplete combo omitted from drawing");
        bindings["Right"] = "dpad_right"; state = Mapping.Compose(controller, json, bindings).Single(); Require(state.Front["dpad_right"] == state.Front["dpad_left"], "split direction duplicates sourced text");
        bindings["Select"] = "unknown"; Require(Mapping.Compose(controller, json, bindings).Single().Unbound.Contains("Select (Pause)"), "unknown anchor remains unbound");
        // a pad with a joystick and no d-pad (arcade panel): the d-pad's labels land on the stick, nothing goes unbound for it
        var stickPad = Path.Combine(scratch, "stick-pad"); Directory.CreateDirectory(stickPad); foreach (var f in Directory.GetFiles(controller)) File.Copy(f, Path.Combine(stickPad, Path.GetFileName(f)));
        var stickAnchors = JsonNode.Parse(File.ReadAllText(Path.Combine(stickPad, "anchors.json")))!.AsObject(); foreach (var d in new[] { "dpad_up", "dpad_left", "dpad_down", "dpad_right" }) stickAnchors["anchors"]!.AsObject().Remove(d);
        File.WriteAllText(Path.Combine(stickPad, "anchors.json"), stickAnchors.ToJsonString());
        var onStick = Mapping.Compose(stickPad, json, bindings).Single();
        Require(onStick.Front["ls"] == "Walk / Walk" && !onStick.Front.ContainsKey("dpad_left") && !onStick.Unbound.Any(u => u.StartsWith("Left") || u.StartsWith("Right")) && onStick.Front["B"] == "Jump / Down+A: Drop", "joystick-only pad takes the d-pad labels on ls");
        var config = Path.Combine(scratch, "retroarch.cfg"); File.WriteAllText(config, "fixture"); var output = Path.Combine(scratch, "cache");
        var rendered = Mapping.Render(controller, json, bindings, output, config).Single(); Require(rendered.BackPath != null && File.Exists(rendered.BackPath), "back only when used");
        var time = File.GetLastWriteTimeUtc(rendered.FrontPath); Mapping.Render(controller, json, bindings, output, config); Require(File.GetLastWriteTimeUtc(rendered.FrontPath) == time, "unchanged cached PNG reused");
        foreach (var dependency in new[] { json, Path.Combine(controller, "anchors.json"), config })
        {
            var prior = File.GetLastWriteTimeUtc(dependency); var cached = DateTime.UtcNow.AddMinutes(1); File.SetLastWriteTimeUtc(rendered.FrontPath, cached); File.SetLastWriteTimeUtc(rendered.BackPath!, cached);
            Mapping.Render(controller, json, bindings, output, config); Require(File.GetLastWriteTimeUtc(rendered.FrontPath) == cached, "cache remains warm before dependency touch");
            File.SetLastWriteTimeUtc(dependency, cached.AddMinutes(1)); Mapping.Render(controller, json, bindings, output, config);
            Require(File.GetLastWriteTimeUtc(rendered.FrontPath) != cached && File.GetLastWriteTimeUtc(rendered.BackPath!) != cached, "cache invalidated by " + Path.GetFileName(dependency)); File.SetLastWriteTimeUtc(dependency, prior);
        }
        var bytes = File.ReadAllBytes(rendered.FrontPath); bindings["A"] = "X"; Mapping.Render(controller, json, bindings, output, config); Require(!bytes.SequenceEqual(File.ReadAllBytes(rendered.FrontPath)), "changed emulator/remap bindings invalidate cache");
        Require(RetroArch.Read(Path.Combine(Path.GetDirectoryName(RetroArch.FromXml(LaunchBoxDir.Value, Nes)!.Value.App)!, "dolphin.exe"), "-L cores/nestopia_libretro.dll", null, Nes).Count == 0, "non-RetroArch reader returns nothing");
        Console.WriteLine("OK  mapping composer/cache: directions, combos, unbound, back, safe filenames, json/anchors/config/binding invalidation");
    }

    public static void Window(string? root = null)
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "window check STA");
        root ??= Directory.Exists(@"C:\LaunchBox-Test") ? @"C:\LaunchBox-Test" : LaunchBoxDir.Value;
        ControlsPlugin.IsEnabled = _ => true;
        var scratch = Scratch(); Copy(Path.Combine(Repo, "controllers"), Path.Combine(scratch, "controllers")); Copy(Path.Combine(Repo, "games"), Path.Combine(scratch, "games"));
        var images = Path.Combine(scratch, "Images", Nes, ImageTypes.ArcadeControlsInformation); var allocations = 0; var refreshes = 0;
        var logo = Mapping.Logo(LaunchBoxDir.Value, Nes, Blaster) ?? throw new InvalidOperationException("mapping: Blaster Master Clear Logo for the API fake");
        var library = Library(root, "Contra", args => { allocations++; return Allocate(images, "Contra", args); }, logo: logo);
        IEmulator? Find(string id) { Require(id == library.Game.EmulatorId, "selected game's EmulatorId"); return library.Emulator; }
        var window = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
        Require(!window.IsVisible && window.Title == "Contra — GuliKit KK3 Max", "Contra title and no visible window");
        Require(window.Content is MappingView, "shared OnSelected MappingView: " + (window.Content as TextBlock)?.Text); var view = (MappingView)window.Content;
        var states = JsonNode.Parse(File.ReadAllText(Mapping.GamePath(Repo, Nes, "Contra")))!["states"]!.AsArray();
        Require(view.Cells.Count == states.Count && view.StateGrid.Columns == Math.Min(states.Count, 3) && view.Controller.Items.Count == 4 && (string)view.Controller.SelectedItem == "GuliKit KK3 Max", "all states in grid, dropdown lists the three pads + Add my controller with the drawn one selected");
        for (var i = 0; i < states.Count; i++)
        {
            Require((string)view.Cells[i].Header == states[i]!["name"]!.GetValue<string>(), "cell title");
            var pictures = ((StackPanel)view.Cells[i].Content).Children.OfType<Image>().ToArray(); Require(pictures.Length > 0 && pictures.All(p => p.Source != null), "loaded image sources");
            Require(Artwork.HasMarker(File.ReadAllBytes(view.Artwork!.Written[view.States[i].State.Name]), Artwork.Marker("Contra", Controller, view.States[i].State.Name)), "Contra artwork marker");
        }
        Require(view.Source.Text == "Source: manual Nintendo Entertainment System/Contra.pdf" && refreshes == 1 && allocations == states.Count, "source, API files, RefreshData callback");
        Require(view.States.All(s => DrawCheck.BandPainted(s.FrontPath, 0, .3f) && DrawCheck.BandPainted(s.FrontPath, .7f, 1)), "header title + API Clear Logo on every rendered state");
        Require(view.States[0].State.Front["B"] == "Jump / Up+A: Jump upward / Down+A: Climb down", "game/platform command line resolves NES A->B");
        var times = view.Artwork!.Written.Values.ToDictionary(p => p, File.GetLastWriteTimeUtc);
        var repeated = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
        Require(repeated.Content is MappingView { Artwork.Writes: 0, ArtworkError: null } && refreshes == 1 && allocations == states.Count && times.All(t => File.GetLastWriteTimeUtc(t.Key) == t.Value), "identical viewer reopen: zero writes, unchanged mtimes, no refresh");
        var missing = Game("Missing: Game?", library.Game.EmulatorId, library.Game.ApplicationPath, "", _ => throw new Exception("must not write missing artwork"));
        var absent = ControlsPlugin.BuildWindow(missing, scratch, root, Find, () => refreshes++); Require(absent.Content is TextBlock { Text: "No mapping found for Missing: Game?." }, "exact no-json content only");
        var offered = ControlsPlugin.BuildWindow(missing, scratch, root, Find, () => refreshes++, offer: true); Require(offered.Content is StackPanel asked && asked.Children.OfType<TextBlock>().Single().Text.Contains("Generate one now?") && asked.Children.OfType<StackPanel>().Single().Children.Count == 2, "unmapped game with a readable emulator is offered a map");
        var unreadable = ControlsPlugin.BuildWindow(missing, scratch, root, _ => null, () => refreshes++, offer: true); Require(unreadable.Content is TextBlock { Text: var why } && why.Contains("doesn't read this game's emulator's button settings"), "no emulator: says why, offers nothing");
        var other = Proxy<IEmulator>((m, _) => m.Name switch { "get_ApplicationPath" => "Emulators/RetroArch/dolphin.exe", "get_CommandLine" => "", "GetAllEmulatorPlatforms" => Array.Empty<IEmulatorPlatform>(), _ => throw new Exception("must not read unsupported emulator") });
        var unsupported = ControlsPlugin.BuildWindow(library.Game, scratch, root, _ => other, () => refreshes++); Require(unsupported.Content is TextBlock { Text: "No mapping found for Contra." }, "non-RetroArch exact missing content");
        var failure = ControlsPlugin.BuildWindow(library.Game, scratch, root, _ => throw new IOException("fixture error"), () => refreshes++); Require(failure.Content is TextBlock { Text: "fixture error" } && !failure.IsVisible, "error text in headless nonmodal window");
        var cold = Path.Combine(scratch, "unsupported"); Copy(Path.Combine(Repo, "controllers"), Path.Combine(cold, "controllers")); Copy(Path.Combine(Repo, "games"), Path.Combine(cold, "games")); var before = Hashes(cold); var imageHashes = Hashes(images);
        var overrideGame = Game("Contra", library.Game.EmulatorId, library.Game.ApplicationPath, "-L cores/unsupported_libretro.dll", a => { allocations++; return Allocate(images, "Contra", a); });
        var overridden = ControlsPlugin.BuildWindow(overrideGame, cold, root, Find, () => refreshes++);
        Require(overridden.Content is TextBlock { Text: "No mapping found for Contra." } && !Directory.Exists(Path.Combine(cold, "renders")) && Hashes(cold).Count == before.Count && Hashes(images).Count == imageHashes.Count && refreshes == 1 && allocations == states.Count, "unsupported core: exact missing text, no render/artwork/allocation/refresh"); Unchanged(before); Unchanged(imageHashes);
        System.Windows.Window locked;
        using (var held = new FileStream(view.Artwork.Written.Values.First(), FileMode.Open, FileAccess.Read, FileShare.None)) locked = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
        Require(locked.Content is MappingView lockedView && lockedView.Cells.Count == states.Count && lockedView.ArtworkError?.Parent is StackPanel && lockedView.ArtworkError.Text.StartsWith("LaunchBox artwork not updated: ", StringComparison.Ordinal) && lockedView.ArtworkError.Text.Length > 30 && refreshes == 1, "locked artwork preserves viewer with error footer and no refresh");
        var manifest = Path.Combine(Mapping.RenderDirectory(scratch, Nes, "Contra"), "lb_images.json"); File.WriteAllText(manifest, "{corrupt");
        var recovered = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
        Require(recovered.Content is MappingView { Artwork.Writes: 0, Artwork.ManifestChanged: true, ArtworkError: null } recoveredView && recoveredView.Artwork!.Written.OrderBy(p => p.Key).SequenceEqual(view.Artwork.Written.OrderBy(p => p.Key)) && refreshes == 2 && Hashes(images).Count == imageHashes.Count && times.All(t => File.GetLastWriteTimeUtc(t.Key) == t.Value), "corrupt manifest recovers owned files without image writes and refreshes recovered entries");
        Require(JsonNode.Parse(File.ReadAllText(manifest))!["images"]!.AsObject().Count == states.Count && !Pending(manifest) && !Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.tmp").Any(), "manifest atomically replaced with valid JSON and successful refresh acknowledged");
        var first = view.States[0]; var changedPath = view.Artwork.Written[first.State.Name]; File.WriteAllBytes(changedPath, Artwork.Mark(File.ReadAllBytes(view.States[1].FrontPath), Artwork.Marker("Contra", Controller, first.State.Name)));
        var refreshFailure = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => { refreshes++; throw new IOException("fixture refresh failed"); });
        Require(refreshFailure.Content is MappingView { Artwork.Changed: 1, ArtworkError.Text: "LaunchBox artwork not updated: fixture refresh failed" } && refreshes == 3 && Pending(manifest), "changed image refresh failure preserves viewer with exact footer and persistent pending flag");
        var retry = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => { Require(Pending(manifest), "pending remains set during refresh callback"); refreshes++; });
        Require(retry.Content is MappingView { Artwork.Writes: 0, ArtworkError: null } && refreshes == 4 && !Pending(manifest), "next unchanged open retries failed refresh and clears pending only after success");
        var acknowledged = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
        Require(acknowledged.Content is MappingView { Artwork.Writes: 0, ArtworkError: null } && refreshes == 4, "successful retry restores unchanged-open refresh suppression");
        var six = Enumerable.Range(0, 6).Select(i => view.States[0] with { State = view.States[0].State with { Name = "State " + i } }).ToArray();
        var grid = new MappingView("Controller", "fixture.pdf", six); Require(grid.Cells.Count == 6 && grid.StateGrid.Columns == 3 && grid.StateGrid.Rows == 2, "six states wrap three by two");
        var backState = view.States[0] with { State = view.States[0].State with { Unbound = new[] { "Select (Pause)" } }, BackPath = view.States[0].FrontPath };
        var backView = new MappingView("Controller", "fixture.pdf", new[] { backState }); var content = (StackPanel)backView.Cells[0].Content;
        Require(content.Children.OfType<Image>().Count() == 2 && content.Children.OfType<TextBlock>().Single().Text == "Not bound on this pad: Select (Pause)", "front/back and exact unbound text");
        var plugin = new ControlsPlugin(scratch); var logged = File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Select(l => l[20..]).ToArray();
        Require(logged[^1].StartsWith("controls plugin loaded 0.3.0-beta", StringComparison.Ordinal) && logged.Contains($"controls: Contra -> {states.Count} states, {states.Count} artwork files") && logged.Contains("controls: Missing: Game? -> no mapping") && logged.Contains("controls: Contra -> no mapping") && logged.Contains("controls: Contra -> error fixture error") && logged.Count(l => l.StartsWith("controls", StringComparison.Ordinal) || l.StartsWith("artwork: Contra -> ", StringComparison.Ordinal)) == logged.Length && logged.Count(l => l == $"artwork: Contra -> {states.Count} files") == 1, "padmaster.log in the plugin directory: loaded version, states/artwork, no mapping, error");
        Require(plugin.Caption == "Show controller mapping" && plugin.ShowInLaunchBox && !plugin.ShowInBigBox && !plugin.SupportsMultipleGames && !plugin.GetIsValidForGames(new[] { library.Game }) && plugin.GetIsValidForGame(library.Game), "single-game LaunchBox menu contract");
        ControlsPlugin.IsEnabled = _ => false; Require(!plugin.GetIsValidForGame(library.Game) && !new PadMasterBadge(scratch).GetAppliesToGame(library.Game), "not enabled: no menu item, no badge"); ControlsPlugin.IsEnabled = _ => true;
        Require(unreadable.Content is TextBlock { Text: var reads } && reads.Contains("Emulators it reads: RetroArch"), "unreadable emulator names the emulators that work");
        // Pad Master's own settings: menu text and the pad drawn come from the settings store; the dropdown redraws and saves the pick.
        var settingsRoot = Path.Combine(scratch, "lbroot"); var plugins = Path.Combine(settingsRoot, "Plugins", "PadMaster"); Copy(Path.Combine(Repo, "controllers"), Path.Combine(plugins, "controllers")); Copy(Path.Combine(Repo, "games"), Path.Combine(plugins, "games"));
        var store = PackageIdentity.StorePath(settingsRoot); (new Settings { MenuCaption = "Controls, please", Controller = "xbox-series" }).Write(store);
        Require(new ControlsPlugin(plugins).Caption == "Controls, please" && plugin.Caption == "Show controller mapping", "menu text follows the settings store beside the plugin; no store = default");
        var saved = ControlsPlugin.StorePath;
        try
        {
            ControlsPlugin.StorePath = _ => store;
            var xbox = ControlsPlugin.BuildWindow(library.Game, scratch, root, Find, () => refreshes++);
            Require(xbox.Content is MappingView { } xboxView && xbox.Title == "Contra — Xbox Series Controller" && (string)xboxView.Controller.SelectedItem == "Xbox Series Controller" && xboxView.States.All(s => s.FrontPath.Contains("xbox-series")), "settings store picks the pad drawn");
            var picked = (MappingView)xbox.Content; picked.Controller.SelectedIndex = picked.Controller.Items.IndexOf("PlayStation DualSense");
            Require(xbox.Content is MappingView { } dual && !ReferenceEquals(dual, picked) && xbox.Title == "Contra — PlayStation DualSense" && dual.States.All(s => s.FrontPath.Contains("dualsense")) && Settings.ReadRequired(store).Controller == "dualsense" && Settings.ReadRequired(store).MenuCaption == "Controls, please", "dropdown pick redraws on that pad and saves it, other settings kept");
            var same = (MappingView)xbox.Content; same.Controller.SelectedIndex = same.Controller.SelectedIndex; Require(ReferenceEquals(same, xbox.Content), "re-selecting the drawn pad does nothing");
            Require(File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Any(l => l.EndsWith("controls: Contra -> dualsense picked, " + states.Count + " states", StringComparison.Ordinal)), "pick logged");
            xbox.Close();
        }
        finally { ControlsPlugin.StorePath = saved; }
        foreach (var w in new[] { window, repeated, absent, unsupported, failure, overridden, locked, recovered, refreshFailure, retry, acknowledged }) w.Close();
        Console.WriteLine($"OK  mapping window: Contra x {states.Count} states, title/cells/images/source, empty bindings no side effects, artwork/refresh error footers, recovered entries refresh, pending refresh retry, unchanged opens skip writes/refresh, six-state grid (STA, never shown; root {root}; scratch {scratch})");
    }

    public static void Images()
    {
        var states = RenderGame(false); var scratch = Scratch(); var folder = Path.Combine(scratch, "Images", Nes, ImageTypes.ArcadeControlsInformation); Directory.CreateDirectory(folder);
        var seed = Path.Combine(@"C:\LaunchBox-Test", "Images", Nes, ImageTypes.ArcadeControlsInformation); if (Directory.Exists(seed)) Copy(seed, folder);
        var foreign = Path.Combine(folder, "Blaster Master-01.png"); File.WriteAllBytes(foreign, File.ReadAllBytes(states[0].FrontPath)); File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "untouched");
        var lockedCandidate = Path.Combine(folder, "Blaster Master-00.png"); File.Copy(foreign, lockedCandidate);
        var unrelated = Path.Combine(folder, "000 unrelated-01.png"); var noise = new byte[2048 * 1024 * 4]; new Random(42).NextBytes(noise);
        using (var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(2048, 1024, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul)))
        {
            System.Runtime.InteropServices.Marshal.Copy(noise, 0, bitmap.GetPixels(), noise.Length); using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); File.WriteAllBytes(unrelated, png.ToArray());
        }
        Require(new FileInfo(unrelated).Length > 4 * 1024 * 1024, "large unrelated PNG fixture");
        File.WriteAllBytes(Path.Combine(folder, "Another title-01.png"), Artwork.Mark(File.ReadAllBytes(states[0].FrontPath), Artwork.Marker(Blaster, Controller, states[0].State.Name)));
        var before = Hashes(folder); var calls = 0; var game = Game(Blaster, "", "", "", args => { calls++; return Allocate(folder, Blaster, args); }); var manifestDir = Path.Combine(scratch, "renders");
        ArtworkResult first;
        using (var held = new FileStream(unrelated, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var candidateHeld = new FileStream(lockedCandidate, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (var path in new[] { unrelated, lockedCandidate })
            {
                var denied = false; try { using var probe = File.OpenRead(path); } catch (IOException) { denied = true; }
                Require(denied, "locked fixture would throw if opened for reading: " + Path.GetFileName(path));
            }
            first = Artwork.Write(game, Controller, states, manifestDir);
        }
        Require(first.Written.Count == states.Count && first.Created == states.Count && calls == states.Count, "N new state files, one API call each, unrelated title marker ignored and locked candidate skipped");
        var manifestPath = Path.Combine(manifestDir, "lb_images.json"); var refreshes = 0; var failed = false;
        Require(first.ManifestChanged && first.RefreshPending && Pending(manifestPath), "new entries persist pending refresh");
        try { Artwork.Refresh(manifestDir, () => { refreshes++; Require(Pending(manifestPath), "pending during failed refresh"); throw new IOException("fixture refresh failed"); }); } catch (IOException) { failed = true; }
        Require(failed && refreshes == 1 && Pending(manifestPath), "failed refresh keeps pending on disk");
        Require(Hashes(folder).Count == before.Count + states.Count, "only N added files"); Unchanged(before);
        foreach (var state in states)
        {
            var path = first.Written[state.State.Name]; var png = File.ReadAllBytes(path); var marker = Artwork.Marker(Blaster, Controller, state.State.Name);
            Require(Artwork.HasMarker(png, marker) && System.Text.Encoding.ASCII.GetString(png, 37, 4) == "tEXt", "correct marker chunk after IHDR");
            using var decoded = SkiaSharp.SKBitmap.Decode(png); Require(decoded != null, "marked PNG decodes");
            var corrupt = (byte[])png.Clone(); corrupt[45] ^= 1; Require(!Artwork.HasMarker(corrupt, marker), "invalid marker CRC rejected");
            Require(Artwork.Mark(png, marker).SequenceEqual(png), "marker insertion idempotent"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }
        var firstHashes = Hashes(folder); var times = first.Written.Values.ToDictionary(p => p, File.GetLastWriteTimeUtc); var second = Artwork.Write(game, Controller, states, manifestDir);
        Require(second.Writes == 0 && !second.ManifestChanged && second.RefreshPending && calls == states.Count && second.Written.OrderBy(p => p.Key).SequenceEqual(first.Written.OrderBy(p => p.Key)), "same N paths reused without writes/allocation, failed refresh remains pending");
        Require(Hashes(folder).Count == firstHashes.Count && times.All(t => File.GetLastWriteTimeUtc(t.Key) == t.Value), "identical run leaves all owned PNG mtimes unchanged"); Unchanged(firstHashes);
        Artwork.Refresh(manifestDir, () => { Require(Pending(manifestPath), "pending remains until successful callback returns"); refreshes++; });
        Require(refreshes == 2 && !Pending(manifestPath), "successful retry acknowledges pending refresh");
        var acknowledged = Artwork.Write(game, Controller, states, manifestDir); Artwork.Refresh(manifestDir, () => refreshes++);
        Require(acknowledged.Writes == 0 && !acknowledged.RefreshPending && !acknowledged.ManifestChanged && refreshes == 2, "unchanged acknowledged call does not refresh again");
        File.Delete(Path.Combine(manifestDir, "lb_images.json")); var rediscovered = Artwork.Write(game, Controller, states, manifestDir);
        Require(rediscovered.Writes == 0 && rediscovered.ManifestChanged && rediscovered.RefreshPending && Pending(manifestPath) && rediscovered.Written.OrderBy(p => p.Key).SequenceEqual(first.Written.OrderBy(p => p.Key)) && Hashes(folder).Count == firstHashes.Count && times.All(t => File.GetLastWriteTimeUtc(t.Key) == t.Value), "missing manifest rediscovers same paths without image writes and schedules refresh"); Unchanged(firstHashes);
        Artwork.Refresh(manifestDir, () => refreshes++); Require(refreshes == 3 && !Pending(manifestPath), "recovered manifest entries refresh without image writes");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(first.Written)); var legacy = Artwork.Write(game, Controller, states, manifestDir);
        Require(legacy.Writes == 0 && !legacy.ManifestChanged && !legacy.RefreshPending && legacy.Written.OrderBy(p => p.Key).SequenceEqual(first.Written.OrderBy(p => p.Key)), "legacy flat manifest retains ownership without duplicate images");
        var edgeFolder = Path.Combine(scratch, "edge-cases", "images"); Directory.CreateDirectory(edgeFolder); var edgeForeign = Path.Combine(edgeFolder, "Blaster Master-01.png"); File.WriteAllBytes(edgeForeign, File.ReadAllBytes(states[0].FrontPath));
        var edgeBefore = Hashes(edgeFolder); var edgeGame = Game(Blaster, "", "", "", a => Allocate(edgeFolder, Blaster, a)); var edgeManifestDir = Path.Combine(scratch, "edge-cases", "renders");
        Artwork.Write(edgeGame, Controller, states, edgeManifestDir); var manifest = Path.Combine(edgeManifestDir, "lb_images.json");
        var tampered = JsonNode.Parse(File.ReadAllText(manifest))!; tampered["images"]![states[0].State.Name] = edgeForeign; File.WriteAllText(manifest, tampered.ToJsonString());
        var replaced = Artwork.Write(edgeGame, Controller, states, edgeManifestDir); Require(replaced.Created == 1 && replaced.Written[states[0].State.Name] != edgeForeign, "unmarked manifest path receives new API path"); Unchanged(edgeBefore);
        var wrongGame = replaced.Written[states[0].State.Name]; File.WriteAllBytes(wrongGame, Artwork.Mark(File.ReadAllBytes(states[0].FrontPath), Artwork.Marker("Another game", Controller, states[0].State.Name))); var wrongBytes = File.ReadAllBytes(wrongGame);
        var reallocated = Artwork.Write(edgeGame, Controller, states, edgeManifestDir); Require(reallocated.Created == 1 && reallocated.Written[states[0].State.Name] != wrongGame && File.ReadAllBytes(wrongGame).SequenceEqual(wrongBytes), "another game's marker never grants overwrite ownership");
        var retained = Artwork.Write(edgeGame, Controller, states.Take(1).ToArray(), edgeManifestDir); Require(retained.Retained.Count == states.Count - 1 && retained.Retained.Values.All(File.Exists), "removed states retained and reported");
        var collisionFolder = Path.Combine(scratch, "collision", "images"); Directory.CreateDirectory(collisionFolder); var collisionPath = Path.Combine(collisionFolder, "Blaster Master-01.png"); File.Copy(edgeForeign, collisionPath); var collisionBefore = Hashes(collisionFolder);
        var collision = Game(Blaster, "", "", "", _ => collisionPath); var refused = false;
        try { Artwork.Write(collision, Controller, states, Path.Combine(scratch, "collision")); } catch (IOException) { refused = true; }
        Require(refused, "API collision never clobbers a file"); Unchanged(collisionBefore); Unchanged(edgeBefore); Unchanged(firstHashes); Require(Hashes(folder).Count == before.Count + states.Count, "main scratch retains exactly N marked artwork files");
        Console.WriteLine("OK  lb images fix2: title-filtered header rediscovery, large unrelated PNG locked, locked candidate skipped, manifest recovery refresh, pending retry then acknowledgment");
        Console.WriteLine($"OK  lb images: {Blaster} -> {states.Count} files under Arcade - Controls Information (marker found, nothing else touched)");
    }
}
