using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PadMaster;

static class Fix2Check
{
    static void Require(bool value, string message) => ReviewCheck.Require(value, message);
    static string Temp() { var root = Path.Combine(Path.GetTempPath(), "PadMaster-fix2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    public static async Task Reads()
    {
        var root = Temp(); var host = Path.Combine(root, "host.xml"); var store = Path.Combine(root, "settings.dat");
        var enabled = new Settings { Enabled = true, AutoOpen = false, Assistant = "claude-code" }; enabled.Write(store);
        var notices = new List<string>(); var log = new List<string>(); var counts = new int[8];
        var lifecycle = new AssistantLifecycle(_ => counts[0]++, () => counts[1]++, _ => counts[2]++, () => counts[3]++, () => counts[4]++, _ => counts[5]++, () => counts[6]++, () => counts[7]++);
        var offGate = new ActivationGate(lifecycle.Start, (_, _) => { }, lifecycle.Stop, _ => { });
        offGate.Update(new(new Settings(), false, true)); offGate.Open(() => lifecycle.Open(enabled, false));
        offGate.Update(new(enabled, true, true)); offGate.Open(() => lifecycle.Open(enabled, false));
        Require(counts.Sum() == 0, "confirmed inactive gate reaches no real lifecycle operations");
        var gate = new ActivationGate(lifecycle.Start, (_, _) => { }, lifecycle.Stop, notices.Add);
        gate.Update(new(enabled, false, false)); gate.Open(() => lifecycle.Open(enabled, false));
        Require(counts.Sum() == 0 && notices.Single().Contains("enabled, but"), "failed first read starts nothing and explains unreadable enabled state");
        var reader = new ActivationReader(host, store, log.Add);
        var first = reader.Read(); Require(first.Readable && first.Active, "missing host file is a good absent-GUID read");
        gate.Update(first); Require(counts[0] == 1 && counts.Skip(1).Sum() == 0, "first good read after failure honors Auto-open off");
        for (var i = 0; i < 10; i++) gate.Update(reader.Read());
        Require(reader.StoreParses == 1 && reader.HostParses == 0 && counts[0] == 1, "unchanged files do not parse or restart");
        File.WriteAllText(host, "<LaunchBox><Settings><DisabledPluginIds");
        gate.Update(reader.Read()); gate.Update(reader.Read());
        Require(gate.Active && counts[7] == 0 && counts[0] == 1 && counts[3] == 0, "G1 failed tick retains active state without stop/start/card");
        Require(log.Count == 1, "partial XML warning deduplicated");
        File.WriteAllText(host, "<LaunchBox><Settings><DisabledPluginIds /></Settings></LaunchBox>");
        using (var locked = new FileStream(host, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) gate.Update(reader.Read());
        Require(gate.Active && counts[7] == 0, "host sharing violation preserves active state");
        gate.Update(reader.Read()); var parsed = reader.HostParses;
        for (var i = 0; i < 10; i++) gate.Update(reader.Read());
        Require(reader.HostParses == parsed, "host timestamp/size cache skips parsing");
        File.Delete(store); gate.Update(reader.Read());
        Require(gate.Active && counts[7] == 0, "disappearing established store is no information, not disabled defaults");
        File.WriteAllText(store, "partial"); gate.Update(reader.Read());
        Require(gate.Active && counts[7] == 0, "partial store preserves active state");
        var corruptNotice = new ActivationReader(host, store, _ => { }).Read();
        Require(!corruptNotice.Active && corruptNotice.CorruptStore && corruptNotice.Notice.Contains("Configure") && corruptNotice.Notice.Contains("press OK") && !corruptNotice.Notice.Contains("shortly"), "H1 corrupt-store notice gives repair route");
        var corruptBytes = File.ReadAllBytes(store);
        try { await Settings.UpdateAsync(store, current => current with { Expanded = false }); throw new InvalidOperationException("corrupt Bridge write unexpectedly succeeded"); }
        catch (System.Text.Json.JsonException) { }
        Require(File.ReadAllBytes(store).SequenceEqual(corruptBytes), "H1 Bridge writes remain strict on corrupt content");
        enabled.Write(store);
        using (var locked = new FileStream(store, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) gate.Update(reader.Read());
        Require(gate.Active && counts[7] == 0 && counts[0] == 1 && counts[3] == 0, "store sharing violation preserves active state");
        gate.Update(reader.Read());
        (enabled with { Enabled = false }).Write(store); gate.Update(reader.Read());
        Require(!gate.Active && counts[7] == 1, "successful off read stops once");
        enabled.Write(store); gate.Update(reader.Read()); gate.Update(reader.Read());
        Require(counts[0] == 2 && counts[1] == 1 && counts[2] == 1 && counts[3] == 1 && counts[4] == 1 && counts[5] == 1 && counts[6] == 1, "real lifecycle enables panel/setup/poll/place/configure once");
        var bytes = File.ReadAllBytes(store); var edits = 0;
        using (var locked = new FileStream(store, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await Settings.UpdateAsync(store, current => { edits++; return current with { Expanded = false }; }); throw new InvalidOperationException("locked read unexpectedly succeeded"); }
            catch (IOException) { }
        }
        Require(edits == 0 && File.ReadAllBytes(store).SequenceEqual(bytes) && Settings.ReadRequired(store).Enabled, "G2 locked read aborts write and preserves enabled bytes");
        await Settings.UpdateAsync(store, current => current with { Expanded = false });
        Require( !Settings.ReadRequired(store).Expanded && Settings.ReadRequired(store).Enabled, "Expanded read/write executes on worker and preserves Enable");
        var replacement = enabled with { Port = 47805 };
        using (var locked = new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var write = Task.Run(() => replacement.Write(store));
            await Task.Delay(80); Require(!write.IsCompleted, "replace retries while target denies delete sharing"); locked.Dispose();
            await write.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Require(Settings.ReadRequired(store).Port == 47805, "bounded retry eventually writes");
        using (var locked = new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await Task.Run(() => enabled.Write(store)); throw new InvalidOperationException("locked replacement unexpectedly succeeded"); }
            catch (IOException) { }
        }
        Require(!Directory.GetFiles(root, "*.tmp").Any() && Settings.ReadRequired(store).Port == 47805, "failed replace cleans temporary file and preserves target");
        Require(!ActivationState.Read(Temp(), _ => { }).Active && ActivationState.Read(Temp(), _ => { }).Readable, "real root Read with missing host/store defaults off successfully");
        Console.WriteLine("OK  G1/G2/G7/G10/G11/G12: cached real reads, failed ticks retained, missing host, real lifecycle, strict locked update, retry/temp cleanup, off-thread expand");
    }

    public static void Placement()
    {
        var store = Path.Combine(Temp(), "settings.dat"); new Settings { Enabled = true }.Write(store);
        var caller = Environment.CurrentManagedThreadId; var writer = caller;
        Settings.UpdateAsync(store, current => { writer = Environment.CurrentManagedThreadId; return current with { Expanded = false }; }).GetAwaiter().GetResult();
        Require(writer != caller && !Settings.ReadRequired(store).Expanded, "G11 expanded IO runs outside the STA caller");
        var panel = new AssistantPanel(false); var window = new AssistantWindow(); var shows = 0;
        var placement = new PanelPlacement(panel, window, _ => { }, _ => shows++);
        var root = new Grid(); FocusManager.SetIsFocusScope(root, true);
        var details = new UserControl { Content = new Grid { Children = { new ScrollViewer { Content = new TextBlock { Text = "details" } } } } };
        var presenter = new ContentControl { Content = details }; root.Children.Add(presenter);
        long now = 0; var host = new PlacementHost(true, root, null, false);
        var driver = new PlacementDriver(placement, () => host, () => now);
        driver.Open("right", true); Require(driver.PlaceCalls == 0 && shows == 0, "H2 available host with null details settles without a floating flash");
        now = 1499; driver.Refresh(); Require(shows == 0, "settle is under two seconds");
        now = 1500; driver.Refresh(); Require(shows == 1 && placement.Floating, "settle deadline permits fallback");
        window.UserClosed += driver.CloseFloating; window.Close(); Require(driver.Opened, "closing fallback preserves chosen docking state");
        host = host with { Available = true, Details = details, DetailsVisible = true }; driver.Refresh();
        Require(!placement.Floating && PanelPlacement.Walk(root).Contains(panel), "details appearing redocks after fallback close");
        host = host with { Details = null, DetailsVisible = false }; driver.Refresh(); Require(placement.Floating && shows == 1, "closed fallback stays hidden");
        FocusManager.SetFocusedElement(window, panel); var calls = driver.PlaceCalls;
        for (var i = 0; i < 10; i++) { host = host with { Details = new UIElement() }; driver.Refresh(); }
        Require(driver.PlaceCalls == calls && shows == 1 && ReferenceEquals(FocusManager.GetFocusedElement(window), panel), "real placement polls normalize fresh placeholders without moving focus");
        host = host with { Details = details, DetailsVisible = true }; driver.Refresh();
        Require(driver.PlaceCalls == calls + 1 && !placement.Floating, "changed real details triggers placement positive control");
        var sidebar = new ContentControl { Name = "SideBarViewModel", Content = new TextBlock { Text = "sidebar" } }; root.Children.Add(sidebar);
        foreach (var chosen in new[] { "left", "floating", "right" })
        {
            driver.Open(chosen, false); var before = driver.PlaceCalls;
            for (var i = 0; i < 5; i++)
            {
                host = host with { Details = new UserControl(), DetailsVisible = i % 2 == 0 }; driver.Refresh();
            }
            Require(driver.PlaceCalls - before == (chosen == "right" ? 5 : 0), "H3 details changes only re-place Right: " + chosen);
            var normal = driver.PlaceCalls; driver.Refresh(true); Require(driver.PlaceCalls == normal + 1, "H3 forced refresh still places: " + chosen);
        }
        driver.Open("floating", false); window.Close(); var closedCalls = driver.PlaceCalls; driver.Refresh();
        Require(!driver.Opened && driver.PlaceCalls == closedCalls, "chosen Floating close stops placement");
        placement.Detach(); window.Shutdown();
        Console.WriteLine("OK  G3/G12: real placement startup settle, fallback close/redock, suppressed fallback, placeholder polling and positive change, chosen Floating close");
    }

    public static void Auth()
    {
        var auth = new AuthCoordinator { ConnectedStatus = "connected · test library" }; var stops = 0; var cards = 0;
        Require(!auth.Apply(AuthState.Unknown, true, true, () => stops++, _ => cards++) && stops == 0 && cards == 0 && auth.Status == auth.ConnectedStatus, "G8 real unknown-auth hold retains session/card and connected status");
        var denied = new AuthState(true, false, "Sign in");
        Require(!auth.Apply(denied, true, true, () => stops++, _ => cards++), "definite denial holds busy turn");
        auth.BetweenTurns(true, () => stops++, _ => cards++); Require(stops == 0 && cards == 0, "busy turn remains running");
        auth.BetweenTurns(false, () => stops++, _ => cards++); Require(stops == 1 && cards == 1, "between-turn denial actually stops and shows card");
        Require(auth.Apply(denied, true, false, () => stops++, _ => cards++) && stops == 2 && cards == 2, "idle denial positive stop control");
        Require(AuthCoordinator.CheckingStatus(false, "", null, "Choose another port.") == "offline · Choose another port.", "missing CLI preserves listener remedy");
        Console.WriteLine("OK  G8/G12: actual auth orchestration hold/defer/stop, restored status and missing-CLI listener remedy");
    }

    public static async Task ResumeAndShutdown()
    {
        foreach (var scenario in new[] { "early", "spawn", "idle", "crash", "timeout", "timeout-before-init", "bad-resume", "error-zero" })
        {
            var childPid = 0;
            var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = new Session("injected", Path.GetTempPath(), "unused", _ => { })
            {
                TurnTimeout = TimeSpan.FromMilliseconds(300),
                Launch = process =>
                {
                    if (scenario == "spawn") throw new IOException("injected spawn failure");
                    var script = scenario == "early" ? "exit 1" : scenario == "idle" ? "exit 0" : scenario is "bad-resume" or "error-zero" ? "[Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"session_id\":\"unrelated-id\"}'); exit " + (scenario == "bad-resume" ? "1" : "0") : scenario == "timeout-before-init" ? "Start-Sleep -Seconds 30" : "[Console]::WriteLine('{\"type\":\"system\",\"subtype\":\"init\"}'); " + (scenario is "timeout" or "crash" ? "Start-Sleep -Seconds 30" : "exit 0");
                    StartScript(process, script); childPid = process.Id;
                }
            };
            session.Interrupted += () => retry.TrySetResult();
            session.AcceptLine("""{"type":"result","subtype":"success","session_id":"remember-me"}"""); session.Start();
            if (scenario is "timeout" or "crash" or "timeout-before-init")
            {
                await Until(() => scenario == "timeout-before-init" ? session.Starts == 1 : session.Initializations == 1); session.Send("injected turn");
                if (scenario == "crash")
                {
                    session.AcceptLine("""{"type":"assistant","message":{"id":"partial","content":[{"type":"text","text":"partial"}]}}""");
                    using var crashed = Process.GetProcessById(childPid); crashed.Kill(true);
                }
            }
            await retry.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(session.LastSessionId == (scenario == "bad-resume" ? null : "remember-me"), "H5 resume id policy: " + scenario);
            session.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        foreach (var shutdown in new[] { false, true })
        {
            var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var session = new Session("injected", Path.GetTempPath(), "unused", _ => { }) { Launch = process => { StartScript(process, "Start-Sleep -Seconds 30"); started.TrySetResult(process.Id); } };
            session.Start(); using var child = Process.GetProcessById(await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Require(!child.HasExited && !session.Busy, "injected child is live and idle before disposal");
            var clock = Stopwatch.StartNew(); if (shutdown) session.Shutdown(); else session.Dispose(); clock.Stop();
            if (shutdown) Require(child.HasExited, "bounded shutdown returns after live child exits");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Require(child.HasExited && clock.ElapsedMilliseconds < 500, "live idle child gone; dispose/shutdown bounded to 500 ms");
        }
        Console.WriteLine("OK  G5/G11: injected early/spawn/idle/crash/timeout resume policies; live idle child disposal and bounded shutdown");
    }

    static void StartScript(Process process, string script)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        process.StartInfo = Session.Command(exe, Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]); process.Start();
    }

    public static async Task StreamAndResume()
    {
        string[] shapes = ["42", "\"x\"", "[]", """{"type":3}""", """{"type":"stream_event","event":42}""", """{"type":"assistant","message":42}""", """{"type":"assistant","message":{"content":[42,"x",[],null,{"type":"text","text":42},{"type":3,"text":"hidden"}]}}"""];
        const string normal = """{"type":"assistant","message":{"id":"normal","content":[{"type":"text","text":"normal reply"}]}}""";
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var session = new Session("injected", Path.GetTempPath(), "unused", logs.Enqueue)
        {
            Launch = process =>
            {
                var script = string.Join(";", shapes.Append(normal).Select(line => "[Console]::WriteLine('" + line.Replace("'", "''") + "')")) + "; Start-Sleep -Seconds 30";
                StartScript(process, script); started.TrySetResult(process.Id);
            }
        })
        {
            session.Reply += text => { output.Enqueue(text); if (text == "normal reply") replied.TrySetResult(); };
            session.Activity += output.Enqueue; session.Failure += failures.Enqueue;
            try
            {
                session.Start(); using var child = Process.GetProcessById(await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                await replied.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(output.SequenceEqual(new[] { "normal reply" }) && failures.IsEmpty && session.Starts == 1 && !child.HasExited, "H4 malformed shapes reach no UI/failure and do not kill child; next reply survives");
                Require(logs.Count(line => line.Contains("expected an object")) == 3 && !logs.Any(line => line.StartsWith("assistant process:")), "H4 non-object roots logged without reaching Run catch");
            }
            finally { session.Shutdown(); }
        }
        output.Clear(); failures.Clear();
        var arguments = new System.Collections.Concurrent.ConcurrentQueue<string[]>();
        var idsAtError = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Session? resumed = null;
        using (resumed = new Session("injected", Path.GetTempPath(), "unused", line => { if (line.StartsWith("assistant resume error result:")) idsAtError.Enqueue(resumed!.LastSessionId); })
        {
            Delay = (_, token) => Task.Delay(1, token),
            Launch = process =>
            {
                arguments.Enqueue(process.StartInfo.ArgumentList.ToArray());
                var first = arguments.Count == 1;
                StartScript(process, first
                    ? "[Console]::WriteLine('{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"session_id\":\"wrong-id\",\"result\":\"raw failure\"}'); [Console]::Error.WriteLine('No conversation found with session ID: remember-me'); exit 1"
                    : "Start-Sleep -Seconds 30");
                if (!first) fresh.TrySetResult();
            }
        })
        {
            resumed.AcceptLine("""{"type":"result","subtype":"success","session_id":"remember-me"}""");
            resumed.Reply += output.Enqueue; resumed.Activity += output.Enqueue; resumed.Failure += failures.Enqueue;
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed.Interrupted += () => ended.TrySetResult();
            try
            {
                resumed.Start(); await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
                resumed.Send("message after the failed resume"); await fresh.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var starts = arguments.ToArray();
                Require(starts.Length == 2 && starts[0].Contains("--resume") && !starts[1].Contains("--resume") && resumed.LastSessionId == null, "H5 measured bad resume starts the next child fresh");
                Require(idsAtError.SequenceEqual(new[] { "remember-me" }) && output.IsEmpty && failures.SequenceEqual(new[] { "the assistant restarted; send that again" }), "H5 bad result does not overwrite id or emit extra failure text");
            }
            finally { resumed.Shutdown(); }
        }
        Console.WriteLine("OK  H4/H5: malformed shapes ignored in live injected stream; measured resume error keeps original id until exit, one plain line, fresh restart");
    }
    static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
