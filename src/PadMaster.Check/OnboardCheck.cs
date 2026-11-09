using System.IO;
using System.Text.Json.Nodes;
using PadMaster;
using PadMaster.Controls;

// "Add my controller...": photo answer, download, scaling, button placement, the written folder, the dropdown. No live assistant.
static class OnboardCheck
{
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("onboard: " + message); }
    static string Repo { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (!Directory.Exists(Path.Combine(d.FullName, "controllers"))) d = d.Parent!; return d.FullName; } }
    const string Photos = """Found it: {"id":"test-pad","displayName":"Test Pad","front":{"url":"https://upload.wikimedia.org/f.jpg","page":"https://commons.wikimedia.org/wiki/File:F.jpg","author":"A. Person","licence":"CC BY 4.0"},"back":{"url":"https://upload.wikimedia.org/b.jpg","page":"https://commons.wikimedia.org/wiki/File:B.jpg","author":"A. Person","licence":"CC BY 4.0"}}""";

    // A real run on the user's own subscription into a scratch plugin folder; then Contra drawn on the new pad. The repo's controllers are not touched.
    public static async Task Live(string name, string assistant = "claude-code")
    {
        var home = Path.Combine(Path.GetTempPath(), "PadMaster-onboard-live"); var controllers = Path.Combine(home, "controllers");
        if (!Directory.Exists(controllers)) foreach (var f in Directory.GetFiles(Path.Combine(Repo, "controllers"), "*", SearchOption.AllDirectories)) { var t = Path.Combine(controllers, Path.GetRelativePath(Path.Combine(Repo, "controllers"), f)); Directory.CreateDirectory(Path.GetDirectoryName(t)!); File.Copy(f, t, true); }
        var (id, problem) = await Onboard.Build(name, assistant, home, Console.WriteLine, Console.WriteLine, CancellationToken.None);
        if (id == null) { Console.WriteLine("RESULT: " + problem); return; }
        var dir = Path.Combine(controllers, id); Console.WriteLine("built: " + dir + "\n" + string.Join("\n", Directory.GetFiles(dir).Select(Path.GetFileName)) + "\n" + File.ReadAllText(Path.Combine(dir, "source.txt")));
        const string nes = "Nintendo Entertainment System", contra = "Contra"; var read = PadMaster.Readers.Select.Read(LaunchBoxDir.Value, nes, contra);
        foreach (var s in Mapping.Render(dir, Mapping.GamePath(Repo, nes, contra), read.Bindings, Path.Combine(home, "renders", id), read.Config, null, "")) Console.WriteLine($"{s.State.Name}: {s.FrontPath}" + (s.State.Unbound.Count > 0 ? "  not bound: " + string.Join(", ", s.State.Unbound) : ""));
    }

    public static void Run()
    {
        var xbox = Path.Combine(Repo, "controllers", "xbox-series"); var scratch = Directory.CreateTempSubdirectory("PadMaster-onboard").FullName; var controllers = Path.Combine(scratch, "controllers");
        Directory.CreateDirectory(Path.Combine(controllers, "xbox-series"));
        // photo answer
        var (found, problem) = Onboard.ParsePhotos(Photos, controllers, "test pad"); Require(problem == null && found is { Id: "test-pad", DisplayName: "Test Pad", Back.Author: "A. Person" } && found.Front.Url.EndsWith("f.jpg"), "good photo answer: " + problem);
        Require(Onboard.ParsePhotos("""{"none":"Nothing on Commons."}""", controllers, "Test Pad").Problem == "No usable photo of Test Pad found. Nothing on Commons.", "none says where it looked");
        Require(Onboard.ParsePhotos(Photos.Replace("test-pad", "Test Pad!"), controllers, "x").Problem!.Contains("bad name"), "bad id refused");
        Require(Onboard.ParsePhotos(Photos.Replace("\"test-pad\"", "\"\""), controllers, "x").Problem!.Contains("bad name"), "empty id refused");
        Require(Onboard.ParsePhotos(Photos.Replace("test-pad", "xbox-series"), controllers, "x").Problem!.Contains("already has a controller called xbox-series"), "existing id refused");
        Require(Onboard.ParsePhotos(Photos.Replace("https://upload.wikimedia.org/f.jpg", "http://x/f.jpg"), controllers, "x").Problem!.Contains("front photo"), "plain http refused");
        Require(Onboard.ParsePhotos("sorry", controllers, "x").Problem!.Contains("wasn't readable"), "prose refused");
        Require(Onboard.ParsePhotos(Photos.Replace("\"back\":{", "\"back\":null,\"x\":{"), controllers, "x").Found!.Back == null, "back may be null");
        // download + scaling
        var front = File.ReadAllBytes(Path.Combine(xbox, "pad_front.jpg")); var back = File.ReadAllBytes(Path.Combine(xbox, "pad_back.jpg"));
        Require(Onboard.Fetch("https://x/f.jpg", _ => Task.FromResult(new byte[] { 1, 2, 3, 4, 5 })).GetAwaiter().GetResult().Problem == "The download isn't a picture.", "non-image bytes refused");
        Require(Onboard.Fetch("https://x/f.jpg", _ => Task.FromResult(new byte[Onboard.MaxBytes + 1])).GetAwaiter().GetResult().Problem!.Contains("20 MB"), "over 20 MB refused");
        Require(Onboard.Fetch("https://x/f.jpg", _ => throw new System.Net.Http.HttpRequestException("404")).GetAwaiter().GetResult().Problem!.Contains("couldn't be downloaded: 404"), "download error said plainly");
        using (var small = SkiaSharp.SKBitmap.Decode(front).Resize(new SkiaSharp.SKImageInfo(480, 318), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear)))
        using (var png = SkiaSharp.SKImage.FromBitmap(small).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        {
            var (jpg, why) = Onboard.Fetch("https://x/f.png", _ => Task.FromResult(png.ToArray())).GetAwaiter().GetResult();
            using var scaled = SkiaSharp.SKBitmap.Decode(jpg); Require(why == null && scaled.Width == 960 && scaled.Height == 636 && jpg![0] == 0xFF && jpg[1] == 0xD8, $"480 px png becomes a 960 px jpg: {scaled?.Width}x{scaled?.Height} {why}");
        }
        // anchors
        var sizes = new Dictionary<string, (int W, int H)> { ["front"] = (960, 636), ["back"] = (960, 636) }; var frontOnly = new Dictionary<string, (int W, int H)> { ["front"] = (960, 636) };
        var shipped = File.ReadAllText(Path.Combine(xbox, "anchors.json"));
        var (doc, wrong) = Onboard.ParseAnchors(shipped, found!, sizes); Require(wrong == null && doc!["anchors"]!.AsObject().Count == 17 && doc["controller"]!.GetValue<string>() == "test-pad" && doc["photos"]!["back"]!.GetValue<string>() == "pad_back.jpg", "shipped xbox anchors pass: " + wrong);
        Require(JsonNode.DeepEquals(doc["anchors"], JsonNode.Parse(shipped)!["anchors"]), "anchors kept exactly");
        var noA = JsonNode.Parse(shipped)!.AsObject(); noA["anchors"]!.AsObject().Remove("A");
        Require(Onboard.ParseAnchors(noA.ToJsonString(), found!, sizes).Problem!.Contains("missing A"), "missing A refused");
        Require(Onboard.ParseAnchors(shipped.Replace("\"x\":624", "\"x\":1624"), found!, sizes).Problem!.Contains("Y is outside its photo"), "x outside the image refused");
        Require(Onboard.ParseAnchors(shipped.Replace("\"y\":590", "\"y\":700"), found!, sizes).Problem!.Contains("dpad_right's label is outside"), "label outside the image refused");
        Require(Onboard.ParseAnchors(shipped.Replace("\"rs\":", "\"paddle\":"), found!, sizes).Problem!.Contains("\"paddle\" isn't a button"), "unknown key refused");
        var joystick = JsonNode.Parse(shipped)!.AsObject(); foreach (var d in new[] { "dpad_up", "dpad_left", "dpad_down", "dpad_right" }) joystick["anchors"]!.AsObject().Remove(d);
        Require(Onboard.ParseAnchors(joystick.ToJsonString(), found!, sizes).Problem == null, "a joystick (ls) stands in for the four directions");
        joystick["anchors"]!.AsObject().Remove("ls"); Require(Onboard.ParseAnchors(joystick.ToJsonString(), found!, sizes).Problem!.Contains("no way to move"), "no d-pad and no stick refused");
        Require(Onboard.AnchorsPrompt("P", sizes, false, "start upper left").Contains("The owner says about it: start upper left") && Onboard.ClassifyPrompt("P", new[] { Path.Combine(xbox, "pad_front.jpg") }, false, "two players").Contains("The owner says about it: two players") && !Onboard.AnchorsPrompt("P", sizes, false).Contains("owner says"), "owner notes reach both prompts");
        Require(Onboard.ParseAnchors(shipped, found!, frontOnly).Problem!.Contains("photo that doesn't exist (\"back\")"), "view back with no back photo refused");
        Require(Onboard.ParseAnchors("""{"anchors":{"A":{"view":"front","x":"left","y":1}}}""", found!, sizes).Problem!.Contains("wrong shape"), "wrong shape refused");
        var prompt = Onboard.AnchorsPrompt("Test Pad", sizes, false); Require(prompt.Contains("pad_back.jpg (view \"back\", 960 x 636 px)") && prompt.Contains("\"dpad_right\"") && prompt.Contains("read them with Read") && !prompt.Contains("{{") && Onboard.AnchorsPrompt("T", sizes, true).Contains("attached"), "anchors prompt");
        Require(Onboard.PhotoPrompt("Test Pad").Contains("Wikimedia Commons first") && !Onboard.PhotoPrompt("x").Contains("{{"), "photo prompt");
        // the whole build with seams: folder written in the shipped format, Renderer draws it; a failed placement leaves nothing
        Task<byte[]> Fetch(string url) => Task.FromResult(url.EndsWith("f.jpg") ? front : back);
        Onboard.AskFunc Answers(params string[] answers) { var i = 0; return (_, _, _) => Task.FromResult<(string?, string?)>((answers[i++], null)); }
        var statuses = new List<string>(); var logged = new List<string>();
        var (id, buildProblem) = Onboard.Build("Test Pad", "claude-code", scratch, statuses.Add, logged.Add, CancellationToken.None, Fetch, Answers(Photos, shipped)).GetAwaiter().GetResult();
        var made = Path.Combine(controllers, "test-pad");
        Require(id == "test-pad" && buildProblem == null && Directory.GetFiles(made).Select(Path.GetFileName).Order().SequenceEqual(["anchors.json", "pad_back.jpg", "pad_front.jpg", "source.txt"]), "built folder has the four files: " + buildProblem);
        Require(File.ReadAllText(Path.Combine(made, "source.txt")) == "pad_front.jpg: https://commons.wikimedia.org/wiki/File:F.jpg\npad_back.jpg:  https://commons.wikimedia.org/wiki/File:B.jpg\nPhotos by A. Person, CC BY 4.0. Scaled to 960 px wide, no other change.\n", "source.txt in the shipped format");
        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(made, "anchors.json")))!; Require(written["displayName"]!.GetValue<string>() == "Test Pad" && written["mapping_note"] != null && Mapping.Controller(scratch, "test-pad") == made, "anchors.json readable as a controller");
        var png1 = Renderer.Render(made, new() { ["A"] = "Jump", ["lb"] = "Shoot" }, "front"); Require(png1.Length > 1000, "the new pad draws");
        Require(statuses.Count == 3 && statuses[0].Contains("looking for a photo of Test Pad") && logged.Any(l => l.Contains("test-pad, 17 buttons, 2 photos")), "status and log");
        Require(Onboard.Build("Test Pad 2", "codex", scratch, _ => { }, _ => { }, CancellationToken.None, Fetch, Answers(Photos.Replace("test-pad", "test-pad-2"), noA.ToJsonString())).GetAwaiter().GetResult() is (null, { } failed) && failed.Contains("missing A") && !Directory.Exists(Path.Combine(controllers, "test-pad-2")), "failed placement leaves no folder");
        Require(Onboard.Build("Test Pad 3", "codex", scratch, _ => { }, _ => { }, CancellationToken.None, Fetch, Answers("""{"none":"No free photo anywhere."}""")).GetAwaiter().GetResult().Problem!.Contains("No free photo anywhere") && Directory.GetDirectories(controllers).Length == 2, "assistant says none: nothing written");
        Require(Onboard.Build("  ", "codex", scratch, _ => { }, _ => { }, CancellationToken.None, Fetch, Answers()).GetAwaiter().GetResult().Problem == "Type the controller's name first.", "empty name");
        // the user's own pictures: the assistant says what each is; one controller per front, the back shared; no web, no order assumed
        Require(Onboard.Slug(" My Arcade Panel! ") == "my-arcade-panel" && Onboard.Slug("***") == "", "slug");
        var own = Path.Combine(scratch, "own"); Directory.CreateDirectory(own);
        foreach (var (src, dst) in new[] { ("pad_front.jpg", "red.jpg"), ("pad_front.jpg", "blue.jpg"), ("pad_back.jpg", "underside.jpg"), ("pad_front.jpg", "cat.jpg") }) File.Copy(Path.Combine(xbox, src), Path.Combine(own, dst));
        string[] ownFiles = ["underside.jpg", "cat.jpg", "red.jpg", "blue.jpg"]; var ownPaths = ownFiles.Select(f => Path.Combine(own, f)).ToArray();
        const string Seen = """{"pictures":[{"file":"underside.jpg","role":"back","label":"underside"},{"file":"cat.jpg","role":"other","label":"a cat"},{"file":"red.jpg","role":"front","label":"red"},{"file":"blue.jpg","role":"front","label":"blue"}]}""";
        var (pics, picsProblem) = Onboard.ParseClassification(Seen, ownPaths); Require(picsProblem == null && pics!.Count == 4 && pics[2] is { Role: "front", Label: "red" } && pics[0].File == ownPaths[0], "classification parsed by file name: " + picsProblem);
        Require(Onboard.ParseClassification(Seen.Replace("red.jpg", "dog.jpg"), ownPaths).Problem!.Contains("wasn't given"), "unknown file refused");
        Require(Onboard.ParseClassification(Seen.Replace("\"front\"", "\"other\""), ownPaths).Problem!.Contains("no front view"), "no front refused");
        Require(Onboard.ClassifyPrompt("P", ownPaths, false).Contains("4 pictures") && Onboard.ClassifyPrompt("P", ownPaths, true).Contains("attached"), "classify prompt");
        var ownStatuses = new List<string>(); var ownLog = new List<string>(); var frontOnlyAnchors = shipped.Replace("\"view\":\"back\"", "\"view\":\"front\"");
        var (ids, ownProblem) = Onboard.BuildOwn("My Arcade Panel", "claude-code", scratch, ownPaths, ownStatuses.Add, ownLog.Add, CancellationToken.None, Answers(Seen, shipped, shipped)).GetAwaiter().GetResult();
        Require(ownProblem == null && ids.SequenceEqual(["my-arcade-panel-red", "my-arcade-panel-blue"]), "two fronts = two controllers, the cat ignored: " + ownProblem);
        foreach (var (ownId, label) in new[] { ("my-arcade-panel-red", "red"), ("my-arcade-panel-blue", "blue") })
        {
            var dir = Path.Combine(controllers, ownId); var source = File.ReadAllText(Path.Combine(dir, "source.txt"));
            Require(Directory.GetFiles(dir).Length == 4 && source.StartsWith("pad_front.jpg: your own picture, " + Path.Combine(own, label + ".jpg")) && source.Contains("pad_back.jpg:  your own picture, " + ownPaths[0]), "own folder " + ownId);
            Require(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "anchors.json")))!["displayName"]!.GetValue<string>() == $"My Arcade Panel ({label})" && Renderer.Render(dir, new() { ["A"] = "Jump" }, "front").Length > 1000, "own pad named by its label and draws: " + ownId);
        }
        using (var scaledOwn = SkiaSharp.SKBitmap.Decode(File.ReadAllBytes(Path.Combine(controllers, "my-arcade-panel-red", "pad_front.jpg")))) Require(scaledOwn.Width == 960, "own picture scaled to 960");
        Require(ownStatuses[0].Contains("looking at your 4 pictures") && !ownStatuses.Any(s => s.Contains("on the web")) && ownLog.Any(l => l.Contains("red.jpg = front (red)")), "own statuses and log");
        Require(Onboard.BuildOwn("My Arcade Panel", "claude-code", scratch, ownPaths, _ => { }, _ => { }, CancellationToken.None, Answers(Seen)).GetAwaiter().GetResult().Problem!.Contains("already has a controller called my-arcade-panel-red"), "own: existing id refused");
        Require(Onboard.BuildOwn("Other", "claude-code", scratch, [Path.Combine(scratch, "missing.png")], _ => { }, _ => { }, CancellationToken.None, Answers()).GetAwaiter().GetResult().Problem == "Pick at least one picture first.", "own: no existing file refused");
        var single = Onboard.BuildOwn("Single", "codex", scratch, [ownPaths[2]], _ => { }, _ => { }, CancellationToken.None, Answers("""{"pictures":[{"file":"red.jpg","role":"front","label":"red"}]}""", frontOnlyAnchors)).GetAwaiter().GetResult();
        Require(single.Problem == null && single.Ids.SequenceEqual(["single"]) && Directory.GetFiles(Path.Combine(controllers, "single")).Length == 3, "one front picture, no label in the name, three files: " + single.Problem);
        // change (revise) and remove
        var reviseLog = new List<string>(); var moved = JsonNode.Parse(shipped)!.AsObject(); moved["anchors"]!["A"]!["x"] = 300;
        Require(Onboard.Revise("my-arcade-panel-red", "A is further left", "claude-code", scratch, _ => { }, reviseLog.Add, CancellationToken.None, Answers(moved.ToJsonString())).GetAwaiter().GetResult() == null, "revise accepted");
        var revised = JsonNode.Parse(File.ReadAllText(Path.Combine(controllers, "my-arcade-panel-red", "anchors.json")))!;
        Require(revised["anchors"]!["A"]!["x"]!.GetValue<int>() == 300 && revised["autoLabels"]!.GetValue<bool>() && File.Exists(Path.Combine(controllers, "my-arcade-panel-red", "anchors.prev.json")) && reviseLog.Any(l => l.Contains("revised: 17 buttons (A is further left)")), "revise wrote the new anchors, kept the old file, logged");
        Require(Onboard.RevisePrompt("P", "{}", sizes, false, "move A").Contains("The owner looked at the drawn result and says: move A") && Onboard.Revise("my-arcade-panel-red", "  ", "claude-code", scratch, _ => { }, _ => { }, CancellationToken.None, Answers()).GetAwaiter().GetResult() == "Say what to change first." && Onboard.Revise("nope", "x", "claude-code", scratch, _ => { }, _ => { }, CancellationToken.None, Answers()).GetAwaiter().GetResult()!.Contains("no controller called nope"), "revise prompt, empty words, unknown id");
        Require(Onboard.Revise("my-arcade-panel-red", "break it", "claude-code", scratch, _ => { }, _ => { }, CancellationToken.None, Answers(noA.ToJsonString())).GetAwaiter().GetResult()!.Contains("missing A") && JsonNode.Parse(File.ReadAllText(Path.Combine(controllers, "my-arcade-panel-red", "anchors.json")))!["anchors"]!["A"]!["x"]!.GetValue<int>() == 300, "a bad revision changes nothing");
        Directory.CreateDirectory(Path.Combine(scratch, "renders", "Nintendo Entertainment System", "Contra", "my-arcade-panel-red")); Mapping.ClearRenders(scratch, "my-arcade-panel-red"); Require(!Directory.Exists(Path.Combine(scratch, "renders", "Nintendo Entertainment System", "Contra", "my-arcade-panel-red")), "renders of a changed controller cleared");
        Directory.CreateDirectory(Path.Combine(scratch, "renders", "Nintendo Entertainment System", "Contra", "my-arcade-panel-blue"));
        Require(Onboard.Remove("my-arcade-panel-blue", scratch) == null && !Directory.Exists(Path.Combine(controllers, "my-arcade-panel-blue")) && !Directory.Exists(Path.Combine(scratch, "renders", "Nintendo Entertainment System", "Contra", "my-arcade-panel-blue")) && Onboard.Remove("nope", scratch)!.Contains("no controller called nope"), "remove deletes the folder and its renders; unknown id refused");
        foreach (var d in Directory.GetDirectories(controllers).Where(d => Path.GetFileName(d) != "test-pad")) Directory.Delete(d, true);
        Require(Onboard.Remove("test-pad", scratch)!.Contains("only controller"), "the last controller stays");
        // the dropdown, on an STA thread
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var state = new RenderedState(new MappingState("Default", new(), new(), []), Path.Combine(xbox, "pad_front.jpg"), null);
                var view = new MappingView("Xbox Series Controller", "x.pdf", [state], controllers: [("dualsense", "PlayStation DualSense"), ("gulikit-kk3-max", "GuliKit KK3 Max"), ("xbox-series", "Xbox Series Controller")], controllerId: "xbox-series");
                int picks = 0, adds = 0, changes = 0, removes = 0, redos = 0; view.ControllerPicked += _ => picks++; view.AddControllerRequested += () => adds++; view.ChangeControllerRequested += () => changes++; view.RemoveControllerRequested += () => removes++; view.RedoMapRequested += () => redos++;
                Require(view.Controller.Items.Count == 4 && (string)view.Controller.Items[3]! == "Add my controller...", "dropdown has 4 items, last Add my controller...");
                view.Change.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); view.Remove.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); view.Redo.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Require(changes == 1 && removes == 1 && redos == 1 && (string)view.Change.Content == "Change this controller..." && (string)view.Remove.Content == "Remove this controller" && (string)view.Redo.Content == "Redo this game's map", "the three buttons raise their events");
                view.Controller.SelectedIndex = 3; Require(adds == 1 && picks == 0 && view.Controller.SelectedIndex == 2, "Add my controller... raises AddControllerRequested only, drawn pad stays selected");
                view.Controller.SelectedIndex = 2; Require(adds == 1 && picks == 0, "re-selecting the drawn pad raises nothing");
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure != null) throw failure;
        Directory.Delete(scratch, true);
        Console.WriteLine("OK  onboard: photo answer, download and 960 px scaling, button placement checked; built folder = 4 shipped-format files that draw; own pictures (slug id, scaled, no web); failures leave nothing; dropdown Add my controller...");
    }
}
