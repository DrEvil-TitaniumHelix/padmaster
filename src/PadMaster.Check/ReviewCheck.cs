using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PadMaster;

static class ReviewCheck
{
    public static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void AuthAndCommands()
    {
        const string subscription = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"max"}""";
        Require(AuthState.Parse(subscription).SignedIn, "subscription accepted");
        foreach (var json in new[] { """{"loggedIn":false}""", """{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty"}""", """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"thirdParty"}""", """{"loggedIn":true,"authMethod":"console","apiProvider":"firstParty"}""", """{"loggedIn":true}""" })
        {
            var auth = AuthState.Parse(json);
            Require(auth.Known && !auth.SignedIn && auth.Reason.Length > 0 && auth.ShouldStop(false) && !auth.ShouldStop(true), "non-subscription setup reason; stop only between turns");
        }
        foreach (var json in new[] { "broken", "{}", "null" }) Require(!AuthState.Parse(json).Known && !AuthState.Parse(json).ShouldStop(false), "unknown auth preserves session");
        var variables = new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE", "ANTHROPIC_FUTURE_OPTION", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_USE_FUTURE", "CLAUDE_CODE_OAUTH_TOKEN" };
        var previous = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in variables) Environment.SetEnvironmentVariable(name, "check-placeholder");
            foreach (var args in new[] { Session.Arguments("unused", null), Session.AuthArguments })
            {
                var command = Session.Command("fake.exe", "unused", args);
                Require(variables.Where(name => name != "CLAUDE_CODE_OAUTH_TOKEN").All(name => !command.Environment.ContainsKey(name)) && command.Environment["CLAUDE_CODE_OAUTH_TOKEN"] == "check-placeholder", "G4 chat/auth strips all provider variables and preserves OAuth token");
                Require(command.ArgumentList.Contains("--setting-sources") && command.ArgumentList.Contains("project"), "chat/auth use project settings");
            }
        }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        var flags = Session.Arguments("unused", "resume-id").ToArray();
        string Value(string name) => flags[Array.IndexOf(flags, name) + 1];
        Require(Value("--tools") == "Read,Grep,Glob" && Value("--allowedTools") == "Read,Grep,Glob,mcp__launchbox__*" && Value("--setting-sources") == "project", "read tools and project settings only");
        var denied = flags.Skip(Array.IndexOf(flags, "--disallowedTools") + 1).ToArray();
        var expected = new[] { ".claude", ".ssh", ".aws", ".azure", ".gnupg" }.SelectMany(folder => new[] { "Read", "Grep", "Glob" }.Select(tool => tool + "(~/" + folder + "/**)"));
        Require(denied.SequenceEqual(expected), "G6 all fifteen credential-folder deny rules are separate arguments");
        var touched = 0;
        foreach (var path in new[] { @"\\server\share\image.png", "https://example/image.png", "file://server/share/image.png", @"relative\image.png", @"\\?\C:\image.png", @"C:\image.png:stream" })
        {
            try { LocalImage.Validate(path, _ => { touched++; return true; }); throw new InvalidOperationException("unsafe image path accepted"); }
            catch (ArgumentException) { }
        }
        Require(touched == 0, "UNC and nonlocal image rejection before filesystem callback");
        var local = Path.Combine(Path.GetTempPath(), "PadMaster-local-image-check.png");
        LocalImage.Validate(local, path => { touched++; return path == local; });
        Require(touched == 1, "valid local image reaches existence check");
        Require(FailureText.Listener(new System.Net.HttpListenerException(183), 47833) == "port 47833 in use - change it in Configure", "port conflict gives fix");
        Require(FailureText.Listener(new System.Net.HttpListenerException(6)).Contains("restart LaunchBox"), "listener failure gives fix");
        Require(FailureText.Configuration(new UnauthorizedAccessException()).Contains("not writable"), "folder failure gives fix");
        var blocked = Path.Combine(Path.GetTempPath(), "PadMaster-write-check-" + Guid.NewGuid().ToString("N")); File.WriteAllText(blocked, "file, not a directory");
        try { new Settings().Write(Path.Combine(blocked, "settings.dat")); throw new InvalidOperationException("blocked folder unexpectedly writable"); }
        catch (IOException e) { Require(FailureText.Configuration(e).Contains("Allow writing"), "write failure maps to actionable status"); }
        var config = Path.Combine(Path.GetTempPath(), "PadMaster-token-check-" + Guid.NewGuid().ToString("N") + ".json");
        var first = new Mcp(47801, "/mcp", []); var second = new Mcp(47801, "/mcp", []);
        Require(first.Token.Length == 64 && first.Token != second.Token, "fresh random per-start tokens");
        Session.WriteMcpConfig(config, 47801, first.Token); Session.WriteMcpConfig(config, 47801, second.Token);
        var written = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(config));
        Require(written?["mcpServers"]?["launchbox"]?["headers"]?["Authorization"]?.GetValue<string>() == "Bearer " + second.Token, "rewritten MCP config carries current bearer"); first.Stop(); second.Stop();
        Console.WriteLine("OK  F12: port/folder corrective messages and blocked-folder write");
        Console.WriteLine("OK  F1/F2/F4/F8: subscription states, credential-free commands, read tools, project settings, image path gate; auth orchestration checked separately");
    }

    public static void Activation()
    {
        var listed = $"<LaunchBox><DisabledPluginIds>bad, {PackageIdentity.Id.ToUpperInvariant()}, 00000000-0000-0000-0000-000000000001</DisabledPluginIds></LaunchBox>";
        var absent = "<LaunchBox><DisabledPluginIds>00000000-0000-0000-0000-000000000001</DisabledPluginIds></LaunchBox>";
        var empty = "<LaunchBox><DisabledPluginIds /></LaunchBox>";
        foreach (var settings in new[] { new Settings { Enabled = true }, new Settings { Enabled = false }, Settings.Decode(null) })
            foreach (var xml in new[] { listed, absent, empty, "<LaunchBox/>", "", "<broken", null })
                Require(ActivationState.Parse(xml, settings).Active == (settings.Enabled && (xml == absent || xml == empty || xml == "<LaunchBox/>")), "activation state table");
        Console.WriteLine("OK  activation XML state table; lifecycle/read sequences run in Fix2Check");
    }

    public static void Panel()
    {
        var panel = new AssistantPanel(false);
        panel.SetExpanded(false); var saves = 0; panel.ExpandedChanged = _ => saves++;
        panel.SetSetup(new("claude-code", "fake.exe", false, Checking: true));
        Require(!panel.SetupVisible && !panel.Expanded && panel.Transcript.Length == 0, "checking adds no card, expansion or chat");
        panel.SetSetup(new("claude-code", "fake.exe", true));
        Require(!panel.Expanded && saves == 0, "signed-in startup keeps saved collapse");
        panel.ToggleExpanded(); Require(saves == 1 && panel.Expanded, "user toggle invokes persistence positive control"); panel.SetReady(true);
        var sends = 0; panel.SendRequested = _ => sends++;
        var before = panel.Transcript; panel.SubmitText("  /cost");
        Require(sends == 0 && panel.Transcript == before, "slash commands never reach child or chat bubbles");
        panel.SubmitText("hello"); Require(sends == 1, "ordinary chat still sends"); panel.Finish(true);
        Fix2Check.Placement();
        for (var i = 0; i < 30; i++) panel.AddTurn("Long transcript line " + i + "\nwith a second line", i % 2 == 0);
        panel.Width = 360; panel.Height = 360; panel.Measure(new Size(360, 360)); panel.Arrange(new Rect(0, 0, 360, 360)); panel.UpdateLayout(); Pump();
        var scroll = PanelPlacement.Walk((DependencyObject)panel.Content).OfType<ScrollViewer>().First(x => x.Content is StackPanel stack && stack.Children.OfType<Border>().Any());
        scroll.ScrollToEnd(); Pump(); panel.UpdateLayout(); var offset = scroll.VerticalOffset;
        Require(offset > 0, "transcript has scroll extent");
        scroll.ScrollToTop(); Pump(); panel.UpdateLayout(); Pump();
        Require(Math.Abs(scroll.VerticalOffset - offset) < 1, "host ScrollToTop preserves transcript position");
        var floated = 0; var reports = 0;
        PanelPlacement.Recover(() => throw new InvalidOperationException("synthetic detach failure"), () => floated++, _ => reports++);
        Require(floated == 1 && reports == 1, "fallback floats after detach failure");
        panel.LoadConfiguration(new Settings { Assistant = "claude-code", Port = 47800 }); panel.OpenConfiguration();
        var port = PanelPlacement.Walk((DependencyObject)panel.Content).OfType<TextBox>().Single(box => box.Text == "47800"); port.Text = "47899";
        panel.LoadConfiguration(new Settings { Assistant = "claude-code", Port = 47801, Placement = "left", AutoOpen = false }); panel.OpenConfiguration();
        var choices = PanelPlacement.Walk((DependencyObject)panel.Content).OfType<ComboBox>().Single(box => box.Items.Count == 3);
        Require(port.Text == "47899" && choices.SelectedIndex == 1, "G9 dirty port survives external reload/reopen while clean placement updates");
        panel.ConfigurationAccepted(new Settings { Assistant = "claude-code", Port = 47802 }); Require(port.Text == "47802", "accepted save clears dirty state");
        var window = new AssistantWindow(); var closed = false; window.UserClosed += () => closed = true; window.Close();
        Require(closed && !window.IsVisible, "user-close signals hidden floating state"); window.Shutdown();
        Console.WriteLine("OK  F3/F5/F9/F11/F13: placeholders/focus, slash guard, collapsed checking, transcript ScrollToTop, fallback after detach, floating close");
    }

    static void Pump()
    {
        var frame = new DispatcherFrame(); _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false); Dispatcher.PushFrame(frame);
    }

    public static async Task Processes()
    {
        var attempts = 0; var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var missing = new Session(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".exe"), Path.GetTempPath(), "unused", _ => { })
        {
            Delay = (_, token) => Task.Delay(1, token), Launch = p => { Interlocked.Increment(ref attempts); p.Start(); }
        };
        missing.Done += success => done.TrySetResult(success);
        missing.Send("test"); Require(!await done.Task.WaitAsync(TimeSpan.FromSeconds(10)) && !missing.Busy, "spawn failure ends busy turn");
        await Task.Delay(100); Require(attempts == 1 && missing.Paused, "a failed start is never retried automatically and pauses the queue");
        done = new(TaskCreationOptions.RunContinuationsAsynchronously); missing.Send("retry");
        Require(!await done.Task.WaitAsync(TimeSpan.FromSeconds(10)) && attempts == 2, "a new message releases the paused queue for exactly one more attempt");
        missing.Dispose(); await missing.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var launches = new List<string[]>(); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var exits = new Session("injected.exe", Path.GetTempPath(), "unused", _ => { })
        {
            Delay = (_, token) => Task.Delay(1, token), Launch = process =>
            {
                lock (launches) { launches.Add(process.StartInfo.ArgumentList.ToArray()); if (launches.Count == 1) first.TrySetResult(); else if (launches.Count == 2) second.TrySetResult(); }
                process.StartInfo = Session.Command(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", Path.GetTempPath(), ["/d", "/c", "exit", "1"]); process.Start();
            }
        };
        exits.AcceptLine("""{"type":"result","subtype":"success","session_id":"fake-resume"}"""); exits.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100); lock (launches) Require(launches.Count == 1, "a silent idle exit is not restarted on its own");
        exits.Send("message after the idle exit");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10)); exits.Dispose(); await exits.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        lock (launches) Require(launches[0].Contains("--resume") && launches[1].Contains("--resume") && exits.LastSessionId == "fake-resume", "H5 silent nonzero idle exit preserves resume for the next message");
        var timeoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var reason = "";
        using var hanging = new Session("unused", "unused", "unused", _ => { }) { TurnTimeout = TimeSpan.FromMilliseconds(25) };
        hanging.Failure += text => reason = text; hanging.Done += ok => timeoutDone.TrySetResult(ok); hanging.BeginTurn();
        Require(!await timeoutDone.Task.WaitAsync(TimeSpan.FromSeconds(2)) && !hanging.Busy && reason.Contains("no answer from the assistant"), "turn timeout clears busy with plain line");
        var childStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childTimedOut = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timedChild = new Session("injected.exe", Path.GetTempPath(), "unused", _ => { })
        {
            TurnTimeout = TimeSpan.FromMilliseconds(250), Delay = (_, ct) => Task.Delay(TimeSpan.FromMinutes(1), ct),
            Launch = process =>
            {
                process.StartInfo = Session.Command(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
                process.Start(); childStarted.TrySetResult(process.Id);
            }
        };
        timedChild.Done += ok => childTimedOut.TrySetResult(ok);
        timedChild.Start(); var pid = await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var observation = Process.GetProcessById(pid); timedChild.Send("test timeout");
        Require(!await childTimedOut.Task.WaitAsync(TimeSpan.FromSeconds(10)), "hung fake child times out");
        await observation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(observation.HasExited, "turn timeout actually kills injected child"); timedChild.Dispose(); await timedChild.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var pendingStart = new Session("fake.exe", Path.GetTempPath(), "unused", _ => { }) { Launch = _ => { launched.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("injected delayed launch"); } };
        pendingStart.Start(); await launched.Task.WaitAsync(TimeSpan.FromSeconds(10)); var watch = Stopwatch.StartNew(); pendingStart.Dispose(); watch.Stop(); release.Set();
        Require(watch.ElapsedMilliseconds < 500, "Dispose does not wait for process start gate"); await pendingStart.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var parsedReplies = 0; var invalidReplies = 0;
        using var parallel = new Session("unused", "unused", "unused", _ => { });
        parallel.Reply += text => { Interlocked.Increment(ref parsedReplies); if (text != "reply") Interlocked.Increment(ref invalidReplies); };
        await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 200; i++) parallel.BeginTurn(); }), Task.Run(() => { for (var i = 0; i < 200; i++) parallel.AcceptLine("""{"type":"assistant","message":{"id":"same","content":[{"type":"text","text":"reply"}]}}"""); }));
        Require(parsedReplies == 200 && invalidReplies == 0 && parallel.ParsedMessages <= 1, "F15 all concurrent parser replies are consistent; collection exceptions propagate");
        Console.WriteLine("OK  F7/F14/F15: nonexistent CLI, busy completion, one attempt per message with a paused queue, silent injected exits preserve resume, turn timeout, concurrent parser/reset");
    }
}
