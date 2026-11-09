using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using PadMaster;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Plugins.Services.Storage;

static class ApiCheck
{
    static readonly string Docs = Path.Combine(LaunchBoxDir.Value, "Core", "Unbroken.LaunchBox.Plugins.xml");
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run()
    {
        LazyStartup(); McpTools(); Bulk(); NoDelete(); Catalog(); Example(); Reload(); Children(); Calls(); Serialization(); Services(); Limits(); DispatcherCheck();
    }
    static Mcp McpTools()
    {
        var library = new Library(); var server = new Mcp(47801, "/mcp", library.Api.Tools());
        var listed = Rpc(server, "tools/list")["tools"]!.AsArray();
        Require(listed.Select(t => t!["name"]!.GetValue<string>()).Order().SequenceEqual(new[] { "controller", "lb_call", "lb_catalog", "lb_create", "lb_get", "lb_list", "lb_set" }), "MCP tools/list includes controller alongside all six lb_* tools");
        var tool = listed.Single(t => t!["name"]!.GetValue<string>() == "controller")!;
        Require(tool["inputSchema"]!["type"]!.GetValue<string>() == "object" && tool["inputSchema"]!["properties"]!.AsObject().Count == 0 && tool["inputSchema"]!["required"]!.AsArray().Count == 0, "controller takes no arguments");
        Require(!library.Api.DocumentationLoaded, "MCP tools/list leaves API documentation lazy");
        return server;
    }
    static JsonNode Rpc(Mcp server, string method, JsonNode? args = null) => (JsonNode)typeof(Mcp).GetMethod("Rpc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, [method, args])!;
    public static void Controller(bool requirePad = true)
    {
        var result = Rpc(McpTools(), "tools/call", new JsonObject { ["name"] = "controller", ["arguments"] = new JsonObject() });
        Require(result["isError"]?.GetValue<bool>() == false, "controller call: " + result["content"]?[0]?["text"]);
        var identity = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        var slots = identity["xinput"]!.AsArray(); var devices = identity["hid"]!.AsArray();
        Require(slots.Count == 4 && slots.Select(s => s!["slot"]!.GetValue<int>()).SequenceEqual(Enumerable.Range(0, 4)) && slots.All(s => s!["connected"] is JsonValue value && value.TryGetValue<bool>(out _)), "controller returns slots 0-3 and connection booleans");
        foreach (var device in devices)
            Require(device!.AsObject().ContainsKey("name") && (device["name"] == null || device["name"] is JsonValue name && name.TryGetValue<string>(out _)) && new[] { "vid", "pid" }.All(key => device[key] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length == 4 && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')) && !string.IsNullOrWhiteSpace(device["path"]?.GetValue<string>()), "controller HID identity shape");
        if (!requirePad && !slots[0]!["connected"]!.GetValue<bool>()) { Console.WriteLine("SKIP controller: no pad"); return; }
        Require(slots[0]!["connected"]!.GetValue<bool>(), "controller: xinput slot 0 is not connected");
        var pad = devices.FirstOrDefault(d => d!["vid"]!.GetValue<string>() == "045E" && d["pid"]!.GetValue<string>() == "028E");
        Require(pad != null, "controller: HID 045E:028E is missing");
        Require(pad!["name"] != null, "controller: HID 045E:028E name is null");
        Console.WriteLine($"OK  controller: xinput slot 0 connected; hid 045E:028E {pad["name"]?.GetValue<string>() ?? "null"}");
    }
    static void LazyStartup()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var library = new Library();
                Require(!library.Api.DocumentationLoaded && !Api.ShapesLoaded, "Api constructor and Tools leave shapes and documentation lazy on STA");
                var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => library.Call("lb_catalog", "{\"type\":\"IGame\"}"))).ToArray();
                Task.WaitAll(callers); Require(library.Api.DocumentationLoaded && callers.All(t => t.Result["type"]!.ToString() == "IGame"), "first concurrent calls load metadata safely off STA");
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Require(thread.Join(TimeSpan.FromSeconds(10)), "lazy startup completes");
        if (error != null) throw error;
        Console.WriteLine("OK  beta startup: Api/Tools on STA leave shapes and documentation lazy; four concurrent worker catalog calls succeed; no live startup timing claimed");
    }
    public static void Bulk()
    {
        var library = new Library(); var settings = new Settings(); var now = DateTime.UtcNow; BulkPrompt? prompt = null; var bars = 0;
        var completions = new System.Collections.Concurrent.ConcurrentQueue<(string line, string? hidden)>();
        var guard = new BulkChanges(() => settings, value => { prompt = value; bars++; return true; }, (line, hidden) => completions.Enqueue((line, hidden))) { Now = () => now };
        var api = new Api(library.Data, Docs, bulk: guard); var tools = api.Tools().ToDictionary(t => t.Name);
        var playlist = Bag.Create<IPlaylist>(("PlaylistId", "bulk"), ("Name", "PLANTED-SUMMARY")); library.Playlists.Add(playlist);
        var rows = new List<Bag>(); playlist.Children[typeof(IPlaylistGame)] = rows; var calls = 0; var removed = 0;
        playlist.Custom = (method, _) => { Require(method.Name == "ClearGames", "bulk uses real ClearGames"); Interlocked.Increment(ref calls); Interlocked.Add(ref removed, rows.Count); rows.Clear(); return null; };
        void Fill(int count) { rows.Clear(); for (var i = 0; i < count; i++) rows.Add(Bag.Create<IPlaylistGame>(("GameId", "g" + i))); }
        JsonNode Clear(string extras = "") => tools["lb_call"].Handler(JsonNode.Parse("{\"type\":\"IPlaylist\",\"id\":\"bulk\",\"method\":\"ClearGames\"" + extras + "}"));
        Fill(19); Clear(); Require(calls == 1 && removed == 19 && !guard.Waiting, "19 removals direct");
        Fill(20); Clear(); Require(calls == 2 && removed == 39, "20 removals direct at threshold 20");
        Fill(21); var held = Clear(",\"summary\":\"PLANTED-SUMMARY\"");
        Require(held["held"]!.GetValue<bool>() && held["count"]!.GetValue<int>() == 21 && calls == 2 && prompt != null, "21 held before any effect");
        Require(held["summary"]!.ToString() == "clear playlist entries" && prompt!.Summary == "clear playlist entries" && !prompt.Summary.Contains("PLANTED"), "planted argument and library name cannot control summary");
        var token = held["token"]!.ToString();
        Require(Clear()["message"]?.ToString() == "another change is waiting for your answer" && calls == 2, "no-self-approval: repeat call never runs held action");
        Reject(() => Clear(",\"token\":\"" + token + "\""), "Only the panel buttons");
        Require(calls == 2 && guard.Waiting, "no-self-approval: token cannot release hold");
        Require(Clear(",\"approved\":true")["refused"]!.GetValue<bool>() && calls == 2, "model approval argument cannot release hold");
        var yes = prompt!; yes.Answer(true); yes.Answer(true);
        Require(SpinWait.SpinUntil(() => completions.Any(c => c.hidden?.StartsWith("The user clicked Yes") == true), 5000), "Yes produces hidden result");
        Require(calls == 3 && removed == 60 && !guard.Waiting, "Yes exactly once despite two clicks");
        Fill(21); Clear(); prompt!.Answer(false); Require(calls == 3 && rows.Count == 21 && completions.Any(c => c.hidden == "The user clicked No; nothing changed."), "No changes nothing and reports hidden turn");
        Clear(); now = now.AddMinutes(6); prompt!.Answer(true); Require(calls == 3 && !guard.Waiting, "expired hold refused");
        Clear(); var old = prompt!; guard.Invalidate("Session restarted. Nothing changed."); old.Answer(true); Require(calls == 3 && !guard.Waiting, "restart invalidates hold");
        Clear(); old = prompt!; guard.Invalidate("Disabled. Nothing changed."); old.Answer(true); Require(calls == 3, "disable invalidates hold");
        Clear(); old = prompt!; guard.Invalidate("Shutdown. Nothing changed."); old.Answer(true); Require(calls == 3, "shutdown invalidates hold");
        playlist.FailChildren = true; held = Clear(); Require(held["held"]!.GetValue<bool>() && held["count"] == null && calls == 3, "unknown clear count held"); prompt!.Answer(false); playlist.FailChildren = false;
        settings = settings with { ConfirmBulk = false }; Clear(); Require(calls == 4 && rows.Count == 0, "setting off runs 21 directly"); settings = settings with { ConfirmBulk = true };
        var unavailable = new Api(library.Data, Docs, bulk: new(() => settings, _ => false, (_, _) => { })); Fill(21);
        Require(unavailable.Tools().Single(t => t.Name == "lb_call").Handler(JsonNode.Parse("{\"type\":\"IPlaylist\",\"id\":\"bulk\",\"method\":\"ClearGames\"}"))["refused"]!.GetValue<bool>() && calls == 4, "invisible panel refuses rather than holding");
        var count = 0; library.Custom = (method, _) => { Require(method.Name == "AddGameController", "collection mutation"); count++; return "controller"; };
        var platforms = new JsonArray(Enumerable.Range(0, 21).Select(i => (JsonNode?)JsonValue.Create("p" + i)).ToArray());
        held = tools["lb_call"].Handler(new JsonObject { ["method"] = "AddGameController", ["args"] = new JsonArray("controller", "category", platforms) });
        Require(held["held"]!.GetValue<bool>() && held["count"]!.GetValue<int>() == 21 && count == 0, "array argument element count held"); prompt!.Answer(false);
        foreach (var size in new[] { 21, 2000 })
        {
            var creation = new Library(); var creationBars = 0;
            var creationGuard = new BulkChanges(() => settings, _ => { creationBars++; return false; }, (_, _) => { });
            var create = new Api(creation.Data, Docs, bulk: creationGuard).Tools().Single(t => t.Name == "lb_create");
            var batch = new JsonArray(Enumerable.Range(0, size).Select(i => (JsonNode)new JsonObject { ["values"] = new JsonObject { ["Name"] = "new" + i } }).ToArray());
            var result = create.Handler(new JsonObject { ["type"] = "IPlaylist", ["items"] = batch });
            Require(result["held"] == null && result["created"]!.GetValue<int>() == size && creationBars == 0 && !creationGuard.Waiting, $"{size} creations run directly with no bar");
            Require(creation.Created == size && creation.Playlists.Count == size && creation.Playlists.Select(p => p.Values["Name"]).Distinct().Count() == size && creation.Background == 1 && creation.Saves == 0 && creation.Runs == 2, $"{size} creations use one background save with no duplicates across both host passes");
        }
        held = Clear(); var pending = prompt; var previousBars = bars; var previousSaves = library.Saves;
        var factory = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IDataManager","method":"AddNewPlaylist","args":["factory-created"]}"""));
        Require(held["held"]!.GetValue<bool>() && factory["held"] == null && library.Created == 1 && library.Saves == previousSaves + 1 && library.Background == 0, "AddNew factory creates directly with one save while 21 removals remain held");
        Require(guard.Waiting && ReferenceEquals(prompt, pending) && bars == previousBars && calls == 4, "creation neither shows another bar nor releases the pending removal"); prompt!.Answer(false);
        Console.WriteLine("OK  beta bulk: 19/20 removals direct, 21 held, Yes once, No/expiry/restart/disable/shutdown, off, no self-approval, planted summary, one pending, unknown counts, arrays, 21/2000 creations direct with one double-run save and no bar, AddNew direct, invisible-panel refusal");
    }
    static void NoDelete()
    {
        const string message = "Deleting is not possible through the assistant. Remove items yourself in LaunchBox.";
        var library = new Library(); var settings = new Settings(); var bars = 0; BulkPrompt? prompt = null;
        var guard = new BulkChanges(() => settings, value => { prompt = value; bars++; return true; }, (_, _) => { });
        var store = new MemoryStore { Value = [1] };
        var api = new Api(library.Data, Docs, new Dictionary<Type, Func<object?>> { [typeof(IPluginDataStore)] = () => store }, bulk: guard); var tools = api.Tools().ToDictionary(t => t.Name);
        var invoked = 0; Func<MethodInfo, object?[], object?> count = (m, _) => { invoked++; return m.ReturnType == typeof(bool) ? true : null; };
        library.Custom = count; library.Games[0].Custom = count;
        var playlist = Bag.Create<IPlaylist>(("PlaylistId", "p"), ("Name", "P")); library.Playlists.Add(playlist); playlist.Custom = count;
        playlist.Children[typeof(IPlaylistGame)] = [Bag.Create<IPlaylistGame>(("GameId", "a"))];
        playlist.Children[typeof(IPlaylistFilter)] = [Bag.Create<IPlaylistFilter>(("FieldKey", "Title"), ("ComparisonTypeKey", "Contains"), ("Value", "x"))];
        foreach (var call in new[]
        {
            """{"method":"TryRemoveGame","args":["a"]}""",
            """{"type":"IDataManager","method":"tryremoveplatform","args":["not a platform"]}""",
            """{"type":"IDataManager","method":"M:Unbroken.LaunchBox.Plugins.Data.IDataManager.TryRemovePlaylist(Unbroken.LaunchBox.Plugins.Data.IPlaylist)","args":["p"]}""",
            """{"type":"IGame","id":"a","method":"TryRemoveCustomField","args":[null]}""",
            """{"type":"IGame","id":"a","method":"TryRemoveAdditionalApplication"}""",
            """{"type":"IPlaylist","id":"p","method":"TryRemovePlaylistFilter","args":[null]}""",
            """{"type":"IPluginDataStore","method":"RemoveValueAsync","args":[{"Key":"test"}]}""",
            """{"method":"TryRemoveGame","args":["a"],"approved":true,"force":true}"""
        })
        {
            var result = tools["lb_call"].Handler(JsonNode.Parse(call)) as JsonObject;
            Require(result?["refused"]?.GetValue<bool>() == true && result["message"]!.ToString() == message, "refused with exact message: " + call);
        }
        settings = settings with { ConfirmBulk = false };
        Require((tools["lb_call"].Handler(JsonNode.Parse("""{"method":"TryRemoveGame","args":["a"]}""")) as JsonObject)?["refused"]?.GetValue<bool>() == true, "bulk setting off cannot enable deletion");
        settings = settings with { ConfirmBulk = true };
        Require(invoked == 0 && bars == 0 && !guard.Waiting && library.Saves == 0 && library.Background == 0 && library.Removes == 0 && store.Removes == 0 && library.Playlists.Count == 1, "locked removals: 0 invocations, 0 saves, no bar, guard idle");
        var data = library.Call("lb_catalog", """{"type":"IDataManager"}""")["methods"]!.AsArray().Select(m => m!["name"]!.ToString()).ToArray();
        var members = library.Call("lb_catalog", """{"type":"IPlaylist"}""")["methods"]!.AsArray().Select(m => m!["name"]!.ToString()).ToArray();
        Require(data.Contains("AddNewGame") && !data.Any(n => n.StartsWith("TryRemove")) && members.Contains("ClearGames") && members.Contains("TryRemovePlaylistGame") && !members.Contains("TryRemovePlaylistFilter"), "catalog hides locked removals and keeps playlist membership");
        var writableCollections = library.Call("lb_catalog", "{}")["types"]!.AsArray().Where(t => t!["kind"]!.ToString() == "interface").Select(t => library.Call("lb_catalog", JsonSerializer.Serialize(new { type = t!["type"]!.ToString() }))).SelectMany(t => t["properties"]!.AsArray().Select(p => (type: t["type"]!.ToString(), name: p!["name"]!.ToString(), set: p["set"]!.GetValue<bool>(), kind: p["type"]!.ToString())))
            .Where(p => p.set && (p.kind.EndsWith("[]") || p.kind.Contains('<') && !p.kind.StartsWith("Nullable<"))).Select(p => p.type + "." + p.name).ToArray();
        Require(writableCollections.Length == 0, "lb_set cannot empty a collection: no library interface exposes a writable collection property: " + string.Join(", ", writableCollections));
        var removed = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPlaylist","id":"p","method":"TryRemovePlaylistGame","args":[{"type":"IPlaylistGame","id":"a","parent":{"type":"IPlaylist","id":"p"}}]}"""));
        Require(removed.GetValue<bool>() && invoked == 1 && bars == 0 && library.Saves == 1, "playlist membership removal still runs");
        playlist.Children[typeof(IPlaylistGame)] = Enumerable.Range(0, 21).Select(i => Bag.Create<IPlaylistGame>(("GameId", "g" + i))).ToList();
        var held = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPlaylist","id":"p","method":"ClearGames"}"""));
        Require(held["held"]?.GetValue<bool>() == true && bars == 1 && invoked == 1 && guard.Waiting, "playlist ClearGames still bulk-guarded"); prompt!.Answer(false);
        Console.WriteLine("OK  no-delete: non-playlist Remove*/Delete*/Clear* refused, 0 invocations, guard idle; playlist RemoveGame/ClearGames still allowed under bulk guard");
    }
    static void Catalog()
    {
        var library = new Library(); var catalog = library.Call("lb_catalog", "{}"); var types = catalog["types"]!.AsArray();
        Require(types.Count >= 40 && catalog["truncated"] == null, "catalog lists at least 40 real reachable types without truncation");
        Require(catalog["documentation"]!.ToString().Contains("loaded"), "host Core XML loaded");
        var game = library.Call("lb_catalog", """{"type":"IGame"}"""); var properties = game["properties"]!.AsArray();
        var setters = properties.Count(p => p!["set"]!.GetValue<bool>() && p["summary"]!.GetValue<string>().Length > 0);
        Require(setters >= 60 && properties.Single(p => p!["name"]!.ToString() == "Title")!["summary"]!.ToString().Contains("title", StringComparison.OrdinalIgnoreCase), "IGame includes 60 documented setters and Title summary");
        var overloaded = game["methods"]!.AsArray().Where(m => m!["name"]!.ToString() == nameof(IGame.GetAllImagesWithDetails)).ToArray();
        Require(overloaded.Length == 2 && overloaded.Select(m => m!["signature"]!.ToString()).Distinct().Count() == 2 && overloaded.All(m => m!["summary"]!.ToString().Length > 0), "XML matches both GetAllImagesWithDetails overload signatures");
        Require(overloaded.Single(m => m!["params"]!.AsArray().Count == 1)!["params"]![0]!["summary"]!.ToString().Length > 0, "overload parameter XML summary");
        var notification = library.Call("lb_catalog", """{"type":"IPluginProgressNotification"}""");
        Require(notification["properties"]!.AsArray().Single(p => p!["name"]!.ToString() == "Message")!["summary"]!.ToString().Contains("message currently displayed"), "inherited interface XML summaries resolved on declaration");
        var missing = new Library(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml")).Call("lb_catalog", """{"type":"IGame"}""");
        Require(missing["documentation"]!.ToString().Contains("reflection names only") && missing["properties"]!.AsArray().Count == properties.Count, "missing XML preserves reflection catalog and declares fallback");
        Require(library.Call("lb_catalog", """{"search":"THIS_WILL_NEVER_MATCH"}""")["types"]!.AsArray().Count == 0, "catalog search filters names and summaries");
        foreach (var secret in new[] { "IPluginSecretStore", "IPluginCredentialStore", "IFirstPartyHttpClientFactory" })
        {
            Require(library.Call("lb_catalog", JsonSerializer.Serialize(new { type = secret }))["service"]!["exposure"]!.ToString() == "not exposed", "sensitive/denied service catalog entry");
            Reject(() => library.Call("lb_get", JsonSerializer.Serialize(new { type = secret })), "Unknown");
        }
        foreach (var tool in library.Tools.Values.Where(t => t.Name is "lb_set" or "lb_create" or "lb_call")) Require(tool.Description.StartsWith("WRITES to the LaunchBox library."), "mutating descriptions disclose writes");
        var count = types.Sum(t => t!["members"]!.GetValue<int>());
        Console.WriteLine($"OK  API catalog: {types.Count} real reachable types, {count} members, {setters} documented IGame setters, overload/inherited XML, missing XML fallback, service exclusions");
    }
    static void Example()
    {
        var library = new Library();
        library.Games[0].Values["ApplicationPath"] = @"C:\Games\Star Wars (USA) (Disc 1).chd"; library.Games[0].Values["LaunchBoxDbId"] = 123;
        library.Games[1].Values["ApplicationPath"] = @"Games\NES\Star Trek (USA).nes";
        var wars = library.Call("lb_list", """{"root":"games","where":{"tItLe":{"contains":"star wars"}},"fields":["Id","Title","Platform","ApplicationPath","LaunchBoxDbId"]}""");
        var trek = library.Call("lb_list", """{"root":"games","where":{"Title":{"contains":"Star Trek"}},"fields":["Id","Title","Platform","ApplicationPath","LaunchBoxDbId"]}""");
        Require(wars["total"]!.GetValue<int>() == 1 && trek["total"]!.GetValue<int>() == 1, "three fake games, two matching universes");
        var selected = wars["rows"]!.AsArray().Concat(trek["rows"]!.AsArray()).ToArray();
        var created = library.Call("lb_create", """{"type":"playlist","values":{"Name":"Space","IncludeWithPlatforms":true,"Notes":"Both universes"}}""");
        var playlist = library.Playlists.Single(); var id = created["rows"]![0]!["PlaylistId"]!.ToString();
        Require(library.Created == 1 && (bool)playlist.Values["IncludeWithPlatforms"]! && library.Background == 1 && library.Refreshes == 1 && library.Runs == 2, "playlist created once, IncludeWithPlatforms survives two passes, one save and one refresh");
        var items = new JsonArray(selected.Select((g, i) =>
        {
            var values = new JsonObject { ["GameId"] = g!["Id"]!.ToString(), ["GameTitle"] = g["Title"]!.ToString(), ["GamePlatform"] = g["Platform"]!.ToString(), ["GameFileName"] = Path.GetFileName(g["ApplicationPath"]!.GetValue<string>()), ["ManualOrder"] = i };
            if (g["LaunchBoxDbId"] is { } databaseId) values["LaunchBoxDbId"] = databaseId.DeepClone();
            return (JsonNode)new JsonObject { ["values"] = values };
        }).ToArray());
        Require(items[0]!["values"]!["LaunchBoxDbId"]!.GetValue<int>() == 123 && !items[1]!["values"]!.AsObject().ContainsKey("LaunchBoxDbId"), "playlist example copies database ID only when present");
        var result = library.Tools["lb_create"].Handler(new JsonObject { ["type"] = "playlistGame", ["parent"] = new JsonObject { ["type"] = "IPlaylist", ["id"] = id }, ["items"] = items });
        Require(result["created"]!.GetValue<int>() == 2 && playlist.Children[typeof(IPlaylistGame)].Count == 2 && library.Background == 2 && library.Runs == 4 && library.Refreshes == 2, "one bulk save creates exactly two rows across four callbacks");
        var row = library.Call("lb_get", $$$"""{"type":"IPlaylistGame","id":"a","parent":{"type":"IPlaylist","id":"{{{id}}}"}}""");
        Require(row["GameTitle"]!.ToString() == "Star Wars" && row["GameId"]!.ToString() == "a", "playlist child reads by parent plus GameId");
        Require(row["GamePlatform"]!.ToString() == "PC" && row["GameFileName"]!.ToString() == "Star Wars (USA) (Disc 1).chd" && row["LaunchBoxDbId"]!.GetValue<int>() == 123 && row["ManualOrder"]!.GetValue<int>() == 0, "playlist row preserves metadata and filename only across both save passes");
        var other = library.Call("lb_get", $$$"""{"type":"IPlaylistGame","id":"b","parent":{"type":"IPlaylist","id":"{{{id}}}"}}""");
        Require(other["GameId"]!.ToString() == "b" && other["GameTitle"]!.ToString() == "Star Trek" && other["GamePlatform"]!.ToString() == "NES" && other["GameFileName"]!.ToString() == "Star Trek (USA).nes" && other["LaunchBoxDbId"] == null && other["ManualOrder"]!.GetValue<int>() == 1, "playlist row strips relative directory and preserves absent database ID");
        var set = library.Call("lb_set", """{"type":"IGame","id":"a","values":{"Favorite":true,"Title":"Star Wars updated"}}""");
        Require(set["Favorite"]!.GetValue<bool>() && library.Games[0].Values["Title"]?.ToString() == "Star Wars updated" && library.Background == 3 && library.Refreshes == 3, "two-field set uses one BackgroundReloadSave");
        Require(library.Call("lb_call", $$$"""{"method":"TryRemovePlaylist","args":["{{{id}}}"]}""")["refused"]!.GetValue<bool>() && library.Playlists.Count == 1 && library.Removes == 0 && library.Saves == 0 && library.Background == 3 && library.Refreshes == 3, "TryRemove refused: no invocation, no save, no refresh");
        Console.WriteLine("OK  API example: Star Wars/Star Trek -> one visible playlist -> two complete rows with filename-only paths and optional database IDs, get/set, removal refused, save/refresh counts");
    }
    static void Reload()
    {
        var library = new Library();
        library.BeforeSecond = () => library.Games[0] = Bag.Create<IGame>(("Id", "a"), ("Title", "disk copy"), ("Platform", "PC"));
        library.Call("lb_set", """{"type":"IGame","id":"a","values":{"Title":"changed","Favorite":true}}""");
        Require(library.Games[0].Values["Title"]?.ToString() == "changed" && (bool)library.Games[0].Values["Favorite"]!, "set re-resolves the replacement object on writer thread");
        library.BeforeSecond = () => { var original = library.Playlists.Single(); library.Playlists[0] = Bag.Create<IPlaylist>(("PlaylistId", original.Values["PlaylistId"]), ("Name", "Space"), ("IncludeWithPlatforms", false)); };
        library.Call("lb_create", """{"type":"playlist","values":{"Name":"Space","IncludeWithPlatforms":true}}""");
        Require(library.Playlists.Count == 1 && library.Created == 1 && (bool)library.Playlists[0].Values["IncludeWithPlatforms"]!, "create re-resolves replacement by recorded key and re-applies flag");
        var id = library.Playlists[0].Values["PlaylistId"]!.ToString();
        library.BeforeSecond = () => library.Playlists[0].Children.Clear();
        library.Call("lb_create", $$$"""{"type":"playlistGame","parent":{"type":"IPlaylist","id":"{{{id}}}"},"items":[{"values":{"GameId":"a"}},{"values":{"GameId":"b"}}]}""");
        Require(library.Playlists[0].Children[typeof(IPlaylistGame)].Count == 2, "bulk recreates disappeared children without duplicates");
        library.BeforeSecond = () => library.Playlists.Clear();
        library.Call("lb_create", """{"type":"playlist","values":{"Name":"Recreated","IncludeWithPlatforms":true}}""");
        Require(library.Playlists.Count == 1 && (bool)library.Playlists[0].Values["IncludeWithPlatforms"]!, "factory recreates a removed object on second pass");
        library.BeforeSecond = () => library.FailRead = true;
        library.Call("lb_set", """{"type":"IGame","id":"a","values":{"Title":"first pass"}}""");
        Require(library.Logs.Any(s => s.Contains("API background save: InvalidOperationException")), "second-pass exceptions logged and never escape");
        var delayed = new Library { DeferSecond = true };
        var response = delayed.Call("lb_create", """{"type":"playlist","values":{"Name":"Space","IncludeWithPlatforms":true}}"""); var before = response.ToJsonString();
        delayed.FinishSecond();
        Require(response.ToJsonString() == before && delayed.Created == 1 && delayed.Playlists.Count == 1, "deferred writer cannot mutate response or duplicate object");
        var handles = new Library(); var returned = handles.Call("lb_call", """{"method":"GetGameById","args":["a"]}""");
        handles.BeforeSecond = () => handles.Games[0] = Bag.Create<IGame>(("Id", "a"), ("Title", "disk copy"));
        handles.Call("lb_set", JsonSerializer.Serialize(new { type = "IGame", id = returned["$handle"]!.ToString(), values = new { Title = "through handle" } }));
        Require(handles.Games[0].Values["Title"]!.ToString() == "through handle", "library handles re-resolve on writer pass");
        Console.WriteLine("OK  API double-run: distinct writer thread, replacement objects, disappeared children, recreation, late writer and logged exceptions");
    }
    static void Children()
    {
        var library = new Library();
        foreach (var spec in new[] { (typeof(ICustomField), "Name", "Difficulty", "Value", "Hard"), (typeof(IAlternateName), "Name", "Alias", "Region", "Europe"), (typeof(IAdditionalApplication), "Id", "app", "Name", "Manual viewer") })
        {
            var child = Bag.Create(spec.Item1, (spec.Item2, spec.Item3), ("GameId", "a"), (spec.Item4, "before")); library.Games[0].Children[spec.Item1] = [child];
            var args = new JsonObject { ["type"] = spec.Item1.Name, ["id"] = spec.Item3, ["parent"] = new JsonObject { ["type"] = "IGame", ["id"] = "a" }, ["values"] = new JsonObject { [spec.Item4] = spec.Item5 } };
            library.Tools["lb_set"].Handler(args); args.Remove("values");
            Require(library.Tools["lb_get"].Handler(args)[spec.Item4]!.ToString() == spec.Item5, "child uses own/natural key: " + spec.Item1.Name);
        }
        var emulator = Bag.Create<IEmulator>(("Id", "em"), ("Title", "Emulator")); library.Emulators.Add(emulator);
        var ep = Bag.Create<IEmulatorPlatform>(("EmulatorId", "em"), ("Platform", "NES"), ("CommandLine", "old")); emulator.Children[typeof(IEmulatorPlatform)] = [ep];
        library.Call("lb_set", """{"type":"IEmulatorPlatform","id":"NES","parent":{"type":"IEmulator","id":"em"},"values":{"CommandLine":"new"}}""");
        Require(library.Call("lb_get", """{"type":"IEmulatorPlatform","id":"NES","parent":{"type":"IEmulator","id":"em"}}""")["CommandLine"]!.ToString() == "new", "emulator platform uses Platform, not EmulatorId");
        library.Platforms.Add(Bag.Create<IPlatform>(("Name", "NES"))); library.Categories.Add(Bag.Create<IPlatformCategory>(("Name", "Consoles"))); library.Controllers.Add(Bag.Create<IGameController>(("Id", "pad"), ("Name", "Controller")));
        foreach (var (type, id, field) in new[] { ("IPlatform", "NES", "Name"), ("IPlatformCategory", "Consoles", "Name"), ("IEmulator", "em", "Id"), ("IGameController", "pad", "Id") })
            Require(library.Call("lb_get", JsonSerializer.Serialize(new { type, id }))[field]!.ToString() == id, "root key: " + type);
        Reject(() => library.Call("lb_get", """{"type":"ICustomField","id":"Difficulty"}"""), "parent");
        var old = library.Background;
        Reject(() => library.Call("lb_set", """{"type":"IGame","id":"a","values":{"Ttile":"oops"}}"""), "Title");
        Reject(() => library.Call("lb_list", """{"root":"games","where":{"Ttile":"oops"}}"""), "Title");
        Require(library.Background == old, "invalid fields are rejected before saving");
        var calls = 0; library.Games[0].Children[typeof(IAdditionalApplication)][0].Custom = (_, _) => { calls++; return "command"; };
        Require(library.Call("lb_call", """{"type":"IAdditionalApplication","id":"app","parent":{"type":"IGame","id":"a"},"method":"GetEffectiveCommandLine"}""").ToString() == "command" && calls == 1, "lb_call resolves a child through parent");
        var added = library.Call("lb_create", """{"type":"additionalApplication","parent":{"type":"IGame","id":"a"},"values":{"Name":"New application"}}""");
        Require(added["created"]!.GetValue<int>() == 1 && library.Games[0].Children[typeof(IAdditionalApplication)].Count == 2, "child factory with generated own Id does not demand Id in values");
        library.Call("lb_create", """{"type":"playlist","values":{"Name":"Filters"}}""");
        library.Call("lb_create", """{"type":"playlistFilter","parent":{"type":"IPlaylist","id":"p1"},"items":[{"values":{"FieldKey":"Title","ComparisonTypeKey":"Contains","Value":"Star Wars"}},{"values":{"FieldKey":"Title","ComparisonTypeKey":"Contains","Value":"Star Trek"}}]}""");
        Require(library.Playlists[0].Children[typeof(IPlaylistFilter)].Count == 2, "composite filter key keeps two comparisons on same field distinct");
        var filters = library.Call("lb_call", """{"type":"IPlaylist","id":"p1","method":"GetAllPlaylistFilters"}""");
        library.Call("lb_set", JsonSerializer.Serialize(new { type = "IPlaylistFilter", id = filters[0]!["$handle"]!.ToString(), values = new { Value = "Wars" } }));
        Require(library.Playlists[0].Children[typeof(IPlaylistFilter)][0].Values["Value"]!.ToString() == "Wars" && library.Logs.Count == 0, "composite-key rename resolves on second pass through returned child handle");
        var parentRow = Bag.Create<IParent>(("PlatformName", "NES")); library.Parents.Add(parentRow);
        var parents = library.Call("lb_call", """{"method":"GetAllParents"}""");
        library.BeforeSecond = () => library.Parents[0] = Bag.Create<IParent>(("PlatformName", "NES"));
        library.Call("lb_set", JsonSerializer.Serialize(new { type = "IParent", id = parents[0]!["$handle"]!.ToString(), values = new { ParentPlatformCategoryName = "Consoles" } }));
        Require(library.Parents[0].Values["ParentPlatformCategoryName"]!.ToString() == "Consoles", "existing Parent handle resolves its six-part identity across reload");
        Console.WriteLine("OK  API keys: five own-ID types, Platform/Category Name, four parent/natural keys, composite filters/Parents, child factories/calls, nearest-name errors");
    }
    static void Calls()
    {
        var library = new Library();
        library.Call("lb_call", """{"type":"ILaunchBoxMainViewModel","method":"PlayGame","args":["a",null,null,null]}""");
        Require(library.Plays == 1 && library.Dispatches == 1 && library.Saves == 0 && library.Background == 0, "PlayGame dispatches once with zero save wrappers");
        library.Call("lb_call", """{"type":"ILaunchBoxMainViewModel","method":"Minimize"}""");
        library.Call("lb_call", """{"type":"ILaunchBoxMainViewModel","method":"Close"}""");
        library.Call("lb_call", """{"method":"Save","args":[false]}""");
        library.Call("lb_call", """{"method":"ForceReload"}""");
        Require(library.Saves == 1 && library.Background == 0 && library.Refreshes == 0 && library.Reloads == 1, "actions/Save/ForceReload have no wrapper or redundant refresh");
        var called = library.Call("lb_call", """{"type":"IGame","id":"a","method":"GetAllCustomFields"}""");
        Require(called.AsArray().Count == 0 && library.Saves == 1, "read method has no save");
        var attempts = 0; library.Games[0].Custom = (method, _) => { attempts++; throw new InvalidOperationException("not a thread affinity exception"); };
        Reject(() => library.Call("lb_call", """{"type":"IGame","id":"a","method":"GetManualPath"}"""), "not a thread");
        Require(attempts == 1 && library.Dispatches == 3, "ordinary exceptions are never retried");
        var methods = library.Call("lb_catalog", """{"type":"IDataManager"}""")["methods"]!.AsArray();
        Require(methods.All(m => m!["mutates"]!.GetValue<bool>() == !new[] { "Get", "Is", "Has", "Find", "Can" }.Any(p => m["name"]!.ToString().StartsWith(p))), "name-based mutates classification is exhaustive");
        Console.WriteLine("OK  API calls: PlayGame once on dispatcher, actions and explicit Save/Reload unwrapped, name classification, no retry after ordinary failure");
    }
    static void Serialization()
    {
        var library = new Library(); library.Games[0].Values["ReleaseDate"] = new DateTime(1999, 5, 19); library.Games[0].Values["Notes"] = new string('x', 30000); library.Games[0].ThrowProperty = "Developer";
        var result = library.Call("lb_get", """{"type":"IGame","id":"a","fields":["Title","Developer","ReleaseDate","Notes","LaunchBoxDbId"]}""");
        Require(result["Title"]!.ToString() == "Star Wars" && result["Developer"]!.ToString().StartsWith("<error:") && result["ReleaseDate"]!.ToString().Contains("1999") && result["LaunchBoxDbId"] == null && Encoding.UTF8.GetByteCount(result["Notes"]!.ToString()) == 20000, "per-field errors, nullable/date serialization, 20k string cap");
        Require(library.Call("lb_list", """{"root":"games","where":{"Developer":{"contains":"value"}}}""")["filterErrors"]!.GetValue<int>() == 1, "filter getter failures are counted without failing list");
        var serialize = typeof(Api).GetMethod("Serialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
        JsonNode? Encode(object value) => (JsonNode?)serialize.Invoke(library.Api, [value, value.GetType(), 0, null]);
        Require(Encode(new[] { 1, 2, 3 })!.AsArray().Count == 3 && Encode(new[] { true, false })![0]!.GetValue<bool>(), "primitive arrays serialize as values");
        Require(Encode(new[] { new[] { 1, 2 } })![0]!.ToString() == "<Int32[]>", "nested collections stop at one level");
        Require(Encode(Guid.Empty)!.ToString() == Guid.Empty.ToString() && Encode(TimeSpan.FromMinutes(2))!.ToString() == "00:02:00" && Encode(DayOfWeek.Monday)!.ToString() == "Monday" && Encode(new object())!.ToString() == "<Object>", "Guid/TimeSpan/enum and unknown type stub");
        var convert = typeof(Api).GetMethod("ConvertValue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Require(Equals(convert.Invoke(library.Api, [JsonValue.Create("Tuesday"), typeof(DayOfWeek)]), DayOfWeek.Tuesday) && Equals(convert.Invoke(library.Api, [JsonValue.Create("00:01:00"), typeof(TimeSpan)]), TimeSpan.FromMinutes(1)) && Equals(convert.Invoke(library.Api, [JsonValue.Create(Guid.Empty.ToString()), typeof(Guid)]), Guid.Empty), "enum/TimeSpan/Guid conversion");
        library.Call("lb_set", """{"type":"IGame","id":"a","values":{"ReleaseDate":"2001-01-02T00:00:00","LaunchBoxDbId":42}}""");
        Require((DateTime)library.Games[0].Values["ReleaseDate"]! == new DateTime(2001, 1, 2) && (int)library.Games[0].Values["LaunchBoxDbId"]! == 42, "nullable/date property conversion through set");
        Console.WriteLine("OK  API serialization: field isolation, 20k strings, primitive arrays, nullable/date/Guid/TimeSpan/enum conversion, unknown stubs");
    }
    static void Services()
    {
        var library = new Library(); var store = new MemoryStore();
        var api = new Api(library.Data, Docs, new Dictionary<Type, Func<object?>> { [typeof(IPluginDataStore)] = () => store });
        var tools = api.Tools().ToDictionary(t => t.Name);
        tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"SetValueAsync","args":[{"Key":"test","Value":[1,2,3]}]}"""));
        Require(store.Writes == 1 && store.Value!.SequenceEqual(new byte[] { 1, 2, 3 }) && library.Saves == 1 && library.Background == 0, "service request constructor, byte array conversion, auto cancellation and exactly-once async write");
        var result = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"GetValueAsync","args":[{"Key":"test"}]}"""));
        Require(result.AsArray().Count == 3 && result[2]!.GetValue<byte>() == 3 && library.Saves == 1, "async generic result returns primitive array without save");
        Require(tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"RemoveValueAsync","args":[{"Key":"test"}]}"""))["refused"]!.GetValue<bool>() && store.Removes == 0 && store.Value != null && library.Saves == 1, "service removal refused without save");
        store.Reads = 0;
        var tokenGet = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"GetValueAsync","args":[{"Key":"mcp-token"}]}"""));
        var tokenSet = tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"SetValueAsync","args":[{"Key":"mcp-token","Value":[9]}]}"""));
        var tokenUntyped = tools["lb_call"].Handler(JsonNode.Parse("""{"method":"GetValueAsync","args":[{"Key":"\u006dcp-token"}]}"""));
        foreach (var variant in new[] { "MCP-Token", " mcp-token", "mcp-token	", " Mcp-TOKEN " })
            Require(tools["lb_call"].Handler(JsonNode.Parse($$$"""{"type":"IPluginDataStore","method":"GetValueAsync","args":[{"Key":{{{JsonValue.Create(variant).ToJsonString()}}}}]}"""))["refused"]?.GetValue<bool>() == true, "token key variant refused: " + variant);
        Require(tokenGet["refused"]?.GetValue<bool>() == true && tokenSet["refused"]?.GetValue<bool>() == true && tokenUntyped["refused"]?.GetValue<bool>() == true && store.Reads == 0 && store.Writes == 1 && library.Saves == 1 && store.Value!.SequenceEqual(new byte[] { 1, 2, 3 }), "token key refused for get/set, typed or not, escaped or not, case/whitespace variants, without touching the store");
        Require(tools["lb_call"].Handler(JsonNode.Parse("""{"type":"IPluginDataStore","method":"GetValueAsync","args":[{"Key":"test"}]}""")).AsArray().Count == 3 && store.Reads == 1, "other keys still readable after the token refusal");
        Require(typeof(ApiServices).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).SequenceEqual(ApiServices.Types) && ApiServices.Types.Length == 10, "ten allowed injection parameters match exposed service registry");
        Console.WriteLine("OK  API services: ten injection types, real request constructors, byte arrays, auto cancellation, async result/write with one save, remove refused, token key and its case/whitespace variants refused");
    }
    static void Limits()
    {
        var library = new Library(); library.Games.Clear();
        for (var i = 0; i < 2100; i++) library.Games.Add(Bag.Create<IGame>(("Id", i.ToString()), ("Title", new string('x', 200)), ("Notes", new string('n', 30000))));
        var result = library.Call("lb_list", """{"root":"games","fields":["Id","Title","Notes"],"limit":999999}""");
        Require(result["total"]!.GetValue<int>() == 2100 && result["truncated"]!.GetValue<bool>() && result["rows"]!.AsArray().Count is > 0 and < 10 && result["nextOffset"]!.GetValue<int>() == result["rows"]!.AsArray().Count && Encoding.UTF8.GetByteCount(result.ToJsonString()) < 80000, "80k list cap stops at boundary and returns usable pagination");
        var next = library.Call("lb_list", $$$"""{"root":"games","fields":["Id"],"offset":{{{result["nextOffset"]}}},"limit":1}""");
        Require(next["rows"]![0]!["Id"]!.ToString() == result["nextOffset"]!.ToString(), "next page loses no row");
        Require(library.Call("lb_list", """{"root":"games","fields":["Id"],"limit":99999}""")["rows"]!.AsArray().Count == 2000 && library.Call("lb_list", """{"root":"games","limit":0}""")["rows"]!.AsArray().Count == 1, "limit clamps to 1-2000");
        result = library.Call("lb_call", """{"method":"GetAllGames"}"""); Require(Encoding.UTF8.GetByteCount(result.ToJsonString()) < 80000, "call collection output capped");
        Require(library.Call("lb_list", """{"root":"games","where":{"Id":{"eq":"2"}}}""")["total"]!.GetValue<int>() == 1, "eq filter");
        Console.WriteLine("OK  API limits: 2100 rows, 80k cap, uninterrupted nextOffset, 1-2000 clamp, bounded method output, cached field metadata");
    }
    static void DispatcherCheck()
    {
        Dispatcher? dispatcher = null; using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(ready.Wait(TimeSpan.FromSeconds(3)), "dispatcher ready");
        try
        {
            var library = new Library(); var effects = 0; var attempts = 0;
            library.Games[0].Custom = (method, _) => { attempts++; dispatcher!.VerifyAccess(); effects++; return "manual.pdf"; };
            var api = new Api(library.Data, Docs, dispatch: action => Api.DispatchOn(dispatcher!, action, TimeSpan.FromSeconds(30)));
            var result = api.Tools().Single(t => t.Name == "lb_call").Handler(JsonNode.Parse("""{"type":"IGame","id":"a","method":"GetManualPath"}"""));
            Require(result.ToString() == "manual.pdf" && attempts == 2 && effects == 1, "only real VerifyAccess failure retries before effect");
            using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
            var operation = dispatcher!.InvokeAsync(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }); Require(entered.Wait(TimeSpan.FromSeconds(2)), "dispatcher intentionally occupied");
            try
            {
                var slow = new Api(library.Data, Docs, new Dictionary<Type, Func<object?>> { [typeof(ILaunchBoxMainViewModel)] = () => library.View }, action => Api.DispatchOn(dispatcher, action, TimeSpan.FromMilliseconds(30)));
                result = slow.Tools().Single(t => t.Name == "lb_call").Handler(JsonNode.Parse("""{"type":"ILaunchBoxMainViewModel","method":"Close"}"""));
                Require(result["status"]!.ToString() == "still running", "dispatcher timeout returns still running");
            }
            finally { release.Set(); }
            operation.Task.Wait(TimeSpan.FromSeconds(3));
        }
        finally { dispatcher!.BeginInvokeShutdown(DispatcherPriority.Normal); Require(thread.Join(TimeSpan.FromSeconds(3)), "dispatcher exits"); }
        Console.WriteLine("OK  API dispatcher: actual VerifyAccess-only retry, one effect, timed still-running response, no windows");
    }
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("API: " + message); }
    static void Reject(Action action, string text)
    {
        try { action(); } catch (Exception error) { while (error is TargetInvocationException { InnerException: { } inner }) error = inner; Require(error.Message.Contains(text, StringComparison.OrdinalIgnoreCase), "expected error " + text + ": " + error.Message); return; }
        throw new InvalidOperationException("API: expected rejection " + text);
    }
    sealed class Library
    {
        public readonly List<Bag> Games = [Bag.Create<IGame>(("Id", "a"), ("Title", "Star Wars"), ("Platform", "PC")), Bag.Create<IGame>(("Id", "b"), ("Title", "Star Trek"), ("Platform", "NES")), Bag.Create<IGame>(("Id", "c"), ("Title", "Other"), ("Platform", "PC"))];
        public readonly List<Bag> Playlists = [], Emulators = [], Platforms = [], Categories = [], Controllers = [], Parents = [];
        public readonly List<string> Logs = [];
        public readonly IDataManager Data;
        public readonly ILaunchBoxMainViewModel View;
        public readonly Api Api;
        public readonly Dictionary<string, Tool> Tools;
        public int Created, Background, Saves, Runs, Refreshes, Dispatches, Plays, Removes, Reloads;
        public bool FailRead, DeferSecond;
        public Func<MethodInfo, object?[], object?>? Custom;
        public Action? BeforeSecond;
        Thread? _worker;
        Exception? _error;
        bool _dispatching;
        public Library(string? docs = null)
        {
            Data = Proxy<IDataManager>((method, args) =>
            {
                var lists = new Dictionary<Type, List<Bag>> { [typeof(IGame)] = Games, [typeof(IPlaylist)] = Playlists, [typeof(IEmulator)] = Emulators, [typeof(IPlatform)] = Platforms, [typeof(IPlatformCategory)] = Categories, [typeof(IGameController)] = Controllers, [typeof(IParent)] = Parents };
                if (FailRead) throw new InvalidOperationException("second-pass failure");
                if (method.Name == nameof(IDataManager.BackgroundReloadSave)) { Background++; Runs++; var action = (Action)args[0]!; action(); var caller = Environment.CurrentManagedThreadId; _worker = new Thread(() => { try { Require(caller != Environment.CurrentManagedThreadId, "writer uses another thread"); BeforeSecond?.Invoke(); Runs++; action(); } catch (Exception e) { _error = e; } }) { IsBackground = true }; if (!DeferSecond) FinishSecond(); return null; }
                if (method.Name == nameof(IDataManager.Save)) { Require(args[0] is false, "Save(false)"); Saves++; return null; }
                if (method.Name == nameof(IDataManager.ForceReload)) { Reloads++; return null; }
                if (method.Name == nameof(IDataManager.TryRemovePlaylist)) { Removes++; return Playlists.RemoveAll(p => ReferenceEquals(p.Value, args[0])) > 0; }
                if (method.Name == nameof(IDataManager.AddNewPlaylist)) { Created++; var value = Bag.Create<IPlaylist>(("PlaylistId", "p" + Created), ("Name", args[0])); Playlists.Add(value); return value.Value; }
                if (method.Name.StartsWith("Get") && lists.TryGetValue(method.ReturnType, out var direct)) return direct.SingleOrDefault(p => p.Values.Values.Contains(args[0]))?.Value;
                var element = method.ReturnType.IsArray ? method.ReturnType.GetElementType() : method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : null;
                if (element != null && lists.TryGetValue(element, out var list)) return ArrayOf(element, list);
                return Custom != null ? Custom(method, args) : throw new NotSupportedException(method.Name);
            });
            View = Proxy<ILaunchBoxMainViewModel>((method, args) =>
            {
                if (method.Name == "RefreshData") { Require(_dispatching, "refresh dispatch"); Refreshes++; }
                if (method.Name == "PlayGame") { Require(_dispatching && ReferenceEquals(args[0], Games[0].Value), "PlayGame dispatch and ID conversion"); Plays++; }
                return null;
            });
            Api = new(Data, docs ?? Docs, new Dictionary<Type, Func<object?>> { [typeof(ILaunchBoxMainViewModel)] = () => View }, action => { Dispatches++; _dispatching = true; try { return action(); } finally { _dispatching = false; } }, Logs.Add);
            Tools = Api.Tools().ToDictionary(t => t.Name);
        }
        public void FinishSecond() { _worker!.Start(); Require(_worker.Join(TimeSpan.FromSeconds(5)), "writer completes"); if (_error != null) throw new InvalidOperationException("writer escaped", _error); }
        public JsonNode Call(string tool, string args) => Tools[tool].Handler(JsonNode.Parse(args));
    }
    sealed class Bag
    {
        public object Value = null!;
        public readonly Dictionary<string, object?> Values = new();
        public readonly Dictionary<Type, List<Bag>> Children = new();
        public string? ThrowProperty;
        public bool FailChildren;
        public Func<MethodInfo, object?[], object?>? Custom;
        public static Bag Create<T>(params (string key, object? value)[] fields) where T : class => Create(typeof(T), fields);
        public static Bag Create(Type type, params (string key, object? value)[] fields)
        {
            var bag = new Bag(); foreach (var (key, value) in fields) bag.Values[key] = value;
            bag.Value = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)bag.Value).Call = bag.Call; return bag;
        }
        object? Call(MethodInfo method, object?[] args)
        {
            if (method.Name.StartsWith("get_")) { var key = method.Name[4..]; if (key == ThrowProperty) throw new InvalidOperationException("getter failed"); return Values.TryGetValue(key, out var value) ? value : Default(method.ReturnType); }
            if (method.Name.StartsWith("set_")) { Values[method.Name[4..]] = args[0]; return null; }
            if (method.Name.StartsWith("GetAll") && method.ReturnType.IsArray && FailChildren) throw new InvalidOperationException("count unavailable");
            if (method.Name.StartsWith("GetAll") && method.ReturnType.IsArray) return ArrayOf(method.ReturnType.GetElementType()!, Children.GetValueOrDefault(method.ReturnType.GetElementType()!) ?? []);
            if (method.Name.StartsWith("AddNew"))
            {
                var type = method.ReturnType; var child = Create(type); if (type.GetProperty("Id") != null) child.Values["Id"] = Guid.NewGuid().ToString(); if (!Children.TryGetValue(type, out var list)) Children[type] = list = []; list.Add(child);
                if (type == typeof(IPlaylistGame)) child.Values["PlaylistId"] = Values.GetValueOrDefault("PlaylistId"); return child.Value;
            }
            return Custom != null ? Custom(method, args) : throw new NotSupportedException(method.Name);
        }
        static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : type == typeof(string) ? "" : null;
    }
    static Array ArrayOf(Type type, List<Bag> items) { var array = Array.CreateInstance(type, items.Count); for (var i = 0; i < items.Count; i++) array.SetValue(items[i].Value, i); return array; }
    sealed class MemoryStore : IPluginDataStore
    {
        public int Writes, Removes, Reads;
        public byte[]? Value;
        public Task<byte[]?> GetValueAsync(PluginDataStoreGetRequest request, CancellationToken token) { Require(request.Key == "test" && !token.CanBeCanceled, "store read request"); Reads++; return Task.FromResult(Value); }
        public async Task SetValueAsync(PluginDataStoreSetRequest request, CancellationToken token) { await Task.Yield(); Require(request.Key == "test", "store write request"); Writes++; Value = request.Value; }
        public async Task RemoveValueAsync(PluginDataStoreRemoveRequest request, CancellationToken token) { await Task.Yield(); Require(request.Key == "test", "store remove request"); Removes++; Value = null; }
    }
    static T Proxy<T>(Func<MethodInfo, object?[], object?> action) where T : class { var proxy = DispatchProxy.Create<T, ApiProxy>(); ((ApiProxy)(object)proxy).Call = action; return proxy; }
}

public class ApiProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Call = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
}
