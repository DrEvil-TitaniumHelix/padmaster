using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using PadMaster;

static class BetaCheck
{
    public static async Task Run()
    {
        FreshDefault(); Job(); await Logs(); await ThrowingLog(); await HiddenQueue();
        var lines = new List<string>(); var host = new HostGate();
        Require(!host.AllowsAssistant(true, lines.Add), "Big Box refuses startup");
        for (var i = 0; i < 8; i++) Require(!host.AllowsAssistant(false, lines.Add), "Big Box stays disabled across later watcher, poll, menu and startup decisions");
        Require(lines.SequenceEqual(new[] { "Big Box: assistant not available" }) && new HostGate().AllowsAssistant(false, lines.Add), "one Big Box log and LaunchBox allowed");
        Console.WriteLine("OK  beta host: Big Box decision disables startup and subsequent entry points; one log line");
    }
    public static void FreshDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "PadMaster-default-" + Guid.NewGuid().ToString("N"));
        var decoded = Settings.Decode(null); var missing = Settings.Read(PackageIdentity.StorePath(root), _ => { }); var activation = ActivationState.Read(root, _ => { });
        Require(!decoded.Enabled && !missing.Enabled && !activation.Active && activation.Readable, "missing store defaults disabled with activation off");
        var old = Settings.Decode("{\"enabled\":true}"u8.ToArray());
        Require(old.ConfirmBulk && old.BulkThreshold == 20 && decoded.ConfirmBulk && decoded.BulkThreshold == 20, "old and missing store bulk defaults on and 20");
        Require(!Settings.TryApply(old, new Dictionary<string, string?> { ["bulkThreshold"] = "0" }, false, out _, out _), "threshold below 1 refused");
        Console.WriteLine("OK  fresh default: real missing-store read/decode, Enabled=false, activation off, bulk defaults on/20");
    }
    static void Job()
    {
        using var process = new Process { StartInfo = Session.Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]) };
        var job = ChildJobs.Create();
        try
        {
            Require(process.Start(), "dummy child starts"); ChildJobs.Assign(job, process);
            var watch = Stopwatch.StartNew(); job.Dispose();
            Require(process.WaitForExit(2000), "job close kills only injected child within 2 seconds");
            Console.WriteLine($"OK  beta job: separate KILL_ON_JOB_CLOSE handle; injected child exited in {watch.ElapsedMilliseconds} ms");
        }
        finally { job.Dispose(); try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } } catch { } }
    }
    static async Task Logs()
    {
        const string marker = "BETA-PRIVATE-TEXT-MARKER";
        var logs = new ConcurrentQueue<string>(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new Session("injected", Path.GetTempPath(), "unused", logs.Enqueue)
        {
            Delay = (_, token) => Task.Delay(TimeSpan.FromMinutes(1), token),
            Launch = process =>
            {
                process.StartInfo = Session.Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine([Console]::ReadLine()); [Console]::WriteLine('invalid " + marker + "'); [Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"" + marker + "\"}'); Start-Sleep -Seconds 30"]);
                process.Start();
            }
        };
        session.Done += _ => done.TrySetResult(); session.Send(marker);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        session.AcceptLine("[\"" + marker + "\"]");
        session.AcceptLine("{\"type\":\"assistant\",\"error\":\"" + marker + "\"}");
        session.AcceptLine("{\"type\":\"result\",\"is_error\":true,\"result\":\"" + marker + "\"}");
        session.AcceptLine("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"lb_set\",\"input\":{\"type\":\"IGame\",\"values\":{\"Title\":\"" + marker + "\"}}}]}}");
        session.AcceptError(marker);
        Require(logs.All(s => !s.Contains(marker, StringComparison.Ordinal)), "log marker absent after send, reply, malformed JSON, errors, stderr and tool args");
        Require(logs.Any(s => s.Contains("child: <redacted,")) && logs.Any(s => s.Contains("invalid JSON")), "actual stderr drain, redaction and parse error exercised");
        Require(!SafeDiagnostics.Error(new IOException(marker)).Contains(marker), "exception text filtered");
        var root = Path.Combine(Path.GetTempPath(), "PadMaster-log-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "padmaster.log"); File.WriteAllText(path, new string('x', RotatingLog.Limit));
        RotatingLog.Write(path, "first"); Require(new FileInfo(path + ".1").Length == RotatingLog.Limit && new FileInfo(path).Length < 100, "one-megabyte rotation");
        File.WriteAllText(path, new string('y', RotatingLog.Limit)); RotatingLog.Write(path, "second");
        Require(File.ReadAllText(path + ".1")[0] == 'y' && Directory.GetFiles(root).Length == 2, "exactly one rotated generation");
        using (var locked = new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.None)) { File.WriteAllText(path, new string('z', RotatingLog.Limit)); RotatingLog.Write(path, "cannot rotate"); }
        Require(new FileInfo(path).Length == RotatingLog.Limit, "failed rotation does not throw or grow the log");
        Console.WriteLine("OK  beta logs: marker absent through real send/stderr/parser/tool paths; 1 MB rotation, one generation, failed rotate contained");
    }
    static async Task HiddenQueue()
    {
        var events = new ConcurrentQueue<string>(); var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var turns = 0;
        BulkPrompt? hold = null; var effects = 0;
        var guard = new BulkChanges(() => new Settings(), prompt => { hold = prompt; return true; }, (_, _) => { });
        using var session = new Session("injected", Path.GetTempPath(), "unused", _ => { })
        {
            Delay = (_, token) => Task.Delay(TimeSpan.FromMinutes(1), token),
            Launch = process =>
            {
                process.StartInfo = Session.Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "$first=[Console]::ReadLine(); Start-Sleep -Milliseconds 100; [Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"first\"}'); $second=[Console]::ReadLine(); if ($second -notlike '*hidden approval*') { exit 2 }; [Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"second\"}'); Start-Sleep -Seconds 30"]);
                process.Start();
            }
        };
        session.HiddenTurn += () => events.Enqueue("hidden");
        session.Interrupted += () => guard.Invalidate("The session stopped. Nothing changed.");
        session.Done += success => { events.Enqueue(success ? "done" : "failed"); if (Interlocked.Increment(ref turns) == 2) done.TrySetResult(); };
        session.Send("first user turn"); session.SendHidden("hidden approval");
        guard.Run("remove", "IGame", 21, () => { effects++; return new JsonObject(); });
        Require(!events.Contains("hidden") && session.Busy, "hidden turn queued while user turn runs");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require(events.SequenceEqual(new[] { "done", "hidden", "done" }) && session.Starts == 1, "hidden turn waits for result and uses the same child once");
        hold!.Answer(true); Require(!guard.Waiting && effects == 0, "actual child interruption invalidates held action");
        Console.WriteLine("OK  beta hidden turn: queued behind active turn, sent once through same child after result");
    }
    static async Task ThrowingLog()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var throws = 0; var reply = ""; var pid = 0;
        using var session = new Session("injected", Path.GetTempPath(), "unused", _ => { Interlocked.Increment(ref throws); throw new IOException("injected log failure", unchecked((int)0x80070020)); })
        {
            Launch = process =>
            {
                process.StartInfo = Session.Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "$null = [Console]::ReadLine(); [Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"logged ok\"}'); Start-Sleep -Seconds 30"]);
                process.Start(); pid = process.Id;
            }
        };
        session.Reply += text => reply = text;
        session.Done += success => done.TrySetResult(success);
        session.Send("message whose logging always fails");
        Require(await done.Task.WaitAsync(TimeSpan.FromSeconds(20)), "a throwing log callback never fails the turn");
        Require(reply == "logged ok" && session.Starts == 1, "the reply still reaches the panel through a throwing logger");
        Require(Volatile.Read(ref throws) > 0, "the throwing logger was actually exercised");
        using (var child = Process.GetProcessById(pid)) Require(!child.HasExited, "a log failure never kills a healthy child");
        session.Shutdown(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"OK  beta logging: {throws} failing log calls through a full injected turn; turn completed, child untouched");
    }

    public static void Activity(Session session)
    {
        var lines = new List<string>(); void Capture(string value) => lines.Add(value); session.Activity += Capture;
        var cases = new[] { ("lb_catalog", "{}", "reading the LaunchBox API…"), ("lb_list", "{}", "searching your library…"), ("lb_get", "{\"type\":\"IGame\",\"id\":\"PRIVATE\"}", "reading a game…"), ("lb_set", "{\"type\":\"IPlaylist\"}", "changing a playlist…"), ("lb_create", "{\"type\":\"PRIVATE\"}", "creating library data…"), ("lb_call", "{\"method\":\"PlayGame\"}", "launching the game…"), ("lb_call", "{\"method\":\"PRIVATE\"}", "working on LaunchBox…"), ("show_image", "{}", "showing an image…"), ("status", "{}", "checking LaunchBox…") };
        session.BeginTurn();
        for (var i = 0; i < cases.Length; i++)
        {
            var (name, input, expected) = cases[i]; var before = lines.Count;
            string Block(string json) => new JsonObject { ["type"] = "tool_use", ["id"] = "tool-" + i, ["name"] = "mcp__launchbox__" + name, ["input"] = JsonNode.Parse(json) }.ToJsonString();
            session.AcceptLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"content_block\":" + Block("{}") + "}}");
            if (name is "lb_get" or "lb_set" or "lb_create" or "lb_call") Require(lines.Count == before, "argument-dependent activity waits for known input");
            var final = "{\"type\":\"assistant\",\"message\":{\"content\":[" + Block(input) + "]}}";
            session.AcceptLine(final); session.AcceptLine(final);
            Require(lines.Count == before + 1 && lines[^1] == expected, "mapping with final arguments and no doubles: " + name);
        }
        session.AcceptLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"id\":\"known\",\"name\":\"lb_get\",\"input\":{\"type\":\"IPlaylist\"}}}}");
        Require(lines[^1] == "reading a playlist…" && lines.All(s => !s.Contains("PRIVATE") && !s.Contains("IGame") && !s.Contains("PlayGame")), "start arguments mapped without raw arguments or API names");
        session.Activity -= Capture;
        Console.WriteLine("OK  beta parser: all lb activities, arguments at start/final, plain types/verbs, no raw arguments, no duplicate activity");
    }
    public static void Panel()
    {
        var panel = new AssistantPanel(false); var answers = 0;
        panel.SetSetup(new("claude-code", "unused", true)); panel.SetReady(true); panel.SetExpanded(false);
        panel.ShowBulk(new("remove games", 21, _ => answers++));
        panel.Width = 360; panel.Measure(new Size(360, 360)); panel.Arrange(new Rect(0, 0, 360, 360)); panel.UpdateLayout();
        Require(panel.Expanded, "hold expands collapsed panel");
        var controls = PanelPlacement.Walk((DependencyObject)panel.Content).OfType<FrameworkElement>().ToArray();
        var input = controls.OfType<TextBox>().Single(box => box.AcceptsReturn);
        var location = input.TranslatePoint(new Point(), panel);
        Require(input.IsEnabled && location.Y >= 40 && location.Y + input.ActualHeight <= 360, "hold leaves composer on screen at 360 pixels");
        panel.SubmitText("hello"); Require(answers == 0, "input send never approves");
        var transcript = panel.Transcript; panel.BeginHiddenTurn(); Require(panel.Transcript == transcript, "hidden turn adds no user bubble");
        var yes = controls.OfType<Button>().Single(button => Equals(button.Content, "Yes, do it")); yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(answers == 1 && !yes.IsEnabled, "button answers once and disables");
        panel.LoadConfiguration(new Settings { Assistant = "claude-code" }); panel.OpenConfiguration();
        var all = PanelPlacement.Walk((DependencyObject)panel.Content).OfType<FrameworkElement>().ToArray();
        Require(all.OfType<CheckBox>().Any(box => Equals(box.Content, "Confirm bulk changes")) && all.OfType<TextBox>().Any(box => box.Text == "20"), "gear contains bulk defaults");
        panel.Measure(new Size(360, 360)); panel.Arrange(new Rect(0, 0, 360, 360)); panel.UpdateLayout();
        var save = all.OfType<Button>().Single(button => Equals(button.Content, "Save"));
        Require(save.TranslatePoint(new Point(), panel).Y + save.ActualHeight <= 360, "gear Save fits in 360 pixels with bulk fields");
        Console.WriteLine("OK  beta panel: expanded hold, plain TextBlock, composer within 360 px, input cannot answer, buttons and gear defaults; no window shown");
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("beta: " + message); }
}
