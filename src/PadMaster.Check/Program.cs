using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadMaster;

AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Fatal(e.Exception); };
Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { e.Handled = true; Fatal(e.Exception); };
try { await Run(args); }
catch (Exception e) { Fatal(e); }

static void Fatal(object error)
{
    try { Console.Error.WriteLine($"FAIL: {error}"); } finally { Environment.Exit(1); }
}

static async Task Run(string[] args)
{
var api = Path.Combine(LaunchBoxDir.Value, "Core", "Unbroken.LaunchBox.Plugins.dll");
Require(File.Exists(api), "real LaunchBox API is required for managed Configure check");
AssemblyLoadContext.Default.Resolving += (context, name) => name.Name switch
{
    "Unbroken.LaunchBox.Plugins" => context.LoadFromAssemblyPath(api),
    "PadMaster.Settings" => context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "dist", "Local", "Plugins", PackageIdentity.Name, "PadMaster.Settings.dll"))),
    _ => null
};
if (args.Length > 0)
{
    if (args[0] == "--adapters" && args.Length == 1) { await CodexCheck.Run(); await Sta(CodexCheck.Panel); await ManagedCheck.Run(); PackageCheck(); return; }
    if (args[0] == "--adapter-arguments-negative" && args.Length == 1) { CodexCheck.Arguments(true); return; }
    if (args[0] == "--hardening") { await BetaCheck.Run(); await Sta(BetaCheck.Panel); ApiCheck.Bulk(); return; }
    if (args[0] == "--fresh-default") { BetaCheck.FreshDefault(); return; }
    if (args[0] == "--icon" && args.Length == 2) { await Sta(() => IconCheck.Draw(args[1])); return; }
    if (args[0] == "--api" && args.Length == 1) { ApiCheck.Run(); return; }
    if (args[0] == "--crash") throw new InvalidOperationException("deliberate main-thread crash probe");
    if (args[0] == "--crash-thread") { await Sta(() => throw new InvalidOperationException("deliberate STA-thread crash probe")); return; }
    if (args[0] == "--shot" && args.Length == 2) { await Sta(() => Shot(args[1])); return; }
    if (args[0] == "--smoke" && args.Length <= 2 && (args.Length == 1 || args[1] is "claude-code" or "codex")) { Environment.ExitCode = await Smoke(args.Length == 2 ? args[1] : null); return; }
    if (args[0] == "--typing" && args.Length == 1) { await Sta(TypingCheck.Run); return; }
    if (args[0] == "--stuck" && args.Length == 1) { await Sta(StuckLegacy.Run); await Sta(StuckCheck.Run); return; }
    Console.WriteLine("FAIL: use no args, --shot <png>, --smoke, --stuck, or --typing"); Environment.ExitCode = 1; return;
}

const int port = 47801;
ParserCheck();
await CodexCheck.Run();
await Sta(CodexCheck.Panel);
await BetaCheck.Run();
await Sta(BetaCheck.Panel);
await Sta(PlacementCheck);
await Sta(StateCheck.Run);
await Sta(SetupCheck);
await LoginCheck();
PackageCheck();
ReviewCheck.AuthAndCommands();
ReviewCheck.Activation();
await Fix2Check.Reads();
Fix2Check.Auth();
await Sta(ReviewCheck.Panel);
await ReviewCheck.Processes();
await Fix2Check.ResumeAndShutdown();
await Fix2Check.StreamAndResume();
await ManagedCheck.Run();
ApiCheck.Run();
var mcp = new Mcp(port, "/mcp", [
    new Tool("echo", "echoes x; throws on boom", new JsonObject { ["type"] = "object" },
        a => a?["x"]?.GetValue<string>() == "boom" ? throw new Exception("kaboom") : new JsonObject { ["got"] = a?["x"]?.GetValue<string>() }),
]);
try { mcp.Start(); }
catch (Exception e) { Console.WriteLine($"FAIL: MCP listener could not start: {e.GetType().Name} ({(e as HttpListenerException)?.ErrorCode}): {e.Message}"); Environment.ExitCode = 1; return; }
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp"), Timeout = TimeSpan.FromSeconds(10) };
Require((await http.PostAsync("", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Unauthorized, "MCP missing token returns 401");
http.DefaultRequestHeaders.Authorization = new("Bearer", "incorrect");
Require((await http.PostAsync("", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Unauthorized, "MCP wrong token returns 401");
http.DefaultRequestHeaders.Authorization = new("Bearer", mcp.Token);
Require((await http.PostAsync("", new StringContent("{}", Encoding.UTF8, "text/plain"))).StatusCode == HttpStatusCode.UnsupportedMediaType, "MCP non-JSON returns 415");
Console.WriteLine("OK  F4 MCP: missing/wrong bearer 401, non-JSON 415");
var n = 0;

async Task<(HttpStatusCode status, JsonNode? body)> Post(string method, JsonNode? p = null, bool notify = false)
{
    var m = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p };
    if (!notify) m["id"] = ++n;
    var r = await http.PostAsync("", new StringContent(m.ToJsonString(), Encoding.UTF8, "application/json"));
    var s = await r.Content.ReadAsStringAsync();
    return (r.StatusCode, s.Length == 0 ? null : JsonNode.Parse(s));
}

void Must(bool ok, string what)
{
    if (ok) return;
    Console.WriteLine($"FAIL: {what}");
    Environment.Exit(1);
}

var (st, init) = await Post("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "check" } });
Must(st == HttpStatusCode.OK, "initialize 200");
Must(init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "launchbox", "serverInfo.name");
Must(init?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18", "protocolVersion");
Must(init?["id"]?.GetValue<int>() == 1, "id echoed");

(st, _) = await Post("notifications/initialized", notify: true);
Must(st == HttpStatusCode.Accepted, "notification 202");

var (_, list) = await Post("tools/list");
Must(list?["result"]?["tools"]?.AsArray().Count == 1 && list["result"]!["tools"]![0]!["name"]!.GetValue<string>() == "echo", "tools/list");

var sw = Stopwatch.StartNew();
var (_, call) = await Post("tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["x"] = "hi" } });
sw.Stop();
Must(call?["result"]?["isError"]?.GetValue<bool>() == false, "call isError false");
Must(JsonNode.Parse(call!["result"]!["content"]![0]!["text"]!.GetValue<string>())?["got"]?.GetValue<string>() == "hi", "call result");

var (_, boom) = await Post("tools/call", new JsonObject { ["name"] = "echo", ["arguments"] = new JsonObject { ["x"] = "boom" } });
Must(boom?["result"]?["isError"]?.GetValue<bool>() == true && boom["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("kaboom"), "handler exception -> isError");

var (_, nope) = await Post("nope");
Must(nope?["error"]?["code"]?.GetValue<int>() == -32601, "unknown method -32601");

var (_, ping) = await Post("ping");
Must(ping?["result"] is JsonObject, "ping");

var get = await http.GetAsync("");
Must(get.StatusCode == HttpStatusCode.MethodNotAllowed, "GET 405");

mcp.Stop();
Console.WriteLine($"OK  tools/call round-trip {sw.Elapsed.TotalMilliseconds:F1} ms");

static Task Sta(Action action)
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { e.Handled = true; Fatal(e.Exception); };
        try { action(); completion.SetResult(); }
        catch (Exception e) { Fatal(e); }
        finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
}

static void Require(bool ok, string what) { if (!ok) throw new InvalidOperationException(what); }

static void ParserCheck()
{
    using var session = new Session("unused", "unused", "unused", _ => { });
    var output = new List<string>(); var reply = ""; var line = ""; var successes = 0; var errors = 0;
    session.Reply += text => { output.Add(text); reply = text; };
    session.Activity += text => { output.Add(text); line = text; }; session.Failure += text => { output.Add(text); line = text; };
    session.Done += success => { if (success) successes++; else errors++; };
    BetaCheck.Activity(session);
    session.BeginTurn();
    string[] events =
    [
        """{"type":"system","subtype":"init","session_id":"SECRET-SESSION","model":"SECRET-MODEL"}""",
        """{"type":"assistant","message":{"id":"tool","content":[{"type":"tool_use","id":"tool-1","name":"mcp__launchbox__games","input":{"platform":"NES"}}]}}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"answer"}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello "}}}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"there."}}}""",
        """{"type":"assistant","message":{"id":"answer","content":[{"type":"text","text":"Hello there."}]}}""",
        """{"type":"result","subtype":"success","result":"Hello there.","session_id":"SECRET-SESSION","total_cost_usd":12.34}""",
        """{"type":"rate_limit_event","secret":"ignore"}""",
        """{"type":"new-event-type","text":"ignore"}""",
        """{"type":"result","subtype":"error_during_execution","result":"SECRET-SESSION cost 12.34","session_id":"SECRET-SESSION"}""",
        "not JSON { SECRET-SESSION cost mcp__launchbox__games"
    ];
    foreach (var item in events) session.AcceptLine(item);
    Require(reply == "Hello there.", "partial + final reply is not doubled");
    Require(output.Contains("looking up games…"), "mapped activity");
    Require(successes == 1 && errors == 1 && session.LastSessionId == "SECRET-SESSION", "result state");
    Require(line == "The assistant could not finish that reply. Please try again.", "error line survives turn completion");
    Require(output.All(text => !text.Contains('{') && !text.Contains("SECRET-") && !text.Contains("cost") && !text.Contains("mcp__") && !text.Contains("12.34")), "CLI metadata never reaches UI");
    session.BeginTurn();
    session.AcceptLine("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"thinking-first"}}}""");
    session.AcceptLine("""{"type":"assistant","message":{"id":"thinking-first","content":[{"type":"thinking","thinking":"private"}]}}""");
    session.AcceptLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"One reply."}}}""");
    session.AcceptLine("""{"type":"assistant","message":{"id":"thinking-first","content":[{"type":"text","text":"One reply."}]}}""");
    session.AcceptLine("""{"type":"stream_event","event":{"type":"message_stop"}}""");
    Require(reply == "One reply.", "F10 thinking before text preserves message id and does not double");
    session.BeginTurn();
    session.AcceptLine("""{"type":"assistant","message":{"id":"two","content":[{"type":"tool_use","name":"secret_tool","input":{"secret":"hidden"}},{"type":"text","text":"Second reply."}]}}""");
    Require(output.Contains("working…") && reply == "Second reply.", "unknown tool and turn reset");
    Console.WriteLine("OK  parser: chat only, activity map, partial/final deduplication, error result, unknown/non-JSON events");
}

static (Grid root, ContentControl side, ContentControl presenter, UserControl details, ScrollViewer scroll) Tree()
{
    var root = new Grid();
    for (var i = 0; i < 5; i++) root.ColumnDefinitions.Add(new ColumnDefinition());
    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); root.RowDefinitions.Add(new RowDefinition());
    var side = new ContentControl { Name = "SideBarViewModel", Content = AssistantLook.Text("Search\n\nPlatforms\n\nNintendo Entertainment System\nSuper Nintendo\nArcade"), Margin = new Thickness(12) };
    Grid.SetRow(side, 1); root.Children.Add(side);
    var scroll = new ScrollViewer { Content = new Grid { Children = { AssistantLook.Text("Game details\n\nManual · Images · Emulator\n\nYour library stays within reach.", 18) } } };
    var details = new UserControl { Content = new Grid { Children = { new Grid { Children = { scroll } } } } };
    var presenter = new ContentControl { Content = details }; Grid.SetColumn(presenter, 4); Grid.SetRow(presenter, 1); root.Children.Add(presenter);
    return (root, side, presenter, details, scroll);
}

static void PlacementCheck()
{
    Require(!typeof(IDisposable).IsAssignableFrom(typeof(AssistantPanel)), "LaunchBox calls Dispose() on every IDisposable visual in its details view, so the panel must not be one");
    Console.WriteLine("OK  host sweep: AssistantPanel is not IDisposable, so LaunchBox's details-view Dispose() walk cannot reach it");
    var panel = new AssistantPanel(false);
    panel.AddTurn("Keep this transcript.", true);
    var window = new AssistantWindow();
    var messages = new List<string>(); var placer = new PanelPlacement(panel, window, messages.Add); var tree = Tree();
    void OneParent(DependencyObject root)
    {
        Require(PanelPlacement.Parent(panel) != null, "panel has a parent");
        Require(PanelPlacement.Walk(root).Count(x => ReferenceEquals(x, panel)) == 1, "exactly one panel in host tree");
    }
    Require(placer.Place("right", tree.root, tree.details, true, false), "right placement"); OneParent(tree.root);
    Require(placer.Place("right", tree.root, tree.details, true, false), "repeat right placement"); OneParent(tree.root);
    var replacement = Tree(); tree.presenter.Content = replacement.details;
    Require(placer.Place("right", tree.root, replacement.details, true, false), "replacement details placement"); OneParent(tree.root);
    Require(!PanelPlacement.Walk(tree.details).Any(x => ReferenceEquals(x, panel)), "old details released");
    Require(placer.Place("left", tree.root, replacement.details, true, false), "left placement"); OneParent(tree.root);
    Require(Grid.GetColumn(tree.side) == 0 && Grid.GetRow(tree.side) == 1, "original sidebar attached layout retained");
    tree.side.Visibility = Visibility.Collapsed;
    Require(!placer.Place("left", tree.root, replacement.details, true, false) && placer.Floating && placer.Status.Contains("sidebar is hidden"), "hidden sidebar floats with accurate reason");
    tree.side.Visibility = Visibility.Visible;
    Require(placer.Place("floating", tree.root, replacement.details, true, false) && placer.Floating, "floating placement"); OneParent(window);
    Require(ReferenceEquals(tree.side.Parent, tree.root), "sidebar restored");
    Require(!placer.Place("right", tree.root, null, false, false) && placer.Floating && placer.Status.Length > 0, "null details fallback"); OneParent(window);
    Require(!placer.Place("right", tree.root, replacement.details, false, false) && placer.Floating, "hidden details fallback"); OneParent(window);
    Require(placer.Place("right", tree.root, replacement.details, true, false), "visible details restored"); OneParent(tree.root);
    var invalid = new Grid();
    Require(!placer.Place("right", invalid, invalid, true, false) && placer.Floating, "invalid right host fallback"); OneParent(window);
    Require(!placer.Place("left", invalid, null, true, false) && placer.Floating, "invalid left host fallback"); OneParent(window);
    Require(messages.Count > 0 && panel.Transcript == "Keep this transcript.", "fallback reported and transcript preserved");
    Require(panel.DataContext != null && panel.Resources.Contains(typeof(Button)), "local context and styles");
    placer.Detach(); window.Shutdown();
    Console.WriteLine("OK  synthetic-tree: right, repeat, replace, left, floating, null/hidden/missing host, one parent, transcript retained");
}

static void PackageCheck()
{
    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "dist"));
    var bridge = Path.Combine(output, "Plugins", "PadMaster");
    var managed = Path.Combine(output, "Local", "Plugins", PackageIdentity.Name);
    var files = Directory.GetFiles(output, "System.Speech.dll", SearchOption.AllDirectories);
    Require(files.Length == 1 && Path.GetDirectoryName(files[0]) == bridge && new FileInfo(files[0]).Length == 686888, "one root Windows Speech assembly, 686888 bytes");
    Require(!Directory.Exists(Path.Combine(bridge, "runtimes")) && !File.Exists(Path.Combine(bridge, "manifest.json")), "manifestless bridge without runtimes");
    Require(!Directory.GetFiles(bridge, "PadMaster.Settings.dll", SearchOption.AllDirectories).Any() && !Directory.GetFiles(Path.Combine(output, "Local"), "PadMaster.Bridge.dll", SearchOption.AllDirectories).Any(), "no duplicate assembly placement");
    Require(!Directory.GetFiles(output, "Unbroken.LaunchBox.Plugins.dll", SearchOption.AllDirectories).Any() && !Directory.GetFiles(output, "*.pdb", SearchOption.AllDirectories).Any(), "no API or PDB shipped");
    var packageFiles = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
    var username = new string(new[] { (char)102, (char)105, (char)115, (char)99, (char)104 });
    foreach (var file in packageFiles)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(file)).Replace("\0", "");
        Require(!new[] { '/', '\\' }.Any(sep => text.Contains("Users" + sep + username, StringComparison.OrdinalIgnoreCase)), $"distribution path leak in {Path.GetFileName(file)}");
    }
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(managed, "manifest.json")))!;
    string Field(string key) => manifest[key]?.GetValue<string>() ?? "";
    Require(manifest["SchemaVersion"]!.GetValue<int>() == 2 && Guid.Parse(Field("PluginId")) == Guid.Parse(PackageIdentity.Id) && Guid.Parse(Field("PluginId")) != Guid.Empty, "schema 2 and fixed GUID");
    Require(Field("Name") == PackageIdentity.Name && Field("Developer").Length > 0 && Field("InstalledVersion") == "0.2.0-beta", "manifest name, developer and version");
    Require(Field("OperatingSystemFamily") == "Windows" && Field("SourceKind") == "Local" && Field("RequiredApplicationVersion").Length == 0, "manifest OS/source/version rules");
    Require(manifest["Compatibility"]?["ApplicationVersionRange"]?.GetValue<string>() == "[14.1.0,14.2.0)", "tested host version range");
    Require(Field("Brief") == "Chat with your own AI assistant inside LaunchBox." && Field("Requirements") == "LaunchBox 14.1.x on Windows. Claude Code or Codex installed and signed in with your own subscription.", "manifest Brief and Requirements verbatim");
    Require(Field("Description") == "This is a beta, an early test release. Padmaster AI Assistant puts a small chat panel inside LaunchBox. Ask about your games, find a manual, check which emulator a game uses, or build playlists from your library, for example every Star Wars and Star Trek game you own. Type or use the microphone. Coming next: each game's controls drawn on the controller in your hands. It uses your own AI subscription (Claude Code or Codex); the plugin stores no keys and its own server listens only on this PC, while your assistant sends what you type and what it reads to your AI provider.", "manifest Description verbatim");
    Require(!manifest.AsObject().ContainsKey("Tags") && Field("IconPath") == "icon.png" && packageFiles.Length == 10, "Tags absent and ten-file distribution with relative icon");
    Require(File.ReadAllBytes(Path.Combine(bridge, "persona", "CLAUDE.md")).SequenceEqual(File.ReadAllBytes(Path.Combine(bridge, "persona", "AGENTS.md"))), "generated Codex persona is identical to the Claude source");
    using (var icon = File.OpenRead(Path.Combine(managed, Field("IconPath"))))
    {
        var bitmap = BitmapDecoder.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Require(bitmap.PixelWidth == 128 && bitmap.PixelHeight == 128, "icon is 128x128 PNG");
    }
    var coerced = Regex.Replace(Field("Name").Trim(), "[\\\"<>|:*?\\\\/\\r\\n]", "_");
    Require(Path.GetFileName(managed) == coerced, "folder equals coerced Name");
    var components = manifest["Components"]!.AsArray(); Require(components.Count == 1, "one component");
    var component = components[0]!;
    Require(component["ComponentType"]!.GetValue<string>() == "game-discovery-source/1" && component["Class"]!.GetValue<string>() == "PadMaster.Configuration.SettingsComponent", "component type and class");
    var assembly = component["Assembly"]!.GetValue<string>();
    Require(assembly == "PadMaster.Settings.dll" && File.Exists(Path.Combine(managed, assembly)), "safe component assembly exists");
    Require(component["Applications"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(new[] { "launchbox" }), "unique supported application");
    Console.WriteLine($"OK  distribution: schema/range/coercion/components, two folders, one Windows Speech DLL (686888 bytes), {packageFiles.Length} files byte-searched, no personal path/API/PDB");
}

static void SetupCheck()
{
    var panel = new AssistantPanel(false);
    const string cli = @"C:\Program Files\Claude Code\claude.exe";
    var expansions = 0; panel.ExpandedChanged = expanded => { if (expanded) expansions++; };
    var realLoginStarts = 0; panel.ConfigurationSaved = (_, signIn) => { if (signIn) realLoginStarts++; };
    panel.SetExpanded(false);
    panel.SetSetup(new("", cli, true));
    Require(panel.SetupVisible && panel.Expanded && expansions == 0, "unconfigured assistant expands setup once");
    panel.SetExpanded(false); panel.SetSetup(new("", cli, true));
    Require(!panel.Expanded && expansions == 0, "setup does not repeatedly force expansion");
    foreach (var json in new[] { "{\"loggedIn\":false}", "not JSON", "{}" })
    {
        panel.SetSetup(new("claude-code", cli, SetupState.ParseAuth(json)));
        Require(panel.SetupVisible, "false or unparseable auth shows setup");
    }
    panel.SetSetup(new("claude-code", null, true)); Require(panel.SetupVisible, "missing CLI shows setup");
    panel.SetSetup(new("claude-code", cli, SetupState.ParseAuth("{\"loggedIn\":true,\"apiProvider\":\"firstParty\",\"authMethod\":\"claude.ai\"}")));
    Require(!panel.SetupVisible && panel.Transcript.Contains("Hi, I’m Padmaster."), "signed-in configured state shows greeting and chat");
    var greeting = panel.Transcript;
    panel.SetSetup(new("claude-code", cli, true));
    Require(panel.Transcript == greeting, "greeting appears once per panel life");
    panel.SetSetup(new("claude-code", cli, false)); Require(panel.SetupVisible && panel.Expanded, "sign-out returns to setup and expands");
    panel.SetSetup(new("", null, false, true));
    Require(!panel.SetupVisible && panel.Transcript == greeting, "nolaunch has no card or new greeting");
    Require(realLoginStarts == 0, "canned setup states never request login");
    var login = Session.LoginCommand(cli, "unused");
    Require(login.ArgumentList.SequenceEqual(new[] { "auth", "login", "--claudeai" }) && login.UseShellExecute && !login.RedirectStandardOutput && !login.RedirectStandardError, "subscription-only visible login command, inspected without executing");
    Console.WriteLine("OK  setup: configured/unconfigured, CLI missing, auth true/false/invalid, one greeting, expand once, nolaunch; zero login requests");
}

static async Task LoginCheck()
{
    var answer = new TaskCompletionSource<AuthState>(TaskCreationOptions.RunContinuationsAsynchronously);
    var launches = 0; var authenticated = 0;
    using var flow = new SignInFlow(() => launches++, token => answer.Task.WaitAsync(token), _ => { }, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    flow.Authenticated += () => authenticated++;
    var first = flow.Start(); await flow.Start();
    Require(launches == 1 && flow.Waiting, "duplicate sign-in click is ignored");
    answer.SetResult(new(true, true, "")); await first.WaitAsync(TimeSpan.FromSeconds(2));
    Require(authenticated == 1 && !flow.Waiting, "fake successful auth stops polling");
    var message = "";
    using var timeout = new SignInFlow(() => launches++, _ => Task.FromResult(new AuthState(true, false, "")), _ => { }, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(25));
    timeout.Message += text => message = text;
    await timeout.Start(); Require(!timeout.Waiting && message.Contains("took too long"), "login timeout returns to button");
    using var stopped = new SignInFlow(() => launches++, _ => Task.FromResult(new AuthState(true, false, "")), _ => { }); stopped.Dispose(); await stopped.Start();
    Require(launches == 2, "teardown prevents login");
    var rejected = 0; var checks = 0; message = "";
    using var other = new SignInFlow(() => { }, _ => { checks++; return Task.FromResult(AuthState.Parse("""{"loggedIn":true,"authMethod":"api_key","apiProvider":"bedrock"}""")); }, _ => { }, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    other.Authenticated += () => rejected++; other.Message += text => message = text;
    await other.Start(); Require(checks == 1 && rejected == 0 && !other.Waiting && message.Contains("user settings"), "G4 finished unsupported login stops immediately with user-settings remedy");
    var finished = false; checks = 0;
    using var pendingOther = new SignInFlow(() => { }, _ => { if (++checks == 2) finished = true; return Task.FromResult(AuthState.Parse("""{"loggedIn":true,"authMethod":"console","apiProvider":"firstParty"}""")); }, _ => { }, TimeSpan.Zero, TimeSpan.FromSeconds(1)) { Finished = () => finished };
    await pendingOther.Start(); Require(checks == 2 && !pendingOther.Waiting, "G4 other-account polling waits for login process completion");
    Console.WriteLine("OK  fake login: one at a time, auth completion, timeout, teardown; no real auth process launched");
}

static void Shot(string path)
{
    Console.WriteLine("mic: injected for screenshots; no audio device opened");
    foreach (var placement in new[] { "right", "left", "floating", "setup", "hold" })
    {
        var panel = new AssistantPanel(false);
        panel.SetMicrophone(Microphone.Injected(_ => { }));
        panel.SetSetup(new("claude-code", @"C:\Tools\Claude\claude.exe", placement != "setup"));
        panel.SetStatus(placement == "setup" ? "not signed in" : "connected · 1,248 games · NES 275"); panel.SetReady(placement != "setup");
        if (placement != "setup")
        {
            panel.AddTurn("Find the manual for Blaster Master.", true);
            panel.AddTurn("I’ll look for the manual in your library.", false);
            if (placement != "hold") panel.ApplyState(new(1, true, 0, false, false, false, "", 1));
            panel.SetActivity("looking up the game…");
        }
        if (placement == "hold") panel.ShowBulk(new("remove games", 21, _ => { }));
        var window = new AssistantWindow(); var placer = new PanelPlacement(panel, window, _ => { }, _ => { });
        var tree = Tree();
        tree.root.Background = AssistantLook.Background;
        tree.root.ColumnDefinitions[0].Width = new GridLength(340);
        tree.root.ColumnDefinitions[1].Width = new GridLength(12);
        tree.root.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        tree.root.ColumnDefinitions[3].Width = new GridLength(12);
        tree.root.ColumnDefinitions[4].Width = new GridLength(360);
        var title = AssistantLook.Text("LaunchBox  /  My library", 17); title.Margin = new Thickness(14, 8, 0, 0); Grid.SetColumnSpan(title, 5); tree.root.Children.Add(title);
        var library = new Border { Background = AssistantLook.Surface, CornerRadius = new CornerRadius(8), Margin = new Thickness(8), Padding = new Thickness(18), Child = AssistantLook.Text("Nintendo\nEntertainment\nSystem\n\nBlaster Master\n\n275 games", 22) };
        Grid.SetColumn(library, 2); Grid.SetRow(library, 1); tree.root.Children.Add(library);
        Require(placer.Place(placement is "setup" or "hold" ? "right" : placement, tree.root, tree.details, true, false), $"shot placement {placement}");
        var content = placement == "floating" ? (FrameworkElement)window.Content : tree.root;
        var width = placement == "floating" ? 520 : 1100; var height = placement == "floating" ? 720 : 760;
        content.Width = width; content.Height = height;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var frame = new DispatcherFrame(); _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var destination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "_" + placement + ".png");
        using (var stream = File.Create(destination)) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        Console.WriteLine($"OK  shot {destination}"); placer.Detach(); window.Shutdown();
    }
}

static async Task<int> Smoke(string? selected)
{
    var failed = 0;
    foreach (var kind in selected == null ? new[] { "claude-code", "codex" } : new[] { selected })
    {
        var name = Settings.AssistantName(kind); var cli = Settings.DetectCli(kind: kind);
        if (cli == null) { Console.WriteLine($"SKIP smoke {name}: not installed"); continue; }
        var auth = await Session.SignedIn(cli, Path.GetTempPath(), _ => { }, kind: kind);
        if (!auth.SignedIn) { Console.WriteLine($"SKIP smoke {name}: not signed in with a subscription; {auth.Reason}"); continue; }
        failed |= await SmokeOne(cli, kind);
    }
    return failed;
}

static async Task<int> SmokeOne(string cli, string kind)
{
    var name = Settings.AssistantName(kind);
    var temp = Path.Combine(Path.GetTempPath(), "PadMaster-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    foreach (var persona in new[] { "CLAUDE.md", "AGENTS.md" }) File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "dist", "Plugins", "PadMaster", "persona", persona)), Path.Combine(temp, persona));
    var config = Path.Combine(temp, "mcp.json");
    var server = new Mcp(47801, "/mcp", [new Tool("status", "Local test bridge status", new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, _ => new JsonObject { ["port"] = 47801 })]);
    Session.WriteMcpConfig(config, 47801, server.Token);
    var smokeLog = Path.Combine(temp, "smoke.log");
    var smokeGate = new object();
    void Note(string line)
    {
        lock (smokeGate)
            for (var attempt = 0; attempt < 5; attempt++)
                try { File.AppendAllText(smokeLog, line + Environment.NewLine); return; }
                catch (IOException) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) { Thread.Sleep(20); }
    }
    using var session = new Session(cli, temp, config, Note) { Kind = kind };
    EventHandler cleanup = (_, _) => session.Dispose();
    AppDomain.CurrentDomain.ProcessExit += cleanup;
    var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var reply = "";
    string? firstSession = null;
    session.Reply += text => reply = text;
    session.Done += success => done.TrySetResult(success);
    session.Failure += text => { Console.WriteLine($"FAIL: smoke {name}: {text}"); done.TrySetResult(false); };
    try
    {
        server.Start();
        Console.WriteLine($"smoke {name}");
        for (var turn = 1; turn <= 2; turn++)
        {
            reply = ""; done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Send(turn == 1 ? "call the status tool and reply with the port number only" : "what port did you just report?");
            bool success;
            try { success = await done.Task.WaitAsync(TimeSpan.FromSeconds(180)); }
            catch (TimeoutException) { Console.WriteLine($"FAIL: smoke {name} turn {turn} timed out after 180 seconds"); return 1; }
            if (!success || !reply.Contains("47801")) { Console.WriteLine($"FAIL: smoke {name} turn {turn}; see {Path.Combine(temp, "smoke.log")}"); return 1; }
            if (turn == 1) firstSession = session.LastSessionId;
            else if (firstSession == null || firstSession != session.LastSessionId) { Console.WriteLine($"FAIL: smoke {name} changed session between turns"); return 1; }
            Console.WriteLine($"OK  smoke {name} turn {turn}: 47801");
        }
        if (session.Starts != (kind == "codex" ? 2 : 1) || session.LastSessionId == null) { Console.WriteLine($"FAIL: smoke {name} unexpected process count or missing session"); return 1; }
        Console.WriteLine($"OK  smoke {name}: two turns, {session.Starts} child processes, same session, MCP connected"); return 0;
    }
    catch (Exception e) { Console.WriteLine($"FAIL: smoke {name}: {SafeDiagnostics.Error(e)}"); return 1; }
    finally { session.Shutdown(); server.Stop(); AppDomain.CurrentDomain.ProcessExit -= cleanup; }
}
}

static class LaunchBoxDir
{
    public static readonly string Value = Environment.GetEnvironmentVariable("LaunchBoxDir") is { Length: > 0 } env ? env : typeof(LaunchBoxDir).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "LaunchBoxDir")?.Value ?? @"C:\LaunchBox";
}
