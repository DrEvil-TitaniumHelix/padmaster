using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using PadMaster;

static class CodexCheck
{
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Codex: " + message); }
    static string Temp() { var path = Path.Combine(Path.GetTempPath(), "PadMaster-codex-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static string[] Fixture(string name) => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "codex_" + name + ".jsonl"));
    static string Emit(IEnumerable<string> lines) => string.Join(";", lines.Select(line => "[Console]::WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(line)) + "')))"));
    static readonly string PowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    static void Launch(Process process, string script)
    {
        process.StartInfo = Session.Command(PowerShell, Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        process.Start();
    }
    static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    public static async Task Run()
    {
        Arguments(); Parser(); Auth(); Watchdog(); await Processes();
    }
    public static void Arguments(bool negative = false)
    {
        var root = Temp(); var config = Path.Combine(root, "mcp.json"); const string secret = "fixture-local-bearer";
        Session.WriteMcpConfig(config, 47801, secret);
        string[] expected = ["exec", "--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules", "-s", "read-only", "-c", "approval_policy=never", "-c", "features.shell_tool=false", "-c", "mcp_servers.launchbox.url=http://127.0.0.1:47801/mcp", "-c", "mcp_servers.launchbox.bearer_token_env_var=PADMASTER_MCP_TOKEN", "-c", "mcp_servers.launchbox.default_tools_approval_mode=approve"];
        var first = Session.ChatCommand("codex.exe", root, config, null, "codex");
        if (negative) first.ArgumentList.Remove("features.shell_tool=false");
        Require(first.ArgumentList.SequenceEqual(expected), "exact first-turn argument list, including shell disabled and MCP approval");
        var next = Session.ChatCommand("codex.exe", root, config, "fixture-thread", "codex");
        Require(next.ArgumentList.SequenceEqual(new[] { "exec", "resume", "fixture-thread" }.Concat(expected.Skip(1).Where(x => x is not ("-s" or "read-only")))), "exact resume arguments omit sandbox flag");
        var cmd = Session.ChatCommand(@"C:\Tools with spaces\codex.cmd", root, config, "fixture-thread", "codex");
        Require(cmd.Arguments.Contains("mcp_servers.launchbox.url=http://127.0.0.1:47801/mcp") && cmd.ArgumentList.Count == 0, "usual cmd shim accepts unquoted TOML values");
        foreach (var start in new[] { first, next, cmd }) Require(start.Environment["PADMASTER_MCP_TOKEN"] == secret && !start.Arguments.Contains(secret) && !start.ArgumentList.Any(x => x.Contains(secret)), "bearer is environment-only");
        var names = new[] { "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_ADAPTER_CHECK", "CODEX_API_KEY" };
        var prior = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "fixture-value");
            var chat = Session.ChatCommand("codex.exe", root, config, null, "codex");
            var status = Session.Command("codex.exe", root, Session.StatusArguments("codex"));
            Require(status.ArgumentList.SequenceEqual(new[] { "login", "status" }), "status command");
            Require(new[] { chat, status }.All(start => !start.Environment.Keys.Any(key => key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.Equals("CODEX_API_KEY", StringComparison.OrdinalIgnoreCase))), "all OPENAI variables and CODEX_API_KEY stripped for chat and status");
        }
        finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], prior[i]); }
        var login = Session.LoginCommand("codex.cmd", root, "codex");
        Require(login.ArgumentList.SequenceEqual(new[] { "login" }) && login.UseShellExecute && login.WindowStyle == ProcessWindowStyle.Normal && !login.RedirectStandardOutput && !login.RedirectStandardError, "visible login only, inspected without executing");
        var fake = Path.Combine(root, "codex.cmd"); File.WriteAllText(fake, "");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", root);
            Require(Settings.DetectCli(kind: "codex") == fake && Settings.DetectCli(fake, "codex") == fake, "Codex PATH and configured path detection without running CLI");
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        Console.WriteLine("OK  Codex arguments: exact fresh/resume flags, cmd shim, environment-only bearer, OPENAI/CODEX_API_KEY stripped, status/login inspected, PATH detection");
    }
    static void Parser()
    {
        var visible = new List<string>(); var logs = new List<string>(); var done = 0; var failures = 0;
        using var session = new Session("unused", "unused", "unused", logs.Add) { Kind = "codex" };
        session.Reply += visible.Add; session.Activity += visible.Add; session.Failure += _ => failures++; session.Done += success => { if (success) done++; };
        session.BeginTurn();
        foreach (var line in Fixture("a1")) session.AcceptLine(line);
        Require(session.LastReply == "I’ll check the status tool.\n\n47899" && done == 1 && session.ParsedMessages == 2 && visible.Count(x => x == "checking LaunchBox…") == 1, "real multi-message turn shares a reply, tool start/completion deduplicated, one done");
        Require(session.LastSessionId == "fixture-thread" && !session.Busy, "thread id and completion");
        session.BeginTurn();
        foreach (var line in Fixture("a2")) session.AcceptLine(line.Replace("fixture-thread", "different-resume-id"));
        Require(session.LastReply == "47899" && done == 2 && session.LastSessionId == "fixture-thread", "real resume keeps first thread id and clears prior reply");
        session.BeginTurn();
        foreach (var line in Fixture("t3")) session.AcceptLine(line);
        Require(session.ParsedMessages == 2 && done == 3, "second captured multi-message turn");
        session.BeginTurn();
        string[] shapes = ["not JSON", "42", "[]", "\"text\"", "null", "{}", """{"type":42} """, """{"type":"item.completed","item":42}""", """{"type":"item.completed","item":{"type":"agent_message","text":{}}}""", """{"type":"thread.started","thread_id":42}""", """{"type":"unknown.future","text":"SECRET-MODEL","usage":123456}""", """{"type":"item.completed","item":{"type":"unknown","text":"SECRET-MODEL"}}"""];
        foreach (var line in shapes.Concat(Fixture("unknown"))) session.AcceptLine(line);
        session.AcceptError("Reading prompt from stdin... SECRET-TOKEN");
        session.AcceptLine("""{"type":"item.completed","item":{"id":"reply","type":"agent_message","text":"Only once."}}""");
        session.AcceptLine("""{"type":"item.completed","item":{"id":"reply","type":"agent_message","text":"Only once."}}""");
        Require(session.LastReply == "Only once." && session.ParsedMessages == 1 && failures == 0, "H4 malformed/unknown values survive and repeated item text is not doubled");
        session.AcceptLine("""{"type":"item.started","item":{"id":"change","type":"mcp_tool_call","server":"launchbox","tool":"lb_set","arguments":{"type":"IGame","id":"SECRET-ID","values":{"Notes":"SECRET-NOTES"}}}}""");
        Require(visible.Contains("changing a game…"), "shared plain lb activity map");
        session.AcceptLine("""{"type":"turn.failed","error":{"message":"SECRET-FAILURE"}}""");
        Require(failures == 1 && done == 3 && !session.Busy, "failure is plain and never a successful completion");
        Require(visible.Concat(logs).All(x => !x.Contains("SECRET-") && !x.Contains("input_tokens") && !x.Contains("fixture-thread") && !x.Contains("Reading prompt")), "no metadata, arguments, errors or stderr leaked");
        Console.WriteLine("OK  Codex parser: scrubbed real fresh/resume/multi-message fixtures, shared tool labels, H4 malformed/unknown events, plain failure, no metadata leaks");
    }
    static void Auth()
    {
        Require(AuthState.ParseCodex("Logged in using ChatGPT\r\n", 0).SignedIn, "ChatGPT subscription accepted");
        Require(!AuthState.ParseCodex("Logged in using ChatGPT", 1).SignedIn, "nonzero status rejected");
        Require(AuthState.ParseCodex("Logged in using an API key - fixture", 0) is { Known: true, SignedIn: false, OtherAccount: true }, "API-key account has setup reason");
        Require(AuthState.ParseCodex("Not logged in", 1) is { Known: true, SignedIn: false } && !AuthState.ParseCodex("unknown ChatGPT status", 0).SignedIn, "signed-out and unknown status rejected");
        var legacy = Settings.Decode("{\"enabled\":true}"u8.ToArray());
        Require(legacy.Assistant == "" && !new SetupState("", "cli", true).Ready, "legacy missing assistant keeps setup behavior");
        Require(Settings.TryApply(legacy, new Dictionary<string, string?> { ["assistant"] = "codex" }, false, out var settings, out _) && Settings.Decode(settings.Encode()).Assistant == "codex", "Codex settings round trip");
        var configured = new Settings { Assistant = "claude-code", Cli = "claude.cmd" };
        Require(Settings.TryApply(configured, new Dictionary<string, string?> { ["assistant"] = "codex", ["cli"] = "claude.cmd" }, false, out var switched, out _) && switched.Cli == "", "switching assistant clears the previous CLI path for auto-detection");
        Require(Settings.TryApply(configured, new Dictionary<string, string?> { ["assistant"] = "codex", ["cli"] = "codex.cmd" }, false, out switched, out _) && switched.Cli == "codex.cmd", "switching assistant preserves a newly chosen path");
        Require(new SetupState("codex", "codex.cmd", true).Ready && new SetupState("codex", "codex.cmd", false).CanSignIn, "Codex setup gating");
        Console.WriteLine("OK  Codex auth/settings: ChatGPT only, API-key and unknown refused, backward-compatible store, Codex round trip");
    }
    public static void Panel()
    {
        var panel = new AssistantPanel(false);
        panel.LoadConfiguration(new Settings { Assistant = "codex" }); panel.SetSetup(new("codex", "codex.cmd", true));
        Require(!panel.SetupVisible, "signed-in Codex shows chat");
        panel.OpenConfiguration();
        panel.Width = 520; panel.Height = 720; panel.Measure(new System.Windows.Size(520, 720)); panel.Arrange(new System.Windows.Rect(0, 0, 520, 720)); panel.UpdateLayout();
        var choice = PanelPlacement.Walk((System.Windows.DependencyObject)panel.Content).OfType<ComboBox>().Single(box => box.Items.Cast<object>().Contains("Codex"));
        Require(choice.SelectedItem as string == "Codex" && choice.Items.Count == 2, "card selects Codex and lists both assistants");
        panel.SetSetup(new("codex", "codex.cmd", false, Reason: AuthState.ParseCodex("Logged in using an API key", 0).Reason));
        Require(panel.SetupVisible, "API-key login shows card");
        using var session = new Session("unused", "unused", "unused", _ => { }) { Kind = "codex" };
        _ = new ChatBridge(session, panel, () => session, () => false, _ => { });
        session.BeginTurn(); foreach (var line in Fixture("a1")) session.AcceptLine(line);
        Require(panel.Transcript.Split("I’ll check the status tool.").Length == 2 && panel.Transcript.Split("47899").Length == 2 && !panel.Busy, "real ChatBridge presents multi-message Codex reply once");
        Console.WriteLine("OK  Codex panel: two choices, configured selection, setup gate, one multi-message reply through ChatBridge");
    }
    static void Watchdog()
    {
        var now = DateTime.UtcNow; var failures = 0;
        using var session = new Session("unused", "unused", "unused", _ => { }) { Kind = "codex", Now = () => now, SilenceTimeout = TimeSpan.FromSeconds(5), TurnTimeout = TimeSpan.FromSeconds(40), WatchdogInterval = TimeSpan.FromMinutes(1) };
        session.Failure += _ => failures++;
        session.BeginTurn(); now += TimeSpan.FromSeconds(4); session.AcceptLine("""{"type":"turn.started"}"""); now += TimeSpan.FromSeconds(4); session.CheckTimeout();
        Require(session.Busy && failures == 0, "stream events reset silence watchdog");
        session.ToolActivity(true, true); now += TimeSpan.FromSeconds(6); session.CheckTimeout();
        Require(session.Busy && session.State.UsedWrites, "active MCP write keeps silence watchdog alive and marks repeat risk");
        session.ToolActivity(false, false); session.SetHeld(true); now += TimeSpan.FromSeconds(60); session.CheckTimeout();
        Require(!session.Busy && failures == 0, "bulk confirmation suspends watchdog");
        session.SetHeld(false); session.CheckTimeout(); Require(session.Busy, "hidden-turn release resets deadline");
        now += TimeSpan.FromSeconds(6); session.CheckTimeout(); Require(!session.Busy && failures == 1, "silent Codex turn times out once");
        Console.WriteLine("OK  Codex watchdog: stream activity, active writes, repeat risk, bulk hold/release and silence deadline");
    }
    static async Task Processes()
    {
        var root = Temp(); var config = Path.Combine(root, "mcp.json"); Session.WriteMcpConfig(config, 47801, "fixture-token");
        var launches = 0; var successes = 0; var interrupted = 0; var hidden = 0;
        var prompts = new[] { "first 'quoted' & \"prompt\"", "hidden outcome", "second" };
        var generations = new ConcurrentQueue<int>(); var commands = new ConcurrentQueue<string[]>(); var failures = new ConcurrentQueue<string>(); var logs = new ConcurrentQueue<string>();
        using (var session = new Session("injected.exe", root, config, logs.Enqueue)
        {
            Kind = "codex",
            Launch = process =>
            {
                commands.Enqueue(process.StartInfo.ArgumentList.ToArray()); var index = Interlocked.Increment(ref launches) - 1;
                Require(index < 3, "no unexpected process restart");
                var expected = prompts[index].Replace("'", "''");
                Launch(process, "$prompt = [Console]::In.ReadToEnd(); if ($prompt.TrimEnd() -ne '" + expected + "') { exit 7 }; " + Emit(Fixture("a1")));
            }
        })
        {
            session.TurnStarted += (_, generation, _) => generations.Enqueue(generation);
            session.HiddenTurn += () => hidden++; session.Interrupted += () => interrupted++;
            session.Done += ok => { if (ok) Interlocked.Increment(ref successes); }; session.Failure += failures.Enqueue;
            session.Start(); Require(session.Starts == 0, "Codex does not launch an idle process");
            session.Send(prompts[0]); session.Send(prompts[2]); session.SendHidden(prompts[1]);
            await Until(() => successes == 3 || !failures.IsEmpty);
            Require(failures.IsEmpty, "queue process failure: " + string.Join(" | ", logs));
            Require(session.Starts == 3 && interrupted == 0 && hidden == 1 && failures.IsEmpty && !session.Busy && session.State.Queued == 0 && !session.State.Restarting, "normal exit advances hidden/user queue without restart/failure");
            Require(generations.SequenceEqual(new[] { 1, 2, 3 }) && commands.First()[1] == "--json" && commands.Skip(1).All(x => x[1] == "resume" && x[2] == "fixture-thread"), "fresh first child and resumed later generations");
            session.Shutdown(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        foreach (var scenario in new[] { "missing", "nonzero", "failed", "error", "early", "spawn", "timeout", "stop" })
        {
            var attempts = 0; var good = 0; var bad = 0; var ended = 0; var now = DateTime.UtcNow;
            var original = """{"type":"thread.started","thread_id":"remember-me"}""";
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var args = new ConcurrentQueue<string[]>();
            using var session = new Session("injected.exe", root, config, _ => { })
            {
                Kind = "codex", Now = () => now, SilenceTimeout = TimeSpan.FromSeconds(5), WatchdogInterval = TimeSpan.FromMinutes(1),
                Launch = process =>
                {
                    args.Enqueue(process.StartInfo.ArgumentList.ToArray()); var attempt = Interlocked.Increment(ref attempts);
                    if (attempt == 1 && scenario == "spawn") throw new IOException("injected");
                    var lines = attempt > 1 ? Fixture("a1") : scenario is "early" or "timeout" or "stop" ? [] : scenario == "missing" ? new[] { original } : scenario is "failed" or "error" ? new[] { original, "{\"type\":\"" + (scenario == "failed" ? "turn.failed" : "error") + "\",\"message\":\"SECRET-ERROR\"}" } : Fixture("a1");
                    Launch(process, "$null = [Console]::In.ReadToEnd(); " + Emit(lines) + (attempt == 1 && scenario is "timeout" or "stop" ? "; Start-Sleep -Seconds 30" : "; exit " + (attempt == 1 && scenario is "early" or "nonzero" ? "2" : "0")));
                }
            };
            session.AcceptLine(original); session.Done += ok => { if (ok) Interlocked.Increment(ref good); else { Interlocked.Increment(ref bad); first.TrySetResult(); } };
            session.Interrupted += () => Interlocked.Increment(ref ended);
            session.Send("retry me");
            if (scenario is "timeout" or "stop")
            {
                await Until(() => session.Starts == 1);
                if (scenario == "timeout") { now += TimeSpan.FromSeconds(6); session.CheckTimeout(); } else session.StopTurn();
            }
            await first.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Until(() => ended == 1);
            Require(good == 0 && bad == 1 && !session.Busy && session.Paused && session.State.CanRetry == (scenario != "stop"), "failure/timeout/Stop state: " + scenario);
            Require(session.LastSessionId == (scenario == "early" ? null : "remember-me"), "resume id cleared only for pre-init failed attempt: " + scenario);
            if (scenario == "stop") session.Send("retry me"); else session.Retry();
            await Until(() => good == 1);
            Require(attempts == 2 && session.State.Generation == 2 && !session.State.CanRetry && !session.State.Restarting && !session.Paused, "Retry/new send recovers with a fresh process: " + scenario);
            Require(args.Last().Contains("resume") == (scenario != "early"), "recovery resume arguments: " + scenario);
            session.Shutdown(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Console.WriteLine("OK  Codex processes: stdin/EOF, one child per turn, hidden/user queue, generations, resume, clean exit, nonzero/missing completion/errors/spawn/timeout/Stop and Retry");
    }
}
